using System.Collections.Generic;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 可复用的"范围伤害发射器"。挂在任意特效预制体上即可让其在一个球形半径内结算伤害——
    /// 爆炸、火场、毒池、震荡波都用它。命中走标准 IDamageable.ReceiveHit，与近战/投射物复用同一套结算与事件。
    ///
    /// 两种节奏（同一份代码）：
    ///   - TickInterval &lt;= 0 → 一次性：Start 时结算一次（爆炸"瞬间炸开"）。
    ///   - TickInterval &gt; 0  → 持续：每隔 TickInterval 结算一次，直到本 GameObject 被销毁（火场）。
    ///     "持续多久"由生成方控制本物体存活时长（Destroy(go, lifetime)），本组件不负责销毁/特效寿命。
    ///
    /// 阵营：生成方用 Init 注入"攻击者是谁/哪一队"，结算时跳过同阵营（友军/自己）。未 Init 则用默认队号 0。
    /// </summary>
    public class AreaDamage : MonoBehaviour
    {
        [Header("范围伤害")]
        [Tooltip("结算半径（米）")]
        [SerializeField] private float _radius = 3f;
        [Tooltip("每次结算对每个目标造成的伤害")]
        [SerializeField] private float _damagePerHit = 30f;
        [SerializeField] private DamageType _type = DamageType.Magical;
        [Tooltip("可命中层；按阵营过滤已足够，通常保持 Everything 即可")]
        [SerializeField] private LayerMask _hitMask = ~0;

        [Header("节奏")]
        [Tooltip("结算间隔(秒)。<=0 = 一次性(爆炸)，仅 Start 结算一次；>0 = 持续(火场)，每隔此秒数结算一次")]
        [SerializeField] private float _tickInterval = 0f;

        [Tooltip("命中是否触发受击反应(受击动画/敌人硬直)。爆炸建议勾选(会震一下)；火场建议取消(否则站在里面被持续锁死/一直播受击)")]
        [SerializeField] private bool _triggerHitReaction = true;

        private const int MaxTargets = 32; // 单次球形检测命中上限（预分配，零 GC）
        private readonly Collider[] _buf = new Collider[MaxTargets];
        private readonly HashSet<int> _tickHitSet = new HashSet<int>(); // 单次结算内去重（一个角色多碰撞体不重复挨打）

        // 攻击者快照（Init 注入；未注入则默认 0 队）
        private int _attackerId;
        private byte _attackerTeam;

        private float _tickTimer;
        private int _tickLimit = -1;
        private int _remainingTicks = -1;

        /// <summary>
        /// 当前实例的每跳伤害与 Tick 节奏。公开只读值既方便运行时诊断，
        /// 也明确区分“Prefab 默认值”和 Instantiate 后注入的本次施法快照。
        /// </summary>
        public float DamagePerHit => _damagePerHit;
        public float TickInterval => _tickInterval;
        public int TickLimit => _tickLimit;

        /// <summary>由生成方在 Instantiate 后立即调用，注入攻击者身份用于阵营过滤。须在 Start 之前调用（同帧 Instantiate 后调用即满足）。</summary>
        public void Init(int attackerId, byte attackerTeam)
        {
            _attackerId = attackerId;
            _attackerTeam = attackerTeam;
        }

        /// <summary>由生成方注入本次实例的动态伤害快照，用于让法术 Modify 影响 AOE 伤害。</summary>
        public void ConfigureDamage(float damagePerHit, DamageType type)
        {
            _damagePerHit = Mathf.Max(0f, damagePerHit);
            _type = type;
        }

        /// <summary>
        /// 注入本次 AreaDamage 实例的结算节奏。0 表示 Start 只结算一次；
        /// 正数表示 Start 首跳后继续按该间隔 Tick。
        /// </summary>
        public void ConfigureTickInterval(float tickInterval)
        {
            _tickInterval = Mathf.Max(0f, tickInterval);
        }

        /// <summary>
        /// 把持续时间转换为确定的 Tick Budget。负数或 0 表示不额外限制；
        /// 正数使用 Ceil(duration / interval)，其中 Start 的立即首跳也占一个预算。
        /// 这避免 Destroy 延迟边界让 5 秒火场偶发多结算第 11 跳。
        /// 必须在 ConfigureTickInterval 之后、Start 之前调用。
        /// </summary>
        public void ConfigureDuration(float duration)
        {
            if (duration <= 0f || _tickInterval <= 0f)
            {
                _tickLimit = -1;
                _remainingTicks = -1;
                return;
            }

            _tickLimit = Mathf.Max(1, Mathf.CeilToInt(duration / _tickInterval));
            _remainingTicks = _tickLimit;
        }

        private void Start()
        {
            // 一次性与持续都在此打出第一跳（爆炸的瞬间炸开 / 火场的第一跳）
            ApplyOnce();
            if (_remainingTicks > 0)
                _remainingTicks--;
            _tickTimer = _tickInterval;
        }

        private void Update()
        {
            if (_tickInterval <= 0f || _remainingTicks == 0)
                return; // 一次性或已用完有限 Tick Budget：不再处理

            _tickTimer -= Time.deltaTime;
            if (_tickTimer <= 0f)
            {
                _tickTimer += _tickInterval;
                ApplyOnce();
                if (_remainingTicks > 0)
                    _remainingTicks--;
            }
        }

        /// <summary>对当前半径内所有敌方存活目标各结算一次伤害（单次内去重）。</summary>
        private void ApplyOnce()
        {
            _tickHitSet.Clear();
            int count = Physics.OverlapSphereNonAlloc(
                transform.position, _radius, _buf, _hitMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                IDamageable target = _buf[i].GetComponentInParent<IDamageable>();
                if (target == null || !target.IsAlive) continue;
                if (target.TeamId == _attackerTeam) continue; // 跳过同阵营（友军/自己）

                Object targetObj = target as Object;
                if (targetObj == null) continue;
                if (!_tickHitSet.Add(targetObj.GetInstanceID())) continue; // 本次结算已命中 → 去重

                Vector3 hitPoint = _buf[i].ClosestPoint(transform.position);
                Vector3 toHit = hitPoint - transform.position;
                Vector3 hitDir = toHit.sqrMagnitude > 1e-6f ? toHit.normalized : Vector3.up;

                var req = new DamageRequest(_attackerId, _attackerTeam, _damagePerHit, _type,
                                            hitPoint, hitDir, _triggerHitReaction);
                target.ReceiveHit(in req);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.4f, 0f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, _radius);
        }
    }
}
