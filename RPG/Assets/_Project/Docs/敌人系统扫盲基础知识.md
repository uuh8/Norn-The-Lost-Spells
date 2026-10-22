# 敌人系统（Enemy System）扫盲与设计文档

> **这份文档的定位**：你从没做过敌人系统，所以这是一份**开工前的扫盲 + 设计**文档，不是事后复盘。目标是让你在动手前就建立完整心智模型，理解"敌人系统到底要做什么、前辈们怎么做、我们为什么这么选、架构怎么拆"。
> 按你要求的四点组织：
> 1. 需求拆解与基础知识原理
> 2. 业界常见实现方案
> 3. 最终选择的方案与技术（什么技术解决了什么问题）
> 4. 架构设计与拆解
>
> 写作刻意基础、啰嗦，照顾零经验。读完这份再去写 spec / plan / 代码。

---

## 〇、先纠正一个新手直觉：敌人 ≠ "会动的攻击玩家的怪"

新手常把"敌人系统"理解成"做一个怪，让它追着玩家打"。但实际上，**敌人系统的 90% 是"决策"而不是"战斗"**。战斗部分（受击、扣血、死亡）你**已经做完了**——`HealthComponent` / `IDamageable` / `DamagePipeline` / `CharacterCombatFeedback` 全是通用的，敌人直接挂上就能挨打、能死。

所以敌人系统真正要新做的是这三件事：

```
   感知（Perception）        决策（Decision）          行动（Action）
   "我看得到玩家吗？"   ──►  "现在我该干什么？"  ──►  "执行：移动/攻击/..."
   距离、视野、仇恨           状态机/行为树              复用已有的移动+伤害管线
```

这三件事合起来叫 **"AI 循环（Sense-Think-Act）"**，是几乎所有游戏 AI 的通用骨架。记住这个三段式，后面所有内容都挂在它上面。

---

# 一、需求拆解与基础知识原理

## 1.1 需求拆解：一个"最小可玩敌人"要具备什么

我们不一上来就做 Boss。先定义一个**最小可玩敌人（MVP Enemy）**——它能让你的三职业第一次有"打谁"的对象。把它拆成可独立实现的小需求：

| # | 子需求 | 一句话说明 | 依赖 |
|---|--------|-----------|------|
| 1 | **能挨打、会死** | 挂血量、能被箭/火球/近战打、死了播动画消失 | 复用已有 `HealthComponent` |
| 2 | **感知玩家** | 玩家进入一定范围/视野 → 进入战斗 | 距离 + 视野判定 |
| 3 | **状态机骨架** | 待机 → 追击 → 攻击 → 受击 → 死亡 的流转 | 复用玩家那套手写状态机思路 |
| 4 | **移动追击** | 朝玩家移动、到攻击距离就停 | 移动（导航或简单朝向） |
| 5 | **攻击玩家** | 到近身距离 → 出招（带前摇）→ 打到玩家 | 复用 `ReceiveHit` 伤害管线 |
| 6 | **前摇预警 telegraph** | 出招前有"起手"提示，给玩家闪避机会 | 动画/特效时序 |
| 7 | **受击反应** | 被打时有反馈（可被打断 or 霸体） | 复用 `DamageReceivedEvent` |
| 8 | **数据驱动配置** | 血量/攻击力/速度/侦测范围等都在 SO 里调 | ScriptableObject |

**关键认知**：1、7 你几乎白嫖（已有系统）；真正的新工作量在 **2~6 的"感知 + 决策 + 行动"**。

## 1.2 基础知识：你必须先理解的几个概念

### (1) AI 循环：Sense → Think → Act（感知—思考—行动）

游戏 AI 不是"智能"，而是**一套每帧重复执行的判断流程**：

- **Sense（感知）**：收集世界信息——玩家在哪？距离多远？我看得到他吗？我血量多少？
- **Think（思考/决策）**：基于感知结果决定"现在该做什么"——追、打、逃、待机？
- **Act（行动）**：执行决策——移动、播放攻击动画、施加伤害。

你玩家的状态机其实也是这个循环（读输入=感知，CheckTransition=决策，Move/攻击=行动）。**敌人就是把"读玩家输入"换成"读对玩家的感知"**——这是理解敌人 AI 最重要的一句话。

### (2) 感知（Perception）的常见手段

- **距离检测**：`Vector3.Distance(self, player)` 或更省的 `sqrMagnitude` 比较。最便宜，最常用。
- **视野检测（FOV cone）**：用**点乘**判断玩家是否在敌人正面的视野锥里。
  - `dot = Vector3.Dot(enemy.forward, dirToPlayer.normalized)`，`dot > cos(半视角)` 表示在视野内。（你在弓箭手 3D 数学里已经学过点乘判方向）
- **视线遮挡（line of sight）**：`Physics.Raycast` 从敌人射向玩家，中间有墙就看不到。避免"隔墙发现你"。
- **仇恨/索敌（aggro）**：进入感知 → 进入战斗状态；脱离一段时间/距离 → 脱战（leash 回到出生点）。

### (3) "决策"的两大主流结构：状态机 vs 行为树

这是敌人 AI 的核心选择，第二节详谈。先建立直觉：

- **有限状态机（FSM）**：敌人在"待机/追击/攻击/死亡"等**有限的状态**间跳转，每个状态规定"在这个状态里做什么、什么条件切到别的状态"。简单直观，状态一多连线会爆炸。
- **行为树（Behavior Tree）**：用一棵"选择/顺序"节点组成的树，每帧从根节点往下"问"该做什么。更适合复杂、可复用的 AI。

### (4) 移动方式：导航网格（NavMesh） vs 简单朝向移动

敌人怎么"走向玩家"？

- **NavMesh + NavMeshAgent**：Unity 内置寻路。先"烘焙"出可行走区域（NavMesh），敌人用 `NavMeshAgent` 自动绕开障碍、走最优路径。**这是业界标准做法**。
- **简单朝向移动（steering）**：直接朝玩家方向走，不绕路。空旷场景够用，有障碍会卡墙。

### (5) 时序与"前摇预警"（telegraph）—— 为什么敌人攻击不能"瞬发"

如果敌人一进攻击范围就**瞬间**造成伤害，玩家根本无法反应，体验是"凭空掉血"，非常糟。所以敌人攻击必须有**前摇（startup）**：

```
前摇(telegraph) ──► 命中判定(active) ──► 后摇(recovery)
"举起武器/地面红圈"   "这一刻才真正打到你"    "收招，有破绽"
```

前摇就是给玩家的**预警信号**，让他有机会闪避/格挡——这正好接上你下一阶段要做的"攻防博弈(M5)"。这套时序你**已经会做了**：玩家攻击用的就是 `normalizedTime` 阈值 + ScriptableObject，敌人攻击完全同一套思路。

### (6) 群战要点（先了解，MVP 可不做）

多个敌人围攻玩家时，如果它们**同时**扑上来无脑打，既不公平也很乱。业界用 **"攻击令牌（attack token）"**：同一时刻只有持有令牌的少数敌人能进攻，其余在外围围而不打，轮流上。先知道有这回事，MVP 阶段不实现。

---

# 二、业界常见实现方案（前辈们怎么做）

针对敌人系统最核心的两个抉择——**决策结构**和**移动方式**——分别列业界方案与取舍。

## 2.1 决策结构方案对比

### 方案 A：有限状态机（FSM, Finite State Machine）

**做法**：定义若干状态（Idle / Patrol / Chase / Attack / Hurt / Die），每个状态有"进入/更新/退出"，状态间用条件转换。

**优点**：
- 概念极简单，新手友好；
- **和你玩家用的是同一套模式**——零学习成本，团队风格统一；
- 调试直观，"当前在哪个状态"一眼可见。

**缺点**：
- 状态数量增长时，**状态间转换连线呈指数爆炸**（N 个状态最多 N² 条边）；
- 复杂行为（"边后退边放风筝、血低于 30% 时狂暴"）会让单个状态变得臃肿。

**谁在用**：绝大多数普通杂兵、街机/动作游戏的常规敌人。**经典、可靠、够用。**

### 方案 B：分层状态机（HFSM, Hierarchical FSM）

**做法**：在 FSM 上加"父状态"分组，比如一个大的"战斗"父状态里再分"追击/攻击/闪避"子状态。

**优点**：缓解 FSM 的连线爆炸，把"脱战→回家""死亡"这类全局转换提到父层统一处理。

**缺点**：比纯 FSM 复杂一些；对 MVP 来说略重。

**谁在用**：中等复杂度敌人、需要"战斗/非战斗"大模式切换的游戏。

### 方案 C：行为树（Behavior Tree, BT）

**做法**：用一棵由 **Selector（选择，优先级 or 短路）/ Sequence（顺序，全部成功）/ 叶子节点（具体动作或条件）** 组成的树。每帧从根 tick 一遍，决定执行哪个叶子。

**优点**：
- **可复用、可组合**——子树能在不同敌人间复用；
- 加新行为是"往树上挂节点"，不像 FSM 要重连所有相关状态；
- 是**复杂敌人/Boss 的业界主流**（很多 3A 用它）。

**缺点**：
- 概念门槛高，对零经验者偏重；
- 简单敌人用 BT 是"杀鸡用牛刀"，反而绕；
- 需要一套 BT 运行时（自己写或用插件 Behavior Designer 等）。

**谁在用**：复杂 NPC、Boss、3A 开放世界敌人。

### 方案 D：效用系统（Utility AI）/ GOAP（目标导向行动规划）

**做法**：
- **Utility AI**：给每个可选行为打一个"效用分"，每帧选分最高的（"血少→治疗分高，敌近→攻击分高"）。
- **GOAP**：给定目标，AI 自己用规划算法搜索出达成目标的行动序列（《F.E.A.R.》成名作）。

**优点**：行为涌现自然、决策"聪明"。

**缺点**：**对你现在的阶段严重过度设计**，调试困难、实现复杂。**了解即可，不用。**

**谁在用**：模拟经营、《F.E.A.R.》《古墓丽影》等对 AI"聪明度"要求高的项目。

## 2.2 移动方式方案对比

### 方案 A：NavMesh + NavMeshAgent（Unity 内置寻路）

**做法**：烘焙场景 NavMesh，敌人挂 `NavMeshAgent`，`SetDestination(player.position)` 自动寻路绕障碍。

**优点**：业界标准、能绕开障碍走最优路、内置避障；改地形重烘焙即可。

**缺点**：`NavMeshAgent` 自带一套移动/旋转逻辑，**和你玩家用的 `CharacterController` 是两套位移系统**，需要决定"敌人用哪套"；Agent 会接管 transform，和手写移动可能打架。

### 方案 B：CharacterController + 简单朝向移动

**做法**：敌人也用 `CharacterController`（和玩家一致），每帧朝玩家方向 `Move`，不寻路。

**优点**：和玩家**完全同一套位移**，手感/碰撞一致；简单、可控；无需烘焙。

**缺点**：不会绕障碍，遇墙会卡（空旷场景无所谓）。

### 方案 C：NavMesh 算路径 + 自己驱动 CharacterController

**做法**：用 NavMesh 只**计算路径点**，但位移仍交给 `CharacterController` 执行。两者优点结合。

**优点**：既能绕路又保持位移系统统一。

**缺点**：实现最复杂，MVP 不必要。

---

# 三、最终选择的方案与技术（什么技术解决了什么问题）

> 选型总原则（贴合你项目的一贯风格）：**能复用已有系统就复用、保持与玩家架构一致、MVP 先简单后扩展、数据驱动**。

## 3.1 决策结构：选 **有限状态机（FSM）**，预留升级到行为树

**为什么选 FSM**：

1. **与玩家架构一致、零学习成本**：你的玩家就是手写 FSM（`PlayerStateMachine` + `PlayerStateBase`，`Exit→swap→Enter`）。敌人沿用同一模式，你能立刻上手，代码风格统一。
2. **MVP 敌人状态少**（待机/追击/攻击/受击/死亡，约 5 个），远没到 FSM 连线爆炸的程度。
3. **过早上行为树 = 过度设计**：BT 的复用优势在敌人种类多、行为复杂时才显现，现在用它是负担。

**解决了什么问题**：用最低的认知成本和最一致的风格，搭起"决策"骨架。**FSM 解决的是"敌人此刻该干什么"的调度问题。**

**升级预留**：把"决策"和"执行"分离（见第四节），将来要换行为树时，只替换"决策层"，"行动层"（移动/攻击）不动。

## 3.2 移动方式：MVP 选 **CharacterController + 简单朝向移动**，第二步再上 NavMesh

**为什么这么选**：

- **第一步**用 `CharacterController` + 朝玩家移动：和玩家位移系统**完全统一**（碰撞、贴地、手感一致），无需烘焙 NavMesh，最快让敌人"动起来"打通闭环。
- **第二步**（场景有障碍后）再引入 **NavMesh 算路径**：那时用"方案 C（NavMesh 算路径 + CharacterController 驱动）"保持位移统一。

**解决了什么问题**：先用最简单的方式**打通"追击"闭环**，避免一开始就陷入 NavMesh 烘焙/Agent 与 CharacterController 打架的泥潭。**位移系统统一**避免了"玩家和敌人两套物理手感不一致"的隐患。

## 3.3 受击/死亡：**直接复用**已有伤害管线（零新代码）

**用什么技术**：敌人挂 `HealthComponent`（已实现 `IDamageable`）+ `CharacterCombatFeedback`（受击闪红/动画/死亡/流血）。

**解决了什么问题**：你的伤害流程从一开始就设计成**攻防对称**——`HealthComponent` 不区分"挂在玩家还是敌人身上"，`DamageReceivedEvent`/`DeathEvent` 按 `TargetId` 派发。所以敌人受击、扣血、死亡、流血**一行新代码都不用写**，挂组件 + 配 SO 即可。这正是当初"值快照 + 纯函数管线 + 事件解耦"架构的回报。

> ⚠️ 关键配置点：**阵营 `TeamId`**。玩家是一队（如 0），敌人必须是另一队（如 1），否则你的箭会因"同阵营穿过"而打不到敌人。这是最容易忘的一步。

## 3.4 敌人攻击：复用 **normalizedTime 时序 + SO** 做"前摇→命中→后摇"

**用什么技术**：敌人攻击动画用 `CrossFadeInFixedTime` 进入（数据驱动状态名，和玩家一致）；命中时机读 `GetCurrentAnimatorStateInfo().normalizedTime` 比 SO 里配的阈值，到点用 `OverlapBox`/距离判定打到玩家，走 `ReceiveHit`。

**解决了什么问题**：
- **前摇 = telegraph**：命中阈值设在动画中后段（如 0.5），前半段就是给玩家的预警，**为下一阶段的闪避/格挡博弈(M5)铺路**。
- **复用既有时序范式**：和玩家攻击同一套（单点触发 + 去重 bool + 排除过渡帧），不发明新机制。

## 3.5 感知：**距离 + 视野锥(点乘) + 可选视线 Raycast**

**用什么技术**：每帧（或降频）算与玩家距离做 `sqrMagnitude` 比较；进阶用 `Vector3.Dot` 判视野锥；需要防穿墙时加一条 `Physics.Raycast` 查视线遮挡。

**解决了什么问题**：决定"何时进入/退出战斗"，避免敌人"全图无视墙壁追你"或"背后也能发现你"的不合理行为。

## 3.6 数据驱动：**EnemyDefinition ScriptableObject**

**用什么技术**：一个 `EnemyDefinition` SO 集中放：最大血量、攻击力、移速、侦测半径、攻击距离、视野角度、攻击冷却、攻击伤害/类型、各动画状态名……

**解决了什么问题**：符合你项目"数值进数据层"原则——**做不同的怪 = 新建一份 SO**，不改代码。策划/你自己调参不碰 C#。

## 3.7 选型总表

| 维度 | 选择 | 解决的问题 | 升级路径 |
|------|------|-----------|---------|
| 决策结构 | 手写 FSM | 低成本、与玩家一致的"该干什么"调度 | 决策层可换行为树 |
| 移动 | CharacterController 朝向移动 | 最快打通追击、位移统一 | 加 NavMesh 算路径 |
| 受击/死亡 | 复用 HealthComponent/反馈 | 零新代码挨打+死亡+流血 | — |
| 攻击时序 | normalizedTime + SO | 前摇 telegraph，为博弈铺路 | — |
| 感知 | 距离+视野锥(+视线) | 合理进/退战 | 加听觉/仇恨表 |
| 配置 | EnemyDefinition SO | 数据驱动，新怪=新 SO | 扩 Boss 阶段数据 |

---

# 四、架构设计与拆解

## 4.1 放在哪个程序集（模块）？

你现有结构：

```
Game.Core   ← EventBus / GameLog（地基）
├── Game.Combat   ← HealthComponent / IDamageable / DamagePipeline / 伤害事件
├── Game.Character ← PlayerControllerBase + 状态机 + 玩家状态（依赖 Core + Combat）
```

**敌人放哪？两个选项**：

- **选项 A（推荐起步）**：放进 **`Game.Character`**。因为敌人和玩家共享大量概念（状态机、CharacterController 移动、动画驱动），放一起能**直接复用玩家的状态机基类思路**，甚至共享一些工具。把 `Game.Character` 理解为"所有角色（玩家+敌人）"模块。
- **选项 B（未来洁癖）**：新建 `Game.Enemy` 程序集（依赖 Core + Combat）。模块更干净，但起步阶段会面临"状态机基类要不要下沉到能被双方共享"的问题，增加前期成本。

**建议**：**先放 `Game.Character`**，等敌人复杂了、确实需要隔离时再拆 `Game.Enemy`。不要为了洁癖在零经验阶段先拆模块。

> 依赖方向铁律不变：敌人可以依赖 `Game.Combat`（伤害）、`Game.Core`（事件），但 `Game.Combat`/`Game.Core` **绝不能反过来依赖敌人**。敌人与玩家、与表现层之间一律走 **EventBus**。

## 4.2 核心分层：把"决策"和"执行"分开（最重要的架构决定）

这是整个敌人架构的灵魂——**Sense-Think-Act 三段式落地为分层**：

```
┌─────────────────────────────────────────────────────────┐
│ EnemyController (MonoBehaviour)                           │
│  - 持有组件引用：CharacterController / Animator / Health   │
│  - 持有感知模块 EnemyPerception                            │
│  - 持有状态机 EnemyStateMachine + 各状态实例               │
│  - 持有数据 EnemyDefinition (SO)                           │
│  - Update(): 驱动感知 → 驱动状态机                          │
│  - 暴露"行动能力"给状态调用（MoveTowards/FaceTarget/...）    │
└─────────────────────────────────────────────────────────┘
        │ 持有                    │ 持有
        ▼                         ▼
┌──────────────────┐   ┌──────────────────────────────────┐
│ EnemyPerception  │   │ EnemyStateMachine（决策层）         │
│ (Sense)          │   │  Idle / Chase / Attack / Hurt / Die │
│ - 距离/视野/视线  │   │  每个状态: Enter/Update/Exit         │
│ - 输出: 玩家引用  │   │  状态调用 Controller 的行动能力       │
│   是否可见/距离    │   │  (Act)                             │
└──────────────────┘   └──────────────────────────────────┘
```

**为什么这样分**：
- **决策层（状态机）只决定"做什么"，不关心"怎么做"**；行动能力（移动、转向、播动画、出招）实现在 `EnemyController` 上，由状态**调用**。
- 这样将来**把 FSM 换成行为树时，只换决策层**，行动能力一行不动。这就是 3.1 说的"升级预留"。
- 和你玩家架构**完全同构**（玩家状态调用 `PlayerControllerBase` 的能力），你一看就懂。

## 4.3 各组件职责拆解

### (1) `EnemyController : MonoBehaviour`（总装 + 行动层）

类比你的 `PlayerControllerBase`。职责：
- `Awake`：取组件（`CharacterController`/`Animator`/`HealthComponent`）、`new` 出感知模块和各状态（**预实例化，零运行时 GC**，和玩家一致）、预 hash 动画状态名。
- `Update`：① 更新感知 → ② 驱动状态机 `Update` → ③ 同步 Animator 参数（和玩家同顺序）。
- **行动能力 API**（给状态调用）：`MoveTowards(targetPos)`、`FaceTarget(targetPos)`、`StopMoving()`、`PlayAttackAnim()`、`DealAttackDamage()` 等。
- 暴露数据（`EnemyDefinition` 各字段）和感知结果给状态读。

### (2) `EnemyPerception`（感知层 / Sense）

普通 C# 类（非 MonoBehaviour）。职责：
- 每帧（或降频）计算：玩家是否在侦测半径内、是否在视野锥内、是否有视线、当前距离。
- 输出供决策读：`HasTarget`、`Target`、`DistanceToTarget`、`CanSeeTarget`。
- **怎么拿到玩家**：MVP 可以用一个简单的"玩家注册"（玩家 `Awake` 时把自己登记到一个静态/单例，或敌人用 Tag/Layer 查一次缓存）。避免每帧 `FindObjectOfType`（慢且有 GC）。

### (3) `EnemyStateMachine` + 状态们（决策层 / Think，调用行动 = Act）

**直接照搬玩家的状态机结构**（`Exit→swap→Enter`）。MVP 状态：

| 状态 | 在干什么 | 转出条件 |
|------|---------|---------|
| `EnemyIdleState` | 待机/原地，等待感知到玩家 | 感知到玩家 → Chase |
| `EnemyChaseState` | `MoveTowards` 玩家、`FaceTarget` | 进入攻击距离 → Attack；丢失目标 → Idle |
| `EnemyAttackState` | 停下、播攻击动画、到 normalizedTime 阈值施加伤害 | 攻击动画结束 → Chase（或按冷却）|
| `EnemyHurtState`(可选) | 播受击动画、短暂硬直 | 硬直结束 → Chase |
| `EnemyDeadState` | 播死亡动画、禁用 AI、延时销毁 | 终态 |

> 注意：`EnemyHurtState`/`EnemyDeadState` 可以**不做成 FSM 状态**，而是由 `CharacterCombatFeedback` 监听 `DamageReceivedEvent`/`DeathEvent` 处理表现，AI 这边只在死亡时**禁用状态机**。两种都行——MVP 建议：**死亡走事件禁用 AI**（最省），受击是否打断 AI 看你要不要"霸体"。这块设计上留个口子，等做 M5 博弈时再细化。

### (4) `EnemyDefinition : ScriptableObject`（数据层）

集中所有可调参数（见 3.6）。每种怪一份资产。

### (5) 复用的既有组件（不新写）

- `HealthComponent`（挨打/扣血/发事件）—— 配 `TeamId=敌方`、`maxHp`。
- `CharacterCombatFeedback`（闪红/受击动画/死亡/流血）—— 配敌人自己的动画状态名 + 血特效。
- 伤害管线、EventBus、GameLog —— 直接用。

## 4.4 敌人"攻击玩家"的数据流（和你已有的完全对称）

```
EnemyAttackState (normalizedTime 到阈值)
   └─► EnemyController.DealAttackDamage()
         └─► OverlapBox/距离判定命中玩家
               └─► 构造 DamageRequest(攻击者=敌人, team=敌方, ...)
                     └─► playerHealth.ReceiveHit(req)   ← 和敌人挨打同一条管线！
                           └─► DamagePipeline.Resolve → 扣血 → 发 DamageReceivedEvent/DeathEvent
                                 └─► 玩家的 CharacterCombatFeedback 响应（闪红/受击/死亡）
```

**这张图的重点**：敌人打玩家、玩家打敌人，**走的是同一条 `ReceiveHit` 管线**，只是 `TeamId` 相反。你不需要写"敌人专属的伤害逻辑"。

## 4.5 性能与一致性注意（延续项目纪律）

- **零 GC 热路径**：状态、感知模块在 `Awake` 预实例化；感知的 `OverlapSphere`/`Raycast` 用 `NonAlloc` 版 + 预分配数组；动画名 `StringToHash` 缓存；不在 Update 里 `FindObjectOfType`/LINQ/new。
- **感知降频**：感知不必每帧算，可每 0.1~0.2s 算一次（用计数器），多个敌人时省开销。
- **CharacterController 在 Update 移动**（和玩家一致，不在 FixedUpdate）。
- **多敌人时**：未来用对象池生成敌人；MVP 阶段先直接 Instantiate。
- **EventBus 清理**：敌人订阅事件时记得 `OnEnable/OnDisable` 成对订阅退订；场景卸载时 `Clear`（你 CLAUDE.md 里已规划的场景生命周期）。

## 4.6 建议的文件落点（放 `Game.Character` 起步）

```
Scripts/Character/
├── Controllers/
│   └── EnemyController.cs            ← 总装 + 行动能力
├── Enemy/                            ← 新建子文件夹
│   ├── EnemyPerception.cs           ← 感知
│   ├── EnemyStateMachine.cs         ← (可直接复用/平行于 PlayerStateMachine)
│   └── States/
│       ├── EnemyStateBase.cs
│       ├── EnemyIdleState.cs
│       ├── EnemyChaseState.cs
│       └── EnemyAttackState.cs
└── ...
Scripts/Combat/Definitions/
└── EnemyDefinition.cs               ← 敌人数据 SO（放 Combat 还是 Character 看依赖；
                                        若只被 Character 读，可放 Character）
```

> 状态机：可以直接复用玩家的 `PlayerStateMachine`/`PlayerStateBase` 吗？——它们目前持有 `PlayerControllerBase` 类型，敌人类型不同。**MVP 最简做法**：写一套平行的 `EnemyStateMachine`/`EnemyStateBase`（持有 `EnemyController`），结构照抄玩家那套。等将来真有共享需求，再考虑把状态机抽象成泛型/接口下沉到 Core。**先别为复用而过早抽象**（你在法师文档里总结过的 Rule of Three）。

---

## 五、落地建议：MVP 的最小步骤清单（开工顺序）

按这个顺序做，每步都可在 Editor 里验证，不会一次性陷进大坑：

1. **能挨打的木桩**：空敌人 GameObject + `HealthComponent`(TeamId=1) + `CharacterCombatFeedback`(配敌人受击/死亡动画 + 血特效)。用你的三职业去打，验证"能打中、会闪红、会死、会流血"。——**这步几乎不写代码，先把复用打通。**
2. **EnemyDefinition SO + EnemyController 骨架**：把数据搭起来，Controller 先只取组件、不做 AI。
3. **感知 EnemyPerception**：实现距离/视野检测，`GameLog` 打印"发现玩家/丢失玩家"验证。
4. **状态机 + Idle/Chase**：敌人能在感知到玩家后**走向**玩家、到攻击距离停下。
5. **EnemyAttackState + 前摇攻击**：到近身播攻击动画，normalizedTime 到点打到玩家（走 ReceiveHit），玩家会掉血/闪红。验证"敌人能打疼我"。
6. **受击/死亡 AI 处理**：死亡时禁用状态机；决定受击是否打断（MVP 可先不打断）。
7. **调参 + 多个敌人**：用 SO 调出不同手感，放 2~3 个敌人试群战观感。

完成 1~6 就达成了**"战斗闭环"**——你的角色第一次有了会追、会打、会死的对手。这正是简历里"敌人 AI 状态机 + 攻防对称伤害管线"最硬的实证。

---

## 附：关键术语速查（自学检索用）

- AI 循环：`Sense-Think-Act`、`game AI loop`
- 决策：`Finite State Machine`、`Hierarchical FSM`、`Behavior Tree (Selector/Sequence)`、`Utility AI`、`GOAP`
- 感知：`perception / aggro`、`field of view (dot product)`、`line of sight raycast`、`leashing / de-aggro`
- 移动：`NavMesh / NavMeshAgent`、`steering behaviors`、`path following`
- 攻击：`attack telegraph`、`startup/active/recovery frames`、`attack token (group AI)`
- 进阶：`boss phase / enrage`、`spawner / wave`、`object pooling for enemies`

标杆参考：**只狼/黑魂（前摇预警、削韧、群战令牌）、怪物猎人（敌人 moveset 与可读性）、《F.E.A.R.》（GOAP 经典）、原神（杂兵 FSM + 元素反应）。**

---

> **结语**：敌人系统对你来说不可怕——它的"战斗部分"你早已做完，真正要学的是"感知—决策—行动"这套 AI 循环，而它的骨架（手写状态机）你在玩家身上已经驾轻就熟。按"先复用打通、再加感知、再加决策、最后加攻击"的顺序走，你会发现敌人不过是"把读输入换成读感知的另一个角色"。读完这份就可以进入 brainstorm → spec → plan → 实现 了。
