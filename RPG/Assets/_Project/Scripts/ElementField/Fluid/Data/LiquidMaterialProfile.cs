using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    [CreateAssetMenu(menuName = "Game/Element Field/Liquid Material Profile", fileName = "LiquidMaterialProfile")]
    public sealed class LiquidMaterialProfile : ScriptableObject
    {
        [SerializeField] private MaterialId _material = MaterialId.Empty;
        [SerializeField, Min(1)] private int _amountUnitsPerParticle = 8;
        [SerializeField, Min(0.001f)] private float _particleMass = 1f;
        [SerializeField, Min(0.001f)] private float _restDensity = 1000f;
        [SerializeField, Range(0f, 1f)] private float _viscosity = 0.08f;
        [SerializeField, Min(0f)] private float _artificialPressure = 0.001f;
        [SerializeField, Min(0f)] private float _cohesionStrength = 12f;
        [SerializeField, Min(0f)] private float _cohesionRestDistanceRatio = 1f;
        [SerializeField, Min(0.0001f)] private float _maximumCohesionDeltaSpeed = 0.2f;
        [SerializeField, Range(0f, 0.99f)] private float _sleepDensityErrorThreshold = 0.02f;

        public LiquidMaterialSettings CreateSettings() => new LiquidMaterialSettings(
            _material,
            (uint)_amountUnitsPerParticle,
            _particleMass,
            _restDensity,
            _viscosity,
            _artificialPressure,
            _cohesionStrength,
            _cohesionRestDistanceRatio,
            _maximumCohesionDeltaSpeed,
            _sleepDensityErrorThreshold);
    }
}
