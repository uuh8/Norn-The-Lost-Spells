# 弓箭手普通攻击 + 箭矢系统（Archer Phase 3）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) 或 superpowers:executing-plans 逐任务实现。步骤用 checkbox 跟踪。Unity 编译/Play 由开发者手动完成；Claude 不声称"能跑"，只保证静态逻辑正确。

**Goal:** 弓箭手点按左键 → 播放 `Attack01_Bow` 单段攻击动画 → 在 `ArrowSpawnTime` 那一刻生成一支沿朝向飞出的物理弹道箭矢 → 命中敌方经现有 `IDamageable`/`DamageRequest` 结算伤害。

**Architecture:** 平行新状态类 `PlayerBowAttackState`（不复用焊死在 Warrior 上的 `PlayerAttackState`），复用 `ComboDefinition`/`ComboResolver`/`AttackDefinition`（1 段连段，永远走向 End）。箭矢用单个 `Arrow`（`Game.Combat`，Rigidbody 弹道 + `OnCollisionEnter` 命中提交），与 `MeleeHitDetector` 平级、复用同一套伤害提交路径。生成触发用 `normalizedTime` 越过 `AttackDefinition.ArrowSpawnTime` 单点 + 一个 bool 去重（不引入 Animation Event）。

**Tech Stack:** Unity 6.3 / C# / `Game.Combat`（Arrow、AttackDefinition）/ `Game.Character`（依赖 Combat：ArcherController、PlayerBowAttackState）/ `CharacterController` / Rigidbody 物理。

## Global Constraints
- **asmdef 方向**：`Game.Combat` 不得引用 `Game.Character`（`Arrow.cs` 只用 Combat 类型）；`Game.Character` 可用 `Game.Combat`。
- **Warrior 零改动**：`PlayerController`/`WarriorController`/`PlayerAttackState` 一行不动。
- **不用 Debug.Log**，用 `Game.Core.GameLog`；命名 `_camelCase`/`PascalCase`；每脚本声明命名空间。
- **热路径零 GC**：状态 Update 每帧不 `new`/LINQ/装箱；`Object.Instantiate` 只在攻击的单点触发一次（非每帧），可接受。
- **不用 Animation Event**：箭矢触发统一用 `normalizedTime` 阈值 + bool 去重（与命中窗口/拖尾同机制）。
- **Unity 6 API**：`Rigidbody.linearVelocity`（不是 `velocity`）。
- 编译/Play 由开发者完成。

设计依据：本轮 brainstorm 决议（用户已确认）——平行类 `PlayerBowAttackState`、单个 `Arrow` + `OnCollisionEnter` + 命中/超时即销毁、沿 `transform.forward` 发射、阵营取自 `HealthComponent`。

---

## File Structure
- **Modify** `Assets/_Project/Scripts/Combat/AttackDefinition.cs` — 加 `ArrowSpawnTime` 单点字段（Task 1）。
- **Create** `Assets/_Project/Scripts/Combat/Arrow.cs` — 飞行箭矢 MonoBehaviour（Task 2）。
- **Create** `Assets/_Project/Scripts/Character/States/PlayerBowAttackState.cs` — 弓箭手普通攻击状态（Task 3）。
- **Modify** `Assets/_Project/Scripts/Character/ArcherController.cs` — 充实：连段/箭矢字段 + 攻击 seam（Task 3）。

每个 Claude 任务：完成代码 → 静态自检 → 提交。每个提交均可独立编译（见各任务依赖说明）。

---

## Task 1：AttackDefinition 加 ArrowSpawnTime 单点字段

**Files:**
- Modify: `Assets/_Project/Scripts/Combat/AttackDefinition.cs`

**Interfaces:**
- Produces：`public float ArrowSpawnTime`（`[Range(0,1)]`，默认 0.4f）。供 Task 3 的 `PlayerBowAttackState` 读取。

- [ ] **Step 1：插入字段**

在 `连段输入窗口` 块与 `[Header("动画")]` 之间插入：

```csharp
        [Header("远程箭矢生成 (归一化动画时间 0~1，单点：normalizedTime 越过此值的那一刻生成一次箭矢；近战不用此字段)")]
        [Range(0f, 1f)] public float ArrowSpawnTime = 0.4f;
```

即改成（上下文）：

```csharp
        [Header("连段输入窗口 (归一化动画时间 0~1，此区间内按攻击键可接下一段)")]
        [Range(0f, 1f)] public float ComboInputStart = 0.40f;
        [Range(0f, 1f)] public float ComboInputEnd   = 0.70f;

        [Header("远程箭矢生成 (归一化动画时间 0~1，单点：normalizedTime 越过此值的那一刻生成一次箭矢；近战不用此字段)")]
        [Range(0f, 1f)] public float ArrowSpawnTime = 0.4f;

        [Header("动画")]
```

- [ ] **Step 2：静态自检**
  - 字段为 `public`、`[Range(0,1)]`、默认 0.4f；纯数据，不引用 Animator/Character。
  - 该改动是**追加**——Warrior 的 AttackDefinition 资产读不到此字段也无影响（近战不使用）。

- [ ] **Step 3：提交**

```bash
git add Assets/_Project/Scripts/Combat/AttackDefinition.cs
git commit -m "feat(combat): add AttackDefinition.ArrowSpawnTime single-point field"
```

---

## Task 2：Arrow 飞行箭矢

**Files:**
- Create: `Assets/_Project/Scripts/Combat/Arrow.cs`

**Interfaces:**
- Consumes：`IDamageable`、`DamageRequest`、`DamageType`（均 `Game.Combat`，已存在）。
- Produces：`public void Init(byte attackerTeam, int attackerId, float damage, DamageType type, Vector3 velocity, Collider shooterCollider)`。供 Task 3 生成箭矢时调用。
- 编译独立：只依赖既有 Combat 类型，不依赖 Task 1/3。

- [ ] **Step 1：创建 `Arrow.cs`**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 飞行箭矢。与 MeleeHitDetector 平级：复用 IDamageable/DamageRequest 的伤害提交路径，
    /// 区别只在"如何发现命中"——飞行碰撞（OnCollisionEnter），而非瞬时 OverlapBox。
    /// 生成时由攻击方 Init 注入伤害快照与初速度；命中敌方结算并销毁，命中环境直接销毁，
    /// 命中同阵营（射手自身/队友）则穿过；超时自毁兜底。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class Arrow : MonoBehaviour
    {
        [SerializeField] private float _maxLifetime = 5f; // 超时自毁，防漏网箭矢累积

        private Rigidbody _rb;
        private Collider _collider;

        // 伤害快照（Init 注入；命中时构造 DamageRequest）
        private byte _attackerTeam;
        private int _attackerId;
        private float _damage;
        private DamageType _type;

        private bool _consumed; // 防同一物理步多次碰撞重复结算/重复销毁

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
        }

        /// <summary>
        /// 攻击方在生成瞬间调用：注入伤害快照与初速度，忽略与射手自身碰撞，定向、计时。
        /// </summary>
        public void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                         Vector3 velocity, Collider shooterCollider)
        {
            _attackerTeam = attackerTeam;
            _attackerId = attackerId;
            _damage = damage;
            _type = type;

            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_collider == null) _collider = GetComponent<Collider>();

            // 忽略与射手自身碰撞，避免出膛瞬间撞到射手 collider 即自毁
            if (shooterCollider != null && _collider != null)
                Physics.IgnoreCollision(_collider, shooterCollider);

            _rb.linearVelocity = velocity; // Unity 6：Rigidbody.velocity → linearVelocity
            if (velocity.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(velocity);

            Destroy(gameObject, _maxLifetime);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_consumed) return;

            IDamageable target = collision.collider.GetComponentInParent<IDamageable>();

            // 同阵营（射手自身/队友）→ 穿过，不结算不销毁
            if (target != null && target.TeamId == _attackerTeam)
                return;

            // 敌方且存活 → 结算一次伤害
            if (target != null && target.IsAlive)
            {
                Vector3 hitPoint = collision.GetContact(0).point;
                Vector3 vel = _rb != null ? _rb.linearVelocity : Vector3.zero;
                Vector3 hitDir = vel.sqrMagnitude > 1e-6f ? vel.normalized : transform.forward;
                var req = new DamageRequest(_attackerId, _attackerTeam, _damage, _type, hitPoint, hitDir);
                target.ReceiveHit(in req);
            }

            // 命中敌方 / 死敌 / 环境（无 IDamageable）→ 销毁
            _consumed = true;
            Destroy(gameObject);
        }
    }
}
```

- [ ] **Step 2：静态自检**
  - 只引用 Combat 类型（无 Character/Animator/InputSystem 引用）→ 不违反 asmdef 方向。
  - 用 `linearVelocity`（非 `velocity`）；`collision.GetContact(0)` 不分配（不用 `collision.contacts`）。
  - `OnCollisionEnter` 非每帧热路径（离散碰撞触发）；`GetComponentInParent` 与 MeleeHitDetector 同款，可接受。
  - 同阵营穿过 + `_consumed` 去重 + 超时自毁 三条防御齐备。

- [ ] **Step 3：提交**

```bash
git add Assets/_Project/Scripts/Combat/Arrow.cs
git commit -m "feat(combat): add Arrow projectile (Rigidbody ballistics + OnCollisionEnter hit submission)"
```

---

## Task 3：ArcherController 充实 + PlayerBowAttackState

**Files:**
- Create: `Assets/_Project/Scripts/Character/States/PlayerBowAttackState.cs`
- Modify: `Assets/_Project/Scripts/Character/ArcherController.cs`（整文件替换空骨架）

**Interfaces:**
- Consumes：Task 1 的 `AttackDefinition.ArrowSpawnTime`；Task 2 的 `Arrow.Init(...)`；既有 `ComboDefinition`/`ComboResolver`/`ComboDecision`/`HealthComponent`/`PlayerControllerBase`/`PlayerStateBase`。
- 必须 Task 1、2 之后做（依赖其类型）。两个文件一起落地才编译（ArcherController `new PlayerBowAttackState(this)`）。

- [ ] **Step 1：创建 `PlayerBowAttackState.cs`**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手普通攻击状态（远程）。与 PlayerAttackState 同骨架（CrossFade 单段 + normalizedTime +
    /// ComboResolver 走向 End + 回移动），但"活动期"是在 ArrowSpawnTime 单点生成一支箭矢，
    /// 而非 OverlapBox 命中窗口；无连段推进（1 段）、无刀光。平行实现，不复用 Warrior 的 PlayerAttackState。
    /// </summary>
    public class PlayerBowAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 动画进度达此值 → 结束（1 段永不 Advance）
        private const float CrossFadeDuration = 0.1f; // 进入攻击段 CrossFade 固定时长

        private readonly ArcherController _archer; // typed 子类引用，拿弓箭手专属成员

        private int _comboIndex;
        private bool _arrowSpawned; // 本次播放是否已生成过箭矢（单点越阈触发一次的去重位）

        public PlayerBowAttackState(ArcherController player) : base(player)
        {
            _archer = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 消耗起手输入
            _arrowSpawned = false;

            if (_archer.Combo == null || _archer.Combo.SegmentCount == 0)
            {
                GameLog.Warn("弓箭手 ComboDefinition 未配置或无段落，无法攻击", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement();   // 锁水平移动，只保留垂直速度
            HandleArrowSpawn(); // 单点：normalizedTime 越过 ArrowSpawnTime 生成一次
            CheckCombo();       // 1 段 → 永远走向 End
        }

        public override void Exit()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 清残留，防攻击结束后误触发
        }

        #endregion

        #region 处理流程函数

        private void StartSegment(int index)
        {
            _arrowSpawned = false; // 新段重置去重位
            int hash = _archer.GetComboStateHash(index);
            _player.Animator.CrossFadeInFixedTime(hash, CrossFadeDuration, 0);
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

        private void HandleArrowSpawn()
        {
            if (_arrowSpawned) return;
            if (_player.Animator.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信

            AttackDefinition seg = _archer.Combo.Segments[_comboIndex];
            if (seg == null) return;

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= seg.ArrowSpawnTime)
            {
                SpawnArrow(seg);
                _arrowSpawned = true;
            }
        }

        private void SpawnArrow(AttackDefinition seg)
        {
            if (_archer.ArrowPrefab == null || _archer.ArrowSpawnPoint == null)
            {
                GameLog.Warn("弓箭手 ArrowPrefab/ArrowSpawnPoint 未配置，无法生成箭矢", "Combat");
                return;
            }

            // 沿角色当前朝向发射（轨道相机风格，无准星瞄准——本阶段简化）
            Vector3 dir = _player.transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = _player.transform.forward; // 退化兜底（直立角色不会发生）
            dir.Normalize();

            Transform sp = _archer.ArrowSpawnPoint;
            GameObject go = Object.Instantiate(_archer.ArrowPrefab, sp.position, Quaternion.LookRotation(dir));

            Arrow arrow = go.GetComponent<Arrow>();
            if (arrow == null)
            {
                GameLog.Warn("ArrowPrefab 上没有 Arrow 组件", "Combat");
                return;
            }

            byte team = _archer.Health != null ? _archer.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            Vector3 velocity = dir * _archer.ProjectileSpeed;
            arrow.Init(team, attackerId, seg.BaseAmount, seg.Type, velocity, _player.CharacterController);
        }

        private void CheckCombo()
        {
            if (_player.Animator.IsInTransition(0)) return;

            AttackDefinition seg = _archer.Combo.Segments[_comboIndex];
            if (seg == null)
            {
                GameLog.Warn($"弓箭手连段第 {_comboIndex} 段未赋值，中断", "Combat");
                TransitionToMovement();
                return;
            }

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            bool hasBuffer = _player.AttackBufferCounter > 0f;

            ComboDecision decision = ComboResolver.Resolve(
                _comboIndex, _archer.Combo.SegmentCount, t, hasBuffer,
                seg.ComboInputStart, seg.ComboInputEnd, EndThreshold);

            switch (decision)
            {
                case ComboDecision.Advance:
                    // 1 段配置下 hasNext 恒 false，永不进此分支；保留以与骨架结构一致
                    _comboIndex++;
                    _player.AttackBufferCounter = 0f;
                    StartSegment(_comboIndex);
                    break;
                case ComboDecision.End:
                    TransitionToMovement();
                    break;
                // ComboDecision.Continue: 维持，无操作
            }
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

- [ ] **Step 2：整文件替换 `ArcherController.cs`**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手控制器：在 PlayerControllerBase 共享能力之上，叠加弓箭手专属的远程普通攻击
    /// （单段 ComboDefinition + PlayerBowAttackState + 箭矢生成）。蓄力重击在 Phase 4。
    /// </summary>
    public class ArcherController : PlayerControllerBase
    {
        [Header("Bow Attack")] [SerializeField] private ComboDefinition _combo; // 单段连段表（普通攻击 1 段）
        [SerializeField] private GameObject _arrowPrefab;     // 箭矢预制体（带 Rigidbody + Collider + Arrow）
        [SerializeField] private Transform _arrowSpawnPoint;  // 箭矢生成点（弓弦中点，BowHero 下的 ArrowSpawnPoint）
        [SerializeField] private float _projectileSpeed = 20f; // 箭矢初速度

        private PlayerBowAttackState _bowAttackState;
        private int[] _comboStateHashes;     // 连段各段 Animator 状态名预 hash（Awake 算一次）
        private HealthComponent _health;      // 阵营来源（缓存，避免每次攻击 GetComponent）

        public ComboDefinition Combo => _combo;
        public GameObject ArrowPrefab => _arrowPrefab;
        public Transform ArrowSpawnPoint => _arrowSpawnPoint;
        public float ProjectileSpeed => _projectileSpeed;
        public PlayerBowAttackState BowAttackState => _bowAttackState;
        public HealthComponent Health => _health;

        protected override void Awake()
        {
            base.Awake();
            _health = GetComponent<HealthComponent>();
            _bowAttackState = new PlayerBowAttackState(this);
            BuildComboStateHashes();
        }

        /// <summary>攻击 seam 实现：有缓冲攻击输入则切到弓箭普通攻击态（Phase 3 点按即射；蓄力在 Phase 4）。</summary>
        public override bool TryStartAttack()
        {
            if (AttackBufferCounter > 0f)
            {
                StateMachine.ChangeState(_bowAttackState);
                return true;
            }
            return false;
        }

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>把连段表各段 AnimationStateName 预 hash 成 int[]（仿 WarriorController，平行实现）。</summary>
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
    }
}
```

- [ ] **Step 3：静态自检**
  - `PlayerBowAttackState`：`_player`（基类）拿共享成员、`_archer` 拿弓箭手成员；`HandleArrowSpawn` 用 `_arrowSpawned` 单点去重、过渡期跳过；`SpawnArrow` 用 `transform.forward`、阵营取自 `_archer.Health`、shooter collider 传 `_player.CharacterController`（`CharacterController : Collider`，合法）。
  - `ArcherController`：`Awake` 先 `base.Awake()`；`new PlayerBowAttackState(this)` 与构造参数 `ArcherController` 类型一致；`TryStartAttack` 重写与 Warrior 同形。
  - asmdef：仅 `Game.Character → Game.Combat` 单向引用（Arrow/AttackDefinition/ComboResolver/HealthComponent），无反向。
  - Warrior 文件未被触碰。

- [ ] **Step 4：提交**

```bash
git add Assets/_Project/Scripts/Character/States/PlayerBowAttackState.cs Assets/_Project/Scripts/Character/ArcherController.cs
git commit -m "feat(character): add PlayerBowAttackState + fill ArcherController (ranged normal attack, spawns Arrow)"
```

---

## Task 4：开发者 Editor 配置（不可由 Claude 代劳）

- [ ] **Step 1：箭矢生成点**
  - 在 `BowHero`（或其武器挂点）下新建空子物体 `ArrowSpawnPoint`，手动摆到弓弦中点、朝向角色前方。**不复用**已有装备挂点（与战士 `VFX_BladeTip` 同类解耦操作）。

- [ ] **Step 2：箭矢预制体**
  - 新建 Arrow 预制体（箭矢模型或占位体）：加 `Rigidbody`（Use Gravity 开、Is Kinematic 关、**Collision Detection = Continuous** 防穿透）、一个 `Collider`（**非 Trigger**，贴合箭体）、`Arrow` 脚本（按需调 `_maxLifetime`）。
  - 给箭矢单独建层（可选，便于将来 LayerMask 管理）。

- [ ] **Step 3：攻击数据资产**
  - 建一份 `AttackDefinition`（菜单 `Game/Combat/Attack Definition`）：`AnimationStateName = "Attack01_Bow"`、`BaseAmount`（如 15）、`Type = Physical`、`ArrowSpawnTime`（如 0.4，对准动画"放箭"那一帧）。命中盒/命中窗口/连段窗口字段对远程无意义，可留默认。
  - 建一份 `ComboDefinition`（菜单 `Game/Combat/Combo Definition`）：`Segments` 长度 1，第 0 段指向上面的 AttackDefinition。

- [ ] **Step 4：Animator 退出过渡（`Attack01_Bow`）**
  - 与 Dash/locomotion 同理（进入由代码 CrossFade，退出需状态自身出边）。在 `BowHero.controller` 加：
    - `Attack01_Bow → Idle_Bow`：条件 `speed` < 0.1；Has Exit Time **开**，Exit Time `0.85`；Duration `0.15`。
    - `Attack01_Bow → Run_Bow`：条件 `speed` > 0.1；Has Exit Time **开**，Exit Time `0.85`；Duration `0.15`。
  - 不给 `Attack01_Bow` 配任何"进入"过渡（进入由 `CrossFadeInFixedTime` 完成）。

- [ ] **Step 5：ArcherController 字段（场景中的 BowPlayer）**
  - `_combo` = Step 3 的 ComboDefinition；`_arrowPrefab` = Step 2 的 Arrow 预制体；`_arrowSpawnPoint` = Step 1 的 ArrowSpawnPoint；`_projectileSpeed` 起步 20。
  - 左键攻击输入已绑定（基类 `OnAttackPerformed` → `AttackBufferCounter`），无需改输入。

- [ ] **Step 6：靶子**
  - 确认场景里的 Enemy 有 `HealthComponent`（敌对 `TeamId`，与弓箭手不同）+ 一个 Collider，方便验证命中。

---

## Task 5：Play 验收 + 提交资产（验收开发者做，提交 Claude 做）

- [ ] **Step 1：编译**
  - 聚焦 Unity，Console 无报错。

- [ ] **Step 2：Play 验收**
  1. 左键 → 弓箭手播放 `Attack01_Bow`，攻击期间锁水平移动
  2. 动画进度到 `ArrowSpawnTime` 那一刻 **生成一支箭**（不是每帧生成、不是 0 支）
  3. 箭沿角色朝向飞出、受重力呈抛物线
  4. 命中 Enemy → Enemy 扣血（`CombatDebugLogger` 打出 DamageReceived/Death），箭销毁
  5. 箭命中墙/地面 → 销毁；**不会**一出膛就撞到射手自身（无自伤、不自毁）
  6. 攻击播完回到 Idle/Run（不卡攻击姿势）
  7. Profiler：攻击瞬间只有一次箭矢 Instantiate，状态 Update 每帧零 GC
- [ ] **Step 3：告知 Claude 结果**
  - 通过 → Claude 执行 Task 6 提交资产；不通过 → 把现象告诉 Claude 排查。

---

## Task 6：提交资产改动（Claude，在开发者验收通过后）

**Files:** Arrow 预制体、AttackDefinition/ComboDefinition 资产、`BowHero.controller`、`BowHero` 与 `BowPlayer`（ArrowSpawnPoint/字段）所在的预制体或 `SampleScene.unity`、相关 `.meta`。

- [ ] **Step 1：核对范围**

```bash
git status --short
```
Expected：新增 Arrow 预制体 + 两个 SO 资产 + 其 `.meta`；`BowHero.controller`、场景/预制体有改动；**无**意外 `.cs` 改动；Warrior 资产未动。

- [ ] **Step 2：提交**

```bash
git add Assets/_Project/Art Assets/_Project/ScriptableObjects Assets/_Project/Scenes/SampleScene.unity
git commit -m "feat(character): wire Archer normal attack assets (arrow prefab, attack SO, Attack01_Bow exit) (Phase 3)"
```

通过后转入 Phase 4（蓄力重击：长按输入 + PlayerChargeAttackState + 蓄力比例映射伤害/箭速）。
