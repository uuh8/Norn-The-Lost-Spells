namespace Game.Run
{
    /// <summary>
    /// 账号Hub的独立只读命令上下文。它只说明完整Profile的所属玩家与版本，
    /// 不具备Run/数值Actor/ConnectionEpoch，真正Session与Scene门由账号HTTP组合层重新校验。
    /// </summary>
    public interface IAccountInventoryCommandContext
    {
        string PlayerId { get; }
        uint ProfileVersion { get; }
    }

    /// <summary>
    /// 让原库存 Controller 判断是否应提交线上意图的最小身份能力。Source 仍然只读；网络认证、命令发送
    /// 与服务器事务不属于 View。不要把 Profile/Store 塞进这个接口绕过 Authority。
    /// </summary>
    public interface IOnlineInventoryCommandContext
    {
        ulong ActorId { get; }
        uint ConnectionEpoch { get; }
    }

    /// <summary>公开地面列表的只读修订；与个人库存版本分别比较，不向UI暴露Authority Store或写操作。</summary>
    public interface IOnlineGroundInventoryContext
    {
        uint GroundVersion { get; }
    }

}
