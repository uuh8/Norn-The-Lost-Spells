using UnityEngine;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 远程走位：远追 / 同层近退 / 高台或中间站档输出。移动时朝实际路径方向，攻击前才面向玩家。
    ///   水平距离与高度差都进入贴脸范围 → 从后方/左后/右后候选中选择完整路径后撤
    ///   射程、高差与 Line of Sight 成立      → 切施法态；冷却中站定等待
    ///   当前站位不能有效射击                → 沿 NavMesh 接近或重新寻找通道
    /// 丢失目标 → 回待机。
    /// </summary>
    public class EnemyKiteState : EnemyStateBase
    {
        private readonly RangedEnemyController _ranged;

        public EnemyKiteState(RangedEnemyController enemy) : base(enemy) { _ranged = enemy; }

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

            EnemyDefinition def = _enemy.Definition;
            float dist = p.DistanceToTarget;

            if (EnemyPerceptionMath.ShouldRetreat(
                    dist,
                    p.VerticalDistanceToTarget,
                    def.RetreatDistance,
                    def.RangedRetreatMaxHeight))
            {
                EnemyNavigationResult retreatResult = _enemy.PlanNavigationAwayFrom(targetPos);
                if (EnemyNavigationMath.ShouldContinueRetreat(retreatResult))
                {
                    _enemy.FaceNavigationOrTarget(targetPos);
                    _enemy.MoveAlongNavigation();
                    return;
                }

                // 狭小高台没有完整后撤路线时，后撤只是失败的意图；继续评估攻击，避免该分支饿死战斗行为。
            }

            // 远程射击资格独立于步行拓扑：独立高台即使没有通往玩家的 PathComplete，
            // 只要射程、高差和 Line of Sight 成立，仍可站定施法。
            if (_ranged.CanAttackTarget(targetPos))
            {
                _enemy.FaceTarget(targetPos);
                if (_enemy.AttackCooldownCounter <= 0f)
                {
                    _enemy.StateMachine.ChangeState(_ranged.RangedAttackState);
                    return;
                }

                _enemy.StayGrounded();
                return;
            }

            // 当前站位没有有效射击通道时才请求 NavMesh 接近；无路可走则保留锁敌并周期重试。
            _enemy.PlanNavigationTo(targetPos, 0.05f);
            _enemy.FaceNavigationOrTarget(targetPos);
            _enemy.MoveAlongNavigation();
        }

        public override void Exit()
        {
            _enemy.StopNavigation(true);
        }
    }
}
