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
        private int _nonEmptyCellCount;
        private int _fireCellCount;
        internal bool HasFire => _fireCellCount > 0;
        private int _unchangedSimulationTicks;
        private bool _requiresSimulation = true;

        internal ElementWorldChunk(ElementChunkKey key, int chunkSize, int cellCount)
        {
            Key = key;
            ChunkSize = chunkSize;
            _grid = new ElementGrid(
                new Vector3Int(chunkSize, chunkSize, chunkSize),
                cellCount,
                chunkSize);
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

        // 这些 Buffer 只向本程序集和测试程序集开放，外部模块不能绕过 ElementField 的写入规则。
        internal ElementCell[] CurrentCells => _grid.CurrentCells;
        internal ElementCell[] NextCells => _grid.NextCells;
        internal bool[] SolidMask => _grid.SolidMask;
        internal int[] AmountDelta => _grid.AmountDelta;
        internal ElementGrid Grid => _grid;

        internal ElementCell GetCell(Vector3Int localCell)
        {
            return _grid.GetCell(localCell);
        }

        internal void SetCell(Vector3Int localCell, ElementCell cell)
        {
            ElementCell before = _grid.GetCell(localCell);
            if (before.MaterialKind == cell.MaterialKind && before.Amount == cell.Amount)
                return;

            bool wasEmpty = before.IsEmpty;
            bool isEmpty = cell.IsEmpty;
            if (!wasEmpty && before.MaterialKind == Game.Materials.MaterialId.Fire) _fireCellCount--;
            if (!isEmpty && cell.MaterialKind == Game.Materials.MaterialId.Fire) _fireCellCount++;

            if (wasEmpty && !isEmpty)
            {
                _nonEmptyCellCount++;
            }
            else if (!wasEmpty && isEmpty)
            {
                _nonEmptyCellCount--;
            }

            // 将“有种类但 Amount=0”一类中间无效值规范成真正的 Empty，避免休眠回收判断被脏数据干扰。
            _grid.SetCell(localCell, isEmpty ? default : cell);

            // SetCell 是离散写入口；任何真实数据变化都必须撤销此前的“稳定”判断。
            // LastRelevantTick 仍由拥有世界 Tick 的调用方更新，避免数据容器自行猜测时间。
            _requiresSimulation = true;
            _unchangedSimulationTicks = 0;
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
            ElementCell[] cells = _grid.CurrentCells;
            for (int index = 0; index < cells.Length; index++)
            {
                if (!cells[index].IsEmpty)
                    count++;
                if (!cells[index].IsEmpty && cells[index].MaterialKind == Game.Materials.MaterialId.Fire)
                    _fireCellCount++;
            }

            _nonEmptyCellCount = count;
        }
    }
}
