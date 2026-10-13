namespace Game.Core
{
    public enum ShopTradeKind : byte { BuyItem, SellItem, BuyMaterial, SellMaterial }
    public enum ShopTradeFailure : byte
    {
        None, InvalidRequest, NotInHub, ResourceBusy, StaleVersion, UnknownOffer,
        InsufficientCurrency, OutOfStock, InventorySpace, NotSellable, ContainerNotEmpty,
        IncompatibleMaterial, InsufficientMaterial, ContainerFull, BudgetExceeded,
        Overflow, SaveFailed, ConfigurationMismatch
    }
    /// <summary>跨Assembly只传稳定身份与数值，不传可写Profile或容器引用；价格由Run权威配置决定。</summary>
    [System.Serializable]
    public struct ShopTradeRequestedEvent : IGameEvent
    {
        public string InventoryId, RequestId, OfferId, ItemInstanceId, ContainerId, BackpackId;
        public uint InventoryVersion;
        public long ShopVersion;
        public ShopTradeKind Kind;
        public ulong Quantity;
        public bool ToBackpack;
        // 默认 false 保留旧请求的严格目标；快捷购买显式允许在同一候选里回退入仓。
        public bool AllowWarehouseFallback;
    }
}
