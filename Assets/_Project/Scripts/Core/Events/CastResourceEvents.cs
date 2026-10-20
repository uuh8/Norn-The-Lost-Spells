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
        // V2分别校验程序、牌堆、法杖资源和库存；Legacy入口不读取追加字段。
        public long ExecutionRevision;
        public uint ResourceVersion, InventoryVersion;
        public bool UseV2;
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
