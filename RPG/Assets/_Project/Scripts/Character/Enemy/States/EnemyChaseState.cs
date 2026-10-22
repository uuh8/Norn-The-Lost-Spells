using UnityEngine;

namespace Game.Character
{
    /// <summary>近战追击：朝玩家移动并转向；进入攻击距离且冷却就绪 → 出招，冷却中则停下等待；丢失目标回待机。</summary>
    public class EnemyChaseState : EnemyStateBase
    {
        private readonly MeleeEnemyController _melee; // typed 子类引用，取近战专属(AttackState)

        public EnemyChaseState(MeleeEnemyController enemy) : base(enemy) { _melee = enemy; }

        public override void Enter()
        {
            _enemy.BeginNavigation();
        }

        public override void Update()
        {
            EnemyPerception p = _enemy.Perception;
            if (!p.HasTarget)
            {
                _enemy.StateMachine.ChangeState(_enemy.IdleState);
                return;
            }

            Vector3 targetPos = p.Target.position;
            // 索敌只由 Detect/Lose Radius 决定。即使当前路径绕障碍，也持续追到最终直达段。
            _enemy.PlanNavigationTo(targetPos, 0.05f);

            if (_enemy.CanAttackThroughNavigation(targetPos, _enemy.Definition.AttackRange))
            {
                _enemy.FaceTarget(targetPos);
                if (_enemy.AttackCooldownCounter <= 0f)
                {
                    _enemy.StateMachine.ChangeState(_melee.AttackState);
                    return;
                }
                _enemy.StayGrounded(); // 在攻击距离但冷却中：停下等待
                return;
            }

            _enemy.FaceNavigationOrTarget(targetPos);
            _enemy.MoveAlongNavigation();
        }

        public override void Exit()
        {
            _enemy.StopNavigation(true);
        }
    }
}
