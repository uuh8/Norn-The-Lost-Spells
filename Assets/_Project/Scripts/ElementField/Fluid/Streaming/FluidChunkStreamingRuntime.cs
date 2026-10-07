using System;
using Game.Core;
using Game.Materials;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    public enum FluidArchiveRollbackReason : byte
    {
        None,
        LeaseUnavailable,
        ReadbackError,
        LayoutMismatch,
        FilteringFailed,
        RebuildFailed,
        EmptyAfterFiltering,
        StoreRejected,
        Unknown,
    }

    /// <summary>
    /// 流体 Chunk Streaming 的 Unity 生命周期边界。GPU 归档期间仍是权威；只有 Readback 完整、
    /// RAM Store 原子提交成功后才释放 slot。回调只登记结果，世界权威变更统一在 Update 执行。
    /// </summary>
    [DefaultExecutionOrder(50)]
    [DisallowMultipleComponent]
    public sealed partial class FluidChunkStreamingRuntime : MonoBehaviour, IFluidDepositSink,
        ILiquidOccupancyReadOnly, IFluidDormantReadOnly, IFluidDormantWakeSink
    {
        private static readonly ProfilerMarker PlanMarker =
            new ProfilerMarker("GpuFluid.Streaming.Plan");
        private static readonly ProfilerMarker ArchiveBuildMarker =
            new ProfilerMarker("GpuFluid.Streaming.ArchiveBuild");
        private static readonly ProfilerMarker ArchiveCommitMarker =
            new ProfilerMarker("GpuFluid.Streaming.ArchiveCommit");
        private static readonly ProfilerMarker AuditMarker =
            new ProfilerMarker("GpuFluid.Streaming.MaterialAudit");
        private static readonly ProfilerMarker ArchiveExcludeMarker =
            new ProfilerMarker("GpuFluid.Streaming.ArchiveExclude");
        private static readonly ProfilerMarker ArchiveMixedSelectionMarker =
            new ProfilerMarker("GpuFluid.Streaming.ArchiveMixedSelection");
        private static readonly ProfilerMarker ArchiveRebuildMarker =
            new ProfilerMarker("GpuFluid.Streaming.ArchiveRebuild");
        private static readonly ProfilerMarker ArchiveStoreMarker =
            new ProfilerMarker("GpuFluid.Streaming.ArchiveStore");
        private static readonly ProfilerMarker RestoreExpandMarker =
            new ProfilerMarker("GpuFluid.Streaming.RestoreExpand");
        private static readonly ProfilerMarker PendingReplayMarker =
            new ProfilerMarker("GpuFluid.Streaming.PendingReplay");

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private FluidChunkLifecycleState _debugState;
        [SerializeField] private uint _debugTransactionId;
        [SerializeField] private int _debugArchivedChunkCount;
        [SerializeField] private int _debugArchivedCellRecordCount;
        [SerializeField] private ulong _debugArchivedParticleCount;
        [SerializeField] private ulong _debugRestoredParticleCount;
        [SerializeField] private int _debugPendingWriteCount;
        [SerializeField] private uint _debugRejectedPendingWriteCount;
        [SerializeField] private uint _debugCompletedArchiveCount;
        [SerializeField] private uint _debugArchiveRollbackCount;
        [SerializeField] private uint _debugArchiveReadbackErrorCount;
        [SerializeField] private uint _debugCompletedRestoreCount;
        [SerializeField] private uint _debugRestoreRollbackCount;
        [SerializeField] private uint _debugRestoreReadbackErrorCount;
        [SerializeField] private uint _debugCapacityPressureCount;
        [SerializeField] private uint _debugOversizedChunkRestoreCount;
        [SerializeField] private int _debugLastArchiveReadbackBytes;
        [SerializeField] private float _debugLastArchiveDurationMilliseconds;
        [SerializeField] private int _debugLastRestoreParticleCount;
        [SerializeField] private float _debugLastRestoreDurationMilliseconds;
        [SerializeField] private uint _debugLocalDormancyArchiveCount;
        [SerializeField] private uint _debugDormantWakeRequestCount;
        [SerializeField] private FluidDormantWakeReason _debugLastDormantWakeReason;
        [SerializeField] private int _debugRetainedAliveSamples;
        [SerializeField] private int _debugRetainedSpeedBelow01;
        [SerializeField] private int _debugRetainedSpeedBelow05;
        [SerializeField] private int _debugRetainedSpeedBelow10;
        [SerializeField] private int _debugRetainedSpeedBelow25;
        [SerializeField] private float _debugRetainedMaximumSpeed;
        [SerializeField] private FluidArchiveRollbackReason _debugLastArchiveRollbackReason;
        [SerializeField] private int _debugLastArchiveLockedParticleCount;
        [SerializeField] private int _debugLastArchiveRecordCount;

        private FluidChunkStreamingSettings _settings;
        private IFluidChunkTransferBackend _backend;
        private IFluidDepositSink _downstream;
        private FluidChunkArchiveBuilder _builder;
        private FluidChunkArchiveStore _store;
        private FluidPendingWriteQueue _pendingWrites;
        private FluidChunkTransferState _transferState;
        private IFluidArchiveReadbackScheduler _readbackScheduler;
        private IFluidRestoreStatusReadbackScheduler _restoreStatusScheduler;
        private NativeArray<FluidGpuArchiveSample> _archiveSamples;
        private NativeArray<FluidGpuRestoreParticle> _restoreParticles;
        private NativeArray<FluidGpuTransferStatus> _restoreStatus;
        private FluidArchiveCellRecord[] _restoreRecords;
        private FluidGpuRestoreParticle[] _restoreParticleStaging;
        private uint[] _archiveUnlockParticleIndices;
        private LiquidMaterialCellSample[] _archiveCellSamples;
        private LiquidMaterialCellSample[] _reactionContactSamples;
        private Vector3Int[] _occupancyMergeKeys;
        private int[] _occupancyMergeIndices;
        private byte[] _occupancyMergeStates;
        private int _occupancyMergeMask;
        private Action<bool> _readbackCallback;
        private Action<bool> _restoreStatusCallback;
        private FluidArchiveReadbackLease _lease;
        private FluidRestoreStatusReadbackLease _restoreLease;
        private LiquidMaterialAmountScaleSnapshot _amountScales;
        private FluidMaterialAudit _materialAudit;
        private ulong _materialAuditEpoch;
        private uint _materialAuditTopologyVersion;
        private uint _materialAuditMutationVersion;
        private ILiquidOccupancyReadOnly _gameplayOccupancy;
        private IFluidGameplayTopologyReadOnly _gameplayTopology;
        private ElementChunkKey _restoringChunk;
        private ElementChunkKey _awaitingGameplayChunk;
        private uint _awaitingGameplayTopologyVersion;
        private int _restoreParticleCount;
        private int _restoreRecordCount;
        private int _restoreRecordIndex;
        private int _restoreRecordParticleOffset;
        private int _restorePreparedParticleCount;
        private uint _restoreSnapshotVersion;
        private uint _restorePreparationTransactionId;
        private bool _awaitingGameplayPublish;
        private bool _restorePreparing;
        private bool _hasRequestedRestoreChunk;
        private ElementChunkKey _requestedRestoreChunk;
        private Vector3 _worldOrigin;
        private float _cellSize;
        private int _chunkSize;
        private int _activeRadiusXZ;
        private int _activeRadiusY;
        private int _particleCapacity;
        private ElementChunkKey _interestChunk;
        private FluidChunkRegion _retainedRegion;
        private bool _hasInterestChunk;
        private bool _hasBackendLease;
        private bool _archiveAfterInterestChange;
        private bool _archiveIncludesSleepingRetained;
        private bool _materialAuditRequested;
        private bool _isInitialized;
        private double _interestChangedAt;
        private double _nextArchiveAttemptAt;
        private double _archiveStartedAt;
        private double _restoreStartedAt;
        private uint _nextTransactionId;

        public bool IsFluidInitialized => _isInitialized && _downstream != null
            && _downstream.IsFluidInitialized;
        public int ArchivedChunkCount => _store != null ? _store.ChunkCount : 0;
        public int ArchivedCellRecordCount => _store != null ? _store.RecordCount : 0;
        public int PendingWriteCount => _pendingWrites != null ? _pendingWrites.Count : 0;
        public uint RejectedPendingWriteCount => _pendingWrites != null ? _pendingWrites.RejectedCount : 0u;
        public uint CurrentTransactionId => _transferState != null ? _transferState.TransactionId : 0u;
        public ulong ArchivedParticleCount => _debugArchivedParticleCount;
        public ulong RestoredParticleCount => _debugRestoredParticleCount;
        public uint ArchiveReadbackErrorCount => _debugArchiveReadbackErrorCount;
        public uint RestoreReadbackErrorCount => _debugRestoreReadbackErrorCount;
        public uint ArchiveRollbackCount => _debugArchiveRollbackCount;
        public uint RestoreRollbackCount => _debugRestoreRollbackCount;
        public uint CapacityPressureCount => _debugCapacityPressureCount;
        public uint OversizedChunkRestoreCount => _debugOversizedChunkRestoreCount;
        public uint CompletedArchiveCount => _debugCompletedArchiveCount;
        public uint CompletedRestoreCount => _debugCompletedRestoreCount;
        public int LastArchiveReadbackBytes => _debugLastArchiveReadbackBytes;
        public float LastArchiveDurationMilliseconds => _debugLastArchiveDurationMilliseconds;
        public int LastRestoreParticleCount => _debugLastRestoreParticleCount;
        public float LastRestoreDurationMilliseconds => _debugLastRestoreDurationMilliseconds;
        public int DormantChunkCount => _store != null ? _store.DormantChunkCount : 0;
        public int DormantCellRecordCount => _store != null ? _store.DormantRecordCount : 0;
        public ulong DormantAmount => _store != null ? _store.DormantAmount : 0u;
        public uint LocalDormancyArchiveCount => _debugLocalDormancyArchiveCount;
        public uint DormantWakeRequestCount => _debugDormantWakeRequestCount;
        public FluidDormantWakeReason LastDormantWakeReason => _debugLastDormantWakeReason;
        public bool IsRestorePreparing => _restorePreparing;
        public int RestorePreparedParticleCount => _restorePreparedParticleCount;
        public int RetainedAliveSamples => _debugRetainedAliveSamples;
        public int RetainedSpeedBelow01 => _debugRetainedSpeedBelow01;
        public int RetainedSpeedBelow05 => _debugRetainedSpeedBelow05;
        public int RetainedSpeedBelow10 => _debugRetainedSpeedBelow10;
        public int RetainedSpeedBelow25 => _debugRetainedSpeedBelow25;
        public float RetainedMaximumSpeed => _debugRetainedMaximumSpeed;
        public FluidArchiveRollbackReason LastArchiveRollbackReason => _debugLastArchiveRollbackReason;
        public int LastArchiveLockedParticleCount => _debugLastArchiveLockedParticleCount;
        public int LastArchiveRecordCount => _debugLastArchiveRecordCount;
        public FluidArchiveStoreRejectionReason LastArchiveStoreRejectionReason =>
            _store != null ? _store.LastRejectionReason : FluidArchiveStoreRejectionReason.None;
        public FluidChunkLifecycleState State => _debugState;
        public bool HasDormantData => _store != null && _store.DormantChunkCount > 0;
        public uint DormantVersion => _store != null ? _store.Version : 0u;
        public int MaximumDormantCellRecords => _settings.MaximumArchivedCellRecords;
        public bool HasValidSnapshot => _isInitialized && _hasInterestChunk
            && ((_gameplayOccupancy != null && _gameplayOccupancy.HasValidSnapshot)
                || (_store != null && _store.ChunkCount > 0));
        public Bounds SnapshotBounds => _isInitialized && _hasInterestChunk
            ? _retainedRegion.ToWorldBounds(_worldOrigin, _cellSize, _chunkSize)
            : default;
        public uint SnapshotVersion => _gameplayOccupancy != null
            ? _gameplayOccupancy.SnapshotVersion
            : 0u;

        public bool TryGetMaterialAudit(MaterialId material, out FluidMaterialAuditSnapshot snapshot)
        {
            if (_materialAudit == null || _materialAuditEpoch == 0u)
            {
                snapshot = default;
                return false;
            }
            snapshot = _materialAudit.GetSnapshot(material, _materialAuditEpoch,
                _materialAuditTopologyVersion, _materialAuditMutationVersion);
            return snapshot.Valid;
        }

        public void RequestMaterialAudit(MaterialId material, ulong submittedParticleWatermark,
            double lastWriteAt)
        {
            if (!_isInitialized || _transferState.IsInFlight || _restorePreparing
                || _awaitingGameplayPublish)
                return;
            if (TryGetMaterialAudit(material, out FluidMaterialAuditSnapshot current)
                && current.IsFreshFor(submittedParticleWatermark, lastWriteAt))
                return;
            _materialAuditRequested = true;
        }

        internal bool TryInitialize(
            in FluidChunkStreamingSettings settings,
            GpuPbfFluidRuntime gpuRuntime,
            Vector3 worldOrigin,
            float cellSize,
            int chunkSize,
            int activeRadiusXZ,
            int activeRadiusY,
            FluidGameplayOccupancyBridge gameplayOccupancy = null)
        {
            return TryInitialize(
                in settings,
                gpuRuntime,
                gpuRuntime,
                new UnityFluidArchiveReadbackScheduler(),
                new UnityFluidRestoreStatusReadbackScheduler(),
                gameplayOccupancy,
                gameplayOccupancy,
                worldOrigin,
                cellSize,
                chunkSize,
                activeRadiusXZ,
                activeRadiusY);
        }

        internal bool TryInitialize(
            in FluidChunkStreamingSettings settings,
            IFluidChunkTransferBackend backend,
            IFluidDepositSink downstream,
            IFluidArchiveReadbackScheduler readbackScheduler,
            Vector3 worldOrigin,
            float cellSize,
            int chunkSize,
            int activeRadiusXZ,
            int activeRadiusY)
        {
            return TryInitialize(in settings, backend, downstream, readbackScheduler,
                new UnityFluidRestoreStatusReadbackScheduler(), null, null, worldOrigin, cellSize,
                chunkSize, activeRadiusXZ, activeRadiusY);
        }

        internal bool TryInitialize(
            in FluidChunkStreamingSettings settings,
            IFluidChunkTransferBackend backend,
            IFluidDepositSink downstream,
            IFluidArchiveReadbackScheduler readbackScheduler,
            IFluidRestoreStatusReadbackScheduler restoreStatusScheduler,
            ILiquidOccupancyReadOnly gameplayOccupancy,
            IFluidGameplayTopologyReadOnly gameplayTopology,
            Vector3 worldOrigin,
            float cellSize,
            int chunkSize,
            int activeRadiusXZ,
            int activeRadiusY)
        {
            if (_isInitialized) return true;
            if (!settings.Enabled || backend == null || downstream == null || readbackScheduler == null
                || restoreStatusScheduler == null
                || cellSize <= 0f || chunkSize <= 0
                || activeRadiusXZ < 0 || activeRadiusY < 0)
                return false;

            _backend = backend;
            _downstream = downstream;
            _readbackScheduler = readbackScheduler;
            _restoreStatusScheduler = restoreStatusScheduler;
            _gameplayOccupancy = gameplayOccupancy;
            _gameplayTopology = gameplayTopology;
            if (!_backend.TryGetParticleCapacity(out _particleCapacity) || _particleCapacity <= 0)
                return false;

            try
            {
                _settings = settings;
                _worldOrigin = worldOrigin;
                _cellSize = cellSize;
                _chunkSize = chunkSize;
                _activeRadiusXZ = activeRadiusXZ;
                _activeRadiusY = activeRadiusY;
                _builder = new FluidChunkArchiveBuilder(_particleCapacity);
                _materialAudit = new FluidMaterialAudit();
                _store = new FluidChunkArchiveStore(
                    settings.MaximumArchivedChunks,
                    settings.MaximumArchivedCellRecords,
                    chunkSize);
                _pendingWrites = new FluidPendingWriteQueue(settings.MaximumPendingWrites);
                _transferState = new FluidChunkTransferState();
                _archiveSamples = new NativeArray<FluidGpuArchiveSample>(
                    _particleCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _restoreParticles = new NativeArray<FluidGpuRestoreParticle>(
                    _particleCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _restoreStatus = new NativeArray<FluidGpuTransferStatus>(
                    1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _restoreRecords = new FluidArchiveCellRecord[_particleCapacity];
                _restoreParticleStaging = new FluidGpuRestoreParticle[_particleCapacity];
                _archiveUnlockParticleIndices = new uint[_particleCapacity];
                _reactionContactSamples = new LiquidMaterialCellSample[_particleCapacity];
                _archiveCellSamples = new LiquidMaterialCellSample[
                    settings.MaximumArchivedCellRecords];
                int occupancyCapacity = TableCapacity(checked(
                    _particleCapacity + settings.MaximumArchivedCellRecords));
                _occupancyMergeKeys = new Vector3Int[occupancyCapacity];
                _occupancyMergeIndices = new int[occupancyCapacity];
                _occupancyMergeStates = new byte[occupancyCapacity];
                _occupancyMergeMask = occupancyCapacity - 1;
                _readbackCallback = OnReadbackCompleted;
                _restoreStatusCallback = OnRestoreStatusCompleted;
                _debugState = FluidChunkLifecycleState.GpuResident;
                _isInitialized = true;
                return true;
            }
            catch (Exception)
            {
                DisposeNativeStorage();
                _backend = null;
                _downstream = null;
                _readbackScheduler = null;
                _restoreStatusScheduler = null;
                return false;
            }
        }

        internal void SetInterestChunk(ElementChunkKey interestChunk)
        {
            SetInterestChunkAt(interestChunk, Time.realtimeSinceStartupAsDouble);
        }

        internal void SetInterestChunkAt(ElementChunkKey interestChunk, double now)
        {
            if (!_isInitialized) return;
            if (_hasInterestChunk && _interestChunk == interestChunk) return;
            _retainedRegion = FluidChunkStreamingPlanner.BuildWarmRegion(
                interestChunk, _activeRadiusXZ, _activeRadiusY, _settings.WarmPaddingChunks);
            _interestChunk = interestChunk;
            _hasInterestChunk = true;
            _archiveAfterInterestChange = true;
            _interestChangedAt = now;
        }

        public bool TryEnqueueDeposit(in ElementWriteRequest request)
        {
            if (!IsFluidInitialized) return false;
            if (!MustDelayWrite(in request)
                && _downstream.TryEnqueueDeposit(in request))
                return true;

            // Downstream 的 false 同时表示 GPU Free 预算或固定 Queue 暂时不足；保留原请求，
            // 由 Tick 在 Archive 释放 slot 后 FIFO 重放，不能让一次水球命中静默丢失。
            if (_pendingWrites.TryEnqueue(in request))
            {
                _debugPendingWriteCount = _pendingWrites.Count;
                _debugRejectedPendingWriteCount = _pendingWrites.RejectedCount;
                return true;
            }
            _debugPendingWriteCount = _pendingWrites.Count;
            _debugRejectedPendingWriteCount = _pendingWrites.RejectedCount;
            return false;
        }

        private void Update()
        {
            Tick(Time.realtimeSinceStartupAsDouble, true);
        }

        internal void TickForTests(double now)
        {
            Tick(now, false);
        }

        private void Tick(double now, bool respectPause)
        {
            using var marker = PlanMarker.Auto();
            if (!_isInitialized) return;
            ProcessReadbackCompletion();
            FinishRestoreAfterGameplayPublish();
            ReplayPendingWrites();
            // Restore 已激活但 Gameplay 尚未发布时，Store 中仍保留旧查询镜像。
            // 此时启动 Archive 可能把新量合入该镜像，随后 RemoveChunk 会误删新量。
            // 将 Topology 发布作为完整事务边界；交接完成后下一 Tick 恢复压力回收。
            if (_awaitingGameplayPublish) return;
            if (!_hasInterestChunk || _transferState.IsInFlight || (respectPause && Time.timeScale <= 0f))
                return;

            if (_restorePreparing)
            {
                ContinueRestorePreparation();
                return;
            }

            PromoteQueuedWake();

            if (!_awaitingGameplayPublish && _hasRequestedRestoreChunk
                && _store.ContainsChunk(_requestedRestoreChunk))
            {
                StartRestorePreparation(_requestedRestoreChunk);
                return;
            }

            if (!_awaitingGameplayPublish && TrySelectColdChunk(out ElementChunkKey restoreChunk))
            {
                StartRestorePreparation(restoreChunk);
                return;
            }


            if (_materialAuditRequested)
            {
                // 反应账本是正确性诊断的输入。先等它追上最新 GPU 反应命令，再取得
                // Archive 快照，避免把两个不同时点的数据拼成伪造的守恒结论。
                if (_backend is IFluidMaterialAuditPreparation preparation
                    && !preparation.TryPrepareMaterialAudit())
                    return;
                _materialAuditRequested = false;
                // 正确性 Gate 复用既有 Archive Readback 取得一个 GPU Queue 时点；即使没有
                // 可归档粒子，ProcessReadbackCompletion 仍会先发布审计，再安全 Cancel 锁。
                BeginArchive(now, false);
                return;
            }

            if (now < _nextArchiveAttemptAt) return;
            _backend.TryGetFreeParticleCount(out uint freeParticles);
            if (freeParticles <= (uint)_settings.GameplaySpawnReserveParticles
                && _debugCapacityPressureCount < uint.MaxValue)
                _debugCapacityPressureCount++;
            float sinceChange = (float)Math.Max(0d, now - _interestChangedAt);
            if (!FluidChunkStreamingPlanner.ShouldStartArchive(
                    _archiveAfterInterestChange,
                    sinceChange,
                    _settings.ArchiveGraceSeconds,
                    freeParticles,
                    _settings.GameplaySpawnReserveParticles,
                    false))
                return;

            bool includeSleepingRetained =
                FluidChunkStreamingPlanner.ShouldIncludeSleepingRetained(
                    _settings.EnableLocalDormancy,
                    freeParticles,
                    _settings.GameplaySpawnReserveParticles,
                    _backend.TryGetActivityCounts(out _, out uint sleepingParticles),
                    sleepingParticles);
            BeginArchive(now, includeSleepingRetained);
        }

        private void BeginArchive(double now, bool includeSleepingRetained)
        {
            uint transactionId = NextTransactionId();
            if (!_transferState.TryBegin(transactionId)) return;
            _debugTransactionId = transactionId;

            Bounds retainedBounds = _retainedRegion.ToWorldBounds(_worldOrigin, _cellSize, _chunkSize);
            _frozenActivityRegions.Clear();
            if (_hasActivityRegions)
            {
                _frozenActivityRegions.CopyFrom(_warmActivityRegions);
                retainedBounds = _frozenActivityRegions.Envelope;
            }
            if (_backend is GpuPbfFluidRuntime gpu)
                gpu.SetArchiveActivityRegions(_hasActivityRegions ? _frozenActivityRegions : null);
            if (!_backend.TryAcquireArchiveReadbackLease(
                    retainedBounds, _worldOrigin, _cellSize, _chunkSize,
                    includeSleepingRetained, transactionId, out _lease))
            {
                // Backend 没有交出 Lease 时不能调用 Cancel/Release；这里只结束 CPU 状态并稍后重试。
                _transferState.RecordCompletion(transactionId, true);
                _transferState.Finish(transactionId);
                if (_debugArchiveRollbackCount < uint.MaxValue) _debugArchiveRollbackCount++;
                _debugLastArchiveRollbackReason = FluidArchiveRollbackReason.LeaseUnavailable;
                _debugTransactionId = 0u;
                _nextArchiveAttemptAt = now + FluidChunkStreamingPlanner.ArchiveRetryDelay(
                    _pendingWrites.Count > 0);
                return;
            }

            _hasBackendLease = true;
            _archiveIncludesSleepingRetained = includeSleepingRetained;
            _debugState = FluidChunkLifecycleState.Archiving;
            _archiveStartedAt = Time.realtimeSinceStartupAsDouble;
            _debugLastArchiveReadbackBytes = checked(_particleCapacity * FluidGpuArchiveSample.Stride);
            _archiveAfterInterestChange = false;
            try
            {
                _readbackScheduler.Request(
                    ref _archiveSamples, _lease.Samples, _readbackCallback);
            }
            catch (Exception)
            {
                _transferState.RecordCompletion(transactionId, true);
                ProcessReadbackCompletion();
            }
            // 无可归档粒子或 Driver 瞬时失败时避免容量压力导致每帧重试。
            _nextArchiveAttemptAt = now + FluidChunkStreamingPlanner.ArchiveRetryDelay(
                _pendingWrites.Count > 0);
        }

        private void OnReadbackCompleted(bool hasError)
        {
            if (!_isInitialized || !_transferState.IsInFlight) return;
            _transferState.RecordCompletion(_transferState.TransactionId, hasError);
        }

        private void OnRestoreStatusCompleted(bool hasError)
        {
            if (!_isInitialized || !_transferState.IsInFlight
                || _transferState.Kind != FluidChunkTransferKind.Restore) return;
            _transferState.RecordCompletion(_transferState.TransactionId, hasError);
        }

        private void ProcessReadbackCompletion()
        {
            if (!_transferState.TryConsumeCompletion(out uint transactionId, out bool hasError))
                return;

            if (_transferState.Kind == FluidChunkTransferKind.Restore)
            {
                ProcessRestoreCompletion(transactionId, hasError);
                return;
            }

            bool committed = false;
            int archivedParticles = 0;
            FluidArchiveRollbackReason rollbackReason = FluidArchiveRollbackReason.None;
            try
            {
                if (hasError || _lease.LayoutVersion != FluidGpuLayout.LayoutVersion)
                {
                    rollbackReason = hasError
                        ? FluidArchiveRollbackReason.ReadbackError
                        : FluidArchiveRollbackReason.LayoutMismatch;
                    if (hasError && _debugArchiveReadbackErrorCount < uint.MaxValue)
                        _debugArchiveReadbackErrorCount++;
                }
                else
                {
                    using (AuditMarker.Auto()) UpdateMaterialAudit();
                    UpdateRetainedSpeedDiagnostics();
                    var context = new FluidArchiveBuildContext(
                        _lease.WorldOrigin, _lease.CellSize, _lease.ChunkSize, _lease.AmountScales);
                    using (ArchiveBuildMarker.Auto())
                    {
                        _debugLastArchiveLockedParticleCount = 0;
                        for (int i = 0; i < _archiveSamples.Length; i++)
                            if ((_archiveSamples[i].Flags
                                & (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked))
                                == (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked))
                                _debugLastArchiveLockedParticleCount++;
                        using (ArchiveExcludeMarker.Auto())
                            committed = !_archiveIncludesSleepingRetained
                                || TryExcludeMixedRetainedCells();
                        if (!committed)
                            rollbackReason = FluidArchiveRollbackReason.FilteringFailed;
                        if (committed)
                        {
                            using (ArchiveRebuildMarker.Auto())
                                committed = _builder.TryRebuild(_archiveSamples, in context);
                            if (!committed)
                                rollbackReason = FluidArchiveRollbackReason.RebuildFailed;
                        }
                        _debugLastArchiveRecordCount = committed ? _builder.RecordCount : 0;
                        if (committed && _builder.RecordCount == 0)
                        {
                            committed = false;
                            rollbackReason = FluidArchiveRollbackReason.EmptyAfterFiltering;
                        }
                        if (committed)
                        {
                            for (int i = 0; i < _archiveSamples.Length; i++)
                                if ((_archiveSamples[i].Flags
                                    & (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked))
                                    == (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked))
                                    archivedParticles++;
                            using (ArchiveStoreMarker.Auto())
                                committed = _frozenActivityRegions.Count > 0
                                    ? _store.TryCommit(_builder, transactionId, _frozenActivityRegions, _worldOrigin, _cellSize * _chunkSize)
                                    : _store.TryCommit(_builder, transactionId, in _retainedRegion);
                            if (!committed && _debugCapacityPressureCount < uint.MaxValue)
                            {
                                _debugCapacityPressureCount++;
                                rollbackReason = FluidArchiveRollbackReason.StoreRejected;
                            }
                        }
                    }
                    if (committed) _amountScales = _lease.AmountScales;
                }

                if (committed)
                {
                    _debugLastArchiveRollbackReason = FluidArchiveRollbackReason.None;
                    using (ArchiveCommitMarker.Auto())
                        _backend.CommitArchiveAndRelease(transactionId);
                    _debugArchivedParticleCount += (uint)archivedParticles;
                    if (_debugCompletedArchiveCount < uint.MaxValue) _debugCompletedArchiveCount++;
                    if (_archiveIncludesSleepingRetained
                        && _debugLocalDormancyArchiveCount < uint.MaxValue)
                        _debugLocalDormancyArchiveCount++;
                }
                else
                {
                    _backend.CancelArchive(transactionId);
                    if (_debugArchiveRollbackCount < uint.MaxValue) _debugArchiveRollbackCount++;
                    _debugLastArchiveRollbackReason = rollbackReason == FluidArchiveRollbackReason.None
                        ? FluidArchiveRollbackReason.Unknown : rollbackReason;
                }
            }
            finally
            {
                if (_hasBackendLease)
                    _backend.ReleaseTransferReadbackLease();
                _hasBackendLease = false;
                _transferState.Finish(transactionId);
                _debugTransactionId = 0u;
                _lease = default;
                _archiveIncludesSleepingRetained = false;
                _debugLastArchiveDurationMilliseconds = ElapsedMilliseconds(_archiveStartedAt);
                _debugState = _store.ChunkCount > 0
                    ? FluidChunkLifecycleState.Cold
                    : FluidChunkLifecycleState.GpuResident;
                _debugArchivedChunkCount = _store.ChunkCount;
                _debugArchivedCellRecordCount = _store.RecordCount;
            }
        }

        private void UpdateMaterialAudit()
        {
            _materialAuditEpoch = _lease.AuditEpoch;
            _materialAuditTopologyVersion = _lease.TopologyVersion;
            _materialAuditMutationVersion = _lease.AuditMutationVersion;
            _materialAudit.Begin(_materialAuditEpoch, _materialAuditTopologyVersion,
                _materialAuditMutationVersion, _lease.SubmittedParticleWatermark,
                _lease.AuditRequestedAt, Time.realtimeSinceStartupAsDouble);

            // Restore Activate 到 Gameplay Topology 发布之间，RAM Chunk 是查询保底镜像；此时与
            // GPU Alive 同时存在，不能相加。等待交接完成后下一次 Archive 才重新给出有效审计。
            if (!_lease.AuditEligible || _lease.ExpectedGmuByMaterial == null
                || _lease.AmountScales == null || _awaitingGameplayPublish)
            {
                _materialAudit.Invalidate();
                return;
            }

            for (int index = 1; index < _lease.ExpectedGmuByMaterial.Length; index++)
            {
                ulong amount = _lease.ExpectedGmuByMaterial[index];
                if (amount != 0u
                    && !_materialAudit.TryAddExpected((MaterialId)(byte)index, amount))
                    return;
            }

            for (int i = 0; i < _archiveSamples.Length; i++)
            {
                FluidGpuArchiveSample sample = _archiveSamples[i];
                if ((sample.Flags & FluidGpuLayout.AliveFlag) == 0u) continue;
                MaterialId material = (MaterialId)(byte)sample.MaterialId;
                if (!_lease.AmountScales.TryGet(material, out uint scale)
                    || !_materialAudit.TryAddGpu(material, scale))
                {
                    _materialAudit.Invalidate();
                    return;
                }
            }

            if (!_store.TryAccumulateMaterialAudit(_materialAudit))
                _materialAudit.Invalidate();
        }

        private bool TryExcludeMixedRetainedCells()
        {
            Bounds retained = _lease.RetainedBounds;
            int unlockCount = 0;
            using (ArchiveMixedSelectionMarker.Auto())
            {
                for (int i = 0; i < _archiveSamples.Length; i++)
                {
                    FluidGpuArchiveSample sample = _archiveSamples[i];
                    if ((sample.Flags & (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked))
                        != (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked)
                        || !IsFrozenRetained(sample.Position, retained))
                        continue;
                    if (!TryGetSampleCell(in sample, out Vector3Int cell)) return false;
                    ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                        cell, _chunkSize, out ElementChunkKey chunk, out _);
                    // Dormant 查询会把 RAM 与 GPU 的不同粒子量按 Cell 合并，因此数量账本允许
                    // 同 Cell 的 Sleeping 子集归档；这不保证 PBF 邻居支撑等价，仍需物理验收。
                    // 只有已有 Cold 权威的 Chunk 必须
                    // 等待 Restore，避免同一 Chunk 同时出现 Cold 与 Dormant 两种可见性语义。
                    bool mustRemainOnGpu = _store.TryGetChunkKind(
                        chunk, out FluidArchivedChunkKind kind)
                        && kind == FluidArchivedChunkKind.Cold;
                    if (!mustRemainOnGpu) continue;
                    _archiveUnlockParticleIndices[unlockCount++] = (uint)i;
                    _archiveSamples[i] = new FluidGpuArchiveSample(
                        sample.Position, (Game.Materials.MaterialId)(byte)sample.MaterialId,
                        sample.Velocity, sample.Flags & ~FluidActivityFlags.ArchiveLocked);
                }
            }
            // GPU 先解锁与已有 Cold 权威冲突的候选，Commit 只释放 CPU Store 真正接收的子集。
            return _backend.TryExcludeArchiveParticleIndices(
                _archiveUnlockParticleIndices, unlockCount);
        }

        private bool TryGetSampleCell(
            in FluidGpuArchiveSample sample, out Vector3Int cell)
        {
            try
            {
                cell = ElementWorldCoordinates.WorldToGlobalCell(
                    sample.Position, _worldOrigin, _cellSize);
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException || exception is OverflowException)
            {
                cell = default;
                return false;
            }
        }

        private void UpdateRetainedSpeedDiagnostics()
        {
            int alive = 0, below01 = 0, below05 = 0, below10 = 0, below25 = 0;
            float maximum = 0f;
            Bounds retained = _lease.RetainedBounds;
            for (int i = 0; i < _archiveSamples.Length; i++)
            {
                FluidGpuArchiveSample sample = _archiveSamples[i];
                if ((sample.Flags & FluidGpuLayout.AliveFlag) == 0u
                    || !IsFrozenRetained(sample.Position, retained))
                    continue;
                float speed = sample.Velocity.magnitude;
                alive++;
                if (speed <= .01f) below01++;
                if (speed <= .05f) below05++;
                if (speed <= .10f) below10++;
                if (speed <= .25f) below25++;
                if (speed > maximum) maximum = speed;
            }
            _debugRetainedAliveSamples = alive;
            _debugRetainedSpeedBelow01 = below01;
            _debugRetainedSpeedBelow05 = below05;
            _debugRetainedSpeedBelow10 = below10;
            _debugRetainedSpeedBelow25 = below25;
            _debugRetainedMaximumSpeed = maximum;
        }

        private void StartRestorePreparation(ElementChunkKey chunk)
        {
            if (_amountScales == null || !_store.TryGetChunkVersion(chunk, out uint snapshotVersion))
                return;
            int recordCount = _store.CopyChunkRecords(chunk, _restoreRecords);
            if (recordCount <= 0) return;
            _restoringChunk = chunk;
            _restoreSnapshotVersion = snapshotVersion;
            _restorePreparationTransactionId = NextTransactionId();
            _restoreRecordCount = recordCount;
            _restoreRecordIndex = 0;
            _restoreRecordParticleOffset = 0;
            _restorePreparedParticleCount = 0;
            _restorePreparing = true;
            _hasRequestedRestoreChunk = false;
            _debugState = FluidChunkLifecycleState.Restoring;
            _restoreStartedAt = Time.realtimeSinceStartupAsDouble;
            ContinueRestorePreparation();
        }

        private void ContinueRestorePreparation()
        {
            int budget = _settings.MaximumRestoreParticlesPerFrame;
            using (RestoreExpandMarker.Auto())
            {
                while (budget > 0 && _restoreRecordIndex < _restoreRecordCount)
                {
                    FluidArchiveCellRecord record = _restoreRecords[_restoreRecordIndex];
                    if (!_amountScales.TryGet(record.Material, out uint scale))
                    {
                        CancelRestorePreparation();
                        return;
                    }
                    int expanded = FluidChunkRestorePlanner.ExpandRange(
                        in record, scale, _worldOrigin, _cellSize, _chunkSize,
                        _restoreSnapshotVersion, _restorePreparationTransactionId,
                        _restoreRecordParticleOffset, budget,
                        _restoreParticleStaging, _restorePreparedParticleCount);
                    if (expanded < 0)
                    {
                        if (_debugOversizedChunkRestoreCount < uint.MaxValue)
                            _debugOversizedChunkRestoreCount++;
                        CancelRestorePreparation();
                        return;
                    }
                    _restorePreparedParticleCount += expanded;
                    budget -= expanded;
                    int recordParticles = (int)(record.Amount / scale);
                    _restoreRecordParticleOffset += expanded;
                    if (_restoreRecordParticleOffset >= recordParticles)
                    {
                        _restoreRecordIndex++;
                        _restoreRecordParticleOffset = 0;
                    }
                }
            }
            if (_restoreRecordIndex < _restoreRecordCount) return;
            int total = _restorePreparedParticleCount;
            uint transactionId = _restorePreparationTransactionId;
            _restorePreparing = false;
            if (total <= 0 || total > _particleCapacity)
            {
                if (_debugOversizedChunkRestoreCount < uint.MaxValue)
                    _debugOversizedChunkRestoreCount++;
                CancelRestorePreparation();
                return;
            }
            for (int i = 0; i < total; i++) _restoreParticles[i] = _restoreParticleStaging[i];
            if (!_transferState.TryBegin(transactionId, FluidChunkTransferKind.Restore)) return;
            _debugTransactionId = transactionId;
            if (!_backend.TryAcquireRestoreStatusLease(
                    _restoreParticles, total, _settings.GameplaySpawnReserveParticles,
                    transactionId, out _restoreLease))
            {
                _transferState.RecordCompletion(transactionId, true);
                _transferState.Finish(transactionId);
                if (_debugRestoreRollbackCount < uint.MaxValue) _debugRestoreRollbackCount++;
                _debugTransactionId = 0u;
                return;
            }
            _hasBackendLease = true;
            _restoreParticleCount = total;
            _debugLastRestoreParticleCount = total;
            try
            {
                _restoreStatusScheduler.Request(
                    ref _restoreStatus, _restoreLease.Status, _restoreStatusCallback);
            }
            catch (Exception)
            {
                _transferState.RecordCompletion(transactionId, true);
                ProcessReadbackCompletion();
            }
        }

        private void CancelRestorePreparation()
        {
            _restorePreparing = false;
            _restoreRecordCount = 0;
            _restoreRecordIndex = 0;
            _restoreRecordParticleOffset = 0;
            _restorePreparedParticleCount = 0;
            _restorePreparationTransactionId = 0u;
            _debugState = _store != null && _store.ChunkCount > 0
                ? FluidChunkLifecycleState.Cold
                : FluidChunkLifecycleState.GpuResident;
        }

        private void ProcessRestoreCompletion(uint transactionId, bool hasError)
        {
            FluidGpuTransferStatus status = _restoreStatus[0];
            bool valid = !hasError && status.Succeeded == 1u
                && status.TransactionId == transactionId
                && status.ParticleCount == (uint)_restoreParticleCount;
            try
            {
                if (valid)
                {
                    _backend.CommitRestore(
                        transactionId, _restoreParticleCount, out _awaitingGameplayTopologyVersion);
                    _awaitingGameplayChunk = _restoringChunk;
                    _awaitingGameplayPublish = true;
                    _debugRestoredParticleCount += (uint)_restoreParticleCount;
                    if (_debugCompletedRestoreCount < uint.MaxValue) _debugCompletedRestoreCount++;
                }
                else
                {
                    _backend.RollbackRestore(transactionId, _restoreParticleCount);
                    if (hasError && _debugRestoreReadbackErrorCount < uint.MaxValue)
                        _debugRestoreReadbackErrorCount++;
                    if (_debugRestoreRollbackCount < uint.MaxValue) _debugRestoreRollbackCount++;
                }
            }
            finally
            {
                _backend.ReleaseTransferReadbackLease();
                _hasBackendLease = false;
                _transferState.Finish(transactionId);
                _debugTransactionId = 0u;
                _restoreLease = default;
                _debugLastRestoreDurationMilliseconds = ElapsedMilliseconds(_restoreStartedAt);
                _restoreParticleCount = 0;
                if (!_awaitingGameplayPublish) _debugState = FluidChunkLifecycleState.Cold;
            }
        }

        private void FinishRestoreAfterGameplayPublish()
        {
            if (!_awaitingGameplayPublish) return;
            if (_gameplayTopology != null
                && (!_gameplayTopology.HasValidSnapshot
                    || _gameplayTopology.TopologyVersion < _awaitingGameplayTopologyVersion))
                return;
            _store.RemoveChunk(_awaitingGameplayChunk);
            _debugArchivedParticleCount = _debugArchivedParticleCount >= (uint)_debugLastRestoreParticleCount
                ? _debugArchivedParticleCount - (uint)_debugLastRestoreParticleCount
                : 0u;
            _awaitingGameplayPublish = false;
            _debugArchivedChunkCount = _store.ChunkCount;
            _debugArchivedCellRecordCount = _store.RecordCount;
            _debugState = _store.ChunkCount > 0
                ? FluidChunkLifecycleState.Cold
                : FluidChunkLifecycleState.GpuResident;
        }

        public bool TryGetAmount(
            Vector3Int globalCell, Game.Materials.MaterialId materialKind, out byte amount)
        {
            amount = 0;
            if (!_isInitialized || !_hasInterestChunk) return false;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell, _chunkSize, out ElementChunkKey chunk, out _);
            if (_hasActivityRegions && !IsRetainedChunk(chunk)) return false;
            if (_store.ContainsChunk(chunk))
            {
                // Cold 区域离屏暂停 Gameplay；Warm 内允许 Cell 级 GPU + Dormant 混合表示，
                // 所以同一 Cell 的两侧 Amount 必须相加，不能用 Chunk 粒度遮掉整份 GPU Snapshot。
                if (!IsRetainedChunk(chunk)) return false;
                bool hasArchive = _store.TryGetAmount(
                    globalCell, materialKind, out uint archiveAmount);
                byte gameplayAmount = 0;
                bool hasGameplay = _gameplayOccupancy != null
                    && _gameplayOccupancy.TryGetAmount(
                        globalCell, materialKind, out gameplayAmount);
                amount = (byte)Math.Min(
                    byte.MaxValue, archiveAmount + (hasGameplay ? gameplayAmount : 0u));
                return hasArchive || hasGameplay;
            }
            return _gameplayOccupancy != null
                && _gameplayOccupancy.TryGetAmount(globalCell, materialKind, out amount);
        }

        public bool TryGetAmountUnitsPerParticle(
            Game.Materials.MaterialId material, out uint amountUnits)
        {
            if (_gameplayOccupancy != null
                && _gameplayOccupancy.TryGetAmountUnitsPerParticle(material, out amountUnits))
                return true;
            amountUnits = 0u;
            return _amountScales != null && _amountScales.TryGet(material, out amountUnits);
        }

        public int CopyOccupiedCells(
            Game.Materials.MaterialId material, LiquidMaterialCellSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!_isInitialized || destination.Length == 0) return 0;
            int count = _gameplayOccupancy != null
                ? _gameplayOccupancy.CopyOccupiedCells(material, destination)
                : 0;
            Array.Clear(_occupancyMergeStates, 0, _occupancyMergeStates.Length);
            for (int i = 0; i < count; i++)
                AddOccupancyMergeCell(destination[i].GlobalCell, i);

            int archiveCount = _hasActivityRegions
                ? _store.CopyRegionCells(_warmActivityRegions, default, false, _worldOrigin,
                    _cellSize * _chunkSize, material, _archiveCellSamples)
                : _store.CopyCells(in _retainedRegion, material, _archiveCellSamples);
            for (int i = 0; i < archiveCount; i++)
            {
                LiquidMaterialCellSample archived = _archiveCellSamples[i];
                if (TryGetOccupancyMergeIndex(archived.GlobalCell, out int existingIndex))
                {
                    int merged = destination[existingIndex].Amount + archived.Amount;
                    destination[existingIndex] = new LiquidMaterialCellSample(
                        archived.GlobalCell, (byte)Math.Min(byte.MaxValue, merged));
                    continue;
                }
                if (count >= destination.Length) break;
                destination[count] = archived;
                AddOccupancyMergeCell(archived.GlobalCell, count);
                count++;
            }
            return count;
        }

        private void AddOccupancyMergeCell(Vector3Int cell, int index)
        {
            int slot = cell.GetHashCode() & int.MaxValue & _occupancyMergeMask;
            for (int i = 0; i < _occupancyMergeStates.Length; i++)
            {
                if (_occupancyMergeStates[slot] == 0)
                {
                    _occupancyMergeStates[slot] = 1;
                    _occupancyMergeKeys[slot] = cell;
                    _occupancyMergeIndices[slot] = index;
                    return;
                }
                if (_occupancyMergeKeys[slot] == cell) return;
                slot = (slot + 1) & _occupancyMergeMask;
            }
        }

        private bool TryGetOccupancyMergeIndex(Vector3Int cell, out int index)
        {
            int slot = cell.GetHashCode() & int.MaxValue & _occupancyMergeMask;
            for (int i = 0; i < _occupancyMergeStates.Length; i++)
            {
                if (_occupancyMergeStates[slot] == 0) break;
                if (_occupancyMergeKeys[slot] == cell)
                {
                    index = _occupancyMergeIndices[slot];
                    return true;
                }
                slot = (slot + 1) & _occupancyMergeMask;
            }
            index = -1;
            return false;
        }

        internal void RequestLiquidReactionContactWakes(MaterialId gpuMaterial, MaterialId dormantMaterial)
        {
            if (!_isInitialized || !HasDormantData || _gameplayOccupancy == null
                || !_gameplayOccupancy.HasValidSnapshot) return;

            // 只枚举 GPU Snapshot，不能枚举合并 RAM 的查询，否则休眠材料会相互唤醒。
            // 每种材料的占用格数不超过 GPU 粒子数，初始化时一次分配即可覆盖全部候选。
            int count = _gameplayOccupancy.CopyOccupiedCells(gpuMaterial, _reactionContactSamples);
            for (int i = 0; i < count; i++)
            {
                LiquidMaterialCellSample sample = _reactionContactSamples[i];
                if (sample.Amount == 0) continue;
                Vector3Int cell = sample.GlobalCell;
                // 与 LiquidCellReactionSystem 保持同格 + 六轴邻格的接触范围。
                TryWakeDormantReactionPartner(cell, dormantMaterial);
                TryWakeDormantReactionPartner(cell + Vector3Int.left, dormantMaterial);
                TryWakeDormantReactionPartner(cell + Vector3Int.right, dormantMaterial);
                TryWakeDormantReactionPartner(cell + Vector3Int.down, dormantMaterial);
                TryWakeDormantReactionPartner(cell + Vector3Int.up, dormantMaterial);
                TryWakeDormantReactionPartner(cell + Vector3Int.back, dormantMaterial);
                TryWakeDormantReactionPartner(cell + Vector3Int.forward, dormantMaterial);
            }
        }

        private void TryWakeDormantReactionPartner(Vector3Int cell, MaterialId material)
        {
            // Store 点查询后只发 Restore 请求。实际转换仍等 GPU Snapshot 发布后规划，
            // 不提前减少 RAM Amount，也不向尚未恢复的粒子发 Convert 命令。
            if (_store.TryGetAmount(cell, material, out uint amount) && amount > 0)
                RequestWake(cell, FluidDormantWakeReason.Reaction);
        }

        public int CopyDormantCells(
            Game.Materials.MaterialId material,
            LiquidMaterialCellSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            return _isInitialized && _hasInterestChunk
                ? _store.CopyCells(
                    in _retainedRegion, FluidArchivedChunkKind.Dormant, material, destination)
                : 0;
        }

        public bool RequestWake(Vector3Int globalCell, FluidDormantWakeReason reason)
        {
            if (!_isInitialized || !_hasInterestChunk) return false;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell, _chunkSize, out ElementChunkKey chunk, out _);
            return RequestWakeChunk(chunk, reason);
        }

        private bool RequestWakeChunk(ElementChunkKey chunk, FluidDormantWakeReason reason)
        {
            if (!IsRetainedChunk(chunk)
                || !_store.TryGetChunkKind(chunk, out FluidArchivedChunkKind kind)
                || kind != FluidArchivedChunkKind.Dormant)
                return false;
            if ((_hasRequestedRestoreChunk && _requestedRestoreChunk == chunk)
                || (_restorePreparing && _restoringChunk == chunk)
                || (_transferState.IsInFlight
                    && _transferState.Kind == FluidChunkTransferKind.Restore
                    && _restoringChunk == chunk)
                || (_awaitingGameplayPublish && _awaitingGameplayChunk == chunk))
                return true;
            if (_hasRequestedRestoreChunk)
                return _wakeQueue.TryEnqueue(chunk, reason);
            _requestedRestoreChunk = chunk;
            _hasRequestedRestoreChunk = true;
            _debugLastDormantWakeReason = reason;
            if (_debugDormantWakeRequestCount < uint.MaxValue)
                _debugDormantWakeRequestCount++;
            return true;
        }

        private Vector3Int ToGlobalCell(in FluidArchiveCellRecord record)
        {
            int layer = _chunkSize * _chunkSize;
            int localZ = record.LocalCellIndex / layer;
            int remainder = record.LocalCellIndex - localZ * layer;
            int localY = remainder / _chunkSize;
            int localX = remainder - localY * _chunkSize;
            return new Vector3Int(
                checked(record.Chunk.X * _chunkSize + localX),
                checked(record.Chunk.Y * _chunkSize + localY),
                checked(record.Chunk.Z * _chunkSize + localZ));
        }

        private void ReplayPendingWrites()
        {
            using var marker = PendingReplayMarker.Auto();
            int budget = _settings.MaximumPendingWrites;
            while (budget-- > 0 && _pendingWrites.TryPeek(out ElementWriteRequest request))
            {
                if (MustDelayWrite(in request) || !_downstream.TryEnqueueDeposit(in request)) return;
                _pendingWrites.TryDequeue(out _);
            }
            _debugPendingWriteCount = _pendingWrites.Count;
            _debugRejectedPendingWriteCount = _pendingWrites.RejectedCount;
        }

        private static float ElapsedMilliseconds(double startedAt)
        {
            return (float)Math.Max(0d, (Time.realtimeSinceStartupAsDouble - startedAt) * 1000d);
        }

        private static int TableCapacity(int itemCapacity)
        {
            int capacity = 1;
            int required = checked(itemCapacity * 2);
            while (capacity < required) capacity = checked(capacity << 1);
            return capacity;
        }

        private bool MustDelayWrite(in ElementWriteRequest request)
        {
            if (!_hasInterestChunk) return false;
            Vector3 radius = Vector3.one * Mathf.Max(0f, request.Radius);
            Vector3Int minCell = ElementWorldCoordinates.WorldToGlobalCell(
                request.WorldPosition - radius, _worldOrigin, _cellSize);
            Vector3Int maxCell = ElementWorldCoordinates.WorldToGlobalCell(
                request.WorldPosition + radius, _worldOrigin, _cellSize);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(minCell, _chunkSize,
                out ElementChunkKey minChunk, out _);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(maxCell, _chunkSize,
                out ElementChunkKey maxChunk, out _);

            if (_transferState.IsInFlight
                && (!IsRetainedChunk(minChunk) || !IsRetainedChunk(maxChunk)))
                return true;

            for (int z = minChunk.Z; z <= maxChunk.Z; z++)
            for (int y = minChunk.Y; y <= maxChunk.Y; y++)
            for (int x = minChunk.X; x <= maxChunk.X; x++)
            {
                ElementChunkKey candidate = new ElementChunkKey(x, y, z);
                if (!_store.ContainsChunk(candidate)) continue;
                if (IsRetainedChunk(candidate)
                    && _store.TryGetChunkKind(candidate, out FluidArchivedChunkKind kind)
                    && kind == FluidArchivedChunkKind.Dormant)
                {
                    // 同材质继续写入可与静态 Cell 并存，由查询端合并 Amount；异材质可能触发反应，
                    // 仍需恢复整 Chunk 后再提交，避免 RAM 物质绕过 GPU 反应链。
                    if (!_store.ContainsMaterialOtherThanInRange(
                            candidate, minCell, maxCell, request.MaterialKind))
                        continue;
                    RequestWakeChunk(candidate, FluidDormantWakeReason.Write);
                }
                return true;
            }
            return false;
        }

        private uint NextTransactionId()
        {
            unchecked { _nextTransactionId++; }
            if (_nextTransactionId == 0u) _nextTransactionId = 1u;
            return _nextTransactionId;
        }

        private void OnDestroy()
        {
            if (_transferState != null && _transferState.IsInFlight)
            {
                if (_hasBackendLease && _readbackScheduler.IsPending)
                    _readbackScheduler.WaitForCompletion();
                uint transactionId = _transferState.TransactionId;
                if (_hasBackendLease)
                {
                    if (_transferState.Kind == FluidChunkTransferKind.Restore)
                    {
                        if (_restoreStatusScheduler.IsPending)
                            _restoreStatusScheduler.WaitForCompletion();
                        _backend.RollbackRestore(transactionId, _restoreParticleCount);
                    }
                    else
                    {
                        _backend.CancelArchive(transactionId);
                    }
                    _backend.ReleaseTransferReadbackLease();
                    _hasBackendLease = false;
                }
                if (_transferState.Phase == FluidChunkTransferPhase.WaitingForReadback)
                    _transferState.RecordCompletion(transactionId, true);
                _transferState.Finish(transactionId);
            }
            DisposeNativeStorage();
            _isInitialized = false;
        }

        private void DisposeNativeStorage()
        {
            if (_archiveSamples.IsCreated) _archiveSamples.Dispose();
            if (_restoreParticles.IsCreated) _restoreParticles.Dispose();
            if (_restoreStatus.IsCreated) _restoreStatus.Dispose();
        }
    }
}
