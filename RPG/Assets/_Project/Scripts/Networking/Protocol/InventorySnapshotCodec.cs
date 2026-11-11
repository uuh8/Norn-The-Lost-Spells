using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Core = Game.Core;
using Game.NetworkProtocol;
using Google.Protobuf;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// 离散完整库存快照的 Wire Adapter。它只认识基础值与字节预算，不认识 Run 的库存服务、
    /// Actor 控制器或 Unity Scene；当前连接身份、目录、父链语义仍由认证组合层和 Run 再次验证。
    /// 完整快照允许离散分配并返回独立 DTO，不能放进每帧/每条 Mana 事实的热路径。
    /// </summary>
    public sealed class InventorySnapshotCodec
    {
        // 完整DTO总上限包含Owner原20MiB预算及最多16384个地面真实实例（根与杖内卡合计）；
        // 通用Item不会得到一份独立扩容预算，各repeated及完整消息仍须同时有界。
        // 最大数组不常驻：编码按CalculateSize分配、接收按已验证TotalBytes分配，发送队列仍只保留两份。
        // 它不是Mirror单条消息上限；组合层必须先放入有界队列，再拆成可靠小页发送。
        public const int MaxPayloadBytes = 32 * 1024 * 1024;
        public const int MaxItems = 4096;
        public const int MaxGroundItems = 4 * MaxItems;
        public const int MaxGroundCoins = Core.OnlineGroundInventorySnapshot.MaximumCoins;
        public const int MaxWands = 3; // 装备槽数保持原三槽，与所有随身杖的资源条目数分开。
        public const int MaxWandStates = MaxItems;
        public const int MaxSpellUses = 4096;
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
        /// 离散发送队列使用的冷路径：先验证完整闭包并CalculateSize，只按实际长度分配一次字节数组。
        /// Codec不保留32MiB最大缓冲，也不把候选数组提前交给调用者；失败时payload始终为null。
        /// 高频Mana/CD仍走资源事实通道，不应逐帧调用这里创建完整对象图。
        /// </summary>
        public bool TryEncode(Core.OnlineInventoryPresentationSnapshot snapshot, out byte[] payload)
        {
            payload = null;
            if (!Valid(snapshot)) return false;
            var message = ToWire(snapshot);
            int size = message.CalculateSize();
            if (size <= 0 || size > MaxPayloadBytes) return false;
            var candidate = new byte[size];
            message.WriteTo(candidate.AsSpan());
            if (!Wire(candidate, MessageKind.Root)) return false;
            payload = candidate; return true;
        }

        /// <summary>
        /// 必须在 MergeFrom 前检查嵌套次数和长度。总字节预算本身不足以阻止数万条空 repeated message
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
                || value.SelectionVersion == 0 || value.SpellUseVersion == 0 || value.SelectedToolSlotPlusOne > 7 || value.Assets == null
                || value.Wands == null || value.Wands.Length > MaxWandStates
                || value.SpellUses == null || value.SpellUses.Length > MaxSpellUses || !Research(value)) return false;
            var assets = value.Assets;
            if (assets.SchemaVersion != Core.OnlineOwnedAssetSnapshot.CurrentSchemaVersion || !Id(assets.RunId) || !Id(assets.PlayerId)
                || !Text(assets.InventoryId, 96) || !Hash(assets.GameplayConfigHash)
                || assets.SelectedWandInstanceId != string.Empty && !Id(assets.SelectedWandInstanceId)
                || assets.ActorId < 1 || assets.ActorId > 4 || assets.InventoryVersion == 0 || assets.Gold < 0 || assets.CurrencyVersion == 0 || assets.Items == null
                || assets.Items.Length > MaxItems) return false;

            // 这里只做无目录的身份/闭包形状检查，不从 ContentId 推断权限、固定属性或可执行法术。
            // Dictionary/HashSet 只在离散完整快照中创建；高频读数必须走单独的值更新通道。
            var items = new Dictionary<string, Core.OnlineOwnedItemSnapshot>(assets.Items.Length, StringComparer.Ordinal);
            var slots = new HashSet<(int Kind, string Parent, int Slot)>();
            int wandCount = 0, equippedWandCount = 0;
            for (int i = 0; i < assets.Items.Length; i++)
            {
                var item = assets.Items[i];
                if (item == null || !Id(item.InstanceId) || !Text(item.ContentId, 128) || item.ParentInstanceId == null
                    || item.Revision == 0 || item.RemainingCharges < 0 || item.X < 0 || item.Y < 0 || item.QuarterTurns < 0
                    || item.QuarterTurns > 3 || !items.TryAdd(item.InstanceId, item)
                    || item.EntryBoundBackpack && item.LocationKind != 4
                    || item.UseTemplateWandStats && item.WandStats != null
                    || item.WandStats != null && !Stats(item.WandStats)
                    || item.ContainerState != null && (!Container(item.ContainerState) || IsWand(item))
                    || item.MaterialState != null && (!Material(item.MaterialState) || IsWand(item) || item.ContainerState != null)
                    || item.AttachmentState != null && (!Attachment(item.AttachmentState) || IsWand(item)
                        || item.ContainerState != null || item.MaterialState != null
                        || item.LocationKind != 1 && item.LocationKind != 2 && item.LocationKind != 7)) return false;
                // 根装备与程序/专区槽是规范位置：XY/旋转不能夹带第二套位置语义。
                // Backpack的Slot=-1表示网格，不占唯一槽；同XY也可能是合法堆叠，留给有目录的Run复核。
                if (item.LocationKind != 1 && (item.X != 0 || item.Y != 0 || item.QuarterTurns != 0
                    || !slots.Add((item.LocationKind, item.ParentInstanceId, item.Slot)))) return false;
                if (item.LocationKind == 3)
                {
                    if (item.ParentInstanceId.Length != 0 || item.Slot < 0 || item.Slot >= MaxWands
                        || !IsWand(item) || item.ContainerState != null || item.MaterialState != null) return false;
                    equippedWandCount++;
                }
                else if (item.LocationKind == 1)
                {
                    if (!Id(item.ParentInstanceId) || item.Slot != -1) return false;
                }
                else if (item.LocationKind == 2)
                {
                    if (!Id(item.ParentInstanceId) || item.Slot < 0 || item.Slot >= 32
                        || IsWand(item) || item.ContainerState != null || item.MaterialState != null) return false;
                }
                else if (item.LocationKind == 4)
                {
                    if (item.ParentInstanceId.Length != 0 || item.Slot != 0 || IsWand(item) || item.ContainerState != null || item.MaterialState != null) return false;
                }
                else if (item.LocationKind == 8)
                {
                    if (item.ParentInstanceId.Length != 0 || item.Slot < 0 || item.Slot >= 7 || IsWand(item)) return false;
                }
                else if (item.LocationKind == 7)
                {
                    // SpellStorage是Run中已有的玩家根位置，不是一个伪造的Bag物品。
                    // Wire只约束有限槽预算；该ContentId是否真是Spell、目录实际容量与槽冲突由Run复核。
                    if (item.ParentInstanceId.Length != 0 || item.Slot < 0 || item.Slot > 255
                        || IsWand(item) || item.ContainerState != null || item.MaterialState != null) return false;
                }
                else return false;
                if (IsWand(item)) wandCount++;
            }
            // 所有随身杖（包括包内备用杖）都已有真实执行状态；资源数组必须逐实例完整覆盖，
            // 才能编辑备用杖安装卡时使用真实ProgramRevision。可读状态不授予装备或选中资格。
            // 空手仍可携带备用杖；没有装备杖时Selected必须为空，不能为通过Codec补默认杖或满Mana。
            if (wandCount != value.Wands.Length || (equippedWandCount == 0 ? assets.SelectedWandInstanceId.Length != 0
                : !items.TryGetValue(assets.SelectedWandInstanceId, out var selected) || selected.LocationKind != 3)) return false;
            for (int i = 0; i < assets.Items.Length; i++)
            {
                var item = assets.Items[i];
                // 此闭包只允许装备包→包内项→备用杖安装卡，或装备杖→安装卡。
                // 固定深度关系排除孤儿/环，不按数组顺序决定父实例是否存在，也不靠ContentId猜物品类别。
                if (item.LocationKind == 1 && (!items.TryGetValue(item.ParentInstanceId, out var bag) || bag.LocationKind != 4)) return false;
                if (item.LocationKind == 2 && (!items.TryGetValue(item.ParentInstanceId, out var parent)
                    || parent.LocationKind != 3 && (parent.LocationKind != 1 || !IsWand(parent)))) return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < value.Wands.Length; i++)
            {
                var wand = value.Wands[i];
                if (wand == null || !Id(wand.InstanceId) || !seen.Add(wand.InstanceId) || wand.ProgramRevision == 0
                    || !NonNegative(wand.CurrentMana) || !NonNegative(wand.CooldownRemaining)
                    || !items.TryGetValue(wand.InstanceId, out var item) || !IsWand(item)
                    || item.LocationKind != 3 && item.LocationKind != 1) return false;
            }
            seen.Clear();
            for (int i = 0; i < value.SpellUses.Length; i++)
            {
                var use = value.SpellUses[i];
                if (use == null || !Id(use.InstanceId) || !seen.Add(use.InstanceId)
                    || !items.TryGetValue(use.InstanceId, out var item)
                    || item.LocationKind != 2 && item.LocationKind != 7
                        && (item.LocationKind != 1 || IsWand(item) || item.ContainerState != null || item.MaterialState != null)) return false;
            }
            // 无ItemKind字段时，只有安装卡/专区卡能由位置确定为必须给资格的项。
            // 包内资格是可选关联；真正ContentId是否Spell、容量/shape/mask等仍由Run严格验证。
            for (int i = 0; i < assets.Items.Length; i++)
                if ((assets.Items[i].LocationKind == 2 || assets.Items[i].LocationKind == 7)
                    && !seen.Contains(assets.Items[i].InstanceId)) return false;
            return Ground(value.Ground, items);
        }

        private static bool Ground(Core.OnlineGroundInventorySnapshot ground, Dictionary<string, Core.OnlineOwnedItemSnapshot> owned)
        {
            // null保留原隔离快照语义；有对象则必须是已知版本。空数组表示当前地面确实为空。
            if (ground == null) return true;
            if (ground.Version == 0 || ground.Items == null || ground.Items.Length > MaxGroundItems
                || ground.Coins == null || ground.Coins.Length > MaxGroundCoins) return false;
            // Coin不挤占Item身份或目录，也不产生Owner钱包操作；12个原NPC稳定身份用固定掩码即可去重。
            ushort coinIdentities = 0;
            for (int i = 0; i < ground.Coins.Length; i++)
            {
                var coin = ground.Coins[i];
                if (coin.Id < 5 || coin.Id > 16 || coin.Amount <= 0 || !Finite(coin.Position)) return false;
                int bit = 1 << (int)(coin.Id - 5);
                if ((coinIdentities & bit) != 0) return false;
                coinIdentities |= (ushort)bit;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal); int total = 0;
            for (int i = 0; i < ground.Items.Length; i++)
            {
                var envelope = ground.Items[i];
                if (envelope == null || !Id(envelope.WorldOwnerId) || !Finite(envelope.Position) || !Unit(envelope.Rotation)
                    || envelope.Item == null || envelope.Children == null || envelope.Children.Length > 32
                    || envelope.Children.Length >= MaxGroundItems - total) return false;
                total += 1 + envelope.Children.Length;
                var item = envelope.Item;
                if (!GroundItemState(item) || item.LocationKind != 5 || item.ParentInstanceId.Length != 0 || item.Slot != -1
                    || envelope.InstanceId != item.InstanceId || envelope.ContentId != item.ContentId || envelope.Revision != item.Revision
                    || !Same(envelope.ContainerState, item.ContainerState) || !Same(envelope.MaterialState, item.MaterialState)
                    || owned.ContainsKey(item.InstanceId) || !seen.Add(item.InstanceId)
                    || envelope.Children.Length != 0 && !IsWand(item)) return false;
                uint occupied = 0;
                for (int c = 0; c < envelope.Children.Length; c++)
                {
                    var child = envelope.Children[c];
                    if (!GroundItemState(child) || child.LocationKind != 2 || child.ParentInstanceId != item.InstanceId
                        || child.Slot < 0 || child.Slot >= 32 || (occupied & (1u << child.Slot)) != 0 || IsWand(child)
                        || child.ContainerState != null || child.MaterialState != null
                        || owned.ContainsKey(child.InstanceId) || !seen.Add(child.InstanceId)) return false;
                    occupied |= 1u << child.Slot;
                }
            }
            // 根与所有安装卡共用唯一身份集合和总预算；公开地面不授予资格或拾取权。
            // 物品Kind/shape/容量仍交共同Catalog复核，不能从ContentId字符串猜属性或补瓶量。
            return true;
        }

        private static bool GroundItemState(Core.OnlineOwnedItemSnapshot item)
            => item != null && Id(item.InstanceId) && Text(item.ContentId, 128) && item.ParentInstanceId != null
                && item.Revision > 0 && item.RemainingCharges >= 0 && item.X == 0 && item.Y == 0 && item.QuarterTurns == 0
                && !item.EntryBoundBackpack && !(item.UseTemplateWandStats && item.WandStats != null)
                && (item.WandStats == null || Stats(item.WandStats))
                && (item.ContainerState == null || !IsWand(item) && Container(item.ContainerState))
                && (item.MaterialState == null || !IsWand(item) && item.ContainerState == null && Material(item.MaterialState))
                && (item.AttachmentState == null || !IsWand(item) && item.ContainerState == null && item.MaterialState == null && Attachment(item.AttachmentState));

        private static bool Same(Core.OnlineOwnedMaterialStateSnapshot left, Core.OnlineOwnedMaterialStateSnapshot right)
            => left == null ? right == null : right != null && left.SchemaVersion == right.SchemaVersion
                && left.MaterialKey == right.MaterialKey && left.Quantity == right.Quantity;

        private static bool Same(Core.OnlineOwnedContainerStateSnapshot left, Core.OnlineOwnedContainerStateSnapshot right)
        {
            if (left == null) return right == null;
            if (right == null || !Container(left) || !Container(right) || left.SchemaVersion != right.SchemaVersion
                || left.Components.Length != right.Components.Length) return false;
            // 组分的顺序没有Gameplay含义；比较真实key/quantity集合，不让两份别名暗藏不同数量。
            for (int i = 0; i < left.Components.Length; i++)
            {
                bool match = false;
                for (int j = 0; j < right.Components.Length; j++)
                    if (left.Components[i].MaterialKey == right.Components[j].MaterialKey
                        && left.Components[i].Quantity == right.Components[j].Quantity) { match = true; break; }
                if (!match) return false;
            }
            return true;
        }

        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Unit(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z)
            && float.IsFinite(value.w) && UnitSquared(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
        private static bool UnitSquared(float value) => value >= .99f && value <= 1.01f;

        private static bool IsWand(Core.OnlineOwnedItemSnapshot item) => item.UseTemplateWandStats || item.WandStats != null;

        private static bool Material(Core.OnlineOwnedMaterialStateSnapshot state)
            // Schema独立于OwnedSchema2；0不是可补默认值。Quantity直接保留uint64，不经过float或capacity。
            => state.SchemaVersion == 1 && state.MaterialKey >= 1 && state.MaterialKey <= 8 && state.Quantity > 0;

        private static bool Attachment(Core.OnlineOwnedSpellAttachmentStateSnapshot state)
            // 来源已离包或消失仍保留设置；这里只验格式，不把来源存在性误当成快照安装资格。
            // Enabled=false仍是完整显式状态，和可选字段null（无设置）不同；Dose不能从瓶余量补写。
            => state.SchemaVersion == 1 && Id(state.ContainerId) && state.Dose > 0;

        private static bool Container(Core.OnlineOwnedContainerStateSnapshot state)
        {
            if (state.SchemaVersion != 1 || state.Components == null || state.Components.Length > 8) return false;
            ushort seen = 0; ulong total = 0;
            for (int i = 0; i < state.Components.Length; i++)
            {
                var amount = state.Components[i];
                if (amount == null || amount.MaterialKey < 1 || amount.MaterialKey > 8 || amount.Quantity == 0
                    || (seen & 1 << amount.MaterialKey) != 0) return false;
                // Quantity为真实整数GMU；不能经过uint32/float截断。各key合法也不代表总量不会溢出。
                try { total = checked(total + amount.Quantity); }
                catch (OverflowException) { return false; }
                seen |= (ushort)(1 << amount.MaterialKey);
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
                SelectedToolSlotPlusOne = source.SelectedToolSlotPlusOne, Ground = ToWire(source.Ground),
                ResearchOpen = source.ResearchOpen, ResearchSessionVersion = source.ResearchSessionVersion,
                ResearchJson = source.ResearchOpen ? ByteString.CopyFromUtf8(JsonUtility.ToJson(source.Research)) : ByteString.Empty,
                Assets = new InventoryOwnedAssetMessage
                {
                    SchemaVersion = source.Assets.SchemaVersion, RunId = source.Assets.RunId, PlayerId = source.Assets.PlayerId,
                    InventoryId = source.Assets.InventoryId, GameplayConfigHash = source.Assets.GameplayConfigHash,
                    SelectedWandInstanceId = source.Assets.SelectedWandInstanceId, ActorId = source.Assets.ActorId, InventoryVersion = source.Assets.InventoryVersion,
                    Gold = source.Assets.Gold, CurrencyVersion = source.Assets.CurrencyVersion
                }
            };
            for (int i = 0; i < source.Assets.Items.Length; i++)
                result.Assets.Items.Add(ToWire(source.Assets.Items[i]));
            for (int i = 0; i < source.Wands.Length; i++)
            {
                var current = source.Wands[i]; result.Wands.Add(new InventoryWandPresentationMessage
                { InstanceId = current.InstanceId, ProgramRevision = current.ProgramRevision, CurrentMana = current.CurrentMana, CooldownRemaining = current.CooldownRemaining });
            }
            for (int i = 0; i < source.SpellUses.Length; i++)
                result.SpellUses.Add(new InventorySpellUsePresentationMessage { InstanceId = source.SpellUses[i].InstanceId, Eligible = source.SpellUses[i].Eligible });
            return result;
        }
        private static InventoryGroundSnapshotMessage ToWire(Core.OnlineGroundInventorySnapshot source)
        {
            if (source == null) return null;
            var result = new InventoryGroundSnapshotMessage { Version = source.Version };
            for (int i = 0; i < source.Items.Length; i++)
            {
                var item = source.Items[i];
                var envelope = new InventoryGroundItemMessage
                {
                    InstanceId = item.InstanceId, ContentId = item.ContentId, WorldOwnerId = item.WorldOwnerId, Revision = item.Revision,
                    PositionX = item.Position.x, PositionY = item.Position.y, PositionZ = item.Position.z,
                    RotationX = item.Rotation.x, RotationY = item.Rotation.y, RotationZ = item.Rotation.z, RotationW = item.Rotation.w,
                    ContainerState = ToWire(item.ContainerState), MaterialState = ToWire(item.MaterialState), Item = ToWire(item.Item)
                };
                for (int c = 0; c < item.Children.Length; c++) envelope.Children.Add(ToWire(item.Children[c]));
                result.Items.Add(envelope);
            }
            for (int i = 0; i < source.Coins.Length; i++)
            {
                var coin = source.Coins[i];
                result.Coins.Add(new InventoryGroundCoinMessage
                { CoinId = coin.Id, Amount = coin.Amount, PositionX = coin.Position.x, PositionY = coin.Position.y, PositionZ = coin.Position.z });
            }
            return result;
        }
        // Owner与公开地面使用同一完整实例映射，避免只给瓶/固体加字段而漏掉随机杖属性、次数或卡附件。
        private static InventoryOwnedItemMessage ToWire(Core.OnlineOwnedItemSnapshot item)
            => new InventoryOwnedItemMessage
            {
                InstanceId = item.InstanceId, ContentId = item.ContentId, ParentInstanceId = item.ParentInstanceId,
                Revision = item.Revision, LocationKind = item.LocationKind, Slot = item.Slot, X = item.X, Y = item.Y,
                QuarterTurns = item.QuarterTurns, RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                UseTemplateWandStats = item.UseTemplateWandStats, EntryBoundBackpack = item.EntryBoundBackpack,
                WandStats = ToWire(item.WandStats), ContainerState = ToWire(item.ContainerState),
                MaterialState = ToWire(item.MaterialState), AttachmentState = ToWire(item.AttachmentState)
            };
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
        private static InventoryOwnedContainerStateMessage ToWire(Core.OnlineOwnedContainerStateSnapshot source)
        {
            if (source == null) return null;
            var result = new InventoryOwnedContainerStateMessage { SchemaVersion = source.SchemaVersion };
            for (int i = 0; i < source.Components.Length; i++)
                result.Components.Add(new InventoryOwnedMaterialAmountMessage
                { MaterialKey = source.Components[i].MaterialKey, Quantity = source.Components[i].Quantity });
            return result;
        }
        private static InventoryOwnedMaterialStateMessage ToWire(Core.OnlineOwnedMaterialStateSnapshot source)
            => source == null ? null : new InventoryOwnedMaterialStateMessage
            { SchemaVersion = source.SchemaVersion, MaterialKey = source.MaterialKey, Quantity = source.Quantity };
        private static InventorySpellAttachmentStateMessage ToWire(Core.OnlineOwnedSpellAttachmentStateSnapshot source)
            => source == null ? null : new InventorySpellAttachmentStateMessage
            { SchemaVersion = source.SchemaVersion, ContainerId = source.ContainerId, Dose = source.Dose, Enabled = source.Enabled };
        private static Core.OnlineInventoryPresentationSnapshot ToCore(InventorySnapshotMessage source)
        {
            var assets = source.Assets;
            var result = new Core.OnlineInventoryPresentationSnapshot
            {
                SchemaVersion = source.SchemaVersion, ConnectionEpoch = source.ConnectionEpoch, SnapshotSequence = source.SnapshotSequence,
                ServerTick = source.ServerTick, ResourceVersion = source.ResourceVersion, SelectionVersion = source.SelectionVersion,
                SpellUseVersion = source.SpellUseVersion, SelectedToolSlotPlusOne = source.SelectedToolSlotPlusOne, Ground = ToCore(source.Ground),
                ResearchOpen = source.ResearchOpen, ResearchSessionVersion = source.ResearchSessionVersion,
                Assets = new Core.OnlineOwnedAssetSnapshot
                {
                    SchemaVersion = assets.SchemaVersion, RunId = assets.RunId, PlayerId = assets.PlayerId, InventoryId = assets.InventoryId,
                    GameplayConfigHash = assets.GameplayConfigHash, SelectedWandInstanceId = assets.SelectedWandInstanceId,
                    ActorId = assets.ActorId, InventoryVersion = assets.InventoryVersion, Gold = assets.Gold, CurrencyVersion = assets.CurrencyVersion,
                    Items = new Core.OnlineOwnedItemSnapshot[assets.Items.Count]
                },
                Wands = new Core.OnlineInventoryWandPresentationSnapshot[source.Wands.Count],
                SpellUses = new Core.OnlineInventorySpellUsePresentationSnapshot[source.SpellUses.Count]
            };
            for (int i = 0; i < assets.Items.Count; i++)
                result.Assets.Items[i] = ToCore(assets.Items[i]);
            for (int i = 0; i < source.Wands.Count; i++)
            {
                var current = source.Wands[i]; result.Wands[i] = new Core.OnlineInventoryWandPresentationSnapshot
                { InstanceId = current.InstanceId, ProgramRevision = current.ProgramRevision, CurrentMana = current.CurrentMana, CooldownRemaining = current.CooldownRemaining };
            }
            for (int i = 0; i < source.SpellUses.Count; i++)
                result.SpellUses[i] = new Core.OnlineInventorySpellUsePresentationSnapshot { InstanceId = source.SpellUses[i].InstanceId, Eligible = source.SpellUses[i].Eligible };
            if (source.ResearchOpen && TryResearchJson(source.ResearchJson.Span, out var research)) result.Research = research;
            return result;
        }
        private static Core.OnlineGroundInventorySnapshot ToCore(InventoryGroundSnapshotMessage source)
        {
            if (source == null) return null;
            var result = new Core.OnlineGroundInventorySnapshot
            { Version = source.Version, Items = new Core.OnlineGroundItemSnapshot[source.Items.Count],
                Coins = new Core.OnlineCoinSnapshot[source.Coins.Count] };
            for (int i = 0; i < source.Items.Count; i++)
            {
                var item = source.Items[i];
                // 完整数组和每个组分均归本次DTO持有，不借用Parser集合或上一批快照；保留真实ulong数量。
                result.Items[i] = new Core.OnlineGroundItemSnapshot
                {
                    InstanceId = item.InstanceId, ContentId = item.ContentId, WorldOwnerId = item.WorldOwnerId, Revision = item.Revision,
                    Position = new Vector3(item.PositionX, item.PositionY, item.PositionZ),
                    Rotation = new Quaternion(item.RotationX, item.RotationY, item.RotationZ, item.RotationW),
                    ContainerState = ToCore(item.ContainerState), MaterialState = ToCore(item.MaterialState),
                    Item = ToCore(item.Item), Children = new Core.OnlineOwnedItemSnapshot[item.Children.Count]
                };
                for (int c = 0; c < item.Children.Count; c++) result.Items[i].Children[c] = ToCore(item.Children[c]);
            }
            for (int i = 0; i < source.Coins.Count; i++)
            {
                var coin = source.Coins[i];
                result.Coins[i] = new Core.OnlineCoinSnapshot
                { Id = coin.CoinId, Amount = coin.Amount, Position = new Vector3(coin.PositionX, coin.PositionY, coin.PositionZ) };
            }
            return result;
        }
        private static Core.OnlineOwnedItemSnapshot ToCore(InventoryOwnedItemMessage item)
            => item == null ? null : new Core.OnlineOwnedItemSnapshot
            {
                InstanceId = item.InstanceId, ContentId = item.ContentId, ParentInstanceId = item.ParentInstanceId, Revision = item.Revision,
                LocationKind = item.LocationKind, Slot = item.Slot, X = item.X, Y = item.Y, QuarterTurns = item.QuarterTurns,
                RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                UseTemplateWandStats = item.UseTemplateWandStats, EntryBoundBackpack = item.EntryBoundBackpack,
                WandStats = ToCore(item.WandStats), ContainerState = ToCore(item.ContainerState),
                MaterialState = ToCore(item.MaterialState), AttachmentState = ToCore(item.AttachmentState)
            };
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
        private static Core.OnlineOwnedContainerStateSnapshot ToCore(InventoryOwnedContainerStateMessage source)
        {
            if (source == null) return null;
            // Schema=1且零项是Wire中的明确空瓶；null message仍保持null，不按模板猜状态。
            // 每个数量对象也新建，安装者保留的DTO不借用Parser或下一次Decode的内部集合。
            var result = new Core.OnlineOwnedContainerStateSnapshot
            {
                SchemaVersion = source.SchemaVersion,
                Components = new Core.OnlineOwnedMaterialAmountSnapshot[source.Components.Count]
            };
            for (int i = 0; i < source.Components.Count; i++)
                result.Components[i] = new Core.OnlineOwnedMaterialAmountSnapshot
                { MaterialKey = (byte)source.Components[i].MaterialKey, Quantity = source.Components[i].Quantity };
            return result;
        }
        private static Core.OnlineOwnedMaterialStateSnapshot ToCore(InventoryOwnedMaterialStateMessage source)
            // Wire先在原uint32范围核验Key；这里才可安全收窄。每项各建独立值对象，绝不借用Parser/旧快照。
            => source == null ? null : new Core.OnlineOwnedMaterialStateSnapshot
            { SchemaVersion = source.SchemaVersion, MaterialKey = (byte)source.MaterialKey, Quantity = source.Quantity };
        private static Core.OnlineOwnedSpellAttachmentStateSnapshot ToCore(InventorySpellAttachmentStateMessage source)
            // 每次Decode独立持有附件对象；不验证来源当前可执行，也不把失效设置静默清掉。
            => source == null ? null : new Core.OnlineOwnedSpellAttachmentStateSnapshot
            { SchemaVersion = source.SchemaVersion, ContainerId = source.ContainerId, Dose = source.Dose, Enabled = source.Enabled };

        private static readonly UTF8Encoding ResearchUtf8 = new UTF8Encoding(false, true);
        private static bool Research(Core.OnlineInventoryPresentationSnapshot owner)
        {
            if (!owner.ResearchOpen) return owner.Research == null || owner.Research.SchemaVersion == 0;
            var r = owner.Research; var a = owner.Assets;
            if (owner.ResearchSessionVersion == 0 || r == null || a == null || r.SchemaVersion != 1
                || r.PlayerId != a.PlayerId || r.InventoryId != a.InventoryId || r.GameplayConfigHash != a.GameplayConfigHash
                || r.SelectedWandId != a.SelectedWandInstanceId || r.InventoryVersion != a.InventoryVersion
                || r.ResourceVersion != owner.ResourceVersion || r.SpellUseVersion != owner.SpellUseVersion
                || !ResearchTree(r, 0)) return false;
            return Encoding.UTF8.GetByteCount(JsonUtility.ToJson(r)) <= Core.OnlineWandResearchStateSnapshot.MaximumUtf8Bytes;
        }

        /// <summary>
        /// 网络层不引用Run：这里只做Core子树的UTF8/形状/数值/canonical边界，领域层随后按真实Catalog验证Graph。
        /// 关闭只传修订，绝不把整个Owner JSON化；容器/法杖等原Protobuf可空message仍沿原严格映射保留null。
        /// </summary>
        private static bool TryResearchJson(ReadOnlySpan<byte> bytes, out Core.OnlineWandResearchStateSnapshot result)
        {
            result = null;
            if (bytes.Length == 0 || bytes.Length > Core.OnlineWandResearchStateSnapshot.MaximumUtf8Bytes) return false;
            try
            {
                string json = ResearchUtf8.GetString(bytes);
                if (!ResearchJsonShape(json)) return false;
                var candidate = JsonUtility.FromJson<Core.OnlineWandResearchStateSnapshot>(json);
                if (candidate == null || candidate.SchemaVersion != 1 || !ResearchTree(candidate, 0)
                    || JsonUtility.ToJson(candidate) != json) return false;
                result = candidate; return true;
            }
            catch (ArgumentException) { return false; }
        }
        private static bool ResearchTree(object value, int depth)
        {
            if (value == null) return true;
            if (depth > 12) return false;
            if (value is float number) return float.IsFinite(number);
            if (value is string text) return text.Length <= 512;
            if (value.GetType().IsPrimitive) return true;
            if (value is Array array)
            { if (array.Length > 16384) return false; foreach (object item in array) if (!ResearchTree(item, depth + 1)) return false; return true; }
            foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (!ResearchTree(field.GetValue(value), depth + 1)) return false;
            return true;
        }
        private static bool ResearchJsonShape(string json)
        {
            var stack = new char[12]; var counts = new int[12]; int depth = 0; bool quoted = false, escaped = false;
            foreach (char c in json)
            {
                if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
                if (c == '"') { quoted = true; continue; }
                if (c == '[' || c == '{') { if (depth == stack.Length) return false; stack[depth] = c; counts[depth++] = 1; }
                else if (c == ']' || c == '}') { if (depth == 0 || stack[--depth] != (c == ']' ? '[' : '{')) return false; }
                else if (c == ',' && depth > 0 && stack[depth - 1] == '[' && ++counts[depth - 1] > 16384) return false;
            }
            return depth == 0 && !quoted;
        }

        private enum MessageKind : byte { Root, Assets, Item, Stats, Wand, SpellUse, ContainerState, MaterialAmount, Ground, GroundItem, MaterialState, AttachmentState, GroundCoin }
        private static bool Wire(ReadOnlySpan<byte> bytes, MessageKind kind)
        {
            int groundRemaining = MaxGroundItems;
            return Wire(bytes, kind, ref groundRemaining);
        }

        private static bool Wire(ReadOnlySpan<byte> bytes, MessageKind kind, ref int groundRemaining)
        {
            // 预检在Parser分配对象前共享全快照计数。外层GroundItem和其Item14是同一根，
            // 根只计一次；每个Children15再消费一次，不能用16384棵各32卡的树扩大预算。
            if (kind == MessageKind.GroundItem && --groundRemaining < 0) return false;
            int offset = 0, repeatedA = 0, repeatedB = 0; ulong seen = 0;
            float qx = 0, qy = 0, qz = 0, qw = 0; bool templateWand = false;
            bool researchOpen = false, researchPayload = false; uint researchVersion = 0;
            int maximum = kind == MessageKind.Root ? 16 : kind == MessageKind.Assets ? 11 : kind == MessageKind.Stats ? 9
                : kind == MessageKind.Item ? 17 : kind == MessageKind.GroundItem ? 15 : kind == MessageKind.Wand || kind == MessageKind.AttachmentState ? 4
                : kind == MessageKind.GroundCoin ? 5 : kind == MessageKind.MaterialState || kind == MessageKind.Ground ? 3 : 2;
            while (offset < bytes.Length)
            {
                if (!Varint(bytes, ref offset, out ulong tag) || tag > uint.MaxValue) return false;
                int field = (int)(tag >> 3); if (field < 1 || field > maximum) return false;
                bool repeated = kind == MessageKind.Root && (field == 10 || field == 11)
                    || kind == MessageKind.Assets && field == 9 || kind == MessageKind.Stats && field == 9
                    || kind == MessageKind.ContainerState && field == 2 || kind == MessageKind.Ground && (field == 2 || field == 3)
                    || kind == MessageKind.GroundItem && field == 15;
                if (!repeated) { ulong bit = 1UL << field; if ((seen & bit) != 0) return false; seen |= bit; }
                else if (kind == MessageKind.Root && field == 11) { if (++repeatedB > MaxSpellUses) return false; }
                else if (kind == MessageKind.Ground && field == 3) { if (++repeatedB > MaxGroundCoins) return false; }
                else if (++repeatedA > (kind == MessageKind.Root ? MaxWandStates : kind == MessageKind.Assets ? MaxItems
                    : kind == MessageKind.ContainerState ? 8 : kind == MessageKind.Ground ? MaxGroundItems
                    : kind == MessageKind.GroundItem ? 32 : MaxAlwaysCast)) return false;
                if (kind == MessageKind.GroundItem && field == 15 && --groundRemaining < 0) return false;

                bool text = kind == MessageKind.Assets && field >= 2 && field <= 6 || (kind == MessageKind.Item || kind == MessageKind.GroundItem) && field <= 3
                    || kind == MessageKind.Stats && field == 9 || (kind == MessageKind.Wand || kind == MessageKind.SpellUse) && field == 1
                    || kind == MessageKind.AttachmentState && field == 2;
                // Root字段12是选择工具的uint32标量，不能沿用“9以后都是嵌套消息”的旧假设。
                bool nested = kind == MessageKind.Root && (field >= 9 && field <= 11 || field == 13) || kind == MessageKind.Assets && field == 9
                    || kind == MessageKind.Item && field >= 13 && field <= 16 || kind == MessageKind.ContainerState && field == 2
                    || kind == MessageKind.Ground && (field == 2 || field == 3) || kind == MessageKind.GroundItem && field >= 12 && field <= 15;
                bool scalarFloat = kind == MessageKind.Stats && field <= 5 || kind == MessageKind.Wand && field >= 3
                    || kind == MessageKind.GroundItem && field >= 5 && field <= 11 || kind == MessageKind.GroundCoin && field >= 3;
                bool researchBlob = kind == MessageKind.Root && field == 16;
                int expected = text || nested || researchBlob ? 2 : scalarFloat ? 5 : kind == MessageKind.Root && field == 4 ? 1 : 0;
                if ((tag & 7) != (ulong)expected) return false;
                if (expected == 2)
                {
                    if (!Varint(bytes, ref offset, out ulong length) || length > (ulong)(bytes.Length - offset)) return false;
                    if (researchBlob)
                    {
                        if (length == 0 || length > Core.OnlineWandResearchStateSnapshot.MaximumUtf8Bytes
                            || !TryResearchJson(bytes.Slice(offset, (int)length), out _)) return false;
                        researchPayload = true;
                    }
                    else if (text)
                    {
                        int limit = kind == MessageKind.Assets ? field == 4 ? 96 : field == 5 ? 64 : 32
                            : (kind == MessageKind.Item || kind == MessageKind.GroundItem) && field == 2 || kind == MessageKind.Stats ? 128 : 32;
                        if (length > (ulong)limit) return false;
                        if (kind == MessageKind.AttachmentState && length != 32) return false;
                        // 所有Wire字符串都是ASCII身份/目录键，不是DisplayName。
                        // 在Parser创建字符串前拒绝控制字节和任何多字节UTF8，避免超长或非法序列藏入对象图。
                        for (int i = 0; i < (int)length; i++) if (bytes[offset + i] < 33 || bytes[offset + i] > 126) return false;
                        if (kind == MessageKind.AttachmentState)
                            for (int i = 0; i < (int)length; i++)
                            {
                                byte value = bytes[offset + i];
                                if (!(value >= (byte)'0' && value <= (byte)'9') && !(value >= (byte)'a' && value <= (byte)'f')) return false;
                            }
                    }
                    else
                    {
                        // 通用地面只增加固定的GroundItem→Item层，Item内部没有再次递归Item的入口。
                        // 各层仍先限制字段和计数；恶意深层消息不会送进Protobuf Parser。
                        // 每一层先检查自己的字段与计数，未知消息/深层伪造不会到达Protobuf Parser。
                        MessageKind child = kind == MessageKind.Root ? field == 9 ? MessageKind.Assets : field == 10 ? MessageKind.Wand
                                : field == 11 ? MessageKind.SpellUse : MessageKind.Ground
                            : kind == MessageKind.Assets ? MessageKind.Item : kind == MessageKind.Item
                                ? field == 13 ? MessageKind.Stats : field == 14 ? MessageKind.ContainerState
                                    : field == 15 ? MessageKind.MaterialState : MessageKind.AttachmentState
                            : kind == MessageKind.Ground ? field == 2 ? MessageKind.GroundItem : MessageKind.GroundCoin : kind == MessageKind.GroundItem
                                ? field == 12 ? MessageKind.ContainerState : field == 13 ? MessageKind.MaterialState : MessageKind.Item : MessageKind.MaterialAmount;
                        if (!Wire(bytes.Slice(offset, (int)length), child, ref groundRemaining)) return false;
                    }
                    offset += (int)length;
                }
                else if (expected != 0)
                {
                    int width = expected == 5 ? 4 : 8; if (bytes.Length - offset < width) return false;
                    if (scalarFloat)
                    {
                        // 所有固定属性/资源/姿态先在Span上拒绝NaN和Infinity；不能把新增完整根
                        // 的Stats遗漏在Ground旧姿态检查之外，再让坏状态进入Parser对象图。
                        int bits = bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24;
                        float value = BitConverter.Int32BitsToSingle(bits); if (!float.IsFinite(value)) return false;
                        if (kind == MessageKind.Stats || kind == MessageKind.Wand)
                        { if (!NonNegative(value)) return false; }
                        else if (kind == MessageKind.GroundItem)
                        { if (field == 8) qx = value; else if (field == 9) qy = value; else if (field == 10) qz = value; else if (field == 11) qw = value; }
                    }
                    offset += width;
                }
                else
                {
                    if (!Varint(bytes, ref offset, out ulong number)) return false;
                    // int32负数按Protobuf规范先符号扩展为uint64，再编码为10字节。
                    // Backpack/WorldItem根的Slot允许-1；5字节0xffffffff会被Parser截成同值，必须拒绝别名编码。
                    bool signed = kind == MessageKind.Item && field >= 5 && field <= 10
                        || kind == MessageKind.Stats && (field == 6 || field == 7);
                    bool wide = kind == MessageKind.MaterialAmount && field == 2 || kind == MessageKind.Assets && (field == 7 || field == 10)
                        || (kind == MessageKind.MaterialState || kind == MessageKind.AttachmentState) && field == 3
                        || kind == MessageKind.GroundCoin && field <= 2;
                    if (signed)
                    {
                        if (number > int.MaxValue && !(kind == MessageKind.Item && field == 6 && number == ulong.MaxValue)) return false;
                    }
                    else if (!wide && number > uint.MaxValue) return false;
                    // 金币是非负int64，不能沿用uint32窄化或允许10字节负数编码截成其它合法币值。
                    if (kind == MessageKind.Assets && (field == 10 && number > long.MaxValue || field == 11 && number == 0)) return false;
                    // 原始Varint先拒绝负int64编码与越界ID；不能等Parser截断后再判断，更不能默认补金额。
                    if (kind == MessageKind.GroundCoin && (field == 1 && (number < 5 || number > 16)
                        || field == 2 && (number == 0 || number > long.MaxValue))) return false;
                    // 0沿用SelectedWand推导，1..7只声明工具槽；绑定实例/容器资格由Run严格核验。
                    if (kind == MessageKind.Root && field == 12 && number > 7) return false;
                    if (kind == MessageKind.Root && field == 14) { if (number > 1) return false; researchOpen = number == 1; }
                    if (kind == MessageKind.Root && field == 15) researchVersion = (uint)number;
                    // Wire的key是uint32，Core用byte；先检查域再收窄，防257截成合法key1。
                    if (kind == MessageKind.MaterialAmount && field == 1 && (number < 1 || number > 8)) return false;
                    // 固体叶节点三个标量均显式且非零：在Parser前拒绝旧Schema、key窄化及空量。
                    if (kind == MessageKind.MaterialState && (field == 1 && number != 1
                        || field == 2 && (number < 1 || number > 8) || field == 3 && number == 0)) return false;
                    if (kind == MessageKind.AttachmentState && (field == 1 && number != 1 || field == 3 && number == 0)) return false;
                    bool boolean = kind == MessageKind.Item && (field == 11 || field == 12 || field == 17)
                        || kind == MessageKind.Stats && field == 8 || kind == MessageKind.SpellUse && field == 2
                        || kind == MessageKind.AttachmentState && field == 4;
                    if (boolean && number > 1) return false;
                    if (kind == MessageKind.Item && field == 12) templateWand = number == 1;
                }
            }
            // 与语义校验独立的严格字段掩码：固体不能靠proto3默认值补schema/key/quantity；
            // 同实例也不能夹带两类状态。显式false模板字段仍中立，不能仅凭其字段出现就判断成法杖。
            if (kind == MessageKind.Root && (researchOpen ? researchVersion == 0 || !researchPayload : researchPayload)) return false;
            if (kind == MessageKind.MaterialState) return seen == ((1UL << 1) | (1UL << 2) | (1UL << 3));
            // Enabled的proto3默认false合法；schema/source/dose必须同时出现，不能把缺项当disabled设置。
            if (kind == MessageKind.AttachmentState) return (seen & 14UL) == 14UL;
            // Id/Amount必须显式出现；坐标的proto3默认0是合法死亡位置，仍拒绝重复或非有限float。
            if (kind == MessageKind.GroundCoin) return (seen & 6UL) == 6UL;
            if (kind == MessageKind.Item)
            {
                bool container = (seen & (1UL << 14)) != 0, material = (seen & (1UL << 15)) != 0,
                    attachment = (seen & (1UL << 16)) != 0;
                if ((container || material) && (templateWand || (seen & (1UL << 13)) != 0)
                    || container && material
                    || attachment && (container || material || templateWand || (seen & (1UL << 13)) != 0)) return false;
            }
            // GroundItem必须显式完整根；普通Item可以没有瓶/固体状态，但不能同时夹带两者。
            // Children为空时proto3不写字段，DTO会恢复为独立空数组；缺Item不能补成模板状态。
            if (kind == MessageKind.GroundItem)
            {
                bool container = (seen & (1UL << 12)) != 0, material = (seen & (1UL << 13)) != 0;
                return (seen & (1UL << 14)) != 0 && !(container && material)
                    && UnitSquared(qx * qx + qy * qy + qz * qz + qw * qw);
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
