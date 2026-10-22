using System;

namespace Game.Run
{
    /// <summary>据点资产的内存聚合；不保存 Scene、敌人或元素模拟。写入由会话持有，UI 只读投影。</summary>
    public sealed class SinglePlayerProfile
    {
        public string ActorId { get; internal set; }
        public ItemOwnershipStore Items { get; private set; }
        public PendingRaidRecord PendingRaid { get; internal set; }
        public RaidSettlementReceipt LastRaidReceipt { get; internal set; }
        public long SaveGeneration { get; private set; }
        public long Currency { get; internal set; }
        public long AdventureProgress { get; internal set; }
        public ShopRuntimeState Shop { get; internal set; }
        internal System.Collections.Generic.List<HubWorldItemRecord> HubWorldItems {get;set;}=new System.Collections.Generic.List<HubWorldItemRecord>();
        internal string[] ToolBindings { get; set; } = new string[Game.Core.HeldItemSlots.ToolCount];
        internal uint ToolConfigurationVersion { get; set; }
        // 仅进程内输出归属计数，不写据点续玩格式；Mapper/Repository也必须拒绝半途快照。
        public int PendingMaterialOutputs { get; internal set; }
        // 仅进程内权限：可靠提交/转场期间，所有通用库存提交也必须拒绝；不保存为续玩状态。
        internal bool RaidActionsFrozen { get; set; }
        public SinglePlayerProfile(string actorId, ItemOwnershipStore items, long generation = 0, long currency = 0)
        {
            if (string.IsNullOrWhiteSpace(actorId) || items == null || generation < 0 || currency < 0) throw new ArgumentException("Profile 基础数据非法。");
            ActorId = actorId; Items = items; SaveGeneration = generation; Currency = currency;
        }
        internal void ConfirmSaved(long generation) { SaveGeneration = generation; }
        internal void Adopt(SinglePlayerProfile candidate)
        {
            ActorId = candidate.ActorId; Items = candidate.Items; SaveGeneration = candidate.SaveGeneration;
            PendingRaid = candidate.PendingRaid; LastRaidReceipt = candidate.LastRaidReceipt; PendingMaterialOutputs = 0;
            // 可靠候选是完整资产聚合；只采用物品会遗失已经写盘的钱包/商店变化。
            Currency = candidate.Currency; AdventureProgress = candidate.AdventureProgress; Shop = candidate.Shop;
            HubWorldItems=new System.Collections.Generic.List<HubWorldItemRecord>(HubWorldItemRecord.CopyAll(candidate.HubWorldItems));
            // 候选不共享可写快捷数组；其后还可能被交易/回滚使用。
            ToolBindings = (string[])candidate.ToolBindings.Clone(); ToolConfigurationVersion++;
        }
    }

    public enum ProfilePersistenceStatus : byte
    {
        Success, Missing, InvalidData, UnsupportedSchema, UnknownContent, IoError, WriteBlocked, RecoveredBackup
    }
    public readonly struct ProfilePersistenceResult
    {
        public readonly ProfilePersistenceStatus Status;
        public readonly string Message;
        public bool Success => Status == ProfilePersistenceStatus.Success || Status == ProfilePersistenceStatus.RecoveredBackup;
        public ProfilePersistenceResult(ProfilePersistenceStatus status, string message = null) { Status = status; Message = message; }
    }
}
