namespace Game.Run
{
    /// <summary>
    /// 让原库存 Controller 判断是否应提交线上意图的最小身份能力。Source 仍然只读；网络认证、命令发送
    /// 与服务器事务不属于 View。不要把 Profile/Store 塞进这个接口绕过 Authority。
    /// </summary>
    public interface IOnlineInventoryCommandContext
    {
        ulong ActorId { get; }
        uint ConnectionEpoch { get; }
    }
}
