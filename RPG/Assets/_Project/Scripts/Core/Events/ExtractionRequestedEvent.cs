namespace Game.Core
{
    public struct ExtractionRequestedEvent : IGameEvent
    { public string RaidId; public bool Inside, Cancel, Start; }
    public struct RaidConditionCompletedEvent : IGameEvent
    { public string RaidId, Key; }
}
