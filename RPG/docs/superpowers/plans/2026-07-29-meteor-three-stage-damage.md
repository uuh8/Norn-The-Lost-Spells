# 陨石术三段伤害数据化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. 本任务未获准使用 SubAgent，必须在当前会话内执行。Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将陨石术的直击、爆炸、火场 DoT 从一个含义模糊的 `BaseDamage` 拆成 `SpellDefinition` 中可独立配置、可被 Tooltip 解释、可由 `CastEvaluator` 快照并由运行时准确执行的三段伤害，同时隔离玩家与 Boss 的陨石资产。

**Architecture:** `Game.Skills` 继续拥有权威法术数据和纯解释逻辑：`SpellDefinition` 保存三段基础伤害，`CastEvaluator` 将 Damage Modifier 烘焙进 `EmitCommand`。`Game.Character.SpellCaster` 只负责把纯数据命令注入 `Game.Combat.NovaFireball`；`NovaFireball` 再分别配置爆炸与火场的 `AreaDamage`，不再用直击伤害覆盖所有阶段。Prefab 仍拥有视觉、碰撞半径和命中反馈等表现/空间配置，但不再拥有陨石权威伤害数值。

**Tech Stack:** Unity 6.3、C#、ScriptableObject、NUnit/EditMode Test、UGUI、ProjectileBase、AreaDamage、YAML Unity Asset/Prefab。

## Global Constraints

- Engine 固定为 Unity 6.3 `6000.3.16f1`，Render Pipeline 为 URP 17.3.0。
- 不使用 Legacy `Input`；本改动不新增输入路径。
- `Game.Skills` 不引用 `Game.Character`；`Game.Combat` 不引用 `Game.Skills`。
- `CastEvaluator` 保持纯解释器，不 Instantiate、不读取 Scene。
- 不在 `Update`/`FixedUpdate` 热路径新增分配、LINQ、Boxing；命中时 Instantiate/组件查询属于离散事件。
- 所有新增或实质修改代码写简体中文学习型注释，解释数据权威、快照、Runtime Adapter 和 DoT 取舍。
- 保留当前工作区所有用户改动；只对本计划列出的目标片段做最小补丁。
- 玩家陨石目标：直击 10、爆炸 15、火场 2/跳、0.5 秒/跳、5 秒，总理论伤害 45。
- Boss 陨石目标：直击 12、爆炸 18、火场 2/跳、0.5 秒/跳、5 秒，总理论伤害 50。
- `ModDamageMul` 同时缩放直击、爆炸、火场每跳；`ModDamageAddFlat` 只加到单次直击，避免一次 Flat Bonus 被每个 DoT Tick 重复放大。
- ElementField Fire Deposit 与 `Burning` 继续独立存在，不计入三段法术伤害字段。

---

## File Map

| 文件 | 责任 |
|---|---|
| `Assets/_Project/Scripts/Skills/SpellDefinition.cs` | 新增爆炸伤害、火场每跳伤害、Tick Interval、Duration 四个权威 Authoring 字段 |
| `Assets/_Project/Scripts/Skills/EmitCommand.cs` | 保存本次施法已经过 Modifier 烘焙的三段伤害快照 |
| `Assets/_Project/Scripts/Skills/CastEvaluator.cs` | 将基础三段伤害按明确规则烘焙进命令 |
| `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs` | 在生成 `NovaFireball` 后把复合伤害快照注入 Combat Runtime |
| `Assets/_Project/Scripts/Combat/Projectiles/NovaFireball.cs` | 区分爆炸和火场伤害，不再把 `_damage` 复制给两个效果 |
| `Assets/_Project/Scripts/Combat/Status/AreaDamage.cs` | 支持运行时同时配置每跳伤害、类型和 Tick Interval |
| `Assets/_Project/Scripts/UI/SpellTooltipPresenter.cs` | 为陨石显示直击/爆炸/火场节奏与理论总伤害 |
| `Assets/_Project/Tests/Skills/CastEvaluatorTests.cs` | 验证三段快照、Multiplier 与 Flat Bonus 语义 |
| `Assets/_Project/Tests/Combat/AreaDamageTests.cs` | 验证运行时配置会分别保留伤害与 Tick Interval |
| `Assets/_Project/Art/Prefabs/Wizard/Magic/NukeConeExplosionFire.prefab` | 增加一次性 `AreaDamage` 执行器，半径 5、Tick=0 |
| `Assets/_Project/ScriptableObjects/Spell/Spell_Static_Meteor.asset` | 玩家三段伤害 10/15/2，0.5 秒，5 秒 |
| `Assets/_Project/ScriptableObjects/P8/Boss/BossSpell/BossSpell_Static_Meteor.asset` | Boss 三段伤害 12/18/2，0.5 秒，5 秒 |
| `Assets/_Project/ScriptableObjects/P8/Boss/Wands/P8_BossWand_MeteorJudgment.asset` | 将 Gravity/Triple/Trigger/Meteor 全部切换到 BossSpell 目录下的专属资产 |
| `Assets/_Project/Docs/2026-07-10 RPG后续开发计划.md` | 记录收口后修复状态与验证边界 |
| `Assets/_Project/Docs/2026-07-26 P8法术型Boss终局解析（更新中）.md` | 记录症状、根因、完整 Runtime 链路、三段数据模型、取舍与验证 |

---

### Task 1: 用失败测试定义三段伤害快照语义

**Files:**
- Modify: `Assets/_Project/Tests/Skills/CastEvaluatorTests.cs`

**Interfaces:**
- Consumes: 现有 `SpellDefinition`、`CastModifierState`、`CastEvaluator.Evaluate`
- Produces: 对 `EmitCommand.ExplosionDamage`、`FireFieldDamagePerTick`、`FireFieldTickInterval`、`FireFieldDuration` 的行为约束

- [ ] **Step 1: 扩展测试用 Static Helper**

```csharp
private static SpellDefinition Static(
    float dmg = 10f,
    float speed = 20f,
    float mana = 0f,
    float explosionDamage = 0f,
    float fireFieldDamagePerTick = 0f,
    float fireFieldTickInterval = 0.5f,
    float fireFieldDuration = 0f)
{
    var s = ScriptableObject.CreateInstance<SpellDefinition>();
    s.Kind = SpellKind.StaticProjectile;
    s.SpawnMode = SpellSpawnMode.SkyfallAtPoint;
    s.BaseDamage = dmg;
    s.ExplosionDamage = explosionDamage;
    s.FireFieldDamagePerTick = fireFieldDamagePerTick;
    s.FireFieldTickInterval = fireFieldTickInterval;
    s.FireFieldDuration = fireFieldDuration;
    // 保留现有其余赋值。
    return s;
}
```

- [ ] **Step 2: 新增基础快照失败测试**

```csharp
[Test]
public void StaticProjectile_BakesThreeStageDamageSnapshot()
{
    Run(1, 999f, Static(
        dmg: 10f,
        explosionDamage: 15f,
        fireFieldDamagePerTick: 2f,
        fireFieldTickInterval: 0.5f,
        fireFieldDuration: 5f));

    Assert.AreEqual(10f, _out[0].Damage, 1e-4f);
    Assert.AreEqual(15f, _out[0].ExplosionDamage, 1e-4f);
    Assert.AreEqual(2f, _out[0].FireFieldDamagePerTick, 1e-4f);
    Assert.AreEqual(0.5f, _out[0].FireFieldTickInterval, 1e-4f);
    Assert.AreEqual(5f, _out[0].FireFieldDuration, 1e-4f);
}
```

- [ ] **Step 3: 新增 Modifier 语义失败测试**

```csharp
[Test]
public void DamageMultiplier_ScalesEveryMeteorDamageChannel()
{
    Run(1, 999f,
        DamageMod(1.5f),
        Static(dmg: 10f, explosionDamage: 15f, fireFieldDamagePerTick: 2f));

    Assert.AreEqual(15f, _out[0].Damage, 1e-4f);
    Assert.AreEqual(22.5f, _out[0].ExplosionDamage, 1e-4f);
    Assert.AreEqual(3f, _out[0].FireFieldDamagePerTick, 1e-4f);
}

[Test]
public void FlatDamageBonus_DoesNotRepeatOnExplosionOrEveryFireFieldTick()
{
    SpellDefinition flat = ScriptableObject.CreateInstance<SpellDefinition>();
    flat.Kind = SpellKind.Modify;
    flat.ModDamageAddFlat = 5f;
    flat.ModDamageMul = 1f;

    Run(1, 999f,
        flat,
        Static(dmg: 10f, explosionDamage: 15f, fireFieldDamagePerTick: 2f));

    Assert.AreEqual(15f, _out[0].Damage, 1e-4f);
    Assert.AreEqual(15f, _out[0].ExplosionDamage, 1e-4f);
    Assert.AreEqual(2f, _out[0].FireFieldDamagePerTick, 1e-4f);
}
```

- [ ] **Step 4: 运行 EditMode 测试确认 RED**

Run: Unity Test Framework，仅运行 `Game.Skills.Tests.CastEvaluatorTests`。

Expected: 编译失败，指出 `SpellDefinition` 和 `EmitCommand` 尚无复合伤害字段；失败原因必须是功能缺失，不得是测试拼写错误。

---

### Task 2: 在 Game.Skills 建立权威 Authoring 与纯数据快照

**Files:**
- Modify: `Assets/_Project/Scripts/Skills/SpellDefinition.cs`
- Modify: `Assets/_Project/Scripts/Skills/EmitCommand.cs`
- Modify: `Assets/_Project/Scripts/Skills/CastEvaluator.cs`

**Interfaces:**
- Consumes: `CastModifierState.DamageAddFlat`、`CastModifierState.DamageMul`
- Produces:
  - `SpellDefinition.ExplosionDamage : float`
  - `SpellDefinition.FireFieldDamagePerTick : float`
  - `SpellDefinition.FireFieldTickInterval : float`
  - `SpellDefinition.FireFieldDuration : float`
  - 对应的四个 `EmitCommand` readonly 字段

- [ ] **Step 1: 在 SpellDefinition 增加复合伤害字段**

```csharp
[Header("Impact Effects（复合命中效果）——陨石等法术使用")]
[Min(0f)]
[Tooltip("爆炸生成时只结算一次的范围伤害。0 表示没有爆炸伤害。")]
public float ExplosionDamage = 0f;

[Min(0f)]
[Tooltip("火场每次 Tick 对范围内每个敌方目标造成的伤害。")]
public float FireFieldDamagePerTick = 0f;

[Min(0.05f)]
[Tooltip("火场两次伤害结算之间的秒数；只改变节奏，不受 Damage Modifier 影响。")]
public float FireFieldTickInterval = 0.5f;

[Min(0f)]
[Tooltip("火场运行时实例的存活秒数；0 表示不生成可造成伤害的持续火场。")]
public float FireFieldDuration = 0f;
```

- [ ] **Step 2: 在 EmitCommand 构造函数和 readonly 字段中加入四个快照**

```csharp
public readonly float ExplosionDamage;
public readonly float FireFieldDamagePerTick;
public readonly float FireFieldTickInterval;
public readonly float FireFieldDuration;
```

构造函数参数放在 `damage, speed, damageType` 后，并逐一赋值，保证字段名、参数名和调用点完全一致。

- [ ] **Step 3: CastEvaluator 按既定规则 Bake**

```csharp
float damageMultiplier = Mathf.Max(0f, mods.DamageMul);
float damage = Mathf.Max(
    0f,
    (spell.BaseDamage + mods.DamageAddFlat) * damageMultiplier);
float explosionDamage = Mathf.Max(
    0f,
    spell.ExplosionDamage * damageMultiplier);
float fireFieldDamagePerTick = Mathf.Max(
    0f,
    spell.FireFieldDamagePerTick * damageMultiplier);
float fireFieldTickInterval = Mathf.Max(0.05f, spell.FireFieldTickInterval);
float fireFieldDuration = Mathf.Max(0f, spell.FireFieldDuration);
```

把五个数值一起传入 `new EmitCommand(...)`。中文注释明确说明 Flat Bonus 只进入一次性直击，Multiplier 才代表整颗复合法术的全局缩放。

- [ ] **Step 4: 运行 CastEvaluatorTests 确认 GREEN**

Run: Unity Test Framework，仅运行 `Game.Skills.Tests.CastEvaluatorTests`。

Expected: 新增三项测试和现有测试全部通过，无编译错误。

---

### Task 3: 用失败测试定义 AreaDamage 的 Runtime 配置入口

**Files:**
- Create: `Assets/_Project/Tests/Combat/AreaDamageTests.cs`
- Create: `Assets/_Project/Tests/Combat/AreaDamageTests.cs.meta`（由 Unity 导入生成）

**Interfaces:**
- Consumes: `AreaDamage`
- Produces:
  - `AreaDamage.DamagePerHit : float`
  - `AreaDamage.TickInterval : float`
  - `AreaDamage.TickLimit : int`
  - `AreaDamage.ConfigureDamage(float, DamageType)`
  - `AreaDamage.ConfigureTickInterval(float)`
  - `AreaDamage.ConfigureDuration(float)`

- [ ] **Step 1: 写失败测试**

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Game.Combat.Tests
{
    public sealed class AreaDamageTests
    {
        [Test]
        public void RuntimeConfiguration_ClampsAndStoresDamageAndTickInterval()
        {
            GameObject root = new GameObject("AreaDamageTest");
            try
            {
                AreaDamage area = root.AddComponent<AreaDamage>();

                area.ConfigureDamage(2f, DamageType.Magical);
                area.ConfigureTickInterval(0.5f);
                area.ConfigureDuration(5f);

                Assert.AreEqual(2f, area.DamagePerHit, 1e-4f);
                Assert.AreEqual(0.5f, area.TickInterval, 1e-4f);
                Assert.AreEqual(10, area.TickLimit);

                area.ConfigureDamage(-10f, DamageType.True);
                area.ConfigureTickInterval(-1f);
                area.ConfigureDuration(-1f);

                Assert.AreEqual(0f, area.DamagePerHit, 1e-4f);
                Assert.AreEqual(0f, area.TickInterval, 1e-4f);
                Assert.AreEqual(-1, area.TickLimit);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
```

- [ ] **Step 2: 运行测试确认 RED**

Run: Unity Test Framework，仅运行 `Game.Combat.Tests.AreaDamageTests`。

Expected: 编译失败，指出 Getter 和 `ConfigureTickInterval` 尚不存在。

---

### Task 4: 让 Combat Runtime 分别执行爆炸与火场快照

**Files:**
- Modify: `Assets/_Project/Scripts/Combat/Status/AreaDamage.cs`
- Modify: `Assets/_Project/Scripts/Combat/Projectiles/NovaFireball.cs`
- Modify: `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs`

**Interfaces:**
- Consumes: Task 2 的 `EmitCommand` 四个复合伤害字段
- Produces:
  - `NovaFireball.ConfigureImpactDamage(float explosionDamage, float fireFieldDamagePerTick, float fireFieldTickInterval, float fireFieldDuration)`
  - 爆炸和火场分别使用自己的伤害/节奏

- [ ] **Step 1: AreaDamage 增加低层 Runtime 配置**

```csharp
public float DamagePerHit => _damagePerHit;
public float TickInterval => _tickInterval;
public int TickLimit => _tickLimit;

public void ConfigureTickInterval(float tickInterval)
{
    _tickInterval = Mathf.Max(0f, tickInterval);
}

public void ConfigureDuration(float duration)
{
    // Start 的立即首跳也占一个预算，避免 Destroy 的延迟边界偶发多出一跳。
    _tickLimit = duration > 0f && _tickInterval > 0f
        ? Mathf.Max(1, Mathf.CeilToInt(duration / _tickInterval))
        : -1;
    _remainingTicks = _tickLimit;
}
```

保留 `ConfigureDamage` 的职责单一性：它配置数值和 DamageType；Tick Interval 由另一个明确 API 配置。

- [ ] **Step 2: NovaFireball 保存本次陨石复合快照**

```csharp
private float _explosionDamage;
private float _fireFieldDamagePerTick;
private float _fireFieldTickInterval;
private float _configuredFireFieldDuration;

public void ConfigureImpactDamage(
    float explosionDamage,
    float fireFieldDamagePerTick,
    float fireFieldTickInterval,
    float fireFieldDuration)
{
    _explosionDamage = Mathf.Max(0f, explosionDamage);
    _fireFieldDamagePerTick = Mathf.Max(0f, fireFieldDamagePerTick);
    _fireFieldTickInterval = Mathf.Max(0.05f, fireFieldTickInterval);
    _configuredFireFieldDuration = Mathf.Max(0f, fireFieldDuration);
}
```

- [ ] **Step 3: NovaFireball 分别生成两种效果**

```csharp
protected override void OnImpact(...)
{
    SpawnEffect(
        _explosionPrefab,
        hitPoint,
        _explosionRotationEuler,
        _explosionLifetime,
        _explosionDamage,
        0f);

    if (_configuredFireFieldDuration > 0f)
    {
        SpawnEffect(
            _fireFieldPrefab,
            hitPoint,
            _fireFieldRotationEuler,
            _configuredFireFieldDuration,
            _fireFieldDamagePerTick,
            _fireFieldTickInterval);
    }
}
```

`SpawnEffect` 必须对找到的每个 `AreaDamage` 调用：

```csharp
area.Init(_attackerId, _attackerTeam);
area.ConfigureDamage(damagePerHit, _type);
area.ConfigureTickInterval(tickInterval);
area.ConfigureDuration(tickInterval > 0f ? lifetime : 0f);
```

禁止继续使用 `_damage` 覆盖爆炸和火场；`_damage` 只留给 `ProjectileBase` 的直接碰撞。

- [ ] **Step 4: SpellCaster 在 Init 前注入复合快照**

```csharp
private static void ConfigureCompositeDamage(
    ProjectileBase projectile,
    in EmitCommand command)
{
    if (projectile is NovaFireball meteor)
    {
        meteor.ConfigureImpactDamage(
            command.ExplosionDamage,
            command.FireFieldDamagePerTick,
            command.FireFieldTickInterval,
            command.FireFieldDuration);
    }
}
```

在 Forward 与 Skyfall 两条生成路径中，必须在 `ProjectileBase.Init(...)` 之前调用。这样 `SpellCaster` 仍只做 Adapter，不执行 AOE。

- [ ] **Step 5: 运行 AreaDamageTests 与 CastEvaluatorTests 确认 GREEN**

Run: Unity Test Framework，运行 `Game.Combat.Tests.AreaDamageTests` 和 `Game.Skills.Tests.CastEvaluatorTests`。

Expected: 全部通过。

---

### Task 5: 更新 Tooltip，展示玩家真正会受到的三段伤害

**Files:**
- Modify: `Assets/_Project/Scripts/UI/SpellTooltipPresenter.cs`

**Interfaces:**
- Consumes: `SpellDefinition` 四个复合字段
- Produces: 静态 Authoring 数据的中文说明，不在 Hover 热路径重复分配

- [ ] **Step 1: 在缓存文本构建阶段追加复合伤害信息**

```csharp
if (spell.BaseDamage > 0f)
    _builder.Append("\n直击伤害：").Append(spell.BaseDamage.ToString("0.##"));
if (spell.ExplosionDamage > 0f)
    _builder.Append("\n爆炸伤害：").Append(spell.ExplosionDamage.ToString("0.##"));
if (spell.FireFieldDamagePerTick > 0f && spell.FireFieldDuration > 0f)
{
    int tickCount = Mathf.CeilToInt(
        spell.FireFieldDuration / Mathf.Max(0.05f, spell.FireFieldTickInterval));
    float fieldTotal = spell.FireFieldDamagePerTick * tickCount;
    _builder.Append("\n火场伤害：")
        .Append(spell.FireFieldDamagePerTick.ToString("0.##"))
        .Append(" / ")
        .Append(spell.FireFieldTickInterval.ToString("0.##"))
        .Append("秒，持续 ")
        .Append(spell.FireFieldDuration.ToString("0.##"))
        .Append("秒")
        .Append("\n理论完整伤害：")
        .Append((spell.BaseDamage + spell.ExplosionDamage + fieldTotal).ToString("0.##"));
}
```

原“基础伤害”改为“直击伤害”。仍利用 `_detailCache`，Pointer Move 不重新格式化字符串。

- [ ] **Step 2: 做静态检查**

Run: `rg -n "基础伤害|直击伤害|爆炸伤害|火场伤害|理论完整伤害" Assets/_Project/Scripts/UI/SpellTooltipPresenter.cs`

Expected: 不再把 `BaseDamage` 向玩家描述成含义模糊的“基础伤害”；新标签完整存在。

---

### Task 6: 配置玩家/Boss 数据资产与爆炸执行器

**Files:**
- Modify: `Assets/_Project/ScriptableObjects/Spell/Spell_Static_Meteor.asset`
- Modify: `Assets/_Project/ScriptableObjects/P8/Boss/BossSpell/BossSpell_Static_Meteor.asset`
- Modify: `Assets/_Project/ScriptableObjects/P8/Boss/Wands/P8_BossWand_MeteorJudgment.asset`
- Modify: `Assets/_Project/Art/Prefabs/Wizard/Magic/NukeConeExplosionFire.prefab`

**Interfaces:**
- Consumes: Task 2 的序列化字段、Task 4 的 `AreaDamage`
- Produces: 玩家 45、Boss 50 的理论完整伤害；Boss Program 不引用普通 Spell 目录

- [ ] **Step 1: 写入玩家陨石**

```yaml
BaseDamage: 10
ExplosionDamage: 15
FireFieldDamagePerTick: 2
FireFieldTickInterval: 0.5
FireFieldDuration: 5
```

- [ ] **Step 2: 写入 Boss 陨石**

```yaml
BaseDamage: 12
ExplosionDamage: 18
FireFieldDamagePerTick: 2
FireFieldTickInterval: 0.5
FireFieldDuration: 5
```

Boss DisplayName/Description 应明确这是 Boss 专属数据，避免误拖玩家资产。

- [ ] **Step 3: Boss Wand 全面切换到 BossSpell GUID**

顺序保持：

```text
BossSpell_Mdy_Gravity
BossSpell_Mult_TripleShooting
BossSpell_Emit_TriggerFireball
BossSpell_Static_Meteor
BossSpell_Emit_TriggerFireball
BossSpell_Static_Meteor
BossSpell_Emit_TriggerFireball
BossSpell_Static_Meteor
```

只替换 GUID，不改变 `BaseDraws=1` 和 Task 8 的并行 Trigger Action 语义。

- [ ] **Step 4: 给爆炸 VFX Root 增加 AreaDamage**

配置：

```text
Radius = 5
Damage Per Hit = 0（仅作为 Prefab 默认值，运行时会注入）
Damage Type = Magical
Hit Mask = Everything
Tick Interval = 0
Trigger Hit Reaction = true
```

火场 Prefab 保持 `Radius=6.7`、`Trigger Hit Reaction=false`；其 Inspector Damage Per Hit 仅为 fallback，运行时会由 SpellDefinition 注入。

- [ ] **Step 5: 资产文本验证**

Run: 使用 `rg` 检查两份 Meteor Asset 的五个字段、Boss Wand 中四类 Boss GUID，以及 Nuke Prefab 中 `AreaDamage` Script GUID。

Expected: 玩家/Boss 数值不同；Boss Wand 不再包含普通 Spell 的四个 GUID。

---

### Task 7: 更新进度与学习解析文档

**Files:**
- Modify: `Assets/_Project/Docs/2026-07-10 RPG后续开发计划.md`
- Modify: `Assets/_Project/Docs/2026-07-26 P8法术型Boss终局解析（更新中）.md`

**Interfaces:**
- Consumes: 实际完成的代码、测试和资产证据
- Produces: 初学者可复习的 Debug/架构/Editor 验证资料

- [ ] **Step 1: 进度文档记录收口后修复**

写清：

- 状态：代码完成/自动化验证情况/仍需 Editor Scene Playthrough 的项目。
- 玩家与 Boss 最终配置。
- 修改文件清单。
- 不把编译、EditMode、PlayMode、Scene Playthrough、Profiler 混写成同一种证据。

- [ ] **Step 2: P8 解析文档新增“陨石术三段伤害数据化”章节**

必须详细解释：

1. 症状：Inspector 显示 15，但玩家数秒死亡。
2. 排查证据：ProjectileBase `_consumed` 排除 Collider 重复；Nuke VFX 没有 `AreaDamage`；FireField 的 1 被 `ConfigureDamage(_damage)` 覆盖成 15。
3. 原始伤害账单：直击 15、爆炸 0、火场约 150～165，加 ElementField Burning。
4. 为什么不能只把 Prefab 的 1 调小：权威数据仍分散，Modifier 和 Tooltip 仍不透明。
5. `SpellDefinition → CastEvaluator → EmitCommand → SpellCaster → NovaFireball → AreaDamage → IDamageable` 完整 Runtime 调用链。
6. 各 Assembly 职责与单向依赖。
7. Flat Bonus 与 Multiplier 的取舍。
8. `AreaDamage.Start` 立即 Tick 对 Tick 数和理论总伤害的影响。
9. Boss 三个独立火场可叠加，为什么单颗 50 不等于玩家必吃 150。
10. ElementField Deposit/Burning 是第四条独立链，不与法术三段字段混算。
11. 测试、Editor 检查、Profiler 中 `GC Alloc`/CPU 的预期影响。
12. 常见失败：爆炸 Prefab 漏挂 AreaDamage、Boss Wand 仍引用普通资产、Tooltip Cache 未刷新、旧 Prefab fallback 值被误认为权威数据。

- [ ] **Step 3: 文档事实核对**

Run: `rg` 对照代码字段名、文件名、测试名和最终数值，删除任何未验证“已通过”表述。

---

### Task 8: 全量验证与 Editor 交接

**Files:**
- Verify only

**Interfaces:**
- Consumes: Tasks 1–7
- Produces: 可复现验证证据和最小 Editor 操作清单

- [ ] **Step 1: 编译/静态验证**

优先使用 Unity Batchmode（若 Editor 未被占用）执行 Script Compilation；若本机 Unity CLI 不可用，明确记录未执行，不以 `dotnet` 构建代替 Unity 编译证据。

- [ ] **Step 2: 运行 EditMode Tests**

至少运行：

```text
Game.Skills.Tests.CastEvaluatorTests
Game.Combat.Tests.AreaDamageTests
```

并运行现有 Skills/Combat 测试集合做回归。记录测试总数、通过/失败和日志文件。

- [ ] **Step 3: 检查 Git Diff 边界**

Run:

```powershell
git diff -- <本计划列出的文件>
git status --short
```

Expected: 没有覆盖用户现有无关改动；不修改第三方材质和无关 Scene。

- [ ] **Step 4: 给用户列出 Unity Editor 验证**

最终只要求用户完成自动化无法替代的检查：

1. 重新导入脚本并确认 Console 0 Error。
2. 打开玩家与 Boss Meteor Asset，核对五个三段伤害字段。
3. 打开 `NukeConeExplosionFire`，确认一次性 `AreaDamage`。
4. Play P7：玩家 Tooltip 显示 10/15/2、0.5 秒、5 秒、理论 45。
5. Play P8：Boss Wand 使用 BossSpell；单颗直击/爆炸/火场扣血符合 12/18/2。
6. 站在一个火场、三个重叠火场、离开火场分别观察 HP。
7. 用 `[增伤]` 验证三段乘 1.5，但 Tick Interval/Duration 不变。
8. Profiler 查看连续陨石命中时 `GC Alloc`、`Physics.OverlapSphereNonAlloc` 和 CPU；离散 Instantiate 可接受，持续 Tick 不应新增每帧托管分配。
