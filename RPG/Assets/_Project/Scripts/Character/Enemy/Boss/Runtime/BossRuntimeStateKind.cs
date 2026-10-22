namespace Game.Character
{
    /// <summary>
    /// Boss FSM 的只读可观察状态，供测试、Debug Overlay 与后续 HUD 使用。
    /// Gameplay 转换仍由 StateMachine 控制，外部不能直接写入。
    /// </summary>
    public enum BossRuntimeStateKind : byte
    {
        None = 0,
        Inactive = 1,
        Approach = 2,
        Decision = 3,
        Cast = 4,
        PhaseTransition = 5,
        Teleport = 6,
    }
}
