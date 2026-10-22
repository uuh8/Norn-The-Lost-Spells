namespace Game.Core
{
    /// <summary>UI 请求保存当前资产；Core 不引用存档实现或文件路径。</summary>
    public struct InventorySaveRequestedEvent : IGameEvent
    {
        public string InventoryId;
        public ulong RequestId;
    }
}
