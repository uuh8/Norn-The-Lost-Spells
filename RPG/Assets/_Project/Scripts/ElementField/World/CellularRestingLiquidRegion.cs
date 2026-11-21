using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>基础格的半开盒 [min,max)。作者配置的盒初始满量；运行时冷区实际占据由 VisitOccupiedBoxes 提供。</summary>
    [Serializable]
    public struct CellularRestingLiquidRegion
    {
        public BoundsInt Cells;
        public MaterialId Material;
    }

    public interface ICellularRestingLiquidReadOnly : IMaterialQuantityReadOnly
    {
        uint CellCapacity { get; }
        ulong GetTotalQuantity(MaterialId material);
        ulong Version { get; }
        ulong RegionLayoutVersion { get; }
        // 区域槽数只增不减。作者索引固定，冷区槽清空后为默认值、之后可复用；表现按布局游标处理旧盒与新盒。
        int RegionCount { get; }
        CellularRestingLiquidRegion GetRegion(int index);
        int FindRegionIndex(Vector3Int cell);
        ulong GetRegionVersion(int regionIndex);
        void VisitTouchingRegions(BoundsInt cells, Action<int> visitor);
        bool VisitLayoutChanges(ulong since, Action<int, BoundsInt, BoundsInt> visitor);
        bool TryGetCell(Vector3Int cell, out MaterialId material, out uint quantity);
        void VisitOccupiedBoxes(int regionIndex, Action<BoundsInt, uint> visitor);
    }

    /// <summary>
    /// 元胞池外的静水数量所有者。均匀长方体只有一个树节点。局部改动沿最长轴二分到目标格，
    /// 成片恢复满量或清空时折叠子树。只读几何可枚举剩余满盒和部分量格，无须遍历初始体积。
    /// </summary>
    public sealed partial class CellularRestingLiquidStore : ICellularRestingLiquidReadOnly
    {
        private struct Node
        {
            public uint Amount;
            public int Low, High;
        }

        private readonly CellularRestingLiquidRegion[] _regions;
        private readonly int[] _roots;
        private readonly ulong[] _regionVersions;
        private readonly List<Node> _nodes = new List<Node> { default };
        private int _freeNode;
        private readonly ulong[] _totals = new ulong[256];
        private readonly Queue<Vector3Int> _changed;
        private readonly HashSet<Vector3Int> _changedSet;
        private readonly int _changeLimit;

        public CellularRestingLiquidStore(CellularRestingLiquidRegion[] regions, uint capacityPerCell, int workCapacity)
        {
            if (capacityPerCell == 0 || capacityPerCell > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(capacityPerCell));
            if (workCapacity < 1 || workCapacity > int.MaxValue / 2) throw new ArgumentOutOfRangeException(nameof(workCapacity));
            CellCapacity = capacityPerCell;
            _regions = regions == null ? Array.Empty<CellularRestingLiquidRegion>() : (CellularRestingLiquidRegion[])regions.Clone();
            _roots = new int[_regions.Length];
            _regionVersions = new ulong[_regions.Length];
            _workLimit = workCapacity;
            _work = new Queue<Vector3Int>(workCapacity);
            _queued = new HashSet<Vector3Int>(workCapacity);
            _deferred = new Vector3Int[workCapacity];
            _deferredSet = new HashSet<Vector3Int>(workCapacity);
            _border = new Queue<FaceCursor>(checked(_regions.Length * 6));
            _changeLimit = Math.Max(1024, workCapacity * 2);
            _layoutJournal = new LayoutChange[_changeLimit];
            _changed = new Queue<Vector3Int>(_changeLimit);
            _changedSet = new HashSet<Vector3Int>(_changeLimit);
            for (int i = 0; i < _regions.Length; i++)
            {
                var region = _regions[i];
                var box = region.Cells;
                var size = box.size;
                if (ElementCellMaterialRules.Motion(region.Material) != ElementCellMotion.Liquid ||
                    size.x <= 0 || size.y <= 0 || size.z <= 0 ||
                    (long)box.position.x + size.x > int.MaxValue || (long)box.position.y + size.y > int.MaxValue ||
                    (long)box.position.z + size.z > int.MaxValue) throw new ArgumentException("静水区需要有效液体及可寻址的正体积。", nameof(regions));
                _totals[(byte)region.Material] = checked(_totals[(byte)region.Material] + (ulong)size.x * (uint)size.y * (uint)size.z * CellCapacity);
                _roots[i] = NewNode(CellCapacity);
            }
            _index = new SpatialNode[checked(Math.Max(0, _regions.Length * 2 - 1))];
            if (_regions.Length != 0) BuildRegionIndex();
            _scanning = _regions.Length != 0;
        }

        public uint CellCapacity { get; }
        public ulong Version { get; private set; }
        public int RegionCount => _regions.Length + _cold.Count;
        public CellularRestingLiquidRegion GetRegion(int index) => index < _regions.Length ? _regions[index] : _cold[index - _regions.Length].Definition;
        private int RootOf(int index) => index < _regions.Length ? _roots[index] : _cold[index - _regions.Length].Root;
        public int FindRegionIndex(Vector3Int cell) => FindRegion(cell);
        public ulong GetRegionVersion(int regionIndex) => regionIndex < _regions.Length ? _regionVersions[regionIndex] : _cold[regionIndex - _regions.Length].Version;
        public ulong GetTotalQuantity(MaterialId material) => _totals[(byte)material];
        public bool TryGetCell(Vector3Int cell, out MaterialId material, out uint quantity)
        {
            int index = FindRegion(cell);
            quantity = index < 0 ? 0 : Read(index, cell);
            material = quantity == 0 ? MaterialId.Empty : GetRegion(index).Material;
            return quantity != 0;
        }

        public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity)
        {
            if (TryGetCell(cell, out MaterialId found, out quantity) && found == material) return true;
            quantity = 0;
            return false;
        }

        public void VisitOccupiedBoxes(int regionIndex, Action<BoundsInt, uint> visitor)
        {
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));
            int root = RootOf(regionIndex);
            if (root != 0) Visit(root, GetRegion(regionIndex).Cells, visitor);
        }

        private void Visit(int index, BoundsInt box, Action<BoundsInt, uint> visitor)
        {
            Node node = _nodes[index];
            if (node.Low == 0)
            {
                if (node.Amount != 0) visitor(box, node.Amount);
                return;
            }
            Split(box, out BoundsInt left, out BoundsInt right);
            Visit(node.Low, left, visitor);
            Visit(node.High, right, visitor);
        }

        private uint Read(int region, Vector3Int cell)
        {
            var box = GetRegion(region).Cells;
            int i = RootOf(region);
            if (i == 0) return 0;
            Node node = _nodes[i];
            while (node.Low != 0)
            {
                Split(box, out BoundsInt left, out BoundsInt right);
                bool low = left.Contains(cell);
                box = low ? left : right;
                i = low ? node.Low : node.High;
                node = _nodes[i];
            }
            return node.Amount;
        }

        private static void Split(BoundsInt box, out BoundsInt left, out BoundsInt right)
        {
            var size = box.size;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            int half = size[axis] / 2;
            var otherSize = size;
            var otherMin = box.position;
            size[axis] = half;
            otherSize[axis] -= half;
            otherMin[axis] += half;
            left = new BoundsInt(box.position, size);
            right = new BoundsInt(otherMin, otherSize);
        }

        private int NewNode(uint amount)
        {
            var value = new Node { Amount = amount };
            if (_freeNode == 0) { _nodes.Add(value); return _nodes.Count - 1; }
            int index = _freeNode;
            _freeNode = _nodes[index].Low;
            _nodes[index] = value;
            return index;
        }

        private void FreeNode(int index)
        {
            _nodes[index] = new Node { Low = _freeNode };
            _freeNode = index;
        }

        private void Write(int index, BoundsInt box, Vector3Int cell, uint quantity)
        {
            Node node = _nodes[index];
            if (box.size == Vector3Int.one) { _nodes[index] = new Node { Amount = quantity }; return; }
            if (node.Low == 0)
            {
                if (node.Amount == quantity) return;
                node.Low = NewNode(node.Amount); node.High = NewNode(node.Amount);
                _nodes[index] = node;
            }
            Split(box, out BoundsInt left, out BoundsInt right);
            if (left.Contains(cell)) Write(node.Low, left, cell, quantity);
            else Write(node.High, right, cell, quantity);
            Node a = _nodes[node.Low], b = _nodes[node.High];
            // 部分量格各自有液面高度；即使上下相邻且数量相等，渲染也不能简化成一个实心高柱。
            if (a.Low == 0 && b.Low == 0 && a.Amount == b.Amount && (a.Amount == 0 || a.Amount == CellCapacity))
            {
                FreeNode(node.Low); FreeNode(node.High);
                _nodes[index] = new Node { Amount = a.Amount };
            }
        }

        private bool CanRecord(Vector3Int cell) => _changed.Count < _changeLimit || _changedSet.Contains(cell);

        private void Set(int region, Vector3Int cell, uint before, uint after)
        {
            if (before == after) return;
            var definition = GetRegion(region);
            Write(RootOf(region), definition.Cells, cell, after);
            int material = (byte)definition.Material;
            _totals[material] = _totals[material] - before + after;
            bool authored = region < _regions.Length;
            TrackEdit(cell, authored ? after != CellCapacity : after != 0);
            Version++;
            if (authored) _regionVersions[region] = Version;
            else UpdateCold(region, before, after);
            if (_changedSet.Add(cell)) _changed.Enqueue(cell);
            WakeNeighbors(cell);
        }

        public bool TryDequeueChanged(out Vector3Int cell)
        {
            if (_changed.Count == 0) { cell = default; return false; }
            cell = _changed.Dequeue();
            _changedSet.Remove(cell);
            return true;
        }
    }
}
