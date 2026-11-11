using System;
using Core = Game.Core;
using Game.NetworkProtocol;
using Google.Protobuf;
using UnityEngine;

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
                ItemId = intent.ItemId ?? string.Empty, SourceWandId = intent.SourceWandId ?? string.Empty,
                SourceProgramRevision = intent.SourceProgramRevision, TargetWandId = intent.TargetWandId ?? string.Empty,
                TargetProgramRevision = intent.TargetProgramRevision, TargetSlot = intent.TargetSlot,
                ToSpellStorage = intent.ToSpellStorage, TransferBudget = intent.TransferBudget,
                AimOriginX = intent.AimOrigin.x, AimOriginY = intent.AimOrigin.y, AimOriginZ = intent.AimOrigin.z,
                AimDirectionX = intent.AimDirection.x, AimDirectionY = intent.AimDirection.y, AimDirectionZ = intent.AimDirection.z,
                WorldOwnerId = intent.WorldOwnerId ?? string.Empty, TargetBackpackId = intent.TargetBackpackId ?? string.Empty,
                GridX = intent.GridX, GridY = intent.GridY, QuarterTurns = intent.QuarterTurns, AutoPlace = intent.AutoPlace,
                AttachmentContainerId = intent.AttachmentContainerId ?? string.Empty,
                AttachmentDose = intent.AttachmentDose, AttachmentEnabled = intent.AttachmentEnabled,
                TargetLocationKind = intent.TargetLocationKind, WholeStack = intent.WholeStack
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
                TargetSlot = message.TargetSlot, ToSpellStorage = message.ToSpellStorage, TransferBudget = message.TransferBudget,
                AimOrigin = new Vector3(message.AimOriginX, message.AimOriginY, message.AimOriginZ),
                AimDirection = new Vector3(message.AimDirectionX, message.AimDirectionY, message.AimDirectionZ),
                WorldOwnerId = message.WorldOwnerId, TargetBackpackId = message.TargetBackpackId,
                GridX = message.GridX, GridY = message.GridY, QuarterTurns = message.QuarterTurns, AutoPlace = message.AutoPlace,
                AttachmentContainerId = message.AttachmentContainerId,
                AttachmentDose = message.AttachmentDose, AttachmentEnabled = message.AttachmentEnabled,
                TargetLocationKind = message.TargetLocationKind, WholeStack = message.WholeStack
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
                InventoryVersion = receipt.InventoryVersion, Failure = (uint)receipt.Failure,
                Transferred = receipt.Transferred, TransferFailure = (uint)receipt.TransferFailure
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
                InventoryVersion = message.InventoryVersion, Failure = (Core.OnlineInventoryCommandFailure)message.Failure,
                Transferred = message.Transferred, TransferFailure = (Core.MaterialTransferFailure)message.TransferFailure
            };
            if (!Valid(candidate)) return false;
            receipt = candidate; return true;
        }

        private static bool Valid(in Core.OnlineInventoryCommandIntent value)
        {
            if (value.CommandId == 0 || value.ConnectionEpoch == 0 || value.ExpectedInventoryVersion == 0
                || value.TargetSlot < 0 || value.TargetSlot > 255) return false;
            if (value.Kind == Core.OnlineInventoryCommandKind.BeginWandResearch
                || value.Kind == Core.OnlineInventoryCommandKind.EndWandResearch)
                // 开关只表达会话状态，不能夹带资产/射线/数量写入；DS仍检查Home与原空间规则。
                return string.IsNullOrEmpty(value.ItemId) && value.TargetSlot == 0
                    && string.IsNullOrEmpty(value.SourceWandId) && string.IsNullOrEmpty(value.TargetWandId)
                    && value.SourceProgramRevision == 0 && value.TargetProgramRevision == 0
                    && !value.ToSpellStorage && value.TransferBudget == 0 && Zero(value.AimOrigin) && Zero(value.AimDirection)
                    && NeutralGround(value) && string.IsNullOrEmpty(value.AttachmentContainerId)
                    && value.AttachmentDose == 0 && !value.AttachmentEnabled && value.TargetLocationKind == 0 && !value.WholeStack;
            if (!Id(value.ItemId)) return false;
            if ((uint)value.Kind >= 12 && (uint)value.Kind <= 15) return GeneralCommand(value);
            // 旧命令继续使用原来的位置表达。新增字段不能让相同Kind同时携带两套目的地/整堆语义。
            if (value.TargetLocationKind != 0 || value.WholeStack) return false;
            bool sourceEmpty = string.IsNullOrEmpty(value.SourceWandId), targetEmpty = string.IsNullOrEmpty(value.TargetWandId);
            if (value.Kind == Core.OnlineInventoryCommandKind.ConfigureSpellAttachment)
            {
                // 配置只描述具体卡的来源/剂量，不搬材料，也不夹带换杖、地面或几何语义。
                // Enabled=false的命令表示清除；Owner里已有的显式disabled状态仍由快照原样保留。
                if (value.TargetSlot != 0 || !sourceEmpty || !targetEmpty || value.SourceProgramRevision != 0
                    || value.TargetProgramRevision != 0 || value.ToSpellStorage || value.TransferBudget != 0
                    || !Zero(value.AimOrigin) || !Zero(value.AimDirection) || !NeutralGround(value)) return false;
                return value.AttachmentEnabled ? Id(value.AttachmentContainerId) && value.AttachmentDose > 0
                    : string.IsNullOrEmpty(value.AttachmentContainerId) && value.AttachmentDose == 0;
            }
            // 新字段不能被其它命令旁路提交，即使其它字段恰好构成合法的F/H/G/K或地面操作。
            if (!string.IsNullOrEmpty(value.AttachmentContainerId) || value.AttachmentDose != 0 || value.AttachmentEnabled) return false;
            bool drop = value.Kind == Core.OnlineInventoryCommandKind.DropContainer;
            if (drop || value.Kind == Core.OnlineInventoryCommandKind.PickupContainer)
            {
                // 地面意图不能夹带工具选择、搬卡或材料写结果。自动/手动装包是互斥表示；
                // 真实归属、形状/空间以及首拾竞争，由Run在版本校验和原子提交时再次裁决。
                if (value.TargetSlot != 0 || !sourceEmpty || !targetEmpty || value.SourceProgramRevision != 0
                    || value.TargetProgramRevision != 0 || value.ToSpellStorage || value.TransferBudget != 0
                    || !Zero(value.AimOrigin) || !Zero(value.AimDirection)) return false;
                if (drop) return NeutralGround(value);
                if (!Id(value.WorldOwnerId)) return false;
                return value.AutoPlace
                    ? string.IsNullOrEmpty(value.TargetBackpackId) && value.GridX == 0 && value.GridY == 0 && value.QuarterTurns == 0
                    : Id(value.TargetBackpackId) && value.GridX >= 0 && value.GridX <= 255 && value.GridY >= 0 && value.GridY <= 255
                        && value.QuarterTurns >= 0 && value.QuarterTurns <= 3;
            }
            if (!NeutralGround(value)) return false;
            if (value.Kind == Core.OnlineInventoryCommandKind.ReleaseSolid)
                // K只携带真实候选实例及库存版本，不给Client数量/射线/选槽写口。
                // 首个包内Material、最多100GMU和原落点都由DS按当前真实库存重新计算。
                return value.TargetSlot == 0 && sourceEmpty && targetEmpty && value.SourceProgramRevision == 0
                    && value.TargetProgramRevision == 0 && !value.ToSpellStorage && value.TransferBudget == 0
                    && Zero(value.AimOrigin) && Zero(value.AimDirection);
            if (value.Kind == Core.OnlineInventoryCommandKind.ThrowContainer)
                return value.TargetSlot < 7 && sourceEmpty && targetEmpty && value.SourceProgramRevision == 0
                    && value.TargetProgramRevision == 0 && !value.ToSpellStorage && value.TransferBudget == 0
                    && Finite(value.AimOrigin) && Unit(value.AimDirection);
            bool collect = value.Kind == Core.OnlineInventoryCommandKind.CollectMaterial;
            if (collect || value.Kind == Core.OnlineInventoryCommandKind.PourContainer)
            {
                // F/H只表达已绑定工具的数量/射线意图，不复用搬卡字段，也不接收客户端写入结果。
                // 这里限制数值规范与请求预算；真实绑定、Actor距离、采样/轨迹及事务授权仍由DS/Run裁决。
                return value.TargetSlot < 7 && sourceEmpty && targetEmpty && value.SourceProgramRevision == 0
                    && value.TargetProgramRevision == 0 && !value.ToSpellStorage && value.TransferBudget > 0
                    && value.TransferBudget <= (collect ? 4096UL : 1024UL) && Finite(value.AimOrigin) && Unit(value.AimDirection);
            }
            // 非F/H（含绑定）的数量/射线字段必须精确为0；不用Vector3近似==放行小脏值。
            if (value.TransferBudget != 0 || !Zero(value.AimOrigin) || !Zero(value.AimDirection)) return false;
            bool toolSlot = value.Kind == Core.OnlineInventoryCommandKind.SelectTool || value.Kind == Core.OnlineInventoryCommandKind.BindTool;
            // 选择/绑定只指向真实实例与稳定装备槽，不得夹带搬卡语义；真实携带Container及绑定由Run再验。
            // 共享规范空字段约束不表示绑定会自动选择；工具槽0..6也不能沿用PlaceCard的宽Wire槽预算。
            if (value.Kind == Core.OnlineInventoryCommandKind.SelectWand || toolSlot)
                return value.TargetSlot < (toolSlot ? 7 : 3) && sourceEmpty && targetEmpty && value.SourceProgramRevision == 0
                    && value.TargetProgramRevision == 0 && !value.ToSpellStorage;
            if (value.Kind != Core.OnlineInventoryCommandKind.PlaceCard) return false;

            // 空Source只表示Storage来源；非空必须携带所读程序版本，防止把一张过期卡拖入新程序。
            // 具体槽上限依赖共同Catalog，所以这里保留0..255的Wire预算，不能写死成程序容量。
            if (sourceEmpty ? value.SourceProgramRevision != 0 : !Id(value.SourceWandId) || value.SourceProgramRevision == 0) return false;
            return value.ToSpellStorage ? targetEmpty && value.TargetProgramRevision == 0
                : Id(value.TargetWandId) && value.TargetProgramRevision > 0;
        }

        /// <summary>
        /// 通用库存意图只校验有限、规范的Wire形状；不按ContentId猜物品Kind、不预判真实来源。
        /// SourceWandId/Revision成对保留拖卡时读到的程序版本，DS仍按当前归属重新核验并原子提交。
        /// 新命令以TargetLocationKind单一表达目标，不与旧ToSpellStorage并行解释同一个字段组。
        /// </summary>
        private static bool GeneralCommand(in Core.OnlineInventoryCommandIntent value)
        {
            if (value.ToSpellStorage || value.TransferBudget != 0 || !Zero(value.AimOrigin) || !Zero(value.AimDirection)
                || !string.IsNullOrEmpty(value.AttachmentContainerId) || value.AttachmentDose != 0 || value.AttachmentEnabled) return false;
            bool sourceEmpty = string.IsNullOrEmpty(value.SourceWandId);
            if (sourceEmpty ? value.SourceProgramRevision != 0 : !Id(value.SourceWandId) || value.SourceProgramRevision == 0) return false;
            if (value.Kind == Core.OnlineInventoryCommandKind.DropItem)
                return value.TargetLocationKind == 0 && value.TargetSlot == 0 && string.IsNullOrEmpty(value.TargetWandId)
                    && value.TargetProgramRevision == 0 && NeutralGround(value);
            if (value.Kind == Core.OnlineInventoryCommandKind.EquipWand)
                return sourceEmpty && !value.WholeStack && value.TargetLocationKind == 3 && value.TargetSlot < 3
                    && string.IsNullOrEmpty(value.TargetWandId) && value.TargetProgramRevision == 0 && NeutralGround(value);
            bool pickup = value.Kind == Core.OnlineInventoryCommandKind.PickupItem;
            if (pickup)
            {
                // 自动拾取不能夹带一个“隐式手动目标”；位置与空间由DS按原单机规则选择。
                if (!sourceEmpty || value.WholeStack || !Id(value.WorldOwnerId)) return false;
                if (value.AutoPlace)
                    return value.TargetLocationKind == 0 && value.TargetSlot == 0 && string.IsNullOrEmpty(value.TargetWandId)
                        && value.TargetProgramRevision == 0 && string.IsNullOrEmpty(value.TargetBackpackId)
                        && value.GridX == 0 && value.GridY == 0 && value.QuarterTurns == 0;
            }
            else if (value.Kind != Core.OnlineInventoryCommandKind.MoveOwned || !string.IsNullOrEmpty(value.WorldOwnerId) || value.AutoPlace) return false;
            if (value.TargetLocationKind == 1)
                return Id(value.TargetBackpackId) && value.TargetSlot == 0 && string.IsNullOrEmpty(value.TargetWandId)
                    && value.TargetProgramRevision == 0 && value.GridX >= 0 && value.GridX <= 255 && value.GridY >= 0
                    && value.GridY <= 255 && value.QuarterTurns >= 0 && value.QuarterTurns <= 3;
            if (!string.IsNullOrEmpty(value.TargetBackpackId) || value.GridX != 0 || value.GridY != 0 || value.QuarterTurns != 0) return false;
            if (value.TargetLocationKind == 2)
                return Id(value.TargetWandId) && value.TargetProgramRevision > 0 && value.TargetSlot < 32;
            if (!string.IsNullOrEmpty(value.TargetWandId) || value.TargetProgramRevision != 0) return false;
            // 拾取保留原单机“直接装备杖/包”的手动位置；同Owner移动杖走独立EquipWand命令。
            return value.TargetLocationKind == 7 || value.TargetLocationKind == 8 && value.TargetSlot < 7
                || pickup && (value.TargetLocationKind == 3 && value.TargetSlot < 3 || value.TargetLocationKind == 4 && value.TargetSlot == 0);
        }

        private static bool Valid(in Core.OnlineInventoryCommandReceipt value)
        {
            uint failure = (uint)value.Failure;
            uint transferFailure = (uint)value.TransferFailure;
            // 收据保留原事务失败与完整ulong数量；失败不能声称已提交数量，成功不能夹带事务失败。
            // 成功0继续兼容选择/移卡；具体F/H必须转移多少属于已执行命令的领域结果，不由Codec猜测。
            return value.CommandId > 0 && value.ConnectionEpoch > 0 && failure <= 11
                && transferFailure <= (uint)Core.MaterialTransferFailure.IncompatibleMaterial
                && (failure == 0 ? value.InventoryVersion > 0 && transferFailure == 0 : value.Transferred == 0);
        }

        private static bool Zero(Vector3 value) => value.x == 0f && value.y == 0f && value.z == 0f;
        private static bool NeutralGround(in Core.OnlineInventoryCommandIntent value)
            => string.IsNullOrEmpty(value.WorldOwnerId) && string.IsNullOrEmpty(value.TargetBackpackId)
                && value.GridX == 0 && value.GridY == 0 && value.QuarterTurns == 0 && !value.AutoPlace;
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Unit(Vector3 value)
        {
            if (!Finite(value)) return false;
            float squared = value.sqrMagnitude;
            // 仅允许float射线的常规单位长度误差，不归一化篡改原意图，也不新增Gameplay角度/距离政策。
            // 有限分量相乘仍可能溢出Infinity，双边范围比较会同时拒绝该情况。
            return squared >= .99f && squared <= 1.01f;
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
            int offset = 0, maximumField = receipt ? 8 : 31;
            ulong seen = 0;
            while (offset < bytes.Length)
            {
                if (!Varint(bytes, ref offset, out ulong tag) || tag > uint.MaxValue) return false;
                int field = (int)(tag >> 3);
                if (field < 1 || field > maximumField) return false;
                ulong bit = 1UL << field;
                if ((seen & bit) != 0) return false;
                seen |= bit;
                bool text = !receipt && (field == 7 || field == 8 || field == 10 || field == 21 || field == 22 || field == 27);
                bool scalarFloat = !receipt && field >= 15 && field <= 20;
                int expected = field == 2 || field == 3 ? 1 : text ? 2 : scalarFloat ? 5 : 0;
                if ((tag & 7) != (ulong)expected) return false;
                if (expected == 1 || expected == 5)
                {
                    int width = expected == 1 ? 8 : 4;
                    if (bytes.Length - offset < width) return false;
                    offset += width;
                }
                else if (text)
                {
                    if (!Varint(bytes, ref offset, out ulong length) || length > (ulong)(bytes.Length - offset)
                        || length != 0 && length != 32) return false;
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
                    if (!Varint(bytes, ref offset, out ulong value)) return false;
                    // 请求预算与已提交数量是真uint64；只对其它标量使用uint32门禁，不能在解析前截断GMU。
                    bool wide = receipt ? field == 7 : field == 14 || field == 28;
                    if (!wide && value > uint.MaxValue) return false;
                    // Core枚举底层是byte，必须在转换前验证原uint，避免257截成SelectWand=1、
                    // 259..264会截成已知命令，256会截成Failure.None。只查Core枚举值会漏掉这些别名。
                    if (field == 6 && (receipt ? value > 11 : value < 1 || value > 17)) return false;
                    if (receipt && field == 8 && value > (uint)Core.MaterialTransferFailure.IncompatibleMaterial) return false;
                    if (!receipt && (field == 12 && value > 255 || (field == 13 || field == 26 || field == 29 || field == 31) && value > 1
                        || (field == 23 || field == 24) && value > 255 || field == 25 && value > 3 || field == 30 && value > 8)) return false;
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
