using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// PBF 的 Authoring Data。它只在 Runtime 初始化边界生成 Settings Snapshot；
    /// 运行中的 GPU Solver 不再逐帧读取这个 ScriptableObject，以隔离 Inspector 编辑与热路径成本。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Element Field/Liquid Simulation Profile", fileName = "LiquidSimulationProfile")]
    public sealed class LiquidSimulationProfile : ScriptableObject
    {
        [Header("Canonical Liquid Materials")]
        [SerializeField] private MaterialCatalog _materialCatalog;
        [SerializeField] private LiquidMaterialProfile[] _liquidMaterials = System.Array.Empty<LiquidMaterialProfile>();
        [Header("Capacity And Spawn")]
        [SerializeField, Min(1)] private int _particleCapacity = 8192;
        [SerializeField, Min(1)] private int _maxSpawnRequests = 128;
        [Tooltip("显式 FluidColliderAuthoring 的 GPU Proxy 上限；超出时按层级顺序确定性截断。")]
        [SerializeField, Min(1)] private int _maxFluidColliders = 64;
        [Header("Particle And Density")]
        [SerializeField, Min(0.001f)] private float _particleRadius = 0.1f;
        [SerializeField, Min(0.001f)] private float _smoothingRadius = 0.25f;

        [Header("Fixed Simulation")]
        [SerializeField, Min(0.1f)] private float _fixedTickRate = 60f;
        [SerializeField, Min(1)] private int _substeps = 2;
        [SerializeField, Min(1)] private int _solverIterations = 4;
        [Tooltip("每次 PBF Apply 最多移动的距离；必须小于 Smoothing Radius，避免一次修正跨过整个邻域。")]
        [SerializeField, Min(0.0001f)] private float _maximumPositionCorrection = 0.08f;
        [SerializeField, Min(0.000001f)] private float _lambdaEpsilon = 0.0001f;
        [SerializeField, Min(0f)] private float _vorticity = 0.01f;
        [Header("Bounded Tensile Constraint")]
        [Tooltip("欠密度自由表面的弱位置级吸引强度；0 会保留当前稳定的单向不可压缩路径。它不是直接的高度值。")]
        [SerializeField, Min(0f)] private float _tensileStrength = 0.00005f;
        [Tooltip("每次 Solver Iteration 允许 Tensile 额外移动粒子的最大距离，单位 m。硬上限用于阻止旧式负压力爆炸。")]
        [SerializeField, Min(0.000001f)] private float _maximumTensilePositionCorrection = 0.0001f;
        [SerializeField] private Vector3 _gravity = new Vector3(0f, -9.81f, 0f);
        [SerializeField, Min(0.001f)] private float _maxSpeed = 25f;

        [Header("Collision And Sleep")]
        [SerializeField, Range(0f, 1f)] private float _collisionFriction = 0.1f;
        [SerializeField, Range(0f, 1f)] private float _collisionRestitution;
        [SerializeField, Min(0f)] private float _sleepThreshold = 0.01f;
        [SerializeField, Min(1)] private int _sleepAfterStableTicks = 30;
        [SerializeField, Range(1, FluidSimulationClock.MaxSupportedCatchUpTicks)]
        private int _maxCatchUpTicks = 3;

        /// <summary>
        /// 把可编辑 Profile 转成已验证的值副本。未来 GpuPbfFluidRuntime 只在 Awake/Enable 调用一次，
        /// 从而让后续 Fixed Tick、Substep 与 Compute 参数绑定都使用同一份不可变规则。
        /// </summary>
        public LiquidSimulationSettings CreateSettings()
        {
            LiquidMaterialSettingsTable materialSettings = CreateMaterialSettingsTable();
            return new LiquidSimulationSettings(
                _particleCapacity,
                _maxSpawnRequests,
                _maxFluidColliders,
                _particleRadius,
                _smoothingRadius,
                _fixedTickRate,
                _substeps,
                _solverIterations,
                _maximumPositionCorrection,
                _lambdaEpsilon,
                _vorticity,
                _gravity,
                _maxSpeed,
                _collisionFriction,
                _collisionRestitution,
                _sleepThreshold,
                _maxCatchUpTicks,
                _sleepAfterStableTicks,
                _tensileStrength,
                _maximumTensilePositionCorrection,
                materialSettings);
        }

        public LiquidMaterialSettingsTable CreateMaterialSettingsTable()
        {
            if (_materialCatalog == null)
                throw new System.InvalidOperationException("LiquidSimulationProfile requires a MaterialCatalog.");
            LiquidMaterialSettingsTable table = new LiquidMaterialSettingsTable(
                _materialCatalog.CreateSnapshot(),
                _liquidMaterials);
            // Kernel 边界属于所有配置过的液体，不应硬编码 Water/Poison 名单。
            // Profile 数组只在初始化期遍历，不进入 Simulation 热路径。
            for (int i = 0; i < _liquidMaterials.Length; i++)
            {
                LiquidMaterialProfile profile = _liquidMaterials[i];
                if (profile == null)
                    continue;
                ValidateKernelSupport(table, profile.CreateSettings().Material);
            }
            return table;
        }

        private void ValidateKernelSupport(LiquidMaterialSettingsTable table, MaterialId material)
        {
            if (!table.TryGet(material, out LiquidMaterialSettings settings)) return;
            if (settings.RestSpacing * settings.CohesionRestDistanceRatio >= _smoothingRadius)
                throw new System.ArgumentOutOfRangeException($"{material} cohesionRestDistanceRatio");
        }

        private void OnValidate()
        {
            // Inspector 只提供友好下限；CreateSettings 仍是唯一可信边界，
            // 因为 Asset 可由版本控制、脚本或未触发 OnValidate 的路径写入。
            _particleCapacity = Mathf.Max(1, _particleCapacity);
            _maxSpawnRequests = Mathf.Max(1, _maxSpawnRequests);
            _maxFluidColliders = Mathf.Max(1, _maxFluidColliders);
            _particleRadius = Mathf.Max(0.001f, _particleRadius);
            _smoothingRadius = Mathf.Max(0.001f, _smoothingRadius);
            _fixedTickRate = Mathf.Max(0.1f, _fixedTickRate);
            _substeps = Mathf.Max(1, _substeps);
            _solverIterations = Mathf.Max(1, _solverIterations);
            // Correction 不能跨越整个 Kernel 支持域；限制到 h 的一半可让下轮邻居查询仍是局部修正。
            _maximumPositionCorrection = Mathf.Clamp(
                _maximumPositionCorrection,
                0.0001f,
                Mathf.Min(
                    _smoothingRadius * 0.5f,
                    _particleRadius * FluidCollisionSweep.MaxSamples / (_solverIterations + 1f)));
            _lambdaEpsilon = Mathf.Max(0.000001f, _lambdaEpsilon);
            _vorticity = Mathf.Max(0f, _vorticity);
            _tensileStrength = Mathf.Max(0f, _tensileStrength);
            // Tensile 必须比主 Density Correction 小几个数量级；它只补回毛细压力，不能重新成为无界负压力。
            _maximumTensilePositionCorrection = Mathf.Clamp(
                _maximumTensilePositionCorrection,
                0.000001f,
                _maximumPositionCorrection);
            float sweepCoverageAfterWorstCorrections = _particleRadius
                * FluidCollisionSweep.MaxSamples
                - _solverIterations * _maximumPositionCorrection;
            float maximumSweepCoveredSpeed = sweepCoverageAfterWorstCorrections
                * _fixedTickRate
                * _substeps;
            _maxSpeed = Mathf.Clamp(_maxSpeed, 0.001f, maximumSweepCoveredSpeed);
            _collisionFriction = Mathf.Clamp01(_collisionFriction);
            _collisionRestitution = Mathf.Clamp01(_collisionRestitution);
            _sleepThreshold = Mathf.Max(0f, _sleepThreshold);
            _sleepAfterStableTicks = Mathf.Max(1, _sleepAfterStableTicks);
            _maxCatchUpTicks = Mathf.Clamp(
                _maxCatchUpTicks,
                1,
                FluidSimulationClock.MaxSupportedCatchUpTicks);
        }
    }
}
