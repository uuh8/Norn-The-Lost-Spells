# 法师陨石重击 + 投射物基类抽象 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给法师加"陨石重击"——长按左键引导（原地定身、脚下光圈、地面落点圆框、鼠标经相机挪动落点），松手后从落点斜上方天空生成陨石直线砸向落点，命中角色爆炸并造成伤害；同时把 Arrow/Fireball/陨石 的公共投射逻辑抽象为 `ProjectileBase`。

**Architecture:** 沿用弓箭手"轻点=普攻 / 长按=重击"的 `TryStartAttack` 路由（决策 A：代码计时 + 阈值门控）。重击是一个 `PlayerWizardHeavyState`（纯 C# 状态，仿 `PlayerChargeAttackState` 的 channel→release 双阶段），引导阶段用屏幕中心射线打地面求落点（零 GC `RaycastNonAlloc`），施法阶段用计时器驱动（不依赖动画 normalizedTime，因法师暂无专用施法动画）。陨石 `NovaFireball` 与现有 `Arrow`/`Fireball` 统一改为继承新的 `ProjectileBase`：基类管 Init/碰撞/同阵营穿过/伤害结算/销毁，子类只重写 `OnImpact` 决定命中表现。

**Tech Stack:** Unity 6.3 / URP 17.3 / C# / 现有 `Game.Combat`（`IDamageable`、`HealthComponent`、`DamageRequest`、`DamageType`、`ChargeAttackDefinition` 范式）与 `Game.Character`（`PlayerControllerBase`、`PlayerStateBase`、状态机、`ArcherController` 的 tap/hold 路由范式）。

## 关于"是否抽象投射物基类"的结论（回答你的提问）

**值得抽，且现在就抽。** 现状 `Arrow` 与 `Fireball` 已有 ~80% 重复：`Init`（注入快照/初速度/忽略施法者/定向/计时）、`OnCollisionEnter`（同阵营穿过 / 敌方 `ReceiveHit` / 命中销毁 / `_consumed` 去重）几乎逐行相同，差异只在"命中后放什么特效/加什么状态"。陨石是第三个，差异同样只在命中表现。把公共骨架提到 `ProjectileBase`，子类仅重写一个 `OnImpact(collision, target, hitPoint, damaged)` 扩展点：
- 新投射物从"复制 90 行"变成"写十几行 OnImpact"，**不易漏掉 `_consumed` 去重、同阵营穿过这类易错点**；
- 伤害结算路径集中一处，未来要改（如统一暴击、命中事件）只改基类；
- 代价：要动到已工作的 `Arrow`（但 `Init` 公开签名不变、序列化字段名不变 → 预制体引用与 Inspector 值不丢，风险可控）。
本计划 Task 1 即完成该抽象并迁移 Arrow/Fireball。

## Global Constraints（项目级硬约束，每个任务都隐含适用）

- **程序集隔离**：`ProjectileBase`/`NovaFireball`/`MeteorAttackDefinition` 放 `Game.Combat`，**不得引用** `Game.Character`；`WizardController`/`PlayerWizardHeavyState` 放 `Game.Character`（可引用 Core + Combat）。
- **命名空间匹配程序集**；私有/保护字段 `_camelCase`；公开成员/类型 `PascalCase`；接口 `IXxx`。
- **零 GC 热路径**：引导期每帧的落点射线必须用 `RaycastNonAlloc` 打进预分配数组；`Update` 内禁止 `new`/LINQ/装箱。离散事件（松手生成陨石、Enter 生成光圈/圆框、命中生成爆炸、`AddComponent`）一次性分配可接受。
- **日志只用 `GameLog`**；**绝不** `Debug.Log`。
- **Animator 进入靠代码、退出靠连线**；引导/施法动画为**可选**（状态名留空则不 CrossFade，不告警）；若填了名字，对应状态必须自带退出过渡，否则卡姿势。
- **输入不改**：重击走 `Attack`（左键）的 tap/hold 路由，复用既有 `IsAttackHeld`；右键 `Dash` 不动。
- **Claude 只改 `.cs` 与纯文本配置**；编译/Animator 连线/Inspector 赋值/Play 测试由开发者在 Editor 完成。不要断言"能跑"，只断言静态逻辑正确。不建 `.meta`。

---

## 文件结构

| 文件 | 程序集 | 动作 | 职责 |
|---|---|---|---|
| `Assets/_Project/Scripts/Combat/ProjectileBase.cs` | Game.Combat | 新增 | 投射物抽象基类：Init / 碰撞 / 同阵营穿过 / 伤害结算 / 销毁 / `OnImpact` 扩展点 |
| `Assets/_Project/Scripts/Combat/Arrow.cs` | Game.Combat | 重写（瘦身） | 继承 `ProjectileBase`，无额外命中表现（抛物线，Init 默认 useGravity=true） |
| `Assets/_Project/Scripts/Combat/Fireball.cs` | Game.Combat | 重写（迁移） | 继承 `ProjectileBase`，`OnImpact` 放爆炸 + 附加燃烧 |
| `Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs` | Game.Character | 小改 | 火球 `Init` 显式传 `useGravity:false`（基类默认改成了 true） |
| `Assets/_Project/Scripts/Combat/NovaFireball.cs` | Game.Combat | 新增 | 陨石投射物：继承 `ProjectileBase`，`OnImpact` 放 NovaExplosion_Hit |
| `Assets/_Project/Scripts/Combat/MeteorAttackDefinition.cs` | Game.Combat | 新增 | 陨石重击数据 SO（仿 ChargeAttackDefinition） |
| `Assets/_Project/Scripts/Character/WizardController.cs` | Game.Character | 改 | 加 tap/hold 路由到重击；陨石/光圈/圆框 预制体引用 + AimMask + 动画 hash + 重击态 |
| `Assets/_Project/Scripts/Character/States/PlayerWizardHeavyState.cs` | Game.Character | 新增 | 引导（定身/瞄落点/光圈圆框）→ 施法（生成陨石）双阶段状态 |

**依赖顺序**：Task 1（基类 + 迁移 Arrow/Fireball + 修火球 Init）→ Task 2（NovaFireball + SO）→ Task 3（WizardController 路由）↔ Task 4（重击态，与 Task 3 互引用一起编译）→ Task 5（Editor）。

---

### Task 1: ProjectileBase 抽象 + 迁移 Arrow/Fireball

**Files:**
- Create: `Assets/_Project/Scripts/Combat/ProjectileBase.cs`
- Modify（重写）: `Assets/_Project/Scripts/Combat/Arrow.cs`
- Modify（重写）: `Assets/_Project/Scripts/Combat/Fireball.cs`
- Modify（1 行）: `Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs`

**Interfaces:**
- Produces: `ProjectileBase.Init(byte attackerTeam, int attackerId, float damage, DamageType type, Vector3 velocity, Collider casterCollider, bool useGravity = true)`；`protected virtual void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged)`；`protected` 字段 `_attackerTeam/_attackerId/_damage/_type/_rb/_collider`。
- 关键不变量：**`Init` 公开签名与 Arrow 原签名一致**（参数顺序同），故所有调用点（`PlayerBowAttackState`、`PlayerChargeAttackState`）无需改；唯一例外是火球调用点依赖旧的 useGravity 默认值，见 Step 4。

- [ ] **Step 1: 写 `ProjectileBase.cs`**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 投射物基类。抽象所有飞行投射物的公共骨架：Init 注入伤害快照/初速度/忽略施法者碰撞/定向/计时；
    /// OnCollisionEnter 统一处理 同阵营穿过 / 敌方结算一次 IDamageable.ReceiveHit / 命中后销毁。
    /// 子类只需重写 OnImpact 决定"命中后、销毁前的额外表现"（爆炸特效、附加状态等）。
    /// Arrow / Fireball / NovaFireball 皆派生于此，消除三者重复逻辑。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public abstract class ProjectileBase : MonoBehaviour
    {
        [Header("飞行 (基类通用)")]
        [SerializeField] protected float _maxLifetime = 5f; // 超时自毁，防漏网投射物累积
        // 模型朝向修正：LookRotation 把 +Z 对到飞行方向；箭尖沿 +Y 的模型填 (90,0,0)，球形特效填 0。
        [SerializeField] protected Vector3 _modelForwardOffsetEuler = Vector3.zero;

        protected Rigidbody _rb;
        protected Collider _collider;

        // 伤害快照（Init 注入；命中时构造 DamageRequest，不回查可能已销毁的攻击者）
        protected byte _attackerTeam;
        protected int _attackerId;
        protected float _damage;
        protected DamageType _type;

        private bool _consumed; // 防同一物理步多次碰撞重复结算/销毁

        protected virtual void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
        }

        /// <summary>
        /// 攻击方生成瞬间调用：注入伤害快照与初速度，忽略与施法者自身碰撞，定向、计时。
        /// useGravity：抛物线箭矢传 true（默认）；直线投射物（瞄准直射/火球/陨石）传 false。
        /// </summary>
        public void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                         Vector3 velocity, Collider casterCollider, bool useGravity = true)
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

            _rb.useGravity = useGravity;
            _rb.linearVelocity = velocity; // Unity 6：Rigidbody.velocity → linearVelocity
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
            bool damaged = false;

            // 敌方且存活 → 结算一次伤害
            if (target != null && target.IsAlive)
            {
                Vector3 vel = _rb != null ? _rb.linearVelocity : Vector3.zero;
                Vector3 hitDir = vel.sqrMagnitude > 1e-6f ? vel.normalized : transform.forward;
                var req = new DamageRequest(_attackerId, _attackerTeam, _damage, _type, hitPoint, hitDir);
                target.ReceiveHit(in req);
                damaged = true;
            }

            // 子类扩展点：命中敌方或环境都会走到（便于"撞地也爆炸"）
            OnImpact(collision, target, hitPoint, damaged);

            _consumed = true;
            Destroy(gameObject);
        }

        /// <summary>命中后、销毁前的子类扩展点（默认空）。target 可能为 null（命中环境）；damaged 表示本次是否结算了伤害。</summary>
        protected virtual void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged) { }
    }
}
```

- [ ] **Step 2: 重写 `Arrow.cs` 为基类的最简实现**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 飞行箭矢。投射物基类的最简实现：仅命中结算 + 销毁，无额外命中表现（不重写 OnImpact）。
    /// 抛物线箭矢，生成时 Init 用默认 useGravity = true。
    /// 注意：模型箭尖沿 +Y，预制体上 _modelForwardOffsetEuler 应保持 (90,0,0)（基类默认 0；既有 Arrow 预制体已序列化该值，迁移后按字段名保留）。
    /// </summary>
    public class Arrow : ProjectileBase
    {
        // 命中表现为空：基类已处理 同阵营穿过 / 敌方 ReceiveHit / 命中销毁。
    }
}
```

- [ ] **Step 3: 重写 `Fireball.cs` 迁移到基类**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 飞行火球。投射物基类 + 命中表现：命中点生成爆炸特效(Fireball_Explosion)，
    /// 并给被命中的角色附加持续燃烧(BurnStatus + OnFire)。直线飞行（生成时 Init 传 useGravity=false）。
    /// </summary>
    public class Fireball : ProjectileBase
    {
        [Header("命中表现")]
        [SerializeField] private GameObject _explosionPrefab;   // Fireball_Explosion
        [SerializeField] private float _explosionLifetime = 2f;

        [Header("燃烧 (DoT)")]
        [SerializeField] private GameObject _onFirePrefab;      // OnFire
        [SerializeField] private float _burnDamagePerTick = 3f;
        [SerializeField] private float _burnInterval = 0.5f;
        [SerializeField] private float _burnDuration = 3f;

        protected override void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged)
        {
            // 命中任何东西都放爆炸
            if (_explosionPrefab != null)
            {
                GameObject fx = Instantiate(_explosionPrefab, hitPoint, Quaternion.identity);
                Destroy(fx, _explosionLifetime);
            }

            // 仅对结算了伤害的角色附加燃烧
            if (damaged)
                ApplyBurn(collision.collider);
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

- [ ] **Step 4: 修 `PlayerWizardAttackState.cs` 火球 Init 显式传 useGravity:false**

基类 `Init` 默认 `useGravity = true`（为保留 Arrow 抛物线 + 不动既有箭矢调用点）。火球要直线飞，必须在生成处显式传 false。把 `SpawnFireball` 里的这一行：

```csharp
            fireball.Init(team, attackerId, seg.BaseAmount, seg.Type, velocity, _player.CharacterController);
```

改为：

```csharp
            // 直线飞行：基类 Init 默认 useGravity=true（抛物线），火球须显式关重力
            fireball.Init(team, attackerId, seg.BaseAmount, seg.Type, velocity, _player.CharacterController, useGravity: false);
```

- [ ] **Step 5: 静态自检**

确认：`PlayerBowAttackState` 的箭矢 `Init`（不传 useGravity）→ 默认 true（抛物线）✓ 未改；`PlayerChargeAttackState` 显式传 false ✓ 未受影响；火球已显式 false ✓。`Fireball` 不再有自己的 `Awake/Init/OnCollisionEnter/_maxLifetime/_modelForwardOffsetEuler`（已上移基类，按字段名沿用序列化值）。

- [ ] **Step 6: 开发者在 Unity Editor 验证编译**

Expected: 全工程编译通过；`Arrow` / `Fireball` 组件在各自预制体上仍在（同 GUID），`_maxLifetime`/`_modelForwardOffsetEuler` 等序列化值保留。若 `Arrow` 预制体的 `_modelForwardOffsetEuler` 意外变 0，手动设回 (90,0,0)。

- [ ] **Step 7: Commit（用户确认后）**

```bash
git add "Assets/_Project/Scripts/Combat/ProjectileBase.cs" "Assets/_Project/Scripts/Combat/Arrow.cs" "Assets/_Project/Scripts/Combat/Fireball.cs" "Assets/_Project/Scripts/Character/States/PlayerWizardAttackState.cs"
git commit -m "refactor(combat): extract ProjectileBase; migrate Arrow/Fireball onto it"
```

---

### Task 2: NovaFireball 陨石 + MeteorAttackDefinition 数据 SO

**Files:**
- Create: `Assets/_Project/Scripts/Combat/NovaFireball.cs`
- Create: `Assets/_Project/Scripts/Combat/MeteorAttackDefinition.cs`

**Interfaces:**
- Consumes: `ProjectileBase`（Task 1）、`IDamageable`、`DamageType`。
- Produces（供 Task 3/4）：`NovaFireball`（组件类型，含序列化 `_explosionPrefab`）；`MeteorAttackDefinition` 的公开字段 `ChannelStateName/ReleaseStateName/TapThreshold/AimMaxDistance/MeteorSpawnDelay/ReleaseDuration/MeteorSpeed/SpawnHeight/SpawnHorizontalBack/Damage/Type/CrossFadeDuration`。

- [ ] **Step 1: 写 `NovaFireball.cs`**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 陨石投射物。从天空斜上方直线砸向引导锁定的落点；命中角色/地面时生成爆炸特效(NovaExplosion_Hit)，
    /// 并对命中的角色结算伤害（伤害与销毁由 ProjectileBase 处理）。直线飞行（生成时 Init 传 useGravity=false）。
    /// </summary>
    public class NovaFireball : ProjectileBase
    {
        [Header("命中表现")]
        [SerializeField] private GameObject _explosionPrefab;   // NovaExplosion_Hit
        [SerializeField] private float _explosionLifetime = 2f;

        protected override void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged)
        {
            if (_explosionPrefab != null)
            {
                GameObject fx = Instantiate(_explosionPrefab, hitPoint, Quaternion.identity);
                Destroy(fx, _explosionLifetime);
            }
        }
    }
}
```

- [ ] **Step 2: 写 `MeteorAttackDefinition.cs`**

```csharp
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的法师陨石重击定义。引导(channel)→松手(release)→陨石坠落 的全部可调参数。
    /// 仿 ChargeAttackDefinition：纯数据，不引用 Animator/Character。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Meteor Attack Definition", fileName = "MeteorAttackDefinition")]
    public class MeteorAttackDefinition : ScriptableObject
    {
        [Header("动画状态名 (可留空；非空才 CrossFade。须与 Animator 节点名精确一致)")]
        public string ChannelStateName = "";  // 引导循环姿势（代码 CrossFade 进入；可空）
        public string ReleaseStateName = "";  // 松手施法（代码 CrossFade 进入；可空）

        [Header("输入")]
        [Tooltip("按住超过此秒数才进入引导；短于此为点按普攻（火球）")]
        public float TapThreshold = 0.15f;

        [Header("瞄准 (引导期间屏幕中心射线打地面求落点)")]
        [Tooltip("射线最大距离；同时作为落点距玩家的最大水平距离上限")]
        public float AimMaxDistance = 30f;

        [Header("松手节奏 (秒)")]
        [Tooltip("松手到陨石生成的延迟（给施法动作留前摇）")]
        public float MeteorSpawnDelay = 0.3f;
        [Tooltip("松手到状态结束(回到移动)的总时长；应 ≥ MeteorSpawnDelay")]
        public float ReleaseDuration = 0.6f;

        [Header("陨石")]
        public float MeteorSpeed = 40f;          // 陨石飞行速度
        public float SpawnHeight = 25f;          // 落点正上方的生成高度
        public float SpawnHorizontalBack = 10f;  // 生成点相对落点的水平后退量（制造"斜上方"入射角）

        [Header("伤害")]
        public float Damage = 60f;
        public DamageType Type = DamageType.Magical;

        [Header("CrossFade")]
        public float CrossFadeDuration = 0.1f;
    }
}
```

- [ ] **Step 3: 开发者在 Unity Editor 验证编译**

Expected: 编译通过；菜单 `Create → Game/Combat/Meteor Attack Definition` 出现。

- [ ] **Step 4: Commit（用户确认后）**

```bash
git add "Assets/_Project/Scripts/Combat/NovaFireball.cs" "Assets/_Project/Scripts/Combat/MeteorAttackDefinition.cs"
git commit -m "feat(combat): add NovaFireball meteor projectile + MeteorAttackDefinition SO"
```

---

### Task 3: WizardController —— tap/hold 路由到陨石重击

**Files:**
- Modify（整文件重写）: `Assets/_Project/Scripts/Character/WizardController.cs`

**Interfaces:**
- Consumes: `PlayerControllerBase`（`IsAttackHeld`、`AttackBufferCounter`、`StateMachine`）、`MeteorAttackDefinition`（Task 2）、`PlayerWizardAttackState`、`PlayerWizardHeavyState`（Task 4）。
- Produces（供 Task 4 重击态读取）：`MeteorAttackDefinition MeteorData`、`GameObject MeteorPrefab`、`GameObject ChannelRingPrefab`、`GameObject TargetIndicatorPrefab`、`LayerMask AimMask`、`HealthComponent Health`、`int MeteorChannelHash`、`int MeteorReleaseHash`；重写 `TryStartAttack()`。

> Task 3 与 Task 4 互相引用，单独不编译，二者完成后一起编译（编译验证在 Task 4 末尾）。

- [ ] **Step 1: 用以下内容整体替换 `WizardController.cs`**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师控制器：共享移动能力之上叠加远程攻击。攻击输入路由（仿 ArcherController 决策 A：代码计时 + 阈值门控）：
    /// 点按左键 → 火球普攻（PlayerWizardAttackState）；长按左键过 TapThreshold → 陨石重击引导（PlayerWizardHeavyState）。
    /// 发射物 Fireball / 陨石 NovaFireball 均派生自 ProjectileBase。
    /// </summary>
    public class WizardController : PlayerControllerBase
    {
        [Header("Wizard Attack (火球普攻)")]
        [SerializeField] private ComboDefinition _combo;          // 单段连段表（普通攻击 1 段）
        [SerializeField] private GameObject _fireballPrefab;      // 火球预制体（带 Rigidbody + Collider + Fireball）
        [SerializeField] private Transform _fireballSpawnPoint;   // 火球生成点（法杖前端）
        [SerializeField] private float _projectileSpeed = 20f;    // 火球初速度

        [Header("Wizard Heavy (陨石重击)")]
        [SerializeField] private MeteorAttackDefinition _meteorData;
        [SerializeField] private GameObject _meteorPrefab;            // NovaFireball
        [SerializeField] private GameObject _channelRingPrefab;       // NovaFireball_Skill_Start（脚下光圈）
        [SerializeField] private GameObject _targetIndicatorPrefab;   // NovaFireball_Pre_Field（落点圆框）
        [SerializeField] private LayerMask _aimMask = ~0;            // 引导落点射线命中层（建议只勾地面层）

        private PlayerWizardAttackState _wizardAttackState;
        private PlayerWizardHeavyState _heavyState;
        private int[] _comboStateHashes;
        private HealthComponent _health;       // 阵营来源（缓存）
        private float _attackHeldTime;         // 攻击键按住累计时长（tap/hold 路由）

        // 陨石引导/施法动画状态名预 hash（可空 → 0 → 不 CrossFade）
        private int _meteorChannelHash;
        private int _meteorReleaseHash;

        public ComboDefinition Combo => _combo;
        public GameObject FireballPrefab => _fireballPrefab;
        public Transform FireballSpawnPoint => _fireballSpawnPoint;
        public float ProjectileSpeed => _projectileSpeed;
        public HealthComponent Health => _health;
        public PlayerWizardAttackState WizardAttackState => _wizardAttackState;

        public MeteorAttackDefinition MeteorData => _meteorData;
        public GameObject MeteorPrefab => _meteorPrefab;
        public GameObject ChannelRingPrefab => _channelRingPrefab;
        public GameObject TargetIndicatorPrefab => _targetIndicatorPrefab;
        public LayerMask AimMask => _aimMask;
        public PlayerWizardHeavyState HeavyState => _heavyState;
        public int MeteorChannelHash => _meteorChannelHash;
        public int MeteorReleaseHash => _meteorReleaseHash;

        protected override void Awake()
        {
            base.Awake();   // 基类：组件/输入/状态机/共享四态/Dash hash

            _health = GetComponent<HealthComponent>();
            _wizardAttackState = new PlayerWizardAttackState(this);
            _heavyState = new PlayerWizardHeavyState(this);
            BuildComboStateHashes();
            BuildMeteorHashes();
        }

        /// <summary>
        /// 攻击输入路由：长按左键过 TapThreshold → 陨石引导态；未达阈值松手(或子帧点按) → 火球普攻。
        /// </summary>
        public override bool TryStartAttack()
        {
            // 按住且有重击数据：累计时长，过阈值进引导
            if (_meteorData != null && IsAttackHeld)
            {
                _attackHeldTime += Time.deltaTime;
                if (_attackHeldTime >= _meteorData.TapThreshold)
                {
                    _attackHeldTime = 0f;
                    AttackBufferCounter = 0f;
                    StateMachine.ChangeState(_heavyState);
                    return true;
                }
                return false; // 仍在 tap 窗口内，按住等待
            }

            // 已松手（或无重击数据）：曾有按下 → 火球普攻点射
            bool hadPress = _attackHeldTime > 0f || AttackBufferCounter > 0f;
            _attackHeldTime = 0f;
            if (hadPress)
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

        /// <summary>把连段表各段 AnimationStateName 预 hash 成 int[]（仿 Warrior/Archer）。</summary>
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

        /// <summary>预 hash 陨石引导/施法动画名。二者可空（引导/施法动画为可选润色）：空 → 0 → 重击态据此跳过 CrossFade，不告警。</summary>
        private void BuildMeteorHashes()
        {
            if (_meteorData == null)
            {
                GameLog.Warn("法师 MeteorAttackDefinition 未配置，陨石重击不可用", "Combat");
                return;
            }
            _meteorChannelHash = string.IsNullOrEmpty(_meteorData.ChannelStateName)
                ? 0 : Animator.StringToHash(_meteorData.ChannelStateName);
            _meteorReleaseHash = string.IsNullOrEmpty(_meteorData.ReleaseStateName)
                ? 0 : Animator.StringToHash(_meteorData.ReleaseStateName);
        }
    }
}
```

- [ ] **Step 2: 静态自检**

确认：路由逻辑与 `ArcherController.TryStartAttack` 同构（charge→meteor 替换）；新增引用都来自 Combat/自身；火球普攻路径不变。

---

### Task 4: PlayerWizardHeavyState —— 引导→施法 双阶段状态

**Files:**
- Create: `Assets/_Project/Scripts/Character/States/PlayerWizardHeavyState.cs`

**Interfaces:**
- Consumes: `PlayerStateBase`（`_player`、`Enter/Update/Exit`）、`WizardController`（Task 3 属性）、`MeteorAttackDefinition`、`NovaFireball`、`GameLog`、相机（`_player.MainCamera`）。
- Produces: 无（终端状态类，由 `WizardController` 构造与状态机调度）。

- [ ] **Step 1: 写 `PlayerWizardHeavyState.cs`**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师陨石重击状态：长按进入引导（原地定身，脚下光圈 + 地面落点圆框，鼠标经相机改变落点准心）；
    /// 松手进入施法（前摇后从落点斜上方天空生成陨石 NovaFireball，直线砸向落点）。数据来自 MeteorAttackDefinition。
    /// 引导/施法动画可选（状态名非空才 CrossFade）；施法节奏用计时器驱动，不依赖动画 normalizedTime。
    /// 平行于 PlayerChargeAttackState，但落点是地面（屏幕中心打地面）、且引导期间完全定身。
    /// </summary>
    public class PlayerWizardHeavyState : PlayerStateBase
    {
        private const int MaxAimHits = 16; // 落点射线一次最多记录命中数（预分配，零 GC）

        private readonly WizardController _wizard;
        private readonly RaycastHit[] _aimHits = new RaycastHit[MaxAimHits];

        private bool _released;            // 是否已松手进入施法阶段
        private bool _meteorSpawned;       // 施法阶段是否已生成过陨石（单点去重）
        private float _releaseTimer;       // 松手后计时（驱动生成与结束）
        private Vector3 _aimPoint;         // 当前(引导)/锁定(施法) 的落点
        private GameObject _ring;          // 脚下光圈实例
        private GameObject _indicator;     // 落点圆框实例

        public PlayerWizardHeavyState(WizardController player) : base(player)
        {
            _wizard = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _released = false;
            _meteorSpawned = false;
            _releaseTimer = 0f;
            _player.AttackBufferCounter = 0f;

            if (_wizard.MeteorData == null)
            {
                GameLog.Warn("法师 MeteorAttackDefinition 未配置，无法施放陨石", "Combat");
                TransitionToMovement();
                return;
            }

            _aimPoint = ComputeAimPoint();

            // 脚下光圈（跟随玩家，作为子物体）
            if (_wizard.ChannelRingPrefab != null)
                _ring = Object.Instantiate(_wizard.ChannelRingPrefab,
                    _player.transform.position, Quaternion.identity, _player.transform);
            // 落点圆框（世界空间，引导期每帧移动）
            if (_wizard.TargetIndicatorPrefab != null)
                _indicator = Object.Instantiate(_wizard.TargetIndicatorPrefab, _aimPoint, Quaternion.identity);

            // 引导动画（可空）
            if (_wizard.MeteorChannelHash != 0)
                _player.Animator.CrossFadeInFixedTime(_wizard.MeteorChannelHash, _wizard.MeteorData.CrossFadeDuration, 0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleRooted(); // 原地定身：只贴地，不水平移动

            if (!_released)
            {
                // 引导：每帧更新落点 + 移动圆框 + 转向落点；松手 → 施法
                _aimPoint = ComputeAimPoint();
                if (_indicator != null) _indicator.transform.position = _aimPoint;
                FaceTarget(_aimPoint);

                if (!_player.IsAttackHeld)
                    BeginRelease();
                return;
            }

            // 施法阶段：计时驱动（不依赖动画进度）
            _releaseTimer += Time.deltaTime;
            MeteorAttackDefinition data = _wizard.MeteorData;
            if (!_meteorSpawned && _releaseTimer >= data.MeteorSpawnDelay)
            {
                SpawnMeteor();
                _meteorSpawned = true;
            }
            if (_releaseTimer >= data.ReleaseDuration)
                TransitionToMovement();
        }

        public override void Exit()
        {
            _player.AttackBufferCounter = 0f;
            // 清理引导期视觉（任何退出路径都清，防泄漏）
            if (_ring != null) { Object.Destroy(_ring); _ring = null; }
            if (_indicator != null) { Object.Destroy(_indicator); _indicator = null; }
        }

        #endregion

        #region 处理流程

        private void BeginRelease()
        {
            _released = true;
            _releaseTimer = 0f;
            // 落点已锁定在 _aimPoint（施法阶段不再更新）；收起引导视觉
            if (_indicator != null) { Object.Destroy(_indicator); _indicator = null; }
            if (_ring != null) { Object.Destroy(_ring); _ring = null; }
            if (_wizard.MeteorReleaseHash != 0)
                _player.Animator.CrossFadeInFixedTime(_wizard.MeteorReleaseHash, _wizard.MeteorData.CrossFadeDuration, 0);
        }

        private void SpawnMeteor()
        {
            if (_wizard.MeteorPrefab == null)
            {
                GameLog.Warn("法师 MeteorPrefab 未配置，无法生成陨石", "Combat");
                return;
            }
            MeteorAttackDefinition data = _wizard.MeteorData;

            // 从落点斜上方天空生成：上方 SpawnHeight，并沿"玩家→落点"反方向后退 SpawnHorizontalBack，制造斜入射
            Vector3 horizDir = _aimPoint - _player.transform.position;
            horizDir.y = 0f;
            if (horizDir.sqrMagnitude < 1e-6f) horizDir = _player.transform.forward;
            horizDir.Normalize();

            Vector3 spawnPos = _aimPoint + Vector3.up * data.SpawnHeight - horizDir * data.SpawnHorizontalBack;
            Vector3 dir = _aimPoint - spawnPos;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.down;
            dir.Normalize();

            GameObject go = Object.Instantiate(_wizard.MeteorPrefab, spawnPos, Quaternion.LookRotation(dir));
            NovaFireball meteor = go.GetComponent<NovaFireball>();
            if (meteor == null)
            {
                GameLog.Warn("MeteorPrefab 上没有 NovaFireball 组件", "Combat");
                return;
            }

            byte team = _wizard.Health != null ? _wizard.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            // 直线飞向落点：关重力
            meteor.Init(team, attackerId, data.Damage, data.Type, dir * data.MeteorSpeed, _player.CharacterController, false);
        }

        /// <summary>
        /// 屏幕中心射线打地面求落点：命中(跳过自身) → 命中点；未命中 → 相机朝向远点投影到玩家脚高。
        /// 再按 AimMaxDistance 做水平距离上限钳制。
        /// </summary>
        private Vector3 ComputeAimPoint()
        {
            MeteorAttackDefinition data = _wizard.MeteorData;
            Camera cam = _player.MainCamera;
            Vector3 point;
            if (cam != null)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                if (!TryRaycastGround(ray, data.AimMaxDistance, out point))
                {
                    point = ray.GetPoint(data.AimMaxDistance);
                    point.y = _player.transform.position.y;
                }
            }
            else
            {
                point = _player.transform.position + _player.transform.forward * data.AimMaxDistance;
            }

            // 水平距离上限钳制（防落点过远）
            Vector3 flat = point - _player.transform.position;
            flat.y = 0f;
            float max = data.AimMaxDistance;
            if (flat.sqrMagnitude > max * max)
            {
                flat = flat.normalized * max;
                point.x = _player.transform.position.x + flat.x;
                point.z = _player.transform.position.z + flat.z;
            }
            return point;
        }

        /// <summary>RaycastNonAlloc 求最近地面命中，跳过自身碰撞体（零 GC）。</summary>
        private bool TryRaycastGround(Ray ray, float maxDistance, out Vector3 point)
        {
            point = default;
            int count = Physics.RaycastNonAlloc(ray, _aimHits, maxDistance,
                                                _wizard.AimMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (_aimHits[i].collider.transform.IsChildOf(_player.transform)) continue; // 跳过自身
                if (_aimHits[i].distance < nearest)
                {
                    nearest = _aimHits[i].distance;
                    point = _aimHits[i].point;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>引导期间把角色平滑转向落点的水平方向（只 yaw）。</summary>
        private void FaceTarget(Vector3 target)
        {
            Vector3 dir = target - _player.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return;
            Quaternion rot = Quaternion.LookRotation(dir);
            _player.transform.rotation = Quaternion.Slerp(
                _player.transform.rotation, rot, _player.RotationSpeed * Time.deltaTime);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        /// <summary>原地定身：只施加垂直贴地速度，不响应水平移动输入。</summary>
        private void HandleRooted()
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

- [ ] **Step 2: 静态自检**

确认：引导每帧的 `ComputeAimPoint` 走 `RaycastNonAlloc`(零 GC)；`Update` 内无 `new`/LINQ；离散分配只在 Enter(光圈/圆框)/松手(陨石)。`Exit` 必清光圈+圆框（防中断泄漏）。`_player.MainCamera` 已在基类暴露。

- [ ] **Step 3: 开发者在 Unity Editor 验证整体编译（Task 3 + Task 4）**

Expected: 全工程编译通过；`WizardController` 与 `PlayerWizardHeavyState` 互引用解析成功。

- [ ] **Step 4: Commit（用户确认后）**

```bash
git add "Assets/_Project/Scripts/Character/WizardController.cs" "Assets/_Project/Scripts/Character/States/PlayerWizardHeavyState.cs"
git commit -m "feat(character): Wizard meteor heavy attack (channel aim + meteor strike)"
```

---

### Task 5: Editor 接线与 Play 模式验证（开发者执行）

**Files:** 无（创建 SO + 配置预制体/角色）。

- [ ] **Step 1: 创建陨石数据 SO**
  - `Create → Game/Combat/Meteor Attack Definition`，命名 `Meteor_Wizard`：
    - `Tap Threshold` ≈ 0.15（与火球普攻 tap/hold 分界）。
    - `Aim Max Distance` ≈ 30（落点最大距离）。
    - `Meteor Spawn Delay` / `Release Duration`：先 0.3 / 0.6，按手感调（Release Duration ≥ Spawn Delay）。
    - `Meteor Speed` ≈ 40、`Spawn Height` ≈ 25、`Spawn Horizontal Back` ≈ 10（调入射角；越大越斜）。
    - `Damage` / `Type=Magical`。
    - `Channel State Name` / `Release State Name`：**建议至少填引导名**避免定身时"原地跑"（见下）。若 WizardHero 暂无专用施法动画，可先填 `Attack01_MagicWand`（两者都填它），或留空（角色保持当前姿势）。

- [ ] **Step 2: 配置 NovaFireball（陨石）预制体**
  - 打开 `Assets/_Project/Art/Prefabs/Wizard/FireMagic/NovaFireball.prefab`：
    - 加 `Rigidbody`（建议 `Collision Detection = Continuous`，陨石速度快防穿透）。
    - 加 非 Trigger `Collider`（如 `SphereCollider`）。
    - 加 `NovaFireball` 脚本：`Explosion Prefab` = `NovaExplosion_Hit`；`Explosion Lifetime` 按特效时长；`Max Lifetime` 默认 5。

- [ ] **Step 3: 在 WizardController 上接线重击**
  - `Meteor Data` = `Meteor_Wizard`；`Meteor Prefab` = `NovaFireball`；`Channel Ring Prefab` = `NovaFireball_Skill_Start`；`Target Indicator Prefab` = `NovaFireball_Pre_Field`。
  - `Aim Mask`：**只勾地面/环境层**（落点应落在地面，不要勾 Player/敌人层，免准心吸附到角色身上）。

- [ ] **Step 4: WizardHero Animator（可选施法动画）**
  - 若 SO 填了 `Channel/Release State Name`：在 Controller 里建对应状态（clip 拖入），并给它们画 `→ Idle_Bow` 的退出过渡（引导态可循环；施法态 Has Exit Time）。**进入靠代码、退出靠连线**。
  - 提示：引导期间角色定身，但基类每帧仍按移动输入写 `speed` 参数——若不进入一个非移动的施法/引导状态，按住 WASD 会出现"原地跑"。所以**建议填 `Channel State Name`**（哪怕复用 `Attack01_MagicWand`）。

- [ ] **Step 5: Play 模式验收清单**
  - 轻点左键 = 火球普攻（不变）；长按左键 > 阈值 = 进入引导：脚下出现光圈、前方地面出现落点圆框。
  - 引导期间**人不能走**（WASD 无位移）；移动鼠标（带动相机）→ 落点圆框随屏幕中心在地面移动；角色转向落点。
  - 松开左键：圆框/光圈消失，约 `Meteor Spawn Delay` 后从落点斜上方天空出现陨石，直线砸向落点。
  - 陨石命中角色：生成 `NovaExplosion_Hit`，目标掉血 + 闪红（CharacterCombatFeedback）；命中地面也爆炸。
  - 普通火球普攻仍正常（直线、爆炸、引燃）——确认 Task 1 迁移没破坏它。
  - 箭矢（弓箭手）普攻仍是抛物线、蓄力直射仍命中准心——确认 ProjectileBase 迁移没破坏 Arrow。
  - Profiler：引导期每帧 0 GC Alloc（落点射线零分配）。

- [ ] **Step 6: Commit 资产（用户确认后）**

```bash
git add "Assets/_Project/ScriptableObjects" "Assets/_Project/Art/Prefabs/Wizard" "Assets/_Project/Scenes/SampleScene.unity"
git commit -m "feat(wizard): wire meteor SO, NovaFireball prefab, controller refs"
```

---

## 已知限制与后续可加强

- **引导不可取消**：一旦进入引导，松手必放陨石（无"取消引导"路径）。要支持取消可加一个取消键或最短引导时间。
- **施法节奏用计时器而非动画事件**：因 WizardHero 暂无专用施法动画，用 `MeteorSpawnDelay/ReleaseDuration` 计时驱动，稳但与动画对齐需手调。后续有专用施法动画后，可改回 `normalizedTime` 单点（仿蓄力态）。
- **陨石为单体**：命中第一个碰撞体即爆炸结算单体伤害（与火球同款）。要做范围伤害(AoE)需在 `NovaFireball.OnImpact` 里做 `OverlapSphere` 群体结算——可作为后续增强。
- **落点圆框/光圈的消失是瞬时销毁**：粒子会立刻消失，原型可接受；要平滑可改为"停止发射 + 延时销毁"。
- **引导期"原地跑"**：见 Task 5 Step 4，建议填 `Channel State Name` 规避。
- **ProjectileBase.Init 默认 useGravity=true**：这是为保留 Arrow 抛物线与既有调用点而定；所有直线投射物（火球/蓄力直射/陨石）都必须在生成处显式传 false（本计划已覆盖三处）。新增直线投射物时勿忘。
```

