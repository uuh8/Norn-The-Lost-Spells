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
            quantity = 0;
            if (!IsInitialized || !_materialRoutes.TryResolve(material, out var backend)) return false;
            switch (backend)
            {
                case MaterialSimulationBackendKind.FixedSolid:
                    return _fixedSolidRuntime != null && _fixedSolidRuntime.TryGetQuantity(cell, material, out quantity);
                case MaterialSimulationBackendKind.GridGas:
                    return _gasRuntime != null && _gasRuntime.TryGetQuantity(cell, material, out quantity);
                case MaterialSimulationBackendKind.GridLiquid:
                    return _gridLiquidRuntime != null && _gridLiquidRuntime.TryGetWorldQuantity(cell, material, out quantity);
                default:
                    // Fire 强度和 PBF 投影不当成可用于材料转换记账的 GMU。
                    return false;
            }
        }
        public bool HasFluidOrGasAt(Vector3Int cell)
        {
            if (_gasRuntime != null)
                for (int i = 0; i < _gasRuntime.MaterialCount; i++)
                    if (_gasRuntime.TryGetQuantity(cell, _gasRuntime.GetMaterial(i), out _)) return true;
            return _gridLiquidRuntime != null && (_gridLiquidRuntime.TryGetWorldQuantity(cell, MaterialId.Water, out _)
                || _gridLiquidRuntime.TryGetWorldQuantity(cell, MaterialId.Poison, out _)
                || _gridLiquidRuntime.TryGetWorldQuantity(cell, MaterialId.Sticky, out _));
        }
        public void NotifyMaterialTopologyChanged(Vector3Int cell)
        {
            _gridLiquidRuntime?.NotifyMaterialTopologyChanged(cell);
            _gasRuntime?.NotifyTopologyChanged(cell);
            if (_store == null) return;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _profile.ChunkSize, out ElementChunkKey key, out Vector3Int local);
            if (!_store.TryGetChunk(key, out ElementWorldChunk chunk)) return;
            int index = ElementFieldCoordinates.ToIndex(local, chunk.Grid.Dimensions);
            chunk.SolidMask[index] = _solidBaker.SampleSolidWorldCell(cell, _origin, _profile.CellSize);
        }
        public bool TryPrepareMaterialTopologyChange(Vector3Int worldCell, bool becomesSolid)
        {
            // Stable 液体区域可能正在借用此格作为池底。先预约它的局部拓扑交接，
            // 失败就保留冰，不能先拆 Collider 再让湖盆继续使用旧支撑。
            return _gridLiquidRuntime == null || _gridLiquidRuntime.TryPrepareStaticCellChange(
                _gridLiquidRuntime.WorldCellToLiquidCell(worldCell), becomesSolid);
        }
    }
}
