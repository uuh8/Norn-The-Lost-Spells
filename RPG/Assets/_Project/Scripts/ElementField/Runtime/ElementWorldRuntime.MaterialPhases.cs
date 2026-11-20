using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private void InitializeMaterialPhases(IMaterialSimulationRouteReadOnly routes)
        {
            bool solid = false, gas = false;
            for (int i = 1; i < 256; i++)
                if (routes.TryResolve((MaterialId)i, out var backend))
                { solid |= backend == MaterialSimulationBackendKind.FixedSolid; gas |= backend == MaterialSimulationBackendKind.GridGas; }
            if (solid && (_fixedSolidRuntime == null || !_fixedSolidRuntime.TryInitialize(this,
                    _solidBaker, _origin, _profile.CellSize, _profile.ChunkSize)))
                throw new InvalidOperationException("FixedSolid 路由需要有效的 FixedSolidWorldRuntime／Profile。");
            if (gas && (_gasRuntime == null || !_gasRuntime.TryInitialize(this,
                    _solidBaker, _origin, _profile.CellSize, _profile.ChunkSize)))
                throw new InvalidOperationException("GridGas 路由需要有效的 GasWorldRuntime／Profile。");
            for (int i = 1; i < 256; i++)
                if (routes.TryResolve((MaterialId)i, out var backend)
                    && (backend == MaterialSimulationBackendKind.FixedSolid && !_fixedSolidRuntime.Supports((MaterialId)i)
                        || backend == MaterialSimulationBackendKind.GridGas && !_gasRuntime.Supports((MaterialId)i)))
                    throw new InvalidOperationException("固体／气体路由缺少材料行为配置。");
            _solidBaker.SetMaterialSolids(_fixedSolidRuntime, _origin, _profile.CellSize);
        }
        public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity)
        {
            return TryReadQuantityCell(cell, material, out _, out _, out quantity, out _);
        }
        public bool HasFluidOrGasAt(Vector3Int cell)
        {
            if (!IsInitialized) return false;
            if (_executionMode == Game.Core.WorldExecutionMode.Replica)
            {
                if (_replicaStore == null) return false;
                // 两个域均已按坐标排序。取一个已安装的组分即可判占据，无需逐材料猜测身份。
                return _replicaStore.TryGetFirstCell(Game.Core.WorldMaterialDomain.Gas, cell, out _)
                    || _replicaStore.TryGetFirstCell(Game.Core.WorldMaterialDomain.Liquid,
                        _replicaVolumeSettings.WorldCellToLiquidCell(cell), out _);
            }
            if (CellularSimulation != null) return CellularSimulation.HasFluidOrGasAt(cell)
                || RestingLiquids != null && RestingLiquids.TryGetCell(cell, out _, out _);
            if (_gasRuntime != null)
                for (int i = 0; i < _gasRuntime.MaterialCount; i++)
                {
                    MaterialId material = _gasRuntime.GetMaterial(i);
                    if (_gasRuntime.TryGetQuantity(cell, material, out _)) return true;
                }
            if (_gridLiquidRuntime == null) return false;
            var nativeCell = _gridLiquidRuntime.WorldCellToLiquidCell(cell);
            for (int i = 0; i < _gridLiquidRuntime.MaterialCount; i++)
            {
                MaterialId material = _gridLiquidRuntime.GetLiquidMaterial(i);
                if (_gridLiquidRuntime.TryGetQuantity(nativeCell, material, out _)) return true;
            }
            return false;
        }
        public void NotifyMaterialTopologyChanged(Vector3Int cell)
        {
            WakeCellularTerrainChange(cell);
            if (CellularSimulation == null)
            {
                _gridLiquidRuntime?.NotifyMaterialTopologyChanged(cell);
                _gasRuntime?.NotifyTopologyChanged(cell);
            }
            if (_store == null) return;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _profile.ChunkSize, out ElementChunkKey key, out Vector3Int local);
            if (!_store.TryGetChunk(key, out ElementWorldChunk chunk)) return;
            int index = ElementFieldCoordinates.ToIndex(local, chunk.Grid.Dimensions);
            chunk.SolidMask[index] = IsCellularBlockingCell(cell) || _solidBaker.SampleSolidWorldCell(cell, _origin, _profile.CellSize);
        }
        public bool TryPrepareMaterialTopologyChange(Vector3Int worldCell, bool becomesSolid)
        {
            // Stable 液体区域可能正在借用此格作为池底。先预约它的局部拓扑交接，
            // 失败就保留冰，不能先拆 Collider 再让湖盆继续使用旧支撑。
            return CellularSimulation != null || _gridLiquidRuntime == null || _gridLiquidRuntime.TryPrepareStaticCellChange(
                _gridLiquidRuntime.WorldCellToLiquidCell(worldCell), becomesSolid);
        }
    }
}
