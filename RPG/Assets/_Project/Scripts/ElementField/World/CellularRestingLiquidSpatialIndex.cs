using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class CellularRestingLiquidStore
    {
        // 这是不可变的区域级 BVH。区外动态材料的每次邻格查询先和根包围盒比较，
        // 不会随远处加入更多静水体而线性扫所有区域，也不用把一个大湖按 Chunk 展开。
        private struct SpatialNode { public Vector3Int Min, Max; public int Region, Left, Right; }
        private readonly SpatialNode[] _index;
        private int _nextNode;

        private sealed class CenterOrder : IComparer<int>
        {
            private readonly CellularRestingLiquidRegion[] _regions;
            private readonly int _axis;
            internal CenterOrder(CellularRestingLiquidRegion[] regions, int axis) { _regions = regions; _axis = axis; }
            public int Compare(int a, int b)
            {
                var first = _regions[a].Cells; var second = _regions[b].Cells;
                long centerA = (long)first.min[_axis] + first.max[_axis], centerB = (long)second.min[_axis] + second.max[_axis];
                int cmp = centerA.CompareTo(centerB);
                return cmp == 0 ? a.CompareTo(b) : cmp;
            }
        }

        private void BuildRegionIndex()
        {
            var ids = new int[_regions.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = i;
            var orders = new[] { new CenterOrder(_regions, 0), new CenterOrder(_regions, 1), new CenterOrder(_regions, 2) };
            Build(ids, 0, ids.Length, orders);
            for (int i = 0; i < _regions.Length; i++)
                if (OverlapsOther(0, i, _regions[i].Cells)) throw new ArgumentException("静水区不能重复拥有同一格。");
        }

        private int Build(int[] ids, int start, int length, CenterOrder[] orders)
        {
            int index = _nextNode++;
            BoundsInt box = _regions[ids[start]].Cells;
            var node = new SpatialNode { Min = box.min, Max = box.max, Region = ids[start] };
            if (length > 1)
            {
                for (int i = 1; i < length; i++)
                {
                    box = _regions[ids[start + i]].Cells;
                    node.Min = Vector3Int.Min(node.Min, box.min);
                    node.Max = Vector3Int.Max(node.Max, box.max);
                }
                // 相距很远的负坐标和正坐标可能跨过 2^31，联合宽度和质心比较用 long。
                long x = (long)node.Max.x - node.Min.x, y = (long)node.Max.y - node.Min.y, z = (long)node.Max.z - node.Min.z;
                int axis = x >= y && x >= z ? 0 : y >= z ? 1 : 2;
                Array.Sort(ids, start, length, orders[axis]);
                int low = length / 2;
                node.Region = -1;
                node.Left = Build(ids, start, low, orders);
                node.Right = Build(ids, start + low, length - low, orders);
            }
            _index[index] = node;
            return index;
        }

        private bool OverlapsOther(int index, int region, BoundsInt box)
        {
            ref SpatialNode n = ref _index[index];
            if (n.Max.x <= box.xMin || n.Min.x >= box.xMax || n.Max.y <= box.yMin || n.Min.y >= box.yMax ||
                n.Max.z <= box.zMin || n.Min.z >= box.zMax) return false;
            return n.Region >= 0 ? n.Region != region : OverlapsOther(n.Left, region, box) || OverlapsOther(n.Right, region, box);
        }

        private int FindRegion(Vector3Int cell)
        {
            int authored = _index.Length == 0 ? -1 : Find(0, cell);
            return authored >= 0 || _coldPages.Count == 0 ? authored : FindCold(cell);
        }

        /// <summary>通过作者区 BVH 与动态冷页索引找相交或相贴的区域，含面、棱、角。精确可见面由渲染器裁剪。</summary>
        public void VisitTouchingRegions(BoundsInt cells, Action<int> visitor)
        {
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));
            if (cells.size.x < 1 || cells.size.y < 1 || cells.size.z < 1) return;
            if (_index.Length != 0) VisitTouching(0, cells, visitor);
            if (_coldPages.Count != 0) VisitColdTouching(cells, visitor);
        }

        private void VisitTouching(int index, BoundsInt box, Action<int> visitor)
        {
            ref SpatialNode n = ref _index[index];
            if (n.Max.x < box.xMin || n.Min.x > box.xMax || n.Max.y < box.yMin || n.Min.y > box.yMax ||
                n.Max.z < box.zMin || n.Min.z > box.zMax) return;
            if (n.Region >= 0) { visitor(n.Region); return; }
            VisitTouching(n.Left, box, visitor);
            VisitTouching(n.Right, box, visitor);
        }

        private int Find(int index, Vector3Int cell)
        {
            ref SpatialNode n = ref _index[index];
            if (cell.x < n.Min.x || cell.x >= n.Max.x || cell.y < n.Min.y || cell.y >= n.Max.y ||
                cell.z < n.Min.z || cell.z >= n.Max.z) return -1;
            if (n.Region >= 0) return n.Region;
            int left = Find(n.Left, cell);
            return left >= 0 ? left : Find(n.Right, cell);
        }
    }
}
