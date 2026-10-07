using System;
using System.IO;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>
    /// Phase C 的真实 Sandbox 验收：通过生产 ElementWorld 写入六次液体，移动 Interest 触发一次
    /// Archive/Restore 往返，并记录数量守恒与固定 ProfilerMarker。失败注入仍由 GPU/Runtime focused tests
    /// 完成，避免在生产后端留下可误开的破坏性开关。
    /// </summary>
    [InitializeOnLoad] // 请求文件只在 Editor 空闲时消费，避免与 Test Runner/编译阶段交叠。
    internal static class FluidChunkStreamingSandboxGate
    {
        private const string ScenePath = "Assets/_Project/Scenes/PBF_FluidSandbox.unity";
        private const string ProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/ElementWorldProfile_P7_PBF.asset";
        private const string RequestPath = "Temp/ElementPerformance/run-streaming-sandbox.request";
        private const string ResultPath = "Temp/ElementPerformance/streaming-sandbox-result.txt";
        private const string RunningKey = "Game.ElementStreaming.SandboxGate.Running";
        private const double TimeoutSeconds = 40d;

        private static ElementWorldRuntime _world;
        private static GpuPbfFluidRuntime _fluid;
        private static FluidChunkStreamingRuntime _streaming;
        private static Transform _interest;
        private static Vector3 _origin;
        private static double _startedAt;
        private static int _phase;
        private static bool _oldRunInBackground;
        private static bool _ownsRunInBackground;
        private static ulong _firstArchivedParticles;
        private static ulong _restoredParticleBaseline;
        private static uint _archiveCountBaseline;
        private static ProfilerRecorder _gc;
        private static ProfilerRecorder _plan;
        private static ProfilerRecorder _archiveBuild;
        private static ProfilerRecorder _archiveCommit;
        private static ProfilerRecorder _restoreExpand;
        private static ProfilerRecorder _pendingReplay;
        private static long _maxGc;
        private static long _planSamples;
        private static long _archiveBuildSamples;
        private static long _archiveCommitSamples;
        private static long _restoreExpandSamples;
        private static long _pendingReplaySamples;
        private static double _nextPoll;

        static FluidChunkStreamingSandboxGate()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
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
                Start();
                return;
            }
            if (!EditorApplication.isPlaying || _world == null) return;
            // 开发者可能开启 Console Error Pause；上一轮故障不应让自动验收只跑一帧后假性超时。
            if (EditorApplication.isPaused) EditorApplication.isPaused = false;
            if (Time.timeScale <= 0f) Time.timeScale = 1f;

            AccumulateProfilerEvidence();
            double elapsed = EditorApplication.timeSinceStartup - _startedAt;
            try
            {
                if (_phase == 0 && elapsed >= 1d)
                {
                    SubmitThree(_origin + new Vector3(0f, 1.5f, 0f));
                    _phase = 1;
                }
                else if (_phase == 1 && elapsed >= 4d)
                {
                    _archiveCountBaseline = _streaming.CompletedArchiveCount;
                    _restoredParticleBaseline = _streaming.RestoredParticleCount;
                    _interest.position = _origin + new Vector3(48f, 0f, 0f);
                    _phase = 2;
                }
                else if (_phase == 2
                    && _streaming.CompletedArchiveCount > _archiveCountBaseline
                    && _streaming.ArchivedParticleCount > 0u)
                {
                    _firstArchivedParticles = _streaming.ArchivedParticleCount;
                    SubmitThree(_interest.position + new Vector3(0f, 1.5f, 0f));
                    _phase = 3;
                }
                else if (_phase == 3 && elapsed >= 13d)
                {
                    _interest.position = _origin;
                    _phase = 4;
                }
                else if (_phase == 4
                    && _streaming.RestoredParticleCount - _restoredParticleBaseline
                        >= _firstArchivedParticles
                    && _streaming.PendingWriteCount == 0
                    && _fluid.PendingSpawnRequestCount == 0)
                {
                    Finish(true, "restore completed");
                }
                else if (elapsed >= TimeoutSeconds)
                {
                    Finish(false, "timeout waiting for archive/restore");
                }
            }
            catch (Exception exception)
            {
                Finish(false, exception.ToString());
            }
        }

        [MenuItem("Tools/Element Performance/Run Fluid Streaming Sandbox Gate")]
        private static void Start()
        {
            ElementWorldProfile profile = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(ProfilePath)
                ?? throw new InvalidOperationException("缺少生产 ElementWorldProfile。");
            var profileData = new SerializedObject(profile);
            profileData.FindProperty("_enableFluidChunkStreaming").boolValue = true;
            profileData.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            ElementWorldInitialDeposit[] initialDeposits =
                Object.FindObjectsByType<ElementWorldInitialDeposit>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < initialDeposits.Length; i++)
                initialDeposits[i].enabled = false;
            SessionState.SetBool(RunningKey, true);
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(RunningKey, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                EditorApplication.isPaused = false;
                Time.timeScale = 1f;
                // Gate 由外部请求启动时 Unity 通常不在前台。若仍使用项目默认的后台暂停，
                // EditorApplication 时间会前进但 Game Frame 不动，最终制造一次假的 40 秒超时。
                _oldRunInBackground = Application.runInBackground;
                Application.runInBackground = true;
                _ownsRunInBackground = true;
                _world = Object.FindFirstObjectByType<ElementWorldRuntime>();
                _fluid = Object.FindFirstObjectByType<GpuPbfFluidRuntime>();
                _streaming = Object.FindFirstObjectByType<FluidChunkStreamingRuntime>();
                if (_world == null || _fluid == null || _streaming == null || !_world.IsInitialized)
                {
                    Finish(false, "Sandbox streaming runtime failed initialization");
                    return;
                }
                var worldData = new SerializedObject(_world);
                _interest = worldData.FindProperty("_interestPoint").objectReferenceValue as Transform;
                if (_interest == null)
                {
                    Finish(false, "Sandbox interest point is missing");
                    return;
                }
                _origin = _interest.position;
                _phase = 0;
                _firstArchivedParticles = 0;
                _restoredParticleBaseline = 0;
                _archiveCountBaseline = 0;
                _maxGc = _planSamples = _archiveBuildSamples = _archiveCommitSamples = 0;
                _restoreExpandSamples = _pendingReplaySamples = 0;
                _startedAt = EditorApplication.timeSinceStartup;
                _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
                _plan = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "GpuFluid.Streaming.Plan", 1);
                _archiveBuild = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveBuild", 1);
                _archiveCommit = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "GpuFluid.Streaming.ArchiveCommit", 1);
                _restoreExpand = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "GpuFluid.Streaming.RestoreExpand", 1);
                _pendingReplay = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "GpuFluid.Streaming.PendingReplay", 1);
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                RestoreRunInBackground();
                DisposeRecorders();
                SessionState.SetBool(RunningKey, false);
                _world = null; _fluid = null; _streaming = null; _interest = null;
                // 丢弃 Gate 为禁用 Initial Deposit 产生的临时场景状态，磁盘中的 Sandbox 保持原样。
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
        }

        private static void SubmitThree(Vector3 center)
        {
            Submit(center + Vector3.left, MaterialId.Water);
            Submit(center, MaterialId.Poison);
            Submit(center + Vector3.right, MaterialId.Sticky);
        }

        private static void Submit(Vector3 position, MaterialId material)
        {
            var request = new ElementWriteRequest(position, material, 64, .35f, false,
                Vector3.zero, Vector3.up);
            if (!_world.TryEnqueueWrite(in request))
                throw new InvalidOperationException("生产写入队列拒绝 " + material);
        }

        private static void AccumulateProfilerEvidence()
        {
            if (_gc.Valid) _maxGc = Math.Max(_maxGc, _gc.LastValue);
            if (_plan.Valid && _plan.LastValue > 0) _planSamples++;
            if (_archiveBuild.Valid && _archiveBuild.LastValue > 0) _archiveBuildSamples++;
            if (_archiveCommit.Valid && _archiveCommit.LastValue > 0) _archiveCommitSamples++;
            if (_restoreExpand.Valid && _restoreExpand.LastValue > 0) _restoreExpandSamples++;
            if (_pendingReplay.Valid && _pendingReplay.LastValue > 0) _pendingReplaySamples++;
        }

        private static void Finish(bool expectedSuccess, string reason)
        {
            GpuFluidPoolDiagnostics pool = _fluid != null ? _fluid.PoolDiagnostics : default;
            bool countersValid = _streaming != null
                && _streaming.CompletedArchiveCount > 0u
                && _streaming.CompletedRestoreCount > 0u
                && _streaming.ArchiveReadbackErrorCount == 0u
                && _streaming.RestoreReadbackErrorCount == 0u
                // Phase D 会主动取消“当前没有合格 Sleeping 样本”或“槽位快照已过期”的事务并重试；
                // Rollback 是安全的竞争结果，Error、最终恢复、待写入清零与 Dropped 才是 Gate 判据。
                && _streaming.PendingWriteCount == 0
                && _streaming.RestoredParticleCount - _restoredParticleBaseline
                    >= _firstArchivedParticles
                && _firstArchivedParticles > 0u
                && pool.Valid
                && pool.Dropped == 0u;
            bool success = expectedSuccess && countersValid;
            string result = $"Success={success}\nReason={reason}\n"
                + $"FirstArchivedParticles={_firstArchivedParticles}\n"
                + $"CurrentArchivedParticles={_streaming?.ArchivedParticleCount ?? 0}\n"
                + $"CurrentArchivedChunks={_streaming?.ArchivedChunkCount ?? 0}\n"
                + $"RestoredParticles={_streaming?.RestoredParticleCount ?? 0}\n"
                + $"ArchiveCount={_streaming?.CompletedArchiveCount ?? 0}\n"
                + $"RestoreCount={_streaming?.CompletedRestoreCount ?? 0}\n"
                + $"ArchiveErrors={_streaming?.ArchiveReadbackErrorCount ?? 0}\n"
                + $"RestoreErrors={_streaming?.RestoreReadbackErrorCount ?? 0}\n"
                + $"ArchiveRollbacks={_streaming?.ArchiveRollbackCount ?? 0}\n"
                + $"RestoreRollbacks={_streaming?.RestoreRollbackCount ?? 0}\n"
                + $"PoolValid={pool.Valid} Alive={pool.Alive} Free={pool.Free} Dropped={pool.Dropped}\n"
                + $"MaxEditorGCBytes={_maxGc}\n"
                + $"MarkerSamples Plan={_planSamples} ArchiveBuild={_archiveBuildSamples} "
                + $"ArchiveCommit={_archiveCommitSamples} RestoreExpand={_restoreExpandSamples} "
                + $"PendingReplay={_pendingReplaySamples}\n";
            Directory.CreateDirectory("Temp/ElementPerformance");
            File.WriteAllText(ResultPath, result);
            if (success) GameLog.Info("[ELEMENT-STREAMING-SANDBOX] " + result, "Editor");
            else GameLog.Error("[ELEMENT-STREAMING-SANDBOX] " + result, "Editor");
            DisposeRecorders();
            EditorApplication.ExitPlaymode();
        }

        private static void RestoreRunInBackground()
        {
            if (!_ownsRunInBackground) return;
            Application.runInBackground = _oldRunInBackground;
            _ownsRunInBackground = false;
        }

        private static void DisposeRecorders()
        {
            if (_gc.Valid) _gc.Dispose();
            if (_plan.Valid) _plan.Dispose();
            if (_archiveBuild.Valid) _archiveBuild.Dispose();
            if (_archiveCommit.Valid) _archiveCommit.Dispose();
            if (_restoreExpand.Valid) _restoreExpand.Dispose();
            if (_pendingReplay.Valid) _pendingReplay.Dispose();
        }
    }
}
