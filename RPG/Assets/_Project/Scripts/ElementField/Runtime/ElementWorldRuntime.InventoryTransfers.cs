using System;
using Game.Core;
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
        // 配置选择纯元胞模式时，场景里可以保留旧组件作为资源引用；未初始化的 Store 不参与预约或几何发布。
        internal FixedSolidWorldRuntime InventorySolids => _fixedSolidRuntime != null && _fixedSolidRuntime.IsMaterialInitialized ? _fixedSolidRuntime : null;
        internal GasWorldRuntime InventoryGases => _gasRuntime != null && _gasRuntime.IsMaterialInitialized ? _gasRuntime : null;
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

        /// <summary>
        /// 实验区在交互时读取已提交的世界数量。压缩液体与活动元胞各自持有不相交的 GMU；
        /// 固定地形和 Fire 强度均不进入数量表。事务中拒绝快照，以免把暂扣的量报告为已离开世界。
        /// </summary>
        internal bool TryCopyExperimentQuantities(MaterialAmount[] quantities, out ulong total, out ulong pending)
        {
            total = pending = 0;
            if (quantities == null || quantities.Length < MaterialAmount.LastLocalInventoryKey) return false;
            Array.Clear(quantities, 0, quantities.Length);
            if (!IsInitialized || IsSimulationFaulted || IsWorldStepInProgress || ExecutionMode == WorldExecutionMode.Replica)
                return false;
            var liquid = InventoryLiquid != null && InventoryLiquid.IsFluidInitialized ? InventoryLiquid : null;
            var solid = InventorySolids;
            var gas = InventoryGases;
            if (CellularSimulation == null && liquid == null && solid == null && gas == null) return false;

            try
            {
                checked
                {
                    for (byte key = 1; key <= MaterialAmount.LastLocalInventoryKey; key++)
                    {
                        if (!MaterialAmount.IsLocalInventoryMaterial(key)) continue;
                        var material = (MaterialId)key;
                        ulong quantity;
                        if (OwnsCellularMaterial(material))
                            quantity = CellularSimulation.GetTotalQuantity(material) + (RestingLiquids?.GetTotalQuantity(material) ?? 0);
                        else
                        {
                            ulong queued = (liquid?.PendingMaterialQuantity(material) ?? 0) + (solid?.PendingMaterialQuantity(material) ?? 0)
                                + (gas?.PendingMaterialQuantity(material) ?? 0);
                            pending += queued;
                            quantity = (liquid?.MaterialQuantity(material) ?? 0) + (solid?.MaterialQuantity(material) ?? 0)
                                + (gas?.MaterialQuantity(material) ?? 0) + queued;
                        }
                        quantities[key - 1] = new MaterialAmount(key, quantity);
                        total += quantity;
                    }
                    // 元胞写命令到下一次 Tick 才会尝试落格；只窥视 FIFO，不能提前应用或把 Legacy 强度加为 GMU。
                    if (CellularSimulation != null && _writeQueue != null)
                        for (int i = 0; i < _writeQueue.Count; i++)
                        {
                            if (!_writeQueue.TryPeek(i, out var request) || !OwnsCellularMaterial(request.MaterialKind)) continue;
                            byte key = (byte)request.MaterialKind;
                            if (!MaterialAmount.IsLocalInventoryMaterial(key)) continue;
                            quantities[key - 1].Quantity += request.TotalAmount;
                            pending += request.TotalAmount;
                            total += request.TotalAmount;
                        }
                }
                return true;
            }
            catch (OverflowException)
            {
                // 可压缩的大库区可能超过整个查询的 ulong 范围。返回不可查询，避免回绕后显示为空。
                Array.Clear(quantities, 0, quantities.Length);
                total = pending = 0;
                return false;
            }
        }
        // 独立数量验收只暂停 Solver，仍维护活动圈和 Registry；禁用组件会触发 OnDisable 清空活动域。
        public bool SimulationPausedForDebug { get; private set; }
        public void SetSimulationPausedForDebug(bool paused) => SimulationPausedForDebug = paused;
        public bool SimulationSuspended {get;private set;}
        // 据点空间暂停保留真实Owner/Registry/查询/绘制；它不等于释放世界或清空材料。
        public void SetSimulationSuspended(bool suspended)=>SimulationSuspended=suspended;
    }
}
