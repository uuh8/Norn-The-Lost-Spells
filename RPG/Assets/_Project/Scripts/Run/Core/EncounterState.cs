namespace Game.Run
{
    /// <summary>
    /// 单个战斗区域的生命周期。使用 byte 可让状态值保持紧凑，也明确它只是有限状态集合。
    /// </summary>
    public enum EncounterState : byte
    {
        Waiting = 0,
        Armed = 1,
        Active = 2,
        Cleared = 3
    }
}
