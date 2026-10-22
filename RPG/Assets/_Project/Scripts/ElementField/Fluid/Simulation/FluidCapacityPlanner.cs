using System;

namespace Game.ElementField
{
    /// <summary>
    /// GPU Buffer 的固定容量 Pure Math。Particle/Hash Capacity 在初始化时归一为 Power-of-two，
    /// 让后续 Bitonic Sort 与位掩码 Hash 具有稳定上界；运行 Tick 不应重复执行这些配置计算。
    /// </summary>
    public static class FluidCapacityPlanner
    {
        private const int MaximumSupportedParticleCapacity = 1 << 29;

        public static int NormalizeParticleCapacity(int requestedCapacity)
        {
            if (requestedCapacity <= 0 || requestedCapacity > MaximumSupportedParticleCapacity)
                throw new ArgumentOutOfRangeException(nameof(requestedCapacity));

            uint value = (uint)requestedCapacity - 1u;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return (int)(value + 1u);
        }

        /// <summary>
        /// Legacy Amount 是离散量，不等于粒子数。向上取整保证尾数 Amount 不会消失，
        /// 再以 Profile 的固定 Particle Pool 上限裁剪，避免单次写入越过 GPU Buffer 容量。
        /// </summary>
        public static uint CalculateSpawnParticleCount(
            uint totalAmount,
            uint amountUnitsPerParticle,
            uint maxParticlesPerSpawn)
        {
            if (amountUnitsPerParticle == 0u)
                throw new ArgumentOutOfRangeException(nameof(amountUnitsPerParticle));
            if (maxParticlesPerSpawn == 0u)
                throw new ArgumentOutOfRangeException(nameof(maxParticlesPerSpawn));

            uint roundedUpCount = totalAmount / amountUnitsPerParticle;
            if (totalAmount % amountUnitsPerParticle != 0u)
                roundedUpCount++;

            return Math.Min(roundedUpCount, maxParticlesPerSpawn);
        }

        public static int CalculateHashTableCapacity(int particleCapacity)
        {
            int normalizedParticleCapacity = NormalizeParticleCapacity(particleCapacity);
            if (normalizedParticleCapacity > int.MaxValue / 2)
                throw new ArgumentOutOfRangeException(nameof(particleCapacity));

            // 至少 2x 的 Bucket 数降低开地址/链式 Range 查询中的平均碰撞压力；
            // 结果仍是 Power-of-two，可在 HLSL 中用 mask 代替昂贵的 modulo。
            return normalizedParticleCapacity * 2;
        }
    }
}
