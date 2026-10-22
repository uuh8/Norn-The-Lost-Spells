namespace Game.Core
{
    public enum InventoryCommandKind : byte
    {
        Place = 0, MoveStack = 1, Split = 2, Drop = 3, Pickup = 4, EquipWand = 5, ReplaceBackpack = 6, OrganizeBackpack = 7
    }
    public enum InventoryPickupDestination : byte { Backpack = 0, EquippedWand = 1, EquippedBackpack = 2, WandSlot = 3, SpellStorage = 4 }
    public enum InventoryDestination : byte { Backpack = 0, Warehouse = 1, WandSlot = 2, SpellStorage = 3 }
    /// <summary>
    /// 上层只提交意图和看到的版本。Core 不引用 Run 实例或 SO；Pickup 由匹配的世界代理接收，
    /// 其他命令的会话订阅在 M1-T3 接入。RequestId 由调用方提供以对应一次反馈。
    /// </summary>
    public struct InventoryCommandEvent : IGameEvent
    {
        public ulong RequestId;
        public InventoryCommandKind Kind;
        public InventoryPickupDestination PickupDestination;
        public InventoryDestination Destination;
        public string InventoryId;
        public bool WholeStack;
        public string ItemId, OwnerId, WorldOwner;
        public int X, Y, QuarterTurns, SlotIndex;
        public uint ExpectedVersion;
    }
}
