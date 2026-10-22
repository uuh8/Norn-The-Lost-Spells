namespace Game.Character
{
    /// <summary>
    /// 与 Player/Enemy FSM 一致的 Exit -> swap -> Enter 契约。
    /// Action Lock 与 Invulnerability 的兜底释放放在 State.Exit，因此正常完成和异常禁用走同一路径。
    /// </summary>
    internal sealed class BossStateMachine
    {
        private BossStateBase _current;

        public BossRuntimeStateKind CurrentKind =>
            _current != null ? _current.Kind : BossRuntimeStateKind.None;

        public void ChangeState(BossStateBase next)
        {
            if (ReferenceEquals(_current, next))
            {
                return;
            }

            _current?.Exit();
            _current = next;
            _current?.Enter();
        }

        public void Tick(float deltaTime)
        {
            _current?.Tick(deltaTime);
        }
    }
}
