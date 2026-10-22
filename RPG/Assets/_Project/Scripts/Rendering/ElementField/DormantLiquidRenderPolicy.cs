using System;
using System.Collections.Generic;
using Game.ElementField;
using Game.Materials;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 只读的几何适配器：将本 Renderer 已筛选的一种液体投影为 Surface Nets 的 Water 输入。
    /// 这是临时视觉快照，绝不修改归档中的 MaterialId、Amount 或请求唤醒。
    /// </summary>
    internal sealed class DormantLiquidSurfaceSnapshot : IElementWorldReadOnly
    {
        private readonly IElementWorldReadOnly _terrain;
        private readonly Dictionary<Vector3Int, byte> _cells;
        private readonly ElementFieldSolidBaker _staticTerrain;
        private readonly Dictionary<Vector3Int, bool> _solidCache;
        private const int MaximumCachedSolidCells = 32768;
        internal readonly Dictionary<ElementChunkKey, ulong> ChunkHashes;

        internal DormantLiquidSurfaceSnapshot(IElementWorldReadOnly terrain, int capacity,
            ElementFieldSolidBaker staticTerrain = null)
        {
            _terrain = terrain;
            _staticTerrain = staticTerrain;
            _solidCache = staticTerrain != null
                ? new Dictionary<Vector3Int, bool>(MaximumCachedSolidCells) : null;
            _cells = new Dictionary<Vector3Int, byte>(capacity);
            ChunkHashes = new Dictionary<ElementChunkKey, ulong>(capacity);
        }

        internal void Clear() { _cells.Clear(); ChunkHashes.Clear(); }

        internal void Add(Vector3Int cell, byte amount)
        {
            if (amount == 0) return;
            _cells[cell] = amount;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, ChunkSize, out var key, out _);
            // 归档枚举顺序不代表几何变化。使用与顺序无关的内容摘要，避免每次归档全场重建。
            unchecked
            {
                ulong hash = ((ulong)(uint)cell.GetHashCode() << 8) | amount;
                hash ^= hash >> 30;
                hash *= 0xbf58476d1ce4e5b9UL;
                hash ^= hash >> 27;
                hash *= 0x94d049bb133111ebUL;
                hash ^= hash >> 31;
                ChunkHashes.TryGetValue(key, out ulong previous);
                ChunkHashes[key] = previous + hash;
            }
            // 滤波扩展到相邻 Cell，Primitive 又有空间半径。两格 Halo 的空页也需要
            // 一个几何 owner，否则网格会在 Chunk 边缘被直线截断。只建视觉索引，不造水。
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell - Vector3Int.one * 2,
                ChunkSize, out var minimum, out _);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell + Vector3Int.one * 2,
                ChunkSize, out var maximum, out _);
            for (int z = minimum.Z; z <= maximum.Z; z++)
            for (int y = minimum.Y; y <= maximum.Y; y++)
            for (int x = minimum.X; x <= maximum.X; x++)
            {
                var haloKey = new ElementChunkKey(x, y, z);
                if (!ChunkHashes.ContainsKey(haloKey)) ChunkHashes.Add(haloKey, 0UL);
            }
        }

        internal ulong NeighborhoodHash(ElementChunkKey key)
        {
            // 邻页提供平滑表面的 Halo；邻页增删也必须让接缝两侧一起失效。
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                for (int z = -1; z <= 1; z++)
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    var neighbor = new ElementChunkKey(key.X + x, key.Y + y, key.Z + z);
                    ChunkHashes.TryGetValue(neighbor, out ulong value);
                    hash = (hash ^ value) * 1099511628211UL;
                    // 静态地面由 Collider 快照提供，不再跟着 Gameplay 页的释放改变水体几何。
                    if (_staticTerrain == null)
                        hash = (hash ^ _terrain.GetChunkVersion(neighbor)) * 1099511628211UL;
                }
            }
            return hash;
        }

        public bool IsInitialized => _terrain.IsInitialized;
        public Vector3 Origin => _terrain.Origin;
        public float CellSize => _terrain.CellSize;
        public int ChunkSize => _terrain.ChunkSize;
        public int MaximumResidentChunkCount => _terrain.MaximumResidentChunkCount;
        public bool IsSolid(Vector3Int cell)
        {
            if (_staticTerrain == null) return _terrain.IsSolid(cell);
            if (_solidCache.TryGetValue(cell, out bool solid)) return solid;
            // 地形与现有 Bake 的静态语义一致。固定容量缓存允许跨 Chunk 复用 Halo 的 Physics 查询，
            // 满时清空并复用存储，不让玩家移动导致内存持续增长。
            if (_solidCache.Count == MaximumCachedSolidCells) _solidCache.Clear();
            solid = _staticTerrain.IsStaticSolidCell(cell, Origin, CellSize);
            _solidCache.Add(cell, solid);
            return solid;
        }
        public uint GetChunkVersion(ElementChunkKey key) => _terrain.GetChunkVersion(key);
        public int CopyVisibleChunkKeys(ElementChunkKey[] destination)
        {
            int count = 0;
            foreach (var pair in ChunkHashes)
            {
                if (count >= destination.Length) break;
                destination[count++] = pair.Key;
            }
            return count;
        }
        public bool TryGetCell(Vector3Int cell, out ElementCell value)
        {
            bool found = _cells.TryGetValue(cell, out byte amount);
            value = found ? new ElementCell(MaterialId.Water, amount) : default;
            return found;
        }
    }

    /// <summary>Cell Proxy 的 Pure 几何规则；Amount 只改变填充高度，不改变世界 Cell 占地。</summary>
    public static class DormantLiquidRenderPolicy
    {
        public static float CalculateHeight(byte amount, float cellSize, float minimumHeightRatio)
        {
            if (!(cellSize > 0f) || !float.IsFinite(cellSize))
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (minimumHeightRatio < 0f || minimumHeightRatio > 1f
                || !float.IsFinite(minimumHeightRatio))
                throw new ArgumentOutOfRangeException(nameof(minimumHeightRatio));
            if (amount == 0) return 0f;
            return cellSize * Mathf.Max(minimumHeightRatio, amount / 255f);
        }

        public static Matrix4x4 CreateCellMatrix(
            Vector3Int globalCell,
            Vector3 worldOrigin,
            float cellSize,
            byte amount,
            float minimumHeightRatio)
        {
            float height = CalculateHeight(amount, cellSize, minimumHeightRatio);
            Vector3 minimum = worldOrigin + (Vector3)globalCell * cellSize;
            Vector3 center = minimum + new Vector3(cellSize * .5f, height * .5f, cellSize * .5f);
            // 略微收窄 XZ，避免透明 Cell 共面产生明显 Z-fighting；相邻块仍保持视觉连贯。
            return Matrix4x4.TRS(center, Quaternion.identity,
                new Vector3(cellSize * .96f, height, cellSize * .96f));
        }
    }
}
