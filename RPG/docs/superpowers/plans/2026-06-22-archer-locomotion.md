# Archer 移动（Phase 2）Implementation Plan

> **For agentic workers:** 本阶段为**开发者 Editor 执行**计划（Animator 窗口 + 预制体配置），无 `.cs`、Claude 不手改 `.controller` 文本。步骤用 checkbox 跟踪；Claude 仅在开发者完成 Editor 操作后负责提交产生的资产改动（`.controller` / `.prefab` / `.meta`）。

**Goal:** 让 Bow 角色用自己的 `BowHero.controller` 跑通移动/跳跃/冲刺/坡度，并把 `ArcherController` 正式挂到 Bow 预制体。

**Architecture:** locomotion 逻辑全继承自 `PlayerControllerBase`（零代码）。差异只在 Animator Controller 过渡线 + Inspector 的 `_dashStateName`。过渡数值镜像已验收的战士 `SingleTwoHandSwordHero.controller`。

**Tech Stack:** Unity 6.3 Animator / `Game.Character`（只读，不改）/ `BowHero.controller`。

## Global Constraints
- **零 `.cs`**：locomotion 全继承，不写代码。
- Claude **不**手改 `BowHero.controller` 文本；过渡由开发者在 Animator 窗口配。
- 参数名必须为 `speed`(float) / `jump`(Trigger) / `isGrounded`(bool)，与代码 hash 精确一致（已建好）。
- 过渡数值**镜像战士 controller**（见 §过渡表）；Duration/ExitTime 属手感调参，可微调。
- **必须**配 `Dash_Bow → Idle_Bow` 退出过渡（否则冲刺后卡姿势）。
- **不**配 `Attack01_Bow` / `Attack01Start_Bow` / `Attack01Maintain_Bow` 的任何过渡（Phase 3/4）。
- **不**动战士 `SingleTwoHandSwordHero.controller`（仅作模板参照）。
- 编译/Play 验证由开发者手动完成。

设计依据：`docs/superpowers/specs/2026-06-22-archer-locomotion-design.md`。

---

## 过渡表（条件 Mode：If=bool true，IfNot=bool false，>/<=float 比较；Duration 为 Has Fixed Duration 开下的秒数）

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

> 每条过渡的 Inspector 操作：选中源状态 → 右键 `Make Transition` → 点目标状态；选中那条箭头，在 Inspector 里：`Has Exit Time` 按表勾/不勾、（勾时）填 `Exit Time`、勾 `Fixed Duration` 并填 `Transition Duration`、在 `Conditions` 列表按表加条件。多条件即在 `Conditions` 里加多行（AND 关系）。

---

## Task 1：配置 BowHero.controller 的 locomotion 过渡 + Dash 退出 + 默认状态

**Files:**
- Modify（开发者在 Animator 窗口）：`Assets/_Project/Art/Animators/BowHero.controller`

**Interfaces:**
- Consumes：已存在的状态节点 `Idle_Bow`/`Run_Bow`/`JumpStart_Bow`/`JumpAir_Bow`/`JumpEnd_Bow`/`Dash_Bow`，参数 `speed`/`jump`/`isGrounded`。
- Produces：可运行的 locomotion 状态机（供 Task 3 验收）。

- [ ] **Step 1：打开 controller 与参数确认**
  - 双击 `Assets/_Project/Art/Animators/BowHero.controller` 打开 Animator 窗口。
  - 在 Parameters 页确认存在且类型正确：`speed`(Float)、`jump`(Trigger)、`isGrounded`(Bool)。

- [ ] **Step 2：设默认状态**
  - 右键 `Idle_Bow` → `Set as Layer Default State`（节点变橙色，Entry 自动连到它）。

- [ ] **Step 3：配 Idle/Run 互切（表 #1、#2）**
  - `Idle_Bow → Run_Bow`：条件 `speed` Greater `0.1` + `isGrounded` true；Has Exit Time **关**；Duration `0.25`。
  - `Run_Bow → Idle_Bow`：条件 `speed` Less `0.1`；Has Exit Time **关**；Duration `0.25`。

- [ ] **Step 4：配起跳进入（表 #3、#4）**
  - `Idle_Bow → JumpStart_Bow`：条件 `jump`（Trigger，无阈值）；Has Exit Time **关**；Duration `0.4`。
  - `Run_Bow → JumpStart_Bow`：条件 `jump`；Has Exit Time **关**；Duration `0.3`。

- [ ] **Step 5：配跳跃链（表 #5、#6）**
  - `JumpStart_Bow → JumpAir_Bow`：**无条件**；Has Exit Time **开**，Exit Time `0.625`；Duration `0.15`。
  - `JumpAir_Bow → JumpEnd_Bow`：条件 `isGrounded` true；Has Exit Time **关**；Duration `0.18`。

- [ ] **Step 6：配落地回移动（表 #7、#8）**
  - `JumpEnd_Bow → Run_Bow`：条件 `speed` Greater `0.1` + `isGrounded` true；Has Exit Time **开**，Exit Time `0.074`；Duration `0.25`。
  - `JumpEnd_Bow → Idle_Bow`：条件 `speed` Less `0.1`；Has Exit Time **开**，Exit Time `0.074`；Duration `0.25`。

- [ ] **Step 7：配走下边缘离地（表 #9、#10）**
  - `Idle_Bow → JumpAir_Bow`：条件 `isGrounded` false；Has Exit Time **开**，Exit Time `0.8125`；Duration `0.25`。
  - `Run_Bow → JumpAir_Bow`：条件 `isGrounded` false；Has Exit Time **开**，Exit Time `0.625`；Duration `0.25`。

- [ ] **Step 8：配 Dash 退出（表 #11，关键修正，不可省）**
  - `Dash_Bow → Idle_Bow`：**无条件**；Has Exit Time **开**，Exit Time `0.625`；Duration `0.15`。
  - 不要给 `Dash_Bow` 配任何"进入"过渡（进入由代码 CrossFade 完成）。

- [ ] **Step 9：自检（开发者）**
  - 逐状态核对出边数量：Idle=3（Run/JumpStart/JumpAir）、Run=3（Idle/JumpStart/JumpAir）、JumpStart=1（JumpAir）、JumpAir=1（JumpEnd）、JumpEnd=2（Run/Idle）、Dash=1（Idle）。
  - `Attack01_Bow`/`Attack01Start_Bow`/`Attack01Maintain_Bow` 无任何新过渡。
  - 保存（Ctrl+S）。

---

## Task 2：Bow 预制体挂 ArcherController + Inspector 配置

**Files:**
- Modify（开发者在 Editor）：`Assets/_Project/Art/Prefabs/BowHero.prefab`

**Interfaces:**
- Consumes：`ArcherController`（`Game.Character`，Phase 1 已落地）、`BowHero.controller`（Task 1）。

- [ ] **Step 1：确认基础组件**
  - 打开 Bow 预制体。根节点需有 `CharacterController` 与 `GroundChecker`（添加 `ArcherController` 时 `[RequireComponent]` 会自动补齐）；子节点的 `Animator` 的 `Controller` 字段指派为 `BowHero.controller`。

- [ ] **Step 2：添加 ArcherController**
  - 根节点 `Add Component` → `ArcherController`。

- [ ] **Step 3：配序列化字段**
  - `_cameraRoot`：拖入相机枢轴 Transform。
  - `_dashStateName`：填 `Dash_Bow`（务必与 Task 1 中 `Dash_Bow` 节点名精确一致）。
  - 其余 Movement/Jump/Slope/Camera/Dash 数值用默认值起步，后续按手感调。
  - （`ArcherController` 上没有 `MeleeHitDetector`/`ComboDefinition`/`BladeTrail` 字段，属正常——那是 Warrior 专属。）

- [ ] **Step 4：自检（开发者）**
  - Inspector 无 Missing Script / Missing 引用；`Animator` 指向 `BowHero.controller`；`_dashStateName == "Dash_Bow"`。
  - 应用/保存预制体。

---

## Task 3：Play 模式验收（开发者）

> 本阶段功能验收口径。Claude 不执行。

- [ ] **Step 1：编译**
  - 聚焦 Unity，确认 Console 无错（本阶段无代码改动，理应无新错误）。

- [ ] **Step 2：locomotion 验收**
  1. Idle ↔ Run 随移动输入切换、转向平滑
  2. 跳跃完整弧线：JumpStart → JumpAir → JumpEnd → 回 Idle/Run
  3. 走出平台边缘（非主动跳）：→ JumpAir → 落地
  4. **冲刺：进入 `Dash_Bow`，结束后回到 Idle/Run，不卡在冲刺姿势**
  5. 冲刺冷却/缓冲正常（冷却期不可再冲、缓冲亚帧容错）
  6. 坡度滑落（若有测试地形）
  7. 左键无攻击反应（弓箭手本阶段无攻击，符合预期）

- [ ] **Step 3：告知 Claude 结果**
  - 通过 → Claude 提交 Task 1/2 产生的资产改动（见 Task 4）。
  - 不通过 → 把现象告诉 Claude，回到对应 Task 排查。

---

## Task 4：提交资产改动（Claude，在开发者验收通过后）

**Files:**
- `Assets/_Project/Art/Animators/BowHero.controller`（Task 1 过渡）
- Bow 预制体 `.prefab`（Task 2）
- 可能新增的 `.meta`（Unity 生成）

- [ ] **Step 1：核对改动范围**

```bash
git status --short
```
Expected：仅 `BowHero.controller`、Bow 预制体、相关 `.meta` 有改动；无 `.cs` 改动；战士 `SingleTwoHandSwordHero.controller` 未被改。

- [ ] **Step 2：提交**

```bash
git add Assets/_Project/Art/Animators/BowHero.controller Assets/_Project/Art/Prefabs/BowHero.prefab
git commit -m "feat(character): wire BowHero locomotion transitions + attach ArcherController (Phase 2)"
```

通过后本分支可进入收尾，转入 Phase 3（弓箭手普通攻击 + 箭矢系统）。
