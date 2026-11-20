using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class CellularRestingLiquidStore
    {
        // 点查询直接哈希 4³ 页。查询很大的作者湖有哪些冷页接壤时，使用只记录实际子枝的层次索引，
        // 不遍历大盒覆盖的空页或场景另一端的冷水。4³ 页坐标只有 30 位，树最大深度固定。
        private const int PageBias = 1 << 29;
        private readonly Dictionary<(Vector3Int, int), byte> _branches = new Dictionary<(Vector3Int, int), byte>();

        private static Vector3Int Biased(Vector3Int page) => new Vector3Int(page.x + PageBias, page.y + PageBias, page.z + PageBias);

        private void IndexColdPage(Vector3Int key, bool add)
        {
            Vector3Int child = Biased(key);
            for (int level = 1; level <= 30; level++)
            {
                int bit = 1 << ((child.x & 1) | ((child.y & 1) << 1) | ((child.z & 1) << 2));
                var parent = new Vector3Int(child.x >> 1, child.y >> 1, child.z >> 1);
                var address = (parent, level);
                _branches.TryGetValue(address, out byte old);
                byte mask = (byte)(add ? old | bit : old & ~bit);
                if (mask == 0) _branches.Remove(address); else _branches[address] = mask;
                if (add ? old != 0 : mask != 0) break;
                child = parent;
            }
        }

        private void VisitColdTouching(BoundsInt box, Action<int> visitor)
        {
            Vector3Int min = box.min;
            if (min.x > int.MinValue) min.x--;
            if (min.y > int.MinValue) min.y--;
            if (min.z > int.MinValue) min.z--;
            Vector3Int first = PageKey(min), last = PageKey(box.max);
            if ((long)last.x - first.x <= 3 && (long)last.y - first.y <= 3 && (long)last.z - first.z <= 3)
            {
                for (int z = first.z; z <= last.z; z++)
                for (int y = first.y; y <= last.y; y++)
                for (int x = first.x; x <= last.x; x++) VisitColdPage(new Vector3Int(x, y, z), box, visitor);
                return;
            }
            VisitBranch(Vector3Int.zero, 30, Biased(first), Biased(last), box, visitor);
        }

        private void VisitBranch(Vector3Int node, int level, Vector3Int first, Vector3Int last, BoundsInt box, Action<int> visitor)
        {
            long width = 1L << level;
            long x = (long)node.x << level, y = (long)node.y << level, z = (long)node.z << level;
            if (x > last.x || x + width <= first.x || y > last.y || y + width <= first.y || z > last.z || z + width <= first.z) return;
            if (level == 0)
            {
                VisitColdPage(new Vector3Int(node.x - PageBias, node.y - PageBias, node.z - PageBias), box, visitor);
                return;
            }
            if (!_branches.TryGetValue((node, level), out byte mask)) return;
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << i)) != 0)
                    VisitBranch(new Vector3Int(node.x * 2 + (i & 1), node.y * 2 + ((i >> 1) & 1),
                        node.z * 2 + (i >> 2)), level - 1, first, last, box, visitor);
        }

        private void VisitColdPage(Vector3Int key, BoundsInt box, Action<int> visitor)
        {
            if (!_coldPages.TryGetValue(key, out ColdPage page)) return;
            foreach (int index in page.Regions)
            {
                BoundsInt candidate = GetRegion(index).Cells;
                if (candidate.xMax >= box.xMin && candidate.xMin <= box.xMax &&
                    candidate.yMax >= box.yMin && candidate.yMin <= box.yMax &&
                    candidate.zMax >= box.zMin && candidate.zMin <= box.zMax) visitor(index);
            }
        }
    }
}
