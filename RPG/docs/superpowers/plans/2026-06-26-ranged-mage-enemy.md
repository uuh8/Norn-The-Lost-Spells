# 远程法师敌人 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增"远程法师敌人"——保持距离、朝玩家发射火球；并把现有敌人重构为 base+subclass(近战/远程)以共享通用逻辑。

**Architecture:** 抽出抽象基类 `EnemyControllerBase`(对标 `PlayerControllerBase`)持有共享组件/感知/状态机/共享态(Idle+Hurt)/行动能力/受击死亡;现 `EnemyController` 改名 `MeleeEnemyController`(贴身近战),新增 `RangedEnemyController`(保距离施法)。两子类经抽象接缝 `EngageState` 提供各自"战斗走位状态"。远程走位用范围档(远追/近退/中间站档输出),火球复用玩家 `Fireball` 预制、`Init` 注入敌方阵营。

**Tech Stack:** Unity 6.3 / URP / C# / 手写 FSM / ScriptableObject 数据驱动 / `normalizedTime` 动画时序 / `EventBus<T>` / `ProjectileBase`(Fireball)。

## Global Constraints

逐条来自 `CLAUDE.md`，每个任务隐式包含：

- **编译/连线/Play 由开发者在 Unity Editor 手动完成**——本计划无自动化测试运行；每个任务以"开发者在 Editor 验证"收尾。只编辑 `.cs`/文本配置，**不创建 `.meta`**(Unity 自动生成)。
- **重命名 MonoBehaviour 脚本**：`.cs` 与其 `.meta` **一起 `git mv`** 保 GUID，使预制/场景引用不丢、序列化字段不重置。
- **模块依赖单向**：敌人代码在 `Game.Character`，可依赖 `Game.Combat`/`Game.Core`；反向不可。`EnemyDefinition`(在 `Game.Combat`)只可引用同模块类型。
- **Unity 序列化按字段名跨类继承层级**：把字段从子类移到基类、保持字段名不变 → 预制体序列化值保留。
- **命名**：私有/保护字段 `_camelCase`；公开成员/类型 `PascalCase`；命名空间匹配程序集。
- **性能**：`Update`/每帧热路径禁止 `new`/LINQ/装箱；状态/感知 `Awake` 预实例化；动画名 `StringToHash` 缓存一次。
- **日志**用 `Game.Core.GameLog`，不用 `Debug.Log`。`CharacterController` 在 `Update` 里 `Move`。
- **提交**：仅在开发者要求时提交；Conventional Commits；message 末尾加 `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`。

## 文件结构

**新建**
- `Assets/_Project/Scripts/Character/Controllers/EnemyControllerBase.cs` — 抽象基类(共享一切)。
- `Assets/_Project/Scripts/Character/Controllers/RangedEnemyController.cs` — 远程子类(走位接缝 + 发射火球)。
- `Assets/_Project/Scripts/Character/Enemy/States/EnemyKiteState.cs` — 远程走位(远追/近退/站档)。
- `Assets/_Project/Scripts/Character/Enemy/States/EnemyRangedAttackState.cs` — 远程施法(单点发火球)。

**改名(git mv .cs + .meta)**
- `EnemyController.cs` → `MeleeEnemyController.cs`(类名同步改，留近战专属)。

**修改**
- `EnemyStateBase.cs` — `_enemy` 类型 → `EnemyControllerBase`。
- `EnemyPerception.cs` — ctor 形参类型 → `EnemyControllerBase`。
- `EnemyIdleState.cs` / `EnemyHurtState.cs` — 走 `EngageState`。
- `EnemyChaseState.cs` / `EnemyAttackState.cs` — typed 到 `MeleeEnemyController`、Finish 走 `EngageState`。
- `EnemyDefinition.cs` — 加远程字段。

> **编译约束**：Unity 按整程序集编译，`Game.Character` 任一文件引用未定义类型会令整程序集编译失败。Task 1 是一次**原子重构**(多文件同改、行为不变);Task 2/3 增量新增。

---

## Task 1：重构为 base + MeleeEnemyController（行为不变）

把现有敌人拆成抽象基类 + 近战子类，共享态/感知改用基类型。**交付物：现有近战敌人行为与重构前完全一致**(纯重构)。

**Files:**
- Create: `Assets/_Project/Scripts/Character/Controllers/EnemyControllerBase.cs`
- Rename: `EnemyController.cs`(+`.meta`) → `MeleeEnemyController.cs`
- Modify: `EnemyStateBase.cs`, `EnemyPerception.cs`, `EnemyIdleState.cs`, `EnemyHurtState.cs`, `EnemyChaseState.cs`, `EnemyAttackState.cs`

**Interfaces:**
- Produces:
  - `EnemyControllerBase`(abstract MonoBehaviour)：`Definition`、`Perception`、`StateMachine`、`IdleState`、`HurtState`、`AttackStateHash`、`HurtStateHash`、`AttackCooldownCounter`、`IsDead`、`Animator`；`abstract EnemyStateBase EngageState { get; }`；行动 `MoveTo(Vector3)`/`MoveAway(Vector3)`/`StayGrounded()`/`FaceTarget(Vector3)`/`CrossFade(int)`；`protected virtual void OnDied()`；`protected EnemyDefinition _definition`、`protected CharacterController _cc`、`protected Animator _animator`；`protected virtual void Awake()`。
  - `MeleeEnemyController : EnemyControllerBase`：`ChaseState`、`AttackState`、`OpenAttackWindow()`、`CloseAttackWindow()`、`EngageState => ChaseState`。
  - `EnemyStateBase._enemy` 类型为 `EnemyControllerBase`。

- [ ] **Step 1: 改名近战控制器（保 GUID）**

```bash
cd "F:/Develop Project/RPG/RPG"
git mv Assets/_Project/Scripts/Character/Controllers/EnemyController.cs Assets/_Project/Scripts/Character/Controllers/MeleeEnemyController.cs
git mv Assets/_Project/Scripts/Character/Controllers/EnemyController.cs.meta Assets/_Project/Scripts/Character/Controllers/MeleeEnemyController.cs.meta
```

- [ ] **Step 2: 创建 `EnemyControllerBase.cs`（抽象基类，逐字）**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 敌人控制器基类（总装 + 行动层）。对标 PlayerControllerBase：持有共享组件/感知/状态机/共享态(Idle+Hurt)，
    /// Update 跑 感知→状态机→同步 Animator，对状态暴露行动能力。具体"战斗走位 + 攻击"由子类经 EngageState 接缝提供：
    /// MeleeEnemyController(贴身近战) / RangedEnemyController(保距离施法)。决策层(状态)只决定做什么，怎么做在本类/子类。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(HealthComponent))]
    public abstract class EnemyControllerBase : MonoBehaviour
    {
        [Header("数据")]
        [SerializeField] protected EnemyDefinition _definition;

        [Header("调参")]
        [SerializeField] private float _rotationSpeed = 10f;
        [SerializeField] private float _gravity = -20f;

        protected CharacterController _cc;
        protected Animator _animator;

        private EnemyPerception _perception;
        private EnemyStateMachine _stateMachine;
        private EnemyIdleState _idleState;
        private EnemyHurtState _hurtState;
        private int _attackStateHash;
        private int _hurtStateHash;
        private int _id;
        private bool _dead;

        private float _verticalVelocity;

        private static readonly int SpeedHash = Animator.StringToHash("speed");

        public EnemyDefinition Definition => _definition;
        public EnemyPerception Perception => _perception;
        public EnemyStateMachine StateMachine => _stateMachine;
        public EnemyIdleState IdleState => _idleState;
        public EnemyHurtState HurtState => _hurtState;
        public int AttackStateHash => _attackStateHash;
        public int HurtStateHash => _hurtStateHash;
        public float AttackCooldownCounter { get; set; }
        public bool IsDead => _dead;
        public Animator Animator => _animator;

        /// <summary>进入战斗后使用的走位状态（多态接缝）：近战=ChaseState，远程=KiteState。</summary>
        public abstract EnemyStateBase EngageState { get; }

        protected virtual void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>();

            _perception = new EnemyPerception(this);
            _stateMachine = new EnemyStateMachine();
            _idleState = new EnemyIdleState(this);
            _hurtState = new EnemyHurtState(this);

            // 攻击/施法动画状态名取自攻击数据(数据驱动)；空 → 0 → CrossFade 不切动画
            string atkStateName = (_definition != null && _definition.Attack != null)
                ? _definition.Attack.AnimationStateName : null;
            _attackStateHash = string.IsNullOrEmpty(atkStateName) ? 0 : Animator.StringToHash(atkStateName);
            if (_attackStateHash == 0)
                GameLog.Warn("敌人攻击动画状态名为空，攻击 CrossFade 无法切换动画", "Enemy");

            _hurtStateHash = (_definition != null && !string.IsNullOrEmpty(_definition.HurtStateName))
                ? Animator.StringToHash(_definition.HurtStateName) : 0;
            _id = gameObject.GetInstanceID();

            if (_definition == null)
                GameLog.Warn("EnemyController 未配置 EnemyDefinition", "Enemy");
        }

        private void Start()
        {
            _stateMachine.ChangeState(_idleState);
        }

        protected virtual void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        protected virtual void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            if (_dead || e.TargetId != _id) return;
            if (e.RemainingHp <= 0f) return;        // 致死那一击交给 OnDeath，不进硬直
            if (!e.TriggerHitReaction) return;       // DoT/环境跳伤：不进硬直，避免被持续伤害锁死
            _stateMachine.ChangeState(_hurtState);
        }

        private void OnDeath(DeathEvent e)
        {
            if (_dead || e.TargetId != _id) return;
            _dead = true;
            OnDied(); // 子类收尾(如关近战命中窗口)；死亡动画+销毁由 CharacterCombatFeedback 负责
        }

        /// <summary>死亡时子类收尾钩子（默认空）。</summary>
        protected virtual void OnDied() { }

        private void Update()
        {
            if (_dead) return;
            if (AttackCooldownCounter > 0f) AttackCooldownCounter -= Time.deltaTime;
            _perception.Tick();
            _stateMachine.Update();
            SyncAnimator();
        }

        private void SyncAnimator()
        {
            if (_animator == null) return;
            Vector3 v = _cc.velocity; v.y = 0f;
            _animator.SetFloat(SpeedHash, v.magnitude);
        }

        #region 行动能力（供状态调用）

        /// <summary>朝目标水平移动(含重力)。</summary>
        public void MoveTo(Vector3 targetPos) => MoveHorizontal(targetPos - transform.position);

        /// <summary>远离目标水平移动(后撤，含重力)。</summary>
        public void MoveAway(Vector3 targetPos) => MoveHorizontal(transform.position - targetPos);

        private void MoveHorizontal(Vector3 dir)
        {
            dir.y = 0f;
            Vector3 horizontal = dir.sqrMagnitude > 1e-6f
                ? dir.normalized * _definition.MoveSpeed : Vector3.zero;
            ApplyGravity();
            Vector3 velocity = horizontal;
            velocity.y = _verticalVelocity;
            _cc.Move(velocity * Time.deltaTime);
        }

        /// <summary>原地不动，仅施加重力贴地。</summary>
        public void StayGrounded()
        {
            ApplyGravity();
            _cc.Move(Vector3.up * _verticalVelocity * Time.deltaTime);
        }

        /// <summary>平滑转向目标的水平方向(只 yaw)。</summary>
        public void FaceTarget(Vector3 targetPos)
        {
            Vector3 dir = targetPos - transform.position; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return;
            Quaternion rot = Quaternion.LookRotation(dir);
            transform.rotation = Quaternion.Slerp(transform.rotation, rot, _rotationSpeed * Time.deltaTime);
        }

        /// <summary>数据驱动 CrossFade 进入指定 Animator 状态（hash 为 0 静默跳过）。</summary>
        public void CrossFade(int stateHash)
        {
            if (_animator != null && stateHash != 0)
                _animator.CrossFadeInFixedTime(stateHash, _definition.CrossFadeDuration, 0);
        }

        private void ApplyGravity()
        {
            if (_cc.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
            else _verticalVelocity += _gravity * Time.deltaTime;
        }

        #endregion
    }
}
```

- [ ] **Step 3: 把 `MeleeEnemyController.cs` 整文件替换为近战子类（逐字）**

(Step 1 已把文件改名；现替换其内容)

```csharp
using UnityEngine;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 近战敌人：贴身挥击。在 EnemyControllerBase 之上加近战命中(MeleeHitDetector)与近战专属状态(Chase/Attack)。
    /// EngageState = ChaseState(接近到 AttackRange 内停下出招)。由 EnemyController 改名而来(.cs+.meta 保 GUID)。
    /// </summary>
    public class MeleeEnemyController : EnemyControllerBase
    {
        [Header("近战")]
        [Tooltip("近战命中判定；其 TeamId 必须=本敌人 HealthComponent.TeamId(敌方)。攻击窗口由攻击状态开关")]
        [SerializeField] private MeleeHitDetector _hitDetector;

        private EnemyChaseState _chaseState;
        private EnemyAttackState _attackState;

        public EnemyChaseState ChaseState => _chaseState;
        public EnemyAttackState AttackState => _attackState;

        public override EnemyStateBase EngageState => _chaseState;

        protected override void Awake()
        {
            base.Awake();
            _chaseState = new EnemyChaseState(this);
            _attackState = new EnemyAttackState(this);
            // 把 SO 的攻击数据注入命中判定器，保证两者一致
            if (_hitDetector != null && _definition != null && _definition.Attack != null)
                _hitDetector.SetAttack(_definition.Attack);
        }

        protected override void OnDied()
        {
            if (_hitDetector != null) _hitDetector.CloseHitWindow();
        }

        /// <summary>开启近战命中窗口（攻击状态在 HitActiveStart 调用）。</summary>
        public void OpenAttackWindow() { if (_hitDetector != null) _hitDetector.OpenHitWindow(); }
        /// <summary>关闭近战命中窗口。</summary>
        public void CloseAttackWindow() { if (_hitDetector != null) _hitDetector.CloseHitWindow(); }
    }
}
```

- [ ] **Step 4: `EnemyStateBase.cs` 改 `_enemy` 类型为基类**

把字段与构造函数形参的 `EnemyController` 改为 `EnemyControllerBase`：

```csharp
        protected readonly EnemyControllerBase _enemy;
        protected EnemyStateBase(EnemyControllerBase enemy) { _enemy = enemy; }
```

(其余 Enter/Update/Exit 抽象方法不变。)

- [ ] **Step 5: `EnemyPerception.cs` ctor 形参类型改基类**

把字段与构造函数形参的 `EnemyController` 改为 `EnemyControllerBase`：

```csharp
        private readonly EnemyControllerBase _enemy;

        public EnemyPerception(EnemyControllerBase enemy) { _enemy = enemy; }
```

(`Tick()` 内部用 `_enemy.Definition`/`_enemy.transform`，签名不变。)

- [ ] **Step 6: `EnemyIdleState.cs` 走 EngageState**

把 `Update` 内的转换目标由 `_enemy.ChaseState` 改为 `_enemy.EngageState`：

```csharp
        public override void Update()
        {
            _enemy.StayGrounded();
            if (_enemy.Perception.HasTarget)
                _enemy.StateMachine.ChangeState(_enemy.EngageState);
        }
```

(构造函数形参类型随基类自动适配——`EnemyIdleState(EnemyControllerBase enemy) : base(enemy)`，若当前写的是 `EnemyController` 请改为 `EnemyControllerBase`。)

- [ ] **Step 7: `EnemyHurtState.cs` 走 EngageState**

把恢复时的转换 `_enemy.ChaseState` 改为 `_enemy.EngageState`：

```csharp
            if (_timer <= 0f)
            {
                EnemyPerception p = _enemy.Perception;
                _enemy.StateMachine.ChangeState(p.HasTarget ? _enemy.EngageState : _enemy.IdleState);
            }
```

(构造函数形参类型为 `EnemyControllerBase`。)

- [ ] **Step 8: `EnemyChaseState.cs` 整文件替换（typed 到 MeleeEnemyController）**

```csharp
using UnityEngine;

namespace Game.Character
{
    /// <summary>近战追击：朝玩家移动并转向；进入攻击距离且冷却就绪 → 出招，冷却中则停下等待；丢失目标回待机。</summary>
    public class EnemyChaseState : EnemyStateBase
    {
        private readonly MeleeEnemyController _melee; // typed 子类引用，取近战专属(AttackState)

        public EnemyChaseState(MeleeEnemyController enemy) : base(enemy) { _melee = enemy; }

        public override void Enter() { }

        public override void Update()
        {
            EnemyPerception p = _enemy.Perception;
            if (!p.HasTarget)
            {
                _enemy.StateMachine.ChangeState(_enemy.IdleState);
                return;
            }

            Vector3 targetPos = p.Target.position;
            _enemy.FaceTarget(targetPos);

            if (p.DistanceToTarget <= _enemy.Definition.AttackRange)
            {
                if (_enemy.AttackCooldownCounter <= 0f)
                {
                    _enemy.StateMachine.ChangeState(_melee.AttackState);
                    return;
                }
                _enemy.StayGrounded(); // 在攻击距离但冷却中：停下等待
                return;
            }

            _enemy.MoveTo(targetPos);
        }

        public override void Exit() { }
    }
}
```

- [ ] **Step 9: `EnemyAttackState.cs` 整文件替换（typed 到 MeleeEnemyController，Finish 走 EngageState）**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 近战出招：原地播攻击动画；按 normalizedTime 在 [HitActiveStart, HitActiveEnd] 开/关命中窗口(复用 MeleeHitDetector)。
    /// 结束判定与 Animator 退出连线协作(进入攻击态后又离开即结束 + EndThreshold/超时兜底)，避免卡死。Exit 关窗 + 启动冷却。
    /// </summary>
    public class EnemyAttackState : EnemyStateBase
    {
        private const float EndThreshold = 0.95f;
        private const float MaxStateTime = 3f;

        private readonly MeleeEnemyController _melee;
        private bool _windowOpen;
        private bool _enteredAnimState;
        private float _elapsed;

        public EnemyAttackState(MeleeEnemyController enemy) : base(enemy) { _melee = enemy; }

        public override void Enter()
        {
            _windowOpen = false;
            _enteredAnimState = false;
            _elapsed = 0f;
            if (_enemy.Perception.Target != null)
                _enemy.FaceTarget(_enemy.Perception.Target.position);
            _enemy.CrossFade(_enemy.AttackStateHash);
        }

        public override void Update()
        {
            _enemy.StayGrounded();
            _elapsed += Time.deltaTime;

            Animator anim = _enemy.Animator;
            AttackDefinition atk = _enemy.Definition != null ? _enemy.Definition.Attack : null;
            if (anim == null || atk == null) { Finish(); return; }

            if (!anim.IsInTransition(0))
            {
                AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(0);
                if (info.shortNameHash == _enemy.AttackStateHash)
                {
                    _enteredAnimState = true;
                    float t = info.normalizedTime % 1f;
                    if (!_windowOpen && t >= atk.HitActiveStart) { _melee.OpenAttackWindow(); _windowOpen = true; }
                    if (_windowOpen && t >= atk.HitActiveEnd) { _melee.CloseAttackWindow(); _windowOpen = false; }
                    if (t >= EndThreshold) Finish();
                    return;
                }
                if (_enteredAnimState) { Finish(); return; }
            }

            if (!_enteredAnimState && _elapsed >= MaxStateTime)
            {
                GameLog.Warn("敌人攻击迟迟未进入攻击动画态(检查 AttackDefinition.AnimationStateName 是否与 Animator 节点名精确一致)，超时结束", "Enemy");
                Finish();
            }
        }

        private void Finish()
        {
            EnemyPerception p = _enemy.Perception;
            _enemy.StateMachine.ChangeState(p.HasTarget ? _enemy.EngageState : _enemy.IdleState);
        }

        public override void Exit()
        {
            if (_windowOpen) { _melee.CloseAttackWindow(); _windowOpen = false; }
            _enemy.AttackCooldownCounter = _enemy.Definition != null ? _enemy.Definition.AttackCooldown : 0f;
        }
    }
}
```

- [ ] **Step 10: 开发者验证（Editor）—— 纯重构，行为不变**
  - 用 IDE/Grep 全局搜索 `EnemyController`（不含 `EnemyControllerBase`）确认无残留引用（除注释）。
  - Unity 编译通过、无报错。
  - 选中场景里现有近战敌人，确认其组件已自动变为 `MeleeEnemyController`、`Definition`/`Hit Detector` 等序列化值仍在（GUID 保留的结果）。
  - 进入 Play：现有近战敌人**行为与重构前完全一致**——侦测→追击→到攻击距离前摇出招打玩家→被打硬直→击杀死亡消失。

- [ ] **Step 11: 提交**

```bash
git add Assets/_Project/Scripts/Character/
git commit -m "refactor(character): extract EnemyControllerBase, rename EnemyController to MeleeEnemyController"
```
(message 末尾补 `Co-Authored-By` 行。)

---

## Task 2：EnemyDefinition 远程字段

**Files:**
- Modify: `Assets/_Project/Scripts/Combat/Definitions/EnemyDefinition.cs`

**Interfaces:**
- Consumes: 无新依赖。
- Produces: `EnemyDefinition` 新增公开字段 `RetreatDistance:float`、`ProjectileSpeed:float`、`ProjectilePrefab:GameObject`。

- [ ] **Step 1: 在 `CrossFade` 分组之前插入"远程"分组**

在 `EnemyDefinition` 里 `[Header("CrossFade")]` 那一行**之前**插入：

```csharp
        [Header("远程 (仅远程敌人 RangedEnemyController 使用)")]
        [Tooltip("玩家比此距离更近时后撤；与 AttackRange 一起构成站档输出的范围带 [RetreatDistance, AttackRange]")]
        public float RetreatDistance = 4f;
        [Tooltip("火球飞行速度")]
        public float ProjectileSpeed = 18f;
        [Tooltip("发射的投射物预制体(拖 Fireball)；其上需有 Fireball 组件")]
        public GameObject ProjectilePrefab;
```

- [ ] **Step 2: 开发者验证（Editor）**
  - 编译通过。
  - 选中任一 `EnemyDefinition` 资产，Inspector 出现"远程"分组三个字段（近战敌人留空即可，不影响）。

- [ ] **Step 3: 提交**

```bash
git add Assets/_Project/Scripts/Combat/Definitions/EnemyDefinition.cs
git commit -m "feat(combat): add ranged fields (retreat distance, projectile) to EnemyDefinition"
```
(末尾补 `Co-Authored-By` 行。)

---

## Task 3：RangedEnemyController + 走位/施法状态

**Files:**
- Create: `Assets/_Project/Scripts/Character/Controllers/RangedEnemyController.cs`
- Create: `Assets/_Project/Scripts/Character/Enemy/States/EnemyKiteState.cs`
- Create: `Assets/_Project/Scripts/Character/Enemy/States/EnemyRangedAttackState.cs`

**Interfaces:**
- Consumes: `EnemyControllerBase`(Task 1)；`EnemyDefinition.{RetreatDistance, ProjectileSpeed, ProjectilePrefab, AttackRange, Attack, AttackCooldown}`(Task 1/2)；`Fireball`/`ProjectileBase.Init(byte,int,float,DamageType,Vector3,Collider,bool)`；`AttackDefinition.{BaseAmount, Type, AnimationStateName, ArrowSpawnTime}`。
- Produces: `RangedEnemyController : EnemyControllerBase`：`KiteState`、`RangedAttackState`、`EngageState => KiteState`、`void SpawnFireball()`；`EnemyKiteState`、`EnemyRangedAttackState`。

- [ ] **Step 1: 创建 `EnemyKiteState.cs`（逐字）**

```csharp
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 远程走位：远追 / 近退 / 中间站档输出。始终面向玩家。
    ///   距离 > AttackRange      → 接近(MoveTo)
    ///   距离 < RetreatDistance  → 后撤(MoveAway，被贴脸时拉开)
    ///   在 [RetreatDistance, AttackRange] 档内且冷却就绪 → 切施法态；冷却中 → 站定等待
    /// 丢失目标 → 回待机。
    /// </summary>
    public class EnemyKiteState : EnemyStateBase
    {
        private readonly RangedEnemyController _ranged;

        public EnemyKiteState(RangedEnemyController enemy) : base(enemy) { _ranged = enemy; }

        public override void Enter() { }

        public override void Update()
        {
            EnemyPerception p = _enemy.Perception;
            if (!p.HasTarget)
            {
                _enemy.StateMachine.ChangeState(_enemy.IdleState);
                return;
            }

            Vector3 targetPos = p.Target.position;
            _enemy.FaceTarget(targetPos); // 始终朝向玩家(便于施法/后撤朝向正确)

            EnemyDefinition def = _enemy.Definition;
            float dist = p.DistanceToTarget;

            if (dist > def.AttackRange)         // 太远 → 接近
            {
                _enemy.MoveTo(targetPos);
                return;
            }
            if (dist < def.RetreatDistance)     // 太近 → 后撤
            {
                _enemy.MoveAway(targetPos);
                return;
            }

            // 档内：冷却就绪 → 施法；否则站定等待
            if (_enemy.AttackCooldownCounter <= 0f)
            {
                _enemy.StateMachine.ChangeState(_ranged.RangedAttackState);
                return;
            }
            _enemy.StayGrounded();
        }

        public override void Exit() { }
    }
}
```

- [ ] **Step 2: 创建 `EnemyRangedAttackState.cs`（逐字）**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 远程施法：站定播施法动画，到 Attack.ArrowSpawnTime 单点发射火球(单发去重 + 排除过渡帧 + shortNameHash 校验)。
    /// 结束判定与 Animator 退出连线协作(进入施法态后又离开即结束 + EndThreshold/超时兜底)，避免卡死。Exit 启动冷却。
    /// 注：施法动画名(AttackDefinition.AnimationStateName)需与 Animator 节点精确一致，否则进不去施法态、超时空放。
    /// </summary>
    public class EnemyRangedAttackState : EnemyStateBase
    {
        private const float EndThreshold = 0.95f;
        private const float MaxStateTime = 3f;

        private readonly RangedEnemyController _ranged;
        private bool _fired;
        private bool _enteredAnimState;
        private float _elapsed;

        public EnemyRangedAttackState(RangedEnemyController enemy) : base(enemy) { _ranged = enemy; }

        public override void Enter()
        {
            _fired = false;
            _enteredAnimState = false;
            _elapsed = 0f;
            if (_enemy.Perception.Target != null)
                _enemy.FaceTarget(_enemy.Perception.Target.position);
            _enemy.CrossFade(_enemy.AttackStateHash);
        }

        public override void Update()
        {
            _enemy.StayGrounded(); // 施法原地不动
            _elapsed += Time.deltaTime;

            Animator anim = _enemy.Animator;
            AttackDefinition atk = _enemy.Definition != null ? _enemy.Definition.Attack : null;
            if (anim == null || atk == null) { Finish(); return; }

            if (!anim.IsInTransition(0))
            {
                AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(0);
                if (info.shortNameHash == _enemy.AttackStateHash)
                {
                    _enteredAnimState = true;
                    float t = info.normalizedTime % 1f;
                    if (!_fired && t >= atk.ArrowSpawnTime) { _ranged.SpawnFireball(); _fired = true; }
                    if (t >= EndThreshold) Finish();
                    return;
                }
                if (_enteredAnimState) { Finish(); return; }
            }

            if (!_enteredAnimState && _elapsed >= MaxStateTime)
            {
                GameLog.Warn("远程敌人施法迟迟未进入施法动画态(检查 AttackDefinition.AnimationStateName)，超时结束", "Enemy");
                Finish();
            }
        }

        private void Finish()
        {
            EnemyPerception p = _enemy.Perception;
            _enemy.StateMachine.ChangeState(p.HasTarget ? _enemy.EngageState : _enemy.IdleState);
        }

        public override void Exit()
        {
            _enemy.AttackCooldownCounter = _enemy.Definition != null ? _enemy.Definition.AttackCooldown : 0f;
        }
    }
}
```

- [ ] **Step 3: 创建 `RangedEnemyController.cs`（逐字）**

```csharp
using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 远程敌人：保持距离施法。EngageState = KiteState(远追/近退/中间站档输出)；
    /// 施法态在 ArrowSpawnTime 朝玩家(胸口高度)直线发射火球——复用玩家 Fireball 预制，Init 注入敌方阵营 → 只伤玩家、自带爆炸+点燃。
    /// </summary>
    public class RangedEnemyController : EnemyControllerBase
    {
        [Header("远程")]
        [Tooltip("火球生成点(法杖前端等)，必须是本敌人下的子物体")]
        [SerializeField] private Transform _projectileSpawnPoint;
        [Tooltip("瞄准玩家时的目标高度偏移(打向胸口而非脚下)")]
        [SerializeField] private float _aimHeightOffset = 1.0f;

        private HealthComponent _health;
        private EnemyKiteState _kiteState;
        private EnemyRangedAttackState _rangedAttackState;

        public EnemyKiteState KiteState => _kiteState;
        public EnemyRangedAttackState RangedAttackState => _rangedAttackState;
        public override EnemyStateBase EngageState => _kiteState;

        protected override void Awake()
        {
            base.Awake();
            _health = GetComponent<HealthComponent>();
            _kiteState = new EnemyKiteState(this);
            _rangedAttackState = new EnemyRangedAttackState(this);
        }

        /// <summary>朝玩家(胸口高度)直线发射一颗火球，注入敌方阵营。由施法态在 ArrowSpawnTime 调用。</summary>
        public void SpawnFireball()
        {
            if (_definition == null || _definition.ProjectilePrefab == null || _projectileSpawnPoint == null)
            {
                GameLog.Warn("远程敌人 ProjectilePrefab/生成点未配置，无法发射", "Enemy");
                return;
            }

            Transform target = Perception.Target;
            Vector3 aimPoint = target != null
                ? target.position + Vector3.up * _aimHeightOffset
                : _projectileSpawnPoint.position + transform.forward;

            Vector3 dir = aimPoint - _projectileSpawnPoint.position;
            if (dir.sqrMagnitude < 1e-6f) dir = transform.forward;
            dir.Normalize();

            GameObject go = Instantiate(_definition.ProjectilePrefab, _projectileSpawnPoint.position,
                                        Quaternion.LookRotation(dir));
            Fireball fireball = go.GetComponent<Fireball>();
            if (fireball == null)
            {
                GameLog.Warn("ProjectilePrefab 上没有 Fireball 组件", "Enemy");
                return;
            }

            byte team = _health != null ? _health.TeamId : (byte)0;
            int attackerId = gameObject.GetInstanceID();
            AttackDefinition atk = _definition.Attack;
            float dmg = atk != null ? atk.BaseAmount : 0f;
            DamageType type = atk != null ? atk.Type : DamageType.Magical;
            Vector3 velocity = dir * _definition.ProjectileSpeed;
            // 直线飞行：火球关重力
            fireball.Init(team, attackerId, dmg, type, velocity, _cc, useGravity: false);
        }
    }
}
```

- [ ] **Step 4: 开发者验证（Editor）—— 远程敌人能 kite + 发火球**
  - 编译通过。
  - 临时搭一个远程敌人：空物体 + `CharacterController` + `HealthComponent`(TeamId=敌方，如 0) + `Animator`(带 `speed` 参数 + 一个施法 state + 退出连线 + GetHit/Die) + `CharacterCombatFeedback`(getHit 留空、die 填对、血特效) + 一个子物体作 `_projectileSpawnPoint` + `RangedEnemyController`。
  - 给它一份 `EnemyDefinition`：`Attack`=一份 `AttackDefinition`(`AnimationStateName`=施法节点名精确一致、`ArrowSpawnTime`≈0.4、`BaseAmount`=伤害、`Type`=Magical)、`AttackRange`(站档外沿如 12)、`RetreatDistance`(内沿如 5，<AttackRange)、`DetectRadius`≥AttackRange(如 14)、`LoseRadius`>DetectRadius、`ProjectilePrefab`=Fireball、`ProjectileSpeed`(如 18)、`HurtStateName`=受击节点名。把 SO 拖给 `RangedEnemyController.Definition`，子物体拖给 `Projectile Spawn Point`。
  - 进入 Play：玩家走近 → 敌人保持在档内、**站定朝你发火球**(命中→掉血/爆炸/点燃)；你**贴脸** → 敌人**后撤**拉开;你**走远超出 AttackRange** → 敌人**接近**;走出 LoseRadius → 脱战回待机;打它 → 硬直/死亡正常。
  - 确认**阵营**：敌人 team ≠ 玩家 team（火球只伤玩家）。

- [ ] **Step 5: 提交**

```bash
git add Assets/_Project/Scripts/Character/Controllers/RangedEnemyController.cs Assets/_Project/Scripts/Character/Enemy/States/EnemyKiteState.cs Assets/_Project/Scripts/Character/Enemy/States/EnemyRangedAttackState.cs
git commit -m "feat(character): add ranged mage enemy (kiting + fireball casting)"
```
(末尾补 `Co-Authored-By` 行。)

---

## Task 4：预制体组装 + 双形态实测（集成验收，纯 Editor）

**Files:** 无（Editor 资产/场景；`.meta` 由 Unity 生成，不手建）。

- [ ] **Step 1: 固化远程法师敌人预制体**
  - 把 Task 3 调好的远程敌人(含 RangedEnemyController + 生成点子物体 + Animator/Feedback/SO)拖成预制体，存 `Assets/_Project/Art/Prefabs/`。
  - 远程敌人所在 Layer 要被玩家命中遮罩包含(玩家能打到它)；其火球生成点朝前、不卡在身体里。

- [ ] **Step 2: 双形态混战实测**
  - 场景同时摆 1~2 个近战敌人 + 1~2 个远程法师敌人。
  - 进入 Play 跑通：① 近战敌人贴身追打、远程敌人保持距离放火球；② 玩家贴脸远程敌人时它后撤；③ 三职业能分别击杀两类敌人；④ 被火球点燃时玩家仍可移动(DoT 不锁，已实现)；⑤ Profiler 抽查战斗稳态无每帧 GC（一次性 Instantiate 火球/特效除外）。

- [ ] **Step 3: 提交（若有场景/预制体变更，开发者确认后）**

```bash
git add Assets/_Project/Art/Prefabs Assets/_Project/Scenes
git commit -m "feat(character): assemble ranged mage enemy prefab and mixed-encounter playtest"
```
(末尾补 `Co-Authored-By` 行。)

---

## 自检（Self-Review）

**规格覆盖**：base+subclass 重构✓(T1) · EnemyDefinition 远程字段✓(T2) · RangedEnemyController + 远追/近退/站档 KiteState✓(T3) · 单点发火球 RangedAttackState✓(T3) · 复用玩家 Fireball + Init 注入敌方阵营✓(T3) · 改名保 GUID/序列化✓(T1) · EngageState 多态接缝✓(T1) · 预制体组装+双形态实测✓(T4)。Game.Combat 不依赖 Character(EnemyDefinition 仅加值类型字段+GameObject 引用)。

**类型一致性**：`EnemyControllerBase.{Definition,Perception,StateMachine,IdleState,HurtState,AttackStateHash,HurtStateHash,AttackCooldownCounter,IsDead,Animator,EngageState,MoveTo,MoveAway,StayGrounded,FaceTarget,CrossFade,OnDied,_definition,_cc,_animator}`、`MeleeEnemyController.{ChaseState,AttackState,OpenAttackWindow,CloseAttackWindow}`、`RangedEnemyController.{KiteState,RangedAttackState,SpawnFireball}` 在定义/消费任务间签名一致。`EnemyStateBase`/`EnemyPerception` ctor 均收 `EnemyControllerBase`。复用 API：`Fireball.Init(byte,int,float,DamageType,Vector3,Collider,bool)`、`AttackDefinition.{BaseAmount,Type,AnimationStateName,ArrowSpawnTime,HitActiveStart,HitActiveEnd}`、`MeleeHitDetector.{SetAttack,OpenHitWindow,CloseHitWindow}` 与现有代码核对一致。

**已知边界(MVP 内可接受)**：① 走位只沿"玩家-我"连线进退，不做横向 strafe/绕圈/找掩体；② 火球直线瞄准当前玩家位置，不做提前量(player 移动可被躲)；③ 远程敌人**必须**配施法动画名(否则进不去施法态、超时空放)——已 GameLog.Warn 提示；④ 不绕障碍(无 NavMesh)；⑤ 多敌人无攻击令牌、被连打无霸体(同既有边界，留待 M5)。
