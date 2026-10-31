using Game.Core;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private bool _materialTransactionInProgress;
        internal bool IsMaterialTransactionInProgress => _materialTransactionInProgress;

        /// <summary>
        /// 库存 Port 的 World 级互斥入口，必须早于 Port scratch 初始化、接口属性读取和 TryReserve。
        /// Adapter 自身的 _executing 不能阻止另一 Adapter 或直接 Port 重入；锁归 World 才能统一保护
        /// 全部数量 Owner 与 Journal。这里也拒绝材料 Tick 中启动新的库存事务，避免交错修改同一个 Store。
        /// DebugPause/Frozen 仍允许主动投放/收取，它们只停止时间，不代表 World 发生不可恢复的故障。
        /// </summary>
        internal MaterialTransferFailure TryBeginMaterialTransaction()
        {
            if (!CanAcceptWorldWrites) return MaterialTransferFailure.WorldUnavailable;
            if (IsWorldStepInProgress) return MaterialTransferFailure.ResourceBusy;
            _materialTransactionInProgress = true;
            return MaterialTransferFailure.None;
        }

        /// <summary>
        /// 外部 TryReserve/Commit/Cancel 或内部 Stage/Publish 可能异常退出。Commit 也可能已经改变库存，
        /// 因此不能捕获后盲目 Undo 并宣称跨端回滚；锁存 Faulted，阻止新的写入和 Source 发布，等待重建 World。
        /// 这与普通容量/几何失败的原有 Undo→Cancel 返回路径不同；普通失败不会污染健康标志。
        /// </summary>
        internal void MarkMaterialTransactionFaulted() => _simulationFaulted = true;

        // 只能由已取得此锁的 Port finally 调用；拒绝的嵌套调用不进入 try/finally，也不能释放外层所有权。
        internal void EndMaterialTransaction() => _materialTransactionInProgress = false;
    }
}
