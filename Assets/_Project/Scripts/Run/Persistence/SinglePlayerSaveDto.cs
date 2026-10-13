using System;

namespace Game.Run
{
    /// <summary>JsonUtility 只能处理普通序列化字段；稳定 ID 和数组替代 Unity 引用/Dictionary。</summary>
    [Serializable]
    public sealed class SinglePlayerSaveDto
    {
        public int SchemaVersion;
        public long SaveGeneration, Currency;
        public uint InventoryVersion;
        public string ActorId;
        public SinglePlayerItemDto[] Items;
        public PendingRaidRecord PendingRaid;
        public RaidSettlementReceipt LastRaidReceipt;
        public bool HasPendingRaid, HasLastRaidReceipt;
        public long AdventureProgress;
        public ShopRuntimeState Shop;
        public string[] ToolBindings;
        public HubWorldItemRecord[] HubWorldItems;
    }
    [Serializable]
    public sealed class SinglePlayerItemDto
    {
        public string InstanceId, ContentId, StackStateKey, OwnerId;
        public int RemainingCharges, X, Y, QuarterTurns, SlotIndex;
        public bool HasUnlimitedCharges;
        public ItemLocationKind LocationKind;
        public Game.Core.MaterialAmount[] Composition;
        public ulong ContainerCapacity;
        public ushort ContainerMaterials;
        public byte MaterialKey;
        public ulong MaterialQuantity;
        public string AttachmentContainerId;
        public ulong AttachmentDose;
        public bool AttachmentEnabled;
    }
}
