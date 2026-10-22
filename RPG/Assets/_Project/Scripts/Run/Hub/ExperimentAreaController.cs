using Game.Core;
using UnityEngine;
using Unity.Profiling;

namespace Game.Run
{
    /// <summary>世界实验许可由玩家实际位置决定；统计只在交互时请求，不在Update遍历材料Owner。</summary>
    public sealed class ExperimentAreaController : MonoBehaviour
    {
        [SerializeField]private SinglePlayerInventorySession _session;
        [SerializeField]private HubDefinition _definition;
        [SerializeField]private Transform _player;
        [SerializeField]private HubAreaVolume _area;
        [SerializeField]private string _worldId="container-world";
        public bool IsInside=>_player!=null && (_area!=null?_area.Contains(_player.position):_definition!=null && _definition.ContainsExperiment(_player.position));
        public ulong LastResidentQuantity{get;private set;}
        public bool DepartureAcknowledged{get;private set;}
        public bool LastQueryReady{get;private set;}
        private bool _publishedPause,_lastPause;
        private static readonly ProfilerMarker UpdateMarker=new ProfilerMarker("Norn.Hub.ExperimentUpdate");
        public string ResidueMessage=>"离开据点清空世界元素；已经采回的材料保留在库存。";
        private void Awake(){if(_session!=null)_session.ExperimentArea=this;}
        private void OnEnable(){if(_session!=null)_session.ExperimentArea=this;EventBus<RaidStateChangedEvent>.Subscribe(OnRaid);EventBus<HubDepartureReviewRequestedEvent>.Subscribe(OnReview);}
        private void OnDisable(){EventBus<RaidStateChangedEvent>.Unsubscribe(OnRaid);EventBus<HubDepartureReviewRequestedEvent>.Unsubscribe(OnReview);if(_session!=null && _session.ExperimentArea==this)_session.ExperimentArea=null;}
        private void OnReview(HubDepartureReviewRequestedEvent request)
        {
            if(request.InventoryId!=_session?.ReadOnly?.InventoryId || _session.RunCoordinator?.State.Phase!=RaidPhase.HubReady)return;
            if(request.Operation!=HubDepartureReviewOperation.Review && request.Operation!=HubDepartureReviewOperation.Depart && request.Operation!=HubDepartureReviewOperation.ConfirmAndDepart)return;
            if(_session.HubInteraction==null || !_session.HubInteraction.TryGetStation(out var station) || station.Kind!=HubStationKind.Expedition)
            {EventBus<HubDepartureReviewResultEvent>.Publish(new HubDepartureReviewResultEvent{InventoryId=request.InventoryId,Message="请先前往地图入口附近出发。"});return;}
            // 新确认规则取消残留查询/审批；真正的资产、在途资源与保存Gate仍由Depart验证。
            LastQueryReady=true;LastResidentQuantity=0;
            bool departing=request.Operation!=HubDepartureReviewOperation.Review;
            // 同步结果先让UI关闭自己的暂停层，再在同一Run Assembly中执行可靠出发。
            EventBus<HubDepartureReviewResultEvent>.Publish(new HubDepartureReviewResultEvent{InventoryId=request.InventoryId,WorldReady=LastQueryReady,
                Departing=departing,ResidentQuantity=LastResidentQuantity,Message=LastQueryReady?ResidueMessage:"实验世界查询未就绪，请稍后再试。"});
            // 暂停解除后存档或资源事务仍可能失败，必须回报实际结果，不能让关闭面板冒充成功出发。
            if(departing && !_session.RunCoordinator.Depart())
                EventBus<HubDepartureReviewResultEvent>.Publish(new HubDepartureReviewResultEvent{InventoryId=request.InventoryId,WorldReady=LastQueryReady,
                    ResidentQuantity=LastResidentQuantity,Message=_session.RunCoordinator.Message});
        }
        private void OnRaid(RaidStateChangedEvent e){if(e.InventoryId==_session?.ReadOnly?.InventoryId && e.Phase==RaidPhase.HubReady){DepartureAcknowledged=false;_publishedPause=false;}}
        private void Update()
        {
            using(UpdateMarker.Auto())
            {
            bool paused=!IsInside;
            if(_session?.ReadOnly==null || _session.RunCoordinator?.State.Phase!=RaidPhase.HubReady || _publishedPause && _lastPause==paused)return;
            _lastPause=paused;_publishedPause=true;
            EventBus<ExperimentSimulationStateEvent>.Publish(new ExperimentSimulationStateEvent{WorldId=_worldId,Paused=paused});
            }
        }
        public bool CheckDeparture()
        {
            LastQueryReady=true;return true;
        }
        public bool AcknowledgeDeparture()
        {
            return CheckDeparture(); // 旧调用兼容；不再把确认状态作为玩法Gate。
        }
    }
}
