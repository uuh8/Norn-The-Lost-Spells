# M3 战斗系统核心循环 — 设计解析

> 目标读者：有 C# 基础、**零战斗系统经验**的开发者。
> 本文讲解 M3 阶段实现的「打到 → 扣血 → 死亡」核心循环：**实现了什么、为什么这样设计、设计是怎么落地的**。
>
> 阅读顺序建议：先看下面的「全局心智模型」，建立整条数据流的直觉；再按子功能逐个精读。每个子功能都做完整的六维分析，彼此独立，可单独跳读。

---

## 全局心智模型

一次攻击在引擎里其实是一连串**纯数据的传递**，而不是「一个对象去打另一个对象」。把它想成一条流水线：

```
   武器挥动（动画/连段系统打开"激活窗口"）
        │
        ▼
 ┌─────────────────────┐
 │  MeleeHitDetector    │  ① 命中判定：窗口期内每帧用 OverlapBox 扫描重叠的碰撞体
 │  （攻击者侧）         │     过滤掉自己/队友/已死/本次已打过的目标
 └─────────┬───────────┘
           │ 构造 DamageRequest（一次命中的"意图快照"，纯 struct）
           ▼
 ┌─────────────────────┐
 │  IDamageable         │  ② 受击契约：目标只要实现这个接口就能"挨打"
 │  .ReceiveHit(req)    │     攻击方不需要知道目标到底是谁
 └─────────┬───────────┘
           │ 目标 = HealthComponent
           ▼
 ┌─────────────────────┐
 │  DamagePipeline      │  ③ 纯函数结算：BaseAmount + 目标防御档案 → 最终伤害
 │  .Resolve(req, def)  │     无副作用、可单元测试
 └─────────┬───────────┘
           │ DamageResult（结算结果，纯 struct）
           ▼
 ┌─────────────────────┐
 │  HealthComponent     │  ④ 扣血 + 钳制；血量归零判定死亡
 │  扣血、判死          │
 └─────────┬───────────┘
           │ 同帧 Publish
           ▼
 ┌─────────────────────┐
 │  EventBus<T>         │  ⑥ 事件广播：DamageReceivedEvent / DeathEvent
 │  （Game.Core）       │     表现层（VFX/UI/音效）订阅消费，逻辑层对它们一无所知
 └─────────────────────┘
```

**两个最重要的设计直觉，先记住：**

1. **数据在流动，对象不互相持有。** 攻击者把「这一拳的意图」打包成一个值（`DamageRequest`）丢出去，丢出去之后攻击者哪怕被销毁也不影响结算。整条链路传的是**值的快照**，不是对象引用。
2. **逻辑和表现彻底分家。** 战斗逻辑（扣血、判死）只负责算账，算完往 `EventBus` 上喊一嗓子。谁来播特效、谁来弹血条、谁来放音效——逻辑层一概不知道。这是 `Game.Combat` 这个模块**不允许**引用 `VFX/UI/AudioSource` 的根本原因。

下面六个子功能，就是把这条流水线拆开逐段讲。

---

## 子功能一：伤害数据契约（`DamageType` / `DamageRequest` / `DamageResult` / `DefenseProfile`）

> 文件：`DamageType.cs`、`DamageRequest.cs`、`DamageResult.cs`、`DefenseProfile.cs`

这是整套系统的「词汇表」——所有模块都靠这几个数据类型对话。先讲它，因为后面每个子功能都要用到。

### ① 实现了什么效果/功能

定义了四个**纯数据**类型，作为战斗各环节之间传递信息的标准容器：

- `DamageType`：伤害类型枚举——物理 / 魔法 / 真实（True 无视防御）。
- `DamageRequest`：**一次命中的意图快照**。攻击者是谁、属于哪个阵营、基础伤害多少、什么类型、打在哪个点、朝哪个方向。
- `DamageResult`：**结算之后的结果**。最终伤害值、伤害类型、是否被减伤过。
- `DefenseProfile`：**目标的防御档案**。护甲、魔抗（本轮只留字段，还没套公式）。

### ② 方案选择（最终方案 + 备选 + trade-off）

**最终方案：`DamageRequest` / `DamageResult` 用 `readonly struct`，`DefenseProfile` 用普通可变 `struct`。**

| 维度 | 选 `readonly struct` | 备选 A：`class` | 备选 B：直接传一堆参数 |
|------|---------------------|----------------|----------------------|
| 内存 | 栈上传值，**零堆分配** | 每次 new 都在堆上分配，触发 GC | 零分配但签名爆炸 |
| 不可变性 | `readonly` 字段，构造后不可改 | 任何人都能改字段 | 无封装 |
| 传递语义 | 值拷贝，天然快照 | 引用，多处共享同一对象有别名风险 | — |
| 可读性 | 一个名字代表一组相关数据 | 同左 | 参数列表 10 个，调用处难读 |

**为什么 `DamageRequest`/`DamageResult` 要 `readonly`，而 `DefenseProfile` 不要？**
- 前两者是**「在流水线里穿行的消息」**——消息一旦发出就不该被中途篡改，`readonly` 用编译器强制保证这一点。
- `DefenseProfile` 是**「目标身上的一份可被修改的状态」**——未来 Buff/Debuff 要动态改护甲（如「破甲」减 Armor），所以它必须可变。

**trade-off 依据**：struct 适合「小、短命、值语义」的数据。这几个类型字段都不多（最多 6 个），生命周期极短（一次命中算完就丢），且我们要的就是「拷贝即快照」的值语义——完全命中 struct 的适用场景。代价是 struct 过大时值拷贝成本会上升，但这里都很小，不构成问题。

### ③ 技术点拆解（技术点 → 底层原理）

**技术点 1：`struct` vs `class` 的内存本质。**
C# 里 `class` 是引用类型，`new` 一个对象会在**托管堆**上分配内存，由 GC 负责回收；`struct` 是值类型，作为**局部变量/参数**时活在**栈**上或直接内联进容器，方法返回即弹栈，不经过 GC。战斗每帧可能产生多次命中，如果每次命中都 `new` 一个 class，就会持续往堆上丢垃圾，攒到一定量触发 GC，造成卡顿（掉帧）。用 struct 从根上避免了这件事。

**技术点 2：`readonly struct` 的「快照」语义为什么能防 bug。**
**当你把一个 struct 作为参数传给方法，传的是一份拷贝**。所以 `DamageRequest` 一旦构造出来传出去，接收方拿到的是独立副本，攻击者那边后续发生任何变化（移动、销毁、改属性）都不会回头影响这份请求。`readonly` 再加一道保险：连接收方自己都改不了这份数据。这两点合起来，就是「攻击者打出去之后被销毁，伤害照样正确结算」这一失败模式的防御基础（详见子功能四的 ④）。

**技术点 3：枚举用 `: byte` 显式指定底层类型。**
`enum DamageType : byte`——默认枚举底层是 `int`（4 字节），这里指定为 `byte`（1 字节）。伤害类型撑死几种，1 字节够用。在大量 `DamageRequest` 流转、以及未来网络同步/序列化时，更小的类型意味着更紧凑的内存和带宽。`AttackerTeam`、`HealthComponent._teamId` 同理用 `byte`。

### ④ 踩坑与设计决策（失败模式防御）

- **攻击者中途销毁 → 空引用**：`DamageRequest` 存的是 `int AttackerId`（`GetInstanceID()` 的结果）和 `byte AttackerTeam`，**不是** `GameObject`/`Transform` 引用。结算时不需要回头访问攻击者对象，所以攻击者哪怕已经 `Destroy`，结算逻辑也不会碰到悬空引用。这是「值快照」最重要的实战价值。
- **请求被中途篡改 → 结算不一致**：`readonly` 字段从编译期杜绝。
- **防御字段提前固化公式 → 返工**：`DefenseProfile` 本轮只留 `Armor`/`MagicResist` 字段、不套公式（见子功能三），避免在数值方案没定型前把公式写死。

### ⑤ 性能审查（GC / 装箱 / 热路径）

- **GC Alloc**：四个类型全是 struct，在命中流程里全程栈上传值，**零堆分配**。✅
- **装箱风险点**：struct 一旦被当成 `object` 或非泛型接口使用，就会被「装箱」——CLR 在堆上包一个盒子，又回到了 GC 问题。本设计里这些 struct 始终以**强类型**传递（`in DamageRequest`、`DamageResult` 返回值、`EventBus<T>` 的泛型约束），没有任何地方把它们转成 `object`，因此无装箱。✅
- `in` 参数（见子功能三）进一步避免了 struct 传参时的值拷贝开销。

### ⑥ 改进反思（局限 + 扩展缝 + 可复用 pattern）

- **当前局限**：`DamageResult.WasMitigated` 目前恒为 `false`（还没有真正的减伤逻辑）；`DefenseProfile` 字段是空壳。这是本轮「先搭骨架、不套数值」的有意取舍。
- **扩展缝**：
  - 加暴击：给 `DamageRequest` 加 `bool IsCrit` 或 `float CritMultiplier`，结算时读取。
  - 加元素/状态：`DamageType` 扩枚举值即可。
  - Buff/Debuff 改防御：直接改目标的 `DefenseProfile` 实例字段（它特意设计成可变）。
- **可复用 pattern**：**「Request/Result 值对象对」**——用一个不可变 struct 表达「意图」，另一个 struct 表达「结果」，中间夹一个纯函数。这个模式在技能结算、AI 决策、网络指令等场景都能照搬。

---

## 子功能二：IDamageable 受击契约

> 文件：`IDamageable.cs`

### ① 实现了什么效果/功能

定义了一个接口 `IDamageable`，约定「**任何能挨打的东西**长什么样」：

```csharp
public interface IDamageable
{
    byte TeamId { get; }              // 阵营，用于敌我识别
    bool IsAlive { get; }            // 是否还活着
    void ReceiveHit(in DamageRequest req); // 受理一次命中
}
```

命中判定方只跟这个接口打交道，不关心目标具体是敌人、可破坏的木箱、还是 Boss 的某个部位。

### ② 方案选择（最终方案 + 备选 + trade-off）

**最终方案：用接口 `IDamageable` 解耦「打人的」和「挨打的」。**

| 维度 | 接口 `IDamageable`（选中） | 备选 A：命中判定直接拿 `HealthComponent` | 备选 B：发个全局事件让目标自己处理 |
|------|--------------------------|----------------------------------------|----------------------------------|
| 耦合 | 攻击方只依赖抽象 | 攻击方写死具体类，无法换实现 | 最松，但同帧性、顺序难保证 |
| 扩展 | 木箱/可破坏物/多段血只要实现接口即可挨打 | 一切都得是 HealthComponent | 同 A 的问题，且调试困难 |
| 同帧确定性 | `ReceiveHit` 是直接方法调用，**同帧返回** | 同左 | 事件可能异步/乱序 |
| 性能 | 一次接口虚调用，开销极小 | 直接调用略快 | 事件分发有额外开销 |

**trade-off 依据**：任务硬约束要求「命中/伤害事件同帧派发」，所以受击不能走「发个事件等目标异步响应」（备选 B 否决）。而把命中判定写死成依赖 `HealthComponent` 又违背「数据驱动 + 可扩展」原则（备选 A 否决）。接口是「同帧直接调用」与「面向抽象、可扩展」的唯一交集。

### ③ 技术点拆解（技术点 → 底层原理）

**技术点 1：接口 = 面向抽象编程。**
`MeleeHitDetector` 里写的是 `IDamageable target`，不是 `HealthComponent target`。这意味着**编译期**它只知道「这东西有 TeamId / IsAlive / ReceiveHit」，不知道也不需要知道具体是什么。明天你新增一个「可破坏的酒桶」类，只要它 `implements IDamageable`，现有的命中判定一行不改就能打碎它。这就是**开闭原则**（对扩展开放、对修改关闭）在这里的落地。

**技术点 2：契约里塞 `TeamId` / `IsAlive`，是为了让攻击方「不结算就能筛掉」。**
命中判定每帧会扫到一堆碰撞体，其中大部分该被跳过（自己、队友、已死的）。把 `TeamId`/`IsAlive` 放进接口，攻击方就能在**调用 `ReceiveHit` 之前**用廉价的属性读取先过滤，避免对不该打的目标白跑一遍结算流程。这是「接口该暴露什么」的一个典型权衡：暴露刚好够过滤的只读信息，但不暴露内部状态（比如不暴露 `CurrentHp` 的 setter）。

**技术点 3：`ReceiveHit(in DamageRequest req)` 把「怎么结算」的主动权交给目标自己。**
注意是目标自己实现 `ReceiveHit`，在里面调用 `DamagePipeline` 用**自己的**防御档案算账（详见子功能四）。攻击方只管「我打了你，这是我的攻击意图」，不替目标决定「你该掉多少血」。这叫**目标侧结算（target-side resolution）**——把减伤逻辑留在最了解自身防御状态的对象里。

### ④ 踩坑与设计决策（失败模式防御）

- **同帧性要求**：`ReceiveHit` 是普通方法调用，调用即同帧执行完毕、同帧把事件发出去，不存在跨帧延迟。如果当初设计成「攻击方发事件 → 目标下一帧响应」，就会出现「这一帧打了，下一帧才掉血」的延迟感和时序 bug。
- **死亡目标重复受击**：契约里有 `IsAlive`，攻击方可据此跳过死人；目标实现里 `ReceiveHit` 第一行也会再查一次 `IsAlive`（双保险，见子功能四 ④）。

### ⑤ 性能审查（GC / 装箱 / 热路径）

- **GC Alloc**：接口本身不分配。✅
- **装箱陷阱（关键）**：`HealthComponent` 是 `class`（引用类型），它实现 `IDamageable`——引用类型转接口**不装箱**，安全。⚠️ 但要警惕：如果将来让某个 **struct** 去实现 `IDamageable`，那么把它当 `IDamageable` 用就会装箱。这也是为什么本轮的可受击者用 class 而非 struct。
- **接口调用开销**：`ReceiveHit` 是一次虚方法/接口分派，比直接调用稍慢，但量级极小，在「一次挥砍命中个位数目标」的频率下完全可忽略。

### ⑥ 改进反思（局限 + 扩展缝 + 可复用 pattern）

- **当前局限**：契约较薄，只覆盖「受伤」。治疗、护盾、霸体等还没有对应方法。
- **扩展缝**：
  - 治疗：未来可加 `IHealable`（CLAUDE.md 已把它列为命名示例），与 `IDamageable` 平行。
  - 多段血/部位破坏：Boss 的不同部位各挂一个 `IDamageable` 实现，命中判定天然支持。
- **可复用 pattern**：**「能力接口（capability interface）」**——用一个小接口表达「这东西能被 XX」，而不是用一个大基类。组合优于继承，木箱能被打但不能被治疗，就只实现 `IDamageable`。

---

## 子功能三：DamagePipeline 纯函数结算

> 文件：`DamagePipeline.cs`

### ① 实现了什么效果/功能

一个**静态纯函数** `Resolve`，输入「攻击意图 + 目标防御档案」，输出「最终伤害结果」：

```csharp
public static DamageResult Resolve(in DamageRequest req, in DefenseProfile def)
```

本轮规则：
- `True` 类型无视防御，直接等于基础伤害。
- `Physical` / `Magical` 读取防御档案，但**暂时 passthrough**（不减伤，公式留空位）。
- 最终伤害**钳制为非负**（不会出现负伤害＝回血）。

### ② 方案选择（最终方案 + 备选 + trade-off）

**最终方案：把伤害计算抽成「不依赖 MonoBehaviour 的静态纯函数」。**

| 维度 | 静态纯函数（选中） | 备选 A：写在 HealthComponent 方法里 | 备选 B：做成可配置的策略对象 |
|------|-------------------|-----------------------------------|---------------------------|
| 可测试性 | EditMode 单测直接调，**无需进 Play、无需建 GameObject** | 必须实例化 MonoBehaviour 才能测 | 可测但要搭脚手架 |
| 复用 | 近战、技能、DoT 都能调同一个 | 绑死在血量组件上 | 可复用 |
| 复杂度 | 最简单 | 简单但耦合 | 偏重，本轮用不上 |
| 灵活度 | 改公式＝改一处函数 | 同左 | 运行时可换策略，但 YAGNI |

**trade-off 依据**：任务明确要求「伤害管道是纯函数、可单元测试、本轮不写数值公式」。纯函数是可测试性的最优解——它没有任何外部状态依赖，给定相同输入永远得到相同输出，测试只需「输入 → 断言输出」。备选 B（策略模式）能在运行时切换公式，但本轮没有这个需求，引入它就是过度设计（YAGNI）。

### ③ 技术点拆解（技术点 → 底层原理）

**技术点 1：什么是「纯函数」，为什么它好测。**
纯函数 = ①输出只由输入决定 ②没有副作用（不改全局状态、不碰文件/网络/单例）。`Resolve` 只读 `req` 和 `def`、只返回 `DamageResult`，不碰任何外部东西。这带来一个巨大好处：**测试它不需要 Unity 运行时**。你不用建场景、不用进 Play、不用实例化 GameObject，直接 `Resolve(造一个请求, 造一个档案)` 然后断言结果。`DamagePipelineTests.cs` 里 5 个测试 0.023 秒跑完，就是这个原因。

**技术点 2：`static class` + `static` 方法的含义。**
`DamagePipeline` 是静态类，不能 `new`，没有实例状态。这恰好匹配「纯函数没有状态」的语义——它就是一个「算式」，不是一个「东西」。调用处 `DamagePipeline.Resolve(...)` 直接用，无需持有实例。

**技术点 3：`in` 参数修饰符。**
`Resolve(in DamageRequest req, in DefenseProfile def)` 里的 `in` 表示「按引用传入，但只读」。
- 不加 `in`：struct 传参会**整份拷贝**到方法内。
- 加 `in`：只传一个引用（地址），不拷贝整个 struct，同时编译器保证方法内不能修改它。
对小 struct 收益不大，但这是一个一致的、零成本的好习惯，struct 越大收益越明显。

**技术点 4：非负钳制 `if (final < 0f) final = 0f`。**
未来 `Physical` 公式若写成 `BaseAmount - Armor`，当护甲超过伤害时结果会变负数，负伤害＝给目标回血，这是隐蔽的逻辑 bug。提前在管道出口钳制，等于给「未来还没写的公式」预设了一道安全网。

### ④ 踩坑与设计决策（失败模式防御）

- **负伤害变回血**：出口 `final < 0 → 0` 钳制，防御的是「未来公式」可能产生的负值。
- **过早固化数值**：Physical/Magical 留 passthrough + 注释占位（`// final = req.BaseAmount - def.Armor`），把「填公式」这件事和「搭架构」解耦。架构现在就能用、能测；数值方案定型后只改这两行。
- **True 漏掉防御处理**：显式 `case True: final = BaseAmount`，从代码上锁死「真实伤害无视防御」的规则，而不是靠「碰巧 passthrough 也等于 BaseAmount」。意图明确，将来给 Physical/Magical 加公式时不会误伤 True。

### ⑤ 性能审查（GC / 装箱 / 热路径）

- **GC Alloc**：函数体内无 `new`、无集合、无闭包；输入 `in` 引用、输出 struct 值。**零分配**。✅
- **装箱**：全程强类型，无。✅
- **热路径**：`switch` 在枚举上是高效跳转；整个函数是纯算术 + 一次比较，开销可忽略。✅

### ⑥ 改进反思（局限 + 扩展缝 + 可复用 pattern）

- **当前局限**：没有真实减伤公式、没有暴击、没有随机浮动、`WasMitigated` 恒 false。
- **扩展缝**：
  - 填公式：改 `case Physical/Magical` 两行即可，签名和调用方都不动。
  - 加暴击/浮动：扩 `DamageRequest` 字段，在 `Resolve` 里读取。
  - 想运行时换公式：再升级为策略对象（当前结构不阻碍这条路）。
- **可复用 pattern**：**「纯函数核心 + 薄壳编排」**——把会变、需要反复验证的**计算**抽成纯函数密集测试，把碰 Unity 的**副作用**（扣血、发事件）留在外层薄薄的 MonoBehaviour 里。计算逻辑因此可以脱离引擎独立演进和测试。

---

## 子功能四：HealthComponent 生命值与受击编排

> 文件：`HealthComponent.cs`

### ① 实现了什么效果/功能

挂在「能挨打的 GameObject」上的 MonoBehaviour，它是 `IDamageable` 的具体实现，负责：
- 持有血量（`_maxHp` / `_currentHp`）、阵营、防御档案。
- 受击时：调纯函数结算 → 扣血 → 钳制 → **同帧**发「受击事件」→ 若血量归零再**同帧**发「死亡事件」。

它是子功能二、三、六的「编排者（orchestrator）」——把契约、结算、事件三者串起来。

### ② 方案选择（最终方案 + 备选 + trade-off）

**最终方案：目标侧结算 + 编排集中在受击方法里 + 数据走序列化字段。**

| 决策点 | 选中方案 | 备选 | trade-off 依据 |
|--------|---------|------|--------------|
| 谁来结算 | 目标自己（target-side） | 攻击方算好再扣 | 目标最了解自身防御/Buff，攻击方不该越权；也便于未来「受击时触发护盾」等目标侧能力 |
| 死亡判定放哪 | 受击后立即在同方法内判 | 单独 Update 轮询血量 | 同帧性要求；轮询会延迟一帧且浪费每帧开销 |
| 数据来源 | `[SerializeField]` 在 Inspector 配 | 硬编码 / 代码 new | 数据驱动原则，策划可在编辑器调参 |

### ③ 技术点拆解（技术点 → 底层原理）

**技术点 1：`Awake` 里做初始化，而不是字段直接赋值。**
```csharp
private void Awake()
{
    _currentHp = _maxHp;
    _id = gameObject.GetInstanceID();
}
```
- `_currentHp = _maxHp`：`_maxHp` 是 Inspector 配的，要等对象实例化、序列化值注入之后才有正确值，所以「满血初始化」必须在 `Awake`（此时序列化值已就位）里做，不能写成字段初始值。
- `_id = gameObject.GetInstanceID()`：`GetInstanceID()` 返回该对象在本次运行中**全局唯一**的整数 ID。缓存它，是为了发事件时用一个轻量的 int 标识「这是谁」，而不是把整个 GameObject 引用塞进事件（事件要保持纯数据、可跨模块、能被攻击方用 `AttackerId` 对上号）。

**技术点 2：`ReceiveHit` 的执行顺序就是「核心循环」本体。**
```
1. if (!IsAlive) return;                       // 死人不再挨打
2. result = DamagePipeline.Resolve(req, 防御); // 算账（纯函数）
3. _currentHp -= result.Final; 钳制到 ≥0       // 扣血
4. Publish(DamageReceivedEvent{...})           // 同帧广播"挨打了"
5. if (_currentHp <= 0) Publish(DeathEvent{...})// 同帧广播"死了"
```
这五步严格顺序执行、同帧完成——这正是「打到 → 扣血 → 死亡」的代码化。

**技术点 3：为什么「受击事件总是发，死亡事件条件发」。**
每次挨打都要让表现层有反馈（飘血字、受击闪白），所以 `DamageReceivedEvent` 无条件发。死亡是特殊时刻（播死亡动画、给经验、清理 AI），只在血量真正归零那一刻发一次 `DeathEvent`。两个事件分开，让订阅方各取所需。

**技术点 4：`in _defenseProfile` 把自己的防御档案按只读引用喂给纯函数。**
目标侧结算的落地：`HealthComponent` 把**自己的** `_defenseProfile` 传进 `Resolve`。同一个 `DamageRequest` 打到护甲不同的两个目标，结果天然不同——因为防御来自目标自身。

### ④ 踩坑与设计决策（失败模式防御）

- **重复结算 / 死后再挨打**：`ReceiveHit` 第一行 `if (!IsAlive) return`。设想同一帧两段命中先后到达，第一段把血打到 0 并发了 `DeathEvent`；第二段进来时 `IsAlive` 已为 false，直接 return，**不会发第二次 DeathEvent、不会把血扣成负数**。这是「重复死亡事件」失败模式的核心防御。
- **血量变负 / 显示异常**：`if (_currentHp < 0f) _currentHp = 0f` 钳制，保证血条/数值永远落在 `[0, max]`。
- **攻击者已销毁**：本组件全程只用 `req.AttackerId`（int），不回访攻击者对象，承接了子功能一的快照防御。
- **初始化时序**：满血在 `Awake` 设，避免「序列化值还没注入就读 `_maxHp`」拿到 0 的时序坑。

### ⑤ 性能审查（GC / 装箱 / 热路径）

- **GC Alloc**：`ReceiveHit` 内无 `new` 堆对象。两个事件用 `new DamageReceivedEvent{...}` 看着像分配，但它们是 **struct 字面量**，在栈上构造、按值传进 `Publish`，**不进堆**。✅
- **装箱**：事件以 `EventBus<DamageReceivedEvent>` 的强类型泛型传递，无装箱（见子功能六）。✅
- **热路径说明**：`ReceiveHit` 不在 `Update` 里被每帧调用，而是命中时才触发，频率低；即便如此它仍做到了零分配，符合 hot-path 规范。

### ⑥ 改进反思（局限 + 扩展缝 + 可复用 pattern）

- **当前局限**：没有无敌帧/受击硬直、没有护盾层、没有「致命一击免疫」之类机制；死亡后 GameObject 的清理（禁用碰撞、播放解体）交给事件订阅方，本组件不负责。
- **扩展缝**：
  - 加治疗：实现 `IHealable.Heal()`，复用同一套钳制 + 事件模式。
  - 加无敌帧：在 `ReceiveHit` 开头加 `if (_invulnerable) return`。
  - 受击触发反击/护盾：就在 `ReceiveHit` 里、结算前后插逻辑（目标侧结算的红利）。
- **可复用 pattern**：**「薄编排器（orchestrator）」**——MonoBehaviour 自己不写算法，只负责「按正确顺序把纯函数和事件串起来」。算法在纯函数里、通信在事件里，编排器本身很薄、易读、易测。

---

## 子功能五：MeleeHitDetector 命中判定

> 文件：`MeleeHitDetector.cs`、`AttackDefinition.cs`

这是本阶段**技术含量最高、权衡最深**的部分——「怎么判断武器打到了谁」。

### ① 实现了什么效果/功能

挂在武器（或攻击者）上的组件。在「攻击激活窗口」开启期间，每帧在武器挂点处做一次盒形重叠检测，找出所有接触到的可受击目标，过滤掉不该打的，给每个合法目标提交一次伤害请求；**同一次挥砍对同一目标只结算一次**。

配套的 `AttackDefinition` 是一个 ScriptableObject，把「这一招」的数据（伤害值、伤害类型、命中盒尺寸、激活时间区间）从代码里抽出来，可在编辑器配置、被多个系统复用。

### ② 方案选择（最终方案 + 备选 + trade-off）

**核心抉择：用什么方式做命中检测？**

| 方案 | 原理 | 优点 | 缺点 | 是否选中 |
|------|------|------|------|--------|
| **OverlapBox 主动扫描**（选中） | 激活帧主动查询「这个盒子里有谁」 | 攻击方完全掌控时机；和动画激活帧天然对齐；无需给武器加刚体/碰撞体 | 高速移动可能穿透（tunneling）；需自己做去重 | ✅ |
| OnTriggerEnter 被动触发 | 给武器挂 Trigger 碰撞体，靠物理引擎回调 | 写起来少 | 时机由物理引擎决定、和激活帧难对齐；进入/停留语义混乱；需要刚体 | ✗ |
| Raycast/SphereCast 射线 | 沿攻击方向打射线 | 适合穿刺/子弹 | 不适合「一片范围的横扫」 | ✗（作为防穿透补充预留） |

**为什么选 OverlapBox 主动扫描：**
1. **时机可控**：近战讲究「在挥砍的某几帧才生效」。主动扫描让攻击方自己决定「现在这帧检测」，与动画的激活帧精确对齐。被动 Trigger 的触发时机由物理引擎和碰撞体重叠时刻决定，很难卡到「正好挥到的那几帧」。
2. **无需物理刚体**：本项目角色用 `CharacterController` 而非 `Rigidbody`，给武器再挂 Trigger + Rigidbody 既增加物理开销又引入不需要的物理交互。主动查询完全绕开这套。
3. **代价可接受**：穿透问题可用 SphereCast 补（已预留思路），去重用 HashSet 自己做（已实现）。

**trade-off 依据**：近战命中的本质需求是「在我说了算的那几帧，检测一片区域」。OverlapBox 主动扫描是「时机可控 + 范围检测 + 无刚体依赖」的唯一组合。被动 Trigger 在「时机可控」上直接出局。

### ③ 技术点拆解（技术点 → 底层原理）

**技术点 1：`Physics.OverlapBoxNonAlloc` —— 为什么是 `NonAlloc` 版本。**
```csharp
int count = Physics.OverlapBoxNonAlloc(
    pivot.position, _attack.HalfExtents, _buf, pivot.rotation,
    _hitMask, QueryTriggerInteraction.Ignore);
```
- `Physics.OverlapBox(...)`（普通版）每次调用都 **new 一个数组** 返回结果——每帧检测就是每帧产生垃圾，GC 灾难。
- `OverlapBoxNonAlloc(..., _buf, ...)`（无分配版）把结果写进**你预先分配好的数组** `_buf`，返回实际命中个数 `count`。数组只在构造时分配一次、之后反复复用，**运行期零分配**。
- 参数含义：盒中心 = 挂点位置；`HalfExtents` = 盒子的半尺寸（从 SO 读）；`pivot.rotation` = 让盒子跟着武器朝向旋转；`_hitMask` = 只检测指定层；`QueryTriggerInteraction.Ignore` = 忽略 Trigger 碰撞体。

**技术点 2：预分配缓冲区 `_buf` + 上限 `MaxHitsPerFrame`。**
```csharp
private const int MaxHitsPerFrame = 16;
private readonly Collider[] _buf = new Collider[MaxHitsPerFrame];
```
NonAlloc 系列要求你给一个固定大小的数组，它最多填这么多。这里上限 16：一次挥砍同时碰到超过 16 个碰撞体的情况在近战里极罕见，多出来的会被静默丢弃（代码注释已标明，需要可调大）。这是「用一个固定上限换取零分配」的经典取舍。

**技术点 3：per-swing 去重 `HashSet<int> _hitSet`。**
激活窗口可能跨好几帧，每帧都扫一次，同一个敌人会被连续扫到好几帧——如果不去重，一次挥砍能把人打 N 次。
```csharp
int id = targetObj.GetInstanceID();
if (!_hitSet.Add(id)) continue;   // 已经打过 → 跳过
```
`HashSet.Add` 返回 `false` 表示「这个 id 之前已存在」，即「本次挥砍已经打过它了」，直接跳过。`OpenHitWindow` 时 `_hitSet.Clear()`，所以去重的范围是「一次挥砍」（per-swing），下一次挥砍重新开始。

**关键细节：去重的 key 是 `IDamageable` 组件的 InstanceID，不是碰撞体的。** 一个敌人可能有多个碰撞体（身体 + 武器 + 多个部位），如果用碰撞体 ID 去重，同一个敌人的不同碰撞体会被当成不同目标重复结算。所以代码先 `ResolveDamageable` 解析到「真正的受击者」，再用受击者的 ID 去重。

**技术点 4：激活窗口 `Open/CloseHitWindow` —— 与连段系统的接入缝。**
本组件**对动画系统一无所知**。它只暴露「开窗 / 关窗」两个方法，由外部（未来 Character 侧的动画归一化时间驱动，或连段系统）来调用。
- `OpenHitWindow` 是**幂等**的：`if (_windowActive) return`，已经开着就不重复清空去重集。这样外部哪怕每帧都调一次「确保开着」也安全。
- 「谁来在动画第 0.3~0.55 时调用 Open/Close」属于本轮 scope 之外，所以 `AttackDefinition.ActiveStart/ActiveEnd` 字段已备好，但驱动逻辑留给后续。这就是「**预留缝**」的具体形态：定好接口形状，不实现填充。

**技术点 5：`HitDirection` 的零向量防御。**
```csharp
Vector3 hitPoint = _buf[i].ClosestPoint(pivot.position);
Vector3 toHit = hitPoint - pivot.position;
Vector3 hitDir = toHit.sqrMagnitude > 1e-6f ? toHit.normalized : pivot.forward;
```
`ClosestPoint` 求碰撞体上离挂点最近的点。但当挂点**已经在目标碰撞体内部**时，最近点就是挂点自己，`toHit` 变成 `(0,0,0)`，normalize 一个零向量会得到 `NaN`/零向量，污染下游受击反应和击退方向。用 `sqrMagnitude > 1e-6f`（一个极小阈值）判断是否「太短」，太短就退回武器朝向 `pivot.forward`。这是一个真实的数据质量防御。
> 注：用 `sqrMagnitude`（长度平方）而非 `magnitude`（长度），是为了**省一次开方运算**——比较大小时平方和原值单调对应，没必要算开方。

### ④ 踩坑与设计决策（失败模式防御）

- **一次挥砍多段伤害**：HashSet per-swing 去重（技术点 3）。
- **同敌多碰撞体重复结算**：去重 key 用受击者 ID 而非碰撞体 ID（技术点 3）。
- **打到自己/队友**：`if (target.TeamId == _attackerTeam) continue`。`_attackerTeam` 必须和攻击者自身的 `HealthComponent.TeamId` 配一致（已加 Tooltip 提醒），否则会误伤队友或打到自己。
- **打到死人**：`if (!target.IsAlive) continue`。
- **击退方向为零向量**：`HitDirection` 阈值回退（技术点 5）。
- **重叠目标超上限被丢**：`MaxHitsPerFrame=16` + 注释标明静默丢弃语义。
- **挂点未配**：`_weaponPivot != null ? _weaponPivot : transform`，没配挂点就退回本组件 Transform，避免空引用。

### ⑤ 性能审查（GC / 装箱 / 热路径）

这是**唯一真正每帧（窗口期）运行的热路径**，重点审查：
- **物理查询**：`OverlapBoxNonAlloc` 写入预分配 `_buf`，**零分配**。✅
- **去重**：`HashSet<int>` 在构造时分配一次、`Clear()` 复用，不重建；`Add(int)` 对值类型 int **不装箱**。✅
- **请求构造**：`new DamageRequest(...)` 是 struct，栈上构造按值传递，不进堆。✅
- **`GetComponentInParent<IDamageable>()` —— 已知的热路径权衡点（计划中明确记录）**：
  - **调用频率**：≈ 单帧重叠碰撞体数 N × 激活帧数 F，一次挥砍量级在「几十次」。
  - **底层成本**：它会沿父链向上逐级查找并做类型匹配。返回的是**已存在的组件**，所以**不产生堆分配**，但「遍历父链 + 类型匹配」本身有 CPU 成本，不是免费的。
  - **本轮决策**：YAGNI——当前频率下成本可忽略，不做缓存优化。
  - **预留的缓存缝**：解析逻辑被刻意隔离在私有方法 `ResolveDamageable(Collider)` 里。将来若 profiler 证明它是瓶颈，只需在这一个方法内部加 `Dictionary<colliderInstanceId, IDamageable>` 缓存或一个碰撞体→受击者的注册表，**调用方一行不改**。
- **结论**：窗口期热路径**零 GC 分配、零装箱**；唯一非零的 CPU 成本是 `GetComponentInParent`，已识别、已量化、已预留优化位。✅

### ⑥ 改进反思（局限 + 扩展缝 + 可复用 pattern）

- **当前局限**：
  - **穿透（tunneling）**：高速挥砍/瞬移时，目标可能在两帧之间「穿过」盒子而漏检。已预留用 SphereCast 在两帧位置间补扫的思路，本轮未实现。
  - 命中盒是单个 AABB 盒（随挂点旋转），复杂武器形状（长剑、链锤）拟合不精确。
  - 窗口驱动（动画时间 → Open/Close）不在本组件，需 Character 侧配套。
- **扩展缝**：
  - 连段系统：直接调 `Open/CloseHitWindow` 控制每段的生效帧，本组件无需改动。
  - 多段命中盒：可让 `AttackDefinition` 描述一串盒子，循环检测。
  - 命中缓存：`ResolveDamageable` 单点优化（见 ⑤）。
- **可复用 pattern**：
  - **「主动查询 + per-事件去重」**——主动扫描拿到候选集，用一个可复用的 HashSet 在「一个逻辑事件（这里是一次挥砍）」范围内去重。技能 AOE、持续伤害区域都能套。
  - **「数据资产 + 行为组件」分离**——`AttackDefinition`（数据，SO）+ `MeleeHitDetector`（行为，组件）。同一个行为组件配不同数据资产 = 不同招式，无需改代码。这是数据驱动的标准落地形态。

---

## 子功能六：事件派发与表现解耦（EventBus + DamageReceivedEvent / DeathEvent + CombatDamage）

> 文件：`Events/DamageReceivedEvent.cs`、`Events/DeathEvent.cs`、`CombatDamage.cs`、依赖 `Game.Core/EventBus.cs`

### ① 实现了什么效果/功能

战斗逻辑算完账之后，通过 `EventBus<T>` 广播两个事件——`DamageReceivedEvent`（挨打了）、`DeathEvent`（死了）。表现层（特效、UI 血条、音效、受击反应）**订阅**这些事件来播放表现。战斗逻辑模块对表现层**零引用、零感知**。

附带 `CombatDamage.Deal(...)` 是给未来技能系统预留的「绕过近战命中、直接对目标结算」的入口。

### ② 方案选择（最终方案 + 备选 + trade-off）

**最终方案：逻辑层只发结构体事件，表现层订阅；用 `Game.Core` 的 `EventBus<T>` 做唯一通道。**

| 维度 | EventBus 事件解耦（选中） | 备选 A：逻辑层直接调 VFX/UI | 备选 B：用 C# 普通 event/委托互相注册 |
|------|--------------------------|---------------------------|-----------------------------------|
| 模块依赖 | Combat 不引用任何表现模块 | Combat 必须引用 VFX/UI 程序集 | 仍需互相持有引用 |
| 可替换性 | 换一套 UI 只换订阅方 | 改 UI 要动战斗代码 | 耦合在对象引用上 |
| 一对多 | 一个事件多方订阅，天然支持 | 要手动逐个调用 | 需自己维护列表 |
| 同帧性 | `Publish` 同步调用订阅者 | 同步 | 同步 |

**trade-off 依据**：这是项目的**硬架构约束**——「模块隔离，跨模块只走 EventBus」「逻辑→表现只能经事件，Combat 内禁止出现 VFX/UI/AudioSource 引用」。备选 A 直接违宪（Combat 会被迫依赖上层表现模块，破坏单向依赖）。EventBus 是唯一合规通道。

### ③ 技术点拆解（技术点 → 底层原理）

**技术点 1：事件为什么必须是 `struct` + `IGameEvent`，绝不能用 `class`。**
`EventBus<T>` 约束 `where T : struct, IGameEvent`。
- `struct`：`Publish(T evt)` 按值传递，JIT 为每个具体 `T` 生成专门代码，**不经过 object 装箱、不在堆上分配**。如果事件是 class，每次 `Publish(new XxxEvent())` 都往堆上丢一个对象，高频战斗事件会喂出 GC 垃圾。
- `IGameEvent`：一个空的标记接口（marker interface），它本身没有方法，作用是「**约束**」——只有实现了它的 struct 才能进 EventBus，防止误用任意 struct，也让代码意图自解释（「这是一个游戏事件」）。

**技术点 2：`EventBus<T>` 为什么用「静态泛型类」而不是「字典 `Dictionary<Type, Delegate>`」。**
这是 `Game.Core/EventBus.cs` 注释里讲的精髓：CLR 对**每一个具体的 `T`** 都会生成一份**独立的** `EventBus<T>` 类型，它的静态字段 `_event` 天然按 `T` 分区。所以 `EventBus<DamageReceivedEvent>` 和 `EventBus<DeathEvent>` 各有各的订阅者列表，互不干扰。`Publish` 时**不需要拿类型去字典里查**，直接访问对应静态字段、直接 `Invoke`——O(1)、无查找、无装箱。这是用「泛型 + CLR 类型系统」换掉「运行时字典查找」的巧妙手法。

**技术点 3：订阅/取消必须成对，否则内存泄漏。**
`_event += handler` 让 EventBus **强引用**了订阅者（通常是某个 MonoBehaviour）。如果只订阅不取消，即使那个对象被 Destroy，EventBus 仍攥着它的引用，GC 永远回收不掉它——内存泄漏，还会在对象已销毁后被 `Publish` 调到，引发空引用。标准做法：`OnEnable` 订阅、`OnDisable` 取消。换场景时 `EventBus<T>.Clear()` 兜底清空。

**技术点 4：事件字段为什么用 int ID / Vector3，而不是 GameObject。**
`DamageReceivedEvent` 里是 `TargetId`/`AttackerId`（int）、`HitPoint`/`HitDirection`（Vector3）这些**纯值**，没有对象引用。这样事件本身是自包含的快照：
- 保持 struct 的零分配特性（塞引用类型字段也行，但纯值更干净）。
- 订阅方拿到的是「发生那一刻的事实」，不会因为对象之后变化/销毁而失真。
- 攻击方能力（如吸血）可以用 `AttackerId` 比对「这是不是我打出去的伤害」来响应。

**技术点 5：`CombatDamage.Deal` —— 预留缝长什么样。**
```csharp
public static void Deal(in DamageRequest req, IDamageable target)
{
    if (target == null || !target.IsAlive) return;
    target.ReceiveHit(in req);
}
```
本轮技能系统还没接，但这个静态方法**先把调用形态锁定下来**：未来技能命中后，构造一个 `DamageRequest`、拿到目标 `IDamageable`、调 `CombatDamage.Deal` 即可，**完全绕开近战的 OverlapBox 命中判定**（技能有自己的命中方式）。它内部就是「判空 + 判活 + 转发 ReceiveHit」，复用了同一套结算/事件管线。这就是「预留缝」的典型样子——**接口定好、内部最小实现、等未来填**。

### ④ 踩坑与设计决策（失败模式防御）

- **逻辑层污染表现依赖**：靠「只发事件、Combat 程序集不引用任何表现模块」从架构上杜绝。
- **事件装箱 / GC**：靠 `struct + IGameEvent` 约束杜绝。
- **订阅泄漏 / 调用已销毁对象**：靠 OnEnable/OnDisable 成对 + 换场景 `Clear()` 杜绝（注意：这是**订阅方**的责任，本轮的临时 `CombatDebugLogger` 已遵守该模式）。
- **首帧订阅空引用**：`Publish` 用 `_event?.Invoke(evt)`，没有订阅者时安全空跑，不报错。

### ⑤ 性能审查（GC / 装箱 / 热路径）

- **GC Alloc**：`new DamageReceivedEvent{...}` 是 struct 字面量，栈上构造、按值发布，**零堆分配**。✅
- **装箱**：泛型约束 `T : struct` 保证全程不装箱。✅
- **派发开销**：`_event?.Invoke` 是一次（或多次，按订阅者数）委托调用，无字典查找。✅
- **隐患提醒**：性能红利的前提是事件**始终是 struct**。哪天有人手滑把事件写成 class，零分配立刻破功——`IGameEvent` + `where T : struct` 双重约束就是为了让这种错误**编译不过**。

### ⑥ 改进反思（局限 + 扩展缝 + 可复用 pattern）

- **当前局限**：
  - `EventBus<T>` 是**同步**的——`Publish` 会在当前帧、当前调用栈里把所有订阅者跑完。订阅者里如果有人做重活会拖慢这一帧；也意味着不能跨帧/异步处理。本轮这正是「同帧派发」要的语义，但要心里有数。
  - 事件没有「消费/拦截」机制（一个订阅者无法阻止其他订阅者收到）。
  - `Clear()` 的调用时机依赖场景生命周期管理器（CLAUDE.md 列为「尚未构建」），目前靠订阅方自律。
- **扩展缝**：
  - 表现层任意扩展：飘血字、击中音效、屏幕震动、受击高亮……各写一个订阅 `DamageReceivedEvent` 的组件即可，战斗逻辑完全不动。
  - 技能伤害：填充 `CombatDamage.Deal` 的调用方（技能系统），管线复用。
  - 攻击方能力（吸血/连击计数）：订阅事件、用 `AttackerId` 过滤。
- **可复用 pattern**：
  - **「逻辑发事件，表现来订阅」（发布-订阅 / 观察者模式）**——这是整个项目跨模块通信的统一范式，战斗只是它的一个使用者。掌握它，后续任何「A 发生了，B/C/D 想知道」的需求都照此办理。
  - **「空实现预留缝」**——`CombatDamage` 示范了如何为还没到来的系统先把接口钉死、内部留最小实现，避免未来接入时反过来改动既有代码。

---

## 附：贯穿全系统的几条「设计原则」总结（给新手的记忆锚点）

1. **传值不传引用**：能用 struct 快照就不用对象引用，天然规避「中途被改 / 对象已销毁」两大类 bug，且零 GC。
2. **纯函数算账，薄壳编排**：会变、要验证的计算抽成纯函数（可脱离引擎单测）；碰 Unity 的副作用留在薄薄的 MonoBehaviour 里。
3. **面向接口，不面向具体类**：`IDamageable` 让「打人的」不需要认识「挨打的」具体是谁，新增可受击物零成本接入。
4. **逻辑与表现用事件解耦**：逻辑只管算和广播，表现只管订阅和播放，两边可独立替换——这是模块隔离的命脉。
5. **热路径零分配**：预分配缓冲区（`_buf`）、复用集合（`_hitSet`）、struct 事件、`NonAlloc` 物理查询——每一处每帧运行的代码都不许往堆上丢垃圾。
6. **预留缝而非提前实现（YAGNI + 扩展点）**：本轮不做的（连段驱动、技能入口、Buff、防御公式），都**只留接口形状和占位**，不写实现。既不过度设计，又让未来接入不必回改既有代码。

> 本文描述的是 M3 阶段的核心循环骨架。受击反应表现、连段窗口驱动、技能伤害入口、Buff/Debuff、防御数值公式均为**有意预留、尚未实现**的部分。
