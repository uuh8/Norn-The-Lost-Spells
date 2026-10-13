namespace Game.Core
{
    /// <summary>统一UI/Input/资产层的槽数；数字0是最后一个手持格的标签，不是首格下标。</summary>
    public static class HeldItemSlots
    {
        public const int WandCount = 3, ToolCount = 7, TotalCount = WandCount + ToolCount;
        public static string KeyLabel(int heldSlot) => heldSlot == 9 ? "0" :
            heldSlot == 0 ? "1" : heldSlot == 1 ? "2" : heldSlot == 2 ? "3" : heldSlot == 3 ? "4" :
            heldSlot == 4 ? "5" : heldSlot == 5 ? "6" : heldSlot == 6 ? "7" : heldSlot == 7 ? "8" : heldSlot == 8 ? "9" : "";
    }
    public enum ToolbeltFailure : byte { None, InvalidSlot, NotCarried, InvalidKind, StaleVersion, Busy, BackpackFull }
    // 快捷位传实例引用；Core不认识库存实现，不能把UI控件当成资产。
    public struct ToolbeltBindingRequestedEvent : IGameEvent
    { public string InventoryId, ItemId; public int ToolSlot; public uint ExpectedVersion; public ulong RequestId; }
    public struct ToolbeltBindingResultEvent : IGameEvent
    { public string InventoryId; public ulong RequestId; public bool Success; public ToolbeltFailure Failure; }
    public struct HeldItemSelectionRequestedEvent : IGameEvent
    { public string InventoryId; public int SlotIndex; }
    public struct ToolbeltChangedEvent : IGameEvent
    { public string InventoryId, ItemId; public int SelectedSlot; public uint ConfigurationVersion, SelectionVersion; }
    public struct HeldItemUseResetEvent : IGameEvent { public string InventoryId; }
}
