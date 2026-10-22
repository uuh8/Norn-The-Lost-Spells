using Game.Skills;

namespace Game.Run
{
    // 每层只有一枚执行预算。仅服务持有可写状态，读者只能取冻结的输出层。
    internal sealed class CastResourceReservation
    {
        internal readonly string TicketId, SourceWandId, CallerId;
        internal readonly uint ProgramRevision;
        internal readonly CastExecutionPlan Plan;
        internal readonly bool[] Attempted;
        internal bool RootCommitted;
        internal string MaterialWorldId;
        internal SpellAttachmentState[][] Attachments;
        internal string[][] Cards;
        internal CastResourceReservation(string ticket, string source, string caller, uint revision, CastExecutionPlan plan)
        { TicketId = ticket; SourceWandId = source; CallerId = caller; ProgramRevision = revision; Plan = plan; Attempted = new bool[plan.LayerCount]; }
    }
    public interface ICastResourceReadOnly
    {
        bool TryGetLayer(string ticket, int layerIndex, out CastExecutionLayer layer);
        bool TryGetMaterialOutput(string ticket, int layerIndex, int command, out SpellMaterialOutputSnapshot output);
        int ActiveReservations { get; }
    }
}
