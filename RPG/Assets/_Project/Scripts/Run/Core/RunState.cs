namespace Game.Run
{
    /// <summary>
    /// 整局游戏的高层状态。StageCleared 表示当前地图已清场但整局仍可继续；
    /// 只有 Completed 与 Failed 是 Terminal State。
    /// </summary>
    public enum RunState : byte
    {
        NotStarted = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        StageCleared = 4
    }
}
