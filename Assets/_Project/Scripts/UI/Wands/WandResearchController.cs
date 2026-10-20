using Game.Core;
using Game.Run;
using Game.Skills;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.UI
{
    /// <summary>常驻区域入口和暂停所有权；独立Panel隐藏不会丢失打开事件。</summary>
    public sealed class WandResearchController : MonoBehaviour
    {
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private HubInteractionController _hub;
        [SerializeField] private RunPauseCoordinator _pause;
        [SerializeField] private SinglePlayerInventoryController _inventory;
        [SerializeField] private ShopController _shop;
        private InputAction _escape;
        private GameObject _panel;
        private WandProgramWorkbenchPresenter _presenter;
        private string _wandId;
        private int _slot, _replayIndex;
        private ulong _sequence, _waiting;
        public bool IsOpen { get; private set; }
        public WandProgramWorkbenchPresenter Presenter => _presenter;
        public static WandResearchController Current { get; private set; }
        private void Awake() { _escape=new InputAction("CloseResearch",InputActionType.Button,"<Keyboard>/escape");_escape.performed+=OnEscape; }
        private void OnEnable()
        {
            Current=this;_escape.Enable();EventBus<HubInteractionResultEvent>.Subscribe(OnInteraction);
            EventBus<InventoryChangedEvent>.Subscribe(OnInventoryChanged);EventBus<InventoryCommandResultEvent>.Subscribe(OnResult);
            EventBus<RaidStateChangedEvent>.Subscribe(OnRaidChanged);
        }
        private void OnDisable()
        {
            Close();if(Current==this)Current=null;_escape.Disable();EventBus<HubInteractionResultEvent>.Unsubscribe(OnInteraction);
            EventBus<InventoryChangedEvent>.Unsubscribe(OnInventoryChanged);EventBus<InventoryCommandResultEvent>.Unsubscribe(OnResult);
            EventBus<RaidStateChangedEvent>.Unsubscribe(OnRaidChanged);
        }
        private void OnDestroy() { _escape.performed-=OnEscape;_escape.Dispose(); }
        private void OnEscape(InputAction.CallbackContext _) { if(IsOpen)Close(); }
        private void Update()
        { if(IsOpen && (_hub==null || !_hub.CanResearch || _inventory!=null && _inventory.IsOpen || _shop!=null && _shop.IsOpen))Close(); }
        private void OnRaidChanged(RaidStateChangedEvent e)
        { if(IsOpen && e.InventoryId==_session.ReadOnly.InventoryId && e.Phase!=RaidPhase.HubReady)Close(); }
        private void OnInteraction(HubInteractionResultEvent e)
        { if(e.Kind==HubInteractionKind.SpellResearch)Open(e); }
        public bool Open(in HubInteractionResultEvent grant)
        {
            if(IsOpen || _hub==null || _pause==null || !_hub.TryConsumeResearchGrant(grant))return false;
            // 先持有自己的暂停位，再关其它界面；中间不会释放最后一位而意外恢复Gameplay。
            _pause.RequestPause(RunPauseReason.SpellResearch);_inventory?.Close();_shop?.Close();
            EventBus<HeldItemUseResetEvent>.Publish(new HeldItemUseResetEvent{InventoryId=_session.ReadOnly.InventoryId});
            if(_panel==null)
            {
                _panel=new GameObject("SpellResearchCanvas_法术研究台");_panel.transform.SetParent(transform,false);_panel.SetActive(false);
                _presenter=_panel.AddComponent<WandProgramWorkbenchPresenter>();_presenter.InitializeOwned();
                _presenter.OwnedWandSelected+=SelectWand;_presenter.InstallRequested+=Install;_presenter.RemoveRequested+=Remove;
                _presenter.ShowCurrentRequested+=Refresh;_presenter.ShowReplayRequested+=ShowReplay;_presenter.CloseRequested+=Close;
            }
            IsOpen=true;_panel.SetActive(true);Refresh();return true;
        }
        public void Close()
        {
            if(!IsOpen)return;IsOpen=false;_presenter?.EndOwnedDrag();if(_panel!=null)_panel.SetActive(false);
            _pause?.ReleasePause(RunPauseReason.SpellResearch);
            if(_session?.ReadOnly!=null)EventBus<HeldItemUseResetEvent>.Publish(new HeldItemUseResetEvent{InventoryId=_session.ReadOnly.InventoryId});
        }
        public void SelectWand(int slot) { if(slot<0||slot>2)return;_slot=slot;Refresh(); }
        private void Refresh()
        {
            if(!IsOpen)return;
            if(!_session.WandsReadOnly.TryGetSlot(_slot,out var wand) || !_session.TryCaptureWandResearch(wand.InstanceId,out var snapshot))
            { _wandId=null;_presenter.BindOwned(null,null,default,null,_slot);return; }
            _wandId=wand.InstanceId;
            _presenter.BindOwned(snapshot.Execution.Program,snapshot.Execution.Draw,snapshot.Inputs,snapshot.Library,_slot,
                (snapshot.AssumesRaidEntry?"出发推演":"当前推演")+(snapshot.Execution.Program.Shuffle?" · 乱序":""),Mathf.Max(snapshot.Execution.CastRemaining,snapshot.Execution.ReloadRemaining));
        }
        private void ShowReplay()
        {
            if(!IsOpen)return;var archive=_session.ExecutionReplays;
            WandExecutionReplay replay=null;
            if(_wandId!=null)archive.TryGetLatest(_wandId,out replay);
            if(replay==null && archive.Count>0)replay=archive.GetOldestFirst((_replayIndex++)%archive.Count);
            if(replay==null){_presenter.SetFeedback("尚无施法记录");return;}
            var root=SpellPreviewRoot.FromRecorded(replay.Before,replay.After,replay.Graph,replay.Trace,replay.Summary,replay.Inputs.AvailableMana);
            var cancelled=new bool[replay.Graph.Scopes.Count];for(int i=0;i<cancelled.Length;i++)cancelled[i]=replay.GetLayerStatus(i)==WandLayerResourceStatus.Cancelled;
            cancelled[0]|=replay.WorldStatus==WandWorldExecutionStatus.Cancelled;
            _presenter.BindRecorded(root,replay.WorldStatus==WandWorldExecutionStatus.Released?"上次实发":replay.WorldStatus==WandWorldExecutionStatus.Cancelled?"上次记录 · 已取消":"上次记录 · 尚未观察到发射",replay.ReleasedOutputs,cancelled);
        }
        private void OnInventoryChanged(InventoryChangedEvent e)
        { if(IsOpen && e.InventoryId==_session.ReadOnly.InventoryId && !_presenter.IsHistory)Refresh(); }
        private void OnResult(InventoryCommandResultEvent e)
        { if(IsOpen && e.InventoryId==_session.ReadOnly.InventoryId && e.RequestId==_waiting && !e.Success)_presenter.SetFeedback("操作未完成："+e.Reason); }
        public void Install(string instanceId,int slot)
        {
            if(!IsOpen || !_hub.CanResearch || _presenter.IsHistory || _wandId==null)return;
            Send(new InventoryCommandEvent{Kind=InventoryCommandKind.Place,Destination=InventoryDestination.WandSlot,ItemId=instanceId,OwnerId=_wandId,SlotIndex=slot});
        }
        public void Remove(string instanceId)
        {
            if(!IsOpen || !_hub.CanResearch || _presenter.IsHistory)return;
            var read=_session.ReadOnly;var items=new InventoryItemSnapshot[read.Count];int count=read.CopyItemsTo(items);
            // 专用法术库按现有空槽放回，不销毁物品；最终容量/归属仍由Run命令裁定。
            for(int slot=0;slot<1024;slot++)
            {
                bool occupied=false;for(int i=0;i<count;i++)if(items[i].Location.Kind==ItemLocationKind.SpellStorage && items[i].Location.SlotIndex==slot)occupied=true;
                if(occupied)continue;
                Send(new InventoryCommandEvent{Kind=InventoryCommandKind.Place,Destination=InventoryDestination.SpellStorage,ItemId=instanceId,OwnerId=read.ActorId,SlotIndex=slot});return;
            }
        }
        private void Send(InventoryCommandEvent command)
        { command.InventoryId=_session.ReadOnly.InventoryId;command.ExpectedVersion=_session.ReadOnly.Version;command.RequestId=_waiting=++_sequence;EventBus<InventoryCommandEvent>.Publish(command); }
    }
}
