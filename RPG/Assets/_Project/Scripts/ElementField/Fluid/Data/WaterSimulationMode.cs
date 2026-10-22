namespace Game.ElementField
{
    /// <summary>
    /// Water 的 Simulation Truth 选择。默认的 0 必须继续指向已验证的 Legacy Cell，
    /// 这样旧 Profile 或尚未配置 GPU 资源的场景不会因枚举新增而意外切换到 PBF。
    /// </summary>
    public enum WaterSimulationMode : byte
    {
        LegacyCell = 0,
        GpuPbf = 1,
        GridLiquid = 2,
    }
}
