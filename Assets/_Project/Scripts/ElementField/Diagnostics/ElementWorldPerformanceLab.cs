using System;
using System.IO;
using Game.Combat;
using Game.Core;
using Game.Materials;
using UnityEngine;
using UnityEngine.Profiling;

namespace Game.ElementField
{
    /// <summary>专用实验场景的生命周期驱动。所有负载都经过生产 Registry，测量中只写预分配样本。</summary>
    [DefaultExecutionOrder(1000)]
    public sealed class ElementWorldPerformanceLab : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private GpuPbfFluidRuntime _fluid;
        [SerializeField] private FluidGameplayOccupancyBridge _gameplay;
        [SerializeField] private ElementWorldExposureSystem _exposure;
        [SerializeField] private Transform _interest;
        [SerializeField] private Transform _camera;
        [SerializeField] private StatusController _probe;
        [SerializeField] private GameObject[] _targets;
        [SerializeField] private ElementWorldPerformanceProfile _profile;
        [SerializeField] private ElementWorldPerformanceProfile[] _commandLineProfiles;
        [SerializeField] private bool _autoStart;
        [SerializeField, TextArea] private string _productionConfiguration;
        private ElementWorldPerformanceProfile _frozen;
        private ElementWorldPerformanceSchedule _prefill, _measure;
        private ElementWorldPerformanceCapture _capture;
        private FluidChunkStreamingRuntime _streaming;
        private double _phaseStarted, _runStarted, _lastWriteAt, _routeDwellUntil;
        private int _oldVsync, _oldTargetFps, _routeIndex;
        private bool _startedOnce, _settingsOwned, _quitOnComplete;
        private bool _oldRunInBackground;
        private int _quitCountdown = -1, _quitExitCode;
        private Vector3 _oldInterestPosition, _oldCameraPosition;
        private string _configuration, _hardware;
        private string _surfaceMode = "all";
        private string _surfaceDiagnosticsMode = "on";
        private bool _forceUncapped;
        private int _targetFpsOverride;
        public ElementPerformancePhase Phase { get; private set; }
        public string LastResultDirectory { get; private set; }

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-ewperfSurface" && i + 1 < args.Length) _surfaceMode = args[++i];
                if (args[i] == "-ewperfSurfaceDiagnostics" && i + 1 < args.Length)
                    _surfaceDiagnosticsMode = args[++i];
                if (args[i] == "-ewperfUncapped") _forceUncapped = true;
                if (args[i] == "-ewperfTargetFps" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out _targetFpsOverride)
                        || _targetFpsOverride < 30 || _targetFpsOverride > 1000)
                    {
                        _targetFpsOverride = int.MinValue;
                    }
                }
                if (args[i] == "-ewperfQuit") _quitOnComplete = true;
                if (args[i] != "-ewperfScenario" || i + 1 >= args.Length) continue;
                string id = args[++i];
                _profile = null;
                if (_commandLineProfiles != null)
                    for (int j = 0; j < _commandLineProfiles.Length; j++)
                        if (_commandLineProfiles[j] != null && _commandLineProfiles[j].ScenarioId == id)
                            _profile = _commandLineProfiles[j];
                _autoStart = true;
            }
            if (_autoStart) StartRun();
        }

        [ContextMenu("Start Performance Run")]
        public void StartRun()
        {
            if (_startedOnce) { GameLog.Warn("每次实验需要重新加载干净场景或重启 Player。", "ElementPerformance"); return; }
            try
            {
                if (_profile == null || _world == null || !_world.IsInitialized || _fluid == null
                    || !_fluid.IsFluidInitialized || _gameplay == null || _exposure == null)
                    throw new InvalidOperationException("实验引用未配置或世界未初始化。");
                if (_targetFpsOverride == int.MinValue)
                    throw new ArgumentException("-ewperfTargetFps 必须在30到1000之间。");
                if (_fluid.SubmittedParticleCount != 0 || _world.WorldTick < 0)
                    throw new InvalidOperationException("实验需要没有历史写入的干净世界。");
                _frozen = Instantiate(_profile);
                if (_forceUncapped) _frozen.FrameRateMode = ElementPerformanceFrameRate.Uncapped;
                _streaming = _world.GetComponent<FluidChunkStreamingRuntime>();
                if (_surfaceMode != "all" && _surfaceMode != "water" && _surfaceMode != "off")
                    throw new ArgumentException("非法的实验 SurfaceMode。");
                if (_surfaceDiagnosticsMode != "on" && _surfaceDiagnosticsMode != "off")
                    throw new ArgumentException("非法的实验 SurfaceDiagnosticsMode。");
                _frozen.ValidateConfiguration();
                _world.ConfigureActivityForPerformance(_frozen.EnableWorldActivityExpansion);
                CreateActivitySources();
                if (_frozen.Movement != ElementPerformanceMovement.None && (_interest == null || _camera == null))
                    throw new InvalidOperationException("需要专用 Interest 和 Camera 引用。");
                if (_frozen.UseProbe && _probe == null) throw new InvalidOperationException("需要状态探针。");
                _prefill = _frozen.CreateSchedule(true);
                _measure = _frozen.CreateSchedule(false);
                _capture = new ElementWorldPerformanceCapture();
                int targetFps = _frozen.FrameRateMode == ElementPerformanceFrameRate.Target60
                    ? (_targetFpsOverride > 0 ? _targetFpsOverride : 60)
                    : -1;
                _configuration = "{\"surfaceMode\":\"" + _surfaceMode
                    + "\",\"surfaceDiagnosticsMode\":\"" + _surfaceDiagnosticsMode
                    + "\",\"targetFpsApplied\":" + targetFps
                    + ",\"scenario\":" + JsonUtility.ToJson(_frozen, true)
                    + ",\"production\":" + (string.IsNullOrEmpty(_productionConfiguration) ? "null" : _productionConfiguration) + "}";
                _hardware = $"Unity={Application.unityVersion}; GPU={SystemInfo.graphicsDeviceName}; "
                    + $"API={SystemInfo.graphicsDeviceType}; Driver={SystemInfo.graphicsDeviceVersion}; "
                    + $"CPU={SystemInfo.processorType}; RAM_MB={SystemInfo.systemMemorySize}; "
                    + $"Resolution={Screen.width}x{Screen.height}; Quality={QualitySettings.names[QualitySettings.GetQualityLevel()]}; "
                    + $"Editor={Application.isEditor}; Development={Debug.isDebugBuild}";
                _oldVsync = QualitySettings.vSyncCount;
                _oldTargetFps = Application.targetFrameRate;
                _oldRunInBackground = Application.runInBackground;
                Application.runInBackground = true;
                // 专用实验 Player 明确固定窗口；初始化后由 Warmup 吸收分辨率切换开销。
                if (!Application.isEditor) Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
                QualitySettings.vSyncCount = 0;
                // 可选Override只服务帧节拍归因实验，并写入configuration；正式60 FPS样本默认仍为60。
                Application.targetFrameRate = targetFps;
                _settingsOwned = true;
                if (_interest != null) _oldInterestPosition = _interest.position;
                if (_camera != null) _oldCameraPosition = _camera.position;
                if (_targets != null)
                    for (int i = 0; i < _targets.Length; i++)
                        if (_targets[i] != null) _targets[i].SetActive(i < _frozen.TargetCount);
                _capture.Start(_frozen.CaptureEnabled);
                _startedOnce = true;
                _runStarted = _lastWriteAt = Time.realtimeSinceStartupAsDouble;
                EnterPhase(ElementPerformancePhase.Warmup, _runStarted);
                GameLog.Info($"[ELEMENT-PERF] Started {_frozen.ScenarioId}; {_hardware}", "ElementPerformance");
            }
            catch (Exception exception)
            {
                GameLog.Error($"[ELEMENT-PERF] Start failed: {exception.Message}", "ElementPerformance");
                Restore();
                _capture?.Stop();
                if (_quitOnComplete && !Application.isEditor) Application.Quit(1);
            }
        }

        private void LateUpdate()
        {
            // 状态 VFX 先完整经历数帧 Disable/Destroy，再退出 DX12 Player；避免在活跃 ParticleSystem
            // 与 GraphicsBuffer 同一帧销毁时把工具层的 Teardown 崩溃误判成模拟失败。
            if (_quitCountdown >= 0)
            {
                if (_quitCountdown-- == 0 && !Application.isEditor) Application.Quit(_quitExitCode);
                return;
            }
            if (Phase == ElementPerformancePhase.Idle || Phase == ElementPerformancePhase.Finished) return;
            double now = Time.realtimeSinceStartupAsDouble;
            double elapsed = now - _phaseStarted;
            if (!_capture.Running) { StopRun(); return; }
            // 先记录刚完成的阶段，再切换，避免结束窗口的卡顿帧被记到 Drain 而逃出统计。
            Record(now);
            switch (Phase)
            {
                case ElementPerformancePhase.Warmup:
                    if (elapsed >= _frozen.WarmupSeconds) EnterPhase(ElementPerformancePhase.Prefill, now);
                    break;
                case ElementPerformancePhase.Prefill:
                    SubmitDue(_prefill, elapsed, now);
                    if (_prefill.RemainingCount == 0) EnterPhase(ElementPerformancePhase.Settle, now);
                    break;
                case ElementPerformancePhase.Settle:
                    if (elapsed >= _frozen.SettleSeconds) EnterPhase(ElementPerformancePhase.Measure, now);
                    break;
                case ElementPerformancePhase.Measure:
                    SubmitDue(_measure, elapsed, now);
                    MoveAlongRoute();
                    if (elapsed >= _frozen.MeasureSeconds)
                    {
                        if (_measure.RemainingCount != 0) _capture.MarkIncomplete();
                        EnterPhase(ElementPerformancePhase.Drain, now);
                    }
                    break;
                case ElementPerformancePhase.Drain:
                    bool writesDrained = _fluid.PendingSpawnRequestCount == 0
                        && _world.PendingWriteCount == 0
                        && (_streaming == null || _streaming.PendingWriteCount == 0);
                    // 最终审计会启动一次只读 Archive 事务。必须先让普通 Archive 释放容量并
                    // 重放全部 Backpressure 写入，否则诊断事务会持续抢占队首并制造伪超时。
                    if (_frozen.RequireFreshMaterialAuditAtDrain && _streaming != null
                        && writesDrained)
                        _streaming.RequestMaterialAudit(
                            _frozen.MeasureMaterial, _fluid.SubmittedParticleCount, _lastWriteAt);
                    bool auditFresh = !_frozen.RequireFreshMaterialAuditAtDrain
                        || (_streaming != null
                            && _streaming.TryGetMaterialAudit(
                                _frozen.MeasureMaterial, out FluidMaterialAuditSnapshot audit)
                            && audit.IsFreshFor(_fluid.SubmittedParticleCount, _lastWriteAt));
                    bool fresh = writesDrained
                        // Streaming Backpressure 会保留尚未获得 GPU 槽位的原始写入；只有它也清空，
                        // “全部写入已处理”的性能结论才成立，不能被下游 Spawn Queue 的空状态提前掩盖。
                        && (_streaming == null || (_streaming.CurrentTransactionId == 0u
                            && _streaming.PendingWakeCount == 0))
                        && _fluid.PoolDiagnostics.IsFreshFor(_fluid.PoolMutationVersion)
                        && _fluid.PoolDiagnostics.RequestedAt > _lastWriteAt
                        && _gameplay.Diagnostics.Valid && _gameplay.Diagnostics.RequestedAt > _lastWriteAt
                        && auditFresh;
                    if (fresh || elapsed >= _frozen.DrainTimeoutSeconds)
                    {
                        if (!fresh) _capture.MarkIncomplete();
                        Finish();
                        return;
                    }
                    break;
            }
        }

        private void EnterPhase(ElementPerformancePhase phase, double now)
        {
            Phase = phase;
            _phaseStarted = now;
            if (phase != ElementPerformancePhase.Measure) return;
            if (_frozen.RemoveActivitySourcesAtMeasure) ReleaseActivitySources();
            _routeIndex = 1;
            _routeDwellUntil = 0d;
            if (_frozen.Movement != ElementPerformanceMovement.None)
                (_frozen.Movement == ElementPerformanceMovement.CameraOnly ? _camera : _interest).position = _frozen.Route[0];
            if (_frozen.UseProbe)
                _probe.ApplyStatus(_frozen.ProbeStatus, _frozen.ProbeInitialIntensity, gameObject.GetInstanceID(), byte.MaxValue);
        }

        private void SubmitDue(ElementWorldPerformanceSchedule schedule, double elapsed, double now)
        {
            for (int i = 0; i < _frozen.MaxWritesPerFrame && schedule.TryDequeueDue(elapsed, out var write); i++)
            {
                bool accepted = ElementRuntimeRegistry.TryEnqueueWrite(in write.Request);
                _lastWriteAt = now;
                var result = new ElementPerformanceWriteResult { Phase = Phase, Write = write, At = now,
                    LatenessSeconds = elapsed - write.DueSeconds, Accepted = accepted };
                _capture.RecordWrite(in result);
                if (!_capture.Running) break;
            }
        }

        private void MoveAlongRoute()
        {
            if (_frozen.Movement == ElementPerformanceMovement.None || _routeIndex >= _frozen.Route.Length) return;
            if (Time.realtimeSinceStartupAsDouble < _routeDwellUntil) return;
            Transform target = _frozen.Movement == ElementPerformanceMovement.CameraOnly ? _camera : _interest;
            // 每帧固定上限工作；折线结点余量不制造无界 while。路线在报告中保留，允许不同帧率产生有限误差。
            target.position = Vector3.MoveTowards(target.position, _frozen.Route[_routeIndex], _frozen.MoveSpeed * Time.unscaledDeltaTime);
            if (target.position == _frozen.Route[_routeIndex])
            {
                _routeIndex++;
                _routeDwellUntil = Time.realtimeSinceStartupAsDouble + _frozen.RouteDwellSeconds;
            }
        }

        private void Record(double now)
        {
            var frame = new ElementPerformanceFrame
            {
                Frame = Time.frameCount, At = now, Phase = Phase,
                WorldTicks = _world.LastFrameTickCount, FluidTicks = _fluid.LastFrameTickCount,
                PendingSpawns = _fluid.PendingSpawnRequestCount, Submitted = _fluid.SubmittedParticleCount,
                WorldDebt = _world.DroppedDebtFrameCount, FluidDebt = _fluid.DroppedDebtFrameCount,
                ProcessedWrites = _world.ProcessedWriteCount, RejectedWrites = _world.RejectedWriteCount,
                ReactionPairs = _world.ReactionPairCount, ResidentChunks = _world.ResidentChunkCount,
                ActiveChunks = _world.ActiveChunkCount, Pool = _fluid.PoolDiagnostics,
                Activity = _fluid.ActivityDiagnostics, Gameplay = _gameplay.Diagnostics,
                Colliders = _exposure.LastColliderCount, Targets = _exposure.LastStatusTargetCount,
                ExposureSaturated = _exposure.LastQueryMayBeSaturated
            };
            if (_streaming != null)
            {
                frame.ArchivedChunks = _streaming.ArchivedChunkCount;
                frame.ArchivedRecords = _streaming.ArchivedCellRecordCount;
                frame.ArchivedParticles = _streaming.ArchivedParticleCount;
                frame.RestoredParticles = _streaming.RestoredParticleCount;
                frame.DormantChunks = _streaming.DormantChunkCount;
                frame.DormantRecords = _streaming.DormantCellRecordCount;
                frame.DormantAmount = _streaming.DormantAmount;
                frame.LocalDormancyArchives = _streaming.LocalDormancyArchiveCount;
                frame.DormantWakeRequests = _streaming.DormantWakeRequestCount;
                frame.LastDormantWakeReason = _streaming.LastDormantWakeReason;
                frame.RestorePreparing = _streaming.IsRestorePreparing;
                frame.RestorePreparedParticles = _streaming.RestorePreparedParticleCount;
                frame.RetainedAliveSamples = _streaming.RetainedAliveSamples;
                frame.RetainedSpeedBelow01 = _streaming.RetainedSpeedBelow01;
                frame.RetainedSpeedBelow05 = _streaming.RetainedSpeedBelow05;
                frame.RetainedSpeedBelow10 = _streaming.RetainedSpeedBelow10;
                frame.RetainedSpeedBelow25 = _streaming.RetainedSpeedBelow25;
                frame.RetainedMaximumSpeed = _streaming.RetainedMaximumSpeed;
                frame.PendingStreamingWrites = _streaming.PendingWriteCount;
                frame.RejectedPendingWrites = _streaming.RejectedPendingWriteCount;
                frame.ArchiveTransactions = _streaming.CompletedArchiveCount;
                frame.RestoreTransactions = _streaming.CompletedRestoreCount;
                frame.ArchiveErrors = _streaming.ArchiveReadbackErrorCount;
                frame.RestoreErrors = _streaming.RestoreReadbackErrorCount;
                frame.ArchiveRollbacks = _streaming.ArchiveRollbackCount;
                frame.RestoreRollbacks = _streaming.RestoreRollbackCount;
                frame.CapacityPressure = _streaming.CapacityPressureCount;
                frame.LastArchiveBytes = _streaming.LastArchiveReadbackBytes;
                frame.LastArchiveMs = _streaming.LastArchiveDurationMilliseconds;
                frame.LastArchiveLockedParticles = _streaming.LastArchiveLockedParticleCount;
                frame.LastArchiveRecords = _streaming.LastArchiveRecordCount;
                frame.LastArchiveRollbackReason = _streaming.LastArchiveRollbackReason;
                frame.LastArchiveStoreRejectionReason = _streaming.LastArchiveStoreRejectionReason;
                frame.LastRestoreParticles = _streaming.LastRestoreParticleCount;
                frame.LastRestoreMs = _streaming.LastRestoreDurationMilliseconds;
                _streaming.TryGetMaterialAudit(MaterialId.Water, out frame.WaterAudit);
                _streaming.TryGetMaterialAudit(MaterialId.Poison, out frame.PoisonAudit);
                _streaming.TryGetMaterialAudit(MaterialId.Sticky, out frame.StickyAudit);
            }
            if (_probe != null && _probe.isActiveAndEnabled)
            {
                frame.Burning = _probe.GetIntensity(StatusKind.Burning); frame.Wet = _probe.GetIntensity(StatusKind.Wet);
                frame.Poisoned = _probe.GetIntensity(StatusKind.Poisoned); frame.Sticky = _probe.GetIntensity(StatusKind.Sticky);
                Vector3Int cell = ElementWorldCoordinates.WorldToGlobalCell(_probe.transform.position, _world.Origin, _world.CellSize);
                _world.MaterialAmounts.TryGetAmount(cell, MaterialId.Water, out frame.ProbeWater);
            }
            frame.ActivityRegions = _world.ActivityRegions != null ? _world.ActivityRegions.Count : 1;
            frame.ActivitySources = _world.ActivitySourceCount;
            frame.RejectedRegions = _world.ActivityRegions != null ? _world.ActivityRegions.RejectedCount : 0;
            frame.UnitSourcesSkippedByBudget = _world.UnitActivitySourcesSkippedByBudget;
            frame.ProjectileSourcesSkippedByBudget = _world.ProjectileActivitySourcesSkippedByBudget;
            frame.PendingWakes = _streaming != null ? _streaming.PendingWakeCount : 0;
            Vector3Int measureCell = ElementWorldCoordinates.WorldToGlobalCell(_frozen.MeasurePosition, _world.Origin, _world.CellSize);
            if (_world.TryGetCell(measureCell, out ElementCell measuredFire) && measuredFire.MaterialKind == MaterialId.Fire)
                frame.MeasureFire = measuredFire.Amount;
            _world.MaterialAmounts?.TryGetAmount(measureCell, MaterialId.Water, out frame.MeasureWater);
            // Unity 原生计数器不创建托管对象；Soak 可按分钟从 CSV 抽样，检查平台而非只看终点。
            frame.TotalAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();
            frame.TotalReservedMemory = Profiler.GetTotalReservedMemoryLong();
            frame.MonoHeapMemory = Profiler.GetMonoHeapSizeLong();
            frame.MonoUsedMemory = Profiler.GetMonoUsedSizeLong();
            frame.GraphicsDriverMemory = Profiler.GetAllocatedMemoryForGraphicsDriver();
            _capture.RecordFrame(ref frame);
        }

        [ContextMenu("Stop Performance Run")]
        public void StopRun()
        {
            if (Phase == ElementPerformancePhase.Idle || Phase == ElementPerformancePhase.Finished) return;
            _capture.MarkIncomplete();
            Finish();
        }

        private void Finish()
        {
            _capture.Stop();
            Phase = ElementPerformancePhase.Finished;
            Restore();
            var exporter = new ElementWorldPerformanceExporter(_capture, _frozen.ScenarioId, _configuration, _hardware);
            string runId = _frozen.ScenarioId + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            bool exported = exporter.TryExport(Path.Combine(Application.persistentDataPath, "ElementPerformance"),
                runId, out string directory, out string error);
            LastResultDirectory = directory;
            if (exported) GameLog.Info($"[ELEMENT-PERF] Finished Incomplete={_capture.Incomplete}; {directory}", "ElementPerformance");
            else GameLog.Error($"[ELEMENT-PERF] Export failed: {error}", "ElementPerformance");
            if (_quitOnComplete && !Application.isEditor)
            {
                // Restore 会恢复原 runInBackground。自动基准窗口失焦时，若这里恢复成 false，
                // 后续 LateUpdate 的退出倒计时便不再推进；保持后台运行直至 Quit。
                Application.runInBackground = true;
                if (_probe != null) _probe.gameObject.SetActive(false);
                _quitExitCode = exported && !_capture.Incomplete ? 0 : 2;
                _quitCountdown = 3;
            }
        }

        private void Restore()
        {
            ReleaseActivitySources();
            if (!_settingsOwned) return;
            QualitySettings.vSyncCount = _oldVsync; Application.targetFrameRate = _oldTargetFps;
            Application.runInBackground = _oldRunInBackground;
            if (_interest != null) _interest.position = _oldInterestPosition;
            if (_camera != null) _camera.position = _oldCameraPosition;
            _settingsOwned = false;
        }
        private void OnDisable() { StopRun(); Restore(); _capture?.Dispose(); }
        private void OnDestroy() { if (_frozen != null) Destroy(_frozen); }

        private GameObject[] _activitySourceObjects;
        private WorldActivitySourceKind[] _activitySourceKinds;
        private void CreateActivitySources()
        {
            Vector3[] units = _frozen.ActivitySourcePositions;
            Vector3[] projectiles = _frozen.ProjectileActivitySourcePositions;
            int unitCount = units != null ? units.Length : 0;
            int projectileCount = projectiles != null ? projectiles.Length : 0;
            int count = unitCount + projectileCount;
            _activitySourceObjects = new GameObject[count];
            _activitySourceKinds = new WorldActivitySourceKind[count];
            for (int i = 0; i < count; i++)
            {
                bool unit = i < unitCount;
                WorldActivitySourceKind kind = unit
                    ? WorldActivitySourceKind.Unit : WorldActivitySourceKind.Projectile;
                int sourceIndex = unit ? i : i - unitCount;
                GameObject source = new GameObject(unit
                    ? "PerformanceUnitActivitySource" : "PerformanceProjectileActivitySource");
                source.transform.position = unit ? units[sourceIndex] : projectiles[sourceIndex];
                _activitySourceObjects[i] = source;
                _activitySourceKinds[i] = kind;
                EventBus<WorldActivitySourceEvent>.Publish(new WorldActivitySourceEvent
                    { Source = source.transform, Kind = kind, Enabled = true });
            }
        }
        private void ReleaseActivitySources()
        {
            if (_activitySourceObjects == null) return;
            for (int i = 0; i < _activitySourceObjects.Length; i++)
            {
                if (_activitySourceObjects[i] == null) continue;
                EventBus<WorldActivitySourceEvent>.Publish(new WorldActivitySourceEvent
                    { Source = _activitySourceObjects[i].transform, Kind = _activitySourceKinds[i], Enabled = false });
                Destroy(_activitySourceObjects[i]);
                _activitySourceObjects[i] = null;
            }
        }
    }
}
