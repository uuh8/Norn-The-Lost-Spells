using System;
using Core = Game.Core;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>
    /// 个人退出的256B离散值边界。只传固定命令/当前连接代次与阶段，不接收资产或Client身份。
    /// 先在Span预检重复字段、长度和枚举原值，再由Protobuf解析；byte枚举不能吞掉257等别名。
    /// </summary>
    public sealed class RunExitCodec
    {
        public const int MaxPayloadBytes = 256;
        public const int MaxFailureBytes = 96;

        public bool TryEncodeIntent(in Core.OnlineRunExitIntent intent, ulong gameplayHash,
            ArraySegment<byte> output, out int written)
        {
            written = 0;
            if (!Valid(intent) || gameplayHash == 0 || output.Array == null) return false;
            return Encode(new RunExitIntentMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion,
                GameplayHash = gameplayHash, ConnectionEpoch = intent.ConnectionEpoch, CommandId = intent.CommandId,
                ThroughReturnPortal = intent.ThroughReturnPortal }, output, out written);
        }

        public bool TryDecodeIntent(ArraySegment<byte> payload, ulong expectedHash, out Core.OnlineRunExitIntent intent)
        {
            intent = default;
            if (expectedHash == 0 || !Payload(payload, false)) return false;
            var message = new RunExitIntentMessage();
            try { message.MergeFrom(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count)); }
            catch (InvalidProtocolBufferException) { return false; }
            if (!ProtocolCompatibility.IsSupported(message.ProtocolVersion) || message.GameplayHash != expectedHash) return false;
            var candidate = new Core.OnlineRunExitIntent { CommandId = message.CommandId, ConnectionEpoch = message.ConnectionEpoch,
                ThroughReturnPortal = message.ThroughReturnPortal };
            if (!Valid(candidate)) return false;
            intent = candidate; return true;
        }

        public bool TryEncodeStatus(in Core.OnlineRunExitStatus status, ulong gameplayHash,
            ArraySegment<byte> output, out int written)
        {
            written = 0;
            if (!Valid(status) || gameplayHash == 0 || output.Array == null) return false;
            return Encode(new RunExitStatusMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion,
                GameplayHash = gameplayHash, ConnectionEpoch = status.ConnectionEpoch, CommandId = status.CommandId,
                Phase = (RunExitWirePhase)(byte)status.Phase, Failure = status.Failure ?? string.Empty,
                CommittedVersion = status.CommittedVersion, PayloadHash = status.PayloadHash ?? string.Empty,
                Reason = (uint)status.Reason }, output, out written);
        }

        public bool TryDecodeStatus(ArraySegment<byte> payload, ulong expectedHash, out Core.OnlineRunExitStatus status)
        {
            status = default;
            if (expectedHash == 0 || !Payload(payload, true)) return false;
            var message = new RunExitStatusMessage();
            try { message.MergeFrom(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count)); }
            catch (InvalidProtocolBufferException) { return false; }
            if (!ProtocolCompatibility.IsSupported(message.ProtocolVersion) || message.GameplayHash != expectedHash
                || (int)message.Phase < 1 || (int)message.Phase > (int)Core.OnlineRunExitPhase.Rejected) return false;
            var candidate = new Core.OnlineRunExitStatus { CommandId = message.CommandId, ConnectionEpoch = message.ConnectionEpoch,
                Phase = (Core.OnlineRunExitPhase)message.Phase, Failure = message.Failure,
                CommittedVersion = message.CommittedVersion, PayloadHash = message.PayloadHash,
                Reason = (Core.OnlineRunExitReason)message.Reason };
            if (!Valid(candidate)) return false;
            status = candidate; return true;
        }

        private static bool Encode(IMessage message, ArraySegment<byte> output, out int written)
        {
            written = 0; int length = message.CalculateSize();
            if (length <= 0 || length > MaxPayloadBytes || length > output.Count) return false;
            message.WriteTo(new Span<byte>(output.Array, output.Offset, length)); written = length; return true;
        }
        private static bool Valid(in Core.OnlineRunExitIntent value) => value.ConnectionEpoch != 0 && Hex(value.CommandId, 32);
        private static bool Valid(in Core.OnlineRunExitStatus value)
        {
            if (value.ConnectionEpoch == 0 || !Hex(value.CommandId, 32) || (byte)value.Phase < 1
                || (byte)value.Phase > (byte)Core.OnlineRunExitPhase.Rejected || !Failure(value.Failure)
                || value.Reason < Core.OnlineRunExitReason.VoluntaryExit || value.Reason > Core.OnlineRunExitReason.FinalDeath) return false;
            if (value.Phase == Core.OnlineRunExitPhase.Committed)
                return value.CommittedVersion != 0 && Hex(value.PayloadHash, 64) && string.IsNullOrEmpty(value.Failure);
            // 失败或仍在保存不能夹带“看似已提交”的版本/摘要，也不能用无原因Rejected当完成。
            return value.CommittedVersion == 0 && string.IsNullOrEmpty(value.PayloadHash)
                && (value.Phase != Core.OnlineRunExitPhase.Rejected || !string.IsNullOrEmpty(value.Failure));
        }
        private static bool Hex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            for (int i = 0; i < value.Length; i++) if (!Hex(value[i])) return false;
            return true;
        }
        private static bool Hex(int value) => value >= '0' && value <= '9' || value >= 'a' && value <= 'f';
        private static bool Failure(string value)
        {
            if (value == null) return true;
            if (value.Length > MaxFailureBytes) return false;
            for (int i = 0; i < value.Length; i++) if (value[i] < 0x21 || value[i] > 0x7e) return false;
            return true;
        }
        private static bool Payload(ArraySegment<byte> bytes, bool status) => bytes.Array != null && bytes.Count > 0
            && bytes.Count <= MaxPayloadBytes && Wire(new ReadOnlySpan<byte>(bytes.Array, bytes.Offset, bytes.Count), status);

        private static bool Wire(ReadOnlySpan<byte> bytes, bool status)
        {
            int offset = 0; uint seen = 0;
            while (offset < bytes.Length)
            {
                if (!Varint(bytes, ref offset, out ulong tag) || tag > uint.MaxValue) return false;
                int field = (int)(tag >> 3);
                if (field < 1 || field > (status ? 9 : 5)) return false;
                uint bit = 1u << field; if ((seen & bit) != 0) return false; seen |= bit;
                bool text = field == 4 || status && (field == 6 || field == 8);
                int expected = field == 2 ? 1 : text ? 2 : 0;
                if ((tag & 7) != (ulong)expected) return false;
                if (expected == 1)
                { if (bytes.Length - offset < 8) return false; offset += 8; }
                else if (text)
                {
                    if (!Varint(bytes, ref offset, out ulong size) || size > (ulong)(bytes.Length - offset)
                        || (field == 4 ? size != 32 : field == 8 ? size != 64 : size > MaxFailureBytes)) return false;
                    for (int i = 0; i < (int)size; i++)
                    {
                        byte value = bytes[offset + i];
                        if (field == 6 ? value < 0x21 || value > 0x7e : !Hex(value)) return false;
                    }
                    offset += (int)size;
                }
                else
                {
                    if (!Varint(bytes, ref offset, out ulong number) || number > uint.MaxValue) return false;
                    if (!status && field == 5 && number > 1) return false;
                    if (status && field == 5 && (number < 1 || number > (uint)Core.OnlineRunExitPhase.Rejected)) return false;
                    // 先校验原始整数，再转byte枚举，拒绝258被截断成FinalDeath的别名。
                    if (status && field == 9 && (number < 1 || number > (uint)Core.OnlineRunExitReason.FinalDeath)) return false;
                }
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
