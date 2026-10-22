using System;
using System.Globalization;
using System.IO;
using Game.Combat;
using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 仅由专用性能场景和-ewperfGameplay启用的Player端清洗验证。
    /// Character只观察投射物命中与Combat状态；水写入仍由Prefab上的ElementField Adapter负责，
    /// 因而不增加Character -> ElementField反向依赖。
    /// </summary>
    [AddComponentMenu("")]
    [RequireComponent(typeof(SpellCaster))]
    [DefaultExecutionOrder(1100)]
    public sealed class PhaseFWaterGameplayBenchmark : MonoBehaviour
    {
        private static readonly StatusKind[] Statuses =
            { StatusKind.Burning, StatusKind.Poisoned, StatusKind.Sticky };

        [SerializeField] private WandLoadout _waterWand;
        [SerializeField, Min(0f)] private float _warmupSeconds = 1f;
        [SerializeField, Min(1f)] private float _timeoutSeconds = 6f;

        private readonly Sample[] _samples = new Sample[3];
        private SpellCaster _caster;
        private double _enabledAt, _castAt, _castGameTime;
        private int _castFrame, _quitCountdown = -1, _unfocusedFrames;
        private bool _requested, _started, _finished, _quitOnComplete, _keepAlive;
        private string _condition = "empty";

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-ewperfGameplay") _requested = true;
                if (args[i] == "-ewperfQuit") _quitOnComplete = true;
                if (args[i] == "-ewperfGameplayKeepAlive") _keepAlive = true;
                if (args[i] == "-ewperfGameplayCondition" && i + 1 < args.Length)
                    _condition = args[++i];
                if (args[i] == "-ewperfGameplayDelay" && i + 1 < args.Length)
                {
                    if (!float.TryParse(args[++i], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out _warmupSeconds)
                        || _warmupSeconds < 0f || _warmupSeconds > 600f)
                    {
                        _warmupSeconds = -1f;
                    }
                }
            }
            if (!_requested) { enabled = false; return; }
            if (_warmupSeconds < 0f)
            {
                GameLog.Error("[ELEMENT-GAMEPLAY-PERF] -ewperfGameplayDelay必须在0到600秒之间。",
                    "ElementPerformance");
                if (!Application.isEditor) Application.Quit(1);
                enabled = false;
                return;
            }

            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            if (!Application.isEditor)
                Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
            _caster = GetComponent<SpellCaster>();
            _enabledAt = Time.realtimeSinceStartupAsDouble;
        }

        private void Update()
        {
            if (!_requested)
                return;
            if (!Application.isFocused)
                _unfocusedFrames++;
            if (_quitCountdown >= 0)
            {
                if (_quitCountdown-- == 0 && !Application.isEditor)
                    Application.Quit(_finished ? 0 : 1);
                return;
            }
            if (!_started)
            {
                if (Time.realtimeSinceStartupAsDouble - _enabledAt >= _warmupSeconds)
                    StartCases();
                return;
            }
            if (!_finished)
                ObserveCases();
        }

        private void StartCases()
        {
            if (_waterWand == null || _caster == null)
            {
                Finish(false, "缺少Water Wand或SpellCaster。");
                return;
            }

            StatusController[] all = FindObjectsByType<StatusController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            Array.Sort(all, CompareTargets);
            int targetCount = 0;
            for (int i = 0; i < all.Length; i++)
                if (all[i].name.StartsWith("PerformanceTarget_", StringComparison.Ordinal))
                    all[targetCount++] = all[i];
            if (targetCount < 6)
            {
                Finish(false, "性能场景少于6个Status Target。");
                return;
            }

            for (int i = 0; i < targetCount; i++) all[i].gameObject.SetActive(false);
            _castAt = Time.realtimeSinceStartupAsDouble;
            _castGameTime = Time.timeAsDouble;
            _castFrame = Time.frameCount;
            _unfocusedFrames = 0;
            for (int i = 0; i < _samples.Length; i++)
            {
                int dryIndex = Mathf.RoundToInt(i * 2f * (targetCount - 1) / 5f);
                int wetIndex = Mathf.RoundToInt((i * 2f + 1f) * (targetCount - 1) / 5f);
                StatusController dry = all[dryIndex];
                StatusController wet = all[wetIndex];
                dry.gameObject.SetActive(true);
                wet.gameObject.SetActive(true);
                dry.ApplyStatus(Statuses[i], 80f, gameObject.GetInstanceID(), 1);
                wet.ApplyStatus(Statuses[i], 80f, gameObject.GetInstanceID(), 1);
                _samples[i] = new Sample(Statuses[i], dry, wet);

                Vector3 aim = wet.transform.position + Vector3.up * 0.1f;
                int emitted = _caster.CastProgram(
                    _waterWand, aim + Vector3.up * 5f, aim, 1,
                    gameObject.GetInstanceID(), null, SpellManaPolicy.IgnoreMana);
                if (emitted != 1)
                {
                    Finish(false, $"{Statuses[i]} Water Wand产出{emitted}，预期1。");
                    return;
                }
                ProjectileBase projectile = FindNewestUnclaimedProjectile();
                if (projectile == null)
                {
                    Finish(false, $"{Statuses[i]}没有生成Water Projectile。");
                    return;
                }
                ProjectileStatusOnImpact direct = projectile.GetComponent<ProjectileStatusOnImpact>();
                if (direct != null) direct.enabled = false;
                int sampleIndex = i;
                projectile.SurfaceImpacted += _ => RecordImpact(sampleIndex);
                _samples[i].Projectile = projectile;
            }
            _started = true;
        }

        private void ObserveCases()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            bool allObserved = true;
            bool allCleaned = true;
            for (int i = 0; i < _samples.Length; i++)
            {
                Sample sample = _samples[i];
                if (sample.WetAt <= 0d && sample.Wet.GetIntensity(StatusKind.Wet) > 0f)
                    sample.WetAt = now;
                _samples[i] = sample;
                allObserved &= sample.ImpactAt > 0d && sample.WetAt > 0d;
                allCleaned &= sample.Wet.GetIntensity(sample.Status) + 0.01f
                    < sample.Dry.GetIntensity(sample.Status);
            }
            if ((allObserved && allCleaned && now - _castAt >= 2d)
                || now - _castAt >= _timeoutSeconds)
                Finish(allObserved && allCleaned, string.Empty);
        }

        private void RecordImpact(int index)
        {
            Sample sample = _samples[index];
            if (sample.ImpactAt <= 0d)
                sample.ImpactAt = Time.realtimeSinceStartupAsDouble;
            _samples[index] = sample;
        }

        private ProjectileBase FindNewestUnclaimedProjectile()
        {
            ProjectileBase[] projectiles = FindObjectsByType<ProjectileBase>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < projectiles.Length; i++)
            {
                bool used = false;
                for (int j = 0; j < _samples.Length; j++)
                    if (_samples[j].Projectile == projectiles[i]) { used = true; break; }
                if (!used) return projectiles[i];
            }
            return null;
        }

        private void Finish(bool setupPassed, string error)
        {
            if (_finished) return;
            double now = Time.realtimeSinceStartupAsDouble;
            var report = new BenchmarkReport
            {
                Passed = setupPassed,
                Error = error,
                RuntimeFrames = Time.frameCount - _castFrame,
                RealtimeSeconds = Math.Max(0d, now - _castAt),
                GameTimeSeconds = Math.Max(0d, Time.timeAsDouble - _castGameTime),
                ScreenWidth = Screen.width,
                ScreenHeight = Screen.height,
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                Condition = _condition,
                ApplicationFocusedAtFinish = Application.isFocused,
                TargetFrameRate = Application.targetFrameRate,
                UnfocusedFrames = _unfocusedFrames,
                ObservedAverageFps = now > _castAt
                    ? (Time.frameCount - _castFrame) / (now - _castAt) : 0d,
                Samples = new SampleReport[_samples.Length]
            };
            for (int i = 0; i < _samples.Length; i++)
            {
                Sample sample = _samples[i];
                float dryFinal = sample.Dry != null ? sample.Dry.GetIntensity(sample.Status) : 0f;
                float wetFinal = sample.Wet != null ? sample.Wet.GetIntensity(sample.Status) : 0f;
                bool passed = setupPassed && sample.ImpactAt > 0d && sample.WetAt > 0d
                    && sample.WetAt - sample.ImpactAt <= 1d && wetFinal + 0.01f < dryFinal;
                report.Passed &= passed;
                report.Samples[i] = new SampleReport
                {
                    Status = sample.Status.ToString(), Passed = passed,
                    CastToImpactSeconds = sample.ImpactAt - _castAt,
                    ImpactToWetSeconds = sample.WetAt - sample.ImpactAt,
                    DryFinal = dryFinal, WetFinal = wetFinal
                };
            }

            try
            {
                string parent = Path.Combine(Application.persistentDataPath, "ElementPerformanceGameplay");
                Directory.CreateDirectory(parent);
                string run = "F01-cleansing-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
                Directory.CreateDirectory(Path.Combine(parent, run));
                File.WriteAllText(Path.Combine(parent, run, "result.json"), JsonUtility.ToJson(report, true));
            }
            catch (Exception exception)
            {
                report.Passed = false;
                GameLog.Error($"[ELEMENT-GAMEPLAY-PERF] 导出失败：{exception.Message}", "ElementPerformance");
            }
            _finished = report.Passed;
            // 与F02共跑时由Lab负责完整周期和退出；Harness只导出结果，不能提前截断压力场景。
            _quitCountdown = _quitOnComplete && !_keepAlive ? 8 : -1;
            if (!_quitOnComplete || _keepAlive) enabled = false;
        }

        private static int CompareTargets(StatusController left, StatusController right)
        {
            return string.CompareOrdinal(left != null ? left.name : string.Empty,
                right != null ? right.name : string.Empty);
        }

        private struct Sample
        {
            public StatusKind Status;
            public StatusController Dry, Wet;
            public ProjectileBase Projectile;
            public double ImpactAt, WetAt;
            public Sample(StatusKind status, StatusController dry, StatusController wet)
            {
                Status = status; Dry = dry; Wet = wet; Projectile = null;
                ImpactAt = WetAt = 0d;
            }
        }

        [Serializable] private sealed class BenchmarkReport
        {
            public bool Passed;
            public string Error, GraphicsApi, Condition;
            public bool ApplicationFocusedAtFinish;
            public int RuntimeFrames, ScreenWidth, ScreenHeight, TargetFrameRate, UnfocusedFrames;
            public double RealtimeSeconds, GameTimeSeconds, ObservedAverageFps;
            public SampleReport[] Samples;
        }

        [Serializable] private sealed class SampleReport
        {
            public string Status;
            public bool Passed;
            public double CastToImpactSeconds, ImpactToWetSeconds;
            public float DryFinal, WetFinal;
        }
    }
}
