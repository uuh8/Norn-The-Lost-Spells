using Game.Materials;
using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 有限元素场的纯数据所有者。构造时一次性分配所有数组，后续 Simulation Tick 只复用这些内存，
    /// 从根源上避免每 Tick new 数组、List 扩容或三维对象图造成的 GC Alloc 与 Cache Miss。
    /// </summary>
    public sealed class ElementGrid
    {
        private ElementCell[] _currentCells;
        private ElementCell[] _nextCells;
        private readonly bool[] _solidMask;
        private readonly int[] _amountDelta;
        private readonly bool[] _cellDirty;
        private readonly bool[] _chunkDirty;
        private readonly uint[] _chunkVersions;

        public ElementGrid(Vector3Int dimensions, int maximumCellCount)
            : this(dimensions, maximumCellCount, chunkSize: 1)
        {
        }

        public ElementGrid(Vector3Int dimensions, int maximumCellCount, int chunkSize)
        {
            if (maximumCellCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumCellCount),
                    maximumCellCount,
                    "Maximum cell count must be greater than zero.");
            }

            if (chunkSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkSize),
                    chunkSize,
                    "Chunk size must be greater than zero.");
            }

            int cellCount = ElementFieldCoordinates.GetCellCount(dimensions);
            if (cellCount > maximumCellCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dimensions),
                    dimensions,
                    $"Grid requires {cellCount} cells, exceeding the configured maximum {maximumCellCount}.");
            }

            Dimensions = dimensions;
            CellCount = cellCount;
            ChunkSize = chunkSize;
            ChunkCounts = new Vector3Int(
                CeilingDivide(dimensions.x, chunkSize),
                CeilingDivide(dimensions.y, chunkSize),
                CeilingDivide(dimensions.z, chunkSize));
            int chunkCount = ElementFieldCoordinates.GetCellCount(ChunkCounts);

            // 四个数组只在初始化时分配。Current/Next 支持后续 Double Buffer；Solid 与元素含量分离，
            // 因为“这里有墙”是空间属性，不应该通过伪造一种 MaterialKind 来表达。
            _currentCells = new ElementCell[cellCount];
            _nextCells = new ElementCell[cellCount];
            _solidMask = new bool[cellCount];
            _amountDelta = new int[cellCount];
            _cellDirty = new bool[cellCount];
            _chunkDirty = new bool[chunkCount];
            _chunkVersions = new uint[chunkCount];
        }

        public Vector3Int Dimensions { get; }
        public int CellCount { get; }
        public int ChunkSize { get; }
        public Vector3Int ChunkCounts { get; }

        // 写权限只在 Game.ElementField 程序集内部开放；外部模块不能取得数组后绕开 Simulation 改数据。
        internal ElementCell[] CurrentCells => _currentCells;
        internal ElementCell[] NextCells => _nextCells;
        internal bool[] SolidMask => _solidMask;
        internal int[] AmountDelta => _amountDelta;
        internal uint[] ChunkVersions => _chunkVersions;

        public ElementCell GetCell(Vector3Int coordinate)
        {
            return _currentCells[ElementFieldCoordinates.ToIndex(coordinate, Dimensions)];
        }

        public ElementCell GetCell(int x, int y, int z)
        {
            return GetCell(new Vector3Int(x, y, z));
        }

        public bool IsSolid(Vector3Int coordinate)
        {
            return _solidMask[ElementFieldCoordinates.ToIndex(coordinate, Dimensions)];
        }

        public bool IsSolid(int x, int y, int z)
        {
            return IsSolid(new Vector3Int(x, y, z));
        }

        public uint GetChunkVersion(int chunkX, int chunkY, int chunkZ)
        {
            int index = ElementFieldCoordinates.ToIndex(
                new Vector3Int(chunkX, chunkY, chunkZ),
                ChunkCounts);
            return _chunkVersions[index];
        }

        internal void SetCell(Vector3Int coordinate, ElementCell cell)
        {
            _currentCells[ElementFieldCoordinates.ToIndex(coordinate, Dimensions)] = cell;
        }

        internal void SetSolid(Vector3Int coordinate, bool isSolid)
        {
            _solidMask[ElementFieldCoordinates.ToIndex(coordinate, Dimensions)] = isSolid;
        }

        internal void ClearSolidMask()
        {
            // Solid Bake 是低频初始化操作；复用既有数组，避免重复 Rebuild 时替换 Grid 或制造悬空只读引用。
            Array.Clear(_solidMask, 0, _solidMask.Length);
        }

        internal int ClearElementsAndCommitVersions()
        {
            int changed = 0;
            for (int index = 0; index < CellCount; index++)
            {
                if (_currentCells[index].MaterialKind != MaterialId.Empty
                    || _currentCells[index].Amount != 0)
                {
                    _currentCells[index] = default;
                    MarkCellDirty(index);
                    changed++;
                }

                // Next Buffer 可能仍保存上一 Stage 的旧快照；Debug Clear 必须同时清空，
                // 否则下一次 Swap 可能把已删除的元素重新带回 Current。
                _nextCells[index] = default;
                _amountDelta[index] = 0;
            }

            CommitDirtyVersions();
            return changed;
        }

        internal void PrepareNextFromCurrent()
        {
            // Array.Copy 复用已分配的 Double Buffer；它不会产生 GC Alloc。
            Array.Copy(_currentCells, _nextCells, CellCount);
        }

        internal void ClearAmountDelta()
        {
            Array.Clear(_amountDelta, 0, _amountDelta.Length);
        }

        internal int CommitPreparedStage()
        {
            int changed = 0;
            for (int index = 0; index < CellCount; index++)
            {
                ElementCell before = _currentCells[index];
                ElementCell after = _nextCells[index];
                if (before.MaterialKind == after.MaterialKind && before.Amount == after.Amount)
                    continue;

                MarkCellDirty(index);
                changed++;
            }

            // 交换引用是 O(1)，避免把 Next 再整体复制回 Current。
            ElementCell[] previousCurrent = _currentCells;
            _currentCells = _nextCells;
            _nextCells = previousCurrent;
            return changed;
        }

        internal void MarkCellDirty(Vector3Int coordinate)
        {
            MarkCellDirty(ElementFieldCoordinates.ToIndex(coordinate, Dimensions));
        }

        internal int CommitDirtyVersions()
        {
            int changedCells = 0;
            for (int index = 0; index < _cellDirty.Length; index++)
            {
                if (!_cellDirty[index])
                    continue;

                _cellDirty[index] = false;
                changedCells++;
            }

            // 一个 Chunk 在同一 Tick 内可能有很多 Cell 变化，但 Version 只增加一次。
            // Renderer 只比较“是否不同”，不需要知道 Chunk 内具体变化了多少次。
            for (int index = 0; index < _chunkDirty.Length; index++)
            {
                if (!_chunkDirty[index])
                    continue;

                _chunkDirty[index] = false;
                _chunkVersions[index]++;
            }

            return changedCells;
        }

        private void MarkCellDirty(int cellIndex)
        {
            _cellDirty[cellIndex] = true;
            Vector3Int cell = ElementFieldCoordinates.FromIndex(cellIndex, Dimensions);
            Vector3Int chunk = new Vector3Int(
                cell.x / ChunkSize,
                cell.y / ChunkSize,
                cell.z / ChunkSize);
            int chunkIndex = ElementFieldCoordinates.ToIndex(chunk, ChunkCounts);
            _chunkDirty[chunkIndex] = true;
        }

        private static int CeilingDivide(int value, int divisor)
        {
            // (value - 1) / divisor + 1 避免 value + divisor - 1 的 int overflow。
            return (value - 1) / divisor + 1;
        }
    }
}
