using UnityEngine;
using UnityEngine.AI;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 敌人控制器基类（总装 + 行动层）。对标 PlayerControllerBase：持有共享组件/感知/状态机/共享态(Idle+Hurt)，
    /// Update 跑 感知→状态机→同步 Animator，对状态暴露行动能力。具体"战斗走位 + 攻击"由子类经 EngageState 接缝提供：
    /// MeleeEnemyController(贴身近战) / RangedEnemyController(保距离施法)。决策层(状态)只决定做什么，怎么做在本类/子类。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(HealthComponent))]
    public abstract class EnemyControllerBase : MonoBehaviour, IAuthorityTickParticipant
    {
        [Header("数据")]
        [SerializeField] protected EnemyDefinition _definition;

        [Header("调参")]
        [SerializeField] private float _rotationSpeed = 10f;
        [SerializeField] private float _gravity = -20f;

        protected CharacterController _cc;
        protected Animator _animator;
        private StatusController _statusController;
        private HealthComponent _health;

        private EnemyPerception _perception;
        private EnemyStateMachine _stateMachine;
        private EnemyIdleState _idleState;
        private EnemyHurtState _hurtState;
        private EnemyNavigationMotor _navigation;
        private int _attackStateHash;
        private int _hurtStateHash;
        private int _id;
        private bool _dead;
        private bool _isOnlineAuthority;
        private bool _isOnlineReplica;
        private ICombatActorRosterReadOnly _onlineRoster;
        private bool _hasAuthorityTick;
        private uint _lastAuthorityTick;

        private float _verticalVelocity;

        private static readonly int SpeedHash = Animator.StringToHash("speed");

        public EnemyDefinition Definition => _definition;
        public EnemyPerception Perception => _perception;
        public EnemyStateMachine StateMachine => _stateMachine;
        public EnemyIdleState IdleState => _idleState;
        public EnemyHurtState HurtState => _hurtState;
        public int AttackStateHash => _attackStateHash;
        public int HurtStateHash => _hurtStateHash;
        public float AttackCooldownCounter { get; set; }
        public bool IsDead => _dead;
        public Animator Animator => _animator;
        public int TeamId => _health != null ? _health.TeamId : 0;
        public bool IsOnlineAuthority => _isOnlineAuthority;
        public bool IsOnlineReplica => _isOnlineReplica;
        /// <summary>本次领域推进的步长；单机由 Update 注入，在线由统一 Authority Driver 注入。</summary>
        public float SimulationDeltaTime { get; private set; }

        /// <summary>由在线场景组合明确指定 Authority，避免通过本机 Player Current 推断角色身份。</summary>
        public void ConfigureOnlineAuthority(ICombatActorRosterReadOnly roster)
        {
            _isOnlineAuthority = true;
            _isOnlineReplica = false;
            _onlineRoster = roster;
            ResetOnlineExecution();
            _perception?.BindRoster(roster);
            _navigation?.SetReplicaSuspended(false);
        }

        /// <summary>Replica 仅消费快照表现，不执行本机 Enemy AI。</summary>
        public void ConfigureOnlineReplica()
        {
            _isOnlineAuthority = false;
            _isOnlineReplica = true;
            _onlineRoster = null;
            ResetOnlineExecution();
            _perception?.BindRoster(null);
            _navigation?.SetReplicaSuspended(true);
        }

        /// <summary>
        /// 统一 Driver 已完成 CanSimulate 门禁后才调用。这里再拒绝重复或过期 Tick，
        /// 防止注册重复等组合错误让同一个 CharacterController 在一轮里 Move 两次。
        /// </summary>
        public void StepAuthority(float deltaTime, uint serverTick)
        {
            if (!_isOnlineAuthority || _dead || !isActiveAndEnabled || _perception == null ||
                _cc == null || !_cc.enabled || deltaTime <= 0f ||
                float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
                return;

            // uint 差值转有符号数可保持正常递增与跨 uint 回卷的先后关系；不接受半个序号空间以前的 Tick。
            if (_hasAuthorityTick && unchecked((int)(serverTick - _lastAuthorityTick)) <= 0)
                return;

            _hasAuthorityTick = true;
            _lastAuthorityTick = serverTick;
            SimulationDeltaTime = deltaTime;
            StepCooldown();
            _perception.Tick();
            StepOnlineLocomotion();
            SyncAnimator();
        }

        private void ResetOnlineExecution()
        {
            _hasAuthorityTick = false;
            SimulationDeltaTime = 0f;
            // 从旧单机攻击态切出必须走 Exit，确保已打开的近战命中窗口得到关闭。
            // 不直接丢弃 CurrentState，避免残留攻击资源绕过 N3 的 locomotion-only 边界。
            if (_stateMachine != null && _stateMachine.CurrentState != null)
                _stateMachine.ChangeState(_idleState);
            _navigation?.StopNavigation(true);
        }

        /// <summary>进入战斗后使用的走位状态（多态接缝）：近战=ChaseState，远程=KiteState。</summary>
        public abstract EnemyStateBase EngageState { get; }

        protected virtual void Awake()
        {
            _cc = GetComponent<CharacterController>();
            NavMeshAgent navMeshAgent = GetComponent<NavMeshAgent>();
            _animator = GetComponentInChildren<Animator>();
            // StatusController 属于 Combat，Character 只消费最终倍率，不理解 Sticky/Reaction 规则。
            // Awake 缓存避免敌人 MoveHorizontal 热路径反复 GetComponent。
            _statusController = GetComponent<StatusController>();
            _health = GetComponent<HealthComponent>();

            _perception = new EnemyPerception(this);
            _stateMachine = new EnemyStateMachine();
            _idleState = new EnemyIdleState(this);
            _hurtState = new EnemyHurtState(this);
            _navigation = new EnemyNavigationMotor(navMeshAgent, _definition, transform);
            // 允许组合层先在 inactive 对象上指定身份；Awake 以后补完同一份配置，不依赖 Start 顺序。
            if (_isOnlineAuthority || _isOnlineReplica)
                _perception.BindRoster(_onlineRoster);
            if (_isOnlineReplica)
                _navigation.SetReplicaSuspended(true);

            // 攻击/施法动画状态名取自攻击数据(数据驱动)；空 → 0 → CrossFade 不切动画
            string atkStateName = (_definition != null && _definition.Attack != null)
                ? _definition.Attack.AnimationStateName : null;
            _attackStateHash = string.IsNullOrEmpty(atkStateName) ? 0 : Animator.StringToHash(atkStateName);
            if (_attackStateHash == 0)
                GameLog.Warn("敌人攻击动画状态名为空，攻击 CrossFade 无法切换动画", "Enemy");

            _hurtStateHash = (_definition != null && !string.IsNullOrEmpty(_definition.HurtStateName))
                ? Animator.StringToHash(_definition.HurtStateName) : 0;
            _id = gameObject.GetInstanceID();

            if (_definition == null)
                GameLog.Warn("EnemyControllerBase 未配置 EnemyDefinition", "Enemy");
        }

        private void Start()
        {
            if (!_isOnlineAuthority && !_isOnlineReplica)
                _stateMachine.ChangeState(_idleState);
        }

        protected virtual void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        protected virtual void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            // N3 尚未接权威战斗；在线不让本地 Combat 事件切入旧攻击/硬直 FSM。
            if (_isOnlineAuthority || _isOnlineReplica) return;
            if (_dead || e.TargetId != _id) return;
            if (e.RemainingHp <= 0f) return;        // 致死那一击交给 OnDeath，不进硬直
            if (!e.TriggerHitReaction) return;      // DoT/环境跳伤：不进硬直，避免被持续伤害锁死
            _stateMachine.ChangeState(_hurtState);
        }

        private void OnDeath(DeathEvent e)
        {
            if (_isOnlineReplica) return;
            if (_dead || e.TargetId != _id) return;
            _dead = true;
            _navigation?.DisableNavigation();
            OnDied(); // 子类收尾(如关近战命中窗口)；死亡动画+销毁由 CharacterCombatFeedback 负责
        }

        /// <summary>死亡时子类收尾钩子（默认空）。</summary>
        protected virtual void OnDied() { }

        private void Update()
        {
            // 在线对象只由显式 Tick 推进，不能同时接受 Unity Update 的第二次感知和位移。
            if (_dead || _isOnlineAuthority || _isOnlineReplica) return;
            SimulationDeltaTime = Time.deltaTime;
            StepCooldown();
            _perception.Tick();
            _stateMachine.Update();
            SyncAnimator();
        }

        private void StepCooldown()
        {
            if (AttackCooldownCounter > 0f)
                AttackCooldownCounter = Mathf.Max(0f, AttackCooldownCounter - SimulationDeltaTime);
        }

        private void StepOnlineLocomotion()
        {
            if (!_perception.HasTarget || _definition == null)
            {
                StopNavigation(true);
                StayGrounded();
                return;
            }

            Vector3 targetPosition = _perception.TargetPosition;
            RangedEnemyController ranged = this as RangedEnemyController;
            if (ranged != null)
            {
                if (EnemyPerceptionMath.ShouldRetreat(
                        _perception.DistanceToTarget, _perception.VerticalDistanceToTarget,
                        _definition.RetreatDistance, _definition.RangedRetreatMaxHeight))
                {
                    EnemyNavigationResult retreat = PlanNavigationAwayFrom(targetPosition);
                    if (EnemyNavigationMath.ShouldContinueRetreat(retreat))
                    {
                        FaceNavigationOrTarget(targetPosition);
                        MoveAlongNavigation();
                        return;
                    }
                }

                if (ranged.CanAttackTarget(targetPosition))
                {
                    HoldOnlinePosition(targetPosition);
                    return;
                }
            }

            // 复用真实 NavMesh 路径与绕障；路径不可用时保留目标并重试，绝不直线移动 Transform 穿墙。
            // 近战保留 AttackRange 的站位距离，N3 仅停步而不进入旧 AttackState 或 CastProgram。
            float stoppingDistance = ranged == null ? Mathf.Max(0.05f, _definition.AttackRange * 0.8f) : 0.05f;
            PlanNavigationTo(targetPosition, stoppingDistance);
            if (ranged == null && CanAttackThroughNavigation(targetPosition, _definition.AttackRange))
            {
                HoldOnlinePosition(targetPosition);
                return;
            }

            FaceNavigationOrTarget(targetPosition);
            MoveAlongNavigation();
        }

        private void HoldOnlinePosition(Vector3 targetPosition)
        {
            _navigation?.HoldPosition();
            FaceTarget(targetPosition);
            StayGrounded();
        }

        private void SyncAnimator()
        {
            if (_animator == null) return;
            Vector3 v = _cc.velocity; v.y = 0f;
            _animator.SetFloat(SpeedHash, v.magnitude);
        }

        #region 行动能力（供状态调用）

        public void BeginNavigation() => _navigation?.BeginNavigation();

        public void StopNavigation(bool clearPath = true) => _navigation?.StopNavigation(clearPath);

        /// <summary>只更新目标、路径与 Steering，不立即移动；状态可先判断能否攻击，再保证本帧只调用一次 Move。</summary>
        public EnemyNavigationResult PlanNavigationTo(Vector3 targetPos, float stoppingDistance)
        {
            float statusMultiplier = _statusController != null
                ? _statusController.MoveSpeedMultiplier
                : 1f;
            float moveSpeed = _definition != null
                ? _definition.MoveSpeed * Mathf.Max(0f, statusMultiplier)
                : 0f;
            return _navigation != null
                ? _navigation.NavigateTo(targetPos, stoppingDistance, moveSpeed, SimulationDeltaTime)
                : EnemyNavigationResult.Unavailable;
        }

        public EnemyNavigationResult PlanNavigationAwayFrom(Vector3 targetPos)
        {
            float statusMultiplier = _statusController != null
                ? _statusController.MoveSpeedMultiplier
                : 1f;
            float moveSpeed = _definition != null
                ? _definition.MoveSpeed * Mathf.Max(0f, statusMultiplier)
                : 0f;
            return _navigation != null
                ? _navigation.TryNavigateRetreat(targetPos, moveSpeed, SimulationDeltaTime)
                : EnemyNavigationResult.Unavailable;
        }

        /// <summary>执行 Agent 给出的水平 Steering；CharacterController 仍是唯一真实位移写入者。</summary>
        public void MoveAlongNavigation()
        {
            Vector3 horizontal = _navigation != null ? _navigation.DesiredVelocity : Vector3.zero;
            ApplyGravity();
            Vector3 velocity = horizontal;
            velocity.y = _verticalVelocity;
            _cc.Move(velocity * SimulationDeltaTime);
            _navigation?.SyncToCharacter();
        }

        /// <summary>原地不动，仅施加重力贴地。</summary>
        public void StayGrounded()
        {
            ApplyGravity();
            _cc.Move(Vector3.up * _verticalVelocity * SimulationDeltaTime);
            _navigation?.SyncToCharacter();
        }

        public bool CanAttackThroughNavigation(Vector3 targetPos, float range)
        {
            return _navigation != null && _navigation.CanAttack(targetPos, range);
        }

        /// <summary>平滑转向目标的水平方向(只 yaw)。</summary>
        public void FaceTarget(Vector3 targetPos)
        {
            Vector3 dir = targetPos - transform.position; dir.y = 0f;
            FaceDirection(dir);
        }

        /// <summary>
        /// 追击/绕行时朝 NavMesh 实际 Steering 方向转身；路径尚未产出速度时才暂时面向玩家。
        /// 攻击态仍显式调用 FaceTarget，确保出手方向准确。
        /// </summary>
        public void FaceNavigationOrTarget(Vector3 targetPos)
        {
            Vector3 targetDirection = targetPos - transform.position;
            Vector3 navigationVelocity = _navigation != null ? _navigation.DesiredVelocity : Vector3.zero;
            FaceDirection(EnemyNavigationMath.SelectFacingDirection(navigationVelocity, targetDirection));
        }

        private void FaceDirection(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f) return;
            Quaternion rot = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.Slerp(transform.rotation, rot, _rotationSpeed * SimulationDeltaTime);
        }

        /// <summary>数据驱动 CrossFade 进入指定 Animator 状态（hash 为 0 静默跳过）。</summary>
        public void CrossFade(int stateHash)
        {
            if (_animator != null && stateHash != 0)
                _animator.CrossFadeInFixedTime(stateHash, _definition.CrossFadeDuration, 0);
        }

        private void ApplyGravity()
        {
            if (_cc.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
            else _verticalVelocity += _gravity * SimulationDeltaTime;
        }

        #endregion
    }
}
