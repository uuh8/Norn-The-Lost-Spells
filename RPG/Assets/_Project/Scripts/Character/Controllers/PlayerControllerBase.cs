using UnityEngine;
using Game.Core;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 玩家控制器基类（抽象）。承载各角色共同的 3C 与 Gameplay 基础能力：
    /// 移动 / 跳跃 / 冲刺 / 相机 / 输入计时器 / 状态机基础设施 / 接地三态 + Dash 态。
    /// 它只定义 TryStartAttack 等扩展入口，不知道具体攻击是近战、弓箭还是法术；角色专属攻击由子类实现。
    /// 共享逻辑只通过 Animator 参数名 / 状态节点名跟各自的 Animator Controller 对话，不绑定具体动画资源。
    /// </summary>
    // RequireComponent 是 Unity 的声明式依赖：把该脚本挂到 GameObject 时，Editor 会自动补齐这两个组件，
    // 使 Awake 中的 GetComponent 成为可靠依赖读取，而不是每帧查找或运行到一半才发现组件缺失。
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(GroundChecker))]
    public abstract class PlayerControllerBase : MonoBehaviour, IPlayerMovementSimulation
    {
        [SerializeField] private PlayerExecutionMode _executionMode;
        private bool _initialized, _inputSubscribed, _isSimulating, _isReplaying;
        private bool _jumpPressedPending, _dashPressedPending, _inputSuppressed;
        private bool _waitJumpRelease, _waitDashRelease, _hasFocus = true;
        private bool _hasReplicaPresentation;
        private PlayerLocomotionKind _lastReplicaPresentation;
        private float _simulationDeltaTime;

        public PlayerExecutionMode ExecutionMode => _executionMode;
        public bool OwnsLocalInput => _executionMode == PlayerExecutionMode.SinglePlayer || _executionMode == PlayerExecutionMode.Prediction;
        public bool CanPlayMovementFeedback => OwnsLocalInput && !_isReplaying;
        public float SimulationDeltaTime => _simulationDeltaTime;
        public Transform CameraRoot => _cameraRoot;

        /// <summary>
        /// 组合层把 Prefab 实例放在 inactive hierarchy 后先配置，再激活触发 Awake。
        /// 因而 Authority／Replica 从出生开始就不创建 Input、读取 Camera 或占用单机 Current。
        /// </summary>
        public void ConfigureExecutionMode(PlayerExecutionMode mode)
        {
            if (_executionMode == mode) return;
            UnbindLocalInput();
            _executionMode = mode;
            ClearOnlineInput();
            if (!_initialized) return;
            if (!OwnsLocalInput)
            {
                if (_inputActions != null) { _inputActions.Dispose(); _inputActions = null; }
                _mainCamera = null;
            }
            else if (isActiveAndEnabled) BindLocalInput();
            if (_executionMode != PlayerExecutionMode.SinglePlayer)
            {
                if (_animator != null) _animator.applyRootMotion = false;
                var portalInput = GetComponent<PortalInteractInput>();
                if (portalInput != null) portalInput.enabled = false;
            }
        }

        /// <summary>
        /// 只采样本地拥有者。performed 先锁存边沿，本方法读取后消费一次；同一 Rendering 帧补跑多 Tick
        /// 不会把 WasPressedThisFrame 重复当成多次跳跃。鼠标增量另在 LateUpdate 每帧采样一次。
        /// </summary>
        public PlayerSimulationFrame SampleInput(uint sequence, uint epoch, bool suppressed = false)
        {
            bool blocked = suppressed || !OwnsLocalInput || !_hasFocus || _inputActions == null;
            if (blocked)
            {
                ClearOnlineInput();
                return new PlayerSimulationFrame(sequence, epoch, Vector2.zero, _cameraYaw, false, false, false, true);
            }
            _inputSuppressed = false;
            bool jumpHeld = _inputActions.Player.Jump.IsPressed();
            bool dashHeld = _inputActions.Player.Dash.IsPressed();
            if (!jumpHeld) _waitJumpRelease = false;
            if (!dashHeld) _waitDashRelease = false;
            bool jumpPressed = _jumpPressedPending && !_waitJumpRelease;
            bool dashPressed = _dashPressedPending && !_waitDashRelease;
            _jumpPressedPending = _dashPressedPending = false;
            if (_mainCamera == null) _mainCamera = Camera.main;
            // 在线相机由组合层在角色Awake后创建；_mainCamera可能仍指向已禁用的连接界面Camera。
            // Prediction直接使用自己LateUpdate维护的轨道Yaw，单机保留原Camera方向语义。
            float yaw = _executionMode == PlayerExecutionMode.Prediction ? _cameraYaw :
                _mainCamera != null ? _mainCamera.transform.eulerAngles.y : _cameraYaw;
            return new PlayerSimulationFrame(sequence, epoch, _inputActions.Player.Move.ReadValue<Vector2>(), yaw,
                jumpPressed, jumpHeld && !_waitJumpRelease, dashPressed, false);
        }

        /// <summary>
        /// 唯一运动推进。Driver 从 Update 调用固定步长；单机 wrapper 传入渲染 deltaTime。
        /// Replay 复用相同运动规则，但禁止重复表现和对其他 Rigidbody 施加推力。
        /// </summary>
        public void Simulate(in PlayerSimulationFrame input, float deltaTime, bool replay = false)
        {
            if (!_initialized || _executionMode == PlayerExecutionMode.Replica || _isSimulating ||
                !IsFinite(deltaTime) || deltaTime <= 0f) return;
            _simulationDeltaTime = deltaTime;
            _isSimulating = true; _isReplaying = replay;
            try
            {
                if (_stateMachine.CurrentState == null)
                {
                    TargetFacing = transform.forward;
                    _stateMachine.ChangeState(_groundedState);
                }
                _groundChecker.Refresh();
                // 历史输入只恢复历史运动；不能把当前菜单／失焦的 Camera 门禁改回过去的值。
                if (!replay) _inputSuppressed = input.InputSuppressed;
                if (input.InputSuppressed)
                {
                    ClearSimulationInput();
                    _moveInput = Vector2.zero;
                }
                else
                {
                    _moveInput = IsFinite(input.MoveAxis.x) && IsFinite(input.MoveAxis.y)
                        ? Vector2.ClampMagnitude(input.MoveAxis, 1f) : Vector2.zero;
                    if (input.JumpPressed) JumpBufferCounter = _jumpBufferTime;
                    if (input.DashPressed) DashBufferCounter = _dashBufferTime;
                }
                float yaw = IsFinite(input.ViewYaw) ? input.ViewYaw : 0f;
                // 轨道相机无 roll；世界 yaw 足以还原水平前／右向量，服务器完全不依赖 Camera.main。
                _moveDirection = _moveInput.sqrMagnitude < .01f ? Vector3.zero :
                    Quaternion.Euler(0f, yaw, 0f) * new Vector3(_moveInput.x, 0f, _moveInput.y).normalized;
                TickInputCounters(deltaTime);
                VerticalVelocity = _jumpCutRuntime.Evaluate(VerticalVelocity, !input.InputSuppressed && input.JumpHeld);
                _stateMachine.Update();
                if (!replay && OwnsLocalInput) SyncAnimatorParameters();
            }
            finally { _isSimulating = false; _isReplaying = false; }
        }

        public PlayerMovementSnapshot CaptureMovementSnapshot()
        {
            return new PlayerMovementSnapshot
            {
                Position = transform.position, Rotation = transform.rotation, TargetFacing = TargetFacing,
                VerticalVelocity = VerticalVelocity, Locomotion = ResolveLocomotion(),
                IsGrounded = _groundChecker != null && _groundChecker.IsGrounded,
                GroundNormal = _groundChecker != null ? _groundChecker.GroundNormal : Vector3.up,
                GroundAngle = _groundChecker != null ? _groundChecker.GroundAngle : 0f,
                CoyoteTimeCounter = CoyoteTimeCounter, JumpBufferCounter = JumpBufferCounter,
                AttackBufferCounter = AttackBufferCounter, AttackCooldownCounter = AttackCooldownCounter,
                DashCooldownCounter = DashCooldownCounter, DashBufferCounter = DashBufferCounter,
                IsAirborneForDash = IsAirborneForDash,
                DashDirection = _dashState != null ? _dashState.Direction : Vector3.zero,
                DashElapsed = _dashState != null ? _dashState.Elapsed : 0f,
                ExtraJumpsRemaining = _airActionBudget != null ? _airActionBudget.RemainingExtraJumps : 0,
                AirDashesRemaining = _airActionBudget != null ? _airActionBudget.RemainingAirDashes : 0,
                JumpCutArmed = _jumpCutRuntime != null && _jumpCutRuntime.IsArmed
            };
        }

        /// <summary>
        /// 恢复运动根与所有影响重放的状态，绝不走普通 Enter／Exit，也不播 VFX。
        /// 暂停 CC 后传送可清除旧接触缓存；同步 Physics 后，下一次显式 Tick 才重新采样实际支撑。
        /// </summary>
        public void RestoreMovementSnapshot(in PlayerMovementSnapshot snapshot)
        {
            if (!_initialized) return;
            bool wasEnabled = _characterController.enabled;
            if (wasEnabled) _characterController.enabled = false;
            transform.SetPositionAndRotation(snapshot.Position, snapshot.Rotation);
            if (wasEnabled) _characterController.enabled = true;
            Physics.SyncTransforms();
            TargetFacing = snapshot.TargetFacing;
            VerticalVelocity = snapshot.VerticalVelocity;
            CoyoteTimeCounter = snapshot.CoyoteTimeCounter;
            JumpBufferCounter = snapshot.JumpBufferCounter;
            AttackBufferCounter = snapshot.AttackBufferCounter;
            AttackCooldownCounter = snapshot.AttackCooldownCounter;
            DashCooldownCounter = snapshot.DashCooldownCounter;
            DashBufferCounter = snapshot.DashBufferCounter;
            IsAirborneForDash = snapshot.IsAirborneForDash;
            _groundChecker.RestoreSnapshot(snapshot.IsGrounded, snapshot.GroundNormal, snapshot.GroundAngle);
            _dashState.RestoreSnapshot(snapshot.DashDirection, snapshot.DashElapsed);
            _airActionBudget.RestoreRemaining(snapshot.ExtraJumpsRemaining, snapshot.AirDashesRemaining);
            _jumpCutRuntime.RestoreArmed(snapshot.JumpCutArmed);
            PlayerStateBase state;
            switch (snapshot.Locomotion)
            {
                case PlayerLocomotionKind.Airborne: state = _airborneState; break;
                case PlayerLocomotionKind.Sliding: state = _slidingState; break;
                case PlayerLocomotionKind.Dash: state = _dashState; break;
                default: state = _groundedState; break;
            }
            _stateMachine.RestoreState(state);
        }

        /// <summary>Replica 只按插值结果更新 Animator；不读 Input、不调用 Move，也不运行 AI／战斗。</summary>
        public void PresentReplicaMovement(float normalizedMoveAmount)
        {
            if (_executionMode != PlayerExecutionMode.Replica || !_initialized) return;
            var kind = ResolveLocomotion();
            bool changed = !_hasReplicaPresentation || kind != _lastReplicaPresentation;
            _lastReplicaPresentation = kind; _hasReplicaPresentation = true;
            if (_animator == null || _animator.runtimeAnimatorController == null) return;
            if (changed && kind == PlayerLocomotionKind.Dash)
            {
                int hash = IsAirborneForDash ? _airDashStateHash : _dashStateHash;
                if (hash != 0) _animator.CrossFadeInFixedTime(hash, .1f, 0);
            }
            else if (changed && kind == PlayerLocomotionKind.Airborne && VerticalVelocity > 0f)
                PlayJumpStartAnimation();
            _moveInput = Vector2.up * Mathf.Clamp01(normalizedMoveAmount);
            SyncAnimatorParameters();
        }

        /// <summary>断线／菜单／失焦只清本地意图；冷却与运动状态仍由服务器 Tick 决定。</summary>
        public void ClearOnlineInput()
        {
            _inputSuppressed = true;
            _waitJumpRelease = _waitDashRelease = true;
            _jumpPressedPending = _dashPressedPending = false;
            _moveInput = Vector2.zero; _moveDirection = Vector3.zero; _lookInput = Vector2.zero;
            ClearSimulationInput();
        }

        private void ClearSimulationInput()
        {
            JumpBufferCounter = DashBufferCounter = AttackBufferCounter = 0f;
        }

        private void TickInputCounters(float deltaTime)
        {
            if (JumpBufferCounter > 0f) JumpBufferCounter -= deltaTime;
            if (CoyoteTimeCounter > 0f) CoyoteTimeCounter -= deltaTime;
            if (AttackBufferCounter > 0f) AttackBufferCounter -= deltaTime;
            if (AttackCooldownCounter > 0f) AttackCooldownCounter -= deltaTime;
            if (DashCooldownCounter > 0f) DashCooldownCounter -= deltaTime;
            if (DashBufferCounter > 0f) DashBufferCounter -= deltaTime;
        }

        private PlayerLocomotionKind ResolveLocomotion()
        {
            if (_stateMachine == null) return PlayerLocomotionKind.Grounded;
            var state = _stateMachine.CurrentState;
            if (ReferenceEquals(state, _dashState)) return PlayerLocomotionKind.Dash;
            if (ReferenceEquals(state, _airborneState)) return PlayerLocomotionKind.Airborne;
            if (ReferenceEquals(state, _slidingState)) return PlayerLocomotionKind.Sliding;
            return PlayerLocomotionKind.Grounded;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        [Header("Movement")] [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _rotationSpeed = 10f;

        [Header("Jump")] [SerializeField] private float _gravityMultiplier = 2f; // 跳跃上升阶段的重力加速度倍数
        [SerializeField] private float _jumpForce = 6f; // 跳跃初速度
        [SerializeField] private float _fallGravityMultiplier = 3.5f; // 跳跃下降阶段重力加速度倍数
        [SerializeField] private float _coyoteTime = 0.15f; // 离地后允许跳跃的宽限期（Coyote Time）
        [SerializeField] private float _jumpBufferTime = 0.15f; // 空中按下跳跃后，在地面的缓冲期（Jump Buffer）
        [SerializeField, Range(0f, 1f)] private float _jumpCutVelocityMultiplier = 0.5f; // 松键时保留的上升速度比例；1 = 关闭 Jump Cut
        [SerializeField, Min(0)] private int _extraAirJumps = 1; // 1 = 二段跳；落地时恢复预算

        [Header("Slope")] [SerializeField] private float _slideSpeed = 6f; // 滑落速度

        [Header("Interaction")] [SerializeField]
        private float _pushForce = 3f;

        [Header("Camera")] [SerializeField] private Transform _cameraRoot; // 相机枢轴，Inspector 里拖 CameraRoot
        [SerializeField] private float _lookSensitivity = 0.12f; // 灵敏度：度/像素
        [SerializeField] private float _pitchMin = -30f; // 最低俯角
        [SerializeField] private float _pitchMax = 70f; // 最高俯角

        [Header("Attack Input")] [SerializeField]
        private float _attackBufferTime = 0.15f; // 攻击缓冲时间（通用：按了攻击键的缓冲）

        [Header("Dash")] [SerializeField] private float _dashSpeed = 20f; // 冲刺水平速度
        [SerializeField] private float _dashDuration = 0.2f; // 冲刺位移持续时间（秒）
        [SerializeField] private float _dashCooldown = 1f; // 冲刺冷却（从 Exit 起算）
        [SerializeField] private float _dashBufferTime = 0.15f; // 冲刺输入缓冲（与攻击/跳跃同惯例）
        [SerializeField, Min(0)] private int _airDashesPerAirborne = 1; // 设为 0 可让某个角色保持仅地面 Dash
        [Tooltip("空中 Dash 开始时保留多少进入前垂直速度：0 = 清除，1 = 完整保留。Dash 期间仍暂停重力积分。")]
        [SerializeField, Range(0f, 1f)] private float _airDashVerticalVelocityRetention = 1f; // 共享默认保持旧行为；Wizard Prefab 显式设为 0
        [SerializeField] private float _dashMoveDelay = 0.1f; // 冲刺位移启动延迟（秒）：等翻滚动画起势后再位移，避免"先闪后翻"。设 0 = 进入即位移
        [SerializeField] private string _dashStateName = "DashForward_SingleTwohandSword"; // Dash 目标 Animator 状态名（数据驱动，各角色填自己 Controller 的节点名）
        [SerializeField] private string _airDashStateName = "AirDash"; // 与地面 Dash 分离，避免共用翻滚动画

        // Unity 组件引用：Awake 缓存一次，后续状态对象直接复用，避免在 Update 热路径反复 GetComponent。
        private CharacterController _characterController;
        private Animator _animator;
        private InputSystem_Actions _inputActions;
        private GroundChecker _groundChecker;
        private Camera _mainCamera;
        private StatusController _statusController;

        // 状态机与共享状态（Awake 创建一次，运行时切换只改引用，不产生 GC）
        private PlayerStateMachine _stateMachine;       // 初始化状态机
        private PlayerGroundedState _groundedState;
        private PlayerAirborneState _airborneState;
        private PlayerSlidingState _slidingState;
        private PlayerDashState _dashState;

        // 运行时数据
        private Vector2 _moveInput;
        private Vector3 _moveDirection;
        private Vector2 _lookInput;
        private float _cameraYaw;
        private float _cameraPitch;
        private float _settingsLookSensitivityMultiplier =
            UserSettingsValues.DefaultMouseSensitivity;

        // 持续状态型 Animator 参数 hash（Controller 每帧统一同步）
        private static readonly int SpeedHash = Animator.StringToHash("speed");
        private static readonly int IsGroundedHash = Animator.StringToHash("isGrounded");
        private static readonly int JumpHash = Animator.StringToHash("jump");

        // Dash 目标状态名预 hash（Awake 算一次，绝不每帧/每次触发 StringToHash）
        private int _dashStateHash;
        private int _airDashStateHash;
        private PlayerAirActionBudget _airActionBudget;
        private JumpCutRuntime _jumpCutRuntime;

        // ── 对外暴露给 State 的属性 ──
        public CharacterController CharacterController => _characterController;
        public GroundChecker GroundChecker => _groundChecker;
        public void MoveCharacter(Vector3 displacement)
        {
            // Replica 从 Snapshot 定位；在线运动只允许在显式 Simulate 内写 CC，阻断遗留自动路径。
            if (_executionMode == PlayerExecutionMode.Replica ||
                (_executionMode != PlayerExecutionMode.SinglePlayer && !_isSimulating)) return;
            // Move更新实际碰撞后立即采样，State的落地/离地转换使用本帧位置，不等待另一个MonoBehaviour.Update。
            _characterController.Move(displacement);_groundChecker.Refresh();
        }
        public Animator Animator => _animator;
        public Camera MainCamera => _mainCamera; // 暴露给瞄准（屏幕中心射线 + 转向相机朝向）
        /// <summary>本机拥有输入的玩家。线上敌人使用注入的 Roster，不再把这个本地入口当成全体玩家。</summary>
        public static PlayerControllerBase Current { get; private set; }
        public Vector2 MoveInput => _moveInput;
        public Vector3 MoveDirection => _moveDirection;
        /// <summary>锁存的目标朝向（水平）：按下方向时更新为该方向，松开后仍朝它转到位，避免"转身中途停下"。</summary>
        public Vector3 TargetFacing { get; set; } = Vector3.forward;
        public float MoveSpeed => _moveSpeed;
        public float StatusMoveSpeedMultiplier => _statusController != null ? _statusController.MoveSpeedMultiplier : 1f;
        public float RotationSpeed => _rotationSpeed;
        public float VerticalVelocity { get; set; }
        public float JumpForce => _jumpForce;
        public float GravityMultiplier => _gravityMultiplier;
        public float FallGravityMultiplier => _fallGravityMultiplier;
        public float CoyoteTime => _coyoteTime;
        public float JumpBufferTime => _jumpBufferTime;
        public float SlideSpeed => _slideSpeed;
        public float CoyoteTimeCounter { get; set; }
        public float JumpBufferCounter { get; set; }

        public PlayerStateMachine StateMachine => _stateMachine;
        public PlayerGroundedState GroundedState => _groundedState;
        public PlayerAirborneState AirborneState => _airborneState;
        public PlayerSlidingState SlidingState => _slidingState;
        public PlayerDashState DashState => _dashState;

        /// <summary>
        /// 通用攻击输入剩余有效时间；大于 0 表示玩家已发出一次攻击请求，但当前状态尚未消费。
        /// 所有角色共用这一份“待处理攻击意图”，避免子类重复维护 bool + timer。
        /// </summary>
        public float AttackBufferCounter { get; set; }
        public float AttackBufferTime => _attackBufferTime;

        /// <summary>
        /// 攻击冷却剩余秒数；大于 0 时不能开始下一次攻击，由 Controller 每帧统一递减。
        /// Buffer 记录“玩家想攻击”，Cooldown 记录“系统暂时不能攻击”，两者职责不同。
        /// </summary>
        public float AttackCooldownCounter { get; set; } // 时长来自攻击时序配置的 AttackCooldown

        public float DashSpeed => _dashSpeed;
        public float DashDuration => _dashDuration;
        public float DashCooldown => _dashCooldown;
        public float DashBufferTime => _dashBufferTime;
        public float DashMoveDelay => _dashMoveDelay;
        public float DashCooldownCounter { get; set; }
        public float DashBufferCounter { get; set; }
        public int DashStateHash => _dashStateHash;
        public int AirDashStateHash => _airDashStateHash;
        public bool IsAirborneForDash { get; set; }     // 表示本次 Dash 是否从 Airborne State 发起
        public float AirDashVerticalVelocityRetention => _airDashVerticalVelocityRetention;

        public void ResetAirActionBudget() => _airActionBudget.ResetForGrounded();
        public bool TryConsumeExtraJump() => _airActionBudget.TryConsumeExtraJump();
        public bool TryConsumeAirDash() => _airActionBudget.TryConsumeAirDash();

        /// <summary>
        /// 只在真正赋予 JumpForce 时调用，而不是在按键回调里调用。
        /// 这样提前按下并松开的 Jump Buffer 在落地起跳后仍会被识别为短跳。
        /// </summary>
        public void ArmJumpCut() => _jumpCutRuntime.Arm();

        /// <summary>
        /// 起跳动画入口 seam。原型角色继续使用 jump Trigger；Wizard 重写后用可调时长的
        /// CrossFade 重播 JumpStart，避免 Animator Controller 中写死 Transition Duration。
        /// </summary>
        public virtual void PlayJumpStartAnimation()
        {
            if (_animator != null && _animator.runtimeAnimatorController != null &&
                _executionMode != PlayerExecutionMode.Authority && !_isReplaying)
                _animator.SetTrigger(JumpHash);
        }

        /// <summary>
        /// Gameplay 的统一“真正起跳”反馈入口。普通跳、Coyote Jump、二段跳都走这里，
        /// 因而角色专属动画与 VFX 不会因新增起跳路径而漏播。
        /// </summary>
        public void PlayJumpStartedFeedback()
        {
            if (!CanPlayMovementFeedback) return;
            PlayJumpStartAnimation();
            PlayJumpVfx();
        }

        /// <summary>角色专属起跳 VFX seam；共享 locomotion 不依赖具体美术 Prefab。</summary>
        protected virtual void PlayJumpVfx() { }

        /// <summary>
        /// 落地时清除 Trigger 型 Animator 的陈旧起跳请求。Wizard 虽已改为代码驱动，
        /// 保留统一清理可防止旧 Controller 数据或热重载遗留 Trigger。
        /// </summary>
        public void ClearPendingJumpAnimation()
        {
            if (CanPlayMovementFeedback && _animator != null && _animator.runtimeAnimatorController != null)
                _animator.ResetTrigger(JumpHash);
        }

        // IsPressed 持续按住为 true，松开为 false。
        // WasPressedThisFrame用于检测“按下”的上升沿。核心特性：只在按下的那一帧返回 true
        // WasReleasedThisFrame用于检测“松开”的下降沿。核心特性：只在松开的那个帧返回 true
        // ReadValue<>()用于读取当前输入的值。例如 moveAction.ReadValue<Vector2>() 读取摇杆偏移。
        public bool IsAttackHeld => _executionMode == PlayerExecutionMode.SinglePlayer && _inputActions != null && _inputActions.Player.Attack.IsPressed();
        public bool AttackPressedThisFrame => _executionMode == PlayerExecutionMode.SinglePlayer && _inputActions != null && _inputActions.Player.Attack.WasPressedThisFrame();

        /// <summary>
        /// 攻击触发 seam：共享的 GroundedState 在攻击优先级位调用本钩子。
        /// 基类默认不攻击（返回 false）；具体角色在子类重写：消耗攻击输入并切到自己的攻击状态，
        /// </summary>
        public virtual bool TryStartAttack() => false;  // 基类：安全的空实现

        /// <summary>
        /// 空中攻击触发 seam：共享的 AirborneState 在落地检测前调用本钩子。
        /// 基类默认不支持空中攻击（返回 false）；具体角色在子类重写（如法师空中火球普攻）。
        /// 与 TryStartAttack 平行：地面/空中两条入口，各角色自行决定是否实现空中分支。
        /// </summary>
        public virtual bool TryStartAirAttack() => false;  // 基类：安全的空实现

        /// <summary>
        /// 角色专属 Animator 参数同步 seam。共享基类只维护 speed/isGrounded；
        /// 具体角色可追加自己 Controller 中真实存在的参数，避免向其他 Animator 写入不存在的参数。
        /// </summary>
        protected virtual void SyncCharacterAnimatorParameters(bool animIsGrounded) { }

        /// <summary>
        /// 屏幕中心发射线 -> RaycastNonAlloc（跳过射手自身碰撞体）-> 取最近命中；未命中则取相机朝向 maxDistance 远点。
        /// buffer 由调用方预分配复用，避免每次点击创建 RaycastHit[]；相机缺失时退化为角色前向远点。
        /// </summary>
        public Vector3 ResolveAimTargetPoint(
            LayerMask aimMask,
            float maxDistance,
            RaycastHit[] buffer)
        {
            Camera cam = _mainCamera;
            if (cam == null)
                return transform.position + transform.forward * maxDistance;

            // ViewportPointToRay 把 Viewport 坐标转换为世界空间射线；(0.5, 0.5) 正好是屏幕中心/准星位置。
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

            // RaycastNonAlloc 把命中结果写进调用方提供的数组，不像 RaycastAll 那样返回新数组。
            // 返回 int count 是为了告诉调用者：数组的前 count 个元素是本次检测的有效结果，请不要越界访问。
            int count = Physics.RaycastNonAlloc(
                ray,
                buffer,
                maxDistance,
                aimMask,
                QueryTriggerInteraction.Ignore
            ); // Ignore 表示跳过 Trigger，只让真实场景碰撞体决定瞄准点。

            // NonAlloc 查询不承诺按距离排序，因此必须遍历有效区间 [0, count) 并自行选择最近命中。
            float nearest = float.MaxValue;
            bool found = false;
            Vector3 point = default;

            for (int i = 0; i < count; i++)
            {
                // IsChildOf 判断 Collider 是否属于角色层级，防止射线从相机出发后先命中自己的模型/碰撞体。
                if (buffer[i].collider.transform.IsChildOf(transform)) continue;

                if (buffer[i].distance < nearest)
                {
                    nearest = buffer[i].distance;
                    point = buffer[i].point;
                    found = true;
                }
            }
            // Ray.GetPoint(distance) 返回射线上指定距离处的世界坐标，为“瞄向天空”提供稳定的远端目标。
            return found ? point : ray.GetPoint(maxDistance);
        }

        #region Unity 事件函数

        protected virtual void Awake()
        {
            if (_initialized) return;
            // 所有玩家共享同一过门视觉接口；仅移动Animator模型，不移动CharacterController物理根。
            // 在线出入口由组合层管理；Authority／Replica 不得偷偷增加读取本机 F 的组件。
            if (_executionMode == PlayerExecutionMode.SinglePlayer)
            {
                if (GetComponent<PlayerPortalTraveler>() == null) gameObject.AddComponent<PlayerPortalTraveler>();
                if (GetComponent<PortalInteractInput>() == null) gameObject.AddComponent<PortalInteractInput>();
            }
            else
            {
                var portalInput = GetComponent<PortalInteractInput>();
                if (portalInput != null) portalInput.enabled = false;
            }
            // GetComponent 在当前 GameObject 上查组件；GetComponentInChildren 还会搜索子层级，
            // Animator 通常挂在角色模型子物体上，所以两者不能混用。
            _characterController = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>();
            _groundChecker = GetComponent<GroundChecker>();
            _groundChecker.UseControllerSampling();
            _statusController = GetComponent<StatusController>();
            if (OwnsLocalInput)
            {
                _inputActions = new InputSystem_Actions(); // 只为本机拥有者创建强类型输入包装器。
                _mainCamera = Camera.main;
                if (_executionMode == PlayerExecutionMode.Prediction && _cameraRoot != null)
                {
                    // 线上出生点可能有任意 yaw。枢轴世界角才是首帧 Camera 的起点，不能首帧归零。
                    Vector3 angles = _cameraRoot.eulerAngles;
                    _cameraYaw = angles.y;
                    _cameraPitch = Mathf.Clamp(angles.x > 180f ? angles.x - 360f : angles.x, _pitchMin, _pitchMax);
                }
            }
            if (_executionMode != PlayerExecutionMode.SinglePlayer && _animator != null)
                _animator.applyRootMotion = false; // 线上物理根只由显式运动或 Replica Snapshot 写入。

            // 初始化FSM State（这是共有的状态，私有的状态在各自的Controller中单独初始化）
            _stateMachine = new PlayerStateMachine();
            _groundedState = new PlayerGroundedState(this);
            _airborneState = new PlayerAirborneState(this);
            _slidingState = new PlayerSlidingState(this);
            _dashState = new PlayerDashState(this);
            _airActionBudget = new PlayerAirActionBudget(_extraAirJumps, _airDashesPerAirborne);
            _jumpCutRuntime = new JumpCutRuntime(_jumpCutVelocityMultiplier);

            // Dash 目标状态名预 hash（数据驱动；空串记 0 并告警，CrossFade 0 不切动画——与连段空状态名同款防御）
            if (string.IsNullOrEmpty(_dashStateName))
            {
                _dashStateHash = 0;
                GameLog.Warn("_dashStateName 为空，冲刺 CrossFade 将无法切换动画", "Character");
            }
            else
            {
                _dashStateHash = Animator.StringToHash(_dashStateName);
            }

            if (string.IsNullOrEmpty(_airDashStateName))
            {
                _airDashStateHash = 0;
                GameLog.Warn("_airDashStateName 为空，空中冲刺只会位移而不会切换专属动画", "Character");
            }
            else
            {
                _airDashStateHash = Animator.StringToHash(_airDashStateName);
            }
            _initialized = true;
        }

        protected virtual void Start()
        {
            // 组合层可能已在 Start 前应用出生／重连 Snapshot，不能再把恢复后的阶段改成 Grounded。
            if (_stateMachine.CurrentState != null) return;
            // Start 而非 Awake 进入初始状态：保证所有 GameObject 的 Awake 已执行完
            TargetFacing = transform.forward;           // 锁存初值 = 出生朝向，避免开局自转到世界 +Z
            _stateMachine.ChangeState(_groundedState);  // 将初始状态切到 GroundedState。
        }

        protected virtual void OnEnable()
        {
            if (OwnsLocalInput) BindLocalInput();
        }

        protected virtual void OnDisable()
        {
            UnbindLocalInput();
            ClearOnlineInput();
        }

        protected virtual void OnDestroy()
        {
            UnbindLocalInput();
            if (_inputActions != null) { _inputActions.Dispose(); _inputActions = null; }
        }

        private void BindLocalInput()
        {
            if (_inputSubscribed || !OwnsLocalInput) return;
            if (_inputActions == null) _inputActions = new InputSystem_Actions();
            if (_mainCamera == null) _mainCamera = Camera.main;
            EventBus<UserSettingsChangedEvent>.Subscribe(OnUserSettingsChanged);
            ApplyUserSettings(UserSettingsService.Current);
            _inputActions.Player.Jump.performed += OnJumpPerformed;
            _inputActions.Player.Attack.performed += OnAttackPerformed;
            _inputActions.Player.Dash.performed += OnDashPerformed;
            _inputActions.Player.Enable();
            _inputSubscribed = true;
            _inputSuppressed = false;
            _waitJumpRelease = _waitDashRelease = _executionMode == PlayerExecutionMode.Prediction;
            Current = this;
        }

        private void UnbindLocalInput()
        {
            if (_inputSubscribed)
            {
                EventBus<UserSettingsChangedEvent>.Unsubscribe(OnUserSettingsChanged);
                _inputActions.Player.Jump.performed -= OnJumpPerformed;
                _inputActions.Player.Attack.performed -= OnAttackPerformed;
                _inputActions.Player.Dash.performed -= OnDashPerformed;
                _inputActions.Player.Disable();
                _inputSubscribed = false;
            }
            if (Current == this) Current = null;
        }

        // CharacterController 不是由 Rigidbody Physics Step 推进的动态刚体；角色主动调用 Move，故放在 Update 跟随输入帧率。
        private void Update()
        {
            // Authority／Prediction 由 Root 的固定 Tick 调用；Replica 只应用 Snapshot。
            if (_executionMode != PlayerExecutionMode.SinglePlayer) return;
            // 暂停时（如打开法杖编程界面 Time.timeScale=0）玩家完全惰性：不读输入、不跑状态机、不入队攻击。
            // 否则在 UI 里左键拖拽会被误读为"按下攻击"，关面板恢复时间瞬间凭空放一发。
            if (Time.timeScale == 0f) return;

            _lookInput = _inputActions.Player.Look.ReadValue<Vector2>();
            var frame = SampleInput(0, 0);
            Simulate(frame, Time.deltaTime);
        }

        private void LateUpdate()
        {
            if (!OwnsLocalInput || _inputSuppressed || !_hasFocus || _inputActions == null) return;
            // 暂停时不转轨道相机：打开编程界面解锁鼠标后，晃鼠标不应让镜头乱晃。
            if (Time.timeScale == 0f) return;

            // 在 LateUpdate 旋转相机枢轴：发生在所有 Update（角色移动）之后
            if (_executionMode == PlayerExecutionMode.Prediction)
                _lookInput = _inputActions.Player.Look.ReadValue<Vector2>();
            HandleCameraRotation();
        }

        private void OnApplicationFocus(bool focused)
        {
            _hasFocus = focused;
            if (!focused && _executionMode != PlayerExecutionMode.SinglePlayer) ClearOnlineInput();
        }

        /// <summary>
        /// 当 CharacterController（角色控制器）在移动过程中与其他带有碰撞体的物体发生碰撞时，引擎会自动调用此函数
        /// 并传入 ControllerColliderHit 类型的参数 hit，其中包含了碰撞的详细信息。
        /// </summary>
        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            // 本地预测只校正自己，绝不能在每次 Replay 时再次给动态世界施加冲量。
            if (_isReplaying || (_executionMode != PlayerExecutionMode.SinglePlayer &&
                _executionMode != PlayerExecutionMode.Authority)) return;
            Rigidbody rb = hit.rigidbody;
            if (rb == null) return; // 静态物体不推
            if (rb.isKinematic) return; // Kinematic 不推
            if (hit.moveDirection.y < -0.3f) return; // 踩在物件上不推
            Vector3 pushDir = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
            rb.AddForce(pushDir * _pushForce, ForceMode.VelocityChange);    // ForceMode.VelocityChange 表示直接改变物体的速度，忽略其质量（即无论物体轻重，都能被推开相同的初始速度）。
        }

        #endregion


        #region 事件回调

        private void OnJumpPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            if (!OwnsLocalInput || Time.timeScale == 0f || !_hasFocus) return;
            if (_executionMode == PlayerExecutionMode.SinglePlayer) JumpBufferCounter = _jumpBufferTime;
            else if (!_inputSuppressed && !_waitJumpRelease) _jumpPressedPending = true;
        }

        /// <summary>
        /// Input System 判定 Attack Action 满足触发条件时调用。
        /// 子类可重写它，在同一个输入时刻补充角色专属快照，例如 Wizard 的点击瞄准点。
        /// </summary>
        protected virtual void OnAttackPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            if (_executionMode != PlayerExecutionMode.SinglePlayer) return;
            // performed 是 Input System 判定该 Action 完成一次交互时触发的回调。
            // 暂停界面也会使用鼠标；UI 点击不能泄漏成恢复游戏后的攻击请求。
            if (Time.timeScale == 0f)
                return;

            AttackBufferCounter = _attackBufferTime;
        }

        private void OnDashPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            if (!OwnsLocalInput || Time.timeScale == 0f || !_hasFocus) return;
            if (_executionMode == PlayerExecutionMode.SinglePlayer) DashBufferCounter = _dashBufferTime;
            else if (!_inputSuppressed && !_waitDashRelease) _dashPressedPending = true;
        }

        private void OnUserSettingsChanged(UserSettingsChangedEvent settingsEvent)
        {
            ApplyUserSettings(settingsEvent.Values);
        }

        private void ApplyUserSettings(UserSettingsValues values)
        {
            // Inspector 中的 _lookSensitivity 仍是角色/设备的基础“度/像素”；用户设置只作为 Clamp 后的倍率。
            // 这样 Settings 不会抹掉不同角色的 Authoring 差异，也不需要让 Game.UI 反向引用 Character。
            _settingsLookSensitivityMultiplier = values.MouseSensitivity;
        }

        #endregion

        private void SyncAnimatorParameters()
        {
            if (_animator == null || _animator.runtimeAnimatorController == null) return;
            // 设置速度参数，确保值在0-1之间
            _animator.SetFloat(SpeedHash, Mathf.Clamp01(_moveInput.magnitude));
            // 检查角色是否处于地面状态且垂直速度小于等于0
            bool animIsGrounded = _groundChecker.IsGrounded && VerticalVelocity <= 0f;
            // 更新地面状态参数
            _animator.SetBool(IsGroundedHash, animIsGrounded);
            SyncCharacterAnimatorParameters(animIsGrounded);
        }

        private void HandleCameraRotation()
        {
            if (_cameraRoot == null) return;
            float effectiveSensitivity =
                _lookSensitivity * _settingsLookSensitivityMultiplier;
            _cameraYaw += _lookInput.x * effectiveSensitivity;
            _cameraPitch -= _lookInput.y * effectiveSensitivity;
            _cameraPitch = Mathf.Clamp(_cameraPitch, _pitchMin, _pitchMax);
            _cameraRoot.rotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
        }

    }
}
