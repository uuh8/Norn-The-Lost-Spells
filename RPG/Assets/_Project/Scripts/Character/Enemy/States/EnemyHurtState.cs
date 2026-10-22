using UnityEngine;

namespace Game.Character
{
    /// <summary>受击硬直：定身播受击动画，计时结束回追击/待机。由 EnemyController 收到自身受击事件时切入(打断当前动作)。</summary>
    public class EnemyHurtState : EnemyStateBase
    {
        private float _timer;

        public EnemyHurtState(EnemyControllerBase enemy) : base(enemy) { }

        public override void Enter()
        {
            _enemy.StopNavigation(true);
            _timer = _enemy.Definition != null ? _enemy.Definition.HurtDuration : 0.3f;
            _enemy.CrossFade(_enemy.HurtStateHash);
        }

        public override void Update()
        {
            _enemy.StayGrounded();
            _timer -= Time.deltaTime;
            if (_timer <= 0f)
            {
                EnemyPerception p = _enemy.Perception;
                _enemy.StateMachine.ChangeState(p.HasTarget ? _enemy.EngageState : _enemy.IdleState);
            }
        }

        public override void Exit() { }
    }
}
