# 设计文档：Archer 移动（Archer Phase 2）

- **日期**：2026-06-22
- **分支**：`feat/archer-locomotion`（栈在已验收的 Phase 1 `feat/player-controller-base` 之上）
- **状态**：已通过 brainstorm，待编写实现计划
- **所属模块**：`Game.Character`（无代码改动）+ `Assets/_Project/Art/Animators/BowHero.controller`（开发者在 Animator 窗口配置）

---

## 1. 背景与阶段定位

Archer Phase 1 已完成并通过开发者 Play 回归：`PlayerController` 拆出 `PlayerControllerBase`，`ArcherController : PlayerControllerBase` 为空骨架，已临时验证能继承移动/跳跃/冲刺。

Phase 2 目标：让 Bow 角色用**自己专属的动画**跑通移动/跳跃/冲刺/坡度——即给 `BowHero.controller` 配好 locomotion 状态机过渡线，并把 `ArcherController` 正式挂到 Bow 预制体、配好参数。

**关键结论：本阶段零 `.cs` 代码。** locomotion/jump/dash 逻辑 100% 继承自 `PlayerControllerBase`，它只通过参数名（`speed`/`jump`/`isGrounded`）和 `DashStateHash`（来自序列化字段 `_dashStateName`）跟 Animator 对话，不绑定任何具体动画资源。Archer 与 Warrior 的差异完全落在"各自的 Animator Controller + 各自的 clip + 各自 Inspector 里的 `_dashStateName`"。

**配置分工（已确认）**：Claude 产出精确过渡规格（本文档），开发者在 Animator 窗口照表连线；Claude **不**手改 `BowHero.controller` 文本。

---

## 2. 需求与验收标准

### 2.1 功能需求
1. `BowHero.controller` 的 5 个 locomotion 状态（`Idle_Bow`/`Run_Bow`/`JumpStart_Bow`/`JumpAir_Bow`/`JumpEnd_Bow`）之间配好由 `speed`/`jump`/`isGrounded` 驱动的过渡线（§3 表），默认状态设为 `Idle_Bow`。
2. `Dash_Bow` 配一条**退出**过渡回 `Idle_Bow`（见 §4 关键修正）。
3. Bow 预制体挂 `ArcherController`，配好 `_cameraRoot` 等序列化引用，`_dashStateName = "Dash_Bow"`。

### 2.2 验收标准（开发者 Play 模式）
- Idle ↔ Run 随移动输入切换、转向平滑
- 跳跃完整弧线：JumpStart → JumpAir → JumpEnd → 回 Idle/Run
- 走出平台边缘（非主动跳）：→ JumpAir → 落地
- **冲刺：经 CrossFade 进入 `Dash_Bow`，结束后回到 Idle/Run（不卡在冲刺姿势）**
- 冲刺冷却/缓冲正常
- 坡度滑落（若有测试地形）
- 弓箭手本阶段无攻击（`TryStartAttack` 用基类默认 false，左键无反应是预期的）

---

## 3. Locomotion 过渡表（镜像战士 `SingleTwoHandSwordHero.controller` 的已验证数值）

条件 Mode：`If`=bool 为 true，`IfNot`=bool 为 false，`>`/`<`=float 比较。Duration 为固定秒数（Has Fixed Duration 开）。

| # | From → To | 条件 | Has Exit Time | Exit Time | Duration |
|---|-----------|------|:---:|:---:|:---:|
| 1 | Idle_Bow → Run_Bow | `speed` > 0.1 且 `isGrounded` If | 关 | – | 0.25 |
| 2 | Run_Bow → Idle_Bow | `speed` < 0.1 | 关 | – | 0.25 |
| 3 | Idle_Bow → JumpStart_Bow | `jump` If | 关 | – | 0.4 |
| 4 | Run_Bow → JumpStart_Bow | `jump` If | 关 | – | 0.3 |
| 5 | JumpStart_Bow → JumpAir_Bow | （无） | 开 | 0.625 | 0.15 |
| 6 | JumpAir_Bow → JumpEnd_Bow | `isGrounded` If | 关 | – | 0.18 |
| 7 | JumpEnd_Bow → Run_Bow | `speed` > 0.1 且 `isGrounded` If | 开 | 0.074 | 0.25 |
| 8 | JumpEnd_Bow → Idle_Bow | `speed` < 0.1 | 开 | 0.074 | 0.25 |
| 9 | Idle_Bow → JumpAir_Bow | `isGrounded` IfNot | 开 | 0.8125 | 0.25 |
| 10 | Run_Bow → JumpAir_Bow | `isGrounded` IfNot | 开 | 0.625 | 0.25 |
| 11 | Dash_Bow → Idle_Bow | （无） | 开 | 0.625 | 0.15 |

补充说明：
- **默认状态 = `Idle_Bow`**（Entry 自动指向默认状态，无需手连 Entry 线）。
- 第 3/4 行 Duration（0.4 / 0.3）镜像战士的对应值；属手感调参，可按喜好微调。
- 第 9/10 行 `Has Exit Time` 保持"开"以严格镜像战士；若日后觉得"走下边缘→下落"反应迟钝，把它改成"关"是安全的调整（条件 `isGrounded` IfNot 本身已足够触发）。
- 第 1/7 行带 `isGrounded` If 是为了"只有在地面才进 Run"，与战士一致；可保留。

---

## 4. 关键修正：`Dash_Bow` / `Attack01_Bow` 的退出过渡（必须配，不可省）

用户初始判断"Dash/Attack 状态相关的连线配不配都不影响表现"，对**进入**成立（`PlayerDashState.Enter()` / `PlayerAttackState.StartSegment()` 用 `CrossFadeInFixedTime(具体hash,...)` 直接点名跳入，不走过渡线），但对**退出不成立**：

- 冲刺结束时，`PlayerDashState.TransitionToMovement()` 只把 FSM 切到 GroundedState/AirborneState，**不做 CrossFade**。
- Animator 仍停在 `Dash_Bow`，必须靠 `Dash_Bow` **自身的出边**离开。战士正是用 `DashForward → Idle`（无条件 + Has Exit Time）退出的。
- 若 `Dash_Bow` 没有出边：冲刺后角色**视觉上卡在冲刺姿势**，直到下次冲刺/攻击再 CrossFade 才解除。

因此第 11 行（`Dash_Bow → Idle_Bow`，无条件、Has Exit Time 0.625）是本阶段必需项。退出落到 `Idle_Bow` 后，第 1 行 Idle→Run 再接管移动态。（战士还有一条永远轮不到的 `Dash→Run` 冗余出边，本设计省去。）

> `Attack01_Bow` 同理需要退出过渡，但属 Phase 3（弓箭手普通攻击）范围，本阶段不配。`Attack01Start_Bow`/`Attack01Maintain_Bow`（Phase 4 蓄力）同样本阶段不动。

---

## 5. 预制体 / Inspector 设置（开发者）

1. Bow 预制体根节点：确保有 `CharacterController` + `GroundChecker`（`PlayerControllerBase` 的 `[RequireComponent]` 会自动要求），Animator（在子节点）已指派 `BowHero.controller`。
2. 给根节点添加 **`ArcherController`** 组件。
3. 指派 `_cameraRoot`（相机枢轴 Transform）；其余 locomotion/jump/dash 数值按手感调（默认值即可起步）。
4. 设 **`_dashStateName = "Dash_Bow"`**（覆盖基类默认的战士名 `DashForward_SingleTwohandSword`）。
   - `MeleeHitDetector` / `ComboDefinition` / `BladeTrail` 是 Warrior 专属字段，`ArcherController` 上根本没有，无需理会。

---

## 6. 非目标（本阶段明确不做）
- 不写任何 `.cs`（locomotion 全继承）。
- Claude 不手改 `BowHero.controller` 文本（由开发者在 Animator 窗口配）。
- 不配 `Attack01_Bow` / `Attack01Start_Bow` / `Attack01Maintain_Bow` 的任何过渡（Phase 3/4）。
- 不动 Warrior 的 `SingleTwoHandSwordHero.controller`（只读作模板参照）。
- 不实现弓箭手攻击/箭矢。

---

## 7. 风险
- **冲刺卡姿势**：忘配第 11 行 Dash 退出 → 冲刺后定格。已在 §4 显式列为必需项。
- **`_dashStateName` 填错**：与 `BowHero.controller` 里实际节点名（`Dash_Bow`）不一致 → 冲刺 CrossFade 静默失败（不切动画、不报错）。Phase 1 已把它从硬编码改为可见的 Inspector 字段 + 空串告警，可发现性已提升；填写时照抄节点名即可。
- **参数名不一致**：`BowHero.controller` 的参数须为 `speed`(float)/`jump`(Trigger)/`isGrounded`(bool)，与代码 hash 名精确一致（用户已确认建好）。
