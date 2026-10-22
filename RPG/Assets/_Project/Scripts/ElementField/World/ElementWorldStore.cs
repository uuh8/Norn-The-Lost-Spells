using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 连续关卡的稀疏 Chunk 仓库。未被法术、烘焙或模拟触及的世界空间不分配 Cell 数组；
    /// 已经存在且含有元素的 Chunk 即使离开玩家附近，也会保留数据供后续重新激活。
    /// </summary>
    public sealed class ElementWorldStore
    {
        private readonly Dictionary<ElementChunkKey, ElementWorldChunk> _chunks;
        private readonly int _cellCountPerChunk;
        private readonly Action<ElementWorldChunk> _chunkInitializer;

        public ElementWorldStore(
            int chunkSize,
            int maximumResidentChunks,
            Action<ElementWorldChunk> chunkInitializer = null)
        {
            if (chunkSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkSize),
                    chunkSize,
                    "Chunk size must be greater than zero.");
            }

            if (maximumResidentChunks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumResidentChunks),
                    maximumResidentChunks,
                    "Maximum resident chunk count must be greater than zero.");
            }

            try
            {
                _cellCountPerChunk = checked(chunkSize * chunkSize * chunkSize);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkSize),
                    chunkSize,
                    "Chunk size produces more cells than an Int32 array can address.");
            }

            ChunkSize = chunkSize;
            MaximumResidentChunks = maximumResidentChunks;
            _chunkInitializer = chunkInitializer;
            _chunks = new Dictionary<ElementChunkKey, ElementWorldChunk>(maximumResidentChunks);
        }

        public int ChunkSize { get; }
        public int MaximumResidentChunks { get; }
        public int ResidentChunkCount => _chunks.Count;

        // 只在 Game.ElementField 内部提供具体 Dictionary，以便 Runtime 使用其 struct Enumerator 做零分配遍历。
        // 上层 Rendering 不会获得这个写入口；公开消费者仍应使用后续只读 World 接口。
        internal Dictionary<ElementChunkKey, ElementWorldChunk> Chunks => _chunks;

        public bool TryGetChunk(ElementChunkKey key, out ElementWorldChunk chunk)
        {
            return _chunks.TryGetValue(key, out chunk);
        }

        public bool TryGetOrCreateChunk(ElementChunkKey key, out ElementWorldChunk chunk)
        {
            if (_chunks.TryGetValue(key, out chunk))
            {
                return true;
            }

            // 上限是故障保护：Demo 中若坐标换算或写入逻辑失控，不允许它静默吃光内存。
            if (_chunks.Count >= MaximumResidentChunks)
            {
                chunk = null;
                return false;
            }

            chunk = new ElementWorldChunk(key, ChunkSize, _cellCountPerChunk);

            // 初始化器必须在 Add/return 之前执行：同一 Tick 中，WriteProcessor 会在拿到 Chunk 后立刻
            // 检查 SolidMask。若先返回再 Bake，Deposit 可能先写进 Collider，水也会穿过尚未初始化的地面。
            _chunkInitializer?.Invoke(chunk);
            _chunks.Add(key, chunk);
            return true;
        }

        public ElementCell GetCellOrEmpty(Vector3Int globalCell)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell,
                ChunkSize,
                out ElementChunkKey key,
                out Vector3Int localCell);

            // Read 不隐式创建 Chunk：查询一片从未出现过元素的远方空间必须是零内存副作用。
            return _chunks.TryGetValue(key, out ElementWorldChunk chunk)
                ? chunk.GetCell(localCell)
                : default;
        }

        public bool TryRemoveEmptyChunk(ElementChunkKey key)
        {
            if (!_chunks.TryGetValue(key, out ElementWorldChunk chunk) || chunk.HasAnyElement)
            {
                return false;
            }

            return _chunks.Remove(key);
        }
    }
}
