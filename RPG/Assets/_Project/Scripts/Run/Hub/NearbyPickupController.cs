using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>离散拾取入口：同Scene真实距离、背包实际形状与事务；失败不移动物品。</summary>
    public sealed partial class NearbyPickupController : MonoBehaviour,INearbyPickupReadOnly
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Transform _player;
        [SerializeField,Min(.1f)] private float _reach=3;
        private IInventoryPresentationSource _onlineSource;
        private bool _online, _forceListNotification, _subscriptionsInstalled, _lifecycleReleased;
        private ulong _onlineActor;
        private uint _onlineEpoch, _groundVersion;
        private string[] _onlineBatch;
        private int _onlineBatchIndex;
        private ulong _pendingCommand;
        private int _pendingCaller, _sendingCaller;
        private string _pendingItem, _sendingItem;
        private bool _receivedReceipt, _sendingOnline, _hasEarlyReceipt;
        private OnlineInventoryCommandReceipt _receipt, _earlyReceipt;
        // 只保存当前一条材料请求的原始读数；它是确认收据的基线，不是另一份资产或历史快照缓存。
        private bool _pendingMaterial;
        private byte _pendingMaterialKey;
        private uint _pendingMaterialGroundVersion;
        private ulong _pendingMaterialQuantity, _pendingOwnedMaterialQuantity;
        private string _pendingMaterialContentId, _pendingMaterialWorldOwner, _pendingMaterialStackState;
        private IInventoryPresentationSource OnlineSource => _onlineSource is Object value && value == null ? null : _onlineSource;
        private IInventoryReadOnly CurrentRead => _account ? _accountSource?.ReadOnly : _online ? OnlineSource?.ReadOnly : _session?.ReadOnly;
        public IInventoryReadOnly PresentationReadOnly => CurrentRead;
        public bool HasPendingOnlinePickup => _account ? _accountBatch != null || !string.IsNullOrEmpty(_accountPending.CommandId)
            : _online && (_onlineBatch != null || _pendingCommand != 0 || _sendingOnline);
        public Transform Player=>_player;
        public void Configure(SinglePlayerInventorySession session,Transform player)
        {ClearAccountRequests();_account=false;_accountSource=null;ClearOnlineRequests();_online=false;_onlineSource=null;_session=session;_player=player;if(session!=null)session.WorldActor=player;}
        /// <summary>
        /// 线上附近列表只读同Actor的已认证Source，不借用单机Session或Profile。Source/Actor/Epoch变化
        /// 结束未完成队列；相同Source重复绑定保留正在等待的收据，不能在每份快照到达时重发拾取。
        /// 首次绑定发布原ListChanged，Character可从同Actor晚绑定原F输入，UI无需反向引用Character。
        /// </summary>
        public void ConfigureOnline(IInventoryPresentationSource source, Transform actor)
        {
            ClearAccountRequests(); _account=false; _accountSource=null;
            var context = source as IOnlineInventoryCommandContext;
            bool changed = !_online || !ReferenceEquals(_onlineSource, source) || _player != actor
                || _onlineActor != (context?.ActorId ?? 0) || _onlineEpoch != (context?.ConnectionEpoch ?? 0);
            if (changed) { ClearOnlineRequests(); _forceListNotification = true; }
            _online = true; _session = null; _onlineSource = source; _player = actor;
            _onlineActor = context?.ActorId ?? 0; _onlineEpoch = context?.ConnectionEpoch ?? 0;
            // 显式装配可先于Unity自动OnEnable（含EditMode组合）。先观察请求/收据，才能把代理的
            // “已发布请求”记成pending；幂等保护也避免随后真实OnEnable把同一回调订阅两次。
            if (isActiveAndEnabled) EnsureSubscriptions();
            RefreshCandidates();
        }
        private bool OnlineContextAlive => this != null && !_lifecycleReleased && _online && isActiveAndEnabled && _player != null && OnlineSource?.ReadOnly != null && OnlineSource.ReadOnly.Version != 0
            && OnlineSource is IOnlineInventoryCommandContext context && context.ActorId != 0 && context.ActorId == _onlineActor
            && context.ConnectionEpoch != 0 && context.ConnectionEpoch == _onlineEpoch;
        private bool CanUse => Time.timeScale > 0 && (_account ? AccountContextAlive : _online ? OnlineContextAlive : _session != null && !_session.RaidWriteLocked);
        public float Reach=>_online || _account?3f:_reach;
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
            if (_account) ProgressAccountPickup();
            // 收据/Owner可能在同一NetworkUpdate以任意顺序到达。确认推进先于10Hz列表节流，
            // 每帧最多结束一条并开始下一条；不会在EventBus同步回调里递归发送整批同版本意图。
            if (_online)
            {
                if (!OnlineContextAlive) ClearOnlineRequests();
                else ProgressOnlinePickup();
            }
            if(Time.unscaledTime<_nextQuery)return;_nextQuery=Time.unscaledTime+.1f;
            using var sample=Marker.Auto();
            RefreshCandidates();
        }
        private void OnEnable() => EnsureSubscriptions();
        private void EnsureSubscriptions()
        {
            if (this == null || _subscriptionsInstalled) return;
            _lifecycleReleased=false;
            EventBus<WorldItemVisualBoundEvent>.Subscribe(OnRegistry);
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(OnOnlineRequest);
            EventBus<OnlineInventoryCommandResultEvent>.Subscribe(OnOnlineReceipt);
            EventBus<OnlineAccountInventoryCommandResultEvent>.Subscribe(OnAccountReceipt);
            _subscriptionsInstalled=true;
        }
        private void OnRegistry(WorldItemVisualBoundEvent e)
        {
            if (this == null || _lifecycleReleased) return;
            // 分配只在离散注册事件发生，查询热路径不会因玩家走近更多物品而new数组。
            if(e.RegisteredCount<=_items.Length)return;int capacity=Mathf.NextPowerOfTwo(e.RegisteredCount);
            System.Array.Resize(ref _items,capacity);System.Array.Resize(ref _scratch,capacity);
        }
        public void RefreshCandidates()
        {
            var read=CurrentRead;string selected=FocusedPickup?.ItemId;int previousIndex=SelectedIndex;
            int count=read!=null && (_account?AccountContextAlive:_online?OnlineContextAlive:_session!=null && !_session.RaidWriteLocked)?WorldItemPickup.CopyNearby(read.InventoryId,_player,Reach,_scratch):0;
            if (_account)
            {
                int visible=0;
                for(int i=0;i<count;i++)if(IsAccountGround(_scratch[i].ItemId,_scratch[i]))_scratch[visible++]=_scratch[i];
                count=visible;
            }
            if (_online)
            {
                int visible = 0;
                for (int i = 0; i < count; i++) if (IsOnlineGround(_scratch[i].ItemId, _scratch[i])) _scratch[visible++] = _scratch[i];
                count = visible;
            }
            uint groundVersion = (OnlineSource as IOnlineGroundInventoryContext)?.GroundVersion ?? 0;
            bool changed=_forceListNotification || count!=Count || _inventoryVersion!=(read?.Version??0) || _online && _groundVersion!=groundVersion;
            for(int i=0;i<count && !changed;i++)changed=_items[i]!=_scratch[i];
            int index=-1;for(int i=0;i<count;i++)if(_scratch[i].ItemId==selected){index=i;break;}
            if(index<0 && count>0)index=Mathf.Clamp(previousIndex,0,count-1);
            changed|=index!=SelectedIndex;
            var swap=_items;_items=_scratch;_scratch=swap;Count=count;SelectedIndex=index;_inventoryVersion=read?.Version??0;
            _groundVersion=groundVersion;_forceListNotification=false;
            FocusedPickup=index>=0?_items[index]:null;
            if(changed){Revision++;EventBus<NearbyPickupListChangedEvent>.Publish(new NearbyPickupListChangedEvent{InventoryId=read?.InventoryId,Revision=Revision});}
            PublishFocus();
        }
        private void PublishFocus()
        {
            bool visible=FocusedPickup!=null && Time.timeScale>0;
            if(_focusVisible==visible && _lastFocus==FocusedPickup)return;
            _focusVisible=visible;_lastFocus=FocusedPickup;
            EventBus<NearbyPickupFocusChangedEvent>.Publish(new NearbyPickupFocusChangedEvent{InventoryId=CurrentRead?.InventoryId,ItemId=FocusedPickup?.ItemId,Anchor=FocusedPickup?.transform,Visible=visible});
        }
        private WorldItemPickup _lastFocus;
        public void SelectRelative(int delta)
        {if(!CanUse)return;RefreshCandidates();if(Count==0)return;
            SelectedIndex=((SelectedIndex+delta)%Count+Count)%Count;FocusedPickup=_items[SelectedIndex];Revision++;
            EventBus<NearbyPickupListChangedEvent>.Publish(new NearbyPickupListChangedEvent{InventoryId=CurrentRead.InventoryId,Revision=Revision});PublishFocus();}
        public bool TryCollectFocused()
        {
            if(!CanUse)return false;
            if(FocusedPickup==null)RefreshCandidates();string id=FocusedPickup?.ItemId;if(id==null)return false;
            // 使用用户选中的身份并再次校验距离；目标失效时不偷换成另一件物品。
            bool success=TryCollect(id);RefreshCandidates();return success;
        }
        private void OnDisable() => ReleaseLifecycle();
        private void OnDestroy() => ReleaseLifecycle();
        /// <summary>
        /// 显式装配和Mono自动启停共用同一订阅所有权；OnDisable后紧接OnDestroy只能释放一次。
        /// EventBus当前派发可能已复制旧委托，退订之外仍需在在线上下文入口先检查Unity fake-null，
        /// 避免已销毁组件被迟到收据访问native的isActiveAndEnabled。只退本实例委托，不清其他Actor事件。
        /// </summary>
        private void ReleaseLifecycle()
        {
            EventBus<WorldItemVisualBoundEvent>.Unsubscribe(OnRegistry);
            EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(OnOnlineRequest);
            EventBus<OnlineInventoryCommandResultEvent>.Unsubscribe(OnOnlineReceipt); _subscriptionsInstalled=false; ClearOnlineRequests();
            EventBus<OnlineAccountInventoryCommandResultEvent>.Unsubscribe(OnAccountReceipt); ClearAccountRequests();
            if (_lifecycleReleased) return;
            _lifecycleReleased=true;
            FocusedPickup=null;_lastFocus=null;_focusVisible=false;Count=0;SelectedIndex=-1;Revision++;
            EventBus<NearbyPickupListChangedEvent>.Publish(new NearbyPickupListChangedEvent{InventoryId=CurrentRead?.InventoryId,Revision=Revision});
            EventBus<NearbyPickupFocusChangedEvent>.Publish(new NearbyPickupFocusChangedEvent{InventoryId=CurrentRead?.InventoryId,Visible=false});
        }
        private void Awake(){if(_session!=null)_session.WorldActor=_player;OnRegistry(new WorldItemVisualBoundEvent{RegisteredCount=WorldItemPickup.RegisteredCount});}
        public bool IsNearby(string itemId)
        {
            var proxy=WorldItemPickup.FindNearby(CurrentRead?.InventoryId,itemId,_player,Reach);
            return proxy!=null && (_account ? AccountContextAlive && IsAccountGround(itemId,proxy) : !_online || OnlineContextAlive && IsOnlineGround(itemId,proxy));
        }
        public bool TryCollectNearest()
        {
            var read=CurrentRead;if(read==null || !CanUse)return false;
            var proxy=WorldItemPickup.FindNearby(read.InventoryId,null,_player,Reach);if(proxy==null)return false;
            if (_online || _account) return TryCollect(proxy.ItemId);
            TryCollect(proxy.ItemId);return true;
        }
        public bool TryCollect(string itemId)
        {
            if (_account) return TryBeginAccountPickup(itemId);
            if (_online) return CanUse && !HasPendingOnlinePickup && TryBeginOnlinePickup(itemId);
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
            if (_account)
            {
                RefreshCandidates(); remaining=Count;
                if (!CanUse || HasPendingOnlinePickup || Count==0) return 0;
                _accountBatch=new string[Count];_accountBatchIndex=0;_accountBatchItem=null;_accountBatchAttempts=0;
                for(int i=0;i<Count;i++)_accountBatch[i]=_items[i].ItemId;
                StartNextAccountPickup();return 0;
            }
            if (_online)
            {
                RefreshCandidates(); remaining=Count;
                if (!CanUse || HasPendingOnlinePickup || Count==0) return 0;
                // 固定这一点击看到的有限ID集合；后到的新掉落不追加，队友先拾走的项只跳过，不自动换物。
                _onlineBatch=new string[Count]; _onlineBatchIndex=0;
                for(int i=0;i<Count;i++)_onlineBatch[i]=_items[i].ItemId;
                StartNextOnlinePickup(); return 0; // 发出请求不代表已拾取；后续只由真实收据+Owner发布成功。
            }
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

        private bool IsOnlineGround(string itemId, WorldItemPickup proxy)
            => proxy!=null && CurrentRead!=null && CurrentRead.TryGetView(itemId,out var item)
                && !item.EntryBoundBackpack
                && item.Location.Kind==ItemLocationKind.WorldItem && item.Location.OwnerId==proxy.WorldOwner;

        private bool TryBeginOnlinePickup(string itemId)
        {
            if (!CanUse || _pendingCommand!=0 || _sendingOnline || string.IsNullOrEmpty(itemId)) return false;
            var proxy=WorldItemPickup.FindNearby(CurrentRead.InventoryId,itemId,_player,3f);
            if (!IsOnlineGround(itemId,proxy)) return false;
            ClearPendingOnlinePickup();
            if (!CaptureMaterialPickupBaseline(itemId)) { ClearPendingOnlinePickup();return false; }
            _sendingOnline=true;_sendingCaller=proxy.GetInstanceID();_sendingItem=itemId;_hasEarlyReceipt=false;
            bool sent;
            try { sent=proxy.RequestOnlinePickup(); }
            finally { _sendingOnline=false;_sendingCaller=0;_sendingItem=null; }
            if (!sent) { ClearPendingOnlinePickup();return false; }
            if (_pendingCommand==0)
            {
                // 代理已发布却没有得到当前身份的命令ID，结果是不确定，不能当作“没发送”继续洪发下一件。
                // 活跃Configure/OnEnable已保证订阅；该保护同时封住旧Epoch代理尚未被新表现替换的窗口。
                ClearPendingOnlinePickup();_onlineBatch=null;_onlineBatchIndex=0;return false;
            }
            if (_hasEarlyReceipt && _earlyReceipt.CommandId==_pendingCommand)
            { _receipt=_earlyReceipt;_receivedReceipt=true; }
            _hasEarlyReceipt=false;return true;
        }
        private void OnOnlineRequest(OnlineInventoryCommandRequestedEvent request)
        {
            if (!_sendingOnline || !OnlineContextAlive || request.CallerId!=_sendingCaller || request.ActorId!=_onlineActor
                || (request.Intent.Kind!=OnlineInventoryCommandKind.PickupContainer && request.Intent.Kind!=OnlineInventoryCommandKind.PickupItem)
                || request.Intent.ItemId!=_sendingItem
                || request.Intent.ConnectionEpoch!=_onlineEpoch || request.Intent.CommandId==0) return;
            _pendingCommand=request.Intent.CommandId;_pendingCaller=request.CallerId;_pendingItem=request.Intent.ItemId;
            _receivedReceipt=false;
        }
        private void OnOnlineReceipt(OnlineInventoryCommandResultEvent result)
        {
            if (!OnlineContextAlive || result.ActorId!=_onlineActor || result.Receipt.ConnectionEpoch!=_onlineEpoch) return;
            // 网络Codec/速率的本地拒绝可能在本Controller观察Request之前同步返回；仅借存当前proxy的一条。
            if (_sendingOnline && result.CallerId==_sendingCaller && _pendingCommand==0)
            { _earlyReceipt=result.Receipt;_hasEarlyReceipt=true;return; }
            if (_pendingCommand==0 || result.CallerId!=_pendingCaller || result.Receipt.CommandId!=_pendingCommand) return;
            _receipt=result.Receipt;_receivedReceipt=true;
        }
        private void ProgressOnlinePickup()
        {
            if (_pendingCommand!=0)
            {
                if (!_receivedReceipt || CurrentRead.Version<_receipt.InventoryVersion) return;
                bool success=_receipt.Success;
                if (success && !OwnerHasConfirmedPickup(_pendingItem)) return;
                string item=_pendingItem;var failure=_receipt.Failure;
                ClearPendingOnlinePickup();
                if (_onlineBatch!=null) _onlineBatchIndex++;
                PublishOnlinePickupResult(item,success,failure);
                if (failure==OnlineInventoryCommandFailure.NotReady || failure==OnlineInventoryCommandFailure.StaleCommand)
                { _onlineBatch=null;_onlineBatchIndex=0; }
            }
            if (_onlineBatch!=null && _pendingCommand==0 && CanUse) StartNextOnlinePickup();
        }
        private bool OwnerHasConfirmedPickup(string id)
        {
            var read=CurrentRead;var source=OnlineSource;
            if (_pendingMaterial) return MaterialPickupWasInstalled(id);
            if (read==null || source==null || !read.TryGetView(id,out var item)) return false;
            // Spell可自动进入专区，包可进入空装备位；确认的是同ID在本Owner真实随身闭包，不能只认瓶子进包。
            // 队友抢拾只会让公开地面消失，不会让本Owner出现此ID，因此不能误报自己获得了物品。
            var root=item.Location;
            for(int depth=0;root.HasParentInstance;depth++)
            {
                if(depth>=read.Count || !read.TryGetView(root.OwnerId,out var parent))return false;
                root=parent.Location;
            }
            return root.OwnerId==read.ActorId && (root.Kind==ItemLocationKind.EquippedBackpack
                || root.Kind==ItemLocationKind.EquippedWand || root.Kind==ItemLocationKind.SpellStorage || root.Kind==ItemLocationKind.EquippedTool);
        }
        /// <summary>
        /// 原材料TryMove会先并入旧堆：部分并堆保留地面同ID余量，完全并堆会消耗原ID。
        /// 发包前记录同一次落地身份、GroundVersion和包内相容GMU；不改Store或先隐藏代理。
        /// </summary>
        private bool CaptureMaterialPickupBaseline(string id)
        {
            if (!CurrentRead.TryGetView(id,out var item)) return false;
            _pendingMaterial=item.Kind==ItemKind.Material;
            if (!_pendingMaterial) return true;
            if (!(OnlineSource is OnlineInventoryPresentationSource source) || source.GroundVersion==0
                || source.PresentationItems==null || !source.PresentationItems.TryGet(id,out var actual) || actual.Material==null) return false;
            _pendingMaterialKey=item.MaterialKey;_pendingMaterialQuantity=item.MaterialQuantity;
            _pendingMaterialContentId=item.ContentId;_pendingMaterialWorldOwner=item.Location.OwnerId;
            _pendingMaterialStackState=actual.StackStateKey;_pendingMaterialGroundVersion=source.GroundVersion;
            return TryReadOwnedMaterialQuantity(out _pendingOwnedMaterialQuantity);
        }
        private bool TryReadOwnedMaterialQuantity(out ulong quantity)
        {
            quantity=0;var read=CurrentRead;
            if (!(OnlineSource is OnlineInventoryPresentationSource source) || source.PresentationItems==null || read==null
                || string.IsNullOrEmpty(source.EquippedBackpackId) || !read.TryGetView(source.EquippedBackpackId,out var bag)
                || bag.Kind!=ItemKind.Backpack || bag.Location.Kind!=ItemLocationKind.EquippedBackpack || bag.Location.OwnerId!=read.ActorId) return false;
            // 具体Store的struct Enumerator只读当前完整投影，不分配查询数组，也不绕过Authority写入。
            foreach (var entry in source.PresentationItems)
            {
                var material=entry.Item.Material;
                if (entry.Location.Kind!=ItemLocationKind.Backpack || entry.Location.OwnerId!=source.EquippedBackpackId
                    || entry.Item.ContentId!=_pendingMaterialContentId || entry.Item.StackStateKey!=_pendingMaterialStackState
                    || material==null || material.MaterialKey!=_pendingMaterialKey) continue;
                if (ulong.MaxValue-quantity<material.Quantity) return false;
                quantity+=material.Quantity;
            }
            return true;
        }
        /// <summary>
        /// Transferred是服务器原收据中的实际并入量，Owner/Ground只从已安装的完整Source读取。
        /// 同库存版本可精确核对守恒；后继版本可能已被K消费或队友继续拾取，只要求覆盖原提交与
        /// 原地面余量上界，不能强求旧净额重现。这里不退款、补量或为等待UI另加游戏锁。
        /// </summary>
        private bool MaterialPickupWasInstalled(string id)
        {
            var read=CurrentRead;
            if (!(OnlineSource is OnlineInventoryPresentationSource source) || read==null
                || source.GroundVersion<=_pendingMaterialGroundVersion || _receipt.Transferred==0
                || _receipt.Transferred>_pendingMaterialQuantity || read.Version<_receipt.InventoryVersion) return false;
            ulong remaining=0;
            if (read.TryGetView(id,out var item))
            {
                if (item.Kind!=ItemKind.Material || item.ContentId!=_pendingMaterialContentId || item.MaterialKey!=_pendingMaterialKey) return false;
                if (item.Location.Kind==ItemLocationKind.WorldItem)
                {
                    // 同ID被拾起再落地属于另一落地身份，不能拿新的WorldOwner确认旧请求。
                    if (item.Location.OwnerId!=_pendingMaterialWorldOwner) return false;
                    remaining=item.MaterialQuantity;
                }
                else if (item.Location.Kind!=ItemLocationKind.Backpack || item.Location.OwnerId!=source.EquippedBackpackId) return false;
            }
            ulong expectedRemaining=_pendingMaterialQuantity-_receipt.Transferred;
            if (read.Version>_receipt.InventoryVersion) return remaining<=expectedRemaining;
            return remaining==expectedRemaining && TryReadOwnedMaterialQuantity(out var owned)
                && owned>=_pendingOwnedMaterialQuantity && owned-_pendingOwnedMaterialQuantity==_receipt.Transferred;
        }
        private void StartNextOnlinePickup()
        {
            while (_onlineBatch!=null && _onlineBatchIndex<_onlineBatch.Length)
            { if(TryBeginOnlinePickup(_onlineBatch[_onlineBatchIndex]))return;_onlineBatchIndex++; }
            _onlineBatch=null;_onlineBatchIndex=0;
        }
        private void PublishOnlinePickupResult(string item,bool success,OnlineInventoryCommandFailure failure)
        {
            var reason=success?InventoryFailureReason.None:failure==OnlineInventoryCommandFailure.NoCapacity?InventoryFailureReason.Capacity
                :failure==OnlineInventoryCommandFailure.StaleVersion?InventoryFailureReason.StaleVersion
                :failure==OnlineInventoryCommandFailure.Busy?InventoryFailureReason.ResourceBusy:InventoryFailureReason.InvalidOwnership;
            EventBus<NearbyPickupResultEvent>.Publish(new NearbyPickupResultEvent
            {InventoryId=CurrentRead?.InventoryId,ItemId=item,Success=success,Reason=reason,RequiresArrangement=!success && reason==InventoryFailureReason.Capacity});
        }
        private void ClearPendingOnlinePickup()
        {
            _pendingCommand=0;_pendingCaller=0;_pendingItem=null;_receivedReceipt=false;_receipt=default;_hasEarlyReceipt=false;_earlyReceipt=default;
            _pendingMaterial=false;_pendingMaterialKey=0;_pendingMaterialGroundVersion=0;
            _pendingMaterialQuantity=_pendingOwnedMaterialQuantity=0;
            _pendingMaterialContentId=_pendingMaterialWorldOwner=_pendingMaterialStackState=null;
        }
        private void ClearOnlineRequests()
        { ClearPendingOnlinePickup();_onlineBatch=null;_onlineBatchIndex=0;_sendingOnline=false;_sendingCaller=0;_sendingItem=null; }
    }
}
