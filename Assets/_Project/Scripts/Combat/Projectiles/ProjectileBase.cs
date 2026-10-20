using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using Unity.Profiling;

namespace Game.Combat
{
    public enum ProjectileTerminationReason : byte { Impact, LifetimeExpired, Cancelled, TimedTrigger }
    /// <summary>
    /// 投射物基类。抽象所有飞行投射物的公共骨架：Init 注入伤害快照、初速度、碰撞规则与生命周期；
    /// Rigidbody/Physics 负责运动和接触检测，OnCollisionEnter 再统一处理同阵营穿过、敌方伤害与命中销毁。
    /// 子类只需重写 OnImpact 决定"命中后、销毁前的额外表现"（爆炸特效、附加状态等）。
    /// Arrow / Fireball / NovaFireball 皆派生于此，消除三者重复逻辑。
    /// </summary>
    // RequireComponent 告诉 Unity：挂载该脚本时必须同时存在 Rigidbody 与 Collider；Editor 会自动补齐缺失组件。
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public abstract class ProjectileBase : MonoBehaviour
    {
        [Header("飞行 (基类通用)")]
        [SerializeField] protected float _maxLifetime = 5f; // 超时自毁，防漏网投射物累积
        // 模型朝向修正：LookRotation 把 +Z 对到飞行方向；箭尖沿 +Y 的模型填 (90,0,0)，球形特效填 0。
        [SerializeField] protected Vector3 _modelForwardOffsetEuler = Vector3.zero;
        [Tooltip("命中后停留多久再销毁(秒)。0 = 立即消失(爆炸类)；箭矢类设 >0 可插在目标/地面上残留一小段时间。")]
        [SerializeField] protected float _impactLingerTime = 0f;
        [Tooltip("追踪目标扫描间隔。仅配置追踪修正的投射物会使用。")]
        [SerializeField] private float _homingScanInterval = 0.1f;

        // 缓存组件引用，避免在 Update/FixedUpdate 等热路径反复 GetComponent。
        protected Rigidbody _rb;
        protected Collider _collider;

        // 伤害快照（Init 注入；命中时构造 DamageRequest，不回查可能已销毁的攻击者）
        protected byte _attackerTeam;
        protected int _attackerId;
        protected float _damage;
        protected DamageType _type;

        private bool _consumed; // 防同一物理步多次碰撞重复结算/销毁
        private bool _terminated;
        private float _lifetimeRemaining, _lifetimeOverride=-1, _lifetimeDelta, _lifetimeMultiplier=1;
        public event System.Action<ProjectileTerminationReason,Vector3,Vector3> Terminated;
        private Vector3 _launchVelocity; // Init 注入的初速度快照：同队穿过时据此恢复被弹偏的直线投射物速度
        private int _bounceRemaining;
        private float _currentSpeed;
        private bool _timedTriggerArmed;
        private float _timedTriggerRemaining;
        private float _homingRadius;
        private float _homingDuration;
        private float _homingTurnRateDegrees;
        private bool _homingEnabled;
        private bool _homingAcquired;
        private float _homingRemaining;
        private float _homingScanTimer;
        private Collider _homingTargetCollider;
        private IDamageable _homingTargetDamageable;
        private float _orbitRadius;
        private float _orbitAngularSpeedDegrees;
        private float _orbitAngleDegrees;
        private float _orbitPlaneTiltDegrees;
        private bool _orbitEnabled;
        private Vector3 _orbitCenter;
        private Vector3 _orbitForward;
        private Vector3 _orbitRight;
        private Vector3 _orbitUp;
        private float _orbitCenterSpeed;
        private bool _hasDebugReflection;
        private Vector3 _lastDebugCollisionPoint;
        private Vector3 _lastDebugCollisionNormal;
        private Vector3 _lastDebugReflectedDirection;
        private float _lastDebugCollisionTime;
        private bool _debugMissingRigidbodyWarned;
        private bool _initialized;

        /// <summary>
        /// 命中真实目标/环境的瞬间触发（命中点, 命中方向）。上层（法术触发）据此在命中点再施放载荷，
        /// 保持 Combat 不反依赖 Skills/Character——这里只发一个通用通知。同阵营穿过不算命中、不触发；超时自毁不触发。
        /// </summary>
        // event 只允许本类触发 Invoke，外部只能 += 订阅或 -= 取消订阅，避免上层伪造命中。
        public event System.Action<Vector3, Vector3> Impacted;
        /// <summary>
        /// 需要真实接触法线的消费者使用该通道；旧 Impacted 继续服务 Spell Payload/VFX，
        /// 两个事件由同一次 ContactPoint 构造，避免不同模块各自猜测表面朝向。
        /// </summary>
        public event System.Action<ProjectileImpactContext> SurfaceImpacted;
        /// <summary>
        /// 定时触发计时器到点时广播当前位置与当前方向。Combat 不知道 payload；上层可选择订阅。
        /// 提前命中/销毁不会触发该事件。
        /// </summary>
        public event System.Action<Vector3, Vector3> TimedTriggerElapsed;
        // 通用生命周期通知；Combat 不依赖预算、库存或法术程序。
        public event System.Action Finished;

        // 在场投射物注册表：新生成的投射物与所有"同阵营"已存在投射物互相 IgnoreCollision，
        // 避免同队火球互撞（连发自撞偏移 / 同队两球相撞误爆炸）。异队不忽略 → 仍碰撞 → 各自爆炸。
        private static readonly List<ProjectileBase> s_active = new List<ProjectileBase>(32);
        // OverlapSphereNonAlloc 复用静态数组接收查询结果，避免追踪扫描每次创建 Collider[]。
        private static readonly Collider[] s_homingHits = new Collider[16];
        private static readonly ProfilerMarker s_motionMarker = new ProfilerMarker("Projectile.Motion");

        public static int ActiveCount => s_active.Count;
        internal static IReadOnlyList<ProjectileBase> ActiveProjectiles => s_active;

        # region Unity生命周期函数
        protected virtual void Awake()
        {
            // Awake 在 Prefab 实例创建后调用一次；GetComponent 只搜索当前 GameObject。
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
        }

        protected virtual void OnDestroy()
        {
            NotifyTermination(ProjectileTerminationReason.Cancelled,transform.position,ResolveCurrentDirection());
            Finished?.Invoke();
            Unregister(); // 从在场表注销（命中销毁 / 超时自毁 / 场景卸载）
        }

        /// <summary>飞行中是否每帧把模型朝向对齐当前速度方向（抛物线箭矢用：机头随下坠俯冲）。默认否（直线投射物方向恒定，Init 定一次即可）。</summary>
        protected virtual bool FaceVelocityInFlight => false;

        protected virtual void OnEnable()
        {
            EventBus<WorldActivitySourceEvent>.Publish(new WorldActivitySourceEvent
                { Source = transform, Kind = WorldActivitySourceKind.Projectile, Enabled = true });
            if (_initialized)
                RegisterAndIgnoreSameTeamProjectiles();
        }

        protected virtual void OnDisable()
        {
            // 禁用/回滚/Scene清理都属于Cancel，不能借OnDestroy伪装正常寿命到点。
            if(_initialized)NotifyTermination(ProjectileTerminationReason.Cancelled,transform.position,ResolveCurrentDirection());
            EventBus<WorldActivitySourceEvent>.Publish(new WorldActivitySourceEvent
                { Source = transform, Kind = WorldActivitySourceKind.Projectile, Enabled = false });
            Unregister();
        }

        private void Update()
        {
            if(_initialized && !_consumed && !_terminated)
            {
                _lifetimeRemaining-=Time.deltaTime;
                if(_lifetimeRemaining<=0)
                {_consumed=true;NotifyTermination(ProjectileTerminationReason.LifetimeExpired,transform.position,ResolveCurrentDirection());Destroy(gameObject);return;}
            }
            // 定时触发使用渲染帧时间，适合“经过若干游戏秒触发”的玩法计时；实际物理运动仍在 FixedUpdate。
            TickTimedTrigger();
            OnProjectileUpdate();
        }

        protected virtual void FixedUpdate()
        {
            if (_consumed || _rb == null) return;

            // Rigidbody 属于 Physics Step，FixedUpdate 按固定时间步调用；使用 fixedDeltaTime 才与物理更新频率一致。
            using (s_motionMarker.Auto())
            {
                if (_orbitEnabled)
                    TickOrbit(Time.fixedDeltaTime);
                else
                    TickHoming(Time.fixedDeltaTime);    // 追踪行为放在 `FixedUpdate` 中执行，因为它修改的是 `Rigidbody.linearVelocity`，属于 Physics Step 使用的运动数据

                // 非抛物线投射物不更新模型朝向；命中冻结(velocity≈0)时自动停止，保留命中姿态
                if (!FaceVelocityInFlight) return;
                Vector3 v = _rb.linearVelocity;
                if (v.sqrMagnitude > 1e-6f)
                    // LookRotation 让局部 +Z 朝向速度；Euler 偏移再补偿模型资产自身的前轴差异。
                    transform.rotation = Quaternion.LookRotation(v) * Quaternion.Euler(_modelForwardOffsetEuler);
            }
        }

        # endregion

        protected virtual void OnProjectileUpdate() { }

        private void TickTimedTrigger()
        {
            if (_consumed || !_timedTriggerArmed) return;

            // Time.deltaTime 是上一渲染帧消耗的缩放后游戏时间，因此倒计时基本不受帧率影响。
            _timedTriggerRemaining -= Time.deltaTime;
            if (_timedTriggerRemaining > 0f) return;

            _consumed = true;
            // ?.Invoke 表示有订阅者才调用；没有 Payload 监听时不会产生 NullReferenceException。
            TimedTriggerElapsed?.Invoke(transform.position, ResolveCurrentDirection());
            NotifyTermination(ProjectileTerminationReason.TimedTrigger,transform.position,ResolveCurrentDirection());
            Destroy(gameObject);
        }

        /// <summary>
        /// 攻击方生成瞬间调用：注入伤害快照与初速度，忽略与施法者自身碰撞，定向、计时。
        /// useGravity：抛物线箭矢传 true（默认）；直线投射物（瞄准直射/火球/陨石）传 false。
        /// </summary>
        public virtual void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                                 Vector3 velocity, Collider casterCollider, bool useGravity = true)
        {
            ClearDebugReflectionFacts();
            _attackerTeam = attackerTeam;
            _attackerId = attackerId;
            _damage = damage;
            _type = type;
            _launchVelocity = velocity; // 直线投射物被同队物体擦碰弹偏后，据此恢复原方向
            _currentSpeed = velocity.magnitude;

            // 正常情况下 Awake 已缓存；再次兜底可兼容测试或特殊初始化顺序。
            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_collider == null) _collider = GetComponent<Collider>();

            // IgnoreCollision 只关闭这一对 Collider 之间的碰撞，不会影响它们与其他对象的碰撞。
            // 忽略施法者自身可避免投射物在出膛瞬间因重叠而自毁。
            if (casterCollider != null && _collider != null)
                Physics.IgnoreCollision(_collider, casterCollider);

            // 与同阵营的其它在场投射物互相忽略碰撞，并把自己登记进表
            _initialized = true;
            RegisterAndIgnoreSameTeamProjectiles();

            // useGravity 决定 Unity Physics 是否每个固定步把重力加到速度上。
            _rb.useGravity = useGravity;
            // ContinuousDynamic 会在两个 Physics Step 之间做连续检测，降低高速物体越过薄碰撞体的“隧穿”风险，
            // 代价是比 Discrete 碰撞检测更贵，因此只用于这些高速投射物。
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            // linearVelocity 是 Unity 6 的刚体线速度，Physics 会据此在后续固定步推进位置并处理碰撞。
            _rb.linearVelocity = velocity;
            if (_orbitEnabled)
                InitializeOrbit(velocity);
            if (velocity.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(velocity) * Quaternion.Euler(_modelForwardOffsetEuler);

            // 明确倒计时才能区分正常寿命和取消；不再安排无法撤回/辨识原因的延迟Destroy。
            _consumed=false;_terminated=false;ResetLifetime();
        }
        public void ConfigureLifetime(float seconds,float additive=0,float multiplier=1)
        { _lifetimeOverride=seconds;_lifetimeDelta=additive;_lifetimeMultiplier=multiplier;if(_initialized)ResetLifetime(); }
        private void ResetLifetime()
        { // 先以真实Prefab/覆盖值为基数加秒，再乘倍率；仅最终结果钳制，保持叠加可预测。
            _lifetimeRemaining=(float)System.Math.Clamp(((double)(_lifetimeOverride<0?_maxLifetime:_lifetimeOverride)+_lifetimeDelta)*_lifetimeMultiplier,.01,600); }
        private void NotifyTermination(ProjectileTerminationReason reason,Vector3 point,Vector3 direction)
        { if(_terminated)return;_terminated=true;Terminated?.Invoke(reason,point,direction); }

        /// <summary>配置本次投射物可用的环境弹跳次数。由 SpellCaster 从 EmitCommand 注入。</summary>
        public void ConfigureBounce(int bounceCount)
        {
            _bounceRemaining = Mathf.Max(0, bounceCount);
        }

        /// <summary>配置本次投射物的有限追踪能力。由 SpellCaster 从 EmitCommand 注入。</summary>
        public void ConfigureHoming(float radius, float duration, float turnRateDegrees)
        {
            _homingRadius = Mathf.Max(0f, radius);
            _homingDuration = Mathf.Max(0f, duration);
            _homingTurnRateDegrees = Mathf.Max(0f, turnRateDegrees);
            _homingEnabled = _homingRadius > 0f && _homingDuration > 0f && _homingTurnRateDegrees > 0f;
            _homingAcquired = false;
            _homingRemaining = 0f;
            _homingScanTimer = 0f;
            _homingTargetCollider = null;
            _homingTargetDamageable = null;
        }

        /// <summary>配置本次投射物的轨道螺旋运动。由 SpellCaster 从 EmitCommand 注入。</summary>
        public void ConfigureOrbit(float radius, float angularSpeedDegrees, float phaseOffsetDegrees, float planeTiltDegrees)
        {
            _orbitRadius = Mathf.Max(0f, radius);
            _orbitAngularSpeedDegrees = angularSpeedDegrees;
            _orbitAngleDegrees = phaseOffsetDegrees;
            _orbitPlaneTiltDegrees = planeTiltDegrees;
            _orbitEnabled = _orbitRadius > 0f && !Mathf.Approximately(_orbitAngularSpeedDegrees, 0f);
            _orbitCenter = Vector3.zero;
            _orbitForward = Vector3.forward;
            _orbitRight = Vector3.right;
            _orbitUp = Vector3.up;
            _orbitCenterSpeed = 0f;
        }

        /// <summary>
        /// 由保护盾调用：按护盾球面法线反射当前投射物，并把伤害归属改成护盾释放者。
        /// </summary>
        public bool ReflectByShield(Vector3 shieldNormal, byte newTeam, int newAttackerId)
        {
            if (_consumed || _rb == null)
                return false;

            Vector3 velocity = _rb.linearVelocity;
            if (velocity.sqrMagnitude <= 1e-6f)
                return false;

            Vector3 normal = shieldNormal.sqrMagnitude > 1e-6f ? shieldNormal.normalized : -velocity.normalized;
            Vector3 incoming = velocity.normalized;
            Vector3 reflected = Vector3.Reflect(incoming, normal);
            if (reflected.sqrMagnitude <= 1e-6f)
                return false;

            reflected.Normalize();
            RecordDebugReflection(transform.position, normal, reflected);
            float speed = velocity.magnitude;
            Vector3 newVelocity = reflected * speed;

            _attackerTeam = newTeam;
            _attackerId = newAttackerId;
            _orbitEnabled = false;
            _homingEnabled = false;
            _homingAcquired = false;
            _homingTargetCollider = null;
            _homingTargetDamageable = null;

            _rb.position += normal * 0.03f;
            _rb.linearVelocity = newVelocity;
            _launchVelocity = newVelocity;
            _currentSpeed = speed;
            RefreshSameTeamProjectileIgnores();

            transform.rotation = Quaternion.LookRotation(reflected) * Quaternion.Euler(_modelForwardOffsetEuler);
            return true;
        }

        private void InitializeOrbit(Vector3 velocity)
        {
            Vector3 forward = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : transform.forward;
            if (forward.sqrMagnitude <= 1e-6f)
                forward = Vector3.forward;

            _orbitForward = forward.normalized;
            _orbitCenter = transform.position;
            _orbitCenterSpeed = velocity.magnitude;

            Vector3 upSeed = Mathf.Abs(Vector3.Dot(_orbitForward, Vector3.up)) > 0.95f
                ? Vector3.forward
                : Vector3.up;

            Quaternion basis = Quaternion.LookRotation(_orbitForward, upSeed);
            if (!Mathf.Approximately(_orbitPlaneTiltDegrees, 0f))
                basis = Quaternion.AngleAxis(_orbitPlaneTiltDegrees, basis * Vector3.right) * basis;

            _orbitRight = basis * Vector3.right;
            _orbitUp = basis * Vector3.up;
        }

        /// <summary>
        /// 开启定时触发。delaySeconds <= 0 时在下一次 Update 触发，避免在 Arm 调用栈内重入施法。
        /// </summary>
        public void ArmTimedTrigger(float delaySeconds)
        {
            _timedTriggerArmed = true;
            _timedTriggerRemaining = Mathf.Max(0f, delaySeconds);
        }

        private void OnCollisionEnter(Collision collision)
        {
            // OnCollisionEnter 由 Unity Physics 在刚体开始接触另一个 Collider 时调用，参数包含碰撞体和接触点。
            // _consumed 保证复杂碰撞在同一物理步产生多个回调时，伤害与 Payload 仍只结算一次。
            if (_consumed) return;

            // GetComponentInParent 从发生接触的 Collider 开始向父节点查找接口，兼容“子节点碰撞体、根节点生命组件”。
            IDamageable target = collision.collider.GetComponentInParent<IDamageable>();

            // 处理同阵营穿过
            if (target != null && target.TeamId == _attackerTeam)
            {
                if (_collider != null)
                    Physics.IgnoreCollision(_collider, collision.collider);
                if (!FaceVelocityInFlight && _rb != null)
                    _rb.linearVelocity = _launchVelocity; // 抛物线投射物速度时变，不恢复
                return;
            }

            // 尝试环境弹跳
            if (TryBounce(collision, target))
                return;

            if (TryHandleCollisionBeforeDefault(collision, target))
                return;

            // GetContact(0) 直接读取第一个 ContactPoint，比先取 contacts 数组更适合这里只需一个命中点的场景。
            ContactPoint contact = collision.GetContact(0);
            Vector3 hitPoint = contact.point;
            Vector3 vel = _rb != null ? _rb.linearVelocity : Vector3.zero;
            Vector3 hitDir = vel.sqrMagnitude > 1e-6f ? vel.normalized : transform.forward;
            bool damaged = false;

            // 敌方且存活 → 结算一次伤害
            if (target != null && target.IsAlive)
            {
                // DamageRequest 是一次命中的值快照；in 以只读引用传递，避免接收方修改请求，也避免较大 struct 拷贝。
                var req = new DamageRequest(_attackerId, _attackerTeam, _damage, _type, hitPoint, hitDir);
                target.ReceiveHit(in req);
                damaged = true;
            }

            // 子类扩展点：命中敌方或环境都会走到（撞地也爆炸/插在目标上残留）
            OnImpact(collision, target, hitPoint, damaged);

            // 命中通知：法术触发据此在命中点跑载荷（普通投射物无监听者，空触发无开销）
            _consumed = true;
            SurfaceImpacted?.Invoke(new ProjectileImpactContext(
                hitPoint,
                hitDir,
                contact.normal,
                target,
                _attackerId,
                _attackerTeam));
            // SpellCaster 若为该投射物绑定了 Payload，会在这个同步回调里以命中点运行下一层法术序列。
            Impacted?.Invoke(hitPoint, hitDir);
            NotifyTermination(ProjectileTerminationReason.Impact,hitPoint,hitDir);

            // impactLingerTime 允许箭矢等命中后短暂停留；Destroy 的实际移除发生在当前帧稍后阶段。
            Destroy(gameObject, _impactLingerTime);
        }

        protected virtual bool TryHandleCollisionBeforeDefault(Collision collision, IDamageable target)
        {
            return false;
        }

        // 弹跳_修正
        private bool TryBounce(Collision collision, IDamageable target)
        {
            // 1. 反弹前置拦截
            // - 碰到了有效伤害目标（如敌人）：不反弹，而是穿透或造成伤害。
            // - 反弹次数耗尽：不再反弹。
            // - 刚体丢失：物理失控，无法反弹。
            if (target != null || _bounceRemaining <= 0 || _rb == null)
                return false;
            // 没有接触点信息无法计算法线，直接退出
            if (collision.contactCount <= 0)
                return false;

            // 2. 确定入射方向
            // 物理引擎的实时速度可能因碰撞衰减或微小抖动而不可靠，因此优先使用记录的初速度 _launchVelocity。
            Vector3 velocity = _rb.linearVelocity;
            Vector3 intendedVelocity = _launchVelocity.sqrMagnitude > 1e-6f ? _launchVelocity : velocity;
            // 获取单位化的入射方向向量；若速度接近零（静止或极慢），退而使用投射物的前朝向。
            Vector3 incoming = intendedVelocity.sqrMagnitude > 1e-6f ? intendedVelocity.normalized : transform.forward;

            // 3. 计算反射方向
            Vector3 normal = collision.GetContact(0).normal;        // 碰撞表面的法线方向
            float normalDot = Vector3.Dot(incoming, normal);      // 入射方向与法线的点积
            // 若 Dot < 0 表示速度确实朝向表面；Reflect 按表面法线计算镜面反射方向。
            // 若 Dot >= 0 表示可能是从表面内部穿出或擦边，此时保持原方向，不做反射。
            Vector3 reflected = normalDot < 0f ? Vector3.Reflect(incoming, normal) : incoming;

            // 反射后速度接近零（例如几乎垂直法线入射导致反射量极小），视为无效反弹。
            if (reflected.sqrMagnitude <= 1e-6f)
                return false;

            reflected.Normalize();

            // 4. 记录调试可视化信息（供开发期在 Scene 视图绘制射线辅助 Debug）
            ContactPoint contact = collision.GetContact(0);
            RecordDebugReflection(contact.point, normal, reflected);

            // 5. 确定反弹速率
            // 优先使用当前记录的速率 _currentSpeed（可能受加速/减速 Buff 影响），
            // 若无效则退回预期速度的模长，再无效则使用初速度的模长。
            float speed = _currentSpeed > 1e-6f ? _currentSpeed : intendedVelocity.magnitude;
            if (speed <= 1e-6f)
                speed = _launchVelocity.magnitude;

            // 6. 应用反弹状态
            _bounceRemaining--;          // 消耗一次反弹次数
            _orbitEnabled = false;       // 反弹后脱离任何追踪/环绕轨道，改为纯直线飞行

            Vector3 newVelocity = reflected * speed;    // 最终的新速度 = 反射方向 × 速率

            // 优化点：沿法线方向将刚体微推离碰撞表面。
            // 物理引擎中，碰撞后刚体可能仍与碰撞体存在微小重叠，
            // 如果不推开，下一固定帧会立即再次触发碰撞，导致投射物“卡在墙里”或连续异常反弹。
            _rb.position += normal * 0.03f;

            _rb.linearVelocity = newVelocity;   // 写入新速度
            _launchVelocity = newVelocity;      // 更新记录的初速度，供后续可能的再次反弹或逻辑使用

            // 7. 更新朝向
            // 将投射物的 Z 轴对齐到反射方向，并叠加模型本身的旋转偏移修正（_modelForwardOffsetEuler）
            transform.rotation = Quaternion.LookRotation(reflected) * Quaternion.Euler(_modelForwardOffsetEuler);

            return true;
        }

        // 轨道_修正
        private void TickOrbit(float deltaTime)
        {
            if (!_orbitEnabled || _rb == null)
                return;

            float safeDeltaTime = Mathf.Max(deltaTime, 1e-5f);
            _orbitCenter += _orbitForward * _orbitCenterSpeed * safeDeltaTime;
            _orbitAngleDegrees += _orbitAngularSpeedDegrees * safeDeltaTime;

            // Mathf.Sin/Cos 接受弧度，因此先用 Deg2Rad 把便于配置的角度转换为弧度。
            // 两个互相垂直基向量的 sin/cos 线性组合描述圆周上的偏移。
            float radians = _orbitAngleDegrees * Mathf.Deg2Rad;
            Vector3 offset = (_orbitRight * Mathf.Cos(radians) + _orbitUp * Mathf.Sin(radians)) * _orbitRadius;
            Vector3 targetPosition = _orbitCenter + offset;
            // 速度 = 本固定步需要完成的位移 / 时间，让 Rigidbody 在下一 Physics Step 逼近轨道目标点。
            Vector3 newVelocity = (targetPosition - _rb.position) / safeDeltaTime;

            _rb.linearVelocity = newVelocity;
            _launchVelocity = newVelocity;
            _currentSpeed = newVelocity.magnitude;
        }

        # region 追踪法术
        private void TickHoming(float deltaTime)
        {
            // 若该投射物未配置追踪特性，直接跳过。
            if (!_homingEnabled)
                return;

            // 阶段 1：目标未锁定 -> 节流扫描

            if (!_homingAcquired)
            {
                _homingScanTimer -= deltaTime;
                // 扫描冷却未结束，本帧不执行耗性能的 OverlapSphere 查询。
                if (_homingScanTimer > 0f)
                    return;

                // 冷却结束，重置下次扫描计时器。
                // 下限钳制为 0.02s（最多每秒扫描50次），防止配置极小值导致每帧扫描拖垮性能。
                _homingScanTimer = Mathf.Max(0.02f, _homingScanInterval);
                TryAcquireHomingTarget();
                return;
            }

            // 阶段 2：目标已锁定 -> 有效性守卫

            // 追踪持续时间耗尽，停止转向（保持当前直线飞行方向）。
            if (_homingRemaining <= 0f)
                return;

            // 多维度的目标失效检测：
            // 1. Collider 引用丢失（目标被 Destroy）
            // 2. Collider 被禁用（目标进入不可交互状态）
            // 3. GameObject 被隐藏（SetActive(false)）
            // 4. 接口引用丢失或目标已死亡
            // 一旦任何条件满足，立即清零剩余追踪时间，使投射物放弃追踪变为直飞弹。
            if (_homingTargetCollider == null ||
                !_homingTargetCollider.enabled ||
                !_homingTargetCollider.gameObject.activeInHierarchy ||
                _homingTargetDamageable == null ||
                !_homingTargetDamageable.IsAlive)
            {
                _homingRemaining = 0f;
                return;
            }

            // 消耗追踪时长
            _homingRemaining -= deltaTime;

            // 阶段 3：计算并应用转向

            Vector3 velocity = _rb.linearVelocity;
            float speed = velocity.magnitude;
            // 速度极小（近乎静止），无法确定当前飞行方向，放弃本帧转向。
            if (speed <= 1e-6f)
                return;


            // 计算指向目标追踪锚点的向量。
            // ResolveHomingTargetPoint 会读取命中 Collider 的 Bounds Center，
            // 这样能适应不同身高/不同Collider类型的单位（如人和巨龙），而不是死板地追踪 Transform.position（往往在脚底）。
            Vector3 toTarget =
                ResolveHomingTargetPoint(_homingTargetCollider) -
                transform.position;

            // 投射物与目标几乎重合（距离极小），方向向量无意义，放弃本帧转向。
            if (toTarget.sqrMagnitude <= 1e-6f)
                return;

            // 计算本物理步允许旋转的最大弧度。
            // _homingTurnRateDegrees 是“度/秒”，乘以 Deg2Rad 转为“弧度/秒”，再乘 deltaTime 得到本步增量。
            float maxRadians = _homingTurnRateDegrees * Mathf.Deg2Rad * deltaTime;

            // 核心转向算法：Vector3.RotateTowards
            // 从当前飞行方向，向目标方向旋转，但单步最大旋转量不超过 maxRadians。
            // 这使得投射物拥有“转向速度上限”，形成平滑的弧线追踪轨迹，而不是瞬间锁死目标。
            Vector3 newDir = Vector3.RotateTowards(velocity.normalized, toTarget.normalized, maxRadians, 0f);

            // 组装新速度：新方向 × 原速率（追踪过程只改方向，不改变飞行速率大小）
            Vector3 newVelocity = newDir * speed;

            // 写入物理引擎并同步更新记录的初速度（供后续碰撞反弹等逻辑使用）
            _rb.linearVelocity = newVelocity;
            _launchVelocity = newVelocity;
        }

        /// <summary>
        /// 尝试在设定的追踪半径内寻找最佳的追踪目标。
        /// 扫描周围所有碰撞体，过滤掉无效目标（自身、友军、死亡单位），选出距离最近的敌方目标并锁定。
        /// </summary>
        private void TryAcquireHomingTarget()
        {
            // 1. 空间扫描
            // OverlapSphereNonAlloc 以投射物当前位置为球心，_homingRadius 为半径，查询所有重叠的 Collider。
            // 结果写入静态复用数组 s_homingHits（避免每帧 GC Alloc 产生内存垃圾）。
            // 返回值 hitCount 是实际碰到的 Collider 数量（受限于数组长度，超过部分会被截断丢弃）。
            int hitCount = Physics.OverlapSphereNonAlloc(transform.position, _homingRadius, s_homingHits);

            // 初始化最佳候选记录
            float bestSqrDistance = float.MaxValue; ; // 记录当前最小距离（初始设为最大值，以便第一个合法目标直接覆盖）
            Collider bestTargetCollider = null;
            IDamageable bestDamageable = null;

            // 2. 遍历与过滤
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = s_homingHits[i];

                // 过滤条件 A：忽略空引用和自身碰撞体（防止投射物追踪自己）
                if (hit == null || hit == _collider)
                    continue;

                // 向上查找挂载在父物体上的 IDamageable 接口（因为 Collider 常挂在子节点，而逻辑脚本在父节点）
                IDamageable damageable = hit.GetComponentInParent<IDamageable>();

                // 过滤条件 B：必须满足以下全部条件，否则跳过：
                // 1) 实现了 IDamageable 接口（是可受伤实体，而不是纯墙壁）
                // 2) 目标存活
                // 3) 阵营不同（避免友军相残，追踪弹不会锁友军）
                if (damageable == null || !damageable.IsAlive || damageable.TeamId == _attackerTeam)
                    continue;

                // 获取该候选目标的精确追踪锚点（头部），而不是简单的 Collider 中心
                Vector3 targetPoint = ResolveHomingTargetPoint(hit);

                // 过滤条件 C：距离筛选
                // 使用 sqrMagnitude（平方距离）代替 magnitude（距离）。
                // 因为平方根运算开销大，且在仅比较大小的情况下，平方距离的大小关系与真实距离完全一致。
                float sqrDistance = (targetPoint - transform.position).sqrMagnitude;
                if (sqrDistance >= bestSqrDistance)
                    continue;   // 距离不如当前最佳候选近，跳过

                // 更新最佳候选记录
                bestSqrDistance = sqrDistance;
                bestTargetCollider = hit;
                bestDamageable = damageable;
            }

            // 3. 锁定目标
            // 如果扫完整个数组都没找到合法目标，直接返回，保持未追踪状态。
            if (bestTargetCollider == null)
                return;

            // 找到了最佳目标，写入投射物的追踪状态字段
            _homingTargetCollider = bestTargetCollider;   // 锁定目标的 Collider，供后续每帧持续朝它转向
            _homingTargetDamageable = bestDamageable;     // 缓存接口引用，避免后续帧重复调用 GetComponentInParent
            _homingAcquired = true;                       // 标记已获取目标，激活追踪转向逻辑
            _homingRemaining = _homingDuration;           // 重置追踪持续时间倒计时（限制追踪弹不能无限拐弯）
        }

        /// <summary>
        /// Homing 的命中目标必须来自真实 Physics 体积，而不是通常位于角色脚底的 Root。
        /// Bounds 是 struct，逐个 FixedUpdate 读取不会产生 GC Alloc，并会随 Collider 移动更新。
        /// </summary>
        protected static Vector3 ResolveHomingTargetPoint(
            Collider targetCollider)
        {
            return targetCollider != null
                ? targetCollider.bounds.center
                : Vector3.zero;
        }

        # endregion

        public ProjectileDebugSnapshot GetDebugSnapshot()
        {
            if (_rb == null && !_debugMissingRigidbodyWarned)
            {
                _debugMissingRigidbodyWarned = true;
                GameLog.Warn(
                    $"Projectile {name} 缺少 Rigidbody，Debug Snapshot 使用零速度",
                    "ProjectileDebug");
            }

            Vector3 velocity = _rb != null ? _rb.linearVelocity : Vector3.zero;
            bool hasTarget = _homingTargetCollider != null;

            return new ProjectileDebugSnapshot(
                transform.position,
                velocity,
                _rb != null && _rb.useGravity,
                _homingEnabled,
                _homingRadius,
                hasTarget,
                hasTarget
                    ? ResolveHomingTargetPoint(
                        _homingTargetCollider)
                    : Vector3.zero,
                _orbitEnabled,
                _orbitCenter,
                _orbitForward,
                _orbitRight,
                _orbitUp,
                _orbitRadius,
                _hasDebugReflection,
                _lastDebugCollisionPoint,
                _lastDebugCollisionNormal,
                _lastDebugReflectedDirection,
                _lastDebugCollisionTime);
        }

        private void RecordDebugReflection(
            Vector3 point, Vector3 normal, Vector3 reflectedDirection)
        {
            _hasDebugReflection = true;
            _lastDebugCollisionPoint = point;
            _lastDebugCollisionNormal = normal;
            _lastDebugReflectedDirection = reflectedDirection;
            _lastDebugCollisionTime = Time.time;
        }

        private void ClearDebugReflectionFacts()
        {
            _hasDebugReflection = false;
            _lastDebugCollisionPoint = Vector3.zero;
            _lastDebugCollisionNormal = Vector3.zero;
            _lastDebugReflectedDirection = Vector3.zero;
            _lastDebugCollisionTime = 0f;
        }

        private Vector3 ResolveCurrentDirection()
        {
            Vector3 v = _rb != null ? _rb.linearVelocity : Vector3.zero;
            if (v.sqrMagnitude > 1e-6f)
                return v.normalized;
            return transform.forward;
        }

        /// <summary>命中后、销毁前的子类扩展点（默认空）。target 可能为 null（命中环境）；damaged 表示本次是否结算了伤害。</summary>
        protected virtual void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged) { }

        /// <summary>
        /// 与所有"同阵营"已在场投射物互相 IgnoreCollision，再把自己登记进表。
        /// 解决：①快速连发自己的火球在出膛处互撞被弹偏；②同队两枚火球相撞误爆炸（它们本就不该互相作用）。
        /// 异阵营投射物不忽略 → 仍会物理碰撞 → 各自 OnCollisionEnter 触发爆炸（保留"异队火球相撞才爆炸"）。
        /// 在离散输入时一次性执行（非每帧热路径），遵循"按键时一次性分配可接受"。
        /// </summary>
        private void RegisterAndIgnoreSameTeamProjectiles()
        {
            for (int i = 0; i < s_active.Count; i++)
            {
                ProjectileBase other = s_active[i];
                if (other == null || other == this) continue; // Unity 重载 == 可识别已销毁对象
                if (other._attackerTeam == _attackerTeam && other._collider != null && _collider != null)
                    Physics.IgnoreCollision(_collider, other._collider);
            }

            if (!s_active.Contains(this))
                s_active.Add(this);
        }

        private void Unregister()
        {
            s_active.Remove(this);
        }

        private void RefreshSameTeamProjectileIgnores()
        {
            if (_collider == null)
                return;

            for (int i = 0; i < s_active.Count; i++)
            {
                ProjectileBase other = s_active[i];
                if (other == null || other == this || other._collider == null)
                    continue;

                if (other._attackerTeam == _attackerTeam)
                    Physics.IgnoreCollision(_collider, other._collider);
            }
        }
    }
}
