using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class LiquidWorldRuntime
    {
        /// <summary>
        /// 按native液体格导出Dynamic与Stable的独立数量owner；不调用会轮转的反应采样或表面Patch接口。
        /// Stable逐格展开是此阶段的明确成本，由Source inspection预算约束，不能静默截断大湖。
        /// </summary>
        internal bool VisitReplicationState(ref WorldCaptureWriter writer)
        {
            if (_store == null) return true;
            if (!writer.Geometry(WorldMaterialDomain.Liquid, _origin, _cellSize, _chunkSize)) return false;
            for (int i = 0; i < _store.ResidentCellCount; i++)
            {
                Vector3Int cell = _store.GetResidentCellAt(i);
                for (int m = 0; m < MaterialCount; m++)
                {
                    if (!writer.Inspect()) return false;
                    var material = GetLiquidMaterial(m);
                    _store.TryGetQuantity(cell, material, out uint quantity);
                    if (!writer.Cell(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic, cell, (byte)material, quantity)) return false;
                }
            }
            if (_stable != null)
                for (int region = 0; region < _stable.ReplicationRegionSlotCount; region++)
                {
                    if (!writer.Inspect()) return false;
                    if (!_stable.TryGetReplicationRegion(region, out var material, out var minimum, out var maximum)) continue;
                    for (int z = minimum.z; z < maximum.z; z++)
                    for (int y = minimum.y; y < maximum.y; y++)
                    for (int x = minimum.x; x < maximum.x; x++)
                    {
                        if (!writer.Inspect()) return false;
                        var cell = new Vector3Int(x, y, z);
                        uint quantity = _stable.ReadReplicationQuantity(region, cell);
                        if (!writer.Cell(WorldMaterialDomain.Liquid, WorldMaterialStorage.Stable, cell, (byte)material, quantity)) return false;
                    }
                }
            for (int i = 0; i < _count; i++)
            {
                PendingDeposit pending = _pending[(_head + i) % _pending.Length];
                if (!writer.Pending(WorldMaterialDomain.Liquid, i, in pending.Request,
                    pending.Remaining, pending.ScanIndex, (uint)pending.WaitingTicks)) return false;
            }
            return true;
        }
    }
}
