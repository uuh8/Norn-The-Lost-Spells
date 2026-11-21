using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 一个世界 Chunk 的常驻数据容器。内部复用 P6-A 的 ElementGrid，从而直接获得预分配的
    /// Current/Next Double Buffer、Solid Mask、Amount Delta 与脏版本基础设施。
    /// </summary>
    public sealed class ElementWorldChunk
    {
        private readonly ElementGrid _grid;
        private readonly ElementWorldStore _store;
        private readonly uint[] _fireWords;
        private ulong _reportedBatch;
        private int _nonEmptyCellCount;
        private int _fireCellCount;
        internal bool HasFire => _fireCellCount > 0;
        private int _unchangedSimulationTicks;
        private bool _requiresSimulation = true;

        internal ElementWorldChunk(ElementChunkKey key, int chunkSize, int cellCount, ElementWorldStore store = null)
        {
            Key = key;
            ChunkSize = chunkSize;
            _store = store;
            _fireWords = new uint[(cellCount - 1) / 32 + 1];
            _grid = new ElementGrid(
                new Vector3Int(chunkSize, chunkSize, chunkSize),
                cellCount,
                chunkSize, store?.CellularSolidAt == null);
        }

        public ElementChunkKey Key { get; }
        public int ChunkSize { get; }
        public int CellCount => _grid.CellCount;
        public bool HasAnyElement => _nonEmptyCellCount > 0;
        internal int NonEmptyCellCount => _nonEmptyCellCount;
        internal int UnchangedSimulationTicks => _unchangedSimulationTicks;
        internal bool RequiresSimulation => _requiresSimulation;
        public ElementChunkActivityState ActivityState { get; internal set; }
        public long LastRelevantTick { get; private set; }

        internal static int CompareKeys(ElementChunkKey a, ElementChunkKey b)
        {
            int z = a.Z.CompareTo(b.Z);
            if (z != 0) return z;
            int y = a.Y.CompareTo(b.Y);
            return y != 0 ? y : a.X.CompareTo(b.X);
        }

        // 反应采样与下落共用 Z/Y/X 的确定顺序。原地 Heap Sort 只操作有效前缀，
        // 逆序铺开的长火线也保持 O(n log n)，每步无需临时数组或比较委托。
        internal static void SortByKey(ElementWorldChunk[] values, int count)
        {
            if (count < 2) return;
            for (int root = count / 2 - 1; root >= 0; root--) SiftDown(values, root, count);
            for (int end = count - 1; end > 0; end--)
            {
                var largest = values[0];
                values[0] = values[end];
                values[end] = largest;
                SiftDown(values, 0, end);
            }
        }

        private static void SiftDown(ElementWorldChunk[] values, int root, int count)
        {
            var value = values[root];
            while (root < count / 2)
            {
                int child = root * 2 + 1;
                if (child + 1 < count && CompareKeys(values[child + 1].Key, values[child].Key) > 0) child++;
                if (CompareKeys(value.Key, values[child].Key) >= 0) break;
                values[root] = values[child];
                root = child;
            }
            values[root] = value;
        }

        // 这些 Buffer 只向本程序集和测试程序集开放，外部模块不能绕过 ElementField 的写入规则。
        internal ElementCell[] CurrentCells => _grid.CurrentCells;
        internal ElementCell[] NextCells => _grid.NextCells;
        internal bool[] SolidMask => _grid.SolidMask;
        internal int[] AmountDelta => _grid.AmountDelta;
        internal ElementGrid Grid => _grid;

        // 元胞世界已持有真实地形、静止固体和环境缓存。旧火通道只查实际写入/接收的位置，
        // 避免一个首次飞入空中的火点触发整块 Physics 烘焙。未迁移的世界沿用原 SolidMask。
        internal bool BlocksMaterial(int index, Vector3Int local)
        {
            var lookup = _store?.CellularSolidAt;
            return lookup != null ? lookup(ElementWorldCoordinates.ComposeGlobalCell(Key, local, ChunkSize)) : SolidMask[index];
        }

        internal ElementCell GetCell(Vector3Int localCell)
        {
            return _grid.GetCell(localCell);
        }

        internal void SetCell(Vector3Int localCell, ElementCell cell)
        {
            int index = ElementFieldCoordinates.ToIndex(localCell, _grid.Dimensions);
            WriteCell(index, cell);
        }

        internal void WriteCell(int index, ElementCell cell)
        {
            ElementCell before = CurrentCells[index];
            if (before.MaterialKind == cell.MaterialKind && before.Amount == cell.Amount)
                return;

            bool wasEmpty = before.IsEmpty;
            bool isEmpty = cell.IsEmpty;
            bool wasFire = !wasEmpty && before.MaterialKind == Game.Materials.MaterialId.Fire;
            bool isFire = !isEmpty && cell.MaterialKind == Game.Materials.MaterialId.Fire;
            if (wasFire != isFire)
            {
                uint bit = 1u << (index & 31);
                if (isFire) { _fireWords[index >> 5] |= bit; _fireCellCount++; }
                else { _fireWords[index >> 5] &= ~bit; _fireCellCount--; }
            }

            if (wasEmpty && !isEmpty)
            {
                _nonEmptyCellCount++;
            }
            else if (!wasEmpty && isEmpty)
            {
                _nonEmptyCellCount--;
            }

            // 将“有种类但 Amount=0”一类中间无效值规范成真正的 Empty，避免休眠回收判断被脏数据干扰。
            CurrentCells[index] = isEmpty ? default : cell;
            NotifyMaterialChanged();

            // SetCell 是离散写入口；任何真实数据变化都必须撤销此前的“稳定”判断。
            // LastRelevantTick 仍由拥有世界 Tick 的调用方更新，避免数据容器自行猜测时间。
            _requiresSimulation = true;
            _unchangedSimulationTicks = 0;
        }

        // 有序位图与线性 Current 的地址相同。空的 32 格只检查一个字，非火 Chunk 直接返回。
        // 返回下一个置位下标，调用方可以在遍历期间安全清掉当前位或之前的位。
        internal int NextFireIndex(int first)
        {
            if (!HasFire || (uint)first >= (uint)CellCount) return -1;
            int word = first >> 5;
            uint bits = _fireWords[word] & (uint.MaxValue << (first & 31));
            while (bits == 0)
            {
                if (++word == _fireWords.Length) return -1;
                bits = _fireWords[word];
            }
            int offset = 0;
            if ((bits & 0xffffu) == 0) { bits >>= 16; offset += 16; }
            if ((bits & 0xffu) == 0) { bits >>= 8; offset += 8; }
            if ((bits & 0xfu) == 0) { bits >>= 4; offset += 4; }
            if ((bits & 3u) == 0) { bits >>= 2; offset += 2; }
            return (word << 5) + offset + ((bits & 1u) == 0 ? 1 : 0);
        }

        internal void NotifyMaterialChanged()
        {
            // 多个火格或一次直接写与后续衰减，只在同一捕获区间登记一次 Chunk。
            if (_store == null || _reportedBatch == _store.ChangeBatch) return;
            _reportedBatch = _store.ChangeBatch;
            _store.ReportMaterialChange(Key);
        }

        internal void MarkRelevant(long worldTick)
        {
            if (worldTick < 0)
                throw new System.ArgumentOutOfRangeException(nameof(worldTick));

            // Tick 只允许单调前进；重复标记同一 Tick 合法，倒退则说明 Runtime 时间源发生错误。
            if (worldTick < LastRelevantTick)
                throw new System.ArgumentOutOfRangeException(nameof(worldTick));

            LastRelevantTick = worldTick;
        }

        internal void WakeForSimulation(long worldTick)
        {
            MarkRelevant(worldTick);
            _requiresSimulation = true;
            _unchangedSimulationTicks = 0;
        }

        internal void RecordSimulationResult(int changedCellCount, int settleAfterUnchangedTicks)
        {
            if (changedCellCount < 0)
                throw new System.ArgumentOutOfRangeException(nameof(changedCellCount));
            if (settleAfterUnchangedTicks <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(settleAfterUnchangedTicks));

            if (changedCellCount > 0)
            {
                _requiresSimulation = true;
                _unchangedSimulationTicks = 0;
                return;
            }

            // 使用连续多 Tick 的 Hysteresis，而不是一次无变化就休眠，避免整数扩散公式在临界点
            // 或交替更新顺序下短暂出现 0 Change 时把仍会继续流动的 Chunk 过早冻结。
            if (_unchangedSimulationTicks < settleAfterUnchangedTicks)
                _unchangedSimulationTicks++;
            if (_unchangedSimulationTicks >= settleAfterUnchangedTicks)
                _requiresSimulation = false;
        }

        internal void RefreshNonEmptyCellCount()
        {
            // Snapshot/Delta Stage 会整批交换 ElementGrid 的 Current/Next 数组，无法逐次经过 SetCell。
            // 每个完整 Tick 结束后统一重算一次，保证 Sleep/Reclaim 判定看到的 HasAnyElement 不会过期。
            int count = 0;
            _fireCellCount = 0;
            System.Array.Clear(_fireWords, 0, _fireWords.Length);
            ElementCell[] cells = _grid.CurrentCells;
            for (int index = 0; index < cells.Length; index++)
            {
                if (!cells[index].IsEmpty)
                    count++;
                if (!cells[index].IsEmpty && cells[index].MaterialKind == Game.Materials.MaterialId.Fire)
                {
                    _fireWords[index >> 5] |= 1u << (index & 31);
                    _fireCellCount++;
                }
            }

            _nonEmptyCellCount = count;
        }
    }
}
