namespace Game.Core
{
    public struct ContainerSelectionChangedEvent : IGameEvent
    { public string InventoryId, ItemId; }
    /// <summary>结果只有值/身份；实际组分由只读 CopyTo 获取独立快照，事件不借出下帧会复用的数组。</summary>
    public struct MaterialTransferResultEvent : IGameEvent
    {
        public string InventoryId, WorldId, SourceId, ItemId;
        public ulong RequestId, Transferred;
        public uint Version;
        public MaterialTransferFailure Failure;
        public bool Success => Failure == MaterialTransferFailure.None && Transferred > 0;
    }
}
