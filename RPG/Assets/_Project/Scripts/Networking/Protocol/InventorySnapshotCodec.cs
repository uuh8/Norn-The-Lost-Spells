using System;
using System.Collections.Generic;
using Core = Game.Core;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>
    /// 离散完整库存快照的 Wire Adapter。它只认识基础值与字节预算，不认识 Run 的库存服务、
    /// Actor 控制器或 Unity Scene；当前连接身份、目录、父链语义仍由认证组合层和 Run 再次验证。
    /// 完整快照允许离散分配并返回独立 DTO，不能放进每帧/每条 Mana 事实的热路径。
    /// </summary>
    public sealed class InventorySnapshotCodec
    {
        public const int MaxPayloadBytes = 64 * 1024;
        public const int MaxItems = 99;
        public const int MaxWands = 3;
        public const int MaxSpellUses = 96;
        public const int MaxAlwaysCast = 32;

        /// <summary>
        /// 先验证值与有限容量，再把 Core DTO 转成 Protobuf。调用者提供有界复用缓冲区；
        /// CalculateSize 在写入前决定是否放得下，失败时 written 必须为0，不能发送半包。
        /// </summary>
        public bool TryEncode(Core.OnlineInventoryPresentationSnapshot snapshot, ArraySegment<byte> output, out int written)
        {
            written = 0;
            if (output.Array == null || !Valid(snapshot)) return false;
            var message = ToWire(snapshot);
            int size = message.CalculateSize();
            if (size <= 0 || size > MaxPayloadBytes || size > output.Count) return false;
            var bytes = new Span<byte>(output.Array, output.Offset, size);
            message.WriteTo(bytes);
            // 编码也走同一结构校验，Schema/映射演进遗漏时直接拒绝，而不是输出接收端无法接受的包。
            if (!Wire(bytes, MessageKind.Root)) return false;
            written = size; return true;
        }

        /// <summary>
        /// 必须在 MergeFrom 前检查嵌套次数和长度。64KiB本身不足以阻止数万条空 repeated message
        /// 的构造；Span 预检不创建未知字段或 DTO，只有已证实有界的消息才进入 Protobuf Parser。
        /// 返回的 DTO 归调用者持有，不借用下次解码的内部缓存，可交给原子只读 Source 安装。
        /// </summary>
        public bool TryDecode(ArraySegment<byte> payload, out Core.OnlineInventoryPresentationSnapshot snapshot)
        {
            snapshot = null;
            if (payload.Array == null || payload.Count <= 0 || payload.Count > MaxPayloadBytes) return false;
            var bytes = new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count);
            if (!Wire(bytes, MessageKind.Root)) return false;
            var message = new InventorySnapshotMessage();
            try { message.MergeFrom(bytes); }
            catch (InvalidProtocolBufferException) { return false; }
            if (!ProtocolCompatibility.IsSupported(message.ProtocolVersion) || message.Assets == null) return false;
            var candidate = ToCore(message);
            if (!Valid(candidate)) return false;
            snapshot = candidate; return true;
        }

        private static bool Valid(Core.OnlineInventoryPresentationSnapshot value)
        {
            if (value == null || value.SchemaVersion != Core.OnlineInventoryPresentationSnapshot.CurrentSchemaVersion
                || value.ConnectionEpoch == 0 || value.SnapshotSequence == 0 || value.ResourceVersion == 0
                || value.SelectionVersion == 0 || value.SpellUseVersion == 0 || value.Assets == null
                || value.Wands == null || value.Wands.Length < 1 || value.Wands.Length > MaxWands
                || value.SpellUses == null || value.SpellUses.Length > MaxSpellUses) return false;
            var assets = value.Assets;
            if (assets.SchemaVersion != Core.OnlineOwnedAssetSnapshot.CurrentSchemaVersion || !Id(assets.RunId) || !Id(assets.PlayerId)
                || !Text(assets.InventoryId, 96) || !Hash(assets.GameplayConfigHash) || !Id(assets.SelectedWandInstanceId)
                || assets.ActorId < 1 || assets.ActorId > 4 || assets.InventoryVersion == 0 || assets.Items == null
                || assets.Items.Length < 1 || assets.Items.Length > MaxItems) return false;

            // 这里只做无目录的身份/闭包形状检查，不从 ContentId 推断权限、固定属性或可执行法术。
            // Dictionary/HashSet 只在离散完整快照中创建；高频读数必须走单独的值更新通道。
            var items = new Dictionary<string, Core.OnlineOwnedItemSnapshot>(assets.Items.Length, StringComparer.Ordinal);
            int wandCount = 0, spellCount = 0;
            for (int i = 0; i < assets.Items.Length; i++)
            {
                var item = assets.Items[i];
                if (item == null || !Id(item.InstanceId) || !Text(item.ContentId, 128) || item.ParentInstanceId == null
                    || item.Revision == 0 || item.RemainingCharges < 0 || item.X < 0 || item.Y < 0 || item.QuarterTurns < 0
                    || item.QuarterTurns > 3 || !items.TryAdd(item.InstanceId, item)) return false;
                if (item.LocationKind == 3)
                {
                    if (item.ParentInstanceId.Length != 0 || item.Slot < 0 || item.Slot >= MaxWands
                        || item.UseTemplateWandStats && item.WandStats != null
                        || !item.UseTemplateWandStats && !Stats(item.WandStats)) return false;
                    wandCount++;
                }
                else if (item.LocationKind == 2)
                {
                    if (!Id(item.ParentInstanceId) || item.Slot < 0 || item.Slot >= 32
                        || item.UseTemplateWandStats || item.WandStats != null) return false;
                    spellCount++;
                }
                else if (item.LocationKind == 7)
                {
                    // SpellStorage是Run中已有的玩家根位置，不是一个伪造的Bag物品。
                    // Wire只约束有限槽预算；该ContentId是否真是Spell、目录实际容量与槽冲突由Run复核。
                    if (item.ParentInstanceId.Length != 0 || item.Slot < 0 || item.Slot > 255
                        || item.UseTemplateWandStats || item.WandStats != null) return false;
                    spellCount++;
                }
                else return false; // 尚无真实 Bag 数据，不接受平面 Backpack 或任意未知位置编号。
            }
            if (wandCount != value.Wands.Length || spellCount != value.SpellUses.Length
                || !items.TryGetValue(assets.SelectedWandInstanceId, out var selected) || selected.LocationKind != 3) return false;
            for (int i = 0; i < assets.Items.Length; i++)
            {
                var item = assets.Items[i];
                if (item.LocationKind == 2 && (!items.TryGetValue(item.ParentInstanceId, out var parent) || parent.LocationKind != 3)) return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < value.Wands.Length; i++)
            {
                var wand = value.Wands[i];
                if (wand == null || !Id(wand.InstanceId) || !seen.Add(wand.InstanceId) || wand.ProgramRevision == 0
                    || !NonNegative(wand.CurrentMana) || !NonNegative(wand.CooldownRemaining)
                    || !items.TryGetValue(wand.InstanceId, out var item) || item.LocationKind != 3) return false;
            }
            seen.Clear();
            for (int i = 0; i < value.SpellUses.Length; i++)
            {
                var use = value.SpellUses[i];
                if (use == null || !Id(use.InstanceId) || !seen.Add(use.InstanceId)
                    || !items.TryGetValue(use.InstanceId, out var item)
                    || item.LocationKind != 2 && item.LocationKind != 7) return false;
            }
            return true;
        }

        private static bool Stats(Core.OnlineOwnedWandStatsSnapshot stats)
        {
            if (stats == null || !NonNegative(stats.MaxMana) || stats.MaxMana == 0 || !NonNegative(stats.ManaRegenPerSecond)
                || !NonNegative(stats.BaseCastDelay) || !NonNegative(stats.BaseReloadTime) || !NonNegative(stats.BaseSpreadDegrees)
                || stats.ProgramCapacity < 1 || stats.ProgramCapacity > 32 || stats.BaseDraws < 1 || stats.BaseDraws > stats.ProgramCapacity
                || stats.AlwaysCastContentIds == null || stats.AlwaysCastContentIds.Length > MaxAlwaysCast) return false;
            for (int i = 0; i < stats.AlwaysCastContentIds.Length; i++) if (!Text(stats.AlwaysCastContentIds[i], 128)) return false;
            return true;
        }
        private static bool NonNegative(float value) => float.IsFinite(value) && value >= 0 && value <= 1000000f;
        private static bool Id(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int i = 0; i < value.Length; i++)
                if (!(value[i] >= '0' && value[i] <= '9' || value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }
        private static bool Hash(string value)
        {
            if (value == null || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
                if (!(value[i] >= '0' && value[i] <= '9' || value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }
        private static bool Text(string value, int limit)
        {
            if (string.IsNullOrEmpty(value) || value.Length > limit) return false;
            // 当前身份/ContentId合同都是ASCII，不承载DisplayName。逐字符拒绝控制符和非法UTF8替代字符，
            // 保证“字节预算”与实际字符数一致；中文名称由本地受控目录提供。
            for (int i = 0; i < value.Length; i++) if (value[i] < 33 || value[i] > 126) return false;
            return true;
        }

        private static InventorySnapshotMessage ToWire(Core.OnlineInventoryPresentationSnapshot source)
        {
            var result = new InventorySnapshotMessage
            {
                ProtocolVersion = ProtocolCompatibility.CurrentVersion, SchemaVersion = source.SchemaVersion,
                ConnectionEpoch = source.ConnectionEpoch, SnapshotSequence = source.SnapshotSequence, ServerTick = source.ServerTick,
                ResourceVersion = source.ResourceVersion, SelectionVersion = source.SelectionVersion, SpellUseVersion = source.SpellUseVersion,
                Assets = new InventoryOwnedAssetMessage
                {
                    SchemaVersion = source.Assets.SchemaVersion, RunId = source.Assets.RunId, PlayerId = source.Assets.PlayerId,
                    InventoryId = source.Assets.InventoryId, GameplayConfigHash = source.Assets.GameplayConfigHash,
                    SelectedWandInstanceId = source.Assets.SelectedWandInstanceId, ActorId = source.Assets.ActorId, InventoryVersion = source.Assets.InventoryVersion
                }
            };
            for (int i = 0; i < source.Assets.Items.Length; i++)
            {
                var item = source.Assets.Items[i];
                result.Assets.Items.Add(new InventoryOwnedItemMessage
                {
                    InstanceId = item.InstanceId, ContentId = item.ContentId, ParentInstanceId = item.ParentInstanceId,
                    Revision = item.Revision, LocationKind = item.LocationKind, Slot = item.Slot, X = item.X, Y = item.Y,
                    QuarterTurns = item.QuarterTurns, RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                    UseTemplateWandStats = item.UseTemplateWandStats, WandStats = ToWire(item.WandStats)
                });
            }
            for (int i = 0; i < source.Wands.Length; i++)
            {
                var current = source.Wands[i]; result.Wands.Add(new InventoryWandPresentationMessage
                { InstanceId = current.InstanceId, ProgramRevision = current.ProgramRevision, CurrentMana = current.CurrentMana, CooldownRemaining = current.CooldownRemaining });
            }
            for (int i = 0; i < source.SpellUses.Length; i++)
                result.SpellUses.Add(new InventorySpellUsePresentationMessage { InstanceId = source.SpellUses[i].InstanceId, Eligible = source.SpellUses[i].Eligible });
            return result;
        }
        private static InventoryOwnedWandStatsMessage ToWire(Core.OnlineOwnedWandStatsSnapshot source)
        {
            if (source == null) return null;
            var result = new InventoryOwnedWandStatsMessage
            {
                MaxMana = source.MaxMana, ManaRegenPerSecond = source.ManaRegenPerSecond, BaseCastDelay = source.BaseCastDelay,
                BaseReloadTime = source.BaseReloadTime, BaseSpreadDegrees = source.BaseSpreadDegrees, ProgramCapacity = source.ProgramCapacity,
                BaseDraws = source.BaseDraws, Shuffle = source.Shuffle
            };
            result.AlwaysCastContentIds.Add(source.AlwaysCastContentIds); return result;
        }
        private static Core.OnlineInventoryPresentationSnapshot ToCore(InventorySnapshotMessage source)
        {
            var assets = source.Assets;
            var result = new Core.OnlineInventoryPresentationSnapshot
            {
                SchemaVersion = source.SchemaVersion, ConnectionEpoch = source.ConnectionEpoch, SnapshotSequence = source.SnapshotSequence,
                ServerTick = source.ServerTick, ResourceVersion = source.ResourceVersion, SelectionVersion = source.SelectionVersion,
                SpellUseVersion = source.SpellUseVersion,
                Assets = new Core.OnlineOwnedAssetSnapshot
                {
                    SchemaVersion = assets.SchemaVersion, RunId = assets.RunId, PlayerId = assets.PlayerId, InventoryId = assets.InventoryId,
                    GameplayConfigHash = assets.GameplayConfigHash, SelectedWandInstanceId = assets.SelectedWandInstanceId,
                    ActorId = assets.ActorId, InventoryVersion = assets.InventoryVersion, Items = new Core.OnlineOwnedItemSnapshot[assets.Items.Count]
                },
                Wands = new Core.OnlineInventoryWandPresentationSnapshot[source.Wands.Count],
                SpellUses = new Core.OnlineInventorySpellUsePresentationSnapshot[source.SpellUses.Count]
            };
            for (int i = 0; i < assets.Items.Count; i++)
            {
                var item = assets.Items[i]; result.Assets.Items[i] = new Core.OnlineOwnedItemSnapshot
                {
                    InstanceId = item.InstanceId, ContentId = item.ContentId, ParentInstanceId = item.ParentInstanceId, Revision = item.Revision,
                    LocationKind = item.LocationKind, Slot = item.Slot, X = item.X, Y = item.Y, QuarterTurns = item.QuarterTurns,
                    RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                    UseTemplateWandStats = item.UseTemplateWandStats, WandStats = ToCore(item.WandStats)
                };
            }
            for (int i = 0; i < source.Wands.Count; i++)
            {
                var current = source.Wands[i]; result.Wands[i] = new Core.OnlineInventoryWandPresentationSnapshot
                { InstanceId = current.InstanceId, ProgramRevision = current.ProgramRevision, CurrentMana = current.CurrentMana, CooldownRemaining = current.CooldownRemaining };
            }
            for (int i = 0; i < source.SpellUses.Count; i++)
                result.SpellUses[i] = new Core.OnlineInventorySpellUsePresentationSnapshot { InstanceId = source.SpellUses[i].InstanceId, Eligible = source.SpellUses[i].Eligible };
            return result;
        }
        private static Core.OnlineOwnedWandStatsSnapshot ToCore(InventoryOwnedWandStatsMessage source)
        {
            if (source == null) return null;
            var result = new Core.OnlineOwnedWandStatsSnapshot
            {
                MaxMana = source.MaxMana, ManaRegenPerSecond = source.ManaRegenPerSecond, BaseCastDelay = source.BaseCastDelay,
                BaseReloadTime = source.BaseReloadTime, BaseSpreadDegrees = source.BaseSpreadDegrees, ProgramCapacity = source.ProgramCapacity,
                BaseDraws = source.BaseDraws, Shuffle = source.Shuffle, AlwaysCastContentIds = new string[source.AlwaysCastContentIds.Count]
            };
            source.AlwaysCastContentIds.CopyTo(result.AlwaysCastContentIds, 0); return result;
        }

        private enum MessageKind : byte { Root, Assets, Item, Stats, Wand, SpellUse }
        private static bool Wire(ReadOnlySpan<byte> bytes, MessageKind kind)
        {
            int offset = 0, repeatedA = 0, repeatedB = 0; ulong seen = 0;
            int maximum = kind == MessageKind.Root ? 11 : kind == MessageKind.Assets || kind == MessageKind.Stats ? 9
                : kind == MessageKind.Item ? 13 : kind == MessageKind.Wand ? 4 : 2;
            while (offset < bytes.Length)
            {
                if (!Varint(bytes, ref offset, out ulong tag) || tag > uint.MaxValue) return false;
                int field = (int)(tag >> 3); if (field < 1 || field > maximum) return false;
                bool repeated = kind == MessageKind.Root && (field == 10 || field == 11)
                    || kind == MessageKind.Assets && field == 9 || kind == MessageKind.Stats && field == 9;
                if (!repeated) { ulong bit = 1UL << field; if ((seen & bit) != 0) return false; seen |= bit; }
                else if (kind == MessageKind.Root && field == 11) { if (++repeatedB > MaxSpellUses) return false; }
                else if (++repeatedA > (kind == MessageKind.Root ? MaxWands : kind == MessageKind.Assets ? MaxItems : MaxAlwaysCast)) return false;

                bool text = kind == MessageKind.Assets && field >= 2 && field <= 6 || kind == MessageKind.Item && field <= 3
                    || kind == MessageKind.Stats && field == 9 || (kind == MessageKind.Wand || kind == MessageKind.SpellUse) && field == 1;
                bool nested = kind == MessageKind.Root && field >= 9 || kind == MessageKind.Assets && field == 9 || kind == MessageKind.Item && field == 13;
                bool scalarFloat = kind == MessageKind.Stats && field <= 5 || kind == MessageKind.Wand && field >= 3;
                int expected = text || nested ? 2 : scalarFloat ? 5 : kind == MessageKind.Root && field == 4 ? 1 : 0;
                if ((tag & 7) != (ulong)expected) return false;
                if (expected == 2)
                {
                    if (!Varint(bytes, ref offset, out ulong length) || length > (ulong)(bytes.Length - offset)) return false;
                    if (text)
                    {
                        int limit = kind == MessageKind.Assets ? field == 4 ? 96 : field == 5 ? 64 : 32
                            : kind == MessageKind.Item && field == 2 || kind == MessageKind.Stats ? 128 : 32;
                        if (length > (ulong)limit) return false;
                    }
                    else
                    {
                        // 嵌套Schema的最大深度固定为Root→Assets→Item→Stats，不接受消息自身递归。
                        MessageKind child = kind == MessageKind.Root ? field == 9 ? MessageKind.Assets : field == 10 ? MessageKind.Wand : MessageKind.SpellUse
                            : kind == MessageKind.Assets ? MessageKind.Item : MessageKind.Stats;
                        if (!Wire(bytes.Slice(offset, (int)length), child)) return false;
                    }
                    offset += (int)length;
                }
                else if (expected != 0)
                { int width = expected == 5 ? 4 : 8; if (bytes.Length - offset < width) return false; offset += width; }
                else
                {
                    if (!Varint(bytes, ref offset, out ulong number) || number > uint.MaxValue) return false;
                    bool boolean = kind == MessageKind.Item && (field == 11 || field == 12)
                        || kind == MessageKind.Stats && field == 8 || kind == MessageKind.SpellUse && field == 2;
                    if (boolean && number > 1) return false;
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
