using UnityEngine;

namespace Game.Core
{
    public struct ContainerImpactEvent : IGameEvent
    { public string InventoryId, WorldId, FlightId; public Vector3 Position; }
    public enum ContainerTransitEndReason : byte { Deposited, SceneEnded, ProxyDestroyed }
    public struct ContainerTransitEndedEvent : IGameEvent
    { public string InventoryId, WorldId, FlightId; public ContainerTransitEndReason Reason; }
    public struct ContainerTransitResolvedEvent : IGameEvent
    { public string InventoryId, FlightId; public ContainerTransitEndReason Reason; public ulong Quantity; }
}
