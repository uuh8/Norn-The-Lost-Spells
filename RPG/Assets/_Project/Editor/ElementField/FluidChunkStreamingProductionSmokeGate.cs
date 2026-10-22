using System;
using System.IO;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>
    /// 对 P7/P8 保存场景执行生产链 Smoke：真实初始化后注入 Water/Poison/Sticky 与 Fire，
    /// 确认写入仍到达 GPU 池且 Streaming 没有错误。完整跨区数量守恒由独立 Sandbox Gate 负责。
    /// </summary>
    [InitializeOnLoad]
    internal static class FluidChunkStreamingProductionSmokeGate
    {
        private static readonly string[] Scenes =
        {
            "Assets/_Project/Scenes/P7_DemoRun.unity",
            "Assets/_Project/Scenes/P8_BossField.unity",
        };
        private const string RequestPath = "Temp/ElementPerformance/run-streaming-production-smoke.request";
        private const string ResultPath = "Temp/ElementPerformance/streaming-production-smoke-result.txt";
        private const string RunningKey = "Game.ElementStreaming.ProductionSmoke.Running";
        private const string SceneIndexKey = "Game.ElementStreaming.ProductionSmoke.SceneIndex";
        private const string EvidenceKey = "Game.ElementStreaming.ProductionSmoke.Evidence";
        private static int _sceneIndex;
        private static double _startedAt;
        private static int _phase;
        private static int _observedFluidTicks;
        private static int _startFrame;
        private static int _lastObservedFrame;
        private static ulong _submittedAtStart;
        private static ElementWorldRuntime _world;
        private static GpuPbfFluidRuntime _fluid;
        private static FluidChunkStreamingRuntime _streaming;
        private static Transform _interest;
        private static string _evidence;
        private static double _nextPoll;
        private static double _nextResumeAttempt;

        static FluidChunkStreamingProductionSmokeGate()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void Update()
        {
            if (!SessionState.GetBool(RunningKey, false))
            {
                if (EditorApplication.timeSinceStartup < _nextPoll) return;
                _nextPoll = EditorApplication.timeSinceStartup + 1d;
                if (!File.Exists(RequestPath) || EditorApplication.isCompiling
                    || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                File.Delete(RequestPath);
                _sceneIndex = 0;
                _evidence = string.Empty;
                SessionState.SetInt(SceneIndexKey, 0);
                SessionState.SetString(EvidenceKey, string.Empty);
                SessionState.SetBool(RunningKey, true);
                OpenAndPlay();
                return;
            }
            if (!EditorApplication.isPlaying)
            {
                // OpenScene + EnterPlaymode 之间若发生 Domain Reload，原调用可能被 Unity 丢弃。
                // SessionState 保留进度，并在 Editor 回到稳定 EditMode 后重新发起，不需人工点 Play。
                if (!EditorApplication.isCompiling && !EditorApplication.isUpdating
                    && !EditorApplication.isPlayingOrWillChangePlaymode
                    && EditorApplication.timeSinceStartup >= _nextResumeAttempt)
                {
                    _nextResumeAttempt = EditorApplication.timeSinceStartup + 2d;
                    _sceneIndex = SessionState.GetInt(SceneIndexKey, 0);
                    if (EditorSceneManager.GetActiveScene().path == Scenes[_sceneIndex])
                        EditorApplication.EnterPlaymode();
                    else
                        OpenAndPlay();
                }
                return;
            }
            if (_world == null) return;
            if (EditorApplication.isPaused) EditorApplication.isPaused = false;
            if (Time.timeScale <= 0f) Time.timeScale = 1f;
            double elapsed = EditorApplication.timeSinceStartup - _startedAt;
            try
            {
                if (_phase == 0 && elapsed >= 1d)
                {
                    Vector3 center = _interest.position + Vector3.up * 1.5f;
                    Submit(center + Vector3.left, MaterialId.Water, 64);
                    Submit(center, MaterialId.Poison, 64);
                    Submit(center + Vector3.right, MaterialId.Sticky, 64);
                    _phase = 1;
                }
                else if (_phase == 1 && elapsed >= 3d)
                {
                    Submit(_interest.position, MaterialId.Fire, 64);
                    _phase = 2;
                }
                else if (_phase == 2 && _fluid.SubmittedParticleCount > _submittedAtStart
                    && _fluid.PoolDiagnostics.Valid)
                {
                    bool valid = _world.IsInitialized && _fluid.IsFluidInitialized
                        && _streaming.IsFluidInitialized
                        && _fluid.SubmittedParticleCount > _submittedAtStart
                        && _streaming.ArchiveReadbackErrorCount == 0u
                        && _streaming.RestoreReadbackErrorCount == 0u
                        && _fluid.PoolDiagnostics.Valid
                        && _fluid.PoolDiagnostics.Dropped == 0u;
                    _evidence += $"{Scenes[_sceneIndex]} Success={valid} "
                        + $"SubmittedDelta={_fluid.SubmittedParticleCount - _submittedAtStart} "
                        + $"Alive={_fluid.PoolDiagnostics.Alive} Free={_fluid.PoolDiagnostics.Free} "
                        + $"Dropped={_fluid.PoolDiagnostics.Dropped} Reactions={_world.ReactionPairCount}\n";
                    SessionState.SetString(EvidenceKey, _evidence);
                    if (!valid) throw new InvalidOperationException("生产场景 Smoke counters failed.");
                    EditorApplication.ExitPlaymode();
                }
                else if ((_lastObservedFrame - _startFrame >= 600)
                    || (elapsed >= 180d && _lastObservedFrame - _startFrame >= 30))
                {
                    throw new TimeoutException(
                        $"生产场景 Smoke 等待 GPU 提交超时。FluidEnabled={_fluid.isActiveAndEnabled}, "
                        + $"StreamingEnabled={_streaming.isActiveAndEnabled}, FluidTicks={_observedFluidTicks}, "
                        + $"PendingSpawn={_fluid.PendingSpawnRequestCount}, Submitted="
                        + $"{_fluid.SubmittedParticleCount - _submittedAtStart}, "
                        + $"PoolValid={_fluid.PoolDiagnostics.Valid}, TimeScale={Time.timeScale}。");
                }

                // EditorApplication.update 在同一个 Game Frame 内可能执行多次；只累计一次，
                // 否则重场景卡帧时会把同一个 LastFrameTickCount 重复计入并制造假进度。
                if (Time.frameCount != _lastObservedFrame)
                {
                    _lastObservedFrame = Time.frameCount;
                    _observedFluidTicks += _fluid.LastFrameTickCount;
                }
            }
            catch (Exception exception)
            {
                Complete(false, exception.ToString());
            }
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(RunningKey, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                _sceneIndex = SessionState.GetInt(SceneIndexKey, 0);
                _evidence = SessionState.GetString(EvidenceKey, string.Empty);
                EditorApplication.isPaused = false;
                Application.runInBackground = true;
                Time.timeScale = 1f;
                _world = Object.FindFirstObjectByType<ElementWorldRuntime>();
                // 生产场景可能同时保留 Debug Runtime；沿 World 的序列化引用取真正的数据通路，
                // 避免 FindFirstObjectByType 误把未接线实例当成验收对象。
                if (_world != null)
                {
                    var worldSerialized = new SerializedObject(_world);
                    _fluid = worldSerialized.FindProperty("_gpuPbfFluidRuntime")
                        .objectReferenceValue as GpuPbfFluidRuntime;
                    _streaming = worldSerialized.FindProperty("_fluidChunkStreamingRuntime")
                        .objectReferenceValue as FluidChunkStreamingRuntime;
                }
                if (_world == null || _fluid == null || _streaming == null || !_world.IsInitialized)
                {
                    Complete(false, Scenes[_sceneIndex] + " initialization failed");
                    return;
                }
                _interest = new SerializedObject(_world).FindProperty("_interestPoint")
                    .objectReferenceValue as Transform;
                if (_interest == null)
                {
                    Complete(false, Scenes[_sceneIndex] + " interest missing");
                    return;
                }
                _submittedAtStart = _fluid.SubmittedParticleCount;
                _observedFluidTicks = 0;
                _startFrame = Time.frameCount;
                _lastObservedFrame = _startFrame;
                _phase = 0;
                _startedAt = EditorApplication.timeSinceStartup;
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                _world = null; _fluid = null; _streaming = null; _interest = null;
                _sceneIndex = SessionState.GetInt(SceneIndexKey, 0) + 1;
                SessionState.SetInt(SceneIndexKey, _sceneIndex);
                if (_sceneIndex < Scenes.Length) OpenAndPlay();
                else Complete(true, "P7/P8 completed");
            }
        }

        private static void OpenAndPlay()
        {
            _sceneIndex = SessionState.GetInt(SceneIndexKey, 0);
            EditorSceneManager.OpenScene(Scenes[_sceneIndex], OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        private static void Submit(Vector3 position, MaterialId material, ushort amount)
        {
            var request = new ElementWriteRequest(position, material, amount, .4f, false,
                Vector3.zero, Vector3.up);
            if (!_world.TryEnqueueWrite(in request))
                throw new InvalidOperationException(Scenes[_sceneIndex] + " rejected " + material);
        }

        private static void Complete(bool success, string reason)
        {
            Directory.CreateDirectory("Temp/ElementPerformance");
            _evidence = SessionState.GetString(EvidenceKey, _evidence ?? string.Empty);
            string result = $"Success={success}\nReason={reason}\n{_evidence}";
            File.WriteAllText(ResultPath, result);
            if (success) GameLog.Info("[ELEMENT-STREAMING-PRODUCTION] " + result, "Editor");
            else GameLog.Error("[ELEMENT-STREAMING-PRODUCTION] " + result, "Editor");
            SessionState.SetBool(RunningKey, false);
            SessionState.EraseInt(SceneIndexKey);
            SessionState.EraseString(EvidenceKey);
            if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.ExitPlaymode();
        }
    }
}
