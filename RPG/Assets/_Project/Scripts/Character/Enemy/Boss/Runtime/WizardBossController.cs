using Game.Combat;
using Game.Core;
using Game.Run;
using Game.Skills;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Character
{
    /// <summary>
    /// P8 法术 Boss 的 Scene Runtime Owner。
    /// Awake 一次性缓存组件、状态和 Program Runtime 数组；Update 不查找 Scene、不 new、不使用 LINQ。
    /// Entry 后只接受第一次 Target，之后没有 Detect/Lose Radius，因此 Player 拉远也不会脱战。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(HealthComponent))]
    [RequireComponent(typeof(StatusController))]
    [RequireComponent(typeof(SpellCaster))]
    public sealed class WizardBossController : MonoBehaviour
    {
        private const int TeleportOverlapCapacity = 32;
        private const float FailedTeleportRetryDelay = 1f;
        private const float GoldenRatioConjugate = 0.61803399f;

        [SerializeField] private BossDefinition _definition;
        [SerializeField] private Transform _castOrigin;
        [SerializeField] private Vector3 _aimOffset = new Vector3(0f, 1f, 0f);

        private CharacterController _characterController;
        private EnemyNavigationMotor _navigation;
        private Animator _animator;
        private HealthComponent _health;
        private StatusController _status;
        private SpellCaster _spellCaster;
        private Collider _casterCollider;

        private BossStateMachine _stateMachine;
        private BossInactiveState _inactiveState;
        private BossApproachState _approachState;
        private BossDecisionState _decisionState;
        private BossCastState _castState;
        private BossPhaseTransitionState _phaseTransitionState;
        private BossTeleportState _teleportState;
        private BossProgramRuntimeState _programRuntime;
        private BossTeleportRuntimeState _teleportRuntime;
        private BossTeleportSampler _teleportSampler;

        private Transform _target;
        private CharacterController _targetCharacterController;
        private StatusController _targetStatus;
        private HealthComponent _targetHealth;
        private bool _isEncounterActive;
        private bool _isActionLocked;
        private bool _isCombatEngaged;
        private int _orbitSign = 1;
        private float _orbitDirectionTimer;
        private float _verticalVelocity;
        private Vector3 _lastHorizontalVelocity;
        private bool _hasMoveXParameter;
        private bool _hasMoveZParameter;
        private BossPhase _currentPhase = BossPhase.Phase1;
        private BossPhase _pendingPhase = BossPhase.Phase1;
        private float _globalCastIntervalRemaining;
        private int _selectedProgramIndex = -1;
        private Vector3 _lockedAimPoint;
        private int _castTriggerHash;
        private int _phaseTransitionTriggerHash;
        private int _teleportFallbackProgramIndex = -1;
        private int _actionDecisionSequence;
        private int _teleportSampleSequence;
        private Vector3 _teleportSourcePosition;
        private Vector3 _teleportDestination;

        private static readonly int SpeedHash =
            Animator.StringToHash("speed");
        private static readonly int MoveXHash =
            Animator.StringToHash("moveX");
        private static readonly int MoveZHash =
            Animator.StringToHash("moveZ");

        public BossDefinition Definition => _definition;
        public HealthComponent Health => _health;
        public Transform Target => _target;
        public bool HasTarget => _target != null;
        public bool IsEncounterActive => _isEncounterActive;
        public bool IsActionLocked => _isActionLocked;
        public bool IsCombatEngaged => _isCombatEngaged;
        public BossPhase CurrentPhase => _currentPhase;
        public BossPhase PendingPhase => _pendingPhase;
        public BossRuntimeStateKind CurrentStateKind =>
            _stateMachine != null
                ? _stateMachine.CurrentKind
                : BossRuntimeStateKind.None;
        public Vector3 LockedAimPoint => _lockedAimPoint;
        public int SelectedProgramIndex => _selectedProgramIndex;
        public float GlobalCastIntervalRemaining =>
            _globalCastIntervalRemaining;
        public float DecisionInterval =>
            _definition != null
                ? Mathf.Max(0.05f, _definition.DecisionInterval)
                : 0.2f;
        public float CastTelegraphDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.CastTelegraphDuration)
                : 0f;
        public float CastRecoveryDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.CastRecoveryDuration)
                : 0f;
        public float PhaseTransitionDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.PhaseTransitionDuration)
                : 0f;
        public float TeleportTelegraphDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.TeleportTelegraphDuration)
                : 0f;
        public float TeleportRecoveryDuration =>
            _definition != null
                ? Mathf.Max(0f, _definition.TeleportRecoveryDuration)
                : 0f;
        public float TeleportCooldownRemaining =>
            _teleportRuntime != null
                ? _teleportRuntime.CooldownRemaining
                : 0f;
        internal int TeleportFallbackProgramIndex =>
            _teleportFallbackProgramIndex;

        public bool IsWithinStopDistance =>
            HasTarget &&
            HorizontalDistanceToTarget <=
            (_definition != null
                ? Mathf.Max(0f, _definition.AttackEnterDistance)
                : 0f);

        private float HorizontalDistanceToTarget
        {
            get
            {
                if (_target == null)
                {
                    return float.PositiveInfinity;
                }

                Vector3 delta = _target.position - transform.position;
                delta.y = 0f;
                return delta.magnitude;
            }
        }

        private void Awake()
        {
            _characterController = GetComponent<CharacterController>();
            _navigation = new EnemyNavigationMotor(
                GetComponent<NavMeshAgent>(),
                _definition,
                transform);
            _animator = GetComponentInChildren<Animator>();
            _health = GetComponent<HealthComponent>();
            _status = GetComponent<StatusController>();
            _spellCaster = GetComponent<SpellCaster>();
            _casterCollider = GetComponent<Collider>();
            if (_castOrigin == null)
            {
                _castOrigin = transform;
            }

            _stateMachine = new BossStateMachine();
            _inactiveState = new BossInactiveState(this);
            _approachState = new BossApproachState(this);
            _decisionState = new BossDecisionState(this);
            _castState = new BossCastState(this);
            _phaseTransitionState =
                new BossPhaseTransitionState(this);
            _teleportState = new BossTeleportState(this);

            int programCount =
                _definition != null &&
                _definition.Programs != null
                    ? _definition.Programs.Length
                    : 0;
            int historyCapacity =
                _definition != null
                    ? Mathf.Max(0, _definition.RecentHistoryCapacity)
                    : 0;
            _programRuntime = new BossProgramRuntimeState(
                programCount,
                historyCapacity);
            _teleportRuntime = new BossTeleportRuntimeState();

            int candidateCount =
                _definition != null
                    ? Mathf.Max(1, _definition.TeleportCandidateCount)
                    : 1;
            var candidateBuffer = new Vector3[candidateCount];
            var overlapBuffer =
                new Collider[TeleportOverlapCapacity];
            _teleportSampler = new BossTeleportSampler(
                new UnityBossTeleportWorldQuery(overlapBuffer),
                candidateBuffer);

            _castTriggerHash = HashOptionalTrigger(
                _definition != null
                    ? _definition.CastAnimatorTrigger
                    : null);
            _phaseTransitionTriggerHash = HashOptionalTrigger(
                _definition != null
                    ? _definition.PhaseTransitionAnimatorTrigger
                    : null);
            CacheOptionalAnimatorParameters();
            _stateMachine.ChangeState(_inactiveState);
        }

        private void OnEnable()
        {
            EventBus<BossEncounterStartedEvent>.Subscribe(
                OnBossEncounterStarted);
            if (_stateMachine != null &&
                _stateMachine.CurrentKind == BossRuntimeStateKind.None)
            {
                RouteAfterAction();
            }
        }

        private void OnDisable()
        {
            EventBus<BossEncounterStartedEvent>.Unsubscribe(
                OnBossEncounterStarted);
            _stateMachine?.ChangeState(null);
            _navigation?.StopNavigation(true);
            _health?.SetInvulnerable(false);
            _isActionLocked = false;
        }

        private void Update()
        {
            if (Time.timeScale == 0f ||
                !_isEncounterActive ||
                _health == null ||
                !_health.IsAlive)
            {
                return;
            }

            float deltaTime = Time.deltaTime;
            _lastHorizontalVelocity = Vector3.zero;
            _programRuntime.TickCooldowns(deltaTime);
            _teleportRuntime.Tick(deltaTime);
            _globalCastIntervalRemaining = Mathf.Max(
                0f,
                _globalCastIntervalRemaining - deltaTime);
            RefreshPendingPhase();
            RefreshCombatEngagement();

            if (_pendingPhase > _currentPhase &&
                !_isActionLocked &&
                CurrentStateKind !=
                BossRuntimeStateKind.PhaseTransition)
            {
                _stateMachine.ChangeState(_phaseTransitionState);
            }
            else
            {
                _stateMachine.Tick(deltaTime);
            }

            ApplyVerticalMotion(deltaTime);
            SyncAnimator();
        }

        /// <summary>
        /// 第一次有效调用永久锁定 Target；之后拒绝 Retarget。
        /// Event 入口和直接 Scene Bootstrap 共用该方法，因此它不是测试专用 API。
        /// </summary>
        public bool TryActivate(Transform target)
        {
            if (_isEncounterActive ||
                target == null ||
                _definition == null)
            {
                return false;
            }

            _target = target;
            _targetCharacterController =
                target.GetComponent<CharacterController>();
            _targetStatus = target.GetComponent<StatusController>();
            _targetHealth = target.GetComponent<HealthComponent>();
            _isEncounterActive = true;
            _isCombatEngaged = false;
            _orbitSign = (gameObject.GetInstanceID() & 1) == 0 ? 1 : -1;
            _orbitDirectionTimer = _definition != null
                ? Mathf.Max(0.1f, _definition.OrbitDirectionInterval)
                : 2f;
            RefreshCombatEngagement();

            _currentPhase = BossPhaseResolver.Resolve(
                BossPhase.Phase1,
                HealthRatio,
                Phase2Threshold,
                Phase3Threshold);
            _pendingPhase = _currentPhase;
            PublishHudInitialized();
            PublishPhaseChanged(_currentPhase, _currentPhase);
            RouteAfterAction();
            return true;
        }

        internal void EnterInactive()
        {
            _stateMachine.ChangeState(_inactiveState);
        }

        internal void EnterApproach()
        {
            _stateMachine.ChangeState(_approachState);
        }

        internal void EnterDecision()
        {
            _stateMachine.ChangeState(_decisionState);
        }

        internal void EnterCast(int programIndex)
        {
            if (!IsValidProgram(programIndex) ||
                _isActionLocked)
            {
                return;
            }

            _selectedProgramIndex = programIndex;
            _lockedAimPoint = _target != null
                ? _target.position + _aimOffset
                : transform.position + transform.forward;
            _stateMachine.ChangeState(_castState);
        }

        internal void EnterTeleport(int fallbackProgramIndex)
        {
            if (_isActionLocked)
            {
                return;
            }

            _teleportFallbackProgramIndex = fallbackProgramIndex;
            _stateMachine.ChangeState(_teleportState);
        }

        internal bool TryAcquireActionLock()
        {
            if (_isActionLocked)
            {
                return false;
            }

            _isActionLocked = true;
            return true;
        }

        internal void ReleaseActionLock()
        {
            _isActionLocked = false;
        }

        internal void MoveTowardTarget(float deltaTime)
        {
            if (_target == null ||
                _definition == null ||
                _characterController == null ||
                !_characterController.enabled)
            {
                return;
            }

            Vector3 toTarget =
                _target.position - transform.position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            float stopDistance =
                Mathf.Max(0f, _definition.AttackEnterDistance);
            if (distance <= stopDistance ||
                distance <= 1e-5f)
            {
                return;
            }

            Vector3 direction = toTarget / distance;
            float moveSpeed = ResolveMoveSpeed(1f);
            EnemyNavigationResult navigationResult = _navigation != null
                ? _navigation.NavigateTo(
                    _target.position,
                    stopDistance,
                    moveSpeed,
                    deltaTime)
                : EnemyNavigationResult.Unavailable;
            MoveUsingNavigationOrFallback(
                direction,
                moveSpeed,
                deltaTime,
                navigationResult);

            Vector3 facingDirection = EnemyNavigationMath.SelectFacingDirection(
                _lastHorizontalVelocity,
                direction);
            FacePoint(transform.position + facingDirection, deltaTime);
        }

        /// <summary>
        /// Decision 与 Cast 共用同一套战斗移动：理想距离内环绕，过近后撤，过远接近。
        /// Cast 只锁定瞄准快照，不锁定位移，所以施法计时与走位可以并行推进。
        /// </summary>
        internal void MoveInCombat(float deltaTime, bool isCasting)
        {
            if (_target == null || _definition == null)
                return;

            if (!_isCombatEngaged)
            {
                MoveTowardTarget(deltaTime);
                return;
            }

            _orbitDirectionTimer -= Mathf.Max(0f, deltaTime);
            if (_orbitDirectionTimer <= 0f)
            {
                _orbitSign = -_orbitSign;
                _orbitDirectionTimer = Mathf.Max(
                    0.1f,
                    _definition.OrbitDirectionInterval);
            }

            Vector3 direction = BossCombatMovementMath.ResolveCombatDirection(
                transform.position,
                _target.position,
                _definition.PreferredCombatMinDistance,
                _definition.PreferredCombatMaxDistance,
                _orbitSign);
            float speedMultiplier = isCasting
                ? _definition.CastMoveSpeedMultiplier
                : _definition.CombatMoveSpeedMultiplier;
            float moveSpeed = ResolveMoveSpeed(speedMultiplier);
            Vector3 navigationTarget = transform.position +
                direction * Mathf.Max(0.1f, _definition.CombatNavigationStepDistance);
            EnemyNavigationResult navigationResult = _navigation != null
                ? _navigation.NavigateTo(
                    navigationTarget,
                    0.05f,
                    moveSpeed,
                    deltaTime)
                : EnemyNavigationResult.Unavailable;
            MoveUsingNavigationOrFallback(
                direction,
                moveSpeed,
                deltaTime,
                navigationResult);
        }

        internal void StopNavigation()
        {
            _lastHorizontalVelocity = Vector3.zero;
            _navigation?.StopNavigation(true);
        }

        private float ResolveMoveSpeed(float stateMultiplier)
        {
            float statusMultiplier =
                _status != null ? _status.MoveSpeedMultiplier : 1f;
            return Mathf.Max(0f, _definition.MoveSpeed) *
                   Mathf.Max(0f, statusMultiplier) *
                   Mathf.Max(0f, stateMultiplier);
        }

        private void MoveUsingNavigationOrFallback(
            Vector3 fallbackDirection,
            float moveSpeed,
            float deltaTime,
            EnemyNavigationResult navigationResult)
        {
            Vector3 velocity = _navigation != null
                ? _navigation.DesiredVelocity
                : Vector3.zero;
            velocity.y = 0f;

            // PlayMode 单元测试或漏配 NavMesh 时仍保留可诊断的直线降级；正式 P8 使用 Agent Steering 绕障碍。
            bool navigationUnavailable =
                navigationResult == EnemyNavigationResult.Unavailable ||
                navigationResult == EnemyNavigationResult.Invalid;
            if (navigationUnavailable && velocity.sqrMagnitude <= 1e-6f)
            {
                fallbackDirection.y = 0f;
                if (fallbackDirection.sqrMagnitude > 1e-6f)
                    velocity = fallbackDirection.normalized * moveSpeed;
            }

            _characterController.Move(velocity * Mathf.Max(0f, deltaTime));
            _lastHorizontalVelocity = _characterController.velocity;
            _lastHorizontalVelocity.y = 0f;
            _navigation?.SyncToCharacter();
        }

        internal void FaceTarget(float deltaTime)
        {
            if (_target != null)
            {
                FacePoint(_target.position, deltaTime);
            }
        }

        internal void FacePoint(Vector3 point, float deltaTime)
        {
            if (_definition == null)
            {
                return;
            }

            Vector3 direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 1e-6f)
            {
                return;
            }

            Quaternion targetRotation =
                Quaternion.LookRotation(direction.normalized);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                Mathf.Max(0f, _definition.TurnSpeedDegrees) *
                Mathf.Max(0f, deltaTime));
        }

        internal int SelectProgram(out float selectedScore)
        {
            if (_definition == null)
            {
                selectedScore = float.NegativeInfinity;
                return -1;
            }

            var context = new BossDecisionContext(
                _currentPhase,
                HorizontalDistanceToTarget,
                _targetStatus != null
                    ? _targetStatus.GetIntensity(StatusKind.Wet)
                    : 0f,
                _targetCharacterController != null
                    ? _targetCharacterController.velocity.magnitude
                    : 0f,
                HealthRatio,
                TargetHealthRatio);
            return BossSpellUtilityEvaluator.SelectProgram(
                _definition.Programs,
                _programRuntime,
                in context,
                out selectedScore);
        }

        internal bool ShouldTeleport(float selectedSpellScore)
        {
            if (_definition == null || _teleportRuntime == null)
            {
                return false;
            }

            BossPhaseDefinition phase =
                _definition.GetPhase(_currentPhase);
            if (phase == null ||
                phase.TeleportWeight <= 0f ||
                _teleportRuntime.CooldownRemaining > 0f)
            {
                return false;
            }

            _actionDecisionSequence++;
            float roll = Mathf.Repeat(
                _actionDecisionSequence * GoldenRatioConjugate,
                1f);
            return BossActionUtilityEvaluator.ShouldTeleport(
                phase.TeleportWeight,
                _teleportRuntime.CooldownRemaining,
                selectedSpellScore,
                roll);
        }

        internal bool TryPrepareTeleport()
        {
            if (_definition == null ||
                _target == null ||
                _characterController == null ||
                _teleportSampler == null)
            {
                return false;
            }

            _teleportSampleSequence++;
            float angleOffset = Mathf.Repeat(
                _teleportSampleSequence * 137.50777f,
                360f);
            var request = new BossTeleportSamplingRequest(
                _target.position,
                _definition.TeleportMinRadius,
                _definition.TeleportMaxRadius,
                angleOffset,
                _definition.NavMeshSampleDistance,
                _definition.MaxGroundSlopeDegrees,
                _definition.TeleportNavMeshAreaMask,
                _definition.TeleportGroundMask.value,
                _definition.TeleportBlockingMask.value);
            var capsuleShape = new BossTeleportCapsuleShape(
                _characterController.center,
                _characterController.radius,
                _characterController.height,
                _characterController.skinWidth,
                transform.lossyScale);

            _teleportSourcePosition = transform.position;
            return _teleportSampler.TryFindDestination(
                in request,
                capsuleShape,
                transform,
                out _teleportDestination);
        }

        internal void PublishTeleportTelegraph()
        {
            PublishTeleportEvent(BossTeleportStage.Telegraph);
        }

        internal void ExecutePreparedTeleport()
        {
            PublishTeleportEvent(BossTeleportStage.Departed);

            bool wasEnabled =
                _characterController != null &&
                _characterController.enabled;
            if (wasEnabled)
            {
                _characterController.enabled = false;
            }

            transform.position = _teleportDestination;
            // Teleport 是一次离散的位置重置；不能把传送前积累的下落速度带到新落点。
            _verticalVelocity = 0f;

            if (wasEnabled)
            {
                _characterController.enabled = true;
            }

            _navigation?.SyncToCharacter();

            PublishTeleportEvent(BossTeleportStage.Arrived);
        }

        internal void CompleteTeleport()
        {
            BossPhaseDefinition phase =
                _definition != null
                    ? _definition.GetPhase(_currentPhase)
                    : null;
            _teleportRuntime.MarkUsed(
                phase != null ? phase.TeleportCooldown : 0f);
            _globalCastIntervalRemaining =
                phase != null ? Mathf.Max(0f, phase.CastInterval) : 0f;
            _teleportFallbackProgramIndex = -1;
            RouteAfterAction();
        }

        internal void CancelTeleport(int fallbackProgramIndex)
        {
            BossPhaseDefinition phase =
                _definition != null
                    ? _definition.GetPhase(_currentPhase)
                    : null;
            float retryDelay = phase != null
                ? Mathf.Min(
                    FailedTeleportRetryDelay,
                    Mathf.Max(0f, phase.TeleportCooldown))
                : FailedTeleportRetryDelay;
            _teleportRuntime.MarkUsed(retryDelay);
            PublishTeleportEvent(BossTeleportStage.Cancelled);
            _teleportFallbackProgramIndex = -1;

            if (IsValidProgram(fallbackProgramIndex))
            {
                EnterCastAfterTeleportCancellation(
                    fallbackProgramIndex);
                return;
            }

            _stateMachine.ChangeState(_decisionState);
        }

        private void EnterCastAfterTeleportCancellation(int programIndex)
        {
            // 先离开 Teleport State，让 Exit 释放 Action Lock，再进入 Cast 重新取得所有权。
            _selectedProgramIndex = programIndex;
            _lockedAimPoint = _target != null
                ? _target.position + _aimOffset
                : transform.position + transform.forward;
            _stateMachine.ChangeState(_castState);
        }

        internal void ReleaseSelectedProgram()
        {
            if (!IsValidProgram(_selectedProgramIndex))
            {
                return;
            }

            BossSpellProgramDefinition program =
                _definition.Programs[_selectedProgramIndex];
            if (program == null || program.Wand == null)
            {
                GameLog.Warn(
                    $"Boss Program Index {_selectedProgramIndex} 缺少 Wand。",
                    "Boss");
                return;
            }

            _spellCaster.CastProgram(
                program.Wand,
                _castOrigin.position,
                _lockedAimPoint,
                _health.TeamId,
                _health.Id,
                _casterCollider,
                SpellManaPolicy.IgnoreMana);
        }

        internal void CompleteCast(int programIndex)
        {
            if (IsValidProgram(programIndex))
            {
                BossSpellProgramDefinition program =
                    _definition.Programs[programIndex];
                _programRuntime.MarkUsed(
                    programIndex,
                    program != null ? program.Cooldown : 0f);
            }

            BossPhaseDefinition phase =
                _definition.GetPhase(_currentPhase);
            _globalCastIntervalRemaining =
                phase != null ? Mathf.Max(0f, phase.CastInterval) : 0f;
            _selectedProgramIndex = -1;
            RouteAfterAction();
        }

        internal void CompletePhaseTransition()
        {
            BossPhase previous = _currentPhase;
            _currentPhase = _pendingPhase;
            PublishPhaseChanged(previous, _currentPhase);
            RouteAfterAction();
        }

        internal void TriggerCastAnimation()
        {
            if (_animator != null && _castTriggerHash != 0)
            {
                _animator.SetTrigger(_castTriggerHash);
            }
        }

        internal void TriggerPhaseTransitionAnimation()
        {
            if (_animator != null &&
                _phaseTransitionTriggerHash != 0)
            {
                _animator.SetTrigger(
                    _phaseTransitionTriggerHash);
            }
        }

        /// <summary>
        /// CharacterController 只解析 Move 给出的位移，不会像 Rigidbody 一样自动受 Gravity 影响。
        /// 水平 Steering 可能因台阶、斜坡或碰撞穿透修正把胶囊抬高，因此所有参战状态在同一处
        /// 追加垂直运动；落地时保留轻微向下速度，让 isGrounded 在连续帧中稳定保持接触。
        /// </summary>
        private void ApplyVerticalMotion(float deltaTime)
        {
            if (_characterController == null ||
                !_characterController.enabled ||
                _definition == null)
            {
                return;
            }

            if (_characterController.isGrounded &&
                _verticalVelocity <= 0f)
            {
                _verticalVelocity = -Mathf.Max(
                    0f,
                    _definition.GroundedStickSpeed);
            }
            else
            {
                _verticalVelocity -= Mathf.Max(
                    0f,
                    _definition.GravityAcceleration) * Mathf.Max(0f, deltaTime);
            }

            _characterController.Move(
                Vector3.up * _verticalVelocity * Mathf.Max(0f, deltaTime));
            // 垂直 Move 也改变了真实 Transform；Agent 使用手动同步模式，必须在最终位置后再对齐。
            _navigation?.SyncToCharacter();
        }

        private void SyncAnimator()
        {
            if (_animator == null ||
                _characterController == null)
            {
                return;
            }

            Vector3 velocity = _lastHorizontalVelocity;
            _animator.SetFloat(SpeedHash, velocity.magnitude);
            Vector3 localVelocity = transform.InverseTransformDirection(velocity);
            float denominator = _definition != null
                ? Mathf.Max(0.01f, _definition.MoveSpeed)
                : 1f;
            if (_hasMoveXParameter)
                _animator.SetFloat(MoveXHash, Mathf.Clamp(localVelocity.x / denominator, -1f, 1f));
            if (_hasMoveZParameter)
                _animator.SetFloat(MoveZHash, Mathf.Clamp(localVelocity.z / denominator, -1f, 1f));
        }

        private void CacheOptionalAnimatorParameters()
        {
            if (_animator == null)
                return;

            AnimatorControllerParameter[] parameters = _animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].type != AnimatorControllerParameterType.Float)
                    continue;
                if (parameters[i].nameHash == MoveXHash)
                    _hasMoveXParameter = true;
                else if (parameters[i].nameHash == MoveZHash)
                    _hasMoveZParameter = true;
            }
        }

        private float HealthRatio =>
            _health != null && _health.MaxHp > 0f
                ? Mathf.Clamp01(_health.CurrentHp / _health.MaxHp)
                : 0f;

        private float TargetHealthRatio =>
            _targetHealth != null && _targetHealth.MaxHp > 0f
                ? Mathf.Clamp01(
                    _targetHealth.CurrentHp /
                    _targetHealth.MaxHp)
                : 0f;

        private float Phase2Threshold =>
            _definition != null && _definition.Phase2 != null
                ? _definition.Phase2.EnterAtOrBelowHealthRatio
                : 0.7f;

        private float Phase3Threshold =>
            _definition != null && _definition.Phase3 != null
                ? _definition.Phase3.EnterAtOrBelowHealthRatio
                : 0.35f;

        private void RefreshPendingPhase()
        {
            BossPhase resolved = BossPhaseResolver.Resolve(
                _currentPhase,
                HealthRatio,
                Phase2Threshold,
                Phase3Threshold);
            if (resolved > _pendingPhase)
            {
                _pendingPhase = resolved;
            }
        }

        private void RefreshCombatEngagement()
        {
            if (_definition == null || !HasTarget)
            {
                _isCombatEngaged = false;
                return;
            }

            _isCombatEngaged = BossCombatMovementMath.ResolveCombatEngagement(
                _isCombatEngaged,
                HorizontalDistanceToTarget,
                _definition.AttackEnterDistance,
                _definition.AttackExitDistance);
        }

        private void RouteAfterAction()
        {
            if (_stateMachine == null)
            {
                return;
            }

            if (!_isEncounterActive || _target == null)
            {
                _stateMachine.ChangeState(_inactiveState);
                return;
            }

            if (_pendingPhase > _currentPhase)
            {
                _stateMachine.ChangeState(_phaseTransitionState);
                return;
            }

            _stateMachine.ChangeState(
                _isCombatEngaged
                    ? _decisionState
                    : _approachState);
        }

        private bool IsValidProgram(int programIndex)
        {
            return _definition != null &&
                   _definition.Programs != null &&
                   programIndex >= 0 &&
                   programIndex < _definition.Programs.Length;
        }

        private void OnBossEncounterStarted(
            BossEncounterStartedEvent startedEvent)
        {
            if (_health == null ||
                startedEvent.BossId != _health.Id)
            {
                return;
            }

            PlayerControllerBase player =
                PlayerControllerBase.Current;
            if (player == null ||
                player.gameObject.GetInstanceID() !=
                startedEvent.PlayerId)
            {
                GameLog.Warn(
                    "Boss Encounter 已启动，但没有找到匹配的当前 Player。",
                    "Boss");
                return;
            }

            TryActivate(player.transform);
        }

        private void PublishPhaseChanged(
            BossPhase previous,
            BossPhase current)
        {
            EventBus<BossPhaseChangedEvent>.Publish(
                new BossPhaseChangedEvent
                {
                    BossId = _health != null ? _health.Id : 0,
                    PreviousPhase = (byte)previous,
                    CurrentPhase = (byte)current,
                    HealthRatio = HealthRatio,
                });
        }

        private void PublishHudInitialized()
        {
            if (_health == null || _definition == null)
            {
                return;
            }

            // 这里只发布 Authoring 数据与当前 HP 的值快照，不把 BossDefinition
            // 或 MonoBehaviour 引用交给 UI，避免跨 Assembly 的可变对象所有权泄漏。
            EventBus<BossHudInitializedEvent>.Publish(
                new BossHudInitializedEvent
                {
                    BossId = _health.Id,
                    DisplayName = _definition.DisplayName,
                    CurrentHp = _health.CurrentHp,
                    MaxHp = _health.MaxHp,
                    CurrentPhase = (byte)_currentPhase,
                    Phase2Threshold = Phase2Threshold,
                    Phase3Threshold = Phase3Threshold,
                });
        }

        private void PublishTeleportEvent(BossTeleportStage stage)
        {
            EventBus<BossTeleportEvent>.Publish(
                new BossTeleportEvent
                {
                    BossId = _health != null ? _health.Id : 0,
                    Stage = stage,
                    SourcePosition = _teleportSourcePosition,
                    DestinationPosition = _teleportDestination,
                });
        }

        private static int HashOptionalTrigger(string triggerName)
        {
            return string.IsNullOrWhiteSpace(triggerName)
                ? 0
                : Animator.StringToHash(triggerName);
        }
    }
}
