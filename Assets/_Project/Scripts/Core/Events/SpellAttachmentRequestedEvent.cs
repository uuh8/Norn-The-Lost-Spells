namespace Game.Core
{
    public struct SpellAttachmentRequestedEvent : IGameEvent
    {
        public string InventoryId, SpellId, ContainerId, CallerId;
        public ulong RequestId, Dose;
        public uint ExpectedVersion;
        public bool Enabled;
    }
    public struct SpellAttachmentResultEvent : IGameEvent
    {
        public string InventoryId, SpellId, CallerId;
        public ulong RequestId;
        public bool Success;
        public InventoryFailureReason Reason;
    }
    public enum SpellMaterialSupplyStatus : byte { Disabled, Ready, Partial, Empty, InvalidSource }
    public struct SpellMaterialSupplyEvent : IGameEvent
    { public string InventoryId, SpellId; public SpellMaterialSupplyStatus Status; public ulong Requested, Approved; }
}
