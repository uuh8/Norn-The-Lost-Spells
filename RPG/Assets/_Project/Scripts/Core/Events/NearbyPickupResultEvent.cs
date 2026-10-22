namespace Game.Core
{
    /// <summary>Run已裁定拾取；满包只通知UI打开重排，不把地面资产假装放入背包。</summary>
    public struct NearbyPickupResultEvent : IGameEvent
    {
        public string InventoryId,ItemId;
        public bool Success,RequiresArrangement;
        public InventoryFailureReason Reason;
    }
}
