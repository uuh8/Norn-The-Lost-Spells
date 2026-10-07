using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public readonly struct LiquidCellPresentationSample
    {
        public readonly uint Quantity;
        public readonly Vector3 OffsetCells, VelocityWorld;
        public LiquidCellPresentationSample(uint quantity, Vector3 offset, Vector3 velocity)
        { Quantity = quantity; OffsetCells = offset; VelocityWorld = velocity; }
    }
    public interface ILiquidTransportReadOnly
    {
        uint TransportEpoch { get; }
        float TransportInterval { get; }
        bool TryGetPresentationSample(Vector3Int cell, MaterialId material, bool previous, out LiquidCellPresentationSample sample);
        int CopyTransportChangedCells(Vector3Int[] destination);
        float GetGameplayBaseHeight(Vector3Int cell);
        bool TryGetTerrainSupport(Vector3Int cell, out LiquidTerrainSupport support);
    }

    /// <summary>
    /// 在本Tick第一次改写一个格前冻结旧数量/姿态；记录范围只含活动改写，不扫描Resident湖底。
    /// Current按已提交源读取，稳定Patch由Rendering快照负责，不能把Stable GMU复制成活动粒子。
    /// </summary>
    public sealed class LiquidTransportHistory
    {
        private readonly Dictionary<Vector3Int, int> _indices;
        private readonly Vector3Int[] _cells;
        private readonly LiquidCellPresentationSample[] _previous;
        private readonly LiquidWorldRuntime _source;
        private int _count;
        public uint Epoch { get; private set; }
        public uint OverflowCount { get; private set; }
        public LiquidTransportHistory(LiquidWorldRuntime source, int capacity, int materialCount)
        { _source = source; _indices = new Dictionary<Vector3Int, int>(capacity); _cells = new Vector3Int[capacity]; _previous = new LiquidCellPresentationSample[capacity * materialCount]; }
        public void BeginTick() { _indices.Clear(); _count = 0; Epoch++; }
        public void CaptureBefore(Vector3Int cell)
        {
            if (_indices.ContainsKey(cell)) return;
            if (_count == _cells.Length) { OverflowCount++; return; }
            int index = _count++; _indices.Add(cell, index); _cells[index] = cell;
            for (int m = 0; m < _source.MaterialCount; m++)
                _previous[index * _source.MaterialCount + m] = _source.ReadCurrentPresentation(cell, _source.GetLiquidMaterial(m));
        }
        public LiquidCellPresentationSample ReadPrevious(Vector3Int cell, MaterialId material)
        {
            if (_indices.TryGetValue(cell, out int index))
                for (int m = 0; m < _source.MaterialCount; m++)
                    if (_source.GetLiquidMaterial(m) == material) return _previous[index * _source.MaterialCount + m];
            return _source.ReadCurrentPresentation(cell, material);
        }
        public bool CopyFrozenRow(Vector3Int cell,LiquidCellPresentationSample[] destination)
        {
            if(!_indices.TryGetValue(cell,out int index))return false;
            System.Array.Copy(_previous,index*_source.MaterialCount,destination,0,_source.MaterialCount);return true;
        }
        public int CopyChangedCells(Vector3Int[] destination)
        { int n = Mathf.Min(_count, destination.Length); System.Array.Copy(_cells, destination, n); return n; }
    }
}
