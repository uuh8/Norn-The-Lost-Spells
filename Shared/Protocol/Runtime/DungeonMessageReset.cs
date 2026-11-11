namespace Game.NetworkProtocol
{
    /// <summary>遭遇稀疏消息也清全部字段与unknown；旧门位姿不能泄入下一Run或无门状态。</summary>
    public sealed partial class DungeonProgressMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = Revision = ServerTick = EncounterIndexPlusOne = Phase = RemainingEnemies = 0;
            GameplayHash = 0; RunId = DungeonId = string.Empty; HasReturnPortal = false;
            ReturnPortalPositionX = ReturnPortalPositionY = ReturnPortalPositionZ = 0;
            ReturnPortalRotationX = ReturnPortalRotationY = ReturnPortalRotationZ = ReturnPortalRotationW = 0;
            _unknownFields = null;
        }
    }
}
