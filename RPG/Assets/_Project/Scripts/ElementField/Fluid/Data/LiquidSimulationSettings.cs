using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 初始化时从 LiquidSimulationProfile 复制出的不可变 Runtime Snapshot。
    /// PBF Tick/Substep 只读取这个值类型，避免在热路径反复访问可由 Inspector 修改的 ScriptableObject，
    /// 也保证同一轮模拟不会在中途混入两套参数。
    /// </summary>
    public readonly struct LiquidSimulationSettings
    {
        public readonly int ParticleCapacity;
        public readonly int HashTableCapacity;
        public readonly int MaxSpawnRequests;
        public readonly int MaxFluidColliders;
        public readonly float ParticleRadius;
        public readonly float SmoothingRadius;
        public readonly float FixedTickRate;
        public readonly int Substeps;
        public readonly int SolverIterations;
        public readonly float MaximumPositionCorrection;
        public readonly float LambdaEpsilon;
        public readonly float Vorticity;
        public readonly float TensileStrength;
        public readonly float MaximumTensilePositionCorrection;
        public readonly Vector3 Gravity;
        public readonly float MaxSpeed;
        public readonly float CollisionFriction;
        public readonly float CollisionRestitution;
        public readonly float SleepThreshold;
        public readonly int SleepAfterStableTicks;
        public readonly int MaxCatchUpTicks;
        public readonly LiquidMaterialSettingsTable LiquidMaterials;

        // 这些兼容只读属性从 Water row 即时解析，不再保存第二份全局数值。
        // Task 24 完成所有 Presentation 迁移后，Renderer 也会直接按 Target Material 读取 GPU row。
        public uint AmountUnitsPerParticle => GetCompatibilityWater().AmountUnitsPerParticle;
        public float ParticleMass => GetCompatibilityWater().ParticleMass;
        public float RestDensity => GetCompatibilityWater().RestDensity;
        public float ArtificialPressure => GetCompatibilityWater().ArtificialPressure;
        public float Viscosity => GetCompatibilityWater().Viscosity;
        public float CohesionStrength => GetCompatibilityWater().CohesionStrength;
        public float CohesionRestDistanceRatio => GetCompatibilityWater().CohesionRestDistanceRatio;
        public float MaximumCohesionDeltaSpeed => GetCompatibilityWater().MaximumCohesionDeltaSpeed;
        public float SleepDensityErrorThreshold => GetCompatibilityWater().SleepDensityErrorThreshold;

        public LiquidSimulationSettings(
            int particleCapacity,
            int maxSpawnRequests,
            int maxFluidColliders,
            float particleRadius,
            float smoothingRadius,
            float fixedTickRate,
            int substeps,
            int solverIterations,
            float maximumPositionCorrection,
            float lambdaEpsilon,
            float vorticity,
            Vector3 gravity,
            float maxSpeed,
            float collisionFriction,
            float collisionRestitution,
            float sleepThreshold,
            int maxCatchUpTicks,
            int sleepAfterStableTicks = 30,
            float tensileStrength = 0.00005f,
            float maximumTensilePositionCorrection = 0.0001f,
            LiquidMaterialSettingsTable liquidMaterials = null)
        {
            ParticleCapacity = FluidCapacityPlanner.NormalizeParticleCapacity(particleCapacity);
            HashTableCapacity = FluidCapacityPlanner.CalculateHashTableCapacity(ParticleCapacity);
            MaxSpawnRequests = RequirePositive(maxSpawnRequests, nameof(maxSpawnRequests));
            MaxFluidColliders = RequirePositive(maxFluidColliders, nameof(maxFluidColliders));
            ParticleRadius = RequirePositiveFinite(particleRadius, nameof(particleRadius));
            SmoothingRadius = RequirePositiveFinite(smoothingRadius, nameof(smoothingRadius));
            FixedTickRate = RequirePositiveFinite(fixedTickRate, nameof(fixedTickRate));
            Substeps = RequirePositive(substeps, nameof(substeps));
            SolverIterations = RequirePositive(solverIterations, nameof(solverIterations));
            MaximumPositionCorrection = RequirePositiveFinite(
                maximumPositionCorrection,
                nameof(maximumPositionCorrection));
            if (MaximumPositionCorrection >= SmoothingRadius)
                throw new ArgumentOutOfRangeException(nameof(maximumPositionCorrection));
            LambdaEpsilon = RequirePositiveFinite(lambdaEpsilon, nameof(lambdaEpsilon));
            Vorticity = RequireNonNegativeFinite(vorticity, nameof(vorticity));
            TensileStrength = RequireNonNegativeFinite(tensileStrength, nameof(tensileStrength));
            MaximumTensilePositionCorrection = RequirePositiveFinite(
                maximumTensilePositionCorrection,
                nameof(maximumTensilePositionCorrection));
            if (MaximumTensilePositionCorrection > MaximumPositionCorrection)
                throw new ArgumentOutOfRangeException(nameof(maximumTensilePositionCorrection));
            Gravity = RequireFinite(gravity, nameof(gravity));
            MaxSpeed = RequirePositiveFinite(maxSpeed, nameof(maxSpeed));
            float maximumSubstepDisplacement = MaxSpeed / (FixedTickRate * Substeps)
                + SolverIterations * MaximumPositionCorrection;
            float boundedSweepCoverage = ParticleRadius * FluidCollisionSweep.MaxSamples;
            if (maximumSubstepDisplacement > boundedSweepCoverage)
                throw new ArgumentOutOfRangeException(nameof(maxSpeed));
            CollisionFriction = RequireUnitInterval(collisionFriction, nameof(collisionFriction));
            CollisionRestitution = RequireUnitInterval(
                collisionRestitution,
                nameof(collisionRestitution));
            SleepThreshold = RequireNonNegativeFinite(sleepThreshold, nameof(sleepThreshold));
            SleepAfterStableTicks = RequirePositive(
                sleepAfterStableTicks,
                nameof(sleepAfterStableTicks));
            MaxCatchUpTicks = FluidSimulationClock.ValidateMaxCatchUpTicks(maxCatchUpTicks);
            LiquidMaterials = liquidMaterials
                ?? throw new ArgumentNullException(nameof(liquidMaterials));
        }

        private LiquidMaterialSettings GetCompatibilityWater()
        {
            if (!LiquidMaterials.TryGet(Game.Materials.MaterialId.Water, out LiquidMaterialSettings row))
                throw new InvalidOperationException("Liquid settings table does not contain Water.");
            return row;
        }

        private static int RequirePositive(int value, string parameterName)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }

        private static float RequirePositiveFinite(float value, string parameterName)
        {
            if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }

        private static float RequireNonNegativeFinite(float value, string parameterName)
        {
            if (value < 0f || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }

        private static float RequireUnitInterval(float value, string parameterName)
        {
            if (value < 0f || value > 1f || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }

        private static Vector3 RequireFinite(Vector3 value, string parameterName)
        {
            if (float.IsNaN(value.x) || float.IsInfinity(value.x)
                || float.IsNaN(value.y) || float.IsInfinity(value.y)
                || float.IsNaN(value.z) || float.IsInfinity(value.z))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }

            return value;
        }
    }
}
