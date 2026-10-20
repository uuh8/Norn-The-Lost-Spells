using Game.Materials;
using Game.Combat;
using Game.Core;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 将“角色接触到的世界材料量”投影为 <see cref="StatusController"/> 上的持续状态。
    /// Runtime 先用世界的可查询区域做 Broadphase，再以 <see cref="Physics.OverlapBoxNonAlloc"/> 找到候选 Collider，
    /// 最后把 Collider Bounds 离散为 Global Cell 并从统一查询入口取得材料 Amount。Water、Fire、Poison、Sticky
    /// 分别映射到哪种状态以及每次最多施加多少强度，由初始化期的 Material Status Projection 数据决定。
    ///
    /// 这是 <c>Game.ElementField -> Game.Combat</c> 的单向 Runtime Adapter：ElementField 只提交“环境来源的持续状态”，
    /// 不拥有角色状态强度、自然衰减或材料反应。<see cref="StatusController"/> 才是这些 Combat 状态的可变所有者。
    ///
    /// PBF Liquid 的求解器即使进入 Solver-Sleeping，只要其低频 Gameplay Snapshot 仍有效，Exposure 仍可读取稳定液体；
    /// Solver 是否需要继续计算与材料是否仍存在于 Gameplay 世界中是两件独立的事。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ElementWorldRuntime))]
    public sealed class ElementWorldExposureSystem : MonoBehaviour
    {
        // 本轮最多缓存的“唯一 StatusController”数量。达到上限时宁可忽略额外目标，也不能在热路径扩容 List。
        private const int MaxTargetsPerQuery = 64;
        // 环境没有角色阵营；用保留的 byte.MaxValue 标记来源，便于 Combat 侧追踪而不伪装成玩家/敌人攻击。
        private const byte EnvironmentTeam = byte.MaxValue;
        // Profiler 只包住一次完整 Exposure 采样，方便把 Physics 查询、Cell 扫描和状态提交与 Solver/Renderer 分开观察。
        private static readonly ProfilerMarker ExposureMarker =
            new ProfilerMarker("ElementWorld.Exposure");

        [Header("Target Query")]
        // 只查询可能携带 StatusController 的层，避免把场景中所有碰撞体转成 Cell Range，控制 Physics 与三重循环成本。
        [SerializeField] private LayerMask _targetLayers;
        // Exposure 是受控低频采样而非每帧检测；它与 GPU Readback Interval 一起决定液体状态的最大可见延迟。
        [SerializeField, Min(0.05f)] private float _exposureInterval = 0.25f;
        // 每次接触刷新状态的保有时间 = Exposure Interval + grace，避免恰好在两次采样之间自然衰减而闪烁。
        [SerializeField, Min(0f)] private float _naturalDecayHoldGraceSeconds = 0.1f;
        [Tooltip("向 Collider 脚底额外探测的世界空间距离。用于覆盖 CharacterController Skin Width 与 PBF 粒子中心离散造成的接触缝隙；运行时最多钳制为一个 Cell。")]
        [SerializeField, Min(0f)] private float _groundContactProbeDepth = 0.15f;

        [Header("GPU Water Gameplay Snapshot")]
        [Tooltip("PBF 模式的 Broadphase 需要该只读 Occupancy Snapshot 的 Bounds；材料 Amount 仍经 Runtime 的统一 Query Router 读取。")]
        [SerializeField] private MonoBehaviour _liquidOccupancyComponent;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        // 以下字段只用于 Inspector 中观察本次 Exposure 的结果；它们不参与状态规则，避免调试信息反向影响 Gameplay。
        [SerializeField] private int _lastColliderCount;
        [SerializeField] private int _lastStatusTargetCount;
        [SerializeField] private int _lastWetApplicationCount;
        [SerializeField] private int _lastBurningApplicationCount;
        [SerializeField] private int _lastPoisonedApplicationCount;
        [SerializeField] private int _lastStickyApplicationCount;
        [SerializeField] private int _lastMaximumWaterAmount;
        [SerializeField] private int _lastMaximumFireAmount;
        [SerializeField] private int _lastMaximumPoisonAmount;
        [SerializeField] private int _lastMaximumStickyAmount;
        [SerializeField] private bool _lastQueryMayBeSaturated;

        // 容量在 Component 构造时固定；Exposure 热路径只覆盖数组，不创建临时集合或 LINQ Enumerator。
        // _targetIds/_targets 以 targetIndex 对齐，保证同一角色拥有多个 Collider 时仍只提交一次状态。
        private readonly Collider[] _colliderBuffer = new Collider[MaxTargetsPerQuery];
        private readonly int[] _targetIds = new int[MaxTargetsPerQuery];
        private readonly StatusController[] _targets = new StatusController[MaxTargetsPerQuery];
        // 平铺工作区的逻辑形状为 [targetIndex, bindingIndex]。每格保存目标 Bounds 覆盖区域内该材料的最大 Amount，
        // 因而不需要在采样后创建 Dictionary 或二维数组，也不会把横跨多个格子的同一种状态重复累加。
        private byte[] _maximumAmounts;

        // Scene 中的世界协调器：提供坐标系、有效查询范围和统一的 Material Amount 只读路由。
        private ElementWorldRuntime _runtime;
        // 时间累加器使 Exposure 使用固定采样间隔，不依赖某一帧的 deltaTime 恰好等于 interval。
        private float _elapsed;
        // GPU 液体已经发布到 CPU 后应立即参与 Gameplay；记录已消费版本可把两个 4 Hz 时钟同步起来，
        // 同时防止同一份 Snapshot 在每个 Update 都额外触发 Physics Query。
        private uint _lastConsumedLiquidSnapshotVersion;
        private bool _hasConsumedLiquidSnapshotVersion;
        private bool _hasAppliedExposure;
        // 当前轮已去重并写入工作区的目标数量；ClearPreviousTargets 后才允许从 0 开始复用。
        private int _targetCount;
        // 环境 GameObject 的稳定实例 ID；StatusController 用它区分环境持续接触与其他来源的同类状态。
        private int _environmentSourceId;
        // 仅供 Broadphase 取得 GPU 液体快照的有效范围；实际 Amount 查询由 _runtime.MaterialAmounts 路由。
        private ILiquidOccupancyReadOnly _liquidOccupancy;
        // 初始化期冻结的“材料 -> 状态”规则数组；热路径按索引读取，避免逐格查询 ScriptableObject 或 Dictionary。
        private MaterialStatusProjectionSnapshot _statusProjection;

        public int LastColliderCount => _lastColliderCount;
        public int LastStatusTargetCount => _lastStatusTargetCount;
        public int LastWetApplicationCount => _lastWetApplicationCount;
        public int LastBurningApplicationCount => _lastBurningApplicationCount;
        public int LastPoisonedApplicationCount => _lastPoisonedApplicationCount;
        public int LastStickyApplicationCount => _lastStickyApplicationCount;
        public bool LastQueryMayBeSaturated => _lastQueryMayBeSaturated;

        private void Awake()
        {
            _runtime = GetComponent<ElementWorldRuntime>();
            // Inspector 只能序列化 MonoBehaviour，运行时再收窄为只读 Contract，阻止 Exposure 意外修改 GPU 粒子状态。
            _liquidOccupancy = _liquidOccupancyComponent as ILiquidOccupancyReadOnly;
            // 同一 GameObject 的默认 Bridge 是配置缺省时的安全回退；显式引用仍优先，便于测试或替换实现。
            if (_liquidOccupancy == null)
                _liquidOccupancy = GetComponent<FluidGameplayOccupancyBridge>();
            _environmentSourceId = gameObject.GetInstanceID();
            InitializeProjection();
        }
        private void InitializeProjection()
        {
            _statusProjection = _runtime != null ? _runtime.MaterialStatusProjection : null;
            // 每个目标/规则一个槽。Awake若早于世界初始化，Start再建立一次；Update绝不懒分配。
            _maximumAmounts = _statusProjection != null
                ? new byte[MaxTargetsPerQuery * _statusProjection.Count]
                : System.Array.Empty<byte>();
        }

        private void OnEnable()
        {
            // 清空旧计时；首份有效液体 Snapshot 可提前触发，缺省路径仍等待完整 interval。
            _elapsed = 0f;
            _hasAppliedExposure = false;
            _lastConsumedLiquidSnapshotVersion = 0u;
            _hasConsumedLiquidSnapshotVersion = false;
        }

        private void Start()
        {
            // 同一GameObject的Awake先后不由组件排列保证；所有Awake完成后才能消费生产规则快照。
            if (_statusProjection == null) InitializeProjection();
            // Awake 负责建立引用；Start 再报告初始化契约问题，给其他同帧 Awake 的 Runtime 留出初始化顺序。
            if (_runtime == null || !_runtime.IsInitialized)
            {
                GameLog.Error(
                    "ElementWorldExposureSystem requires an initialized ElementWorldRuntime on the same GameObject.",
                    "ElementField");
            }

            if (_targetLayers.value == 0)
            {
                GameLog.Warn(
                    "ElementWorldExposureSystem has an empty Target Layers mask.",
                    "ElementField");
            }
        }

        private void OnDisable()
        {
            // 只清除本 Adapter 的临时引用与工作区；已提交的持续状态交由 StatusController 的 hold/decay 规则收口。
            ClearPreviousTargets();
            _elapsed = 0f;
            _hasAppliedExposure = false;
            _lastConsumedLiquidSnapshotVersion = 0u;
            _hasConsumedLiquidSnapshotVersion = false;
        }

        private void Update()
        {
            // 时间暂停时世界材料接触也暂停，避免打开 UI 后角色状态仍在悄悄积累或衰减。
            if (Time.timeScale > 0f)
                TickExposure(Time.deltaTime);
        }

        /// <summary>
        /// 用累加器把任意帧率的 deltaTime 转为固定间隔 Exposure Tick。
        /// <paramref name="deltaTime"/> 无效时直接拒绝，避免 NaN 进入累加器后让系统永久无法恢复；较大帧间隔会
        /// 通过 while 补齐应发生的采样轮次，使单位时间内的状态施加量不依赖帧率。
        /// </summary>
        public void TickExposure(float deltaTime)
        {
            if (deltaTime <= 0f
                || float.IsNaN(deltaTime)
                || float.IsInfinity(deltaTime)
                || _runtime == null
                || !_runtime.IsInitialized || _statusProjection == null)
            {
                return;
            }

            // OnValidate 只覆盖 Inspector 编辑；这里再次钳制可防御 Runtime 工具或损坏序列化数据。
            float interval = Mathf.Max(0.05f, _exposureInterval);

            _elapsed += deltaTime;
            if (TryConsumeNewLiquidSnapshot() && _elapsed < interval)
            {
                // 首次接触保留立即生效的一次剂量；之后提前采样只消费已过去时间对应的份额。
                // 否则 Snapshot 加快或恰好落在周期采样后，会提高 Burning/Wet 等状态的增长速度。
                float doseScale = _hasAppliedExposure ? _elapsed / interval : 1f;
                _elapsed = 0f;
                using (ExposureMarker.Auto())
                    ApplyExposureOnce(doseScale);
                return;
            }

            while (_elapsed >= interval)
            {
                _elapsed -= interval;
                using (ExposureMarker.Auto())
                    ApplyExposureOnce();
            }
        }

        private bool TryConsumeNewLiquidSnapshot()
        {
            if (_liquidOccupancy == null || !_liquidOccupancy.HasValidSnapshot)
                return false;

            uint currentVersion = _liquidOccupancy.SnapshotVersion;
            if (_hasConsumedLiquidSnapshotVersion
                && currentVersion == _lastConsumedLiquidSnapshotVersion)
            {
                return false;
            }

            _lastConsumedLiquidSnapshotVersion = currentVersion;
            _hasConsumedLiquidSnapshotVersion = true;
            return true;
        }

        /// <summary>测试入口复用真实时间累加逻辑，避免测试绕开 Update 中最容易出错的 interval 行为。</summary>
        public void TickForTests(float deltaTime)
        {
            TickExposure(deltaTime);
        }

        private void ApplyExposureOnce(float doseScale = 1f)
        {
            _hasAppliedExposure = true;
            // 上一轮的去重表和最大值只能服务上一轮；先清空保证离开材料区域的目标不会继续被错误刷新。
            ClearPreviousTargets();
            ResetLastDebugStats();

            // Legacy Cell 与 GPU Liquid Snapshot 的有效区域可能不同。Planner 根据最终 Water Backend 选择 0~多个
            // Broadphase Bounds；Fire 的 Cell 路径不能因 Liquid Snapshot 尚未完成而被提前跳过。
            Bounds legacyBounds = _runtime.GetActiveWorldBounds();
            bool hasLiquidSnapshot = _liquidOccupancy != null
                && _liquidOccupancy.HasValidSnapshot;
            Bounds liquidBounds = hasLiquidSnapshot
                ? _liquidOccupancy.SnapshotBounds
                : default;
            ElementExposureBroadphasePlan broadphase = ElementExposureBroadphasePlanner.Plan(
                _runtime.EffectiveWaterSimulationMode,
                legacyBounds,
                hasLiquidSnapshot,
                liquidBounds);

            // 多个 Broadphase Bounds 可能命中同一个角色，FindOrAddTarget 会将它们归并到同一个 targetIndex。
            if (_runtime.ActivityExpansionEnabled && _runtime.ActivityRegions.Count > 0)
            {
                // 每个小区域独立 Broadphase，目标去重缓冲跨区域共享，重叠不会叠加一次状态。
                for (int queryIndex = 0; queryIndex < _runtime.ActivityRegions.Count; queryIndex++)
                    QueryAndSampleTargets(_runtime.ActivityRegions.GetBounds(queryIndex));
            }
            else
            {
                for (int queryIndex = 0; queryIndex < broadphase.QueryCount; queryIndex++)
                    QueryAndSampleTargets(broadphase.GetQueryBounds(queryIndex));
            }

            // 这一次提交会刷新 StatusController 的“持续接触”时间；hold 略大于采样周期，保障正常帧抖动下连续接触。
            float holdSeconds = Mathf.Max(0.05f, _exposureInterval)
                + _naturalDecayHoldGraceSeconds;
            for (int i = 0; i < _targetCount; i++)
            {
                StatusController target = _targets[i];
                if (target == null || !target.isActiveAndEnabled)
                    continue;

                for (int bindingIndex = 0; bindingIndex < _statusProjection.Count; bindingIndex++)
                {
                    MaterialStatusProjection binding = _statusProjection.Get(bindingIndex);
                    // 目标 Bounds 中只取最大 Amount：大 Collider 跨多格时，“接触到最浓的一格”决定本 Tick 强度，
                    // 不会因 Collider 体积或多个格子而把同一材料的效果倍增。
                    byte amount = _maximumAmounts[i * _statusProjection.Count + bindingIndex];
                    if (amount == 0 || binding.MaximumApplyPerExposureTick <= 0f)
                        continue;

                    // byte Amount 归一化到 [0,1] 后乘 Authoring 的每 Tick 上限；映射数据与采样机制分离，
                    // 新材料/新状态只需添加 Projection Binding，不需要在此处添加新的 if 分支。
                    target.ApplySustainedStatus(
                        binding.Status,
                        binding.MaximumApplyPerExposureTick * (amount / (float)byte.MaxValue) * doseScale,
                        _environmentSourceId,
                        EnvironmentTeam,
                        holdSeconds);
                    if (binding.Status == StatusKind.Wet)
                    {
                        _lastWetApplicationCount++;
                        _lastMaximumWaterAmount = Mathf.Max(_lastMaximumWaterAmount, amount);
                    }
                    else if (binding.Status == StatusKind.Burning)
                    {
                        _lastBurningApplicationCount++;
                        _lastMaximumFireAmount = Mathf.Max(_lastMaximumFireAmount, amount);
                    }
                    else if (binding.Status == StatusKind.Poisoned)
                    {
                        _lastPoisonedApplicationCount++;
                        _lastMaximumPoisonAmount = Mathf.Max(_lastMaximumPoisonAmount, amount);
                    }
                    else if (binding.Status == StatusKind.Sticky)
                    {
                        _lastStickyApplicationCount++;
                        _lastMaximumStickyAmount = Mathf.Max(_lastMaximumStickyAmount, amount);
                    }
                }
            }

            _lastStatusTargetCount = _targetCount;
        }

        private void QueryAndSampleTargets(Bounds queryBounds)
        {
            // 空 Bounds 无法代表任何可接触世界区域，也避免传入退化 extents 给 Physics API。
            if (queryBounds.size.sqrMagnitude <= 0f)
                return;

            // NonAlloc 版本把 Collider 写入预分配 buffer；返回值到达容量时代表可能还有候选项未被处理，
            // 这是明确的容量边界，而不是允许在 Gameplay 热路径动态分配更多内存。
            int colliderCount = Physics.OverlapBoxNonAlloc(
                queryBounds.center,
                queryBounds.extents,
                _colliderBuffer,
                Quaternion.identity,
                _targetLayers,
                QueryTriggerInteraction.Collide);
            if (colliderCount >= MaxTargetsPerQuery)
                _lastQueryMayBeSaturated = true;
            _lastColliderCount += colliderCount;

            for (int i = 0; i < colliderCount; i++)
            {
                Collider candidate = _colliderBuffer[i];
                // 及时释放 Unity Object 引用，避免 buffer 在下一轮前无意义地延长已销毁/禁用对象的可达性。
                _colliderBuffer[i] = null;
                if (candidate == null)
                    continue;

                // Collider 可以挂在角色子节点；状态所有者位于角色根节点，所以向父级查找并随后按 root InstanceID 去重。
                StatusController target = candidate.GetComponentInParent<StatusController>();
                if (target == null || !target.isActiveAndEnabled)
                    continue;

                int targetIndex = FindOrAddTarget(target);
                if (targetIndex >= 0)
                {
                    SampleBounds(candidate.bounds, queryBounds, targetIndex);
                }
            }
        }

        private int FindOrAddTarget(StatusController target)
        {
            int targetId = target.gameObject.GetInstanceID();
            // 线性扫描上限固定为 64；相比 Dictionary，它没有额外分配，且该规模下常数成本更可预测。
            for (int i = 0; i < _targetCount; i++)
            {
                if (_targetIds[i] == targetId)
                    return i;
            }

            // 容量耗尽时拒绝新目标；调用方仍可继续处理已缓存目标，下一轮再尝试，而不是破坏数组边界。
            if (_targetCount >= MaxTargetsPerQuery)
            {
                _lastQueryMayBeSaturated = true;
                return -1;
            }

            int index = _targetCount++;
            _targetIds[index] = targetId;
            _targets[index] = target;
            return index;
        }

        /// <summary>
        /// 在一个目标 Collider 与一个 Broadphase 区域的交集中做 Narrowphase。
        /// 连续 Bounds 被映射成有限的 Global Cell 闭区间，再通过 MaterialStateQueryRouter 查询每条投影规则的材料。
        /// 注意这里从不直接访问 Liquid Snapshot：统一 Router 使用最终 Material Route 选择 Cell 或 GPU Occupancy，
        /// 因而不会把迁移残留的 Cell Water 与 GPU Water 再相加一次。
        /// </summary>
        private void SampleBounds(
            Bounds targetBounds,
            Bounds activeBounds,
            int targetIndex)
        {
            // CharacterController 的 Skin Width 会让 Collider 数学底面略高于真实落脚面；PBF 粒子又以中心点
            // 归入 0.25m Cell。只采样原始 AABB 会在 Terrain 上漏掉“画面已踩入液体、粒子 Cell 却刚好在脚下”
            // 的接触。这里只向下扩一条受控 Foot Probe，不扩大水平或头顶范围，避免隔墙/擦边误触发。
            float probeDepth = Mathf.Min(
                Mathf.Max(0f, _groundContactProbeDepth),
                _runtime.CellSize);
            Vector3 contactMinimum = targetBounds.min;
            contactMinimum.y -= probeDepth;
            Bounds contactBounds = targetBounds;
            contactBounds.SetMinMax(contactMinimum, targetBounds.max);

            if (!contactBounds.Intersects(activeBounds))
                return;

            // 气体按胸部／头部吸入采样，不能复用液体的脚底探针。
            // 多 Collider 仍汇聚到原 target/material 最大值，角色只提交一次状态。
            for (int bindingIndex = 0; bindingIndex < _statusProjection.Count; bindingIndex++)
            {
                var binding = _statusProjection.Get(bindingIndex);
                if (!_runtime.MaterialRoutes.TryResolve(binding.Material, out var backend)
                    || backend != MaterialSimulationBackendKind.GridGas) continue;
                Vector3 chest = targetBounds.center; chest.y = Mathf.Lerp(targetBounds.min.y, targetBounds.max.y, .55f);
                Vector3 head = chest; head.y = Mathf.Lerp(targetBounds.min.y, targetBounds.max.y, .9f);
                var chestCell = ElementWorldCoordinates.WorldToGlobalCell(chest, _runtime.Origin, _runtime.CellSize);
                var headCell = ElementWorldCoordinates.WorldToGlobalCell(head, _runtime.Origin, _runtime.CellSize);
                byte chestAmount = 0, headAmount = 0;
                if (activeBounds.Contains(chest)) _runtime.MaterialAmounts.TryGetAmount(chestCell, binding.Material, out chestAmount);
                if (activeBounds.Contains(head)) _runtime.MaterialAmounts.TryGetAmount(headCell, binding.Material, out headAmount);
                int workspaceIndex = targetIndex * _statusProjection.Count + bindingIndex;
                _maximumAmounts[workspaceIndex] = (byte)Mathf.Max(_maximumAmounts[workspaceIndex], Mathf.Max(chestAmount, headAmount));
            }

            // 只采样目标真正落在可查询世界区域的部分；Interest Region 外的保留 Chunk 不应持续施加环境状态。
            Vector3 sampleMin = Vector3.Max(contactBounds.min, activeBounds.min);
            Vector3 sampleMax = Vector3.Min(contactBounds.max, activeBounds.max);
            // 世界 Bounds 采用 half-open [min,max) 约定。max 减极小 inset 后再 floor，得到可安全 for-loop 的闭区间 max，
            // 防止落在相邻格边界的 Collider 将不属于交集的下一格也采样进去。
            float inset = Mathf.Min(0.0001f, _runtime.CellSize * 0.001f);
            Vector3 inclusiveMax = new Vector3(
                Mathf.Max(sampleMin.x, sampleMax.x - inset),
                Mathf.Max(sampleMin.y, sampleMax.y - inset),
                Mathf.Max(sampleMin.z, sampleMax.z - inset));

            Vector3Int min = ElementWorldCoordinates.WorldToGlobalCell(
                sampleMin,
                _runtime.Origin,
                _runtime.CellSize);
            Vector3Int max = ElementWorldCoordinates.WorldToGlobalCell(
                inclusiveMax,
                _runtime.Origin,
                _runtime.CellSize);

            // 三重循环是 Narrowphase 成本，其规模由“Collider 与活动世界相交的格子数 × Binding 数”决定。
            // 通常角色 Collider 很小；Target Layer 和 Broadphase 都应维持这个前提，不能把它当成全世界扫描。
            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                Vector3Int globalCell = new Vector3Int(x, y, z);
                // Router 是唯一材料读取入口：它根据 Route 选 Element Cell 或只读 GPU Occupancy，并保留“未知/不支持”失败语义。
                IMaterialAmountReadOnly query = _runtime.MaterialAmounts;
                for (int bindingIndex = 0; bindingIndex < _statusProjection.Count; bindingIndex++)
                {
                    MaterialStatusProjection binding = _statusProjection.Get(bindingIndex);
                    if (_runtime.MaterialRoutes.TryResolve(binding.Material, out var backend)
                        && backend == MaterialSimulationBackendKind.GridGas) continue;
                    byte amount = 0;
                    query?.TryGetAmount(globalCell, binding.Material, out amount);
                    // 状态接触是只读查询，Archive Amount 已能提供 Wet/Poisoned/Sticky。
                    // 每次采样都唤醒整页会反复重建 GPU 粒子，使角色脚下水面不断换形。
                    // 只有真正改变液体的写入、反应或物理交互才应请求恢复。
                    // 每个 target/material 只保留最大量，多个 Collider、多个 Broadphase 或多个格子均会汇聚到同一槽。
                    int workspaceIndex = targetIndex * _statusProjection.Count + bindingIndex;
                    if (amount > _maximumAmounts[workspaceIndex])
                        _maximumAmounts[workspaceIndex] = amount;
                }
            }
        }

        private void ClearPreviousTargets()
        {
            // 不必清整块最大容量数组：只清本轮实际使用的 target 行，工作量随实际命中目标数增长。
            for (int i = 0; i < _targetCount; i++)
            {
                _targetIds[i] = 0;
                _targets[i] = null;
                if (_statusProjection != null)
                    System.Array.Clear(_maximumAmounts, i * _statusProjection.Count, _statusProjection.Count);
            }
            _targetCount = 0;
        }

        private void ResetLastDebugStats()
        {
            // Inspector 统计按 Exposure Tick 归零，显示的是最近一次采样，不是无界累积计数。
            _lastColliderCount = 0;
            _lastStatusTargetCount = 0;
            _lastWetApplicationCount = 0;
            _lastBurningApplicationCount = 0;
            _lastPoisonedApplicationCount = 0;
            _lastStickyApplicationCount = 0;
            _lastQueryMayBeSaturated = false;
            _lastMaximumWaterAmount = 0;
            _lastMaximumFireAmount = 0;
            _lastMaximumPoisonAmount = 0;
            _lastMaximumStickyAmount = 0;
        }

        private void OnValidate()
        {
            // Inspector 的 Min Attribute 不保护脚本/序列化写入；钳制防止 0 间隔造成每帧甚至无限频率采样。
            _exposureInterval = Mathf.Max(0.05f, _exposureInterval);
            _naturalDecayHoldGraceSeconds = Mathf.Max(0f, _naturalDecayHoldGraceSeconds);
            _groundContactProbeDepth = Mathf.Max(0f, _groundContactProbeDepth);
        }
    }
}
