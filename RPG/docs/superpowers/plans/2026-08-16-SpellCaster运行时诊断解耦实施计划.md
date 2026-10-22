# SpellCaster 运行时诊断解耦 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. 本项目未授权 SubAgent，禁止使用 `subagent-driven-development`。

**Goal:** 在不删除 P0 Runtime Cast Trace、不改变施法 Gameplay 语义的前提下，把所有条件编译与诊断格式化实现移出 `SpellCaster.cs`，让该文件可以沿着 `CastWand -> RunCast -> EmitCommand -> Spawn` 顺序阅读。

**Architecture:** 新增同属 `Game.Character` 的普通 C# Helper `SpellCastRuntimeDiagnostics`，专门持有开发构建中的 `CastTraceCollector`、条件编译和日志格式化。`SpellCaster` 继续持有 Inspector 中的 `_traceLevel` 并保留 public `TraceLevel` Contract，但只通过少量语义明确的方法调用诊断 Helper；法术求值、Mana、Payload、Prefab 生成和 `ProjectileBase.Init` 不改变。

**Tech Stack:** Unity 6.3、C#、Unity conditional compilation symbols、NUnit EditMode tests、现有 `Game.Character` / `Game.Skills` asmdef。

## Global Constraints

- 保留 `SpellCaster.CastWand(...)`、`CastProgram(...)`、`TryBindCurrentRunSession()`、`Wand`、`TraceLevel` 的 public 签名与行为。
- 保留 Editor/Development Build 的 `Off / Summary / Detailed` Trace；普通 Release Build 的 `TraceLevel` 仍表现为 `Off`。
- `SpellCaster.cs` 最终不得出现 `#if`、`#else`、`#endif`；条件编译只能存在于诊断 Helper。
- 不修改 `CastEvaluator` 的求值语义，不复制第二份法术解释 switch。
- 不改变 Mana 整笔预检、Payload 递归、SpawnMode、投射物初始化和伤害链路。
- 完整保留当前工作树中 `SpellCaster.cs` 已有的学习注释、参数换行和方法重排；不得用 HEAD 版本覆盖工作树。
- 不触碰当前工作树中的 GPU PBF、Scene、Shader、Compute 或其他无关改动。
- 不手工创建 `.meta`；新增 `.cs` 和测试文件的 `.meta` 由 Unity Editor 导入时生成。
- 不使用 `Debug.Log`，诊断输出继续通过 `GameLog`。
- 不创建额外 spec；本计划已经吸收设计、取舍、文件清单、测试和回滚要求。
- 不自动提交当前脏工作树；如以后提交，使用 `refactor(character): extract spell cast diagnostics`。

---

## File Structure

- Create: `Assets/_Project/Scripts/Character/Spells/SpellCastRuntimeDiagnostics.cs`
  - `Game.Character` 内部诊断 Helper；持有所有 `#if UNITY_EDITOR || DEVELOPMENT_BUILD`、Trace Collector 与日志格式化。
- Modify: `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs`
  - 只保留 Gameplay/Runtime Adapter 主流程和对 Helper 的窄调用。
- Create: `Assets/_Project/Tests/Character/SpellCasterTraceLevelTests.cs`
  - Character EditMode characterization test；保护现有 public `TraceLevel` Contract。
- Modify: `Assets/_Project/Docs/2026-07-11 P0 完整解析.md`
  - 把“诊断实现位于 SpellCaster”更新为“SpellCaster 调用独立 Runtime Diagnostics Helper”。
- Reuse without modification: `Assets/_Project/Tests/Skills/CastTraceTests.cs`
  - 继续证明 `Evaluate` 与 `EvaluateWithTrace` 输出等价。
- Reuse without modification: `Assets/_Project/Tests/Character/SpellCasterBossProgramTests.cs`
  - 继续保护 `CastProgram` / Mana Policy 路径。

---

### Task 1: 用 Character EditMode Test 锁定 TraceLevel Contract

**Files:**
- Create: `Assets/_Project/Tests/Character/SpellCasterTraceLevelTests.cs`
- Test: `Assets/_Project/Tests/Character/SpellCasterTraceLevelTests.cs`

**Interfaces:**
- Consumes: `SpellCaster.TraceLevel : CastTraceLevel`
- Produces: Editor 中配置为 `Detailed` 时 public getter 仍返回 `Detailed` 的回归证据。

- [ ] **Step 1: 写当前实现也应通过的 characterization test**

```csharp
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public class SpellCasterTraceLevelTests
    {
        [Test]
        public void TraceLevel_InEditor_ReturnsConfiguredLevel()
        {
            var gameObject = new GameObject("SpellCasterTraceLevelTests");
            try
            {
                SpellCaster caster = gameObject.AddComponent<SpellCaster>();
                FieldInfo field = typeof(SpellCaster).GetField(
                    "_traceLevel",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.That(field, Is.Not.Null);
                field.SetValue(caster, CastTraceLevel.Detailed);

                Assert.That(caster.TraceLevel, Is.EqualTo(CastTraceLevel.Detailed));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
```

- [ ] **Step 2: 在 Unity Test Runner 中运行基线 Character test**

Run: Unity Editor -> `Window > General > Test Runner > EditMode` -> filter `SpellCasterTraceLevelTests`

Expected: `TraceLevel_InEditor_ReturnsConfiguredLevel` PASS。

- [ ] **Step 3: 记录基线而不生成 `.meta`**

确认 Unity 尚未打开时只存在 `.cs`；Unity 导入后由 Editor 自动生成 `.meta`。不要手写 GUID。

---

### Task 2: 提取 SpellCastRuntimeDiagnostics

**Files:**
- Create: `Assets/_Project/Scripts/Character/Spells/SpellCastRuntimeDiagnostics.cs`
- Modify: `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs`

**Interfaces:**
- Consumes: `CastEvaluator.Evaluate(...)`、`CastEvaluator.EvaluateWithTrace(...)`、`CastTraceCollector`、`CastTraceLevel`、`List<EmitCommand>`。
- Produces:
  - `int AllocateCastId()`
  - `CastTraceLevel ResolveLevel(CastTraceLevel configuredLevel)`
  - `CastSummary EvaluateCommands(...)`
  - `void LogManaPreflightFailure(...)`

- [ ] **Step 1: 创建非 MonoBehaviour Helper 骨架**

```csharp
using System.Collections.Generic;
using Game.Core;
using Game.Skills;

namespace Game.Character
{
    /// <summary>
    /// SpellCaster 的开发诊断协作者：集中条件编译、Trace 收集和日志格式化。
    /// Gameplay 主流程只调用窄接口，不需要理解 Editor/Development Build 分支。
    /// </summary>
    internal sealed class SpellCastRuntimeDiagnostics
    {
        private static int s_nextCastId;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private readonly CastTraceCollector _traceCollector = new CastTraceCollector(64);
#endif

        internal int AllocateCastId()
        {
            return ++s_nextCastId;
        }

        internal CastTraceLevel ResolveLevel(CastTraceLevel configuredLevel)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return configuredLevel;
#else
            return CastTraceLevel.Off;
#endif
        }
    }
}
```

- [ ] **Step 2: 把正式求值/Trace 分支迁入 `EvaluateCommands`**

```csharp
internal CastSummary EvaluateCommands(
    IReadOnlyList<SpellDefinition> spells,
    int baseDraws,
    CastModifierState incomingMods,
    List<EmitCommand> output,
    CastTraceLevel configuredLevel,
    int castId,
    int depth,
    float requiredMana)
{
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    CastSummary summary = configuredLevel == CastTraceLevel.Detailed
        ? CastEvaluator.EvaluateWithTrace(
            spells, baseDraws, float.PositiveInfinity,
            incomingMods, output, _traceCollector)
        : CastEvaluator.Evaluate(
            spells, baseDraws, float.PositiveInfinity,
            incomingMods, output);

    LogTrace(configuredLevel, castId, depth, requiredMana, summary, output);
    return summary;
#else
    return CastEvaluator.Evaluate(
        spells, baseDraws, float.PositiveInfinity,
        incomingMods, output);
#endif
}
```

要求：`EvaluateWithTrace` 与 `Evaluate` 仍共用 `CastEvaluator.EvaluateCore`；Helper 只选择入口，不实现法术规则。

- [ ] **Step 3: 迁移 Mana 失败诊断**

把当前 `SpellCaster.LogManaPreflightFailure(...)` 的方法体原样迁移到 Helper，并增加以下参数：

```csharp
internal void LogManaPreflightFailure(
    IReadOnlyList<SpellDefinition> spells,
    int baseDraws,
    CastModifierState incomingMods,
    List<EmitCommand> output,
    CastTraceLevel configuredLevel,
    int castId,
    int depth,
    float requiredMana,
    float availableMana)
```

必须维持以下不变量：

- Release Build 方法为 no-op。
- `Off` 不求值、不记录。
- `Summary` 只打印失败汇总。
- `Detailed` 使用真实 `availableMana` 执行一次无副作用的 `EvaluateWithTrace`。
- Detailed 失败诊断后必须 `output.Clear()`，不能让临时 Emit 进入 Spawn 阶段。
- 该方法不能调用 `ManaComponent.Spend`、`Instantiate` 或任何伤害入口。

- [ ] **Step 4: 迁移日志格式化实现**

把以下现有方法从 `SpellCaster` 移入 Helper，保持 switch、字段和消息内容不变：

```text
LogTrace
LogTraceStep
FormatModifiers
FormatEmit
```

`LogTrace` 的新签名显式接收外部状态，避免 Helper 反向持有 `SpellCaster`：

```csharp
private void LogTrace(
    CastTraceLevel configuredLevel,
    int castId,
    int depth,
    float requiredMana,
    CastSummary summary,
    List<EmitCommand> output)
```

日志中的 Emit 数量读取 `output.Count`；逐步日志继续读取 Helper 自己持有的 `_traceCollector`。

- [ ] **Step 5: 静态检查 Helper 的 Release 边界**

Run:

```powershell
Select-String -LiteralPath 'Assets/_Project/Scripts/Character/Spells/SpellCastRuntimeDiagnostics.cs' -Pattern '#if UNITY_EDITOR \|\| DEVELOPMENT_BUILD','#else','#endif'
```

Expected: 条件编译只围绕 Collector、有效等级、诊断求值和日志实现；Gameplay Spawn/Mana 扣费代码不存在于 Helper。

---

### Task 3: 把 SpellCaster 主流程压缩为窄诊断调用

**Files:**
- Modify: `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs`
- Test: `Assets/_Project/Tests/Character/SpellCasterTraceLevelTests.cs`
- Test: `Assets/_Project/Tests/Character/SpellCasterBossProgramTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `SpellCastRuntimeDiagnostics` 四个方法。
- Produces: 不包含 Preprocessor Directive、但 Gameplay 与 public Contract 不变的 `SpellCaster`。

- [ ] **Step 1: 替换诊断字段和 TraceLevel getter**

保留：

```csharp
[SerializeField] private CastTraceLevel _traceLevel = CastTraceLevel.Off;
```

移除 `SpellCaster` 自己的 `s_nextCastId` 和 `_traceCollector`，新增：

```csharp
private readonly SpellCastRuntimeDiagnostics _diagnostics =
    new SpellCastRuntimeDiagnostics();
```

将 getter 改为：

```csharp
public CastTraceLevel TraceLevel => _diagnostics.ResolveLevel(_traceLevel);
```

- [ ] **Step 2: 统一 castId 分配**

`CastWand` 与 `CastProgram` 都改为：

```csharp
int castId = _diagnostics.AllocateCastId();
```

不得改变传给 `RunCast` 的 `depth = 0`、Mana Policy 或其他参数。

- [ ] **Step 3: 压缩 Mana 失败路径**

把 `RunCast` 内部的条件编译块替换成：

```csharp
if (!TrySpendMana(requiredMana, manaPolicy))
{
    _diagnostics.LogManaPreflightFailure(
        spells,
        baseDraws,
        incomingMods,
        _emits,
        _traceLevel,
        castId,
        depth,
        requiredMana,
        availableMana);
    _emits.Clear();
    return 0;
}
```

主流程仍显式 `_emits.Clear()`，即使 Release Helper 为 no-op 也不会消费上次求值结果。

- [ ] **Step 4: 把求值分支压缩为一次调用**

```csharp
_diagnostics.EvaluateCommands(
    spells,
    baseDraws,
    incomingMods,
    _emits,
    _traceLevel,
    castId,
    depth,
    requiredMana);
```

调用后原有 `_playedSfx.Clear()`、`int count = _emits.Count`、ProfilerMarker、SpawnMode switch 和 Spawn 方法保持原顺序。

- [ ] **Step 5: 删除 SpellCaster 中已经迁移的诊断实现**

删除：

```text
所有 #if / #else / #endif
LogManaPreflightFailure
LogTrace
LogTraceStep
FormatModifiers
FormatEmit
```

不得删除：

```text
MaxRuntimeTriggerDepth
s_runtimeSpawnMarker
_emits
_playedSfx
_landingHits
TrySpendMana
WirePayload
ConfigureProjectileMotion
SpawnForwardProjectile / Static / Skyfall
```

- [ ] **Step 6: 检查主文件阅读顺序**

Run:

```powershell
$path = 'Assets/_Project/Scripts/Character/Spells/SpellCaster.cs'
Select-String -LiteralPath $path -Pattern '^\s*#(if|else|endif)'
```

Expected: 无输出。

再人工确认 `RunCast` 可连续阅读为：

```text
Depth Guard
-> EstimateManaCost
-> TrySpendMana
-> EvaluateCommands
-> Clear SFX scope
-> foreach EmitCommand
-> SpawnMode dispatch
```

- [ ] **Step 7: 运行 Character EditMode tests**

Run: Unity Test Runner EditMode，filter：

```text
SpellCasterTraceLevelTests
SpellCasterBossProgramTests
```

Expected: 全部 PASS。

---

### Task 4: 回归 Trace 等价性、文档与编译边界

**Files:**
- Modify: `Assets/_Project/Docs/2026-07-11 P0 完整解析.md`
- Test: `Assets/_Project/Tests/Skills/CastTraceTests.cs`
- Verify: `RPG.sln`

**Interfaces:**
- Consumes: Task 2/3 完成后的 Helper 与 SpellCaster。
- Produces: 当前源码位置准确的 P0 学习说明和分层验证证据。

- [ ] **Step 1: 更新 P0 文档中的源码位置说明**

文档必须明确：

```text
SpellCaster：只决定何时调用诊断、何时进入 Gameplay Spawn。
SpellCastRuntimeDiagnostics：持有条件编译、Collector、Summary/Detailed 日志格式化。
CastEvaluator：Evaluate 与 EvaluateWithTrace 继续共用 EvaluateCore。
```

保留 P0 对 `TraceLevel = Off`、Profiler 采样前关闭 Detailed Trace 和证据分级的说明。

- [ ] **Step 2: 运行 Skills Trace tests**

Run: Unity Test Runner EditMode，filter `CastTraceTests`

Expected: 全部 PASS，尤其是：

```text
EvaluateWithTrace_SingleEmit_RecordsStartEmitAndComplete
EvaluateAndEvaluateWithTrace_ProduceEquivalentCommands
```

- [ ] **Step 3: 执行静态编译检查**

Run:

```powershell
dotnet build 'RPG.sln' --no-restore
```

Expected: Exit code 0。若 Unity 生成的 `.csproj` 或本机 Unity reference 导致环境性失败，记录原始错误，不把它误报成 Unity Editor 编译结果。

- [ ] **Step 4: 检查空白、引用与范围**

Run:

```powershell
git diff --check
git grep -n -E '^\s*#(if|else|endif)' -- 'Assets/_Project/Scripts/Character/Spells/SpellCaster.cs'
git grep -n 'TraceLevel' -- 'Assets/_Project/Scripts/Character/**/*.cs'
git status --short
```

Expected:

- `git diff --check` 无新增空白错误。
- `SpellCaster.cs` 不再含条件编译。
- `SpellPerformanceLabController` 仍能读取 public `TraceLevel`。
- `git status` 中所有原有 GPU PBF / Scene 等无关文件保持未触碰。

- [ ] **Step 5: Unity Editor 手动验证**

1. 打开包含玩家 `SpellCaster` 的测试场景。
2. `Trace Level = Off`：普通法术正常生成，不出现 `[SpellTrace]`。
3. `Trace Level = Summary`：每层出现一次汇总，不改变 Emit 数量。
4. `Trace Level = Detailed`：出现逐指令 Trace，普通法术仍生成相同投射物。
5. Mana 不足时：不生成投射物；Detailed 只记录失败，不遗留临时 Emit。
6. 打开 Performance Lab：Trace 非 Off 时仍出现“会污染性能数据”的警告。
7. 进入一次普通施法与一次 Trigger Payload，确认 Payload 仍在触发点展开。

证据边界：上述步骤由开发者在 Unity Editor 实际完成后，才能声称 Scene / PlayMode 行为通过。

- [ ] **Step 6: 回滚与 Debug 路径**

如果编译失败，按以下顺序定位：

```text
缺类型/方法
-> 检查 Helper namespace 是否为 Game.Character
-> 检查方法签名与 SpellCaster 调用参数顺序
-> 检查 using Game.Core / Game.Skills / System.Collections.Generic

Trace 有输出但不生成投射物
-> 检查 Detailed 失败诊断后 output.Clear
-> 检查正常 EvaluateCommands 后没有误 Clear

Release 仍返回 Detailed
-> 检查 ResolveLevel 的 #if/#else 是否全部位于 Helper

Performance Lab 编译失败
-> 检查 SpellCaster public TraceLevel 是否保留
```

不要使用 `git checkout -- SpellCaster.cs` 或 `git reset --hard` 回滚，因为这会丢失用户当前未提交的学习注释与方法重排。只对本次新增 Helper 和精确修改 hunk 做反向补丁。

