namespace Game.Core
{
    public struct ExtractionProgressChangedEvent : IGameEvent
    { public string InventoryId, RaidId; public bool Ready, Inside, Cancelled, Running; public float Remaining; public ulong Collected, Required; }
}
