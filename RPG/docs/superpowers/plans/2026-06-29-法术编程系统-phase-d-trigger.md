# 法术编程系统 · 阶段 D：触发递归 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给法术加"触发"：触发投射物命中时，在命中点再施放它后面的法术（载荷）；若载荷里又是触发，则继续往后链（和 Noita 一样的链式触发）。

**Architecture:** 触发 = emit 法术加一个 `IsTrigger` 标记。求值器读到触发 emit 时，把其后的法术后缀**捕获为载荷**塞进 `EmitCommand` 并结束本层求值；`SpellCaster` 生成触发投射物时订阅其命中事件，命中时**以预算 1 在命中点再运行一次求值器**（复用阶段 A 早留的 `incomingMods` 接缝）。`ProjectileBase` 只新增一个通用"命中通知"事件，保持 Combat 不反依赖 Skills/Character。

**Tech Stack:** Unity 6.3 / C# / 复用 `CastEvaluator`、`ProjectileBase`、`SpellCaster` / NUnit EditMode（求值器触发捕获可单测）。

**前置：** spec `docs/superpowers/specs/2026-06-29-spell-system-phase-d-trigger-design.md`（先读它的第 1~4 节理解"触发=带载荷的投射物、命中时再运行一次求值器=递归"）。阶段 A/B/C 已合入 main。

## Global Constraints

- **分工**：Claude/子代理只写 `.cs` 并提交；编译、跑 EditMode 测试、建资产、Play 验证由开发者在 Unity 手动做。"运行/编译/Play"步骤标 **(开发者)**，子代理在该步停下、不执行、不臆造结果。
- 子代理**不创建 `.meta`**（Unity 生成）。
- 命名空间匹配程序集：`Game.Skills` / `Game.Combat` / `Game.Character`。命名：私有字段 `_camelCase`，公有 `PascalCase`。
- 日志用 `Game.Core.GameLog`，不用 `Debug.Log`。
- 性能：求值/施放在"开火/命中"这种离散事件触发（非每帧热路径）；捕获载荷会一次性 `new List`（每个触发一次），属"离散输入一次性分配"，可接受；禁止 LINQ/装箱在循环里。
- **不设递归护栏**（spec 决策 3）：有限法杖 `Capacity` + **载荷是严格更短的后缀** → 递归天然收敛。⚠️ 这条安全依赖"载荷=更短后缀"这个不变量，代码注释里写明，别破坏它。
- **模块隔离**：`ProjectileBase`（Game.Combat）只暴露一个通用 `Impacted` 事件（命中点/方向），**不引用** Skills/Character；由 `SpellCaster`（Game.Character）订阅它来跑载荷。依赖方向不变。
- **锁定的触发语义**（实现严格照此）：求值器读到"触发 emit"且预算>0、法力够 → 产出该投射物、把"其后的后缀"捕获为载荷、**结束本层求值**（后缀不在本层单独产出）；命中时 `Evaluate(载荷, baseDraws=1, incomingMods=载荷快照)` 在命中点产出（下一个若也是触发就继续链）。仅"命中触发"，飞行未命中（超时自毁）不触发。

## File Structure

| 程序集 | 文件 | 新增/改动 |
|---|---|---|
| Game.Skills | `SpellDefinition.cs` | 改：Emit 段加 `bool IsTrigger` |
| Game.Skills | `EmitCommand.cs` | 改：加载荷字段 `Payload` + `PayloadMods` + `HasPayload` |
| Game.Skills | `CastEvaluator.cs` | 改：触发 emit 捕获后缀为载荷 + 结束本层；`BakeEmit` 带 payload；新增 `CaptureSuffix` |
| Game.Skills.Tests | `CastEvaluatorTests.cs` | 改：追加触发捕获测试 |
| Game.Combat | `ProjectileBase.cs` | 改：加 `event Action<Vector3,Vector3> Impacted`，命中时触发 |
| Game.Character | `SpellCaster.cs` | 改：抽出 `RunCast` 核心 + 触发投射物命中时跑载荷（递归） |

---

### Task 1: 求值器的触发捕获（Game.Skills，可单测）

> 💡 **这一步在干嘛**：让"法术"能被标记为触发；让"产出指令"能携带一段载荷；让求值器读到触发时，把它后面的法术**打包成载荷**挂上去，并就此停止本轮产出（后面那串改成"命中时才发生"）。这一步是纯逻辑、可以单测——能脱离 Unity 验证"触发到底捕获了什么"。

**Files:**
- Modify: `Assets/_Project/Scripts/Skills/SpellDefinition.cs`
- Modify: `Assets/_Project/Scripts/Skills/EmitCommand.cs`
- Modify: `Assets/_Project/Scripts/Skills/CastEvaluator.cs`
- Test: `Assets/_Project/Tests/Skills/CastEvaluatorTests.cs`

**Interfaces:**
- Produces:
  - `SpellDefinition.IsTrigger`（bool，Emit 用）
  - `EmitCommand.Payload`（`IReadOnlyList<SpellDefinition>`，非触发为 null）、`EmitCommand.PayloadMods`（`CastModifierState`）、`EmitCommand.HasPayload`（bool）
  - `EmitCommand` 新构造签名：`(GameObject, float damage, float speed, DamageType, float spreadDegrees, AudioClip castSfx, IReadOnlyList<SpellDefinition> payload, CastModifierState payloadMods)`

- [ ] **Step 1: `SpellDefinition.cs` 加 `IsTrigger`**

把 Emit 段（`DamageType` + `CastSfx` 那几行）改为：

```csharp
        public float BaseDamage = 10f;
        public float BaseSpeed = 20f;
        public DamageType DamageType = DamageType.Magical;
        [Tooltip("施放音效。一次施法里同一音效只播一次（多重/连发不会叠成多声）。可留空。")]
        public AudioClip CastSfx;
        [Tooltip("是否触发投射物：命中时在命中点再施放它之后的法术(载荷)；下一个若也是触发则继续链。仅 Kind=Emit 有意义。")]
        public bool IsTrigger = false;
```

- [ ] **Step 2: `EmitCommand.cs` 加载荷字段**（整文件替换）

```csharp
using System.Collections.Generic;
using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// 求值器输出的一条"该产出什么"的纯数据。运行时 SpellCaster 据此 Instantiate 投射物并调 ProjectileBase.Init。
    /// 数值已是"基础值×修正快照"的最终结果。触发投射物额外带"载荷"：命中时再施放的法术后缀 + 其起始修正快照。
    /// </summary>
    public readonly struct EmitCommand
    {
        public readonly GameObject ProjectilePrefab; // 要生成的投射物预制体
        public readonly float Damage;                // 最终伤害
        public readonly float Speed;                 // 最终速度
        public readonly DamageType DamageType;       // 伤害类型
        public readonly float SpreadDegrees;         // 散射角度
        public readonly AudioClip CastSfx;           // 施放音效（一次施法去重）
        public readonly IReadOnlyList<SpellDefinition> Payload; // 触发载荷：命中时施放的法术后缀；非触发为 null
        public readonly CastModifierState PayloadMods;          // 载荷的起始修正快照（继承触发当时的加成）

        public bool HasPayload => Payload != null && Payload.Count > 0;

        public EmitCommand(GameObject projectilePrefab, float damage, float speed, DamageType damageType,
                           float spreadDegrees, AudioClip castSfx,
                           IReadOnlyList<SpellDefinition> payload, CastModifierState payloadMods)
        {
            ProjectilePrefab = projectilePrefab;
            Damage = damage;
            Speed = speed;
            DamageType = damageType;
            SpreadDegrees = spreadDegrees;
            CastSfx = castSfx;
            Payload = payload;
            PayloadMods = payloadMods;
        }
    }
}
```

- [ ] **Step 3: `CastEvaluator.cs` 触发捕获**（替换 `Evaluate` 方法体的 emit 分支 + `BakeEmit` + 新增 `CaptureSuffix`）

把 `Evaluate` 里的 `for` 循环整段替换为下面这版（新增 `bool ended` + 触发分支；其余不变）：

```csharp
            int drawBudget = baseDraws;
            float manaLeft = availableMana;
            float manaSpent = 0f;
            bool fizzled = false;
            CastModifierState mods = incomingMods;

            for (int i = 0; i < spells.Count; i++)
            {
                SpellDefinition spell = spells[i];
                if (spell == null) continue;

                bool ended = false;

                switch (spell.Kind)
                {
                    case SpellKind.Modify:
                        mods = mods.Apply(spell);
                        break;

                    case SpellKind.Multicast:
                        drawBudget += spell.ExtraDraws;
                        break;

                    case SpellKind.Emit:
                        if (drawBudget <= 0) break;          // 预算用尽：本发不产出
                        if (spell.ManaCost > manaLeft)        // 法力不足：中断本次施法
                        {
                            fizzled = true;
                            break;
                        }
                        manaLeft -= spell.ManaCost;
                        manaSpent += spell.ManaCost;
                        if (spell.IsTrigger)
                        {
                            // 触发：把"其后的后缀"捕获为载荷（命中时再跑），并结束本层求值（后缀不在本层单独产出）
                            output.Add(BakeEmit(spell, mods, CaptureSuffix(spells, i + 1)));
                            drawBudget--;
                            ended = true;
                            break;
                        }
                        output.Add(BakeEmit(spell, mods, null)); // 普通产出：无载荷
                        drawBudget--;
                        break;
                }

                if (fizzled || ended) break; // 法力不足 或 触发结束本层 → 跳出读取循环
            }

            return new CastSummary(manaSpent, fizzled);
```

把 `BakeEmit` 替换为带 payload 的版本，并在其下新增 `CaptureSuffix`：

```csharp
        /// <summary>把一个 Emit 法术按当前修正快照算出最终产出指令。payload 仅触发投射物非空。</summary>
        private static EmitCommand BakeEmit(SpellDefinition spell, CastModifierState mods, IReadOnlyList<SpellDefinition> payload)
        {
            float damage = (spell.BaseDamage + mods.DamageAddFlat) * mods.DamageMul;
            float speed = spell.BaseSpeed * mods.SpeedMul;
            return new EmitCommand(spell.ProjectilePrefab, damage, speed, spell.DamageType, mods.SpreadDegrees, spell.CastSfx, payload, mods);
        }

        /// <summary>
        /// 捕获触发的载荷 = 序列中 start 起的后缀（跳过 null）。这是一条"比当前序列更短的后缀"——
        /// 递归（链式触发）据此天然收敛（每深一层、待处理序列更短），所以无需递归护栏。别把它改成整根序列。
        /// 在"命中"这种离散事件触发，一次性分配可接受。
        /// </summary>
        private static List<SpellDefinition> CaptureSuffix(IReadOnlyList<SpellDefinition> spells, int start)
        {
            var payload = new List<SpellDefinition>();
            for (int j = start; j < spells.Count; j++)
                if (spells[j] != null) payload.Add(spells[j]);
            return payload;
        }
```

- [ ] **Step 4: `CastEvaluatorTests.cs` 追加触发测试**

先把现有的 `Emit` 工厂方法**替换**为带 `trigger` 参数的版本（默认 false，不影响现有调用）：

```csharp
        private static SpellDefinition Emit(float dmg = 10f, float speed = 20f, float mana = 0f, bool trigger = false)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Emit;
            s.BaseDamage = dmg; s.BaseSpeed = speed; s.DamageType = DamageType.Magical; s.ManaCost = mana;
            s.IsTrigger = trigger;
            return s;
        }
```

再在 `CastEvaluatorTests` 类内追加：

```csharp
        [Test]
        public void NonTrigger_HasNoPayload()
        {
            Run(1, 999f, Emit());
            Assert.AreEqual(1, _out.Count);
            Assert.IsFalse(_out[0].HasPayload);
        }

        [Test]
        public void Trigger_CapturesSuffixAsPayload_AndEndsCast()
        {
            // 触发火球 + 2 个后续 → 本层只产出触发火球；后续 2 个成为它的载荷
            Run(1, 999f, Emit(trigger: true), Emit(), Emit());
            Assert.AreEqual(1, _out.Count);           // 后缀不在本层单独产出
            Assert.IsTrue(_out[0].HasPayload);
            Assert.AreEqual(2, _out[0].Payload.Count); // 载荷 = 触发之后的 2 个
        }

        [Test]
        public void Trigger_PayloadExcludesTriggerItself()
        {
            var trig = Emit(trigger: true);
            var after = Emit();
            Run(1, 999f, trig, after);
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(1, _out[0].Payload.Count);
            Assert.AreSame(after, _out[0].Payload[0]); // 载荷是"之后的"，不含触发自身
        }

        [Test]
        public void Trigger_AtSequenceEnd_HasEmptyPayload()
        {
            Run(1, 999f, Emit(trigger: true)); // 触发后面没东西
            Assert.AreEqual(1, _out.Count);
            Assert.IsFalse(_out[0].HasPayload); // 空载荷 → 命中时不再产出
        }
```

- [ ] **Step 5: (开发者) 跑 EditMode 测试**

Test Runner → EditMode → Run All。预期：新增 4 个触发测试 + 既有 29 个全部 **PASS**（共 33）；编译无错。

- [ ] **Step 6: 提交**

```bash
git add Assets/_Project/Scripts/Skills/SpellDefinition.cs Assets/_Project/Scripts/Skills/EmitCommand.cs Assets/_Project/Scripts/Skills/CastEvaluator.cs Assets/_Project/Tests/Skills/CastEvaluatorTests.cs
git commit -m "feat(skills): trigger spells — evaluator captures suffix as payload (chained), with tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: 投射物命中通知接缝（Game.Combat）

> 💡 **这一步在干嘛**：触发要在投射物"命中那一刻"做事，但 `ProjectileBase` 在 Combat 模块、不能依赖法术系统。所以这里只给它开一个**通用的"我命中了"广播**（带命中点和方向），谁想监听都行。法术触发(Game.Character)去监听它来跑载荷——这样 Combat 不反依赖上层，隔离不破。

**Files:**
- Modify: `Assets/_Project/Scripts/Combat/Projectiles/ProjectileBase.cs`

**Interfaces:**
- Produces: `ProjectileBase.Impacted`（`event System.Action<Vector3, Vector3>`，参数 = 命中点、命中方向）。命中真实目标/环境时触发一次；同阵营穿过不算命中、不触发；超时自毁不触发。

- [ ] **Step 1: 加 `Impacted` 事件字段**

在 `ProjectileBase` 字段区（如 `_launchVelocity` 附近）加：

```csharp
        /// <summary>
        /// 命中真实目标/环境的瞬间触发（命中点, 命中方向）。上层（法术触发）据此在命中点再施放载荷，
        /// 保持 Combat 不反依赖 Skills/Character——这里只发一个通用通知。同阵营穿过不算命中、不触发；超时自毁不触发。
        /// </summary>
        public event System.Action<Vector3, Vector3> Impacted;
```

- [ ] **Step 2: 在 `OnCollisionEnter` 命中时广播**（替换整个 `OnCollisionEnter` 方法）

```csharp
        private void OnCollisionEnter(Collision collision)
        {
            if (_consumed) return;

            IDamageable target = collision.collider.GetComponentInParent<IDamageable>();

            // 同阵营（施法者自身/队友）→ 穿过，不结算不销毁、不算命中（不触发）。
            if (target != null && target.TeamId == _attackerTeam)
            {
                if (_collider != null)
                    Physics.IgnoreCollision(_collider, collision.collider);
                if (!FaceVelocityInFlight && _rb != null)
                    _rb.linearVelocity = _launchVelocity; // 抛物线投射物速度时变，不恢复
                return;
            }

            Vector3 hitPoint = collision.GetContact(0).point;
            Vector3 vel = _rb != null ? _rb.linearVelocity : Vector3.zero;
            Vector3 hitDir = vel.sqrMagnitude > 1e-6f ? vel.normalized : transform.forward;
            bool damaged = false;

            // 敌方且存活 → 结算一次伤害
            if (target != null && target.IsAlive)
            {
                var req = new DamageRequest(_attackerId, _attackerTeam, _damage, _type, hitPoint, hitDir);
                target.ReceiveHit(in req);
                damaged = true;
            }

            // 子类扩展点：命中敌方或环境都会走到（撞地也爆炸/插在目标上残留）
            OnImpact(collision, target, hitPoint, damaged);

            // 命中通知：法术触发据此在命中点跑载荷（普通投射物无监听者，空触发无开销）
            Impacted?.Invoke(hitPoint, hitDir);

            _consumed = true;
            Destroy(gameObject, _impactLingerTime);
        }
```

- [ ] **Step 3: (开发者) 编译**

回 Unity 编译。预期：全工程编译无错。（本任务无自动化测试。）

- [ ] **Step 4: 提交**

```bash
git add Assets/_Project/Scripts/Combat/Projectiles/ProjectileBase.cs
git commit -m "feat(combat): add ProjectileBase.Impacted hit-notification event (for spell triggers)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: 命中时跑载荷（Game.Character，递归）

> 💡 **这一步在干嘛**：把"命中时在命中点再运行一次求值器"接起来。`SpellCaster` 生成触发投射物时，订阅它的 `Impacted` 事件；命中时**以预算 1、用载荷的修正快照、从命中点沿命中方向再跑一遍**——这就是递归。载荷里若又是触发，新投射物又会订阅自己的命中事件，自然链下去。把原来 `CastWand` 的生成逻辑抽成一个可被复用/递归调用的 `RunCast`。

**Files:**
- Modify: `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs`

**Interfaces:**
- Consumes: `EmitCommand.HasPayload/Payload/PayloadMods`（Task 1）、`ProjectileBase.Impacted`（Task 2）、`CastEvaluator.Evaluate`、`SpellAiming.SpreadOffsetDegrees`、`ProjectileBase.Init`（已有）。
- Produces: `SpellCaster.CastWand(Vector3 spawnPos, Vector3 aimPoint, byte team, int attackerId, Collider casterCollider)`（签名不变）。

- [ ] **Step 1: 整文件替换 `SpellCaster.cs`**

```csharp
using System.Collections.Generic;
using UnityEngine;
using Game.Core;
using Game.Combat;
using Game.Skills;

namespace Game.Character
{
    /// <summary>
    /// 法术施放器：把"纯求值结果"落地成真实投射物。挂在施法者（法师，未来也可敌人）身上。
    /// CastWand 跑当前法杖；RunCast 是可复用/递归的核心——触发投射物命中时，以预算 1 在命中点再 RunCast 它的载荷（链式触发）。
    /// 递归由有限法杖天然收敛（载荷是更短后缀），无需护栏。纯求值在 Game.Skills；本组件是"数据 → Unity 实例化"的唯一桥。
    /// </summary>
    public class SpellCaster : MonoBehaviour
    {
        [SerializeField] private WandLoadout _wand;
        [Tooltip("可用法力（占位）。资源系统是后续阶段；现给一个大值，求值器不会 fizzle。")]
        [SerializeField] private float _availableMana = 9999f;

        // 求值产出缓冲（预分配复用，求值器内 Clear()）
        private readonly List<EmitCommand> _emits = new List<EmitCommand>(16);
        // 本次施法已播过的音效：去重，多重/连发的同一音效只响一次
        private readonly HashSet<AudioClip> _playedSfx = new HashSet<AudioClip>();

        public WandLoadout Wand => _wand;

        /// <summary>运行当前法杖：朝 aimPoint 从 spawnPos 施放。返回产出数。</summary>
        public int CastWand(Vector3 spawnPos, Vector3 aimPoint, byte team, int attackerId, Collider casterCollider)
        {
            if (_wand == null || _wand.Spells == null || _wand.Spells.Length == 0)
            {
                GameLog.Warn("SpellCaster 未配置 WandLoadout 或法杖为空，无法施放", "Skills");
                return 0;
            }

            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f) baseDir = transform.forward; // 退化兜底
            baseDir.Normalize();

            return RunCast(_wand.Spells, _wand.BaseDraws, CastModifierState.Default,
                           spawnPos, baseDir, team, attackerId, casterCollider);
        }

        /// <summary>
        /// 运行一段法术序列，在 spawnPos 沿 baseDir 生成投射物。返回求值产出数。
        /// 触发投射物订阅命中事件：命中时以"预算 1"在命中点再 RunCast 它的载荷（链式触发递归）。
        /// 命中回调发生在之后的物理帧、不在本循环内重入，故复用 _emits 安全。
        /// </summary>
        private int RunCast(IReadOnlyList<SpellDefinition> spells, int baseDraws, CastModifierState incomingMods,
                            Vector3 spawnPos, Vector3 baseDir, byte team, int attackerId, Collider casterCollider)
        {
            CastEvaluator.Evaluate(spells, baseDraws, _availableMana, incomingMods, _emits);
            _playedSfx.Clear();

            int count = _emits.Count;
            for (int i = 0; i < count; i++)
            {
                EmitCommand cmd = _emits[i];

                // 施放音效：一次施法里同一音效只播一次
                if (cmd.CastSfx != null && _playedSfx.Add(cmd.CastSfx))
                    AudioSource.PlayClipAtPoint(cmd.CastSfx, spawnPos);

                if (cmd.ProjectilePrefab == null)
                {
                    GameLog.Warn("EmitCommand.ProjectilePrefab 为空（法术未配置预制体），跳过该发", "Skills");
                    continue;
                }

                float yaw = SpellAiming.SpreadOffsetDegrees(i, count, cmd.SpreadDegrees);
                Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * baseDir;

                GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, Quaternion.LookRotation(dir));
                ProjectileBase proj = go.GetComponent<ProjectileBase>();
                if (proj == null)
                {
                    GameLog.Warn($"法术预制体 {cmd.ProjectilePrefab.name} 上没有 ProjectileBase 组件", "Skills");
                    Object.Destroy(go);
                    continue;
                }

                // 触发：命中时在命中点以预算 1 跑载荷（载荷里再有触发 → 自然链式；有限后缀 → 自然收敛）
                if (cmd.HasPayload)
                {
                    IReadOnlyList<SpellDefinition> payload = cmd.Payload;
                    CastModifierState payloadMods = cmd.PayloadMods;
                    proj.Impacted += (hitPoint, hitDir) =>
                        RunCast(payload, 1, payloadMods, hitPoint, hitDir, team, attackerId, casterCollider);
                }

                // 直线投射物关重力；命中走标准 ProjectileBase → ReceiveHit
                proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, dir * cmd.Speed, casterCollider, useGravity: false);
            }

            return count;
        }
    }
}
```

- [ ] **Step 2: (开发者) 编译**

回 Unity 编译。预期：全工程编译无错。行为在 Task 4 Play 验证。

- [ ] **Step 3: 提交**

```bash
git add Assets/_Project/Scripts/Character/Spells/SpellCaster.cs
git commit -m "feat(character): trigger projectiles re-cast payload at impact (chained recursion)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Editor 组装 + Play 验证（开发者）

> 💡 **这一步在干嘛**：建一个"触发火球"法术资产（就是火球 + 勾上 IsTrigger），加进调色板，然后在游戏里编出触发法杖、验证"命中处再放法术"和"链式"。

- [ ] **Step 1: 建触发火球法术**：复制 `Spell_Emit_Fireball` 为新资产 `Spell_Emit_TriggerFireball`（或 Create → Game/Skills/Spell Definition）：Kind=Emit；ProjectilePrefab=火球预制体；BaseDamage/BaseSpeed/DamageType/CastSfx/Icon 照旧；**勾选 `Is Trigger`**；DisplayName="触发火球"。
- [ ] **Step 2: 加进调色板**：把它拖进 `SpellLibrary.Available`。
- [ ] **Step 3: Play 验证**（按 Tab 编辑法杖，组合后关面板左键开火）：
  - 法杖 `[触发火球, 火球]` → 飞一颗火球，**命中处再射出一颗火球**。
  - 法杖 `[触发火球, 三重, 火球, 火球, 火球]` → 火球命中处**一次射 3 颗火球**。
  - 法杖 `[触发火球, 触发火球, 火球]` → **三段连锁**：火球①命中 → 火球②；火球②命中 → 火球③。
  - 法杖 `[增伤, 触发火球, 火球]` → 触发火球带增伤，且命中产出的火球**也继承增伤**（载荷继承修正快照）。
  - 法杖 `[触发火球]`（后面空）→ 命中处不再产出（空载荷）。
  - 触发火球飞出去**没命中任何东西（超时消失）→ 不触发**（符合"命中触发"）。
- [ ] **Step 4: 提交**：Unity 生成的 `.meta` + 新法术资产 + SpellLibrary 改动（开发者或让 Claude 代提交）。

---

## 阶段 D 完成后的产物
触发递归上线——法术能在命中处链式触发后续法术，法术系统四阶段（求值内核 / 接运行时 / 拖拽 UI / 触发递归）全部齐备。

## 不在本阶段范围
定时触发（飞行 X 秒后自动触发）、触发特效打磨、UI 上对触发法术的特殊标识、与递归无关的全局活跃投射物上限（除非 Profiler 发现需要）。

## Self-Review
- **Spec 覆盖**：IsTrigger 标记(T1)、载荷=其后后缀+修正快照(T1 CaptureSuffix/BakeEmit)、触发结束本层(T1 ended)、命中通知接缝(T2 Impacted)、命中时预算1+incomingMods 在命中点跑载荷=递归(T3)、链式(载荷里再有触发自然续，T1+T3)、仅命中触发(T2 同阵营/超时不触发)、不设护栏(有限后缀不变量，全程注释)、模块隔离(T2 仅通用事件)。✅
- **占位符扫描**：无 TBD/TODO；每个代码步骤为完整方法/文件。✅
- **类型一致性**：`EmitCommand` 新构造 8 参在 T1 定义、`BakeEmit` 按此调用；`HasPayload/Payload/PayloadMods`、`IsTrigger`、`Impacted(Vector3,Vector3)`、`RunCast(...)`、`CastEvaluator.Evaluate`、`SpellAiming.SpreadOffsetDegrees`、`ProjectileBase.Init` 在定义处与调用处一致。✅
- **编译顺序**：T1(Skills 自洽，含测试)→T2(Combat 自洽)→T3(用 T1 的 EmitCommand 载荷 + T2 的 Impacted)→T4 Editor。逐任务可编译。✅
