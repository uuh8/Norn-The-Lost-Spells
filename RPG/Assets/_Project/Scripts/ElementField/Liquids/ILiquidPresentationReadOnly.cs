using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 正式液体 Renderer 的完整只读数据面。把原先对 LiquidWorldRuntime 的具体依赖收口，
    /// 使远端数量缓存也能走同一份分层、网格塑形和 Shader；此接口没有推进或写入方法。
    /// Transport 历史仅为表现插值，不授权客户端重新运行 Flux Solver。
    /// </summary>
    public interface ILiquidPresentationReadOnly : ILiquidSurfaceReadOnly, ILiquidSurfaceLayerReadOnly,
        ILiquidStableSurfaceSnapshotReadOnly, ILiquidLayeredSurfaceReadOnly, ILiquidTransportReadOnly
    {
        bool IsFluidInitialized { get; }
        int MaximumActiveTransportCells { get; }
        int MaximumStableRegionCells { get; }
        int ActiveCellCount { get; }
        void CopyPresentationChunkQuantities(ElementChunkKey key, ushort[] destination);
        bool CopyPresentationRow(Vector3Int cell, ushort[] quantities, int offset,
            LiquidCellPresentationSample[] current, LiquidCellPresentationSample[] previous);
        bool TryGetTerrainSupportForPresentation(Vector3Int cell, out LiquidTerrainSupport support);
        float GetPresentationBaseHeight(Vector3Int cell);
        bool TryGetRecentPresentationDeposit(out Vector3 position);
    }
}
