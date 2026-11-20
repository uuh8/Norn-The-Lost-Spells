using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class CellularRestingLiquidStore
    {
        private struct ColdRegion
        {
            public CellularRestingLiquidRegion Definition;
            public int Root;
            public ulong Quantity, Version;
        }

        private sealed class ColdPage
        {
            public readonly int[] RegionByCell = new int[64];
            public readonly List<int> Regions = new List<int>(4);
        }

        private struct LayoutChange { public int Region; public BoundsInt Previous, Current; }
        private readonly LayoutChange[] _layoutJournal;
        private readonly List<ColdRegion> _cold = new List<ColdRegion>();
        private readonly Stack<int> _freeRegions = new Stack<int>();
        private readonly Dictionary<Vector3Int, ColdPage> _coldPages = new Dictionary<Vector3Int, ColdPage>();
        private readonly Stack<ColdPage> _freeColdPages = new Stack<ColdPage>();
        public ulong RegionLayoutVersion { get; private set; }

        /// <summary>已绑定读者自上次游标起顺序读取新增、退休和复用。若日志过旧，返回 false 并由调用方重取快照。</summary>
        public bool VisitLayoutChanges(ulong since, Action<int, BoundsInt, BoundsInt> visitor)
        {
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));
            ulong end = RegionLayoutVersion;
            if (since > end || end - since > (ulong)_layoutJournal.Length) return false;
            while (since < end)
            {
                var record = _layoutJournal[(int)(since++ % (ulong)_layoutJournal.Length)];
                visitor(record.Region, record.Previous, record.Current);
            }
            return true;
        }

        private void RecordLayout(int region, BoundsInt previous, BoundsInt current)
        {
            int offset = (int)(RegionLayoutVersion % (ulong)_layoutJournal.Length);
            _layoutJournal[offset] = new LayoutChange { Region = region, Previous = previous, Current = current };
            RegionLayoutVersion++;
        }

        private static Vector3Int PageKey(Vector3Int c) => new Vector3Int(c.x >> 2, c.y >> 2, c.z >> 2);
        private static int InPage(Vector3Int c) => (c.x & 3) + ((c.y & 3) << 2) + ((c.z & 3) << 4);
        private static bool InteriorOverlap(BoundsInt a, BoundsInt b) =>
            a.xMin < b.xMax && a.xMax > b.xMin && a.yMin < b.yMax && a.yMax > b.yMin && a.zMin < b.zMax && a.zMax > b.zMin;

        private int FindCold(Vector3Int c) =>
            _coldPages.TryGetValue(PageKey(c), out ColdPage page) ? page.RegionByCell[InPage(c)] - 1 : -1;

        private int ClaimCold(Vector3Int cell, MaterialId material, int previous)
        {
            if (previous >= 0) return DivideColdForMaterial(previous, cell, material);
            Vector3Int key = PageKey(cell);
            var lower = new Vector3Int(key.x << 2, key.y << 2, key.z << 2);
            var size = new Vector3Int((int)Math.Min(4L, (long)int.MaxValue - lower.x),
                (int)Math.Min(4L, (long)int.MaxValue - lower.y), (int)Math.Min(4L, (long)int.MaxValue - lower.z));
            var box = new BoundsInt(lower, size);
            _coldPages.TryGetValue(key, out ColdPage page);
            // 跨过作者地层边缘或同页其它材料时，只缩小这条局部路径；绝不占用作者湖的空洞定义。
            while ((_index.Length != 0 && OverlapsOther(0, -1, box)) || OverlapsCold(page, box))
            {
                Split(box, out BoundsInt left, out BoundsInt right);
                box = left.Contains(cell) ? left : right;
            }
            return RegisterCold(box, material, NewNode(0), 0);
        }

        private bool OverlapsCold(ColdPage page, BoundsInt box)
        {
            if (page == null) return false;
            foreach (int index in page.Regions) if (InteriorOverlap(box, GetRegion(index).Cells)) return true;
            return false;
        }

        private int DivideColdForMaterial(int previous, Vector3Int cell, MaterialId material)
        {
            ColdRegion old = _cold[previous - _regions.Length];
            BoundsInt box = old.Definition.Cells;
            int node = old.Root;
            RetireCold(previous, false);
            // 此格在旧冷区中尚未被占据。沿数量树找到包含它的最大空叶，
            // 其它有量的兄弟子树直接改挂在原材质的区域记录上，不展开、复制或重复记账。
            while (_nodes[node].Low != 0)
            {
                Node branch = _nodes[node];
                Split(box, out BoundsInt left, out BoundsInt right);
                bool descendLow = left.Contains(cell);
                int sibling = descendLow ? branch.High : branch.Low;
                BoundsInt siblingBox = descendLow ? right : left;
                ulong amount = Sum(sibling, siblingBox);
                if (amount != 0) RegisterCold(siblingBox, old.Definition.Material, sibling, amount);
                else FreeNode(sibling);
                FreeNode(node);
                node = descendLow ? branch.Low : branch.High;
                box = descendLow ? left : right;
            }
            return RegisterCold(box, material, node, 0);
        }

        private ulong Sum(int root, BoundsInt box)
        {
            Node node = _nodes[root];
            if (node.Low == 0)
            {
                var size = box.size;
                return (ulong)size.x * (uint)size.y * (uint)size.z * node.Amount;
            }
            Split(box, out BoundsInt left, out BoundsInt right);
            return Sum(node.Low, left) + Sum(node.High, right);
        }

        private int RegisterCold(BoundsInt box, MaterialId material, int root, ulong quantity)
        {
            int slot = _freeRegions.Count == 0 ? _cold.Count : _freeRegions.Pop();
            var value = new ColdRegion { Definition = new CellularRestingLiquidRegion { Cells = box, Material = material },
                Root = root, Quantity = quantity, Version = ++Version };
            if (slot == _cold.Count) _cold.Add(value); else _cold[slot] = value;
            int index = _regions.Length + slot;
            Vector3Int key = PageKey(box.min);
            if (!_coldPages.TryGetValue(key, out ColdPage page))
            {
                page = _freeColdPages.Count == 0 ? new ColdPage() : _freeColdPages.Pop();
                _coldPages.Add(key, page);
                IndexColdPage(key, true);
            }
            page.Regions.Add(index);
            FillLookup(page, box, index + 1);
            RecordLayout(index, default, box);
            return index;
        }

        private static void FillLookup(ColdPage page, BoundsInt box, int encoded)
        {
            for (int z = box.zMin; z < box.zMax; z++)
            for (int y = box.yMin; y < box.yMax; y++)
            for (int x = box.xMin; x < box.xMax; x++)
                page.RegionByCell[InPage(new Vector3Int(x, y, z))] = encoded;
        }

        private void UpdateCold(int region, uint before, uint after)
        {
            int index = region - _regions.Length;
            var state = _cold[index];
            state.Quantity = state.Quantity - before + after;
            state.Version = Version;
            _cold[index] = state;
            if (state.Quantity == 0) RetireCold(region, true);
        }

        private void RetireCold(int region, bool freeRoot)
        {
            int slot = region - _regions.Length;
            var state = _cold[slot];
            BoundsInt box = state.Definition.Cells;
            Vector3Int key = PageKey(box.min);
            var page = _coldPages[key];
            FillLookup(page, box, 0);
            page.Regions.Remove(region);
            if (page.Regions.Count == 0)
            {
                _coldPages.Remove(key); IndexColdPage(key, false); _freeColdPages.Push(page);
            }
            if (freeRoot) FreeNode(state.Root); // 最后一格已清空时，数量树已经折叠回单一空叶。
            _cold[slot] = new ColdRegion { Version = ++Version };
            _freeRegions.Push(slot);
            RecordLayout(region, box, default);
        }
    }
}
