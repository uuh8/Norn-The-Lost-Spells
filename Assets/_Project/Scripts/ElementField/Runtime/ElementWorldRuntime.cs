using Game.Materials;
using System;
using System.Collections.Generic;
using Game.Combat;
using Game.Core;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField
{
    /// <summary>
    /// P6-B 连续关卡的 Unity Adapter：固定 World Origin，管理 Interest Point、稀疏 Chunk 生命周期、
    /// 写入队列与固定 Tick Simulation。Projectile 只能通过 IElementWriteSink 入队，不能取得 Store 写权限。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ElementFieldSolidBaker))]
    public sealed partial class ElementWorldRuntime : MonoBehaviour, IElementWriteSink, IElementWorldReadOnly, IElementWorldChunkReadOnly, IMaterialAmountReadOnly, IMaterialQuantityReadOnly
    {
        private const float TransformTolerance = 0.0001f;

        private static readonly ProfilerMarker ApplyWritesMarker =
            new ProfilerMarker("ElementWorld.ApplyWrites");
        private static readonly ProfilerMarker SimulateMarker =
            new ProfilerMarker("ElementWorld.Simulate");
        private static readonly ProfilerMarker RebuildActivityMarker =
            new ProfilerMarker("ElementWorld.RebuildActivity");
        private static readonly ProfilerMarker BakeChunkSolidMarker =
            new ProfilerMarker("ElementWorld.BakeChunkSolid");
        private static readonly ProfilerMarker LiquidReactionMarker =
            new ProfilerMarker("ElementWorld.LiquidReaction");
        private static readonly ProfilerMarker GridFireReactionMarker =
            new ProfilerMarker("GridLiquid.FireReaction");
        private static readonly ProfilerMarker GridFireCollectMarker =
            new ProfilerMarker("GridLiquid.FireReaction.CollectFire");
        private static readonly ProfilerMarker GridFireContactsMarker =
            new ProfilerMarker("GridLiquid.FireReaction.ProcessContacts");

        [Header("Gameplay Data")]
        [SerializeField] private ElementWorldProfile _profile;

        [Header("GPU PBF Water (Optional)")]
        [Tooltip("仅当 Profile 选择 GpuPbf 时使用；缺失或未初始化会拒绝 Water，不会回写 Legacy Cell。")]
        [SerializeField] private GpuPbfFluidRuntime _gpuPbfFluidRuntime;
        [Tooltip("Task 10 的低频 Water Occupancy；只用于 Gameplay 反应，不参与即时 GPU Solver。")]
        [SerializeField] private FluidGameplayOccupancyBridge _fluidGameplayOccupancy;

        [Header("Fluid Chunk Streaming (Optional)")]
        [Tooltip("仅当 Profile 开启 Fluid Chunk Streaming 且有效后端为 GpuPbf 时接管流体写入。")]
        [SerializeField] private FluidChunkStreamingRuntime _fluidChunkStreamingRuntime;

        [Header("Grid Liquid (Optional)")]
        [SerializeField] private LiquidWorldRuntime _gridLiquidRuntime;

        [Header("Fixed Solids / Real Gases (Optional)")]
        [SerializeField] private FixedSolidWorldRuntime _fixedSolidRuntime;
        [SerializeField] private GasWorldRuntime _gasRuntime;

        [Header("Streaming Interest Point")]
        [Tooltip("只决定附近哪些 Chunk 工作，不会成为世界 Origin，也不会让 ElementWorldRoot 跟随玩家移动。")]
        [SerializeField] private Transform _interestPoint;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private Vector3Int _interestGlobalCell;
        [SerializeField] private Vector3Int _interestChunkKey;
        [SerializeField] private long _worldTick;
        [SerializeField] private int _residentChunkCount;
        [SerializeField] private int _activeChunkCount;
        [SerializeField] private int _activeElementCellCount;
        [SerializeField] private int _sleepingChunkCount;
        [SerializeField] private int _reclaimedChunkCountLastTick;
        [SerializeField] private int _bakedChunkCount;
        [SerializeField] private int _lastBakedSolidCellCount;
        [SerializeField] private ElementFieldSimulationStats _lastStats;
        [SerializeField] private int _lastGridFireChunks;
        [SerializeField] private int _lastGridFireSamples;

        private ElementWorldStore _store;
        private ElementWriteQueue _writeQueue;
        private ElementWorldSimulator _simulator;
        private ElementFieldSimulationSettings _settings;
        private ElementFieldTickAccumulator _tickAccumulator;
        private ElementFieldSolidBaker _solidBaker;
        private ElementWorldChunk[] _activeChunks;
        private ElementWorldChunk[] _gridFireChunks;
        private int _gridFireChunkCount;
        private int _gridFireCursorChunk;
        private int _gridFireCursorCell;
        private ElementChunkKey[] _reclaimKeys;
        private Vector3 _origin;
        private float _tickInterval;
        private ElementChunkKey _interestChunk;
        private bool _hasInterestChunk;
        private bool _fluidStreamingEnabled;
        private bool _hasGridLiquidRoute;
        private WaterSimulationMode _effectiveWaterSimulationMode;
        private MaterialSimulationRouteSnapshot _materialRoutes;
        private MaterialReactionCatalogSnapshot _materialReactions;
        private MaterialStatusProjectionSnapshot _materialStatusProjection;
        private MaterialStateQueryRouter _materialQuery;
        private IFluidDepositSink _fluidDepositSink;
        private ILiquidOccupancyReadOnly _liquidOccupancy;
        private ILiquidOccupancyReadOnly _liquidReactionOccupancy;
        private FluidReactionCommandQueue _fluidReactionQueue;
        private LiquidCellReactionSystem _liquidCellReactionSystem;
        private LiquidReactionAdapter _gridLiquidReactions;
        private LiquidCellFireSample[] _fireReactionSamples;
        private bool _hasReportedUnsupportedPbfFallback;
        private ulong _droppedDebtFrameCount;
        private ulong _processedWriteCount;
        private ulong _rejectedWriteCount;
        private ulong _reactionPairCount;
        private readonly Collider[] _reactionDamageColliders = new Collider[64];
        private readonly int[] _reactionDamageTargetIds = new int[64];
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private int _gcSampledWorldTicks, _gcAllocationWorldTicks;
        private long _gcWorldTickBytes, _gcMaximumWorldTickBytes;
#endif

        public bool IsInitialized { get; private set; }
        public Vector3 Origin => _origin;
        public float CellSize => IsInitialized ? _settings.CellSize : 0f;
        public int ChunkSize => IsInitialized ? _profile.ChunkSize : 0;
        public int MaximumResidentChunkCount => IsInitialized ? _profile.MaximumResidentChunks : 0;
        public long WorldTick => _worldTick;
        public ElementChunkKey InterestChunk => _interestChunk;
        public Vector3 InterestPosition=>_interestPoint!=null?_interestPoint.position:transform.position;
        public int ResidentChunkCount => _residentChunkCount;
        public int ActiveChunkCount => _activeChunkCount;
        public int SleepingChunkCount => _sleepingChunkCount;
        public int PendingWriteCount => _writeQueue != null ? _writeQueue.Count : 0;
        public ElementFieldSimulationStats LastStats => _lastStats;
        public WaterSimulationMode EffectiveWaterSimulationMode => _effectiveWaterSimulationMode;
        public IMaterialSimulationRouteReadOnly MaterialRoutes => _materialRoutes;
        public IMaterialAmountReadOnly MaterialAmounts => _materialQuery;
        public IFluidDormantReadOnly DormantLiquids => _fluidChunkStreamingRuntime;
        public ILiquidSurfaceReadOnly GridLiquids => _gridLiquidRuntime;
        public FixedSolidWorldRuntime FixedSolids => _fixedSolidRuntime;
        public GasWorldRuntime Gases => _gasRuntime;
        public IFixedSolidSurfaceReadOnly SolidSurfaces => _fixedSolidRuntime;
        public IGasVolumeReadOnly GasVolumes => _gasRuntime;
        public IMaterialQuantityReadOnly MaterialQuantities => this;
        public MaterialStatusProjectionSnapshot MaterialStatusProjection => _materialStatusProjection;
        public int LastFrameTickCount { get; private set; }
        public ulong DroppedDebtFrameCount => _droppedDebtFrameCount;
        public ulong ProcessedWriteCount => _processedWriteCount;
        public ulong RejectedWriteCount => _rejectedWriteCount;
        public ulong ReactionPairCount => _reactionPairCount;
        public int LastGridFireChunkCount => _lastGridFireChunks;
        public int LastGridFireSampleCount => _lastGridFireSamples;

        private void Awake()
        {
            if (!TryInitialize())
                enabled = false;
        }

        private void OnEnable()
        {
            if (!IsInitialized && !TryInitialize())
            {
                enabled = false;
                return;
            }

            if (!ElementRuntimeRegistry.TryRegister(this))
            {
                GameLog.Error(
                    "Another Element Runtime already owns the scene write sink.",
                    "ElementField");
                enabled = false;
            }
            EnableActivitySources();
        }

        private void OnDisable()
        {
            DisableActivitySources();
            ElementRuntimeRegistry.Unregister(this);
        }

        private void Update()
        {
            LastFrameTickCount = 0;
            if (!IsInitialized || Time.timeScale <= 0f)
                return;

            RefreshInterestChunk();

            RefreshWorldActivity();

            if (SimulationPausedForDebug || SimulationSuspended) return;

            int ticks = _tickAccumulator.Consume(Time.deltaTime);
            LastFrameTickCount = ticks;
            if (_tickAccumulator.DroppedDebtLastConsume)
                _droppedDebtFrameCount++;
            for (int i = 0; i < ticks; i++)
                SimulateOneTick();
        }

        /// <summary>
        /// 测试与 Debug 使用显式 deltaTime；正常 Gameplay 仍由 Update 驱动。
        /// </summary>
        public int TickForTests(float deltaTime)
        {
            LastFrameTickCount = 0;
            if (!IsInitialized)
                return 0;

            RefreshInterestChunk();
            RefreshWorldActivity();
            int ticks = _tickAccumulator.Consume(deltaTime);
            LastFrameTickCount = ticks;
            if (_tickAccumulator.DroppedDebtLastConsume)
                _droppedDebtFrameCount++;
            for (int i = 0; i < ticks; i++)
                SimulateOneTick();

            return ticks;
        }

        public bool TryEnqueueWrite(in ElementWriteRequest request)
        {
            return IsInitialized && RetainWriteActivity(in request) && ElementWorldWriteRouter.TryRoute(
                _materialRoutes,
                in request,
                _writeQueue,
                _fluidDepositSink,
                _gridLiquidRuntime, _fixedSolidRuntime, _gasRuntime);
        }

        /// <summary>
        /// 返回 Interest Box 内已经 Resident 的 Chunk，而不是只返回 Solver Active Chunk。
        /// 稳定水会进入 Solver-Sleeping；若 Rendering 误用 Active Snapshot，水一稳定就会消失。
        /// </summary>
        public int CopyVisibleChunkKeys(ElementChunkKey[] destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (!IsInitialized || !_hasInterestChunk || _store == null || destination.Length == 0)
                return 0;

            int count = 0;
            Dictionary<ElementChunkKey, ElementWorldChunk>.Enumerator enumerator =
                _store.Chunks.GetEnumerator();
            while (enumerator.MoveNext() && count < destination.Length)
            {
                KeyValuePair<ElementChunkKey, ElementWorldChunk> pair = enumerator.Current;
                ElementChunkKey key = pair.Key;
                // Presentation 没必要为只有 SolidMask、却没有 Water/Fire 的 Resident Chunk 分配 View。
                // 稳定水虽然 Solver-Sleeping，但 HasAnyElement 仍为 true，因此不会被这条规则误隐藏。
                if (!pair.Value.HasAnyElement)
                    continue;
                if (!IsVisibleChunk(key))
                {
                    continue;
                }

                destination[count++] = key;
            }
            enumerator.Dispose();

            // Dictionary 不保证枚举顺序。小规模 Buffer 使用原地 Insertion Sort，既不产生 GC，
            // 又让 View 分配、重建预算和测试结果在每次运行中保持确定性。
            for (int i = 1; i < count; i++)
            {
                ElementChunkKey value = destination[i];
                int previous = i - 1;
                while (previous >= 0 && CompareVisibleChunkKeys(destination[previous], value) > 0)
                {
                    destination[previous + 1] = destination[previous];
                    previous--;
                }

                destination[previous + 1] = value;
            }

            return count;
        }

        /// <summary>
        /// Global Cell 只读查询。不存在的稀疏 Chunk 等价于 Empty，读取不会隐式分配 Chunk。
        /// </summary>
        public bool TryGetCell(Vector3Int globalCell, out ElementCell cell)
        {
            if (!TryResolveResidentCell(globalCell, out ElementWorldChunk chunk, out Vector3Int localCell))
            {
                cell = default;
                return false;
            }

            cell = chunk.GetCell(localCell);
            return true;
        }

        public int CopyChunkCells(ElementChunkKey key, ElementCell[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!IsInitialized || _store == null || !_store.TryGetChunk(key, out ElementWorldChunk chunk)) return 0;
            if (destination.Length < chunk.CellCount)
                throw new ArgumentException("Chunk Snapshot Buffer 必须容纳整个 Chunk。", nameof(destination));
            // MonoBehaviour 的 World Tick 与 Presentation 读取都在 Main Thread 顺序执行；
            // 一次值拷贝消除逐格 Global→Chunk 换算和 Dictionary 查找，不跨帧缓存旧 Gameplay 状态。
            Array.Copy(chunk.CurrentCells, 0, destination, 0, chunk.CellCount);
            return chunk.CellCount;
        }

        public bool IsSolid(Vector3Int globalCell)
        {
            return TryResolveResidentCell(globalCell, out ElementWorldChunk chunk, out Vector3Int localCell)
                && chunk.Grid.IsSolid(localCell);
        }

        public uint GetChunkVersion(ElementChunkKey key)
        {
            // 每个 ElementWorldChunk 内部复用一个单 Chunk ElementGrid，所以局部版本坐标固定为 0,0,0。
            return IsInitialized && _store != null && _store.TryGetChunk(key, out ElementWorldChunk chunk)
                ? chunk.Grid.GetChunkVersion(0, 0, 0)
                : 0u;
        }

        /// <summary>
        /// Exposure/后续 Rendering 使用的只读 Interest Region 查询。稳定水所在的 Solver-Sleeping Chunk
        /// 仍必须可读，否则一旦水面稳定，角色 Wet 与视觉都会错误消失。Read 永远不会创建 Chunk。
        /// </summary>
        public bool TryGetActiveCell(Vector3Int globalCell, out ElementCell cell)
        {
            if (!IsInitialized || _store == null)
            {
                cell = default;
                return false;
            }

            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell,
                _profile.ChunkSize,
                out ElementChunkKey key,
                out Vector3Int localCell);
            if (!_hasInterestChunk
                || !IsGameplayChunk(key)
                || !_store.TryGetChunk(key, out ElementWorldChunk chunk))
            {
                cell = default;
                return false;
            }

            cell = chunk.GetCell(localCell);
            return true;
        }

        public bool TryGetAmount(Vector3Int globalCell, MaterialId material, out byte amount)
        {
            if (TryGetActiveCell(globalCell, out ElementCell cell)
                && cell.MaterialKind == material)
            {
                amount = cell.Amount;
                return true;
            }

            amount = 0;
            return false;
        }

        /// <summary>
        /// 当前 Interest Region 的连续 World AABB，仅用于集中 Physics Broadphase；
        /// 精确 Exposure 仍会逐 Global Cell 检查 Chunk 是否真的 Active。
        /// </summary>
        public Bounds GetActiveWorldBounds()
        {
            if (!IsInitialized || !_hasInterestChunk)
                return default;

            float chunkWorldSize = _profile.ChunkSize * _profile.CellSize;
            var minKey = new Vector3Int(
                _interestChunk.X - _profile.ActiveRadiusXZChunks,
                _interestChunk.Y - _profile.ActiveRadiusYChunks,
                _interestChunk.Z - _profile.ActiveRadiusXZChunks);
            var chunkCounts = new Vector3Int(
                _profile.ActiveRadiusXZChunks * 2 + 1,
                _profile.ActiveRadiusYChunks * 2 + 1,
                _profile.ActiveRadiusXZChunks * 2 + 1);
            Vector3 size = (Vector3)chunkCounts * chunkWorldSize;
            Vector3 min = _origin + (Vector3)minKey * chunkWorldSize;
            return new Bounds(min + size * 0.5f, size);
        }

        private bool TryInitialize()
        {
            if (IsInitialized)
                return true;
            if (_profile == null)
            {
                GameLog.Error("ElementWorldRuntime requires an ElementWorldProfile.", "ElementField");
                return false;
            }
            if (_profile.MaterialCatalog == null
                || _profile.MaterialSimulationRouting == null
                || _profile.MaterialReactionBindings == null
                || _profile.MaterialStatusProjection == null)
            {
                GameLog.Error(
                    "ElementWorldProfile requires Material Catalog, Material Simulation Routing and Material Reaction Binding assets.",
                    "ElementField");
                return false;
            }
            if (_interestPoint == null)
            {
                GameLog.Error("ElementWorldRuntime requires an Interest Point Transform.", "ElementField");
                return false;
            }
            if (!HasSupportedTransform())
            {
                GameLog.Error(
                    "ElementWorldRuntime requires identity world rotation and unit world scale.",
                    "ElementField");
                return false;
            }

            _solidBaker = GetComponent<ElementFieldSolidBaker>();
            if (_solidBaker == null)
            {
                GameLog.Error(
                    "ElementWorldRuntime requires ElementFieldSolidBaker so Collider geometry can block Cell flow.",
                    "ElementField");
                return false;
            }

            try
            {
                _origin = transform.position;
                MaterialCatalogSnapshot catalog = _profile.MaterialCatalog.CreateSnapshot();
                MaterialReactionCatalogSnapshot reactions =
                    _profile.MaterialReactionBindings.CreateSnapshot(catalog);
                MaterialStatusProjectionSnapshot statusProjection =
                    _profile.MaterialStatusProjection.CreateSnapshot(catalog);
                bool supportsPbf = SystemInfo.supportsComputeShaders
                    && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
                WaterSimulationCompatibilityDecision waterDecision =
                    WaterSimulationCompatibilityPolicy.Resolve(_profile.WaterSimulationMode, supportsPbf);
                ReportHardwareFallbackOnce(in waterDecision);

                if (waterDecision.WaterBackend == MaterialSimulationBackendKind.GridLiquid
                    && (_gridLiquidRuntime == null
                        || !_gridLiquidRuntime.TryInitialize(_origin, _profile.CellSize,
                            _profile.ChunkSize, _solidBaker)))
                    throw new InvalidOperationException("GridLiquid Route 缺少已配置的 LiquidWorldRuntime。");

                // 硬件支持时，PBF Component/Compute/Profile 错误属于配置失败，不能静默写回 Cell。
                if (waterDecision.WaterBackend == MaterialSimulationBackendKind.GpuPbfLiquid
                    && (_gpuPbfFluidRuntime == null
                        || !_gpuPbfFluidRuntime.TryEnsureInitializedForWorld()))
                {
                    throw new InvalidOperationException(
                        "Water Route requests GpuPbfLiquid, but GpuPbfFluidRuntime is missing or failed initialization.");
                }

                MaterialSimulationRouteSnapshot finalRoutes = MaterialSimulationRouteSnapshot.Create(
                    catalog,
                    _profile.MaterialSimulationRouting,
                    in waterDecision);
                InitializeMaterialPhases(finalRoutes);
                // Poison/Sticky 也可以单独选择 Grid；一次初始化只能指向同一个数量权威。
                bool hasGridRoute = false;
                for (int materialId = 1; materialId <= byte.MaxValue; materialId++)
                    if (finalRoutes.TryResolve((MaterialId)materialId, out var backend)
                        && backend == MaterialSimulationBackendKind.GridLiquid)
                    {
                        hasGridRoute = true;
                        break;
                    }
                if (hasGridRoute && (_gridLiquidRuntime == null
                    || !_gridLiquidRuntime.TryInitialize(_origin, _profile.CellSize,
                        _profile.ChunkSize, _solidBaker)))
                    throw new InvalidOperationException("GridLiquid Route 缺少已配置的 LiquidWorldRuntime。");
                if (hasGridRoute)
                    for (int materialId = 1; materialId <= byte.MaxValue; materialId++)
                        if (finalRoutes.TryResolve((MaterialId)materialId, out var backend)
                            && backend == MaterialSimulationBackendKind.GridLiquid
                            && !_gridLiquidRuntime.Supports((MaterialId)materialId))
                            throw new InvalidOperationException(
                                $"GridLiquid 配置缺少材料 {(MaterialId)materialId} 的行为 Profile。");
                _hasGridLiquidRoute = hasGridRoute;
                _effectiveWaterSimulationMode = waterDecision.EffectiveMode;
                _settings = _profile.CreateSimulationSettings();
                if (_settings.SimulateCellWater
                    != (_effectiveWaterSimulationMode == WaterSimulationMode.LegacyCell))
                {
                    // Unsupported Graphics 的回退是初始化期的 effective mode 决策；Profile 本身不被改写。
                    _settings = new ElementFieldSimulationSettings(
                        _settings.CellSize,
                        _settings.MaxDownFlowPerTick,
                        _settings.MaxLateralFlowPerTick,
                        _settings.FireDecayPerTick,
                        _settings.ChunkSize,
                        _effectiveWaterSimulationMode == WaterSimulationMode.LegacyCell,
                        _settings.Extinguish,_settings.MaxFireDownFlowPerTick);
                }

                FluidChunkStreamingSettings streamingSettings =
                    _profile.CreateFluidChunkStreamingSettings();
                _fluidStreamingEnabled = streamingSettings.Enabled
                    && _effectiveWaterSimulationMode == WaterSimulationMode.GpuPbf;
                if (_fluidStreamingEnabled)
                {
                    if (_fluidChunkStreamingRuntime == null
                        || !_fluidChunkStreamingRuntime.TryInitialize(
                            in streamingSettings,
                            _gpuPbfFluidRuntime,
                            _origin,
                            _settings.CellSize,
                            _profile.ChunkSize,
                            _profile.ActiveRadiusXZChunks,
                            _profile.ActiveRadiusYChunks,
                            _fluidGameplayOccupancy))
                    {
                        throw new InvalidOperationException(
                            "Fluid Chunk Streaming is enabled, but FluidChunkStreamingRuntime is missing or failed initialization.");
                    }
                    // 所有液体写入先经过 Streaming Authority，才能在 Archive 窗口保持单写者语义。
                    _fluidDepositSink = _fluidChunkStreamingRuntime;
                }
                else
                {
                    _fluidDepositSink = _gpuPbfFluidRuntime;
                }
                _store = new ElementWorldStore(
                    _profile.ChunkSize,
                    _profile.MaximumResidentChunks,
                    InitializeWorldChunk);
                _writeQueue = new ElementWriteQueue(_profile.MaxPendingWrites);
                if (_effectiveWaterSimulationMode == WaterSimulationMode.GpuPbf
                    && _fluidGameplayOccupancy != null
                    && _gpuPbfFluidRuntime != null)
                {
                    // Queue/Planner/Scratch 都在初始化边界创建；后续 10Hz Tick 只覆盖已有数组。
                    _liquidOccupancy = _fluidGameplayOccupancy;
                    _liquidReactionOccupancy = _fluidGameplayOccupancy;
                    _fluidReactionQueue = new FluidReactionCommandQueue(_profile.MaxPendingWrites);
            _liquidCellReactionSystem = new LiquidCellReactionSystem(
                        _profile.MaxPendingWrites,
                        reactions);
                    _fireReactionSamples = new LiquidCellFireSample[_profile.MaxPendingWrites];
                    if (!_gpuPbfFluidRuntime.TryAttachReactionQueue(
                            _fluidReactionQueue,
                            _origin,
                            _settings.CellSize))
                    {
                        _liquidOccupancy = null;
                        _liquidReactionOccupancy = null;
                        _fluidReactionQueue = null;
            _liquidCellReactionSystem = null;
                        _fireReactionSamples = null;
                    }
                }
                if (_fluidStreamingEnabled)
                    _liquidOccupancy = _fluidChunkStreamingRuntime;
                _simulator = new ElementWorldSimulator(
                    _profile.MaximumResidentChunks,
                    reactions,
                    _profile.SettleAfterUnchangedTicks);
                _tickAccumulator = new ElementFieldTickAccumulator(
                    _profile.TickRate,
                    _profile.MaxCatchUpTicks);
                _tickInterval = 1f / _profile.TickRate;
                if (_hasGridLiquidRoute)
                {
                    var gridTuning = new ElementReactionTuningSnapshot(
                        _settings.Extinguish, default, _profile.ToxicCombustion,
                        _profile.IgniteGoo, _profile.AbsorbWater, 0f, 0f);
                    _gridLiquidReactions = new LiquidReactionAdapter(
                        _gridLiquidRuntime, this, reactions, in gridTuning,
                        _profile.MaxPendingWrites,
                        TryApplyGridFireDelta, TryApplyGridFireAdd, ApplyWorldReactionDamage);
                    if (_fireReactionSamples == null)
                        _fireReactionSamples = new LiquidCellFireSample[_profile.MaxPendingWrites];
                }

                // 两个工作数组按 Resident 上限一次性分配；后续 Tick 只覆盖内容，不 new List/Array。
                _activeChunks = new ElementWorldChunk[_profile.MaximumResidentChunks];
                if (_hasGridLiquidRoute)
                    _gridFireChunks = new ElementWorldChunk[_profile.MaximumResidentChunks];
                _reclaimKeys = new ElementChunkKey[_profile.MaximumResidentChunks];
                // 只有 Catalog、Compatibility、PBF 配置和其余 Runtime 资源全部成功后才发布最终路由。
                _materialRoutes = finalRoutes;
                _materialReactions = reactions;
                _materialStatusProjection = statusProjection;
                _materialQuery = new MaterialStateQueryRouter(finalRoutes, this,
                    _liquidOccupancy, _gridLiquidRuntime, _fixedSolidRuntime, _gasRuntime);
                InitializeMaterialConversions();
                IsInitialized = true;

                RefreshInterestChunk();
                RebuildActivitySets();
                return true;
            }
            catch (Exception exception)
            {
                GameLog.Error(
                    $"ElementWorldRuntime initialization failed: {exception.Message}",
                    "ElementField");
                return false;
            }
        }

        private void ReportHardwareFallbackOnce(in WaterSimulationCompatibilityDecision decision)
        {
            if (decision.UsedUnsupportedHardwareFallback && !_hasReportedUnsupportedPbfFallback)
            {
                _hasReportedUnsupportedPbfFallback = true;
                GameLog.Warn(
                    "当前 Graphics Device 不支持 ComputeShader；Water 已在本次 Play 初始化回退为 Legacy Cell。",
                    "ElementField");
            }
        }

        private void InitializeWorldChunk(ElementWorldChunk chunk)
        {
            // Store 首次创建 Chunk 时同步 Bake，保证本 Tick 后续 Deposit/Flow 立刻看到正确 SolidMask。
            // Physics.CheckBox 的成本只在稀疏 Chunk 分配边界发生，不会污染常规 10 Hz Solver 热路径。
            using (BakeChunkSolidMarker.Auto())
            {
                _lastBakedSolidCellCount = _solidBaker.BakeWorldChunk(
                    chunk,
                    _origin,
                    _settings.CellSize);
            }
            _bakedChunkCount++;
        }

        private void SimulateOneTick()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 包住写入、Grid Tick、材料反应及旧 Cell 模拟；与 Grid 自己的计量做差，
            // 可判断全局 GC 是否发生在液体路由上游，而不靠一次帧级曲线猜测。
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
#endif
            _worldTick++;

            var writeStats = new ElementFieldSimulationStats
            {
                RejectedWrites = _writeQueue.TakeRejectedEnqueueCount(),
            };
            using (ApplyWritesMarker.Auto())
            {
                while (_writeQueue.TryDequeue(out ElementWriteRequest request))
                {
                    writeStats.ProcessedWrites++;
                    if (!ElementWorldWriteProcessor.TryApply(
                            _store,
                            in request,
                            _origin,
                            _settings.CellSize,
                            _worldTick,
                            out _))
                    {
                        writeStats.RejectedWrites++;
                    }
                }
            }

            // 新写入会创建/续租 Chunk，因此必须在模拟前重建一次 Active Snapshot。
            RebuildActivitySets();
            // 固体只处理待写／拓扑变化，气体与液体共享固定时钟和活动范围。
            _conversionScheduler?.BeginTick(_tickInterval,LiquidActivityDomain());
            _fixedSolidRuntime?.Step();
            _fixedSolidRuntime?.StepGravity(_tickInterval);
            if (_gasRuntime != null && _gasRuntime.IsMaterialInitialized)
            { _gasRuntime.SetActivityDomain(LiquidActivityDomain()); _gasRuntime.Step(_tickInterval); }
            if (_hasGridLiquidRoute)
            {
                // 沿用Gameplay活动域；镜头外的角色／投射物／反应仍可唤醒液体。
                _gridLiquidRuntime.SetActivityDomain(LiquidActivityDomain());
                _gridLiquidRuntime.Step(_tickInterval);
                using (GridFireReactionMarker.Auto())
                {
                    int gridFireCount;
                    using (GridFireCollectMarker.Auto())
                    {
                        CollectAndSortGridFireChunks();
                        gridFireCount = CollectGridFireReactionSamples();
                    }
                    _lastGridFireChunks = _gridFireChunkCount;
                    _lastGridFireSamples = gridFireCount;
                    _conversionScheduler?.ObserveFire(_fireReactionSamples,gridFireCount);
                    using (GridFireContactsMarker.Auto())
                        _gridLiquidReactions.Process(_fireReactionSamples, gridFireCount, _tickInterval);
                }
            }
            PlanAndApplyGpuWaterFireReactions();
            ElementFieldSimulationStats simulationStats;
            using (SimulateMarker.Auto())
            {
                simulationStats = _simulator.SimulateActiveChunks(
                    _store,
                    _activeChunks,
                    _activeChunkCount,
                    _worldTick,
                    _tickInterval,
                    in _settings);
            }
            simulationStats.ProcessedWrites = writeStats.ProcessedWrites;
            _conversionScheduler?.Step(_tickInterval);
            simulationStats.RejectedWrites = writeStats.RejectedWrites;
            _lastStats = simulationStats;
            _processedWriteCount += (uint)simulationStats.ProcessedWrites;
            _rejectedWriteCount += (uint)simulationStats.RejectedWrites;
            _reactionPairCount += (uint)simulationStats.ReactionPairs;

            // Solver 可能按需创建边界接收 Chunk；再次分类使 Inspector 与下一 Tick 立即看到真实生命周期。
            RebuildActivitySets();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long allocated = GC.GetAllocatedBytesForCurrentThread() - gcBefore;
            _gcSampledWorldTicks++;
            if (allocated > 0)
            {
                _gcAllocationWorldTicks++;
                _gcWorldTickBytes += allocated;
                if (allocated > _gcMaximumWorldTickBytes)
                    _gcMaximumWorldTickBytes = allocated;
            }
#endif
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_gcSampledWorldTicks > 0)
                GameLog.Info($"Element World: GC {_gcAllocationWorldTicks}/{_gcSampledWorldTicks} ticks, "
                    + $"total {_gcWorldTickBytes} B, max {_gcMaximumWorldTickBytes} B/tick.",
                    "ElementField");
#endif
        }

        private void PlanAndApplyGpuWaterFireReactions()
        {
            using var marker = LiquidReactionMarker.Auto();
            if (_effectiveWaterSimulationMode != WaterSimulationMode.GpuPbf
                || _liquidReactionOccupancy == null
                || !_liquidReactionOccupancy.HasValidSnapshot
                || _fluidReactionQueue == null
                || _liquidCellReactionSystem == null)
            {
                return;
            }

            SortActiveChunksForReaction();
            int fireCount = CollectFireReactionSamples();
            if (_fluidChunkStreamingRuntime != null)
            {
                // 接触唤醒服从当前材料反应数据；水和黏液任何一侧归档时都需覆盖。
                // 这里只恢复表示，后面的 GPU-only 规划仍是实际反应命令的唯一入口。
                if (_materialReactions.TryResolve(MaterialId.Water, MaterialId.Sticky,
                        out ElementReactionId liquidReaction)
                    && liquidReaction == ElementReactionId.AbsorbWater)
                {
                    _fluidChunkStreamingRuntime.RequestLiquidReactionContactWakes(
                        MaterialId.Water, MaterialId.Sticky);
                    _fluidChunkStreamingRuntime.RequestLiquidReactionContactWakes(
                        MaterialId.Sticky, MaterialId.Water);
                }
                for (int i = 0; i < fireCount; i++)
                {
                    Vector3Int cell = _fireReactionSamples[i].GlobalCell;
                    // Reaction 使用同格与六轴邻格；先请求相关 Dormant Chunk 恢复，随后仍由 GPU-only
                    // Snapshot 规划 Consume/Convert，避免命令打向尚未存在的粒子。
                    _fluidChunkStreamingRuntime.RequestWake(cell, FluidDormantWakeReason.Reaction);
                    _fluidChunkStreamingRuntime.RequestWake(cell + Vector3Int.right, FluidDormantWakeReason.Reaction);
                    _fluidChunkStreamingRuntime.RequestWake(cell + Vector3Int.left, FluidDormantWakeReason.Reaction);
                    _fluidChunkStreamingRuntime.RequestWake(cell + Vector3Int.up, FluidDormantWakeReason.Reaction);
                    _fluidChunkStreamingRuntime.RequestWake(cell + Vector3Int.down, FluidDormantWakeReason.Reaction);
                    _fluidChunkStreamingRuntime.RequestWake(cell + Vector3Int.forward, FluidDormantWakeReason.Reaction);
                    _fluidChunkStreamingRuntime.RequestWake(cell + Vector3Int.back, FluidDormantWakeReason.Reaction);
                }
            }
            ToxicCombustionTuning toxicTuning = _profile.ToxicCombustion;
            IgniteGooTuning igniteGooTuning = _profile.IgniteGoo;
            AbsorbWaterTuning absorbWaterTuning = _profile.AbsorbWater;
            _liquidCellReactionSystem.TryPlanAndCommit(
                _liquidReactionOccupancy,
                _fireReactionSamples,
                fireCount,
                Mathf.Max(_tickInterval, _fluidGameplayOccupancy.SnapshotIntervalSeconds),
                in _settings.Extinguish,
                in toxicTuning,
                in igniteGooTuning,
                in absorbWaterTuning,
                _origin,
                _settings.CellSize,
                _fluidReactionQueue);

            while (_fluidReactionQueue.TryDequeueFireDelta(out ElementCellDeltaRequest delta))
                TryApplyInternalFireDelta(in delta);
            while (_fluidReactionQueue.TryDequeueElementAdd(out ElementCellAddRequest add))
                TryApplyInternalElementAdd(in add);
            while (_fluidReactionQueue.TryDequeueDamage(out WorldReactionDamageCommand damage))
                ApplyWorldReactionDamage(in damage);
        }

        private void ApplyWorldReactionDamage(in WorldReactionDamageCommand command)
        {
            if (command.Amount <= 0f || command.Radius <= 0f) return;

            // 世界反应没有角色 TargetId，但仍发布同一条事实事件；一次 Command 只发布一次，
            // 不能放入命中循环，否则爆炸范围内每个 Collider 都会重复播放一份 VFX。
            EventBus<ElementReactionEvent>.Publish(new ElementReactionEvent
            {
                TargetId = 0,
                Reaction = command.Reaction,
                Phase = ElementReactionPhase.Resolved,
                WorldPosition = command.WorldPosition,
                NormalizedStrength = 1f,
                ExpectedDuration = 0f,
            });

            int count = Physics.OverlapSphereNonAlloc(
                command.WorldPosition,
                command.Radius,
                _reactionDamageColliders,
                ~0,
                QueryTriggerInteraction.Ignore);
            int unique = 0;
            for (int i = 0; i < count; i++)
            {
                Collider collider = _reactionDamageColliders[i];
                _reactionDamageColliders[i] = null;
                IDamageable target = collider != null ? collider.GetComponentInParent<IDamageable>() : null;
                UnityEngine.Object targetObject = target as UnityEngine.Object;
                if (target == null || !target.IsAlive || targetObject == null) continue;
                int id = targetObject.GetInstanceID();
                bool duplicate = false;
                for (int seen = 0; seen < unique; seen++) duplicate |= _reactionDamageTargetIds[seen] == id;
                if (duplicate) continue;
                _reactionDamageTargetIds[unique++] = id;
                Vector3 direction = ((Vector3)collider.bounds.center - command.WorldPosition).normalized;
                var request = new DamageRequest(
                    gameObject.GetInstanceID(), byte.MaxValue, command.Amount, DamageType.Magical,
                    collider.ClosestPoint(command.WorldPosition), direction, false);
                target.ReceiveHit(in request);
            }
            for (int i = 0; i < unique; i++) _reactionDamageTargetIds[i] = 0;
        }

        private int CollectFireReactionSamples()
        {
            int count = 0;
            int size = _profile.ChunkSize;
            for (int chunkIndex = 0; chunkIndex < _activeChunkCount; chunkIndex++)
            {
                ElementWorldChunk chunk = _activeChunks[chunkIndex];
                for (int z = 0; z < size && count < _fireReactionSamples.Length; z++)
                for (int y = 0; y < size && count < _fireReactionSamples.Length; y++)
                for (int x = 0; x < size && count < _fireReactionSamples.Length; x++)
                {
                    var local = new Vector3Int(x, y, z);
                    ElementCell cell = chunk.GetCell(local);
                    if (cell.MaterialKind != MaterialId.Fire || cell.Amount == 0)
                        continue;
                    _fireReactionSamples[count++] = new LiquidCellFireSample(
                        ElementWorldCoordinates.ComposeGlobalCell(chunk.Key, local, size),
                        cell.Amount);
                }
            }
            return count;
        }

        private void CollectAndSortGridFireChunks()
        {
            _gridFireChunkCount = 0;
            // Grid 只需要含 Fire 的 Chunk；对整个活动世界做插入排序会随湖泊规模白白增长。
            for (int i = 0; i < _activeChunkCount; i++)
            {
                ElementWorldChunk chunk = _activeChunks[i];
                if (!chunk.HasFire) continue;
                int insert = _gridFireChunkCount++;
                while (insert > 0
                    && CompareReactionKeys(_gridFireChunks[insert - 1].Key, chunk.Key) > 0)
                {
                    _gridFireChunks[insert] = _gridFireChunks[insert - 1];
                    insert--;
                }
                _gridFireChunks[insert] = chunk;
            }
        }

        private int CollectGridFireReactionSamples()
        {
            if (_gridFireChunkCount == 0)
            {
                _gridFireCursorChunk = 0;
                _gridFireCursorCell = 0;
                return 0;
            }

            int count = 0;
            int size = _profile.ChunkSize;
            int cellsPerChunk = size * size * size;
            int startChunk = _gridFireCursorChunk % _gridFireChunkCount;
            for (int visited = 0; visited < _gridFireChunkCount; visited++)
            {
                int chunkIndex = (startChunk + visited) % _gridFireChunkCount;
                ElementWorldChunk chunk = _gridFireChunks[chunkIndex];
                ElementCell[] current = chunk.CurrentCells;
                int firstCell = visited == 0 ? _gridFireCursorCell : 0;
                for (int index = firstCell; index < cellsPerChunk; index++)
                {
                    // 循环本身已提供合法线性索引。空格只读同一Current Buffer，
                    // 确认是Fire后才还原三维坐标，省掉稀疏Chunk的除法/ToIndex往返。
                    // 每次采集重新取数组引用，不能跨Tick保留Double Buffer交换前的旧数组。
                    ElementCell cell = current[index];
                    if (cell.MaterialKind != MaterialId.Fire || cell.Amount == 0) continue;
                    int x = index % size;
                    int y = index / size % size;
                    int z = index / (size * size);
                    var local = new Vector3Int(x, y, z);
                    _fireReactionSamples[count++] = new LiquidCellFireSample(
                        ElementWorldCoordinates.ComposeGlobalCell(chunk.Key, local, size),
                        cell.Amount);
                    if (count < _fireReactionSamples.Length) continue;

                    // 超过固定采样容量时保存下一格；下一 Tick 继续走，而不是让前 256 格独占反应。
                    _gridFireCursorChunk = index + 1 < cellsPerChunk
                        ? chunkIndex : (chunkIndex + 1) % _gridFireChunkCount;
                    _gridFireCursorCell = index + 1 < cellsPerChunk ? index + 1 : 0;
                    return count;
                }
            }
            _gridFireCursorChunk = 0;
            _gridFireCursorCell = 0;
            return count;
        }

        private bool TryApplyInternalFireDelta(in ElementCellDeltaRequest request)
        {
            if (request.ExpectedMaterial != MaterialId.Fire
                || request.AmountToRemove == 0
                || !TryResolveResidentCell(request.GlobalCell, out ElementWorldChunk chunk, out Vector3Int local))
            {
                return false;
            }

            ElementCell current = chunk.GetCell(local);
            if (current.MaterialKind != MaterialId.Fire || current.Amount == 0)
                return false;

            int remaining = Mathf.Max(0, current.Amount - request.AmountToRemove);
            chunk.SetCell(
                local,
                remaining == 0
                    ? default
                    : new ElementCell(MaterialId.Fire, (byte)remaining));
            chunk.Grid.MarkCellDirty(local);
            chunk.WakeForSimulation(_worldTick);
            return true;
        }

        private bool TryApplyInternalElementAdd(in ElementCellAddRequest request)
        {
            if (request.Material == MaterialId.Empty || request.AmountToAdd == 0
                || !TryResolveResidentCell(request.GlobalCell, out ElementWorldChunk chunk, out Vector3Int local))
                return false;

            ElementCell current = chunk.GetCell(local);
            // 反应产物不能覆盖另一种 CPU 材质；当前唯一产物是 Fire，空 Cell 或已有 Fire 才能累加。
            if (!current.IsEmpty && current.MaterialKind != request.Material)
                return false;
            int amount = Mathf.Min(byte.MaxValue, current.Amount + request.AmountToAdd);
            chunk.SetCell(local, new ElementCell(request.Material, (byte)amount));
            chunk.Grid.MarkCellDirty(local);
            chunk.WakeForSimulation(_worldTick);
            return true;
        }

        private bool TryApplyGridFireAdd(in ElementCellAddRequest request)
        {
            if (request.Material != MaterialId.Fire || request.AmountToAdd == 0 || _store == null)
                return false;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(request.GlobalCell,
                _profile.ChunkSize, out ElementChunkKey key, out Vector3Int local);
            if (!_store.TryGetOrCreateChunk(key, out ElementWorldChunk chunk)
                || chunk.Grid.IsSolid(local)) return false;
            ElementCell current = chunk.GetCell(local);
            if (!current.IsEmpty && current.MaterialKind != MaterialId.Fire) return false;
            if (current.Amount + request.AmountToAdd > byte.MaxValue) return false;
            int amount = current.Amount + request.AmountToAdd;
            chunk.SetCell(local, new ElementCell(MaterialId.Fire, (byte)amount));
            chunk.Grid.MarkCellDirty(local);
            chunk.WakeForSimulation(_worldTick);
            return true;
        }

        private bool TryApplyGridFireDelta(in ElementCellDeltaRequest request)
        {
            if (request.ExpectedMaterial != MaterialId.Fire
                || !TryResolveResidentCell(request.GlobalCell, out ElementWorldChunk chunk,
                    out Vector3Int local)) return false;
            ElementCell current = chunk.GetCell(local);
            // Grid 反应先预留完整数量，再执行副作用；不足时整笔拒绝，不能部分灭火却扣足水。
            return current.MaterialKind == MaterialId.Fire
                && current.Amount >= request.AmountToRemove
                && TryApplyInternalFireDelta(in request);
        }

        private void SortActiveChunksForReaction()
        {
            for (int index = 1; index < _activeChunkCount; index++)
            {
                ElementWorldChunk value = _activeChunks[index];
                int insert = index - 1;
                while (insert >= 0 && CompareReactionKeys(_activeChunks[insert].Key, value.Key) > 0)
                {
                    _activeChunks[insert + 1] = _activeChunks[insert];
                    insert--;
                }
                _activeChunks[insert + 1] = value;
            }
        }

        private static int CompareReactionKeys(ElementChunkKey left, ElementChunkKey right)
        {
            int z = left.Z.CompareTo(right.Z);
            if (z != 0)
                return z;
            int y = left.Y.CompareTo(right.Y);
            return y != 0 ? y : left.X.CompareTo(right.X);
        }

        private void RefreshInterestChunk()
        {
            // 死亡或Single Scene卸载可先销毁玩家。保留最后的Interest坐标，世界仍能结算在途材料，不能读假null Transform。
            if(_interestPoint==null)return;
            _interestGlobalCell = ElementWorldCoordinates.WorldToGlobalCell(
                _interestPoint.position,
                _origin,
                _profile.CellSize);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                _interestGlobalCell,
                _profile.ChunkSize,
                out ElementChunkKey nextInterestChunk,
                out _);

            if (_hasInterestChunk && nextInterestChunk == _interestChunk)
                return;

            _interestChunk = nextInterestChunk;
            _hasInterestChunk = true;
            _interestChunkKey = new Vector3Int(
                _interestChunk.X,
                _interestChunk.Y,
                _interestChunk.Z);

            if (_fluidStreamingEnabled)
                _fluidChunkStreamingRuntime.SetInterestChunk(_interestChunk);

            // 跨 Chunk 时立即刷新，不必等待下一个 Simulation Tick 才更新调度窗口。
            RebuildActivitySets();
        }

        private void RebuildActivitySets()
        {
            if (!_hasInterestChunk || _store == null)
                return;

            using var marker = RebuildActivityMarker.Auto();
            _activeChunkCount = 0;
            _activeElementCellCount = 0;
            _sleepingChunkCount = 0;
            _reclaimedChunkCountLastTick = 0;
            int reclaimCount = 0;

            Dictionary<ElementChunkKey, ElementWorldChunk>.Enumerator enumerator =
                _store.Chunks.GetEnumerator();
            while (enumerator.MoveNext())
            {
                KeyValuePair<ElementChunkKey, ElementWorldChunk> pair = enumerator.Current;
                ElementWorldChunk chunk = pair.Value;
                bool insidePlayerRegion = IsGameplayChunk(pair.Key);
                // 未结束的 Fire 是世界自身的活动需求，离屏后仍推进到反应/衰减结束。
                bool reactionFront = ActivityExpansionEnabled && chunk.HasFire;
                ElementChunkActivityState state = ElementChunkActivityPlanner.Evaluate(
                    pair.Key,
                    insidePlayerRegion || reactionFront ? pair.Key : _interestChunk,
                    chunk.HasAnyElement,
                    chunk.RequiresSimulation,
                    chunk.LastRelevantTick,
                    _worldTick,
                    _profile.SleepGraceTicks,
                    _profile.ActiveRadiusXZChunks,
                    _profile.ActiveRadiusYChunks);

                chunk.ActivityState = state;
                if (state == ElementChunkActivityState.Active)
                {
                    // 只有玩家兴趣范围或真实 Gameplay 事件可以续租。若 Grace Lease 自己每 Tick 都刷新时间，
                    // 远处 Chunk 会永远 Active，Sleep 将永远无法发生。
                    if (insidePlayerRegion)
                        chunk.MarkRelevant(_worldTick);
                    _activeChunks[_activeChunkCount++] = chunk;
                    _activeElementCellCount += chunk.NonEmptyCellCount;
                }
                else if (state == ElementChunkActivityState.Sleeping)
                {
                    _sleepingChunkCount++;
                }
                else
                {
                    _reclaimKeys[reclaimCount++] = pair.Key;
                }
            }
            enumerator.Dispose();

            // Dictionary 枚举过程中不能 Remove；先把 Key 写入预分配 Buffer，枚举结束后再统一回收。
            for (int i = 0; i < reclaimCount; i++)
            {
                if (_store.TryRemoveEmptyChunk(_reclaimKeys[i]))
                    _reclaimedChunkCountLastTick++;
            }

            _residentChunkCount = _store.ResidentChunkCount;
        }

        private bool HasSupportedTransform()
        {
            bool unitScale = (transform.lossyScale - Vector3.one).sqrMagnitude
                <= TransformTolerance * TransformTolerance;
            bool identityRotation = Quaternion.Angle(transform.rotation, Quaternion.identity)
                <= TransformTolerance;
            return unitScale && identityRotation;
        }

        private bool TryResolveResidentCell(
            Vector3Int globalCell,
            out ElementWorldChunk chunk,
            out Vector3Int localCell)
        {
            if (!IsInitialized || _store == null)
            {
                chunk = null;
                localCell = default;
                return false;
            }

            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell,
                _profile.ChunkSize,
                out ElementChunkKey key,
                out localCell);
            return _store.TryGetChunk(key, out chunk);
        }

        private int CompareVisibleChunkKeys(ElementChunkKey left, ElementChunkKey right)
        {
            // View Cap 小于可见元素 Chunk 数时优先保留离 Interest Point 更近的 Chunk。
            // Manhattan Distance 只做整数加法，不需要 sqrt，且在当前 Axis-Aligned Interest Box 中稳定。
            long leftDistance = Math.Abs((long)left.X - _interestChunk.X)
                + Math.Abs((long)left.Y - _interestChunk.Y)
                + Math.Abs((long)left.Z - _interestChunk.Z);
            long rightDistance = Math.Abs((long)right.X - _interestChunk.X)
                + Math.Abs((long)right.Y - _interestChunk.Y)
                + Math.Abs((long)right.Z - _interestChunk.Z);
            int distance = leftDistance.CompareTo(rightDistance);
            if (distance != 0)
                return distance;

            int z = left.Z.CompareTo(right.Z);
            if (z != 0)
                return z;
            int y = left.Y.CompareTo(right.Y);
            return y != 0 ? y : left.X.CompareTo(right.X);
        }
    }
}
