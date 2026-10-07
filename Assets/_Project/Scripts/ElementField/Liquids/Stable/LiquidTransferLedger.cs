using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>在一个模拟 Tick 内验证、提交或取消跨 owner 转移；Prepared 不算成第二份量。</summary>
    public sealed class LiquidTransferLedger
    {
        public ulong CommittedGmu { get; private set; }
        public ulong AbsorbedGmu { get; private set; }
        public ulong SectorCommittedGmu { get; private set; }
        public uint CancelledTransfers { get; private set; }
        public uint VersionConflicts { get; private set; }

        public void RecordAbsorption(uint quantity) => AbsorbedGmu += quantity;

        public uint TryMove(StableLiquidRegion source, StableLiquidRegion destination,
            uint requested)
        {
            if (source == null || destination == null || !source.IsActive
                || !destination.IsActive || source.Material != destination.Material
                || requested == 0) return 0;
            uint sourceVersion = source.Version;
            uint destinationVersion = destination.Version;
            uint accepted = (uint)System.Math.Min((ulong)requested,
                System.Math.Min(source.Quantity, destination.Capacity - destination.Quantity));
            if (accepted == 0) { CancelledTransfers++; return 0; }
            if (source.Version != sourceVersion || destination.Version != destinationVersion)
            { VersionConflicts++; return 0; }
            // 接收方先获得数量；同一主线程调用内若来源提交失败，撤销接收方。
            if (!destination.TryAdd(accepted)) { CancelledTransfers++; return 0; }
            if (!source.TryRemove(accepted))
            {
                destination.TryRemove(accepted);
                CancelledTransfers++;
                return 0;
            }
            CommittedGmu += accepted;
            SectorCommittedGmu += accepted;
            return accepted;
        }

        public bool TryMove(StableLiquidRegion source, Vector3Int target, uint requested,
            LiquidVolumeStore destination, LiquidActivityScheduler activity)
        {
            if (source == null || destination == null || requested == 0) return false;
            uint version = source.Version;
            uint available = (uint)System.Math.Min((ulong)requested, source.Quantity);
            uint accepted = (uint)Mathf.Min((int)available, destination.FreeCapacity(target));
            if (accepted == 0) { CancelledTransfers++; return false; }
            if (source.Version != version)
            {
                VersionConflicts++;
                return false;
            }
            MaterialId material = source.Material;
            if (!destination.TryAdd(target, material, accepted))
            {
                CancelledTransfers++;
                return false;
            }
            if (!source.TryRemove(accepted))
            {
                destination.TryRemove(target, material, accepted);
                CancelledTransfers++;
                return false;
            }
            CommittedGmu += accepted;
            activity.WakeNeighbors(target);
            return true;
        }
    }
}
