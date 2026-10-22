using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 单种 Liquid 的不可变 Runtime 参数。GMU Scale 与 PBF Physics 参数同属该 Material row，
    /// 但语义仍分离：前者只用于 Gameplay 量化，后者只用于粒子生成和 Solver。
    /// </summary>
    public readonly struct LiquidMaterialSettings
    {
        public LiquidMaterialSettings(
            MaterialId material,
            uint amountUnitsPerParticle,
            float particleMass,
            float restDensity,
            float viscosity,
            float artificialPressure,
            float cohesionStrength,
            float cohesionRestDistanceRatio,
            float maximumCohesionDeltaSpeed,
            float sleepDensityErrorThreshold)
        {
            if (material == MaterialId.Empty) throw new ArgumentOutOfRangeException(nameof(material));
            if (amountUnitsPerParticle == 0u) throw new ArgumentOutOfRangeException(nameof(amountUnitsPerParticle));
            Material = material;
            AmountUnitsPerParticle = amountUnitsPerParticle;
            ParticleMass = Positive(particleMass, nameof(particleMass));
            RestDensity = Positive(restDensity, nameof(restDensity));
            Viscosity = Unit(viscosity, nameof(viscosity));
            ArtificialPressure = NonNegative(artificialPressure, nameof(artificialPressure));
            CohesionStrength = NonNegative(cohesionStrength, nameof(cohesionStrength));
            CohesionRestDistanceRatio = NonNegative(cohesionRestDistanceRatio, nameof(cohesionRestDistanceRatio));
            MaximumCohesionDeltaSpeed = Positive(maximumCohesionDeltaSpeed, nameof(maximumCohesionDeltaSpeed));
            SleepDensityErrorThreshold = UnitExclusive(sleepDensityErrorThreshold, nameof(sleepDensityErrorThreshold));
        }

        public MaterialId Material { get; }
        public uint AmountUnitsPerParticle { get; }
        public float ParticleMass { get; }
        public float RestDensity { get; }
        public float Viscosity { get; }
        public float ArtificialPressure { get; }
        public float CohesionStrength { get; }
        public float CohesionRestDistanceRatio { get; }
        public float MaximumCohesionDeltaSpeed { get; }
        public float SleepDensityErrorThreshold { get; }
        public float RestSpacing => Mathf.Pow(ParticleMass / RestDensity, 1f / 3f);

        private static float Positive(float value, string name) =>
            value > 0f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name);
        private static float NonNegative(float value, string name) =>
            value >= 0f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name);
        private static float Unit(float value, string name) =>
            value >= 0f && value <= 1f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name);
        private static float UnitExclusive(float value, string name) =>
            value >= 0f && value < 1f && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name);
    }
}
