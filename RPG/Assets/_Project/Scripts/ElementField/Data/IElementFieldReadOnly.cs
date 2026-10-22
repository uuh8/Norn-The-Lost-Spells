using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// Rendering 等消费者可见的只读窗口。接口提供数据与 Dirty Version，但不提供写入或“清除 Dirty”操作，
    /// 因此多个 Renderer 可以独立比较自己的 lastSeenVersion，不会互相抢占一次性事件。
    /// </summary>
    public interface IElementFieldReadOnly
    {
        bool IsInitialized { get; }
        Vector3 Origin { get; }
        Vector3Int Dimensions { get; }
        float CellSize { get; }
        int ChunkSize { get; }
        Vector3Int ChunkCounts { get; }
        ElementCell GetCell(int x, int y, int z);
        bool IsSolid(int x, int y, int z);
        uint GetChunkVersion(int chunkX, int chunkY, int chunkZ);
    }
}
