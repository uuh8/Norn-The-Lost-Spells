using System;
using Game.Core;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 把不稳定的 Rendering Frame 时间转换为稳定 Simulation Tick 的值类型累计器。
    /// 使用 double 保存时间债务，可降低长时间累加 float 造成的边界漂移；本结构不分配托管内存。
    /// </summary>
    internal struct ElementFieldTickAccumulator
    {
        private readonly double _tickInterval;
        private readonly int _maxCatchUpTicks;
        private double _accumulatedTime;

        public ElementFieldTickAccumulator(float tickRate, int maxCatchUpTicks)
        {
            if (tickRate <= 0f || float.IsNaN(tickRate) || float.IsInfinity(tickRate))
                throw new ArgumentOutOfRangeException(nameof(tickRate));
            if (maxCatchUpTicks <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCatchUpTicks));

            _tickInterval = 1d / tickRate;
            _maxCatchUpTicks = maxCatchUpTicks;
            _accumulatedTime = 0d;
            DroppedDebtLastConsume = false;
        }

        public bool DroppedDebtLastConsume { get; private set; }

        public int Consume(float deltaTime)
        {
            if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
                throw new ArgumentOutOfRangeException(nameof(deltaTime));

            DroppedDebtLastConsume = false;
            if (deltaTime == 0f)
                return 0;

            _accumulatedTime += deltaTime;
            int ticks = 0;
            while (_accumulatedTime >= _tickInterval && ticks < _maxCatchUpTicks)
            {
                _accumulatedTime -= _tickInterval;
                ticks++;
            }

            if (ticks == _maxCatchUpTicks && _accumulatedTime >= _tickInterval)
            {
                // 卡顿产生的旧债务继续保留会形成 Spiral of Death：追赶越多，当前帧越慢，下一帧欠债越多。
                // Vertical Slice 选择牺牲极端卡顿期间的模拟时间完整性，换取恢复后的帧稳定性。
                _accumulatedTime = 0d;
                DroppedDebtLastConsume = true;
            }

            return ticks;
        }
    }

    /// <summary>
    /// 有限元素场的 Unity Adapter：负责生命周期、Profile 快照、固定 Tick 调度、Solid Bake 与只读查询。
    /// Water/Fire 规则仍由纯 ElementFieldSimulator 实现，Rendering 和 Projectile 不会取得 Grid 写权限。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ElementFieldSolidBaker))]
    public sealed class ElementFieldRuntime : MonoBehaviour, IElementFieldReadOnly, IElementWriteSink
    {
        private const float TransformTolerance = 0.0001f;
        private const float CatchUpWarningCooldown = 5f;

        private static readonly ProfilerMarker ApplyWritesMarker =
            new ProfilerMarker("ElementField.ApplyWrites");
        private static readonly ProfilerMarker SimulateMarker =
            new ProfilerMarker("ElementField.Simulate");

        [Header("Gameplay Data")]
        [SerializeField] private ElementFieldProfile _profile;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private ElementFieldSimulationStats _lastStats;
        [SerializeField] private int _bakedSolidCellCount;

        private ElementGrid _grid;
        private ElementWriteQueue _writeQueue;
        private ElementFieldSimulator _simulator;
        private ElementFieldSimulationSettings _settings;
        private ElementFieldTickAccumulator _tickAccumulator;
        private ElementFieldSolidBaker _solidBaker;
        private Vector3 _origin;
        private float _tickInterval;
        private float _nextCatchUpWarningTime;

        public static ElementFieldRuntime Active { get; private set; }

        public bool IsInitialized { get; private set; }
        public Vector3 Origin => _origin;
        public Vector3Int Dimensions => _grid != null ? _grid.Dimensions : Vector3Int.zero;
        public float CellSize => IsInitialized ? _settings.CellSize : 0f;
        public int ChunkSize => _grid != null ? _grid.ChunkSize : 0;
        public Vector3Int ChunkCounts => _grid != null ? _grid.ChunkCounts : Vector3Int.zero;
        public IElementFieldReadOnly ReadOnlyField => this;
        public ElementFieldSimulationStats LastStats => _lastStats;
        public int BakedSolidCellCount => _bakedSolidCellCount;
        public int PendingWriteCount => _writeQueue != null ? _writeQueue.Count : 0;

        private void Awake()
        {
            if (!TryInitialize())
                enabled = false;
        }

        private void OnEnable()
        {
            // 组件在初始化失败后由 Inspector 修复并重新启用时，再给它一次安全初始化机会。
            if (!IsInitialized && !TryInitialize())
            {
                enabled = false;
                return;
            }

            if (Active != null && Active != this)
            {
                GameLog.Error(
                    "Only one ElementFieldRuntime can be active in the P6-A vertical slice.",
                    "ElementField");
                enabled = false;
                return;
            }

            if (!ElementRuntimeRegistry.TryRegister(this))
            {
                GameLog.Error(
                    "Another Element Runtime already owns the scene write sink.",
                    "ElementField");
                enabled = false;
                return;
            }

            Active = this;
        }

        private void OnDisable()
        {
            ElementRuntimeRegistry.Unregister(this);
            if (Active == this)
                Active = null;
        }

        private void Update()
        {
            // 项目暂停菜单通过 Time.timeScale = 0 暂停 Gameplay；此时不能让环境场继续在后台流动。
            if (!IsInitialized || Time.timeScale <= 0f)
                return;

            TickForTests(Time.deltaTime);
            if (_tickAccumulator.DroppedDebtLastConsume
                && Time.unscaledTime >= _nextCatchUpWarningTime)
            {
                _nextCatchUpWarningTime = Time.unscaledTime + CatchUpWarningCooldown;
                GameLog.Warn(
                    "ElementField discarded excessive fixed-tick debt to avoid Spiral of Death.",
                    "ElementField");
            }
        }

        public bool TryEnqueueWrite(in ElementWriteRequest request)
        {
            return IsInitialized && _writeQueue.TryEnqueue(in request);
        }

        /// <summary>
        /// 使用显式 deltaTime 驱动累计器，是自动化测试和确定性调试入口；正常 Gameplay 由 Update 调用。
        /// 返回实际执行的 Tick 数，不把 Rendering Frame 数误当作 Simulation Tick 数。
        /// </summary>
        public int TickForTests(float deltaTime)
        {
            if (!IsInitialized)
                return 0;

            int ticks = _tickAccumulator.Consume(deltaTime);
            for (int i = 0; i < ticks; i++)
                SimulateOneTick();
            return ticks;
        }

        [ContextMenu("Step One Tick")]
        public void StepOnceForDebug()
        {
            if (IsInitialized)
                SimulateOneTick();
        }

        [ContextMenu("Clear Field")]
        public void ClearFieldForDebug()
        {
            if (!IsInitialized)
                return;

            _grid.ClearElementsAndCommitVersions();
            _lastStats = default;
        }

        [ContextMenu("Rebuild Solid Mask")]
        public void RebuildSolidMaskForDebug()
        {
            if (IsInitialized)
                _bakedSolidCellCount = _solidBaker.Bake(_grid, _origin, _settings.CellSize);
        }

        public ElementCell GetCell(int x, int y, int z)
        {
            EnsureInitialized();
            return _grid.GetCell(x, y, z);
        }

        public bool IsSolid(int x, int y, int z)
        {
            EnsureInitialized();
            return _grid.IsSolid(x, y, z);
        }

        public uint GetChunkVersion(int chunkX, int chunkY, int chunkZ)
        {
            EnsureInitialized();
            return _grid.GetChunkVersion(chunkX, chunkY, chunkZ);
        }

        private bool TryInitialize()
        {
            if (IsInitialized)
                return true;
            if (_profile == null)
            {
                GameLog.Error("ElementFieldRuntime requires an ElementFieldProfile.", "ElementField");
                return false;
            }
            if (!HasSupportedTransform())
            {
                GameLog.Error(
                    "ElementFieldRuntime currently requires identity world rotation and unit world scale.",
                    "ElementField");
                return false;
            }

            try
            {
                _settings = _profile.CreateSimulationSettings();
                _grid = new ElementGrid(
                    _profile.Dimensions,
                    _profile.MaximumCellCount,
                    _profile.ChunkSize);
                _writeQueue = new ElementWriteQueue(_profile.MaxPendingWrites);
                _simulator = new ElementFieldSimulator();
                _tickAccumulator = new ElementFieldTickAccumulator(
                    _profile.TickRate,
                    _profile.MaxCatchUpTicks);
                _tickInterval = 1f / _profile.TickRate;
                _origin = transform.position;
                _solidBaker = GetComponent<ElementFieldSolidBaker>();

                // Runtime 创建 Grid 后显式 Bake，避免依赖同一 GameObject 上两个 Awake 的默认执行先后。
                _bakedSolidCellCount = _solidBaker.Bake(_grid, _origin, _settings.CellSize);
                IsInitialized = true;
                return true;
            }
            catch (Exception exception)
            {
                GameLog.Error(
                    $"ElementFieldRuntime initialization failed: {exception.Message}",
                    "ElementField");
                return false;
            }
        }

        private void SimulateOneTick()
        {
            ElementFieldSimulationStats stats;
            using (ApplyWritesMarker.Auto())
            {
                stats = _simulator.ApplyWrites(
                    _grid,
                    _writeQueue,
                    _origin,
                    _settings.CellSize);
            }

            using (SimulateMarker.Auto())
            {
                _simulator.SimulateStages(
                    _grid,
                    _tickInterval,
                    in _settings,
                    ref stats);
            }

            _lastStats = stats;
        }

        private bool HasSupportedTransform()
        {
            bool unitScale = (transform.lossyScale - Vector3.one).sqrMagnitude
                <= TransformTolerance * TransformTolerance;
            bool identityRotation = Quaternion.Angle(transform.rotation, Quaternion.identity)
                <= TransformTolerance;
            return unitScale && identityRotation;
        }

        private void EnsureInitialized()
        {
            if (!IsInitialized)
                throw new InvalidOperationException("ElementFieldRuntime is not initialized.");
        }
    }
}
