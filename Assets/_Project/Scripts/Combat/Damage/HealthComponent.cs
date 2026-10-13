using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 挂在“能受伤 GameObject”上的生命值组件，也是 IDamageable 的具体实现。
    /// 它持有目标自己的血量、阵营和防御配置，并作为统一 Damage Funnel：调用 DamagePipeline 纯计算，
    /// 再修改 Runtime HP，同帧发布受击事件；HP 归零时继续发布死亡事件。
    /// </summary>
    public class HealthComponent : MonoBehaviour, IDamageable
    {
        // ──────────────────────────────────────────────
        // 配置数据 (Inspector 赋值，运行时不应该被外部直接修改)
        // ──────────────────────────────────────────────
        [SerializeField] private float _maxHp = 100f;
        [SerializeField] private byte _teamId = 0;

        // ──────────────────────────────────────────────
        // 运行时状态 (Runtime State，每个场景实例独立拥有)
        // ──────────────────────────────────────────────
        [SerializeField] private float _currentHp;
        private int _id;                                 // 实例唯一 Id，供事件系统精准寻址
        private bool _isInvulnerable;                    // 无敌状态开关

        // ──────────────────────────────────────────────
        // 公开只读属性 (IDamageable 接口实现及状态暴露)
        // ──────────────────────────────────────────────
        public byte TeamId => _teamId;
        public bool IsAlive => _currentHp > 0f;
        public float CurrentHp => _currentHp;
        public float MaxHp => _maxHp;
        public int Id => _id;   // = gameObject.GetInstanceID()，与 DamageReceivedEvent.TargetId 同源，供血条/表现层按 id 过滤
        public bool IsInvulnerable => _isInvulnerable;

        private void Awake()
        {
            _currentHp = _maxHp;
            // GetInstanceID 获取 Unity 引擎分配的实例 ID，供事件消费者筛选目标；它不是跨存档的永久 Id。
            _id = gameObject.GetInstanceID();
        }

        /// <summary>
        /// 统一伤害入口：入口过滤 → 纯计算 → 扣血 → 同步发布表现/死亡所需的事实事件。
        /// </summary>
        public void ReceiveHit(in DamageRequest req)
        {
            // ── 1. 入口拦截 ──
            if (!IsAlive || _isInvulnerable) return;

            // ── 2. 纯计算 ──
            // DamagePipeline 是无副作用的静态工具：只根据 req 和 defenseProfile 算出最终伤害结果，不直接碰 MonoBehaviour 状态。
            // HealthComponent 才是 _currentHp 的唯一拥有者和修改者（单一职责原则）。
            DamageResult result = DamagePipeline.Resolve(in req);

            // ── 3. 状态修改 ──
            _currentHp -= result.Final;
            if (_currentHp < 0f) _currentHp = 0f;   // 扣血钳制到 ≥0

            // ── 4. 受击事件发布 ──
            // EventBus.Publish 是同步调用：当前 ReceiveHit 返回前，已订阅的 HUD、受击表现等消费者就会收到事件并执行逻辑。
            // 事件只携带值数据，不把 HealthComponent 或攻击者对象直接暴露给表现模块，解耦了逻辑层和表现层。
            EventBus<DamageReceivedEvent>.Publish(new DamageReceivedEvent
            {
                TargetId     = _id,
                AttackerId   = req.AttackerId,
                Amount       = result.Final,     // 最终扣血量
                Type         = result.Type,       // 伤害类型（物理/法术等）
                HitPoint     = req.HitPoint,      // 世界空间命中点，供特效生成
                HitDirection = req.HitDirection,  // 命中方向，供受击力/击退表现
                RemainingHp  = _currentHp,        // 扣血后的剩余血量，供血条直接刷新
                TriggerHitReaction = req.TriggerHitReaction, // 是否触发受击硬直/动画
            });

            // ── 5. 死亡事件发布 ──
            // 受击事件总是先于死亡事件发布，保证消费者可以先显示本次伤害数字和 0 HP 血条，再进入死亡表现。
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

        /// <summary>
        /// P8 当前只有 Phase Transition 一个无敌 Owner，因此 bool 足够且易于在 State.Exit 兜底释放。
        /// 将来若 Shield/Buff 也能同时申请无敌，应升级为 Token 或引用计数，避免一个 Owner 提前关闭另一个。
        /// </summary>
        public void SetInvulnerable(bool value)
        {
            _isInvulnerable = value;
        }
        /// <summary>新生命的离散入口：M4复用玩家对象回据点，需要由HP Owner重置并通知现有HUD。</summary>
        public void ResetForNewLife()
        {
            _currentHp = _maxHp; _isInvulnerable = false;
            GetComponent<StatusController>()?.ResetForNewLife();
            EventBus<DamageReceivedEvent>.Publish(new DamageReceivedEvent { TargetId = _id, RemainingHp = _currentHp });
        }
#if UNITY_EDITOR
        [ContextMenu("Debug/Receive Lethal Hit")]
        private void DebugReceiveLethalHit()
        {
            // 独立灰盒无攻击AI；Editor试玩沿真实伤害入口触发死亡，不直接改HP或伪造DeathEvent。
            if(Application.isPlaying)ReceiveHit(new DamageRequest(0,TeamId==1?(byte)2:(byte)1,_maxHp*10,DamageType.True,transform.position,Vector3.forward));
        }
#endif
    }
}
