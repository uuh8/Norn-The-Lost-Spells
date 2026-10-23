using System;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>
    /// N3平铺运动协议；只验证Wire/数值，不拥有身份、连接序号窗口或角色状态。
    /// 每实例预建两个接收对象；返回对象只借用到下一次同类Decode，且本实例不是线程安全的。
    /// </summary>
    public sealed class MovementCodec
    {
        public const int MaxPayloadBytes = 512;
        private const float MaxCounterSeconds = 86400f;
        private readonly MovementInputMessage _input = new MovementInputMessage();
        private readonly MovementStateMessage _state = new MovementStateMessage();

        // 位图仅描述本Schema的标量wire类型，不建立Dictionary或按消息分配数组。
        private const ulong InputFloatFields = (1UL << 4) | (1UL << 5) | (1UL << 6);
        private const ulong InputBoolFields = (1UL << 7) | (1UL << 8) | (1UL << 9) | (1UL << 10);
        private const ulong StateFloatFields = (1UL << 9) | (1UL << 10) | (1UL << 11) | (1UL << 12) |
            (1UL << 13) | (1UL << 14) | (1UL << 15) | (1UL << 16) | (1UL << 19) | (1UL << 20) |
            (1UL << 21) | (1UL << 22) | (1UL << 23) | (1UL << 24) | (1UL << 25) | (1UL << 26) |
            (1UL << 27) | (1UL << 28) | (1UL << 30) | (1UL << 31) | (1UL << 32) | (1UL << 33);
        private const ulong StateBoolFields = (1UL << 6) | (1UL << 7) | (1UL << 8) | (1UL << 18) | (1UL << 29) | (1UL << 36) | (1UL << 38);

        public bool TryDecodeInput(ArraySegment<byte> payload, out MovementInputMessage message)
        {
            message = null;
            _input.ResetForReuse();
            if (!HasPayload(payload)) return false;
            var bytes = new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count);
            if (!ValidateWire(bytes, true)) return false;
            try { _input.MergeFrom(bytes); }
            catch (InvalidProtocolBufferException) { _input.ResetForReuse(); return false; }
            if (!Validate(_input)) { _input.ResetForReuse(); return false; }
            message = _input;
            return true;
        }

        public bool TryDecodeState(ArraySegment<byte> payload, out MovementStateMessage message)
        {
            message = null;
            _state.ResetForReuse();
            if (!HasPayload(payload)) return false;
            var bytes = new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count);
            if (!ValidateWire(bytes, false)) return false;
            try { _state.MergeFrom(bytes); }
            catch (InvalidProtocolBufferException) { _state.ResetForReuse(); return false; }
            if (!Validate(_state)) { _state.ResetForReuse(); return false; }
            message = _state;
            return true;
        }

        public bool TryEncodeInput(MovementInputMessage message, ArraySegment<byte> destination, out int written)
        {
            written = 0;
            return Validate(message) && TryEncode(message, destination, true, out written);
        }

        public bool TryEncodeState(MovementStateMessage message, ArraySegment<byte> destination, out int written)
        {
            written = 0;
            return Validate(message) && TryEncode(message, destination, false, out written);
        }

        private static bool TryEncode(IMessage message, ArraySegment<byte> destination, bool input, out int written)
        {
            written = 0;
            if (destination.Array == null) return false;
            int size = message.CalculateSize();
            if (size < 1 || size > MaxPayloadBytes || size > destination.Count) return false;
            // 精确Span只写本条消息；失败时written仍为0，调用方不能发送工作缓冲区中的内容。
            var bytes = new Span<byte>(destination.Array, destination.Offset, size);
            message.WriteTo(bytes);
            // 本机调用方也可能给消息Merge过未知字段；编码后同样拒绝，不把UnknownFields偷偷发上网。
            if (!ValidateWire(bytes, input)) return false;
            written = size;
            return true;
        }

        private static bool HasPayload(ArraySegment<byte> payload) =>
            payload.Array != null && payload.Count >= 1 && payload.Count <= MaxPayloadBytes;

        private static bool Validate(MovementInputMessage m) => m != null &&
            ProtocolCompatibility.IsSupported(m.ProtocolVersion) && m.ConnectionEpoch != 0 && m.Sequence != 0 && m.SimulationHash != 0 &&
            Finite(m.MoveX) && m.MoveX >= -1 && m.MoveX <= 1 && Finite(m.MoveY) && m.MoveY >= -1 && m.MoveY <= 1 && Finite(m.ViewYaw);

        private static bool Validate(MovementStateMessage m) => m != null &&
            ProtocolCompatibility.IsSupported(m.ProtocolVersion) && m.ActorId >= 1 && m.ActorId <= 8 && m.ConnectionEpoch != 0 &&
            m.ServerTick != 0 && m.SnapshotSequence != 0 && m.SimulationHash != 0 && m.Locomotion <= 3 && m.ExtraJumpsRemaining <= 8 && m.AirDashesRemaining <= 8 &&
            Finite(m.PositionX) && Finite(m.PositionY) && Finite(m.PositionZ) && Finite(m.Yaw) && Finite(m.VerticalVelocity) &&
            Finite(m.TargetFacingX) && Finite(m.TargetFacingY) && Finite(m.TargetFacingZ) &&
            Finite(m.GroundNormalX) && Finite(m.GroundNormalY) && Finite(m.GroundNormalZ) &&
            Finite(m.GroundAngle) && m.GroundAngle >= 0 && m.GroundAngle <= 180 &&
            Counter(m.CoyoteTimeCounter) && Counter(m.JumpBufferCounter) && Counter(m.AttackBufferCounter) &&
            Counter(m.AttackCooldownCounter) && Counter(m.DashCooldownCounter) && Counter(m.DashBufferCounter) &&
            Finite(m.DashDirectionX) && Finite(m.DashDirectionY) && Finite(m.DashDirectionZ) &&
            Counter(m.DashElapsed) && m.DashElapsed >= 0;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        // 原运动计时器减dt后可略负；协议只挡住非有限值和不合理的超大时间，不改变既有运动语义。
        private static bool Counter(float value) => Finite(value) && value >= -MaxCounterSeconds && value <= MaxCounterSeconds;

        private static bool ValidateWire(ReadOnlySpan<byte> bytes, bool input)
        {
            int offset = 0;
            int maximumField = input ? 11 : 39;
            int hashField = input ? 11 : 37;
            ulong floats = input ? InputFloatFields : StateFloatFields;
            ulong booleans = input ? InputBoolFields : StateBoolFields;
            ulong seen = 0;
            while (offset < bytes.Length)
            {
                if (!ReadVarint(bytes, ref offset, out ulong tag) || tag > uint.MaxValue) return false;
                uint field = (uint)tag >> 3;
                if (field < 1 || field > maximumField) return false;
                ulong bit = 1UL << (int)field;
                // 普通Protobuf允许unknown与last-wins；N3同Build通道刻意收紧，避免恶意包建立UnknownFieldSet。
                if ((seen & bit) != 0) return false;
                seen |= bit;
                int expectedWire = (floats & bit) != 0 ? 5 : field == hashField ? 1 : 0;
                if (((uint)tag & 7) != expectedWire) return false;
                if (expectedWire != 0)
                {
                    int width = expectedWire == 5 ? 4 : 8;
                    if (bytes.Length - offset < width) return false;
                    offset += width;
                }
                else
                {
                    if (!ReadVarint(bytes, ref offset, out ulong value)) return false;
                    if ((booleans & bit) != 0) { if (value > 1) return false; }
                    else if ((input || field != 2) && value > uint.MaxValue) return false;
                }
            }
            return true;
        }

        private static bool ReadVarint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift <= 63; shift += 7)
            {
                if (offset >= bytes.Length) return false;
                byte next = bytes[offset++];
                // 第十字节只能贡献第64位；同时拒绝冗长0尾，保证uint/bool/tag都使用规范varint。
                if (shift == 63 && next > 1) return false;
                value |= (ulong)(next & 0x7F) << shift;
                if ((next & 0x80) == 0) return shift == 0 || next != 0;
            }
            return false;
        }
    }
}
