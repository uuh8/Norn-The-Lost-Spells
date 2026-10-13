namespace Game.Core
{
    /// <summary>成功写入后通知读者刷新；无操作成功或失败不伪造变化版本。</summary>
    public struct InventoryChangedEvent : IGameEvent
    {
        public uint Version;
        public string InventoryId;
    }
}
