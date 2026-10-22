# Enemy NavMesh 寻路完整解析（更新中）

> 当前状态：代码实现、`NavMeshSurface` Bake、Enemy Base Prefab 的 `NavMeshAgent` 装配与静态编译已完成；三个生产 `EnemyDefinition` 已显式写入导航参数，测试程序集当前共 23 个用例（`EnemyNavigationMath` 16 个、失败恢复 4 个、生产资源 Contract 3 个）。之前的 Unity Test Runner 已验证原有 16 个用例；新增用例的完整 Runner 重跑、真实场景 Play Mode 行为回归和 Profiler `GC Alloc` 仍需继续验证。在这些 Gate 完成前，不能把功能标记为最终验收通过。

## Task 1：静态障碍寻路、远程后撤与不可达恢复

本 Task 解决的玩家可见问题是：Enemy 发现玩家后只计算“玩家位置减去自己位置”这一条直线方向，然后调用 `CharacterController.Move()`，所以墙壁一旦挡在两者之间，它既不知道墙后存在另一条路，也不知道当前目标可能完全不可达，只会持续顶墙。现在普通近战和远程 Enemy 会把场景中预先烘焙的可行走空间交给 `NavMesh`，由 `NavMeshAgent` 计算路径拐角、下一个 Steering 速度和多个 Enemy 之间的 `Local Avoidance`；真正修改角色位置的仍然是现有 `CharacterController`。近战会沿路径绕障碍接近，远程会沿路径接近并在玩家贴脸时从后方、左后、右后三个候选中选一个完整可达的后撤点。索敌只使用 `DetectRadius/LoseRadius`，没有视野锥或墙体 `Line of Sight`；一旦锁定，即使中间有墙也会继续寻路、周期 Repath，直到玩家离开 `LoseRadius`。移动时 Enemy 朝实际 Steering 方向转身，只有停下和攻击时才面向玩家。

### 1. 为什么 NavMesh 能解决“撞墙”

`NavMesh`（Navigation Mesh）可以理解成一张“角色允许行走的地面地图”。Unity 在 Editor 中执行 Bake 时，会根据地面、墙体、Agent 半径、台阶高度和坡度等配置，生成一片连通的多边形区域。墙体占据的空间不会进入这张地图，离墙太近、宽度不足以容纳 Enemy 胶囊体的区域也会被裁掉。

原来的直线追击只有一个局部方向：

```text
direction = playerPosition - enemyPosition
```

这个方向没有“地图”信息，所以看不到障碍。Pathfinding 会在 `NavMesh` 连通图上寻找从起点到终点的路径，结果可能是：

- `PathComplete`：存在从 Enemy 到目标的完整路径。
- `PathPartial`：只能走到某个边界，之后没有连通区域。
- `PathInvalid`：当前没有可用路径。
- `pathPending`：异步路径计算尚未完成，结果暂时不能用于攻击判断。

代码不能把“直线距离很近”直接等价为“已经走到可攻击位置”。薄墙两侧的两个对象直线距离可能只有一米，但实际完整路径要绕很远，甚至完全不存在。因此本 Task 不做视野射线，而用路径几何判断攻击资格：路径必须是 `PathComplete`，`remainingDistance` 与直线距离都进入攻击范围，并且两者差值不应仍表现为明显绕行。这样墙不会让 Enemy 丢失目标，同时 Enemy 也不会因为直线距离先进入范围而提前停在墙后。

### 2. 为什么没有让 NavMeshAgent 直接移动角色

项目原有移动权威是 `CharacterController`，它负责碰撞约束、重力贴地以及实际 `velocity`；状态减速来自 `StatusController.MoveSpeedMultiplier`；Animator 又读取 `CharacterController.velocity` 驱动移动参数。如果直接打开 `NavMeshAgent.updatePosition`，Agent 和 `CharacterController` 都可能修改同一个 Transform，形成两个移动权威：一个系统刚把角色推到路径位置，另一个系统又根据碰撞或重力改位置，容易出现抖动、穿透、速度不一致和难以复现的反馈循环。

最终采用单向数据流：

```text
Enemy FSM 决定去哪里
  -> NavMeshAgent 计算 Pathfinding / Steering / Local Avoidance
  -> EnemyNavigationMotor 暴露 desiredVelocity
  -> EnemyControllerBase 叠加状态速度与重力
  -> CharacterController.Move() 写入真实位置
  -> EnemyNavigationMotor 把 Agent.nextPosition 同步到真实位置
  -> Animator 读取 CharacterController.velocity
```

`NavMeshAgent.updatePosition = false`、`updateRotation = false` 是这里最重要的边界。Agent 保留“导航模拟器”职责，`CharacterController` 保留“场景运动执行器”职责。信息只从 Agent 流向 Controller，再把最终位置同步回 Agent，不允许两个组件同时争抢 Transform。

### 3. FSM 如何管理导航生命周期

Enemy 的 State 是预先创建并复用的普通 C# 对象。`EnemyStateMachine.ChangeState()` 无条件执行旧状态 `Exit()`、替换引用、再执行新状态 `Enter()`，所以 `Enter/Exit` 是导航资源最稳定的开关边界：

- `EnemyIdleState.Enter()`：停止 Agent 并清除旧路径。
- `EnemyChaseState.Enter()`：启用寻路；`Update()` 提交玩家目标、读取路径结果并移动。
- `EnemyChaseState.Exit()`：停止并清理路径，保证攻击或受击后旧路径不会继续模拟。
- `EnemyKiteState.Enter/Exit()`：与 Chase 相同，但 Update 还会选择后撤候选。
- `EnemyAttackState.Enter()`、`EnemyRangedAttackState.Enter()`：再次停止导航，形成防御性边界；攻击期间只执行原地重力。
- `EnemyHurtState.Enter()`：停止导航，防止受击硬直期间被 Agent 推走。
- `DeathEvent`：禁用 Agent，再执行子类死亡清理；近战子类仍会正确关闭命中窗口。

这延续了项目已有的设计原则：State 决定“当前做什么”，`EnemyControllerBase`/`EnemyNavigationMotor` 提供“具体怎么移动”的能力；资源释放集中在 `Exit()` 或明确的状态 `Enter()`，避免各状态遗留内部路径。

### 4. 路径刷新为什么不能每帧 SetDestination

玩家每帧都可能移动，但这不意味着 Enemy 必须每帧重新运行 Pathfinding。无条件在每次 State Update 调用 `SetDestination()`，会让多个 Enemy 在相同帧一起提交路径请求，CPU 峰值随着敌人数增加而放大。

本实现满足以下条件之一才刷新：

- 第一次进入导航状态；
- 距离上次路径请求超过 `PathRefreshInterval`；
- 玩家相对上次目标点的水平位移超过 `DestinationMoveThreshold`；
- 卡住检测要求强制重算。

第一次路径请求立即发生；第二次刷新根据 Enemy Instance ID 产生很小的错峰偏移，之后恢复固定间隔。这样玩家不会明显感到响应延迟，又能避免所有 Enemy 在同一帧形成 Pathfinding 尖峰。失败的目标投影同样受刷新间隔限制，不会因为 `NavMesh.SamplePosition()` 失败就在每一帧疯狂重试。

### 5. 远程 Enemy 如何选择后撤点

直接使用“自己减去玩家”的反方向，只能说明理想方向，不能证明该位置在 `NavMesh` 上或存在完整路径。本实现生成三个固定候选：正后方、左后 60°、右后 60°。候选数组在 Motor 构造时只分配一次，热路径重复写入，不产生每帧 GC。

每个候选依次经过：

```text
理想候选位置
  -> NavMesh.SamplePosition：投影到附近可行走表面
  -> 与玩家水平距离必须大于当前位置
  -> NavMeshAgent.CalculatePath：必须是 PathComplete
  -> 按与玩家的距离评分，选择拉开距离最大的候选
```

候选生成公式放在 `EnemyNavigationMath` 纯逻辑中。XZ 平面上的 ±60° 旋转使用显式矩阵公式，而没有调用依赖 Unity Native Runtime 的 `Quaternion.AngleAxis`。这让算法能脱离 Scene 执行测试，也直接复习了二维旋转：

```text
x' = cosθ * x + sinθ * z
z' = -sinθ * x + cosθ * z
```

第一阶段只解决安全后撤，不包含找掩体、横向绕圈、团队槽位或 `Utility AI`。这是 YAGNI：先让现有 Kite 行为不撞墙，再根据真实玩法和 Profiler 证据扩展。

### 6. 不可达、卡住与持续追击

`PathPartial` 并不会让 Enemy 放弃玩家。只要 Agent 仍然提供有效 `desiredVelocity`，Enemy 会先沿部分路径走到最近可达边界；到达边界后仍无法形成完整路径，就按照 `PathRefreshInterval` 周期重算。脱战的唯一条件仍是玩家离开 `LoseRadius`。

另一种情况是路径显示 Complete，但多个 Enemy 拥挤或碰撞让 `CharacterController` 没有实际前进。Motor 每隔 `StuckCheckInterval` 比较实际位置，如果 Agent 有移动意图而水平位移小于 `StuckProgressDistance`，就发出 `Repath`，之后继续追击，不再存在 `Abandon` 或“短暂禁止重新索敌”。

路径刷新时 `NavMeshAgent.pathPending` 可能短暂给出零 Steering。若 Agent 仍持有旧路径，Motor 会保留上一帧有效速度，避免每次重算都肉眼可见地刹停；若新目标采样失败，也保留旧路径并继续重试。Agent 因出生点误差或运行中同步误差暂时不在 NavMesh 上时，会在进入追击及追击期间用 `NavMesh.SamplePosition + Warp` 尝试重新贴合。

朝向和位移使用同一份运动依据：有导航速度时朝 `desiredVelocity` 转身；没有移动速度、等待冷却或进入攻击态时才朝玩家转身。这消除了“身体始终盯着玩家却横向绕墙”的强烈人机感。

### 7. 数据、脚本与 Assembly 责任

#### 创建文件

- `Enemy/Navigation/EnemyNavigationMath.cs`：纯数学规则；路径刷新、后撤方向、进展距离。
- `Enemy/Navigation/EnemyNavigationFailureTracker.cs`：纯失败累计；只输出 `None/Repath`，不直接操作 FSM，也不允许导航失败改变锁敌状态。
- `Enemy/Navigation/EnemyNavigationMotor.cs`：Unity Adapter；唯一直接读取 `NavMeshAgent/NavMesh` API 的 Enemy 导航对象。
- `Tests/Character/EnemyNavigationMathTests.cs`：候选方向、阈值、浮点边界、朝向选择、Pending 速度保留和攻击路径几何测试。
- `Tests/Character/EnemyNavigationFailureTrackerTests.cs`：持续失败反复 Repath、长时间不丢目标与恢复清零测试。

#### 修改文件

- `Game.Combat/EnemyDefinition.cs`：继续承载现有 Enemy AI 数据，新增 Path 刷新、采样、后撤、卡住与重索敌参数。这里保存纯数值，不依赖 `Game.Character`。
- `EnemyControllerBase.cs`：总装 Agent/Motor，叠加状态速度与重力，保留 `CharacterController` 移动权威，并按 Steering 控制移动朝向。
- `EnemyPerception.cs`：只负责 360° 半径感知与 `DetectRadius/LoseRadius` 滞回；墙体和朝向都不影响已锁定目标。
- `EnemyIdle/Chase/Kite/Attack/RangedAttack/HurtState.cs`：把导航启停与状态生命周期对齐。

`Game.Character` 可以依赖 `Game.Combat` 的数据；`Game.Combat` 没有反向依赖导航行为。没有新增跨模块全局事件，因为导航只发生在单个 Enemy 内部，不需要通过 `EventBus` 广播。

### 8. 替代方案与 trade-off

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| Agent 直接移动 Transform | 接入代码少，原生避障完整 | 绕过现有 CharacterController、重力、状态倍率与 Animator 速度链，形成移动权威冲突 | 未采用 |
| Agent 算路，CharacterController 执行 | 保留现有架构，获得 Pathfinding/Steering/Local Avoidance | 必须正确同步 nextPosition，状态启停更严格 | 本 Task 采用 |
| `NavMesh.CalculatePath` + 手写 Corner Following | 移动控制完全自定义 | 还要自己写 Corner 推进、重算、减速和多 Agent 避让，重复 Unity 已有能力 | 暂不采用 |

静态 Bake 的优点是运行稳定、CPU 成本可控；代价是运行时新增墙体或 ElementWorld 动态地形不会自动改变路径。本 Task 明确不把 ElementWorld 网格变化接入动态 NavMesh 重建，因为高频更新会引入昂贵的 Surface Build、同步时序和新的性能风险。

### 9. 测试与当前证据

已完成的证据：

- TDD RED：生产类型不存在时，测试程序集以预期 `CS0246/CS0103` 失败。
- Unity Test Runner：筛选 `EnemyNavigation` 后 16 个 EditMode 测试全部通过，0 fail；覆盖刷新阈值、三个后撤方向、浮点距离边界、移动朝向、Pending 速度保留、攻击路径几何、持续 Repath 和恢复清零。
- .NET 静态编译：`Game.Character.csproj` 与 `Game.Character.Tests.csproj` 为 0 warning / 0 error。
- Unity Editor 自动刷新：Editor Log 记录 `Tundra build success`，`Game.Character.dll`、`Game.Character.Tests.dll` 和相关程序集完成 IL Post Process。

尚未完成的证据：

- Play Mode 场景绕行、后撤、不可达持续重算与 FSM 联动的完整人工回归。
- Profiler 的 CPU Pathfinding 峰值与 `GC Alloc = 0B`。

### 10. 你需要在 Unity Editor 中做的事

#### 10.1 Scene 与 NavMeshSurface

1. 打开 `Assets/_Project/Scenes/P7_DemoRun.unity`。
2. 新建空物体 `Navigation`，位置保持 `(0,0,0)`。
3. 添加 `NavMeshSurface`。
4. `Agent Type` 先使用 Humanoid；若 Enemy 胶囊明显更宽，再创建匹配 Enemy 的自定义 Agent Type。
5. `Collect Objects` 可先使用 `All`；若 Scene 中非环境对象过多，改用 `Volume` 并覆盖整个关卡。
6. `Include Layers` 只包含地面和静态关卡几何所在 Layer。Player、Enemy、Projectile、Trigger、VFX、ElementField 表现网格不要参与 Bake。
7. 点击 Bake，在 Scene 视图打开 Navigation 可视化，确认蓝色区域能绕过墙体，墙边与狭窄门口保留足够 Agent Radius。

#### 10.2 Enemy Prefab

分别打开：

- `Assets/_Project/Art/Prefabs/BaseEnemy/SwordEnemy_Base.prefab`
- `Assets/_Project/Art/Prefabs/BaseEnemy/MagicEnemy_Base.prefab`

给根节点添加 `NavMeshAgent`，建议初始值：

- `Agent Type`：必须与 P7 `NavMeshSurface` Bake 使用的类型一致。
- `Radius`：与现有 `CharacterController.radius` 接近，不要大于狭窄通道的一半。
- `Height`：与 `CharacterController.height` 接近。
- `Base Offset`：让 Agent 胶囊底部贴地，不要悬空或埋入地面。
- `Speed`：Inspector 值只作初始显示，Runtime 会用 `EnemyDefinition.MoveSpeed × StatusMultiplier` 覆盖。
- `Acceleration`：先用 `8~16`，过低会转角拖沓，过高可能显得瞬间变速。
- `Angular Speed`：代码关闭 `updateRotation`，实际朝向由 FSM 控制；移动中看 Steering，攻击/停下时才看玩家。
- `Stopping Distance`：Runtime 按近战/远程状态覆盖。
- `Obstacle Avoidance`：开启；多个 Enemy 场景中再调整 Quality 与 Avoidance Priority。

检查 `P7_EliteSwordEnemy`、`P7_EliteMagicEnemy` Prefab Variant 是否正确继承 Agent。若 Variant 覆盖了根组件，需逐个确认。

#### 10.3 索敌规则

Controller Inspector 不再有 `Line Of Sight Obstacle Mask/Origin Height/Target Height`。索敌只有两项数据：未锁定时使用 `DetectRadius`，已锁定时使用更大的 `LoseRadius`。墙体不会中断追击；攻击是否就绪由完整路径和路径距离判断。

#### 10.4 出生点

每个普通 Enemy 的根节点应位于 Bake 后的蓝色区域上。Agent Type 不一致或出生点离 NavMesh 太远时，Console 会出现 `EnemyNavigation` 警告；Motor 会周期尝试重新贴合和 Repath，但不会退回旧的直线撞墙，也不会因为导航失败清除玩家目标。

### 11. Play Mode 验收矩阵

1. **单墙绕行**：玩家与近战 Enemy 之间放一面可绕过的墙；Enemy 应从侧面绕行。
2. **U 形障碍**：Enemy 位于 U 内、玩家在外；它应沿开口离开，不持续顶住 U 的底部。
3. **完全封闭**：玩家位于无出口区域；Enemy 走到最近边界并持续周期重算，玩家仍在 `LoseRadius` 内时不得回 Idle。
4. **近战隔墙**：直线距离小于 AttackRange 但路径仍需绕行；不得停在墙后挥砍，应继续走向最终接近段。
5. **远程隔墙**：距离在施法范围内但路径仍明显绕行；不得停在墙后，应继续沿路径接近。进入最终可攻击段后允许施法，本系统没有射线视野规则。
6. **远程贴脸**：玩家接近到 RetreatDistance 内；远程 Enemy 应选择可达后撤点，不倒退撞墙。
7. **候选受阻**：正后方被墙挡住、侧后方开放；Enemy 应选择左后或右后。
8. **多 Enemy 拥堵**：同时激活多个敌人；观察 Local Avoidance，允许短暂拥挤，但不能全部永久卡死。
9. **受击/攻击**：进入 Hurt 或 Attack 后必须停住；动画结束回 Engage 后重新寻路。
10. **死亡**：死亡后 Agent 禁用，尸体不继续沿路径滑动。
11. **暂停恢复**：打开 Wand Editor 暂停后不应出现位置漂移，恢复后路径继续更新。

### 12. Profiler 与 Debug 路线

#### 症状：Enemy 完全不动

```text
P7 是否存在 NavMeshSurface
-> 是否已 Bake 且 Scene 可见蓝色区域
-> Enemy 的 Agent Type 是否匹配 Surface
-> 出生点是否 on NavMesh
-> pathPending 是否结束
-> pathStatus 是否 Complete/Partial/Invalid
-> desiredVelocity 是否非零
-> CharacterController.Move 是否执行
-> Agent.nextPosition 是否同步到实际 Transform
```

#### 症状：仍然停在墙后或过早攻击

检查墙体是否参与 `NavMeshSurface` Bake、蓝色区域是否从墙体穿过；再观察 `pathStatus/remainingDistance`。本系统没有 `Line of Sight Mask`，墙体是否构成绕行由烘焙后的 NavMesh 拓扑决定。

#### 症状：远程 Enemy 不后撤

确认 `RetreatDistance` 大于实际直线距离；检查 `RetreatStepDistance` 是否过大、`RetreatSampleRadius` 是否过小；在 Scene 视图确认三个候选附近存在同一 Agent Type 的 NavMesh；候选必须真正增加与玩家的水平距离并且拥有 `PathComplete`。

#### Profiler

- CPU Timeline 中观察 Navigation/Pathfinding 相关 Sample，确认多个 Enemy 的请求没有持续集中在同一帧。
- Memory/GC 模块检查寻路稳定运行时 `GC Alloc = 0B`。三个候选数组、`NavMeshPath`、Failure Tracker 都只在 Awake 构造一次。
- 若敌人数增长后 CPU 偏高，先增大 `PathRefreshInterval`，再用证据决定是否降低 Obstacle Avoidance Quality；不要先删除避障或退回逐帧直线移动。

### 13. 面试复盘表达

可以这样概括：我在现有手写 FSM 和 `CharacterController` 架构上接入了 Unity AI Navigation。为了避免 `NavMeshAgent` 与 `CharacterController` 同时写 Transform，我关闭了 Agent 的自动位置和旋转更新，让它只负责 Pathfinding、Steering 和 Local Avoidance，最终 `desiredVelocity` 仍由 `CharacterController.Move` 执行，再同步 `nextPosition`。路径请求通过时间与目标位移阈值限频，并按 Enemy 错峰；Path Pending 或一次刷新失败时尽量沿旧路径继续走。索敌只由 `DetectRadius/LoseRadius` 决定，导航失败只触发持续 Repath，不会让 Enemy 放弃玩家。攻击资格使用完整路径距离与直线距离的几何关系，避免在仍绕墙时提前站定。移动朝向跟随实际 Steering，攻击前才朝玩家。远程后撤会生成三个可达候选并用完整路径筛选。纯方向、阈值、失败计时和目标高度回退从 Unity Adapter 中拆出，并由 17 个导航逻辑用例和 3 个资源 Contract 用例覆盖边界；真实 Play Mode 与 Profiler 作为后续集成证据。

## Task 2：定位并修复“有时正确寻路、有时原地不动”

本 Task 处理真实 Enemy Encounter 暴露出的间歇性问题：同一套 Enemy 代码有时能正确绕障碍追击，有时锁定玩家后却站在原地。排查沿 Runtime 日志、目标投影、资源序列化三层逐步缩小范围。资源层的七个导航字段已经补齐，运行时仍出现“目标附近没有可采样 NavMesh”，所以这次根因不是单纯的资产零值，而是目标点的垂直坐标不适合直接拿来做 `NavMesh.SamplePosition`。

### 1. 症状、证据与根因链

完整诊断链如下：

```text
  Enemy 已锁定玩家但不移动
    -> 真实 Enemy 区域 Console：目标附近没有可采样 NavMesh
    -> 记录到目标=(-0.11, 2.17, 24.64)，Enemy=(-0.23, 0.08, 24.48)
    -> 目标与 Enemy 脚下 NavMesh 的垂直差约 2.09m，大于配置采样半径 2m
    -> 直接以玩家 Transform 做 SamplePosition 失败，Enemy 没有第一条有效路径
    -> 运行时把玩家跳跃/中心根节点的 Y 当成地面查询高度，表现为原地不动
```

同一轮检查还确认旧资产缺少七个导航字段，已通过资源写回补齐；其中 `PathRefreshInterval = 0` 会让目标刷新条件几乎每帧都成立，增加 `SetDestination`/采样压力。它与高度问题属于两条独立边界，当前都已修复并由 Contract 测试保护。

### 2. 修复内容与为什么放在数据层

三个生产配置现在都显式保存以下推荐值：

| 字段 | 值 | Runtime 含义 |
|---|---:|---|
| `PathRefreshInterval` | `0.2s` | 限制常规路径刷新频率 |
| `DestinationMoveThreshold` | `0.5m` | 玩家移动足够远时允许提前刷新 |
| `DestinationSampleRadius` | `2m` | 把玩家根节点投影到附近可行走 NavMesh 的搜索半径 |
| `RetreatStepDistance` | `4m` | 远程 Enemy 单次后撤候选距离 |
| `RetreatSampleRadius` | `1.5m` | 后撤候选投影半径 |
| `StuckCheckInterval` | `0.5s` | 检查实际移动进展的周期 |
| `StuckProgressDistance` | `0.05m` | 一个周期内判定有进展的最小水平位移 |

修改的生产资产是：

- `Enemy_Sword_Definition.asset`
- `Enemy_Wizard_Definition.asset`
- `P7_EliteMagicEnemyDefinition.asset`

这些参数继续位于 `Game.Combat` 的 `EnemyDefinition`，因为它们属于每种 Enemy 的数据驱动行为调参；`Game.Character` 的 Motor 只读取并执行，不把某一种 Enemy 的推荐数值硬编码进去。直接把 Motor 的最小值从 `0.05m` 强制扩大到 `2m` 虽然能掩盖旧资产问题，却不能解决目标 Transform 的高度偏移，因此没有采用。

### 3. 目标高度回退：为什么保留 XZ、只替换 Y

`EnemyPerception` 的目标是玩家根节点。玩家跳跃、角色中心设计或视觉层级偏移都会让这个根节点暂时高于脚下地面；而 `NavMesh.SamplePosition` 的查询球体必须覆盖可行走表面。障碍附近还存在第二个边界：按原始高度查询可能先采到障碍顶部或断开的高层 `NavMesh`，采样本身成功但最终路径只有 `PathPartial`。Motor 现在按以下优先级查询和筛选：

1. 先把玩家目标保留 XZ、投影到 Enemy 当前导航平面，并直接用 `CalculatePath` 检查玩家 XZ 的真实拓扑；完整路径可以直接采用，`PathPartial` 也会保留为可继续逼近的候选。
2. 再按“同层投影小半径 → 玩家原始高度小半径 → 同层投影扩大半径”生成固定三个采样查询。数组在 Motor 构造时一次分配，热路径不产生 `GC Alloc`。
3. 每个采样点不能只看 `SamplePosition` 是否成功，还要同步执行 `CalculatePath`。候选排序先比较路径质量：`PathComplete` 优先于 `PathPartial`；质量相同时，再选择水平端点最接近玩家真实 XZ 的候选。这样障碍顶部的断开小岛即使先被采到，也不能抢走同层完整绕行路径。
4. 扩大半径仍取 `max(4m, 配置半径×2, Agent 半径×4)`，只作为最后回退；所有候选都不可用时保留旧路径并持续 Repath，不会清除目标或返回 Idle。

这条回退只改变“查询点”，不把 Enemy 直接传送到玩家脚下，也不绕过墙体；`SetDestination` 仍必须接受采样到的 NavMesh 点，`PathPartial` 仍会沿边界持续 Repath。纯函数 `EnemyNavigationMath.ProjectToNavigationPlane` 用 EditMode 用例验证了“保留 XZ、使用 Agent 高度”的契约；扩大半径只是 Unity Adapter 的最后一次受控查询，不改变攻击路径几何判定。

### 4. 回归保护与验证状态

新增 `EnemyNavigationAssetContractTests.cs`，通过 `UnityEditor.AssetDatabase` 逐个加载三个生产 `EnemyDefinition`，要求七个导航字段全部大于零。它验证的是“生产数据是否真正序列化”，与原有纯逻辑测试关注的算法边界互补；纯逻辑侧除 `ProjectToNavigationPlane` 外，又增加了三个候选筛选回归用例：导航平面查询必须排在原始 Transform 高度之前、完整路径必须优先于更近的 Partial Path、路径质量相同时必须选择更接近目标 XZ 的端点。

本次观察到的验证证据：

- RED：修复前的一次性资源 Contract 检查列出 `3 × 7 = 21` 个缺失字段并以失败退出。
- GREEN：补齐资产后，同一检查输出 `PASS: 3 EnemyDefinition assets contain 7 positive navigation fields each`。
- 静态编译：`Game.Character.csproj` 和 `Game.Character.Tests.csproj` 均为 `0 warning / 0 error`。
- Unity 自动刷新后的编译日志没有 C# 编译错误或引用异常；在修复前的真实 Enemy Encounter 日志中保留了上述高度采样失败证据，修复后的 Play Mode 还需重新进入同一 Encounter 观察该警告是否消失。

仍待完成的集成证据：

- 当前 `P7_DemoRun` 场景存在用户尚未保存的修改，运行 Test Runner 会要求保存或丢弃场景；本次没有替用户处理这些改动。因此当前 23 个用例均已通过测试程序集编译，但新增集合尚未在 Unity Test Runner 中完整执行。
- 复测已进入真正包含敌人的区域：HUD 显示 `区域 1/4`、`剩余敌人: 2`，并捕获到两名 Enemy 的目标高度采样失败日志；之前把 Area 1 无敌人状态当成验证依据是不成立的。
- 目标高度回退已完成代码与静态编译验证；下一次 Play Mode 应在相同 Encounter 观察两名 Enemy 是否沿障碍绕行、是否仍有 `[EnemyNavigation]` 采样失败警告，并重点复测玩家跳跃/贴墙/站在 NavMesh 边缘时首次锁定。

### 5. 被排除或拒绝的修复

- **恢复直线追击作为 fallback**：会重新引入撞墙，并让错误 NavMesh 配置在开发期静默存在。
- **导航失败后清除目标或回 Idle**：与“一旦索敌就在 `LoseRadius` 内持续追击”的玩法目标冲突，也会削弱压迫感。
- **只增加重试次数**：目标高度仍高于查询球体时，重复同一个失败请求不能解决根因，还会增加 Pathfinding 压力。
- **只扩大 Motor 内部 Clamp**：能缓解旧资产，却破坏数据驱动边界，未来资源仍可能漏序列化其他导航字段。

### 6. Unity Editor 最小复测步骤

1. 先确认并保存你当前的 `P7_DemoRun` 场景改动。
2. 打开 Test Runner 的 EditMode，筛选 `EnemyNavigation`，运行全部 23 个测试。
3. 重置或重新进入一个包含 Enemy 的 Encounter。
4. 让玩家先站在正常蓝色 NavMesh 中间触发索敌，再跳跃、站到墙边或 NavMesh 边缘触发索敌；几种情况下 Enemy 都应持续提交路径。
5. 若仍不动，优先记录该 Enemy 的 `isOnNavMesh/pathPending/pathStatus/remainingDistance/desiredVelocity` 与 Console 中的 `[EnemyNavigation]` 警告，再判断是出生点、目标投影还是 Bake 连通性问题，不再用随机调参代替证据。

## Task 3：修复多 Enemy 在静态障碍入口的 Local Avoidance 死锁

本 Task 处理目标高度回退完成后仍可复现的第二类“有路径但原地不动”。实际场景中两名近战 Enemy 会并排停在木桩同一侧；两者已经索敌，最新运行也没有目标采样失败。Scene 视图的蓝色 `NavMesh` 明确在木桩 `MeshCollider` 周围形成了不可行走孔洞，说明静态障碍已参与 Bake，路径拓扑并没有把木桩当作地面。

### 1. 根因与旧恢复逻辑为什么无效

两个 Enemy Prefab 的 `NavMeshAgent.avoidancePriority` 都是 `50`。它们从相近位置追同一目标、选择同一个障碍拐点时，`Local Avoidance` 可能进入对称让行：双方都等待对方先走，最终 `desiredVelocity` 被压到零。旧的 `EnemyNavigationFailureTracker` 能发现“完整路径仍很远但实际位置没有进展”，但只把 `_forceRefresh` 设为 `true`；下一帧重新计算后仍是同一条合法路径和同一组对称避障输入，因此 Repath 会无限重复，不能产生新的决策。

### 2. 两层恢复机制

第一层在 `EnemyNavigationMotor` 构造时把统一优先级改为由稳定 `InstanceId` 映射到 `20..79`。`avoidancePriority` 数值越小代表越优先，这让相邻 Enemy 能确定谁先通过；映射在一次 `Awake` 后保持不变，所以不会出现每帧随机换边造成的抖动。

第二层只在以下条件同时成立时启动短时恢复：路径是 `PathComplete`、`remainingDistance` 表明仍应移动、一个进展检查周期内没有达到最小水平位移。恢复速度不朝玩家直线计算，而是朝 `NavMeshAgent.steeringTarget`——当前路径的下一个拐点——推进，最短持续 `0.35s`。因此它绕过的是暂时输出零速度的 `Local Avoidance`，仍服从已 Bake 的 NavMesh 拐角，不会退化成穿墙或撞墙追击。恢复结束后继续使用正常 `desiredVelocity`。

### 3. 修改文件、验证与剩余边界

- `EnemyNavigationMath.cs`：新增稳定避障优先级映射和零分配的卡住恢复速度计算。
- `EnemyNavigationMotor.cs`：在构造时分散优先级；Repath 信号出现时开启限时恢复，并以当前路径拐点作为恢复方向。
- `EnemyNavigationMathTests.cs`：新增“避障速度归零时仍向路径拐点前进”和“相邻 Enemy 不再得到相同优先级”两个回归用例。

验证遵循 RED → GREEN：测试先因两个目标 API 不存在而产生 `CS0117`；实现后纯规则实算得到恢复速度 `(3, 0, 4)`，相邻实例映射为 `61/62`，断言通过。完整 `Game.Character.csproj` 编译结果为 `0 warning / 0 error`。当前 `C:` 盘可用空间为 `0GB`，Unity 因无法写入用户临时目录持续弹出 `Moving file failed`；这会阻止本轮 Unity Test Runner 和 Play Mode 集成复测，但不属于导航代码错误。本轮没有删除用户文件，静态编译改用 `F:` 盘临时目录完成。

剩余 Play Mode 验收重点：让两名以上 Enemy 同时从木桩同侧追击玩家，确认最多短暂停顿约一个检查周期，之后至少高优先级 Enemy 先行，其他 Enemy 随后通过；单 Enemy 绕行、隔墙不攻击、退出 `LoseRadius` 才脱战的既有行为也要回归。

## Task 4：修复 `remainingDistance = Infinity` 导致的间歇性停步

本 Task 针对“路径已经计算完成、Steering 也有速度，但 Enemy 偶尔原地不动”的间歇性问题。一次真实 Play Mode 追击快照显示：`PathStatus=PathComplete`、`HasPath=True`、`DesiredVelocity` 非零，同时 `NavMeshAgent.remainingDistance` 暂时为 `Infinity`。Unity 在路径刚刷新、`nextPosition` 同步或拐角重算期间允许该字段暂时表示“距离未知”；它不等价于“已经到达”。旧实现使用 `!float.IsInfinity(remainingDistance)` 作为移动前置条件，因此会把这段有效 Steering 清零并返回 `Reached`，这正好解释了障碍绕行时的概率性停步。

### 修改与验证

- `EnemyNavigationMath.ShouldMoveAlongPath`：把“是否继续移动”提取为可测试纯规则。`remainingDistance` 为正无穷时保留移动意图，即使 Local Avoidance 本帧暂时给出零速度，也交给卡住检测继续观察；`NaN`、负无穷仍视为无效；有限距离继续使用停止距离和到达容差判断。
- `EnemyNavigationMotor.NavigateToInternal`：统一调用上述规则，不再把正无穷距离直接判定为 `Reached`。已有路径且 Agent 正在提供 Steering 时，CharacterController 会继续沿 NavMesh 拐点推进。
- `EnemyNavigationMathTests.ShouldMoveAlongPath_WhenDistanceIsUnknownButAgentStillSteers_ReturnsTrue`：新增回归用例，防止再次把“距离未知”误当成“已到达”。

验证证据：Unity 重新编译后 `Game.Character.dll` 与 `Game.Character.Tests.dll` 均成功生成，日志出现 `Tundra build success`，且当前代码中已移除一次性 `[EnemyNavigationProbe]` 探针。此前的 `CS0117` 是 RED 阶段测试先引用新 API 而生产方法尚未加入的暂态编译错误；方法补齐后该错误不再出现在最新编译结果中。
