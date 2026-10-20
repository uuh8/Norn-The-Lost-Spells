using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        // Export 仍属于玩家资产，不能记作燃烧/消散。随 World 生命周期释放，不保存箱庭续玩。
        private readonly ulong[] _inventoryExports = new ulong[256];
        private readonly ulong[] _inventoryImports = new ulong[256];
        internal LiquidWorldRuntime InventoryLiquid => _gridLiquidRuntime;
        internal void RecordInventoryExport(MaterialId material, ulong quantity) => _inventoryExports[(byte)material] += quantity;
        public ulong ExportedMaterialQuantity(MaterialId material) => _inventoryExports[(byte)material];
        internal void RecordInventoryImport(MaterialId material, ulong quantity) => _inventoryImports[(byte)material] += quantity;
        internal void RetainInventoryDepositActivity(Vector3 point,float radius)
        {
            // 真实投掷命中可远离玩家活动圈，沿用法术写入租约；圈只调度模拟，不能拒绝合法投放。
            var request=new ElementWriteRequest(point,MaterialId.Water,0,radius,false);
            RetainWriteActivity(in request);
        }
        public ulong ImportedMaterialQuantity(MaterialId material) => _inventoryImports[(byte)material];
        // 独立数量验收只暂停 Solver，仍维护活动圈和 Registry；禁用组件会触发 OnDisable 清空活动域。
        public bool SimulationPausedForDebug { get; private set; }
        public void SetSimulationPausedForDebug(bool paused) => SimulationPausedForDebug = paused;
        public bool SimulationSuspended {get;private set;}
        // 据点空间暂停保留真实Owner/Registry/查询/绘制；它不等于释放世界或清空材料。
        public void SetSimulationSuspended(bool suspended)=>SimulationSuspended=suspended;
    }
}
