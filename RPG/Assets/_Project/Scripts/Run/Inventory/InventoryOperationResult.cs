using Game.Core;

namespace Game.Run
{
    /// <summary>操作结果为值快照；UI 根据 Reason 反馈，根据 Version 刷新，不自行写入资产。</summary>
    public readonly struct InventoryOperationResult
    {
        public readonly bool Success;
        public readonly InventoryFailureReason Reason;
        public readonly uint Version;

        public InventoryOperationResult(bool success, InventoryFailureReason reason, uint version)
        {
            Success = success;
            Reason = reason;
            Version = version;
        }
    }
}
