# M3 实时战斗系统 —— 核心闭环框架 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `Game.Combat` asmdef 内实现"打到 → 扣血 → 死亡"核心闭环：IDamageable 契约、HealthComponent、纯函数 DamagePipeline、近战 OverlapBox 命中判定，其余系统只预留接口。

**Architecture:** 命中判定（`MeleeHitDetector`，激活窗口内每帧 `OverlapBoxNonAlloc`）过滤自身/同阵营/已死/已命中后构造按值快照的 `DamageRequest` → 调 `IDamageable.ReceiveHit` → 目标 `HealthComponent` 用自身防御档案调纯函数 `DamagePipeline.Resolve` → 扣血 → **同帧** 经 `EventBus<T>` 派发 `DamageReceivedEvent` / `DeathEvent` → 表现层订阅消费。逻辑层零表现引用，伤害计算可单测。

**Tech Stack:** Unity 6.3 / C# / `Game.Core.EventBus<T>` / `Game.Core.GameLog` / Unity Test Framework (NUnit, EditMode) / Physics.OverlapBoxNonAlloc。

---

## ⚠️ 执行环境约束（务必先读）

- **编译与测试由开发者在 Unity 编辑器手动执行**（见 CLAUDE.md "Claude's Scope"）。Claude/执行 agent 只产出 `.cs` 与文本配置；所有 "Run test" 步骤是**开发者动作**，在 Unity 中完成：`Window > General > Test Runner > EditMode > Run All`。
- **场景与 `.asset`、`.unity` 文件由开发者手动创建**（Task 10）。Claude 不生成二进制/YAML 资产。
- 所有热路径（`Update` / `DoOverlap`）禁止 `new` / LINQ / 装箱；事件全部 struct；`Publish` 同步、命中当帧派发。
- `Game.Combat.asmdef` 已存在且**仅引用 Core**（GUID `510f…`）。本计划不修改它，禁止加入对 Character / Skills 的引用。

---

## File Structure

新增文件（全部 `namespace Game.Combat`，除测试为 `Game.Combat.Tests`）：

| 路径 | 职责 |
|------|------|
| `Assets/_Project/Scripts/Combat/DamageType.cs` | 伤害类型枚举 |
| `Assets/_Project/Scripts/Combat/DamageRequest.cs` | 攻击意图（按值快照）只读 struct |
| `Assets/_Project/Scripts/Combat/DamageResult.cs` | 结算结果只读 struct |
| `Assets/_Project/Scripts/Combat/DefenseProfile.cs` | 防御档案 struct（字段预留） |
| `Assets/_Project/Scripts/Combat/DamagePipeline.cs` | 纯函数伤害计算 |
| `Assets/_Project/Scripts/Combat/IDamageable.cs` | 可受伤害契约 |
| `Assets/_Project/Scripts/Combat/Events/DamageReceivedEvent.cs` | 受击事件 |
| `Assets/_Project/Scripts/Combat/Events/DeathEvent.cs` | 死亡事件 |
| `Assets/_Project/Scripts/Combat/HealthComponent.cs` | 生命值 + IDamageable 实现 |
| `Assets/_Project/Scripts/Combat/AttackDefinition.cs` | 数据驱动攻击定义 SO |
| `Assets/_Project/Scripts/Combat/MeleeHitDetector.cs` | 近战命中判定 |
| `Assets/_Project/Scripts/Combat/CombatDamage.cs` | 【预留缝】Skills 直接结算入口 |
| `Assets/_Project/Scripts/Combat/_Debug/CombatDebugLogger.cs` | 【临时】事件日志订阅者 |
| `Assets/_Project/Scripts/Combat/_Debug/MeleeSwingTestDriver.cs` | 【临时】窗口驱动站位 |
| `Assets/_Project/Tests/Combat/Game.Combat.Tests.asmdef` | EditMode 测试程序集 |
| `Assets/_Project/Tests/Combat/DamagePipelineTests.cs` | DamagePipeline 单测 |

---

## Task 1: 伤害数据类型

**Files:**
- Create: `Assets/_Project/Scripts/Combat/DamageType.cs`
- Create: `Assets/_Project/Scripts/Combat/DamageRequest.cs`
- Create: `Assets/_Project/Scripts/Combat/DamageResult.cs`
- Create: `Assets/_Project/Scripts/Combat/DefenseProfile.cs`

纯数据类型，无行为，不单测。后续 Pipeline 任务覆盖其使用。

- [ ] **Step 1: 创建 DamageType.cs**

```csharp
namespace Game.Combat
{
    /// <summary>伤害类型。True 无视防御与抗性。</summary>
    public enum DamageType : byte
    {
        Physical = 0,
        Magical  = 1,
        True     = 2,
    }
}
```

- [ ] **Step 2: 创建 DamageRequest.cs**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 一次命中的攻击意图。攻击者数据按值快照，结算期间不回查攻击者对象，
    /// 规避"攻击者已销毁但伤害仍在结算"的空引用失败模式。
    /// </summary>
    public readonly struct DamageRequest
    {
        public readonly int AttackerId;
        public readonly byte AttackerTeam;
        public readonly float BaseAmount;
        public readonly DamageType Type;
        public readonly Vector3 HitPoint;
        public readonly Vector3 HitDirection;

        public DamageRequest(int attackerId, byte attackerTeam, float baseAmount,
                             DamageType type, Vector3 hitPoint, Vector3 hitDirection)
        {
            AttackerId   = attackerId;
            AttackerTeam = attackerTeam;
            BaseAmount   = baseAmount;
            Type         = type;
            HitPoint     = hitPoint;
            HitDirection = hitDirection;
        }
    }
}
```

- [ ] **Step 3: 创建 DamageResult.cs**

```csharp
namespace Game.Combat
{
    /// <summary>DamagePipeline.Resolve 的输出。纯数据。</summary>
    public readonly struct DamageResult
    {
        public readonly float Final;
        public readonly DamageType Type;
        public readonly bool WasMitigated;

        public DamageResult(float final, DamageType type, bool wasMitigated)
        {
            Final        = final;
            Type         = type;
            WasMitigated = wasMitigated;
        }
    }
}
```

- [ ] **Step 4: 创建 DefenseProfile.cs**

```csharp
namespace Game.Combat
{
    /// <summary>
    /// 目标防御档案。本轮仅预留字段，DamagePipeline 暂不套数值公式
    /// （Physical/Magical 走 passthrough）。未来 Buff/Debuff 通过修正此档案接入。
    /// </summary>
    public struct DefenseProfile
    {
        public float Armor;       // 预留：物理减伤参数
        public float MagicResist; // 预留：魔法减伤参数
    }
}
```

- [ ] **Step 5: 提交**

```bash
git add "Assets/_Project/Scripts/Combat/DamageType.cs" "Assets/_Project/Scripts/Combat/DamageRequest.cs" "Assets/_Project/Scripts/Combat/DamageResult.cs" "Assets/_Project/Scripts/Combat/DefenseProfile.cs"
git commit -m "feat(combat): add damage data types (request/result/profile/type)" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: EditMode 测试程序集

**Files:**
- Create: `Assets/_Project/Tests/Combat/Game.Combat.Tests.asmdef`

- [ ] **Step 1: 创建测试 asmdef**

```json
{
    "name": "Game.Combat.Tests",
    "rootNamespace": "",
    "references": [
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner",
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

- [ ] **Step 2: 开发者验证程序集被识别**

In Unity: 切回编辑器待其编译。打开 `Window > General > Test Runner > EditMode`。
Expected: 出现 `Game.Combat.Tests`（暂无测试用例）。若未出现，检查 `com.unity.test-framework`（manifest 已含 1.6.0）。

- [ ] **Step 3: 提交**

```bash
git add "Assets/_Project/Tests/Combat/Game.Combat.Tests.asmdef"
git commit -m "test(combat): add EditMode test assembly" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: DamagePipeline（TDD）

**Files:**
- Create: `Assets/_Project/Tests/Combat/DamagePipelineTests.cs`
- Create: `Assets/_Project/Scripts/Combat/DamagePipeline.cs`

- [ ] **Step 1: 写失败测试**

`Assets/_Project/Tests/Combat/DamagePipelineTests.cs`：

```csharp
using NUnit.Framework;
using UnityEngine;

namespace Game.Combat.Tests
{
    public class DamagePipelineTests
    {
        private static DamageRequest MakeRequest(float amount, DamageType type)
        {
            return new DamageRequest(
                attackerId: 1, attackerTeam: 0,
                baseAmount: amount, type: type,
                hitPoint: Vector3.zero, hitDirection: Vector3.forward);
        }

        [Test]
        public void True_IgnoresDefense_ReturnsBaseAmount()
        {
            var req = MakeRequest(50f, DamageType.True);
            var def = new DefenseProfile { Armor = 9999f, MagicResist = 9999f };

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            Assert.That(result.Final, Is.EqualTo(50f));
        }

        [Test]
        public void Physical_WithZeroDefense_ReturnsBaseAmount()
        {
            var req = MakeRequest(30f, DamageType.Physical);
            var def = new DefenseProfile { Armor = 0f, MagicResist = 0f };

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            Assert.That(result.Final, Is.EqualTo(30f));
        }

        [Test]
        public void Magical_WithZeroDefense_ReturnsBaseAmount()
        {
            var req = MakeRequest(40f, DamageType.Magical);
            var def = new DefenseProfile { Armor = 0f, MagicResist = 0f };

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            Assert.That(result.Final, Is.EqualTo(40f));
        }

        [Test]
        public void Resolve_NegativeBase_ClampsToZero()
        {
            var req = MakeRequest(-10f, DamageType.Physical);
            var def = new DefenseProfile();

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            Assert.That(result.Final, Is.EqualTo(0f));
        }

        [Test]
        public void Resolve_PreservesDamageType()
        {
            var req = MakeRequest(10f, DamageType.Magical);
            var def = new DefenseProfile();

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            Assert.That(result.Type, Is.EqualTo(DamageType.Magical));
        }
    }
}
```

- [ ] **Step 2: 写 stub 使测试可编译并失败（红）**

`Assets/_Project/Scripts/Combat/DamagePipeline.cs`：

```csharp
namespace Game.Combat
{
    public static class DamagePipeline
    {
        public static DamageResult Resolve(in DamageRequest req, in DefenseProfile def)
        {
            return new DamageResult(0f, req.Type, false); // stub
        }
    }
}
```

- [ ] **Step 3: 开发者运行测试，确认失败**

In Unity: Test Runner > EditMode > Run All。
Expected: `True_IgnoresDefense_ReturnsBaseAmount` / `Physical_WithZeroDefense_ReturnsBaseAmount` / `Magical_WithZeroDefense_ReturnsBaseAmount` 红（Final 期望非 0 实得 0）；`Resolve_NegativeBase_ClampsToZero` 与 `Resolve_PreservesDamageType` 绿。

- [ ] **Step 4: 实现真正逻辑**

替换 `DamagePipeline.cs` 全文：

```csharp
namespace Game.Combat
{
    /// <summary>
    /// 纯函数伤害计算管道。不依赖 MonoBehaviour，可在 EditMode 单测。
    /// 本轮：True 无视防御；Physical/Magical 读取防御档案但暂用 passthrough，
    /// 具体减伤公式留待后续（见 DefenseProfile）。
    /// </summary>
    public static class DamagePipeline
    {
        public static DamageResult Resolve(in DamageRequest req, in DefenseProfile def)
        {
            float final;
            bool mitigated = false;

            switch (req.Type)
            {
                case DamageType.True:
                    final = req.BaseAmount; // 无视防御
                    break;
                case DamageType.Physical:
                    // 预留公式位：例如 final = req.BaseAmount - def.Armor
                    final = req.BaseAmount;
                    break;
                case DamageType.Magical:
                    // 预留公式位：例如 final = req.BaseAmount * (1f - def.MagicResist)
                    final = req.BaseAmount;
                    break;
                default:
                    final = req.BaseAmount;
                    break;
            }

            if (final < 0f) final = 0f; // 结果非负钳制
            return new DamageResult(final, req.Type, mitigated);
        }
    }
}
```

- [ ] **Step 5: 开发者运行测试，确认通过（绿）**

In Unity: Test Runner > EditMode > Run All。
Expected: 5 个测试全绿。

- [ ] **Step 6: 提交**

```bash
git add "Assets/_Project/Scripts/Combat/DamagePipeline.cs" "Assets/_Project/Tests/Combat/DamagePipelineTests.cs"
git commit -m "feat(combat): add pure DamagePipeline with EditMode tests" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: IDamageable 契约 + 事件

**Files:**
- Create: `Assets/_Project/Scripts/Combat/IDamageable.cs`
- Create: `Assets/_Project/Scripts/Combat/Events/DamageReceivedEvent.cs`
- Create: `Assets/_Project/Scripts/Combat/Events/DeathEvent.cs`

- [ ] **Step 1: 创建 IDamageable.cs**

```csharp
namespace Game.Combat
{
    /// <summary>
    /// "可受伤害"统一契约，由 HealthComponent 实现。
    /// 命中判定方通过 ReceiveHit 提交攻击意图，实现方用自身防御档案结算。
    /// </summary>
    public interface IDamageable
    {
        /// <summary>所属阵营。命中判定据此跳过同阵营（含攻击者自身）。</summary>
        byte TeamId { get; }

        /// <summary>是否存活。已死目标不再受理命中。</summary>
        bool IsAlive { get; }

        /// <summary>受理一次命中。实现方负责结算、扣血并同帧派发事件。</summary>
        void ReceiveHit(in DamageRequest req);
    }
}
```

- [ ] **Step 2: 创建 Events/DamageReceivedEvent.cs**

```csharp
using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 目标受到伤害后同帧派发。表现层（VFX/UI/受击反应）订阅消费。
    /// 攻击者侧能力（吸血/伤害数字）亦可据 AttackerId + Amount 同帧响应。
    /// </summary>
    public struct DamageReceivedEvent : IGameEvent
    {
        public int TargetId;
        public int AttackerId;
        public float Amount;
        public DamageType Type;
        public Vector3 HitPoint;
        public Vector3 HitDirection;
        public float RemainingHp;
    }
}
```

- [ ] **Step 3: 创建 Events/DeathEvent.cs**

```csharp
using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>目标生命值归零后同帧派发。</summary>
    public struct DeathEvent : IGameEvent
    {
        public int TargetId;
        public int AttackerId;
        public Vector3 Position;
    }
}
```

- [ ] **Step 4: 提交**

```bash
git add "Assets/_Project/Scripts/Combat/IDamageable.cs" "Assets/_Project/Scripts/Combat/Events/DamageReceivedEvent.cs" "Assets/_Project/Scripts/Combat/Events/DeathEvent.cs"
git commit -m "feat(combat): add IDamageable contract and combat events" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: HealthComponent

**Files:**
- Create: `Assets/_Project/Scripts/Combat/HealthComponent.cs`

- [ ] **Step 1: 创建 HealthComponent.cs**

```csharp
using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 生命值管理 + IDamageable 实现。封装自身防御档案，受击时调用纯函数
    /// DamagePipeline.Resolve，扣血后同帧经 EventBus 派发 DamageReceived / Death。
    /// </summary>
    public class HealthComponent : MonoBehaviour, IDamageable
    {
        [SerializeField] private float _maxHp = 100f;
        [SerializeField] private byte _teamId = 0;
        [SerializeField] private DefenseProfile _defenseProfile;

        private float _currentHp;
        private int _id;

        public byte TeamId => _teamId;
        public bool IsAlive => _currentHp > 0f;
        public float CurrentHp => _currentHp;
        public float MaxHp => _maxHp;

        private void Awake()
        {
            _currentHp = _maxHp;
            _id = gameObject.GetInstanceID();
        }

        public void ReceiveHit(in DamageRequest req)
        {
            if (!IsAlive) return;

            DamageResult result = DamagePipeline.Resolve(in req, in _defenseProfile);
            _currentHp -= result.Final;
            if (_currentHp < 0f) _currentHp = 0f;

            EventBus<DamageReceivedEvent>.Publish(new DamageReceivedEvent
            {
                TargetId     = _id,
                AttackerId   = req.AttackerId,
                Amount       = result.Final,
                Type         = result.Type,
                HitPoint     = req.HitPoint,
                HitDirection = req.HitDirection,
                RemainingHp  = _currentHp,
            });

            if (_currentHp <= 0f)
            {
                EventBus<DeathEvent>.Publish(new DeathEvent
                {
                    TargetId   = _id,
                    AttackerId = req.AttackerId,
                    Position   = transform.position,
                });
            }
        }
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add "Assets/_Project/Scripts/Combat/HealthComponent.cs"
git commit -m "feat(combat): add HealthComponent implementing IDamageable" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: AttackDefinition ScriptableObject

**Files:**
- Create: `Assets/_Project/Scripts/Combat/AttackDefinition.cs`

- [ ] **Step 1: 创建 AttackDefinition.cs**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的近战攻击定义。MeleeHitDetector 读取几何与数值；
    /// Character 侧窗口驱动读取 ActiveStart/ActiveEnd（归一化动画时间，本轮不实现驱动）。
    /// 连段/技能系统将复用本资产。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Attack Definition", fileName = "AttackDefinition")]
    public class AttackDefinition : ScriptableObject
    {
        [Header("伤害")]
        public float BaseAmount = 10f;
        public DamageType Type = DamageType.Physical;

        [Header("命中体积 (OverlapBox 半尺寸)")]
        public Vector3 HalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

        [Header("激活窗口 (归一化动画时间 0~1，供 Character 侧驱动)")]
        [Range(0f, 1f)] public float ActiveStart = 0.30f;
        [Range(0f, 1f)] public float ActiveEnd   = 0.55f;
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add "Assets/_Project/Scripts/Combat/AttackDefinition.cs"
git commit -m "feat(combat): add data-driven AttackDefinition ScriptableObject" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: MeleeHitDetector

**Files:**
- Create: `Assets/_Project/Scripts/Combat/MeleeHitDetector.cs`

热路径权衡见文末「性能注记」。本组件对动画系统无知：窗口由外部经 Open/CloseHitWindow 控制（连段接入缝）。

- [ ] **Step 1: 创建 MeleeHitDetector.cs**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 近战命中判定。窗口激活期间每帧在武器挂点处做 OverlapBoxNonAlloc，
    /// 过滤自身/同阵营/已死/已命中后构造 DamageRequest 交目标结算。
    ///
    /// 窗口由外部（Character 侧动画驱动 / 连段系统）通过 Open/CloseHitWindow 控制，
    /// 本组件对动画系统无知 —— 这正是连段系统的接入缝。
    /// </summary>
    public class MeleeHitDetector : MonoBehaviour
    {
        [SerializeField] private AttackDefinition _attack;
        [SerializeField] private Transform _weaponPivot;          // 命中体积中心；为空时退回本 Transform
        [SerializeField] private Transform _ownerRoot;            // 攻击者根，用于 AttackerId
        [SerializeField] private byte _attackerTeam = 0;
        [SerializeField] private LayerMask _hitMask = ~0;

        private const int MaxHitsPerFrame = 16;
        private readonly Collider[] _buf = new Collider[MaxHitsPerFrame];
        private readonly HashSet<int> _hitSet = new HashSet<int>(); // per-swing 去重，预分配复用

        private bool _windowActive;
        private int _attackerId;

        /// <summary>开启攻击激活窗口。幂等：窗口已开时不重复清空去重集，
        /// 以便 Character 侧每帧调用 EnsureOpen 语义。</summary>
        public void OpenHitWindow()
        {
            if (_windowActive) return;
            _windowActive = true;
            _hitSet.Clear();
            _attackerId = _ownerRoot != null ? _ownerRoot.GetInstanceID() : gameObject.GetInstanceID();
        }

        /// <summary>关闭攻击激活窗口。幂等。</summary>
        public void CloseHitWindow()
        {
            _windowActive = false;
        }

        private void Update()
        {
            if (!_windowActive || _attack == null) return;
            DoOverlap();
        }

        private void DoOverlap()
        {
            Transform pivot = _weaponPivot != null ? _weaponPivot : transform;
            int count = Physics.OverlapBoxNonAlloc(
                pivot.position, _attack.HalfExtents, _buf, pivot.rotation,
                _hitMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                IDamageable target = ResolveDamageable(_buf[i]);
                if (target == null) continue;
                if (!target.IsAlive) continue;
                if (target.TeamId == _attackerTeam) continue;     // 跳过同阵营（含自身）

                Object targetObj = target as Object;
                if (targetObj == null) continue;
                int id = targetObj.GetInstanceID();
                if (!_hitSet.Add(id)) continue;                   // 本次挥砍已命中 → 去重

                Vector3 hitPoint = _buf[i].ClosestPoint(pivot.position);
                Vector3 hitDir = (hitPoint - pivot.position).normalized;

                var req = new DamageRequest(
                    _attackerId, _attackerTeam, _attack.BaseAmount,
                    _attack.Type, hitPoint, hitDir);
                target.ReceiveHit(in req);
            }
        }

        // 命中体 Collider → IDamageable 解析。集中于此，便于后续按需缓存（见计划文末性能注记）。
        private static IDamageable ResolveDamageable(Collider col)
        {
            return col.GetComponentInParent<IDamageable>();
        }

        private void OnDrawGizmosSelected()
        {
            if (_attack == null) return;
            Transform pivot = _weaponPivot != null ? _weaponPivot : transform;
            Gizmos.color = _windowActive ? Color.red : Color.yellow;
            Gizmos.matrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, _attack.HalfExtents * 2f);
        }
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add "Assets/_Project/Scripts/Combat/MeleeHitDetector.cs"
git commit -m "feat(combat): add MeleeHitDetector with OverlapBox hit detection and per-swing dedup" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 8: 预留缝 —— Skills 直接结算入口

**Files:**
- Create: `Assets/_Project/Scripts/Combat/CombatDamage.cs`

- [ ] **Step 1: 创建 CombatDamage.cs**

```csharp
namespace Game.Combat
{
    /// <summary>
    /// 【预留缝】Game.Skills 未来对目标直接结算伤害的入口（绕过近战命中判定）。
    /// 本轮不接技能系统；保留签名以锁定调用形态：技能命中后构造 DamageRequest 调本方法。
    /// </summary>
    public static class CombatDamage
    {
        public static void Deal(in DamageRequest req, IDamageable target)
        {
            if (target == null || !target.IsAlive) return;
            target.ReceiveHit(in req);
        }
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add "Assets/_Project/Scripts/Combat/CombatDamage.cs"
git commit -m "feat(combat): reserve CombatDamage facade for future Skills entry" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 9: 临时验证脚手架

**Files:**
- Create: `Assets/_Project/Scripts/Combat/_Debug/CombatDebugLogger.cs`
- Create: `Assets/_Project/Scripts/Combat/_Debug/MeleeSwingTestDriver.cs`

两者均为临时验证用，核心闭环验证通过后可删除。**不使用 Input 类**（避免依赖输入系统 / 遵守 CLAUDE.md），改用定时自动脉冲 + Inspector 右键菜单。

- [ ] **Step 1: 创建 CombatDebugLogger.cs**

```csharp
using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 【临时验证脚本】订阅战斗事件并经 GameLog 打印，用于 Scene 内手动验证核心闭环。
    /// 核心系统验证通过后可删除。
    /// </summary>
    public class CombatDebugLogger : MonoBehaviour
    {
        private void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamage);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamage);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void OnDamage(DamageReceivedEvent e)
        {
            GameLog.Info(
                $"Target {e.TargetId} took {e.Amount} {e.Type} dmg from {e.AttackerId}, HP left {e.RemainingHp}",
                "Combat");
        }

        private void OnDeath(DeathEvent e)
        {
            GameLog.Info($"Target {e.TargetId} died (killer {e.AttackerId})", "Combat");
        }
    }
}
```

- [ ] **Step 2: 创建 MeleeSwingTestDriver.cs**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 【临时验证脚本】站位代替 Character 侧的攻击激活窗口驱动：
    /// 每隔 _interval 秒自动开启窗口 _windowDuration 秒，用于验证 MeleeHitDetector。
    /// 也提供 Inspector 右键菜单手动开/关。真正的窗口驱动（数据驱动 normalized-time）
    /// 由 Character 侧实现，不在本轮。
    /// </summary>
    public class MeleeSwingTestDriver : MonoBehaviour
    {
        [SerializeField] private MeleeHitDetector _detector;
        [SerializeField] private float _windowDuration = 0.3f;
        [SerializeField] private float _interval = 2f;

        private float _timer;
        private bool _open;

        private void Update()
        {
            if (_detector == null) return;

            _timer += Time.deltaTime;
            if (!_open && _timer >= _interval)
            {
                _detector.OpenHitWindow();
                _open = true;
                _timer = 0f;
            }
            else if (_open && _timer >= _windowDuration)
            {
                _detector.CloseHitWindow();
                _open = false;
                _timer = 0f;
            }
        }

        [ContextMenu("Open Hit Window")]
        private void DebugOpen()
        {
            if (_detector != null) _detector.OpenHitWindow();
        }

        [ContextMenu("Close Hit Window")]
        private void DebugClose()
        {
            if (_detector != null) _detector.CloseHitWindow();
        }
    }
}
```

- [ ] **Step 3: 提交**

```bash
git add "Assets/_Project/Scripts/Combat/_Debug/CombatDebugLogger.cs" "Assets/_Project/Scripts/Combat/_Debug/MeleeSwingTestDriver.cs"
git commit -m "test(combat): add temporary scene verification scaffolding" -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 10: 手动 Scene 验证（开发者执行）

无代码产出。开发者在 Unity 中搭建最小场景验证核心闭环。

- [ ] **Step 1: 创建 AttackDefinition 资产**

In Unity: `Assets/_Project/ScriptableObjects/` 右键 `Create > Game > Combat > Attack Definition`。
设 `BaseAmount = 25`、`Type = Physical`、`HalfExtents = (0.5,0.5,0.5)`。

- [ ] **Step 2: 搭建目标对象**

新建空场景。建一个 Cube（自带 BoxCollider），命名 `Enemy`：
- 挂 `HealthComponent`，设 `MaxHp = 100`、`TeamId = 1`。
- 确保其 Layer 在攻击者 `_hitMask` 内（默认 `~0` 全选即可）。

- [ ] **Step 3: 搭建攻击者 + 武器**

建空物体 `Player`（TeamId 概念上 = 0）；其下建子物体 `Weapon`：
- `Weapon` 挂 `MeleeHitDetector`：`_attack` = Step1 资产；`_weaponPivot` = Weapon 自身；`_ownerRoot` = Player；`_attackerTeam = 0`；`_hitMask` = 含 Enemy 的层。
- 把 `Weapon` 摆到与 `Enemy` 重叠的位置（验证去重最直接）。
- `Player` 挂 `MeleeSwingTestDriver`，`_detector` = Weapon 上的 MeleeHitDetector。

- [ ] **Step 4: 挂调试日志**

场景任意物体（如新建 `_CombatDebug`）挂 `CombatDebugLogger`。

- [ ] **Step 5: 进入 Play，逐项核对**

Expected（Console 经 `[Combat]` 输出）：
1. 每次自动脉冲（窗口开 0.3s 跨多帧）→ **仅一条** `DamageReceived`（去重生效，不是每帧一条），HP 每次 -25。
2. HP 100 → 75 → 50 → 25 → 0，第 4 次脉冲触发 `died`，且其后不再有 `DamageReceived`（死亡早退）。
3. 把 `Enemy` 的 `TeamId` 改为 `0`（与攻击者同阵营）重测 → **无任何** `DamageReceived`（同阵营/自身过滤生效）。
4. 选中 `Weapon`，Scene 视图可见命中盒 gizmo（窗口激活时红、否则黄）。

- [ ] **Step 6（可选）：GC 校验**

Window > Analysis > Profiler，Play 中观察激活窗口期 `GC.Alloc` 列：`DoOverlap` 应为 0 分配（`OverlapBoxNonAlloc` + 预分配 buffer/HashSet + struct 事件）。

---

## 性能注记：MeleeHitDetector 热路径中的 GetComponentInParent（应用户要求）

**调用频率**：`ResolveDamageable` 即 `Collider.GetComponentInParent<IDamageable>()`，在**窗口激活的每一帧、对 OverlapBox 命中的每个 collider** 各调一次。
量级 = `重叠 collider 数 N × 激活帧数 F`。近战场景 N 通常 1~3、F 数帧 → 单次挥砍数十次调用。

**权衡**：
- `GetComponentInParent` 沿父链向上遍历 + 接口类型匹配，**返回已存在组件、不分配堆**（不破坏零 GC 目标），但并非零成本（父链遍历 + 类型检查）。
- 在上述量级下开销可忽略，**本轮不预先优化（YAGNI）**。
- 体积巨大命中很多目标、或父链很深时，此调用会成为可测量成本。

**预留的缓存优化点（本轮不实现，仅留缝）**：
- 解析逻辑已**单独隔离在 `ResolveDamageable(Collider)`**，未来可在此内置 `Dictionary<int /*collider instanceID*/, IDamageable>` 缓存，或改为"HealthComponent 启动时把自身 collider 注册进全局/局部映射"的注册表方案，命中时 O(1) 反查、彻底免遍历。
- 改造仅触及该私有方法与（可选）HealthComponent 的注册调用，不影响 `DoOverlap` 与对外 API。

---

## 反穿透（tunneling）预留缝

本轮基础为每激活帧 `OverlapBoxNonAlloc`，对近战宽弧已足够。若后续出现快/细武器穿透：
- 在 `DoOverlap` 前后记录 `pivot.position` 上一帧值，对快攻追加 `Physics.SphereCast(prevPos → curPos)` 的帧间扫描补判，命中同样经 `_hitSet` 去重。
- 该补强应做成按武器/AttackDefinition 可开关，**本轮不实现**。

---

## Self-Review

**Spec 覆盖**：① IDamageable → Task 4 ✓；② HealthComponent → Task 5 ✓；③ DamagePipeline(三类型/预留防御/纯函数可单测) → Task 3 ✓；④ Hit Detection(OverlapBox/动画驱动窗口缝/反穿透缝/去重) → Task 7 + 性能/反穿透注记 ✓；AttackDefinition SO → Task 6 ✓；Tests asmdef → Task 2 ✓；预留缝(Skills 入口/连段窗口 API/Buff via 纯函数/目标选择不进 Combat/Hit Reaction 事件字段) → Task 8 + 各处注释 ✓；GetComponentInParent 频率与缓存缝说明 → 性能注记 ✓。

**占位符扫描**：无 TBD/TODO 性占位；所有代码步骤含完整可编译代码。

**类型一致性**：`DamageRequest` 构造参数顺序（attackerId, attackerTeam, baseAmount, type, hitPoint, hitDirection）在测试、HealthComponent 间接、MeleeHitDetector 三处一致；`DamagePipeline.Resolve(in DamageRequest, in DefenseProfile)` 签名在 stub/实现/测试/HealthComponent 一致；`OpenHitWindow/CloseHitWindow` 命名在 MeleeHitDetector 与 MeleeSwingTestDriver 一致；事件字段在定义与 publish/订阅处一致。

**硬约束核对**：Combat 仅依赖 Core（asmdef 未改）；逻辑层仅经 EventBus 通信、无 VFX/UI/Audio 引用；事件均 struct + IGameEvent；Publish 同步当帧；热路径无 new/LINQ/装箱（NonAlloc + 预分配 + struct + `in`）。
