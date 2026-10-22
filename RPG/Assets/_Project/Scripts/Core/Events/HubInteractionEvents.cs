namespace Game.Core
{
    public enum HubInteractionKind : byte { Warehouse, Shop, Expedition, SpellResearch }
    public struct HubInteractionRequestedEvent : IGameEvent { public string InventoryId, CallerId; public ulong RequestId; public HubInteractionKind Kind; }
    public struct HubInteractionResultEvent : IGameEvent { public string InventoryId, CallerId; public ulong RequestId; public HubInteractionKind Kind; public bool Success; public string Message; }
    public enum HubDepartureReviewOperation : byte { Review, Depart, ConfirmAndDepart }
    public struct HubDepartureReviewRequestedEvent : IGameEvent {public string InventoryId;public HubDepartureReviewOperation Operation;}
    public struct HubDepartureReviewResultEvent : IGameEvent {public string InventoryId;public bool WorldReady,Departing;public ulong ResidentQuantity;public string Message;}
    /// <summary>离散同步查询；Core只承载材料键/数量，不引用ElementField或Run的世界实现。</summary>
    public sealed class ExperimentWorldQuery
    { public string WorldId;public bool Handled,Ready;public readonly MaterialAmount[] Materials=new MaterialAmount[8];public ulong Total; }
    public struct ExperimentWorldQueryEvent : IGameEvent { public ExperimentWorldQuery Query; }
    public struct ExperimentSimulationStateEvent : IGameEvent {public string WorldId;public bool Paused;}
}
