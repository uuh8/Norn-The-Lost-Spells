using System;
using Game.Combat;

namespace Game.ElementField
{
    /// <summary>
    /// 一次 ElementField Runtime 初始化时生成的只读调参快照。
    /// Simulator 不在 Tick 内回查可变 ScriptableObject，保证同一轮模拟规则稳定且便于测试。
    /// </summary>
    public readonly struct ElementFieldSimulationSettings
    {
        public readonly float CellSize;
        public readonly byte MaxDownFlowPerTick;
        public readonly byte MaxLateralFlowPerTick;
        public readonly byte FireDecayPerTick;
        public readonly byte MaxFireDownFlowPerTick;
        public readonly int ChunkSize;
        public readonly bool SimulateCellWater;
        public readonly ExtinguishTuning Extinguish;

        public ElementFieldSimulationSettings(
            float cellSize,
            byte maxDownFlowPerTick,
            byte maxLateralFlowPerTick,
            byte fireDecayPerTick,
            int chunkSize,
            ExtinguishTuning extinguish)
            : this(
                cellSize,
                maxDownFlowPerTick,
                maxLateralFlowPerTick,
                fireDecayPerTick,
                chunkSize,
                true,
                extinguish)
        {
        }

        public ElementFieldSimulationSettings(
            float cellSize,
            byte maxDownFlowPerTick,
            byte maxLateralFlowPerTick,
            byte fireDecayPerTick,
            int chunkSize,
            bool simulateCellWater,
            ExtinguishTuning extinguish,
            byte maxFireDownFlowPerTick=0)
        {
            if (cellSize <= 0f || float.IsNaN(cellSize) || float.IsInfinity(cellSize))
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));

            CellSize = cellSize;
            MaxDownFlowPerTick = maxDownFlowPerTick;
            MaxLateralFlowPerTick = maxLateralFlowPerTick;
            FireDecayPerTick = fireDecayPerTick;
            MaxFireDownFlowPerTick=maxFireDownFlowPerTick;
            ChunkSize = chunkSize;
            SimulateCellWater = simulateCellWater;
            Extinguish = extinguish;
        }
    }
}
