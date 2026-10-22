namespace Game.Run
{
    /// <summary>
    /// 最后一个 Encounter 完成后的流程策略。
    /// TerminalVictory 保留 P7 旧语义；AwaitStageExit 让地图一继续运行，等待 Reward/Portal。
    /// </summary>
    public enum RunCompletionMode : byte
    {
        TerminalVictory = 0,
        AwaitStageExit = 1
    }
}
