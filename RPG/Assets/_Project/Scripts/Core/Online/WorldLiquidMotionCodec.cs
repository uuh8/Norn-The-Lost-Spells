using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 仅表现使用的有界 word 合同；数量 payload 和 checkpoint 布局不经过本 codec。
    /// Prefix: magic / SampleTime低高32bit / Interval / ChunkSize / AnchorCount / RowCount。
    /// Anchor: KeyXYZ / Generation低高 / Revision低高 / Subscription低高 / IsResident。
    /// Row: AnchorIndex / localIndex低24bit与MaterialId高8bit / 两个四word端点。
    /// 端点: uint数量 / Half(offsetX,offsetY) / Half(offsetZ,velocityX) / Half(velocityY,velocityZ)。
    /// GMU、地址和版本不量化；只压缩相对姿态。世界大坐标绝不能先转float再打包。
    /// </summary>
    public static class WorldLiquidMotionCodec
    {
        public const uint Magic = 0x4E4C4D31;
        public const int PrefixWords = 7, AnchorWords = 10, RowWords = 10;
        public const int MaxWords = PrefixWords + WorldLiquidMotionFrame.MaxAnchors * AnchorWords
            + WorldLiquidMotionFrame.MaxRows * RowWords;
        public const float MaxOffsetCells = 4f, MaxVelocityWorld = 64f;

        /// <summary>
        /// 同步预检整个body，调用方也可用它保护不经过wire的直接Sink调用。
        /// WorldEpoch/ConnectionEpoch/Sequence/MaterialTick属于页头，不由body授权或覆盖。
        /// 两个小规模有界去重循环避免热路径临时HashSet；最大32个Anchor、128行。
        /// </summary>
        public static bool Validate(WorldLiquidMotionFrame frame)
        {
            if (frame == null || !ValidPrefix(frame.SampleTime, frame.Interval, frame.ChunkSize,
                    frame.AnchorCount, frame.RowCount)) return false;
            for (int a = 0; a < frame.AnchorCount; a++)
            {
                if (!ValidAnchor(frame.Anchors[a], frame.ChunkSize)) return false;
                for (int previous = 0; previous < a; previous++)
                    if (frame.Anchors[previous].Key == frame.Anchors[a].Key) return false;
            }
            for (int r = 0; r < frame.RowCount; r++)
            {
                var row = frame.Rows[r];
                if ((uint)row.AnchorIndex >= (uint)frame.AnchorCount || row.MaterialId == 0
                    || row.PreviousQuantity == 0 && row.CurrentQuantity == 0
                    || !ValidEndpoint(row.PreviousQuantity, row.PreviousOffset, row.PreviousVelocity)
                    || !ValidEndpoint(row.CurrentQuantity, row.CurrentOffset, row.CurrentVelocity)) return false;
                var anchor = frame.Anchors[row.AnchorIndex];
                if (!anchor.IsResident && row.CurrentQuantity != 0
                    || !TryLocalIndex(row.Cell, anchor.Key, frame.ChunkSize, out _)) return false;
                for (int previous = 0; previous < r; previous++)
                    if (frame.Rows[previous].Cell == row.Cell && frame.Rows[previous].MaterialId == row.MaterialId)
                        return false;
            }
            return true;
        }

        public static bool TryEncode(WorldLiquidMotionFrame frame, uint[] words, out int count)
        {
            count = 0;
            // 所有可失败的检查发生在首个word写入之前；调用方可以保留上次冻结发送数组。
            if (words == null || !Validate(frame)) return false;
            int required = PrefixWords + frame.AnchorCount * AnchorWords + frame.RowCount * RowWords;
            if (words.Length < required) return false;
            words[0] = Magic;
            WriteUlong(words, 1, unchecked((ulong)BitConverter.DoubleToInt64Bits(frame.SampleTime)));
            words[3] = unchecked((uint)BitConverter.SingleToInt32Bits(frame.Interval));
            words[4] = (uint)frame.ChunkSize; words[5] = (uint)frame.AnchorCount; words[6] = (uint)frame.RowCount;
            int offset = PrefixWords;
            for (int a = 0; a < frame.AnchorCount; a++, offset += AnchorWords)
            {
                var anchor = frame.Anchors[a];
                words[offset] = unchecked((uint)anchor.Key.X);
                words[offset + 1] = unchecked((uint)anchor.Key.Y);
                words[offset + 2] = unchecked((uint)anchor.Key.Z);
                WriteUlong(words, offset + 3, anchor.Generation);
                WriteUlong(words, offset + 5, anchor.Revision);
                WriteUlong(words, offset + 7, anchor.SubscriptionEpoch);
                words[offset + 9] = anchor.IsResident ? 1u : 0u;
            }
            for (int r = 0; r < frame.RowCount; r++, offset += RowWords)
            {
                var row = frame.Rows[r];
                TryLocalIndex(row.Cell, frame.Anchors[row.AnchorIndex].Key, frame.ChunkSize, out uint local);
                words[offset] = (uint)row.AnchorIndex;
                words[offset + 1] = local | (uint)row.MaterialId << 24;
                WriteEndpoint(words, offset + 2, row.PreviousQuantity, row.PreviousOffset, row.PreviousVelocity);
                WriteEndpoint(words, offset + 6, row.CurrentQuantity, row.CurrentOffset, row.CurrentVelocity);
            }
            count = required; return true;
        }

        /// <summary>
        /// 先完整验证原始words，再写调用方buffer。失败时连Count、SampleTime和外部身份都不修改；
        /// 成功也只覆盖body，外部四个身份字段仍由已认证的分页Owner赋值。
        /// </summary>
        public static bool TryDecode(uint[] words, int count, WorldLiquidMotionFrame destination)
        {
            if (destination == null || !ValidateWords(words, count)) return false;
            int anchors = (int)words[5], rows = (int)words[6], size = (int)words[4];
            int offset = PrefixWords;
            for (int a = 0; a < anchors; a++, offset += AnchorWords)
                destination.Anchors[a] = ReadAnchor(words, offset);
            for (int r = 0; r < rows; r++, offset += RowWords)
                destination.Rows[r] = ReadRow(words, offset, size);
            destination.SampleTime = BitConverter.Int64BitsToDouble(unchecked((long)ReadUlong(words, 1)));
            destination.Interval = BitConverter.Int32BitsToSingle(unchecked((int)words[3]));
            destination.ChunkSize = size; destination.AnchorCount = anchors; destination.RowCount = rows;
            return true;
        }

        private static bool ValidateWords(uint[] words, int count)
        {
            if (words == null || count < PrefixWords || count > MaxWords || count > words.Length
                || words[0] != Magic || words[4] > 256 || words[5] > WorldLiquidMotionFrame.MaxAnchors
                || words[6] > WorldLiquidMotionFrame.MaxRows) return false;
            int size = (int)words[4], anchors = (int)words[5], rows = (int)words[6];
            double time = BitConverter.Int64BitsToDouble(unchecked((long)ReadUlong(words, 1)));
            float interval = BitConverter.Int32BitsToSingle(unchecked((int)words[3]));
            if (!ValidPrefix(time, interval, size, anchors, rows)
                || count != PrefixWords + anchors * AnchorWords + rows * RowWords) return false;
            int offset = PrefixWords;
            for (int a = 0; a < anchors; a++, offset += AnchorWords)
            {
                if (words[offset + 9] > 1) return false;
                var anchor = ReadAnchor(words, offset);
                if (!ValidAnchor(anchor, size)) return false;
                for (int previous = 0; previous < a; previous++)
                    if (ReadAnchor(words, PrefixWords + previous * AnchorWords).Key == anchor.Key) return false;
            }
            int firstRow = offset;
            uint cells = (uint)(size * size * size);
            for (int r = 0; r < rows; r++, offset += RowWords)
            {
                if (words[offset] >= anchors || words[offset + 1] >> 24 == 0
                    || (words[offset + 1] & 0xFFFFFF) >= cells
                    || !ValidPackedEndpoint(words, offset + 2) || !ValidPackedEndpoint(words, offset + 6)
                    || words[offset + 2] == 0 && words[offset + 6] == 0) return false;
                var anchor = ReadAnchor(words, PrefixWords + (int)words[offset] * AnchorWords);
                if (!anchor.IsResident && words[offset + 6] != 0) return false;
                // Anchor已经去重，同anchor/local/material相等就是同一原生格同材料重复。
                for (int previous = firstRow; previous < offset; previous += RowWords)
                    if (words[previous] == words[offset] && words[previous + 1] == words[offset + 1]) return false;
            }
            return true;
        }

        private static bool ValidPrefix(double time, float interval, int size, int anchors, int rows)
            => double.IsFinite(time) && time >= 0 && float.IsFinite(interval) && interval >= .01f && interval <= 1f
                && size >= 1 && size <= 256 && anchors >= 0 && anchors <= WorldLiquidMotionFrame.MaxAnchors
                && rows >= 0 && rows <= WorldLiquidMotionFrame.MaxRows && (rows == 0 || anchors > 0);

        private static bool ValidAnchor(in WorldLiquidMotionAnchor anchor, int size)
            => anchor.Key.Domain == WorldMaterialDomain.Liquid && anchor.Generation != 0 && anchor.Revision != 0
                && anchor.SubscriptionEpoch != 0 && ValidAxis(anchor.Key.X, size)
                && ValidAxis(anchor.Key.Y, size) && ValidAxis(anchor.Key.Z, size);

        private static bool ValidAxis(int chunk, int size)
        {
            long first = (long)chunk * size;
            return first >= int.MinValue && first + size - 1 <= int.MaxValue;
        }

        private static bool TryLocalIndex(Vector3Int cell, WorldChunkKey key, int size, out uint index)
        {
            index = 0;
            long x = (long)cell.x - (long)key.X * size, y = (long)cell.y - (long)key.Y * size,
                z = (long)cell.z - (long)key.Z * size;
            if (x < 0 || x >= size || y < 0 || y >= size || z < 0 || z >= size) return false;
            index = (uint)(x + size * (y + size * z)); return true;
        }

        private static bool ValidEndpoint(uint quantity, Vector3 offset, Vector3 velocity)
            => quantity <= ushort.MaxValue && ValidVector(offset, MaxOffsetCells) && ValidVector(velocity, MaxVelocityWorld)
                && (quantity != 0 || IsZero(offset) && IsZero(velocity));

        private static bool IsZero(Vector3 value) => value.x == 0 && value.y == 0 && value.z == 0;
        private static bool ValidVector(Vector3 value, float maximum) => float.IsFinite(value.x)
            && float.IsFinite(value.y) && float.IsFinite(value.z) && Mathf.Abs(value.x) <= maximum
            && Mathf.Abs(value.y) <= maximum && Mathf.Abs(value.z) <= maximum;

        private static bool ValidPackedEndpoint(uint[] words, int offset)
        {
            uint quantity = words[offset];
            if (quantity > ushort.MaxValue) return false;
            // 空端点必须使用规范的全零姿态，防止隐藏NaN/非零速度绕过后续空格快路径。
            if (quantity == 0) return words[offset + 1] == 0 && words[offset + 2] == 0 && words[offset + 3] == 0;
            ReadEndpoint(words, offset, out _, out var position, out var velocity);
            return ValidEndpoint(quantity, position, velocity);
        }

        private static void WriteEndpoint(uint[] words, int offset, uint quantity, Vector3 position, Vector3 velocity)
        {
            words[offset] = quantity;
            words[offset + 1] = quantity == 0 ? 0 : HalfPair(position.x, position.y);
            words[offset + 2] = quantity == 0 ? 0 : HalfPair(position.z, velocity.x);
            words[offset + 3] = quantity == 0 ? 0 : HalfPair(velocity.y, velocity.z);
        }

        private static void ReadEndpoint(uint[] words, int offset, out uint quantity, out Vector3 position, out Vector3 velocity)
        {
            quantity = words[offset];
            position = new Vector3(Low(words[offset + 1]), High(words[offset + 1]), Low(words[offset + 2]));
            velocity = new Vector3(High(words[offset + 2]), Low(words[offset + 3]), High(words[offset + 3]));
        }

        private static WorldLiquidMotionAnchor ReadAnchor(uint[] words, int offset)
            => new WorldLiquidMotionAnchor(new WorldChunkKey(WorldMaterialDomain.Liquid,
                unchecked((int)words[offset]), unchecked((int)words[offset + 1]), unchecked((int)words[offset + 2])),
                ReadUlong(words, offset + 3), ReadUlong(words, offset + 5), ReadUlong(words, offset + 7), words[offset + 9] == 1);

        private static WorldLiquidMotionSample ReadRow(uint[] words, int offset, int size)
        {
            int anchorIndex = (int)words[offset]; var key = ReadAnchor(words, PrefixWords + anchorIndex * AnchorWords).Key;
            int local = (int)(words[offset + 1] & 0xFFFFFF);
            // Anchor的完整坐标区间已用long验证，以下int恢复不会溢出；不能Clamp非法坐标。
            var cell = new Vector3Int(key.X * size + local % size, key.Y * size + local / size % size,
                key.Z * size + local / (size * size));
            ReadEndpoint(words, offset + 2, out uint previous, out var previousOffset, out var previousVelocity);
            ReadEndpoint(words, offset + 6, out uint current, out var currentOffset, out var currentVelocity);
            return new WorldLiquidMotionSample(cell, (byte)(words[offset + 1] >> 24), previous, current,
                previousOffset, previousVelocity, currentOffset, currentVelocity, anchorIndex);
        }

        private static uint HalfPair(float first, float second) => (uint)Mathf.FloatToHalf(first) | (uint)Mathf.FloatToHalf(second) << 16;
        private static float Low(uint value) => Mathf.HalfToFloat((ushort)value);
        private static float High(uint value) => Mathf.HalfToFloat((ushort)(value >> 16));
        private static ulong ReadUlong(uint[] words, int offset) => words[offset] | (ulong)words[offset + 1] << 32;
        private static void WriteUlong(uint[] words, int offset, ulong value)
        { words[offset] = unchecked((uint)value); words[offset + 1] = (uint)(value >> 32); }

        public static uint ComputeChecksum(uint[] words, int count) => WorldChunkPayloadCodec.ComputeChecksum(words, count);
    }
}
