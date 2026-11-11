namespace Game.Core
{
    /// <summary>个人主动退出阶段；只有Committed表示业务最终收据已确认，不由断线或Journal成功推断。</summary>
    public enum OnlineRunExitPhase : byte
    { None, Draining, Submitting, Uncertain, Committed, Rejected }

    /// <summary>原因来自DS稳定候选，HP归零消息和客户端按键不能自行宣告最终死亡。</summary>
    public enum OnlineRunExitReason : byte { None, VoluntaryExit, FinalDeath }

    /// <summary>仅预留复活判定扩展口，不引入新道具、救援或任意等待时限。</summary>
    public interface IOnlineRevivalDecisionProvider
    {
        bool IsFinalDeath(ulong actorId);
    }

    /// <summary>当前版本没有复活机制，由明确策略裁决，不使用尸体消失的表现计时。</summary>
    public sealed class NoRevivalProvider : IOnlineRevivalDecisionProvider
    {
        public static readonly NoRevivalProvider Instance = new NoRevivalProvider();
        private NoRevivalProvider() { }
        public bool IsFinalDeath(ulong actorId) => actorId >= 1 && actorId <= 4;
    }

    public struct OnlineRunExitIntent
    {
        public string CommandId;
        public uint ConnectionEpoch;
        // F使用原门与ESC主动退出分开；这仍只是意图，成功资格和进度只能由DS判断。
        public bool ThroughReturnPortal;
    }

    public struct OnlineRunExitStatus
    {
        public string CommandId, Failure, PayloadHash;
        public uint ConnectionEpoch, CommittedVersion;
        public OnlineRunExitPhase Phase;
        public OnlineRunExitReason Reason;
    }

    /// <summary>UI只表达个人意图，不提供资产、金币、PlayerId或结算成功权。</summary>
    public struct OnlineRunExitRequestedEvent : IGameEvent { public string CommandId; public bool ThroughReturnPortal; }
    public struct OnlineRunExitStatusChangedEvent : IGameEvent { public OnlineRunExitStatus Status; }
    /// <summary>本机原F门入口的值意图；Room仅在同Actor/Epoch/World再次核验后开始可靠个人退出。</summary>
    public struct OnlineReturnPortalRequestedEvent : IGameEvent
    { public ulong ActorId; public uint ConnectionEpoch; public int WorldSceneHandle; }
}
