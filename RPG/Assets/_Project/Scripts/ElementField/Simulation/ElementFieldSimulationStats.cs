using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 单个 Tick 的纯数值统计。使用 struct 和整数，不创建字符串，
    /// 便于 Runtime、Profiler 或 Debug UI 在需要时读取。
    /// </summary>
    [Serializable]
    public struct ElementFieldSimulationStats
    {
        [SerializeField] private int _processedWrites;
        [SerializeField] private int _rejectedWrites;
        [SerializeField] private int _changedCells;
        [SerializeField] private int _reactionPairs;
        [SerializeField] private int _waterTransfers;
        [SerializeField] private int _fireCellsDecayed;

        public int ProcessedWrites { get => _processedWrites; internal set => _processedWrites = value; }
        public int RejectedWrites { get => _rejectedWrites; internal set => _rejectedWrites = value; }
        public int ChangedCells { get => _changedCells; internal set => _changedCells = value; }
        public int ReactionPairs { get => _reactionPairs; internal set => _reactionPairs = value; }
        public int WaterTransfers { get => _waterTransfers; internal set => _waterTransfers = value; }
        public int FireCellsDecayed { get => _fireCellsDecayed; internal set => _fireCellsDecayed = value; }
    }
}
