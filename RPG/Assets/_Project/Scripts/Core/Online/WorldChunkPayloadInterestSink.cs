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

    /// <summary>
    /// 客户端缓存的可选元数据退休能力。它与普通Interest退出分开：Evict仅撤销只读数量并保留
    /// Generation/Revision；Retire会真正忘记该Key历史，只有可信协调器已经用当前连接的单调
    /// SubscriptionEpoch和可靠Leave隔离全部旧页/旧Enter，并释放实际占据之后，才能调用。
    /// 本接口不验证Wire身份、不发送Leave，也不修改DS材料；调用方必须先建立上述协议栅栏。
    /// 不具备此扩展的Installer继续沿旧合同保留历史，不能被协调器猜测为已支持安全归池。
    /// </summary>
    public interface IWorldChunkPayloadRetirementSink
    {
        bool TryRetireInterest(WorldChunkKey key);
    }
}
