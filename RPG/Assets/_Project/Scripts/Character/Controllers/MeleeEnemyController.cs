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
