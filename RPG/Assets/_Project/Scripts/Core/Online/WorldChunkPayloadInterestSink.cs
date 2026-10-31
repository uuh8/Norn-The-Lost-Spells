namespace Game.Core
{
    /// <summary>
    /// 客户端离开材料Interest的中立缓存释放入口。释放已安装数量/可见性，保留同WorldEpoch内
    /// 的Generation/Revision栅栏；这不是权威Chunk删除，重入必须重新安装当前Baseline。
    /// 固定容量工作区可以保留以避免探索时反复分配，但释放后的payload不得继续被读取或渲染。
    /// </summary>
    public interface IWorldChunkPayloadInterestSink
    {
        bool TryEvictInterest(WorldChunkKey key);
    }
}
