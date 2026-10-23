namespace Game.Character
{
    /// <summary>
    /// 状态机调度员。普通 C# 类，不是 MonoBehaviour。
    /// 由 PlayerControllerBase 持有，PlayerControllerBase.Update() 手动驱动它。
    /// 只负责"持有当前状态"和"切换状态"，不含游戏逻辑。
    /// </summary>
    public class PlayerStateMachine
    {
        public PlayerStateBase CurrentState { get; private set; }

        /// <summary>
        /// 按固定顺序切换状态：旧状态释放资源 -> 替换唯一引用 -> 新状态初始化。
        /// ?. 是 C# null 条件运算符；首次进入状态时 CurrentState 为 null，因此不会调用 Exit。
        /// </summary>
        public void ChangeState(PlayerStateBase newState)
        {
            CurrentState?.Exit();
            CurrentState = newState;
            CurrentState.Enter();
        }

        /// <summary>
        /// 运动校正恢复已有阶段，不是一次新的 Gameplay 转换；执行 Enter/Exit 会重置空中预算、
        /// 消费输入、启动 Dash 冷却并重复反馈。只允许角色 Owner 为预创建的运动 State 调用。
        /// </summary>
        public void RestoreState(PlayerStateBase state) => CurrentState = state;

        /// <summary>
        /// 每帧推进当前状态，由 PlayerControllerBase.Update() 调用。
        /// 状态机自己不知道 Unity 的生命周期，需要外部手动驱动。
        /// </summary>
        public void Update()
        {
            CurrentState?.Update();
        }
    }
}
