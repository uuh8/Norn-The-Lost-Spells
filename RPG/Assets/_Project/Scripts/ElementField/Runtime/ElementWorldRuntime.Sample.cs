using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime : IElementWorldMaterialReadOnly
    {
        public bool IsMaterialCellBlocked(Vector3Int worldCell)
        {
            if (!IsInitialized) return false;
            if (CellularSimulation != null) return IsSolid(worldCell);
            // 场景中的墙可能从未有任何 Legacy 元素进入，不能要求先分配材料页才能看见墙。
            // 环境采样排除角色与生成 Collider；两类材料固体均从其已经发布的数量或地形占据读取。
            if (GetTerrainMaterial(worldCell) != MaterialId.Empty || IsCellularBlockingCell(worldCell)
                || _solidBaker != null && _solidBaker.SampleEnvironmentSolidWorldCell(worldCell, _origin, CellSize)) return true;
            if (_executionMode == WorldExecutionMode.Replica)
                return _replicaSolids != null && _replicaSolids.BlocksWholeCell(worldCell);
            return _fixedSolidRuntime != null && _fixedSolidRuntime.BlocksCell(worldCell);
        }

        internal bool IsCollectionTargetSurface(RaycastHit hit, Vector3 direction, Vector3Int target, MaterialId material)
        {
            // 固体收取允许触及目标自己的外表面。生成的 Collider 可能合并整页；其它格的墙仍须截住射线。
            var backend = ResolveConversionBackend(material);
            if (backend != MaterialSimulationBackendKind.FixedSolid
                && (backend != MaterialSimulationBackendKind.Cellular || !ElementCellMaterialRules.OccupiesWholeCell(material))) return false;
            if (!hit.collider.TryGetComponent<GeneratedMaterialSolidCollider>(out _)) return false;
            var inside = hit.point + direction * (CellSize * .001f);
            return Vector3Int.FloorToInt((inside - _origin) / CellSize) == target;
        }

        public bool TrySampleMaterial(Vector3Int worldCell, MaterialId material, out ElementWorldMaterialSample sample)
        {
            sample = default;
            if (!IsInitialized || material == MaterialId.Empty) return false;

            if (TryReadQuantityCell(worldCell, material, out var source, out var nativeCell, out uint quantity, out uint capacity))
            {
                bool blocking = false;
                if (source == ElementWorldMaterialSource.Cellular)
                {
                    // 可与残留固体同格的气体不继承固体的阻挡属性。
                    blocking = ElementCellMaterialRules.OccupiesWholeCell(material) && IsCellularBlockingCell(worldCell);
                }
                if (source == ElementWorldMaterialSource.FixedSolid)
                {
                    if (_executionMode == WorldExecutionMode.Replica)
                    {
                        // 同格冰和残留可以共存。数量已从 Replica 安装态取到，这里只取当前材料自身的阻挡规则。
                        blocking = _replicaSolids.MaterialBlocksWholeCell(material);
                    }
                    else blocking = _fixedSolidRuntime.BlocksWholeCell(material);
                }
                sample = new ElementWorldMaterialSample(worldCell, nativeCell, material, source, quantity, capacity, 0, blocking);
                return true;
            }

            // 只读 Current，可查看休眠火格；角色的活跃域 Exposure 继续由原玩法入口裁决。
            var backend = ResolveConversionBackend(material);
            if ((_executionMode == WorldExecutionMode.Replica || backend == MaterialSimulationBackendKind.ElementCell)
                && TryGetCell(worldCell, out ElementCell legacy) && legacy.MaterialKind == material && legacy.Amount > 0)
            {
                sample = new ElementWorldMaterialSample(worldCell, worldCell, material, ElementWorldMaterialSource.LegacyCell,
                    0, 0, legacy.Amount, false);
                return true;
            }
            if (backend == MaterialSimulationBackendKind.GpuPbfLiquid && _materialQuery != null
                && _materialQuery.TryGetAmount(worldCell, material, out byte projection) && projection > 0)
            {
                sample = new ElementWorldMaterialSample(worldCell, worldCell, material, ElementWorldMaterialSource.GpuLiquidProjection,
                    0, 0, projection, false);
                return true;
            }
            if (GetTerrainMaterial(worldCell) != material) return false;
            sample = new ElementWorldMaterialSample(worldCell, worldCell, material, ElementWorldMaterialSource.Terrain,
                0, 0, 0, true);
            return true;
        }

        /// <summary>
        /// 数量、原生地址和容量必须由同一所有者回答。库存瞄准也用本入口，但不触发 Physics 查询、
        /// 表面重建、创建空页或唤醒休眠；单独的显示/接触强度不混入 GMU。
        /// </summary>
        private bool TryReadQuantityCell(Vector3Int worldCell, MaterialId material, out ElementWorldMaterialSource source,
            out Vector3Int nativeCell, out uint quantity, out uint capacity)
        {
            source = ElementWorldMaterialSource.None;
            nativeCell = worldCell;
            quantity = capacity = 0;
            if (!IsInitialized || material == MaterialId.Empty) return false;

            if (_executionMode == WorldExecutionMode.Replica)
            {
                // Replica 无 Simulation Route/写 Owner。只读已安装的各域数据，不能回落到本机 Solver。
                if (_replicaSolids != null && _replicaSolids.TryGetQuantity(worldCell, material, out quantity))
                { source = ElementWorldMaterialSource.FixedSolid; capacity = _replicaSolids.CellCapacity; return true; }
                if (_replicaGases != null && _replicaGases.TryGetQuantity(worldCell, material, out quantity))
                { source = ElementWorldMaterialSource.GridGas; capacity = _replicaGases.CellCapacity; return true; }
                nativeCell = _replicaVolumeSettings.WorldCellToLiquidCell(worldCell);
                if (_replicaLiquids == null || !_replicaLiquids.TryGetQuantity(nativeCell, material, out quantity)) return false;
                source = ElementWorldMaterialSource.GridLiquid;
                capacity = (uint)_replicaLiquids.CellCapacityGmu;
                return true;
            }

            if (OwnsCellularMaterial(material))
            {
                capacity = CellularCellCapacity;
                if (RestingLiquids != null && RestingLiquids.TryGetQuantity(worldCell, material, out quantity))
                {
                    source = ElementWorldMaterialSource.RestingLiquid;
                    return true;
                }
                source = ElementWorldMaterialSource.Cellular;
                return CellularSimulation.TryGetQuantity(worldCell, material, out quantity);
            }
            switch (ResolveConversionBackend(material))
            {
                case MaterialSimulationBackendKind.GridLiquid when _gridLiquidRuntime != null:
                    // 整数 FloorDivide 确保负坐标也只映射到唯一粗格。Stable 和 Dynamic 由原 Owner 合并。
                    source = ElementWorldMaterialSource.GridLiquid;
                    nativeCell = _gridLiquidRuntime.WorldCellToLiquidCell(worldCell);
                    capacity = (uint)_gridLiquidRuntime.CellCapacityGmu;
                    return _gridLiquidRuntime.TryGetQuantity(nativeCell, material, out quantity);
                case MaterialSimulationBackendKind.FixedSolid when _fixedSolidRuntime != null:
                    source = ElementWorldMaterialSource.FixedSolid;
                    capacity = _fixedSolidRuntime.CellCapacity;
                    return _fixedSolidRuntime.TryGetQuantity(worldCell, material, out quantity);
                case MaterialSimulationBackendKind.GridGas when _gasRuntime != null:
                    source = ElementWorldMaterialSource.GridGas;
                    capacity = _gasRuntime.CellCapacity;
                    return _gasRuntime.TryGetQuantity(worldCell, material, out quantity);
                default:
                    return false;
            }
        }
    }
}
