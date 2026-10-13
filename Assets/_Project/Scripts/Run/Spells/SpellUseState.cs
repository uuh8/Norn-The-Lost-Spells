using System.Collections.Generic;
using Game.Core;

namespace Game.Run
{
    internal interface IInventoryCommitPolicy
    {
        InventoryFailureReason Prepare(Dictionary<string, InventoryEntry> candidate, out object preparation, out bool changed);
        void Commit(object preparation);
    }
    public readonly struct SpellUseState
    {
        public readonly int RemainingCharges;
        public readonly bool Unlimited, IsQualification, Eligible;
        public SpellUseState(int remaining, bool unlimited, bool qualification, bool eligible)
        { RemainingCharges = remaining; Unlimited = unlimited; IsQualification = qualification; Eligible = eligible; }
    }
    public interface ISpellUseReadOnly
    {
        string RaidId { get; }
        uint Version { get; }
        bool TryGetState(string instanceId, out SpellUseState state);
    }
}
