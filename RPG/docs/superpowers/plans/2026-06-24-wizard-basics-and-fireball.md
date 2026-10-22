# 法师（Wizard）基础操作 + 火球普攻 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让法师角色拥有与战士/弓箭手同级的基础操作（行走/跳跃/冲刺由基类复用），并新增「点按发射火球」的远程普攻——火球命中后爆炸、对目标结算直击伤害，并附加持续燃烧（DoT）+ OnFire 特效。

**Architecture:** 法师与弓箭手同构：`WizardController : PlayerControllerBase` 复用全部移动能力，仅叠加远程普攻；普攻态 `PlayerWizardAttackState`（纯 C# 状态，仿 `PlayerBowAttackState`）在动画单点生成发射物。发射物 `Fireball`（`Game.Combat`，仿 `Arrow`）命中后：①生成 `Fireball_Explosion` 爆炸特效；②走 `IDamageable.ReceiveHit` 标准路径结算直击伤害；③在目标上挂 `BurnStatus`（新 DoT 组件）周期性扣血并附 `OnFire` 特效。整条伤害链与近战/箭矢复用同一套 `DamageRequest`/`HealthComponent`/事件。

**Tech Stack:** Unity 6.3 / URP 17.3 / C# / 现有 `Game.Combat`（`IDamageable`、`HealthComponent`、`DamagePipeline`、`DamageRequest`、`AttackDefinition`、`ComboDefinition`、`ComboResolver`）与 `Game.Character`（`PlayerControllerBase`、`PlayerStateBase`、状态机）。

## Global Constraints（项目级硬约束，每个任务都隐含适用）

- **程序集隔离**：`Game.Combat` **不得引用** `Game.Character`/`Game.Skills`/`Game.Rendering`；`Game.Character` 可引用 `Game.Core` + `Game.Combat`。新增 `Fireball`/`BurnStatus` 放 `Game.Combat`，`WizardController`/`PlayerWizardAttackState` 放 `Game.Character`。
- **命名空间必须匹配程序集**：`Game.Combat` / `Game.Character`。
- **命名规范**：私有/保护字段 `_camelCase`；公开成员与类型 `PascalCase`；接口 `IXxx`。
- **零 GC 热路径**：`Update`/`FixedUpdate`/每帧路径内禁止 `new`、LINQ、装箱。离散输入（命中瞬间 `Instantiate` 火球/特效、`AddComponent`）一次性分配可接受。`DamageRequest` 是 `struct`，按值传递不分配。
- **日志**：只用 `GameLog.Info/Warn/Error`，**绝不** `Debug.Log`。
- **Animator 进入靠代码、退出靠连线**：攻击/冲刺态用 `CrossFadeInFixedTime(hash,…)` 代码进入（状态名数据驱动、`Awake` 预 hash）；这些 Animator 状态**必须自带退出过渡**，否则角色卡姿势。错误/空状态名 `CrossFade` 静默失效（空名记 0 + `GameLog.Warn`）。
- **Claude 只改 `.cs` 与纯文本配置**；**编译、Animator 连线、Inspector 赋值、Play 模式测试由开发者在 Unity Editor 手动完成**——本计划中所有「验证」步骤即开发者在 Editor 的动作，不存在自动化测试管线（项目无 MonoBehaviour 计时的自动化测试；现有 EditMode 测试只覆盖纯函数 `DamagePipeline`）。不要断言「能跑」，只断言静态逻辑正确。
- **不要为新资产建 `.meta`**（Unity 自动生成）。`WizardController.cs`/`PlayerWizardAttackState.cs` 已存在（含各自 `.meta`），本计划是**重写其内容**而非重命名，GUID 不变——务必用编辑而非删除重建，且确认 `PlayerWizardAttackState` 当前未作为组件挂在任何 GameObject 上（它将从 MonoBehaviour 改为纯 C# 类）。

---

## 现状与文件结构

**已存在但为残缺脚手架（需重写）：**
- `Assets/_Project/Scripts/Character/WizardController.cs` — 引用了不存在的 `BuildComboStateHashes()`/`BuildChargeHashes()`、含多余蓄力字段、空 `Start/Update`，无法编译。
- `Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs` — 是默认 `MonoBehaviour` 模板、**无命名空间**，与状态机骨架完全不符。

**本计划涉及文件：**
| 文件 | 程序集 | 动作 | 职责 |
|---|---|---|---|
| `Assets/_Project/Scripts/Combat/BurnStatus.cs` | Game.Combat | 新增 | 持续燃烧 DoT 组件：周期 `ReceiveHit` 扣血 + 挂/销毁 OnFire 特效，到时自毁 |
| `Assets/_Project/Scripts/Combat/Fireball.cs` | Game.Combat | 新增 | 飞行火球：仿 `Arrow`，命中后爆炸 + 直击伤害 + 附加 `BurnStatus` |
| `Assets/_Project/Scripts/Character/WizardController.cs` | Game.Character | 重写 | 法师子类：复用移动，叠加点按火球普攻；连段 hash 缓存；`TryStartAttack` |
| `Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs` | Game.Character | 重写 | 法师普攻态（纯 C#，仿 `PlayerBowAttackState`），单点生成火球 |

**依赖顺序**：`BurnStatus`（Task 1）→ `Fireball` 依赖它（Task 2）→ `WizardController`（Task 3）与 `PlayerWizardAttackState`（Task 4）互相引用、同程序集一起编译 → Editor 接线与 Play 验证（Task 5）。

---

### Task 1: BurnStatus —— 持续燃烧 DoT 组件

**Files:**
- Create: `Assets/_Project/Scripts/Combat/BurnStatus.cs`

**Interfaces:**
- Consumes: `IDamageable`（同物体获取，`IsAlive`/`ReceiveHit`）、`DamageRequest(int,byte,float,DamageType,Vector3,Vector3)`、`DamageType`。
- Produces: `public void Apply(int attackerId, byte attackerTeam, float damagePerTick, DamageType type, float interval, float duration, GameObject onFirePrefab)` —— Task 2 的 `Fireball` 会调用它。

- [ ] **Step 1: 写 `BurnStatus.cs`**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 持续燃烧状态（DoT）。火球命中后由 Fireball 附加到目标身上：
    /// 每隔 _interval 秒对目标结算一次 _damagePerTick 点伤害，持续 duration 秒后自动移除；
    /// 期间在目标身上挂一个 OnFire 特效（结束时销毁）。重复命中刷新持续时间（不叠加第二份特效）。
    /// 伤害仍走 IDamageable.ReceiveHit 标准路径，故与火球直击/近战/箭矢复用同一套结算与事件。
    /// </summary>
    public class BurnStatus : MonoBehaviour
    {
        private IDamageable _target;     // 同物体上的可受击目标（缓存）

        // 燃烧快照（Apply 注入；攻击者可能已销毁，故按值保存）
        private int _attackerId;
        private byte _attackerTeam;
        private float _damagePerTick;
        private DamageType _type;
        private float _interval;

        private float _remaining;        // 剩余燃烧时间
        private float _tickTimer;        // 距下一次结算的计时
        private GameObject _vfx;         // 挂在目标身上的 OnFire 特效实例
        private bool _active;

        private void Awake()
        {
            _target = GetComponent<IDamageable>();
        }

        /// <summary>
        /// 施加/刷新燃烧。首次调用生成 OnFire 特效并开始计时；重复调用刷新持续时间与参数（不再生成第二份特效）。
        /// </summary>
        public void Apply(int attackerId, byte attackerTeam, float damagePerTick, DamageType type,
                          float interval, float duration, GameObject onFirePrefab)
        {
            _attackerId    = attackerId;
            _attackerTeam  = attackerTeam;
            _damagePerTick = damagePerTick;
            _type          = type;
            _interval      = Mathf.Max(0.05f, interval); // 防 0/负间隔导致每帧结算
            _remaining     = duration;

            if (!_active)
            {
                _active = true;
                _tickTimer = _interval; // 首跳延迟一个 interval（直击伤害已在命中帧结算）
                if (onFirePrefab != null && _vfx == null)
                    _vfx = Object.Instantiate(onFirePrefab, transform.position, transform.rotation, transform);
            }
        }

        private void Update()
        {
            if (!_active) return;

            // 目标已死亡：停止燃烧并清理（ReceiveHit 对死者本就早退，这里负责收尾特效与自毁）
            if (_target == null || !_target.IsAlive) { Stop(); return; }

            float dt = Time.deltaTime;
            _remaining -= dt;
            _tickTimer -= dt;

            if (_tickTimer <= 0f)
            {
                _tickTimer += _interval;
                var req = new DamageRequest(_attackerId, _attackerTeam, _damagePerTick,
                                            _type, transform.position, Vector3.up);
                _target.ReceiveHit(in req);
            }

            if (_remaining <= 0f) Stop();
        }

        private void Stop()
        {
            _active = false;
            if (_vfx != null) Destroy(_vfx);
            Destroy(this); // 移除自身组件（燃烧结束）
        }
    }
}
```

- [ ] **Step 2: 静态自检**

确认：命名空间 `Game.Combat`；字段 `_camelCase`；无 `Debug.Log`；`Update` 内无 `new`/LINQ（`DamageRequest` 为 struct，不分配）；`Apply` 的离散 `Instantiate` 可接受。无需引用任何上层程序集。

- [ ] **Step 3: 开发者在 Unity Editor 验证编译**

切到 Unity，等待编译。Expected: Console 无 `BurnStatus` 相关报错（注意此时 `Fireball` 尚未创建，但 `BurnStatus` 不依赖它，应独立通过编译）。

- [ ] **Step 4: Commit（用户确认后）**

```bash
git add "Assets/_Project/Scripts/Combat/BurnStatus.cs"
git commit -m "feat(combat): add BurnStatus DoT (periodic ReceiveHit + OnFire vfx)"
```

---

### Task 2: Fireball —— 飞行火球（爆炸 + 直击 + 引燃）

**Files:**
- Create: `Assets/_Project/Scripts/Combat/Fireball.cs`

**Interfaces:**
- Consumes: `IDamageable`、`HealthComponent`、`DamageRequest`、`DamageType`、`BurnStatus.Apply(...)`（Task 1）。
- Produces: `public void Init(byte attackerTeam, int attackerId, float damage, DamageType type, Vector3 velocity, Collider casterCollider, bool useGravity = false)` —— Task 4 的普攻态会调用它（签名与 `Arrow.Init` 平行，但 `useGravity` 默认 `false` 走直线）。

- [ ] **Step 1: 写 `Fireball.cs`**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 飞行火球。与 Arrow 平级：复用 IDamageable/DamageRequest 的伤害提交路径，区别在命中后的表现——
    /// 命中时：① 在命中点生成爆炸特效(Fireball_Explosion)；② 对目标结算一次直击伤害；
    /// ③ 给目标附加持续燃烧(BurnStatus + OnFire)。
    /// 生成时由施法方 Init 注入伤害快照与初速度；命中敌方结算并销毁，命中环境直接销毁(仍放爆炸)，
    /// 命中同阵营(施法者自身/队友)则穿过；超时自毁兜底。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class Fireball : MonoBehaviour
    {
        [Header("命中表现")]
        [SerializeField] private GameObject _explosionPrefab;   // Fireball_Explosion，命中点生成
        [SerializeField] private float _explosionLifetime = 2f; // 爆炸特效存活时间后销毁

        [Header("燃烧 (DoT)")]
        [SerializeField] private GameObject _onFirePrefab;      // OnFire，附着到目标
        [SerializeField] private float _burnDamagePerTick = 3f; // 每跳伤害
        [SerializeField] private float _burnInterval = 0.5f;    // 跳与跳的间隔(秒)
        [SerializeField] private float _burnDuration = 3f;      // 燃烧总时长(秒)

        [Header("飞行")]
        [SerializeField] private float _maxLifetime = 5f;       // 超时自毁，防漏网火球累积
        // 模型朝向修正：LookRotation 把 +Z 对到飞行方向；火球多为球形特效，一般保持 0。
        [SerializeField] private Vector3 _modelForwardOffsetEuler = Vector3.zero;

        private Rigidbody _rb;
        private Collider _collider;

        // 直击伤害快照（Init 注入）
        private byte _attackerTeam;
        private int _attackerId;
        private float _damage;
        private DamageType _type;

        private bool _consumed; // 防同一物理步多次碰撞重复结算/销毁

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
        }

        /// <summary>施法方生成瞬间调用：注入伤害快照与初速度，忽略与施法者自身碰撞，定向、计时。</summary>
        public void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                         Vector3 velocity, Collider casterCollider, bool useGravity = false)
        {
            _attackerTeam = attackerTeam;
            _attackerId = attackerId;
            _damage = damage;
            _type = type;

            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_collider == null) _collider = GetComponent<Collider>();

            // 忽略与施法者自身碰撞，避免出膛瞬间撞到施法者 collider 即自毁
            if (casterCollider != null && _collider != null)
                Physics.IgnoreCollision(_collider, casterCollider);

            _rb.useGravity = useGravity;        // 直射：关重力走直线
            _rb.linearVelocity = velocity;       // Unity 6：Rigidbody.velocity → linearVelocity
            if (velocity.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(velocity) * Quaternion.Euler(_modelForwardOffsetEuler);

            Destroy(gameObject, _maxLifetime);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_consumed) return;

            IDamageable target = collision.collider.GetComponentInParent<IDamageable>();

            // 同阵营（施法者自身/队友）→ 穿过，不结算不销毁
            if (target != null && target.TeamId == _attackerTeam)
                return;

            Vector3 hitPoint = collision.GetContact(0).point;

            // 敌方且存活 → 直击伤害 + 附加燃烧
            if (target != null && target.IsAlive)
            {
                Vector3 vel = _rb != null ? _rb.linearVelocity : Vector3.zero;
                Vector3 hitDir = vel.sqrMagnitude > 1e-6f ? vel.normalized : transform.forward;
                var req = new DamageRequest(_attackerId, _attackerTeam, _damage, _type, hitPoint, hitDir);
                target.ReceiveHit(in req);

                ApplyBurn(collision.collider);
            }

            // 命中任何东西（敌方/环境）都放爆炸特效
            if (_explosionPrefab != null)
            {
                GameObject fx = Instantiate(_explosionPrefab, hitPoint, Quaternion.identity);
                Destroy(fx, _explosionLifetime);
            }

            _consumed = true;
            Destroy(gameObject);
        }

        /// <summary>在被命中目标承载 HealthComponent 的根物体上获取/新增 BurnStatus 并施加燃烧。</summary>
        private void ApplyBurn(Collider hitCollider)
        {
            HealthComponent health = hitCollider.GetComponentInParent<HealthComponent>();
            if (health == null) return;

            BurnStatus burn = health.GetComponent<BurnStatus>();
            if (burn == null) burn = health.gameObject.AddComponent<BurnStatus>();
            burn.Apply(_attackerId, _attackerTeam, _burnDamagePerTick, _type,
                       _burnInterval, _burnDuration, _onFirePrefab);
        }
    }
}
```

- [ ] **Step 2: 静态自检**

确认：`OnCollisionEnter` 内无 LINQ；唯一分配是命中瞬间的离散 `Instantiate`/`AddComponent`（可接受）。`BurnStatus` 在 `health.gameObject` 上（即 `IDamageable` 所在物体），`AddComponent` 触发其 `Awake` 即时缓存 `_target`，故紧随的 `Apply` 安全。`CharacterController` 继承自 `Collider`，故 Task 4 传 `_player.CharacterController` 作 `casterCollider` 合法。

- [ ] **Step 3: 开发者在 Unity Editor 验证编译**

Expected: Console 无报错；`Fireball` 与 `BurnStatus` 均编译通过。

- [ ] **Step 4: Commit（用户确认后）**

```bash
git add "Assets/_Project/Scripts/Combat/Fireball.cs"
git commit -m "feat(combat): add Fireball projectile (explosion + direct hit + burn)"
```

---

### Task 3: WizardController —— 法师子类（重写）

**Files:**
- Modify（整文件重写）: `Assets/_Project/Scripts/Character/WizardController.cs`

**Interfaces:**
- Consumes: `PlayerControllerBase`（`AttackBufferCounter`、`StateMachine`、`Animator`）、`ComboDefinition`、`AttackDefinition`、`HealthComponent`、`PlayerWizardAttackState`（Task 4，构造 `new PlayerWizardAttackState(this)`）。
- Produces（供 Task 4 普攻态读取）：`ComboDefinition Combo`、`GameObject FireballPrefab`、`Transform FireballSpawnPoint`、`float ProjectileSpeed`、`HealthComponent Health`、`int GetComboStateHash(int)`；并重写 `bool TryStartAttack()`。

> 注：Task 3 与 Task 4 互相引用同程序集类型，**单独看任一任务无法编译，二者完成后一起编译通过**——这是相互引用类型的固有情况，编译验证放在 Task 4 末尾。

- [ ] **Step 1: 用以下内容整体替换 `WizardController.cs`**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师控制器：在 PlayerControllerBase 共享能力（移动/跳跃/冲刺/相机/状态机）之上，
    /// 叠加远程普通攻击（点按 → PlayerWizardAttackState，沿角色朝向发射火球）。无蓄力、连段为 1 段。
    /// 与 ArcherController 同构，但发射物为 Fireball（命中爆炸 + 引燃 DoT）而非 Arrow。
    /// </summary>
    public class WizardController : PlayerControllerBase
    {
        [Header("Wizard Attack")]
        [SerializeField] private ComboDefinition _combo;          // 单段连段表（普通攻击 1 段）
        [SerializeField] private GameObject _fireballPrefab;      // 火球预制体（带 Rigidbody + Collider + Fireball）
        [SerializeField] private Transform _fireballSpawnPoint;   // 火球生成点（法杖前端）
        [SerializeField] private float _projectileSpeed = 20f;    // 火球初速度

        private PlayerWizardAttackState _wizardAttackState;
        private int[] _comboStateHashes;
        private HealthComponent _health;       // 阵营来源（缓存）

        public ComboDefinition Combo => _combo;
        public GameObject FireballPrefab => _fireballPrefab;
        public Transform FireballSpawnPoint => _fireballSpawnPoint;
        public float ProjectileSpeed => _projectileSpeed;
        public HealthComponent Health => _health;
        public PlayerWizardAttackState WizardAttackState => _wizardAttackState;

        protected override void Awake()
        {
            base.Awake();   // 基类：组件/输入/状态机/共享四态/Dash hash

            _health = GetComponent<HealthComponent>();
            _wizardAttackState = new PlayerWizardAttackState(this);
            BuildComboStateHashes();
        }

        /// <summary>攻击 seam：点按攻击键（有缓冲）→ 切普通攻击态（沿朝向发射火球）。法师无蓄力。</summary>
        public override bool TryStartAttack()
        {
            if (AttackBufferCounter > 0f)
            {
                AttackBufferCounter = 0f;
                StateMachine.ChangeState(_wizardAttackState);
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

        /// <summary>把连段表各段 AnimationStateName 预 hash 成 int[]（仿 Warrior/Archer，平行实现）。</summary>
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
    }
}
```

- [ ] **Step 2: 静态自检**

确认：已删除原残缺文件里的 `BuildChargeHashes()` 调用、蓄力字段、空 `Start/Update`；命名空间 `Game.Character`；只用基类共享成员 + 自有攻击成员。

---

### Task 4: PlayerWizardAttackState —— 法师普攻态（重写）

**Files:**
- Modify（整文件重写）: `Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs`

**Interfaces:**
- Consumes: `PlayerStateBase`（`_player`、`HandleRotation()`、`Enter/Update/Exit`）、`WizardController`（Task 3 的属性/方法）、`AttackDefinition`、`ComboResolver.Resolve(...)`、`ComboDecision`、`Fireball.Init(...)`（Task 2）、`GameLog`。
- Produces: 无（终端状态类，仅被 `WizardController` 构造与状态机调度）。

- [ ] **Step 1: 用以下内容整体替换 `PlayerWizardAttackState.cs`**（含从 `MonoBehaviour` 改为纯 C# 状态，加上命名空间）

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师普通攻击状态（远程）。与 PlayerBowAttackState 同骨架：CrossFade 单段 + normalizedTime 单点生成发射物 +
    /// ComboResolver 走向 End + 边走边射；区别仅在发射物是 Fireball（命中爆炸 + 引燃）而非 Arrow。1 段、无蓄力、无刀光。
    /// </summary>
    public class PlayerWizardAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 动画进度达此值 → 结束（1 段永不 Advance）
        private const float CrossFadeDuration = 0.1f; // 进入攻击段 CrossFade 固定时长

        private readonly WizardController _wizard;    // typed 子类引用，拿法师专属成员

        private int _comboIndex;
        private bool _fireballSpawned;                // 本次播放是否已生成过火球（单点越阈触发一次的去重位）

        public PlayerWizardAttackState(WizardController player) : base(player)
        {
            _wizard = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 消耗起手输入
            _fireballSpawned = false;

            if (_wizard.Combo == null || _wizard.Combo.SegmentCount == 0)
            {
                GameLog.Warn("法师 ComboDefinition 未配置或无段落，无法攻击", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement();      // 边走边射：保留完整水平移动（不锁脚）
            base.HandleRotation(); // 随移动方向转向；火球在 ArrowSpawnTime 沿当前朝向射出
            HandleFireballSpawn(); // 单点：normalizedTime 越过 ArrowSpawnTime 生成一次
            CheckCombo();          // 1 段 → 永远走向 End
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
            _fireballSpawned = false; // 新段重置去重位
            int hash = _wizard.GetComboStateHash(index);
            _player.Animator.CrossFadeInFixedTime(hash, CrossFadeDuration, 0);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // 边走边射：与接地态相同的完整移动（水平 MoveDirection*MoveSpeed + 垂直 VerticalVelocity）
            Vector3 velocity = _player.MoveDirection * _player.MoveSpeed;
            velocity.y = _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        private void HandleFireballSpawn()
        {
            if (_fireballSpawned) return;
            if (_player.Animator.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信

            AttackDefinition seg = _wizard.Combo.Segments[_comboIndex];
            if (seg == null) return;

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= seg.ArrowSpawnTime)
            {
                SpawnFireball(seg);
                _fireballSpawned = true;
            }
        }

        private void SpawnFireball(AttackDefinition seg)
        {
            if (_wizard.FireballPrefab == null || _wizard.FireballSpawnPoint == null)
            {
                GameLog.Warn("法师 FireballPrefab/FireballSpawnPoint 未配置，无法生成火球", "Combat");
                return;
            }

            // 沿角色当前朝向发射（轨道相机风格，无准星瞄准——基础阶段简化）
            Vector3 dir = _player.transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = _player.transform.forward; // 退化兜底
            dir.Normalize();

            Transform sp = _wizard.FireballSpawnPoint;
            GameObject go = Object.Instantiate(_wizard.FireballPrefab, sp.position, Quaternion.LookRotation(dir));

            Fireball fireball = go.GetComponent<Fireball>();
            if (fireball == null)
            {
                GameLog.Warn("FireballPrefab 上没有 Fireball 组件", "Combat");
                return;
            }

            byte team = _wizard.Health != null ? _wizard.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            Vector3 velocity = dir * _wizard.ProjectileSpeed;
            // useGravity 用 Fireball 默认值 false（直线飞行）
            fireball.Init(team, attackerId, seg.BaseAmount, seg.Type, velocity, _player.CharacterController);
        }

        private void CheckCombo()
        {
            if (_player.Animator.IsInTransition(0)) return;

            AttackDefinition seg = _wizard.Combo.Segments[_comboIndex];
            if (seg == null)
            {
                GameLog.Warn($"法师连段第 {_comboIndex} 段未赋值，中断", "Combat");
                TransitionToMovement();
                return;
            }

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            bool hasBuffer = _player.AttackBufferCounter > 0f;

            ComboDecision decision = ComboResolver.Resolve(
                _comboIndex, _wizard.Combo.SegmentCount, t, hasBuffer,
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

- [ ] **Step 2: 静态自检**

确认：类不再是 `MonoBehaviour`，继承 `PlayerStateBase`；命名空间 `Game.Character`；与 `PlayerBowAttackState` 一致地用 `_player`（基类成员）+ `_wizard`（typed 专属）。确认本文件原 `.meta` 未动（GUID 保留）。

- [ ] **Step 3: 确认 `PlayerWizardAttackState` 未被当作组件挂载**

因为它从 `MonoBehaviour` 变为纯 C# 类，若曾被拖到某 GameObject 上会产生「缺失脚本」。它是新建脚手架、未接线，预期未挂载——开发者在 Editor 扫一眼 Wizard 相关 GameObject 确认无 Missing Script。

- [ ] **Step 4: 开发者在 Unity Editor 验证整体编译（Task 3 + Task 4 一起）**

Expected: Console 无报错；`WizardController` 与 `PlayerWizardAttackState` 相互引用解析成功，全工程编译通过。

- [ ] **Step 5: Commit（用户确认后）**

```bash
git add "Assets/_Project/Scripts/Character/WizardController.cs" "Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs"
git commit -m "feat(character): Wizard controller + ranged fireball normal attack state"
```

---

### Task 5: Editor 接线与 Play 模式验证（开发者执行）

> 本任务无 `.cs` 改动，全部在 Unity Editor。按弓箭手已有接线类比即可。

**Files:** 无（创建 SO 资产 + 配置预制体/场景对象）。

- [ ] **Step 1: 创建攻击数据 SO**
  - 右键 `Create → Game/Combat/Attack Definition`，命名如 `Atk_Wizard_Fireball`：
    - `BaseAmount` = 火球直击伤害（如 15）；`Type` = `Magical`。
    - `ArrowSpawnTime` = 火球生成的归一化时机（如 0.4，对应 `Attack01_MagicWand` 出手帧）。
    - `ComboInputStart/End`、`HitActive*`、`Trail*` 对单段远程不影响（保持默认）。
    - `AnimationStateName` = **`Attack01_MagicWand`**（必须与 Animator 状态名完全一致）。
  - 右键 `Create → Game/Combat/Combo Definition`，命名 `Combo_Wizard`：`Segments` 长度 1，元素 0 拖入 `Atk_Wizard_Fireball`。

- [ ] **Step 2: 配置 Fireball 预制体**
  - 打开 `Assets/_Project/Art/Prefabs/Wizard/FireMagic/Fireball.prefab`：
    - 加 `Rigidbody`（`Use Gravity` 可留勾，运行时由 `Init` 设 false；建议 `Collision Detection = Continuous` 防穿透；`Interpolate` 可选）。
    - 加 `Collider`（如 `SphereCollider`，**非 Trigger**，因走 `OnCollisionEnter`）。
    - 加 `Fireball` 脚本，并赋值：`Explosion Prefab` = `Fireball_Explosion`；`On Fire Prefab` = `OnFire`；`Burn Damage Per Tick`/`Burn Interval`/`Burn Duration` 按手感调；`Max Lifetime` 默认 5；`Model Forward Offset Euler` 火球球形特效一般留 0（若有方向性拖尾则调到对齐飞行方向）。
  - 确认 `Fireball_Explosion`、`OnFire` 两个特效预制体的粒子时长合理（爆炸由 `Explosion Lifetime` 秒后销毁；OnFire 由 `BurnStatus` 在燃烧结束时销毁）。

- [ ] **Step 3: 搭法师角色 GameObject / 预制体**
  - 一个带模型 + `WizardHero` Animator 的角色根物体，挂：`CharacterController`、`GroundChecker`、`HealthComponent`（设 `MaxHp`、`TeamId`——与敌人不同）、`WizardController`、`CharacterCombatFeedback`。
  - `WizardController` 赋值：`Combo` = `Combo_Wizard`；`Fireball Prefab` = `Fireball`；`Fireball Spawn Point` = 法杖前端的一个空子物体 Transform；`Projectile Speed`（如 20）；并填基类的 `Camera Root`、移动/跳跃/冲刺参数。
  - 基类 `_dashStateName` 字段（Inspector 名「Dash State Name」）设为 **`DashForward_MagicWand`**（法师 Animator 的冲刺状态名）。
  - `CharacterCombatFeedback` 填：`Get Hit State Name` = `GetHit_MagicWand`，`Die State Name` = `Die_MagicWand`，`Disable On Death` 拖入 `WizardController`，其余按既有受击/死亡反馈文档调。

- [ ] **Step 4: WizardHero Animator 连线（进入靠代码、退出靠连线）**
  - `Attack01_MagicWand`：**画一条出去**的过渡 `Attack01_MagicWand → Idle_Bow`（或 `Run_MagicWand`），勾 `Has Exit Time`（如 0.85，与 `EndThreshold` 呼应），否则普攻后卡姿势。无需进入过渡（代码 CrossFade）。
  - `DashForward_MagicWand`：同理画 `→ Idle_Bow` 的 `Has Exit Time` 退出过渡。
  - `GetHit_MagicWand`：画 `→ Idle_Bow` 的 `Has Exit Time` 退出过渡。
  - `Die_MagicWand`：取消 `Loop Time`，不画出去的过渡（停在最后一帧，对象随后被销毁）。
  - 确认 `speed`/`isGrounded`/`jump` 参数与移动/跳跃状态过渡已配置（与弓箭手一致），保证行走/跑/跳/落地动画正常。

- [ ] **Step 5: 物理层 / 阵营自检**
  - 法师 `TeamId` 与目标假人/敌人不同（同阵营会被穿过、不结算）。
  - 目标需有 `HealthComponent`（+ Collider）；`Fireball` 的 `Aim`/层无特殊要求（靠 `IgnoreCollision` 排除施法者自身、靠 `TeamId` 穿过友军）。

- [ ] **Step 6: Play 模式验收清单**
  - 行走/跑（`speed` 驱动）、跳跃（coyote/buffer/分段重力）、冲刺（`DashForward_MagicWand` + 冷却/缓冲）均正常——这些由基类复用，应「开箱即用」。
  - 点按鼠标左键：播 `Attack01_MagicWand`，在 `ArrowSpawnTime` 处从法杖前端沿角色朝向射出火球，且**可边走边射**（不锁脚）。
  - 火球命中目标：生成 `Fireball_Explosion` 爆炸；目标掉血并闪红（`CharacterCombatFeedback`）；目标身上出现 `OnFire`，随后每 `Burn Interval` 持续掉血，`Burn Duration` 后 OnFire 消失。
  - 目标血量归零：播 `Die_MagicWand` → 延时销毁；燃烧随之停止、OnFire 清理。
  - 火球不应撞到施法者自身、不应命中同阵营。
  - Profiler 确认普攻每帧无 GC Alloc（火球/特效/AddComponent 仅在离散命中瞬间分配，可接受）。

- [ ] **Step 7: Commit 资产（用户确认后；`.cs` 已在前序任务提交）**

```bash
git add "Assets/_Project/ScriptableObjects" "Assets/_Project/Art/Prefabs/Wizard" "Assets/_Project/Art/Animators/WizardHero.controller" "Assets/_Project/Scenes/SampleScene.unity"
git commit -m "feat(wizard): wire fireball combo SO, prefab components, animator exits, scene"
```

---

## 已知限制与后续可加强

- **燃烧每跳会触发受击反馈**：`BurnStatus` 走 `ReceiveHit` → 每跳发 `DamageReceivedEvent`，`CharacterCombatFeedback` 会每跳闪红 + 播 `GetHit`，燃烧期间可能反复打断动画。原型可接受；要消除可在 `DamageRequest` 增加「DoT/静默」标志，或让反馈组件对短间隔的连续受击做节流。
- **燃烧不叠层**：重复命中只刷新持续时间（`Apply` 在 `_active` 时不再生成第二份 OnFire、刷新参数）。如需叠加多层 DoT，需要把单组件改为「多实例燃烧列表」。
- **无瞄准准星**：火球沿角色朝向直射（同弓箭手普攻）。要做屏幕中心准星瞄准，可比照弓箭手蓄力重击的屏幕中心 `RaycastNonAlloc` 方案另起一个增强任务。
- **燃烧 tunable 在 Fireball 预制体上**（非独立 SO）。若后续技能系统要复用，可抽成 `BurnDefinition` ScriptableObject。
- **法师死亡后相机**：玩家被销毁后跟随相机会失去目标，与既有受击/死亡反馈文档同一限制，正式版应走「死亡 → 游戏状态切换/复活」而非直接销毁玩家。
```

