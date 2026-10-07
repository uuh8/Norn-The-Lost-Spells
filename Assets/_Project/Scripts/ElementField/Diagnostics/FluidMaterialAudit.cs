using System;
using Game.Materials;

namespace Game.ElementField
{
    /// <summary>
    /// 固定 256 个 MaterialId 槽的无分配总账。Begin 划定审计 Epoch；不同版本的
    /// GPU/RAM 数字不能组合，防止异步 Readback 的“最新值拼盘”伪装成数量结论。
    /// </summary>
    public sealed class FluidMaterialAudit
    {
        private readonly ulong[] _expected = new ulong[byte.MaxValue + 1];
        private readonly ulong[] _gpu = new ulong[byte.MaxValue + 1];
        private readonly ulong[] _ram = new ulong[byte.MaxValue + 1];
        private ulong _epoch;
        private uint _topologyVersion;
        private uint _mutationVersion;
        private bool _overflowed;
        private ulong _submittedParticleWatermark;
        private double _requestedAt;
        private double _completedAt;

        public void Begin(ulong epoch, uint topologyVersion, uint mutationVersion)
            => Begin(epoch, topologyVersion, mutationVersion, 0u, 0d, 0d);

        public void Begin(ulong epoch, uint topologyVersion, uint mutationVersion,
            ulong submittedParticleWatermark, double requestedAt, double completedAt)
        {
            Array.Clear(_expected, 0, _expected.Length);
            Array.Clear(_gpu, 0, _gpu.Length);
            Array.Clear(_ram, 0, _ram.Length);
            _epoch = epoch;
            _topologyVersion = topologyVersion;
            _mutationVersion = mutationVersion;
            _overflowed = false;
            _submittedParticleWatermark = submittedParticleWatermark;
            _requestedAt = requestedAt;
            _completedAt = completedAt;
        }

        public bool TryAddExpected(MaterialId material, ulong amount) =>
            TryAdd(_expected, material, amount);

        public bool TryAddGpu(MaterialId material, ulong amount) =>
            TryAdd(_gpu, material, amount);

        public bool TryAddRam(MaterialId material, ulong amount) =>
            TryAdd(_ram, material, amount);

        /// <summary>
        /// 反应结果、Readback 或版本边界无法证明时主动失效。无效与“差额为零”是两种状态，
        /// 导出端必须保留这个区别，防止缺失证据被当成守恒通过。
        /// </summary>
        public void Invalidate() => _overflowed = true;

        public FluidMaterialAuditSnapshot GetSnapshot(MaterialId material, ulong epoch,
            uint topologyVersion, uint mutationVersion)
        {
            int index = (byte)material;
            bool versionsMatch = epoch == _epoch && topologyVersion == _topologyVersion
                && mutationVersion == _mutationVersion;
            return new FluidMaterialAuditSnapshot(material, epoch, _expected[index],
                _gpu[index], _ram[index], topologyVersion, mutationVersion,
                _submittedParticleWatermark, _requestedAt, _completedAt,
                material != MaterialId.Empty && versionsMatch && !_overflowed);
        }

        private bool TryAdd(ulong[] destination, MaterialId material, ulong amount)
        {
            if (material == MaterialId.Empty) { _overflowed = true; return false; }
            int index = (byte)material;
            if (ulong.MaxValue - destination[index] < amount)
            {
                _overflowed = true;
                return false;
            }
            destination[index] += amount;
            return true;
        }
    }
}
