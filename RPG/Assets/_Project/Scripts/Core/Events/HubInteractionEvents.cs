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
    {
        public string WorldId;
        public bool Handled, Ready;
        // 保留旧合同 Materials[key-1] 的索引。岩土没有可采 GMU，对应位置保持空值。
        public readonly MaterialAmount[] Materials = new MaterialAmount[MaterialAmount.LastLocalInventoryKey];
        // Total 沿用实验区的口径，包括实际存量及队列尚未落格的部分。PendingQuantity 可单独扣出。
        public ulong Total, PendingQuantity;
    }
    public struct ExperimentWorldQueryEvent : IGameEvent { public ExperimentWorldQuery Query; }
    public struct ExperimentSimulationStateEvent : IGameEvent {public string WorldId;public bool Paused;}
}
