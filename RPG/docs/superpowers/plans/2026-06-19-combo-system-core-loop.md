# Combo System Core Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a data-driven linear combo system on top of the verified single-attack loop, so pressing attack inside a per-segment combo window advances through an ordered sequence of attack animations, each with its own damage/hitbox.

**Architecture:** Combat keeps pure combo *data* (`ComboDefinition` = ordered `AttackDefinition[]`; per-segment combo-input window fields; an Animator state-name *string*) and a pure decision function (`ComboResolver`, EditMode-tested like `DamagePipeline`). Character drives it: `PlayerAttackState` holds the `comboIndex`, calls `ComboResolver`, and on `Advance` does `CrossFade(hash)` + swaps the detector's active `AttackDefinition`. Animator state names are hashed once at startup on the Character side, so Combat never references `UnityEngine.Animator`.

**Tech Stack:** Unity 6.3, C#, Game.Combat / Game.Character asmdefs, NUnit EditMode tests.

---

## Confirmed design decisions (from brainstorm)

1. **Input buffer:** reuse the existing `AttackBufferCounter`; `PlayerAttackState.Exit()` zeroes it to prevent leak into a fresh attack.
2. **Animation switch:** `Animator.CrossFadeInFixedTime(hash, …)`; the per-segment Animator state name lives as a `string` on `AttackDefinition` and is pre-hashed once on the Character side.
3. **Reset responsibility:** a single `PlayerAttackState` instance owns the whole combo; `comboIndex` is reset only in `Exit()`, which every exit path (natural end / future interrupt) passes through.
4. **Pure function:** extract `ComboResolver.Resolve(...)` returning `ComboDecision {Continue, Advance, End}`, covered by EditMode tests.

## Hard constraints (do not violate)

- `Game.Combat` may reference **only** `Game.Core`. New Combat types must be pure data (string / enum / float / int) — **no** `UnityEngine.Animator` / `UnityEngine.InputSystem`.
- `Game.Character` may use Animator APIs (existing compliant pattern).
- `PlayerAttackState.Update` is a hot path: no `new` / LINQ / boxing.
- `CrossFade` must use pre-hashed `int` state hashes — no per-frame `StringToHash`, no runtime string building.

## File structure

| File | Asmdef | Action | Responsibility |
|------|--------|--------|----------------|
| `Assets/_Project/Scripts/Combat/AttackDefinition.cs` | Combat | Modify | + combo-input window fields + `AnimationStateName` string |
| `Assets/_Project/Scripts/Combat/ComboDefinition.cs` | Combat | Create | Ordered `AttackDefinition[]` segments (SO) |
| `Assets/_Project/Scripts/Combat/ComboDecision.cs` | Combat | Create | `enum ComboDecision : byte` |
| `Assets/_Project/Scripts/Combat/ComboResolver.cs` | Combat | Create | Pure advance/continue/end decision |
| `Assets/_Project/Tests/Combat/ComboResolverTests.cs` | Combat.Tests | Create | EditMode tests for `ComboResolver` |
| `Assets/_Project/Scripts/Combat/MeleeHitDetector.cs` | Combat | Modify | + `SetAttack(AttackDefinition)` segment-swap seam |
| `Assets/_Project/Scripts/Character/PlayerController.cs` | Character | Modify | + `ComboDefinition` field, pre-hashed state hashes, accessors |
| `Assets/_Project/Scripts/Character/States/PlayerStateBase.cs` | Character | Modify | Remove now-unused `AttackHash` |
| `Assets/_Project/Scripts/Character/States/PlayerAttackState.cs` | Character | Rewrite | Combo driver: comboIndex, ComboResolver, CrossFade, segment swap |

> **Note on Unity manual steps:** Claude only edits `.cs`/text files. After each task the developer compiles in the Unity Editor. EditMode tests (Task 3) and the in-editor scene/Animator verification (Task 8) are developer actions — the plan states expected results, it does not assert them.

---

### Task 1: Extend AttackDefinition with combo fields

**Files:**
- Modify: `Assets/_Project/Scripts/Combat/AttackDefinition.cs`

- [ ] **Step 1: Add combo-input window + animation state name fields**

Append the two new `[Header]` blocks after the existing `ActiveEnd` field. Final file content:

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的近战攻击定义。MeleeHitDetector 读取几何与数值；
    /// Character 侧窗口驱动读取 ActiveStart/ActiveEnd（归一化动画时间）。
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

        [Header("连段输入窗口 (归一化动画时间 0~1，此区间内按攻击键可接下一段)")]
        [Range(0f, 1f)] public float ComboInputStart = 0.40f;
        [Range(0f, 1f)] public float ComboInputEnd   = 0.70f;

        [Header("动画")]
        [Tooltip("本段对应的 Animator 状态名，必须与 Animator Controller 中的 state 名完全一致；" +
                 "Character 侧在 Awake 预 hash，本字段是纯字符串，不引用 Animator。")]
        public string AnimationStateName = "";
    }
}
```

- [ ] **Step 2: Compile in Unity Editor**

Expected: compiles with no errors. Existing `AttackDefinition` assets keep their values; new fields appear in the Inspector with defaults.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Project/Scripts/Combat/AttackDefinition.cs"
git commit -m "feat(combat): add combo-input window and animation state name to AttackDefinition"
```

---

### Task 2: Create ComboDefinition ScriptableObject

**Files:**
- Create: `Assets/_Project/Scripts/Combat/ComboDefinition.cs`

- [ ] **Step 1: Write the ComboDefinition SO**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的线性连段定义：按出招顺序排列的段落，每段引用一个 AttackDefinition。
    /// 纯数据，不引用 Animator/InputSystem。新武器 = 新建一份本资产，无需改代码。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Combo Definition", fileName = "ComboDefinition")]
    public class ComboDefinition : ScriptableObject
    {
        [Tooltip("按出招顺序排列的连段段落；索引 0 为起手段。")]
        public AttackDefinition[] Segments;

        /// <summary>段数；Segments 为空时返回 0。</summary>
        public int SegmentCount => Segments != null ? Segments.Length : 0;
    }
}
```

- [ ] **Step 2: Compile in Unity Editor**

Expected: compiles with no errors. `Create > Game > Combat > Combo Definition` appears in the asset create menu.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Project/Scripts/Combat/ComboDefinition.cs"
git commit -m "feat(combat): add ComboDefinition SO (ordered AttackDefinition segments)"
```

---

### Task 3: ComboResolver pure function + EditMode tests (TDD)

**Files:**
- Create: `Assets/_Project/Scripts/Combat/ComboDecision.cs`
- Create: `Assets/_Project/Scripts/Combat/ComboResolver.cs`
- Test: `Assets/_Project/Tests/Combat/ComboResolverTests.cs`

- [ ] **Step 1: Add the ComboDecision enum**

```csharp
namespace Game.Combat
{
    /// <summary>ComboResolver.Resolve 的输出：本帧连段该做什么。</summary>
    public enum ComboDecision : byte
    {
        Continue = 0, // 维持当前段，继续播放
        Advance  = 1, // 推进到下一段
        End      = 2, // 连段结束，退出攻击态
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `Assets/_Project/Tests/Combat/ComboResolverTests.cs`:

```csharp
using NUnit.Framework;
using Game.Combat;

namespace Game.Combat.Tests
{
    public class ComboResolverTests
    {
        private const float Start = 0.40f;
        private const float End = 0.70f;
        private const float EndThreshold = 0.85f;

        [Test]
        public void InsideWindow_WithBuffer_HasNext_Advances()
        {
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.50f, true, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Advance, d);
        }

        [Test]
        public void InsideWindow_NoBuffer_Continues()
        {
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.50f, false, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Continue, d);
        }

        [Test]
        public void InsideWindow_WithBuffer_LastSegment_DoesNotAdvance()
        {
            // comboIndex 2 of 3 → no next segment
            ComboDecision d = ComboResolver.Resolve(2, 3, 0.50f, true, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Continue, d);
        }

        [Test]
        public void BeforeWindow_WithBuffer_Continues()
        {
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.20f, true, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Continue, d);
        }

        [Test]
        public void AfterWindow_BeforeEndThreshold_WithBuffer_Continues()
        {
            // 0.75 is past End(0.70) but before EndThreshold(0.85): window missed, not yet ending
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.75f, true, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.Continue, d);
        }

        [Test]
        public void ReachedEndThreshold_NoBuffer_Ends()
        {
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.90f, false, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.End, d);
        }

        [Test]
        public void LastSegment_ReachedEndThreshold_Ends()
        {
            ComboDecision d = ComboResolver.Resolve(2, 3, 0.90f, false, Start, End, EndThreshold);
            Assert.AreEqual(ComboDecision.End, d);
        }

        [Test]
        public void AdvancePriorityOverEnd_WhenWindowOverlapsThreshold()
        {
            // window end (0.95) beyond threshold; inside window + buffer + next → Advance wins over End
            ComboDecision d = ComboResolver.Resolve(0, 3, 0.90f, true, 0.40f, 0.95f, EndThreshold);
            Assert.AreEqual(ComboDecision.Advance, d);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they FAIL (developer, Unity Test Runner → EditMode)**

Expected: compile error / failing tests — `ComboResolver` does not exist yet.

- [ ] **Step 4: Write the minimal implementation**

Create `Assets/_Project/Scripts/Combat/ComboResolver.cs`:

```csharp
namespace Game.Combat
{
    /// <summary>
    /// 纯函数连段判定。不依赖 MonoBehaviour/Animator，可在 EditMode 单测（仿 DamagePipeline）。
    /// 给定当前段进度与是否有缓冲输入，决定本帧维持/推进/结束。
    /// </summary>
    public static class ComboResolver
    {
        /// <param name="comboIndex">当前段索引（0 起）。</param>
        /// <param name="segmentCount">连段总段数。</param>
        /// <param name="normalizedTime">当前段动画归一化进度（0~1）。</param>
        /// <param name="hasBufferedInput">是否有未消耗的攻击缓冲输入。</param>
        /// <param name="inputStart">本段连段输入窗口起点（归一化）。</param>
        /// <param name="inputEnd">本段连段输入窗口终点（归一化）。</param>
        /// <param name="endThreshold">动画进度达到此值且未推进则结束连段。</param>
        public static ComboDecision Resolve(
            int comboIndex, int segmentCount, float normalizedTime, bool hasBufferedInput,
            float inputStart, float inputEnd, float endThreshold)
        {
            bool hasNext = comboIndex + 1 < segmentCount;

            // 1. 输入窗口内 + 有缓冲输入 + 有下一段 → 推进（优先于结束）
            if (hasBufferedInput && hasNext &&
                normalizedTime >= inputStart && normalizedTime <= inputEnd)
            {
                return ComboDecision.Advance;
            }

            // 2. 动画接近播完且未推进 → 结束连段
            if (normalizedTime >= endThreshold)
            {
                return ComboDecision.End;
            }

            // 3. 其它 → 维持当前段
            return ComboDecision.Continue;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they PASS (developer, Unity Test Runner → EditMode)**

Expected: all 8 `ComboResolverTests` pass.

- [ ] **Step 6: Commit**

```bash
git add "Assets/_Project/Scripts/Combat/ComboDecision.cs" "Assets/_Project/Scripts/Combat/ComboResolver.cs" "Assets/_Project/Tests/Combat/ComboResolverTests.cs"
git commit -m "feat(combat): add ComboResolver pure function with EditMode tests"
```

---

### Task 4: Add segment-swap seam to MeleeHitDetector

**Files:**
- Modify: `Assets/_Project/Scripts/Combat/MeleeHitDetector.cs`

- [ ] **Step 1: Add the `SetAttack` method next to the existing `Attack` getter**

Find this block (around line 30-31):

```csharp
        // Public API
        public AttackDefinition Attack => _attack;
```

Replace it with:

```csharp
        // Public API
        public AttackDefinition Attack => _attack;

        /// <summary>
        /// 切换当前生效的攻击定义（连段换段时由 Character 侧调用）。
        /// 不主动清空去重集；换段方应配合 CloseHitWindow，使新段窗口在 ActiveStart
        /// 重新 OpenHitWindow 时（幂等 open 会 Clear）自然得到一次干净的 per-swing 去重。
        /// </summary>
        public void SetAttack(AttackDefinition attack)
        {
            _attack = attack;
        }
```

- [ ] **Step 2: Compile in Unity Editor**

Expected: compiles with no errors.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Project/Scripts/Combat/MeleeHitDetector.cs"
git commit -m "feat(combat): add MeleeHitDetector.SetAttack segment-swap seam"
```

---

### Task 5: Wire ComboDefinition + pre-hashed state hashes into PlayerController

**Files:**
- Modify: `Assets/_Project/Scripts/Character/PlayerController.cs`

- [ ] **Step 1: Add the `Game.Core` using directive**

Find (line 1-2):

```csharp
using UnityEngine;
using Game.Combat;
```

Replace with:

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;
```

- [ ] **Step 2: Add the serialized ComboDefinition field**

Find the Combat header block:

```csharp
        [Header("Combat")]
        [SerializeField] private MeleeHitDetector _meleeHitDetector; // 在Inspector里拖入武器上的组件
        [SerializeField] private float _attackBufferTime = 0.15f;    // 攻击缓冲时间
```

Replace with:

```csharp
        [Header("Combat")]
        [SerializeField] private MeleeHitDetector _meleeHitDetector; // 在Inspector里拖入武器上的组件
        [SerializeField] private float _attackBufferTime = 0.15f;    // 攻击缓冲时间
        [SerializeField] private ComboDefinition _combo;             // 当前武器的连段表（拖入 SingleTwoHandSword）
```

- [ ] **Step 3: Add the pre-hashed state-hash cache field**

Find the runtime data block:

```csharp
        // 运行时数据
        private Vector2 _moveInput;
        private Vector3 _moveDirection;
        private Vector2 _lookInput;
        private float _cameraYaw;    // 累积的水平角
        private float _cameraPitch;  // 累积的俯仰角
```

Replace with:

```csharp
        // 运行时数据
        private Vector2 _moveInput;
        private Vector3 _moveDirection;
        private Vector2 _lookInput;
        private float _cameraYaw;    // 累积的水平角
        private float _cameraPitch;  // 累积的俯仰角

        // 连段各段 Animator 状态名的预 hash 结果（Awake 算一次，避免每次切段 StringToHash）
        private int[] _comboStateHashes;
```

- [ ] **Step 4: Add the Combat accessors for PlayerAttackState**

Find the combat accessor block:

```csharp
        public float AttackBufferCounter { get; set; }
        public float AttackBufferTime => _attackBufferTime;
        public MeleeHitDetector MeleeHitDetector => _meleeHitDetector;
```

Replace with:

```csharp
        public float AttackBufferCounter { get; set; }
        public float AttackBufferTime => _attackBufferTime;
        public MeleeHitDetector MeleeHitDetector => _meleeHitDetector;
        public ComboDefinition Combo => _combo;

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }
```

- [ ] **Step 5: Build the hash cache in Awake**

Find the end of `Awake()`:

```csharp
            // 创建状态机和所有状态实例
            // 所有 State 在这里 new 好，运行时切换状态只是改引用，不产生 GC
            _stateMachine = new PlayerStateMachine();
            _groundedState = new PlayerGroundedState(this);
            _airborneState = new PlayerAirborneState(this);
            _slidingState = new PlayerSlidingState(this);
            _attackState = new PlayerAttackState(this);
        }
```

Replace with:

```csharp
            // 创建状态机和所有状态实例
            // 所有 State 在这里 new 好，运行时切换状态只是改引用，不产生 GC
            _stateMachine = new PlayerStateMachine();
            _groundedState = new PlayerGroundedState(this);
            _airborneState = new PlayerAirborneState(this);
            _slidingState = new PlayerSlidingState(this);
            _attackState = new PlayerAttackState(this);

            BuildComboStateHashes();
        }

        /// <summary>
        /// 把连段表各段的 AnimationStateName 预 hash 成 int[]，运行期切段直接用 hash CrossFade，
        /// 不做每帧/每次切段的 StringToHash。状态名为空时记 0 并告警（CrossFade 0 不会切动画）。
        /// </summary>
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
                    GameLog.Warn($"连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }
```

- [ ] **Step 6: Compile in Unity Editor**

Expected: compiles with no errors. `PlayerController` now shows a `Combo` slot under Combat; leave it empty for now (filled in Task 8).

- [ ] **Step 7: Commit**

```bash
git add "Assets/_Project/Scripts/Character/PlayerController.cs"
git commit -m "feat(character): wire ComboDefinition and pre-hash combo Animator state names"
```

---

### Task 6: Remove the now-unused AttackHash from PlayerStateBase

**Files:**
- Modify: `Assets/_Project/Scripts/Character/States/PlayerStateBase.cs`

> `PlayerAttackState` will switch from `SetTrigger(AttackHash)` to `CrossFade` (Task 7). `JumpHash` stays (used by jump). Removing the unused `AttackHash` avoids a CS0414 "assigned but never used" warning.

- [ ] **Step 1: Drop the AttackHash field**

Find:

```csharp
		protected static readonly int JumpHash   = Animator.StringToHash("jump");
		protected static readonly int AttackHash = Animator.StringToHash("attack");
```

Replace with:

```csharp
		protected static readonly int JumpHash   = Animator.StringToHash("jump");
```

- [ ] **Step 2: Compile in Unity Editor**

Expected: compiles. (If a compile error references `AttackHash`, Task 7 has not been applied yet — that's fine if Task 7 follows immediately; otherwise apply Task 6 and Task 7 together before compiling.)

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Project/Scripts/Character/States/PlayerStateBase.cs"
git commit -m "refactor(character): remove unused AttackHash (attack now uses CrossFade)"
```

---

### Task 7: Rewrite PlayerAttackState as the combo driver

**Files:**
- Rewrite: `Assets/_Project/Scripts/Character/States/PlayerAttackState.cs`

> Depends on Tasks 1-6 (ComboResolver, ComboDefinition, SetAttack, PlayerController accessors, AttackHash removal). Compile only after both Task 6 and Task 7 are written.

- [ ] **Step 1: Replace the entire file**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 攻击状态 —— 连段驱动。同一个实例承载整套连段：
    /// 1. Enter 起手段 0：CrossFade 到该段动画 + 把该段 AttackDefinition 推给命中判定器
    /// 2. Update 每帧：按段 ActiveStart/End 开/关命中窗口；用 ComboResolver 判定维持/推进/结束
    ///    - Advance：comboIndex++，消耗缓冲输入，CrossFade 下一段并换段
    ///    - End：动画播完未接 → 切回移动状态
    /// 3. Exit：comboIndex 归零 + 清攻击缓冲（防泄漏）+ 关命中窗口
    ///    —— 任何离开攻击态的路径（自然结束/未来受击打断）都经 Exit，是连段归零的唯一责任点。
    /// </summary>
    public class PlayerAttackState : PlayerStateBase
    {
        // 动画进度达到此值且本段未推进 → 结束连段（与旧单段 85% 切回保持一致）
        private const float EndThreshold = 0.85f;
        // 段切换 CrossFade 固定时长（秒）
        private const float CrossFadeDuration = 0.1f;

        // 当前打到第几段（0 起）。仅在本状态内维护，唯一归零点是 Exit()。
        private int _comboIndex;

        public PlayerAttackState(PlayerController player) : base(player) { }

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 消耗起手输入，防重复触发

            if (_player.Combo == null || _player.Combo.SegmentCount == 0)
            {
                GameLog.Warn("ComboDefinition 未配置或无段落，无法攻击", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            // 攻击时施加向下压力，保持与地面接触（防止飘起来）
            HandleGravity();
            // 攻击时锁定水平移动，只保留垂直速度（重力）
            HandleMovement();
            // 每帧按当前段进度开/关命中窗口
            HandleAttackWindow();
            // 连段判定：维持/推进/结束
            CheckCombo();
        }

        public override void Exit()
        {
            _comboIndex = 0;                              // 归零（唯一责任点）
            _player.AttackBufferCounter = 0f;            // 清残留，防连段结束后误触发新普攻
            _player.MeleeHitDetector?.CloseHitWindow();  // 关窗，防残留
        }

        /// <summary>切到第 index 段：换命中数据 + 关窗（让新段在 ActiveStart 重新开窗清去重）+ CrossFade 动画。</summary>
        private void StartSegment(int index)
        {
            AttackDefinition seg = _player.Combo.Segments[index];

            if (_player.MeleeHitDetector != null)
            {
                _player.MeleeHitDetector.SetAttack(seg);
                _player.MeleeHitDetector.CloseHitWindow();
            }

            int hash = _player.GetComboStateHash(index);
            _player.Animator.CrossFadeInFixedTime(hash, CrossFadeDuration, 0);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // 锁定水平移动；CC 每帧必须被 Move，否则 isGrounded 检测失效
            Vector3 velocity = Vector3.up * _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        private void HandleAttackWindow()
        {
            if (_player.MeleeHitDetector == null) return;
            AttackDefinition def = _player.MeleeHitDetector.Attack;
            if (def == null) return;

            // 过渡期：normalizedTime 读到的是源状态的值，不代表当前段进度 → 强制关窗
            if (_player.Animator.IsInTransition(0))
            {
                _player.MeleeHitDetector.CloseHitWindow();
                return;
            }

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;

            if (t >= def.ActiveStart && t <= def.ActiveEnd)
                _player.MeleeHitDetector.OpenHitWindow();
            else
                _player.MeleeHitDetector.CloseHitWindow();
        }

        private void CheckCombo()
        {
            // 过渡期不做连段判定：normalizedTime 还是上一段的值（也防同帧切段后立刻又判一次）
            if (_player.Animator.IsInTransition(0)) return;

            AttackDefinition seg = _player.Combo.Segments[_comboIndex];
            // Inspector 里 Segments 该格未赋值时 seg 为 null，安全退出而不是每帧 NRE
            // （与 Enter/HandleAttackWindow/BuildComboStateHashes 的 null 防御风格保持一致）
            if (seg == null)
            {
                GameLog.Warn($"连段第 {_comboIndex} 段 AttackDefinition 未赋值，连段中断", "Combat");
                TransitionToMovement();
                return;
            }

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            bool hasBuffer = _player.AttackBufferCounter > 0f;

            ComboDecision decision = ComboResolver.Resolve(
                _comboIndex, _player.Combo.SegmentCount, t, hasBuffer,
                seg.ComboInputStart, seg.ComboInputEnd, EndThreshold);

            switch (decision)
            {
                case ComboDecision.Advance:
                    _comboIndex++;
                    _player.AttackBufferCounter = 0f; // 消耗输入：防同帧重复推进 + 防泄漏
                    StartSegment(_comboIndex);
                    break;
                case ComboDecision.End:
                    TransitionToMovement();
                    break;
                // ComboDecision.Continue: 维持当前段，无操作
            }
        }

        private void TransitionToMovement()
        {
            if (_player.GroundChecker.IsGrounded)
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                _player.StateMachine.ChangeState(_player.AirborneState);
        }
    }
}
```

- [ ] **Step 2: Compile in Unity Editor**

Expected: compiles with no errors (with Task 6 applied). `NormalAttackStateHash` and `SetTrigger(AttackHash)` are gone; combo entry is via `CrossFadeInFixedTime`.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Project/Scripts/Character/States/PlayerAttackState.cs"
git commit -m "feat(character): drive linear combo in PlayerAttackState via ComboResolver + CrossFade"
```

---

### Task 8: In-editor combo verification (developer, joint)

> No code. This validates the loop in the real scene used for M3 (Player + Enemy + CombatDebugLogger). Claude assists; the developer performs Unity actions.

- [ ] **Step 1: Create per-segment AttackDefinition assets**

In `Assets/_Project/ScriptableObjects/`, create 7 `Game > Combat > Attack Definition` assets, one per clip. For each, set `AnimationStateName` to the **exact** Animator state name of that clip (e.g. `NormalAttack01_SingleTwohandSword`, `NormalAttack02_...`, `Combo01_...` … `Combo05_...`). Tune `BaseAmount`, `HalfExtents`, `ActiveStart/End`, and `ComboInputStart/End` per segment (a sane start: ActiveStart 0.3 / ActiveEnd 0.55, ComboInputStart 0.45 / ComboInputEnd 0.75).

- [ ] **Step 2: Create the ComboDefinition asset**

Create `Game > Combat > Combo Definition` (e.g. `Combo_SingleTwoHandSword`). Set `Segments` size to 7 and drag the 7 AttackDefinitions in attack order.

- [ ] **Step 3: Wire references**

On the Player's `PlayerController`, assign `_combo` = `Combo_SingleTwoHandSword`. Confirm `_meleeHitDetector` is still assigned (its serialized `_attack` is now overwritten at runtime by `SetAttack`, so its inspector value only matters before the first attack).

- [ ] **Step 4: Set up the Animator Controller**

Ensure all 7 attack clips exist as states whose names match the `AnimationStateName` strings exactly. Each attack state should have an exit transition back to locomotion (ExitTime ~0.9, like the existing `NormalAttack01` state) so the visual returns to Idle/Run when the combo ends. The `attack` trigger is no longer used to enter attacks (entry is now `CrossFade`); remove or leave any `attack`-triggered transitions as long as they don't fire spuriously (e.g. avoid an Any-State `attack` transition that would interrupt mid-combo).

- [ ] **Step 5: Play and verify**

Enter Play mode and verify (watch `[Combat]` Console output from `CombatDebugLogger`):
1. Single press → segment 0 plays, hits once per swing, returns to locomotion (no combo).
2. Press within the combo window of each segment → advances 0→1→2…→6, each segment hits the enemy once (per-swing dedup resets between segments).
3. Press *outside* the window (too early before `ComboInputStart`, or after `ComboInputEnd`) → does **not** advance; combo ends naturally.
4. After the combo ends, the buffered input does **not** auto-start a fresh attack (Exit cleared `AttackBufferCounter`); a new attack needs a new press.
5. Jump or walk off a ledge mid-combo → `Exit()` runs, `comboIndex` resets; next attack starts from segment 0.
6. (Optional) Profiler: during the combo, `PlayerAttackState.Update` / `MeleeHitDetector` show 0 B GC Alloc.

- [ ] **Step 6: Report results**

Report any mismatch (wrong animation switching = check `AnimationStateName` vs Animator state name; combo never advances = check `ComboInputStart/End` vs actual clip timing; double-hit = check segment close/open timing).

---

## Self-review notes

- **Spec coverage:** ① reuse `AttackBufferCounter` + Exit-clear → Task 7 `Enter`/`CheckCombo`/`Exit`. ② `CrossFadeInFixedTime(hash)` + `AnimationStateName` on `AttackDefinition` + Character-side pre-hash → Tasks 1, 5, 7. ③ `comboIndex` reset only in `Exit()` → Task 7. ④ `ComboResolver` + EditMode tests → Task 3. `ComboDefinition` data-driven, new weapon = new asset → Task 2 + Task 8. `MeleeHitDetector` segment swap → Task 4.
- **Failure modes:** same-frame double advance (buffer zeroed on Advance + `IsInTransition` guard + normalizedTime reset); no reset after interrupt (single `Exit()` reset point); buffer leak (Exit clears `AttackBufferCounter`); wrong CrossFade hash (pre-hash with empty-name `GameLog.Warn`, Task 5; Task 8 Step 4 Animator-name check); unassigned `Segments[]` slot → null element (CheckCombo `seg == null` guard: `GameLog.Warn` + `TransitionToMovement`, consistent with other null defenses).
- **Constraints:** Combat additions (`ComboDefinition`, `ComboResolver`, `ComboDecision`, `AttackDefinition` fields, `SetAttack`) are pure data/logic — no Animator/InputSystem. Hashing happens Character-side. No `new`/LINQ/boxing in `PlayerAttackState.Update`; `CrossFade` uses cached `int` hashes.
