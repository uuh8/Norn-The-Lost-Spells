using System;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>
    /// 认证后的独立世界身份握手：由 DS 分配 WorldEpoch，并绑定本次可信 Run/Dungeon/连接代次。
    /// 这里不认证 Ticket，也不授予 Gameplay 权限；调用者必须提供认证流程已经确认的独立身份，
    /// 不能从同一个未验证消息构造 expected 再与自身比较。玩法 Hash 继续由既有兼容 Gate 负责。
    /// 每次入场/重连只握手一次，允许短寿命消息与字符串分配；此 Codec 不进入材料 Tick 热路径。
    /// </summary>
    public sealed class WorldIdentityCodec
    {
        public const int MaxPayloadBytes = 256;

        /// <summary>
        /// 编码使用当前应用协议版本；WorldEpoch 与 RoomEpoch/ConnectionEpoch 分别标识世界生命期、
        /// 业务房间和连接生命期，重连不能以连接代次替代或重新推导世界身份。
        /// 先验证全部输入和输出容量，再写入 caller 借用段，失败时 written 保持零。
        /// </summary>
        public bool TryEncode(JoinConnectionIdentity identity, ulong worldEpoch, ArraySegment<byte> output, out int written)
        {
            written = 0;
            if (!ValidIdentity(identity) || worldEpoch == 0 || output.Array == null) return false;
            var message = new WorldIdentityMessage
            {
                ProtocolVersion = ProtocolCompatibility.CurrentVersion,
                RunId = identity.RunId,
                DungeonId = identity.DungeonId,
                RoomEpoch = identity.RoomEpoch,
                ConnectionEpoch = identity.ConnectionEpoch,
                WorldEpoch = worldEpoch
            };
            int size = message.CalculateSize();
            if (size < 1 || size > MaxPayloadBytes || size > output.Count) return false;
            message.WriteTo(new Span<byte>(output.Array, output.Offset, size));
            written = size;
            return true;
        }

        /// <summary>
        /// 接收段只在同步回调中借用。先以原始 Span 检查长度、字段形状与 ASCII ID，再构造 Protobuf，
        /// 避免恶意字符串或 unknown 字段先触发解析分配；重复字段也不能借 last-value-wins 隐藏歧义。
        /// 所有验证成功后才发布 worldEpoch，拒绝候选始终输出零，不污染后续正式世界的安装状态。
        /// </summary>
        public bool TryDecode(ArraySegment<byte> payload, JoinConnectionIdentity expected, out ulong worldEpoch)
        {
            worldEpoch = 0;
            if (!ValidIdentity(expected) || payload.Array == null || payload.Count < 1 || payload.Count > MaxPayloadBytes) return false;
            var bytes = new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count);
            if (!ValidWire(bytes)) return false;
            var message = new WorldIdentityMessage();
            try { message.MergeFrom(bytes); }
            catch (InvalidProtocolBufferException) { return false; }
            if (!ProtocolCompatibility.IsSupported(message.ProtocolVersion) || message.RunId != expected.RunId ||
                message.DungeonId != expected.DungeonId || message.RoomEpoch != expected.RoomEpoch ||
                message.ConnectionEpoch != expected.ConnectionEpoch || message.WorldEpoch == 0) return false;
            worldEpoch = message.WorldEpoch;
            return true;
        }

        private static bool ValidIdentity(JoinConnectionIdentity identity) => identity != null &&
            ValidRunId(identity.RunId) && ValidDungeonId(identity.DungeonId) && identity.RoomEpoch != 0 && identity.ConnectionEpoch != 0;

        private static bool ValidRunId(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int i = 0; i < value.Length; i++)
                if (!IsLowerHex(value[i])) return false;
            return true;
        }

        private static bool ValidDungeonId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32 || !IsLowerLetter(value[0])) return false;
            for (int i = 1; i < value.Length; i++)
                if (!IsDungeonTail(value[i])) return false;
            return true;
        }

        private static bool IsLowerLetter(int value) => value >= 'a' && value <= 'z';
        private static bool IsDigit(int value) => value >= '0' && value <= '9';
        private static bool IsLowerHex(int value) => IsDigit(value) || value >= 'a' && value <= 'f';
        private static bool IsDungeonTail(int value) => IsLowerLetter(value) || IsDigit(value) || value == '_' || value == '-';

        /// <summary>
        /// 固定六字段合同：1/4/5 为 uint32 varint，2/3 为有界 ASCII 字符串，6 为 fixed64。
        /// 禁止 unknown/重复/错误 wire type，且六字段必须全部出现；proto3 允许省略默认值，
        /// 但本握手没有合法零版本/零代次/空身份，不能把缺字段当成可安装世界的默认值。
        /// </summary>
        private static bool ValidWire(ReadOnlySpan<byte> bytes)
        {
            int offset = 0;
            uint seen = 0;
            while (offset < bytes.Length)
            {
                if (!TryReadVarint(bytes, ref offset, out ulong tag) || tag > uint.MaxValue) return false;
                int field = (int)(tag >> 3);
                if (field < 1 || field > 6 || (seen & 1u << field) != 0) return false;
                seen |= 1u << field;
                int wireType = field == 6 ? 1 : field == 2 || field == 3 ? 2 : 0;
                if ((tag & 7) != (ulong)wireType) return false;
                if (wireType == 1)
                {
                    if (bytes.Length - offset < 8) return false;
                    offset += 8;
                }
                else if (wireType == 2)
                {
                    if (!TryReadVarint(bytes, ref offset, out ulong size) || size > 32 || size > (ulong)(bytes.Length - offset) ||
                        (field == 2 ? size != 32 : size < 1)) return false;
                    for (int i = 0; i < (int)size; i++)
                    {
                        byte character = bytes[offset + i];
                        if (field == 2)
                        {
                            if (!IsLowerHex(character)) return false;
                        }
                        else if (i == 0 ? !IsLowerLetter(character) : !IsDungeonTail(character)) return false;
                    }
                    offset += (int)size;
                }
                else if (!TryReadVarint(bytes, ref offset, out ulong value) || value == 0 || value > uint.MaxValue) return false;
            }
            return seen == 0x7Eu;
        }

        /// <summary>
        /// 最多读取十字节且拒绝 ulong 溢出与多余零尾字节，避免相同数值具有多个可接受表示。
        /// 这里不调用 Protobuf Parser，也不创建字符串或集合；只有完整预检成功才进入实际解析。
        /// </summary>
        private static bool TryReadVarint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift <= 63; shift += 7)
            {
                if (offset >= bytes.Length) return false;
                byte next = bytes[offset++];
                if (shift == 63 && next > 1) return false;
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return shift == 0 || next != 0;
            }
            return false;
        }
    }
}
