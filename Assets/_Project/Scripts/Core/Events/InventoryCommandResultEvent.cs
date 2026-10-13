namespace Game.Core
{
    /// <summary>命令的明确结果；UI 不用猜测变化事件是否代表自己这次操作。</summary>
    public struct InventoryCommandResultEvent : IGameEvent
    {
        public ulong RequestId;
        public string ItemId, InventoryId;
        public bool Success;
        public InventoryFailureReason Reason;
        public uint Version;
    }
}
