# Bounce Modifier Spell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a bounce modifier spell that lets later emitted projectiles bounce off environment collisions a configured number of times.

**Architecture:** Bounce is modeled as a `Modify` spell, not as a special `BouncyFireball` prefab. The value flows through `SpellDefinition -> CastModifierState -> CastEvaluator -> EmitCommand -> SpellCaster -> ProjectileBase`, preserving the existing spell interpreter architecture and Combat module boundaries.

**Tech Stack:** Unity 6.3, C#, Unity Physics, `Rigidbody.linearVelocity`, `Vector3.Reflect`, ScriptableObject-driven spell data, NUnit EditMode tests.

## Global Constraints

- Answer and documentation should remain in Simplified Chinese for the user.
- Use Unity Input System; do not use legacy `Input`.
- Keep `Game.Skills` pure; no Unity scene object instantiation in `CastEvaluator`.
- Do not make `Game.Combat` depend on `Game.Skills` or `Game.Character`.
- Use `GameLog`, not `Debug.Log`.
- Do not create `.meta` files manually.
- Do not claim Unity Editor compile or Play Mode verification has passed unless the user verifies it.
- This plan edits local project files directly; do not create a git worktree.

---

## File Structure

Files to modify:

| File | Responsibility |
|---|---|
| `Assets/_Project/Scripts/Skills/SpellDefinition.cs` | Add `ModBounceAdd` field for `Modify` spell assets |
| `Assets/_Project/Scripts/Skills/CastModifierState.cs` | Carry accumulated bounce count through pure evaluation |
| `Assets/_Project/Scripts/Skills/EmitCommand.cs` | Carry final bounce count from evaluator to runtime bridge |
| `Assets/_Project/Scripts/Skills/CastEvaluator.cs` | Bake `mods.BounceCount` into `EmitCommand` |
| `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs` | Configure each instantiated projectile with `cmd.BounceCount` |
| `Assets/_Project/Scripts/Combat/Projectiles/ProjectileBase.cs` | Store runtime bounce count and reflect environment collisions |
| `Assets/_Project/Tests/Skills/CastModifierStateTests.cs` | Add pure modifier tests |
| `Assets/_Project/Tests/Skills/CastEvaluatorTests.cs` | Add evaluator output and payload inheritance tests |

No new runtime scripts are required for the MVP.

---

### Task 1: Add Bounce To Pure Modifier State

**Files:**
- Modify: `Assets/_Project/Scripts/Skills/SpellDefinition.cs`
- Modify: `Assets/_Project/Scripts/Skills/CastModifierState.cs`
- Test: `Assets/_Project/Tests/Skills/CastModifierStateTests.cs`

**Interfaces:**
- Consumes: `SpellDefinition.ModBounceAdd : int`
- Produces: `CastModifierState.BounceCount : int`
- Produces: `CastModifierState.Default` with `BounceCount == 0`
- Produces: `CastModifierState.Apply(SpellDefinition modify)` accumulating bounce additively

- [ ] **Step 1: Inspect current tests**

Run:

```powershell
Get-Content -Path 'Assets/_Project/Tests/Skills/CastModifierStateTests.cs' -Raw
```

Expected: existing tests cover damage/speed/spread defaults or modifier application.

- [ ] **Step 2: Write failing tests for bounce default and accumulation**

Add tests equivalent to:

```csharp
using NUnit.Framework;
using UnityEngine;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class CastModifierStateTests
    {
        [Test]
        public void Default_HasZeroBounceCount()
        {
            CastModifierState state = CastModifierState.Default;

            Assert.AreEqual(0, state.BounceCount);
        }

        [Test]
        public void Apply_AddsBounceCount()
        {
            var bounce = ScriptableObject.CreateInstance<SpellDefinition>();
            bounce.Kind = SpellKind.Modify;
            bounce.ModBounceAdd = 2;

            CastModifierState state = CastModifierState.Default.Apply(bounce);

            Assert.AreEqual(2, state.BounceCount);
        }

        [Test]
        public void Apply_StacksBounceCountAdditively()
        {
            var first = ScriptableObject.CreateInstance<SpellDefinition>();
            first.Kind = SpellKind.Modify;
            first.ModBounceAdd = 1;

            var second = ScriptableObject.CreateInstance<SpellDefinition>();
            second.Kind = SpellKind.Modify;
            second.ModBounceAdd = 2;

            CastModifierState state = CastModifierState.Default.Apply(first).Apply(second);

            Assert.AreEqual(3, state.BounceCount);
        }
    }
}
```

If the file already contains a `CastModifierStateTests` class, add only the new test methods, not a duplicate class.

- [ ] **Step 3: Run EditMode tests and confirm failure**

Run through Unity Test Runner EditMode, or via the project’s existing Unity test command if configured.

Expected before implementation:

```text
CS1061: 'CastModifierState' does not contain a definition for 'BounceCount'
CS1061: 'SpellDefinition' does not contain a definition for 'ModBounceAdd'
```

- [ ] **Step 4: Add `ModBounceAdd` to `SpellDefinition`**

In `SpellDefinition.cs`, under the `Modify` section, add:

```csharp
[Min(0)]
[Tooltip("给后续投射物增加的环境弹跳次数。仅 Kind=Modify 时使用。")]
public int ModBounceAdd = 0;
```

Keep default `0` so existing modifier spell assets remain behaviorally unchanged.

- [ ] **Step 5: Add `BounceCount` to `CastModifierState`**

Update constructor and fields:

```csharp
public readonly int BounceCount;

public CastModifierState(float damageAddFlat, float damageMul, float speedMul, float spreadDegrees, int bounceCount)
{
    DamageAddFlat = damageAddFlat;
    DamageMul = damageMul;
    SpeedMul = speedMul;
    SpreadDegrees = spreadDegrees;
    BounceCount = bounceCount;
}
```

Update default:

```csharp
public static CastModifierState Default => new CastModifierState(0f, 1f, 1f, 0f, 0);
```

Update `Apply`:

```csharp
public CastModifierState Apply(SpellDefinition modify)
{
    return new CastModifierState(
        DamageAddFlat + modify.ModDamageAddFlat,
        DamageMul * modify.ModDamageMul,
        SpeedMul * modify.ModSpeedMul,
        SpreadDegrees + modify.ModSpreadAddDegrees,
        BounceCount + modify.ModBounceAdd);
}
```

- [ ] **Step 6: Run tests and confirm Task 1 passes**

Run the `CastModifierStateTests` EditMode tests.

Expected:

```text
Default_HasZeroBounceCount PASS
Apply_AddsBounceCount PASS
Apply_StacksBounceCountAdditively PASS
```

- [ ] **Step 7: Commit Task 1**

```powershell
git add Assets/_Project/Scripts/Skills/SpellDefinition.cs Assets/_Project/Scripts/Skills/CastModifierState.cs Assets/_Project/Tests/Skills/CastModifierStateTests.cs
git commit -m "feat(skills): add bounce modifier state"
```

If unrelated user changes exist in any of these files, inspect diff carefully and stage only the bounce-related hunks.

---

### Task 2: Bake Bounce Into EmitCommand

**Files:**
- Modify: `Assets/_Project/Scripts/Skills/EmitCommand.cs`
- Modify: `Assets/_Project/Scripts/Skills/CastEvaluator.cs`
- Test: `Assets/_Project/Tests/Skills/CastEvaluatorTests.cs`

**Interfaces:**
- Consumes: `CastModifierState.BounceCount : int`
- Produces: `EmitCommand.BounceCount : int`
- Preserves: `EmitCommand.PayloadMods.BounceCount` for trigger/timed payload recursion

- [ ] **Step 1: Add failing evaluator tests**

Add tests equivalent to:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Game.Combat;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class CastEvaluatorTests
    {
        [Test]
        public void Evaluate_BounceModifier_BakesBounceIntoLaterEmit()
        {
            var bounce = ScriptableObject.CreateInstance<SpellDefinition>();
            bounce.Kind = SpellKind.Modify;
            bounce.ModBounceAdd = 2;

            var fireball = ScriptableObject.CreateInstance<SpellDefinition>();
            fireball.Kind = SpellKind.Emit;
            fireball.BaseDamage = 10f;
            fireball.BaseSpeed = 20f;
            fireball.DamageType = DamageType.Magical;

            var output = new List<EmitCommand>();

            CastEvaluator.Evaluate(new[] { bounce, fireball }, 1, 999f, CastModifierState.Default, output);

            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(2, output[0].BounceCount);
        }

        [Test]
        public void Evaluate_TriggerPayload_PreservesBounceInPayloadMods()
        {
            var bounce = ScriptableObject.CreateInstance<SpellDefinition>();
            bounce.Kind = SpellKind.Modify;
            bounce.ModBounceAdd = 1;

            var trigger = ScriptableObject.CreateInstance<SpellDefinition>();
            trigger.Kind = SpellKind.Emit;
            trigger.PayloadTrigger = PayloadTriggerMode.OnImpact;
            trigger.BaseDamage = 10f;
            trigger.BaseSpeed = 20f;

            var payloadEmit = ScriptableObject.CreateInstance<SpellDefinition>();
            payloadEmit.Kind = SpellKind.Emit;
            payloadEmit.BaseDamage = 5f;
            payloadEmit.BaseSpeed = 10f;

            var output = new List<EmitCommand>();

            CastEvaluator.Evaluate(new[] { bounce, trigger, payloadEmit }, 1, 999f, CastModifierState.Default, output);

            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(1, output[0].BounceCount);
            Assert.AreEqual(1, output[0].PayloadMods.BounceCount);
        }
    }
}
```

If `CastEvaluatorTests.cs` already contains a class, add only these methods.

- [ ] **Step 2: Run tests and confirm failure**

Expected before implementation:

```text
CS1061: 'EmitCommand' does not contain a definition for 'BounceCount'
```

- [ ] **Step 3: Extend `EmitCommand`**

In `EmitCommand.cs`, add field:

```csharp
public readonly int BounceCount;
```

Update constructor signature:

```csharp
public EmitCommand(GameObject projectilePrefab, float damage, float speed, DamageType damageType,
                   float spreadDegrees, int bounceCount, AudioClip castSfx,
                   IReadOnlyList<SpellDefinition> payload, CastModifierState payloadMods,
                   PayloadTriggerMode payloadTrigger, float payloadDelaySeconds)
```

Assign:

```csharp
BounceCount = bounceCount;
```

- [ ] **Step 4: Update `CastEvaluator.BakeEmit`**

Change the `new EmitCommand(...)` call to include `mods.BounceCount` after `mods.SpreadDegrees`:

```csharp
return new EmitCommand(spell.ProjectilePrefab, damage, speed, spell.DamageType,
                       mods.SpreadDegrees, mods.BounceCount, spell.CastSfx,
                       payload, mods, trigger, delay);
```

- [ ] **Step 5: Run evaluator tests**

Expected:

```text
Evaluate_BounceModifier_BakesBounceIntoLaterEmit PASS
Evaluate_TriggerPayload_PreservesBounceInPayloadMods PASS
```

- [ ] **Step 6: Commit Task 2**

```powershell
git add Assets/_Project/Scripts/Skills/EmitCommand.cs Assets/_Project/Scripts/Skills/CastEvaluator.cs Assets/_Project/Tests/Skills/CastEvaluatorTests.cs
git commit -m "feat(skills): bake bounce count into emit commands"
```

If unrelated user changes exist in these files, stage only bounce-related hunks.

---

### Task 3: Bridge Bounce From SpellCaster To ProjectileBase

**Files:**
- Modify: `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs`
- Modify: `Assets/_Project/Scripts/Combat/Projectiles/ProjectileBase.cs`

**Interfaces:**
- Consumes: `EmitCommand.BounceCount : int`
- Produces: `ProjectileBase.ConfigureBounce(int bounceCount) : void`

- [ ] **Step 1: Add runtime configuration API to `ProjectileBase`**

In `ProjectileBase.cs`, add field:

```csharp
private int _bounceRemaining;
private float _currentSpeed;
```

Add public method:

```csharp
public void ConfigureBounce(int bounceCount)
{
    _bounceRemaining = Mathf.Max(0, bounceCount);
}
```

This method belongs in `Game.Combat` but does not reference `Game.Skills`.

- [ ] **Step 2: Store current speed in `Init`**

In `ProjectileBase.Init(...)`, after `_launchVelocity = velocity;`, add:

```csharp
_currentSpeed = velocity.magnitude;
```

This preserves projectile speed after reflection.

- [ ] **Step 3: Call configuration from `SpellCaster`**

In `SpellCaster.cs`, after `proj` is validated and before `proj.Init(...)`, add:

```csharp
proj.ConfigureBounce(cmd.BounceCount);
```

Recommended location:

```csharp
if (cmd.HasPayload)
{
    ...
}

proj.ConfigureBounce(cmd.BounceCount);
proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, dir * cmd.Speed, casterCollider, useGravity: false);
```

- [ ] **Step 4: Static compile check**

Use Unity compilation if available. If not, run a repository search:

```powershell
rg "new EmitCommand|ConfigureBounce|BounceCount" Assets/_Project/Scripts Assets/_Project/Tests
```

Expected:

```text
EmitCommand constructor calls include BounceCount.
SpellCaster calls ConfigureBounce.
ProjectileBase defines ConfigureBounce.
```

- [ ] **Step 5: Commit Task 3**

```powershell
git add Assets/_Project/Scripts/Character/Spells/SpellCaster.cs Assets/_Project/Scripts/Combat/Projectiles/ProjectileBase.cs
git commit -m "feat(character): pass bounce config to projectiles"
```

If unrelated user changes exist in `SpellCaster.cs`, stage only the `ConfigureBounce` hunk.

---

### Task 4: Implement Environment Bounce In ProjectileBase

**Files:**
- Modify: `Assets/_Project/Scripts/Combat/Projectiles/ProjectileBase.cs`

**Interfaces:**
- Consumes: `_bounceRemaining : int`
- Consumes: `_currentSpeed : float`
- Produces: environment collision reflection before `OnImpact` / `Impacted` / `Destroy`

- [ ] **Step 1: Add private helper `TryBounce`**

In `ProjectileBase.cs`, add:

```csharp
private bool TryBounce(Collision collision, IDamageable target)
{
    if (target != null || _bounceRemaining <= 0 || _rb == null)
        return false;

    if (collision.contactCount <= 0)
        return false;

    Vector3 velocity = _rb.linearVelocity;
    Vector3 incoming = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : transform.forward;
    Vector3 normal = collision.GetContact(0).normal;
    Vector3 reflected = Vector3.Reflect(incoming, normal);

    if (reflected.sqrMagnitude <= 1e-6f)
        return false;

    reflected.Normalize();
    float speed = _currentSpeed > 1e-6f ? _currentSpeed : velocity.magnitude;
    if (speed <= 1e-6f)
        speed = _launchVelocity.magnitude;

    _bounceRemaining--;
    Vector3 newVelocity = reflected * speed;
    _rb.linearVelocity = newVelocity;
    _launchVelocity = newVelocity;

    transform.rotation = Quaternion.LookRotation(reflected) * Quaternion.Euler(_modelForwardOffsetEuler);
    return true;
}
```

Design notes:

- `target != null` means enemies are terminal hits, not bounce surfaces.
- `_launchVelocity` is updated so same-team pass-through recovery uses the reflected direction after bouncing.
- Returning `true` means the collision was consumed by bounce and must not trigger impact/payload/destroy.

- [ ] **Step 2: Insert bounce branch in `OnCollisionEnter`**

After the same-team pass-through block and before hit point/damage/impact logic, add:

```csharp
if (TryBounce(collision, target))
    return;
```

The local structure should become:

```csharp
IDamageable target = collision.collider.GetComponentInParent<IDamageable>();

if (target != null && target.TeamId == _attackerTeam)
{
    ...
    return;
}

if (TryBounce(collision, target))
    return;

Vector3 hitPoint = collision.GetContact(0).point;
...
```

- [ ] **Step 3: Review payload semantics**

Verify by reading the code that the bounce branch returns before:

```csharp
OnImpact(collision, target, hitPoint, damaged);
_consumed = true;
Impacted?.Invoke(hitPoint, hitDir);
Destroy(gameObject, _impactLingerTime);
```

Expected:

```text
Environment bounce does not trigger OnImpact.
Environment bounce does not trigger Impacted payload.
Environment bounce does not destroy projectile.
```

- [ ] **Step 4: Review timed trigger semantics**

Verify `TickTimedTrigger()` remains in `Update()` and still runs while projectile is bouncing.

Expected:

```text
AfterDelay payload can still trigger while the projectile is bouncing.
```

- [ ] **Step 5: Commit Task 4**

```powershell
git add Assets/_Project/Scripts/Combat/Projectiles/ProjectileBase.cs
git commit -m "feat(combat): bounce projectiles off environment"
```

---

### Task 5: Unity Editor Asset Setup And Manual Verification

**Files:**
- Create in Unity Editor: `Spell_Mod_Bounce.asset` under the project’s preferred spell ScriptableObject folder
- Modify in Unity Editor: `SpellLibrary.asset`
- Modify in Unity Editor: `WandLoadout.asset`

**Interfaces:**
- Consumes: `SpellDefinition.ModBounceAdd`
- Produces: a usable bounce modifier spell visible in wand editor

- [ ] **Step 1: Create bounce modifier spell asset**

In Unity Editor:

1. Create a new `SpellDefinition`.
2. Name it `Spell_Mod_Bounce`.
3. Set `Kind = Modify`.
4. Set `DisplayName = 弹跳`.
5. Assign an icon.
6. Set `ModBounceAdd = 1`.
7. Leave projectile fields empty; this spell does not emit a projectile.

- [ ] **Step 2: Add spell to library**

In `SpellLibrary.asset`, add `Spell_Mod_Bounce` to `Available`.

Expected:

```text
The bounce spell appears in the wand editor palette.
```

- [ ] **Step 3: Test ordinary bounce**

Configure:

```text
[弹跳] [普通火球]
```

Play Mode expected behavior:

```text
Fireball bounces once off wall/floor.
After the bounce is consumed, the next environment collision destroys/impacts normally.
Enemy hit still damages and destroys immediately.
```

- [ ] **Step 4: Test stacked bounce**

Configure:

```text
[弹跳] [弹跳] [普通火球]
```

Expected:

```text
Fireball bounces twice before returning to normal impact behavior.
```

- [ ] **Step 5: Test impact trigger interaction**

Configure:

```text
[弹跳] [触发火球] [普通火球]
```

Expected:

```text
Trigger fireball bounces off environment without releasing payload.
When it finally hits enemy or hits environment after bounce count reaches 0, payload releases from that final impact point.
```

- [ ] **Step 6: Test timed trigger interaction**

Configure:

```text
[弹跳] [定时火球] [普通火球]
```

Expected:

```text
Timed fireball may bounce while alive.
If it survives until PayloadDelaySeconds, it disappears and releases payload at its current position and direction.
```

- [ ] **Step 7: Check common failure symptoms**

If fireball never bounces:

```text
Check ModBounceAdd > 0.
Check bounce spell is before the emit spell.
Check SpellDefinition.Kind = Modify.
Check SpellCaster calls ConfigureBounce.
```

If payload triggers on every bounce:

```text
Check TryBounce returns before OnImpact and Impacted.
```

If direction changes but speed dies:

```text
Check _currentSpeed is set from Init velocity magnitude.
Check Rigidbody linear damping on prefab.
```

- [ ] **Step 8: Commit Editor asset changes if desired**

After Unity generates/updates assets and `.meta` files:

```powershell
git add Assets/_Project/ScriptableObjects Assets/_Project/Art/UI/Skill_Icon
git commit -m "feat(skills): add bounce modifier spell asset"
```

Only stage files created or modified for bounce. Do not stage unrelated ScriptableObject deletions or unrelated prefab changes.

---

### Task 6: Update Learning Documentation

**Files:**
- Create: `Assets/_Project/Docs/弹跳类修正法术_设计解析_2026-07-04.md`

**Interfaces:**
- Consumes: final implementation details from Tasks 1-5
- Produces: Chinese learning document explaining architecture, trade-offs, debugging, and interview Q&A

- [ ] **Step 1: Create design explanation doc**

Write a Chinese document covering:

```text
1. 玩家效果与需求
2. 为什么弹跳是 Modify 而不是 Emit
3. 方案 A/B/C 对比
4. 最终方案的数据流
5. 每层脚本职责
6. payload 语义
7. Debug 方法
8. 面试 Q&A
9. 面试者完整讲解
```

- [ ] **Step 2: Verify doc has no placeholders**

Run:

```powershell
Select-String -Path 'Assets/_Project/Docs/弹跳类修正法术_设计解析_2026-07-04.md' -Pattern 'TODO|TBD|待补|占位|xxx|XXX'
```

Expected:

```text
No output.
```

- [ ] **Step 3: Commit documentation**

```powershell
git add Assets/_Project/Docs/弹跳类修正法术_设计解析_2026-07-04.md
git commit -m "docs(skills): explain bounce modifier spell design"
```

---

## Self-Review

Spec coverage:

- Requirement: Bounce as `Modify` spell -> Task 1, Task 2, Task 5.
- Requirement: Flow through spell interpreter architecture -> Task 1, Task 2, Task 3.
- Requirement: Runtime projectile reflection -> Task 3, Task 4.
- Requirement: Payload semantics preserved -> Task 4, Task 5.
- Requirement: EditMode tests for pure logic -> Task 1, Task 2.
- Requirement: Editor setup checklist -> Task 5.
- Requirement: Chinese learning doc -> Task 6.

Placeholder scan:

- No `TODO`, `TBD`, or unspecified implementation steps are intentionally left.

Type consistency:

- `SpellDefinition.ModBounceAdd : int`
- `CastModifierState.BounceCount : int`
- `EmitCommand.BounceCount : int`
- `ProjectileBase.ConfigureBounce(int bounceCount) : void`
- `SpellCaster` consumes `cmd.BounceCount`

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-04-bounce-modifier-spell.md`. Two execution options:

1. **Subagent-Driven (recommended)** - Dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
