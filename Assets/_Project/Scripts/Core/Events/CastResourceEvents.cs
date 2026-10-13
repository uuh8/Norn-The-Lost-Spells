namespace Game.Core
{
    public enum CastResourceOperation : byte { PrepareRoot, PrepareLayer, Complete, Cancel, Abandon, AbandonCaller }
    // Core 只携带身份，不依赖 Skills/Run；权威执行计划由 Run 自行建立。
    public struct CastResourceRequestedEvent : IGameEvent
    {
        public string InventoryId, SourceWandId, CallerId, TicketId;
        public string MaterialWorldId;
        public ulong RequestId;
        public uint ProgramRevision;
        public int LayerIndex;
        public CastResourceOperation Operation;
    }
    public struct CastResourceResultEvent : IGameEvent
    {
        public CastResourceOperation Operation;
        public string InventoryId, SourceWandId, CallerId, TicketId;
        public ulong RequestId;
        public bool Success;
        public OwnedWandFailureReason Reason;
    }
    public struct SpellRaidLifecycleRequestedEvent : IGameEvent
    { public string InventoryId, RaidId; public bool Begin; }
    public struct SpellRaidLifecycleResultEvent : IGameEvent
    { public string InventoryId, RaidId; public bool Success; public InventoryFailureReason Reason; }
}
