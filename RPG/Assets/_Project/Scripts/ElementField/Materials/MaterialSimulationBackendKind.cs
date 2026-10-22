namespace Game.ElementField
{
    /// <summary>
    /// 一次 Material Write 的唯一 Simulation Owner。Unsupported 不是“写入失败后随便回退”，
    /// 而是初始化期明确发布的路由结果，Router 遇到它时不会触碰任何 Sink。
    /// </summary>
    public enum MaterialSimulationBackendKind : byte
    {
        Unsupported = 0,
        ElementCell = 1,
        GpuPbfLiquid = 2,
        GridLiquid = 3,
        FixedSolid = 4,
        GridGas = 5,
    }
}
