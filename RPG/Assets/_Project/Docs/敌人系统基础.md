# 敌人系统（Enemy System）设计解析文档

> **这份文档的定位**：把这次"近战敌人 MVP"的完整开发过程，按**开发顺序**拆成一个个小功能讲给你（战斗设计新手）听。每个小功能按六个维度展开：① 需求拆解 ② 基础知识与原理 ③ 业界常见实现方案 ④ 用到什么技术(含 Unity 组件) ⑤ 架构设计与拆解 ⑥ 踩坑与 Debug。最后用"面试者"口吻完整复盘一遍开发思路。
> 配套阅读：开工前的扫盲文档 `Enemy_System_Primer.md`（讲"敌人系统该做什么、为什么这么选"），本文是"实际怎么做出来的"。

---

## 〇、先看全貌：这次到底加了/改了哪些脚本

新手最容易迷失在"一堆文件"里，所以先给你一张**文件地图**。这次一共 **新增 9 个脚本、修改 2 个脚本、复用 6 个既有系统（零改动）**。

### 新增的脚本（9 个）

| 脚本 | 所在程序集/文件夹 | 职责（一句话） |
|------|------------------|----------------|
| `EnemyDefinition.cs` | `Game.Combat` / Combat/Definitions | 数据驱动的敌人数值（移动/感知/攻击/受击参数）的 ScriptableObject |
| `EnemyStateBase.cs` | `Game.Character` / Character/Enemy | 敌人所有状态的抽象基类（Enter/Update/Exit） |
| `EnemyStateMachine.cs` | `Game.Character` / Character/Enemy | 敌人状态机调度员（Exit→换引用→Enter） |
| `EnemyPerception.cs` | `Game.Character` / Character/Enemy | 感知：判断玩家是否在范围内（距离+滞回） |
| `EnemyController.cs` | `Game.Character` / Character/Controllers | 总装+行动层：取组件、驱动感知与状态机、暴露移动/转向/攻击能力 |
| `EnemyIdleState.cs` | `Game.Character` / Character/Enemy/States | 待机状态 |
| `EnemyChaseState.cs` | `Game.Character` / Character/Enemy/States | 追击状态 |
| `EnemyAttackState.cs` | `Game.Character` / Character/Enemy/States | 近战攻击状态（前摇+命中窗口） |
| `EnemyHurtState.cs` | `Game.Character` / Character/Enemy/States | 受击硬直状态 |

### 修改的脚本（2 个）

| 脚本 | 改了什么 | 为什么 |
|------|---------|--------|
| `PlayerControllerBase.cs` | 新增静态属性 `Current`，OnEnable 注册、OnDisable 注销 | 让敌人零查找拿到当前玩家 |
| `MeleeHitDetector.cs` | 命中循环里新增"跳过攻击者自身层级" | 防御性修复：命中体绝不打到自己身体（见踩坑 B） |

### 复用的既有系统（零代码改动）

| 系统 | 敌人怎么用它 |
|------|-------------|
| `HealthComponent` / `IDamageable` | 敌人挂上就能挨打、扣血、发受击/死亡事件 |
| `DamagePipeline` | 敌我伤害走同一条纯函数结算 |
| `CharacterCombatFeedback` | 敌人闪红、死亡动画、流血复用它 |
| `MeleeHitDetector` | 敌人近战命中判定复用它（只是改了防御性逻辑） |
| `AttackDefinition` | 敌人的"一次挥击"数据就是它 |
| `EventBus<T>` / `GameLog` | 跨系统通信与日志 |

> **第一个要建立的认知**：敌人系统真正"新写"的代码集中在 **感知 + 决策(状态机) + 行动(控制器)**；而"战斗"部分（受击/扣血/死亡）几乎全是**复用**。这正是当初把伤害系统设计成"敌我对称"的回报。

---

## 一、全局心智模型：敌人 = "把读玩家输入换成读对玩家的感知"的另一个角色

在拆解之前，先装进脑子一个模型——所有游戏 AI 的通用骨架 **Sense → Think → Act（感知—思考—行动）**：

```
   感知(Sense)            决策(Think)              行动(Act)
   "我看得到玩家吗?"  ──►  "现在我该干什么?"  ──►   "执行:移动/攻击/受击"
   EnemyPerception         EnemyStateMachine        EnemyController 的能力
                           + 各状态                  (复用伤害管线)
```

关键洞察：**你的玩家其实也是这个循环**——读输入(Sense)、CheckTransition(Think)、Move/攻击(Act)。敌人只是把"读玩家输入"换成"读对玩家的感知"。所以敌人状态机和玩家状态机长得几乎一样——这是我刻意让两者**架构同构**，你学会一个就懂另一个。

整条开发主线（也是本文的小功能顺序）：

```
数据(EnemyDefinition) → 玩家入口(Current) → FSM骨架+控制器 → 感知 →
待机/追击 → 近战攻击(前摇+命中窗口) → 受击/死亡 → 预制体组装
                                  ↑                    ↑
                              踩坑A:攻击卡死        踩坑B:敌人自残
```

---

## 二、小功能拆解（按开发顺序）

---

### 小功能 1：数据驱动的敌人定义 `EnemyDefinition`

#### 1. 需求拆解
敌人的血量、移速、侦测半径、攻击距离、攻击冷却、受击硬直时长……这些数字不能写死在代码里。我要做不同的怪（快的、慢的、感知远的）时，应该是**新建一份数据资产、改几个数字**，而不是改代码。

#### 2. 基础知识与原理
- **数据与逻辑分离**：游戏开发的核心原则之一。逻辑（怎么追、怎么打）写在脚本里只写一次；数值（追多快、打多疼）放在数据资产里，每个怪一份。
- **ScriptableObject（SO）**：Unity 提供的"数据容器资产"。它不是挂在场景物体上的组件，而是存在 `Project` 里的一个 `.asset` 文件，可以被多个对象引用。非常适合放这种"配置数据"。

#### 3. 业界常见实现方案
- **硬编码**：数值写在 MonoBehaviour 字段里。最差——做新怪要复制脚本、改代码。
- **ScriptableObject（本项目选）**：数值放 SO，策划/你自己在 Inspector 调，零代码。Unity 项目主流。
- **外部表格（CSV/JSON/Excel 导表）**：大型项目用，几百个怪时表格比逐个 SO 高效，但需要一套导表工具链。MVP 阶段杀鸡用牛刀。

#### 4. 用到什么技术
- `ScriptableObject`、`[CreateAssetMenu]`（让你能在右键 Create 菜单里创建该资产）、`[Header]`/`[Tooltip]`（Inspector 分组与提示）。

#### 5. 架构设计与拆解
`EnemyDefinition` 放在 `Game.Combat`，因为它只引用同模块的 `AttackDefinition`（敌人的"一次挥击"数据），不引用任何角色/动画类——保持"纯数据"。字段分四组：移动(`MoveSpeed`)、感知(`DetectRadius`/`LoseRadius`)、攻击(`AttackRange`/`AttackCooldown`/`Attack`)、受击(`HurtDuration`/`HurtStateName`)，外加 `CrossFadeDuration`。
注意一个刻意的取舍：**血量和阵营不放在这个 SO 里**，而是留在 `HealthComponent` 上（它本来就序列化了 `maxHp`/`teamId`）。避免同一个数据有两个来源(single source of truth)。

#### 6. 踩坑与 Debug
你当时问了一个好问题：**"项目里攻击都用 `ComboDefinition`，为什么敌人用 `AttackDefinition`？"** 这正好点出二者的层级关系：
- `AttackDefinition` = **一次攻击**的原子数据（伤害/命中盒/命中窗口/动画名）。
- `ComboDefinition` = 把多个 `AttackDefinition` 按顺序串起来的**连段链** + 连段冷却。

敌人 MVP 只有**单段挥击**，没有连段推进，所以用原子的 `AttackDefinition` 正合适；用 `ComboDefinition` 等于套一个只有 1 个元素的壳，还得拖上 `ComboResolver` 那套连段逻辑——属于过度设计。而且 `MeleeHitDetector` 本来吃的就是 `AttackDefinition`，敌人直接喂它最顺。**结论：原子攻击=`AttackDefinition`，连招=`ComboDefinition`，按粒度选对即可。**

---

### 小功能 2：玩家静态注册表 `PlayerControllerBase.Current`

#### 1. 需求拆解
敌人每帧都要知道"玩家在哪"才能算距离、追击。问题是：敌人怎么拿到玩家这个对象的引用？

#### 2. 基础知识与原理
- 最朴素的做法 `FindObjectOfType<Player>()` 每帧调用——**很慢且产生垃圾(GC)**，因为它要遍历场景所有对象。每帧、每个敌人都这么干，性能会崩。
- **注册表/服务定位(Service Locator)模式**：让"被找的人"主动把自己登记到一个全局可访问的入口；"找人的人"直接读这个入口。一次登记，处处零成本读取。

#### 3. 业界常见实现方案
- **静态属性/单例注册（本项目选）**：玩家启用时把 `this` 写进一个静态字段，敌人直接读。单人游戏最简洁。
- **Tag 查找并缓存**：敌人 Awake 时 `FindWithTag("Player")` 一次并存起来。不用改玩家代码，但依赖 Tag 配置、多玩家/重生场景不好处理。
- **事件广播玩家位置**：玩家发位置事件、敌人订阅。最解耦，但对"需要实时精确距离"的感知来说绕且偏重。

#### 4. 用到什么技术
- C# `static` 属性（`public static PlayerControllerBase Current { get; private set; }`）、Unity 生命周期 `OnEnable`/`OnDisable`。

#### 5. 架构设计与拆解
我在 `PlayerControllerBase`（所有玩家职业的抽象基类）上加了一个静态 `Current`：`OnEnable` 时 `Current = this`，`OnDisable` 时 `if (Current == this) Current = null`。注意那个 `if` 守卫：**只有当离场的玩家恰好是当前登记者时才清空**，避免"一个被禁用的旧玩家把当前活跃玩家误清掉"。因为敌人(`Game.Character`)和玩家在同一程序集，敌人能直接读 `PlayerControllerBase.Current`，零依赖额外系统。

#### 6. 踩坑与 Debug
本身没踩坑。但这是一个"埋点"：它纯内部、不改变任何可见行为，所以验证时只能确认"编译通过 + 玩家照常游玩"，真正用到它要等下一个感知功能。**新手要习惯这种"为后续铺路、本步无可见效果"的任务**。

---

### 小功能 3：敌人状态机骨架 + 控制器核心（决策层与行动层分离）

#### 1. 需求拆解
敌人要在"待机/追击/攻击/受击"等状态间切换。我需要一套能"持有当前状态、切换状态、每帧驱动状态"的机制，以及一个挂在敌人物体上、统管这一切的总控脚本。

#### 2. 基础知识与原理
- **有限状态机(FSM)**：把"敌人此刻该按什么规则行动"拆成有限个状态，每个状态管自己的逻辑和"什么条件切到别的状态"。
- **决策层 vs 行动层分离**：这是整个敌人架构的灵魂。**状态(决策层)只决定"做什么"，怎么做(移动/转向/出招)实现在控制器(行动层)里，由状态调用**。好处：将来想把 FSM 换成更高级的"行为树"，只需替换决策层，行动能力一行不动。

#### 3. 业界常见实现方案
- **手写 FSM（本项目选）**：状态少时最简单、最直观、和玩家风格统一。
- **分层状态机(HFSM)**：状态多时用父状态分组，缓解连线爆炸。
- **行为树(Behavior Tree)**：复杂敌人/Boss 主流，可复用子树，但概念门槛高、MVP 用它是杀鸡用牛刀。
- **效用系统/GOAP**：让 AI 自己"算分"或"规划"，很聪明但对当前阶段是严重过度设计。

#### 4. 用到什么技术
- 普通 C# 类（状态、状态机都**不是** MonoBehaviour，性能更可控）；`MonoBehaviour`（`EnemyController`）；`[RequireComponent]`（强制依赖 `CharacterController`+`HealthComponent`）；`CharacterController.Move`；`Animator.SetFloat`（同步 speed 参数驱动 Idle↔Run）。

#### 5. 架构设计与拆解
四个文件一次性建立（它们互相引用，必须同时存在才能编译）：
- `EnemyStateBase`：抽象基类，构造时传入具体 `EnemyController` 存为 `_enemy`，定义 `Enter/Update/Exit`。
- `EnemyStateMachine`：`ChangeState` = 旧状态 `Exit()` → 换引用 → 新状态 `Enter()`；`Update()` 驱动当前状态。**和玩家的 `PlayerStateMachine` 结构一模一样**。
- `EnemyController`：`Awake` 取组件、预实例化感知与状态机（**预实例化=零运行时 GC**）；`Update` 跑"感知→状态机→同步动画"；并对状态暴露**行动能力**：`MoveTo(目标位置)`（朝目标水平移动+重力）、`StayGrounded()`（原地只贴地）、`FaceTarget()`（只 yaw 平滑转向）。
- 重力与贴地：用一个 `_verticalVelocity`，接地时给 -2f、离地累加重力，每次 `Move` 带上。和玩家同套路。

为什么状态用普通 C# 类而不是 MonoBehaviour？因为它们是"逻辑"，不需要 Unity 的生命周期/序列化；用普通类可以在 `Awake` 一次性 new 好、运行时只切引用，**切状态零分配**。

#### 6. 踩坑与 Debug
此步的关键约束是 **"Unity 按整程序集编译"**——`Game.Character` 里任一文件引用了还没创建的类型，整个程序集都编译不过。所以我刻意让这四个文件**同一批创建**（一个开发任务），中间不留半成品。这一步交付物是"敌人能感知到玩家并打印日志"，本身还不会动（状态机里还没有任何状态）。

---

### 小功能 4：感知 `EnemyPerception`（距离 + 滞回）

#### 1. 需求拆解
判断玩家是否进入战斗范围：进来了就追，离远了就脱战。还要避免玩家站在边界上时敌人"进战/脱战"疯狂抖动。

#### 2. 基础知识与原理
- **距离检测**：`(玩家位置 - 我位置)` 的长度和半径比。
- **`sqrMagnitude` 优化**：比较距离时用"平方距离"对"平方半径"，省掉一次开方(`sqrt`)。只在真正需要距离数值时才开方。每帧每怪都跑的逻辑，这种小优化值得。
- **滞回(Hysteresis)**：进入和退出用**不同的阈值**——进战用较小的 `DetectRadius`，脱战用较大的 `LoseRadius`。这样在边界附近不会反复横跳。这是工程里经典的防抖手段（温控、施密特触发器都是这个思想）。

#### 3. 业界常见实现方案
- **纯距离(本项目 MVP)**：360° 球形侦测，最简单。
- **视野锥(FOV)**：用点乘判断玩家是否在敌人正前方一定角度内（背后看不见）。
- **视线遮挡(Line of Sight)**：再加一条 Raycast，中间有墙就看不到，避免"隔墙发现你"。
- **听觉/仇恨表**：更拟真的多通道感知。

#### 4. 用到什么技术
- `Vector3` 运算、`sqrMagnitude`、`Mathf.Sqrt`、`PlayerControllerBase.Current`（小功能 2 的成果）、`GameLog.Info`（打印进战/脱战）。

#### 5. 架构设计与拆解
`EnemyPerception` 是普通 C# 类，构造时拿 `EnemyController`。`Tick()` 每帧：从 `Current` 拿玩家 → 算平方距离 → **根据当前是否已锁定目标选择半径**（`HasTarget ? LoseRadius : DetectRadius`，这就是滞回）→ 更新 `HasTarget`/`Target`/`DistanceToTarget`。它只"感知并暴露结果"，不做任何决策——决策是状态的事。

#### 6. 踩坑与 Debug
评审时发现一个**隐患（提前规避）**：`DistanceToTarget` 只在"锁定成功"的分支更新，丢失目标时它保留旧值。所以约定：**所有读 `DistanceToTarget` 的地方必须先确认 `HasTarget`**。追击状态正是这么写的（先判 `HasTarget` 再读距离），所以没出 bug——这属于"在评审阶段就把潜在坑标注出来、让后续代码绕开"。

---

### 小功能 5：待机 + 追击（敌人会走向你并在攻击距离停下）

#### 1. 需求拆解
敌人默认待机；感知到玩家就走过去；走到攻击距离就停下、面向玩家；玩家跑远了回待机。

#### 2. 基础知识与原理
- **状态转换即优先级**：在每个状态的 `Update` 里检查"该不该切到别的状态"。
- **转向(steering)**：朝目标方向移动；只转 yaw（左右），不让身体俯仰前倾。

#### 3. 业界常见实现方案
- **简单朝向移动(本项目 MVP)**：直接朝玩家走，不绕障碍。空旷场景够用。
- **NavMesh + NavMeshAgent**：Unity 内置寻路，能绕开障碍走最优路，业界标准。但它自带一套位移/旋转，和玩家用的 `CharacterController` 是两套系统，需要协调。
- **NavMesh 算路径 + 自己用 CharacterController 驱动**：两者优点结合，实现最复杂。

我选简单朝向移动起步，理由：**和玩家位移系统统一(都用 CharacterController)、手感/碰撞一致、无需烘焙 NavMesh，最快打通追击闭环**。等场景有障碍了再升级 NavMesh。

#### 4. 用到什么技术
- `EnemyController.MoveTo/StayGrounded/FaceTarget`、`EnemyPerception`、`Animator` 的 `speed` 参数（驱动 Idle↔Run 混合动画）。

#### 5. 架构设计与拆解
- `EnemyIdleState`：原地 `StayGrounded`；`HasTarget` → 切 `ChaseState`。
- `EnemyChaseState`：`!HasTarget` → 回 `IdleState`；否则 `FaceTarget`；在攻击距离内 → `StayGrounded`（停下）；否则 `MoveTo`（继续追）。
- `EnemyController` 增加 `IdleState`/`ChaseState` 实例与属性，并在 **`Start()`**（而非 Awake）里设初始状态为 Idle——`Start` 保证所有对象的 `Awake` 都已执行完，引用都就绪。

#### 6. 踩坑与 Debug
本步刻意做成"到攻击距离**只停下**、不出招"——把攻击留到下一个功能。这样每一步都可独立在 Editor 验证（敌人会追、会停），不会一次性堆太多没验证的逻辑。

---

### 小功能 6：近战攻击（前摇 telegraph + 命中窗口）⭐ 含一个重要 Debug

#### 1. 需求拆解
敌人到攻击距离、冷却就绪时出招：先有"前摇"给玩家反应机会，然后在某个时间段真正造成伤害，打完进入冷却。

#### 2. 基础知识与原理
- **前摇/命中/后摇 三段式**：一次攻击在时间轴上分三段。**前摇(startup)就是给玩家的"预警(telegraph)"**——敌人举起武器到真正打中之间的时间，玩家据此闪避/格挡。如果攻击"瞬发"（一进范围就掉血），体验极差。
- **命中窗口(active window)**：只有这一小段时间内武器才真正能打到人。
- **动画驱动时序**：用动画的归一化进度 `normalizedTime`（0~1）去对照数据里配的窗口（`HitActiveStart`/`HitActiveEnd`），到点开/关命中判定。
- **冷却(cooldown)**：两次攻击的最小间隔，让射速与"动画多长"解耦、可独立调。

#### 3. 业界常见实现方案
- **Animation Event**：在动画时间轴上打标记回调函数。所见即所得，但时机散落进动画文件、跨角色不一致、不好测。
- **normalizedTime 阈值(本项目选)**：所有战斗时机统一"读进度比阈值"，集中在 SO 里、可单元测试。与玩家攻击一套范式。
- **命中判定方式**：近战常用 `OverlapBox`（在武器位置扫一个盒子看碰到谁）；本项目敌人**复用** `MeleeHitDetector`（就是 OverlapBox + 阵营过滤 + per-swing 去重）。

#### 4. 用到什么技术
- `Animator.CrossFadeInFixedTime`（代码点名播放攻击动画）、`GetCurrentAnimatorStateInfo(0).normalizedTime`、`Animator.IsInTransition`、`shortNameHash`（校验确实在攻击态）、`MeleeHitDetector.OpenHitWindow/CloseHitWindow`、`AttackDefinition`（命中盒/窗口/伤害/动画名）。

#### 5. 架构设计与拆解
- `EnemyAttackState.Enter`：对准目标 + `CrossFade` 攻击动画。
- `Update`：`StayGrounded`（出招原地不动）；读 `normalizedTime`，到 `HitActiveStart` 开命中窗口、到 `HitActiveEnd` 关；动画播完结束。
- `Exit`：**关窗 + 启动冷却**。放在 `Exit` 是因为它是"离开攻击态的唯一必经出口"——即使攻击被受击/死亡打断，冷却也一定会启动、窗口也一定会关。
- `EnemyController` 增加：`AttackState`、`AttackCooldownCounter`（在 Update 顶部每帧递减）、`OpenAttackWindow/CloseAttackWindow`（转调 `MeleeHitDetector`）、`CrossFade` 帮助方法、攻击动画名预 hash。
- `EnemyChaseState` 改：在攻击距离内且冷却就绪 → 切 `AttackState`；冷却中 → 停下等待。

#### 6. 踩坑与 Debug ⭐（攻击后**卡死**）
**现象**：敌人走到面前，打了一下就不打了，也不再追我——彻底冻住。
**排查（系统化调试）**：
- "打了一下"=攻击动画确实播了、命中窗口确实开过（说明动画名匹配、CrossFade 成功）。
- "之后冻住"=状态机**卡在 `EnemyAttackState`** 出不来（它的 Update 只剩 `StayGrounded`，永远不回 Chase）。
**根因**：原代码只在"仍处于攻击动画态 + `normalizedTime≥0.9`"时才结束。但 Unity 里用 CrossFade 进入的动画态**必须配一条"Has Exit Time"退出连线**（否则会定格）。这条退出连线会在代码采样到 0.9 **之前**就把动画切走：切走过程中 `IsInTransition` 为真 → 提前 return；切到 Idle 后 `shortNameHash` 不再等于攻击态 → 每帧提前 return → **结束判定那行永远执行不到 → 永久卡死**。本质是"**代码想抓 0.9，却和动画自己的退出连线抢拍并抢输了**"。
**修复**：把结束判定改成"**一旦进入过攻击态、之后又离开了攻击态，就结束**"——与退出连线**协作**而非抢拍。同时保留 `EndThreshold` 作为"动画循环/无退出连线"时的兜底，再加一个 `MaxStateTime` 超时兜底（且**只在"从未进入过攻击态"时生效**，避免误伤超过该时长的长动画）——这样即使动画名配错(CrossFade 静默失败、永远进不去攻击态)，敌人也不会永久冻住，而是超时结束并打告警。
**新手教训**：**凡是用代码 CrossFade 进入、又靠 Animator 退出连线离开的状态，逻辑结束条件不要去"抓某个精确进度"，而要"检测自己是否已经离开了那个状态"。**

---

### 小功能 7：受击硬直 + 死亡（事件驱动，复用既有反馈）

#### 1. 需求拆解
敌人被打中要"踉跄"（短暂硬直、打断当前动作、播受击动画），恢复后继续追打；血量归零要播死亡动画、停止 AI、延时消失。

#### 2. 基础知识与原理
- **硬直/受击打断(hit-stun)**：被击中后短暂失控，是打击感的重要来源。
- **事件驱动解耦**：敌人不主动去问"我被打了吗"，而是**订阅**伤害系统发出的事件。伤害结算在哪发生不重要，只要发了 `DamageReceivedEvent`/`DeathEvent`，敌人就能响应。
- **攻防对称**：玩家打敌人和敌人打玩家走的是**同一条** `IDamageable.ReceiveHit → DamagePipeline` 管线，只是阵营相反。所以敌人挨打、扣血、发事件、死亡，**全是复用**。

#### 3. 业界常见实现方案
- **死亡/受击做成 FSM 状态**（本项目受击这么做）：硬直由状态控制时长，能和 AI 节奏协调。
- **死亡走事件 + 禁用 AI**（本项目死亡这么做）：死亡动画与销毁交给通用的 `CharacterCombatFeedback`，AI 这边只置 `_dead` 停跑。最省。
- **统一受击系统**：更复杂项目会有专门的 hit-reaction 系统处理硬直方向、削韧、霸体等。

#### 4. 用到什么技术
- `EventBus<DamageReceivedEvent>` / `EventBus<DeathEvent>`（struct 事件、零装箱）、`OnEnable/OnDisable` 成对订阅退订（防泄漏）、`gameObject.GetInstanceID()`（按 `TargetId` 过滤"打的是不是我"）、`CrossFade`（受击动画）、计时器。

#### 5. 架构设计与拆解
- `EnemyHurtState`：`Enter` 置硬直计时 + CrossFade 受击动画；`Update` 定身 + 倒计时，到点回 Chase/Idle。
- `EnemyController` 增加：订阅两个事件；`OnDamageReceived`：若是自己(`TargetId==_id`)且未死、且**这一击没致死**(`RemainingHp>0`)→ 切 `HurtState`（致死那一击交给死亡处理，不进硬直）；`OnDeath`：置 `_dead`、关命中窗口（死亡动画+销毁交给 `CharacterCombatFeedback`）；`Update` 首行 `if (_dead) return;` 让死后停跑 AI。
- 一个优雅之处：受击切状态用 `ChangeState`，它会先调当前状态的 `Exit()`。所以**如果敌人正在出招时被打断，`EnemyAttackState.Exit` 会自动关命中窗口 + 启动冷却**——不需要额外写"被打断时记得关窗"。这就是"把收尾绑在唯一必经出口(Exit)"的红利。

#### 6. 踩坑与 Debug（动画"双重驱动"冲突）
连线时要避一个坑：**敌人的 `CharacterCombatFeedback` 上的 `Get Hit State Name` 必须留空**。因为受击动画现在由 `EnemyHurtState` 驱动（它 CrossFade 并控制硬直时长）；如果 `CharacterCombatFeedback` 也同时 CrossFade 一个受击动画，两处会打架。让 `HurtState` 独占受击动画，`CharacterCombatFeedback` 只负责闪红+流血+死亡。另外死亡动画名要填敌人实际 controller 的节点名（如 `Die_SingleTwoHandSword`），别留旧 controller 的 `Die_noWeapon`，否则静默不播。

---

### 小功能 8：预制体组装 + 阵营配置 ⭐ 含一个重要 Debug

#### 1. 需求拆解
把调好的敌人固化成预制体，摆几个，跑通完整战斗闭环。

#### 2. 基础知识与原理
- **预制体(Prefab)**：把一组组件+配置打包成可复用模板。
- **阵营(Team)过滤**：`MeleeHitDetector`/`Arrow` 用 `TeamId` 决定"谁是敌、谁是友"，跳过同阵营。
- **Layer 与命中遮罩**：物理查询(OverlapBox/Raycast/碰撞)用 LayerMask 决定能打到哪些层。敌人必须在玩家命中遮罩包含的层上。

#### 3. 用到什么技术
- Prefab、Layer/LayerMask、`HealthComponent.TeamId`、`MeleeHitDetector._attackerTeam`。

#### 4. 架构设计与拆解
敌人预制体组件清单：`CharacterController` + `HealthComponent`(敌方队号) + `Animator`(敌人 controller) + `CharacterCombatFeedback`(getHit 留空、die 填对、血特效) + 武器枢轴子物体 + `MeleeHitDetector`(队号=敌方、pivot、ownerRoot、hitMask=玩家层) + `EnemyController`(拖 Definition、拖 HitDetector)。

#### 5. 踩坑与 Debug ⭐（敌人"自残"、玩家不掉血）
**现象**：敌人正常出招，但每次攻击是**敌人自己掉血**、最后把自己打死了；玩家反而不掉血。
**排查**：
- 敌人 `HealthComponent.TeamId = 0`，玩家 = 1，而敌人 `MeleeHitDetector._attackerTeam = 1`（与敌人自己的队号 0 **不一致**）。
- `MeleeHitDetector` 靠"`target.TeamId == _attackerTeam` 则跳过"来过滤。敌人出招时 OverlapBox 会扫到**敌人自己的身体**：自己队号 0 ≠ `_attackerTeam` 1 → 不被当作同队 → **打到了自己**；而玩家队号 1 == `_attackerTeam` 1 → 被当作"友军" → **跳过、不打玩家**。
**根因**：违反了一条铁律——**`MeleeHitDetector._attackerTeam` 必须等于该角色自己的 `HealthComponent.TeamId`**。
**修复（两层）**：
- **配置修复（治本）**：把敌人 detector 的 `_attackerTeam` 改成与敌人 `HealthComponent.TeamId` 一致（本项目设为 0），玩家保持 1。于是敌人不再自残、能打玩家，玩家也能打敌人。
- **代码硬化（防御性，治未来）**：给 `MeleeHitDetector` 的命中循环加一条"**跳过攻击者自身层级**"（`IsChildOf(_ownerRoot)`）。这样无论阵营怎么配错，挥击都**绝不会打到自己身体**。这是"defense in depth(纵深防御)"——找到根因后，在另一层加一道保险，让同类错误不再发生。注意这个改动对**正确配置的玩家是无副作用的**（玩家的命中体本来就不会扫到自己的根层级目标），所以安全。
**新手教训**：阵营是近战命中最容易配错的一环；写组件时除了"按数据过滤"，最好再加一道"结构性过滤(不打自己)"做兜底。

---

## 三、面试讲解（模拟面试者，完整复盘开发思路与流程）

> 下面用第一人称、面试口吻，把整条思路串起来。

面试官您好，我来讲一下这个 RPG 里**近战敌人系统**的开发。在做之前我先给自己建立了一个判断：**敌人系统的难点其实不在"战斗"，而在"决策"**。因为这个项目的伤害链路我一开始就设计成了攻防对称——`HealthComponent` 实现 `IDamageable`，伤害走纯函数 `DamagePipeline`，命中后通过 `EventBus` 同帧广播 `DamageReceivedEvent`/`DeathEvent`。这套东西不区分挂在玩家还是敌人身上。所以敌人的"挨打、扣血、死亡、流血"几乎是零成本复用的，我真正要新写的是 **感知—决策—行动** 这个 AI 循环。这个判断决定了我整体的工作量分配和架构取向。

**架构上我做的第一个、也是最重要的决定，是决策层和行动层分离。** 我没有把"怎么追、怎么打"写进一个大 MonoBehaviour，而是仿照项目里玩家那套手写状态机，平行做了一套 `EnemyStateMachine` + `EnemyStateBase` + 具体状态(Idle/Chase/Attack/Hurt)。状态只决定"现在该做什么"，而"具体怎么移动、转向、开合命中窗口"实现在 `EnemyController` 上、由状态调用。这样做的回报是双重的：一是和玩家架构**同构**，团队里谁懂玩家就懂敌人；二是**给未来留了升级路**——如果哪天敌人复杂到需要行为树，我只需要替换决策层，行动能力一行都不用动。我选 FSM 而不是一上来就上行为树，是因为 MVP 敌人只有四五个状态，远没到 FSM 连线爆炸的程度，行为树在这个规模属于过度设计。

**数据上我坚持数据驱动**，把所有数值收进一个 `EnemyDefinition` 的 ScriptableObject，做新怪就是新建资产改数字，不碰代码。这里有个小细节体现了我的取舍意识：血量和阵营我没放进这个 SO，而是留在 `HealthComponent` 上，避免同一份数据有两个来源。还有面试官可能会问的——我用 `AttackDefinition` 而不是项目里常见的 `ComboDefinition` 配敌人攻击，是因为前者是"一次攻击"的原子单位、后者是"连段链"，敌人只有单段挥击，用原子粒度正合适，也正好是 `MeleeHitDetector` 直接消费的类型。

**性能上我全程守住零 GC 热路径**：状态和感知模块都在 `Awake` 预实例化、运行时只切引用；动画状态名一次性 `StringToHash` 缓存；感知用 `sqrMagnitude` 比较避免开方；事件是 struct、走泛型 EventBus 零装箱；玩家引用我用一个静态注册表 `PlayerControllerBase.Current` 让敌人零查找拿到，而不是每帧 `FindObjectOfType`。

**真正体现工程能力的是两个在联调阶段暴露、我系统化定位并修复的 bug。** 第一个是敌人攻击一次后**整个冻住**。我没有乱改，而是先收集证据：敌人"打了一下"说明动画播了、命中窗口开过；"之后冻住"说明状态机卡在攻击态出不来。顺着这个我定位到根因——我的结束判定依赖"在攻击动画态内采样到进度≥0.9"，但 CrossFade 进入的动画态必须配 Has-Exit-Time 退出连线，这条连线会在我采样到 0.9 之前就把动画切走，导致结束那行永远执行不到。本质是我的代码在和 Animator 自己的退出连线抢拍并抢输了。修复的思路是从"抢拍"改成"协作"：**不再去抓某个精确进度，而是检测"我是否已经离开了攻击态"**，离开了就结束；同时保留进度兜底和一个只在"从未进入过攻击态"时生效的超时兜底，这样连动画名配错都不会让敌人永久卡死。我把它沉淀成一条可复用经验：凡是代码 CrossFade 进入、Animator 连线离开的状态，结束条件要判"是否已离开"，而不是"抓某个进度"。

第二个 bug 更典型——敌人出招时**打的是自己、把自己打死了**，玩家反而不掉血。我排查发现是阵营配错了：敌人命中判定器的 `_attackerTeam` 和它自己 `HealthComponent` 的队号不一致，导致 OverlapBox 扫到自己身体时没被当成同队、于是自残；而玩家恰好和判定器同队、被当成友军跳过。根因是违反了"判定器队号必须等于自己角色队号"这条不变量。我做了两层修复：配置上把队号对齐（治本），**代码上再给 `MeleeHitDetector` 加一道"跳过攻击者自身层级"的结构性过滤**（治未来）——这样无论阵营怎么配错，挥击都绝不会打到自己。这就是纵深防御：找到根因后在另一层加保险，让同类错误不再复现；而且我确认这个改动对正确配置的玩家是无副作用的，所以敢动这个共享组件。

**最后说下我的协作流程。** 这个功能我是先写扫盲文档建立共识、再写设计、再写一份逐任务的实现计划，然后按计划一个任务一个任务推进，每个任务我都自己过一遍 spec 合规和代码质量，关键改动还做了独立评审，全部完成后还做了一次全分支终审确认可合并。因为引擎相关的编译、动画连线、Play 验证只能在 Editor 里人工做，所以我把每个任务都设计成"代码改完→在 Editor 里验证一个明确的可见结果→再做下一个"的节奏，绝不一次性堆一堆没验证的逻辑。这套节奏让两个 bug 都在它们各自的任务验证环节就被逮到，而不是堆到最后一起爆。

总结一下，我认为这个敌人系统体现的不是"写了多少 AI"，而是**复用既有系统的判断力、决策/行动分离带来的可扩展性、零 GC 的工程纪律，以及系统化定位并分层修复 bug 的能力**。下一步我会在它之上加攻防博弈（闪避 i-frame、弹反）和通用状态/CC 框架，把战斗从"单向输出"推向"双向对话"。

---

## 附：可复用的经验速记

- **敌人 = 感知 + 决策 + 行动**；战斗(受击/死亡)能复用就复用——攻防对称的伤害管线是最大红利。
- **决策层(状态)与行动层(控制器)分离**：换行为树只换决策层。
- **数据驱动**：数值进 SO，新怪=新资产；血量/阵营单一来源(HealthComponent)。
- **AttackDefinition=一次攻击(原子)，ComboDefinition=连段链**，按粒度选。
- **玩家引用用静态注册表**，别每帧 FindObjectOfType。
- **感知用 sqrMagnitude + 滞回**防边界抖动；读 DistanceToTarget 前先判 HasTarget。
- **收尾放 Exit()**：受击打断出招时自动关窗+冷却，无需额外处理。
- **CrossFade 进入、连线离开的状态**：结束判"是否已离开该态"，别抓精确进度（攻击卡死的教训）。
- **MeleeHitDetector._attackerTeam 必须 = 自己 HealthComponent.TeamId**；并加"跳过自身层级"做纵深防御（自残的教训）。
- **受击动画让 HurtState 独占**，CharacterCombatFeedback 的 getHit 名留空，避免双重 CrossFade。
- **逐任务"改完即在 Editor 验证一个可见结果"**，bug 才会在当步暴露而非最后爆炸。
