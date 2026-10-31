using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        /// <summary>
        /// 只枚举真实Store，不取Presentation Interest；Source先验证完整遍历再复制。
        /// Dictionary的具体类型枚举器是struct，不通过IEnumerable装箱，不创建临时keys数组。
        /// </summary>
        internal bool VisitReplicationState(ref WorldCaptureWriter writer)
        {
            if (!writer.Geometry(WorldMaterialDomain.LegacyCell, Origin, CellSize, ChunkSize)) return false;
            foreach (var pair in _store.Chunks)
            {
                ElementWorldChunk chunk = pair.Value;
                int size = chunk.ChunkSize;
                for (int i = 0; i < chunk.CellCount; i++)
                {
                    if (!writer.Inspect()) return false;
                    ElementCell cell = chunk.CurrentCells[i];
                    if (cell.IsEmpty) continue;
                    var local = new Vector3Int(i % size, i / size % size, i / (size * size));
                    Vector3Int native = ElementWorldCoordinates.ComposeGlobalCell(chunk.Key, local, size);
                    if (!writer.Cell(WorldMaterialDomain.LegacyCell, WorldMaterialStorage.Resident, native,
                        (byte)cell.MaterialKind, cell.Amount)) return false;
                }
            }
            if (_writeQueue != null)
                for (int i = 0; i < _writeQueue.Count; i++)
                {
                    _writeQueue.TryPeek(i, out ElementWriteRequest request);
                    if (!writer.Pending(WorldMaterialDomain.LegacyCell, i, in request, request.TotalAmount, 0, 0)) return false;
                }
            if (_gridLiquidRuntime != null && !_gridLiquidRuntime.VisitReplicationState(ref writer)) return false;
            if (_fixedSolidRuntime != null && !_fixedSolidRuntime.VisitReplicationState(ref writer)) return false;
            return _gasRuntime == null || _gasRuntime.VisitReplicationState(ref writer);
        }
    }
}
