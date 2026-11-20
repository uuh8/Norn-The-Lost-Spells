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

    /// <summary>
    /// 可选的只读材料占据事实。环境Collider的粗格OR只表示“可能碰到边缘”，不能证明格中心有地板；
    /// 真实冰等整格材料则由数量定义阻挡，允许数量已提交而Collider尚未发布的窗口继续支撑液体。
    /// 此能力不创建Collider、不唤醒Chunk、不推进模拟，也不把Rendering变成材料数量Owner。
    /// </summary>
    public interface ILiquidPresentationSolidSource
    {
        bool IsLogicalSolidForPresentation(Vector3Int cell);
    }
}
