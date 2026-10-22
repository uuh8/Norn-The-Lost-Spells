# 法术编程系统 · 阶段 A：求值器内核 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `Game.Skills` 里建一个纯逻辑、可 EditMode 单测的法术求值器 `CastEvaluator`，把一串法术（`SpellDefinition[]`）"运行"成一组"该产出什么"的指令（`EmitCommand[]`），实现 emit / modify / multicast 三种语义 + 法力 fizzle。

**Architecture:** 对标现有 `Game.Combat.DamagePipeline`——纯静态类、不碰 Unity 实例化、输入数据进、结果数据出。求值器**只算"产出什么"，不 Instantiate**；真正生成投射物是后续阶段 B 的 `SpellCaster` 的事。`Evaluate` 带一个 `incomingMods` 参数，从第一天就让函数"可被递归调用"，为后期的触发（trigger）留好位子，但本阶段不实现触发、不接运行时、不做 UI。

**Tech Stack:** Unity 6.3 / C# / NUnit EditMode 测试 / `Game.Skills` asmdef（已存在，依赖 Core + Combat）。

**前置文档：** 设计 spec `docs/superpowers/specs/2026-06-28-spell-programming-system-design.md`（先读它的第 1~6 节理解模型）。

## Global Constraints

以下规则来自 `CLAUDE.md`，每个任务都隐含适用：

- **分工：Claude / 子代理只写 `.cs` 与纯文本配置（`.asmdef`）并提交；编译、运行 EditMode 测试由开发者在 Unity 编辑器手动完成。** 不要声称"测试通过/编译通过"，只能说"代码静态正确"。本计划中所有"运行测试"步骤标注为 **(开发者)**——子代理不执行它们，只在该步骤处停下等开发者验证。
- **不要为新建资源创建 `.meta` 文件**（Unity 自动生成）。
- **命名空间必须匹配程序集**：`Game.Skills` 源码用 `namespace Game.Skills`；测试用 `namespace Game.Skills.Tests`。
- **命名**：私有/保护字段 `_camelCase`；公有成员与类型 `PascalCase`；接口 `IXxx`。
- **性能**：`Evaluate` 由"开火"这种离散输入触发（非每帧热路径），但仍按零 GC 习惯写——产出列表由调用方预分配并 `Clear()` 复用，不在方法内 `new List`；修正状态用 `readonly struct`。禁止 LINQ / 装箱。
- **日志**：如需日志用 `Game.Core.GameLog`，绝不用 `Debug.Log`（本阶段基本不需要日志）。
- **TDD 适配说明**：本项目无法由 Claude 跑 Unity 测试，故不走严格的"先红后绿"逐步运行；每个任务内**先写测试代码、再写实现代码**，任务末尾由**开发者在 Test Runner 跑一次**作为绿灯门禁，然后提交。

## 本阶段锁定的求值语义（实现时严格照此）

- 一次"开火" = 对法杖序列做**一次从左到右的单遍读取**（不回绕）。
- `drawBudget` 初值 = `baseDraws`（基础投射物预算，调用方传，默认 1）。
- 读到 **Modify**：把该修正叠加进"当前修正状态"`mods`（影响其后产出）。不消耗预算。
- 读到 **Multicast**：`drawBudget += spell.ExtraDraws`（双重=+1、三重=+2）。不消耗预算。
- 读到 **Emit**：若 `drawBudget > 0` 且法力够 → 产出一条 `EmitCommand`（数值=基础值×当前修正快照），`drawBudget--`、扣法力；若法力不够 → 标记 `Fizzled` 并中断本次施法；若 `drawBudget <= 0` → 跳过本发（不产出）。
- 伤害公式：`(BaseDamage + DamageAddFlat) * DamageMul`；速度：`BaseSpeed * SpeedMul`；散射角度直接取 `mods.SpreadDegrees`。

## File Structure

| 文件 | 职责 |
|---|---|
| `Assets/_Project/Scripts/Skills/SpellKind.cs` | 枚举：求值器眼里的三种语义 Emit/Modify/Multicast |
| `Assets/_Project/Scripts/Skills/CastModifierState.cs` | `readonly struct`：当前累积修正 + `Default` + `Apply` |
| `Assets/_Project/Scripts/Skills/EmitCommand.cs` | `readonly struct`：一条"该产出什么"的最终数据 |
| `Assets/_Project/Scripts/Skills/SpellDefinition.cs` | `ScriptableObject`：一个法术的数据（含图标字段） |
| `Assets/_Project/Scripts/Skills/CastEvaluator.cs` | `static` 纯求值器 + `CastSummary` 返回摘要 |
| `Assets/_Project/Tests/Skills/Game.Skills.Tests.asmdef` | EditMode 测试程序集 |
| `Assets/_Project/Tests/Skills/CastModifierStateTests.cs` | 修正状态的单测 |
| `Assets/_Project/Tests/Skills/CastEvaluatorTests.cs` | 求值器的单测 |

---

### Task 1: 数据词汇 + 测试程序集

建立整套系统的"词汇"（枚举/结构/SO）和测试程序集，并用 `CastModifierState` 的纯逻辑做第一个可独立验证的单测。

**Files:**
- Create: `Assets/_Project/Scripts/Skills/SpellKind.cs`
- Create: `Assets/_Project/Scripts/Skills/CastModifierState.cs`
- Create: `Assets/_Project/Scripts/Skills/EmitCommand.cs`
- Create: `Assets/_Project/Scripts/Skills/SpellDefinition.cs`
- Create: `Assets/_Project/Tests/Skills/Game.Skills.Tests.asmdef`
- Test: `Assets/_Project/Tests/Skills/CastModifierStateTests.cs`

**Interfaces:**
- Produces（后续任务依赖这些精确名字）：
  - `enum SpellKind : byte { Emit, Modify, Multicast }`
  - `readonly struct CastModifierState`：字段 `float DamageAddFlat, DamageMul, SpeedMul, SpreadDegrees`；`static CastModifierState Default`；`CastModifierState Apply(SpellDefinition modify)`
  - `readonly struct EmitCommand(GameObject ProjectilePrefab, float Damage, float Speed, DamageType DamageType, float SpreadDegrees)`
  - `class SpellDefinition : ScriptableObject`，公有字段：`SpellKind Kind`、`string DisplayName`、`Sprite Icon`、`float ManaCost`、`GameObject ProjectilePrefab`、`float BaseDamage`、`float BaseSpeed`、`DamageType DamageType`、`float ModDamageAddFlat`、`float ModDamageMul`、`float ModSpeedMul`、`float ModSpreadAddDegrees`、`int ExtraDraws`

- [ ] **Step 1: 建枚举 `SpellKind.cs`**

```csharp
namespace Game.Skills
{
    /// <summary>法术在"求值器眼里"的语义类别。玩家可见的丰富分类（投射物/静态投射物/修正/多重…）收敛成这三种内核语义。</summary>
    public enum SpellKind : byte
    {
        Emit = 0,       // 产出一个投射物/存在物（携带当前修正快照）
        Modify = 1,     // 修改"当前修正状态"，影响其后产出的实体
        Multicast = 2,  // 扩大本次施法的投射物预算（多产出几发）
    }
}
```

- [ ] **Step 2: 建 `SpellDefinition.cs`（数据驱动法术定义）**

```csharp
using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// 一个法术的数据定义（数据驱动：新增法术 = 新建一份本资产，不写代码）。
    /// 字段按 Kind 分组使用：求值器对 Emit 读 Base*/ProjectilePrefab；对 Modify 读 Mod*；对 Multicast 读 ExtraDraws。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Skills/Spell Definition", fileName = "SpellDefinition")]
    public class SpellDefinition : ScriptableObject
    {
        [Header("通用")]
        public SpellKind Kind = SpellKind.Emit;
        public string DisplayName = "";
        [Tooltip("技能图标（仿 Noita，背包/编程框里展示）。可留空，后续补图标资源。")]
        public Sprite Icon;
        [Tooltip("施放本法术消耗的法力")]
        public float ManaCost = 0f;

        [Header("Emit（投射物）—— 仅 Kind=Emit 用")]
        [Tooltip("要生成的投射物预制体（其上需有 ProjectileBase 派生组件，如 Fireball）")]
        public GameObject ProjectilePrefab;
        public float BaseDamage = 10f;
        public float BaseSpeed = 20f;
        public DamageType DamageType = DamageType.Magical;

        [Header("Modify（修正）—— 仅 Kind=Modify 用（默认值为恒等：不改变任何东西）")]
        public float ModDamageAddFlat = 0f;
        public float ModDamageMul = 1f;
        public float ModSpeedMul = 1f;
        public float ModSpreadAddDegrees = 0f;

        [Header("Multicast（多重）—— 仅 Kind=Multicast 用")]
        [Tooltip("本次施法额外增加的投射物预算。双重=1，三重=2")]
        public int ExtraDraws = 0;
    }
}
```

- [ ] **Step 3: 建 `CastModifierState.cs`**

```csharp
namespace Game.Skills
{
    /// <summary>
    /// 求值过程中"当前累积的修正"。求值器从左到右读取修正法术时更新它；产出投射物时把它快照进 EmitCommand。
    /// readonly struct：按值传递、零 GC、可作为递归求值的"继承起点"（后期触发用）。
    /// </summary>
    public readonly struct CastModifierState
    {
        public readonly float DamageAddFlat; // 平铺加伤（先加）
        public readonly float DamageMul;     // 伤害倍率（后乘）
        public readonly float SpeedMul;      // 速度倍率
        public readonly float SpreadDegrees; // 散射角度（扇形半角，度）

        public CastModifierState(float damageAddFlat, float damageMul, float speedMul, float spreadDegrees)
        {
            DamageAddFlat = damageAddFlat;
            DamageMul = damageMul;
            SpeedMul = speedMul;
            SpreadDegrees = spreadDegrees;
        }

        /// <summary>初始（恒等）状态：加伤 0、倍率 1、散射 0。乘法用 1 作单位元，保证"只改伤害的修正"不影响速度。</summary>
        public static CastModifierState Default => new CastModifierState(0f, 1f, 1f, 0f);

        /// <summary>把一个 Modify 法术叠加到当前状态，返回新状态（不可变）。加法项相加、乘法项相乘、散射相加。</summary>
        public CastModifierState Apply(SpellDefinition modify)
        {
            return new CastModifierState(
                DamageAddFlat + modify.ModDamageAddFlat,
                DamageMul * modify.ModDamageMul,
                SpeedMul * modify.ModSpeedMul,
                SpreadDegrees + modify.ModSpreadAddDegrees);
        }
    }
}
```

- [ ] **Step 4: 建 `EmitCommand.cs`**

```csharp
using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// 求值器输出的一条"该产出什么"的纯数据。运行时 SpellCaster（阶段 B）据此 Instantiate 投射物并调 ProjectileBase.Init。
    /// 数值已是"基础值×修正快照"的最终结果，SpellCaster 不再二次计算。
    /// </summary>
    public readonly struct EmitCommand
    {
        public readonly GameObject ProjectilePrefab; // 要生成的投射物预制体（来自 emit 法术）
        public readonly float Damage;                // 最终伤害
        public readonly float Speed;                 // 最终速度
        public readonly DamageType DamageType;       // 伤害类型
        public readonly float SpreadDegrees;         // 散射角度（SpellCaster 据此把多发打散成扇形）

        public EmitCommand(GameObject projectilePrefab, float damage, float speed, DamageType damageType, float spreadDegrees)
        {
            ProjectilePrefab = projectilePrefab;
            Damage = damage;
            Speed = speed;
            DamageType = damageType;
            SpreadDegrees = spreadDegrees;
        }
    }
}
```

- [ ] **Step 5: 建测试程序集 `Game.Skills.Tests.asmdef`**

```json
{
    "name": "Game.Skills.Tests",
    "rootNamespace": "",
    "references": [
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner",
        "Game.Skills",
        "Game.Combat",
        "Game.Core"
    ],
    "includePlatforms": [
        "Editor"
    ],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [
        "UNITY_INCLUDE_TESTS"
    ],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 6: 写 `CastModifierStateTests.cs`**

```csharp
using NUnit.Framework;
using UnityEngine;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class CastModifierStateTests
    {
        private static SpellDefinition Modify(float dmgMul = 1f, float speedMul = 1f, float dmgAdd = 0f, float spread = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Modify;
            s.ModDamageMul = dmgMul;
            s.ModSpeedMul = speedMul;
            s.ModDamageAddFlat = dmgAdd;
            s.ModSpreadAddDegrees = spread;
            return s;
        }

        [Test]
        public void Default_IsIdentity()
        {
            var d = CastModifierState.Default;
            Assert.AreEqual(0f, d.DamageAddFlat, 1e-4f);
            Assert.AreEqual(1f, d.DamageMul, 1e-4f);
            Assert.AreEqual(1f, d.SpeedMul, 1e-4f);
            Assert.AreEqual(0f, d.SpreadDegrees, 1e-4f);
        }

        [Test]
        public void Apply_DamageMul_Multiplies_LeavesSpeedUnchanged()
        {
            var s = CastModifierState.Default.Apply(Modify(dmgMul: 1.5f));
            Assert.AreEqual(1.5f, s.DamageMul, 1e-4f);
            Assert.AreEqual(1f, s.SpeedMul, 1e-4f);   // 恒等保持：只改伤害不影响速度
        }

        [Test]
        public void Apply_Twice_AccumulatesMultiplicatively()
        {
            var s = CastModifierState.Default.Apply(Modify(dmgMul: 2f)).Apply(Modify(dmgMul: 2f));
            Assert.AreEqual(4f, s.DamageMul, 1e-4f);
        }

        [Test]
        public void Apply_SpreadAndFlat_AreAdditive()
        {
            var s = CastModifierState.Default.Apply(Modify(dmgAdd: 5f, spread: 15f)).Apply(Modify(dmgAdd: 5f, spread: 15f));
            Assert.AreEqual(10f, s.DamageAddFlat, 1e-4f);
            Assert.AreEqual(30f, s.SpreadDegrees, 1e-4f);
        }
    }
}
```

- [ ] **Step 7: (开发者) 编译 + 跑 EditMode 测试**

操作：Unity → `Window → General → Test Runner` → `EditMode` 标签 → `Run All`。
预期：`CastModifierStateTests` 的 4 个测试全部 **PASS**；工程无编译错误。

- [ ] **Step 8: 提交**

```bash
git add Assets/_Project/Scripts/Skills/ Assets/_Project/Tests/Skills/
git commit -m "feat(skills): add spell data vocabulary + modifier state with tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: 求值器核心 —— emit + modify + 预算

实现求值器的线性骨架：单遍读取、修正累积、预算限制产出。**这一步就把 `Evaluate` 的完整签名定死**（含 `availableMana` 与 `incomingMods` 参数），本任务先不实现法力与多重逻辑，但参数位置一次留对，后续任务只加逻辑、不改签名。

**Files:**
- Create: `Assets/_Project/Scripts/Skills/CastEvaluator.cs`
- Test: `Assets/_Project/Tests/Skills/CastEvaluatorTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SpellDefinition` / `SpellKind` / `CastModifierState` / `EmitCommand`。
- Produces:
  - `readonly struct CastSummary(float ManaSpent, bool Fizzled)`
  - `static CastSummary CastEvaluator.Evaluate(IReadOnlyList<SpellDefinition> spells, int baseDraws, float availableMana, CastModifierState incomingMods, List<EmitCommand> output)` —— `output` 由调用方预分配，方法内 `Clear()` 后回填。

- [ ] **Step 1: 写 `CastEvaluator.cs`（含 emit + modify + 预算；mana 参数暂不生效）**

```csharp
using System.Collections.Generic;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>求值结果摘要（产出列表通过 output 参数回填，避免每次施法都分配新 List）。</summary>
    public readonly struct CastSummary
    {
        public readonly float ManaSpent;
        public readonly bool Fizzled; // 因法力不足提前中断

        public CastSummary(float manaSpent, bool fizzled)
        {
            ManaSpent = manaSpent;
            Fizzled = fizzled;
        }
    }

    /// <summary>
    /// 法术编程系统的解释器内核：从左到右"运行"一段法杖序列，算出本次施法该产出哪些投射物。
    /// 纯逻辑、不碰 Unity 实例化（对标 Combat.DamagePipeline），可 EditMode 单测。运行时由 SpellCaster（阶段 B）把 EmitCommand 变成真实投射物。
    /// 语义：Emit 产出（消耗预算）；Modify 累积修正（影响其后）；Multicast 增大预算；预算耗尽或法力不足即停。单遍读取、不回绕。
    /// incomingMods 让本方法可被递归调用（后期触发：命中时以快照为起点再跑子序列）。
    /// </summary>
    public static class CastEvaluator
    {
        public static CastSummary Evaluate(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            float availableMana,
            CastModifierState incomingMods,
            List<EmitCommand> output)
        {
            output.Clear();
            if (spells == null || spells.Count == 0)
                return new CastSummary(0f, false);

            int drawBudget = baseDraws;
            CastModifierState mods = incomingMods;

            for (int i = 0; i < spells.Count; i++)
            {
                SpellDefinition spell = spells[i];
                if (spell == null) continue;

                switch (spell.Kind)
                {
                    case SpellKind.Modify:
                        mods = mods.Apply(spell);
                        break;

                    case SpellKind.Emit:
                        if (drawBudget <= 0) break; // 预算用尽，本发不产出
                        output.Add(BakeEmit(spell, mods));
                        drawBudget--;
                        break;

                    // SpellKind.Multicast：在 Task 3 加入
                }
            }

            return new CastSummary(0f, false);
        }

        /// <summary>把一个 Emit 法术按当前修正快照算出最终产出指令。伤害=(基础+平铺加)×倍率；速度=基础×倍率。</summary>
        private static EmitCommand BakeEmit(SpellDefinition spell, CastModifierState mods)
        {
            float damage = (spell.BaseDamage + mods.DamageAddFlat) * mods.DamageMul;
            float speed = spell.BaseSpeed * mods.SpeedMul;
            return new EmitCommand(spell.ProjectilePrefab, damage, speed, spell.DamageType, mods.SpreadDegrees);
        }
    }
}
```

- [ ] **Step 2: 写 `CastEvaluatorTests.cs`（emit/modify/预算 部分）**

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Game.Skills;
using Game.Combat;

namespace Game.Skills.Tests
{
    public class CastEvaluatorTests
    {
        private readonly List<EmitCommand> _out = new List<EmitCommand>();

        // ── 构造测试用法术 ──
        private static SpellDefinition Emit(float dmg = 10f, float speed = 20f, float mana = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Emit;
            s.BaseDamage = dmg; s.BaseSpeed = speed; s.DamageType = DamageType.Magical; s.ManaCost = mana;
            return s;
        }
        private static SpellDefinition DamageMod(float mul)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Modify; s.ModDamageMul = mul;
            return s;
        }
        private static SpellDefinition Multi(int extra)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Multicast; s.ExtraDraws = extra;
            return s;
        }

        private CastSummary Run(int baseDraws, float mana, params SpellDefinition[] wand)
            => CastEvaluator.Evaluate(wand, baseDraws, mana, CastModifierState.Default, _out);

        [Test]
        public void EmptyWand_EmitsNothing()
        {
            Run(1, 999f);
            Assert.AreEqual(0, _out.Count);
        }

        [Test]
        public void SingleFireball_EmitsOne_WithBaseValues()
        {
            Run(1, 999f, Emit(dmg: 15f, speed: 20f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(15f, _out[0].Damage, 1e-4f);
            Assert.AreEqual(20f, _out[0].Speed, 1e-4f);
        }

        [Test]
        public void DamageMod_BeforeEmit_BoostsIt()
        {
            Run(1, 999f, DamageMod(1.5f), Emit(dmg: 15f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(22.5f, _out[0].Damage, 1e-4f);
        }

        [Test]
        public void DamageMod_AfterEmit_DoesNotBoostIt()
        {
            Run(1, 999f, Emit(dmg: 15f), DamageMod(1.5f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(15f, _out[0].Damage, 1e-4f); // 修正只影响其后
        }

        [Test]
        public void BaseDraws_LimitsEmits()
        {
            Run(1, 999f, Emit(), Emit());          // 预算 1 → 只产出第一发
            Assert.AreEqual(1, _out.Count);

            Run(2, 999f, Emit(), Emit());          // 预算 2 → 两发都产出
            Assert.AreEqual(2, _out.Count);
        }
    }
}
```

- [ ] **Step 3: (开发者) 跑 EditMode 测试**

操作：Test Runner → EditMode → Run All。
预期：`CastEvaluatorTests` 的 5 个测试 + Task 1 的 4 个测试全部 **PASS**。

- [ ] **Step 4: 提交**

```bash
git add Assets/_Project/Scripts/Skills/CastEvaluator.cs Assets/_Project/Tests/Skills/CastEvaluatorTests.cs
git commit -m "feat(skills): CastEvaluator linear core (emit + modify + draw budget)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: 多重施法 (Multicast)

给求值器加上多重语义：读到 multicast 就把 `ExtraDraws` 加进预算。这是"组合涌现"的关键演示（`[三重, 增伤, 火球×3]` → 3 发都增伤）。

**Files:**
- Modify: `Assets/_Project/Scripts/Skills/CastEvaluator.cs`（在 `Evaluate` 的 switch 里加一个 case）
- Modify: `Assets/_Project/Tests/Skills/CastEvaluatorTests.cs`（追加多重相关测试）

**Interfaces:** 不变（仅在既有 `Evaluate` 内部加分支）。

- [ ] **Step 1: 在 `CastEvaluator.Evaluate` 的 switch 中加入 Multicast 分支**

把 `Evaluate` 内的 switch 替换为下面这版（其余代码不变，仅多了 `case SpellKind.Multicast`）：

```csharp
                switch (spell.Kind)
                {
                    case SpellKind.Modify:
                        mods = mods.Apply(spell);
                        break;

                    case SpellKind.Multicast:
                        drawBudget += spell.ExtraDraws; // 双重 +1 / 三重 +2，扩大本次预算
                        break;

                    case SpellKind.Emit:
                        if (drawBudget <= 0) break;
                        output.Add(BakeEmit(spell, mods));
                        drawBudget--;
                        break;
                }
```

- [ ] **Step 2: 在 `CastEvaluatorTests.cs` 追加多重测试**

在 `CastEvaluatorTests` 类内追加以下测试方法：

```csharp
        [Test]
        public void Triple_WithThreeProjectiles_EmitsThree()
        {
            Run(1, 999f, Multi(2), Emit(), Emit(), Emit()); // 预算 1+2=3
            Assert.AreEqual(3, _out.Count);
        }

        [Test]
        public void Triple_DamageMod_AppliesToAllEmits()
        {
            Run(1, 999f, Multi(2), DamageMod(2f), Emit(dmg: 10f), Emit(dmg: 10f), Emit(dmg: 10f));
            Assert.AreEqual(3, _out.Count);
            foreach (var e in _out)
                Assert.AreEqual(20f, e.Damage, 1e-4f); // 多重内修正一次成本、作用于全部
        }

        [Test]
        public void Multicast_BudgetUnfilled_IsDiscarded_NoWrap()
        {
            Run(1, 999f, Multi(2), Emit()); // 预算 3 但只有 1 个投射物 → 产出 1（单遍不回绕，余量作废）
            Assert.AreEqual(1, _out.Count);
        }

        [Test]
        public void Multicast_AfterEmit_ReopensBudget()
        {
            Run(1, 999f, Emit(), Multi(2), Emit()); // 1→产出#1(预算0)→+2(预算2)→产出#2
            Assert.AreEqual(2, _out.Count);
        }
```

- [ ] **Step 3: (开发者) 跑 EditMode 测试**

操作：Test Runner → EditMode → Run All。
预期：新增 4 个多重测试 + 之前全部测试 **PASS**。

- [ ] **Step 4: 提交**

```bash
git add Assets/_Project/Scripts/Skills/CastEvaluator.cs Assets/_Project/Tests/Skills/CastEvaluatorTests.cs
git commit -m "feat(skills): add multicast (draw budget expansion) to evaluator

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: 法力与 fizzle

给求值器加上法力约束：每个 emit 法术扣 `ManaCost`，法力不足时中断本次施法并标记 `Fizzled`。这是"组合不能无脑连发"的唯一闸门，也补全 `CastSummary.ManaSpent`。

**Files:**
- Modify: `Assets/_Project/Scripts/Skills/CastEvaluator.cs`（替换 `Evaluate` 方法体，加入法力逻辑）
- Modify: `Assets/_Project/Tests/Skills/CastEvaluatorTests.cs`（追加法力测试）

**Interfaces:** 不变（签名 Task 2 已定；本步实现 `availableMana` 与 `CastSummary.ManaSpent/Fizzled` 的真实语义）。

- [ ] **Step 1: 用含法力逻辑的版本替换整个 `Evaluate` 方法**

将 `CastEvaluator.Evaluate` 整个方法替换为：

```csharp
        public static CastSummary Evaluate(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            float availableMana,
            CastModifierState incomingMods,
            List<EmitCommand> output)
        {
            output.Clear();
            if (spells == null || spells.Count == 0)
                return new CastSummary(0f, false);

            int drawBudget = baseDraws;
            float manaLeft = availableMana;
            float manaSpent = 0f;
            bool fizzled = false;
            CastModifierState mods = incomingMods;

            for (int i = 0; i < spells.Count; i++)
            {
                SpellDefinition spell = spells[i];
                if (spell == null) continue;

                switch (spell.Kind)
                {
                    case SpellKind.Modify:
                        mods = mods.Apply(spell);
                        break;

                    case SpellKind.Multicast:
                        drawBudget += spell.ExtraDraws;
                        break;

                    case SpellKind.Emit:
                        if (drawBudget <= 0) break;          // 预算用尽：本发不产出（但继续读，后面的多重可能再开预算）
                        if (spell.ManaCost > manaLeft)        // 法力不足：中断本次施法
                        {
                            fizzled = true;
                            break;
                        }
                        manaLeft -= spell.ManaCost;
                        manaSpent += spell.ManaCost;
                        output.Add(BakeEmit(spell, mods));
                        drawBudget--;
                        break;
                }

                if (fizzled) break; // 跳出整个读取循环
            }

            return new CastSummary(manaSpent, fizzled);
        }
```

- [ ] **Step 2: 在 `CastEvaluatorTests.cs` 追加法力测试**

在 `CastEvaluatorTests` 类内追加：

```csharp
        [Test]
        public void EnoughMana_EmitsAll_ReportsSpent()
        {
            var summary = Run(1, 100f, Multi(2), Emit(mana: 6f), Emit(mana: 6f), Emit(mana: 6f));
            Assert.AreEqual(3, _out.Count);
            Assert.IsFalse(summary.Fizzled);
            Assert.AreEqual(18f, summary.ManaSpent, 1e-4f);
        }

        [Test]
        public void InsufficientMana_FizzlesMidCast()
        {
            // 可用 10，每发 6：第 1 发后剩 4，第 2 发 6>4 → fizzle
            var summary = Run(1, 10f, Multi(2), Emit(mana: 6f), Emit(mana: 6f), Emit(mana: 6f));
            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(summary.Fizzled);
            Assert.AreEqual(6f, summary.ManaSpent, 1e-4f);
        }

        [Test]
        public void ZeroCostSpells_NeverFizzle()
        {
            var summary = Run(1, 0f, Emit(mana: 0f));
            Assert.AreEqual(1, _out.Count);
            Assert.IsFalse(summary.Fizzled);
        }
```

- [ ] **Step 3: (开发者) 跑 EditMode 测试 —— 阶段 A 全绿门禁**

操作：Test Runner → EditMode → Run All。
预期：本任务 3 个法力测试 + 前述所有测试（Task1 的 4 + Task2 的 5 + Task3 的 4 = 13，加本任务 3，共 **16 个测试**）全部 **PASS**。

- [ ] **Step 4: 提交**

```bash
git add Assets/_Project/Scripts/Skills/CastEvaluator.cs Assets/_Project/Tests/Skills/CastEvaluatorTests.cs
git commit -m "feat(skills): add mana cost + fizzle to evaluator

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## 阶段 A 完成后的产物

- `Game.Skills` 里一个纯逻辑、16 个单测护航的求值器，能把任意 `[多重/修正/投射物]` 序列正确算成一组 `EmitCommand`。
- **完全不接 Unity 运行时、不生成任何投射物、无 UI**——这些是阶段 B/C/D。
- 求值器签名已为"递归调用"（触发）留好 `incomingMods` 参数，阶段 D 加触发是纯增量。

## 不在本阶段范围（明确排除）

- 把 `EmitCommand` 变成真实投射物 / 接 `CombatDamage` / 法师左键改成跑法杖 → **阶段 B**。
- 背包 + 拖拽编程框 UI + 技能图标展示 → **阶段 C**（图标字段本阶段已在 `SpellDefinition` 预留）。
- 触发变种（递归 + payload + 性能护栏）、`CasterConfig`（充能/施放延迟/法力回复随时间）、牌组指针/回绕 → **后续阶段**。

## Self-Review（写完后自检）

- **Spec 覆盖**：emit/modify（T2）、multicast（T3）、mana fizzle（T4）、图标字段（T1 的 `SpellDefinition.Icon`）、递归就绪签名 `incomingMods`（T2）、纯函数可测（全程 EditMode）、`EmitCommand` 携带最终值（T2 `BakeEmit`）、spec 第 11 节单测清单逐条对应（空/单发/修正前后/预算/多重/多重余量作废/法力 fizzle）。✅
- **占位符扫描**：无 TBD/TODO；每个代码步骤均为完整可编译代码。✅
- **类型一致性**：`SpellKind`、`CastModifierState`（DamageAddFlat/DamageMul/SpeedMul/SpreadDegrees）、`EmitCommand`（ProjectilePrefab/Damage/Speed/DamageType/SpreadDegrees）、`CastSummary`（ManaSpent/Fizzled）、`Evaluate(spells, baseDraws, availableMana, incomingMods, output)`、`BakeEmit` 在 T1–T4 全程一致；`Evaluate` 签名在 T2 一次定死，T3/T4 只加逻辑不改签名。✅
