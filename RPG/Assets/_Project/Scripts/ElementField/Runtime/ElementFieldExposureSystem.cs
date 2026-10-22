using Game.Materials;
using Game.Combat;
using Game.Core;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 把 ElementField 中的 Water/Fire 反向映射为角色 Status。
    ///
    /// 系统每个 Exposure Interval 只对整个 Field 做一次 NonAlloc Broadphase Query，
    /// 再把命中的 Collider Bounds 映射为 Cell 范围；不会为每个 Cell 创建 Trigger/GameObject，
    /// 也不会让每个角色在 Update 中各自扫描整个 Grid。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ElementFieldRuntime))]
    public sealed class ElementFieldExposureSystem : MonoBehaviour
    {
        private const int MaxTargetsPerQuery = 64;
        private const byte EnvironmentTeam = byte.MaxValue;
        private static readonly ProfilerMarker ExposureMarker =
            new ProfilerMarker("ElementField.Exposure");

        [Header("Target Query")]
        [Tooltip("只查询可接收 Wet/Burning 的 Player/Enemy Collider Layer；Collider 所在子物体也必须位于这些 Layer。")]
        [SerializeField] private LayerMask _targetLayers;

        [Tooltip("两次环境暴露之间的秒数。降低频率可减少 Physics Query，但状态增长会更阶梯化。")] 
        [SerializeField, Min(0.05f)] private float _exposureInterval = 0.25f;

        [Tooltip("离开 Cell 后额外暂停 NaturalDecay 的抗抖时间；不会暂停 Reaction 或 DoT。")] 
        [SerializeField, Min(0f)] private float _naturalDecayHoldGraceSeconds = 0.1f;

        [Header("Status Apply Per Exposure")]
        [Tooltip("满 Water Cell（Amount=255）在一次 Exposure 中增加的 Wet 强度。")]
        [SerializeField, Min(0f)] private float _maxWetApplyPerTick = 10f;

        [Tooltip("满 Fire Cell（Amount=255）在一次 Exposure 中增加的 Burning 强度。")] 
        [SerializeField, Min(0f)] private float _maxBurningApplyPerTick = 10f;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private int _lastColliderCount;
        [SerializeField] private int _lastStatusTargetCount;
        [SerializeField] private int _lastWetApplicationCount;
        [SerializeField] private int _lastBurningApplicationCount;
        [SerializeField] private int _lastMaximumWaterAmount;
        [SerializeField] private int _lastMaximumFireAmount;

        // 所有容器都在 Component 构造阶段一次性创建；Update/Exposure 热路径不 new、不用 LINQ。
        private readonly Collider[] _colliderBuffer = new Collider[MaxTargetsPerQuery];
        private readonly int[] _targetIds = new int[MaxTargetsPerQuery];
        private readonly StatusController[] _targets = new StatusController[MaxTargetsPerQuery];
        private readonly byte[] _maxWater = new byte[MaxTargetsPerQuery];
        private readonly byte[] _maxFire = new byte[MaxTargetsPerQuery];

        private ElementFieldRuntime _runtime;
        private float _elapsed;
        private int _targetCount;
        private int _environmentSourceId;

        private void Awake()
        {
            _runtime = GetComponent<ElementFieldRuntime>();
            // Environment Damage/Status 只保存稳定的 InstanceID 快照，不让 Status 持有场景对象引用。
            _environmentSourceId = gameObject.GetInstanceID();
        }

        private void OnEnable()
        {
            _elapsed = 0f;
        }

        private void Start()
        {
            if (_runtime == null || !_runtime.IsInitialized)
            {
                GameLog.Error(
                    "ElementFieldExposureSystem requires an initialized ElementFieldRuntime on the same GameObject.",
                    "ElementField");
            }

            if (_targetLayers.value == 0)
            {
                GameLog.Warn(
                    "ElementFieldExposureSystem has an empty Target Layers mask; no character can receive exposure.",
                    "ElementField");
            }
        }

        private void OnDisable()
        {
            ClearPreviousTargets();
            _elapsed = 0f;
        }

        private void Update()
        {
            // Wand Editor 用 timeScale=0 暂停 Gameplay；暂停时环境不能继续叠加 Wet/Burning。
            if (Time.timeScale <= 0f)
                return;

            TickExposure(Time.deltaTime);
        }

        /// <summary>
        /// 把不稳定的 Rendering deltaTime 累加成较低频的 Exposure Step。
        /// Status Apply Amount 的语义是“每次 Exposure”，因此间隔也是 Gameplay 调参的一部分。
        /// </summary>
        public void TickExposure(float deltaTime)
        {
            if (deltaTime <= 0f
                || float.IsNaN(deltaTime)
                || float.IsInfinity(deltaTime)
                || _runtime == null
                || !_runtime.IsInitialized)
            {
                return;
            }

            float interval = Mathf.Max(0.05f, _exposureInterval);
            _elapsed += deltaTime;
            while (_elapsed >= interval)
            {
                _elapsed -= interval;
                // AutoScope 是 struct；Profiler 未采样时开销很低，也不会产生逐 Tick GC Alloc。
                using (ExposureMarker.Auto())
                {
                    ApplyExposureOnce();
                }
            }
        }

        /// <summary>
        /// 自动化测试与确定性 Debug 入口；故意复用正式累计器，而不是提供一条绕过间隔的测试专线。
        /// </summary>
        public void TickForTests(float deltaTime)
        {
            TickExposure(deltaTime);
        }

        private void ApplyExposureOnce()
        {
            ClearPreviousTargets();
            ResetLastDebugStats();

            Vector3Int dimensions = _runtime.Dimensions;
            float cellSize = _runtime.CellSize;
            Vector3 fieldSize = new Vector3(
                dimensions.x * cellSize,
                dimensions.y * cellSize,
                dimensions.z * cellSize);
            Vector3 queryCenter = _runtime.Origin + fieldSize * 0.5f;

            // Broadphase 只找“可能与 Field 相交”的 Collider；精确 Cell 强度由后续 Bounds 扫描决定。
            int colliderCount = Physics.OverlapBoxNonAlloc(
                queryCenter,
                fieldSize * 0.5f,
                _colliderBuffer,
                Quaternion.identity,
                _targetLayers,
                QueryTriggerInteraction.Collide);
            _lastColliderCount = colliderCount;

            for (int i = 0; i < colliderCount; i++)
            {
                Collider candidate = _colliderBuffer[i];
                _colliderBuffer[i] = null;
                if (candidate == null)
                    continue;

                StatusController target = candidate.GetComponentInParent<StatusController>();
                if (target == null || !target.isActiveAndEnabled)
                    continue;

                int targetIndex = FindOrAddTarget(target);
                if (targetIndex < 0)
                    continue;

                SampleBounds(candidate.bounds, ref _maxWater[targetIndex], ref _maxFire[targetIndex]);
            }

            for (int i = 0; i < _targetCount; i++)
            {
                StatusController target = _targets[i];
                if (target == null || !target.isActiveAndEnabled)
                    continue;

                // Hold 覆盖到下一次 Exposure 再加少量 Grace，使 4 Hz 离散补充在 Gameplay 上
                // 被理解为连续接触；它只影响 NaturalDecay，不会阻止 Extinguish 等反应。
                float naturalDecayHoldSeconds = Mathf.Max(0.05f, _exposureInterval)
                    + _naturalDecayHoldGraceSeconds;

                if (_maxWater[i] > _lastMaximumWaterAmount)
                    _lastMaximumWaterAmount = _maxWater[i];
                if (_maxFire[i] > _lastMaximumFireAmount)
                    _lastMaximumFireAmount = _maxFire[i];

                if (_maxWater[i] > 0 && _maxWetApplyPerTick > 0f)
                {
                    float wetAmount = _maxWetApplyPerTick * (_maxWater[i] / (float)byte.MaxValue);
                    target.ApplySustainedStatus(
                        StatusKind.Wet,
                        wetAmount,
                        _environmentSourceId,
                        EnvironmentTeam,
                        naturalDecayHoldSeconds);
                    _lastWetApplicationCount++;
                }

                if (_maxFire[i] > 0 && _maxBurningApplyPerTick > 0f)
                {
                    float burningAmount = _maxBurningApplyPerTick * (_maxFire[i] / (float)byte.MaxValue);
                    target.ApplySustainedStatus(
                        StatusKind.Burning,
                        burningAmount,
                        _environmentSourceId,
                        EnvironmentTeam,
                        naturalDecayHoldSeconds);
                    _lastBurningApplicationCount++;
                }
            }

            _lastStatusTargetCount = _targetCount;
        }

        private int FindOrAddTarget(StatusController target)
        {
            int targetId = target.gameObject.GetInstanceID();
            for (int i = 0; i < _targetCount; i++)
            {
                if (_targetIds[i] == targetId)
                    return i;
            }

            // NonAlloc Query 的容量也是本轮去重容量；超出预算的 Collider/Target 留到后续性能扩展处理。
            if (_targetCount >= MaxTargetsPerQuery)
                return -1;

            int index = _targetCount++;
            _targetIds[index] = targetId;
            _targets[index] = target;
            return index;
        }

        private void SampleBounds(Bounds bounds, ref byte maxWater, ref byte maxFire)
        {
            Vector3 origin = _runtime.Origin;
            Vector3Int dimensions = _runtime.Dimensions;
            float cellSize = _runtime.CellSize;
            Vector3 fieldMax = origin + new Vector3(
                dimensions.x * cellSize,
                dimensions.y * cellSize,
                dimensions.z * cellSize);

            // 先做连续空间 AABB Intersection；完全在 Field 外的 Target 不进入 Cell 遍历。
            if (bounds.max.x <= origin.x || bounds.min.x >= fieldMax.x
                || bounds.max.y <= origin.y || bounds.min.y >= fieldMax.y
                || bounds.max.z <= origin.z || bounds.min.z >= fieldMax.z)
            {
                return;
            }

            Vector3 sampleMin = Vector3.Max(bounds.min, origin);
            Vector3 sampleMax = Vector3.Min(bounds.max, fieldMax);

            // Bounds.max 落在两个 Cell 的公共边界时，那个“只接触、无体积重叠”的下一格不应被采样。
            float inset = Mathf.Min(0.0001f, cellSize * 0.001f);
            Vector3 inclusiveMax = new Vector3(
                Mathf.Max(sampleMin.x, sampleMax.x - inset),
                Mathf.Max(sampleMin.y, sampleMax.y - inset),
                Mathf.Max(sampleMin.z, sampleMax.z - inset));

            Vector3Int min = ClampToField(
                ElementFieldCoordinates.WorldToCellUnchecked(sampleMin, origin, cellSize),
                dimensions);
            Vector3Int max = ClampToField(
                ElementFieldCoordinates.WorldToCellUnchecked(inclusiveMax, origin, cellSize),
                dimensions);

            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                ElementCell cell = _runtime.GetCell(x, y, z);
                if (cell.IsEmpty)
                    continue;

                if (cell.MaterialKind == MaterialId.Water && cell.Amount > maxWater)
                    maxWater = cell.Amount;
                else if (cell.MaterialKind == MaterialId.Fire && cell.Amount > maxFire)
                    maxFire = cell.Amount;
            }
        }

        private void ClearPreviousTargets()
        {
            for (int i = 0; i < _targetCount; i++)
            {
                _targetIds[i] = 0;
                _targets[i] = null;
                _maxWater[i] = 0;
                _maxFire[i] = 0;
            }
            _targetCount = 0;
        }

        private void ResetLastDebugStats()
        {
            _lastColliderCount = 0;
            _lastStatusTargetCount = 0;
            _lastWetApplicationCount = 0;
            _lastBurningApplicationCount = 0;
            _lastMaximumWaterAmount = 0;
            _lastMaximumFireAmount = 0;
        }

        private static Vector3Int ClampToField(Vector3Int coordinate, Vector3Int dimensions)
        {
            return new Vector3Int(
                Mathf.Clamp(coordinate.x, 0, dimensions.x - 1),
                Mathf.Clamp(coordinate.y, 0, dimensions.y - 1),
                Mathf.Clamp(coordinate.z, 0, dimensions.z - 1));
        }

        private void OnValidate()
        {
            _exposureInterval = Mathf.Max(0.05f, _exposureInterval);
            _naturalDecayHoldGraceSeconds = Mathf.Max(0f, _naturalDecayHoldGraceSeconds);
            _maxWetApplyPerTick = Mathf.Max(0f, _maxWetApplyPerTick);
            _maxBurningApplyPerTick = Mathf.Max(0f, _maxBurningApplyPerTick);
        }
    }
}
