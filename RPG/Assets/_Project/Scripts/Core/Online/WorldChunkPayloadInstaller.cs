namespace Game.Core
{
    /// <summary>
    /// Core到实际只读材料Replica的安装端口。调用发生在完整页/Checksum/数量/版本检查之后，
    /// 版本提交之前。实现必须预检全部容量及所有候选，再一次发布，返回false时整个旧Store保持不变。
    /// words仅在同步调用内借用，不得修改、保留数组引用或在安装中回调Assembler；不能在这里发ACK。
    /// 成功意味着新数据已可读，随后Assembler才能统一推进版本并允许上层ACK。
    /// 抛异常意味着实现违反了可判定的原子边界；调用方无法回滚外部已发生的写，必须销毁重建Replica。
    /// </summary>
    public interface IWorldChunkPayloadInstaller
    {
        bool TryInstall(WorldChunkPayloadHeader header, uint[] words, int count);
    }
}
