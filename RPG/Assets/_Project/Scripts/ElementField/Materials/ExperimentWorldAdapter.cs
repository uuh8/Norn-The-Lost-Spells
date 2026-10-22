using Game.Core;
using Game.Materials;
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
        private void OnPause(ExperimentSimulationStateEvent e){if(e.WorldId==_worldId)_world.SetSimulationSuspended(e.Paused);}
        private void OnQuery(ExperimentWorldQueryEvent e)
        {
            var query=e.Query;if(query==null || query.Handled || query.WorldId!=_worldId)return;
            query.Handled=true;if(_world==null || !_world.IsInitialized || _world.InventoryLiquid==null)return;
            query.Ready=true;query.Total=0;
            for(byte key=1;key<=8;key++)
            {var id=(MaterialId)key;ulong quantity=_world.InventoryLiquid.MaterialQuantity(id)+_world.InventoryLiquid.PendingMaterialQuantity(id)
                +(_world.FixedSolids?.MaterialQuantity(id)??0)+(_world.FixedSolids?.PendingMaterialQuantity(id)??0)+(_world.Gases?.MaterialQuantity(id)??0)+(_world.Gases?.PendingMaterialQuantity(id)??0);
                query.Materials[key-1]=new MaterialAmount(key,quantity);query.Total+=quantity;}
        }
    }
}
