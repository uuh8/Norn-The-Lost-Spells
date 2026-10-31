using Game.Core;

namespace Game.ElementField
{
    public sealed partial class GasWorldRuntime
    {
        /// <summary>气体Current与尚未找到空间的Pending是不同owner；枚举不驱动Solver或Dissipation。</summary>
        internal bool VisitReplicationState(ref WorldCaptureWriter writer)
        {
            if (_store == null) return true;
            if (!writer.Geometry(WorldMaterialDomain.Gas, _origin, _size, _store.ChunkSize)) return false;
            for (int i = 0; i < _store.ResidentCellCount; i++)
            {
                if (!writer.Inspect()) return false;
                if (_store.TryGetCell(i, out MaterialVolumeCell cell)
                    && !writer.Cell(WorldMaterialDomain.Gas, WorldMaterialStorage.Resident, cell.Cell, (byte)cell.Material, cell.Quantity)) return false;
            }
            if (_pending != null)
                for (int i = 0; i < _pending.Count; i++)
                {
                    _pending.TryPeek(i, out MaterialPhasePendingDeposit pending);
                    if (!writer.Pending(WorldMaterialDomain.Gas, i, in pending.Request, pending.Remaining, pending.Cursor, pending.WaitingTicks)) return false;
                }
            return true;
        }
    }
}
