using System;
namespace Game.Run
{
    /// <summary>出发回滚只需资产字段；避免DTO→Pending→DTO递归，触发Unity inline序列化深度限制。</summary>
    [Serializable]
    public sealed class ProfileAssetSnapshot
    {
        public string ActorId;
        public long SaveGeneration, Currency;
        public uint InventoryVersion;
        public SinglePlayerItemDto[] Items;
        public RaidSettlementReceipt LastRaidReceipt;
        public long AdventureProgress;
        public ShopRuntimeState Shop;
        public string[] ToolBindings;
        public HubWorldItemRecord[] HubWorldItems;
        public static ProfileAssetSnapshot From(SinglePlayerSaveDto dto) => new ProfileAssetSnapshot
        { ActorId = dto.ActorId, SaveGeneration = dto.SaveGeneration, Currency = dto.Currency, InventoryVersion = dto.InventoryVersion,
            Items = dto.Items, LastRaidReceipt = dto.LastRaidReceipt, AdventureProgress = dto.AdventureProgress, Shop = dto.Shop?.Copy(),
            ToolBindings = dto.ToolBindings == null ? null : (string[])dto.ToolBindings.Clone(),
            HubWorldItems=HubWorldItemRecord.CopyAll(dto.HubWorldItems??Array.Empty<HubWorldItemRecord>()) };
        public SinglePlayerSaveDto ToDto() => new SinglePlayerSaveDto { SchemaVersion = ProfileSaveMapper.CurrentSchema,
            ActorId = ActorId, SaveGeneration = SaveGeneration, Currency = Currency, InventoryVersion = InventoryVersion,
            Items = Items, LastRaidReceipt = LastRaidReceipt, HasLastRaidReceipt = LastRaidReceipt != null && !string.IsNullOrEmpty(LastRaidReceipt.RaidId),
            AdventureProgress = AdventureProgress, Shop = Shop?.Copy(),
            ToolBindings = ToolBindings == null ? null : (string[])ToolBindings.Clone(),
            HubWorldItems=HubWorldItemRecord.CopyAll(HubWorldItems??Array.Empty<HubWorldItemRecord>()) };
    }
}
