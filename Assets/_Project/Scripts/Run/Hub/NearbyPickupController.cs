using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>离散拾取入口：同Scene真实距离、背包实际形状与事务；失败不移动物品。</summary>
    public sealed class NearbyPickupController : MonoBehaviour,INearbyPickupReadOnly
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Transform _player;
        [SerializeField,Min(.1f)] private float _reach=3;
        public Transform Player=>_player;
        public void Configure(SinglePlayerInventorySession session,Transform player)
        {_session=session;_player=player;session.WorldActor=player;}
        public float Reach=>_reach;
        public WorldItemPickup FocusedPickup {get;private set;}
        private float _nextQuery;
        private WorldItemPickup[] _items=new WorldItemPickup[64],_scratch=new WorldItemPickup[64];
        private uint _inventoryVersion;
        public int Count{get;private set;}
        public int SelectedIndex{get;private set;}=-1;
        public uint Revision{get;private set;}
        public string GetItemId(int index)=>index>=0 && index<Count?_items[index]?.ItemId:null;
        private bool _focusVisible;
        private bool _collectingAll;
        private static readonly Unity.Profiling.ProfilerMarker Marker=new Unity.Profiling.ProfilerMarker("Norn.Items.NearbyQuery");
        private void Update()
        {
            if(Time.unscaledTime<_nextQuery)return;_nextQuery=Time.unscaledTime+.1f;
            using var sample=Marker.Auto();
            RefreshCandidates();
        }
        private void OnEnable()=>EventBus<WorldItemVisualBoundEvent>.Subscribe(OnRegistry);
        private void OnRegistry(WorldItemVisualBoundEvent e)
        {
            // 分配只在离散注册事件发生，查询热路径不会因玩家走近更多物品而new数组。
            if(e.RegisteredCount<=_items.Length)return;int capacity=Mathf.NextPowerOfTwo(e.RegisteredCount);
            System.Array.Resize(ref _items,capacity);System.Array.Resize(ref _scratch,capacity);
        }
        public void RefreshCandidates()
        {
            var read=_session?.ReadOnly;string selected=FocusedPickup?.ItemId;int previousIndex=SelectedIndex;
            int count=read!=null && !_session.RaidWriteLocked?WorldItemPickup.CopyNearby(read.InventoryId,_player,_reach,_scratch):0;
            bool changed=count!=Count || _inventoryVersion!=(read?.Version??0);
            for(int i=0;i<count && !changed;i++)changed=_items[i]!=_scratch[i];
            int index=-1;for(int i=0;i<count;i++)if(_scratch[i].ItemId==selected){index=i;break;}
            if(index<0 && count>0)index=Mathf.Clamp(previousIndex,0,count-1);
            changed|=index!=SelectedIndex;
            var swap=_items;_items=_scratch;_scratch=swap;Count=count;SelectedIndex=index;_inventoryVersion=read?.Version??0;
            FocusedPickup=index>=0?_items[index]:null;
            if(changed){Revision++;EventBus<NearbyPickupListChangedEvent>.Publish(new NearbyPickupListChangedEvent{InventoryId=read?.InventoryId,Revision=Revision});}
            PublishFocus();
        }
        private void PublishFocus()
        {
            bool visible=FocusedPickup!=null && Time.timeScale>0;
            if(_focusVisible==visible && _lastFocus==FocusedPickup)return;
            _focusVisible=visible;_lastFocus=FocusedPickup;
            EventBus<NearbyPickupFocusChangedEvent>.Publish(new NearbyPickupFocusChangedEvent{InventoryId=_session?.ReadOnly?.InventoryId,ItemId=FocusedPickup?.ItemId,Anchor=FocusedPickup?.transform,Visible=visible});
        }
        private WorldItemPickup _lastFocus;
        public void SelectRelative(int delta)
        {if(Time.timeScale<=0 || _session==null || _session.RaidWriteLocked)return;RefreshCandidates();if(Count==0)return;
            SelectedIndex=((SelectedIndex+delta)%Count+Count)%Count;FocusedPickup=_items[SelectedIndex];Revision++;
            EventBus<NearbyPickupListChangedEvent>.Publish(new NearbyPickupListChangedEvent{InventoryId=_session.ReadOnly.InventoryId,Revision=Revision});PublishFocus();}
        public bool TryCollectFocused()
        {
            if(Time.timeScale<=0 || _session==null || _session.RaidWriteLocked)return false;
            if(FocusedPickup==null)RefreshCandidates();string id=FocusedPickup?.ItemId;if(id==null)return false;
            // 使用用户选中的身份并再次校验距离；目标失效时不偷换成另一件物品。
            bool success=TryCollect(id);RefreshCandidates();return success;
        }
        private void OnDisable()
        {
            EventBus<WorldItemVisualBoundEvent>.Unsubscribe(OnRegistry);
            FocusedPickup=null;_lastFocus=null;_focusVisible=false;Count=0;SelectedIndex=-1;Revision++;
            EventBus<NearbyPickupListChangedEvent>.Publish(new NearbyPickupListChangedEvent{InventoryId=_session?.ReadOnly?.InventoryId,Revision=Revision});
            EventBus<NearbyPickupFocusChangedEvent>.Publish(new NearbyPickupFocusChangedEvent{InventoryId=_session?.ReadOnly?.InventoryId,Visible=false});
        }
        private void Awake(){if(_session!=null)_session.WorldActor=_player;OnRegistry(new WorldItemVisualBoundEvent{RegisteredCount=WorldItemPickup.RegisteredCount});}
        public bool IsNearby(string itemId)=>WorldItemPickup.FindNearby(_session?.ReadOnly?.InventoryId,itemId,_player,_reach)!=null;
        public bool TryCollectNearest()
        {
            var read=_session?.ReadOnly;if(read==null || Time.timeScale<=0 || _session.RaidWriteLocked)return false;
            var proxy=WorldItemPickup.FindNearby(read.InventoryId,null,_player,_reach);if(proxy==null)return false;
            TryCollect(proxy.ItemId);return true;
        }
        public bool TryCollect(string itemId)
        {
            var read=_session?.ReadOnly;var proxy=WorldItemPickup.FindNearby(read?.InventoryId,itemId,_player,_reach);
            if(read==null || proxy==null || _session.RaidWriteLocked)return false;
            string bagId=null;InventoryItemSnapshot bag=default;
            foreach(var e in _session.Profile.Items)if(e.Location.Kind==ItemLocationKind.EquippedBackpack && e.Location.OwnerId==read.ActorId)
                {bagId=e.Item.InstanceId;read.TryGetView(bagId,out bag);break;}
            if(!read.TryGetView(itemId,out var item))return false;
            var result=new InventoryOperationResult(false,InventoryFailureReason.Capacity,read.Version);
            // 先补同类材料堆再找空格，避免旧堆在包底而顶部空位被另一小份GMU抢占。
            if(item.Kind==ItemKind.Material&&_session.Profile.Items.TryGet(itemId,out var source)&&source.Material!=null)
                foreach(var entry in _session.Profile.Items)
                    if(entry.Location.Kind==ItemLocationKind.Backpack&&entry.Location.OwnerId==bagId&&MaterialStackOps.Compatible(source,entry.Item))
                    {result=proxy.TryCollect(entry.Location,read.Version);if(result.Success)break;}
            // 离散拾取逐槽尝试真实归属事务；失败不占位，满专区才继续普通背包。
            if(item.Kind==ItemKind.Spell)
                for(int slot=0;slot<_session.Profile.Items.Catalog.SpellStorageCapacity && !result.Success;slot++)
                    result=proxy.TryCollect(new ItemLocation(ItemLocationKind.SpellStorage,read.ActorId,0,0,0,slot),read.Version);
            if(!result.Success && item.Kind==ItemKind.Backpack && bagId==null)
                result=proxy.TryCollect(new ItemLocation(ItemLocationKind.EquippedBackpack,read.ActorId,0,0,0,0),read.Version);
            else if(!result.Success && bagId!=null && item.Kind!=ItemKind.Backpack)
            {
                // 尝试只创建候选事务，失败无提交；有形状空洞、旋转与堆叠时仍以真实Store裁定为准。
                for(int rotation=0;rotation<4 && !result.Success;rotation++)
                    for(int y=0;y<=bag.Rules.BackpackHeight-item.Rules.Shape.GetHeight(rotation) && !result.Success;y++)
                        for(int x=0;x<=bag.Rules.BackpackWidth-item.Rules.Shape.GetWidth(rotation) && !result.Success;x++)
                            result=proxy.TryCollect(bagId,new GridPlacement(x,y,rotation),read.Version);
            }
            if(result.Success)EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent{InventoryId=read.InventoryId,Version=read.Version});
            if(!_collectingAll)EventBus<NearbyPickupResultEvent>.Publish(new NearbyPickupResultEvent{InventoryId=read.InventoryId,ItemId=itemId,Success=result.Success,
                Reason=result.Reason,RequiresArrangement=!result.Success && (result.Reason==InventoryFailureReason.Capacity
                    || result.Reason==InventoryFailureReason.Occupied || result.Reason==InventoryFailureReason.OutOfBounds)});return result.Success;
        }
        public int CollectAll(out int remaining)
        {
            remaining=Count;if(_session?.ReadOnly==null||_session.RaidWriteLocked||_collectingAll)return 0;
            RefreshCandidates();int original=Count,success=0;
            // 单击的稳定身份快照：同步库存事件会刷新候选数组，不能一边遍历一边用新的索引。
            var ids=new string[original];for(int i=0;i<original;i++)ids[i]=_items[i].ItemId;
            _collectingAll=true;
            try{for(int i=0;i<ids.Length;i++)
            {
                int attempts=_session.Profile.Items.Count+1;bool changed=false;
                while(attempts-->0)
                {
                    uint before=_session.ReadOnly.Version;
                    if(!TryCollect(ids[i])||_session.ReadOnly.Version==before)break;
                    changed=true;
                    if(!_session.ReadOnly.TryGetView(ids[i],out var item)||item.Location.Kind!=ItemLocationKind.WorldItem)break;
                }
                if(changed)success++;
            }}finally{_collectingAll=false;}
            RefreshCandidates();remaining=Count;return success;
        }
    }
}
