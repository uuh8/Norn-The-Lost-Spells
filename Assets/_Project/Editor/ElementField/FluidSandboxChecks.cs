using System;
using Game.Core;
using Game.ElementField;
using Game.Rendering;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>
    /// Task 13 的 Sandbox 实机 Gate：让真实 Runtime/Compute/Renderer 连续运行一分钟，
    /// 再读取 Development Async Counters 与 ProfilerRecorder。它只负责留证，不替代 10 分钟人工 Matrix。
    /// </summary>
    [InitializeOnLoad]
    internal static class FluidSandboxChecks
    {
        private const string ScenePath = "Assets/_Project/Scenes/PBF_FluidSandbox.unity";
        private const string RunningKey = "Game.PbfTask13.SandboxGate.Running";
        private const string StartTimeKey = "Game.PbfTask13.SandboxGate.StartTime";
        private const double DurationSeconds = 60d;
        private const double WarmupSeconds = 10d;

        private static ProfilerRecorder _gcRecorder;
        private static ProfilerRecorder _simulationRecorder;
        private static long _maximumGcBytes;
        private static long _maximumSimulationNanoseconds;
        private static long _simulationNanosecondsTotal;
        private static long _simulationSampleCount;

        static FluidSandboxChecks()
        {
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        [MenuItem("Tools/PBF Fluid/Run 60 Second Sandbox Gate")]
        private static void StartSandboxGate()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += StartSandboxGate;
                return;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            SessionState.SetBool(RunningKey, true);
            SessionState.SetFloat(StartTimeKey, 0f);
            EditorApplication.EnterPlaymode();
        }

        /// <summary>
        /// CI/agent 可调用入口。不要配合 -quit：Gate 会在 60 秒取证并回到 EditMode 后自行退出 Batchmode。
        /// </summary>
        public static void RunAuthorizedPhaseFGate()
        {
            StartSandboxGate();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(RunningKey, false))
                return;

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                SessionState.SetFloat(StartTimeKey, (float)EditorApplication.timeSinceStartup);
                _maximumGcBytes = 0;
                _maximumSimulationNanoseconds = 0;
                _simulationNanosecondsTotal = 0;
                _simulationSampleCount = 0;
                _gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
                _simulationRecorder = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts,
                    "GpuFluid.SpatialHash.Build",
                    1);
                GameLog.Info("[PBF-PHASE-F-SANDBOX] START Duration=60s Config=4K/Anisotropic", "Editor");
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                DisposeRecorders();
                SessionState.SetBool(RunningKey, false);
                SessionState.SetFloat(StartTimeKey, 0f);
                GameLog.Info("[PBF-PHASE-F-SANDBOX] FINISHED", "Editor");
                if (Application.isBatchMode)
                    EditorApplication.Exit(0);
            }
        }

        private static void Update()
        {
            if (!SessionState.GetBool(RunningKey, false) || !EditorApplication.isPlaying)
                return;

            double startTime = SessionState.GetFloat(StartTimeKey, 0f);
            if (startTime <= 0d)
                return;

            double elapsed = EditorApplication.timeSinceStartup - startTime;
            if (elapsed >= WarmupSeconds)
                AccumulateProfilerSamples();
            if (elapsed >= DurationSeconds)
            {
                RecordRuntimeEvidence();
                EditorApplication.ExitPlaymode();
            }
        }

        private static void AccumulateProfilerSamples()
        {
            if (_gcRecorder.Valid)
                _maximumGcBytes = Math.Max(_maximumGcBytes, _gcRecorder.LastValue);
            if (!_simulationRecorder.Valid || _simulationRecorder.Count == 0)
                return;

            long sample = _simulationRecorder.LastValue;
            _maximumSimulationNanoseconds = Math.Max(_maximumSimulationNanoseconds, sample);
            _simulationNanosecondsTotal += sample;
            _simulationSampleCount++;
        }

        private static void RecordRuntimeEvidence()
        {
            GpuPbfFluidRuntime fluid = Object.FindFirstObjectByType<GpuPbfFluidRuntime>();
            GpuLiquidSurfaceRenderer renderer = Object.FindFirstObjectByType<GpuLiquidSurfaceRenderer>();
            if (fluid == null || renderer == null)
            {
                GameLog.Error("[PBF-PHASE-F-SANDBOX] Runtime 或 Renderer 缺失。", "Editor");
                return;
            }

            var fluidState = new SerializedObject(fluid);
            var rendererState = new SerializedObject(renderer);
            long averageSimulationNanoseconds = _simulationSampleCount > 0
                ? _simulationNanosecondsTotal / _simulationSampleCount
                : 0;
            uint awake = ReadUInt(fluidState, "_debugAwakeParticleCount");
            uint sleeping = ReadUInt(fluidState, "_debugSleepingParticleCount");
            uint interest = ReadUInt(fluidState, "_debugInterestParticleCount");
            uint rejected = ReadUInt(fluidState, "_debugRejectedParticleCount");
            uint spawnQueueOverflow = ReadUInt(fluidState, "_debugSpawnRequestOverflow");
            int colliderOverflow = ReadInt(fluidState, "_debugColliderOverflow");
            uint reactionOverflow = ReadUInt(fluidState, "_debugReactionQueueOverflow");
            uint triangleOverflow = ReadUInt(rendererState, "_debugTriangleOverflow");
            GameLog.Info(
                "[PBF-PHASE-F-SANDBOX] RESULT "
                + $"Awake={awake} Sleeping={sleeping} Interest={interest} Rejected={rejected} "
                + $"SpawnQueueOverflow={spawnQueueOverflow} ColliderOverflow={colliderOverflow} "
                + $"ReactionOverflow={reactionOverflow} TriangleOverflow={triangleOverflow} "
                + $"MaxGCBytes={_maximumGcBytes} "
                + $"SimulationAvgNs={averageSimulationNanoseconds} "
                + $"SimulationMaxNs={_maximumSimulationNanoseconds}",
                "Editor");
        }

        private static uint ReadUInt(SerializedObject serialized, string name)
        {
            SerializedProperty property = serialized.FindProperty(name)
                ?? throw new InvalidOperationException($"缺少计数器 {name}。");
            return property.uintValue;
        }

        private static int ReadInt(SerializedObject serialized, string name)
        {
            SerializedProperty property = serialized.FindProperty(name)
                ?? throw new InvalidOperationException($"缺少计数器 {name}。");
            return property.intValue;
        }

        private static void DisposeRecorders()
        {
            if (_gcRecorder.Valid)
                _gcRecorder.Dispose();
            if (_simulationRecorder.Valid)
                _simulationRecorder.Dispose();
        }
    }
}
