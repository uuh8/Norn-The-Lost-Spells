namespace Game.Core
{
    /// <summary>
    /// 资产命令的公共失败语义。Core 只描述结果，不认识物品 SO 或 Run 的实例表。
    /// 显式编号可供后续 UI/EventBus 消费；新增值只追加，避免改变已有结果协议。
    /// </summary>
    public enum InventoryFailureReason : byte
    {
        None = 0,
        NotFound = 1,
        WrongKind = 2,
        OutOfBounds = 3,
        Occupied = 4,
        Capacity = 5,
        InvalidOwnership = 6,
        InvalidState = 7,
        StaleVersion = 8,
        InvalidIdentity = 9,
        DuplicateIdentity = 10,
        UnknownContent = 11,
        InsufficientUses = 12,
        ResourceBusy = 13,
        WarehouseUnavailable = 14
    }
}
