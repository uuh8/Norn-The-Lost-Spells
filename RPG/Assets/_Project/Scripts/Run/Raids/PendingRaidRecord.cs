using System;
using Game.Core;
namespace Game.Run
{
    [Serializable]
    public sealed class PendingRaidRecord
    {
        public string RaidId, HubActorId, RaidActorId;
        public RaidPhase Phase;
        public string[] ProtectedIds, HubAssetIds, QualifiedIds;
        // 仅出发前资产快照；不包含敌人/材料世界，也不允许嵌套Pending记录。
        public ProfileAssetSnapshot Rollback;
        public PendingRaidRecord Copy() => new PendingRaidRecord { RaidId = RaidId, HubActorId = HubActorId, RaidActorId = RaidActorId,
            Phase = Phase, ProtectedIds = (string[])ProtectedIds.Clone(), HubAssetIds = (string[])HubAssetIds.Clone(),
            QualifiedIds = (string[])QualifiedIds.Clone(), Rollback = Rollback };
        public bool Protects(string id) => Array.IndexOf(ProtectedIds, id) >= 0;
        public bool IsHubAsset(string id) => Array.IndexOf(HubAssetIds, id) >= 0;
    }
}
