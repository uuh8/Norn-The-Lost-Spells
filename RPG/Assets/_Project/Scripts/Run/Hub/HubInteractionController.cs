using Game.Core;
using UnityEngine;
using Unity.Profiling;

namespace Game.Run
{
    /// <summary>F区域交互；根命令在Run重新检查阶段和实际范围，UI不能远程开仓库/交易。</summary>
    public sealed class HubInteractionController : MonoBehaviour
    {
        [SerializeField]private SinglePlayerInventorySession _session;
        [SerializeField]private HubDefinition _definition;
        [SerializeField]private Transform _player;
        [SerializeField]private HubAreaVolume _warehouseArea;
        [SerializeField]private HubAreaVolume _shopArea;
        [SerializeField]private HubAreaVolume _researchArea;
        [SerializeField]private RunPauseCoordinator _researchPause;
        private readonly string _researchCaller = System.Guid.NewGuid().ToString("N");
        private ulong _researchSequence, _researchGrant, _consumedGrant;
        private SinglePlayerShopSession _accountShop;
        /// <summary>商店适配主动登记；不靠Scene名字开放，也不要求给既有Scene新增序列化跨组件接线。</summary>
        internal void BindAccountShop(SinglePlayerShopSession shop)
        {
            if (shop != null && shop.InventorySession == _session && shop.gameObject.scene == gameObject.scene) _accountShop = shop;
        }
        internal void ReleaseAccountShop(SinglePlayerShopSession shop) { if (_accountShop == shop) _accountShop = null; }
        private bool AccountShopAvailable => _accountShop != null && _accountShop.isActiveAndEnabled && _accountShop.AccountEntryAvailable;
        private bool AccountInteractionAllowed(HubInteractionKind kind) => kind == HubInteractionKind.Warehouse
            || kind == HubInteractionKind.Expedition || kind == HubInteractionKind.Shop && AccountShopAvailable;
        // 老教学的Legacy程序不能借V2研究台展示另一套执行语义。
        public bool CanResearch => isActiveAndEnabled && _session != null && _session.ReadOnly != null && _session.UseSpellProgrammingV2
            && !_session.IsAwayRuntime && !_session.RaidWriteLocked && !_session.Profile.RaidActionsFrozen
            && _researchPause!=null && (_researchPause.ActiveReasons & (RunPauseReason.SceneTransition|RunPauseReason.TerminalResult))==0
            && _session.RunCoordinator != null && _session.RunCoordinator.State.Phase == RaidPhase.HubReady
            && _researchArea != null && _player != null && _researchArea.Contains(_player.position);
        private static readonly ProfilerMarker QueryMarker=new ProfilerMarker("Norn.Hub.RegionQuery");
        public HubDefinition Definition=>_definition;
        private bool TryGetInteractionIdentity(out string inventoryId, out bool account)
        {
            inventoryId = null; account = false;
            if (_session == null) return false;
            if (_session.AccountInventoryReadOnly)
            {
                account = true;
                if (!_session.TryGetAccountHubSummary(out var summary)) return false;
                inventoryId = summary.InventoryId; return true;
            }
            if (_session.ReadOnly == null || _session.RunCoordinator?.State.Phase != RaidPhase.HubReady) return false;
            inventoryId = _session.ReadOnly.InventoryId; return true;
        }
        public bool TryGetStation(out HubDefinition.Station station)
        {
            using var sample=QueryMarker.Auto();station=default;
            if(!TryGetInteractionIdentity(out _, out bool account) || _definition?.Stations==null || _player==null)return false;
            if(account && (!isActiveAndEnabled || !gameObject.scene.isLoaded
                || _session.gameObject.scene!=gameObject.scene || _player.gameObject.scene!=gameObject.scene))return false;
            Vector3 point=_player.position;float best=float.PositiveInfinity;bool found=false;
            for(int i=0;i<_definition.Stations.Length;i++)
            {
                var candidate=_definition.Stations[i];
                // 账号商店只有静态配置已冻结、账号HTTP适配已订阅时才出现；研究台仍维持独立未接通的门。
                if(account && candidate.Kind!=HubStationKind.Warehouse && candidate.Kind!=HubStationKind.Expedition
                    && !(candidate.Kind == HubStationKind.Shop && AccountShopAvailable))continue;
                HubAreaVolume area=candidate.Kind==HubStationKind.Shop?_shopArea:candidate.Kind==HubStationKind.Warehouse?_warehouseArea:
                    candidate.Kind==HubStationKind.SpellResearch?_researchArea:null;
                if (candidate.Kind==HubStationKind.SpellResearch && !CanResearch) continue;
                if(area!=null)
                {
                    // 已配置区域即为唯一空间规则：区外/停用时不得退回旧2m点，否则作者看到的边界不可信。
                    if(!area.Contains(point))continue;candidate.Position=area.Center;
                }
                else
                {
                    float radius=_definition.InteractionRadius;
                    if(!float.IsFinite(radius) || radius<=0 || (candidate.Position-point).sqrMagnitude>radius*radius)continue;
                }
                float distance=(candidate.Position-point).sqrMagnitude;
                if(!float.IsFinite(distance) || distance>=best)continue;
                best=distance;station=candidate;found=true;
            }
            return found;
        }
        private void Awake(){if(_session!=null)_session.HubInteraction=this;}
        private void OnEnable(){if(_session!=null)_session.HubInteraction=this;EventBus<HubInteractionRequestedEvent>.Subscribe(OnRequest);EventBus<HubDepartureReviewRequestedEvent>.Subscribe(OnDepartureReview);}
        private void OnDisable()
        {
            // 停用期间不能保留尚未消费的打开许可；重新启用必须由新的F请求获得授权。
            if(_researchSequence>_consumedGrant)_consumedGrant=_researchSequence;
            EventBus<HubInteractionRequestedEvent>.Unsubscribe(OnRequest);EventBus<HubDepartureReviewRequestedEvent>.Unsubscribe(OnDepartureReview);
            if(_session!=null && _session.HubInteraction==this)_session.HubInteraction=null;
        }
        private void OnDepartureReview(HubDepartureReviewRequestedEvent request)
        {
            if(_session!=null && _session.AccountInventoryReadOnly)
            {
                if(TryGetInteractionIdentity(out var accountId, out _) && request.InventoryId==accountId)
                    EventBus<HubDepartureReviewResultEvent>.Publish(new HubDepartureReviewResultEvent{InventoryId=accountId,
                        Message="单人服务器入场尚未接通；原本地资产入口保持禁用。"});
                return;
            }
            // 移走实验区后，远征确认仍由据点负责；旧实验测试场景有原Controller时避免双处理。
            if(_session?.ExperimentArea!=null||request.InventoryId!=_session?.ReadOnly?.InventoryId
                ||_session.RunCoordinator?.State.Phase!=RaidPhase.HubReady)return;
            if(request.Operation!=HubDepartureReviewOperation.Review&&request.Operation!=HubDepartureReviewOperation.Depart&&request.Operation!=HubDepartureReviewOperation.ConfirmAndDepart)return;
            if(!TryGetStation(out var station)||station.Kind!=HubStationKind.Expedition)
            {EventBus<HubDepartureReviewResultEvent>.Publish(new HubDepartureReviewResultEvent{InventoryId=request.InventoryId,Message="请先前往地图入口附近出发。"});return;}
            bool depart=request.Operation!=HubDepartureReviewOperation.Review;
            EventBus<HubDepartureReviewResultEvent>.Publish(new HubDepartureReviewResultEvent{InventoryId=request.InventoryId,WorldReady=true,Departing=depart,Message="出发保存携带物，世界元素不随转场保留。"});
            if(depart&&!_session.RunCoordinator.Depart())EventBus<HubDepartureReviewResultEvent>.Publish(new HubDepartureReviewResultEvent{InventoryId=request.InventoryId,WorldReady=true,Message=_session.RunCoordinator.Message});
        }
        public void Request(HubInteractionKind kind)
        {
            if (!TryGetInteractionIdentity(out var inventoryId, out bool account)) return;
            if(account && !AccountInteractionAllowed(kind))
            {PublishAccountRejection(inventoryId,kind,null,0);return;}
            EventBus<HubInteractionRequestedEvent>.Publish(new HubInteractionRequestedEvent { InventoryId=inventoryId,
                Kind=kind, CallerId=kind==HubInteractionKind.SpellResearch?_researchCaller:null,
                RequestId=kind==HubInteractionKind.SpellResearch?++_researchSequence:0 });
        }
        public bool TryConsumeResearchGrant(in HubInteractionResultEvent result)
        {
            if (!result.Success || result.Kind != HubInteractionKind.SpellResearch || result.InventoryId != _session?.ReadOnly?.InventoryId
                || result.CallerId != _researchCaller || result.RequestId == 0 || result.RequestId != _researchGrant || result.RequestId != _researchSequence
                || result.RequestId <= _consumedGrant || !CanResearch) return false;
            _consumedGrant=result.RequestId; return true;
        }
        private void OnRequest(HubInteractionRequestedEvent command)
        {
            if(!TryGetInteractionIdentity(out var inventoryId,out bool account)||command.InventoryId!=inventoryId)return;
            if(account && !AccountInteractionAllowed(command.Kind))
            {PublishAccountRejection(inventoryId,command.Kind,command.CallerId,command.RequestId);return;}
            bool success=TryGetStation(out var station) && (byte)station.Kind==(byte)command.Kind;
            if(command.Kind==HubInteractionKind.SpellResearch)
            {
                success &= CanResearch && command.CallerId==_researchCaller && command.RequestId==_researchSequence && command.RequestId>_researchGrant;
                if(success)_researchGrant=command.RequestId;
            }
            EventBus<HubInteractionResultEvent>.Publish(new HubInteractionResultEvent{InventoryId=command.InventoryId,Kind=command.Kind,Success=success,
                CallerId=command.CallerId,RequestId=command.RequestId,Message=success?station.DisplayName:"请进入对应交互区域。"});
        }
        private static void PublishAccountRejection(string inventoryId,HubInteractionKind kind,string caller,ulong request)
            =>EventBus<HubInteractionResultEvent>.Publish(new HubInteractionResultEvent{InventoryId=inventoryId,Kind=kind,
                CallerId=caller,RequestId=request,Success=false,Message="该账号交易/研究尚未接通服务器，原本地资产操作保持禁用。"});
    }
}
