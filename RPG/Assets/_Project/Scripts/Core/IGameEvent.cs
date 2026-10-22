namespace Game.Core
{
    /// <summary>
    /// Marker interface for all EventBus event types.
    /// Implement on a struct to define a game event; the struct fields carry event payload.
    /// </summary>
    public interface IGameEvent { }
}
