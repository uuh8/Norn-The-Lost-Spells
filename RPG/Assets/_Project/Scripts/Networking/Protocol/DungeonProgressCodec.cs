using System;
using Core = Game.Core;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>
    /// Run级进度的有界Protobuf适配。客户端以认证身份提供独立的预期Run/Dungeon/Hash，不能拿消息内的身份
    /// 与自己比较就算认证成功。这里只有稀疏状态转换才编码；字符串解析/消息对象不进入每Tick路径。
    /// </summary>
    public sealed class DungeonProgressCodec
    {
        public const int MaxPayloadBytes = 256;
        public bool TryEncode(in Core.OnlineDungeonProgress progress, string runId, string dungeonId, ulong gameplayHash,
            ArraySegment<byte> output, out int written)
        {
            written = 0;
            if (!Valid(progress) || !RunId(runId) || !DungeonId(dungeonId) || gameplayHash == 0 || output.Array == null) return false;
            var message = new DungeonProgressMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion, GameplayHash = gameplayHash,
                RunId = runId, DungeonId = dungeonId, Revision = progress.Revision, ServerTick = progress.ServerTick,
                EncounterIndexPlusOne = progress.EncounterIndexPlusOne, Phase = (uint)progress.Phase, RemainingEnemies = progress.RemainingEnemies };
            int size = message.CalculateSize(); if (size < 1 || size > MaxPayloadBytes || size > output.Count) return false;
            var bytes = new Span<byte>(output.Array, output.Offset, size); message.WriteTo(bytes);
            if (!Wire(bytes)) return false;
            written = size; return true;
        }
        public bool TryDecode(ArraySegment<byte> payload, string expectedRunId, string expectedDungeonId, ulong expectedGameplayHash,
            out Core.OnlineDungeonProgress progress)
        {
            progress = default;
            if (expectedGameplayHash == 0 || !RunId(expectedRunId) || !DungeonId(expectedDungeonId) || payload.Array == null ||
                payload.Count < 1 || payload.Count > MaxPayloadBytes) return false;
            var bytes = new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count);
            // 先在借用的Span上限制字符串/标量个数，避免先构造巨大未知字段对象再检查包长。
            if (!Wire(bytes)) return false;
            var message = new DungeonProgressMessage();
            try { message.MergeFrom(bytes); } catch (InvalidProtocolBufferException) { return false; }
            if (!ProtocolCompatibility.IsSupported(message.ProtocolVersion) || message.GameplayHash != expectedGameplayHash ||
                message.RunId != expectedRunId || message.DungeonId != expectedDungeonId || message.Phase < 1 || message.Phase > 3) return false;
            var candidate = new Core.OnlineDungeonProgress { Revision = message.Revision, ServerTick = message.ServerTick,
                EncounterIndexPlusOne = message.EncounterIndexPlusOne, Phase = (Core.OnlineDungeonPhase)message.Phase,
                RemainingEnemies = message.RemainingEnemies };
            if (!Valid(candidate)) return false;
            progress = candidate; return true;
        }
        private static bool Valid(in Core.OnlineDungeonProgress p) => p.Revision > 0 && p.ServerTick > 0 &&
            p.EncounterIndexPlusOne >= 1 && p.EncounterIndexPlusOne <= 4 && p.Phase >= Core.OnlineDungeonPhase.Armed &&
            p.Phase <= Core.OnlineDungeonPhase.StageCleared && p.RemainingEnemies <= 12 &&
            (p.Phase != Core.OnlineDungeonPhase.StageCleared || p.RemainingEnemies == 0);
        private static bool RunId(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int i = 0; i < value.Length; i++) if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }
        private static bool DungeonId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32 || value[0] < 'a' || value[0] > 'z') return false;
            for (int i = 1; i < value.Length; i++) if (!(value[i] >= 'a' && value[i] <= 'z') && !(value[i] >= '0' && value[i] <= '9') && value[i] != '_' && value[i] != '-') return false;
            return true;
        }
        private static bool Wire(ReadOnlySpan<byte> bytes)
        {
            int offset = 0; uint seen = 0;
            while (offset < bytes.Length)
            {
                if (!Varint(bytes, ref offset, out ulong tag) || tag > uint.MaxValue) return false;
                int field = (int)(tag >> 3); if (field < 1 || field > 9 || (seen & 1u << field) != 0) return false;
                seen |= 1u << field;
                int expected = field == 2 ? 1 : field == 3 || field == 4 ? 2 : 0;
                if ((tag & 7) != (ulong)expected) return false;
                if (expected == 1) { if (bytes.Length - offset < 8) return false; offset += 8; }
                else if (expected == 2)
                {
                    if (!Varint(bytes, ref offset, out ulong size) || size > (ulong)(bytes.Length - offset) || size > 32 ||
                        (field == 3 ? size != 32 : size < 1)) return false;
                    for (int i = 0; i < (int)size; i++)
                    {
                        byte c = bytes[offset + i];
                        if (field == 3)
                        { if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false; }
                        else if (i == 0 ? c < 'a' || c > 'z' : !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
                    }
                    offset += (int)size;
                }
                else if (!Varint(bytes, ref offset, out ulong value) || value > uint.MaxValue || field == 8 && (value < 1 || value > 3)) return false;
            }
            return true;
        }
        private static bool Varint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift <= 63; shift += 7)
            {
                if (offset >= bytes.Length) return false;
                byte next = bytes[offset++]; if (shift == 63 && next > 1) return false;
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return shift == 0 || next != 0;
            }
            return false;
        }
    }
}
