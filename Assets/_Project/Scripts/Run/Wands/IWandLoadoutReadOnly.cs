namespace Game.Run
{
    public readonly struct WandStateSnapshot
    {
        public readonly string InstanceId;
        public readonly int SlotIndex;
        public readonly bool Selected, Carried;
        public readonly float CurrentMana, MaxMana, CooldownRemaining, CastCooldown,ManaRegenPerSecond;
        internal WandStateSnapshot(WandRuntimeState state, int slot, bool selected, bool carried)
        { InstanceId = state.InstanceId; SlotIndex = slot; Selected = selected; Carried = carried;
            CurrentMana = state.CurrentMana; MaxMana = state.MaxMana; CooldownRemaining = state.CooldownRemaining; CastCooldown = state.CastCooldown;ManaRegenPerSecond=state.ManaRegenPerSecond; }
    }
    public interface IWandLoadoutReadOnly
    {
        string InventoryId { get; }
        string SelectedWandId { get; }
        uint ResourceVersion { get; }
        bool TryGetSlot(int slot, out WandStateSnapshot state);
        bool TryGetState(string id, out WandStateSnapshot state);
        bool TryGetProgram(string id, out WandProgramSnapshot program);
    }
}
