namespace Game.NetworkProtocol
{
    /// <summary>冷路径仍清理全部标量和unknown，不能把旧连接的命令留给下一次复用。</summary>
    public sealed partial class RunExitIntentMessage
    {
        public void ResetForReuse()
        { ProtocolVersion = ConnectionEpoch = 0; GameplayHash = 0; CommandId = string.Empty; ThroughReturnPortal = false; _unknownFields = null; }
    }
    public sealed partial class RunExitStatusMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = ConnectionEpoch = CommittedVersion = Reason = 0; GameplayHash = 0;
            CommandId = Failure = PayloadHash = string.Empty; Phase = RunExitWirePhase.Unspecified; _unknownFields = null;
        }
    }
}
