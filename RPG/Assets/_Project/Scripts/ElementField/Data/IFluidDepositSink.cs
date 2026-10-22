namespace Game.ElementField
{
    /// <summary>
    /// ElementWorld Router 可见的最小 GPU Water 写入契约。Projectile 仍只认识 ElementWriteRequest，
    /// 不依赖 GPU Runtime、Buffer 或 ComputeShader；是否能接纳请求由液体子系统自己回答。
    /// </summary>
    public interface IFluidDepositSink
    {
        bool IsFluidInitialized { get; }
        bool TryEnqueueDeposit(in ElementWriteRequest request);
    }

    /// <summary>
    /// Deposit Adapter 的下游 Spawn 契约。把“Amount 如何量化”为粒子与“Queue 是否接纳”分开，
    /// 使拒绝时不前进 Seed，也便于不启动 GPU 的 Pure EditMode Test 覆盖完整转换。
    /// </summary>
    public interface IFluidSpawnSink
    {
        bool TryEnqueueSpawn(in FluidSpawnRequest request);
    }
}
