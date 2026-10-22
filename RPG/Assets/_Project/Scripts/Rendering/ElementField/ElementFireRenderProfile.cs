using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// Fire Cell 的纯表现参数。它只决定粒子抽样密度、三层概率、尺寸与位置抖动，
    /// 不进入 ElementField Simulation，也不会改变 Fire Amount、Decay 或 Water/Fire Reaction。
    /// 使用 ScriptableObject 后，表现调参可在不重新编译代码的情况下迭代，并能由多个场景复用。
    /// </summary>
    [CreateAssetMenu(
        fileName = "ElementFireRenderProfile",
        menuName = "Game/Rendering/Element Fire Render Profile")]
    public sealed class ElementFireRenderProfile : ScriptableObject
    {
        [Header("Visual Tick And Sampling")]
        [Tooltip("每次扫描最多让多少个 Active Fire Cell 进入三层发射判断；只降级视觉，不删除 Gameplay Cell。")]
        [SerializeField, Min(1)] private int _maxSampledFireCells = 1024;

        [Tooltip("两次视觉扫描之间的秒数。0.10s = 10Hz，与 P6-A Gameplay Tick 同量级但职责独立。")]
        [SerializeField, Min(0.02f)] private float _emissionInterval = 0.10f;

        [Header("Layer Probability At Full Amount")]
        [Tooltip("Amount=255 时 Body 在一次视觉 Tick 中发射的概率。Body 负责稳定外焰轮廓。")]
        [SerializeField, Range(0f, 1f)] private float _bodyProbabilityAtFullAmount = 1f;

        [Tooltip("Amount=255 时 Core 的发射概率。Core 使用 Additive，数量应低于 Body 以控制 Overdraw。")]
        [SerializeField, Range(0f, 1f)] private float _coreProbabilityAtFullAmount = 0.75f;

        [Tooltip("Amount=255 时 Sparks 的发射概率。火星是高频点缀，必须保持稀疏。")]
        [SerializeField, Range(0f, 1f)] private float _sparkProbabilityAtFullAmount = 0.12f;

        [Header("Amount To Particle Mapping")]
        [Tooltip("低 Amount 时相对于 Particle System Main Start Size 的倍率。")]
        [SerializeField, Min(0f)] private float _minSizeMultiplier = 0.4f;

        [Tooltip("满 Amount 时相对于 Particle System Main Start Size 的倍率。")]
        [SerializeField, Min(0f)] private float _maxSizeMultiplier = 1.2f;

        [Tooltip("发射点在 Cell Center 上方向上随机抬高的最大米数；XZ 抖动由 CellSize 的 35% 自动限制。")]
        [SerializeField, Min(0f)] private float _verticalJitter = 0.08f;

        public int MaxSampledFireCells => _maxSampledFireCells;
        public float EmissionInterval => _emissionInterval;
        public float BodyProbabilityAtFullAmount => _bodyProbabilityAtFullAmount;
        public float CoreProbabilityAtFullAmount => _coreProbabilityAtFullAmount;
        public float SparkProbabilityAtFullAmount => _sparkProbabilityAtFullAmount;
        public float MinSizeMultiplier => _minSizeMultiplier;
        public float MaxSizeMultiplier => _maxSizeMultiplier;
        public float VerticalJitter => _verticalJitter;

        private void OnValidate()
        {
            _maxSampledFireCells = Mathf.Max(1, _maxSampledFireCells);
            _emissionInterval = IsFinitePositive(_emissionInterval)
                ? Mathf.Max(0.02f, _emissionInterval)
                : 0.10f;
            _bodyProbabilityAtFullAmount = ClampProbability(_bodyProbabilityAtFullAmount);
            _coreProbabilityAtFullAmount = ClampProbability(_coreProbabilityAtFullAmount);
            _sparkProbabilityAtFullAmount = ClampProbability(_sparkProbabilityAtFullAmount);
            _minSizeMultiplier = ClampNonNegative(_minSizeMultiplier);
            _maxSizeMultiplier = Mathf.Max(
                _minSizeMultiplier,
                ClampNonNegative(_maxSizeMultiplier));
            _verticalJitter = ClampNonNegative(_verticalJitter);
        }

        private static float ClampProbability(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? 0f
                : Mathf.Clamp01(value);
        }

        private static float ClampNonNegative(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? 0f
                : Mathf.Max(0f, value);
        }

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
