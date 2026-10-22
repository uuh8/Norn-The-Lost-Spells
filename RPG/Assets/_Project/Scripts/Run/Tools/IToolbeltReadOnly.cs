namespace Game.Run
{
    public interface IToolbeltReadOnly
    {
        string InventoryId { get; }
        uint ConfigurationVersion { get; }
        uint SelectionVersion { get; }
        int SelectedSlot { get; }
        string SelectedItemId { get; }
        bool TryGetBinding(int toolSlot, out string instanceId);
    }
}
