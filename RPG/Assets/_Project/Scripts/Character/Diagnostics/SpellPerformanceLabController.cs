using System;
using Game.Combat;
using Game.Core;
using Unity.Profiling;
using UnityEngine;

namespace Game.Character
{
    public enum SpellPerformanceRunMode : byte
    {
        StandardLadder = 0,
        Custom = 1
    }

    public enum SpellPerformanceRunState : byte
    {
        Idle = 0,
        Filling = 1,
        Holding = 2,
        Completed = 3
    }

    [Serializable]
    public sealed class SpellPerformanceScenario
    {
        public string Name = "Scenario";
        public bool Enabled = true;
        public SpellCaster Caster;
        public Transform SpawnPoint;
        public Transform AimTarget;
        public byte Team = 1;
        public int AttackerId = 1;
        public Collider CasterCollider;
    }

    public class SpellPerformanceLabController : MonoBehaviour
    {
        [Header("Scenario")]
        [SerializeField] private SpellPerformanceScenario[] _scenarios;
        [SerializeField, Min(0)] private int _selectedScenarioIndex;
        [SerializeField] private SpellPerformanceRunMode _runMode;
        [SerializeField] private bool _autoStart;

        [Header("Standard Ladder")]
        [SerializeField] private int[] _standardTargets = { 1, 10, 50, 100, 200 };

        [Header("Custom")]
        [SerializeField, Min(1)] private int _customTarget = 50;
        [SerializeField] private bool _maintainCustomIndefinitely;

        [Header("Common")]
        [SerializeField, Min(1)] private int _maxCastsPerFrame = 1;
        [SerializeField, Min(0.1f)] private float _holdSeconds = 3f;
        [SerializeField, Min(0.5f)] private float _fillTimeoutSeconds = 10f;

        private static readonly ProfilerMarker s_fillMarker =
            new ProfilerMarker("PerformanceLab.Fill");

        private SpellPerformanceScenario _activeScenario;
        private SpellPerformanceRunState _state;
        private int _tierIndex;
        private int _currentTarget;
        private int _peakActive;
        private float _phaseTimer;
        private float _stallTimer;

        public SpellPerformanceRunState State => _state;
        public int CurrentTarget => _currentTarget;
        public int PeakActive => _peakActive;

        private void Start()
        {
            if (_autoStart)
                StartLabRun();
        }

        [ContextMenu("Start Lab Run")]
        public void StartLabRun()
        {
            if (!Application.isPlaying)
            {
                GameLog.Warn("SpellPerformanceLab can only run in Play Mode", "PerformanceLab");
                return;
            }

            if (_state == SpellPerformanceRunState.Filling
                || _state == SpellPerformanceRunState.Holding)
            {
                GameLog.Warn(
                    $"Performance Lab is already running: target={_currentTarget}, active={ProjectileBase.ActiveCount}, peak={_peakActive}",
                    "PerformanceLab");
                return;
            }

            SpellPerformanceScenario candidate;
            if (!TryResolveScenario(out candidate))
                return;

            _activeScenario = candidate;

            if (_activeScenario.Caster.TraceLevel != CastTraceLevel.Off)
            {
                GameLog.Warn(
                    "SpellCaster Trace is enabled; Console overhead will pollute performance data",
                    "PerformanceLab");
            }

            _tierIndex = 0;
            _peakActive = ProjectileBase.ActiveCount;
            BeginTarget(_runMode == SpellPerformanceRunMode.Custom
                ? Mathf.Max(1, _customTarget)
                : ResolveStandardTarget(0));
        }

        [ContextMenu("Stop Lab Run")]
        public void StopLabRun()
        {
            if (!Application.isPlaying)
            {
                GameLog.Warn("SpellPerformanceLab can only run in Play Mode", "PerformanceLab");
                return;
            }

            int active = ProjectileBase.ActiveCount;
            float elapsed = _phaseTimer;
            GameLog.Info(
                $"Performance Lab stopped: target={_currentTarget}, active={active}, peak={_peakActive}, elapsed={elapsed:0.###}",
                "PerformanceLab");
            _state = SpellPerformanceRunState.Idle;
        }

        private bool TryResolveScenario(out SpellPerformanceScenario scenario)
        {
            scenario = null;
            if (_scenarios == null
                || _selectedScenarioIndex < 0
                || _selectedScenarioIndex >= _scenarios.Length)
            {
                GameLog.Warn("Scenario index is out of range or the list is empty", "PerformanceLab");
                return false;
            }

            scenario = _scenarios[_selectedScenarioIndex];
            if (scenario == null || !scenario.Enabled)
            {
                GameLog.Warn("Selected scenario is null or disabled", "PerformanceLab");
                return false;
            }

            if (scenario.Caster == null
                || scenario.SpawnPoint == null
                || scenario.AimTarget == null)
            {
                GameLog.Warn(
                    "Scenario requires Caster, SpawnPoint, and AimTarget",
                    "PerformanceLab");
                return false;
            }

            if (scenario.Caster.Wand == null)
            {
                GameLog.Warn("Scenario SpellCaster has no WandLoadout", "PerformanceLab");
                return false;
            }

            return true;
        }

        private int ResolveStandardTarget(int index)
        {
            if (_standardTargets == null || index < 0 || index >= _standardTargets.Length)
                return -1;

            return Mathf.Max(1, _standardTargets[index]);
        }

        private void Update()
        {
            if (_state == SpellPerformanceRunState.Idle
                || _state == SpellPerformanceRunState.Completed)
                return;

            int active = ProjectileBase.ActiveCount;
            if (active > _peakActive)
                _peakActive = active;

            if (_state == SpellPerformanceRunState.Filling)
                TickFilling(active);
            else if (_state == SpellPerformanceRunState.Holding)
                TickHolding(active);
        }

        private void TickFilling(int active)
        {
            _phaseTimer += Time.unscaledDeltaTime;

            if (active < _currentTarget)
                FillToTarget();

            active = ProjectileBase.ActiveCount;
            if (active >= _currentTarget)
            {
                float elapsed = _phaseTimer;
                _state = SpellPerformanceRunState.Holding;
                _phaseTimer = 0f;
                GameLog.Info(
                    $"Target reached; starting Hold: target={_currentTarget}, active={active}, peak={_peakActive}, elapsed={elapsed:0.###}",
                    "PerformanceLab");
                return;
            }

            if (IsFiniteRun() && _phaseTimer >= _fillTimeoutSeconds)
            {
                GameLog.Warn(
                    $"Fill timed out: target={_currentTarget}, active={active}, peak={_peakActive}, elapsed={_phaseTimer:0.###}",
                    "PerformanceLab");
                _state = SpellPerformanceRunState.Completed;
            }
        }

        private void TickHolding(int active)
        {
            if (active < _currentTarget)
            {
                FillToTarget();

                active = ProjectileBase.ActiveCount;
                if (active < _currentTarget)
                {
                    if (IsFiniteRun())
                    {
                        _stallTimer += Time.unscaledDeltaTime;
                        if (_stallTimer >= _fillTimeoutSeconds)
                        {
                            GameLog.Warn(
                                $"Hold stalled below target: target={_currentTarget}, active={active}, peak={_peakActive}, hold={_phaseTimer:0.###}, stall={_stallTimer:0.###}",
                                "PerformanceLab");
                            _state = SpellPerformanceRunState.Completed;
                        }
                    }

                    return;
                }
            }

            _stallTimer = 0f;
            _phaseTimer += Time.unscaledDeltaTime;

            if (_runMode == SpellPerformanceRunMode.Custom
                && _maintainCustomIndefinitely)
                return;

            if (_phaseTimer >= _holdSeconds)
                CompleteCurrentTarget();
        }

        private bool IsFiniteRun()
        {
            return _runMode != SpellPerformanceRunMode.Custom
                || !_maintainCustomIndefinitely;
        }

        private void FillToTarget()
        {
            using (s_fillMarker.Auto())
            {
                int maxCasts = Mathf.Max(1, _maxCastsPerFrame);
                for (int i = 0; i < maxCasts; i++)
                {
                    if (ProjectileBase.ActiveCount >= _currentTarget)
                        break;

                    int emitted = _activeScenario.Caster.CastWand(
                        _activeScenario.SpawnPoint.position,
                        _activeScenario.AimTarget.position,
                        _activeScenario.Team,
                        _activeScenario.AttackerId,
                        _activeScenario.CasterCollider);

                    if (emitted <= 0)
                        break;

                    int active = ProjectileBase.ActiveCount;
                    if (active > _peakActive)
                        _peakActive = active;
                }
            }
        }

        private void CompleteCurrentTarget()
        {
            int active = ProjectileBase.ActiveCount;
            GameLog.Info(
                $"Target completed: target={_currentTarget}, active={active}, peak={_peakActive}, elapsed={_phaseTimer:0.###}",
                "PerformanceLab");

            if (_runMode == SpellPerformanceRunMode.Custom)
            {
                _state = SpellPerformanceRunState.Completed;
                return;
            }

            _tierIndex++;
            int nextTarget = ResolveStandardTarget(_tierIndex);
            if (nextTarget < 0)
            {
                _state = SpellPerformanceRunState.Completed;
                GameLog.Info(
                    $"Standard ladder completed: target={_currentTarget}, active={active}, peak={_peakActive}, elapsed={_phaseTimer:0.###}",
                    "PerformanceLab");
                return;
            }

            BeginTarget(nextTarget);
        }

        private void BeginTarget(int target)
        {
            if (target <= 0)
            {
                int active = ProjectileBase.ActiveCount;
                float elapsed = _phaseTimer;
                GameLog.Warn(
                    $"Target count is invalid; test stopped: target={target}, active={active}, peak={_peakActive}, elapsed={elapsed:0.###}",
                    "PerformanceLab");
                _state = SpellPerformanceRunState.Completed;
                return;
            }

            _currentTarget = target;
            _phaseTimer = 0f;
            _stallTimer = 0f;
            _state = SpellPerformanceRunState.Filling;
            GameLog.Info(
                $"Target started: target={_currentTarget}, active={ProjectileBase.ActiveCount}, peak={_peakActive}, elapsed={_phaseTimer:0.###}",
                "PerformanceLab");
        }
    }
}
