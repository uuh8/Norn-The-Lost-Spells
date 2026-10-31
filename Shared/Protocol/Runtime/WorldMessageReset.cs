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
        }
    }
}
