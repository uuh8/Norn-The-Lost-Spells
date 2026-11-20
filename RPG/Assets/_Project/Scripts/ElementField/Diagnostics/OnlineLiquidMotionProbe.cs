using System;
using System.IO;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 显式Native Development诊断：Authority仅经正式ElementWriteRequest注水，Replica仅查询现有ReadModel。
    /// 空ENV不创建对象、不Find、不分配缓冲、不轮询磁盘；没有Multiplayer/Rendering依赖。
    /// 表现Window/Alpha只是输入证据，不能据此声称GPU已绘制、帧级Zero-GC或画面验收通过。
    /// </summary>
    public sealed class OnlineLiquidMotionProbe : MonoBehaviour
    {
        private const double DurationSeconds = 10, SampleInterval = .05, InjectionInterval = .4;
        private const int MaximumFrames = 16384, MaximumSamples = 256, InjectionCount = 10;
        private const ushort InjectionQuantity = 512;
        private static readonly Vector3 InjectionOffset = new Vector3(.125f, 4.125f, .125f);
        private string _injectionPattern="pulse-fall";
        private int _maximumInjections=InjectionCount;
        private ushort _requestedInjectionQuantity=InjectionQuantity;
        private double _requestedInjectionInterval=InjectionInterval;
        private float _requestedInjectionRadius=.25f;
        private Vector3 _requestedInitialVelocity;
        private string _directory, _requestPath, _reportPath;
        private ElementWorldRuntime _world;
        private ILiquidSurfaceReadOnly _liquid;
        private WorldReplicationSource _quantitySource;
        private WorldLiquidMotionSource _motionSource;
        private WorldReplicaBuffer _quantityBuffer;
        private WorldLiquidMotionCaptureBuffer _motionBuffer;
        private Report _report;
        private Sample[] _samples;
        private Injection[] _injections;
        private float[] _frameMilliseconds;
        private int _sampleCount, _frameCount, _framesDropped;
        private double _nextPoll, _startedAt, _lastFrameAt, _nextSample, _nextInjection;
        private bool _started, _finished;
        private long _lastCaptureTick = long.MinValue;
        private bool _lastCaptureSucceeded;
        private int _lastQuantityFailure, _lastMotionRows, _lastMovingRows;
        private ulong _lastPreviousGmu, _lastCurrentGmu;
        private double _lastCaptureMilliseconds;
        private int _pid;
        private long _processStartUtcTicks;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
#if DEVELOPMENT_BUILD && !UNITY_EDITOR
            // 必须先检查开关。正常运行不会创建诊断GameObject，也不会扫描场景或检查文件。
            string configured = Environment.GetEnvironmentVariable("NORN_LIQUID_MOTION_PROBE");
            if (string.IsNullOrWhiteSpace(configured)) return;
            if (!TryGetOutputDirectory(configured, out string directory))
            { GameLog.Warn("显式液体诊断要求绝对目录。", "LiquidMotionProbe"); return; }
            string pattern=Environment.GetEnvironmentVariable("NORN_LIQUID_MOTION_PATTERN");
            if(!string.IsNullOrEmpty(pattern) && pattern!="pulse-fall" && pattern!="continuous-pour")
            {GameLog.Warn("未知显式液体诊断模式；本次不创建探针。","LiquidMotionProbe");return;}
            var root = new GameObject("ExplicitNativeLiquidMotionProbe");
            DontDestroyOnLoad(root);
            var probe = root.AddComponent<OnlineLiquidMotionProbe>();
            // 只在显式诊断中复现正式UseDefinition的发射参数，不取得容器/库存写权。
            // 2秒连续200GMU/.1s、v=(8,2,0)用于检验出生历史和喷流；消耗/F/Mouse仍由独立键鼠回归验收。
            // 与旧512GMU/.4s零初速脉冲分开标记，避免把单滴下落当作持续挥洒的证据。
            if(pattern=="continuous-pour")
            {probe._injectionPattern=pattern;probe._maximumInjections=20;probe._requestedInjectionQuantity=200;
                probe._requestedInjectionInterval=.1;probe._requestedInjectionRadius=.2f;probe._requestedInitialVelocity=new Vector3(8,2,0);}
            probe._directory = directory; probe._requestPath = Path.Combine(directory, "start.request");
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            { probe._pid = process.Id; probe._processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks; }
            probe._reportPath = Path.Combine(directory, "liquid-motion-" + probe._pid + "-" + probe._processStartUtcTicks + ".json");
#endif
        }

        private static bool TryGetOutputDirectory(string configured, out string directory)
        {
            directory = null;
            if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured)) return false;
            try { directory = Path.GetFullPath(configured); return true; }
            catch (Exception) { return false; }
        }

        private void Update()
        {
            if (_finished || string.IsNullOrEmpty(_directory)) return;
            try
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (!_started)
                {
                    if (now < _nextPoll) return;
                    _nextPoll = now + .25;
                    // 所有进程读同一个启动信号，不能由DS删掉导致第二个Client错过它。
                    if (!File.Exists(_requestPath) || !TryFindReadyWorld()) return;
                    Begin(); return;
                }
                if (_world == null || !_world.IsInitialized || _world.GridLiquids != _liquid)
                { Finish("WorldDetached", null); return; }
                float elapsedMilliseconds = (float)((now - _lastFrameAt) * 1000);
                _lastFrameAt = now;
                if (_frameCount < _frameMilliseconds.Length) _frameMilliseconds[_frameCount++] = elapsedMilliseconds;
                else _framesDropped++;
                if (now - _startedAt >= DurationSeconds) { Finish("Completed", null); return; }
                if (_world.ExecutionMode == WorldExecutionMode.Authority && _report.InjectionAttempts < _maximumInjections && now >= _nextInjection)
                {
                    var request = new ElementWriteRequest(_world.Origin + InjectionOffset, MaterialId.Water, _requestedInjectionQuantity, _requestedInjectionRadius, false,_requestedInitialVelocity);
                    bool accepted = _world.TryEnqueueWrite(in request);
                    int index = _report.InjectionAttempts++;
                    _injections[index] = new Injection { ElapsedSeconds = now - _startedAt, WorldTick = _world.WorldTick,
                        Accepted = accepted, RequestedGmu = _requestedInjectionQuantity };
                    if (accepted) { _report.InjectionAccepted++; _report.AcceptedGmu += _requestedInjectionQuantity; }
                    // 使用真实间隔，不在卡顿后连发追赶债务。此探针不证明逐Frame的玩家输入或容器扣量。
                    _nextInjection = now + _requestedInjectionInterval;
                }
                if (now >= _nextSample) { SampleState(now); _nextSample = now + SampleInterval; }
            }
            catch (Exception error) { Finish("ProbeError", error.ToString()); }
        }

        private bool TryFindReadyWorld()
        {
            // 查找仅在显式start后的准备阶段执行；开始计量后固定绑定，不每帧Find或选另一张图。
            var worlds = FindObjectsByType<ElementWorldRuntime>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            ElementWorldRuntime selected = null;
            for (int i = 0; i < worlds.Length; i++)
            {
                var candidate = worlds[i];
                if (!candidate.IsInitialized || candidate.IsSimulationFaulted || candidate.GridLiquids == null
                    || candidate.ExecutionMode != WorldExecutionMode.Authority && candidate.ExecutionMode != WorldExecutionMode.Replica
                    || candidate.ExecutionMode == WorldExecutionMode.Authority && candidate.SimulationSuspended) continue;
                if (selected != null) return false; // 多个合法世界时不猜测要注入哪一个。
                selected = candidate;
            }
            _world = selected; return _world != null;
        }

        private void Begin()
        {
            _liquid = _world.GridLiquids;
            _samples = new Sample[MaximumSamples]; _injections = new Injection[_maximumInjections];
            _frameMilliseconds = new float[MaximumFrames];
            if (_world.ExecutionMode == WorldExecutionMode.Authority)
            {
                _quantitySource = new WorldReplicationSource(_world); _motionSource = new WorldLiquidMotionSource(_world);
                // 独占诊断缓冲，不覆写正式复制Coordinator仍可能使用的CaptureBuffer。
                _quantityBuffer = new WorldReplicaBuffer(65536, 1024, 2000000);
                _motionBuffer = new WorldLiquidMotionCaptureBuffer();
            }
            _report = new Report { State = "Sampling", Mode = _world.ExecutionMode.ToString(), ProcessId = _pid,
                ProcessStartUtcTicks = _processStartUtcTicks, StartedUtc = DateTime.UtcNow.ToString("O"),
                Scene = _world.gameObject.scene.path, RequestedDurationSeconds = DurationSeconds,
                InjectionPosition = _world.Origin + InjectionOffset, StartWorldTick = _world.WorldTick,
                InjectionPattern=_injectionPattern,RequestedInjectionInterval=_requestedInjectionInterval,
                RequestedInjectionQuantity=_requestedInjectionQuantity,RequestedInitialVelocity=_requestedInitialVelocity,
                InitialLiquidGmu = _liquid.TotalQuantity, InitialPendingGmu = _liquid.PendingQuantity,
                FrameTimingScope = "Native Update-to-Update wall time; includes this explicit probe and extra read-only capture cost. No GPU draw or Zero-GC claim.",
                SurfaceEvidenceScope = "ReadModel input/window/alpha/version only; no Renderer, mesh, dispatch or visible-pixel observation." };
            // 初始化分配不混入下面10秒的帧时间；JSON、排序与IO统一在窗口结束之后执行。
            _startedAt = _lastFrameAt = _nextSample = _nextInjection = Time.realtimeSinceStartupAsDouble;
            _started = true;
        }

        private void SampleState(double now)
        {
            if (_sampleCount == _samples.Length) { _report.DroppedStateSamples++; return; }
            var transport = _liquid as ILiquidTransportReadOnly;
            var playback = _liquid as ILiquidPresentationPlaybackSource;
            var replica = _liquid as WorldReplicaLiquidReadModel;
            ulong window = playback != null ? playback.PresentationWindowId : 0;
            float alpha = 1;
            bool alphaAvailable = playback != null && playback.TryGetPresentationAlpha(window, out alpha);
            // Authority每个材料Tick至多额外捕获一次。只读capture会增加成本，单独记录供解释帧时间。
            if (_quantitySource != null && _world.WorldTick != _lastCaptureTick && !_world.IsWorldStepInProgress)
            {
                _lastCaptureTick = _world.WorldTick; _report.MotionCaptureAttempts++;
                double before = Time.realtimeSinceStartupAsDouble;
                bool quantityValid = _quantitySource.TryCapture(_quantityBuffer, out var failure);
                _lastQuantityFailure = (int)failure;
                _lastCaptureSucceeded = quantityValid && _motionSource.TryCapture(_motionBuffer, _quantityBuffer);
                _lastMotionRows = _lastMovingRows = 0; _lastPreviousGmu = _lastCurrentGmu = 0;
                if (_lastCaptureSucceeded)
                {
                    _report.MotionCaptureSucceeded++; _lastMotionRows = _motionBuffer.Count;
                    if (_lastMotionRows > 0) _report.MotionCapturesWithRows++;
                    for (int i = 0; i < _motionBuffer.Count; i++)
                    {
                        var row = _motionBuffer.Rows[i]; _lastPreviousGmu += row.PreviousQuantity; _lastCurrentGmu += row.CurrentQuantity;
                        if (row.PreviousQuantity != row.CurrentQuantity || row.PreviousOffset != row.CurrentOffset
                            || row.PreviousVelocity.sqrMagnitude > 1e-8f || row.CurrentVelocity.sqrMagnitude > 1e-8f) _lastMovingRows++;
                    }
                }
                _lastCaptureMilliseconds = (Time.realtimeSinceStartupAsDouble - before) * 1000;
                _report.ExtraCaptureTotalMilliseconds += _lastCaptureMilliseconds;
                _report.ExtraCaptureMaximumMilliseconds = Math.Max(_report.ExtraCaptureMaximumMilliseconds, _lastCaptureMilliseconds);
            }
            var cell = Vector3Int.FloorToInt((_world.Origin + InjectionOffset - _liquid.Origin) / _liquid.CellSize);
            _liquid.TryGetQuantity(cell, MaterialId.Water, out uint injectionCellQuantity);
            _samples[_sampleCount++] = new Sample { ElapsedSeconds = now - _startedAt, Frame = Time.frameCount,
                WorldTick = _world.ExecutionMode == WorldExecutionMode.Replica && _world.ReplicaStore != null ? _world.ReplicaStore.WorldTick : _world.WorldTick,
                TransportEpoch = transport != null ? transport.TransportEpoch : 0,
                LiquidGmu = _liquid.TotalQuantity, PendingGmu = _liquid.PendingQuantity, InjectionCellWaterGmu = injectionCellQuantity,
                SurfaceVersion = _liquid.SurfaceVersion, DynamicSurfaceVersion = _liquid.DynamicSurfaceVersion,
                WindowId = window, AlphaAvailable = alphaAvailable, Alpha = alpha,
                // 与表现探针保存同一组值时间轴。这里只记录readonly Source，不把它称为GPU像素证据。
                PresentationServerTime = replica != null ? replica.PresentationServerTime : 0,
                SourceSampleTime = replica != null ? replica.PresentationSampleTime : 0,
                SourcePlaybackStartTime = replica != null ? replica.PresentationPlaybackStartTime : 0,
                BufferedWindows = replica != null ? replica.BufferedPresentationWindows : 0,
                BufferedOverflows = replica != null ? replica.BufferedPresentationOverflows : 0,
                BufferedDiscontinuities = replica != null ? replica.BufferedPresentationDiscontinuities : 0,
                SourceHolding = replica != null && replica.PresentationHolding,
                SourceSnapped = replica != null && replica.PresentationSnapped,
                PresentationRevision = playback != null ? playback.PresentationRevision : 0,
                PresentationInvalidationRevision = playback != null ? playback.PresentationInvalidationRevision : 0,
                MotionCaptureWorldTick = _lastCaptureTick, MotionCaptureSucceeded = _lastCaptureSucceeded,
                QuantityCaptureFailure = _lastQuantityFailure, MotionRows = _lastMotionRows, MovingRows = _lastMovingRows,
                MotionPreviousGmu = _lastPreviousGmu, MotionCurrentGmu = _lastCurrentGmu,
                ExtraCaptureMilliseconds = _lastCaptureMilliseconds, SimulationFaulted = _world.IsSimulationFaulted,
                SimulationSuspended = _world.SimulationSuspended, SimulationPausedForDebug = _world.SimulationPausedForDebug };
        }

        private void Finish(string state, string error)
        {
            if (_finished) return;
            _finished = true;
            if (_report == null) { enabled = false; return; }
            _report.State = state; _report.Error = error; _report.FinishedUtc = DateTime.UtcNow.ToString("O");
            _report.ActualDurationSeconds = Time.realtimeSinceStartupAsDouble - _startedAt;
            _report.FrameTimes = SummarizeFrameTimes(_frameMilliseconds, _frameCount, _framesDropped);
            _report.Samples = new Sample[_sampleCount]; Array.Copy(_samples, _report.Samples, _sampleCount);
            _report.Injections = new Injection[_report.InjectionAttempts]; Array.Copy(_injections, _report.Injections, _report.InjectionAttempts);
            try
            {
                Directory.CreateDirectory(_directory);
                // PID+进程启动时间隔离双Client及PID复用；CreateNew保护既有失败/成功证据，不覆盖旧文件。
                using (var file = new FileStream(_reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(file)) writer.Write(JsonUtility.ToJson(_report, true));
            }
            catch (Exception writeError) { GameLog.Warn("显式液体诊断报告写入失败：" + writeError.Message, "LiquidMotionProbe"); }
            enabled = false;
        }

        private static FrameStatistics SummarizeFrameTimes(float[] samples, int count, int dropped)
        {
            var result = new FrameStatistics { SampleCount = count, DroppedSamples = dropped };
            if (count == 0) return result;
            var sorted = new float[count]; Array.Copy(samples, sorted, count); Array.Sort(sorted);
            double sum = 0; for (int i = 0; i < count; i++) sum += sorted[i];
            result.MeanMilliseconds = sum / count; result.MaximumMilliseconds = sorted[count - 1];
            result.P50Milliseconds = sorted[(int)Math.Ceiling(count * .50) - 1];
            result.P95Milliseconds = sorted[(int)Math.Ceiling(count * .95) - 1];
            result.P99Milliseconds = sorted[(int)Math.Ceiling(count * .99) - 1]; return result;
        }

        private void OnDisable() { if (_started && !_finished) Finish("Interrupted", null); }
        private void OnApplicationQuit() { if (_started && !_finished) Finish("ProcessQuit", null); }

        [Serializable] private sealed class FrameStatistics
        { public int SampleCount, DroppedSamples; public double MeanMilliseconds; public float P50Milliseconds, P95Milliseconds, P99Milliseconds, MaximumMilliseconds; }
        [Serializable] private struct Injection
        { public double ElapsedSeconds; public long WorldTick; public bool Accepted; public uint RequestedGmu; }
        [Serializable] private struct Sample
        {
            public double ElapsedSeconds, ExtraCaptureMilliseconds;
            public double PresentationServerTime, SourceSampleTime, SourcePlaybackStartTime;
            public int BufferedWindows;public long BufferedOverflows, BufferedDiscontinuities;
            public int Frame, QuantityCaptureFailure, MotionRows, MovingRows;
            public long WorldTick, MotionCaptureWorldTick;
            public uint TransportEpoch, InjectionCellWaterGmu, SurfaceVersion, DynamicSurfaceVersion, PresentationRevision, PresentationInvalidationRevision;
            public ulong LiquidGmu, PendingGmu, WindowId, MotionPreviousGmu, MotionCurrentGmu;
            public float Alpha;
            public bool AlphaAvailable, MotionCaptureSucceeded, SimulationFaulted, SimulationSuspended, SimulationPausedForDebug, SourceHolding, SourceSnapped;
        }
        [Serializable] private sealed class Report
        {
            public string State, Error, Mode, StartedUtc, FinishedUtc, Scene, FrameTimingScope, SurfaceEvidenceScope;
            public string InjectionPattern;
            public double RequestedInjectionInterval;
            public uint RequestedInjectionQuantity;
            public Vector3 RequestedInitialVelocity;
            public int ProcessId, InjectionAttempts, InjectionAccepted, DroppedStateSamples, MotionCaptureAttempts, MotionCaptureSucceeded, MotionCapturesWithRows;
            public long ProcessStartUtcTicks, StartWorldTick;
            public ulong AcceptedGmu, InitialLiquidGmu, InitialPendingGmu;
            public double RequestedDurationSeconds, ActualDurationSeconds, ExtraCaptureTotalMilliseconds, ExtraCaptureMaximumMilliseconds;
            public Vector3 InjectionPosition;
            public FrameStatistics FrameTimes;
            public Sample[] Samples;
            public Injection[] Injections;
        }
    }
}
