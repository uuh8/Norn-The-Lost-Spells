namespace Game.Character
{
    /// <summary>待机：原地贴地，感知到玩家就转入追击。</summary>
    public class EnemyIdleState : EnemyStateBase
    {
        public EnemyIdleState(EnemyControllerBase enemy) : base(enemy) { }

        public override void Enter()
        {
            _enemy.StopNavigation(true);
        }

        public override void Update()
        {
            _enemy.StayGrounded();
            if (_enemy.Perception.HasTarget)
                _enemy.StateMachine.ChangeState(_enemy.EngageState);
        }

        public override void Exit() { }
    }
}
