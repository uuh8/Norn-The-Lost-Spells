# 弓箭手蓄力重击（Archer Phase 4）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans（或 subagent-driven-development）逐任务实现。步骤用 checkbox 跟踪。Unity 编译/Play 由开发者手动完成；Claude 不声称"能跑"，只保证静态逻辑正确。

**Goal:** 弓箭手按住左键进入蓄力（拉弓→满弓保持，蓄力时长累计封顶），松开按蓄力比例（0~1 线性）发射一支伤害/箭速更高的箭；点按仍走 Phase 3 普通攻击。

**Architecture:** 输入路由（代码计时 + 阈值门控，tap 不经拉弓）在 `ArcherController.TryStartAttack` 完成：按住过 `TapThreshold` → 新状态 `PlayerChargeAttackState`；未达阈值松手 → Phase 3 `PlayerBowAttackState`。蓄力数据放数据驱动 SO `ChargeAttackDefinition`（三段状态名 + 输入/蓄力参数 + 蓄力→伤害/箭速线性端点）。放箭复用 Phase 3 的 `Arrow` 与 `_arrowPrefab`/`_arrowSpawnPoint`，不改 Arrow。基类加一个 `IsAttackHeld` 轮询属性。

**Tech Stack:** Unity 6.3 / C# / `Game.Combat`（ChargeAttackDefinition SO）/ `Game.Character`（base 属性、ArcherController、PlayerChargeAttackState）/ Unity Input System（轮询 `Attack.IsPressed()`）。

## Global Constraints
- **Warrior 与 Phase 3 普攻零回归**：`PlayerController`/`WarriorController`/`PlayerAttackState`/`PlayerBowAttackState`/`Arrow` 不改。
- **asmdef 方向**：`Game.Combat` 不引用 `Game.Character`（SO 只是数据）；`Game.Character` 可用 `Game.Combat`。
- 基类改动仅一处、**追加**（`IsAttackHeld` 属性），不触碰 Phase 1 既有生命周期方法。
- 不用 `Debug.Log`，用 `GameLog`；命名 `_camelCase`/`PascalCase`；声明命名空间。
- 热路径零 GC：状态 Update 每帧不 `new`/LINQ/装箱；`Object.Instantiate` 每次放箭一次（非每帧）。
- 不用 Animation Event：放箭用 `normalizedTime ≥ ArrowSpawnTime` 单点 + bool 去重；并用 `shortNameHash == 放箭态` 确保只在放箭态生成。
- Unity 6 API：`Rigidbody.linearVelocity`（Arrow 已用，本阶段不改 Arrow）。
- 编译/Play 由开发者完成。

设计依据：本轮 brainstorm 决议——输入模型 A（代码计时+阈值门控）、专用放箭 clip `Attack01RepeatFire_Bow`、`IsAttackHeld` 入基类、`ChargeAttackDefinition` 做成 SO。

---

## File Structure
- **Modify** `Assets/_Project/Scripts/Character/PlayerControllerBase.cs` — 加 `IsAttackHeld` 属性（Task 1）。
- **Create** `Assets/_Project/Scripts/Combat/ChargeAttackDefinition.cs` — 蓄力数据 SO（Task 2）。
- **Create** `Assets/_Project/Scripts/Character/States/PlayerChargeAttackState.cs` — 蓄力状态（Task 3）。
- **Modify** `Assets/_Project/Scripts/Character/ArcherController.cs` — 输入路由 + 蓄力字段/hash（Task 3）。

每个 Claude 任务：完成 → 静态自检 → 提交。提交编译顺序：Task 1、2 各自独立编译；Task 3 依赖 1+2。

---

## Task 1：基类加 IsAttackHeld 轮询属性

**Files:**
- Modify: `Assets/_Project/Scripts/Character/PlayerControllerBase.cs`

**Interfaces:**
- Produces：`public bool IsAttackHeld`（轮询 `Attack.IsPressed()`）。供 Task 3 路由与蓄力态读取。

- [ ] **Step 1：加属性**

在 `AttackBufferCounter` / `AttackBufferTime` 属性附近加一行：

```csharp
        public bool IsAttackHeld => _inputActions.Player.Attack.IsPressed(); // 攻击键当前是否按住（Phase 4 蓄力轮询用）
```

- [ ] **Step 2：静态自检**
  - 仅追加一个属性，未改任何既有方法/生命周期；`_inputActions` 已是基类字段。
  - `InputAction.IsPressed()` 是 Input System 既有 API。

- [ ] **Step 3：提交**

```bash
git add Assets/_Project/Scripts/Character/PlayerControllerBase.cs
git commit -m "feat(character): expose IsAttackHeld on PlayerControllerBase for charge polling"
```

---

## Task 2：ChargeAttackDefinition 数据 SO

**Files:**
- Create: `Assets/_Project/Scripts/Combat/ChargeAttackDefinition.cs`

**Interfaces:**
- Consumes：`DamageType`（`Game.Combat`）。
- Produces：字段 `DrawStateName`/`MaintainStateName`/`LooseStateName`/`TapThreshold`/`MaxChargeTime`/`MinDamage`/`MaxDamage`/`MinSpeed`/`MaxSpeed`/`ArrowSpawnTime`/`Type`。供 Task 3 读取。
- 编译独立。

- [ ] **Step 1：创建 `ChargeAttackDefinition.cs`**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的蓄力重击定义。三段动画状态名 + 输入/蓄力参数 + 蓄力比例(0~1)到伤害/箭速的线性端点。
    /// 仿 AttackDefinition/ComboDefinition：纯数据，不引用 Animator/Character。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Charge Attack Definition", fileName = "ChargeAttackDefinition")]
    public class ChargeAttackDefinition : ScriptableObject
    {
        [Header("动画状态名 (须与 Animator Controller 节点名精确一致)")]
        public string DrawStateName = "Attack01Start_Bow";        // 拉弓（代码 CrossFade 进入）
        public string MaintainStateName = "Attack01Maintain_Bow"; // 满弓保持（循环；由 Animator HasExitTime 过渡进入，代码不 hash 它）
        public string LooseStateName = "Attack01RepeatFire_Bow";  // 松开放箭（代码 CrossFade 进入）

        [Header("输入 / 蓄力")]
        [Tooltip("按住超过此秒数才进入蓄力；短于此为点按普攻")]
        public float TapThreshold = 0.15f;
        [Tooltip("蓄满所需秒数；超过按满（比例封顶 1）")]
        public float MaxChargeTime = 1.5f;

        [Header("蓄力比例 0~1 线性映射 → 伤害")]
        public float MinDamage = 15f;
        public float MaxDamage = 45f;

        [Header("蓄力比例 0~1 线性映射 → 箭速")]
        public float MinSpeed = 18f;
        public float MaxSpeed = 36f;

        [Header("放箭生成 (归一化动画时间 0~1，RepeatFire 越过此值生成一次蓄力箭)")]
        [Range(0f, 1f)] public float ArrowSpawnTime = 0.3f;

        [Header("伤害类型")]
        public DamageType Type = DamageType.Physical;
    }
}
```

- [ ] **Step 2：静态自检**
  - 纯数据，仅引用 `Game.Combat` 的 `DamageType`；不引用 Animator/Character。
  - `MaintainStateName` 代码不 hash（满弓由 Animator 过渡驱动），保留作开发者配 Animator 的参照。

- [ ] **Step 3：提交**

```bash
git add Assets/_Project/Scripts/Combat/ChargeAttackDefinition.cs
git commit -m "feat(combat): add ChargeAttackDefinition SO (charge tunables + linear damage/speed endpoints)"
```

---

## Task 3：PlayerChargeAttackState + ArcherController 输入路由

**Files:**
- Create: `Assets/_Project/Scripts/Character/States/PlayerChargeAttackState.cs`
- Modify: `Assets/_Project/Scripts/Character/ArcherController.cs`（整文件替换）

**Interfaces:**
- Consumes：Task 1 的 `IsAttackHeld`；Task 2 的 `ChargeAttackDefinition`；既有 `Arrow.Init`、`PlayerControllerBase`、`PlayerStateBase`、`HealthComponent`。
- 必须 Task 1、2 之后。两个文件一起落地才编译（ArcherController `new PlayerChargeAttackState(this)`）。

- [ ] **Step 1：创建 `PlayerChargeAttackState.cs`**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手蓄力重击状态。按住进入：拉弓→满弓保持（循环, 累计蓄力, 封顶）；松开→放箭，
    /// 按蓄力比例(0~1)线性决定伤害/箭速。数据来自 ChargeAttackDefinition，与 PlayerBowAttackState 平行。
    /// 拉弓→保持 由 Animator HasExitTime 过渡驱动（Maintain 循环）；进入(拉弓)/放箭由代码 CrossFade。
    /// </summary>
    public class PlayerChargeAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 放箭动画进度达此值 → 结束
        private const float CrossFadeDuration = 0.1f;

        private readonly ArcherController _archer;

        private float _chargeElapsed; // 已累计蓄力时长（拉弓+保持期间，封顶）
        private bool _released;       // 是否已松手进入放箭阶段
        private bool _arrowSpawned;   // 放箭态是否已生成过箭（单点去重）
        private float _ratio;         // 松手瞬间锁定的蓄力比例 0~1

        public PlayerChargeAttackState(ArcherController player) : base(player)
        {
            _archer = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _chargeElapsed = 0f;
            _released = false;
            _arrowSpawned = false;
            _ratio = 0f;
            _player.AttackBufferCounter = 0f;

            if (_archer.ChargeData == null)
            {
                GameLog.Warn("弓箭手 ChargeAttackDefinition 未配置，无法蓄力", "Combat");
                TransitionToMovement();
                return;
            }

            // CrossFade 拉弓；之后 拉弓→满弓保持 由 Animator HasExitTime 过渡自动流转
            _player.Animator.CrossFadeInFixedTime(_archer.ChargeDrawHash, CrossFadeDuration, 0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement(); // 锁水平移动

            if (!_released)
            {
                // 蓄力累计（拉弓+保持期间），封顶
                float max = _archer.ChargeData.MaxChargeTime;
                _chargeElapsed += Time.deltaTime;
                if (_chargeElapsed > max) _chargeElapsed = max;

                // 松开 → 放箭
                if (!_player.IsAttackHeld)
                    Release();
                return;
            }

            // 放箭阶段：必须确认当前真的在放箭态（CrossFade 过渡中 current 仍是 保持态，避免误判进度）
            AnimatorStateInfo info = _player.Animator.GetCurrentAnimatorStateInfo(0);
            if (info.shortNameHash != _archer.ChargeLooseHash) return;

            float t = info.normalizedTime % 1f;
            if (!_arrowSpawned && t >= _archer.ChargeData.ArrowSpawnTime)
            {
                SpawnChargedArrow(_archer.ChargeData);
                _arrowSpawned = true;
            }
            if (t >= EndThreshold)
                TransitionToMovement();
        }

        public override void Exit()
        {
            _player.AttackBufferCounter = 0f;
        }

        #endregion

        #region 处理流程函数

        private void Release()
        {
            _released = true;
            float max = _archer.ChargeData.MaxChargeTime;
            _ratio = max > 0f ? Mathf.Clamp01(_chargeElapsed / max) : 1f;
            _player.Animator.CrossFadeInFixedTime(_archer.ChargeLooseHash, CrossFadeDuration, 0);
        }

        private void SpawnChargedArrow(ChargeAttackDefinition data)
        {
            if (_archer.ArrowPrefab == null || _archer.ArrowSpawnPoint == null)
            {
                GameLog.Warn("弓箭手 ArrowPrefab/ArrowSpawnPoint 未配置，无法生成箭矢", "Combat");
                return;
            }

            Vector3 dir = _player.transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = _player.transform.forward;
            dir.Normalize();

            Transform sp = _archer.ArrowSpawnPoint;
            GameObject go = Object.Instantiate(_archer.ArrowPrefab, sp.position, Quaternion.LookRotation(dir));
            Arrow arrow = go.GetComponent<Arrow>();
            if (arrow == null)
            {
                GameLog.Warn("ArrowPrefab 上没有 Arrow 组件", "Combat");
                return;
            }

            float damage = Mathf.Lerp(data.MinDamage, data.MaxDamage, _ratio);
            float speed = Mathf.Lerp(data.MinSpeed, data.MaxSpeed, _ratio);
            byte team = _archer.Health != null ? _archer.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            arrow.Init(team, attackerId, damage, data.Type, dir * speed, _player.CharacterController);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            Vector3 velocity = Vector3.up * _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

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

- [ ] **Step 2：整文件替换 `ArcherController.cs`**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手控制器：在 PlayerControllerBase 共享能力之上，叠加远程普通攻击（点按，PlayerBowAttackState）
    /// 与蓄力重击（按住，PlayerChargeAttackState）。攻击输入路由（决策 A：代码计时 + 阈值门控）：
    /// 点按→普攻；按住过 TapThreshold→蓄力，tap 不经拉弓。
    /// </summary>
    public class ArcherController : PlayerControllerBase
    {
        [Header("Bow Attack")] [SerializeField] private ComboDefinition _combo; // 单段连段表（普通攻击 1 段）
        [SerializeField] private GameObject _arrowPrefab;     // 箭矢预制体（带 Rigidbody + Collider + Arrow）
        [SerializeField] private Transform _arrowSpawnPoint;  // 箭矢生成点（弓弦中点）
        [SerializeField] private float _projectileSpeed = 20f; // 普通攻击箭矢初速度

        [Header("Charge Attack")] [SerializeField] private ChargeAttackDefinition _chargeData; // 蓄力重击数据 SO

        private PlayerBowAttackState _bowAttackState;
        private PlayerChargeAttackState _chargeAttackState;
        private int[] _comboStateHashes;
        private HealthComponent _health;       // 阵营来源（缓存）
        private float _attackHeldTime;         // 攻击键按住累计时长（tap/hold 路由）

        // 蓄力动画状态名预 hash（Awake 算一次）；满弓态由 Animator 过渡驱动，代码不 hash
        private int _chargeDrawHash;
        private int _chargeLooseHash;

        public ComboDefinition Combo => _combo;
        public GameObject ArrowPrefab => _arrowPrefab;
        public Transform ArrowSpawnPoint => _arrowSpawnPoint;
        public float ProjectileSpeed => _projectileSpeed;
        public HealthComponent Health => _health;
        public PlayerBowAttackState BowAttackState => _bowAttackState;

        public ChargeAttackDefinition ChargeData => _chargeData;
        public PlayerChargeAttackState ChargeAttackState => _chargeAttackState;
        public int ChargeDrawHash => _chargeDrawHash;
        public int ChargeLooseHash => _chargeLooseHash;

        protected override void Awake()
        {
            base.Awake();
            _health = GetComponent<HealthComponent>();
            _bowAttackState = new PlayerBowAttackState(this);
            _chargeAttackState = new PlayerChargeAttackState(this);
            BuildComboStateHashes();
            BuildChargeHashes();
        }

        /// <summary>
        /// 攻击输入路由（决策 A：代码计时 + 阈值门控，tap 不经拉弓）：
        /// 按住累计时长，过 TapThreshold → 蓄力态；未达阈值松手（或子帧点按）→ 普通攻击点射。
        /// </summary>
        public override bool TryStartAttack()
        {
            // 按住且有蓄力数据：累计时长，过阈值进蓄力
            if (_chargeData != null && IsAttackHeld)
            {
                _attackHeldTime += Time.deltaTime;
                if (_attackHeldTime >= _chargeData.TapThreshold)
                {
                    _attackHeldTime = 0f;
                    AttackBufferCounter = 0f;
                    StateMachine.ChangeState(_chargeAttackState);
                    return true;
                }
                return false; // 仍在 tap 窗口内，按住等待
            }

            // 已松手（或无蓄力数据）：曾有按下 → 普通攻击点射
            bool hadPress = _attackHeldTime > 0f || AttackBufferCounter > 0f;
            _attackHeldTime = 0f;
            if (hadPress)
            {
                AttackBufferCounter = 0f;
                StateMachine.ChangeState(_bowAttackState);
                return true;
            }
            return false;
        }

        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

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
                    GameLog.Warn($"弓箭手连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }

        private void BuildChargeHashes()
        {
            if (_chargeData == null)
            {
                GameLog.Warn("弓箭手 ChargeAttackDefinition 未配置，蓄力重击不可用", "Combat");
                return;
            }
            _chargeDrawHash = HashState(_chargeData.DrawStateName);
            _chargeLooseHash = HashState(_chargeData.LooseStateName);
        }

        private static int HashState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName))
            {
                GameLog.Warn("蓄力动画状态名为空，CrossFade 将无法切换动画", "Combat");
                return 0;
            }
            return Animator.StringToHash(stateName);
        }
    }
}
```

- [ ] **Step 3：静态自检**
  - `PlayerChargeAttackState`：`_player` 拿基类成员（含 `IsAttackHeld`）、`_archer` 拿弓箭手成员；放箭用 `shortNameHash == ChargeLooseHash` 确保只在放箭态、`!_arrowSpawned` 单点去重；`_ratio` 在 `Release` 锁定、`Lerp` 映射伤害/箭速；`Arrow.Init` 调用与签名一致。
  - `ArcherController`：`Awake` 先 `base.Awake()`，建普攻 + 蓄力两态 + 两套 hash；`TryStartAttack` 路由完整（按住计时→过阈值蓄力；松手/子帧→普攻）；普通攻击路径与 Phase 3 行为等价（点按仍触发 `_bowAttackState`）。
  - asmdef：仅 `Game.Character → Game.Combat`。
  - Warrior / `PlayerAttackState` / `PlayerBowAttackState` / `Arrow` 未改。

- [ ] **Step 4：提交**

```bash
git add Assets/_Project/Scripts/Character/States/PlayerChargeAttackState.cs Assets/_Project/Scripts/Character/ArcherController.cs
git commit -m "feat(character): add PlayerChargeAttackState + charge input routing in ArcherController"
```

---

## Task 4：开发者 Editor 配置（不可由 Claude 代劳）

- [ ] **Step 1：确认/补齐蓄力三段状态**
  - `BowHero.controller` 里确认存在 `Attack01Start_Bow`、`Attack01Maintain_Bow`、`Attack01RepeatFire_Bow` 三个状态节点（缺则添加，指派对应 clip）。
  - 将 `Attack01Maintain_Bow` 的 clip 设为 **Loop**（满弓保持循环）。

- [ ] **Step 2：蓄力内部过渡**
  - `Attack01Start_Bow → Attack01Maintain_Bow`：**无条件**，Has Exit Time **开**（Exit Time ~0.9，等拉弓播完），Duration ~0.1。（这是代码不管的那一跳）
  - `Attack01Start_Bow`、`Attack01Maintain_Bow`、`Attack01RepeatFire_Bow` **不**配任何"进入"过渡（拉弓/放箭由代码 CrossFade 进入；满弓由上面这条过渡进入）。

- [ ] **Step 3：放箭退出过渡**
  - `Attack01RepeatFire_Bow → Idle_Bow`：`speed` < 0.1；Has Exit Time **开**，Exit Time `0.85`；Duration `0.15`。
  - `Attack01RepeatFire_Bow → Run_Bow`：`speed` > 0.1；Has Exit Time **开**，Exit Time `0.85`；Duration `0.15`。

- [ ] **Step 4：蓄力数据资产**
  - 建一份 `ChargeAttackDefinition`（菜单 `Game/Combat/Charge Attack Definition`）：三段状态名填 `Attack01Start_Bow`/`Attack01Maintain_Bow`/`Attack01RepeatFire_Bow`；`TapThreshold`（~0.15）、`MaxChargeTime`（~1.5）、`MinDamage`/`MaxDamage`（如 15/45）、`MinSpeed`/`MaxSpeed`（如 18/36）、`ArrowSpawnTime`（对准放箭那帧）、`Type=Physical`。

- [ ] **Step 5：挂到 ArcherController（场景 BowPlayer）**
  - `_chargeData` = Step 4 的资产。其余 Phase 3 字段（`_combo`/`_arrowPrefab`/`_arrowSpawnPoint`/`_projectileSpeed`）保持已配。

---

## Task 5：Play 验收 + 提交资产（验收开发者做，提交 Claude 做）

- [ ] **Step 1：编译**：聚焦 Unity，Console 无报错。
- [ ] **Step 2：Play 验收**
  1. **点按**左键 → 仍是 Phase 3 普通攻击（不显示拉弓），发一支普通箭
  2. **按住**超过 TapThreshold → 进入拉弓→满弓保持（循环），角色锁水平移动
  3. 松开 → 播 `Attack01RepeatFire_Bow`，在 ArrowSpawnTime 发**一支**蓄力箭
  4. 蓄得越久，箭伤害/箭速越高（满弓封顶）；刚过阈值就松 → 接近最小值
  5. 蓄力箭命中 Enemy → 扣血更多（对比普通箭）
  6. 放箭播完回到 Idle/Run（不卡姿势）
  7. Profiler：每次放箭一次 Instantiate，状态 Update 每帧零 GC
- [ ] **Step 3：告知 Claude 结果**：通过 → Claude 提交资产；不通过 → 描述现象排查。

---

## Task 6：提交资产改动（Claude，在开发者验收通过后）

**Files:** `ChargeAttackDefinition` 资产（+`.meta`）、`BowHero.controller`、`SampleScene.unity`（_chargeData 赋值）、新脚本 `.meta`。

- [ ] **Step 1：核对范围**

```bash
git status --short
```
Expected：新增 ChargeAttackDefinition 资产 + `.meta`；`BowHero.controller`、`SampleScene.unity` 有改动；新脚本 `.meta`；**无**意外 `.cs` 改动；Warrior 资产未动。

- [ ] **Step 2：提交**

```bash
git add Assets/_Project/ScriptableObjects Assets/_Project/Art/Animators/BowHero.controller Assets/_Project/Scenes/SampleScene.unity Assets/_Project/Scripts/Character/States/PlayerChargeAttackState.cs.meta Assets/_Project/Scripts/Combat/ChargeAttackDefinition.cs.meta
git commit -m "feat(character): wire Archer charge-attack assets (ChargeAttackDefinition, charge anim transitions) (Phase 4)"
```

通过后 Archer 四阶段（基类重构 / 移动 / 普攻+箭矢 / 蓄力重击）全部完成。
