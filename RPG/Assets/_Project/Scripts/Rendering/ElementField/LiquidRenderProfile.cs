using System;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 无分配的隔帧调度器。Reset 后第一次必定更新，保证新分配的 Transform Buffer
    /// 不会在初始化前被 Anisotropic Density 读取。
    /// </summary>
    public struct FluidAnisotropyUpdateSchedule
    {
        private readonly int _intervalFrames;
        private int _framesUntilUpdate;
        private uint _lastTopologyVersion;
        private bool _hasTopologyVersion;

        public FluidAnisotropyUpdateSchedule(int intervalFrames)
        {
            if (intervalFrames <= 0)
                throw new ArgumentOutOfRangeException(nameof(intervalFrames));
            _intervalFrames = intervalFrames;
            _framesUntilUpdate = 0;
            _lastTopologyVersion = 0u;
            _hasTopologyVersion = false;
        }

        public bool ShouldUpdateAndAdvance(uint topologyVersion)
        {
            // Spawn/reuse 后旧 Buffer 对新 active slot 没有有效 affine，必须抢在 Density 前立即重算。
            // 这里只比较 !=，因此 uint 从 MaxValue wrap 到 0 仍会被识别为新版本。
            if (!_hasTopologyVersion || topologyVersion != _lastTopologyVersion)
            {
                _lastTopologyVersion = topologyVersion;
                _hasTopologyVersion = true;
                _framesUntilUpdate = _intervalFrames - 1;
                return true;
            }

            if (_framesUntilUpdate > 0)
            {
                _framesUntilUpdate--;
                return false;
            }

            _framesUntilUpdate = _intervalFrames - 1;
            return true;
        }

        public void Reset()
        {
            _framesUntilUpdate = 0;
            _hasTopologyVersion = false;
        }
    }

    /// <summary>
    /// GPU Liquid Surface 的 Presentation 参数。它只决定单套 Density Field 怎样采样与分配资源，
    /// 不修改 PBF 粒子位置、速度或 Gameplay Occupancy。资源尺寸在初始化时快照化。
    /// </summary>
    [CreateAssetMenu(
        fileName = "LiquidRenderProfile",
        menuName = "Game/Rendering/Liquid Render Profile")]
    public sealed class LiquidRenderProfile : ScriptableObject
    {
        [Header("Density Lattice")]
        [Tooltip("目标 Voxel 边长（米）。实际边长会在 Resolution Clamp 后重新计算，保证覆盖完整 Bounds。")]
        [SerializeField, Min(0.001f)] private float _targetVoxelSize = 0.1f;
        [Tooltip("3D Texture 每个轴的最大 Sample 数。显存与 Dispatch 成本近似按三次方增长。")]
        [SerializeField, Min(2)] private int _maximumResolutionPerAxis = 128;
        [Tooltip("在 Interest Bounds 六个方向扩张的距离（米），为 Kernel 与边界表面保留采样空间。")]
        [SerializeField, Min(0f)] private float _boundsPadding = 0.3f;
        [Header("Surface Extraction")]
        [Tooltip("Density 等于该值的位置会在 Task 7 被提取成液体表面。")]
        [SerializeField, Min(0.000001f)] private float _isoLevel = 500f;
        [Tooltip("Triangle Buffer 的硬容量；Marching Cubes 超出时只增加 Overflow Counter。")]
        [SerializeField, Min(1)] private int _maximumTriangleCount = 262144;

        [Header("Anisotropic Reconstruction")]
        [Tooltip("开启后先从粒子邻域构造方向性 Kernel；关闭时完全不提交 Anisotropy Compute，便于与球形 Kernel 做 A/B。")]
        [SerializeField] private bool _useAnisotropy = true;
        [Tooltip("每隔多少 Rendering Frame 重算一次方向矩阵。中间帧复用长期 Buffer 中最后一次有效结果。")]
        [SerializeField, Min(1)] private int _anisotropyUpdateIntervalFrames = 2;
        [Tooltip("包含粒子自身的最少邻居数；不足时回退以原粒子为中心的 Isotropic Identity。")]
        [SerializeField, Min(2)] private int _anisotropyNeighborThreshold = 5;
        [Tooltip("世界空间椭球任一轴的最小尺度。必须不大于 1，避免体积保持后短轴无限收缩。")]
        [SerializeField, Range(0.01f, 1f)] private float _minimumAnisotropyScale = 0.65f;
        [Tooltip("世界空间椭球任一轴的最大尺度。必须不小于 1。")]
        [SerializeField, Range(1f, 2f)] private float _maximumAnisotropyScale = 1.8f;
        [Tooltip("最长轴/最短轴的硬上限，防止稀疏表面粒子被拉成针。")]
        [SerializeField, Min(1f)] private float _maximumAnisotropyRatio = 2.5f;

        [Header("Stylized Crown Reconstruction")]
        [Tooltip("只在表现层把邻居充分的内部粒子 Kernel 向世界 +Y 延展；不改变 PBF、碰撞或 Gameplay Occupancy。")]
        [SerializeField] private bool _useStylizedCrown = true;
        [Tooltip("最多额外延展多少个 Smoothing Radius，范围 0–1；它不是实际粒子高度。Profile 只在进入 Play Mode 时生成 Snapshot。")]
        [SerializeField, Range(0f, 1f)] private float _crownHeightRatio = 0.8f;
        [Tooltip("支持度到冠高权重的指数。越大越集中于水滩内部，边缘越薄；修改后需重新进入 Play Mode。")]
        [SerializeField, Min(0.01f)] private float _crownFalloff = 1.5f;
        [Tooltip("邻居数不超过该值时视为边缘，不增加卡通冠高。邻居统计包含粒子自身。")]
        [SerializeField, Min(0)] private int _crownEdgeNeighborCount = 4;
        [Tooltip("邻居数达到该值时视为内部，应用完整 Crown Height Ratio；必须大于 Edge Neighbor Count。")]
        [SerializeField, Min(1)] private int _crownInteriorNeighborCount = 12;

        public LiquidRenderSettings CreateSettings()
        {
            return new LiquidRenderSettings(
                _targetVoxelSize,
                _maximumResolutionPerAxis,
                _maximumTriangleCount,
                _boundsPadding,
                _isoLevel,
                _useAnisotropy,
                _anisotropyUpdateIntervalFrames,
                _anisotropyNeighborThreshold,
                _minimumAnisotropyScale,
                _maximumAnisotropyScale,
                _maximumAnisotropyRatio,
                _useStylizedCrown,
                _crownHeightRatio,
                _crownFalloff,
                _crownEdgeNeighborCount,
                _crownInteriorNeighborCount);
        }

        private void OnValidate()
        {
            _targetVoxelSize = Mathf.Max(0.001f, _targetVoxelSize);
            _maximumResolutionPerAxis = Mathf.Max(2, _maximumResolutionPerAxis);
            _boundsPadding = Mathf.Max(0f, _boundsPadding);
            _isoLevel = Mathf.Max(0.000001f, _isoLevel);
            _maximumTriangleCount = Mathf.Max(1, _maximumTriangleCount);
            _anisotropyUpdateIntervalFrames = Mathf.Max(1, _anisotropyUpdateIntervalFrames);
            _anisotropyNeighborThreshold = Mathf.Max(2, _anisotropyNeighborThreshold);
            _minimumAnisotropyScale = Mathf.Clamp(_minimumAnisotropyScale, 0.01f, 1f);
            _maximumAnisotropyScale = Mathf.Clamp(_maximumAnisotropyScale, 1f, 2f);
            _maximumAnisotropyRatio = Mathf.Max(1f, _maximumAnisotropyRatio);
            _crownHeightRatio = Mathf.Clamp(_crownHeightRatio, 0f, 1f);
            _crownFalloff = Mathf.Max(0.01f, _crownFalloff);
            _crownEdgeNeighborCount = Mathf.Max(0, _crownEdgeNeighborCount);
            _crownInteriorNeighborCount = Mathf.Max(
                _crownEdgeNeighborCount + 1,
                _crownInteriorNeighborCount);
        }
    }

    /// <summary>
    /// 从 ScriptableObject 复制出的不可变 Surface 配置。Profile 在 Play Mode 中被修改时，
    /// 已初始化资源仍使用旧 Snapshot，避免一帧内混入两套 Resolution/Buffer 容量。
    /// </summary>
    public readonly struct LiquidRenderSettings
    {
        public readonly float TargetVoxelSize;
        public readonly int MaximumResolutionPerAxis;
        public readonly int MaximumTriangleCount;
        public readonly float BoundsPadding;
        public readonly float IsoLevel;
        public readonly bool UseAnisotropy;
        public readonly int AnisotropyUpdateIntervalFrames;
        public readonly int AnisotropyNeighborThreshold;
        public readonly float MinimumAnisotropyScale;
        public readonly float MaximumAnisotropyScale;
        public readonly float MaximumAnisotropyRatio;
        public readonly int AnisotropyCellRadius;
        public readonly bool UseStylizedCrown;
        public readonly float CrownHeightRatio;
        public readonly float CrownFalloff;
        public readonly int CrownEdgeNeighborCount;
        public readonly int CrownInteriorNeighborCount;
        public LiquidRenderSettings(
            float targetVoxelSize,
            int maximumResolutionPerAxis,
            int maximumTriangleCount,
            float boundsPadding,
            float isoLevel,
            bool useAnisotropy = false,
            int anisotropyUpdateIntervalFrames = 2,
            int anisotropyNeighborThreshold = 5,
            float minimumAnisotropyScale = 0.65f,
            float maximumAnisotropyScale = 1.8f,
            float maximumAnisotropyRatio = 2.5f,
            bool useStylizedCrown = true,
            float crownHeightRatio = 0.8f,
            float crownFalloff = 1.5f,
            int crownEdgeNeighborCount = 4,
            int crownInteriorNeighborCount = 12)
        {
            TargetVoxelSize = RequirePositiveFinite(targetVoxelSize, nameof(targetVoxelSize));
            if (maximumResolutionPerAxis < 2)
                throw new ArgumentOutOfRangeException(nameof(maximumResolutionPerAxis));
            if (maximumTriangleCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumTriangleCount));

            MaximumResolutionPerAxis = maximumResolutionPerAxis;
            MaximumTriangleCount = maximumTriangleCount;
            BoundsPadding = RequireNonNegativeFinite(boundsPadding, nameof(boundsPadding));
            IsoLevel = RequirePositiveFinite(isoLevel, nameof(isoLevel));
            if (anisotropyUpdateIntervalFrames <= 0)
                throw new ArgumentOutOfRangeException(nameof(anisotropyUpdateIntervalFrames));
            if (anisotropyNeighborThreshold < 2)
                throw new ArgumentOutOfRangeException(nameof(anisotropyNeighborThreshold));

            MinimumAnisotropyScale = RequirePositiveFinite(
                minimumAnisotropyScale,
                nameof(minimumAnisotropyScale));
            MaximumAnisotropyScale = RequirePositiveFinite(
                maximumAnisotropyScale,
                nameof(maximumAnisotropyScale));
            MaximumAnisotropyRatio = RequirePositiveFinite(
                maximumAnisotropyRatio,
                nameof(maximumAnisotropyRatio));
            if (MinimumAnisotropyScale > 1f)
                throw new ArgumentOutOfRangeException(nameof(minimumAnisotropyScale));
            if (MaximumAnisotropyScale < 1f)
                throw new ArgumentOutOfRangeException(nameof(maximumAnisotropyScale));
            if (MaximumAnisotropyScale > 2f)
                throw new ArgumentOutOfRangeException(nameof(maximumAnisotropyScale));
            if (MaximumAnisotropyRatio < 1f)
                throw new ArgumentOutOfRangeException(nameof(maximumAnisotropyRatio));
            CrownHeightRatio = RequireNonNegativeFinite(
                crownHeightRatio,
                nameof(crownHeightRatio));
            if (CrownHeightRatio > 1f)
                throw new ArgumentOutOfRangeException(nameof(crownHeightRatio));
            CrownFalloff = RequirePositiveFinite(crownFalloff, nameof(crownFalloff));
            if (crownEdgeNeighborCount < 0)
                throw new ArgumentOutOfRangeException(nameof(crownEdgeNeighborCount));
            if (crownInteriorNeighborCount <= crownEdgeNeighborCount)
                throw new ArgumentOutOfRangeException(nameof(crownInteriorNeighborCount));

            UseAnisotropy = useAnisotropy;
            AnisotropyUpdateIntervalFrames = anisotropyUpdateIntervalFrames;
            AnisotropyNeighborThreshold = anisotropyNeighborThreshold;
            MinimumAnisotropyScale = minimumAnisotropyScale;
            MaximumAnisotropyScale = maximumAnisotropyScale;
            MaximumAnisotropyRatio = maximumAnisotropyRatio;
            UseStylizedCrown = useStylizedCrown;
            CrownEdgeNeighborCount = crownEdgeNeighborCount;
            CrownInteriorNeighborCount = crownInteriorNeighborCount;
            // 查询 Cell 半径必须覆盖最长椭球轴；硬上限 2 把最坏候选范围限制为 5^3=125 Cell。
            AnisotropyCellRadius = UseStylizedCrown && CrownHeightRatio > 0f
                ? 2
                : Mathf.CeilToInt(MaximumAnisotropyScale);
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
    }
}
