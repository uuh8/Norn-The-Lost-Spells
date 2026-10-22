using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 稀疏元素世界提供给 Presentation 的只读边界。
    ///
    /// Visible Chunk 与 Solver Active Chunk 不是同一个概念：稳定水可以停止 Simulation，
    /// 但只要它仍位于玩家的显示范围内，就必须继续被 Rendering 看见、被 Exposure 读取。
    /// 接口只提供按值读取和调用方 Buffer 写入，不暴露 Store、Grid 或任何写权限。
    /// </summary>
    public interface IElementWorldReadOnly
    {
        bool IsInitialized { get; }
        Vector3 Origin { get; }
        float CellSize { get; }
        int ChunkSize { get; }
        int MaximumResidentChunkCount { get; }

        /// <summary>
        /// 把当前 Presentation 可见范围内的 Resident Chunk Key 复制到调用方复用的数组。
        /// 返回实际写入数量；不会分配新数组，也不会因为读取而创建 Chunk。
        /// </summary>
        int CopyVisibleChunkKeys(ElementChunkKey[] destination);

        bool TryGetCell(Vector3Int globalCell, out ElementCell cell);
        bool IsSolid(Vector3Int globalCell);
        uint GetChunkVersion(ElementChunkKey key);
    }

    /// <summary>
    /// 可选的批量只读能力：把一个 Chunk 的 Current Cell 值复制到消费者复用的数组。
    /// 顺序为 (z*size+y)*size+x；未知/未初始化 Chunk 返回0，不创建世界数据。
    /// 不返回内部数组或 ElementWorldChunk，因此 Rendering 仍然没有 Gameplay 写权限。
    /// </summary>
    public interface IElementWorldChunkReadOnly
    {
        int CopyChunkCells(ElementChunkKey key, ElementCell[] destination);
    }
}
