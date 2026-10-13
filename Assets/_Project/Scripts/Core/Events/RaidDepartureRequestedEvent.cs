namespace Game.Core
{
    public enum RaidCommand : byte { Protect, Unprotect, Depart, StarterKit, Abandon, RetrySave }
    public struct RaidDepartureRequestedEvent : IGameEvent
    { public string InventoryId, ItemId; public RaidCommand Command; }
}
