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
        // 在线分支显式绑定当前Actor的Owner投影；即使传null解绑，也不能再读取保留Hub的旧Session。
        private bool _onlineConfigured, _onlineCaptured, _onlineCloseRequested, _returnToInventory, _geometryCloseRequested;
        private OnlineInventoryPresentationSource _onlineSource;
        private IOnlineHomeResearchAccess _onlineAccess;
        private ulong _onlineCommand, _commandSnapshot, _observedSnapshot;
        private OnlineInventoryCommandKind _onlineKind;
        private uint _commandSession, _receiptVersion;
        public string LastInteractionStatus { get; private set; } = string.Empty;
        public bool IsOpen { get; private set; }
        public WandProgramWorkbenchPresenter Presenter => _presenter;
        public static WandResearchController Current { get; private set; }
        private void Awake() { _escape=new InputAction("CloseResearch",InputActionType.Button,"<Keyboard>/escape");_escape.performed+=OnEscape; }
        private void OnEnable()
        {
            Current=this;_escape.Enable();EventBus<HubInteractionResultEvent>.Subscribe(OnInteraction);
            EventBus<InventoryChangedEvent>.Subscribe(OnInventoryChanged);EventBus<InventoryCommandResultEvent>.Subscribe(OnResult);
            EventBus<RaidStateChangedEvent>.Subscribe(OnRaidChanged);
            EventBus<OnlineHomeResearchRequestedEvent>.Subscribe(OnOnlineResearchRequested);
            EventBus<OnlineInventoryCommandResultEvent>.Subscribe(OnOnlineResult);
        }
        private void OnDisable()
        {
            if (_onlineConfigured) DetachOnlineView(); else Close();
            if(Current==this)Current=null;_escape.Disable();EventBus<HubInteractionResultEvent>.Unsubscribe(OnInteraction);
            EventBus<InventoryChangedEvent>.Unsubscribe(OnInventoryChanged);EventBus<InventoryCommandResultEvent>.Unsubscribe(OnResult);
            EventBus<RaidStateChangedEvent>.Unsubscribe(OnRaidChanged);
            EventBus<OnlineHomeResearchRequestedEvent>.Unsubscribe(OnOnlineResearchRequested);
            EventBus<OnlineInventoryCommandResultEvent>.Unsubscribe(OnOnlineResult);
        }
        private void OnDestroy() { _escape.performed-=OnEscape;_escape.Dispose(); }
        private void OnEscape(InputAction.CallbackContext _) { if(IsOpen)Close(); }
        private void Update()
        {
            if (_onlineConfigured) { ObserveOnlineOwner(); return; }
            if(IsOpen && (_hub==null || !_hub.CanResearch || _inventory!=null && _inventory.IsOpen || _shop!=null && _shop.IsOpen))Close();
        }
        private void OnRaidChanged(RaidStateChangedEvent e)
        { if(!_onlineConfigured && IsOpen && _session?.ReadOnly!=null && e.InventoryId==_session.ReadOnly.InventoryId && e.Phase!=RaidPhase.HubReady)Close(); }
        private void OnInteraction(HubInteractionResultEvent e)
        { if(!_onlineConfigured && e.Kind==HubInteractionKind.SpellResearch)Open(e); }
        public bool Open(in HubInteractionResultEvent grant)
        {
            if (_onlineConfigured) return RequestOnlineOpen();
            if(IsOpen || _session!=null && _session.AccountInventoryReadOnly || _hub==null || _pause==null || !_hub.TryConsumeResearchGrant(grant))return false;
            // 先持有自己的暂停位，再关其它界面；中间不会释放最后一位而意外恢复Gameplay。
            _pause.RequestPause(RunPauseReason.SpellResearch);_inventory?.Close();_shop?.Close();
            EventBus<HeldItemUseResetEvent>.Publish(new HeldItemUseResetEvent{InventoryId=_session.ReadOnly.InventoryId});
            EnsurePanel();
            IsOpen=true;_panel.SetActive(true);Refresh();return true;
        }
        private void EnsurePanel()
        {
            if(_panel==null)
            {
                _panel=new GameObject("SpellResearchCanvas_法术研究台");_panel.transform.SetParent(transform,false);_panel.SetActive(false);
                _presenter=_panel.AddComponent<WandProgramWorkbenchPresenter>();_presenter.InitializeOwned();
                _presenter.OwnedWandSelected+=SelectWand;_presenter.InstallRequested+=Install;_presenter.RemoveRequested+=Remove;
                _presenter.ShowCurrentRequested+=Refresh;_presenter.ShowReplayRequested+=ShowReplay;_presenter.CloseRequested+=Close;
            }
        }
        public void Close()
        {
            if (_onlineConfigured)
            {
                if (!IsOpen) return;
                _onlineCloseRequested = true; _presenter?.EndOwnedDrag(); TryRequestOnlineClose(); return;
            }
            if(!IsOpen)return;IsOpen=false;_presenter?.EndOwnedDrag();if(_panel!=null)_panel.SetActive(false);
            _pause?.ReleasePause(RunPauseReason.SpellResearch);
            if(_session?.ReadOnly!=null)EventBus<HeldItemUseResetEvent>.Publish(new HeldItemUseResetEvent{InventoryId=_session.ReadOnly.InventoryId});
        }
        public void SelectWand(int slot) { if(slot<0||slot>2)return;_slot=slot;Refresh(); }
        private void Refresh()
        {
            if(!IsOpen)return;
            IInventoryPresentationSource source = _onlineConfigured ? _onlineSource : _session;
            if(source?.WandsReadOnly==null || !source.WandsReadOnly.TryGetSlot(_slot,out var wand) || !source.TryCaptureWandResearch(wand.InstanceId,out var snapshot))
            { _wandId=null;_presenter.BindOwned(null,null,default,null,_slot);return; }
            _wandId=wand.InstanceId;
            _presenter.BindOwned(snapshot.Execution.Program,snapshot.Execution.Draw,snapshot.Inputs,snapshot.Library,_slot,
                (snapshot.AssumesRaidEntry?"出发推演":"当前推演")+(snapshot.Execution.Program.Shuffle?" · 乱序":""),Mathf.Max(snapshot.Execution.CastRemaining,snapshot.Execution.ReloadRemaining));
            if (_onlineConfigured && _onlineCommand!=0) _presenter.SetFeedback(LastInteractionStatus);
        }
        private void ShowReplay()
        {
            if(!IsOpen)return;
            WandExecutionReplay replay=null;
            if (_onlineConfigured)
            {
                if (!ValidOnlineBinding() || !_onlineSource.ResearchOpen) return;
                if (_wandId!=null) _onlineSource.TryGetLatestReplay(_wandId,out replay);
                if (replay==null && _onlineSource.ReplayCount>0) replay=_onlineSource.GetReplayOldestFirst((_replayIndex++)%_onlineSource.ReplayCount);
            }
            else
            {
                var archive=_session.ExecutionReplays;
                if(_wandId!=null)archive.TryGetLatest(_wandId,out replay);
                if(replay==null && archive.Count>0)replay=archive.GetOldestFirst((_replayIndex++)%archive.Count);
            }
            if(replay==null){_presenter.SetFeedback("尚无施法记录");return;}
            var root=SpellPreviewRoot.FromRecorded(replay.Before,replay.After,replay.Graph,replay.Trace,replay.Summary,replay.Inputs.AvailableMana);
            var cancelled=new bool[replay.Graph.Scopes.Count];for(int i=0;i<cancelled.Length;i++)cancelled[i]=replay.GetLayerStatus(i)==WandLayerResourceStatus.Cancelled;
            cancelled[0]|=replay.WorldStatus==WandWorldExecutionStatus.Cancelled;
            _presenter.BindRecorded(root,replay.WorldStatus==WandWorldExecutionStatus.Released?"上次实发":replay.WorldStatus==WandWorldExecutionStatus.Cancelled?"上次记录 · 已取消":"上次记录 · 尚未观察到发射",replay.ReleasedOutputs,cancelled);
            if (_onlineConfigured && _onlineCommand!=0) _presenter.SetFeedback(LastInteractionStatus);
        }
        private void OnInventoryChanged(InventoryChangedEvent e)
        {
            if (_onlineConfigured) { if (e.InventoryId==_onlineSource?.ReadOnly?.InventoryId) ObserveOnlineOwner(); return; }
            if(IsOpen && _session?.ReadOnly!=null && e.InventoryId==_session.ReadOnly.InventoryId && !_presenter.IsHistory)Refresh();
        }
        private void OnResult(InventoryCommandResultEvent e)
        { if(!_onlineConfigured && IsOpen && _session?.ReadOnly!=null && e.InventoryId==_session.ReadOnly.InventoryId && e.RequestId==_waiting && !e.Success)_presenter.SetFeedback("操作未完成："+e.Reason); }
        public void Install(string instanceId,int slot)
        {
            if (_onlineConfigured) { SubmitOnlineEdit(instanceId, slot, false); return; }
            if(!IsOpen || !_hub.CanResearch || _presenter.IsHistory || _wandId==null)return;
            Send(new InventoryCommandEvent{Kind=InventoryCommandKind.Place,Destination=InventoryDestination.WandSlot,ItemId=instanceId,OwnerId=_wandId,SlotIndex=slot});
        }
        public void Remove(string instanceId)
        {
            if (_onlineConfigured) { SubmitOnlineEdit(instanceId, 0, true); return; }
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

        /// <summary>
        /// Scope只交接同Actor/Scene的只读源与空间能力，不交接DS Owner或本地Profile。相同引用重复通知是幂等刷新；
        /// 换Actor、重连或卸载先结束本机Canvas/Input capture，旧收据不能打开新世界的研究台。
        /// </summary>
        public void ConfigureOnline(IInventoryPresentationSource source, IOnlineHomeResearchAccess access)
        {
            if (_onlineConfigured && ReferenceEquals(_onlineSource, source) && ReferenceEquals(_onlineAccess, access))
            { ObserveOnlineOwner(); return; }
            if (!_onlineConfigured && IsOpen) Close();
            DetachOnlineView(); _onlineConfigured = true;
            _onlineSource = source as OnlineInventoryPresentationSource; _onlineAccess = access;
            ObserveOnlineOwner();
        }
        internal void BindOnlineInventory(SinglePlayerInventoryController inventory) => _inventory = inventory;
        private bool ValidOnlineBinding()
        {
            if (!_onlineConfigured || _onlineSource==null || !_onlineSource.HasSnapshot || _onlineSource.ReadOnly.Version==0
                || _onlineAccess==null || _onlineAccess is Object value && value==null || _onlineAccess.Actor==null) return false;
            var context = (IOnlineInventoryCommandContext)_onlineSource;
            return context.ActorId!=0 && context.ActorId==_onlineAccess.ActorId && context.ConnectionEpoch!=0
                && context.ConnectionEpoch==_onlineAccess.ConnectionEpoch && _onlineAccess.Actor.scene.isLoaded
                && _onlineAccess.Actor.scene.handle==_onlineAccess.WorldSceneHandle && gameObject.scene.handle==_onlineAccess.WorldSceneHandle;
        }
        private void OnOnlineResearchRequested(OnlineHomeResearchRequestedEvent request)
        {
            if (!ValidOnlineBinding() || request.ActorId!=_onlineAccess.ActorId || request.ConnectionEpoch!=_onlineAccess.ConnectionEpoch
                || request.WorldSceneHandle!=_onlineAccess.WorldSceneHandle) return;
            RequestOnlineOpen();
        }
        private bool RequestOnlineOpen()
        {
            if (!ValidOnlineBinding() || !_onlineAccess.CanResearch || IsOpen || _onlineCommand!=0 || _onlineSource.ResearchOpen) return false;
            _onlineCloseRequested = _returnToInventory = false;
            PublishOnline(new OnlineInventoryCommandIntent { Kind=OnlineInventoryCommandKind.BeginWandResearch });
            return true;
        }
        private void TryRequestOnlineClose()
        {
            if (!_onlineCloseRequested || !ValidOnlineBinding() || !_onlineSource.ResearchOpen || _onlineCommand!=0) return;
            PublishOnline(new OnlineInventoryCommandIntent { Kind=OnlineInventoryCommandKind.EndWandResearch });
        }
        private void PublishOnline(OnlineInventoryCommandIntent intent)
        {
            var context = (IOnlineInventoryCommandContext)_onlineSource;
            intent.CommandId=OnlineInventoryCommandIds.Next(); intent.ConnectionEpoch=context.ConnectionEpoch;
            intent.ExpectedInventoryVersion=_onlineSource.ReadOnly.Version;
            // 同步EventBus可能立刻回拒绝收据，所以先记录全部关联字段，再发布。收据不直接改变窗口或真实物品。
            _onlineCommand=intent.CommandId; _onlineKind=intent.Kind; _commandSession=_onlineSource.ResearchSessionVersion;
            _commandSnapshot=_onlineSource.SnapshotSequence; _receiptVersion=0;
            Feedback(intent.Kind==OnlineInventoryCommandKind.BeginWandResearch?"正在打开研究台…":
                intent.Kind==OnlineInventoryCommandKind.EndWandResearch?"正在关闭研究台…":"正在确认物品操作…");
            EventBus<OnlineInventoryCommandRequestedEvent>.Publish(new OnlineInventoryCommandRequestedEvent
            { ActorId=context.ActorId,CallerId=GetInstanceID(),Intent=intent });
        }
        private void OnOnlineResult(OnlineInventoryCommandResultEvent result)
        {
            if (!ValidOnlineBinding() || _onlineCommand==0 || result.CallerId!=GetInstanceID() || result.ActorId!=_onlineAccess.ActorId
                || result.Receipt.ConnectionEpoch!=_onlineAccess.ConnectionEpoch || result.Receipt.CommandId!=_onlineCommand) return;
            if (!result.Receipt.Success)
            {
                bool closing=_onlineKind==OnlineInventoryCommandKind.EndWandResearch;
                ClearOnlineCommand(); if (closing) _onlineCloseRequested=false;
                Feedback(result.Receipt.Failure==OnlineInventoryCommandFailure.NoCapacity?"目标位置空间不足，物品保留原位。":
                    result.Receipt.Failure==OnlineInventoryCommandFailure.StaleVersion || result.Receipt.Failure==OnlineInventoryCommandFailure.StaleProgram
                        ?"物品状态已更新，请重新操作。":"操作未完成，请稍后重试。"); return;
            }
            _receiptVersion=result.Receipt.InventoryVersion; ObserveOnlineOwner();
        }
        private void ObserveOnlineOwner()
        {
            // Begin/End的收据与完整Owner可以任意先后到达。窗口只跟随已验证Open；版本推进才结束该会话请求。
            // 装拆还需要成功收据和更晚完整快照，不能拿另一条资源小包或未提交的本地拖拽当作成功。
            if (!ValidOnlineBinding()) { DetachOnlineView(); return; }
            bool changed=_observedSnapshot!=_onlineSource.SnapshotSequence;
            _observedSnapshot=_onlineSource.SnapshotSequence;
            bool open=_onlineSource.ResearchOpen;
            if (_onlineCommand!=0 &&
                ((_onlineKind==OnlineInventoryCommandKind.BeginWandResearch && open || _onlineKind==OnlineInventoryCommandKind.EndWandResearch && !open)
                    && _onlineSource.ResearchSessionVersion>_commandSession
                || _onlineKind==OnlineInventoryCommandKind.MoveOwned && _receiptVersion!=0
                    && _onlineSource.SnapshotSequence>_commandSnapshot && _onlineSource.ReadOnly.Version>=_receiptVersion)) ClearOnlineCommand();
            if (!open)
            {
                bool switchBack=IsOpen && _returnToInventory;
                HideOnlinePanel(); _onlineCloseRequested=_returnToInventory=_geometryCloseRequested=false;
                if (switchBack && _inventory!=null) _inventory.OpenBackpack();
                return;
            }
            if (!IsOpen)
            {
                // 原工作台/布局完全复用。互斥界面先释放自己的capture，再由研究窗口取得独立Owner，世界暂停由DS事实控制。
                _inventory?.Close(); _shop?.Close(); EnsurePanel(); IsOpen=true; _panel.SetActive(true);
                _onlineCaptured=true;
                EventBus<InventoryPresentationVisibilityEvent>.Publish(new InventoryPresentationVisibilityEvent { OwnerId=GetInstanceID(),Open=true });
                ResetHeldUse(); Refresh();
            }
            else if (changed && !_presenter.IsHistory) Refresh();
            if (_inventory!=null && _inventory.IsOpen) { _returnToInventory=true; _inventory.Close(); _onlineCloseRequested=true; }
            if (_shop!=null && _shop.IsOpen) { _shop.Close(); _onlineCloseRequested=true; }
            if (_onlineAccess.CanResearch) _geometryCloseRequested=false;
            else if (!_geometryCloseRequested) { _geometryCloseRequested=true; _onlineCloseRequested=true; }
            TryRequestOnlineClose();
        }
        private void SubmitOnlineEdit(string instanceId,int slot,bool remove)
        {
            // Presenter只报告真实实例ID和作者槽号。此处重读同Owner父链、源/目标程序修订，再提交原MoveOwned；
            // 不触碰Store或Profile，失败/未知结果都保持原图标，当前推演仍由可信冻结Draw/Inputs生成。
            if (!IsOpen || !ValidOnlineBinding() || !_onlineSource.ResearchOpen || !_onlineAccess.CanResearch || _onlineCloseRequested
                || _onlineCommand!=0 || _presenter.IsHistory || _wandId==null || !_onlineSource.ReadOnly.TryGetView(instanceId,out var item)
                || item.Kind!=ItemKind.Spell || !IsOwnedOnline(item)) return;
            uint sourceRevision=0,targetRevision=0;
            if (item.Location.Kind==ItemLocationKind.WandSlot)
            {
                if (!_onlineSource.WandsReadOnly.TryGetProgram(item.Location.OwnerId,out var from) || from.ProgramRevision==0) return;
                sourceRevision=from.ProgramRevision;
            }
            if (remove)
            {
                if (item.Location.Kind!=ItemLocationKind.WandSlot || item.Location.OwnerId!=_wandId) return;
                slot=FindFreeSpellStorageSlot(); if (slot<0) { Feedback("法术库空间不足，物品保留原位。"); return; }
            }
            else
            {
                if (!_onlineSource.WandsReadOnly.TryGetProgram(_wandId,out var target) || target.ProgramRevision==0
                    || slot<0 || slot>=target.Program.Count) return;
                targetRevision=target.ProgramRevision;
            }
            PublishOnline(new OnlineInventoryCommandIntent { Kind=OnlineInventoryCommandKind.MoveOwned,ItemId=instanceId,
                SourceWandId=item.Location.Kind==ItemLocationKind.WandSlot?item.Location.OwnerId:null,SourceProgramRevision=sourceRevision,
                TargetWandId=remove?null:_wandId,TargetProgramRevision=targetRevision,TargetSlot=slot,
                TargetLocationKind=(int)(remove?ItemLocationKind.SpellStorage:ItemLocationKind.WandSlot) });
        }
        private bool IsOwnedOnline(in InventoryItemSnapshot item)
        {
            var read=_onlineSource.ReadOnly; var location=item.Location;
            for (int depth=0;location.HasParentInstance;depth++)
            { if (depth>=read.Count || !read.TryGetView(location.OwnerId,out var parent)) return false; location=parent.Location; }
            return location.OwnerId==read.ActorId && (location.Kind==ItemLocationKind.EquippedWand
                || location.Kind==ItemLocationKind.EquippedBackpack || location.Kind==ItemLocationKind.SpellStorage);
        }
        private int FindFreeSpellStorageSlot()
        {
            var read=_onlineSource.ReadOnly; var items=new InventoryItemSnapshot[read.Count]; int count=read.CopyItemsTo(items);
            for (int slot=0;slot<_onlineSource.SpellStorageCapacity;slot++)
            {
                bool used=false; for (int i=0;i<count;i++) if (items[i].Location.Kind==ItemLocationKind.SpellStorage && items[i].Location.SlotIndex==slot) { used=true; break; }
                if (!used) return slot;
            }
            return -1;
        }
        private void Feedback(string message) { LastInteractionStatus=message??string.Empty; if (IsOpen) _presenter?.SetFeedback(LastInteractionStatus); }
        private void ClearOnlineCommand() { _onlineCommand=0; _onlineKind=0; _receiptVersion=0; LastInteractionStatus=string.Empty; }
        private void ResetHeldUse()
        { if (_onlineSource?.ReadOnly!=null) EventBus<HeldItemUseResetEvent>.Publish(new HeldItemUseResetEvent { InventoryId=_onlineSource.ReadOnly.InventoryId }); }
        private void HideOnlinePanel()
        {
            bool wasOpen=IsOpen; IsOpen=false; _presenter?.EndOwnedDrag(); if (_panel!=null) _panel.SetActive(false);
            if (_onlineCaptured)
            {
                _onlineCaptured=false;
                EventBus<InventoryPresentationVisibilityEvent>.Publish(new InventoryPresentationVisibilityEvent { OwnerId=GetInstanceID(),Open=false });
            }
            if (wasOpen) ResetHeldUse();
        }
        private void DetachOnlineView()
        {
            HideOnlinePanel(); ClearOnlineCommand(); _onlineSource=null; _onlineAccess=null; _observedSnapshot=0;
            _onlineCloseRequested=_returnToInventory=_geometryCloseRequested=false; _wandId=null; LastInteractionStatus=string.Empty;
        }
    }
}
