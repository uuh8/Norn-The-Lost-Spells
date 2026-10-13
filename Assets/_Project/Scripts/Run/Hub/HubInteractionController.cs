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
        private static readonly ProfilerMarker QueryMarker=new ProfilerMarker("Norn.Hub.RegionQuery");
        public HubDefinition Definition=>_definition;
        public bool TryGetStation(out HubDefinition.Station station)
        {
            using var sample=QueryMarker.Auto();station=default;
            if(_session?.ReadOnly==null || _session.RunCoordinator?.State.Phase!=RaidPhase.HubReady || _definition?.Stations==null || _player==null)return false;
            Vector3 point=_player.position;float best=float.PositiveInfinity;bool found=false;
            for(int i=0;i<_definition.Stations.Length;i++)
            {
                var candidate=_definition.Stations[i];HubAreaVolume area=candidate.Kind==HubStationKind.Shop?_shopArea:candidate.Kind==HubStationKind.Warehouse?_warehouseArea:null;
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
        private void OnEnable(){if(_session!=null)_session.HubInteraction=this;EventBus<HubInteractionRequestedEvent>.Subscribe(OnRequest);}
        private void OnDisable(){EventBus<HubInteractionRequestedEvent>.Unsubscribe(OnRequest);if(_session!=null && _session.HubInteraction==this)_session.HubInteraction=null;}
        public void Request(HubInteractionKind kind)=>EventBus<HubInteractionRequestedEvent>.Publish(new HubInteractionRequestedEvent{InventoryId=_session.ReadOnly?.InventoryId,Kind=kind});
        private void OnRequest(HubInteractionRequestedEvent command)
        {
            if(command.InventoryId!=_session?.ReadOnly?.InventoryId)return;
            bool success=TryGetStation(out var station) && (byte)station.Kind==(byte)command.Kind;
            EventBus<HubInteractionResultEvent>.Publish(new HubInteractionResultEvent{InventoryId=command.InventoryId,Kind=command.Kind,Success=success,
                Message=success?station.DisplayName:"请进入对应的商店或仓库交互区域。"});
        }
    }
}
