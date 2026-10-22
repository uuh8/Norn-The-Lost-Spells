# PlayerControllerBase 抽取（Archer Phase 1）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把单体 `PlayerController` 拆成 `PlayerControllerBase`（共享移动/跳跃/冲刺/相机/状态机+接地三态+Dash）+ `WarriorController`（连段攻击专属）+ `ArcherController`（空骨架），为第二个角色 Archer 铺底，且 **Warrior 行为零变更、预制体引用零丢失**。

**Architecture:** 抽象基类承载共享能力；共享状态（Grounded/Airborne/Sliding/Dash）的 `_player` 改指基类；攻击触发用虚钩子 `TryStartAttack()` 解耦；Dash 目标动画状态名改为序列化字段数据驱动。执行上**先原地提取基类（GUID 不变）、最后连同 `.meta` 重命名**，绝不删文件重建。

**Tech Stack:** Unity 6.3 / C# / `Game.Character` asmdef（不触 `Game.Combat`）/ Unity Input System / `CharacterController`。

## Global Constraints

- 全程只动 `Game.Character`；**不修改 `Game.Combat`** 任何文件（`MeleeHitDetector`/`AttackDefinition`/`ComboResolver` 等）。
- **零行为变更**：locomotion / 攻击的手感数值与逻辑、Update 顺序、计时器递减、Dash→Attack→Jump 优先级，全部逐位保持。
- **保住脚本 GUID**：禁止"删 `PlayerController.cs` + 新建 `WarriorController.cs`"。重命名时连同既有 `.meta` 一起 `git mv`（移动既有 `.meta`，不重建 GUID）。
- 不调用 `Debug.Log`，用 `Game.Core.GameLog`。命名：私有/保护字段 `_camelCase`，公开成员/类型 `PascalCase`。每个脚本声明 `namespace Game.Character`。
- 热路径（Update/状态 Update）禁止 `new`/LINQ/装箱；状态实例只在 `Awake` 创建一次。
- Claude 只改 `.cs` 与纯文本；**编译与 Play 模式验证由开发者在 Unity 手动完成**，Claude 不声称"能跑"，只保证静态逻辑等价。
- 不创建新 `.meta`（新建 `.cs` 的 `.meta` 由 Unity 自动生成）；唯一例外是 Task 3 用 `git mv` **移动**既有 `.meta`。
- 不动空壳 `PlayerLocomotion.cs`（非目标）。

**每个 Claude 任务的"验证"统一为**：完成代码后跑该任务的静态自检清单 → 提交。开发者随后在 Unity 编译（Task 1–3 之间可各做一次中途编译确认）。真正的功能验收在 Task 4（开发者 Play 模式回归）。

设计依据：`docs/superpowers/specs/2026-06-22-player-controller-base-extraction-design.md`。

---

## File Structure

- **Create** `Assets/_Project/Scripts/Character/PlayerControllerBase.cs` — 抽象基类，共享能力（Task 1）。
- **Modify** `Assets/_Project/Scripts/Character/PlayerController.cs` — 瘦身为只剩 Warrior 专属、继承基类（Task 1）；加 `TryStartAttack` 重写（Task 2）；Task 3 连 `.meta` 重命名为 `WarriorController.cs`。
- **Create** `Assets/_Project/Scripts/Character/ArcherController.cs` — 空骨架（Task 2）。
- **Modify** `Assets/_Project/Scripts/Character/States/PlayerStateBase.cs` — `_player` 改 `PlayerControllerBase`（Task 2）。
- **Modify** `Assets/_Project/Scripts/Character/States/PlayerGroundedState.cs` — 攻击分支改 `TryStartAttack()`（Task 2）。
- **Modify** `PlayerAirborneState.cs` / `PlayerSlidingState.cs` — 构造参数改基类（Task 2）。
- **Modify** `PlayerDashState.cs` — 构造参数改基类 + 用 `_player.DashStateHash`（Task 2）。
- **Modify** `PlayerAttackState.cs` — 构造接 Warrior 类、存 typed `_warrior`（Task 2）；类名随 Task 3 重命名同步更新。

---

## Task 1：提取 PlayerControllerBase（原地，GUID 不变，可编译）

**Files:**
- Create: `Assets/_Project/Scripts/Character/PlayerControllerBase.cs`
- Modify: `Assets/_Project/Scripts/Character/PlayerController.cs`（整文件替换为瘦身版）

**Interfaces:**
- Produces（供后续任务依赖的基类公开面，签名精确）：
  - 属性：`CharacterController CharacterController`、`GroundChecker GroundChecker`、`Animator Animator`、`Vector2 MoveInput`、`Vector3 MoveDirection`、`float MoveSpeed`、`float RotationSpeed`、`float VerticalVelocity {get;set;}`、`float JumpForce`、`float GravityMultiplier`、`float FallGravityMultiplier`、`float CoyoteTime`、`float JumpBufferTime`、`float SlideSpeed`、`float CoyoteTimeCounter {get;set;}`、`float JumpBufferCounter {get;set;}`、`PlayerStateMachine StateMachine`、`PlayerGroundedState GroundedState`、`PlayerAirborneState AirborneState`、`PlayerSlidingState SlidingState`、`PlayerDashState DashState`、`float AttackBufferCounter {get;set;}`、`float AttackBufferTime`、`float DashSpeed`、`float DashDuration`、`float DashCooldown`、`float DashBufferTime`、`float DashCooldownCounter {get;set;}`、`float DashBufferCounter {get;set;}`、`int DashStateHash`。
  - 虚方法：`public virtual bool TryStartAttack()`（默认 `return false;`）。
  - 生命周期钩子：`protected virtual void Awake()`（已创建组件/状态机/共享状态/dash hash）。
- 本任务后 `PlayerStateBase._player` 仍是 `PlayerController` 类型（Task 2 才改）。`PlayerController : PlayerControllerBase` 仍持有 `AttackState` 属性（GroundedState 本任务仍走旧路径）。

- [ ] **Step 1：新建 `PlayerControllerBase.cs`，写入以下完整内容**

```csharp
using UnityEngine;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 角色控制器基类（抽象）。承载 Warrior / Archer 共享的能力：
    /// 移动 / 跳跃 / 冲刺 / 相机 / 输入计时器 / 状态机基础设施 / 接地三态 + Dash 态。
    /// 不含任何角色专属攻击逻辑——那在 WarriorController / ArcherController 子类。
    /// 共享逻辑只通过 Animator 参数名 / 状态节点名跟各自的 Animator Controller 对话，不绑定具体动画资源。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(GroundChecker))]
    public abstract class PlayerControllerBase : MonoBehaviour
    {
        [Header("Movement")] [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _rotationSpeed = 10f;

        [Header("Jump")] [SerializeField] private float _gravityMultiplier = 2f; // 跳跃上升阶段的重力加速度倍数
        [SerializeField] private float _jumpForce = 6f; // 跳跃初速度
        [SerializeField] private float _fallGravityMultiplier = 3.5f; // 跳跃下降阶段重力加速度倍数
        [SerializeField] private float _coyoteTime = 0.15f; // 离地后允许跳跃的宽限期（Coyote Time）
        [SerializeField] private float _jumpBufferTime = 0.15f; // 空中按下跳跃后，在地面的缓冲期（Jump Buffer）

        [Header("Slope")] [SerializeField] private float _slideSpeed = 6f; // 滑落速度

        [Header("Interaction")] [SerializeField] private float _pushForce = 3f;

        [Header("Camera")] [SerializeField] private Transform _cameraRoot; // 相机枢轴，Inspector 里拖 CameraRoot
        [SerializeField] private float _lookSensitivity = 0.12f; // 灵敏度：度/像素
        [SerializeField] private float _pitchMin = -30f; // 最低俯角
        [SerializeField] private float _pitchMax = 70f; // 最高俯角

        [Header("Attack Input")] [SerializeField] private float _attackBufferTime = 0.15f; // 攻击缓冲时间（通用：按了攻击键的缓冲）

        [Header("Dash")] [SerializeField] private float _dashSpeed = 20f;      // 冲刺水平速度
        [SerializeField] private float _dashDuration = 0.2f;                    // 冲刺持续时间（秒）
        [SerializeField] private float _dashCooldown = 1f;                      // 冲刺冷却（从 Exit 起算）
        [SerializeField] private float _dashBufferTime = 0.15f;                 // 冲刺输入缓冲（与攻击/跳跃同惯例）
        [SerializeField] private string _dashStateName = "DashForward_SingleTwohandSword"; // Dash 目标 Animator 状态名（数据驱动，各角色填自己 Controller 的节点名）

        // 组件引用
        private CharacterController _characterController;
        private Animator _animator;
        private InputSystem_Actions _inputActions;
        private GroundChecker _groundChecker;
        private Camera _mainCamera;

        // 状态机与共享状态（Awake 创建一次，运行时切换只改引用，不产生 GC）
        private PlayerStateMachine _stateMachine;
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

        // 持续状态型 Animator 参数 hash（Controller 每帧统一同步）
        private static readonly int SpeedHash = Animator.StringToHash("speed");
        private static readonly int IsGroundedHash = Animator.StringToHash("isGrounded");

        // Dash 目标状态名预 hash（Awake 算一次，绝不每帧/每次触发 StringToHash）
        private int _dashStateHash;

        // ── 对外暴露给 State 的属性 ──
        public CharacterController CharacterController => _characterController;
        public GroundChecker GroundChecker => _groundChecker;
        public Animator Animator => _animator;
        public Vector2 MoveInput => _moveInput;
        public Vector3 MoveDirection => _moveDirection;
        public float MoveSpeed => _moveSpeed;
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

        public float AttackBufferCounter { get; set; }
        public float AttackBufferTime => _attackBufferTime;

        public float DashSpeed => _dashSpeed;
        public float DashDuration => _dashDuration;
        public float DashCooldown => _dashCooldown;
        public float DashBufferTime => _dashBufferTime;
        public float DashCooldownCounter { get; set; }
        public float DashBufferCounter { get; set; }
        public int DashStateHash => _dashStateHash;

        /// <summary>
        /// 攻击触发 seam：共享的 GroundedState 在攻击优先级位调用本钩子。
        /// 基类默认不攻击（返回 false）；具体角色在子类重写：消耗攻击输入并切到自己的攻击状态，
        /// 切了返回 true（GroundedState 据此短路 return，保留 Dash→Attack→Jump 优先级）。
        /// </summary>
        public virtual bool TryStartAttack() => false;

        #region Unity事件函数

        protected virtual void Awake()
        {
            _characterController = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>(); // Animator 在子节点上，用 GetComponentInChildren
            _groundChecker = GetComponent<GroundChecker>();
            _inputActions = new InputSystem_Actions();
            _mainCamera = Camera.main;

            _stateMachine = new PlayerStateMachine();
            _groundedState = new PlayerGroundedState(this);
            _airborneState = new PlayerAirborneState(this);
            _slidingState = new PlayerSlidingState(this);
            _dashState = new PlayerDashState(this);

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
        }

        private void Start()
        {
            // Start 而非 Awake 进入初始状态：保证所有 GameObject 的 Awake 已执行完
            _stateMachine.ChangeState(_groundedState);
            Cursor.lockState = CursorLockMode.Locked; // 锁定并隐藏鼠标
        }

        private void OnEnable()
        {
            _inputActions.Player.Enable();
            _inputActions.Player.Jump.performed += OnJumpPerformed;
            _inputActions.Player.Attack.performed += OnAttackPerformed;
            _inputActions.Player.Dash.performed += OnDashPerformed;
        }

        private void OnDisable()
        {
            _inputActions.Player.Jump.performed -= OnJumpPerformed;
            _inputActions.Player.Attack.performed -= OnAttackPerformed;
            _inputActions.Player.Dash.performed -= OnDashPerformed;
            _inputActions.Player.Disable();
        }

        // CharacterController 不走物理管线，在 Update 里 Move 才能每帧响应输入
        private void Update()
        {
            _moveInput = _inputActions.Player.Move.ReadValue<Vector2>();
            _lookInput = _inputActions.Player.Look.ReadValue<Vector2>();
            _moveDirection = CalculateMoveDirection();

            // 计时器统一在 Controller 递减（全局数据，与当前 State 无关）
            if (JumpBufferCounter > 0f) JumpBufferCounter -= Time.deltaTime;
            if (CoyoteTimeCounter > 0f) CoyoteTimeCounter -= Time.deltaTime;
            if (AttackBufferCounter > 0f) AttackBufferCounter -= Time.deltaTime;
            if (DashCooldownCounter > 0f) DashCooldownCounter -= Time.deltaTime;
            if (DashBufferCounter > 0f) DashBufferCounter -= Time.deltaTime;

            // ① 先驱动状态机（可能改 VerticalVelocity）② 后同步 Animator（拿最终数据）——顺序不能反
            _stateMachine.Update();
            SyncAnimatorParameters();
        }

        private void LateUpdate()
        {
            // 在 LateUpdate 旋转相机枢轴：发生在所有 Update（角色移动）之后
            HandleCameraRotation();
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            Rigidbody rb = hit.rigidbody;
            if (rb == null) return;          // 静态物体不推
            if (rb.isKinematic) return;      // Kinematic 不推
            if (hit.moveDirection.y < -0.3f) return; // 踩在物件上不推
            Vector3 pushDir = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
            rb.AddForce(pushDir * _pushForce, ForceMode.VelocityChange);
        }

        #endregion

        private void SyncAnimatorParameters()
        {
            _animator.SetFloat(SpeedHash, Mathf.Clamp01(_moveInput.magnitude));
            bool animIsGrounded = _groundChecker.IsGrounded && VerticalVelocity <= 0f;
            _animator.SetBool(IsGroundedHash, animIsGrounded);
        }

        private Vector3 CalculateMoveDirection()
        {
            if (_moveInput.sqrMagnitude < 0.01f) return Vector3.zero;

            Vector3 cameraForward = _mainCamera.transform.forward;
            Vector3 cameraRight = _mainCamera.transform.right;
            cameraForward.y = 0f;
            cameraRight.y = 0f;
            cameraForward.Normalize();
            cameraRight.Normalize();

            return (cameraForward * _moveInput.y + cameraRight * _moveInput.x).normalized;
        }

        private void HandleCameraRotation()
        {
            _cameraYaw += _lookInput.x * _lookSensitivity;
            _cameraPitch -= _lookInput.y * _lookSensitivity;
            _cameraPitch = Mathf.Clamp(_cameraPitch, _pitchMin, _pitchMax);
            _cameraRoot.rotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
        }

        private void OnJumpPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            JumpBufferCounter = _jumpBufferTime;
        }

        private void OnAttackPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            AttackBufferCounter = _attackBufferTime;
        }

        private void OnDashPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            DashBufferCounter = _dashBufferTime;
        }
    }
}
```

- [ ] **Step 2：整文件替换 `PlayerController.cs` 为以下瘦身版（仍名 PlayerController，继承基类，只剩 Warrior 专属）**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 战士控制器：在 PlayerControllerBase 的共享能力之上，叠加连段近战攻击专属逻辑
    /// （MeleeHitDetector / ComboDefinition / 刀光拖尾 / PlayerAttackState / 连段 hash 缓存）。
    /// 注意：本阶段类名仍为 PlayerController；Task 3 会连同 .meta 一起重命名为 WarriorController，
    /// 以保住预制体按 GUID 引用本脚本的链接（届时序列化字段全部自动保留）。
    /// </summary>
    public class PlayerController : PlayerControllerBase
    {
        [Header("Combat")] [SerializeField] private MeleeHitDetector _meleeHitDetector; // 拖入武器上的命中判定组件
        [SerializeField] private ComboDefinition _combo; // 当前武器的连段表

        [Header("VFX")] [SerializeField] private TrailRenderer _bladeTrail; // 剑刃拖尾，挂在 VFX_BladeTip 上

        private PlayerAttackState _attackState;
        private int[] _comboStateHashes; // 连段各段 Animator 状态名的预 hash（Awake 算一次）

        public MeleeHitDetector MeleeHitDetector => _meleeHitDetector;
        public ComboDefinition Combo => _combo;
        public TrailRenderer BladeTrail => _bladeTrail;
        public PlayerAttackState AttackState => _attackState;

        protected override void Awake()
        {
            base.Awake();
            _attackState = new PlayerAttackState(this);
            BuildComboStateHashes();
        }

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>
        /// 把连段表各段的 AnimationStateName 预 hash 成 int[]，运行期切段直接用 hash CrossFade。
        /// 状态名为空时记 0 并告警（CrossFade 0 不会切动画）。
        /// </summary>
        private void BuildComboStateHashes()
        {
            int count = _combo != null ? _combo.SegmentCount : 0;
            _comboStateHashes = new int[count];
            for (int i = 0; i < count; i++)
            {
                AttackDefinition seg = _combo.Segments[i];
                string stateName = seg != null ? seg.AnimationStateName : null;
                if (string.IsNullOrEmpty(stateName))
                {
                    _comboStateHashes[i] = 0;
                    GameLog.Warn($"连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }
    }
}
```

- [ ] **Step 3：静态自检**
  - `PlayerControllerBase.cs`：含全部共享成员；`Awake` 为 `protected virtual` 且做了 dash hash；`TryStartAttack()` 虚方法返回 false；`[RequireComponent]` 已搬到基类。
  - `PlayerController.cs`：只剩 Combat/VFX 两组字段 + `_attackState`/`_comboStateHashes` + 四个属性 + `Awake` override（先 `base.Awake()`）+ `GetComboStateHash`/`BuildComboStateHashes`。**未**再声明任何已移到基类的字段/属性（无重复）。
  - 共享状态文件、`PlayerStateBase`、`PlayerDashState`、`PlayerAttackState`、`PlayerGroundedState` **本任务一律未改**——它们经 `_player`（仍是 `PlayerController`）访问的成员，现在由 `PlayerController` 从基类继承而来，编译应通过。
  - 确认未触碰 `Game.Combat`。
  - （开发者）在 Unity 编译一次，确认无报错、Player 预制体组件仍是 `PlayerController` 且字段未变。

- [ ] **Step 4：提交**

```bash
git add Assets/_Project/Scripts/Character/PlayerControllerBase.cs Assets/_Project/Scripts/Character/PlayerController.cs
git commit -m "refactor(character): extract PlayerControllerBase from PlayerController (in-place, GUID unchanged)"
```

---

## Task 2：状态层改指基类 + 攻击 seam + Dash 状态名数据驱动 + Archer 骨架（可编译）

**Files:**
- Modify: `Assets/_Project/Scripts/Character/States/PlayerStateBase.cs`
- Modify: `Assets/_Project/Scripts/Character/States/PlayerGroundedState.cs`
- Modify: `Assets/_Project/Scripts/Character/States/PlayerAirborneState.cs`
- Modify: `Assets/_Project/Scripts/Character/States/PlayerSlidingState.cs`
- Modify: `Assets/_Project/Scripts/Character/States/PlayerDashState.cs`
- Modify: `Assets/_Project/Scripts/Character/States/PlayerAttackState.cs`
- Modify: `Assets/_Project/Scripts/Character/PlayerController.cs`（加 `TryStartAttack` 重写）
- Create: `Assets/_Project/Scripts/Character/ArcherController.cs`

**Interfaces:**
- Consumes：Task 1 的 `PlayerControllerBase` 公开面 + `public virtual bool TryStartAttack()`；`PlayerController.AttackState`（Warrior 专属）。
- Produces：`PlayerStateBase(PlayerControllerBase player)` 构造签名；`PlayerController.TryStartAttack()` 重写；`PlayerAttackState(PlayerController player)` 仍接具体 Warrior 类（Task 3 才随重命名变 `WarriorController`）。

- [ ] **Step 1：`PlayerStateBase.cs` —— `_player` 改基类类型**
  - 把字段 `protected readonly PlayerController _player;` 改为 `protected readonly PlayerControllerBase _player;`
  - 把构造 `protected PlayerStateBase(PlayerController player)` 改为 `protected PlayerStateBase(PlayerControllerBase player)`
  - 其余（`JumpHash`、`HandleRotation`）不动。

- [ ] **Step 2：`PlayerAirborneState.cs` / `PlayerSlidingState.cs` —— 构造参数改基类**
  - `PlayerAirborneState`：`public PlayerAirborneState(PlayerController player) : base(player) { }` → `public PlayerAirborneState(PlayerControllerBase player) : base(player) { }`
  - `PlayerSlidingState`：`public PlayerSlidingState(PlayerController player) : base(player) { }` → `public PlayerSlidingState(PlayerControllerBase player) : base(player) { }`
  - 两个文件方法体一律不动。

- [ ] **Step 3：`PlayerDashState.cs` —— 构造改基类 + 用 `_player.DashStateHash`**
  - 构造：`public PlayerDashState(PlayerController player) : base(player) { }` → `public PlayerDashState(PlayerControllerBase player) : base(player) { }`
  - 删除这一行常量：`private static readonly int DashStateHash = Animator.StringToHash("DashForward_SingleTwohandSword");`（连同其上方注释一并删除）
  - `Enter()` 内把 `_player.Animator.CrossFadeInFixedTime(DashStateHash, CrossFadeDuration, 0);` 改为 `_player.Animator.CrossFadeInFixedTime(_player.DashStateHash, CrossFadeDuration, 0);`
  - 保留 `private const float CrossFadeDuration = 0.1f;` 与其它逻辑不变。

- [ ] **Step 4：`PlayerGroundedState.cs` —— 构造改基类 + 攻击分支改虚钩子**
  - 构造：`public PlayerGroundedState(PlayerController player) : base(player)` → `public PlayerGroundedState(PlayerControllerBase player) : base(player)`
  - 在 `CheckTransition()` 内，把现有攻击分支：

  ```csharp
            // 攻击输入（优先于跳跃检测）
            if (_player.AttackBufferCounter > 0f)
            {
                _player.StateMachine.ChangeState(_player.AttackState);
                return;
            }
  ```

  替换为：

  ```csharp
            // 攻击输入（优先于跳跃检测）。具体攻击逻辑由角色子类经 TryStartAttack() 决定——
            // 共享 GroundedState 不知道也不关心是连段近战还是弓箭，只负责保留 Dash→Attack→Jump 优先级。
            if (_player.TryStartAttack())
                return;
  ```

  - Dash 分支（其上）、Jump 分支（其下）、离地/超坡逻辑一律不动。

- [ ] **Step 5：`PlayerController.cs` —— 加 `TryStartAttack` 重写**
  - 在类内（建议紧接 `Awake` 之后）加入：

  ```csharp
        /// <summary>攻击 seam 实现：有缓冲攻击输入则切到连段攻击态。保留 Dash→Attack→Jump 优先级。</summary>
        public override bool TryStartAttack()
        {
            if (AttackBufferCounter > 0f)
            {
                StateMachine.ChangeState(_attackState);
                return true;
            }
            return false;
        }
  ```

- [ ] **Step 6：`PlayerAttackState.cs` —— 存 typed `_warrior`，Warrior 专属成员改读 `_warrior`**
  - 新增一个 typed 字段，并在构造里赋值（构造参数本任务仍是 `PlayerController`）：

  ```csharp
        // _player（基类引用）只能拿到共享成员；Warrior 专属的 Combo/MeleeHitDetector/连段hash/刀光
        // 需要具体子类引用，这里另存一份 typed 的 _warrior。
        private readonly PlayerController _warrior;

        public PlayerAttackState(PlayerController player) : base(player)
        {
            _warrior = player;
        }
  ```

  （删除原来的空构造 `public PlayerAttackState(PlayerController player) : base(player) { }`，用上面带赋值的版本替换。）
  - 把方法体内对 **Warrior 专属成员**的访问由 `_player.` 改为 `_warrior.`，共计这些：
    - `_player.Combo` → `_warrior.Combo`（出现在 `Enter` 的判空、`StartSegment` 取 `Segments[index]`、`CheckCombo` 取 `Segments[_comboIndex]` 与 `Combo.SegmentCount`）
    - `_player.MeleeHitDetector` → `_warrior.MeleeHitDetector`（`StartSegment`、`HandleAttackWindow`、`Exit`）
    - `_player.GetComboStateHash(...)` → `_warrior.GetComboStateHash(...)`（`StartSegment`）
    - `_player.BladeTrail` → `_warrior.BladeTrail`（`Enter` 的 `Clear()`、`Exit`、`HandleTrailWindow` 等所有出现处）
  - **保持 `_player.` 不变**的（这些是基类成员）：`_player.AttackBufferCounter`、`_player.Animator`、`_player.GroundChecker`、`_player.StateMachine`、`_player.GroundedState`、`_player.AirborneState`、`_player.CharacterController`、`_player.VerticalVelocity`。
  - 验证手段：改完后在本文件搜索 `_player.Combo`、`_player.MeleeHitDetector`、`_player.GetComboStateHash`、`_player.BladeTrail` 应**零命中**。

- [ ] **Step 7：新建 `ArcherController.cs` 空骨架**

```csharp
namespace Game.Character
{
    /// <summary>
    /// 弓箭手控制器。本阶段（Archer Phase 1）仅继承 PlayerControllerBase，
    /// 即拥有与战士一致的移动/跳跃/冲刺/坡度能力、但无任何攻击（TryStartAttack 用基类默认 false）。
    /// 普通攻击 / 蓄力重击 / 箭矢系统在 phases 3–4 充实。
    /// </summary>
    public class ArcherController : PlayerControllerBase
    {
    }
}
```

- [ ] **Step 8：静态自检**
  - 全部共享状态（Grounded/Airborne/Sliding/Dash）的构造参数均为 `PlayerControllerBase`；它们经 `_player` 只访问基类成员（搜不到对 `_player.AttackState`/`_player.Combo` 等 Warrior 成员的引用）。
  - `PlayerGroundedState` 攻击分支已是 `if (_player.TryStartAttack()) return;`，且 Dash→（Attack）→Jump 顺序未变。
  - `PlayerDashState` 不再有硬编码状态名常量，改用 `_player.DashStateHash`。
  - `PlayerAttackState` 经 `_warrior` 访问全部 Warrior 专属成员；`PlayerController` 有 `public override bool TryStartAttack()`。
  - `ArcherController` 可作为非抽象类实例化（基类无未实现的 abstract 成员）。
  - 未触碰 `Game.Combat`。
  - （开发者）Unity 编译一次确认无报错；此时行为应与 Task 1 完全一致（Warrior 攻击经 seam 走通）。

- [ ] **Step 9：提交**

```bash
git add Assets/_Project/Scripts/Character/States/ Assets/_Project/Scripts/Character/PlayerController.cs Assets/_Project/Scripts/Character/ArcherController.cs
git commit -m "refactor(character): retype shared states to base, add TryStartAttack seam + data-driven dash state name + ArcherController stub"
```

---

## Task 3：连同 `.meta` 重命名 PlayerController → WarriorController（保 GUID）

**Files:**
- Rename: `PlayerController.cs` → `WarriorController.cs`（**含其 `.meta`**）
- Modify: `WarriorController.cs`（类名 + 注释）、`PlayerAttackState.cs`（构造参数与 `_warrior` 字段类型）

**Interfaces:**
- Consumes：Task 1/2 成果。
- Produces：具体 Warrior 类型最终名为 `WarriorController`；预制体按既有 GUID 继续引用本脚本。

- [ ] **Step 1：确认 `.meta` 存在且被 git 跟踪**

```bash
git ls-files Assets/_Project/Scripts/Character/PlayerController.cs Assets/_Project/Scripts/Character/PlayerController.cs.meta
```
Expected：两行都列出（`.cs` 与 `.cs.meta` 均在版本控制内）。若 `.meta` 未列出，**停止**并先让开发者在 Unity 里确保 `.meta` 已生成并提交，再继续（贸然新建 `.meta` 会改变 GUID）。

- [ ] **Step 2：用 `git mv` 同时移动 `.cs` 与既有 `.meta`（GUID 随 `.meta` 保留）**

```bash
git mv Assets/_Project/Scripts/Character/PlayerController.cs Assets/_Project/Scripts/Character/WarriorController.cs
git mv Assets/_Project/Scripts/Character/PlayerController.cs.meta Assets/_Project/Scripts/Character/WarriorController.cs.meta
```

- [ ] **Step 3：`WarriorController.cs` —— 类名改名**
  - `public class PlayerController : PlayerControllerBase` → `public class WarriorController : PlayerControllerBase`
  - 更新类上方 XML 注释里"本阶段类名仍为 PlayerController；Task 3 会…重命名为 WarriorController"那段，改成陈述句（例如："战士控制器：在 PlayerControllerBase 之上叠加连段近战攻击专属逻辑。"），去掉过渡期措辞。

- [ ] **Step 4：`PlayerAttackState.cs` —— 同步具体类型名**
  - `private readonly PlayerController _warrior;` → `private readonly WarriorController _warrior;`
  - `public PlayerAttackState(PlayerController player) : base(player)` → `public PlayerAttackState(WarriorController player) : base(player)`
  - `_warrior = player;` 不变。

- [ ] **Step 5：全局确认无残留 `PlayerController` 类型引用**

```bash
git grep -n "PlayerController\b" -- "Assets/_Project/Scripts/Character/*.cs" "Assets/_Project/Scripts/Character/States/*.cs"
```
Expected：**仅** `PlayerControllerBase` 的命中（作为基类名出现）。若出现裸 `PlayerController`（非 `PlayerControllerBase`），说明有引用漏改，逐处修正后重跑直至干净。

- [ ] **Step 6：静态自检**
  - `WarriorController.cs` 与 `WarriorController.cs.meta` 并存；`.meta` 内 GUID 与重命名前 `PlayerController.cs.meta` 相同（`git mv` 不改内容）。
  - `WarriorController.Awake` 里 `new PlayerAttackState(this)` 的 `this` 现为 `WarriorController`，与 Step 4 的构造参数类型一致。
  - 文件名 `WarriorController.cs` == 类名 `WarriorController`（Unity MonoBehaviour 硬性要求）。
  - 未触碰 `Game.Combat`。

- [ ] **Step 7：提交**

```bash
git add -A Assets/_Project/Scripts/Character/
git commit -m "refactor(character): rename PlayerController to WarriorController (git mv with .meta, GUID preserved)"
```

---

## Task 4：开发者 Play 模式回归验收（不可由 Claude 代劳）

> 这是本阶段唯一的功能验收口径：**Warrior 行为零变更、预制体引用零丢失**。Claude 不执行本任务，仅在此列出清单供开发者照做。

- [ ] **Step 1：让 Unity 重新导入/编译**
  - 切回 Unity 编辑器使其聚焦，为新建的 `PlayerControllerBase.cs` / `ArcherController.cs` 生成 `.meta` 并编译；确认 Console 无编译错误。

- [ ] **Step 2：确认预制体引用零丢失（保 GUID 路径的验证）**
  - 打开 Player 预制体：组件应**自动**显示为 `WarriorController`（非 Missing Script）。
  - 全部序列化字段原样保留：Movement/Jump/Slope/Interaction/Camera/Attack Input/Dash 各数值，以及 `MeleeHitDetector`/`ComboDefinition`/`BladeTrail` 三个引用。
  - 确认 `Dash` 区出现新字段 `_dashStateName`，值为 `DashForward_SingleTwohandSword`（若为空请填上）。
  - 若组件变 Missing Script 或字段被重置 → §4.5 路径被违反，回退本分支重做。

- [ ] **Step 3：Play 模式 9 项手感回归**（逐项确认与重构前一致）
  1. 移动：八方向相机相对移动、转向平滑
  2. 跳跃：起跳初速度、上升/下落分段重力
  3. Coyote Time：走出边缘仍可跳
  4. Jump Buffer：空中提前按跳、落地立即起跳
  5. 冲刺：方向锁定、固定时长、CrossFade 进入 `DashForward_SingleTwohandSword` 正常
  6. Dash 冷却/缓冲：冷却期不可再冲、缓冲亚帧容错
  7. 坡度滑落：超坡下滑、平地恢复、滑出坡底转空中
  8. 连段攻击：Dash→Attack→Jump 优先级、连段推进/结束、命中判定、刀光拖尾窗口
  9. Profiler：热路径零 GC Alloc

- [ ] **Step 4：（可选）确认 Archer 骨架可用**
  - 临时把 `ArcherController` 挂到任一带 `CharacterController`+`GroundChecker` 的对象上、配相机字段，确认能移动/跳跃/冲刺、无攻击——证明基类已与 Warrior 解耦。phases 2–4 再正式接 Bow 预制体。

通过后本分支可进入收尾（合并/PR），并转入 Phase 2。
