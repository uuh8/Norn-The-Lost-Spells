# 弹跳类修正法术设计 Spec

日期：2026-07-04  
功能名：Bounce Modifier Spell / 弹跳类修正法术  
阶段：设计确认后进入实现计划  
范围：只设计“弹跳作为 Modify 法术影响后续投射物”的 MVP，不实现追踪、穿透、反弹伤害衰减、弹跳 VFX、弹跳 SFX。

---

## 1. 需求拆解与基础知识

### 1.1 玩家可见效果

玩家在法杖中配置：

```text
[弹跳修正] [普通火球]
```

施法时，普通火球获得弹跳能力：

1. 火球碰到墙面、地面等环境时，如果还有剩余弹跳次数，不销毁。
2. 火球根据碰撞面法线反射方向，继续飞行。
3. 每次环境弹跳消耗 1 次弹跳次数。
4. 弹跳次数耗尽后，再次碰到环境时回到普通投射物逻辑：触发 `OnImpact` / `Impacted` 并销毁。
5. 碰到敌人时不弹跳，仍然造成伤害并销毁。

### 1.2 系统语义

弹跳法术不是一个新的 `Emit`，而是一个 `Modify`。

它不直接生成投射物，而是改变后续投射物的运行时行为：

```text
[弹跳修正] [火球]
```

含义是：

```text
火球本身仍然是火球，但本次发射时带有 N 次环境弹跳能力。
```

这与现有修正法术一致：

| 修正法术 | 修改内容 |
|---|---|
| 增伤 | 修改后续 `Emit` 的最终伤害 |
| 加速 | 修改后续 `Emit` 的最终速度 |
| 散射 | 修改后续 `Emit` 的散射角 |
| 弹跳 | 修改后续 `Emit` 的投射物生命周期行为 |

### 1.3 基础知识

弹跳的核心数学是向量反射。

投射物当前飞行方向为 `V`，碰撞面法线为 `N`，反射方向为：

```text
R = V - 2 * dot(V, N) * N
```

Unity 提供：

```csharp
Vector3 reflected = Vector3.Reflect(currentDirection, hitNormal);
```

需要涉及的 Unity / C# 知识：

- `Collision`：Unity 物理碰撞信息。
- `ContactPoint.normal`：碰撞面的法线方向。
- `Rigidbody.linearVelocity`：Unity 6 中的刚体速度属性。
- `Vector3.Reflect`：根据法线计算反射方向。
- `CollisionDetectionMode.ContinuousDynamic`：高速投射物防止穿透。
- 运行时状态：剩余弹跳次数属于“当前投射物实例”，不能写回 `SpellDefinition` 资产。

### 1.4 MVP 明确边界

本功能只做：

- `Modify` 法术增加弹跳次数。
- 后续 `Emit` 投射物获得弹跳次数。
- 环境碰撞弹跳。
- 敌人碰撞照常伤害和销毁。
- 弹跳不触发命中 payload。
- 定时 payload 不受弹跳影响，投射物活到时间仍会触发。
- EditMode tests 覆盖纯解释器语义。

本功能不做：

- 弹跳音效。
- 弹跳 VFX。
- 弹跳后伤害衰减。
- 弹跳后速度衰减。
- 可弹跳 LayerMask 精细配置。
- 曲面/复杂物理材质真实弹性模拟。
- 通用 `ProjectileBehavior` 插件系统。

---

## 2. 可选实现方案

### 2.1 方案 A：Unity `Physic Material` 自动反弹

做法：

给投射物和环境 Collider 配置 `Physic Material`，通过 `Bounciness` 和 `Bounce Combine` 让 Unity 物理系统自动反弹。

优点：

- 实现最快。
- 接近物理模拟。
- 不需要手写反射公式。

缺点：

- 难以精确控制弹跳次数。
- 难以和 `SpellDefinition -> CastEvaluator -> EmitCommand` 的法术解释链路绑定。
- 难以定义“弹跳时是否触发 payload”。
- 表现受质量、阻力、材质、碰撞设置影响，调试不可控。
- 不适合未来做穿透、追踪、分裂等可组合 projectile behavior。

结论：

适合物理小游戏，不适合当前法术编程系统。

### 2.2 方案 B：在 `ProjectileBase.OnCollisionEnter` 中手动反射

做法：

投射物命中环境时，如果剩余弹跳次数大于 0：

1. 读取碰撞点法线。
2. 用 `Vector3.Reflect` 计算反射方向。
3. 设置 `Rigidbody.linearVelocity = reflected * speed`。
4. 扣除一次弹跳次数。
5. `return`，阻止本次碰撞继续进入 `OnImpact` / `Impacted` / `Destroy`。

优点：

- 逻辑可控。
- 容易支持弹跳次数。
- 容易和 payload 语义保持一致。
- 只需沿现有架构扩展，不引入大型新框架。
- 面试中能清楚讲出“数据 -> 解释器 -> 命令 -> 运行时 -> 投射物行为”的链路。

缺点：

- `ProjectileBase` 会承担更多 projectile behavior 逻辑。
- 未来如果行为类型大量增加，可能需要升级成通用 behavior pipeline。

结论：

这是本阶段推荐方案。

### 2.3 方案 C：通用 `ProjectileBehavior` 策略系统

做法：

把弹跳、穿透、追踪、分裂等都抽象成运行时 behavior：

```text
BounceBehavior
PierceBehavior
HomingBehavior
SplitBehavior
```

`ProjectileBase` 在 `Update`、`OnCollisionEnter`、`OnDestroy` 等生命周期点调用 behavior pipeline。

优点：

- 长期扩展性最好。
- 行为之间可以拆分得更独立。
- 后续做复杂 Noita-like projectile behavior 更自然。

缺点：

- 当前阶段过重。
- 需要设计 behavior 接口、生命周期、优先级、组合冲突、序列化和测试方式。
- 会把一个明确的 MVP 变成一个较大的架构工程。

结论：

作为未来演进方向保留，不作为本次 MVP。

---

## 3. 最终选择的方案与技术

最终选择：方案 B，手动反射 + 沿现有法术解释器链路传递弹跳参数。

### 3.1 总体链路

```text
SpellDefinition.ModBounceAdd
    ↓
CastModifierState.BounceCount
    ↓
CastEvaluator.BakeEmit(...)
    ↓
EmitCommand.BounceCount
    ↓
SpellCaster
    ↓
ProjectileBase.ConfigureBounce(...)
    ↓
ProjectileBase.OnCollisionEnter(...)
    ↓
Vector3.Reflect + Rigidbody.linearVelocity
```

### 3.2 技术选择

| 问题 | 技术选择 | 说明 |
|---|---|---|
| 如何把弹跳作为法术配置 | `SpellDefinition.ModBounceAdd` | 弹跳是 `Modify` 法术字段 |
| 如何让多个弹跳修正叠加 | `CastModifierState.BounceCount` | 解释器从左到右累计 |
| 如何把解释结果传给运行时 | `EmitCommand.BounceCount` | 边界对象携带最终快照 |
| 如何给投射物实例配置弹跳 | `ProjectileBase.ConfigureBounce(int bounceCount)` | 不继续拉长 `Init` 参数列表 |
| 如何计算弹跳方向 | `Vector3.Reflect` | 用碰撞法线计算反射 |
| 如何继续飞行 | `Rigidbody.linearVelocity` | 直接写入反射后的速度 |
| 如何避免弹跳时触发 payload | 弹跳分支提前 `return` | 不执行 `OnImpact`、`Impacted`、`Destroy` |
| 如何防止同阵营误触发 | 保留现有同阵营 pass-through 优先级 | 同阵营仍然不结算、不销毁、不弹跳 |

### 3.3 运行时语义

碰撞处理顺序：

```text
如果 _consumed：
    return

如果碰到同阵营：
    IgnoreCollision / 恢复速度
    return

如果没有可受击目标，且剩余弹跳次数 > 0：
    计算反射方向
    设置速度
    剩余弹跳次数--
    return

否则：
    如果敌人可受击，造成伤害
    调 OnImpact
    _consumed = true
    触发 Impacted
    Destroy
```

这样可以保证：

- 弹跳不会误触发命中类 payload。
- 定时类 payload 仍然可以在弹跳飞行途中触发。
- 敌人命中仍然是终点，不被弹跳吞掉。
- 弹跳耗尽后，环境碰撞仍然可以成为命中类 payload 的触发点。

---

## 4. 架构设计与拆解

### 4.1 所在架构层

弹跳类修正法术横跨五个层次：

```text
数据层：SpellDefinition
纯规则层：CastModifierState / CastEvaluator
边界对象层：EmitCommand
运行时桥接层：SpellCaster
投射物执行层：ProjectileBase
```

它不应该进入：

- `DamagePipeline`：弹跳不是伤害公式。
- `HealthComponent`：弹跳不是生命系统。
- `WizardController`：角色输入不关心投射物是否弹跳。
- UI 拖拽逻辑：UI 只展示和编辑法术，不解释弹跳规则。

### 4.2 数据层改动

`SpellDefinition` 增加：

```csharp
public int ModBounceAdd = 0;
```

规则：

- 只对 `Kind = Modify` 有意义。
- 默认值为 0。
- 弹跳修正法术资产设置为 `Kind = Modify`、`ModBounceAdd = 1` 或 2。

### 4.3 纯规则层改动

`CastModifierState` 增加：

```csharp
public readonly int BounceCount;
```

`Default` 中为 0。

`Apply(SpellDefinition modify)` 中累加：

```csharp
BounceCount + modify.ModBounceAdd
```

`CastEvaluator.BakeEmit` 将当前 `mods.BounceCount` 写入 `EmitCommand`。

### 4.4 边界对象层改动

`EmitCommand` 增加：

```csharp
public readonly int BounceCount;
```

它表示：

```text
该投射物本次生成时拥有多少次环境弹跳。
```

注意它不是“剩余弹跳次数”。  
剩余次数属于具体投射物实例，应该存在 `ProjectileBase`。

### 4.5 运行时桥接层改动

`SpellCaster` 在生成投射物并取到 `ProjectileBase` 后调用：

```csharp
proj.ConfigureBounce(cmd.BounceCount);
```

建议调用位置：

```text
Instantiate prefab
GetComponent<ProjectileBase>
订阅 payload 事件
ConfigureBounce
Init
```

`ConfigureBounce` 放在 `Init` 前后都可以。为了让初始化流程清晰，建议在 `Init` 前配置行为参数，然后 `Init` 设置速度和生命周期。

### 4.6 投射物执行层改动

`ProjectileBase` 增加：

```csharp
private int _bounceRemaining;
private float _currentSpeed;
```

`ConfigureBounce(int bounceCount)`：

```csharp
_bounceRemaining = Mathf.Max(0, bounceCount);
```

`Init(...)` 中保存当前速度：

```csharp
_currentSpeed = velocity.magnitude;
```

`OnCollisionEnter` 中，在同阵营 pass-through 后、伤害/销毁前，插入环境弹跳分支：

```text
if (target == null && _bounceRemaining > 0)
```

执行：

```text
ContactPoint contact = collision.GetContact(0)
Vector3 incoming = current velocity direction
Vector3 reflected = Vector3.Reflect(incoming, contact.normal)
_rb.linearVelocity = reflected * _currentSpeed
transform.rotation = Quaternion.LookRotation(reflected) * offset
_bounceRemaining--
return
```

### 4.7 与 payload 的关系

命中触发：

```text
弹跳时不触发 Impacted。
弹跳耗尽后撞环境，才触发 Impacted。
碰敌人时直接触发 Impacted。
```

定时触发：

```text
弹跳不影响计时。
如果投射物弹跳过程中活到 delay，TimedTriggerElapsed 触发并销毁。
```

### 4.8 与未来功能的关系

本次不引入通用 `ProjectileBehavior` 系统，但命名应为未来保留空间。

建议方法名避免过窄：

```csharp
ConfigureBounce(int bounceCount)
```

可接受。

如果未来引入穿透、追踪、分裂，再考虑升级为：

```csharp
ConfigureRuntimeBehavior(in ProjectileRuntimeConfig config)
```

当前不提前引入该结构，避免过度设计。

---

## 5. 测试与验证要求

### 5.1 EditMode tests

需要增加或扩展 `CastEvaluatorTests` / `CastModifierStateTests`：

1. `CastModifierState.Default` 的 `BounceCount` 为 0。
2. 单个弹跳修正会让后续 `EmitCommand.BounceCount` 等于配置值。
3. 多个弹跳修正可以叠加。
4. payload 捕获时，`PayloadMods` 保留 `BounceCount`。

### 5.2 Unity Editor 手动验证

需要用户在 Editor 中：

1. 创建 `SpellDefinition`：`Spell_Mod_Bounce`。
2. 设置 `Kind = Modify`。
3. 设置 `ModBounceAdd = 1` 或 2。
4. 添加图标。
5. 加入 `SpellLibrary.Available`。
6. 在 `WandLoadout` 中测试：

```text
[弹跳] [普通火球]
[弹跳] [弹跳] [普通火球]
[弹跳] [触发火球] [普通火球]
[弹跳] [定时火球] [普通火球]
```

验证表现：

- 火球撞环境会反射。
- 每次环境反射消耗一次次数。
- 撞敌人仍然伤害并销毁。
- 弹跳时不释放命中 payload。
- 定时 payload 可以在弹跳飞行中释放。

---

## 6. 成功标准

功能完成后应满足：

1. 弹跳作为 `Modify` 法术影响后续 `Emit`。
2. 弹跳参数沿现有解释器链路传递，不写特殊 `BouncyFireball`。
3. `ProjectileBase` 能在环境碰撞时反射并继续飞行。
4. 敌人命中、同阵营 pass-through、命中 payload、定时 payload 语义保持清晰。
5. 纯解释器逻辑有 EditMode tests。
6. 用户可以在 Unity Editor 中通过新增一个 `SpellDefinition` 资产完成配置。

---

## 7. 面试表达点

可以这样描述：

> 我没有把弹跳做成一个新的火球 prefab 或一个 `BouncyFireball` 特例，而是把它建模成法术编程系统中的 `Modify` 指令。解释器从左到右读取法术时，会把弹跳次数累积进 `CastModifierState`，后续 `Emit` 被烘焙成 `EmitCommand` 时携带这个运行时行为快照。`SpellCaster` 作为桥接层把快照配置给 `ProjectileBase`。投射物碰撞环境时，根据碰撞法线用 `Vector3.Reflect` 计算反射方向，并在剩余弹跳次数耗尽前阻止本次碰撞进入 `Impacted` 和销毁逻辑。这样弹跳、命中触发、定时触发都能保持语义一致，同时没有破坏 `Game.Skills` 和 `Game.Combat` 的模块边界。
