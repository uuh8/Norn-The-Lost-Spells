# 连段系统（Combo System）设计解析

> 目标读者：有 C# 基础、刚跑通 M3 单次攻击闭环、**零连段系统经验**的开发者。
> 本文重点讲「为什么这样设计」和「怎么落地」，不是复述代码做了什么。
> 阅读顺序：先看「全局心智模型」建立整条数据流的直觉，再按子功能逐个精读——每个子功能都独立走完 ① 需求 ② 基础原理 ③ 备选方案 ④ 选型 ⑤ 架构 ⑥ 性能预算 ⑦ 复盘接口 七个维度，可单独跳读。
>
> 前置阅读：`M3_Combat_Design.md`（单次攻击：命中→扣血→死亡）。连段系统是在它之上叠的一层「出招序列调度」，**完全复用** M3 的命中判定与结算管线。

---

## 全局心智模型

连段的本质：**把"一次攻击"从单段变成"一串有序的段"，并在每段动画的特定时间窗口里，根据玩家是否再次按键，决定接下一段还是收招。**

关键直觉先记住三条：

1. **数据在 Combat，驱动在 Character。** 「有哪几段、每段多少伤害、命中盒多大、什么时候能接下一段、对应哪个动画」全是 Combat 侧的**纯数据**（`AttackDefinition` / `ComboDefinition`）；「读动画进度、切动画、推进段数」是 Character 侧的**行为**（`PlayerAttackState`）。Combat 永远不碰 `Animator`。
2. **判定是一个纯函数。** 「这一帧该维持/推进/收招」被抽成一个不依赖 Unity 的纯函数 `ComboResolver.Resolve(...)`，像 M3 的 `DamagePipeline` 一样可以脱离引擎单测。
3. **整套连段由一个状态实例承载，归零只有一个出口。** `PlayerAttackState` 一个实例从第 0 段驱动到最后一段，段数 `comboIndex` 只在 `Exit()` 归零——任何离开攻击态的路径都经过 `Exit()`，所以「连段没清零」这个失败模式从结构上被消灭。

数据流简图：

```
玩家按攻击键
   │  PlayerController.OnAttackPerformed → AttackBufferCounter = 0.15s（输入缓冲，复用 M3）
   ▼
PlayerGroundedState: AttackBufferCounter>0 → ChangeState(AttackState)
   │
   ▼
┌─ PlayerAttackState（Character 侧·连段驱动主体）────────────────────────┐
│ Enter(): comboIndex=0 → StartSegment(0)                                │
│                                                                        │
│ StartSegment(i):                                                       │
│   MeleeHitDetector.SetAttack(Combo.Segments[i])  ← 换段缝（子功能4）   │
│   MeleeHitDetector.CloseHitWindow()              ← 关窗，重置 per-swing │
│   Animator.CrossFadeInFixedTime(GetComboStateHash(i)) ← 预hash（子功能5）│
│                                                                        │
│ Update() 每帧:                                                         │
│   HandleAttackWindow()  → 按本段 ActiveStart/End 开/关命中（复用 M3）  │
│   CheckCombo():                                                        │
│     读 Animator.normalizedTime + AttackBufferCounter                   │
│        │                                                               │
│        ▼  纯函数（Combat 侧·子功能3）                                  │
│     ComboResolver.Resolve(comboIndex, segCount, t, hasBuffer,         │
│                           seg.ComboInputStart, seg.ComboInputEnd, 0.85)│
│        │                                                               │
│        ├─ Advance → comboIndex++; 消耗buffer; StartSegment(i+1)        │
│        ├─ End     → TransitionToMovement()                             │
│        └─ Continue→ 维持                                               │
│                                                                        │
│ Exit(): comboIndex=0; AttackBufferCounter=0; CloseHitWindow()         │
│         ← 连段归零的唯一出口                                           │
└────────────────────────────────────────────────────────────────────┘
   │ 读纯数据
   ▼
┌─ Game.Combat（纯数据 / 纯逻辑，禁 Animator/InputSystem）──────────────┐
│ AttackDefinition[]（每段：伤害/命中盒/激活窗/连段窗/动画名）（子功能1）│
│ ComboDefinition（有序段表 SO）（子功能2）                              │
│ ComboResolver / ComboDecision（纯函数判定）（子功能3）                 │
│ MeleeHitDetector.SetAttack（换段缝）（子功能4）                        │
└────────────────────────────────────────────────────────────────────┘
```

下面六个子功能逐段拆解。

---

## 子功能一：AttackDefinition 的连段字段扩展

> 文件：`Assets/_Project/Scripts/Combat/AttackDefinition.cs`
> 新增字段：`ComboInputStart` / `ComboInputEnd`（连段输入窗口，归一化时间）、`AnimationStateName`（本段动画状态名）

### ① 需求拆解与验收标准

要解决的问题：M3 的 `AttackDefinition` 只描述「一次攻击的伤害与命中几何 + 一个激活窗口」。连段需要每段额外回答两个问题：
- **什么时候允许接下一段？** → `ComboInputStart` / `ComboInputEnd`（一段归一化时间区间，玩家在此区间内按键才接招）。
- **这一段播哪个动画？** → `AnimationStateName`（Animator 里对应 state 的名字）。

验收标准：
- 策划能在 Inspector 里逐段配置这两个窗口和动画名，**改数值不需要改代码**。
- `AnimationStateName` 是纯字符串，**不引入任何 `UnityEngine.Animator` 类型引用**（守住 asmdef 边界）。
- 原有字段（`BaseAmount` / `Type` / `HalfExtents` / `ActiveStart` / `ActiveEnd`）一字不改，单次攻击闭环照常工作。

### ② 涉及的基础知识和原理

- **归一化动画时间（normalizedTime）**：Animator 把一个动画 clip 的播放进度映射到 `[0,1]`，0=起点、1=终点（循环动画第二圈是 1~2，所以代码里要 `% 1f` 取小数部分）。连段的「激活窗口」和「连段输入窗口」都用这个 0~1 的归一化坐标来描述「动画播到百分之几时」，这样**与 clip 实际秒数解耦**——换一个更长/更短的挥砍动画，窗口比例不用重配。
- **ScriptableObject 序列化字段**：`public` 字段会被 Unity 序列化并显示在 Inspector，`[Range(0,1)]` 让它显示为滑条并钳制取值，`[Header]`/`[Tooltip]` 是给策划看的分组与说明。这些是「数据驱动」的载体。
- **为什么动画名是 `string` 而不是直接存 Animator 引用**：`Game.Combat` 程序集**不允许**依赖表现层。Animator 属于表现，但「一个状态的名字」只是一个字符串标识符——字符串是中立的纯数据。把它放在 Combat 不违反边界；真正碰 Animator 的 `StringToHash`/`CrossFade` 都发生在 Character 侧（见子功能五）。

### ③ 实现该功能有哪些方案

连段输入窗口放哪：
- **A. 放在每段的 `AttackDefinition` 上**（逐段独立窗口）。
- B. 放在 `ComboDefinition` 上做一个全局统一窗口（所有段共用一组数值）。
- C. 硬编码在状态机里（如固定「40%~70% 可接招」）。

动画名归属：
- **A. 作为 `string` 字段放在 `AttackDefinition`**（每段数据自带动画名）。
- B. Character 侧维护一个与段平行的动画名数组。
- C. 约定命名，从段 id 用规则推导出状态名。

### ④ 方案与技术选择

**连段输入窗口选 A（逐段放在 AttackDefinition）**：后段往往比前段窗口更紧（收招越来越快是动作游戏常见手感），逐段独立给了这个自由度，且成本为零（就是两个 float）。B 的全局统一窗口缺乏表现力；C 硬编码违反数据驱动。

**动画名选 A（string 放在 AttackDefinition）**：brainstorm 决策②确认。理由——**单一真相**：一段的「伤害+命中盒+激活窗+连段窗+动画名」全在同一个资产里，策划配一处即可，不会出现「第 3 段的伤害在 Combat、第 3 段的动画名在 Character」这种两处对齐、容易错位的局面（B 的痛点）。代价是 Combat 数据里出现了一个表现性的字符串——但约束明确允许字符串，且**hash 化在 Character 侧做**，Combat 仍然零 Animator 依赖。C 的约定命名太脆，改名就崩。

### ⑤ 架构设计与拆解

这三个新字段是**纯被动数据**，自己不含任何逻辑，被三个不同的消费者在不同时机读取：

| 字段 | 谁读 | 何时 | 用途 |
|------|------|------|------|
| `ComboInputStart/End` | `ComboResolver`（经 `PlayerAttackState.CheckCombo` 传入） | 每帧攻击中 | 判断当前是否在「可接招」区间 |
| `AnimationStateName` | `PlayerController.BuildComboStateHashes` | `Awake` 一次 | 预 hash 成 int 供 CrossFade |
| `ActiveStart/End`（原有） | `MeleeHitDetector.Attack`（经 `HandleAttackWindow`） | 每帧攻击中 | 开/关命中窗口 |

依赖方向：`AttackDefinition` 不依赖任何上层，处于 Combat 最底层。它被 `ComboDefinition` 以数组元素的形式聚合（子功能二）。

### ⑥ 性能预算前置

- 预算：**运行期零开销**。字段就是序列化数据，读取是字段访问。
- 守约机制：唯一有成本的操作是把 `AnimationStateName` 字符串 `StringToHash`，这件事被**推迟到 Character 侧 Awake 做一次**（子功能五），不在每帧/每次切段发生。所以本子功能对热路径贡献 0 GC、0 字符串查找。

### ⑦ 预留复盘接口

- 扩展点：未来想给「每段是否锁移动 / 是否可被翻滚取消 / 受击硬直时长」等加配置，继续往 `AttackDefinition` 加纯数据字段即可，消费者各自取用。
- 排查起点：
  - 「连段接不上」→ 先怀疑该段 `ComboInputStart/End` 与动画实际可接时机不符（窗口太早或太窄）。
  - 「动画不切换」→ 先查 `AnimationStateName` 是否与 Animator state 名**逐字符**一致（大小写、下划线）。

---

## 子功能二：ComboDefinition（连段表 ScriptableObject）

> 文件：`Assets/_Project/Scripts/Combat/ComboDefinition.cs`

### ① 需求拆解与验收标准

要解决的问题：把「一把武器的整套连招顺序」表达成一份可配置的数据资产。本轮是**线性序列**（第 0 段 → 第 1 段 → … → 最后一段）。

验收标准：
- 一个 `ComboDefinition` 资产 = 一把武器的连段表；**新增武器 = 新建一份资产 + 拖入它的若干 `AttackDefinition`，不改任何代码**。
- 提供 `SegmentCount`，且 `Segments` 为空时返回 0（不抛异常）。

### ② 涉及的基础知识和原理

- **ScriptableObject 作为"共享数据容器"**：它是磁盘上的资产，不挂在场景物体上，适合存「设计数据」。多个角色/武器可以引用同一份，改一处全生效。
- **引用数组 `AttackDefinition[]`**：连段表持有的是对各段 `AttackDefinition` 资产的**引用**，不是拷贝。所以同一个段（比如一个通用的「收招重击」）可以被多套连段复用。
- **表达式体属性 + null 合并**：`SegmentCount => Segments != null ? Segments.Length : 0` 是一个只读计算属性，把「空数组安全」收敛在数据侧，调用方不必到处判空。

### ③ 实现该功能有哪些方案

- **A. 有序的 `AttackDefinition[]` 引用数组**（线性序列）。
- B. 内联结构体数组（把每段的数据直接嵌在 ComboDefinition 里，不引用独立资产）。
- C. 图/树结构（节点 + 转移条件），直接支持分支连段（轻轻重、轻重轻……）。

放哪个程序集：Combat（能被 Character 读）还是 Character。

### ④ 方案与技术选择

**选 A，放 Combat**：
- vs B：引用独立 `AttackDefinition` 资产可跨连段复用单段、且与「单次攻击也用 AttackDefinition」保持一致；内联结构体会让单段数据无法复用。
- vs C：树结构能支持分支，但本轮 scope 明确**只做线性**，引入图结构是过度设计（YAGNI）。线性数组用 `comboIndex` 自增即可推进，简单到不会错。C 被预留为未来扩展（见⑦）。
- 放 Combat 的依据：它只引用 `AttackDefinition`（同在 Combat），是纯数据，Character 依赖 Combat 因而能读它；反过来放 Character 会让连段数据无法被未来的纯 Combat 逻辑（如技能连段）复用。

### ⑤ 架构设计与拆解

职责极薄：一个有序引用数组 + 一个 count 属性。它是**段数据的聚合器**。

调用关系：
- 被 `PlayerController._combo` 序列化持有（Inspector 拖入）。
- `BuildComboStateHashes` 遍历它取每段动画名。
- `PlayerAttackState` 用 `comboIndex` 索引 `Segments[i]`，并用 `SegmentCount` 喂给 `ComboResolver` 判断「还有没有下一段」。

### ⑥ 性能预算前置

- 预算：只读数据，**无每帧成本**。`SegmentCount` 是 O(1) 字段访问。
- 守约：不在运行期创建/修改，纯查表。

### ⑦ 预留复盘接口

- 扩展点：
  - **分支连段** → 把线性 `AttackDefinition[]` 升级为节点图（每节点带输入条件 + 多个后继），`ComboResolver` 的「下一段」从 `index+1` 改成「按输入选后继」。**关键**：因为段数据本身在 `AttackDefinition`、序列结构独立在 `ComboDefinition`，升级序列结构时段数据零改动。
  - **多武器** → 每把武器一份资产，切武器时换 `PlayerController._combo`（并重建 hash 缓存，见子功能五⑦）。
- 排查起点：「连段段数不对/越界」→ 查 `SegmentCount` 与 `Segments` 实际长度；「某段为空」→ Inspector 里 `Segments` 该格未赋值，会被 `CheckCombo` 的 null 防御兜住（见子功能六）。

---

## 子功能三：ComboResolver 纯函数判定 + ComboDecision 枚举 + EditMode 单测

> 文件：`Combat/ComboResolver.cs`、`Combat/ComboDecision.cs`、`Tests/Combat/ComboResolverTests.cs`

### ① 需求拆解与验收标准

要解决的问题：把「这一帧连段该怎么走」这个判断，从一堆 `if/else` 散落在状态机里，收敛成一个**可独立验证**的决策。输出三选一：`Continue`（维持当前段）/ `Advance`（推进下一段）/ `End`（收招）。

验收标准：
- 给定（当前段号、总段数、动画进度、是否有缓冲输入、本段连段窗口起止、收招阈值）→ 确定性地返回三种决策之一。
- **不依赖任何 Unity 类型**，可在 EditMode 下单测。
- 8 个单测覆盖：窗口内有缓冲推进、无缓冲维持、最后一段不推进、窗口前维持、窗口后收招前维持、到阈值收招、最后一段到阈值收招、推进优先于收招。

### ② 涉及的基础知识和原理

- **纯函数（pure function）**：输出只由入参决定、无副作用（不读写全局、不碰 Animator/时间）。好处是**可脱离引擎单测**——不进 Play、不建 GameObject，直接「输入→断言输出」。这正是 M3 `DamagePipeline` 的同款套路。
- **枚举作判定结果（`enum ComboDecision : byte`）**：用具名枚举而不是「魔法 int / 多个 bool」表达三态，语义自解释且 `switch` 能穷举。`: byte` 指定 1 字节底层类型，按值返回**不装箱**、零 GC。
- **闭区间边界判断**：`normalizedTime >= inputStart && normalizedTime <= inputEnd` 是标准的「在窗口内」判定。理解 `>=`/`<=` 的含义对调窗口手感很关键（边界是包含的）。
- **优先级顺序**：先判 `Advance` 再判 `End`，保证「窗口与收招阈值重叠时，一次有效接招输入赢过收招」——这是手感正确性的核心，单测 `AdvancePriorityOverEnd` 专门钉住它。

### ③ 实现该功能有哪些方案

- **A. 纯静态函数**（无状态、可单测）。
- B. 把判定逻辑直接内联进 `PlayerAttackState.Update`。
- C. 策略对象（运行时可替换不同判定规则）。

### ④ 方案与技术选择

**选 A**（brainstorm 决策④）：
- vs B：内联进状态机就只能在 Play 模式连真机测，慢且脆；抽成纯函数后，所有边界 case（窗口上下界、最后一段、推进 vs 收招优先级）都能在毫秒级单测里钉死。连段手感 bug 往往就是这些边界错位，单测是最划算的防线。
- vs C：策略对象支持运行时换判定规则，但本轮没有这个需求，YAGNI。当前纯函数结构也不挡未来升级成策略。

### ⑤ 架构设计与拆解

- 入参全是基本类型（int/float/bool）——**这是它能保持纯净的前提**：调用方负责把 Animator 的 `normalizedTime`、缓冲计时器、SO 里的窗口数值「翻译」成这些基本类型再传进来。
- 内部先算 `hasNext = comboIndex + 1 < segmentCount`，再按「Advance 优先 → End → Continue」三段判定。
- 输出 `ComboDecision` 被 `CheckCombo` 的 `switch` 消费。
- 依赖：零。它谁都不依赖，是整个连段系统里最容易测试、最稳定的一块。

### ⑥ 性能预算前置

- 预算：**零 GC、零装箱**，每帧攻击中调用一次。
- 守约：函数体只有比较和返回，无 `new`、无集合、无闭包；返回的 `enum:byte` 按值传递不装箱。

### ⑦ 预留复盘接口

- 扩展点：
  - 分支连段 → 返回类型可扩展为「Advance + 目标分支」；新增规则就新增分支 + 对应单测。
  - 加「取消窗口」「最早可接帧」等规则 → 加入参 + 加单测。
- 排查起点：**连段时机相关的 bug，第一站就是这里**。复现某个手感问题 → 写一个还原该输入组合的单测（红）→ 在 `Resolve` 里修（绿），逻辑不必进 Play 模式反复试。

---

## 子功能四：MeleeHitDetector.SetAttack（换段缝）

> 文件：`Assets/_Project/Scripts/Combat/MeleeHitDetector.cs`（新增 `SetAttack` 方法）

### ① 需求拆解与验收标准

要解决的问题：M3 的 `MeleeHitDetector` 持有**单个** `AttackDefinition`（只读 `Attack` getter）。连段每段的伤害/命中盒不同，需要在切段时把检测器当前生效的攻击数据**换掉**。

验收标准：
- 提供一个换段入口 `SetAttack(AttackDefinition)`。
- 换段后，新段能对**同一个敌人**重新命中（即 per-swing 去重要按段重置，否则第 2 段打不到第 1 段已打过的敌人）。
- 单次攻击闭环不受影响。

### ② 涉及的基础知识和原理

- **只读 getter vs 可写 setter**：M3 暴露 `public AttackDefinition Attack => _attack;`（只读）。连段要改它，于是加一个最小 setter 写 `_attack`。前提是 `_attack` 字段非 `readonly`（确实是 `[SerializeField] private`）。
- **per-swing 去重与窗口的关系**：检测器内有一个 `HashSet<int> _hitSet` 记录「本次挥砍已命中过谁」，避免一次挥砍多帧重复结算。`OpenHitWindow()` 是**幂等**的：从「关」到「开」的那次调用会 `_hitSet.Clear()`。所以**只要窗口在两段之间真正关过一次，再开时去重集自动清空**——这就是新段能重新命中老敌人的机制。

### ③ 实现该功能有哪些方案

- **A. 加一个 `SetAttack` setter，复用同一个检测器**。
- B. 每段重建/替换一个 `MeleeHitDetector`。
- C. 武器上挂多个检测器，每段启用对应的一个。

去重重置方式：
- **A. 复用既有「关窗→开窗清去重」机制**（不额外写清理）。
- B. `SetAttack` 里主动 `_hitSet.Clear()`。

### ④ 方案与技术选择

**换段选 A（单检测器 + setter）**：最小改动、零额外对象；B/C 都要管理多个检测器实例与生命周期，复杂且无收益。

**去重重置选 A（复用关窗/开窗）**：`StartSegment` 切段时调 `CloseHitWindow()`，新段动画在 CrossFade 过渡期 `HandleAttackWindow` 也会持续关窗，过渡结束后到达新段 `ActiveStart` 才 `OpenHitWindow()`——这次「关→开」自然触发 `_hitSet.Clear()`。**不在 `SetAttack` 里主动清去重**（注释已写明这个契约），避免两处都管去重导致语义重叠。

### ⑤ 架构设计与拆解

- `SetAttack(AttackDefinition)`：就一行 `_attack = attack`。职责单一——「换当前生效的攻击数据」。
- 与既有成员的关系：换完后，`DoOverlap`（命中检测）和 `HandleAttackWindow`（经 `Attack` getter 读 `ActiveStart/End`）都会读到新段数据。
- 调用方：`PlayerAttackState.StartSegment`（Character 侧），且调用顺序是 `SetAttack(newSeg)` 紧跟 `CloseHitWindow()`。

### ⑥ 性能预算前置

- 预算：换段是低频事件（一段一次），**零分配**。
- 守约：setter 是字段赋值；命中检测仍是 M3 的 `OverlapBoxNonAlloc`（预分配缓冲），不受影响。

### ⑦ 预留复盘接口

- 扩展点：未来技能系统也可以 `SetAttack` 后开窗复用这套近战检测；或扩展为「一段多个命中盒」。
- 排查起点：
  - 「同一段把敌人打了好几下」→ 查该段的 `ActiveStart/End` 是否过宽，或窗口在段内没正确关过。
  - 「第 2 段打不到第 1 段已命中的敌人」→ 查两段之间窗口是否真的关过（去重集没清）。
  - 「某段伤害/命中盒不对」→ 查 `StartSegment` 是否用正确的 `Segments[index]` 调了 `SetAttack`。

---

## 子功能五：PlayerController 的连段接入（_combo 字段 + Awake 预 hash 缓存）

> 文件：`Assets/_Project/Scripts/Character/PlayerController.cs`
> 新增：`_combo` 序列化字段、`_comboStateHashes` 缓存、`BuildComboStateHashes()`、`GetComboStateHash(int)`

### ① 需求拆解与验收标准

要解决的问题：连段驱动需要两样东西从 Controller 拿——① 当前武器的连段表 `ComboDefinition`；② 每段动画状态名对应的 **Animator state hash（int）**，且**绝不能每帧/每次切段都 `StringToHash`**（字符串查找有成本）。

验收标准：
- `_combo` 在 Inspector 可拖入（拖 `SingleTwoHandSword` 连段表）。
- 各段 hash 在 `Awake` **预计算一次**，缓存进 `int[] _comboStateHashes`。
- `GetComboStateHash(index)` 越界/未配置返回 0（不抛异常）。
- 某段动画名为空时 `GameLog.Warn` 告警（便于定位配置漏填）。

### ② 涉及的基础知识和原理

- **`Animator.StringToHash`**：Animator 内部用 int hash 标识 state，不是字符串。`StringToHash("Combo01_...")` 把名字转成那个 int。每次转换都要算 hash——放到每帧/每次切段就是无谓的重复开销。正解是**算一次缓存起来**。
- **为什么在 `Awake` 算**：序列化字段（`_combo`）在 `Awake` 时已注入完毕；`StringToHash` 是静态方法、不需要 Animator 实例，所以 `Awake` 是预计算的最早安全点。M3 里 `SpeedHash/IsGroundedHash` 用 `static readonly` 字段初始化也是同样的「hash 只算一次」思想，这里因为名字来自数据（运行时才知道）所以改成 Awake 建数组。
- **边界落点**：hash 化发生在 **Character 侧**。这是「动画名字符串放 Combat 数据」能成立的关键——Combat 只存中立字符串，碰 Animator 的 `StringToHash` 在允许用 Animator 的 Character 侧做。

### ③ 实现该功能有哪些方案

- A. 每次 CrossFade 用 `CrossFade(string)` 重载，内部每次 `StringToHash`。
- **B. Awake 预 hash 成 `int[]`，CrossFade 用 hash 重载**。
- C. 把 hash 缓存在 `AttackDefinition` 资产上（数据侧持 hash）。

### ④ 方案与技术选择

**选 B**（brainstorm 决策②的落地）：
- vs A：`CrossFade(string)` 每次切段都做字符串 hash；若动画名是动态拼接（`$"Combo0{i}"`）还会产生字符串 GC。预 hash 后切段只传一个 int，**运行期零字符串、零 GC**。
- vs C：把 hash 存进 Combat 的 `AttackDefinition` 等于让 Combat 数据掺入「Animator hash」这种表现概念，且 hash 是 Unity 运行时概念不适合序列化进资产。放 Character 的 `int[]` 缓存最干净。

### ⑤ 架构设计与拆解

- `_combo`（序列化）：数据来源。
- `BuildComboStateHashes()`：`Awake` 调用一次。遍历 `_combo.Segments`，每段读 `AnimationStateName`：空 → 记 0 + `GameLog.Warn`；非空 → `StringToHash` 存入 `_comboStateHashes[i]`。**这里有一处 null 防御风格**（段为空时取 null 名 → 记 0 告警），与状态机里的 null 防御一脉相承。
- `GetComboStateHash(int)`：对外只读访问器，带边界检查（数组空/越界 → 0）。
- 消费方：`PlayerAttackState.StartSegment` 调 `GetComboStateHash(index)` 拿 hash 去 CrossFade；`PlayerAttackState` 还经 `Combo` 属性读段数据。

依赖方向：Character 依赖 Combat（读 `ComboDefinition`/`AttackDefinition`）——合规的既有方向。

### ⑥ 性能预算前置

- 预算：**每帧零字符串查找、零 GC**；`StringToHash` 只允许在初始化期发生。
- 守约：N 次 `StringToHash`（N=段数）集中在 `Awake` 跑一次；运行期 `GetComboStateHash` 是 O(1) 带界检查的数组读。`BuildComboStateHashes` 里 `new int[count]` 是一次性初始化分配，不在热路径。

### ⑦ 预留复盘接口

- 扩展点：
  - **换武器** → 切 `_combo` 后需要**重建 hash 缓存**。当前 `BuildComboStateHashes` 只在 `Awake` 调；未来加换武器功能时，把它提成一个可在换武器时调用的公共方法即可（这就是预留的检查点）。
- 排查起点：
  - 「动画不切换」→ 看 Console 是否有 `连段第 i 段 AnimationStateName 为空` 告警；若无告警但仍不切，说明名字非空但与 Animator state 名不匹配（hash 算出来了但 Animator 找不到对应 state，CrossFade 静默失败）。
  - 「切段越界」→ `GetComboStateHash` 返回 0，CrossFade(0) 静默不切，是兜底而非崩溃。

---

## 子功能六：PlayerAttackState 重写（连段驱动主体）

> 文件：`Assets/_Project/Scripts/Character/States/PlayerAttackState.cs`
> （配套：`PlayerStateBase` 移除了不再使用的 `AttackHash`——攻击不再用 SetTrigger）

### ① 需求拆解与验收标准

要解决的问题：把 M3 的「单段攻击状态」升级为「驱动整套连段的状态」。它要负责：段计数推进、切段动画、按段开关命中、自然收招、被打断时的清理，以及配置漏填时的安全退出。

验收标准（对应 Play 模式 6 项核查）：
1. 单次按键 → 只打第 0 段，正常收招回移动态。
2. 在每段连段窗口内按键 → 依次推进 0→1→…→末段，每段对敌人命中一次。
3. 窗口外按键（过早/过晚）→ 不推进，自然收招。
4. 连段结束后，残留缓冲输入**不会**自动触发下一次普攻。
5. 连段中跳跃/走下平台 → 中断并把 `comboIndex` 归零，下次从第 0 段起。
6. （可选）攻击中 `Update` 0 GC Alloc。

### ② 涉及的基础知识和原理

- **状态机生命周期 Enter/Update/Exit**：连段被设计成「一个 `PlayerAttackState` 实例从头驱动到尾」，段切换在 `Update` 内部完成、**不重进状态**。所以 `comboIndex` 是状态的内部字段，`Exit()` 是它唯一的归零出口。
- **CrossFade 与 SetTrigger 的本质区别**：
  - `SetTrigger("attack")` 是给 Animator 设一个触发器，**实际切哪个 state、何时切，由 Animator Controller 里连好的 Transition 决定**——连段要在 7 个 clip 间精确跳转，靠两两连线既繁琐又脆。
  - `CrossFadeInFixedTime(hash, duration, layer)` 是**代码直接命令 Animator 淡入到指定 state**，不依赖 Transition 连线、时机完全由代码掌控。连段需要「我说切哪段就切哪段」，所以全段（含第 0 段）都用 CrossFade，`attack` trigger 因此废弃（`AttackHash` 已从基类移除）。
- **`IsInTransition` + `normalizedTime` 的配合**：CrossFade 过渡期间，`GetCurrentAnimatorStateInfo(0).normalizedTime` 读到的还是「淡出中的旧 state」的进度，不代表新段。所以 `HandleAttackWindow` 和 `CheckCombo` 都先 `if (IsInTransition(0)) return/关窗`——**过渡期不读进度、不判定**，避免用错误的时间去开窗或推进。
- **输入缓冲复用**：连段「续段输入」和普攻「起手输入」其实是同一个动作——「攻击键被按下」。所以复用同一个 `AttackBufferCounter`（M3 已有，Controller 每帧递减），区别只在「当前在哪个状态」这个上下文。
- **Enter 内调用 ChangeState 的重入**：配置缺失时 `Enter()` 会调 `TransitionToMovement()`→`ChangeState(...)`→（本状态 `Exit()` + 新状态 `Enter()`）再返回。此刻 `comboIndex`/`buffer` 都已是 0，`Exit()` 是无害的重复写，且 `CloseHitWindow` 幂等——重入安全。

### ③ 实现该功能有哪些方案

段承载方式：
- **A. 一个状态实例驱动整套连段，段切换在 Update 内部完成。**
- B. 每段重进一次 `AttackState`（`comboIndex` 外置到 Controller）。

输入缓冲：
- **A. 复用 `AttackBufferCounter`。**
- B. 新增独立的连段缓冲计时器。

归零责任方：
- **A. `comboIndex` 在状态内部，`Exit()` 单点归零。**
- B. 分散在各转移点手动清。

### ④ 方案与技术选择

**段承载选 A**（决策③）：一个实例承载 → `comboIndex` 自然限定在「停留于攻击态的时间」内，`Exit()` 成为唯一归零点。B 把 `comboIndex` 外置、每段重进，归零责任分散，正是「中断后没清零」失败模式的温床。

**输入缓冲选 A**（决策①）：复用单一缓冲、最小状态。代价是必须在 `Exit()` 清 `AttackBufferCounter` 防泄漏——见⑤的失败模式防御。B 的独立缓冲隔离更彻底但状态翻倍，本轮不需要。

**归零选 A**（决策③）：`Exit()` 单点。配合 `PlayerStateMachine.ChangeState` 必先调 `CurrentState.Exit()` 的既有机制，任何离开攻击态的路径（自然收招、跳跃、未来受击打断）都强制走 `Exit()`，结构性地保证归零。

### ⑤ 架构设计与拆解

方法职责划分：
- `Enter()`：`comboIndex=0` + 消耗起手输入；配置缺失（`Combo==null` 或 0 段）→ 告警 + 安全退出；否则 `StartSegment(0)`。
- `StartSegment(i)`：换段数据（`SetAttack`）+ 关窗（重置 per-swing 去重）+ `CrossFadeInFixedTime(GetComboStateHash(i))`。
- `Update()`：`HandleGravity`（贴地）→ `HandleMovement`（锁水平、仍调 `CC.Move` 保持接地检测）→ `HandleAttackWindow`（按段开关命中，复用 M3）→ `CheckCombo`（判定推进/收招）。
- `CheckCombo()`：过渡期早退 → 取本段 `seg`（**null 防御**）→ `ComboResolver.Resolve` → `switch`：`Advance` 则 `comboIndex++` + 消耗 buffer + `StartSegment(i+1)`；`End` 则收招回移动态；`Continue` 不动。
- `Exit()`：`comboIndex=0` + `AttackBufferCounter=0` + 关窗。

**五个失败模式各自的防御**（这是本子功能的设计核心）：
1. **同帧重复推进**：`Advance` 后立即把 `AttackBufferCounter=0`（`hasBuffer` 变 false）+ `StartSegment` 触发 CrossFade（下一帧 `IsInTransition` 为真 → `CheckCombo` 早退）+ 一帧只调一次 `CheckCombo`。三重保证。
2. **中断后没清零**：`Exit()` 单点归零，`ChangeState` 必经 `Exit()`。
3. **缓冲泄漏导致误触发下一次普攻**：`Exit()` 清 `AttackBufferCounter`，使 `GroundedState` 不会在连段刚结束时凭残留缓冲又进攻击态。
4. **CrossFade 用错 hash 导致不切换**：空名在 `BuildComboStateHashes` 记 0 并告警；`CrossFade(0)` 静默不切而非崩溃（可接受的降级）。
5. **段数据为空（Inspector 漏填）**：`CheckCombo` 取 `seg` 后 `if (seg == null)` → 告警 + 安全退出，而不是 `seg.ComboInputStart` 触发 NRE。

> **实现过程中的真实偏离（代码是唯一真相）**：第 5 项的 `seg == null` 防御**不在最初的 write-plan 里**，是 review 循环中补的——原计划 `CheckCombo` 直接 `seg.ComboInputStart`，被指出 Inspector 漏填某段时会每帧 NRE，遂加上与 `Enter`/`HandleAttackWindow`/`BuildComboStateHashes` 一致的 null 防御。

依赖：本状态依赖 `PlayerController`（拿 `Combo`/`GetComboStateHash`/`MeleeHitDetector`/`Animator`/计时器/各状态引用）、`MeleeHitDetector`（`SetAttack`/开关窗）、`ComboResolver`（判定）、`ComboDefinition`/`AttackDefinition`（数据）。它是连段系统里**唯一碰 Animator 的地方**——边界守在这里。

### ⑥ 性能预算前置

- 预算：`Update`（攻击中每帧）**0 GC Alloc、无 LINQ、无装箱**。
- 守约：`Vector3` 运算是栈上 struct；`ComboResolver.Resolve` 返回 `enum:byte` 不装箱；`switch` 无分配。唯一的字符串插值在 `GameLog.Warn($"...")`——但只在「段为空」的配置错误路径上、且触发后立即 `TransitionToMovement` 退出（不会每帧循环），稳态路径不经过它；加之 `GameLog` 在非开发构建里整条调用被 IL 剥离。

### ⑦ 预留复盘接口

- 扩展点：
  - **受击/死亡打断真正接入**：本轮只预留——外部只要 `ChangeState(到受击/其它状态)`，`Exit()` 自动完成归零与关窗，无需在这里写打断逻辑。
  - **分支连段**：把 `comboIndex++` 的线性推进，换成「按输入方向选下一段」；`CheckCombo` 的 `Advance` 分支和 `ComboResolver` 协同改动，`StartSegment` 不变。
  - **`StartSegment` 的下一段空值**：当前 `StartSegment` 不对「下一段为空」单独防御——但这是安全的：`SetAttack(null)` 只是赋值、`CrossFade(0)` 静默不切、`HandleAttackWindow` 的 `def==null` 早退不会开窗，随后下一帧 `CheckCombo` 的 null 防御接管退出。即「晚一帧、但不崩」。若未来要「立即退出」，可在 `StartSegment` 补同样的 null 检查。
- 排查起点（按现象定位）：
  - 连段推进/收招时机不对 → 先去 **`ComboResolver` 单测**复现（子功能三）。
  - 动画不切/切错 → 查 `AnimationStateName` 与 Animator state 名、`BuildComboStateHashes` 告警（子功能五）。
  - 连段不归零/打断后还在连 → 确认离开路径是否真的走了 `ChangeState`（从而经 `Exit()`）。
  - 连段后冒出一次多余普攻 → 查 `Exit()` 是否清了 `AttackBufferCounter`。
  - 某段重复命中 / 跨段打不到 → 查 `StartSegment` 的关窗与新段 `ActiveStart` 重新开窗（子功能四）。

---

## 附：连段系统与 M3 的关系（一句话收尾）

连段系统**没有重写任何战斗结算**——命中检测、`DamageRequest`、`DamagePipeline`、`HealthComponent`、事件派发全部是 M3 原物。连段只做了一件事：**在 M3 的「单次攻击」外面套了一层"出招序列调度"**——用纯数据描述序列、用纯函数判定推进、用一个状态实例驱动动画与换段。这正是分层架构的红利：上层能力（连段）叠加时，下层闭环（命中→扣血→死亡）零改动。

> 本文描述的是连段系统的**线性核心循环**。分支连段树、多武器连段表、受击/死亡真正打断连段，均为**有意预留、本轮未实现**的扩展点。
