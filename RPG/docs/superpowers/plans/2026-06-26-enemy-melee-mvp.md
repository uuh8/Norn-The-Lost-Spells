# 近战敌人 MVP 实现计划（Enemy Melee MVP）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现一个会感知玩家、追击、带前摇近战攻击、被打硬直、会死亡的近战敌人，让三职业第一次拥有可对战的对手（战斗闭环）。

**Architecture:** 沿用玩家的手写状态机思路，平行实现一套敌人 FSM（`EnemyStateMachine` + `EnemyStateBase` + 各状态），决策层（状态）与行动层（`EnemyController` 的能力 API）分离。受击/扣血/死亡/流血**复用**既有 `HealthComponent` / `CharacterCombatFeedback`，近战命中**复用** `MeleeHitDetector`，跨系统通信走 `EventBus`。敌人 AI/移动/攻击数值集中在数据驱动的 `EnemyDefinition` ScriptableObject。

**Tech Stack:** Unity 6.3 / URP / C# / Unity Input System / 手写 FSM / ScriptableObject 数据驱动 / `normalizedTime` 动画时序 / `EventBus<T>` 发布订阅。

## Global Constraints

逐条来自 `CLAUDE.md`，每个任务都隐式包含：

- **编译与测试由开发者在 Unity Editor 手动完成**——本计划不含自动化测试运行步骤；每个任务以"开发者在 Editor 验证"checkpoint 收尾。Claude 只编辑 `.cs` 与文本配置，**不创建 `.meta`**（Unity 自动生成）。
- **模块依赖单向**：`Game.Combat` 不得引用 `Game.Character`/`Skills`/`Rendering`；敌人代码放 `Game.Character`，可依赖 `Game.Combat` + `Game.Core`。跨模块通信走 `EventBus`。
- **每个脚本声明与所在程序集匹配的命名空间**（`Game.Character` 或 `Game.Combat`）。
- **命名**：私有/保护字段 `_camelCase`；公开成员/类型 `PascalCase`；接口 `IXxx`。
- **性能**：`Update`/每帧热路径禁止 `new`/LINQ/装箱；状态与感知在 `Awake` 预实例化；动画名 `StringToHash` 缓存一次。
- **日志**：用 `Game.Core.GameLog`（`Info`/`Warn`/`Error`），**不用** `Debug.Log`。
- **事件**：`struct` + `IGameEvent`。
- **CharacterController 在 `Update` 里 `Move`**（不在 `FixedUpdate`）。
- **提交**：仅在开发者要求时提交；用 Conventional Commits；commit message 末尾加 `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`。

---

## 文件结构（File Structure）

**新建：**
- `Assets/_Project/Scripts/Combat/Definitions/EnemyDefinition.cs` — 敌人 AI/移动/攻击数值 SO（`Game.Combat`，仅引用 `AttackDefinition`）。
- `Assets/_Project/Scripts/Character/Enemy/EnemyStateBase.cs` — 敌人状态抽象基类。
- `Assets/_Project/Scripts/Character/Enemy/EnemyStateMachine.cs` — 敌人 FSM 调度。
- `Assets/_Project/Scripts/Character/Enemy/EnemyPerception.cs` — 感知（距离 + 滞回）。
- `Assets/_Project/Scripts/Character/Enemy/States/EnemyIdleState.cs`
- `Assets/_Project/Scripts/Character/Enemy/States/EnemyChaseState.cs`
- `Assets/_Project/Scripts/Character/Enemy/States/EnemyAttackState.cs`
- `Assets/_Project/Scripts/Character/Enemy/States/EnemyHurtState.cs`
- `Assets/_Project/Scripts/Character/Controllers/EnemyController.cs` — 总装 + 行动层（跨任务增量构建）。

**修改：**
- `Assets/_Project/Scripts/Character/Controllers/PlayerControllerBase.cs` — 增加静态玩家注册表 `Current`。

**复用（不改代码，仅在 Editor 配置）：** `HealthComponent`、`CharacterCombatFeedback`、`MeleeHitDetector`、`AttackDefinition`、`EventBus`、`GameLog`。

> **重要编译约束**：Unity 按整程序集编译，`Game.Character` 里任一文件引用未创建的类型会导致**整个程序集编译失败**。因此任务顺序保证"每个任务结束时 `Game.Character` 可编译"：`EnemyController` 在 Task 3 建立核心，Task 4–6 **增量修改**它来接入新状态。

---

## Task 1：EnemyDefinition 数据 SO

**Files:**
- Create: `Assets/_Project/Scripts/Combat/Definitions/EnemyDefinition.cs`

**Interfaces:**
- Produces: `Game.Combat.EnemyDefinition`（ScriptableObject），公开字段 `MoveSpeed:float`、`DetectRadius:float`、`LoseRadius:float`、`AttackRange:float`、`AttackCooldown:float`、`Attack:AttackDefinition`、`HurtDuration:float`、`HurtStateName:string`、`CrossFadeDuration:float`。

- [ ] **Step 1: 创建 SO 脚本**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的近战敌人定义：AI 感知 / 移动 / 攻击 / 受击 的全部可调参数。
    /// 纯数据，仅引用 AttackDefinition（同模块），不引用 Animator/Character。新怪 = 新建一份本资产。
    /// HP/阵营仍在 HealthComponent 上配置（避免重复），本 SO 不含 HP。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Enemy Definition", fileName = "EnemyDefinition")]
    public class EnemyDefinition : ScriptableObject
    {
        [Header("移动")]
        public float MoveSpeed = 3.5f;

        [Header("感知 (半径，带滞回防抖)")]
        [Tooltip("进入战斗的侦测半径")]
        public float DetectRadius = 12f;
        [Tooltip("脱战半径；须 > DetectRadius，避免在边界反复进出战")]
        public float LoseRadius = 16f;

        [Header("攻击")]
        [Tooltip("玩家进入此距离且冷却就绪 → 出招")]
        public float AttackRange = 2.2f;
        [Tooltip("两次攻击的最小间隔(秒)")]
        public float AttackCooldown = 1.5f;
        [Tooltip("挥击数据：命中盒 HalfExtents / 命中窗口 HitActiveStart-End / 伤害 / 动画状态名")]
        public AttackDefinition Attack;

        [Header("受击")]
        [Tooltip("受击硬直时长(秒)：被打中后这段时间定身播受击动画，不追不打")]
        public float HurtDuration = 0.4f;
        [Tooltip("受击动画状态名(可空则不 CrossFade)；须与敌人 Animator 节点名精确一致")]
        public string HurtStateName = "";

        [Header("CrossFade")]
        public float CrossFadeDuration = 0.1f;
    }
}
```

- [ ] **Step 2: 开发者验证（Editor）**
  - Unity 编译通过，无报错。
  - 右键 `Project` 窗口 → `Create → Game → Combat → Enemy Definition`，能成功创建一份 `EnemyDefinition` 资产（暂不填 `Attack`）。

- [ ] **Step 3: 提交**

```bash
git add Assets/_Project/Scripts/Combat/Definitions/EnemyDefinition.cs
git commit -m "feat(combat): add EnemyDefinition ScriptableObject for melee enemy tuning"
```

---

## Task 2：玩家静态注册表

让敌人零查找、零 GC 地拿到当前玩家。

**Files:**
- Modify: `Assets/_Project/Scripts/Character/Controllers/PlayerControllerBase.cs`（`OnEnable` 159-165 / `OnDisable` 167-173 区域，及属性区）

**Interfaces:**
- Produces: `Game.Character.PlayerControllerBase.Current`（`public static PlayerControllerBase`，只读）——当前启用的玩家；无则为 `null`。

- [ ] **Step 1: 增加静态属性**

在 `PlayerControllerBase` 的对外属性区（如紧邻 `public Camera MainCamera => _mainCamera;` 之后）加入：

```csharp
        /// <summary>当前启用的玩家实例（单人游戏的轻量注册表）。敌人感知据此拿玩家，免每帧 FindObjectOfType。多玩家时为最后启用者。</summary>
        public static PlayerControllerBase Current { get; private set; }
```

- [ ] **Step 2: 在 OnEnable 注册**

把现有 `OnEnable`（159-165 行）改为在末尾登记自己：

```csharp
        private void OnEnable()
        {
            _inputActions.Player.Enable();
            _inputActions.Player.Jump.performed += OnJumpPerformed;
            _inputActions.Player.Attack.performed += OnAttackPerformed;
            _inputActions.Player.Dash.performed += OnDashPerformed;
            Current = this; // 注册为当前玩家
        }
```

- [ ] **Step 3: 在 OnDisable 注销**

把现有 `OnDisable`（167-173 行）改为在末尾清除自己：

```csharp
        private void OnDisable()
        {
            _inputActions.Player.Jump.performed -= OnJumpPerformed;
            _inputActions.Player.Attack.performed -= OnAttackPerformed;
            _inputActions.Player.Dash.performed -= OnDashPerformed;
            _inputActions.Player.Disable();
            if (Current == this) Current = null; // 注销（仅当自己仍是当前者）
        }
```

- [ ] **Step 4: 开发者验证（Editor）**
  - 编译通过。
  - 进入 Play：玩家正常游玩（注册表改动不影响既有行为）。

- [ ] **Step 5: 提交**

```bash
git add Assets/_Project/Scripts/Character/Controllers/PlayerControllerBase.cs
git commit -m "feat(character): expose static PlayerControllerBase.Current for enemy perception"
```

---

## Task 3：敌人核心 —— FSM 骨架 + 感知 + 控制器（敌人能"感知"玩家）

本任务一次性建立可编译的核心四件套，交付物：场景里放一个敌人，靠近会打印"发现玩家"、远离打印"脱战"。

**Files:**
- Create: `Assets/_Project/Scripts/Character/Enemy/EnemyStateBase.cs`
- Create: `Assets/_Project/Scripts/Character/Enemy/EnemyStateMachine.cs`
- Create: `Assets/_Project/Scripts/Character/Enemy/EnemyPerception.cs`
- Create: `Assets/_Project/Scripts/Character/Controllers/EnemyController.cs`

**Interfaces:**
- Consumes: `EnemyDefinition`（Task 1）、`PlayerControllerBase.Current`（Task 2）、`MeleeHitDetector.SetAttack`、`GameLog`。
- Produces:
  - `EnemyStateBase`（abstract，ctor `(EnemyController)`，`Enter()/Update()/Exit()`，protected `_enemy`）
  - `EnemyStateMachine`（`CurrentState`、`ChangeState(EnemyStateBase)`、`Update()`）
  - `EnemyPerception`（ctor `(EnemyController)`、`Tick()`、`bool HasTarget`、`Transform Target`、`float DistanceToTarget`）
  - `EnemyController`（`MonoBehaviour`），公开：`EnemyDefinition Definition`、`EnemyPerception Perception`、`EnemyStateMachine StateMachine`、`Animator Animator`、`void MoveTo(Vector3)`、`void StayGrounded()`、`void FaceTarget(Vector3)`

- [ ] **Step 1: EnemyStateBase**

```csharp
namespace Game.Character
{
    /// <summary>敌人状态抽象基类。平行于 PlayerStateBase：普通 C# 对象，持有具体 EnemyController。</summary>
    public abstract class EnemyStateBase
    {
        protected readonly EnemyController _enemy;
        protected EnemyStateBase(EnemyController enemy) { _enemy = enemy; }

        public abstract void Enter();
        public abstract void Update();
        public abstract void Exit();
    }
}
```

- [ ] **Step 2: EnemyStateMachine**

```csharp
namespace Game.Character
{
    /// <summary>敌人状态机调度员。平行于 PlayerStateMachine：Exit→换引用→Enter，由 EnemyController.Update 驱动。</summary>
    public class EnemyStateMachine
    {
        public EnemyStateBase CurrentState { get; private set; }

        public void ChangeState(EnemyStateBase newState)
        {
            CurrentState?.Exit();
            CurrentState = newState;
            CurrentState.Enter();
        }

        public void Update() => CurrentState?.Update();
    }
}
```

- [ ] **Step 3: EnemyPerception**

```csharp
using UnityEngine;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 感知（Sense）：每帧用 sqrMagnitude 比侦测半径判断玩家是否在范围内；用滞回(进用 DetectRadius、
    /// 已锁定用更大的 LoseRadius)防止边界抖动。MVP 只用半径(360°)；视野锥/视线 Raycast 留作扩展。
    /// 玩家来源 = PlayerControllerBase.Current（零查找）。
    /// </summary>
    public class EnemyPerception
    {
        private readonly EnemyController _enemy;

        public bool HasTarget { get; private set; }
        public Transform Target { get; private set; }
        public float DistanceToTarget { get; private set; }

        public EnemyPerception(EnemyController enemy) { _enemy = enemy; }

        public void Tick()
        {
            EnemyDefinition def = _enemy.Definition;
            PlayerControllerBase player = PlayerControllerBase.Current;
            if (def == null || player == null)
            {
                if (HasTarget) GameLog.Info("丢失目标(玩家不存在)", "Enemy");
                HasTarget = false; Target = null; return;
            }

            Vector3 to = player.transform.position - _enemy.transform.position;
            to.y = 0f;
            float sqr = to.sqrMagnitude;
            float radius = HasTarget ? def.LoseRadius : def.DetectRadius;

            if (sqr <= radius * radius)
            {
                if (!HasTarget) GameLog.Info("发现玩家 → 进入战斗", "Enemy");
                HasTarget = true;
                Target = player.transform;
                DistanceToTarget = Mathf.Sqrt(sqr);
            }
            else
            {
                if (HasTarget) GameLog.Info("玩家脱离 → 脱战", "Enemy");
                HasTarget = false; Target = null;
            }
        }
    }
}
```

- [ ] **Step 4: EnemyController（核心版）**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 敌人控制器（总装 + 行动层）。类比 PlayerControllerBase：Awake 取组件、预实例化感知与状态机；
    /// Update 跑 感知→状态机→同步 Animator。对状态暴露"行动能力"(MoveTo/StayGrounded/FaceTarget)。
    /// 受击/扣血/死亡/流血复用 HealthComponent + CharacterCombatFeedback；近战命中复用 MeleeHitDetector。
    /// 决策层(状态)只决定"做什么"，怎么做在本类实现——将来换行为树只换决策层。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(HealthComponent))]
    public class EnemyController : MonoBehaviour
    {
        [Header("数据")]
        [SerializeField] private EnemyDefinition _definition;

        [Header("引用")]
        [Tooltip("近战命中判定；其 TeamId 必须=本敌人 HealthComponent.TeamId(敌方)。攻击窗口由攻击状态开关")]
        [SerializeField] private MeleeHitDetector _hitDetector;

        [Header("调参")]
        [SerializeField] private float _rotationSpeed = 10f;
        [SerializeField] private float _gravity = -20f;

        private CharacterController _cc;
        private Animator _animator;

        private EnemyPerception _perception;
        private EnemyStateMachine _stateMachine;

        private float _verticalVelocity;

        private static readonly int SpeedHash = Animator.StringToHash("speed");

        public EnemyDefinition Definition => _definition;
        public EnemyPerception Perception => _perception;
        public EnemyStateMachine StateMachine => _stateMachine;
        public Animator Animator => _animator;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>();

            _perception = new EnemyPerception(this);
            _stateMachine = new EnemyStateMachine();

            if (_definition == null)
                GameLog.Warn("EnemyController 未配置 EnemyDefinition", "Enemy");
            // 把 SO 的攻击数据注入命中判定器，保证两者一致
            if (_hitDetector != null && _definition != null && _definition.Attack != null)
                _hitDetector.SetAttack(_definition.Attack);
        }

        private void Update()
        {
            _perception.Tick();
            _stateMachine.Update();
            SyncAnimator();
        }

        private void SyncAnimator()
        {
            if (_animator == null) return;
            Vector3 v = _cc.velocity; v.y = 0f;
            _animator.SetFloat(SpeedHash, v.magnitude);
        }

        #region 行动能力（供状态调用）

        /// <summary>朝目标水平移动(含重力)。</summary>
        public void MoveTo(Vector3 targetPos)
        {
            Vector3 dir = targetPos - transform.position; dir.y = 0f;
            Vector3 horizontal = dir.sqrMagnitude > 1e-6f
                ? dir.normalized * _definition.MoveSpeed : Vector3.zero;
            ApplyGravity();
            Vector3 velocity = horizontal;
            velocity.y = _verticalVelocity;
            _cc.Move(velocity * Time.deltaTime);
        }

        /// <summary>原地不动，仅施加重力贴地。</summary>
        public void StayGrounded()
        {
            ApplyGravity();
            _cc.Move(Vector3.up * _verticalVelocity * Time.deltaTime);
        }

        /// <summary>平滑转向目标的水平方向(只 yaw)。</summary>
        public void FaceTarget(Vector3 targetPos)
        {
            Vector3 dir = targetPos - transform.position; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return;
            Quaternion rot = Quaternion.LookRotation(dir);
            transform.rotation = Quaternion.Slerp(transform.rotation, rot, _rotationSpeed * Time.deltaTime);
        }

        private void ApplyGravity()
        {
            if (_cc.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
            else _verticalVelocity += _gravity * Time.deltaTime;
        }

        #endregion
    }
}
```

- [ ] **Step 5: 开发者验证（Editor）**
  - 编译通过。
  - 新建一个空 GameObject 作敌人：加 `CharacterController`、`HealthComponent`、`EnemyController`，把 Task 1 的 `EnemyDefinition` 资产拖给 `_definition`（`Attack` 可暂空）。`_hitDetector` 暂留空。
  - 放进有玩家的场景，进入 Play：玩家走近敌人 → Console 出现 `[Enemy] 发现玩家 → 进入战斗`；走远 → `[Enemy] 玩家脱离 → 脱战`。敌人本身暂不动（尚无状态）。

- [ ] **Step 6: 提交**

```bash
git add Assets/_Project/Scripts/Character/Enemy/EnemyStateBase.cs Assets/_Project/Scripts/Character/Enemy/EnemyStateMachine.cs Assets/_Project/Scripts/Character/Enemy/EnemyPerception.cs Assets/_Project/Scripts/Character/Controllers/EnemyController.cs
git commit -m "feat(character): add enemy FSM scaffold, perception and controller core"
```

---

## Task 4：待机 + 追击（敌人会走向玩家并在攻击距离停下）

**Files:**
- Create: `Assets/_Project/Scripts/Character/Enemy/States/EnemyIdleState.cs`
- Create: `Assets/_Project/Scripts/Character/Enemy/States/EnemyChaseState.cs`
- Modify: `Assets/_Project/Scripts/Character/Controllers/EnemyController.cs`

**Interfaces:**
- Consumes: `EnemyController.{Perception, StateMachine, Definition, MoveTo, StayGrounded, FaceTarget}`。
- Produces: `EnemyIdleState`、`EnemyChaseState`；`EnemyController.{IdleState, ChaseState}` 属性 + `Start()` 设初始状态。

- [ ] **Step 1: EnemyIdleState**

```csharp
namespace Game.Character
{
    /// <summary>待机：原地贴地，感知到玩家就转入追击。</summary>
    public class EnemyIdleState : EnemyStateBase
    {
        public EnemyIdleState(EnemyController enemy) : base(enemy) { }

        public override void Enter() { }

        public override void Update()
        {
            _enemy.StayGrounded();
            if (_enemy.Perception.HasTarget)
                _enemy.StateMachine.ChangeState(_enemy.ChaseState);
        }

        public override void Exit() { }
    }
}
```

- [ ] **Step 2: EnemyChaseState（本任务版：到攻击距离仅停下，攻击在 Task 5 接入）**

```csharp
using UnityEngine;

namespace Game.Character
{
    /// <summary>追击：朝玩家移动并转向；进入攻击距离则停下(Task 5 接入出招)；丢失目标回待机。</summary>
    public class EnemyChaseState : EnemyStateBase
    {
        public EnemyChaseState(EnemyController enemy) : base(enemy) { }

        public override void Enter() { }

        public override void Update()
        {
            EnemyPerception p = _enemy.Perception;
            if (!p.HasTarget)
            {
                _enemy.StateMachine.ChangeState(_enemy.IdleState);
                return;
            }

            Vector3 targetPos = p.Target.position;
            _enemy.FaceTarget(targetPos);

            if (p.DistanceToTarget <= _enemy.Definition.AttackRange)
            {
                _enemy.StayGrounded(); // 到攻击距离：停下（攻击在 Task 5 接入）
                return;
            }

            _enemy.MoveTo(targetPos);
        }

        public override void Exit() { }
    }
}
```

- [ ] **Step 3: EnemyController 增加状态字段与初始化**

在 `EnemyController` 字段区（`_stateMachine` 声明之后）加入：

```csharp
        private EnemyIdleState _idleState;
        private EnemyChaseState _chaseState;
```

在属性区（`StateMachine` 属性之后）加入：

```csharp
        public EnemyIdleState IdleState => _idleState;
        public EnemyChaseState ChaseState => _chaseState;
```

在 `Awake` 中 `_stateMachine = new EnemyStateMachine();` 之后加入：

```csharp
            _idleState = new EnemyIdleState(this);
            _chaseState = new EnemyChaseState(this);
```

在 `Awake` 方法之后、`Update` 方法之前加入 `Start`（延后到 Start 设初始状态，确保所有 Awake 完成）：

```csharp
        private void Start()
        {
            _stateMachine.ChangeState(_idleState);
        }
```

- [ ] **Step 4: 开发者验证（Editor）**
  - 编译通过。
  - 给敌人 Animator 配一个 `speed` 浮点参数驱动 Idle↔Run 混合（与玩家同款；若暂无敌人动画可跳过，仅观察位移）。
  - 进入 Play：玩家走进 `DetectRadius` → 敌人朝玩家移动；到 `AttackRange` 内 → 敌人停下并面向玩家；玩家走出 `LoseRadius` → 敌人回待机静止。

- [ ] **Step 5: 提交**

```bash
git add Assets/_Project/Scripts/Character/Enemy/States/EnemyIdleState.cs Assets/_Project/Scripts/Character/Enemy/States/EnemyChaseState.cs Assets/_Project/Scripts/Character/Controllers/EnemyController.cs
git commit -m "feat(character): add enemy idle and chase states"
```

---

## Task 5：近战攻击（敌人前摇出招并对玩家造成伤害）

**Files:**
- Create: `Assets/_Project/Scripts/Character/Enemy/States/EnemyAttackState.cs`
- Modify: `Assets/_Project/Scripts/Character/Controllers/EnemyController.cs`
- Modify: `Assets/_Project/Scripts/Character/Enemy/States/EnemyChaseState.cs`

**Interfaces:**
- Consumes: `EnemyController.{Animator, Perception, Definition, StayGrounded, FaceTarget}`、`AttackDefinition.{AnimationStateName, HitActiveStart, HitActiveEnd}`、`MeleeHitDetector.{OpenHitWindow, CloseHitWindow}`。
- Produces: `EnemyAttackState`；`EnemyController.{AttackState, AttackStateHash, AttackCooldownCounter, OpenAttackWindow(), CloseAttackWindow(), CrossFade(int)}`。

- [ ] **Step 1: EnemyAttackState**

```csharp
using UnityEngine;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 近战出招：原地播攻击动画；按 normalizedTime 在 [HitActiveStart, HitActiveEnd] 开/关命中窗口
    /// (复用 MeleeHitDetector)；动画接近播完(EndThreshold)结束并启动冷却。
    /// 前摇(命中窗口开启前)= 给玩家的 telegraph 预警，为后续闪避/格挡博弈铺路。
    /// 时序读法与玩家攻击一致：排除过渡帧 + 校验 shortNameHash 确认确在攻击态。
    /// </summary>
    public class EnemyAttackState : EnemyStateBase
    {
        private const float EndThreshold = 0.9f;
        private bool _windowOpen;

        public EnemyAttackState(EnemyController enemy) : base(enemy) { }

        public override void Enter()
        {
            _windowOpen = false;
            if (_enemy.Perception.Target != null)
                _enemy.FaceTarget(_enemy.Perception.Target.position); // 出招瞬间对准
            _enemy.CrossFade(_enemy.AttackStateHash);
        }

        public override void Update()
        {
            _enemy.StayGrounded(); // 出招原地不动

            Animator anim = _enemy.Animator;
            AttackDefinition atk = _enemy.Definition != null ? _enemy.Definition.Attack : null;
            if (anim == null || atk == null) { Finish(); return; }
            if (anim.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信

            AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(0);
            if (info.shortNameHash != _enemy.AttackStateHash) return; // 确认确在攻击态

            float t = info.normalizedTime % 1f;

            if (!_windowOpen && t >= atk.HitActiveStart) { _enemy.OpenAttackWindow(); _windowOpen = true; }
            if (_windowOpen && t >= atk.HitActiveEnd) { _enemy.CloseAttackWindow(); _windowOpen = false; }

            if (t >= EndThreshold) Finish();
        }

        private void Finish()
        {
            EnemyPerception p = _enemy.Perception;
            _enemy.StateMachine.ChangeState(p.HasTarget ? _enemy.ChaseState : _enemy.IdleState);
        }

        public override void Exit()
        {
            // Exit 是唯一必经出口：保证关窗 + 启动冷却（即使被受击/死亡打断也成立）
            if (_windowOpen) { _enemy.CloseAttackWindow(); _windowOpen = false; }
            _enemy.AttackCooldownCounter = _enemy.Definition != null ? _enemy.Definition.AttackCooldown : 0f;
        }
    }
}
```

- [ ] **Step 2: EnemyController 增加攻击支持**

在字段区（`_chaseState` 之后）加入：

```csharp
        private EnemyAttackState _attackState;
        private int _attackStateHash;
```

在属性区（`ChaseState` 之后）加入：

```csharp
        public EnemyAttackState AttackState => _attackState;
        public int AttackStateHash => _attackStateHash;
        public float AttackCooldownCounter { get; set; }
```

在 `Awake` 中 `_chaseState = new EnemyChaseState(this);` 之后加入实例化与预 hash：

```csharp
            _attackState = new EnemyAttackState(this);
            // 攻击动画状态名取自攻击数据(数据驱动)；空 → 0 → CrossFade 不切动画
            string atkStateName = (_definition != null && _definition.Attack != null)
                ? _definition.Attack.AnimationStateName : null;
            _attackStateHash = string.IsNullOrEmpty(atkStateName) ? 0 : Animator.StringToHash(atkStateName);
            if (_attackStateHash == 0)
                GameLog.Warn("敌人攻击动画状态名为空，攻击 CrossFade 无法切换动画", "Enemy");
```

在 `Update` 顶部（`_perception.Tick();` 之前）加入冷却递减：

```csharp
            if (AttackCooldownCounter > 0f) AttackCooldownCounter -= Time.deltaTime;
```

在行动能力 region 内（`FaceTarget` 之后）加入命中窗口与 CrossFade 帮助方法：

```csharp
        /// <summary>开启近战命中窗口（攻击状态在 HitActiveStart 调用）。</summary>
        public void OpenAttackWindow() { if (_hitDetector != null) _hitDetector.OpenHitWindow(); }

        /// <summary>关闭近战命中窗口。</summary>
        public void CloseAttackWindow() { if (_hitDetector != null) _hitDetector.CloseHitWindow(); }

        /// <summary>数据驱动 CrossFade 进入指定 Animator 状态（hash 为 0 静默跳过）。</summary>
        public void CrossFade(int stateHash)
        {
            if (_animator != null && stateHash != 0)
                _animator.CrossFadeInFixedTime(stateHash, _definition.CrossFadeDuration, 0);
        }
```

- [ ] **Step 3: 修改 EnemyChaseState 接入攻击转换**

把 Task 4 写的 `EnemyChaseState.Update` 中的攻击距离判定块：

```csharp
            if (p.DistanceToTarget <= _enemy.Definition.AttackRange)
            {
                _enemy.StayGrounded(); // 到攻击距离：停下（攻击在 Task 5 接入）
                return;
            }
```

替换为：

```csharp
            if (p.DistanceToTarget <= _enemy.Definition.AttackRange)
            {
                if (_enemy.AttackCooldownCounter <= 0f)
                {
                    _enemy.StateMachine.ChangeState(_enemy.AttackState);
                    return;
                }
                _enemy.StayGrounded(); // 在攻击距离但冷却中：停下等待
                return;
            }
```

- [ ] **Step 4: 开发者验证（Editor）**
  - 编译通过。
  - 为敌人创建一份 `AttackDefinition` 资产：填 `HalfExtents`（命中盒，如 (0.8,0.8,1.2)）、`HitActiveStart`/`HitActiveEnd`（如 0.4 / 0.6）、`BaseAmount`、`Type`、`AnimationStateName`=敌人 Animator 里攻击节点名。拖给 `EnemyDefinition.Attack`。
  - 给敌人加一个子物体作武器枢轴；加 `MeleeHitDetector`：`_attackerTeam`=敌方(与 `HealthComponent.TeamId` 一致，如 1)、`_weaponPivot`=枢轴、`_ownerRoot`=敌人根、`_hitMask` 勾选玩家所在层。拖给 `EnemyController._hitDetector`。
  - 敌人 Animator 配攻击状态，并加**退出连线**（攻击 → 移动/Idle，Has Exit Time），否则定格。
  - 确认**玩家**有 `HealthComponent`(TeamId=玩家方，如 0) + `CharacterCombatFeedback`。
  - 进入 Play：敌人靠近 → 播攻击动画(前摇)→ 命中窗口期打到玩家 → 玩家掉血/闪红/流血；之后约 `AttackCooldown` 秒才再次出招。

- [ ] **Step 5: 提交**

```bash
git add Assets/_Project/Scripts/Character/Enemy/States/EnemyAttackState.cs Assets/_Project/Scripts/Character/Enemy/States/EnemyChaseState.cs Assets/_Project/Scripts/Character/Controllers/EnemyController.cs
git commit -m "feat(character): add enemy melee attack state with telegraph and hit window"
```

---

## Task 6：受击硬直 + 死亡（敌人被打会踉跄、会正确死亡）

**Files:**
- Create: `Assets/_Project/Scripts/Character/Enemy/States/EnemyHurtState.cs`
- Modify: `Assets/_Project/Scripts/Character/Controllers/EnemyController.cs`

**Interfaces:**
- Consumes: `EventBus<DamageReceivedEvent>`、`EventBus<DeathEvent>`、`DamageReceivedEvent.{TargetId, RemainingHp}`、`DeathEvent.TargetId`、`EnemyController.{StayGrounded, CrossFade, HurtStateHash, ChaseState, IdleState, Perception}`。
- Produces: `EnemyHurtState`；`EnemyController.{HurtState, HurtStateHash}` + 事件订阅 + `_dead` 守卫。

- [ ] **Step 1: EnemyHurtState**

```csharp
using UnityEngine;

namespace Game.Character
{
    /// <summary>受击硬直：定身播受击动画，计时结束回追击/待机。由 EnemyController 收到自身受击事件时切入(打断当前动作)。</summary>
    public class EnemyHurtState : EnemyStateBase
    {
        private float _timer;

        public EnemyHurtState(EnemyController enemy) : base(enemy) { }

        public override void Enter()
        {
            _timer = _enemy.Definition != null ? _enemy.Definition.HurtDuration : 0.3f;
            _enemy.CrossFade(_enemy.HurtStateHash);
        }

        public override void Update()
        {
            _enemy.StayGrounded();
            _timer -= Time.deltaTime;
            if (_timer <= 0f)
            {
                EnemyPerception p = _enemy.Perception;
                _enemy.StateMachine.ChangeState(p.HasTarget ? _enemy.ChaseState : _enemy.IdleState);
            }
        }

        public override void Exit() { }
    }
}
```

- [ ] **Step 2: EnemyController 增加受击/死亡支持**

在 `using` 区确认含 `using Game.Combat;` 与 `using Game.Core;`（Task 3 已加）。

在字段区（`_attackStateHash` 之后）加入：

```csharp
        private EnemyHurtState _hurtState;
        private int _hurtStateHash;
        private int _id;
        private bool _dead;
```

在属性区（`AttackState`/`AttackStateHash` 附近）加入：

```csharp
        public EnemyHurtState HurtState => _hurtState;
        public int HurtStateHash => _hurtStateHash;
        public bool IsDead => _dead;
```

在 `Awake` 中实例化攻击状态之后加入：

```csharp
            _hurtState = new EnemyHurtState(this);
            _hurtStateHash = (_definition != null && !string.IsNullOrEmpty(_definition.HurtStateName))
                ? Animator.StringToHash(_definition.HurtStateName) : 0;
            _id = gameObject.GetInstanceID();
```

在 `Start` 方法之后加入事件订阅/退订与处理（成对放在 OnEnable/OnDisable）：

```csharp
        private void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            if (_dead || e.TargetId != _id) return;
            if (e.RemainingHp <= 0f) return; // 致死那一击交给 OnDeath 处理，不进硬直
            _stateMachine.ChangeState(_hurtState);
        }

        private void OnDeath(DeathEvent e)
        {
            if (_dead || e.TargetId != _id) return;
            _dead = true;
            if (_hitDetector != null) _hitDetector.CloseHitWindow();
            // 死亡动画 + 延时销毁由 CharacterCombatFeedback 负责；本控制器只停 AI
        }
```

把 `Update` 改为死亡后停跑 AI——在方法**最顶部**加入：

```csharp
            if (_dead) return;
```

（即 `Update` 第一行就是 `if (_dead) return;`，其后才是冷却递减/感知/状态机/同步。）

- [ ] **Step 3: 开发者验证（Editor）**
  - 编译通过。
  - 敌人 `EnemyDefinition.HurtStateName` 填受击动画节点名；敌人 Animator 配受击与死亡状态，受击状态加退出连线(→移动/Idle)。
  - **关键避坑**：敌人的 `CharacterCombatFeedback` 把 `_getHitStateName` **留空**（受击动画改由 `EnemyHurtState` 驱动，避免两处同时 CrossFade 冲突）；`_dieStateName` 正常填死亡节点名、配 `_destroyDelay`。
  - 进入 Play：用任一职业打敌人 → 敌人踉跄(硬直、暂停追打)→ 恢复追击；持续打至血量归零 → 播死亡动画 → 延时消失，且死亡后不再移动/攻击/再触发硬直。

- [ ] **Step 4: 提交**

```bash
git add Assets/_Project/Scripts/Character/Enemy/States/EnemyHurtState.cs Assets/_Project/Scripts/Character/Controllers/EnemyController.cs
git commit -m "feat(character): add enemy hurt stagger and death handling via combat events"
```

---

## Task 7：敌人预制体组装 + 多敌人实测（集成验收）

纯 Editor 任务：把组件固化成预制体、摆 2~3 个、完整试玩战斗闭环。无代码改动。

**Files:** 无（Editor 资产与场景操作；预制体 `.meta` 由 Unity 生成，Claude 不创建）。

- [ ] **Step 1: 组装敌人预制体**
  - 敌人 GameObject 组件清单：`CharacterController`（半径/高度匹配模型）、`HealthComponent`（`TeamId`=敌方如 1、`maxHp`）、`Animator`（敌人 Controller，含 `speed` 参数 + 攻击/受击/死亡状态 + 各自退出连线）、`CharacterCombatFeedback`（`_getHitStateName` 留空、`_dieStateName` 填死亡节点、血特效 `BloodExplosion`/`BloodDripping`）、子物体武器枢轴 + `MeleeHitDetector`（team=敌方、pivot、ownerRoot、hitMask=玩家层）、`EnemyController`（`_definition`、`_hitDetector`）。
  - 敌人放在一个**玩家命中遮罩包含**的 Layer 上（确保箭/火球/近战 OverlapBox 能打到）。
  - 拖成预制体存到 `Assets/_Project/Art/Prefabs/`（或既有敌人 Prefab 目录）。

- [ ] **Step 2: 校验阵营互打**
  - 玩家 `HealthComponent.TeamId`=玩家方(如 0)，敌人=敌方(如 1)，两者不同（否则箭因同阵营穿过而打不到敌人）。
  - 玩家武器 `MeleeHitDetector._attackerTeam` 与玩家 `TeamId` 一致；敌人同理。

- [ ] **Step 3: 多敌人试玩**
  - 场景摆 2~3 个敌人预制体，分散在不同距离。
  - 进入 Play 跑通完整闭环：① 走近被侦测 → 敌人追击 → 前摇攻击打到玩家(掉血/闪红/流血)；② 玩家用三职业(近战/箭/火球/陨石)反击 → 敌人硬直 → 击杀 → 死亡消失；③ 走远脱战。
  - 用 Profiler 抽查战斗中 GC Alloc：稳态应无每帧分配（一次性 Instantiate 特效/投射物除外）。

- [ ] **Step 4: 提交（若有场景/预制体变更需开发者确认后）**

```bash
git add Assets/_Project/Art/Prefabs Assets/_Project/Scenes
git commit -m "feat(character): assemble melee enemy prefab and place encounter for playtest"
```

---

## 自检（Self-Review）

**规格覆盖**：感知✓(Task3) · 待机/追击✓(Task4) · 前摇近战攻击+伤害✓(Task5,复用 MeleeHitDetector) · 受击硬直✓(Task6,HurtState) · 死亡✓(Task6,事件禁用 AI+Feedback 销毁) · 数据驱动 SO✓(Task1) · 玩家引用注册表✓(Task2) · 手动摆放✓(Task7) · 阵营互打✓(Task7)。决策/行动分离✓、与玩家架构同构✓、Game.Combat 不依赖 Character✓(EnemyDefinition 仅引用 AttackDefinition)。

**类型一致性**：`Definition`/`Perception`/`StateMachine`/`Animator`/`MoveTo`/`StayGrounded`/`FaceTarget`/`IdleState`/`ChaseState`/`AttackState`/`HurtState`/`AttackStateHash`/`HurtStateHash`/`AttackCooldownCounter`/`OpenAttackWindow`/`CloseAttackWindow`/`CrossFade`/`IsDead` 在定义任务与消费任务间签名一致。`EnemyStateBase` ctor `(EnemyController)` 被四个状态一致沿用。复用既有 API：`MeleeHitDetector.{SetAttack,OpenHitWindow,CloseHitWindow}`、`AttackDefinition.{AnimationStateName,HitActiveStart,HitActiveEnd,HalfExtents,BaseAmount,Type}`、`DamageReceivedEvent.{TargetId,RemainingHp}`、`DeathEvent.TargetId`、`PlayerControllerBase.Current` 均与现有代码核对。

**已知边界（MVP 内可接受）**：① 移动用 CharacterController 朝向，不绕障碍（空旷场景适用，后续接 NavMesh 算路径）；② 感知仅半径(360°)，无视野锥/视线遮挡(留扩展)；③ 受击为可被连续打断的 stunlock(后续 M5 配削韧/霸体)；④ 每次受击都进硬直；⑤ 燃烧 DoT 每跳会触发硬直/流血(与既有受击表现局限同源，待 DamageRequest 加静默标记)；⑥ 无对象池(多敌人后再加)。
