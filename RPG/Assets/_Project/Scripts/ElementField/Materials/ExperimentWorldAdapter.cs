using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>统计实际数量Owner，移除测试供给后空世界自然为0；不会给仓库创造试验材料。</summary>
    public sealed class ExperimentWorldAdapter : MonoBehaviour
    {
        [SerializeField]private ElementWorldRuntime _world;
        [SerializeField]private string _worldId="container-world";
        private void OnEnable(){EventBus<ExperimentWorldQueryEvent>.Subscribe(OnQuery);EventBus<ExperimentSimulationStateEvent>.Subscribe(OnPause);}
        private void OnDisable(){EventBus<ExperimentWorldQueryEvent>.Unsubscribe(OnQuery);EventBus<ExperimentSimulationStateEvent>.Unsubscribe(OnPause);}
        private void OnPause(ExperimentSimulationStateEvent e){if(e.WorldId==_worldId && _world!=null)_world.SetSimulationSuspended(e.Paused);}
        private void OnQuery(ExperimentWorldQueryEvent e)
        {
            var query=e.Query;if(query==null || query.Handled || query.WorldId!=_worldId)return;
            query.Handled = true;
            query.Total = query.PendingQuantity = 0;
            query.Ready = _world != null && _world.TryCopyExperimentQuantities(query.Materials, out query.Total, out query.PendingQuantity);
        }
    }
}
