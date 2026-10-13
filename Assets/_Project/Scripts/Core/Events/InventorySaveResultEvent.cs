namespace Game.Core
{
    public struct InventorySaveResultEvent : IGameEvent
    {
        public string InventoryId, Message;
        public ulong RequestId;
        public bool Success;
        public long Generation;
        public uint Version;
    }
}
