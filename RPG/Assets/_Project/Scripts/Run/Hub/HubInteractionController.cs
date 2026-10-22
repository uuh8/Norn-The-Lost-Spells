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
        // 老教学的Legacy程序不能借V2研究台展示另一套执行语义。
        public bool CanResearch => isActiveAndEnabled && _session != null && _session.ReadOnly != null && _session.UseSpellProgrammingV2
            && !_session.IsAwayRuntime && !_session.RaidWriteLocked && !_session.Profile.RaidActionsFrozen
            && _researchPause!=null && (_researchPause.ActiveReasons & (RunPauseReason.SceneTransition|RunPauseReason.TerminalResult))==0
            && _session.RunCoordinator != null && _session.RunCoordinator.State.Phase == RaidPhase.HubReady
            && _researchArea != null && _player != null && _researchArea.Contains(_player.position);
        private static readonly ProfilerMarker QueryMarker=new ProfilerMarker("Norn.Hub.RegionQuery");
        public HubDefinition Definition=>_definition;
        public bool TryGetStation(out HubDefinition.Station station)
        {
            using var sample=QueryMarker.Auto();station=default;
            if(_session?.ReadOnly==null || _session.RunCoordinator?.State.Phase!=RaidPhase.HubReady || _definition?.Stations==null || _player==null)return false;
            Vector3 point=_player.position;float best=float.PositiveInfinity;bool found=false;
            for(int i=0;i<_definition.Stations.Length;i++)
            {
                var candidate=_definition.Stations[i];HubAreaVolume area=candidate.Kind==HubStationKind.Shop?_shopArea:candidate.Kind==HubStationKind.Warehouse?_warehouseArea:
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
            if (_session?.ReadOnly == null) return;
            EventBus<HubInteractionRequestedEvent>.Publish(new HubInteractionRequestedEvent { InventoryId=_session.ReadOnly.InventoryId,
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
            if(command.InventoryId!=_session?.ReadOnly?.InventoryId)return;
            bool success=TryGetStation(out var station) && (byte)station.Kind==(byte)command.Kind;
            if(command.Kind==HubInteractionKind.SpellResearch)
            {
                success &= CanResearch && command.CallerId==_researchCaller && command.RequestId==_researchSequence && command.RequestId>_researchGrant;
                if(success)_researchGrant=command.RequestId;
            }
            EventBus<HubInteractionResultEvent>.Publish(new HubInteractionResultEvent{InventoryId=command.InventoryId,Kind=command.Kind,Success=success,
                CallerId=command.CallerId,RequestId=command.RequestId,Message=success?station.DisplayName:"请进入对应交互区域。"});
        }
    }
}
