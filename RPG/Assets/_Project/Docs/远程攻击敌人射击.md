# 远程法师敌人（Ranged Mage Enemy）设计解析文档

> **这份文档的定位**：把"远程法师敌人"这个复杂功能，按**开发顺序**拆成一个个小功能讲给你（战斗设计新手）听。每个小功能按七个维度展开：① 需求拆解 ② 基础知识与原理 ③ 业界常见实现方案 ④ 用到什么技术(含 Unity 组件) ⑤ 架构设计与拆解(改/增了哪些脚本) ⑥ 踩坑与 Debug ⑦ 大厂面试 Q&A。最后用"面试者"口吻完整复盘一遍开发思路。
>
> **配套阅读**：`Enemy_System_Design.md`（近战敌人 MVP 的完整解析）是本文的前置。本文的核心命题是——**"我已经有一个近战敌人了，怎么用最小代价长出第二种、行为完全不同的敌人？"** 答案是一次面向对象的重构 + 一个多态接缝。

---

## 〇、先看全貌：这次到底加了 / 改了哪些脚本

新手最容易迷失在"一堆 diff"里，先给你一张**文件地图**。这次的关键不是"写了多少新代码"，而是"为了让两种敌人共存，先把旧代码重构干净"。

### 重构 / 改名（1 个，行为不变）

| 操作 | 文件 | 为什么 |
|------|------|--------|
| **抽出基类** | 新增 `EnemyControllerBase.cs`（abstract） | 把近战敌人里"和近战无关的通用部分"（组件、感知、状态机、共享态、行动能力、受击死亡）全部上移，给两种敌人共享 |
| **改名 + 留专属** | `EnemyController.cs` → `MeleeEnemyController.cs`（`git mv` .cs+.meta 保 GUID） | 旧的 `EnemyController` 瘦身成"只剩近战专属"（命中判定 + 追击/攻击态）的子类 |

### 新增的脚本（3 个）

| 脚本 | 所在程序集 / 文件夹 | 职责（一句话） |
|------|------------------|----------------|
| `EnemyControllerBase.cs` | `Game.Character` / Character/Controllers | 抽象基类：所有敌人共享的总装 + 行动层；留一个抽象接缝 `EngageState` |
| `RangedEnemyController.cs` | `Game.Character` / Character/Controllers | 远程子类：`EngageState => KiteState`；`SpawnFireball()` 复用玩家火球、注入敌方阵营 |
| `EnemyKiteState.cs` | `Game.Character` / Character/Enemy/States | 远程走位：远追 / 近退 / 中间站档输出 |
| `EnemyRangedAttackState.cs` | `Game.Character` / Character/Enemy/States | 远程施法：站定播施法动画、到点单发火球 |

> 说明：表里写了 4 行，其中 `EnemyControllerBase.cs` 既是"重构产物"也是"新增文件"，本质上一份。

### 修改的脚本（共享态随基类型升级 + 数据扩展）

| 脚本 | 改了什么 | 为什么 |
|------|---------|--------|
| `EnemyDefinition.cs` | 加 3 个远程字段：`RetreatDistance` / `ProjectileSpeed` / `ProjectilePrefab` | 远程行为也要数据驱动，不写死在代码里 |
| `EnemyStateBase.cs` | `_enemy` 字段类型 `EnemyController` → `EnemyControllerBase` | 状态要能服务于"任何敌人"，不绑死近战 |
| `EnemyPerception.cs` | ctor 形参类型同上升级 | 感知是通用的，谁都能用 |
| `EnemyIdleState.cs` / `EnemyHurtState.cs` | 转换目标由 `ChaseState` 改为 `EngageState` | 待机/受击恢复后进"战斗态"，但具体是追击还是走位由子类决定 |
| `EnemyChaseState.cs` / `EnemyAttackState.cs` | typed 到 `MeleeEnemyController` | 这两个是近战专属态，明确绑定近战子类 |

### 复用的既有系统（零代码改动）

| 系统 | 法师敌人怎么用它 |
|------|-----------------|
| `Fireball` / `ProjectileBase` | **直接复用玩家法师的火球预制**，靠 `Init(...)` 把"攻击者阵营"换成敌方，火球就只伤玩家、还自带爆炸 + 点燃 |
| `HealthComponent` / `CharacterCombatFeedback` | 法师挨打、扣血、闪红、流血、死亡，全部复用 |
| `EnemyPerception` / `EnemyStateMachine` / 待机/受击态 | 感知与状态机骨架，近战远程共用一套 |

**一句话总览**：这次真正的工作量是"**一次保行为不变的重构**"，新功能反而很薄——因为 80% 的能力在近战敌人时就建好了，远程敌人只是换了"战斗时怎么走位、怎么出手"。

---

## 一、核心心智模型：把"两种敌人的差异"收敛到一个点

新手做"第二种敌人"最常见的错误，是把近战敌人的脚本**整个复制一份**改成远程——于是你有了两份 90% 重复的代码，以后改一个 bug 要改两遍。

正确的思路只有一句话：

> **找出两种敌人"相同的部分"和"不同的部分"，把相同的抽到基类、不同的留一个"插槽"让子类填。**

近战和远程敌人，**相同**的有：取组件、感知玩家、状态机、待机、受击、死亡、移动/转向/重力这些"行动能力"。**不同**的只有一件事：

- 近战：进入战斗后要**贴上去**（Chase），到身边挥刀；
- 远程：进入战斗后要**保持距离**（Kite），站定放火球。

所以我设计了一个抽象接缝：

```csharp
// 基类只承诺"进入战斗后有一个走位状态"，但不说是哪个
public abstract EnemyStateBase EngageState { get; }
```

- 近战子类：`public override EnemyStateBase EngageState => _chaseState;`
- 远程子类：`public override EnemyStateBase EngageState => _kiteState;`

待机态发现玩家 → `ChangeState(EngageState)`；受击恢复 → 回 `EngageState`。**待机态/受击态完全不知道自己服务的是近战还是远程**，它们只认"战斗走位态"这个抽象。这就是面向对象的**开闭原则**（对扩展开放、对修改关闭）：将来加第三种敌人（比如召唤师），只需再写一个子类 + 一个走位状态，**一行旧代码都不用动**。

> 这个接缝和玩家端是**同构**的：玩家基类 `PlayerControllerBase` 也用 `virtual bool TryStartAttack()` 这个接缝，让战士/弓手/法师各自实现攻击。整个项目"基类持共享、子类填差异"的味道是一致的——这点在面试里是加分项，说明架构有**一致的心智模型**。

---

## 二、小功能拆解（按开发顺序）

开发顺序严格按依赖关系：先**重构出地基**（否则远程子类无处挂靠）→ 再**扩数据字段** → 再**写走位** → 最后**写施法发射**。

---

### 小功能 1：重构成 base + 子类（行为不变）

#### 1. 需求拆解
在不改变现有近战敌人任何行为的前提下，把 `EnemyController` 拆成：抽象基类 `EnemyControllerBase`（共享一切）+ `MeleeEnemyController`（近战专属）。交付标准是一句很硬的话：**重构前后，近战敌人在 Play 模式下的表现必须一模一样。**

#### 2. 基础知识与原理
- **重构（Refactoring）**：在不改变外部行为的前提下改善内部结构。它的价值不在"现在"，在"未来"——为即将到来的远程敌人腾出干净的扩展点。
- **继承与多态**：基类定义"做什么"的骨架，子类提供"具体怎么做"。
- **Unity 序列化的隐藏规则**：Unity 按**字段名**序列化，且这套机制**跨继承层级**——把 `[SerializeField] _definition` 从子类移到基类、**只要字段名不变**，预制体里已经拖好的引用就不会丢。这是重构能"无痛"的技术前提。
- **脚本 GUID 与 `.meta`**：Unity 给每个资源一个 GUID 存在同名 `.meta` 里，预制体/场景靠 GUID 引用脚本。**改名脚本必须让 `.meta` 跟着走**，GUID 才不变，引用才不断。

#### 3. 业界常见实现方案
- **复制粘贴改一改**（反面教材）：两份重复代码，维护地狱。新手最常踩。
- **base + subclass（本项目选）**：OOP 经典。适合"少数几种、差异明确"的敌人，可读性最好。
- **组件组合 / ECS**（Unity DOTS、《守望先锋》风格）：把"移动""感知""攻击"拆成独立组件，敌人 = 组件的拼装。极致灵活、利于海量单位，但小项目里是过度设计。
- **数据驱动单类 + 行为枚举**：一个 `EnemyController` 里用 `enum EnemyType { Melee, Ranged }` 分支。省类但 `if/switch` 会越长越脏，违反开闭原则。

#### 4. 用到什么技术
- C# `abstract class` / `abstract` 属性 / `protected virtual` 方法（模板方法模式：`Awake()` 基类做通用、子类 `override` 后先 `base.Awake()` 再补自己的）。
- `git mv`（同时移动 `.cs` 和 `.meta`）保 GUID。
- Unity `[SerializeField]` 跨继承层级的字段名序列化。
- `[RequireComponent(typeof(CharacterController))]` / `[RequireComponent(typeof(HealthComponent))]` 放在基类上——两种敌人都强制带这两个组件。

#### 5. 架构设计与拆解
- **上移到 `EnemyControllerBase`**：`_definition/_cc/_animator` 字段、感知与状态机、`_idleState/_hurtState`、动画 hash、事件订阅/退订、`OnDamageReceived/OnDeath` 的全部门控逻辑、`Update` 顺序（感知→状态机→同步 Animator）、行动能力 `MoveTo/MoveAway/StayGrounded/FaceTarget/CrossFade/ApplyGravity`、抽象 `EngageState`、空钩子 `protected virtual void OnDied()`。
- **留在 `MeleeEnemyController`**：`_hitDetector`、`_chaseState/_attackState`、`OpenAttackWindow/CloseAttackWindow`、`EngageState => _chaseState`、`OnDied()` 里关命中窗口、`Awake()` 里把攻击数据注入命中判定器。
- **顺手为远程预留**：基类同时加了 `MoveAway()`（后撤），近战用不到，但远程走位要——属于"在重构时一并把扩展点留好"，不是过度设计。
- **共享态升级**：`EnemyStateBase._enemy`、`EnemyPerception` 的类型升到 `EnemyControllerBase`；`EnemyIdleState/EnemyHurtState` 转 `EngageState`；`EnemyChaseState/EnemyAttackState` 绑定 `MeleeEnemyController`。

#### 6. 踩坑与 Debug
- **坑 A：手工创建了 `.meta`。** 实现时给新文件 `EnemyControllerBase.cs` 顺手生成了一个手写 GUID 的 `.meta`。这违反项目铁律"**`.meta` 一律由 Unity 生成**"——手写 GUID 有冲突风险、且可能格式不全。**修复**：删掉手写 `.meta`，让开发者打开 Unity 时自动生成。判断依据：`EnemyControllerBase` 是抽象类，没有任何预制体直接引用它的 GUID（预制体引用的是具体的 `MeleeEnemyController`，那份 GUID 已通过 `git mv` 保住），所以重新生成绝对安全。
- **坑 B：改名脚本时的 `.meta` 同步。** `git mv` 必须 `.cs` 和 `.meta` **两个文件都 move**。只 move `.cs`、删了重建 `.meta`，会让 GUID 变化→场景里近战敌人身上的脚本引用变成 "Missing (Mono Script)"、所有 Inspector 拖好的值清零。
- **验证手段**：因为 Unity 不能在我这边编译/运行，所以本任务的"测试"是**静态**的——全局搜索 `EnemyController` 这个裸 token，确认除了 `MeleeEnemyController`、`EnemyControllerBase`、注释和日志字符串外，没有任何遗留的旧类型引用（Unity 是**整程序集编译**，一个错名就让整个 `Game.Character` 编译失败）。最终由开发者在 Editor 里编译 + Play 回归确认"行为不变"。

#### 7. 大厂面试 Q&A
- **Q：你怎么保证一次重构"行为不变"？没有单元测试怎么办？**
  A：三层防护。第一，重构只做"结构搬运"不动逻辑——上移字段、改类型、加抽象点，每一处都能对照原代码逐行确认语义等价。第二，静态检查：全局搜旧类型名确认无残留，靠"整程序集编译失败"这一硬约束兜底类型一致性。第三，因为是 Unity 项目、表现层逻辑必须在引擎里跑，所以最终用**人工回归**——重构前录一遍近战敌人的完整行为，重构后再跑一遍逐项比对。如果是纯逻辑类（比如伤害结算 `DamagePipeline`），我会上 EditMode 单测。

- **Q：为什么用继承而不是组件组合 / ECS？**
  A：取决于规模和差异维度。我这里只有两种敌人、差异点高度集中（就"战斗时怎么走位"一个轴），用 base+subclass 可读性最好、心智负担最低，而且和项目里玩家端的 `PlayerControllerBase` 架构同构，团队认知一致。如果敌人种类爆炸、能力需要自由排列组合（飞行+召唤+护盾随意拼），或者要支持成百上千单位的性能，我才会转向组件组合 / DOTS——那时继承的"类爆炸"和组合爆炸问题才会真正出现。架构选型要服务当下规模，不为想象中的需求买单。

---

### 小功能 2：EnemyDefinition 远程数据字段

#### 1. 需求拆解
远程敌人的"后撤距离、火球速度、火球预制"必须可在 Inspector 调，不能写死在代码里——否则策划想把火球调快一点都要找程序改代码重编译。

#### 2. 基础知识与原理
- **数据驱动（Data-Driven Design）**：行为逻辑（代码）和数值参数（数据）分离。代码只读数据、不含魔法数字；改数值不碰代码、不重编译。
- **ScriptableObject**：Unity 的"数据容器资产"，是数据驱动的标准载体。一份 SO = 一种敌人的全部数值，可被多个敌人实例共享。

#### 3. 业界常见实现方案
- **ScriptableObject（本项目选）**：Unity 原生、可视化编辑、可引用其他资产（这里 `ProjectilePrefab` 直接拖预制）。
- **外部表格（CSV/JSON/Excel 导表）**：大型项目主流，策划在 Excel 配、导成表，适合海量数值与版本管理；但读取要写解析层、引用 prefab 不直观。
- **硬编码常量**：原型期偶尔用，本质是技术债。

#### 4. 用到什么技术
- 在既有 `EnemyDefinition : ScriptableObject` 里加三个 `public` 字段：`float RetreatDistance` / `float ProjectileSpeed` / `GameObject ProjectilePrefab`，配 `[Header]` / `[Tooltip]` 让 Inspector 分组、有说明。
- **模块隔离守恒**：`EnemyDefinition` 在 `Game.Combat`，新加的字段只用值类型 + `GameObject` 引用，**没有引入对 `Game.Character` 的反向依赖**——保持单向依赖不被破坏。

#### 5. 架构设计与拆解
就改一个文件：在 `[Header("CrossFade")]` 之前插入"远程"分组的三个字段。近战敌人的 SO 留空即可，互不影响（同一个类、不同实例，各填各的）。

#### 6. 踩坑与 Debug
- **设计约束（非 bug，但要写进 Tooltip 提醒策划）**：`RetreatDistance` 必须 **< `AttackRange`**，两者之间才是"站档输出区"。如果填反了，会出现"既想后撤又想接近"的抖动。代码侧对边界做了防御（见小功能 3），但合理数值仍靠 Tooltip 约束。

#### 7. 大厂面试 Q&A
- **Q：什么数据该放 SO，什么该走导表？**
  A：分水岭是"规模 + 协作方式 + 是否引用资产"。原型期、种类少、且要直接引用预制/材质这类 Unity 资产时，SO 最顺手——可视化、强类型、拖拽即引用。一旦数值条目上千、需要策划在 Excel 批量调平衡、要做 diff/版本对比，就该上导表，把 SO 退化成"运行时缓存"。我现在两三种敌人，SO 足够；但我会把"读取入口"收敛好，将来换导表只改加载层、不动消费方代码。

---

### 小功能 3：远程走位 Kite（远追 / 近退 / 中间站档）

#### 1. 需求拆解
法师不能像近战那样贴脸。它要维持一个"舒适射程"：玩家太远就靠近到能打、玩家贴脸就后撤拉开、距离正合适就站定输出。并且**始终面向玩家**（不然火球朝向、后撤方向都不对）。

#### 2. 基础知识与原理
- **Kiting（放风筝）**：远程单位边拉开距离边输出的经典战术，源自 RTS/ARPG。AI 版 kiting = 把"我和目标的距离"映射到"接近/后撤/站定"三种动作。
- **射程带（band）**：用两个半径把空间分成三段——`> AttackRange` 接近、`< RetreatDistance` 后撤、`[RetreatDistance, AttackRange]` 输出。
- **状态内的连续决策**：这是一个会**每帧重新判断**的状态（不像攻击态是一次性流程），所以它在 `Update` 里读距离、选动作。

#### 3. 业界常见实现方案
- **状态机里的分段判断（本项目选）**：简单直观，三个 `if` 解决，适合"进退"这种低维决策。
- **Steering Behaviors（Reynolds 操舵行为）**：把 Seek（靠近）/ Flee（逃离）/ Arrive（减速到达）做成可叠加的力，输出平滑、可组合避障。商业 ARPG 常用，但对"只沿连线进退"的 MVP 是杀鸡用牛刀。
- **行为树 / Utility AI**：把"接近/后撤/输出/找掩体"作为多个行为，用打分或优先级选择。扩展性强，是大厂复杂 AI 的主流，但前期投入大。
- **NavMesh + Agent**：用导航网格寻路绕障。需要烘焙地图，本 MVP 直接沿直线走、不绕障。

#### 4. 用到什么技术
- 一个新状态 `EnemyKiteState : EnemyStateBase`，构造收 `RangedEnemyController`。
- 复用基类行动能力：`MoveTo`（接近）/ `MoveAway`（后撤，重构时预留的那个）/ `StayGrounded`（站定）/ `FaceTarget`（转向）。
- 复用 `EnemyPerception` 的 `HasTarget` / `Target` / `DistanceToTarget`。
- 读 `EnemyDefinition.AttackRange` / `RetreatDistance` / `AttackCooldownCounter` 决策。

#### 5. 架构设计与拆解
`EnemyKiteState.Update()` 的决策树（顺序即优先级）：
1. 没目标 → 回 `IdleState`；
2. `FaceTarget`（无条件，每帧朝向玩家）；
3. `dist > AttackRange` → `MoveTo`（太远，接近）；
4. `dist < RetreatDistance` → `MoveAway`（太近，后撤）；
5. 落在档内 + 冷却就绪 → `ChangeState(RangedAttackState)`（开火）；
6. 档内但冷却中 → `StayGrounded`（站定等冷却）。

> 注意"进出战斗"的滞回（DetectRadius/LoseRadius）在 `EnemyPerception` 里、属于另一层；这里的 band 只管"已经在战斗中时怎么走位"。两层半径职责不同，别混。

#### 6. 踩坑与 Debug
- **坑（编译错误 CS0246）**：`EnemyKiteState` 里写了 `EnemyDefinition def = _enemy.Definition;`，但文件顶部只 `using UnityEngine;` ——`EnemyDefinition` 在 `Game.Combat` 命名空间，缺 `using Game.Combat;`，报 `CS0246: type 'EnemyDefinition' could not be found`。**根因**：计划里那份"逐字代码"漏写了这行 using，实现时照抄了。**修复**：补 `using Game.Combat;`。另两个新文件（施法态、远程控制器）本来就有这行，所以只有走位态翻车——很典型的"逐字搬运也会继承源头的笔误"。
- **边界稳健性**：当 `dist` 恰好等于 `RetreatDistance` 时，第 4 步（后撤）优先于第 5 步（开火），即边界上偏向"先拉开再打"——这是更安全的防御性偏置，不会卡在边界抖动。

#### 7. 大厂面试 Q&A
- **Q：你的 kiting 就三个 if，会不会在射程边界来回抖动（接近一帧、后撤一帧）？**
  A：当前不会，因为接近(`>AttackRange`)和后撤(`<RetreatDistance`)之间隔着一整段"站档区"，两个阈值不相邻，存在天然缓冲带，不会出现"跨过一条线就反向"的颤振。如果以后把这条带压窄到接近重合，我会引入**滞回**——比如接近的目标距离取 `AttackRange` 但后撤只在更近的 `RetreatDistance` 触发（现在就是这样），或者给动作切换加一个最小保持时间。这和我们感知层用 DetectRadius/LoseRadius 双半径防抖是同一个思想。

- **Q：直线进退太呆，玩家绕圈它就背对挨打，怎么升级？**
  A：MVP 只沿"我—玩家"连线进退，确实不会横向走位（strafe）。要更聪明，我会从两个方向加：一是引入 **steering behaviors**，把 seek/flee 换成可叠加的操舵力，再加一个垂直于连线的 strafe 力做绕圈、加 avoidance 力躲队友和障碍；二是上 **NavMesh** 让后撤会绕开墙角、不会撤进死胡同。再进一步就是行为树/Utility，让它会"找掩体""拉队友身后"。但这些都要等核心战斗手感定下来再加，避免过早复杂化。

---

### 小功能 4：远程施法发射（单点发火球 + 复用玩家火球 + 注入敌方阵营）

#### 1. 需求拆解
站定后播一段施法动画，在动画的某个时间点**发射一颗火球**朝玩家飞去；火球命中玩家要扣血、爆炸、点燃；**只能伤玩家、不能误伤其他敌人**；一次施法只发一颗；施法完进入冷却、回到走位态。

#### 2. 基础知识与原理
- **动画驱动时序（无 Animation Event）**：本项目统一用 `GetCurrentAnimatorStateInfo(0).normalizedTime`（动画归一化进度 0→1）对照 SO 里的时间点来触发，不用 Unity 的 Animation Event。火球生成是**单点触发**：到 `ArrowSpawnTime` 那一刻发一次。
- **单点触发的三重保险**：① 一个 `bool _fired` 防一次施法发多颗；② `IsInTransition(0)` 跳过过渡帧（CrossFade 期间别误判）；③ `shortNameHash == AttackStateHash` 确认"确实进到施法状态了"再发。这和弓箭手 `Arrow` 的生成判定是同一套。
- **投射物复用与阵营注入**：火球本身是通用的 `Fireball`（玩家法师也用），危险与否取决于"谁发的"。生成后调 `Init(...)` 把**攻击者阵营**塞进去——传敌方 team，伤害结算时 `TeamId == 攻击方` 的目标被跳过，于是火球**只伤玩家、自动放过其他敌人**。
- **直线 vs 抛物线**：火球传 `useGravity:false` 走直线（瞄哪打哪）；箭矢传 `true` 走抛物线。

#### 3. 业界常见实现方案
- **Instantiate 投射物（本项目选，原型期）**：每次开火 `Instantiate` 一颗、命中或超时销毁。简单直接。
- **对象池（Object Pool）**：预先造一批火球循环复用，避免频繁 `Instantiate`/`Destroy` 的 GC 与卡顿。**子弹/技能特效的工业标准**，是这套的首选优化项。
- **Hitscan（瞬时射线）**：高速弹（枪械）常用，发射即 `Raycast` 判定命中，无飞行物。火球要看得见飞行轨迹，所以用实体投射物而非 hitscan。
- **提前量瞄准（lead/predictive aiming）**：按玩家速度预测落点，打"提前量"。本 MVP 只瞄当前位置，玩家移动可躲——这是刻意保留的"可被玩家技巧规避"的设计空间。

#### 4. 用到什么技术
- 新状态 `EnemyRangedAttackState : EnemyStateBase`：`Enter` 转向 + CrossFade 施法动画；`Update` 按 `normalizedTime` 到 `ArrowSpawnTime` 调 `SpawnFireball()`；`Exit` 启动冷却。
- 新控制器 `RangedEnemyController.SpawnFireball()`：`Instantiate` `EnemyDefinition.ProjectilePrefab` → 取 `Fireball` 组件 → `Init(team, attackerId, 伤害, 类型, 方向*速度, _cc, useGravity:false)`。
- 瞄准：朝 `Target.position + Vector3.up * _aimHeightOffset`（打胸口不打脚）；方向归一化；`Quaternion.LookRotation` 让火球朝飞行方向。
- 复用 `ProjectileBase.Init` 里的 `Physics.IgnoreCollision(火球, _cc)`——传敌人自己的 `CharacterController` 当 caster collider，火球出膛不会撞自己自爆。
- 鲁棒结束判定：`_enteredAnimState`（进过施法态没）+ `EndThreshold 0.95`（到尾声结束）+ `MaxStateTime` 超时兜底（且只在"还没进过施法态"时才超时，防止误杀正常施法）。

#### 5. 架构设计与拆解
- `EnemyRangedAttackState` 几乎是近战 `EnemyAttackState` 的"镜像"：把"开/关命中窗口"换成"到点发一颗火球"，结束判定与冷却逻辑结构完全一致——因为这套"进入动画态→到点干一件事→鲁棒收尾"的骨架在近战时已经打磨好了，远程直接复用骨架、只换中间那一步。
- `RangedEnemyController` 持 `_kiteState` / `_rangedAttackState`，`EngageState => _kiteState`；`Awake()` 先 `base.Awake()` 再建自己的两个状态、缓存 `HealthComponent`。
- `SpawnFireball()` 对 `_definition` / `ProjectilePrefab` / `_projectileSpawnPoint` / `Fireball` 组件**逐个判空 + GameLog.Warn**，任何一个没配都不会 NRE，只是不发并告警。

#### 6. 踩坑与 Debug
- **坑（阵营注入，呼应火场那次的老坑）**：复用玩家火球时，如果**忘了 `Init` 注入敌方 team**，火球会带默认 team（0）。在我们的配置里敌人就是 team 0、玩家 team 1——结果火球把"team 0"当友方放过，**反而不伤玩家、却可能误伤其他敌人**。这和之前"陨石火场对敌人没伤害却伤玩家"是同一类根因：**AOE/投射物的阵营必须显式从攻击者注入，绝不能依赖默认值**。修复就是 `SpawnFireball` 里从 `HealthComponent.TeamId` 读敌人阵营传进 `Init`。
- **坑（施法结束竞态，复用近战修过的方案）**：Animator 上施法状态的"Has Exit Time"退出连线会让动画**提前离开施法态**，如果结束判定只认"`normalizedTime >= 0.95` 且仍在施法态"，可能永远等不到那一帧→卡死。沿用近战修过的方案：**进过施法态后又离开 = 结束**，再加 `EndThreshold` 和"没进过就超时"双兜底。
- **静态验证的重点**：因为不能在我这边编译，写完后逐一核对——`ProjectileBase.Init` 的 7 个参数顺序、`CharacterController` 确实继承自 `Collider`（能当 caster collider 传）、`AttackDefinition` 的 `BaseAmount/Type/ArrowSpawnTime/AnimationStateName` 字段名——全部对照已提交源码确认无误，再交开发者 Play 验证。

#### 7. 大厂面试 Q&A
- **Q：为什么不用 Animation Event 触发发射，而是手写 normalizedTime 判断？**
  A：一致性和可控性。整个项目所有战斗时序（近战命中窗口、刀光、连段输入窗、箭矢生成）都统一读 `normalizedTime` 对照 SO 数值，不混用 Animation Event。好处：发射时机是**数据**（在 SO 里调），不依赖美术在动画片段里埋帧；逻辑全在代码里、好读好调好搜；也避免了 Animation Event 在 CrossFade、动画被打断时回调时机不稳的坑。代价是要自己处理"过渡帧""单次去重"，我用 `IsInTransition` + `shortNameHash` + `_fired` 三重保险覆盖了。

- **Q：每次开火都 Instantiate，会不会有性能问题？怎么优化？**
  A：会，频繁 `Instantiate`/`Destroy` 产生 GC 和潜在卡顿，弹幕一密就明显。标准优化是**对象池**：预热一批火球，发射时从池里取、命中或超时后归还而非销毁，把运行时分配降到零。我现在是原型期、敌人数量少，先用 Instantiate 把玩法跑通，但生成入口收敛在 `SpawnFireball` 一个方法里，将来接对象池只改这一处。另外特效（爆炸、点燃）同理也应池化。我会在 Profiler 里确认战斗稳态零每帧 GC 后，把池化列为第一优化项。

- **Q：火球只瞄当前位置，玩家走两步就躲了，这是 bug 吗？**
  A：是刻意的设计取舍，不是 bug。直瞄给玩家留了"用走位规避远程"的操作空间，符合"远程敌人应该可被风筝、可被躲"的手感预期。如果要提高压迫感，我会加**提前量瞄准**——按玩家当前速度预测 `ArrowSpawnTime` 后的位置来定方向，甚至按火球飞行时间做迭代求解。是否加、加多少，是数值/手感问题，留给战斗策划调，而不是写死。

---

## 三、面试者视角：完整复盘这次开发（口述）

> 下面这段，是假设我在大厂面试里，面试官说"讲讲你这个远程法师敌人怎么做的"，我会怎么从头讲。

这个需求表面看是"加一种新敌人"，但我接到手第一反应不是写新代码，而是先问自己一个问题：**我已经有一个近战敌人了，新敌人和它有多少是相同的？** 盘下来发现，取组件、感知玩家、状态机、待机、受击、死亡、移动转向重力——这些全是共享的，真正不同的只有一件事：近战进战斗要贴上去挥刀，远程进战斗要保持距离放火球。差异维度只有一个：**战斗时怎么走位、怎么出手**。

所以我把这个功能拆成了四步，严格按依赖顺序：先重构出地基，再扩数据，再写走位，最后写发射。

**第一步是一次"行为不变"的重构**，这是整件事里我最看重的一步。我把 `EnemyController` 拆成抽象基类 `EnemyControllerBase`（装所有共享的东西）和近战子类 `MeleeEnemyController`，并在基类上留了一个抽象接缝 `EngageState`——基类只承诺"进入战斗后有一个走位状态"，但不说是哪个，近战返回追击态、远程返回走位态。这样待机态、受击态在切换时只认这个抽象，**完全不知道也不关心自己服务的是哪种敌人**，这就是开闭原则：以后加第三种敌人，只写子类、不碰旧代码。这一步有两个 Unity 特有的坑必须处理好：改名脚本要用 `git mv` 把 `.cs` 和 `.meta` 一起搬，GUID 不变、场景里的引用才不断；字段从子类上移到基类要保持字段名不变，靠 Unity"按字段名跨继承序列化"的机制，预制体里拖好的值才不会清零。重构没有自动化测试兜底，我靠的是"逐行确认语义等价 + 全局搜旧类型名 + 整程序集编译失败兜底 + 开发者 Play 回归"四层防护。

**第二步把远程数值外置到 ScriptableObject**——后撤距离、火球速度、火球预制三个字段，让策划在 Inspector 里调，代码里不留魔法数字。这一步顺手守住了模块单向依赖：数据类在 `Game.Combat`，只加值类型和 `GameObject` 引用，不反向依赖角色模块。

**第三步写走位状态 `EnemyKiteState`**，本质是把"我和玩家的距离"映射到三个动作：太远接近、太近后撤、正合适就站定输出，并且每帧都朝向玩家。我用两个半径划出一条"站档输出带"，两个阈值不相邻、天然有缓冲，所以不会在边界抖动。这里我特意区分了两层半径——进出战斗的滞回是感知层的事，走位的进退是战斗内的事，职责不混。这一步踩了个很典型的小坑：走位态引用了数据类却漏 `using`，编译报 CS0246，根因是我从计划逐字搬代码、把源头的笔误也搬过来了，补一行 using 就好——它提醒我"逐字搬运不等于免检"。

**第四步写施法发射**，这是复用做得最漂亮的一步。火球我**直接用玩家法师的那颗预制**，因为投射物本身是中立的，危险与否取决于谁发的——生成后调 `Init` 把敌人的阵营注入进去，伤害结算时同阵营自动跳过，于是这颗火球只伤玩家、自动放过其他敌人，还白嫖了它自带的爆炸和点燃。这里最关键的坑也在阵营：**复用投射物时阵营必须显式从攻击者注入，绝不能吃默认值**——我之前在陨石火场上就栽过一次同类根因（默认阵营导致敌我搞反），这次提前就防住了。发射时机用全项目统一的 `normalizedTime` 对照 SO 时间点触发，配 `bool` 去重 + 过渡帧过滤 + 状态 hash 校验三重保险保证一次施法只发一颗；施法的结束判定我直接复用了近战那次踩坑修好的方案——"进过动画态后又离开即结束"加超时兜底，绕开了 Animator 的 Has-Exit-Time 和 normalizedTime 抢跑导致的卡死。

回头看，这个功能真正的工程价值不在"会发火球"，而在于**我用一次干净的重构，让第二种敌人的增量代码薄到只剩三个小文件**，而且为第三种、第四种敌人铺好了同样的扩展路径。如果让我继续往下做，我的优先级会是：火球和特效上对象池消掉运行时 GC、给走位加 steering 和 NavMesh 让它会绕障和横向走位、再把"何时该攻击"抽象成攻击令牌避免多个远程同时齐射。但这些都建立在核心手感先跑通、再按 Profiler 和手感数据逐项加的前提上——我不会为想象中的需求提前把架构做复杂。

---

> **文档结束。** 配套：`Enemy_System_Design.md`（近战敌人 MVP）、`Enemy_System_Primer.md`（敌人系统扫盲）、`Optimization/Combat_And_Movement_Optimizations.md`（已有功能优化清单，对象池/感知节流等可续接本文的"下一步"）。
