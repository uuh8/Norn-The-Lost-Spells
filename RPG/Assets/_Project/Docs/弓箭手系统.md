# 弓箭手（Archer）系统 — 设计解析

> 目标读者：有 C# 基础、**零战斗系统/3C（Character / Camera / Control）经验**的战斗设计新人。
> 本文复盘「从零做出第二个可玩角色——弓箭手」的完整思路：需求 → 原理 → 业界方案 → 选型 → 落地 → 踩坑。目的不是让你背代码，而是让你理解**每一步为什么这样做**，以后能把这些套路迁移到别的角色/技能上。
>
> 阅读顺序：先读「全局心智模型」建立整条数据流的直觉；再按子功能逐个精读。每个子功能都用同一套 **六维分析**（需求拆解 / 基础原理 / 业界方案 / 用到的技术 / 架构拆解 / 踩坑 Debug），彼此独立，可单独跳读。
>
> 前置阅读（强烈建议）：`M3_Combat_Design.md`（伤害管线）、`Combo_System_Design.md`（连段）、`Dash_System_Design.md`（buffer/cooldown 计数器套路）。弓箭手大量复用了这三套基础设施，本文只讲「弓箭手新增/特有」的部分，复用的地方会指给你看。

---

## 全局心智模型

在做第二个角色之前，先想清楚一件事：**战士（Warrior）和弓箭手（Archer）有 80% 是一样的**——都要走路、跳跃、冲刺、被相机控制、用一套状态机。真正不同的只有 20%：战士近战挥砍、弓箭手远程射箭。

所以「做弓箭手」这件事，本质是在回答两个问题：

1. **怎么让两个角色共享那 80%，又各自拥有那 20%？** → 这是**架构问题**（子功能一、二）。
2. **远程攻击这 20% 具体怎么实现？** → 这是**玩法问题**（子功能三~九）。

下面这张图是弓箭手「按下左键」之后发生的全部事情，先建立直觉，看不懂没关系，后面逐段拆：

```
   按下鼠标左键（Attack）
        │ Input System 回调一次 → AttackBufferCounter = 0.15s（只登记意图）
        ▼
   PlayerGroundedState.CheckTransition（每帧，优先级 Dash→Attack→Jump）
        │  调用 _player.TryStartAttack()  ← 共享代码不知道"攻击"具体是什么
        ▼
   ArcherController.TryStartAttack()（子类重写，这里才知道是"弓箭")
        │  轮询 IsAttackHeld，累计按住时长 _attackHeldTime
        │
        ├─ 按住时长 < TapThreshold 就松手 → 点按 → PlayerBowAttackState（普攻）
        │                                          │ CrossFade 普攻动画
        │                                          │ 动画到 ArrowSpawnTime → 生成 1 支箭（直线/抛物）
        │                                          ▼ 动画 ≥85% → 回移动态
        │
        └─ 按住时长 ≥ TapThreshold → 蓄力 → PlayerChargeAttackState（重击）
                   │ CrossFade 拉弓 → (Animator 自动过渡) 满弓循环
                   │ 发布 AimStateChangedEvent{Active=true} → 准心 UI 显示
                   │ 每帧累计 _chargeElapsed（封顶），角色转向相机水平朝向
                   │ 松手 → 锁定蓄力比例 _ratio(0~1) → CrossFade 放箭动画
                   │ 放箭动画到 ArrowSpawnTime →
                   │     屏幕中心射线求命中点（剔除自身）→ 箭直线飞向准心
                   │     伤害/箭速 = Lerp(min, max, _ratio)
                   ▼ 动画 ≥85% → 回移动态 + 发布 AimStateChangedEvent{Active=false}
```

**四个贯穿全程的设计直觉，先记住：**

1. **共享的代码不认识具体角色。** 共享的 `PlayerGroundedState` 只会喊一句「该攻击的话你自己处理」（`TryStartAttack()`），至于攻击是挥刀还是射箭，它不知道也不关心。这叫**多态 seam（接缝）**。

2. **箭矢和挥砍走的是同一条伤害管线。** 不管是近战 `OverlapBox` 命中，还是箭矢 `OnCollisionEnter` 命中，最后都调用同一个 `IDamageable.ReceiveHit(in DamageRequest)`。**「怎么发现命中」可以不同，「命中后怎么算伤害」必须统一。**

3. **所有时序都读动画进度，不用 Animation Event。** 什么时候生成箭、什么时候结束，全部用 `normalizedTime`（动画播放进度 0~1）去和数据里的阈值比。这是本项目刻意的统一约定。

4. **表现（准心 UI）只是 gameplay 的旁观者。** 准心显隐不是攻击逻辑直接去 `SetActive`，而是攻击逻辑「广播一个事件」，UI 自己订阅、自己显隐。gameplay 永远不认识 UI。

---
---

## 子功能一：把「一个角色」拆成「共享基类 + 角色子类」

> 涉及文件：`PlayerControllerBase.cs`（新增，抽象基类）、`WarriorController.cs`（由 `PlayerController.cs` 改名而来）、`ArcherController.cs`（新增）、`PlayerStateBase.cs`（`_player` 改类型）、`PlayerGroundedState.cs`（攻击分支改调 `TryStartAttack()`）、`PlayerDashState.cs`（Dash 动画名数据驱动）

这是整个弓箭手开发的**地基**。没有它，弓箭手就得把战士的移动/跳跃/冲刺代码复制一遍——两份代码以后改一处就得改两次，迟早不同步出 bug。

### 1. 需求拆解

要解决的问题：**让两个角色共享移动/跳跃/冲刺/相机/状态机，但各自拥有不同的攻击和动画。**

拆成可验收的小目标：
- 共享能力（走/跳/冲刺/相机/接地三态+冲刺态）只写一份。
- 战士行为**零改变**（这是硬约束——重构不能把已经做好的战士改坏）。
- 弓箭手能复用全部共享能力，只补「弓箭」专属部分。
- 共享代码里**不能出现** `WarriorController` / `ArcherController` 任何一个具体类名（否则就不叫"共享"了）。

### 2. 该功能涉及的基础知识和原理

**继承（inheritance）与多态（polymorphism）。** 这是面向对象的两块基石：
- **继承**：`WarriorController : PlayerControllerBase` 表示「战士控制器**是一种**角色控制器」，自动获得基类所有能力。
- **多态**：基类定义一个「占位方法」`virtual bool TryStartAttack()`，子类各自 `override` 它给出不同实现。运行时调用 `_player.TryStartAttack()` 会自动跑到**真实类型**对应的版本——基类代码因此能「调用一个它还不知道怎么实现的方法」。

**抽象类（abstract class）。** `PlayerControllerBase` 标了 `abstract`，意思是「这个类不完整，不能直接挂到 GameObject 上，必须由子类补全」。这正确表达了「世上没有'通用角色'，只有战士或弓箭手」。

**模板方法模式（Template Method）。** 基类的 `Awake()` 标成 `protected virtual`，里面搭好共享骨架（建状态机、建四个共享状态）；子类 `override Awake()`，**先调 `base.Awake()`** 再补自己的东西（建攻击状态、预 hash 自己的动画名）。骨架在基类、细节在子类——这就是模板方法。

**「接缝（seam）」的概念。** 共享代码里那处「我知道这里要做某事，但具体做什么交给子类」的虚方法调用点，业界叫 seam。`TryStartAttack()` 就是攻击接缝：把「攻击该排在 Dash 之后、Jump 之前」这个**优先级规则**留在共享处，把「攻击具体是什么」**外包**给子类。

### 3. 实现该功能有哪些方案（业界常见）

把「多个相似角色」组织起来，常见有四条路：

- **A. 复制粘贴。** 每个角色一份完整脚本。最快上手，最早烂尾——共享逻辑改一次要同步 N 份，必然漏。只适合一次性原型。
- **B. 继承（base + subclass）** ← 本项目选这个。共享放基类，差异放子类。适合「角色数量少、共享多、差异是'多/少几种攻击'这种**行为型**差异」。
- **C. 组件化 / 组合（composition）。** 把「移动」「攻击」「跳跃」各做成独立组件，角色 = 一堆组件的拼装（Unity 的 ECS、或「能力系统 Ability System」就是这思路）。适合「角色多、能力要自由组合（这个角色能飞+射箭，那个能飞+近战）」的大型项目。最灵活，但前期架子重。
- **D. 泛型基类 `Base<T>`。** 用泛型参数把子类类型带进基类。能省掉一些强转，但会让状态机、Inspector、序列化都被泛型「传染」，可读性和 Unity 兼容性变差。

**选型理由：** 现在只有 2 个角色，差异就是「近战一种连段」vs「远程两种攻击」，是典型的**行为型差异**——继承最省力且最直观。组合（C）是更大规模时的正解，但现在上它属于过度设计。泛型（D）在本项目被明确否决（见下方「无泛型」约束），因为它的复杂度换不来等价收益。

### 4. 用到哪些技术（含 Unity 组件）

- **C# 继承 / `abstract` / `virtual` / `override` / `protected`**：架构主干。
- **C# 属性（property）做只读暴露**：基类用 `public CharacterController CharacterController => _characterController;` 把私有组件以只读形式给状态用——状态能读不能换。
- **Unity `[RequireComponent]`**：`[RequireComponent(typeof(CharacterController))]` 挂在基类上，保证任何子类的 GameObject 都自带必需组件。
- **Unity 序列化继承**：子类 Inspector 会**自动显示基类的 `[SerializeField]` 字段**（移动速度、跳跃力…）再加上自己的字段——这是继承在 Unity 序列化层的自然延伸。

### 5. 架构设计与拆解

最终的类关系：

```
PlayerControllerBase (abstract MonoBehaviour)
│   持有：CharacterController / Animator / 输入 / 相机 / 状态机
│   + 四个共享状态：Grounded / Airborne / Sliding / Dash
│   + 计时器：Jump/Coyote/Attack/Dash×2
│   + seam：public virtual bool TryStartAttack() => false;
│   + 模板方法：protected virtual void Awake()
│
├── WarriorController  ：近战。建 PlayerAttackState；override TryStartAttack 进连段态
└── ArcherController   ：远程。建 PlayerBowAttackState + PlayerChargeAttackState；
                          override TryStartAttack 路由 点按/按住
```

**状态如何拿到「子类专属成员」？关键设计——不用泛型。**
- 共享状态（Grounded/Airborne/Sliding/Dash）的 `_player` 字段类型是**基类** `PlayerControllerBase`，只用基类成员，所以对谁都通用。
- 角色专属状态（`PlayerBowAttackState` 等）在构造时收**具体子类**，额外存一份 typed 引用：

```csharp
public class PlayerBowAttackState : PlayerStateBase
{
    private readonly ArcherController _archer;   // 拿弓箭手专属成员（箭预制体、连段表…）
    public PlayerBowAttackState(ArcherController player) : base(player) // base 存基类引用
    { _archer = player; }
}
```

这样 `_player` 走基类共享成员，`_archer` 走子类专属成员，**一份不重不漏**，且全程无泛型。

**seam 怎么保住优先级：** 共享的 `PlayerGroundedState.CheckTransition` 里那一段是——

```csharp
// Dash 最高优先级（略）
if (_player.TryStartAttack()) return;   // 攻击：具体干啥由子类决定，这里只占"攻击优先级"这个位
// 再往下才是 Jump、离地、滑坡
```

基类 `TryStartAttack()` 返回 `false`（不攻击）；战士 override 后「有缓冲输入就进连段态、返回 true」；弓箭手 override 后「路由点按/按住、进对应攻击态、返回 true」。**Dash→Attack→Jump 的顺序只写在共享处一次**，三个角色都遵守。

### 6. 踩坑与 Debug

**坑 1：改名 MonoBehaviour 把预制体引用搞丢（最严重）。**
我最初打算「删掉 `PlayerController.cs`、新建 `WarriorController.cs`」。这是**错的**——Unity 靠 `.meta` 文件里的 **GUID** 把场景/预制体上的脚本引用和文件关联起来。删文件再新建会生成**新 GUID**，于是战士预制体上的脚本变成「Missing Script」，所有 Inspector 上拖好的引用、调好的数值**全部清零**。
✅ 正确做法：把 `.cs` **连同它的 `.meta` 一起改名**（`git mv PlayerController.cs WarriorController.cs` + `git mv PlayerController.cs.meta WarriorController.cs.meta`），GUID 不变，预制体引用毫发无伤。**这是本项目重命名脚本的铁律。**

**坑 2：以为「每个 commit 都能编译」，其实是一次原子改动。**
抽基类时，基类 `Awake` 里 `new PlayerGroundedState(this)` 传的 `this` 是基类类型，但 `PlayerGroundedState` 的构造函数当时还要求 `PlayerController`——于是报 `CS1503: cannot convert PlayerControllerBase to PlayerController`。
教训：**「抽基类」+「把状态的 `_player` 改成基类类型」是一个不可分割的编译单元**，必须一起改完才编译得过。我当初把它们想成两个独立步骤是错的。修复就是把 `PlayerStateBase._player` 及所有共享状态构造函数的参数类型一并改成 `PlayerControllerBase`。

---
---

## 子功能二：让代码「点名」动画 —— 数据驱动的 Animator 状态进入

> 涉及文件：`PlayerControllerBase.cs`（`_dashStateName` 序列化 + Awake 预 hash）、`AttackDefinition.cs`（`AnimationStateName`）、`ChargeAttackDefinition.cs`（三个状态名）、各攻击/冲刺状态里的 `CrossFadeInFixedTime`

弓箭手有自己的一套动画（`Idle_Bow`/`Run_Bow`/`Attack01_Bow`/`Dash_Bow`…），和战士的动画**名字完全不同**。但驱动它们的 C# 代码是共享的。代码怎么在不写死动画名的前提下，进入正确的动画？

### 1. 需求拆解

- 同一段共享代码（比如冲刺状态），要能在战士身上播 `DashForward_SingleTwohandSword`、在弓箭手身上播 `Dash_Bow`。
- 攻击动画名也得能逐角色、逐连段配置。
- 配错名字时要有**可诊断的提示**，不能默默失效让人摸不着头脑。

### 2. 该功能涉及的基础知识和原理

**Animator 进入一个状态有两种方式：**
- **过渡连线（Transition）驱动**：在 Animator 窗口画箭头 + 设条件参数（如 `speed > 0.1` 从 Idle 连到 Run）。适合**连续型**的移动动画。
- **代码 CrossFade 驱动**：`animator.CrossFadeInFixedTime(stateHash, 0.1f, 0)` 让代码**直接点名**要播哪个状态，**不需要在 Animator 里画进入连线**。适合**离散触发**的攻击/冲刺/放箭。

**`CrossFadeInFixedTime` 用 hash 不用字符串。** Animator 内部用整数 hash 标识状态。`Animator.StringToHash("Dash_Bow")` 把字符串转成这个整数。**字符串转 hash 有开销且会产生临时垃圾**，所以本项目的铁律是：**状态名在 `Awake` 里预转一次 hash 存起来，运行时只用 hash，绝不每帧/每次触发 `StringToHash`。**

**「数据驱动」在这里的含义。** 把「动画状态叫什么名字」从代码里抽出来，变成 Inspector 上可填的字符串字段（基类的 `_dashStateName`、SO 里的 `AnimationStateName`）。代码只认这个字段、不认具体字符串值——于是同一份代码配不同的值就能驱动不同角色。

### 3. 实现该功能有哪些方案（业界常见）

- **A. 硬编码状态名字符串**：`CrossFade("Dash_Bow")`。最直接，但换角色就得改代码、加角色就得加分支，违背共享初衷。
- **B. 每个角色一套 Animator + 代码读数据里的状态名**（本项目）：代码数据驱动，每个角色挂自己的 Animator Controller，名字写在 Inspector/SO。
- **C. Animator 全用过渡连线 + 触发器参数**：完全不用代码 CrossFade。问题是攻击/连段/蓄力这种「精确单点触发 + 多段流转」用纯参数连线会爆炸成一张蜘蛛网，且时序难和代码对齐。
- **D. Animator Override Controller**：做一个「模板 Controller」，各角色只换里面的动画 clip、状态名保持一致。也可行；本项目没用是因为两个角色的状态机结构（弓箭手有蓄力三段）并不完全一致，强行统一反而别扭。

### 4. 用到哪些技术（含 Unity 组件）

- **Unity `Animator` 组件** + 每角色一个 **Animator Controller** 资产（`.controller`，文本可序列化）。
- **`Animator.CrossFadeInFixedTime(int, float, int)`**：固定秒数过渡到指定状态（不受动画时长缩放影响，手感稳定）。
- **`Animator.StringToHash`**：字符串→hash，**只在 Awake 调**。
- **`[SerializeField] string` / ScriptableObject 字符串字段**：承载可配置的状态名。

### 5. 架构设计与拆解

**两类参数，两种同步策略（重要区分）：**
- **连续状态型参数**（`speed`、`isGrounded`）：由 `PlayerControllerBase.Update` 里的 `SyncAnimatorParameters()` **每帧统一同步**。它们驱动移动动画的过渡连线。
- **事件型触发**（`jump`）：由发起的状态**一次性**触发（`SetTrigger`），绝不每帧同步。
- **离散状态进入**（攻击/冲刺/放箭）：由代码 `CrossFade` 点名，**不走参数也不走连线**。

预 hash 的防御性写法（基类 Awake）：

```csharp
if (string.IsNullOrEmpty(_dashStateName)) {
    _dashStateHash = 0;
    GameLog.Warn("_dashStateName 为空，冲刺 CrossFade 将无法切换动画", "Character");
} else {
    _dashStateHash = Animator.StringToHash(_dashStateName);
}
```

空名字 → hash 记 0 + 告警；`CrossFade(0)` 不会切任何动画（静默失败）。这是「配置错误尽量自曝」的小设计。

### 6. 踩坑与 Debug

**坑 1：弓箭手冲刺「只平移、没动画」。**
现象：弓箭手能冲刺（位移对的），但人物是「滑过去」的，没有冲刺动作。
原因：`_dashStateName` 是基类序列化字段，弓箭手预制体上它还留着基类默认值 `DashForward_SingleTwohandSword`（战士的状态名）。弓箭手的 Animator 里根本没有这个状态，`CrossFade` 到一个不存在的状态 = **静默无效**，于是没动画。
✅ 修复：在弓箭手 Inspector 把 `Dash State Name` 填成 `Dash_Bow`。这正是「数据驱动 + 静默失败」的典型代价——**好处是加角色不用改代码，代价是忘填名字时不报错、只是没反应**。

**坑 2（要牢记的通用陷阱）：代码 CrossFade 进入的状态，仍然需要自己的「退出」连线。**
代码只负责**进入**（CrossFade 点名），但**离开**它不管——攻击/冲刺结束时，C# 只是把状态机切回移动态，**并不会** CrossFade 回 Idle。如果你在 Animator 里没给 `Dash_Bow`、`Attack01_Bow` 画**出去**的过渡连线（如 `Dash_Bow → Idle_Bow`，勾 Has Exit Time），角色就会**定格在那个姿势**。
口诀：**进入靠代码，退出靠连线。** 移动动画（Idle/Run/Jump）则两头都靠连线（`speed`/`isGrounded`/`jump`）。

---
---

## 子功能三：远程攻击的「载体」 —— 箭矢投射物（Arrow）

> 涉及文件：`Arrow.cs`（新增，投射物）、`AttackDefinition.cs`（新增 `ArrowSpawnTime`）

近战的命中是「挥砍那一刻、武器附近的一个盒子里有谁」。远程完全不同：**伤害不在角色身上发生，而是飞出去一个物体，它撞到谁才结算。** 这个「飞出去的物体」就是箭矢。

### 1. 需求拆解

- 一个会飞的箭矢：有初速度、受（或不受）重力、撞到东西就处理。
- 撞到**敌人**：结算一次伤害；撞到**环境**：销毁；撞到**自己/队友**：穿过去不结算。
- 出膛瞬间不能撞到射手自己。
- 漏网的箭（没撞到任何东西）要能自己消失，否则越积越多。
- **复用近战那条伤害管线**，不另起炉灶。

### 2. 该功能涉及的基础知识和原理

**Unity 物理：`Rigidbody` + `Collider` + 碰撞回调。**
- `Rigidbody` 让物体受物理引擎驱动（速度、重力、碰撞）。给它一个 `linearVelocity`（Unity 6 里 `velocity` 改名为 `linearVelocity`），它就会按这个速度飞。
- `Collider` 是碰撞形状。两个带 Collider 的物体相撞，且至少一方有非 Kinematic `Rigidbody` 时，Unity 在它们接触那帧回调 `OnCollisionEnter(Collision)`。
- 注意：角色用的是 `CharacterController`（不走物理管线），而**投射物用 `Rigidbody`**——这是本项目「角色 CC、子弹 Rigidbody」的明确分工。

**值快照（value snapshot）。** 箭飞出去后可能要 1 秒才命中，这期间射手可能已经死了/被销毁。所以箭在**生成瞬间**就把「攻击者 ID、阵营、伤害值、伤害类型」拷贝进自己身上（`Init` 注入），命中时用这份快照构造 `DamageRequest`，**绝不回头再去问射手**（它可能已经不存在了）。这与近战 `DamageRequest` 是同一个设计哲学。

**阵营过滤（team filtering）。** 每个可受击物体有 `TeamId`。箭命中时先比阵营：`target.TeamId == _attackerTeam` 就直接 `return`（穿过，不结算不销毁），避免射到自己和队友。

### 3. 实现该功能有哪些方案（业界常见）

**「子弹怎么命中」三大流派：**
- **A. 物理投射物（physical projectile）** ← 本项目。真有一个带 Rigidbody 的物体在场景里飞，`OnCollisionEnter` 命中。直观、能看到箭飞、能被墙挡、能做抛物线。缺点：高速时可能**穿模**（一帧飞过薄墙），且场上物体多。
- **B. 射线命中（hitscan）**：开枪瞬间发一条 `Raycast`，射线打到谁就是谁，没有飞行时间。FPS 的步枪常用。优点：绝不穿模、零飞行物体。缺点：没有飞行表现（得另配 tracer 特效），不适合「能看见箭飞、能闪避」的玩法。
- **C. 数据驱动的伪投射物**：逻辑上用射线/插值算命中，视觉上播一个纯特效。性能最好，逻辑最可控，但实现最绕。
- **高速穿模的工业解法**：给 Rigidbody 开 **Continuous（连续）碰撞检测**，或每帧用 `Raycast` 沿位移补检（sweep）。本项目箭速不高，暂用默认离散检测。

**选型理由：** 弓箭手要的是「看得见的箭、能形成抛物线手感、未来能被闪避/打落」，物理投射物（A）最贴合，也最容易做出「蓄力直射 vs 普攻抛物」的差异。

### 4. 用到哪些技术（含 Unity 组件）

- **`Rigidbody`**（`linearVelocity`、`useGravity`）、**`Collider`**、**`OnCollisionEnter`**。
- **`Physics.IgnoreCollision(colliderA, colliderB)`**：让箭的 Collider 和射手的 Collider 互相忽略，解决「出膛即自撞」。
- **`[RequireComponent(typeof(Rigidbody))]` / `(typeof(Collider))`**：保证箭预制体组件齐全。
- **`Object.Instantiate` / `Destroy(go, lifetime)`**：生成与超时自毁。
- **复用 `IDamageable.ReceiveHit(in DamageRequest)` + `DamageRequest`**：与近战同一条管线。

### 5. 架构设计与拆解

`Arrow` 与 `MeleeHitDetector` **平级**——它们是「两种发现命中的方式」，但汇入同一条伤害管线：

```
MeleeHitDetector（OverlapBox，每帧扫窗口内）─┐
Arrow（OnCollisionEnter，飞行中撞到）        ─┤─► 构造 DamageRequest ─► target.ReceiveHit()
                                               └─ HealthComponent 内：DamagePipeline 算 → 扣血 → 发事件
```

`Arrow.Init(...)` 注入快照 + 配置飞行：

```csharp
public void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                 Vector3 velocity, Collider shooterCollider, bool useGravity = true)
{
    // 拷贝伤害快照（团队/ID/伤害/类型）…
    if (shooterCollider != null) Physics.IgnoreCollision(_collider, shooterCollider); // 不自撞
    _rb.useGravity = useGravity;        // 普攻 true（抛物线）；瞄准直射 false（走直线）
    _rb.linearVelocity = velocity;      // 一次性给初速度
    Destroy(gameObject, _maxLifetime);  // 兜底自毁
}
```

命中处理的判断顺序（`OnCollisionEnter`）：① 已结算过就 return（防一帧多撞）→ ② 同阵营穿过 → ③ 敌方存活则 `ReceiveHit` → ④ 无论命中敌人/环境都销毁。

`useGravity` 这个开关是后面「瞄准直射」复用本系统的关键伏笔：普攻让箭走抛物线（真实），蓄力瞄准让箭走直线（指哪打哪），**同一个 Arrow 类，一个 bool 切换**。

### 6. 踩坑与 Debug

**坑：箭射出去是「竖着」飞的（模型朝向不对）。**
现象：箭能射出、方向对，但箭身是竖着的，不是箭头朝飞行方向。
原因：`Quaternion.LookRotation(velocity)` 会把物体的 **+Z 轴**对齐到飞行方向（Unity 约定）。但这个箭的美术模型「箭尖」是沿 **+Y 轴**建的。于是 +Z 对准了飞行方向，箭尖（+Y）却指向侧上方 → 看起来竖着。
✅ 修复：加一个可调的修正旋转 `_modelForwardOffsetEuler = (90, 0, 0)`，把模型的 +Y 转到飞行方向：
```csharp
transform.rotation = Quaternion.LookRotation(velocity) * Quaternion.Euler(_modelForwardOffsetEuler);
```
做成 Inspector 字段而非写死，是因为「美术模型的轴向朝向」是**资产属性**，换个箭模型可能要改成 -90；让美术/设计能自己调，比改代码好。**通用教训：凡是 `LookRotation` 对齐的是 +Z，模型若不是 +Z 朝前就要加这种修正旋转。**

---
---

## 子功能四：什么时候射出去 —— 动画驱动的单点时序

> 涉及文件：`AttackDefinition.cs`（`ArrowSpawnTime`）、`ChargeAttackDefinition.cs`（`ArrowSpawnTime`）、`PlayerBowAttackState.cs` / `PlayerChargeAttackState.cs`（`HandleArrowSpawn` / 放箭判定）

箭不能在「按下左键那一刻」就生成——那样箭会在角色还没拉弓时就飞出去，很假。它应该在**放箭动画的某一帧**生成，和「手指松开弓弦」的视觉对齐。怎么做到？

### 1. 需求拆解

- 在攻击动画播放到「松弦」那一刻（而不是动画开头）生成箭。
- 这个时刻要**可配置**（设计师能调，配合不同动画）。
- 一次攻击**只生成一支箭**，不能因为「松弦帧持续好几帧」而每帧都生成。

### 2. 该功能涉及的基础知识和原理

**`normalizedTime`：动画的归一化进度。** `animator.GetCurrentAnimatorStateInfo(0).normalizedTime` 返回当前动画播了多少——`0` = 刚开始，`1` = 播完一遍（循环动画会继续 1.x、2.x，所以常 `% 1f` 取小数部分）。**用「进度百分比」而不是「第几秒」来标记时机**，这样动画快慢变了时机也跟着对。

**单点触发（single-point trigger）vs 窗口（window）。**
- **窗口**是一对值（`HitActiveStart`~`HitActiveEnd`），表示「这段进度内持续有效」（近战命中窗口就是这样，每帧检测）。
- **单点**是一个值（`ArrowSpawnTime`），表示「进度越过这一点时，触发一次」。生成箭是单点。

**单点的「只触发一次」防抖。** 进度越过 0.4 之后，后续每帧 `t >= 0.4` 都成立。所以要一个 `bool _arrowSpawned` 守卫：触发后置 `true`，本次播放不再触发。这和近战「一次挥砍用 `HashSet` 记住打过谁、不重复打」是同一思路的退化版（单目标版）。

**过渡期的 `normalizedTime` 不可信。** 两个动画 CrossFade 切换的过渡帧里，`GetCurrentAnimatorStateInfo(0)` 返回的「当前状态」可能还是**旧状态**，它的 `normalizedTime` 跟你要判断的新动画无关。所以判断前要么 `if (animator.IsInTransition(0)) return;`（普攻这么做），要么校验 `info.shortNameHash == 目标状态hash`（蓄力放箭这么做）——**确认「我真的在我以为的那个状态里」再读进度。**

### 3. 实现该功能有哪些方案（业界常见）

- **A. Animation Event（动画事件）**：在动画时间轴上打一个标记，到那一帧 Unity 回调一个函数。这是 Unity 官方最常见的做法。优点：所见即所得，美术能在动画里直接放。缺点：时机和数据散落在动画文件里（不在代码/SO 里），不好统一管理、不好做单元测试、跨角色不一致。
- **B. `normalizedTime` 阈值轮询**（本项目）：每帧读动画进度，和 SO 里的阈值比。优点：所有战斗时机**统一**用一套机制、集中在 SO 里、可被纯函数化测试、和近战/连段共用同一套约定。缺点：要自己处理「只触发一次」和「过渡期」两个细节。
- **C. 定时器（按秒）**：进入状态后数 0.3 秒生成。最简单但最脆——换个动画长度就错位，不随动画快慢自适应。

**选型理由：** 本项目**刻意全程用 B、不用 A**——让命中窗口、连段窗口、刀光窗口、箭矢生成全部是「读 `normalizedTime` 比阈值」的同一套心智模型。统一带来的可维护性，盖过了 Animation Event 的「所见即所得」。

### 4. 用到哪些技术（含 Unity 组件）

- **`Animator.GetCurrentAnimatorStateInfo(0)`** → `.normalizedTime` / `.shortNameHash`。
- **`Animator.IsInTransition(0)`**：判断是否处于过渡帧。
- **`[Range(0f,1f)] float ArrowSpawnTime`**：SO 上的可调单点时刻。
- **`bool` 守卫位**：单点去重。

### 5. 架构设计与拆解

普攻的单点生成（`PlayerBowAttackState.HandleArrowSpawn`）：

```csharp
private void HandleArrowSpawn()
{
    if (_arrowSpawned) return;                       // 已生成 → 不再生成
    if (_player.Animator.IsInTransition(0)) return;  // 过渡期进度不可信
    float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
    if (t >= seg.ArrowSpawnTime) { SpawnArrow(seg); _arrowSpawned = true; }
}
```

蓄力放箭多一道「确认真的在放箭态」的校验（因为放箭前有「满弓保持」态，CrossFade 过渡中当前态可能还是保持态）：

```csharp
AnimatorStateInfo info = _player.Animator.GetCurrentAnimatorStateInfo(0);
if (info.shortNameHash != _archer.ChargeLooseHash) return;  // 不在放箭态就别读进度
float t = info.normalizedTime % 1f;
if (!_arrowSpawned && t >= data.ArrowSpawnTime) { SpawnChargedArrow(data); _arrowSpawned = true; }
```

### 6. 踩坑与 Debug

本子功能的「坑」主要是前述两个**原理性陷阱**已在设计阶段规避：① 不加 `bool` 守卫会一帧生成一串箭；② 不排除过渡期会在错误的动画进度上误触发（CrossFade 切入瞬间旧动画可能已接近 1，立刻越阈）。代码里 `_arrowSpawned` 守卫 + `IsInTransition`/`shortNameHash` 校验就是这两个坑的解药。新手最容易在「单点为什么生成了好几支箭」上卡住——记住**单点必配去重位**。

---
---

## 子功能五：一键两用 —— 点按普攻 / 按住蓄力的输入路由

> 涉及文件：`ArcherController.cs`（`TryStartAttack` 路由）、`PlayerControllerBase.cs`（`IsAttackHeld` 暴露、`AttackBufferCounter`）

弓箭手两种攻击共用鼠标左键：**轻点 = 普攻，按住 = 蓄力重击**（像原神弓箭手）。怎么从「同一个按键」分辨出玩家想要哪一种？

### 1. 需求拆解

- 同一个键：短按触发普攻、长按进入蓄力。
- 短按要「跟手」——哪怕一帧的点击也不能丢（普攻不能漏）。
- 长按的判定阈值（多久算"按住"）可配置。
- 普攻**不经过拉弓动作**（轻点就直接射，不先摆个拉弓姿势）。

### 2. 该功能涉及的基础知识和原理

**两种读输入的方式：事件回调 vs 每帧轮询。**
- **事件回调**：`Attack.performed += OnAttackPerformed`，按下那一帧回调一次。适合捕捉「**发生了一次**按下」这种离散事件——哪怕只按一帧也不会漏。
- **每帧轮询**：`Attack.IsPressed()`，每帧问「现在还按着吗」。适合判断「**持续**按住了多久」。
- **本功能两者都要**：用回调写下 `AttackBufferCounter`（保证「按了一下」不丢），用轮询 `IsAttackHeld` 累计按住时长（判断够不够长进蓄力）。

**输入缓冲（input buffer），复用自冲刺/连段的同一套路。** 按下回调里 `AttackBufferCounter = 0.15f`；`PlayerControllerBase.Update` 每帧 `-= Time.deltaTime`。`> 0` 表示「最近按过」。它的作用是**亚帧容错**：哪怕玩家点得飞快、按下和松开发生在同一帧，缓冲也记住了「这一下」，普攻不会漏。

**阈值门控（threshold gating）。** 累计「按住了多久」`_attackHeldTime`，和 `TapThreshold`（如 0.15s）比：没到阈值就松手 = 点按 → 普攻；超过阈值还按着 = 长按 → 蓄力。

### 3. 实现该功能有哪些方案（业界常见）

分辨「点按 vs 长按」业界常见三种：

- **A. 纯缓冲 + 松手判定**：按下记缓冲，松手时看按了多久。简单，但「按住进蓄力」要等到松手才知道，蓄力没法「按住即时进入」。
- **B. 代码手动计时 + 阈值门控**（本项目）：每帧累计按住时长，**一旦越过阈值立刻进蓄力**（不等松手）；没越过就松手则普攻。手感最贴近原神（按住到点马上拉弓）。
- **C. Input System 的 Interaction（Hold/Tap）**：用 Unity Input System 自带的 `Hold`/`Tap` 交互配置，框架帮你分辨。省代码，但两种交互的边界、和缓冲/状态机的配合不如手写可控，调试时「为什么没触发」更黑盒。

**选型理由：** 选 B 是因为要「按住到阈值的瞬间就拉弓蓄力」的即时手感（A 做不到即时），同时要对边界**完全可控、可调试**（C 太黑盒）。手写几行计时换来确定性，值。

### 4. 用到哪些技术（含 Unity 组件）

- **Unity Input System**：`InputSystem_Actions`（生成的输入类）、`Attack.performed`（回调）、`Attack.IsPressed()`（轮询）。
- **buffer counter 计时套路**：`AttackBufferCounter` + `Update` 递减（与 Jump/Coyote/Dash 同款）。
- **`Time.deltaTime` 累计**：`_attackHeldTime += Time.deltaTime`。

### 5. 架构设计与拆解

路由全在 `ArcherController.TryStartAttack()`（每帧被共享 GroundedState 调用）：

```csharp
public override bool TryStartAttack()
{
    // ① 按住中 + 有蓄力数据：累计时长，越阈→进蓄力态（不等松手，即时）
    if (_chargeData != null && IsAttackHeld)
    {
        _attackHeldTime += Time.deltaTime;
        if (_attackHeldTime >= _chargeData.TapThreshold)
        {
            _attackHeldTime = 0f; AttackBufferCounter = 0f;
            StateMachine.ChangeState(_chargeAttackState);   // 进蓄力
            return true;
        }
        return false;       // 还在 tap 窗口内，按住等待（先不切状态）
    }

    // ② 已松手（或没配蓄力）：曾经按下过 → 普攻点射
    bool hadPress = _attackHeldTime > 0f || AttackBufferCounter > 0f;
    _attackHeldTime = 0f;
    if (hadPress) { AttackBufferCounter = 0f; StateMachine.ChangeState(_bowAttackState); return true; }
    return false;
}
```

读这段的关键：它**每帧都被调用**。按住期间不停走 ① 累计时长、返回 false（不切状态、停留在地面态等待）；一旦越阈就切蓄力；若在越阈前松手，下一帧走 ② 判定为点按、进普攻。`IsAttackHeld` 是基类暴露的轮询入口（`_inputActions.Player.Attack.IsPressed()`），`AttackBufferCounter` 兜住「快到一帧」的点击。

### 6. 踩坑与 Debug

本功能在设计阶段就把边界想清楚了，落地没踩大坑。值得新手注意的**易错点**：
- 必须在进蓄力/普攻时把 `_attackHeldTime` 和 `AttackBufferCounter` **清零**，否则下一次攻击会带着上次的残留时长，导致「轻点却直接进蓄力」之类的串味。代码里每条出口都清了。
- `TryStartAttack` 返回 `false` 时**不能切状态**——它表示「我还在等待玩家决定点按还是长按」，此时角色应停留在地面态正常移动。把「等待」错误地实现成「进某个中间态」会让角色在按住的前 0.15 秒不能动。

---
---

## 子功能六：蓄得越久越强 —— 蓄力到伤害/箭速的线性映射

> 涉及文件：`ChargeAttackDefinition.cs`（SO，min/max 端点）、`PlayerChargeAttackState.cs`（累计 `_chargeElapsed`、锁定 `_ratio`、`Mathf.Lerp`）

蓄力的灵魂是「投入与回报成正比」：拉弓越久，箭越疼、越快。这要求把「按住时长」换算成「伤害和速度」。

### 1. 需求拆解

- 蓄力时长越长，伤害越高、箭速越快，到满蓄封顶。
- 伤害/速度的最小值（轻蓄）、最大值（满蓄）、蓄满所需时间，都要可配置。
- 松手那一刻**锁定**蓄力程度，之后放箭动画期间不再变化。

### 2. 该功能涉及的基础知识和原理

**归一化比例（normalized ratio）。** 把「已蓄时长」除以「蓄满时长」并 `Clamp01` 到 0~1：`ratio = Clamp01(elapsed / maxChargeTime)`。0 = 没蓄，1 = 满蓄。**把「绝对时长」化成「0~1 的程度」**，是把输入映射到任意输出区间的通用第一步。

**线性插值 `Mathf.Lerp(a, b, t)`。** 当 `t` 从 0 走到 1，返回值从 `a` 平滑走到 `b`：`Lerp(15, 45, 0.5) = 30`。用它把 `ratio` 映射到伤害区间 `[MinDamage, MaxDamage]` 和速度区间 `[MinSpeed, MaxSpeed]`。

**封顶（clamp）。** 蓄过头要按满算，所以 `_chargeElapsed` 累加时 `if (_chargeElapsed > max) _chargeElapsed = max;`，`ratio` 自然不超过 1。

**「锁定」快照。** 松手瞬间把 `ratio` 存进 `_ratio` 字段定住；放箭是在之后的动画帧才发生，那时再用 `_ratio` 算伤害/速度——**决定强度的时刻（松手）和使用强度的时刻（放箭）解耦**。

### 3. 实现该功能有哪些方案（业界常见）

蓄力→数值的映射曲线：
- **A. 线性**（本项目）：`Lerp`，匀速增长。最易懂、最好调，原型首选。
- **B. 曲线（AnimationCurve）**：用 Unity 的 `AnimationCurve` 字段让设计师画任意曲线（先慢后快、阶梯…）。手感空间大，是成熟项目的常见升级。
- **C. 分段/档位**：蓄力分 1/2/3 档，到档位才跳变（很多动作游戏的「蓄力等级」）。反馈更明确（有"叮"的升级感），但不连续。

**选型理由：** 原型阶段要的是「快速可玩 + 好调参」，线性（A）最直接。代码已经把 min/max 端点抽进 SO，将来要升级到曲线（B）只需把两个 `Lerp` 换成 `curve.Evaluate(ratio)`，改动很小——**先线性、留好升级位**。

### 4. 用到哪些技术（含 Unity 组件）

- **`Mathf.Lerp` / `Mathf.Clamp01`**：映射与归一化。
- **ScriptableObject 数据资产**（`ChargeAttackDefinition`）：承载 `MinDamage/MaxDamage/MinSpeed/MaxSpeed/MaxChargeTime/TapThreshold` 等旋钮，设计师在 `.asset` 上调，不碰代码。
- **`Time.deltaTime` 累计**：`_chargeElapsed`。

### 5. 架构设计与拆解

```csharp
// 蓄力期间（每帧，未松手）：累计 + 封顶
_chargeElapsed += Time.deltaTime;
if (_chargeElapsed > data.MaxChargeTime) _chargeElapsed = data.MaxChargeTime;

// 松手瞬间：锁定比例
_ratio = max > 0f ? Mathf.Clamp01(_chargeElapsed / max) : 1f;

// 放箭瞬间：用锁定的比例映射伤害/速度
float damage = Mathf.Lerp(data.MinDamage, data.MaxDamage, _ratio);
float speed  = Mathf.Lerp(data.MinSpeed,  data.MaxSpeed,  _ratio);
```

数据（端点、时间）在 SO，逻辑（累计、锁定、映射）在状态——**数据与逻辑分离**，调手感不动代码。

### 6. 踩坑与 Debug

本功能逻辑直接，未踩运行期坑。新手提醒：`MaxChargeTime` 为 0 会让 `elapsed / max` 除零，代码用 `max > 0f ? ... : 1f` 兜了底（视作满蓄）。这类「除以可配置值」的地方都要想一下「配 0 会怎样」。

---
---

## 子功能七：指哪打哪 —— 屏幕中心射线瞄准

> 涉及文件：`PlayerChargeAttackState.cs`（`HandleAimRotation` / `TryRaycastAim` / `SpawnChargedArrow`）、`ChargeAttackDefinition.cs`（`AimMaxDistance`）、`ArcherController.cs`（`AimMask`）

蓄力重击要「准心对哪、箭就飞哪」，和相机朝向一致（原神弓箭手重击手感）。普攻是沿角色朝向的抛物线，蓄力则要**精确命中屏幕中心准心点的直线**。

### 1. 需求拆解

- 蓄力时角色转向相机的水平朝向（不再受移动方向控制）。
- 箭的方向由「屏幕正中央那个点对着的世界位置」决定，**含俯仰**（能往上/往下射）。
- 箭走**直线**精确命中准心点（不受重力下坠影响）。
- 准心射线**不能打到角色自己**（否则方向算反）。

### 2. 该功能涉及的基础知识和原理

**屏幕/视口 → 世界射线。** `camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0))` 从相机出发，穿过**视口中心**（0.5,0.5 = 正中央，准心所在）射向世界，得到一条 `Ray`（起点 + 方向）。

**射线检测 `Physics.Raycast`。** 沿 `Ray` 投射，命中最近的 Collider 时给出 `RaycastHit`（含 `point` 命中点、`distance`、`collider`）。命中 → 取 `hit.point` 当目标；没命中 → 取射线上 `AimMaxDistance` 处的远点当目标。

**目标点 → 箭方向。** `dir = (targetPoint - 箭生成点).normalized`。注意：方向是从**箭生成点（弓弦）**指向目标点，而不是从相机——这样近距离也不会因相机在身后而算歪（前提是射线别打到自己，见踩坑）。

**LayerMask 与忽略自身。** `LayerMask` 是一个位掩码，告诉 `Raycast` 只检测哪些层。轨道相机在角色**身后**，屏幕中心射线会**先穿过角色自己**——若不处理，命中点落在角色身上。两种解法：① 用 LayerMask 排除 Player 层；② 代码里跳过「属于射手的 Collider」。本项目最终用 ② 做到「与 Mask 配置无关」的稳健。

**直线飞行 = 关重力。** 复用子功能三埋的 `useGravity` 开关：瞄准箭 `Init(..., useGravity:false)`，走直线精确命中；普攻箭 `useGravity:true`，走抛物线。

### 3. 实现该功能有哪些方案（业界常见）

**瞄准模式：**
- **A. 轻量瞄准（不换相机）**（本项目）：不切专用瞄准相机，只让角色转向相机朝向 + 屏幕中心射线定目标。改动小、够用。
- **B. 专用瞄准相机**：进蓄力时切到一个过肩/第一人称瞄准 vcam（拉近、收窄 FOV）。沉浸感强，但要管理相机切换、灵敏度、过渡，工作量大。

**命中精度：**
- **C. 精确命中准心**（本项目）：射线求真实命中点，箭直飞该点。指哪打哪。
- **D. 仅按朝向发射**：沿相机/角色前向发射，不求命中点。简单但近处会偏（方向没对准准心实际指的物体）。

**选型理由：** 原型要「原神式指哪打哪」的**核心手感**，但不想背上相机系统的复杂度——所以 A（轻量）+ C（精确）。专用瞄准相机（B）留作未来体验升级。

### 4. 用到哪些技术（含 Unity 组件）

- **`Camera`**（基类 `MainCamera` 暴露）、**`Camera.ViewportPointToRay`**。
- **`Physics.RaycastNonAlloc`**（见踩坑，零 GC 版）、**`RaycastHit`**、**`LayerMask`**、**`QueryTriggerInteraction.Ignore`**。
- **`Quaternion.Slerp` / `Quaternion.LookRotation`**：蓄力期间平滑转向相机水平朝向。
- **`Transform.IsChildOf`**：判断命中的 Collider 是否属于射手自身。

### 5. 架构设计与拆解

蓄力期间转向相机水平朝向（只 yaw，不含俯仰——身体不该后仰）：

```csharp
private void HandleAimRotation() {
    Vector3 f = cam.transform.forward; f.y = 0f;   // 去掉俯仰，只取水平朝向
    Quaternion target = Quaternion.LookRotation(f);
    _player.transform.rotation = Quaternion.Slerp(_player.transform.rotation, target,
                                                  _player.RotationSpeed * Time.deltaTime);
}
```

放箭时求目标点并发射（含俯仰，所以箭能上下飞）：

```csharp
Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
Vector3 targetPoint = TryRaycastAim(ray, data.AimMaxDistance, out Vector3 p)
    ? p : ray.GetPoint(data.AimMaxDistance);          // 命中点 or 远点
Vector3 dir = (targetPoint - sp.position).normalized; // 从弓弦指向目标（含俯仰）
arrow.Init(team, id, damage, type, dir * speed, _player.CharacterController, useGravity:false); // 直射
```

注意「转向只取水平、射击方向含俯仰」的拆分：身体不后仰（好看），但箭能瞄高瞄低（好用）。

### 6. 踩坑与 Debug

**坑 1（核心 bug）：蓄力箭往身后飞。**
现象：蓄力箭不往准心方向飞，反而朝角色背后射。
排查：方向 `dir = targetPoint - sp.position`。轨道相机在角色**身后**，屏幕中心射线第一个撞到的是**角色自己的 Collider**，于是 `hit.point` 落在角色身体上——而身体在弓弦生成点（在身前）的**后方**，`dir` 自然指向后方。又因为当时 `Aim Mask` 设成了 `Everything`（没排除 Player），射线确实会打到自己。
✅ 修复（稳健、与 Mask 无关）：把 `Physics.Raycast` 换成 `Physics.RaycastNonAlloc` 取**全部**命中，跳过「属于射手的 Collider」后再取最近命中：

```csharp
private bool TryRaycastAim(Ray ray, float maxDistance, out Vector3 point) {
    point = default;
    int count = Physics.RaycastNonAlloc(ray, _aimHits, maxDistance, _archer.AimMask, QueryTriggerInteraction.Ignore);
    float nearest = float.MaxValue; bool found = false;
    for (int i = 0; i < count; i++) {
        if (_aimHits[i].collider.transform.IsChildOf(_player.transform)) continue; // 跳过自己（IsChildOf 含自身）
        if (_aimHits[i].distance < nearest) { nearest = _aimHits[i].distance; point = _aimHits[i].point; found = true; }
    }
    return found;
}
```

为什么不只是「让美术把 Player 层加进排除 Mask」？因为那依赖一个**外部配置永远正确**的隐含前提——忘了配就又反向。代码里剔除自身，无论 Mask 怎么设都对。`_aimHits` 是**预分配**的 `RaycastHit[16]`（`RaycastNonAlloc` 把结果写进现成数组，不产生 GC 垃圾——遵守项目「热路径零分配」铁律；放箭虽非每帧，但预分配是好习惯）。
> 注意：`RaycastNonAlloc` 结果**不保证按距离排序**，所以要自己扫一遍取最近，不能直接拿第 0 个。

---
---

## 子功能八：准心怎么显隐 —— 表现层与 gameplay 的事件解耦

> 涉及文件：`AimStateChangedEvent.cs`（`Game.Core` 事件）、`CrosshairUI.cs`（`Game.Rendering` 订阅）、`PlayerChargeAttackState.cs`（发布）

进蓄力显示准心、退蓄力隐藏准心。问题是：负责攻击的代码在 `Game.Character`，准心 UI 在 `Game.Rendering`——按架构规矩，**gameplay 不许认识 UI**。怎么让 UI 在该显的时候显？

### 1. 需求拆解

- 进入蓄力瞄准 → 显示准心；退出 → 隐藏。
- 攻击逻辑**不能直接引用** UI（不能 `crosshair.SetActive(true)`）。
- 退出的所有路径（正常放完、被打断…）都要确保隐藏，不残留。

### 2. 该功能涉及的基础知识和原理

**模块隔离与单向依赖。** 本项目每个域是独立 asmdef，规矩是「上层可依赖下层，下层绝不依赖上层；跨模块只走 `Game.Core` 的 EventBus」。`Game.Rendering`（UI）依赖 `Game.Core`，但 `Game.Character`（gameplay）**不依赖** `Game.Rendering`。所以攻击代码不可能直接调 UI。

**事件总线（EventBus）/ 发布-订阅。** 解耦的标准答案：gameplay **发布**一个事件（「我开始瞄准了」），UI **订阅**这个事件、自己响应。双方都只认识 `Game.Core` 里那个事件结构体，**互不认识对方**。这就是「观察者模式」的工程化形态。

**为什么事件是 `struct` 不是 `class`。** `EventBus<T>` 约束 `T : struct, IGameEvent`，发布时按值传递、**零装箱、零 GC**。这是项目对 EventBus 的硬性要求。

### 3. 实现该功能有哪些方案（业界常见）

- **A. EventBus 发布-订阅**（本项目）：最干净的解耦，gameplay 发事件、UI 自订阅。多个表现（准心、音效、镜头抖动）能各自独立订阅同一事件，互不干扰。
- **B. 直接引用 UI**：攻击代码持有 `CrosshairUI` 引用直接 `SetActive`。最快，但把 UI 焊死进 gameplay，违反单向依赖，换 UI/加表现就要改 gameplay。
- **C. 轮询状态**：UI 每帧去问「角色在不在蓄力」。要 UI 反向依赖 gameplay（又违规），且每帧轮询浪费。

**选型理由：** A 是项目既定架构的唯一合规解，也是最可扩展的——以后「瞄准时压低 FOV」「播拉弓音效」都只需再写个订阅者，碰都不用碰攻击代码。

### 4. 用到哪些技术（含 Unity 组件）

- **`EventBus<T>` + `IGameEvent`**（`Game.Core`）：`Publish` / `Subscribe` / `Unsubscribe`。
- **`struct AimStateChangedEvent { public bool Active; }`**：零 GC 事件载荷。
- **MonoBehaviour 生命周期**：`OnEnable` 订阅 / `OnDisable` 退订（防止悬挂订阅泄漏）。
- **`GameObject.SetActive`**：刻意只切一个 GameObject 显隐——这样 `CrosshairUI` **无需引用 UGUI 程序集**（不碰 `UnityEngine.UI` 类型），依赖最小。

### 5. 架构设计与拆解

```
PlayerChargeAttackState.Enter()  ── Publish ──►  EventBus<AimStateChangedEvent>{Active=true}
PlayerChargeAttackState.Exit()   ── Publish ──►  EventBus<AimStateChangedEvent>{Active=false}
                                                        │
                              CrosshairUI（OnEnable 订阅）◄┘ → _crosshair.SetActive(e.Active)
```

发布方（攻击状态）：

```csharp
public override void Enter() { /* …CrossFade 拉弓… */
    EventBus<AimStateChangedEvent>.Publish(new AimStateChangedEvent { Active = true }); }
public override void Exit()  {
    EventBus<AimStateChangedEvent>.Publish(new AimStateChangedEvent { Active = false }); }
```

订阅方（UI，`Game.Rendering`）：

```csharp
private void OnEnable()  => EventBus<AimStateChangedEvent>.Subscribe(OnAimStateChanged);
private void OnDisable() => EventBus<AimStateChangedEvent>.Unsubscribe(OnAimStateChanged);
private void OnAimStateChanged(AimStateChangedEvent e) { if (_crosshair != null) _crosshair.SetActive(e.Active); }
```

**为什么把隐藏放在 `Exit()`：** 状态机切换**必然**经过 `Exit()`（`ChangeState` 是 `Exit→swap→Enter`）。所以不管蓄力是正常放完、还是将来被「受击打断」之类的新路径中断，只要离开蓄力态就一定会发 `Active=false`——准心**绝不会残留**。这和冲刺「在 Exit 启动冷却」是同一个「Exit 是唯一收尾点」的设计直觉。

### 6. 踩坑与 Debug

本功能按架构既定模式实现，未踩坑。新手须知两个**纪律点**：① 订阅/退订必须成对放在 `OnEnable`/`OnDisable`，漏退订会在对象销毁后仍被回调（悬挂引用/泄漏）；② 收尾（发 `Active=false`）放在 `Exit()` 而不是「放箭结束那一处」，才能覆盖所有离开路径——把收尾绑在「唯一必经的出口」上，是状态机里反复出现的可靠性套路。

---
---

## 子功能九：边走边射 —— 攻击态下的「移动权」（bug 修复专题）

> 涉及文件：`PlayerBowAttackState.cs`（`HandleMovement` 改为完整移动 + 加 `HandleRotation`）

这是弓箭手最后修的一个手感 bug，单独成节，因为它讲清了一个新手极易踩的概念：**「动画在播」和「角色在动」是两件独立的事。**

### 1. 需求拆解

- 普攻时希望「边走边射」，不必停下来才能射击。
- 站着不动射击时，行为照旧（沿当前朝向射）。

### 2. 该功能涉及的基础知识和原理

**移动动画 ≠ 实际位移。** 角色的位移来自 `CharacterController.Move(velocity)` 这行代码；而「播不播 run 动画」由 Animator 的 `speed` 参数决定。这两者**各走各的**：`speed` 由 `SyncAnimatorParameters` 每帧按输入设置（和当前状态无关），位移则由**当前状态的 `HandleMovement`** 决定。所以完全可能「动画在跑、人没动」——只要状态的 `HandleMovement` 没真正施加水平速度。

**「锁脚」是一种主动设计。** 近战挥砍通常**故意**锁住移动（`HandleMovement` 只施加垂直速度、丢弃水平输入），让攻击有「定身」的力量感。普攻 bow 状态最初照抄了这个锁脚逻辑——于是普攻时不能走。

### 3. 实现该功能有哪些方案（业界常见）

攻击时的移动权，业界常见档位：
- **A. 完全锁脚**：攻击期间不能移动（重攻击、近战连段常用，强调力量/承诺感）。
- **B. 完全自由移动**（本项目普攻选这个）：攻击期间用和平时一样的移动逻辑（弓箭手轻射、风筝流常用）。
- **C. 减速移动**：攻击期间能动但速度打折（很多 MMO 的施法移动）。
- **进阶：上下半身分层（Animator Layer + Avatar Mask）**：下半身播跑步、上半身播射箭，真正的「跑射分离」。表现最好，但要配遮罩和分层，复杂度高。

**选型理由：** 弓箭手定位是灵活的远程风筝手，普攻给**完全自由移动（B）**最贴定位、改动也最小（复用地面态的移动逻辑）。当前普攻动画是全身的，跑射姿态融合（分层方案）属于后续美术增强，原型阶段不做。

### 4. 用到哪些技术（含 Unity 组件）

- **`CharacterController.Move`**：施加完整水平+垂直速度。
- **复用基类 `HandleRotation()`**：随移动方向平滑转向。
- **`MoveDirection` / `MoveSpeed` / `VerticalVelocity`**：基类暴露的移动数据。

### 5. 架构设计与拆解

把普攻状态的 `HandleMovement` 从「只给垂直速度（锁脚）」改成「和地面态一样的完整移动」，并在 `Update` 里补一句转向：

```csharp
public override void Update() {
    HandleGravity();
    HandleMovement();      // 边走边射：完整水平移动（不再锁脚）
    base.HandleRotation(); // 随移动方向转向；箭在 ArrowSpawnTime 沿当前朝向射出
    HandleArrowSpawn();
    CheckCombo();
}
private void HandleMovement() {
    Vector3 velocity = _player.MoveDirection * _player.MoveSpeed; // 关键：恢复水平分量
    velocity.y = _player.VerticalVelocity;
    _player.CharacterController.Move(velocity * Time.deltaTime);
}
```

加 `HandleRotation` 是为了让「边走边射」时箭跟着移动朝向走（无移动输入时 `HandleRotation` 直接返回，保持原朝向，站定射击行为不变）。

### 6. 踩坑与 Debug

**坑：普攻后零点几秒「能出 run 动画却走不动」。**
现象：点按射箭后短时间内，按移动键只播 run 动画、人在原地不动。
排查：普攻进了 `PlayerBowAttackState`，它的 `HandleMovement` 当时只施加垂直速度（锁脚，照抄近战）；而 `SyncAnimatorParameters` 仍每帧按输入把 `speed` 设成正值——于是 **Animator 显示 run（speed>0），但状态没给水平位移**，表现为「动画在跑、人没动」。
✅ 修复：如上，恢复水平移动分量 + 加转向。
**通用教训：当你看到「动画对、位移不对」或反过来，先分清是哪一路出问题——动画看 Animator 参数（`SyncAnimatorParameters`），位移看当前状态的 `HandleMovement`。两者解耦，要分开 debug。**

---
---

## 贯穿全程的设计套路总结（最该带走的东西）

把弓箭手拆完，你会发现真正反复出现的不是「弓箭」知识，而是几条**可迁移的工程套路**。做下一个角色/技能时直接套：

1. **共享放基类、差异放子类，用虚方法（seam）留接缝。** 让「优先级、流程骨架」这种共性只写一次，「具体干啥」交给子类。`TryStartAttack()` 是范例。

2. **「怎么发现」可以多样，「之后怎么做」必须统一。** 近战 OverlapBox、远程 OnCollisionEnter，发现命中的方式不同，但都汇入同一条 `ReceiveHit`/`DamagePipeline`。新增攻击形态时，先问「我能不能复用那条管线」。

3. **数据驱动：把会调的东西抽进 Inspector/ScriptableObject。** 动画状态名、伤害/速度端点、阈值时间——代码只认字段，配不同值跑不同表现。好处是加角色不改代码，代价是配错时静默失败（要配 `GameLog.Warn` 自曝）。

4. **时序统一读 `normalizedTime` 比阈值，不用 Animation Event。** 命中窗口、连段窗口、刀光、箭矢生成全用一套心智模型。单点触发记得配「只触发一次」的 `bool` 守卫 + 排除过渡帧。

5. **buffer counter 计时套路：输入与动作解耦。** 按键只登记意图（写一个会过期的计数器），状态机在自己的节奏里读取。Jump/Coyote/Attack/Dash/蓄力全是它。

6. **状态机的收尾放在 `Exit()`——唯一必经出口。** 冷却启动、准心隐藏、清缓冲，都挂在 `Exit`，就覆盖了所有离开路径（包括未来新增的打断），不会漏。

7. **表现层只订阅事件，gameplay 永不认识表现。** 准心、音效、镜头都靠订阅 EventBus 的 `struct` 事件接入，gameplay 只管广播。

8. **分清「动画」和「逻辑」两条独立的线。** 动画播什么（Animator 参数/状态）和角色实际怎么动/怎么结算（C# 状态逻辑）是解耦的两路——出 bug 先判断是哪一路，分开 debug。

9. **`LookRotation` 永远对齐 +Z；模型不是 +Z 朝前就加修正旋转。** 箭、武器、任何要朝向某方向的物体都适用。

10. **改 MonoBehaviour 脚本名：`.cs` 和 `.meta` 一起 `git mv`，保住 GUID。** 否则预制体引用全丢。这是 Unity 工程纪律，不是代码逻辑，但忘了它损失最惨。

> 把这十条记住，你已经掌握了本项目战斗模块「怎么想」的内核。具体的 API、字段名都能查、能忘，但这套**拆解问题 → 选型 → 留好扩展位**的思路，才是战斗设计的真功夫。
