namespace Game.ElementField
{
    /// <summary>
    /// Projectile 等写入方可见的最小契约。调用方只提交不可变 Request，不获得 Grid、Chunk 或 Runtime
    /// 的具体引用，从而让固定实验场与稀疏连续世界可以替换而不修改法术 Prefab。
    /// </summary>
    public interface IElementWriteSink
    {
        bool TryEnqueueWrite(in ElementWriteRequest request);
    }
}
