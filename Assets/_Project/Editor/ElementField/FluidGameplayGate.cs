using System;
using System.Collections.Generic;
using System.IO;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Skills;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>
    /// Phase F 的真实 Gameplay Smoke：Editor 只负责搭建和采样，实际结果仍经过
    /// SpellCaster -> Projectile Physics -> Element Deposit -> GPU Snapshot -> Exposure -> Status。
    /// </summary>
    [InitializeOnLoad]
    internal static class FluidGameplayGate
    {
        private const string ScenePath = "Assets/_Project/Scenes/ElementWorld_Performance.unity";
        private const string WandPath =
            "Assets/_Project/ScriptableObjects/Spell/Programs/Validation/Wand_FluidWater.asset";
        private const string RequestPath = "Temp/ElementPerformance/run-phase-f-gameplay.request";
        private const string ResultPath = "Temp/ElementPerformance/phase-f-gameplay-result.json";
        private const string StateKey = "ElementPerformance.PhaseFGameplay.State";
        private const string OpenedAdditiveKey = "ElementPerformance.PhaseFGameplay.OpenedAdditive";
        private const string PreviousSceneHandleKey = "ElementPerformance.PhaseFGameplay.PreviousSceneHandle";
        private const string OwnsRunInBackgroundKey =
            "ElementPerformance.PhaseFGameplay.OwnsRunInBackground";
        private const string PreviousRunInBackgroundKey =
            "ElementPerformance.PhaseFGameplay.PreviousRunInBackground";
        private const string PreviousTargetFrameRateKey =
            "ElementPerformance.PhaseFGameplay.PreviousTargetFrameRate";
        private const string PreviousVSyncCountKey =
            "ElementPerformance.PhaseFGameplay.PreviousVSyncCount";
        private const string PreviousFocusedWindowTypeKey =
            "ElementPerformance.PhaseFGameplay.PreviousFocusedWindowType";
        private static readonly StatusKind[] Statuses =
            { StatusKind.Burning, StatusKind.Poisoned, StatusKind.Sticky };
        private static readonly Sample[] Samples = new Sample[3];
        private static Scene _gateScene;
        private static Scene _previousActiveScene;
        private static bool _openedAdditive;
        private static GateState _state;
        private static double _enteredAt;
        private static double _enteredGameTime;
        private static int _enteredFrame;
        private static int _pauseCorrectionCount;
        private static int _timeScaleCorrectionCount;
        private static ElementWorldRuntime _world;
        private static FluidGameplayOccupancyBridge _gameplayOccupancy;
        private static GameObject _casterObject;

        static FluidGameplayGate()
        {
            _state = (GateState)SessionState.GetInt(StateKey, (int)GateState.Idle);
            _openedAdditive = SessionState.GetBool(OpenedAdditiveKey, false);
            int previousHandle = SessionState.GetInt(PreviousSceneHandleKey, 0);
            if (previousHandle != 0) _previousActiveScene = FindSceneByHandle(previousHandle);
            if (_state != GateState.Idle) _gateScene = SceneManager.GetSceneByPath(ScenePath);
            EditorApplication.update += Poll;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.pauseStateChanged += OnPauseStateChanged;
        }

        [MenuItem("Tools/Element Performance/Run Fluid Gameplay Gate")]
        private static void RunFromMenu() => Begin();

        private static void Poll()
        {
            // Enter/Exit Play Mode 都可能触发 Domain Reload；SessionState 保存阶段，Poll 负责在
            // Unity 没有派发边沿回调时恢复推进，防止自动 Gate 永久停在切换边界。
            if (_state == GateState.EnteringPlay)
            {
                if (EditorApplication.isPlaying) EnterRuntimeObservation();
                else if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorApplication.EnterPlaymode();
                return;
            }
            if (_state == GateState.ExitingPlay && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                RestoreEditorScene();
                return;
            }
            if (_state == GateState.Idle)
            {
                if (!File.Exists(RequestPath) || EditorApplication.isCompiling
                    || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                    return;
                File.Delete(RequestPath);
                Begin();
                return;
            }
            if (EditorApplication.isPlaying)
            {
                // Console Error Pause 或前一轮UI暂停会让 Rigidbody 与Status只推进一帧，
                // 产生“三个水球同时不落地”的假性失败。Gate必须固定自己的测量时钟条件。
                if (EditorApplication.isPaused) EditorApplication.isPaused = false;
                if (!float.IsFinite(Time.timeScale) || Time.timeScale <= 0f)
                {
                    Time.timeScale = 1f;
                    _timeScaleCorrectionCount++;
                }
            }
            if (_state == GateState.WaitRuntime) TryStartCases();
            else if (_state == GateState.Observe) ObserveCases();
        }

        private static void Begin()
        {
            if (_state != GateState.Idle || EditorApplication.isPlayingOrWillChangePlaymode) return;
            Directory.CreateDirectory("Temp/ElementPerformance");
            if (File.Exists(ResultPath)) File.Delete(ResultPath);
            _previousActiveScene = SceneManager.GetActiveScene();
            _gateScene = SceneManager.GetSceneByPath(ScenePath);
            _openedAdditive = !_gateScene.IsValid() || !_gateScene.isLoaded;
            if (_openedAdditive)
                _gateScene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            SceneManager.SetActiveScene(_gateScene);
            SessionState.SetBool(OpenedAdditiveKey, _openedAdditive);
            SessionState.SetInt(PreviousSceneHandleKey, _previousActiveScene.handle);
            SetState(GateState.EnteringPlay);
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode
                && _state == GateState.EnteringPlay)
            {
                EnterRuntimeObservation();
            }
            else if (change == PlayModeStateChange.EnteredEditMode
                && _state == GateState.ExitingPlay)
            {
                RestoreEditorScene();
            }
        }

        private static void EnterRuntimeObservation()
        {
            _gateScene = SceneManager.GetSceneByPath(ScenePath);
            // Lab稍后会被禁用并恢复它自己的设置；Gate需独立持有后台运行，
            // 否则Unity失焦时Editor计时继续，而Rigidbody/Status只推进一帧。
            SessionState.SetBool(PreviousRunInBackgroundKey, Application.runInBackground);
            SessionState.SetInt(PreviousTargetFrameRateKey, Application.targetFrameRate);
            SessionState.SetInt(PreviousVSyncCountKey, QualitySettings.vSyncCount);
            EditorWindow previousFocused = EditorWindow.focusedWindow;
            SessionState.SetString(
                PreviousFocusedWindowTypeKey,
                previousFocused != null ? previousFocused.GetType().AssemblyQualifiedName : string.Empty);
            SessionState.SetBool(OwnsRunInBackgroundKey, true);
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            FocusGameView();
            // Error Pause 可能与EnteredPlayMode同一边界发生，不能等下一次Poll才恢复。
            if (EditorApplication.isPaused) EditorApplication.isPaused = false;
            if (!float.IsFinite(Time.timeScale) || Time.timeScale <= 0f) Time.timeScale = 1f;
            _enteredFrame = Time.frameCount;
            _pauseCorrectionCount = 0;
            _timeScaleCorrectionCount = 0;
            SetState(GateState.WaitRuntime);
            _enteredAt = Time.realtimeSinceStartupAsDouble;
            _enteredGameTime = Time.timeAsDouble;
        }

        private static void OnPauseStateChanged(PauseState pauseState)
        {
            if (pauseState != PauseState.Paused || !EditorApplication.isPlaying
                || _state == GateState.Idle || _state == GateState.ExitingPlay)
                return;
            // 直接处理Pause边沿，避免重复错误在Poll之后重新暂停物理一整轮。
            _pauseCorrectionCount++;
            EditorApplication.isPaused = false;
        }

        private static void RestoreEditorScene()
        {
            if (SessionState.GetBool(OwnsRunInBackgroundKey, false))
            {
                Application.runInBackground = SessionState.GetBool(
                    PreviousRunInBackgroundKey, false);
                Application.targetFrameRate = SessionState.GetInt(
                    PreviousTargetFrameRateKey, -1);
                QualitySettings.vSyncCount = SessionState.GetInt(PreviousVSyncCountKey, 0);
                RestorePreviousEditorWindow();
                SessionState.EraseBool(OwnsRunInBackgroundKey);
                SessionState.EraseBool(PreviousRunInBackgroundKey);
                SessionState.EraseInt(PreviousTargetFrameRateKey);
                SessionState.EraseInt(PreviousVSyncCountKey);
                SessionState.EraseString(PreviousFocusedWindowTypeKey);
            }
            if (_previousActiveScene.IsValid() && _previousActiveScene.isLoaded)
                SceneManager.SetActiveScene(_previousActiveScene);
            if (_openedAdditive)
            {
                Scene scene = SceneManager.GetSceneByPath(ScenePath);
                if (scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
            SetState(GateState.Idle);
            SessionState.EraseBool(OpenedAdditiveKey);
            SessionState.EraseInt(PreviousSceneHandleKey);
        }

        private static void TryStartCases()
        {
            _world = FindInGateScene<ElementWorldRuntime>();
            if (_world == null || !_world.IsInitialized)
            {
                if (Time.realtimeSinceStartupAsDouble - _enteredAt > 10d)
                    Finish(false, "ElementWorldRuntime 10 秒内未初始化。");
                return;
            }
            _gameplayOccupancy = FindInGateScene<FluidGameplayOccupancyBridge>();
            if (_gameplayOccupancy == null)
            {
                Finish(false, "性能场景缺少 FluidGameplayOccupancyBridge。");
                return;
            }

            ElementWorldPerformanceLab lab = FindInGateScene<ElementWorldPerformanceLab>();
            if (lab != null) lab.enabled = false;
            // Lab.OnDisable 会恢复它持有的设置。必须在该生命周期回调之后重新取得 Gate 的测量设置。
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            WandLoadout wand = AssetDatabase.LoadAssetAtPath<WandLoadout>(WandPath);
            if (wand == null) { Finish(false, "缺少 Water Validation Wand。"); return; }

            StatusController[] all = UnityEngine.Object.FindObjectsByType<StatusController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var targets = new List<StatusController>(all.Length);
            for (int i = 0; i < all.Length; i++)
                if (all[i].gameObject.scene == _gateScene
                    && all[i].name.StartsWith("PerformanceTarget_", StringComparison.Ordinal))
                    targets.Add(all[i]);
            targets.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            if (targets.Count < 6) { Finish(false, "性能场景少于 6 个 Status Target。"); return; }

            for (int i = 0; i < targets.Count; i++) targets[i].gameObject.SetActive(false);
            // 在全部测试靶之间均匀取样，既避免水池互相污染，也不把 Gate 绑定到固定的场景数量。
            int[] picks = new int[Samples.Length * 2];
            for (int i = 0; i < picks.Length; i++)
                picks[i] = Mathf.RoundToInt(i * (targets.Count - 1f) / (picks.Length - 1f));
            _casterObject = new GameObject("PhaseF Real SpellCaster");
            SceneManager.MoveGameObjectToScene(_casterObject, _gateScene);
            SpellCaster caster = _casterObject.AddComponent<SpellCaster>();

            double castAt = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < Samples.Length; i++)
            {
                StatusController dry = targets[picks[i * 2]];
                StatusController wet = targets[picks[i * 2 + 1]];
                dry.gameObject.SetActive(true);
                wet.gameObject.SetActive(true);
                dry.ApplyStatus(Statuses[i], 80f, _casterObject.GetInstanceID(), 1);
                wet.ApplyStatus(Statuses[i], 80f, _casterObject.GetInstanceID(), 1);
                Samples[i] = new Sample(Statuses[i], dry, wet, castAt);

                Vector3 aim = wet.transform.position + Vector3.up * 0.1f;
                Vector3 spawn = aim + Vector3.up * 5f;
                int emitted = caster.CastProgram(
                    wand, spawn, aim, 1, _casterObject.GetInstanceID(), null,
                    SpellManaPolicy.IgnoreMana);
                if (emitted != 1) { Finish(false, $"{Statuses[i]} Water Wand 产出 {emitted}，预期 1。"); return; }
                ElementOrbProjectile projectile = FindNewestUnclaimedProjectile();
                if (projectile == null) { Finish(false, $"{Statuses[i]} 未生成真实 Water Projectile。"); return; }
                ProjectileStatusOnImpact direct = projectile.GetComponent<ProjectileStatusOnImpact>();
                if (direct != null) direct.enabled = false;
                int sampleIndex = i;
                projectile.SurfaceImpacted += context => RecordImpact(sampleIndex, context);
                Samples[i].Projectile = projectile;
            }
            _enteredAt = castAt;
            // 帧数、游戏时间和真实时间必须来自同一施法边界，不能混入初始化阶段的帧数。
            _enteredGameTime = Time.timeAsDouble;
            _enteredFrame = Time.frameCount;
            SetState(GateState.Observe);
        }

        private static ElementOrbProjectile FindNewestUnclaimedProjectile()
        {
            ElementOrbProjectile[] projectiles = UnityEngine.Object.FindObjectsByType<ElementOrbProjectile>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < projectiles.Length; i++)
            {
                bool used = false;
                for (int j = 0; j < Samples.Length; j++)
                    if (Samples[j].Projectile == projectiles[i]) { used = true; break; }
                if (!used && projectiles[i].gameObject.scene == _gateScene) return projectiles[i];
            }
            return null;
        }

        private static void RecordImpact(int index, ProjectileImpactContext context)
        {
            Sample sample = Samples[index];
            sample.ImpactAt = Time.realtimeSinceStartupAsDouble;
            sample.ImpactPoint = context.Point;
            Samples[index] = sample;
        }

        private static void ObserveCases()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            bool allObserved = true;
            for (int i = 0; i < Samples.Length; i++)
            {
                Sample sample = Samples[i];
                if (sample.ImpactAt > 0d)
                {
                    Vector3Int cell = ElementWorldCoordinates.WorldToGlobalCell(
                        sample.ImpactPoint, _world.Origin, _world.CellSize);
                    if (sample.WaterAt <= 0d
                        && _world.MaterialAmounts.TryGetAmount(cell, Game.Materials.MaterialId.Water, out byte water)
                        && water > 0)
                    {
                        sample.WaterAt = now;
                        sample.WaterAmount = water;
                        FluidGameplayDiagnostics diagnostics = _gameplayOccupancy.Diagnostics;
                        sample.WaterSnapshotVersion = diagnostics.SnapshotVersion;
                        sample.WaterSnapshotRequestedAt = diagnostics.RequestedAt;
                        sample.WaterSnapshotCompletedAt = diagnostics.CompletedAt;
                        sample.WaterSnapshotPublishedAt = diagnostics.PublishedAt;
                    }
                }
                if (sample.WetAt <= 0d && sample.Wet.GetIntensity(StatusKind.Wet) > 0f)
                    sample.WetAt = now;
                Samples[i] = sample;
                allObserved &= sample.ImpactAt > 0d && sample.WaterAt > 0d && sample.WetAt > 0d;
            }
            bool allCleaned = true;
            for (int i = 0; i < Samples.Length; i++)
                allCleaned &= Samples[i].Wet.GetIntensity(Samples[i].Status) + 0.01f
                    < Samples[i].Dry.GetIntensity(Samples[i].Status);
            // Wet 的首次出现不保证 Combat 已执行下一次反应。仍保留首次时间戳与6秒失败上限，
            // 只延后结果收集，不能靠延长等待修改 Impact→Wet 的既定1秒门槛。
            if ((allObserved && allCleaned && now - _enteredAt >= 2d) || now - _enteredAt >= 6d)
                CompleteResults(now);
        }

        private static void CompleteResults(double now)
        {
            var report = new GateReport
            {
                Passed = true,
                Error = string.Empty,
                RuntimeFrames = Time.frameCount - _enteredFrame,
                RealtimeSeconds = now - _enteredAt,
                GameTimeSeconds = Time.timeAsDouble - _enteredGameTime,
                ObservedAverageFps = (Time.frameCount - _enteredFrame) / Math.Max(0.000001d, now - _enteredAt),
                CaptureFramerate = Time.captureFramerate,
                MaximumDeltaTime = Time.maximumDeltaTime,
                ScreenWidth = Screen.width,
                ScreenHeight = Screen.height,
                PauseCorrections = _pauseCorrectionCount,
                TimeScaleCorrections = _timeScaleCorrectionCount,
                FinalTimeScale = Time.timeScale,
                TargetFrameRate = Application.targetFrameRate,
                VSyncCount = QualitySettings.vSyncCount,
                ApplicationFocused = Application.isFocused,
                FocusedEditorWindow = EditorWindow.focusedWindow != null
                    ? EditorWindow.focusedWindow.GetType().FullName
                    : string.Empty,
                Samples = new SampleReport[Samples.Length]
            };
            for (int i = 0; i < Samples.Length; i++)
            {
                Sample sample = Samples[i];
                float dryFinal = sample.Dry.GetIntensity(sample.Status);
                float wetFinal = sample.Wet.GetIntensity(sample.Status);
                bool passed = sample.ImpactAt > 0d && sample.WaterAt > 0d && sample.WetAt > 0d
                    && sample.WaterAt - sample.ImpactAt <= 1d
                    && sample.WetAt - sample.ImpactAt <= 1d
                    && wetFinal + 0.01f < dryFinal;
                report.Passed &= passed;
                report.Samples[i] = new SampleReport
                {
                    Status = sample.Status.ToString(), Passed = passed,
                    CastToImpactSeconds = sample.ImpactAt - sample.CastAt,
                    ImpactToWaterSeconds = sample.WaterAt - sample.ImpactAt,
                    ImpactToWetSeconds = sample.WetAt - sample.ImpactAt,
                    ImpactToSnapshotRequestSeconds = sample.WaterSnapshotRequestedAt - sample.ImpactAt,
                    ImpactToSnapshotCompleteSeconds = sample.WaterSnapshotCompletedAt - sample.ImpactAt,
                    ImpactToSnapshotPublishSeconds = sample.WaterSnapshotPublishedAt - sample.ImpactAt,
                    SnapshotPublishToObservationSeconds = sample.WaterAt - sample.WaterSnapshotPublishedAt,
                    WaterSnapshotVersion = sample.WaterSnapshotVersion,
                    WaterAmount = sample.WaterAmount,
                    DryFinal = dryFinal, WetFinal = wetFinal,
                    ObservedSeconds = now - sample.CastAt,
                };
            }
            WriteReport(report);
            Finish(report.Passed, report.Passed ? string.Empty : "一个或多个真实投射物清洗样本未达标。", false);
        }

        private static void Finish(bool passed, string error, bool write = true)
        {
            if (write) WriteReport(new GateReport { Passed = passed, Error = error, Samples = Array.Empty<SampleReport>() });
            if (_casterObject != null) UnityEngine.Object.Destroy(_casterObject);
            SetState(GateState.ExitingPlay);
            EditorApplication.ExitPlaymode();
        }

        private static void SetState(GateState state)
        {
            _state = state;
            SessionState.SetInt(StateKey, (int)state);
            GameLog.Info($"[PHASE-F-GAMEPLAY] State={state}", "Editor");
        }

        private static void WriteReport(GateReport report)
        {
            Directory.CreateDirectory("Temp/ElementPerformance");
            File.WriteAllText(ResultPath, JsonUtility.ToJson(report, true));
        }

        private static T FindInGateScene<T>() where T : Component
        {
            T[] values = UnityEngine.Object.FindObjectsByType<T>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < values.Length; i++)
                if (values[i].gameObject.scene == _gateScene) return values[i];
            return null;
        }

        private static Scene FindSceneByHandle(int handle)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.handle == handle) return scene;
            }
            return default;
        }

        private static void FocusGameView()
        {
            Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null)
                return;

            EditorWindow gameView = EditorWindow.GetWindow(gameViewType, false, "Game", true);
            gameView?.Focus();
        }

        private static void RestorePreviousEditorWindow()
        {
            string typeName = SessionState.GetString(PreviousFocusedWindowTypeKey, string.Empty);
            if (string.IsNullOrEmpty(typeName))
                return;

            Type windowType = Type.GetType(typeName);
            if (windowType == null)
                return;

            UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(windowType);
            if (windows.Length > 0 && windows[0] is EditorWindow editorWindow)
                editorWindow.Focus();
        }

        private enum GateState { Idle, EnteringPlay, WaitRuntime, Observe, ExitingPlay }

        private struct Sample
        {
            public StatusKind Status;
            public StatusController Dry, Wet;
            public ElementOrbProjectile Projectile;
            public double CastAt, ImpactAt, WaterAt, WetAt;
            public double WaterSnapshotRequestedAt, WaterSnapshotCompletedAt, WaterSnapshotPublishedAt;
            public Vector3 ImpactPoint;
            public uint WaterSnapshotVersion;
            public byte WaterAmount;
            public Sample(StatusKind status, StatusController dry, StatusController wet, double castAt)
            {
                Status = status;
                Dry = dry;
                Wet = wet;
                Projectile = null;
                CastAt = castAt;
                ImpactAt = WaterAt = WetAt = 0d;
                WaterSnapshotRequestedAt = WaterSnapshotCompletedAt = WaterSnapshotPublishedAt = 0d;
                ImpactPoint = default;
                WaterSnapshotVersion = 0u;
                WaterAmount = 0;
            }
        }

        [Serializable] private sealed class GateReport
        {
            public bool Passed;
            public string Error;
            public int RuntimeFrames, PauseCorrections, TimeScaleCorrections;
            // 配置值只说明目标；实际帧数/两种时钟说明本轮是否可作为性能证据。Editor 结果不等同 Player 验收。
            public double RealtimeSeconds, GameTimeSeconds, ObservedAverageFps;
            public int CaptureFramerate, ScreenWidth, ScreenHeight;
            public float MaximumDeltaTime;
            public float FinalTimeScale;
            public int TargetFrameRate, VSyncCount;
            public bool ApplicationFocused;
            public string FocusedEditorWindow;
            public SampleReport[] Samples;
        }
        [Serializable] private sealed class SampleReport
        {
            public string Status;
            public bool Passed;
            public double CastToImpactSeconds, ImpactToWaterSeconds, ImpactToWetSeconds, ObservedSeconds;
            public double ImpactToSnapshotRequestSeconds, ImpactToSnapshotCompleteSeconds;
            public double ImpactToSnapshotPublishSeconds, SnapshotPublishToObservationSeconds;
            public uint WaterSnapshotVersion;
            public int WaterAmount;
            public float DryFinal, WetFinal;
        }
    }
}
