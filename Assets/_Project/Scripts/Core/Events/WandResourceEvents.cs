namespace Game.Core
{
    public enum OwnedWandFailureReason : byte
    { None, InvalidRequest, UnknownSource, NotCarried, StaleProgram, Cooldown, InsufficientMana, NoReceiver,
        InsufficientUses, MissingEligibility, InvalidProgram, BudgetConsumed, ResourceBusy, SpawnFailed }
    // Core 仅传原始字段，不引用 Run/Skills；支付结果同步匹配调用者和请求，防止串会话或重复扣费。
    public struct WandSelectionRequestedEvent : IGameEvent { public string InventoryId; public int SlotIndex; }
    public struct WandResourceRequestedEvent : IGameEvent
    {
        public string InventoryId, SourceWandId, CallerId;
        public ulong RequestId;
        public uint ProgramRevision;
        public float ManaCost;
        public bool IsRootCast;
    }
    public struct WandResourceResultEvent : IGameEvent
    {
        public string InventoryId, SourceWandId, CallerId;
        public ulong RequestId;
        public bool Success;
        public OwnedWandFailureReason Reason;
        public float CurrentMana, CooldownRemaining;
    }
}
