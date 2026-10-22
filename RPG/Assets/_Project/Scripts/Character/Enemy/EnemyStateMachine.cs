namespace Game.Character
{
    /// <summary>敌人状态机调度员。平行于 PlayerStateMachine：Exit→换引用→Enter，由 EnemyController.Update 驱动。</summary>
    public class EnemyStateMachine
    {
        public EnemyStateBase CurrentState { get; private set; }

        public void ChangeState(EnemyStateBase newState)
        {
            CurrentState?.Exit();
            CurrentState = newState;
            CurrentState.Enter();
        }

        public void Update() => CurrentState?.Update();
    }
}
