# 受击 / 死亡反馈（受击动画 + 闪红 + 死亡动画 + 消失）— 实现说明

> 需求：角色（玩家或敌人）受到伤害时播放**受击动画**并**短暂闪红**；血量归零后播放**死亡动画**再**消失（销毁）**。
> 本文讲：怎么实现的、为什么这么设计、你在 Unity Editor 端要做哪些事。

---

## 一、之前为什么"没反应"

伤害链路其实早就通了：箭/近战命中 → `HealthComponent.ReceiveHit` 扣血 → 同帧 `Publish` 两个事件：
- `DamageReceivedEvent`（每次受击都发，带 `TargetId / Amount / RemainingHp / HitPoint …`）
- `DeathEvent`（血量归零那一帧再发，带 `TargetId / Position …`）

但**之前没有任何人订阅这两个事件去做表现**，所以扣血了也看不出来、死了也不消失。这次要做的，就是补一个「订阅者」把这些事件变成画面反馈。**伤害逻辑一行没改**，只是给已有事件加了消费端——这正是事件驱动架构的好处。

---

## 二、整体思路：一个组件，订阅事件，按"是不是我"过滤

新增一个组件 **`CharacterCombatFeedback`**（放在 `Game.Combat`，因为玩家和敌人都有 `HealthComponent`，而敌人没有 `Game.Character` 脚本——放 Combat 能被所有"能挨打的对象"共用）。

它的工作流：

```
HealthComponent 扣血 → Publish DamageReceivedEvent / DeathEvent（全局广播）
        │
        ▼
CharacterCombatFeedback（每个角色身上挂一个，各自订阅）
        │  先过滤：e.TargetId == 我的 gameObject.GetInstanceID()？ 不是我就忽略
        ├─ 收到 DamageReceivedEvent →  闪红 + （若没被打死）播受击动画
        └─ 收到 DeathEvent          →  播死亡动画 + 禁用控制器 + 延时销毁
```

**关键点：怎么知道"打到的是不是我"？** 事件里带了 `TargetId`，它就是 `HealthComponent` 那边用 `gameObject.GetInstanceID()` 算出来的目标实例 ID。我的反馈组件和 `HealthComponent` 在**同一个 GameObject** 上（用 `[RequireComponent(typeof(HealthComponent))]` 强制），所以我算出的 `gameObject.GetInstanceID()` 和事件里的 `TargetId` 一致——相等就说明"这条事件说的是我"。全场所有角色都订阅同一个事件，靠这个 ID 各认各的。

---

## 三、三块表现分别怎么实现

### 1. 受击动画

收到 `DamageReceivedEvent` 且**本次没被打死**（`e.RemainingHp > 0`）时，`CrossFadeInFixedTime` 到受击状态：

```csharp
if (e.RemainingHp > 0f && _getHitHash != 0 && _animator != null)
    _animator.CrossFadeInFixedTime(_getHitHash, _getHitCrossFade, 0);
```

- 状态名**数据驱动**（Inspector 填 `GetHit_Bow` / `GetHit_SingleTwohandSword`），在 `Awake` 预 hash 成 int，和项目其它 CrossFade 状态一个套路。
- **为什么致死那一击不播受击动画**：血量归零时 `HealthComponent` 同一帧会**先发 `DamageReceivedEvent`、再发 `DeathEvent`**。如果致死也播受击，就会"受击动画闪一下立刻被死亡动画打断"。所以我用 `e.RemainingHp > 0` 把受击动画限定在"还活着"，致死那下直接交给死亡动画。

### 2. 受击闪红

用 **`MaterialPropertyBlock`（MPB）** 把所有渲染器的颜色推向红色，再随时间插值回原色：

```csharp
Color c = Color.Lerp(_flashColor, _originalColors[i], t); // t: 0=最红 → 1=原色
renderer.GetPropertyBlock(_mpb);
_mpb.SetColor(_colorPropId, c);
renderer.SetPropertyBlock(_mpb);
```

- **为什么用 MaterialPropertyBlock 而不是直接改 `renderer.material.color`**：直接访问 `renderer.material` 会让 Unity**实例化一份材质副本**（每个角色一份，且永不释放，造成材质泄漏 + 打断合批）。MPB 是"覆盖参数"机制，不复制材质、不泄漏，性能好——这是 Unity 改渲染参数的标准做法。
- **原色从哪来**：`Awake` 里从每个渲染器的 `sharedMaterial` 读一次 `_BaseColor` 存好，闪完插值回它。
- **零 GC**：MPB 只 new 一次复用，渲染器数组 `Awake` 缓存一次，闪红期间每帧只是结构体计算，无分配。
- **颜色属性名**：默认 `_BaseColor`（URP Lit 的属性名）。如果你的角色用的是别的 Shader，可能是 `_Color`，在 Inspector 改 `Color Property` 即可。

### 3. 死亡动画 + 消失

收到 `DeathEvent`：

```csharp
_dead = true;                                              // 之后忽略一切受击反馈
_animator.CrossFadeInFixedTime(_dieHash, _dieCrossFade, 0); // 死亡动画
foreach (var b in _disableOnDeath) b.enabled = false;      // 禁用控制器/输入/AI，死后不能动
Destroy(gameObject, _destroyDelay);                        // 播完动画后销毁
```

- **`_disableOnDeath` 是个组件数组**：死亡瞬间把它们 `enabled = false`。你把角色的控制器（`ArcherController`/`WarriorController`）、敌人的 AI 等拖进去，角色死后就不会还能走动/攻击。
  - 为什么用数组让你拖、而不是代码里直接关控制器？因为 `Game.Combat` 按架构**不能引用 `Game.Character`**（不能 `GetComponent<ArcherController>`）。用 `Behaviour[]` 让你在 Inspector 拖，既解耦、又通用（敌人拖它自己的 AI 即可）。
- **`_destroyDelay`**：死亡动画播完后再销毁，设成略大于死亡动画时长（默认 2 秒）。

---

## 四、为什么放在 `Game.Combat`，而不是 Rendering / Character

- **Rendering 不行**：`Game.Rendering` 只依赖 `Game.Core`，看不到 `DamageReceivedEvent`/`DeathEvent`（它们在 `Game.Combat`，且带 `DamageType` 这种 Combat 类型）。
- **Character 不合适**：放 `Game.Character` 的话敌人也得挂 Character 脚本，但敌人通常只有 `HealthComponent + Animator`、没有角色控制器。
- **Combat 正好**：玩家和敌人都有 `HealthComponent`，反馈组件和它同源；`DamageReceivedEvent` 的注释里本来就写了它给"受击反应"消费。所以这是所有"能挨打的对象"共享反馈的最自然位置，且不引用任何上层表现模块，不违反"gameplay 不依赖 presentation"。

---

## 五、你在 Unity Editor 要做的事

> 给**每个**要有反馈的角色（弓箭手 BowPlayer、战士 SwordPlayer、以及敌人 Enemy）都做一遍。

### 步骤 A：加组件、填动画名
1. 选中角色 GameObject（带 `HealthComponent` 的那个），Add Component → **`Character Combat Feedback`**。
2. 填 **Get Hit State Name** / **Die State Name**：
   - 弓箭手：`GetHit_Bow` / `Die_Bow`
   - 战士：`GetHit_SingleTwohandSword` / `Die_SingleTwohandSword`
   - 敌人：填它自己 Animator Controller 里的受击/死亡状态名
3. **Animator** 字段留空即可（会自动在子物体上找）；若你的层级特殊找不到，手动拖角色的 Animator。

### 步骤 B：在 Animator Controller 里建好这两个状态（关键，否则 CrossFade 静默失效）
对每个角色的 Controller（`BowHero` / `SingleTwoHandSwordHero` / 敌人的）：
1. 把**受击 clip**、**死亡 clip** 各拖进 Base Layer，新建两个状态，**状态名必须和上面填的字符串完全一致**（大小写也要一致）。
2. **受击状态**：加一条**出去**的过渡 `GetHit_XXX → Idle`（或你的移动默认态），勾 **Has Exit Time**（比如 0.8），让受击动画播完自动回到正常。
   - 因为我们是"代码 CrossFade 进入、Animator 连线退出"——**进入靠代码、退出靠连线**，不连退出线角色会卡在受击姿势。
3. **死亡状态**：
   - 把死亡 clip 的 **Loop Time 取消勾选**（死亡不循环）。
   - **不需要**画出去的过渡（角色会被销毁，让它停在死亡最后一帧即可）。
   - 这两个状态都不用画"进入"的连线（代码 CrossFade 直接点名）。

### 步骤 C：配死亡时禁用的组件
在 `Character Combat Feedback` 的 **Disable On Death** 列表里：
- 玩家：拖入它的控制器（`ArcherController` / `WarriorController`）。这样死后不能再移动/攻击。
- 敌人：拖入敌人的 AI / 移动脚本。

### 步骤 D：调参数
- **Destroy Delay**：设成略大于死亡动画时长（默认 2s）。
- **Flash Color / Flash Duration**：闪红颜色与时长（默认红、0.15s），按手感调。
- **Color Property**：默认 `_BaseColor`（URP Lit）。**如果 Play 时闪红不生效**，多半是 Shader 颜色属性名不同，改成 `_Color` 试试。

### 步骤 E：确认前置
- 角色（含敌人）身上要有 `HealthComponent`（组件已 `[RequireComponent]`，缺了会自动补上）。
- 确认伤害真的打得到（敌人 `TeamId` 和玩家不同，否则同阵营互相穿过不结算）。

---

## 六、已知限制与后续可加强

- **没有"硬直/受击打断"**：目前受击只是叠播一个动画 + 闪红，玩家在受击动画期间**仍能移动**（逻辑状态机没有进入一个"受击态"）。要做真正的 hit-stun（受击期间锁操作、可被高优先级打断），需要在 `Game.Character` 的状态机里加一个 `PlayerHitStunState`，由角色订阅事件后切入——这是更大的一步，本次未做。
- **闪红是整体染色**：用的是 `_BaseColor` 整体着色插值。如果想要更高级的"受击高光"，可以改用 `_EmissionColor`（需材质开启 Emission），或上专门的受击 Shader/全屏后处理。
- **多材质角色**：取的是每个渲染器 `sharedMaterial` 的主色作为原色，MPB 对该渲染器所有子网格统一染色——闪红够用；若角色各部位原色差异极大且要精确还原，可扩展成按材质槽记录。
- **玩家死亡后相机**：玩家被销毁后，跟随它的相机会失去目标。原型阶段可接受；正式版应改为「死亡 → 切换游戏状态/复活流程」而非直接销毁玩家。

---

## 七、本次改动文件

| 文件 | 说明 |
|---|---|
| `Assets/_Project/Scripts/Combat/CharacterCombatFeedback.cs` | **新增**：受击动画 + 闪红 + 死亡动画 + 销毁，订阅 `DamageReceivedEvent`/`DeathEvent` 按 `TargetId` 过滤 |

> 伤害逻辑（`HealthComponent` / `Arrow` / `MeleeHitDetector` / `DamagePipeline`）**未改动**——本功能纯粹是给既有事件加了一个消费端。
