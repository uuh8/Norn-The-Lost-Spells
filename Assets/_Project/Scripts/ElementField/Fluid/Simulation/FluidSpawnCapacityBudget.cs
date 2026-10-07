using System;

namespace Game.ElementField
{
    /// <summary>
    /// CPU 侧保守 Free-slot 预算。AsyncGPUReadback 发布的是过去某一提交点的快照，
    /// 因此用 Submitted Watermark 扣掉快照之后接受的请求，避免旧 Free 值造成过量提交。
    /// </summary>
    public sealed class FluidSpawnCapacityBudget
    {
        private ulong _acceptedParticles;

        public FluidSpawnCapacityBudget(uint initialCapacity)
        {
            if (initialCapacity == 0u) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            AvailableParticles = initialCapacity;
        }

        public uint AvailableParticles { get; private set; }
        public ulong AcceptedParticles => _acceptedParticles;

        public bool TryReserve(uint particleCount)
        {
            if (particleCount == 0u || particleCount > AvailableParticles) return false;
            AvailableParticles -= particleCount;
            _acceptedParticles = checked(_acceptedParticles + particleCount);
            return true;
        }

        public void Refund(uint particleCount)
        {
            if (particleCount == 0u || particleCount > _acceptedParticles)
                throw new ArgumentOutOfRangeException(nameof(particleCount));
            _acceptedParticles -= particleCount;
            AvailableParticles = checked(AvailableParticles + particleCount);
        }

        public bool ApplyPoolReadback(uint gpuFreeParticles, ulong submittedWatermark)
        {
            if (submittedWatermark > _acceptedParticles) return false;
            ulong unreflectedParticles = _acceptedParticles - submittedWatermark;
            AvailableParticles = unreflectedParticles >= gpuFreeParticles
                ? 0u
                : gpuFreeParticles - (uint)unreflectedParticles;
            return true;
        }

        public void InvalidateUntilNextReadback() => AvailableParticles = 0u;
    }
}
