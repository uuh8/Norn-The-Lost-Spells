using System;
using UnityEngine;

namespace Game.Core
{
    public static partial class WorldChunkPayloadCodec
    {
        // 第五域独立使用 W2，旧四域仍写 W1。每个 resident 分块携带同一可信基底指纹，
        // 因而分块可以乱序安装，原始湖水和固定地形无需生成逐格网络样本。
        public const uint CellularMagic = 0x4E355732;
        public const int CellularPrefixWords = 15, SpanWords = 10;
        public static int CellOffset(uint[] w) => w[0] == CellularMagic ? CellularPrefixWords : 11;
        public const int CellularPendingWords = 20;
        public static int PendingWordCount(uint[] w) => w[0] == CellularMagic ? CellularPendingWords : PendingWords;
        public static int PendingOffset(uint[] w) => CellOffset(w) + (int)w[1] * CellWords;
        public static int SpanOffset(uint[] w) => PendingOffset(w) + (int)w[2] * PendingWordCount(w);
        public static int SpanCount(uint[] w) => w[0] == CellularMagic ? (int)w[14] : 0;

        public static bool SameCellularGeometry(in WorldGridGeometry a, in WorldGridGeometry b) =>
            a.Domain == b.Domain && a.BaseVersion == b.BaseVersion && a.CellCapacity == b.CellCapacity &&
            a.Origin.Equals(b.Origin) && a.CellSize == b.CellSize && a.ChunkSize == b.ChunkSize;

        public static void WriteSpan(uint[] w, int i, in WorldCellularSpan span)
        {
            var p = span.Cells.position; var size = span.Cells.size;
            w[i] = (uint)span.Kind;
            w[i+1] = unchecked((uint)p.x); w[i+2] = unchecked((uint)p.y); w[i+3] = unchecked((uint)p.z);
            w[i+4] = (uint)size.x; w[i+5] = (uint)size.y; w[i+6] = (uint)size.z;
            w[i+7] = span.MaterialId; w[i+8] = span.OriginalQuantity; w[i+9] = span.Quantity;
        }

        public static WorldCellularSpan ReadSpan(uint[] w, int i) => new WorldCellularSpan((WorldCellularSpanKind)w[i],
            new BoundsInt(unchecked((int)w[i+1]), unchecked((int)w[i+2]), unchecked((int)w[i+3]),
                (int)w[i+4], (int)w[i+5], (int)w[i+6]), (byte)w[i+7], w[i+8], w[i+9]);

        internal static int CompareSpans(in WorldCellularSpan a, in WorldCellularSpan b)
        {
            int compare = ((byte)a.Kind).CompareTo((byte)b.Kind);
            if (compare != 0) return compare;
            Vector3Int first = a.Cells.position, second = b.Cells.position;
            compare = first.x.CompareTo(second.x); if (compare != 0) return compare;
            compare = first.y.CompareTo(second.y); return compare != 0 ? compare : first.z.CompareTo(second.z);
        }

        private static bool ValidSpan(in WorldCellularSpan s, uint capacity, WorldChunkKey key, int size)
        {
            var p = s.Cells.position; var extent = s.Cells.size;
            if (extent.x <= 0 || extent.y <= 0 || extent.z <= 0 || (long)p.x + extent.x > int.MaxValue ||
                (long)p.y + extent.y > int.MaxValue || (long)p.z + extent.z > int.MaxValue ||
                CellKey(key.Domain, p, size) != key || CellKey(key.Domain, s.Cells.max - Vector3Int.one, size) != key) return false;
            if (s.Kind == WorldCellularSpanKind.Terrain)
                return (s.MaterialId == 0 || s.MaterialId == 9 || s.MaterialId == 10) && s.OriginalQuantity == 0 && s.Quantity == 0;
            bool liquid = s.MaterialId == 1 || s.MaterialId == 3 || s.MaterialId == 4 || s.MaterialId == 12;
            if (s.Kind == WorldCellularSpanKind.AuthoredLiquid) return liquid && s.OriginalQuantity == capacity && s.Quantity < capacity;
            bool cold = liquid || s.MaterialId == 5 || s.MaterialId == 6 || s.MaterialId == 11;
            return s.Kind == WorldCellularSpanKind.ColdMaterial && cold && s.OriginalQuantity == 0 && s.Quantity > 0 && s.Quantity <= capacity;
        }

        private static bool Occupies(in WorldCellularSpan span) =>
            span.Kind == WorldCellularSpanKind.Terrain ? span.MaterialId != 0 : span.Quantity != 0;

        private static bool Overlap(BoundsInt a, BoundsInt b) => a.xMin < b.xMax && a.xMax > b.xMin &&
            a.yMin < b.yMax && a.yMax > b.yMin && a.zMin < b.zMax && a.zMax > b.zMin;

        private static bool ValidateCellular(uint[] w, in WorldGridGeometry geometry, WorldChunkKey key)
        {
            int cellEnd = PendingOffset(w), spanStart = SpanOffset(w), spanEnd = spanStart + SpanCount(w) * SpanWords;
            Vector3Int previous = default;
            bool hasBulk = false, solid = false;
            ulong sum = 0;
            for (int i = CellOffset(w); i < cellEnd; i += CellWords)
            {
                var c = ReadCell(w, i);
                if (c.MaterialId == 2 || c.MaterialId > 12) return false;
                if (i == CellOffset(w) || previous != c.Cell) { hasBulk = solid = false; sum = 0; previous = c.Cell; }
                bool gas = c.MaterialId == 7 || c.MaterialId == 8;
                if ((!gas && hasBulk) || (gas && solid)) return false;
                if (!gas)
                {
                    hasBulk = true;
                    solid = c.MaterialId == 5 || c.MaterialId == 9 || c.MaterialId == 10 || c.MaterialId == 11;
                    if (solid && sum != 0) return false;
                }
                sum += c.Quantity;
                if (sum > geometry.CellCapacity) return false;
            }
            WorldCellularSpan prior = default;
            for (int i = spanStart; i < spanEnd; i += SpanWords)
            {
                if (w[i] < 1 || w[i] > 3 || w[i+7] > byte.MaxValue) return false;
                var span = ReadSpan(w, i);
                if (!ValidSpan(span, geometry.CellCapacity, key, geometry.ChunkSize) ||
                    (i != spanStart && CompareSpans(prior, span) >= 0)) return false;
                prior = span;
                for (int j = spanStart; j < i; j += SpanWords)
                {
                    var other = ReadSpan(w, j);
                    if (Overlap(span.Cells, other.Cells) && (span.Kind == other.Kind ||
                        (span.Kind != WorldCellularSpanKind.Terrain && other.Kind != WorldCellularSpanKind.Terrain) ||
                        (Occupies(span) && Occupies(other)))) return false;
                }
                if (!Occupies(span)) continue;
                Vector3Int lastGas = default;
                ulong gasQuantity = 0;
                for (int j = CellOffset(w); j < cellEnd; j += CellWords)
                {
                    var cell = ReadCell(w, j);
                    if (!span.Cells.Contains(cell.Cell)) continue;
                    // 低液面与干黏液留下的容积可含两种气体。沙、冰、地形封闭整格，即使 GMU 较少也不透气。
                    if (span.Kind == WorldCellularSpanKind.Terrain || span.MaterialId == 5 || span.MaterialId == 11 ||
                        cell.MaterialId != 7 && cell.MaterialId != 8) return false;
                    if (lastGas != cell.Cell) { lastGas = cell.Cell; gasQuantity = 0; }
                    gasQuantity += cell.Quantity;
                    if (gasQuantity + span.Quantity > geometry.CellCapacity) return false;
                }
            }
            return true;
        }
    }
}
