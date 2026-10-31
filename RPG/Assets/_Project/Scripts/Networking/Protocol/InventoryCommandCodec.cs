using System;
using Core = Game.Core;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>
    /// 把Core库存操作意图/结果映射到独立的512B Protobuf消息。这里只负责Wire形状、配置兼容性
    /// 与字段的一致性，不拥有玩家身份、库存或去重账本；这些权限必须来自当前认证连接和Run。
    /// 点击/拖拽是离散命令，允许创建短寿命消息，不能把本Codec当成每帧Mana同步的零分配通道。
    /// </summary>
    public sealed class InventoryCommandCodec
    {
        public const int MaxPayloadBytes = 512;

        /// <summary>
        /// in表示只读借用值合同，编码过程不改UI提交的意图。可选字符串null被规范为empty，
        /// 让Core默认struct与proto3默认值一致；身份、程序版本和目标含义仍须显式满足约束。
        /// </summary>
        public bool TryEncodeIntent(in Core.OnlineInventoryCommandIntent intent, ulong gameplayHash,
            ArraySegment<byte> output, out int written)
        {
            written = 0;
            if (gameplayHash == 0 || !Valid(intent) || output.Array == null) return false;
            var message = new InventoryCommandMessage
            {
                ProtocolVersion = ProtocolCompatibility.CurrentVersion, GameplayHash = gameplayHash,
                CommandId = intent.CommandId, ConnectionEpoch = intent.ConnectionEpoch,
                ExpectedInventoryVersion = intent.ExpectedInventoryVersion, Kind = (uint)intent.Kind,
                ItemId = intent.ItemId, SourceWandId = intent.SourceWandId ?? string.Empty,
                SourceProgramRevision = intent.SourceProgramRevision, TargetWandId = intent.TargetWandId ?? string.Empty,
                TargetProgramRevision = intent.TargetProgramRevision, TargetSlot = intent.TargetSlot,
                ToSpellStorage = intent.ToSpellStorage
            };
            return Encode(message, output, false, out written);
        }

        /// <summary>
        /// 先在Span上预检再MergeFrom，拒绝重复标量与未知字段，避免Protobuf的last-value-wins
        /// 隐藏命令篡改。expectedHash来自认证Run，不从本包反向创建信任；成功仍不代表已授权执行。
        /// </summary>
        public bool TryDecodeIntent(ArraySegment<byte> payload, ulong expectedHash,
            out Core.OnlineInventoryCommandIntent intent)
        {
            intent = default;
            if (expectedHash == 0 || !Payload(payload, false)) return false;
            var message = new InventoryCommandMessage();
            try { message.MergeFrom(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count)); }
            catch (InvalidProtocolBufferException) { return false; }
            if (!ProtocolCompatibility.IsSupported(message.ProtocolVersion) || message.GameplayHash != expectedHash) return false;
            var candidate = new Core.OnlineInventoryCommandIntent
            {
                CommandId = message.CommandId, ConnectionEpoch = message.ConnectionEpoch,
                ExpectedInventoryVersion = message.ExpectedInventoryVersion, Kind = (Core.OnlineInventoryCommandKind)message.Kind,
                ItemId = message.ItemId, SourceWandId = message.SourceWandId, SourceProgramRevision = message.SourceProgramRevision,
                TargetWandId = message.TargetWandId, TargetProgramRevision = message.TargetProgramRevision,
                TargetSlot = message.TargetSlot, ToSpellStorage = message.ToSpellStorage
            };
            if (!Valid(candidate)) return false;
            intent = candidate; return true;
        }

        /// <summary>
        /// 收据独立于完整资产快照。失败可能发生在Workspace尚未就绪时，因此失败允许版本0；
        /// 成功必须有真实非零资产版本，不能通过一个客户端提供的Success布尔值宣布资产变更。
        /// </summary>
        public bool TryEncodeReceipt(in Core.OnlineInventoryCommandReceipt receipt, ulong gameplayHash,
            ArraySegment<byte> output, out int written)
        {
            written = 0;
            if (gameplayHash == 0 || !Valid(receipt) || output.Array == null) return false;
            var message = new InventoryCommandReceiptMessage
            {
                ProtocolVersion = ProtocolCompatibility.CurrentVersion, GameplayHash = gameplayHash,
                CommandId = receipt.CommandId, ConnectionEpoch = receipt.ConnectionEpoch,
                InventoryVersion = receipt.InventoryVersion, Failure = (uint)receipt.Failure
            };
            return Encode(message, output, true, out written);
        }

        /// <summary>只恢复结果值；pending命令匹配、旧Epoch丢弃和刷新原库存UI由上层连接组合负责。</summary>
        public bool TryDecodeReceipt(ArraySegment<byte> payload, ulong expectedHash,
            out Core.OnlineInventoryCommandReceipt receipt)
        {
            receipt = default;
            if (expectedHash == 0 || !Payload(payload, true)) return false;
            var message = new InventoryCommandReceiptMessage();
            try { message.MergeFrom(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count)); }
            catch (InvalidProtocolBufferException) { return false; }
            if (!ProtocolCompatibility.IsSupported(message.ProtocolVersion) || message.GameplayHash != expectedHash) return false;
            var candidate = new Core.OnlineInventoryCommandReceipt
            {
                CommandId = message.CommandId, ConnectionEpoch = message.ConnectionEpoch,
                InventoryVersion = message.InventoryVersion, Failure = (Core.OnlineInventoryCommandFailure)message.Failure
            };
            if (!Valid(candidate)) return false;
            receipt = candidate; return true;
        }

        private static bool Valid(in Core.OnlineInventoryCommandIntent value)
        {
            if (value.CommandId == 0 || value.ConnectionEpoch == 0 || value.ExpectedInventoryVersion == 0
                || !Id(value.ItemId) || value.TargetSlot < 0 || value.TargetSlot > 255) return false;
            bool sourceEmpty = string.IsNullOrEmpty(value.SourceWandId), targetEmpty = string.IsNullOrEmpty(value.TargetWandId);
            if (value.Kind == Core.OnlineInventoryCommandKind.SelectWand)
                return value.TargetSlot < 3 && sourceEmpty && targetEmpty && value.SourceProgramRevision == 0
                    && value.TargetProgramRevision == 0 && !value.ToSpellStorage;
            if (value.Kind != Core.OnlineInventoryCommandKind.PlaceCard) return false;

            // 空Source只表示Storage来源；非空必须携带所读程序版本，防止把一张过期卡拖入新程序。
            // 具体槽上限依赖共同Catalog，所以这里保留0..255的Wire预算，不能写死成程序容量。
            if (sourceEmpty ? value.SourceProgramRevision != 0 : !Id(value.SourceWandId) || value.SourceProgramRevision == 0) return false;
            return value.ToSpellStorage ? targetEmpty && value.TargetProgramRevision == 0
                : Id(value.TargetWandId) && value.TargetProgramRevision > 0;
        }

        private static bool Valid(in Core.OnlineInventoryCommandReceipt value)
        {
            uint failure = (uint)value.Failure;
            return value.CommandId > 0 && value.ConnectionEpoch > 0 && failure <= 11
                && (failure != 0 || value.InventoryVersion > 0);
        }

        private static bool Id(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int i = 0; i < value.Length; i++)
                if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }

        private static bool Encode(IMessage message, ArraySegment<byte> output, bool receipt, out int written)
        {
            written = 0;
            int size = message.CalculateSize();
            if (size < 1 || size > MaxPayloadBytes || size > output.Count) return false;
            // CalculateSize先检查精确容量；WriteTo只接触当前消息的Span，written=0时调用者不能发缓冲区。
            var bytes = new Span<byte>(output.Array, output.Offset, size);
            message.WriteTo(bytes);
            if (!Wire(bytes, receipt)) return false;
            written = size; return true;
        }

        private static bool Payload(ArraySegment<byte> payload, bool receipt)
        {
            return payload.Array != null && payload.Count > 0 && payload.Count <= MaxPayloadBytes
                && Wire(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count), receipt);
        }

        private static bool Wire(ReadOnlySpan<byte> bytes, bool receipt)
        {
            int offset = 0, maximumField = receipt ? 6 : 13;
            ulong seen = 0;
            while (offset < bytes.Length)
            {
                if (!Varint(bytes, ref offset, out ulong tag) || tag > uint.MaxValue) return false;
                int field = (int)(tag >> 3);
                if (field < 1 || field > maximumField) return false;
                ulong bit = 1UL << field;
                if ((seen & bit) != 0) return false;
                seen |= bit;
                bool text = !receipt && (field == 7 || field == 8 || field == 10);
                int expected = field == 2 || field == 3 ? 1 : text ? 2 : 0;
                if ((tag & 7) != (ulong)expected) return false;
                if (expected == 1)
                {
                    if (bytes.Length - offset < 8) return false;
                    offset += 8;
                }
                else if (text)
                {
                    if (!Varint(bytes, ref offset, out ulong length) || length > (ulong)(bytes.Length - offset)
                        || (field == 7 ? length != 32 : length != 0 && length != 32)) return false;
                    // ID固定为ASCII小写hex。先查原字节，既限定UTF8输入，也不为恶意长字符串分配对象。
                    for (int i = 0; i < (int)length; i++)
                    {
                        byte value = bytes[offset + i];
                        if (!(value >= (byte)'0' && value <= (byte)'9') && !(value >= (byte)'a' && value <= (byte)'f')) return false;
                    }
                    offset += (int)length;
                }
                else
                {
                    if (!Varint(bytes, ref offset, out ulong value) || value > uint.MaxValue) return false;
                    // Core枚举底层是byte，必须在转换前验证原uint，避免257截成SelectWand=1、
                    // 256截成Failure.None=0。范围检查只放在Core值上会漏掉这种攻击。
                    if (field == 6 && (receipt ? value > 11 : value < 1 || value > 2)) return false;
                    if (!receipt && (field == 12 && value > 255 || field == 13 && value > 1)) return false;
                }
            }
            return true;
        }

        // Protobuf通常接受同值的冗长Varint；本命令入口只接受最短表示，避免多种字节形式绕过
        // 上层日志/去重诊断。第10字节最多1，终止字节不能是额外的0，截断立即拒绝。
        private static bool Varint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
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
