# 冲刺（Dash）核心循环 — 设计解析

> 目标读者：有 C# 基础、**零战斗/3C（Character / Camera / Control）系统经验**的开发者。
> 本文讲解「地面冲刺」这个角色移动技能从**需求 → 原理 → 业界方案 → 选型 → 落地**的完整思路，目的不是让你记住代码长什么样，而是让你理解**为什么这样设计是对的**，从而能把这些套路迁移到别的功能上。
>
> 阅读顺序：先看「全局心智模型」建立整条数据流的直觉；再按子功能逐个精读。每个子功能都用同一套**五维分析**，彼此独立，可单独跳读。
>

---

## 全局心智模型

很多人以为「冲刺」就是「按一下键，角色往前飞一段」。但在一个有状态机的角色系统里，它其实是**一条从输入到位移、再到冷却上锁的责任链**。先把这条链记住：

```
   按下 Dash 键（鼠标右键 / 左 Shift）
        │  Input System 在按下那一帧回调一次
        ▼
   OnDashPerformed ─────► DashBufferCounter = 0.15s
        │                 （只登记"我想冲"这个意图，不立即冲）
        │
        │  PlayerController.Update 每帧把
        │  DashBufferCounter / DashCooldownCounter 各减 Time.deltaTime
        ▼
   PlayerGroundedState.CheckTransition（每帧执行，且只有"地面态"才有这段）
        │  闸门 = DashBufferCounter > 0  且  DashCooldownCounter <= 0
        │         （有意图       ∧      能力不在冷却）
        │  优先级 = Dash → Attack → Jump（顺序靠前者赢）
        ▼  命中闸门 → StateMachine.ChangeState(DashState)
   PlayerDashState
        ├─ Enter : 锁定方向(=transform.forward) + 清缓冲 + CrossFade 冲刺动画
        ├─ Update: 沿锁定方向 CharacterController.Move + 计时(_elapsed)
        │          _elapsed ≥ DashDuration → 收招
        └─ Exit  : DashCooldownCounter = DashCooldown   ← 冷却的唯一启动点
        │  收招走 TransitionToMovement → 做"落点判定"
        ▼
   GroundedState（落点在地面）   /   AirborneState（落点悬空）
```

**三个最重要的设计直觉，先记住：**

1. **输入和动作是解耦的。** 按键那一刻不直接执行冲刺，而是往一个倒计时计数器里写下「最近有人想冲」。真正决定冲不冲、什么时候冲，是状态机在它自己的节奏里读取这个计数器。这套「buffer counter（输入缓冲计数器）」模式，是本项目已经用过四次的同一招（见文末总结）。

2. **冷却是「离开时上锁」，只有一个上锁点。** 冷却计时器不在按键时启动、不在进入冲刺时启动，而是在**离开冲刺状态（Exit）**时启动。因为状态机切换必然经过 `Exit`，这一个点就覆盖了所有离开路径——再也不用去每条分支里补「记得启动冷却」。

3. **「能不能冲」由两个独立的计数器把关。** 一个管「最近想不想冲」（buffer），一个管「能力在不在冷却」（cooldown）。它们互不知道对方存在，组合起来才放行。这种「正交（orthogonal）」拆分让每个计数器都只干一件简单的事。

下面六个子功能，就是把这条链拆开逐段讲。

---
---

## 子功能一：冲刺参数与计时器设计

> 涉及文件：`PlayerController.cs`（`_dashSpeed/_dashDuration/_dashCooldown/_dashBufferTime` 字段、对应只读属性、`DashCooldownCounter/DashBufferCounter` 计数器、`Update` 递减块）

这是整个冲刺的「数据底座」：三个可调旋钮（速度、时长、冷却）和两个运行期计数器（冷却剩余、缓冲剩余）。其它子功能都从这里取数。

### 1. 需求拆解与验收标准

要解决的具体问题：把冲刺的**手感参数**和**运行期状态**找个合适的地方安放，让设计师能调、让逻辑能读。

- 需要三个**编译期可调、运行期只读**的旋钮：水平速度、持续时长（秒）、冷却时长（秒）。
- 需要两个**运行期可变**的计数器：冷却还剩多久（`DashCooldownCounter`）、缓冲意图还剩多久（`DashBufferCounter`）。
- 计数器必须**与当前是哪个状态无关地持续倒计时**——冷却不能因为你切到了别的状态就停。

验收标准：
- 冷却生效、一个冷却周期内只能冲一次（对应 Task 4 Step 3 第 ② 项，**待验证**）。
- 速度/时长/冷却/缓冲四个值能在 Inspector 上直接拖动调整（**补充**，源自「裸字段」选型的直接结果）。

### 2. 该功能涉及的基础知识和原理

**`[SerializeField] private` 与 Inspector 暴露。** Unity 的序列化系统会把带 `[SerializeField]` 的私有字段存进场景/预制体，并在 Inspector 显示成可调控件。这让「数据」脱离代码、交给设计师调，是 Unity「data-driven」的最小形态。`private + [SerializeField]` 比 `public` 更好：外部代码不能乱改，只有 Inspector 和本类能写。

**计数器（counter）模式的本质。** 一个 `float` 计数器存的是「这件事还剩多少秒有效」。每帧 `counter -= Time.deltaTime` 让它自然衰减，`counter > 0` 表示「还在有效期内」，把它设成 0 表示「消耗/作废」。它本质是一个**会自己过期的布尔信号**——比单纯的 `bool` 多了「时间维度」。

**为什么计数器要集中在 `Update` 里每帧递减。** 冷却和缓冲是**全局事实**：无论角色此刻在地面、空中还是攻击中，冷却都该继续走。如果把递减写进某个具体 State 的 `Update`，那这个 State 不活跃时计时就停了——这是个隐蔽 bug 源。所以本项目把所有这类计数器（Jump Buffer / Coyote Time / Attack Buffer / Dash 两个）统一放在 `PlayerController.Update` 递减，State 只负责**读**它们做判断，不负责**维护**。

**只读属性 vs `{ get; set; }`。** `public float DashSpeed => _dashSpeed;` 是只读的——State 能读冲刺速度但改不了它（旋钮归 Controller / 设计师）。而 `public float DashCooldownCounter { get; set; }` 是可读可写的——因为 `DashDashState.Exit` 要给它赋值、`Update` 要递减它。**给每个成员恰好够用的可见性**，是封装的日常体现。

### 3. 实现该功能有哪些方案（游戏开发业界常见的实现方案）

**参数承载方式：**
- **裸字段（inline serialized fields）**：参数直接挂在角色脚本上。适合「这个能力只有这一个主体、只有一套配置」的场景。
- **ScriptableObject（数据资产）**：把参数抽成独立 `.asset` 文件。适合「多个角色/武器共享或各有一套、设计师要不改 GameObject 就换数据、别的模块也要消费这份数据」的场景（本项目的 `AttackDefinition`/`ComboDefinition` 就是这么做的）。
- **常量硬编码**：直接写死在代码里。只适合永不调整的值。

**计时器存放方式：**
- **集中在角色控制器每帧递减**（本项目惯例）。
- **各状态自持、自己递减**：计时器跟着状态走，状态不活跃就不走（容易漏）。
- **全局时间/技能管理器**：一个单例统一管理所有冷却（MMO/MOBA 技能多时常见，杀鸡用牛刀于单个冲刺）。

### 4. 方案与技术选择

**最终选择：裸字段 + 计数器集中在 `PlayerController.Update` 递减。**

| 维度 | 裸字段（本项目选用，对照 Jump 先例） | ScriptableObject（对照 Attack 先例） | 常量硬编码 |
|------|-----------------------------------|-----------------------------------|-----------|
| 谁能调 | 设计师在 Inspector 调 | 设计师在资产里调 | 只有程序员改代码 |
| 多套配置 | 只支持一套（够用） | 天然支持多套/共享 | 不支持 |
| 接线成本 | 0（字段就在脚本上） | 要建 `.asset`、拖引用 | 0 |
| 与现有一致性 | 与 `_jumpForce/_coyoteTime` 同层并列 | 与战斗数据同构但本能力用不上 | 破坏可调性 |
| 适用判断 | ✅ 冲刺是「和跳跃同层的单一角色能力」 | 杀鸡用牛刀（无多实例需求） | ❌ 失去手感调参能力 |

选裸字段的核心理由：冲刺在概念上和「跳跃」是同一层的东西——单一主体、一套配置、需要频繁调手感。沿用 Jump 的裸字段先例，让所有「移动能力旋钮」并排放在一起，零额外接线。如果将来真出现「多个角色各有不同冲刺档案」，再把这几个字段抽进 ScriptableObject 也只是局部重构——这是「YAGNI（你不会需要它）」原则：不为假想的未来提前买单。

计时器集中递减，则是直接复用项目里已被三个计数器（Jump/Coyote/Attack）验证过的惯例，保证「全局事实全局维护」。

### 5. 架构设计与拆解

数据所有权一目了然：**`PlayerController` 拥有全部参数和计数器；各 State 只读参数、读写计数器。**

```
PlayerController（数据底座）
├─ 旋钮（[SerializeField]，只读对外）
│    _dashSpeed = 14   _dashDuration = 0.2   _dashCooldown = 1   _dashBufferTime = 0.15
│        └─► DashSpeed / DashDuration / DashCooldown / DashBufferTime（只读属性）
│
├─ 计数器（{ get; set; }，可读写对外）
│    DashCooldownCounter   ← Exit 写入、Update 递减、CheckTransition 读
│    DashBufferCounter     ← OnDashPerformed 写入、Enter 清零、Update 递减、CheckTransition 读
│
└─ Update() 每帧递减块（与现有三计时器并列）
     if (DashCooldownCounter > 0f) DashCooldownCounter -= Time.deltaTime;
     if (DashBufferCounter   > 0f) DashBufferCounter   -= Time.deltaTime;
```

注意一个细节：四个旋钮目前是**纯 `[SerializeField] float`，没有加 `[Range]`**——和战斗里 `AttackDefinition` 那种 `[Range(0,1)]` 的归一化窗口不同，冲刺这几个是**真实物理量**（米/秒、秒），没有天然的 0~1 上下界，所以不加 Range。这是「字段语义决定了要不要约束控件」的一个小例子。

---
---

## 子功能二：位移与方向锁定的实现

> 涉及文件：`PlayerDashState.cs`（`Enter` 锁方向、`HandleDashMovement`、`Update` 计时）

这是冲刺的「肉体」：让角色沿**按下瞬间的朝向**、以恒定高速、平移固定时长，期间无视移动输入、不转向。

### 1. 需求拆解与验收标准

要解决的具体问题：产生一段**方向锁定、速度恒定、时长固定**的水平位移，且全程零内存分配。

- 方向 = 进入冲刺瞬间角色的正前方（轨道相机风格，单一前向冲刺，本轮不做八方向）。
- 进入冲刺后方向**锁死**：期间转动相机/推摇杆都不改变冲刺方向。
- 位移持续 `_dashDuration` 秒后自然结束。

验收标准：
- 地面按键 → 角色沿当前面朝方向前冲（Task 4 Step 3 第 ① 项，**待验证**）。
- 反复冲刺时 Profiler 中 `PlayerDashState.Update` 路径 **零 GC Alloc**（第 ⑦ 项，**待验证**，但可由静态分析预判，见维度 5）。
- 朝悬崖冲 → 水平冲完整段不被中途打断（第 ③ 项的位移部分，**待验证**；落点判定归子功能六）。

### 2. 该功能涉及的基础知识和原理

**`CharacterController.Move` 与另外两种位移的本质区别。** 这是 3C 里最该搞懂的一点：

| 位移方式 | 本质 | 碰撞 | 驱动循环 |
|---------|------|------|---------|
| `transform.position +=` | 直接瞬移坐标 | **无**（会穿墙穿地） | 任意 |
| `Rigidbody.AddForce` | 进物理引擎，按牛顿力学积分 | 有（物理碰撞响应） | **必须 FixedUpdate** |
| `CharacterController.Move` | 引擎内置的 **collide-and-slide（碰撞即滑动）** 算法 | 有（但不走物理管线） | **必须 Update** |

`CharacterController.Move(delta)` 接收一个「这一帧想移动多少」的位移向量，引擎帮你做「撞墙就贴着墙滑、踩台阶就抬上去、超过坡度就挡住」，但**不参与刚体物理**（不会被别的力推、不积累动量）。它要在 `Update` 调用（不是 `FixedUpdate`），因为它不属于物理步长循环——放错地方会导致输入延迟。冲刺、跳跃、走路在本项目里全走它，位移方式统一。

**为什么乘 `Time.deltaTime`。** `_dashSpeed`（14）是「米/秒」，但每帧只过了 `Time.deltaTime` 秒（60fps 时约 0.0167 秒）。所以这一帧该移动的距离 = 速度 × 这一帧的时长 = `_dashSpeed * Time.deltaTime`。不乘的话，冲刺速度会随帧率变化（高帧率冲得远）——乘 `deltaTime` 是**帧率无关**的基础保证。

**世界空间方向与 `transform.forward`。** `transform.forward` 是角色当前朝向在**世界坐标系**下的单位向量（蓝色 Z 轴指向）。冲刺要的是世界空间的位移方向，所以直接取它。取完把 `y` 清零（只要水平分量，冲刺不该有上下漂）再 `Normalize()`（重新归一化成长度 1，否则清零后长度会变短，速度就不准了）。

**向量归一化与零向量退化（`sqrMagnitude` 陷阱）。** `Normalize()` 对一个接近零的向量会返回 `Vector3.zero`。理论上如果某帧 `transform.forward` 恰好纯竖直（x、z 都≈0），清零 y 后就是零向量，归一化得到零方向 → 「原地冲刺却照样进冷却」。对一个直立的 CharacterController 人形来说这**不可达**（角色不会脸朝天/朝地），所以代码当前没加兜底。这属于「理论边界、当前场景到不了」的取舍——知道它存在，但不为到不了的情况加代码（**待验证**，可选加固：`if (_dashDirection.sqrMagnitude < 1e-4f) ...` 给个回退方向）。

### 3. 实现该功能有哪些方案（游戏开发业界常见的实现方案）

**位移驱动方式：**
- **代码直接 `Move`**：每帧用代码算位移喂给 `CharacterController.Move`。可控、确定、易调试。
- **Root Motion（动画位移）**：让冲刺动画里「角色根骨骼的位移」直接驱动移动。动画师做什么位移，角色就走什么——贴合动画、但难精确控制距离/速度，且和代码位移混用易打架。
- **物理冲量 `AddForce`**：给刚体一个瞬时冲量让它滑出去。手感「物理」、会自然受摩擦/碰撞影响，但难精确控制、且本项目用 CharacterController 不用 Rigidbody。

**方向决定方式：**
- **锁定朝向**：进入瞬间锁死方向（本项目）。
- **实时跟随摇杆**：冲刺中可被移动输入持续转向（更灵活、也更难控、易做出「绕圈冲」）。
- **八方向网格化**：把输入吸附到 8 个固定方向（老式 2D/格斗常见）。

**速度曲线：**
- **恒定速度**（本项目）；**缓动（ease in/out）**：起步加速、收尾减速，手感更「重」。

### 4. 方案与技术选择

**最终选择：代码直接 `Move` + 进入时锁定 `transform.forward` + 恒定速度。**

| 维度 | 代码 Move（选用） | Root Motion | 物理 AddForce |
|------|------------------|-------------|---------------|
| 距离/速度可控性 | ✅ 精确（速度×时长） | 取决于动画，难调 | 受摩擦/质量影响，难调 |
| 与现有移动一致 | ✅ Jump/走路/攻击都用 Move | 需引入 Root Motion 管线 | 需引入 Rigidbody |
| 确定性/可调试 | ✅ 纯代码、可静态推演 | 依赖动画资产 | 依赖物理求解 |
| 硬约束契合 | ✅ 不引入新依赖 | — | ❌ 与 CharacterController 冲突 |

锁定朝向而非实时跟随，是本轮明确的游戏设定（轨道相机风格、单一前冲）；恒定速度是最简实现，手感不足时未来再加缓动。整体选型遵循一条主线：**与项目已有的位移范式（CharacterController.Move）保持一致，不为一个能力引入新管线**。

### 5. 架构设计与拆解

```
PlayerDashState.Enter()
   _dashDirection = transform.forward; _dashDirection.y = 0; _dashDirection.Normalize();
        └─ 锁方向：之后整段冲刺都用这个缓存的 _dashDirection，绝不再读 transform.forward

PlayerDashState.Update()              （每帧）
   ├─ HandleDashMovement()
   │     if (VerticalVelocity < 0) VerticalVelocity = -2;     // 贴地压力，保持 CC 贴地/离地判定有效
   │     Vector3 velocity = _dashDirection * DashSpeed;        // 水平：锁定方向 × 速度
   │     velocity.y = VerticalVelocity;                        // 垂直：贴地分量
   │     CharacterController.Move(velocity * Time.deltaTime);   // 一次 Move 搞定碰撞
   ├─ _elapsed += Time.deltaTime;
   └─ if (_elapsed >= DashDuration) TransitionToMovement();    // 时间到 → 收招（落点判定见子功能六）

   注意：全程不调用 base.HandleRotation()  → 方向锁定（不转向）
```

**零 GC Alloc 的静态论证（为什么不用跑 Profiler 也能预判）：** `HandleDashMovement` 里 `_dashDirection * DashSpeed`、`velocity.y = ...`、`velocity * Time.deltaTime` 全是 `Vector3` **值类型（struct）** 运算，`velocity` 是栈上局部变量；没有 `new` 对象、没有 LINQ、没有装箱（boxing）。所以这条每帧热路径不产生堆分配。Profiler 实测仍标 **待验证**，但代码层面已无分配点。

**贴地压力 `-2` 的复用。** 这段「`VerticalVelocity < 0` 就压到 `-2`」和攻击状态 `PlayerAttackState` 里一模一样——给一个微小向下速度，让 `CharacterController` 持续「踩」在地面上，离地检测才准。这个 `-2` 是两处共用的魔法数字（评审提过可抽成共享常量，非必须）。

---
---

## 子功能三：动画接入方式（CrossFade 驱动）

> 涉及文件：`PlayerDashState.cs`（`DashStateHash`、`CrossFadeDuration`、`Enter` 里的 `CrossFadeInFixedTime`）；Animator 接线属 Task 4（**待接线**）

让冲刺动作在视觉上播出来，并在结束后回到移动动画，且切换手感稳定。

### 1. 需求拆解与验收标准

要解决的具体问题：进入冲刺状态时切到冲刺动画片段，结束后回到 locomotion（移动）动画，切换过渡时长稳定、不受动画播放速度影响。

验收标准：
- 冲刺时播放冲刺动画，结束后平滑回到移动/待机动画（**补充**，**待验证**）。
- 「逻辑退出冲刺」与「动画收尾」大致同步——不出现「C# 已退出冲刺态、动画还在播冲刺」的错位（**补充**，**待验证**；根因见维度 2 的单位陷阱）。

### 2. 该功能涉及的基础知识和原理

**Animator 状态机与「状态（state）」。** 角色动画由一张 Animator 状态机图驱动，每个「状态节点」绑一个动画片段（clip）。平时靠**连线（Transition）+ 条件/参数**在节点间跳转。

**CrossFade 的原理。** `Animator.CrossFadeInFixedTime(stateHash, duration, layer)` 是**用代码直接命令 Animator「在 `duration` 秒内混合过渡到指定状态」**，绕过状态机图里的连线和条件。它在「当前动画」和「目标动画」之间做加权混合（前者权重降到 0、后者升到 1），所以切换是平滑的而非硬切。

**`CrossFadeInFixedTime` vs `CrossFade`：固定秒数 vs 归一化时长。** 普通 `CrossFade` 的过渡时长是**归一化的**（相对目标动画长度的比例）；`CrossFadeInFixedTime` 的时长是**真实秒数**。冲刺要的是「无论动画多长，过渡都恒定 0.1 秒」的稳定手感，所以用 FixedTime 版本。

**state name hash vs parameter hash。** 这里 `StringToHash("DashForward_SingleTwohandSword")` 算的是**状态节点名**的哈希（喂给 CrossFade）；而跳跃用的 `StringToHash("jump")` 算的是 **Animator 参数名**的哈希（喂给 `SetTrigger`）。两者都是 hash，但指向不同东西——别混淆。

**为什么 hash 要预缓存（`static readonly`）。** `Animator.StringToHash` 每次都要算字符串哈希。如果每帧/每次冲刺都现算，是无谓开销。`private static readonly int DashStateHash = ...` 让它在**类型加载时只算一次**，之后永远复用——这是和 `PlayerStateBase.JumpHash` 完全一致的模式。字符串只出现在这一处，热路径上只有一个 `int`。

**normalizedTime（归一化时间）与真实秒数（`_dashDuration`）的单位陷阱。** 这是本子功能最该警惕的坑：
- 代码侧用 **`_elapsed`（真实秒数）** 计时，到 `_dashDuration`（0.2 秒真实时间）就退出状态。
- Animator 侧的返回连线用 **`Has Exit Time`（归一化进度 0~1）** 决定动画何时回 locomotion。
- 两者**单位不同**：`Has Exit Time = 0.8` 对应的真实时间 = `0.8 × clip 时长`。如果这个真实时间明显 > `_dashDuration`，就会出现「逻辑早退出了、动画还在播冲刺」的错位。
- 这和连段系统里 `normalizedTime`（归一化）与真实秒数不一致是**同一类问题**。解决办法：接线时先查 clip 真实长度，让 `Has Exit Time × clip时长 ≈ _dashDuration`（Task 4，**待验证**）。

### 3. 实现该功能有哪些方案（游戏开发业界常见的实现方案）

**进入动画的方式：**
- **`SetTrigger` + Transition 连线**：在 Animator 图里连一条带 trigger 条件的进入连线，代码触发 trigger（本项目的 Jump 用法）。图里看得见、但要手工连线，且 `Any State` 连线易导致动画被反复重触发。
- **`CrossFade` 代码直驱**（本项目 Dash/Combo 用法）：代码点名目标状态直接过渡，Animator 侧只需放一个状态节点、无需进入连线。
- **Playable API**：完全用代码构建动画图，最灵活也最重，适合复杂程序化动画。

**返回 locomotion 的方式：**
- **`Has Exit Time` 连线**：动画播到某进度自动回（本项目 Dash 计划用法）。
- **代码再 CrossFade 回去**：退出时再点名 locomotion。
- **状态条件连线**：靠 `speed/isGrounded` 等参数自动回（本项目走路/跳跃落地用法）。

### 4. 方案与技术选择

**最终选择：进入用 `CrossFadeInFixedTime`，返回用 `Has Exit Time` 连线。**

| 维度 | CrossFade（Dash 选用） | SetTrigger + Transition（Jump 先例） |
|------|----------------------|-----------------------------------|
| Animator 接线量 | 只放一个状态节点（无进入连线、无参数） | 要加参数 + 一条带条件的进入连线 |
| 进入确定性 | ✅ 代码点名目标，确定 | 依赖图里的条件判定 |
| 失败模式 | 状态名字符串须与节点名精确一致 | `Any State→Dash` 易重复触发冲刺动画 |
| 逻辑内聚 | ✅ 进入逻辑和冷却/计时同在代码层 | 进入分散在 Animator 图里 |

为什么不因为「Dash 是单一动作、结构像 Jump」就抄 Trigger？因为 CrossFade 在 Animator 侧**更少的可动部件**（零参数、零进入连线）反而更不容易出「Any-State 重触发」这类经典 dash bug，且进入逻辑留在代码层、和冷却/计时同处一处，内聚更好。代价仅是「字符串名必须和节点名一致」——这是运行期唯一静态检查覆盖不到的风险点，已在代码注释和 Task 4 清单里重点标注。

### 5. 架构设计与拆解

```
PlayerDashState（代码侧）
   private static readonly int DashStateHash =
       Animator.StringToHash("DashForward_SingleTwohandSword");   ← 类型加载时算一次
   private const float CrossFadeDuration = 0.1f;                  ← 固定过渡秒数

   Enter():
      Animator.CrossFadeInFixedTime(DashStateHash, CrossFadeDuration, 0);
                                     │            │                 └ layer 0
                                     │            └ 0.1 秒真实过渡
                                     └ 目标状态（按 hash 点名）

Animator Controller（资产侧，Task 4 待接线）
   [状态节点] DashForward_SingleTwohandSword   ← 名字必须 = 上面字符串
        │  Has Exit Time（按 clip 真实长度换算，≈ _dashDuration）
        ▼
   [locomotion / 移动 Blend Tree]
   ⚠️ 不要加 Any State → Dash 连线（进入只由代码 CrossFade 驱动）
```

**模块边界提醒**：动画名字符串 `"DashForward_SingleTwohandSword"` 写在 `Game.Character` 的 `PlayerDashState` 里，`StringToHash` 也在这一层做——`Game.Combat` 完全不碰 Animator。这和连段系统「动画名作为纯字符串数据放 Combat、但 hash 化推到 Character 侧」是同一条边界纪律（冲刺更简单，因为它整个都在 Character，不跨模块）。

---
---

## 子功能四：冷却与输入缓冲的正交设计

> 涉及文件：`PlayerController.cs`（`OnDashPerformed`、两个计数器、`Update` 递减）；`PlayerGroundedState.cs`（闸门条件）；`PlayerDashState.cs`（`Exit` 不清缓冲）

这是冲刺最「精巧」的一块：用**两个互不知情的计数器**，既做到「冷却中不能再冲」，又做到「冷却中按键不会被记下、导致冷却一结束就自动偷冲」。

### 1. 需求拆解与验收标准

要解决的具体问题：把「能力锁（冷却）」和「输入容错（缓冲）」这两件本质不同的事，拆成两个互不干扰的机制。

- 冷却期内按冲刺键：**不触发**冲刺。
- 冷却期内的按键**不应**被缓存到冷却结束后自动触发（不能「偷冲」）。
- 但在能冲的时候，按键要有一点点**亚帧容错**（按早几毫秒也能冲出来）。

验收标准：
- 一个冷却周期内只能冲一次（Task 4 Step 3 第 ② 项，**待验证**）。
- 冷却中按一下键后静候，**不会**在冷却结束瞬间自动冲出（第 ⑥ 项，**待验证**）。

### 2. 该功能涉及的基础知识和原理

**「冷却（cooldown）」语义 vs 「缓冲窗口（buffer window）」语义。** 这是两种完全不同的时间机制，却常被新手混为一谈：
- **冷却**：一个**能力锁**。用完后锁一段时间，期间不可用。语义是「不准用」。
- **缓冲窗口**：一个**输入容错**。把「刚才那下按键」记一小段时间，等条件满足就消费。语义是「记住你想用」。

如果用「缓冲」的思路去处理「冷却中的按键」（即冷却时也把按键记下来），就会出现：冷却 1 秒、你在第 0.3 秒按了一下、系统记住、到第 1 秒冷却一结束立刻自动冲——这就是「偷冲」，手感很差（你早就不想冲了）。

**正交（orthogonal）设计。** 两个机制各管一件事、互不引用，就叫正交。本项目让 `DashBufferCounter`（缓冲）只管「最近想不想冲」，`DashCooldownCounter`（冷却）只管「能力锁没锁」，闸门处用 `&&` 组合。各自逻辑都极简，组合出完整行为。

**为什么 `buffer << cooldown` 时「偷冲」会自动消失。** 关键数值：缓冲 0.15 秒、冷却 1 秒。两个计数器都在 `Update` 里每帧独立递减。你在冷却期按一下 → `DashBufferCounter` 被设成 0.15 → 它在 0.15 秒后就衰减到 0（远早于冷却的 1 秒结束）。等冷却真的结束时，缓冲**早已过期**，闸门的 `DashBufferCounter > 0` 不成立 → 不会偷冲。**「丢弃」是数值关系自然产生的，不需要任何特判代码。** 这是正交设计的漂亮之处。

### 3. 实现该功能有哪些方案（游戏开发业界常见的实现方案）

**冷却系统模式：**
- **硬冷却（fixed cooldown）**：用完锁固定时长（本项目）。
- **体力/资源消耗型**：冲刺扣体力，体力不足不能冲，体力随时间回复（很多动作游戏的闪避）。
- **充能多格（charge stacks）**：攒 N 格、每格独立冷却，可连续冲 N 次（如《原神》冲刺、某些 MOBA 技能）。

**冷却期内的输入处理：**
- **直接丢弃**：冷却中按键在源头忽略。
- **完整缓冲**：冷却中按键照存，冷却一结束就触发（→ 会偷冲，通常不可取）。
- **缓冲与冷却正交**（本项目）：缓冲只给亚帧容错，冷却独立把关，靠 `buffer << cooldown` 让冷却期按键自然过期。

### 4. 方案与技术选择

**最终选择：硬冷却 + 缓冲/冷却正交。**

| 维度 | 正交设计（选用） | 完整缓冲（攻击式） | 回调里硬丢弃 |
|------|----------------|------------------|-------------|
| 冷却期按键 | 缓冲自然过期 = 等效丢弃 | **冷却结束偷冲**（手感差） | 源头丢弃 |
| 能冲时的容错 | ✅ 有 0.15s 亚帧容错 | 有但过度 | 无（临界帧可能丢输入） |
| 特判代码 | ✅ 零特判（数值关系搞定） | — | 回调多一行 gate |
| 与现有惯例 | ✅ 复用 buffer counter 模式 | 复用但语义错配 | 偏离惯例 |

为什么不像攻击那样做「完整缓冲」？因为攻击的缓冲是为了接「窗口」（窗口很快会开，缓冲帮你卡点），而冲刺的冷却很长（1 秒），把按键缓冲那么久只会制造「偷冲」。所以这里把缓冲刻意做短、只用于亚帧容错，把「能不能冲」的大权交给独立的冷却闸门——两者正交，各司其职。

### 5. 架构设计与拆解

三处协作，缺一不可：

```
① 写入意图（PlayerController.OnDashPerformed）
     DashBufferCounter = _dashBufferTime;     // 只写缓冲，绝不碰冷却 ← 正交的起点

② 闸门组合（PlayerGroundedState.CheckTransition）
     if (DashBufferCounter > 0f && DashCooldownCounter <= 0f)   // 有意图 ∧ 不在冷却
         ChangeState(DashState);

③ 离开上锁（PlayerDashState.Exit）
     DashCooldownCounter = DashCooldown;       // 启动冷却
     // 刻意不清 DashBufferCounter：闸门是双条件，Exit 刚把冷却设为正数，
     //   残留缓冲会被冷却挡住、绝不会漏触发 → 无需显式清，省一处易漏的维护点
```

时间轴示意（冷却期按键为何不偷冲）：

```
t=0.0  冲刺结束，Exit 启动冷却： Cooldown=1.0
t=0.3  玩家按键：              Buffer=0.15
t=0.45 Buffer 衰减到 0         （Buffer 过期，意图作废）
t=1.0  Cooldown 衰减到 0       （此刻 Buffer 早已=0 → 闸门不放行 → 不偷冲）✓
```

一个值得玩味的细节：`Enter` 里其实也清了一次 `DashBufferCounter`（消耗起手输入）。所以「防重复触发」有**双重保险**：进入时清缓冲 + 冷却闸门拦截再次进入。即便某天 `Enter` 的清零被改掉，冷却闸门仍兜底——这种「不依赖单点、多层防御」是稳健设计的常见手法。

---
---

## 子功能五：状态机触发点与优先级仲裁

> 涉及文件：`PlayerGroundedState.cs`（`CheckTransition` 顶部的 Dash 分支）；以及「其它 State 都不含 Dash 检测」这一**结构性事实**

决定「冲刺在哪儿被触发、和攻击/跳跃同帧竞争时谁赢、为什么空中/攻击中不能冲」。

### 1. 需求拆解与验收标准

要解决的具体问题：把冲刺的触发检测放到正确的位置，并和已有的攻击、跳跃排好优先级。

- 冲刺**只在地面状态**可触发。
- 同一帧若同时有冲刺和攻击/跳跃意图，**冲刺优先**（Dash → Attack → Jump）。
- 空中、攻击中、滑坡中按冲刺键 → **不触发**。

验收标准：
- 连段攻击进行中按冲刺键 → 不打断攻击（Task 4 Step 3 第 ④ 项，**待验证**）。
- 跳起/下落中按冲刺键 → 不触发（第 ⑤ 项，**待验证**）。

### 2. 该功能涉及的基础知识和原理

**状态机的「每帧转移检测」。** 本项目每个 State 的 `Update` 末尾都会跑一个 `CheckTransition()`，逐条检查「是否该切到别的状态」。这是状态机最核心的一环：状态自己决定什么时候、切到哪。

**卫语句（guard clause）+ `return` 的短路仲裁。** `CheckTransition` 里一串 `if (条件) { ChangeState(...); return; }`。一旦某条命中就切状态并 `return`，后面的条件根本不会执行。于是**代码里谁写在前面，谁的优先级就高**——优先级不是一个数字字段，而是**代码顺序**本身。把 Dash 块放在最前面，它就赢过后面的 Attack 和 Jump。

**「grounded-only」用结构而非 `if` 实现。** 冲刺「只能在地面触发」，本项目**不是**在冲刺逻辑里写 `if (isGrounded)`，而是**只把 Dash 检测写在 `PlayerGroundedState` 一个地方**——其它状态（Airborne/Sliding/Attack）的 `CheckTransition` 里压根没有 Dash 分支。于是「空中不能冲、攻击中不能冲、滑坡中不能冲」是**自动成立**的，因为那些状态根本不去看冲刺输入。这叫「用代码的物理结构表达约束」，比到处写 `if` 更难出错（你不可能忘记在某个状态里加判断——因为本来就不该加）。

### 3. 实现该功能有哪些方案（游戏开发业界常见的实现方案）

**触发检测位置：**
- **各状态自查转移**（本项目）：每个状态在自己的 `CheckTransition` 里决定能去哪。
- **集中转移表（transition table）**：用一张「从哪个状态、什么条件、到哪个状态」的数据表统一描述（状态多时清晰，少时啰嗦）。
- **全局输入路由**：一个中枢先解析输入再分发（输入复杂、多设备时常见）。

**优先级实现：**
- **顺序短路**（本项目）：写在前面的先判、命中即 return。
- **优先级数值排序**：给每个候选转移一个 priority 数值，排序后取最高。
- **可打断性矩阵**：维护一张「当前状态能否被 X 打断」的表（格斗游戏的 cancel 系统常用）。

### 4. 方案与技术选择

**最终选择：插入 `PlayerGroundedState.CheckTransition` 顶部 + 顺序短路 + Dash > Attack > Jump。**

| 维度 | 顺序短路（选用） | 优先级数值排序 | 可打断性矩阵 |
|------|----------------|---------------|-------------|
| 实现复杂度 | ✅ 极简（一串 if + return） | 需排序结构 | 需维护矩阵 |
| 与现有一致 | ✅ 沿用 Attack/Jump 既有写法 | 需重构现有转移 | 需重构 |
| 可读性 | ✅ 顺序即优先级，一眼看懂 | 要查数值 | 要查表 |
| 适用规模 | 候选转移少时最佳 | 候选多时更优 | cancel 系统复杂时 |

为什么 Dash 排最前？因为冲刺是「位移逃生 / 进攻起手」的强意图——玩家按下时通常希望立刻生效、压过普攻和跳跃；且本轮冲刺不可被打断，给它最高优先级能避免「想冲却先出了一刀」。grounded-only 用「不在别处加检测」实现，而非满地写 `if`，是为了让约束**不可能被忘记**。

### 5. 架构设计与拆解

```
PlayerGroundedState.CheckTransition()   （仅"地面态"拥有这段）
   ① if (DashBufferCounter>0 && DashCooldownCounter<=0) → ChangeState(DashState);  return;  ← 最高优先级
   ② if (AttackBufferCounter>0)                         → ChangeState(AttackState); return;
   ③ if (JumpBufferCounter>0)                           → ExecuteJump();            return;
   ④ if (!IsGrounded)                                   → ChangeState(AirborneState);return;
   ⑤ if (GroundAngle > slopeLimit)                      → ChangeState(SlidingState);

PlayerAirborneState / PlayerSlidingState / PlayerAttackState 的转移检测里
   —— 没有任何 Dash 分支 ——
   ⇒ 空中 / 滑坡 / 攻击中 按 Dash 键：DashBufferCounter 照样被写，但没人去读它，
      它静静衰减到 0 → 自然"被忽略"。grounded-only 由此结构性成立。
```

一个含义要点：「攻击中按冲刺被忽略」**不是**靠攻击状态主动拒绝冲刺实现的，而是靠「攻击状态根本不检测冲刺」。这正是上面说的「用结构表达约束」——你想让某状态不能触发某能力，最稳的办法是让那个状态压根看不见这个输入。

---
---

## 子功能六：Exit 单一职责与失败模式防御

> 涉及文件：`PlayerDashState.cs`（`Exit`、`TransitionToMovement`）；`PlayerStateMachine.cs`（`ChangeState` 必调 `Exit` 的保证）

把冲刺的「收尾」做对：无论怎么结束都正确启动冷却、回到正确的状态（地/空），并系统性地堵住一组失败模式。

### 1. 需求拆解与验收标准

要解决的具体问题：保证冲刺**所有**离开路径都启动冷却；冲刺结束后根据「落在哪」回到地面态或空中态；防御重复触发、冷却漏启动等失败模式。

验收标准：
- 朝悬崖冲 → 水平冲完整段不被中途打断，结束时悬空则进入 Airborne 自然下落（Task 4 Step 3 第 ③ 项，**待验证**）。
- 一个冷却周期内只能冲一次（第 ② 项，**待验证**）。
- 连续快速按键不会重复触发冲刺（**补充**，**待验证**）。

### 2. 该功能涉及的基础知识和原理

**状态机 `Exit` 钩子的保证。** 本项目的 `PlayerStateMachine.ChangeState` 是这样写的：

```
CurrentState?.Exit();   // 先无条件调用旧状态的 Exit
CurrentState = newState; // 再换引用
CurrentState.Enter();   // 最后调用新状态的 Enter
```

关键在「**无条件**」：任何状态切换都必然先走旧状态的 `Exit`。这给了我们一个极强的保证——**只要把「冷却启动」放进 `DashState.Exit`，那么无论冲刺因为什么原因结束（自然到时、未来的受击打断、死亡），冷却都一定会启动**。

**单一职责 / 单一责任点（single point of responsibility）。** 与其在每条「离开冲刺」的分支里都写一遍「记得启动冷却」（容易漏一条就出 bug），不如把它收敛到唯一一个必经之处（`Exit`）。这和连段系统把 `comboIndex` 归零收敛到 `PlayerAttackState.Exit` 是**同一个手法**：找到所有路径的公共出口，把清理/收尾放在那一个点。

**「进入即承诺」（committed action）。** 冲刺设计为不可中途打断——一旦进入就跑完整段。好处是行为可预测（玩家知道按下就会冲完）、实现简单（不用处理「冲一半被打断」的中间态）。代价是响应性略低（冲刺中不能立刻做别的）。

**落点判定。** 冲刺可能从地面冲到悬崖外。结束时不能盲目回「地面态」，而要**当场检测脚下有没有地**：有地回 `GroundedState`，悬空回 `AirborneState`（由后者接管重力下落）。这样「冲下悬崖」这个边界情况，被收尾时的一次判定优雅吸收，无需在冲刺过程中加打断逻辑。

### 3. 实现该功能有哪些方案（游戏开发业界常见的实现方案）

**冷却启动点：**
- **在 Exit 启动**（本项目）：从冲刺结束起算，覆盖所有离开路径。
- **在 Enter 启动**：从冲刺开始起算（总锁定 = max(时长, 冷却)，更跟手）。
- **在转移函数里内联**：每个切出点各写一遍（易漏）。

**离地处理：**
- **冲到底、Exit 判落点**（本项目）：不打断，结束时再决定回哪。
- **离地即提前结束转空中**：一离地就切 Airborne（违反「不可打断」，多一条路径）。

**退出落点：**
- **固定回 Grounded**：简单但冲下悬崖会卡在地面态。
- **落点判定**（本项目）：按脚下实况回地/空。

### 4. 方案与技术选择

**最终选择：Exit 单点启动冷却 + 冲到底 + 落点判定。**

| 维度 | Exit 单点（选用） | Enter 启动 | 各分支内联 |
|------|------------------|-----------|-----------|
| 覆盖所有离开路径 | ✅ ChangeState 必调 Exit | ✅ ChangeState 必调 Enter | ❌ 易漏某条 |
| 冷却语义 | 从结束算（总锁定=时长+冷却） | 从开始算（更跟手） | 取决于写法 |
| 维护成本 | ✅ 只有一处 | 一处 | 每加一条路径都要补 |

选 Exit 启动而非 Enter：单一责任更干净，且对未来可能新增的「受击打断」路径天然安全（Exit 一样会跑）。语义上是「冲完再等冷却」，手感可通过调小 `_dashCooldown` 补偿。

**失败模式 ↔ 防御映射**（按落地代码重新整理）：

| 失败模式 | 后果 | 防御机制 | 在哪 |
|---------|------|---------|------|
| 中途被打断导致冷却没启动 | 可无限冲刺 | 冷却唯一启动点在 `Exit`，`ChangeState` 必调 `Exit` | `PlayerDashState.Exit` + `PlayerStateMachine.ChangeState` |
| 连续按键重复触发 | 抖动/连冲 | `Enter` 清缓冲 + 冷却闸门拦截再入（双保险） | `Enter` + `CheckTransition` |
| 冷却中"偷冲" | 冷却末尾自动冲 | 缓冲(0.15s) ≪ 冷却(1s) 自然过期；闸门要求 `Cooldown<=0` | 正交闸门（子功能四） |
| 同帧攻击/跳跃抢占 | 冲刺被吃掉 | Dash 块在 `CheckTransition` 最前 + `return` 短路 | `CheckTransition`（子功能五） |
| 冲下悬崖卡在地面态 | 该下落却不落 | `TransitionToMovement` 落点判定 → 悬空回 Airborne | `TransitionToMovement` |
| CrossFade 切不动 | 无冲刺动画 | 状态名 hash 预缓存 + 接线时节点名精确匹配 | `DashStateHash` + Task 4 |

### 5. 架构设计与拆解

```
任何离开冲刺的路径
        │
        ▼
PlayerStateMachine.ChangeState(next)
        │  CurrentState?.Exit();   ← 无条件，保证 DashState.Exit 必跑
        ▼
PlayerDashState.Exit()
        DashCooldownCounter = DashCooldown;   ← 冷却唯一启动点
        （刻意不清 DashBufferCounter，理由见子功能四）

冲刺自然结束（_elapsed ≥ DashDuration）走的具体出口：
PlayerDashState.TransitionToMovement()
        if (GroundChecker.IsGrounded) ChangeState(GroundedState);   ← 落点在地
        else                          ChangeState(AirborneState);   ← 落点悬空
                                          （两条都经 ChangeState → 都会触发上面的 Exit）
```

**模块依赖边界**：以上全部发生在 `Game.Character` 内部（`PlayerDashState`、`PlayerStateMachine`、`PlayerGroundedState`、`PlayerController`），**完全不引用 `Game.Combat`**——冲刺无伤害结算，连 `using Game.Combat;` 都没有。这条边界是项目架构「单向依赖、模块隔离」的体现：能不依赖就不依赖。

---
---

## 贯穿本功能的设计原则总结

冲刺看似是个新功能，但它几乎全部由**项目里已经反复出现过的套路**拼成。把这些套路认出来，比记住冲刺本身更有价值——因为下一个功能大概率还会用到它们。

### 套路一：buffer counter（输入缓冲计数器）—— 这是第 4 次应用

「按键时只登记一个会过期的倒计时、由消费方在自己的节奏里读取」这个模式，在本项目里已经是第 N 次出现：

| # | 应用 | 计数器 | 写入点 | 消费/判定点 | 解决的问题 |
|---|------|--------|--------|------------|-----------|
| 1 | Jump Buffer | `JumpBufferCounter` | 按跳跃键 | 落地瞬间消费 | 落地前按跳也能跳 |
| 2 | Coyote Time | `CoyoteTimeCounter` | 离地瞬间 | 空中判定可否跳 | 走出边缘后仍有宽限 |
| 3 | Attack Buffer | `AttackBufferCounter` | 按攻击键 | 连段窗口/起手消费 | 卡点接下一段 |
| 4 | Combo Input Buffer | （复用 `AttackBufferCounter`） | 按攻击键 | `ComboResolver` 判窗口 | 连段衔接容错 |
| 5 | **Dash Buffer** | `DashBufferCounter` | 按冲刺键 | `CheckTransition` 闸门消费 | 冲刺亚帧容错 |

它们共享同一套机制：`Counter = Time` 写入、`Update` 里 `-= Time.deltaTime` 衰减、`> 0` 判有效、`= 0` 表消耗。**Coyote Time 也是同一类倒计时**，只是写入时机是「离地」而非「按键」。一旦你认出「会过期的意图/宽限」，第一反应就该是这个模式。

冲刺的新意在于：它把这个 buffer **和一个独立的 cooldown 计数器正交组合**——buffer 管「想不想」、cooldown 管「能不能」。这是同一套零件的新拼法。

### 套路二：单一责任点（single point of responsibility）

「找到所有路径的公共出口，把收尾收敛到那一个点」：
- 连段系统：`comboIndex` 归零只在 `PlayerAttackState.Exit`。
- 冲刺系统：冷却启动只在 `PlayerDashState.Exit`。

依赖的是同一个引擎事实——`ChangeState` 必调 `Exit`。认出这个保证，就能把一类「记得在每条分支补 XXX」的易漏 bug 一次性消灭。

### 套路三：用结构表达约束，而非到处写 if

「冲刺只能在地面触发」不是靠 `if (isGrounded)`，而是靠「只有 `GroundedState` 检测冲刺」。约束被编码进代码的**物理结构**里，让错误的状态根本看不见这个输入——比满地写判断更难出错。

### 套路四：顺序即优先级

`CheckTransition` 里「卫语句 + return」的短路链，让代码书写顺序直接成为转移优先级（Dash > Attack > Jump）。无需优先级数值、无需排序——简单到位。

### 套路五：数据驱动的裸字段 + 帧率无关 + 零分配热路径

- 手感参数 `[SerializeField]` 暴露给设计师调（与 Jump 同层）。
- 所有位移 `× Time.deltaTime`，帧率无关。
- 每帧热路径（`Update → HandleDashMovement`）只用 struct 值运算，无 `new`/LINQ/装箱，零 GC Alloc（**待 Profiler 验证**，但代码层面已无分配点）。

这三条是本项目所有移动逻辑的共同底线，冲刺只是又一次遵守它们。

---

> **下一步（待你完成）**：Task 4 —— 在 Animator Controller 里建 `DashForward_SingleTwohandSword` 状态节点（名字精确匹配）、配 `Has Exit Time` 返回连线（按 clip 真实秒数对齐 `_dashDuration`）、在 Inspector 调四个参数、跑 Play-mode 七项验证 + Profiler 零 GC 检查。本文所有「待验证」结论，都将在那时被实测确认或修正。
