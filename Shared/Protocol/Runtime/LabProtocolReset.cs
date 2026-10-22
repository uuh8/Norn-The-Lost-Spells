namespace Game.NetworkProtocol
{
    /// <summary>快照解析前清空旧字段和未知字段；字段演进时与 lab.proto 同步维护。</summary>
    public sealed partial class LocalLabSnapshot
    {
        public void ResetForReuse()
        {
            ProtocolVersion = 0;
            ActorId = 0;
            ServerTick = 0;
            LastAcceptedProbeSequence = 0;
            _unknownFields = null;
        }
    }
}
