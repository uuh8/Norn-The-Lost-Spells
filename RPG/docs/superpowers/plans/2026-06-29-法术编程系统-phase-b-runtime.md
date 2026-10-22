# 法术编程系统 · 阶段 B：接运行时 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把阶段 A 的纯求值器接到真实战斗：新增 `SpellCaster`（运行法杖 → 把 `EmitCommand` 实例化成真实 `ProjectileBase` 投射物），并把法师左键从"硬编码火球/陨石"改成"运行当前法杖"。

**Architecture:** 求值（`Game.Skills.CastEvaluator`，已完成）保持纯逻辑；新增 `SpellCaster`（`Game.Character` 的 MonoBehaviour）作为"纯数据 → Unity 实例化"的桥：它调 `Evaluate` 拿 `List<EmitCommand>`，按一个纯函数 `SpellAiming.SpreadOffsetDegrees` 把多发散成扇形，朝"按下瞬间锁存的准心 `ClickAimPoint`"用现有 `ProjectileBase.Init` 生成投射物。法师的施法状态只负责动画/时机，释放点调 `SpellCaster.CastWand`。

**Tech Stack:** Unity 6.3 / C# / 复用现有 `ProjectileBase`/`Fireball`/`HealthComponent` / NUnit EditMode（仅纯函数 `SpellAiming`）。

**前置：** 阶段 A 已合入 main（`Game.Skills`：`SpellDefinition`/`CastEvaluator`/`EmitCommand`/`CastModifierState`）。spec：`docs/superpowers/specs/2026-06-28-spell-programming-system-design.md`。

## Global Constraints

来自 `CLAUDE.md`，每个任务隐含适用：

- **分工：Claude/子代理只写 `.cs` 与纯文本配置（`.asmdef`）并提交；编译、运行测试、Play 验证由开发者在 Unity 手动完成。** 不要声称"编译通过/能跑"，只能说"静态逻辑正确"。本计划中"编译 / 跑测试 / Play 验证"步骤标注 **(开发者)**——子代理在该步骤停下，不执行。
- 子代理**不能跑 Unity**：实现子代理只转写代码 + 提交，不编译不跑测试。本阶段大部分是 MonoBehaviour 集成代码，**无自动化测试**（靠开发者 Play 验证）；唯一可单测的是纯函数 `SpellAiming`（Task 1，EditMode）。
- **不创建 `.meta` 文件**（Unity 生成）。开发者打开 Editor 后会为新文件生成 `.meta`，最后统一提交。
- **命名空间匹配程序集**：`Game.Skills` / `Game.Character` / `Game.Skills.Tests`。命名：私有字段 `_camelCase`，公有 `PascalCase`。
- 性能：`CastWand` 由"开火"离散输入触发（非每帧热路径），但 `EmitCommand` 列表由 `SpellCaster` 预分配复用、求值器内 `Clear()`；禁止 LINQ/装箱。
- 日志用 `Game.Core.GameLog`（分类标签 `"Skills"`），不用 `Debug.Log`。

## 本阶段锁定的设计决策

1. **法师左键 = 运行当前法杖**，完全取代旧的"点按火球 / 长按陨石"。保留按下瞬间锁存准心（`ClickAimPoint`）+ 入队补发（快速连点不丢、方向按点按那一刻算）。
2. **陨石（hold）暂休眠**：`WizardController` 里的陨石字段/状态/hash **保留但不再路由**（避免改预制体序列化引用），`PlayerWizardHeavyState` 文件保留不再进入。陨石将在后续阶段作为"目标地面型法术"重做。（这是有意为之；审查若标记"死代码"，属本计划明确决策。）
3. **施法动画/时机仍由 `ComboDefinition` 段 0 驱动**（`AnimationStateName` + `ArrowSpawnTime`）；**投射物数据改由法杖提供**。所以法师仍需配一个 1 段 Combo 作为施法动画/节奏。
4. **多发散射**：`SpellCaster` 用纯函数 `SpellAiming.SpreadOffsetDegrees` 把同批 N 发在 `[-spread/2, +spread/2]` 上均匀展开（绕世界 Y 偏航）。spread=0 时重叠（符合数据；要散开就加"散射"修正法术）。
5. **依赖方向**：`Game.Character` 新增对 `Game.Skills` 的引用（spec 既定方向：Character 用 Skills 施法）。Skills 不反向依赖 Character。

## File Structure

| 文件 | 职责 |
|---|---|
| `Assets/_Project/Scripts/Skills/SpellAiming.cs` | 纯静态：`SpreadOffsetDegrees(index,count,spread)` 散射偏航角（可单测） |
| `Assets/_Project/Scripts/Skills/WandLoadout.cs` | `ScriptableObject`：法杖 = `SpellDefinition[] Spells` + `int BaseDraws` |
| `Assets/_Project/Tests/Skills/SpellAimingTests.cs` | `SpellAiming` 的 EditMode 单测 |
| `Assets/_Project/Scripts/Character/Game.Character.asmdef` | 增加对 `Game.Skills` 的引用 |
| `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs` | MonoBehaviour：运行法杖 → 实例化 `ProjectileBase` 投射物 |
| `Assets/_Project/Scripts/Character/Controllers/WizardController.cs` | 左键改为运行法杖（移除 hold→陨石路由；陨石休眠） |
| `Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs` | 释放点改调 `SpellCaster.CastWand`（不再硬编码 Fireball） |

---

### Task 1: 纯散射函数 + 法杖容器（Game.Skills，可单测）

**Files:**
- Create: `Assets/_Project/Scripts/Skills/SpellAiming.cs`
- Create: `Assets/_Project/Scripts/Skills/WandLoadout.cs`
- Test: `Assets/_Project/Tests/Skills/SpellAimingTests.cs`

**Interfaces:**
- Produces:
  - `static float SpellAiming.SpreadOffsetDegrees(int index, int count, float spreadDegrees)`
  - `class WandLoadout : ScriptableObject` with `public SpellDefinition[] Spells;` and `public int BaseDraws = 1;`

- [ ] **Step 1: 建 `SpellAiming.cs`**

```csharp
using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 法术发射方向辅助（纯函数，可单测）。SpellCaster 用它把"同一批 count 发投射物"散成扇形。
    /// </summary>
    public static class SpellAiming
    {
        /// <summary>
        /// 把同批 count 发投射物在 [-spread/2, +spread/2] 上均匀展开，返回第 index 发的偏航角（度）。
        /// count<=1 或 spread<=0 → 0。例：count=3, spread=30 → index 0/1/2 → -15/0/+15；count=2, spread=20 → -10/+10。
        /// </summary>
        public static float SpreadOffsetDegrees(int index, int count, float spreadDegrees)
        {
            if (count <= 1 || spreadDegrees <= 0f) return 0f;
            float t = (float)index / (count - 1); // 0..1
            return Mathf.Lerp(-spreadDegrees * 0.5f, spreadDegrees * 0.5f, t);
        }
    }
}
```

- [ ] **Step 2: 建 `WandLoadout.cs`**

```csharp
using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 法杖 = 一段"法术程序"：从左到右的法术序列 + 基础投射物预算。运行时由 SpellCaster 喂给 CastEvaluator。
    /// 早期写死在此资产里；后续阶段 C 由拖拽编程框 UI 编辑。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Skills/Wand Loadout", fileName = "WandLoadout")]
    public class WandLoadout : ScriptableObject
    {
        [Tooltip("法杖里从左到右的法术序列（求值器据此运行）")]
        public SpellDefinition[] Spells;

        [Tooltip("基础投射物预算（施放数）。多重法术在此之上叠加")]
        public int BaseDraws = 1;
    }
}
```

- [ ] **Step 3: 建 `SpellAimingTests.cs`**

```csharp
using NUnit.Framework;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class SpellAimingTests
    {
        [Test]
        public void SingleProjectile_NoOffset()
        {
            Assert.AreEqual(0f, SpellAiming.SpreadOffsetDegrees(0, 1, 30f), 1e-4f);
        }

        [Test]
        public void ZeroSpread_NoOffset()
        {
            Assert.AreEqual(0f, SpellAiming.SpreadOffsetDegrees(1, 3, 0f), 1e-4f);
        }

        [Test]
        public void Three_Spread30_FansEvenly()
        {
            Assert.AreEqual(-15f, SpellAiming.SpreadOffsetDegrees(0, 3, 30f), 1e-4f);
            Assert.AreEqual(0f, SpellAiming.SpreadOffsetDegrees(1, 3, 30f), 1e-4f);
            Assert.AreEqual(15f, SpellAiming.SpreadOffsetDegrees(2, 3, 30f), 1e-4f);
        }

        [Test]
        public void Two_Spread20_SymmetricEdges()
        {
            Assert.AreEqual(-10f, SpellAiming.SpreadOffsetDegrees(0, 2, 20f), 1e-4f);
            Assert.AreEqual(10f, SpellAiming.SpreadOffsetDegrees(1, 2, 20f), 1e-4f);
        }
    }
}
```

- [ ] **Step 4: (开发者) 跑 EditMode 测试**

操作：Test Runner → EditMode → Run All。预期：`SpellAimingTests` 4 个 + 阶段 A 的 18 个全部 **PASS**；工程编译无错。

- [ ] **Step 5: 提交**

```bash
git add Assets/_Project/Scripts/Skills/SpellAiming.cs Assets/_Project/Scripts/Skills/WandLoadout.cs Assets/_Project/Tests/Skills/SpellAimingTests.cs
git commit -m "feat(skills): add WandLoadout SO + pure SpellAiming spread helper with tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: SpellCaster（Game.Character ← Skills）

新增运行法杖、把 `EmitCommand` 实例化成真实投射物的桥接组件，并打通 `Game.Character → Game.Skills` 引用。本任务无自动化测试（Unity 集成），由开发者编译验证。

**Files:**
- Modify: `Assets/_Project/Scripts/Character/Game.Character.asmdef`（references 增加 Game.Skills 的 GUID）
- Create: `Assets/_Project/Scripts/Character/Spells/SpellCaster.cs`

**Interfaces:**
- Consumes: `Game.Skills.CastEvaluator.Evaluate`, `EmitCommand`, `CastModifierState`, `WandLoadout`, `SpellAiming`（Task 1/阶段A）；`Game.Combat.ProjectileBase.Init`。
- Produces: `int SpellCaster.CastWand(Vector3 spawnPos, Vector3 aimPoint, byte team, int attackerId, Collider casterCollider)` + `WandLoadout SpellCaster.Wand { get; }`

- [ ] **Step 1: 给 `Game.Character.asmdef` 增加 Game.Skills 引用**

把 references 数组替换为下面这版（在末尾新增 `"GUID:1b5a5875a02ff0f4ba52f02d43f76eb1"`，即 Game.Skills；其余 4 个保持原样）：

```json
{
    "name": "Game.Character",
    "rootNamespace": "",
    "references": [
        "GUID:510f5541b1215f1449e6418b6638bd1c",
        "GUID:22c3013598b2cc848a08181aa794a019",
        "GUID:e6c1abe66df22c04d88b8a9e6e7053d1",
        "GUID:75469ad4d38634e559750d17036d5f7c",
        "GUID:1b5a5875a02ff0f4ba52f02d43f76eb1"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 2: 建 `SpellCaster.cs`**

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
    /// CastWand：运行当前法杖 → CastEvaluator 求值 → 按 EmitCommand 逐个 Instantiate ProjectileBase，
    /// 朝 aimPoint 用 SpellAiming 散成扇形，复用现有 ProjectileBase.Init（直线投射物关重力）。
    /// 纯求值在 Game.Skills；本组件是"数据 → Unity 实例化"的唯一桥。
    /// </summary>
    public class SpellCaster : MonoBehaviour
    {
        [SerializeField] private WandLoadout _wand;
        [Tooltip("可用法力（占位）。资源系统是后续阶段；现给一个大值，求值器不会 fizzle。")]
        [SerializeField] private float _availableMana = 9999f;

        // 求值产出缓冲（预分配复用，求值器内 Clear()）——离散输入触发，避免每次施放新建 List
        private readonly List<EmitCommand> _emits = new List<EmitCommand>(16);

        public WandLoadout Wand => _wand;

        /// <summary>
        /// 运行当前法杖：求值 → 生成投射物。返回产出数量。
        /// spawnPos 投射物生成点；aimPoint 准心瞄准点（方向 = aimPoint - spawnPos）；team/attackerId 阵营快照；
        /// casterCollider 施法者碰撞体（忽略自撞）。
        /// </summary>
        public int CastWand(Vector3 spawnPos, Vector3 aimPoint, byte team, int attackerId, Collider casterCollider)
        {
            if (_wand == null || _wand.Spells == null || _wand.Spells.Length == 0)
            {
                GameLog.Warn("SpellCaster 未配置 WandLoadout 或法杖为空，无法施放", "Skills");
                return 0;
            }

            CastEvaluator.Evaluate(_wand.Spells, _wand.BaseDraws, _availableMana, CastModifierState.Default, _emits);

            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f) baseDir = transform.forward; // 退化兜底
            baseDir.Normalize();

            int count = _emits.Count;
            for (int i = 0; i < count; i++)
            {
                EmitCommand cmd = _emits[i];
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

                // 直线投射物关重力（与现有火球一致）；命中走标准 ProjectileBase → ReceiveHit
                proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, dir * cmd.Speed, casterCollider, useGravity: false);
            }

            return count;
        }
    }
}
```

- [ ] **Step 3: (开发者) 编译**

操作：回 Unity 让其编译（会为新 `Spells/` 文件夹 + `SpellCaster.cs` 生成 `.meta`）。预期：`Game.Character` 引用到 `Game.Skills`，全工程编译无错。（本任务无自动化测试。）

- [ ] **Step 4: 提交**

```bash
git add Assets/_Project/Scripts/Character/Game.Character.asmdef Assets/_Project/Scripts/Character/Spells/SpellCaster.cs
git commit -m "feat(character): add SpellCaster bridge (wand -> real projectiles); ref Game.Skills

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: 法师左键改为运行法杖

把法师攻击从"硬编码火球 / 长按陨石"改成"左键运行法杖"。两处全文件替换。

**Files:**
- Modify (full replace): `Assets/_Project/Scripts/Character/Controllers/WizardController.cs`
- Modify (full replace): `Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs`

**Interfaces:**
- Consumes: `SpellCaster.CastWand` (Task 2); existing `ClickAimPoint`/`HasClickAim`/`FireballSpawnPoint`/`Health`/`Combo` on WizardController.
- Produces: `WizardController.SpellCaster { get; }` (used by the attack state).

注意：本任务**移除** `WizardController` 的 `_fireballPrefab` / `_projectileSpeed` 序列化字段及其 `FireballPrefab`/`ProjectileSpeed` getter（投射物改由法杖提供，预制体上这两个引用会被 Unity 丢弃，属预期）。陨石相关成员**保留休眠**（不再路由）。

- [ ] **Step 1: 全文件替换 `WizardController.cs`**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师控制器：共享移动能力之上叠加远程攻击。攻击 = 运行"法杖"（法术编程系统）：
    /// 按左键 → 求值当前法杖（SpellCaster + Game.Skills.CastEvaluator）→ 生成对应投射物。
    /// 取代旧的"点按火球 / 长按陨石"硬编码攻击。陨石相关字段/状态暂保留为休眠（不再路由），后续作为法术重做。
    /// </summary>
    public class WizardController : PlayerControllerBase
    {
        [Header("Wizard Attack (法杖施放)")]
        [SerializeField] private ComboDefinition _combo;          // 施法动画/时机驱动（1 段：动画状态名 + ArrowSpawnTime 决定何时释放法杖）
        [SerializeField] private Transform _fireballSpawnPoint;   // 投射物生成点（法杖前端）
        [Tooltip("空中普攻动画状态名（Animator 节点 JumpAttack_MagicWand）；空 → 0 → 空中攻击退回地面普攻动画")]
        [SerializeField] private string _airAttackStateName = "JumpAttack_MagicWand";

        [Header("Wizard Heavy (陨石重击 — 暂休眠，后续作为法术重做)")]
        [SerializeField] private MeteorAttackDefinition _meteorData;
        [SerializeField] private GameObject _meteorPrefab;
        [SerializeField] private GameObject _channelRingPrefab;
        [SerializeField] private GameObject _targetIndicatorPrefab;
        [SerializeField] private LayerMask _aimMask = ~0;

        [Header("Aim")]
        [Tooltip("屏幕中心瞄准的可命中层（排除 Player 层，免瞄到自己）")]
        [SerializeField] private LayerMask _fireballAimMask = ~0;
        [Tooltip("屏幕中心瞄准的射线最大距离；未命中时取相机朝向该远点")]
        [SerializeField] private float _aimMaxDistance = 100f;

        private PlayerWizardAttackState _wizardAttackState;
        private PlayerWizardHeavyState _heavyState;   // 休眠：构造但不再进入
        private SpellCaster _spellCaster;             // 同物体上的法术施放器（运行法杖 → 生成投射物）
        private int[] _comboStateHashes;
        private HealthComponent _health;

        // ── 攻击输入（常驻处理：点按锁存准心 + 入队，见 UpdateAttackInput）──
        private bool _castQueued;                // 是否有一次待施放（按下入队；发出/过期出队）
        private float _castQueueTimer;           // 入队存活计时：跨过射速冷却仍能补发，过期作废
        private Vector3 _clickAimPoint;          // 按下那一刻锁存的准心瞄准点（消除出手时相机/身体已变的方向漂移）
        private bool _hasClickAim;
        private readonly RaycastHit[] _aimHits = new RaycastHit[16]; // 点按锁存瞄准用射线缓冲（预分配，零每帧 GC）

        private int _meteorChannelHash;          // 休眠
        private int _meteorReleaseHash;          // 休眠
        private int _airAttackStateHash;

        public ComboDefinition Combo => _combo;
        public Transform FireballSpawnPoint => _fireballSpawnPoint;
        public HealthComponent Health => _health;
        public SpellCaster SpellCaster => _spellCaster;
        public PlayerWizardAttackState WizardAttackState => _wizardAttackState;
        public Vector3 ClickAimPoint => _clickAimPoint; // 施法态释放时读取：按下那一刻锁存的准心
        public bool HasClickAim => _hasClickAim;

        public MeteorAttackDefinition MeteorData => _meteorData;
        public GameObject MeteorPrefab => _meteorPrefab;
        public GameObject ChannelRingPrefab => _channelRingPrefab;
        public GameObject TargetIndicatorPrefab => _targetIndicatorPrefab;
        public LayerMask AimMask => _aimMask;
        public LayerMask FireballAimMask => _fireballAimMask;
        public float AimMaxDistance => _aimMaxDistance;
        public PlayerWizardHeavyState HeavyState => _heavyState;
        public int MeteorChannelHash => _meteorChannelHash;
        public int MeteorReleaseHash => _meteorReleaseHash;
        public int AirAttackStateHash => _airAttackStateHash;

        protected override void Awake()
        {
            base.Awake();   // 基类：组件/输入/状态机/共享四态/Dash hash

            _health = GetComponent<HealthComponent>();
            _spellCaster = GetComponent<SpellCaster>();
            _wizardAttackState = new PlayerWizardAttackState(this);
            _heavyState = new PlayerWizardHeavyState(this);
            BuildComboStateHashes();
            BuildMeteorHashes();

            _airAttackStateHash = string.IsNullOrEmpty(_airAttackStateName)
                ? 0 : Animator.StringToHash(_airAttackStateName);
        }

        // 远程角色全程常驻准心：Start 保证初始可见，OnEnable 覆盖重新启用，OnDisable 隐藏。
        protected override void Start()
        {
            base.Start();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = false });
        }

        // 入队存活时长：覆盖一次射速冷却 + 通用攻击缓冲，确保冷却期内/动画播放中按下的下一次施放能可靠补发。
        private float CastQueueLifetime =>
            (_combo != null ? _combo.AttackCooldown : 0f) + AttackBufferTime;

        /// <summary>
        /// 每帧攻击输入常驻处理（由基类 Update 调用，始终运行，不被攻击状态打断）：
        /// 上升沿 → 锁存"点按那一刻"的准心瞄准点 + 入队一次施放；入队计时过期 → 作废。
        /// 这样快速连点即便落在动画/射速冷却内也不丢，冷却一过补发，方向用按下瞬间的准心。
        /// </summary>
        protected override void UpdateAttackInput()
        {
            if (AttackPressedThisFrame)
            {
                _clickAimPoint = ResolveAimTargetPoint(_fireballAimMask, _aimMaxDistance, _aimHits);
                _hasClickAim = true;
                _castQueued = true;
                _castQueueTimer = CastQueueLifetime;
            }

            if (_castQueueTimer > 0f)
            {
                _castQueueTimer -= Time.deltaTime;
                if (_castQueueTimer <= 0f) _castQueued = false; // 过期作废
            }
        }

        /// <summary>地面攻击：有入队施放且射速冷却已过 → 运行法杖。（左键 = 运行当前法杖。）</summary>
        public override bool TryStartAttack()
        {
            if (_castQueued && AttackCooldownCounter <= 0f)
            {
                FireQueuedCast();
                return true;
            }
            return false;
        }

        /// <summary>空中攻击：同样运行法杖（方向用按下锁存的 ClickAimPoint）。</summary>
        public override bool TryStartAirAttack()
        {
            if (_castQueued && AttackCooldownCounter <= 0f)
            {
                FireQueuedCast();
                return true;
            }
            return false;
        }

        /// <summary>出队 + 启动射速冷却 + 切到施法态（其 Enter 据接地决定空中/地面动画，释放点由 SpellCaster 运行法杖）。</summary>
        private void FireQueuedCast()
        {
            _castQueued = false;
            _castQueueTimer = 0f;
            AttackCooldownCounter = _combo != null ? _combo.AttackCooldown : 0f; // 射速冷却（与动画长度解耦）
            AttackBufferCounter = 0f;
            StateMachine.ChangeState(_wizardAttackState);
        }

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>把连段表各段 AnimationStateName 预 hash 成 int[]。</summary>
        private void BuildComboStateHashes()
        {
            int count = _combo != null ? _combo.SegmentCount : 0;
            _comboStateHashes = new int[count];
            for (int i = 0; i < count; i++)
            {
                AttackDefinition seg = _combo.Segments[i];
                string stateName = seg != null ? seg.AnimationStateName : null;
                if (string.IsNullOrEmpty(stateName))
                {
                    _comboStateHashes[i] = 0;
                    GameLog.Warn($"法师连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }

        // 休眠：陨石动画名预 hash（陨石暂不路由，后续作为法术重做时再启用）。
        private void BuildMeteorHashes()
        {
            if (_meteorData == null) return;
            _meteorChannelHash = string.IsNullOrEmpty(_meteorData.ChannelStateName)
                ? 0 : Animator.StringToHash(_meteorData.ChannelStateName);
            _meteorReleaseHash = string.IsNullOrEmpty(_meteorData.ReleaseStateName)
                ? 0 : Animator.StringToHash(_meteorData.ReleaseStateName);
        }
    }
}
```

- [ ] **Step 2: 全文件替换 `PlayerWizardAttackState.cs`**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师施法状态：播放施法动画（来自 ComboDefinition 段 0），在 normalizedTime 越过 ArrowSpawnTime 的单点
    /// 调 SpellCaster 运行当前法杖 → 生成对应投射物。1 段、无蓄力、边走边施。
    /// 投射物数据来自法杖（法术编程系统）；动画/时机来自 Combo 段。
    /// </summary>
    public class PlayerWizardAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 动画进度达此值 → 结束
        private const float CrossFadeDuration = 0.1f; // 进入施法段 CrossFade 固定时长

        private readonly WizardController _wizard;

        private int _comboIndex;
        private bool _castReleased;                    // 本次播放是否已运行过法杖（单点越阈触发一次的去重位）
        private bool _airborne;                        // 本次起手是否在空中：决定播空中动画 + 用真实重力

        public PlayerWizardAttackState(WizardController player) : base(player)
        {
            _wizard = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f;
            _castReleased = false;
            _airborne = !_player.GroundChecker.IsGrounded;

            if (_wizard.Combo == null || _wizard.Combo.SegmentCount == 0)
            {
                GameLog.Warn("法师 ComboDefinition 未配置或无段落（施法动画/时机来自 Combo 段），无法施法", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement();      // 边走边施：保留完整水平移动
            HandleAimRotation();   // 身体转向相机水平朝向
            HandleCastRelease();   // 单点：normalizedTime 越过 ArrowSpawnTime 运行一次法杖
            CheckEnd();            // 释放即交还控制权（节奏交给射速冷却）
        }

        public override void Exit()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f;
        }

        #endregion

        #region 处理流程函数

        private void StartSegment(int index)
        {
            _castReleased = false;
            int hash = _airborne && _wizard.AirAttackStateHash != 0
                ? _wizard.AirAttackStateHash
                : _wizard.GetComboStateHash(index);
            _player.Animator.CrossFadeInFixedTime(hash, CrossFadeDuration, 0);
        }

        private void HandleGravity()
        {
            if (_airborne)
            {
                float multiplier = _player.VerticalVelocity < 0f
                    ? _player.FallGravityMultiplier
                    : _player.GravityMultiplier;
                _player.VerticalVelocity += Physics.gravity.y * multiplier * Time.deltaTime;
            }
            else if (_player.VerticalVelocity < 0f)
            {
                _player.VerticalVelocity = -2f;
            }
        }

        private void HandleMovement()
        {
            Vector3 velocity = _player.MoveDirection * _player.MoveSpeed;
            velocity.y = _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        private void HandleCastRelease()
        {
            if (_castReleased) return;
            if (_player.Animator.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信

            AttackDefinition seg = _wizard.Combo.Segments[_comboIndex];
            if (seg == null) return;

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= seg.ArrowSpawnTime)
            {
                ReleaseCast();
                _castReleased = true;
            }
        }

        /// <summary>运行当前法杖：朝按下瞬间锁存的 ClickAimPoint，从法杖前端施放所有 EmitCommand（由 SpellCaster 落地）。</summary>
        private void ReleaseCast()
        {
            if (_wizard.SpellCaster == null || _wizard.FireballSpawnPoint == null)
            {
                GameLog.Warn("法师 SpellCaster/FireballSpawnPoint 未配置，无法施法", "Skills");
                return;
            }

            Vector3 spawnPos = _wizard.FireballSpawnPoint.position;
            Vector3 aimPoint = _wizard.HasClickAim
                ? _wizard.ClickAimPoint
                : spawnPos + _player.transform.forward * 10f; // 未锁存（理论上不会）才退回前向远点
            byte team = _wizard.Health != null ? _wizard.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();

            _wizard.SpellCaster.CastWand(spawnPos, aimPoint, team, attackerId, _player.CharacterController);
        }

        /// <summary>释放即结束回到移动态；兜底：动画接近播完(EndThreshold)也强制结束，避免卡死。</summary>
        private void CheckEnd()
        {
            if (_castReleased)
            {
                TransitionToMovement();
                return;
            }
            if (_player.Animator.IsInTransition(0)) return;
            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= EndThreshold)
                TransitionToMovement();
        }

        #endregion

        #region 功能函数

        private void TransitionToMovement()
        {
            if (_player.GroundChecker.IsGrounded)
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}
```

- [ ] **Step 3: (开发者) 编译**

操作：回 Unity 编译。预期：全工程编译无错（`WizardController` 不再引用 `FireballPrefab`/`ProjectileSpeed`；`PlayerWizardAttackState` 改调 `SpellCaster.CastWand`；陨石休眠成员仍编译）。本任务无自动化测试——行为验证在 Task 4 Play 模式。

- [ ] **Step 4: 提交**

```bash
git add Assets/_Project/Scripts/Character/Controllers/WizardController.cs Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs
git commit -m "feat(character): wizard left-click runs the wand (replaces hardcoded fireball/meteor)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Editor 组装 + Play 验证（开发者）

纯 Editor 工作，由开发者完成（Claude 不建资源/不连预制体）。

- [ ] **Step 1: 建法术资产**（右键 → Create → Game/Skills/Spell Definition）：
  - **火球**：Kind=Emit；ProjectilePrefab=现有 `Fireball` 预制体；BaseDamage=15；BaseSpeed=20；DamageType=Magical；ManaCost=0；DisplayName="火球"；Icon 留空。
  - **增伤**：Kind=Modify；ModDamageMul=1.5（其余 Mul 保持 1）。
  - **加速**：Kind=Modify；ModSpeedMul=1.5。
  - **散射**：Kind=Modify；ModSpreadAddDegrees=20。
  - **三重**：Kind=Multicast；ExtraDraws=2。
- [ ] **Step 2: 建法杖**（Create → Game/Skills/Wand Loadout）：先放 `Spells=[火球]`，`BaseDraws=1`。
- [ ] **Step 3: 给法师预制体加 `SpellCaster` 组件**，把上面的 WandLoadout 拖到其 `_wand`；确认 `WizardController._fireballSpawnPoint` 仍指向法杖前端（不变）、`_combo` 仍指向施法动画的 1 段 Combo。（`_fireballPrefab`/`_projectileSpeed` 字段已从脚本移除，Inspector 不再显示，属预期。）
- [ ] **Step 4: Play 验证组合涌现**（改 WandLoadout.Spells 数组即可换效果）：
  - `[火球]` → 朝准心 1 发火球（与改造前一致）。
  - `[增伤, 火球]` → 1 发更强火球（看敌人掉血更多）。
  - `[三重, 火球, 火球, 火球]` → 同时 3 发火球。
  - `[三重, 散射, 火球, 火球, 火球]` → 3 发呈扇形散开。
  - 快速连点 → 稳定补发、方向锁按下瞬间准心（不偏）。
  - 空中按左键 → 同样运行法杖。
- [ ] **Step 5: 提交 Unity 生成的 `.meta` + 资产**（开发者或让 Claude 代提交）：新法术/法杖 `.asset`、`Spells/` 文件夹与 `SpellCaster.cs` 的 `.meta`、法师预制体改动。

---

## 阶段 B 完成后的产物

- 法师左键 = 运行法杖，真实投射物由法术编程系统（求值 → SpellCaster）生成；组合涌现（增伤/加速/散射/三重）可玩可见。
- 纯求值器（Game.Skills）+ 桥（SpellCaster）+ 输入（WizardController）三层清晰；散射方向是可单测纯函数。

## 不在本阶段范围

- 拖拽编程框 UI + 背包 + 技能图标展示 → 阶段 C。
- 触发变种（递归 + payload + 护栏）、法力资源系统（回复/UI）、陨石作为"地面目标法术"重做、追踪/弹跳/穿透修正 → 后续阶段。

## Self-Review

- **Spec 覆盖**：EmitCommand → 真实投射物（T2 SpellCaster）、左键运行法杖取代火球/陨石（T3）、ClickAimPoint 方向（T3 复用）、散射扇形（T1 纯函数 + T2 应用）、复用 ProjectileBase + 关重力（T2）、ProjectilePrefab 判空（T2，复审遗留项已纳入）、Character→Skills 依赖（T2 asmdef）、施法动画/时机仍由 Combo 段驱动（T3）。✅
- **占位符扫描**：无 TBD/TODO；每个代码步骤为完整可编译代码或完整文件。✅
- **类型一致性**：`SpellCaster.CastWand(Vector3,Vector3,byte,int,Collider)` 在 T2 定义、T3（ReleaseCast）按此签名调用；`WandLoadout.Spells/BaseDraws`、`SpellAiming.SpreadOffsetDegrees(int,int,float)` 在 T1 定义、T2 使用一致；`CastEvaluator.Evaluate`/`EmitCommand`/`CastModifierState.Default` 沿用阶段 A 既有签名。✅
- **已知决策**：陨石休眠（保留成员不路由）+ 移除 `_fireballPrefab`/`_projectileSpeed` 字段——均在"设计决策"中明示，审查据此判定非缺陷。
