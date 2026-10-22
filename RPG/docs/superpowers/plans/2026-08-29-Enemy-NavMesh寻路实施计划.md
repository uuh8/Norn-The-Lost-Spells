# Enemy NavMesh 寻路 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为普通近战与远程 Enemy 增加基于静态 `NavMesh` 的自动寻路、绕障碍、远程安全后撤和持续 Repath，同时保留现有手写 FSM、`CharacterController`、状态减速与 Animator 链路；Boss 不在本轮范围内。

**Architecture:** `NavMeshAgent` 只负责 Pathfinding、Steering 与 `Local Avoidance`，通过关闭 `updatePosition/updateRotation` 避免成为第二个 Transform 写入者；`CharacterController.Move()` 继续执行真实位移与重力。新的 `EnemyNavigationMotor` 位于 `Game.Character`，由 `EnemyControllerBase` 总装，`Chase/Kite` 状态提交目标，攻击、受击、待机和死亡状态通过 FSM 生命周期停止导航。后撤候选生成、刷新判定和失败计时保持为可测试的纯逻辑。

**Tech Stack:** Unity 6.3（6000.3.16f1）、C#、AI Navigation 2.0.12、`NavMeshAgent`、`NavMeshSurface`、`NavMesh.SamplePosition`、`CharacterController`、手写 FSM、NUnit EditMode、Unity Test Runner、.NET 静态编译。

**Spec:** 项目 `AGENTS.md` 明确要求只维护一份详细实施计划、默认不创建独立 Spec；本文件同时承载已确认设计、实施步骤、Editor 配置与验收标准。

**Execution Status:** 代码、纯逻辑测试、学习文档、.NET 静态编译与 Unity Editor 脚本编译已完成；`NavMeshSurface` Bake、Prefab `NavMeshAgent` 装配、Unity Test Runner、真实 Play Mode 与 Profiler Gate 仍按 Editor Gate 单独记录。

> **2026-08-30 需求修订：** 索敌只使用 `DetectRadius/LoseRadius`；锁定后无视墙体遮挡持续 Pathfinding/Repath，直到玩家离开 `LoseRadius`；攻击资格用完整路径几何而非视线射线；移动时朝实际 Steering 方向，攻击/停下时才朝玩家。导航失败只会继续重试，不会清除目标或回到 `Idle`。`EnemyReacquisitionGate`、`UnreachableTimeout`、`ReacquireDelay` 与三个 LOS Inspector 字段不属于最终实现。

## Global Constraints

- 只修改普通 `MeleeEnemyController` 与 `RangedEnemyController` 的共享移动链；不修改 `WizardBossController` 或 Boss FSM。
- 第一阶段只支持烘焙的静态障碍；不引入运行时 `NavMesh` 重建、动态 `NavMeshObstacle.Carving`、跳跃 Link、找掩体或巡逻。
- `NavMeshAgent` 不直接写 Transform：必须设置 `updatePosition = false`、`updateRotation = false`；真实位移只能由 `CharacterController.Move()` 完成。
- 不在 `Update()`、状态 `Update()` 或导航热路径中使用 LINQ、闭包、每帧 `new`、数组分配或装箱。
- `SetDestination()` 不得逐帧无条件调用；用刷新间隔、目标位移阈值与错峰初始计时限制 Pathfinding 请求。
- 新脚本和实质修改加入简体中文学习型注释，解释职责边界、Unity API 语义、失败模式与 trade-off；日志只能使用 `GameLog`。
- 不手工创建 `.meta`；场景 Bake、Prefab 组件与 Inspector 引用由开发者在 Unity Editor 中完成。
- 静态编译、EditMode、PlayMode、真实场景 Playthrough 与 Profiler 是不同证据，交付时分别报告，不互相替代。
- 工作树已有大量用户改动；只触碰本计划列出的文件，绝不清理或覆盖无关修改。

## 玩家可见结果与 Runtime 链路

玩家进入侦测半径后，近战 Enemy 会沿烘焙路径绕过墙体接近，远程 Enemy 会沿路径接近并在贴脸时从后方、左后、右后三个可达候选中选择安全后撤点。路径计算尚未完成、路径不完整或仍在绕行时，Enemy 继续追击而不提前攻击；持续不可达时周期性重算，只有玩家离开 `LoseRadius` 才脱战，不会永久顶墙或在 `Idle/Engage` 间逐帧抖动。

```text
EnemyControllerBase.Update
  -> EnemyPerception.Tick（Detect/Lose 半径滞回）
  -> EnemyStateMachine.Update
       -> EnemyChaseState / EnemyKiteState
       -> EnemyNavigationMotor.NavigateTo / TryNavigateRetreat
       -> NavMeshAgent.SetDestination（仅满足刷新条件时）
       -> NavMeshAgent.desiredVelocity（Pathfinding + Steering + Avoidance 输出）
       -> EnemyControllerBase.MoveAlongNavigation
       -> CharacterController.Move（唯一真实位移写入）
       -> EnemyNavigationMotor.SyncToCharacter（同步 nextPosition）
  -> Animator speed <- CharacterController.velocity
```

## 文件结构与职责

| 文件 | 操作 | 单一职责 |
|---|---|---|
| `Assets/_Project/Scripts/Character/Enemy/Navigation/EnemyNavigationMath.cs` | 创建 | 纯逻辑：路径刷新判断、目标高度投影、三个后撤方向生成、有效进度判断；无 Scene 查询 |
| `Assets/_Project/Scripts/Character/Enemy/Navigation/EnemyNavigationFailureTracker.cs` | 创建 | 纯状态：累计不可达/卡住时间并给出周期 Repath 信号，不改变锁敌状态 |
| `Assets/_Project/Scripts/Character/Enemy/Navigation/EnemyNavigationMotor.cs` | 创建 | Unity Adapter：封装 `NavMeshAgent`、目标高度/边缘回退采样、路径更新、候选路径筛选、实际位置同步 |
| `Assets/_Project/Tests/Character/EnemyNavigationMathTests.cs` | 创建 | EditMode：锁定刷新阈值、目标高度投影、候选方向与零方向回退 |
| `Assets/_Project/Tests/Character/EnemyNavigationFailureTrackerTests.cs` | 创建 | EditMode：锁定连续失败、周期 Repath、恢复清零与不丢目标 |
| `Assets/_Project/Scripts/Combat/Definitions/EnemyDefinition.cs` | 修改 | 增加数据驱动的刷新、采样、后撤、失败恢复参数 |
| `Assets/_Project/Scripts/Character/Controllers/EnemyControllerBase.cs` | 修改 | 总装导航组件，保留 `CharacterController` 为唯一移动执行者，提供路径能力 |
| `Assets/_Project/Scripts/Character/Enemy/EnemyPerception.cs` | 修改 | 保持 Detect/Lose 半径与滞回语义，不增加遮挡判断 |
| `Assets/_Project/Scripts/Character/Enemy/States/EnemyIdleState.cs` | 修改 | `Enter` 清理导航；仅在感知真正丢失目标时待机 |
| `Assets/_Project/Scripts/Character/Enemy/States/EnemyChaseState.cs` | 修改 | 沿完整/部分路径持续追击；完整路径几何满足时才近战攻击 |
| `Assets/_Project/Scripts/Character/Enemy/States/EnemyKiteState.cs` | 修改 | 导航接近、候选点后撤；完整路径几何满足时才远程攻击 |
| `Assets/_Project/Scripts/Character/Enemy/States/EnemyAttackState.cs` | 修改 | 攻击进入时确保导航停止，避免内部 Agent 模拟漂移 |
| `Assets/_Project/Scripts/Character/Enemy/States/EnemyRangedAttackState.cs` | 修改 | 同上 |
| `Assets/_Project/Scripts/Character/Enemy/States/EnemyHurtState.cs` | 修改 | 受击进入时停止导航 |
| `Assets/_Project/Docs/2026-08-29 Enemy NavMesh寻路完整解析（更新中）.md` | 创建 | 初学者学习文档：原理、链路、trade-off、Debug、Editor、面试表达 |

---

### Task 1: 纯导航决策与失败计时

**Files:**
- Create: `Assets/_Project/Tests/Character/EnemyNavigationMathTests.cs`
- Create: `Assets/_Project/Tests/Character/EnemyNavigationFailureTrackerTests.cs`
- Create: `Assets/_Project/Scripts/Character/Enemy/Navigation/EnemyNavigationMath.cs`
- Create: `Assets/_Project/Scripts/Character/Enemy/Navigation/EnemyNavigationFailureTracker.cs`

**Interfaces:**
- Produces: `EnemyNavigationMath.ShouldRefreshPath(float elapsed, Vector3 previousTarget, Vector3 currentTarget, float interval, float moveThreshold)`。
- Produces: `EnemyNavigationMath.FillRetreatDirections(Vector3 awayDirection, Vector3[] output) -> int`，固定写入后方、左后 60°、右后 60°三个水平单位方向，不产生数组分配。
- Produces: `EnemyNavigationMath.HasProgress(Vector3 previous, Vector3 current, float minimumDistance)`。
- Produces: `EnemyNavigationFailureTracker.Tick(float deltaTime, bool pathUnreachable, bool wantsToMove, bool madeProgress, float repathInterval) -> EnemyNavigationFailureSignal`。
- Produces: `EnemyNavigationFailureSignal.None / Repath`，恢复为可达或不再期望移动时调用 `Reset()`；该信号只触发重算，不改变目标。

- [x] **Step 1: 写后撤方向与刷新策略的失败测试**

  测试必须以手算字面值断言：`Vector3.back` 的三个候选应是后方与绕 Y 轴 ±60°；目标位移未过阈值且刷新间隔未到时返回 false，任一阈值达到时返回 true；零向量应回退 `Vector3.back`。

- [x] **Step 2: 写失败计时的失败测试**

  覆盖连续不可达持续触发 `Repath`、恢复可达后计时清零、没有移动意图时不误判卡住。

- [x] **Step 3: 运行 RED 验证**

  Run: `dotnet build Game.Character.Tests.csproj --no-restore`

  Expected: FAIL，原因是 `EnemyNavigationMath`、`EnemyNavigationFailureTracker` 与枚举尚不存在；不得接受测试语法或 asmdef 引用错误造成的失败。

- [x] **Step 4: 实现最小纯逻辑**

  使用 `Vector3.RotateTowards` 或 `Quaternion.AngleAxis` 只在调用栈上生成值类型结果；输出数组由调用方持有。失败跟踪器只保存浮点计时与周期 Repath 状态，不引用 `MonoBehaviour`、`NavMeshAgent` 或 Scene 对象。

- [x] **Step 5: 运行 GREEN 与回归编译**

  Run: `dotnet build Game.Character.Tests.csproj --no-restore`

  Expected: build succeeds with 0 errors。Unity Test Runner 中真实执行 NUnit 仍列入 Editor 验收，命令行 build 只证明测试与生产程序集静态可编译。

---

### Task 2: NavMeshAgent 适配层

**Files:**
- Create: `Assets/_Project/Scripts/Character/Enemy/Navigation/EnemyNavigationMotor.cs`
- Modify: `Assets/_Project/Scripts/Combat/Definitions/EnemyDefinition.cs`
- Modify: `Assets/_Project/Scripts/Character/Controllers/EnemyControllerBase.cs`

**Interfaces:**
- Consumes: Task 1 的刷新、候选方向与失败跟踪 API。
- Constructs: `EnemyNavigationMotor(NavMeshAgent agent, EnemyDefinition definition, Transform owner)`，在构造时关闭 Agent 的 Transform 写入。
- Produces: `BeginNavigation()`、`StopNavigation(bool clearPath)`、`DisableNavigation()`。
- Produces: `NavigateTo(Vector3 worldTarget, float stoppingDistance, float moveSpeed, float deltaTime) -> EnemyNavigationResult`。
- Produces: `TryNavigateRetreat(Vector3 threatPosition, float moveSpeed, float deltaTime) -> EnemyNavigationResult`。
- Produces: `SyncToCharacter()`，每次 `CharacterController.Move` 后同步 Agent 模拟位置。
- Produces: 只读 `PathPending`、`HasCompletePath`、`RemainingDistance`、`DesiredVelocity`。
- Produces: `EnemyControllerBase.PlanNavigationTo(...)`、`PlanNavigationAwayFrom(...)`、`CanAttackThroughNavigation(Vector3 targetPos, float range)`；导航失败不改变感知目标。

- [x] **Step 1: 为可观察的导航刷新与失败行为补失败测试**

  在 Task 1 测试中先增加“目标小幅抖动不会重复刷新”“发生有效位移会清除卡住累计”“持续失败会再次 Repath”的用例，并运行 RED，确保它们因缺少对应分支失败。

- [x] **Step 2: 实现 `EnemyDefinition` 导航参数**

  增加以下默认数据并用 Tooltip 解释单位与 trade-off：

  - `PathRefreshInterval = 0.2f`
  - `DestinationMoveThreshold = 0.5f`
  - `DestinationSampleRadius = 2f`
  - `RetreatStepDistance = 4f`
  - `RetreatSampleRadius = 1.5f`
  - `StuckCheckInterval = 0.5f`
  - `StuckProgressDistance = 0.05f`

- [x] **Step 3: 实现 `EnemyNavigationMotor`**

  在构造函数中缓存组件、预分配 `Vector3[3]` 和一个复用 `NavMeshPath`；禁止热路径 `new`。初始化时关闭 Agent 的 Transform 写入与旋转写入。`NavigateTo` 先用 `NavMesh.SamplePosition` 投影目标，真实高度查询失败时将目标 Y 投影到 Agent 所在水平面，仍失败时只做一次受控扩大半径查询，再按刷新策略调用 `SetDestination`，从 `desiredVelocity` 提供 Steering。路径为 `Partial` 时允许沿现有路径走到边界但永远不报告可攻击；`Invalid` 时原地贴地并持续触发失败跟踪。

- [x] **Step 4: 实现后撤候选筛选**

  三个候选按纯逻辑生成；逐个 `SamplePosition`，排除未增加与玩家水平距离的点，再用复用 `NavMeshPath` 过滤非 `PathComplete` 候选，选择拉开距离最大的点。只有候选变化超过阈值或刷新到期才重新筛选。

- [x] **Step 5: 接入 `EnemyControllerBase` 移动权威**

  添加 `[RequireComponent(typeof(NavMeshAgent))]` 并缓存 Motor/Agent。把状态倍率后的 MoveSpeed 同步给 Agent，读取 `desiredVelocity` 后由 `_cc.Move()` 执行；重力仍沿用 `_verticalVelocity`。移动结束必须调用 Motor 同步模拟位置。`StayGrounded` 不产生水平移动，并在状态 Enter 中由明确的 Stop 调用阻止 Agent 漂移。

- [x] **Step 6: 处理目标高度偏移**

  目标 Transform 可能来自角色中心、跳跃位置或视觉锚点，导致它与脚下 NavMesh 的垂直差超过采样半径；障碍顶部也可能成为“采样成功但路径不连通”的错误候选。Motor 优先把玩家 XZ 投影到当前 Agent 导航平面并验证路径，再比较同层、原始高度和扩大半径三个固定查询；`PathComplete` 优先于 `PathPartial`，同质量时选更接近玩家 XZ 的端点。始终保留 XZ 并持续追击，不引入视线射线或遮挡 Mask。

- [x] **Step 7: 静态编译**

  Run: `dotnet build Game.Character.csproj --no-restore`

  Expected: build succeeds with 0 errors；若 Unity 自动生成 csproj 尚未收录新文件，必须明确标注为工程文件未 Refresh，不能把它误报为生产代码验证通过。

---

### Task 3: FSM、感知与攻击资格接入

**Files:**
- Modify: `Assets/_Project/Scripts/Character/Enemy/EnemyPerception.cs`
- Modify: `Assets/_Project/Scripts/Character/Enemy/States/EnemyIdleState.cs`
- Modify: `Assets/_Project/Scripts/Character/Enemy/States/EnemyChaseState.cs`
- Modify: `Assets/_Project/Scripts/Character/Enemy/States/EnemyKiteState.cs`
- Modify: `Assets/_Project/Scripts/Character/Enemy/States/EnemyAttackState.cs`
- Modify: `Assets/_Project/Scripts/Character/Enemy/States/EnemyRangedAttackState.cs`
- Modify: `Assets/_Project/Scripts/Character/Enemy/States/EnemyHurtState.cs`

**Interfaces:**
- Consumes: Task 2 的 Base 导航能力与 `EnemyDefinition` 的 Detect/Lose 数据。
- Produces: `EnemyPerception.Tick()` 的半径感知与滞回，不因墙体或导航失败清除 Target。
- Produces: Chase/Kite 的完整生命周期：Enter 开始导航、Exit 停止并清理路径。

- [x] **Step 1: 先写持续追击语义的失败测试**

  测试导航失败只产生周期 `Repath`，不会发出放弃目标信号；脱战边界只由 `LoseRadius` 决定。先运行 RED。

- [x] **Step 2: 固化纯半径感知**

  `EnemyPerception.Tick()` 只使用水平距离和 `DetectRadius/LoseRadius` 滞回；墙体、朝向、路径状态都不参与锁敌或丢失目标判断。

- [x] **Step 3: 改造近战 Chase**

  进入时启用导航；更新时先导航到玩家目标。只有 `PathComplete`、非 Pending、`RemainingDistance <= AttackRange` 且路径几何进入最终直达段时才能进入攻击。冷却中到达攻击距离则停止水平移动并贴地；导航失败仍保留目标并继续重试。

- [x] **Step 4: 改造远程 Kite**

  太远时导航接近；太近时调用候选点后撤；范围带内只有完整路径几何进入最终直达段才施法。候选全部失败时保留目标并持续重算，不回 Idle。

- [x] **Step 5: 固化状态资源释放**

  `Idle.Enter`、`Attack.Enter`、`RangedAttack.Enter`、`Hurt.Enter` 停止导航；`Chase/Kite.Exit` 清理路径。死亡钩子在 Base 中先 `DisableNavigation`，然后调用子类清理，保证近战命中窗口逻辑不丢失。

- [x] **Step 6: 运行测试程序集编译**

  Run: `dotnet build Game.Character.Tests.csproj --no-restore`

  Expected: build succeeds with 0 errors。

---

### Task 4: 学习文档与 Unity Editor 装配

**Files:**
- Create: `Assets/_Project/Docs/2026-08-29 Enemy NavMesh寻路完整解析（更新中）.md`

**Interfaces:**
- Consumes: Tasks 1-3 的最终代码与 Inspector 字段。
- Produces: 可独立学习、复盘与面试表达的中文文档，以及逐项 Editor 验收清单。

- [x] **Step 1: 编写数百字 Task 总览**

  从玩家可见问题“直线追击撞墙”开始，解释 `NavMesh` 是烘焙的可行走空间图，`NavMeshAgent` 提供路径与 Steering，`CharacterController` 是真实运动执行者，FSM 决定何时追、退、停、打。

- [x] **Step 2: 记录完整 Runtime 链与 Assembly 职责**

  逐文件列出创建/修改脚本；说明 `Game.Combat` 只保存 Enemy 数据，`Game.Character` 拥有路径行为和 Unity Adapter；解释 `Pending/Complete/Partial/Invalid`、`SetDestination` 限频、`nextPosition` 同步、目标高度回退和候选点评分。

- [x] **Step 3: 记录替代方案与 trade-off**

  对比 Agent 全权移动、Agent 规划 + CharacterController 执行、手写 Corner Following；说明为何第一阶段不支持动态障碍、运行时重建、复杂掩体 AI。

- [x] **Step 4: 写 Editor 配置清单**

  明确要求：

  1. 在 `P7_DemoRun` 新建 `Navigation` GameObject 并添加 `NavMeshSurface`。
  2. 配置只收集地面与静态环境 Layer，Agent Type 使用 Humanoid 或与 Enemy 尺寸匹配的自定义类型。
  3. Bake 后用蓝色可行走区域确认墙体周围留出至少 Agent Radius。
  4. 给 `SwordEnemy_Base`、`MagicEnemy_Base` 添加并配置 `NavMeshAgent`，`Radius/Height/Base Offset` 贴合 `CharacterController`；关闭 Agent 自身自动旋转/位移由代码保证。
  5. 检查 P7 Elite Prefab Variant 是否继承 Base 组件。
  6. 将所有出生点放在已烘焙 `NavMesh` 上。

- [x] **Step 5: 写 Play Mode 与 Profiler 验收矩阵**

  覆盖单墙绕行、U 形障碍、完全封闭不可达、近战隔墙、远程隔墙、远程贴脸后撤、多敌人拥堵、受击/攻击中停止、死亡停止、暂停恢复。Profiler 检查 Pathfinding 峰值是否错峰、`GC Alloc` 是否为 0B、`SetDestination` 是否未逐帧调用。

---

### Task 5: 最终验证与交付审查

**Files:**
- Review all files listed above.

**Interfaces:**
- Produces: 静态验证证据、未完成的 Unity Editor 验证边界与最小回滚方案。

- [x] **Step 1: 运行格式与差异审查**

  Run: `git diff --check`

  Expected: no whitespace errors。随后用 `git diff -- <本计划文件列表>` 确认没有修改 Boss、Scene、Prefab、Package 或用户无关文件。

- [x] **Step 2: 运行静态编译**

  Run: `dotnet build Game.Character.csproj --no-restore`

  Run: `dotnet build Game.Character.Tests.csproj --no-restore`

  Expected: both succeed with 0 errors；warnings 单独记录，不能省略。

- [ ] **Step 3: Unity Editor 待验证项**

  开发者 Refresh 后运行 Unity Test Runner 的 `Game.Character.Tests`，再完成 NavMesh Bake、Prefab 组件装配与上述 Play Mode 矩阵。只有 Unity Editor 实际输出才能证明序列化、Agent 上图、路径状态与 Animator/FSM 联动。

- [x] **Step 4: 回滚与 Debug 顺序**

  若 Enemy 不动，按 `NavMeshSurface 是否 Bake -> 出生点/Agent 是否 on NavMesh -> 目标真实高度采样/水平投影回退 -> SetDestination 是否成功 -> pathPending/pathStatus -> desiredVelocity -> CharacterController.Move -> nextPosition 同步` 排查；若隔墙停住，检查 Bake 后路径是否真的连通，不检查视线 Mask；若远程不后撤，检查采样半径、候选是否增加距离及路径是否 Complete。代码回滚只需移除 Motor 接入并恢复 Chase/Kite 的直接 `MoveTo/MoveAway`，不影响 Combat、Spell 或 Boss。
