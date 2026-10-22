namespace Game.Core
{
    public enum SpellExecutionObservationKind : byte { OutputReleased, LayerCancelled }
    public struct SpellExecutionObservedEvent : IGameEvent
    {
        public string InventoryId, SourceWandId, CallerId, TicketId;
        public uint ProgramRevision;
        public int LayerId, OutputId;
        public double SimulationTime;
        public SpellExecutionObservationKind Kind;
    }
}
