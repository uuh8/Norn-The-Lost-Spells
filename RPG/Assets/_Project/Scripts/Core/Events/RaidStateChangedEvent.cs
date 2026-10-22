namespace Game.Core
{
    public enum RaidPhase : byte { HubReady, Departing, Exploring, Extracting, Settling, SaveFailed }
    public enum RaidOutcome : byte { Success, Death, Abandoned, Closed, Crash }
    public struct RaidStateChangedEvent : IGameEvent
    { public string InventoryId, RaidId, Message; public RaidPhase Phase; public bool Success; }
    public struct RaidResultEvent : IGameEvent
    { public string InventoryId, RaidId; public RaidOutcome Outcome; public int Returned, Lost; public long Generation; }
}
