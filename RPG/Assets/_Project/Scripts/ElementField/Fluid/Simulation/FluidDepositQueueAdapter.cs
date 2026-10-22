using Game.Materials;
using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 把 Legacy Amount 单位无损量化成一次完整 Spawn Command。这里的“无损”指永不静默 clamp：
    /// 尾数用 Ceiling 保留；若完整粒子数超过 Pool Capacity，则整笔拒绝，由调用方决定反馈策略。
    /// </summary>
    public sealed class FluidDepositQueueAdapter : IFluidDepositSink
    {
        private readonly IFluidSpawnSink _spawnSink;
        private readonly LiquidMaterialSettingsTable _materials;
        private readonly uint _particleCapacity;
        private readonly float _particleRadius;
        private uint _nextSeed;

        public FluidDepositQueueAdapter(
            IFluidSpawnSink spawnSink,
            LiquidMaterialSettingsTable materials,
            int particleCapacity,
            float particleRadius)
        {
            _spawnSink = spawnSink ?? throw new ArgumentNullException(nameof(spawnSink));
            _materials = materials ?? throw new ArgumentNullException(nameof(materials));
            if (particleCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(particleCapacity));
            if (!IsNonNegativeFinite(particleRadius))
                throw new ArgumentOutOfRangeException(nameof(particleRadius));

            _particleCapacity = (uint)particleCapacity;
            _particleRadius = particleRadius;
        }

        public bool IsFluidInitialized => true;

        public bool TryEnqueueDeposit(in ElementWriteRequest request)
        {
            if (!IsFluidInitialized
                || !_materials.TryGet(request.MaterialKind, out LiquidMaterialSettings material))
                return false;

            uint totalAmount = request.TotalAmount;
            uint particleCount = totalAmount / material.AmountUnitsPerParticle;
            if (totalAmount % material.AmountUnitsPerParticle != 0u)
                particleCount++;
            if (particleCount == 0u || particleCount > _particleCapacity)
                return false;

            // Projectile Impact 在进入 Request 前必须给出真实法线；旧 Initial Deposit/Debug Source
            // 没有表面概念时保持向上的兼容落点，避免 Task 14 让既有 Sandbox 静默失去全部水源。
            Vector3 packingNormal = request.SurfaceNormal.sqrMagnitude > 1e-12f
                ? request.SurfaceNormal
                : Vector3.up;
            if (!FluidSpawnPackingPlanner.TryCreate(
                    particleCount,
                    material.ParticleMass,
                    material.RestDensity,
                    _particleRadius,
                    request.Radius,
                    request.WorldPosition,
                    packingNormal,
                    out FluidSpawnPacking packing))
            {
                return false;
            }

            uint flags = request.UseLinearFalloff
                ? FluidSpawnFlags.UseLinearFalloff
                : 0u;
            flags |= FluidSpawnFlags.DensityPacked;
            var spawn = new FluidSpawnRequest(
                packing.Center,
                request.InitialVelocity,
                packing.RequiredRadius,
                particleCount,
                (uint)request.MaterialKind,
                _nextSeed,
                flags,
                material.RestSpacing);
            if (!_spawnSink.TryEnqueueSpawn(in spawn))
                return false;

            unchecked
            {
                _nextSeed++;
            }
            return true;
        }

        private static bool IsPositiveFinite(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsNonNegativeFinite(float value)
        {
            return value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

    }
}
