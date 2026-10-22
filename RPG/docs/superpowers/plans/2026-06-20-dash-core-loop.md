# Dash（冲刺）核心循环 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在已验证的角色状态机上新增一个地面触发、不可被打断、按固定时长沿当前朝向位移的冲刺（Dash）状态，含独立冷却与输入缓冲。

**Architecture:** 新增 `PlayerDashState`（普通 C# 状态，Enter/Update/Exit 生命周期），位移由代码驱动 `CharacterController.Move`（不用 Root Motion）。冲刺参数为 PlayerController 上的裸字段（仿 Jump 先例）。动画用 `CrossFadeInFixedTime`（代码点名目标状态，状态名 hash 预缓存）。冷却计时器与缓冲计时器均挂在 PlayerController、每帧统一递减；冷却的唯一启动点是 `PlayerDashState.Exit()`。触发检测插在 `PlayerGroundedState.CheckTransition` 最前（优先级 Dash → Attack → Jump）。

**Tech Stack:** Unity 6.3（6000.3.16f1）、URP、C#、Unity Input System（`InputSystem_Actions`，已存在 `Player.Dash` Action）、`CharacterController`。

## Global Constraints

- 全部新增/修改文件位于 `Game.Character` asmdef；**禁止引用任何 `Game.Combat` 类型**（冲刺无伤害结算），不引入新的跨模块依赖。
- 命名空间 `Game.Character`；私有实例字段 `_lowerCamelCase`；公开成员/类型 `PascalCase`；`static readonly` hash 缓存用 `PascalCase`。
- 热路径（`PlayerDashState.Update` 及其调用链）**禁止 `new`/LINQ/装箱**。
- 任何 Animator 状态名以字符串出现，必须在**类型加载时**经 `static readonly int = Animator.StringToHash(...)` 预缓存一次（仿 `PlayerStateBase.JumpHash` 模式）；**禁止每帧/每次触发做 `StringToHash` 或字符串拼接**。
- 永不使用 legacy `Input` 类；只用 `InputSystem_Actions`。
- 日志只用 `Game.Core.GameLog`，不直接 `Debug.Log`（本轮冲刺逻辑不需要日志，除非新增告警）。
- **编译与 Play-mode 测试由开发者在 Unity Editor 手动执行**；本计划的代码步骤只保证静态逻辑正确，subagent 不声称"已运行通过"，也不创建 `.meta` 文件。
- 本轮锁定的 6 项设计决策（不得偏离）：① 裸字段参数 ② CrossFade 动画接入 ③ 冷却在 `Exit()` 启动（从冲刺结束起算）④ 缓冲与冷却正交 ⑤ 冲到底、Exit 判落点 ⑥ 触发优先级 Dash → Attack → Jump。纯逻辑判定**不抽**纯函数、不加 EditMode 单测（判定仅为布尔 AND，无边界 case）。
- 本轮**不做、只确保不挡路**：空中冲刺、八方向冲刺、冲刺取消连段、无敌帧/穿透。

## File Structure

| 文件 | 职责 | 动作 |
|------|------|------|
| `Assets/_Project/Scripts/Character/PlayerController.cs` | 冲刺裸字段、冷却/缓冲计时器（每帧递减）、Dash 输入回调、状态实例与只读访问器 | 修改 |
| `Assets/_Project/Scripts/Character/States/PlayerDashState.cs` | 冲刺状态本体（方向锁定、位移、计时、CrossFade、Exit 启动冷却） | 新建 |
| `Assets/_Project/Scripts/Character/States/PlayerGroundedState.cs` | 在 `CheckTransition` 最前新增 Dash 触发分支 | 修改 |

> Task 1 = 数据层（PlayerController 成员，可独立编译）。Task 2 = 行为层（PlayerDashState，引用 Task 1 成员；未被实例化的类可独立编译）。Task 3 = 接线（实例化 `_dashState` + GroundedState 触发，使冲刺可达）。Task 4 = 开发者在编辑器做 Animator 接线 + Play-mode 验证。

---

### Task 1: PlayerController —— 冲刺数据层（字段 / 计时器 / 输入 / 访问器）

**Files:**
- Modify: `Assets/_Project/Scripts/Character/PlayerController.cs`

**Interfaces:**
- Consumes: 现有 `_inputActions.Player.Dash`（Input wrapper 已生成，成员 `@Dash`）；现有 `Update()` 计时器递减块；现有 `OnEnable/OnDisable` 订阅块。
- Produces（供后续 Task 读取）：
  - 只读属性 `float DashSpeed`、`float DashDuration`、`float DashCooldown`、`float DashBufferTime`
  - 读写属性 `float DashCooldownCounter { get; set; }`、`float DashBufferCounter { get; set; }`
  - 私有回调 `void OnDashPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)`
  - （注意：状态实例 `_dashState` 与属性 `DashState` 在 Task 3 添加，本任务**不**添加，以保证本任务可独立编译。）

- [ ] **Step 1: 新增冲刺裸字段**

在 `[Header("Combat")]` 字段块之后（`_combo` 字段行的下方）插入：

```csharp
        [Header("Dash")] [SerializeField] private float _dashSpeed = 14f;      // 冲刺水平速度
        [SerializeField] private float _dashDuration = 0.2f;                    // 冲刺持续时间（秒）
        [SerializeField] private float _dashCooldown = 1f;                      // 冲刺冷却（从 Exit 起算）
        [SerializeField] private float _dashBufferTime = 0.15f;                 // 冲刺输入缓冲（与攻击/跳跃同惯例）
```

- [ ] **Step 2: 新增对外只读属性 + 计时器读写属性**

在 `public ComboDefinition Combo => _combo;`（约束区"对外暴露给 State 的属性"末尾）之后插入：

```csharp
        public float DashSpeed => _dashSpeed;
        public float DashDuration => _dashDuration;
        public float DashCooldown => _dashCooldown;
        public float DashBufferTime => _dashBufferTime;
        public float DashCooldownCounter { get; set; }
        public float DashBufferCounter { get; set; }
```

- [ ] **Step 3: 订阅 / 取消订阅 Dash 输入**

在 `OnEnable()` 内 `_inputActions.Player.Attack.performed += OnAttackPerformed;` 之后加：

```csharp
            _inputActions.Player.Dash.performed += OnDashPerformed;
```

在 `OnDisable()` 内 `_inputActions.Player.Attack.performed -= OnAttackPerformed;` 之后加：

```csharp
            _inputActions.Player.Dash.performed -= OnDashPerformed;
```

- [ ] **Step 4: 每帧递减冲刺计时器**

在 `Update()` 的计时器块里（`if (AttackBufferCounter > 0f) AttackBufferCounter -= Time.deltaTime;` 之后）加：

```csharp
            if (DashCooldownCounter > 0f) DashCooldownCounter -= Time.deltaTime;
            if (DashBufferCounter > 0f) DashBufferCounter -= Time.deltaTime;
```

- [ ] **Step 5: 新增 Dash 输入回调**

在 `OnAttackPerformed(...)` 方法之后（类末尾、`OnAttackPerformed` 闭合括号下方）加：

```csharp
        private void OnDashPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            // 与 Jump/Attack 同惯例：只记录"有一个待消耗的冲刺输入"，由 GroundedState 在闸门处消费。
            // 冷却与缓冲正交：本回调只管缓冲；能力锁由 GroundedState 用 DashCooldownCounter 单独把关。
            DashBufferCounter = _dashBufferTime;
        }
```

- [ ] **Step 6: 静态自检**

确认：① 6 个属性签名与 Interfaces 完全一致；② `OnDashPerformed` 只写 `DashBufferCounter`，不读不写冷却（保证缓冲与冷却正交）；③ 未引用任何 `Game.Combat` 新类型；④ 未添加 `_dashState`/`DashState`（留给 Task 3）。

- [ ] **Step 7: 开发者编译验证（手动）**

开发者在 Unity Editor 触发编译。预期：无编译错误（`_inputActions.Player.Dash` 成员存在，已确认于 `InputSystem_Actions.cs`）。

- [ ] **Step 8: Commit**

```bash
git add "Assets/_Project/Scripts/Character/PlayerController.cs"
git commit -m "feat(character): add dash tunables, cooldown/buffer timers and input callback

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: PlayerDashState —— 冲刺状态本体

**Files:**
- Create: `Assets/_Project/Scripts/Character/States/PlayerDashState.cs`

**Interfaces:**
- Consumes（来自 Task 1 / 现有 PlayerController）：`DashSpeed`、`DashDuration`、`DashCooldown`、`DashBufferCounter`、`DashCooldownCounter`、`VerticalVelocity`、`CharacterController`、`GroundChecker`、`Animator`、`StateMachine`、`GroundedState`、`AirborneState`、`transform`。
- Produces：`PlayerDashState`（继承 `PlayerStateBase`，构造签名 `PlayerDashState(PlayerController player)`）。供 Task 3 实例化。

- [ ] **Step 1: 创建文件并写入完整实现**

创建 `Assets/_Project/Scripts/Character/States/PlayerDashState.cs`，内容：

```csharp
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 冲刺状态。地面触发、不可被打断、按固定时长沿"进入瞬间角色朝向"做水平位移。
    /// 1. Enter：锁定冲刺方向(=进入瞬间 transform.forward) + 消耗缓冲输入 + CrossFade 冲刺动画
    /// 2. Update：按 DashSpeed 沿锁定方向水平位移 + 贴地压力 + 计时；时间到 → 回移动状态
    /// 3. Exit：启动冷却（DashCooldownCounter = DashCooldown）—— 冷却启动的唯一责任点。
    ///    任何离开冲刺的路径（自然结束 / 未来受击打断）都经 ChangeState→Exit，保证冷却必启动。
    /// </summary>
    public class PlayerDashState : PlayerStateBase
    {
        // 冲刺"动画状态(state)"名的预 hash：仿 JumpHash 的 static readonly 模式，类型加载时算一次，
        // 绝不每帧/每次触发 StringToHash。供 CrossFadeInFixedTime 使用，
        // 字符串须与 Animator Controller 里的状态节点名精确一致。
        private static readonly int DashStateHash = Animator.StringToHash("DashForward_SingleTwohandSword");

        private const float CrossFadeDuration = 0.1f; // 进入冲刺的 CrossFade 固定时长（秒）

        private Vector3 _dashDirection; // 进入瞬间锁定的世界空间冲刺方向（已去 y、归一化）
        private float _elapsed;         // 本次冲刺已过时间（秒）

        public PlayerDashState(PlayerController player) : base(player) { }

        #region 状态机函数

        public override void Enter()
        {
            // 锁定冲刺方向 = 进入瞬间角色朝向（轨道相机风格，单一前向冲刺，本轮不做八方向）
            _dashDirection = _player.transform.forward;
            _dashDirection.y = 0f;
            _dashDirection.Normalize();

            _elapsed = 0f;
            _player.DashBufferCounter = 0f; // 消耗起手输入，防冲刺中/同帧重复触发

            // CrossFade 进入冲刺动画：代码直接点名目标状态，Animator 侧无需进入连线
            _player.Animator.CrossFadeInFixedTime(DashStateHash, CrossFadeDuration, 0);
        }

        public override void Update()
        {
            HandleDashMovement();

            _elapsed += Time.deltaTime;
            if (_elapsed >= _player.DashDuration)
                TransitionToMovement();
        }

        public override void Exit()
        {
            // 冷却启动唯一责任点：从冲刺结束起算（决策③）。
            // 经 ChangeState 的任何退出路径都会跑到这里，冷却必然启动。
            _player.DashCooldownCounter = _player.DashCooldown;

            // 刻意不清 DashBufferCounter（区别于 PlayerAttackState.Exit 清 AttackBufferCounter）：
            // 冲刺触发闸门是"DashBufferCounter>0 且 DashCooldownCounter<=0"的双重条件，
            // 而本方法刚把冷却设为正数，残留缓冲会被冷却挡住、绝不会漏触发新冲刺。
            // 因此无需显式清缓冲——这是有意为之，不是漏写。
        }

        #endregion

        #region 处理流程函数

        private void HandleDashMovement()
        {
            // 垂直：沿用 Attack 的 -2 贴地压力，保持 CC 贴地与离地判定有效（决策⑤）
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;

            // 水平：锁定方向 × 冲刺速度。冲刺期间不响应移动输入、不转向（方向锁定）
            Vector3 velocity = _dashDirection * _player.DashSpeed;
            velocity.y = _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        #endregion

        #region 功能函数

        /// <summary>冲刺自然结束：用落点判定决定回地面还是空中（决策⑤离地边界由此吸收）。</summary>
        private void TransitionToMovement()
        {
            if (_player.GroundChecker.IsGrounded)
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}
```

- [ ] **Step 2: 静态自检**

确认：① `Update` 调用链零 `new`/LINQ/装箱（`_dashDirection * DashSpeed` 为 `Vector3` 值类型运算，`velocity` 为栈上结构体）；② 无 `Game.Combat`、`UnityEngine.InputSystem` 引用；③ `DashStateHash` 为 `static readonly`，仅类型加载时算一次；④ 不调用 `HandleRotation`（方向锁定）；⑤ Exit 只启动冷却、不归零方向计时（下次 Enter 会重置 `_elapsed`/`_dashDirection`）。

- [ ] **Step 3: 开发者编译验证（手动）**

开发者在 Unity Editor 触发编译。预期：无编译错误（类未被实例化也应通过编译）。

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Project/Scripts/Character/States/PlayerDashState.cs"
git commit -m "feat(character): add PlayerDashState (locked-direction dash via CrossFade)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: 接线 —— 实例化 DashState + GroundedState 触发分支

**Files:**
- Modify: `Assets/_Project/Scripts/Character/PlayerController.cs`
- Modify: `Assets/_Project/Scripts/Character/States/PlayerGroundedState.cs`

**Interfaces:**
- Consumes：Task 2 的 `PlayerDashState`；Task 1 的 `DashBufferCounter`、`DashCooldownCounter`。
- Produces：`public PlayerDashState DashState => _dashState;`（供 GroundedState 切换）。

- [ ] **Step 1: PlayerController 声明状态字段**

在状态实例字段块（`private PlayerAttackState _attackState;` 行）之后加：

```csharp
        private PlayerDashState _dashState;
```

- [ ] **Step 2: PlayerController 在 Awake 实例化**

在 `Awake()` 内 `_attackState = new PlayerAttackState(this);` 之后加：

```csharp
            _dashState = new PlayerDashState(this);
```

- [ ] **Step 3: PlayerController 暴露 DashState 属性**

在 `public PlayerAttackState AttackState => _attackState;` 之后加：

```csharp
        public PlayerDashState DashState => _dashState;
```

- [ ] **Step 4: PlayerGroundedState 新增 Dash 触发（最高优先级）**

在 `PlayerGroundedState.CheckTransition()` 的**最前面**（现有"攻击输入"块 `if (_player.AttackBufferCounter > 0f)` 之前）插入：

```csharp
            // 冲刺输入（最高优先级：Dash → Attack → Jump）。
            // 缓冲与冷却正交：DashBufferCounter 给亚帧容错；DashCooldownCounter 把关能力锁。
            if (_player.DashBufferCounter > 0f && _player.DashCooldownCounter <= 0f)
            {
                _player.StateMachine.ChangeState(_player.DashState);
                return;
            }

```

- [ ] **Step 5: 静态自检**

确认：① Dash 分支在 Attack/Jump 之前且带 `return`（决策⑥优先级）；② 闸门条件同时校验缓冲 `> 0` 与冷却 `<= 0`（正交，决策④）；③ 仅 GroundedState 检测 Dash —— AirborneState/SlidingState/AttackState 均不检测，故"仅地面可触发""攻击中按冲刺被忽略""空中不可冲刺"自然成立；④ 属性/字段名与 Task 1、Task 2 一致（`DashState`/`DashBufferCounter`/`DashCooldownCounter`）。

- [ ] **Step 6: 开发者编译验证（手动）**

开发者在 Unity Editor 触发编译。预期：无编译错误，冲刺状态现已可被 GroundedState 切入。

- [ ] **Step 7: Commit**

```bash
git add "Assets/_Project/Scripts/Character/PlayerController.cs" "Assets/_Project/Scripts/Character/States/PlayerGroundedState.cs"
git commit -m "feat(character): wire dash state instance and grounded trigger (Dash>Attack>Jump)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: 编辑器接线 + Play-mode 验证（开发者手动，Claude 不执行）

> 本任务无代码改动，全部在 Unity Editor 完成；subagent 不能编译/运行 Unity，仅产出此清单供开发者执行。

- [ ] **Step 1: Animator Controller 接线**

- 把 `DashForward_SingleTwohandSword` 动画拖入 `SingleTwoHandSwordHero.controller`，建一个状态节点，**节点名精确为 `DashForward_SingleTwohandSword`**（必须与 `PlayerDashState.DashStateHash` 的字符串一致，否则 CrossFade 切不过去）。
- 从该 Dash 状态加一条**回 locomotion（移动 Blend Tree）的返回连线**：`Has Exit Time` 勾选，无条件 —— 让动画播完后画面自动回到移动态（状态机由计时器退出，这条连线只负责"视觉返回"）。
- ⚠️ **Has Exit Time 的数值必须结合 `DashForward_SingleTwohandSword` clip 的真实秒数长度来设**，并与代码里的 `_dashDuration`（默认 0.2 秒，**真实秒数**）做对照：`Has Exit Time` 是归一化进度(0~1)，其对应的真实时间 = `Exit Time × clip 时长`。若该真实时间明显 **大于** `_dashDuration`，会出现"**C# 逻辑已退出冲刺态、但动画还在播冲刺动作**"的视觉/逻辑错位（与连段系统里 `normalizedTime`(归一化) 和真实秒数不一致是同一类坑）。理想情况：Exit Time 对应的真实时间 ≈ `_dashDuration`，让"逻辑退出"与"动画收尾"大致同步。
- **不要**加 `Any State → Dash` 连线（进入由代码 CrossFade 驱动；Any-State 连线会有冲刺动画被反复重触发的风险）。
- **不需要**新增 `dash` trigger 参数（CrossFade 方案不依赖 trigger）。

- [ ] **Step 2: Inspector 参数**

**先在 Project 窗口选中 `DashForward_SingleTwohandSword` clip，查看其真实秒数长度**（Inspector 预览底部或 Import Settings 里可见），用于 Step 1 的 Has Exit Time 换算与下面 `_dashDuration` 的对照。然后在 Player 上设置 `_dashSpeed`/`_dashDuration`/`_dashCooldown`/`_dashBufferTime`（默认 14 / 0.2 / 1 / 0.15，按手感微调）；若希望"逻辑退出 ≈ 动画收尾"，让 `_dashDuration` 与 clip 时长（或 Step 1 选定的 Exit Time 真实时间）大致对齐。

- [ ] **Step 3: Play-mode 验证清单**

1. 地面按冲刺键（右键 / 左 Shift）→ 角色沿**当前面朝方向**前冲，播放冲刺动画。
2. **冷却**：连点冲刺键 → 一个冷却周期内只能冲一次；冷却结束后才能再冲。
3. **离地边界（决策⑤）**：朝悬崖/缺口冲 → 冲刺水平走完整段不被打断，结束时若悬空则进入 Airborne（自然下落），不在冲刺中途被切断。
4. **攻击中忽略**：连段攻击进行中按冲刺键 → 不打断攻击（AttackState 不检测 Dash）。
5. **空中不可冲**：跳起/下落中按冲刺键 → 不触发（仅 GroundedState 检测）。
6. **无"冷却末尾偷冲"**：冷却中按一下冲刺键后静候 → 不会在冷却结束瞬间自动冲出（缓冲 0.15s ≪ 冷却 1s，按键自然过期）。
7. **零 GC**：用 Profiler 观察反复冲刺，确认 `PlayerDashState.Update` 路径 0 GC Alloc。

- [ ] **Step 4: 通过后收尾**

全部通过后，依 `superpowers:finishing-a-development-branch` 决定合并/PR。

---

## 失败模式与防御对照（实现时务必保持）

| 失败模式 | 防御 | 位置 |
|---|---|---|
| 冲刺中途被打断导致冷却没启动 | 冷却唯一启动点在 `Exit()`，`ChangeState` 必调 `Exit` | `PlayerDashState.Exit` |
| 连续按键重复触发冲刺 | Enter 消耗缓冲（清 `DashBufferCounter`）+ 冷却闸门拦截再次进入 | `PlayerDashState.Enter` + `PlayerGroundedState.CheckTransition` |
| 冷却期按键"偷冲" | 缓冲(0.15s) ≪ 冷却(1s) 自然过期；闸门要求 `Cooldown<=0` | 正交闸门 |
| 同帧攻击/跳跃与冲刺争抢 | CheckTransition 内 Dash 在最前且 `return` | `PlayerGroundedState.CheckTransition` |
| CrossFade 切不动（状态名不符） | 状态名 hash `static readonly` 预缓存；编辑器接线要求节点名精确匹配 | `DashStateHash` + Task 4 Step 1 |
| 冲刺动画被反复重触发 | 禁用 `Any State → Dash`，进入只由代码 CrossFade | Task 4 Step 1 |
| 冲刺中离地后失去贴地检测 | 沿用 `-2` 贴地压力；离地由 Exit 落点判定吸收 | `HandleDashMovement` + `TransitionToMovement` |

## Self-Review

- **决策覆盖**：① 裸字段=Task 1 Step 1；② CrossFade=Task 2（`DashStateHash`+`CrossFadeInFixedTime`）；③ Exit 启冷却=Task 2 `Exit`；④ 正交=Task 1 `OnDashPerformed` + Task 3 闸门；⑤ 冲到底/Exit 判落点=Task 2 `Update`+`TransitionToMovement`；⑥ 优先级=Task 3 Step 4。纯函数不抽=本计划无 EditMode 任务。均有对应任务。
- **占位符扫描**：无 TBD/TODO；每个代码步骤含完整代码。
- **类型一致性**：`DashSpeed/DashDuration/DashCooldown/DashBufferTime`（只读）、`DashCooldownCounter/DashBufferCounter`（读写）、`DashState`、`OnDashPerformed`、`PlayerDashState(PlayerController)`、`DashStateHash` 在各任务间命名一致。
- **作用域**：仅 `Game.Character` 三文件；零 Combat 依赖；Animator 仅在 Character 侧使用。
- **歧义**：冷却"从结束起算"已明确（Exit 启动）；"仅地面可触发"由"只有 GroundedState 检测 Dash"显式实现。
