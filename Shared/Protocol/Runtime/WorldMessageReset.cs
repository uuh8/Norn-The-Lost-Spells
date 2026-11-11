namespace Game.NetworkProtocol
{
    /// <summary>仅清有效长度，不释放已预热容量；Schema演进必须同时补齐字段与unknown清理。</summary>
    public sealed partial class WorldPageMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = 0; GameplayHash = 0; ConnectionEpoch = 0; WorldEpoch = 0;
            SnapshotId = 0; Kind = 0; Domain = 0; ChunkX = ChunkY = ChunkZ = 0;
            Generation = BaseRevision = Revision = 0; PageIndex = PageCount = ServerTick = 0;
            Checksum = 0; WordOffset = TotalWords = 0; Words.Clear(); _unknownFields = null;
            // Codec复用同一对象解析数据页与Enter/Leave。省略新字段必须回到非法零代，
            // 不能沿用上一条消息的订阅，从而让缺代次/旧连接数据穿过生命周期栅栏。
            SubscriptionEpoch = 0;
        }
    }

    /// <summary>世界身份的复用清理；新字段与unknown fields必须一起清除，不把旧连接上下文带入下一次解析。</summary>
    public sealed partial class WorldIdentityMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = RoomEpoch = ConnectionEpoch = 0;
            RunId = DungeonId = ""; WorldEpoch = 0; _unknownFields = null;
        }
    }
}
