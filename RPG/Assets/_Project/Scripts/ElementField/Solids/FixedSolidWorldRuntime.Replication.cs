using Game.Core;

namespace Game.ElementField
{
    public sealed partial class FixedSolidWorldRuntime
    {
        /// <summary>只读Current数量及FIFO Pending；不访问Mesh/Collider，不触发几何重建。</summary>
        internal bool VisitReplicationState(ref WorldCaptureWriter writer)
        {
            if (_store == null) return true;
            if (!writer.Geometry(WorldMaterialDomain.Solid, _origin, _size, _store.ChunkSize)) return false;
            for (int i = 0; i < _store.ResidentCellCount; i++)
            {
                if (!writer.Inspect()) return false;
                if (_store.TryGetCell(i, out MaterialVolumeCell cell)
                    && !writer.Cell(WorldMaterialDomain.Solid, WorldMaterialStorage.Resident, cell.Cell, (byte)cell.Material, cell.Quantity)) return false;
            }
            if (_pending != null)
                for (int i = 0; i < _pending.Count; i++)
                {
                    _pending.TryPeek(i, out MaterialPhasePendingDeposit pending);
                    if (!writer.Pending(WorldMaterialDomain.Solid, i, in pending.Request, pending.Remaining, pending.Cursor, pending.WaitingTicks)) return false;
                }
            return true;
        }
    }
}
