namespace Game.Core
{
    public struct ShopTradeResultEvent : IGameEvent
    {
        public string InventoryId, RequestId, Message;
        public bool Success, Replayed;
        public ShopTradeFailure Failure;
        public long Currency, CurrencyDelta, ShopVersion, Generation;
        public ulong ActualQuantity;
        public ulong WarehouseQuantity;
        public MaterialAmount[] Materials;
    }
}
