using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Run
{
    /// <summary>只把服务器冻结的值导入内存；不读取本地存档，不发放 StarterKit，也不重建业务物品身份。</summary>
    public static class OnlineOwnedWorkspaceFactory
    {
        public static bool TryCreate(ItemCatalogIndex catalog, OnlineOwnedAssetSnapshot snapshot,
            string expectedGameplayConfigHash, out OnlineOwnedWorkspace workspace, out string error)
            => TryCreateCore(catalog, snapshot, expectedGameplayConfigHash, false, out workspace, out error);

        /// <summary>
        /// 仅由可信Home地图装配选择：仍建立同一个DS Owner，但沿原Home规则不提前消费下次Raid资格。
        /// 此入口不接受Client命令中的purpose，也不读取本地档；真实Emit仍走同一Mana/次数事务。
        /// 独立方法名保留原TryCreate的Reflection/API兼容性，不能给普通副本默认为和平语义。
        /// </summary>
        public static bool TryCreatePractice(ItemCatalogIndex catalog, OnlineOwnedAssetSnapshot snapshot,
            string expectedGameplayConfigHash, out OnlineOwnedWorkspace workspace, out string error)
            => TryCreateCore(catalog, snapshot, expectedGameplayConfigHash, true, out workspace, out error);

        private static bool TryCreateCore(ItemCatalogIndex catalog, OnlineOwnedAssetSnapshot snapshot,
            string expectedGameplayConfigHash, bool peacefulPractice, out OnlineOwnedWorkspace workspace, out string error)
        {
            workspace = null; error = null;
            if (catalog == null || snapshot == null || snapshot.SchemaVersion != OnlineOwnedAssetSnapshot.CurrentSchemaVersion)
                return Fail("缺少当前版本的 Owned 入局数据。", out error);
            if (!ValidHash(expectedGameplayConfigHash) || !string.Equals(snapshot.GameplayConfigHash, expectedGameplayConfigHash, StringComparison.Ordinal))
                return Fail("入局 Gameplay 配置 Hash 不一致。", out error);
            // 金币来自业务服可信Entry，与Items同一冻结事实。0余额合法，但未知货币修订与负数不能被默认值掩盖。
            // 在建Store/资格/施法服务前拒绝，失败不得创建半个Owner或给旧Schema补启动金币。
            if (snapshot.Gold < 0 || snapshot.CurrencyVersion == 0)
                return Fail("入局统一金币余额或DS货币修订非法。", out error);
            if (string.IsNullOrWhiteSpace(snapshot.RunId) || string.IsNullOrWhiteSpace(snapshot.PlayerId)
                || string.IsNullOrWhiteSpace(snapshot.InventoryId) || snapshot.ActorId < 1 || snapshot.ActorId > 4
                || snapshot.InventoryVersion == 0
                || snapshot.Items == null || snapshot.Items.Length > OnlineOwnedAssetSnapshot.MaximumItems)
                return Fail("Owned 入局身份、选择或容量非法。", out error);

            var entries = new InventoryEntry[snapshot.Items.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                var item = snapshot.Items[i];
                if (item == null || item.Revision == 0 || !catalog.TryGet(item.ContentId, out var definition)
                    || !catalog.TryGetRules(item.ContentId, out var rules))
                    return Fail("入局物品缺少实例修订或已知 ContentId。", out error);
                if (!TryReadLocation(item, snapshot.PlayerId, out var location))
                    return Fail("入局只接受当前玩家装备根和真实携带父链，位置或父 ID 非法。", out error);

                WandStats? instanceStats = null;
                // 备用杖也是同一种真实资产：位置在背包不意味着可以省略固定属性，
                // 更不能让安装卡容量回退到另一套模板规则。最终父链/容量仍交给 Store 验证。
                if (definition.Kind == ItemKind.Wand)
                {
                    if (item.UseTemplateWandStats)
                    {
                        if (item.WandStats != null || !catalog.TryGetWandStats(item.ContentId, out _))
                            return Fail("模板法杖标记与实例属性冲突。", out error);
                    }
                    else
                    {
                        var source = item.WandStats;
                        if (source == null || source.AlwaysCastContentIds == null)
                            return Fail("法杖缺少明确的模板标记或完整实例属性。", out error);
                        var stats = new WandStats(source.MaxMana, source.ManaRegenPerSecond, source.BaseCastDelay,
                            source.ProgramCapacity, source.BaseDraws, source.BaseReloadTime, source.BaseSpreadDegrees,
                            source.Shuffle, source.AlwaysCastContentIds);
                        if (!stats.IsValid || !catalog.ValidateWandTraits(stats))
                            return Fail("法杖实例固定属性非法。", out error);
                        instanceStats = stats;
                    }
                }
                else if (item.UseTemplateWandStats || item.WandStats != null)
                    return Fail("非法杖物品不能携带法杖固定属性。", out error);

                if (!TryReadComposition(item, rules, out var composition, out error)) return false;
                if (!TryReadMaterial(item, rules, out var material, out error)) return false;
                if (!TryReadAttachment(item, definition, out var attachment, out error)) return false;
                if (item.EntryBoundBackpack && definition.Kind != ItemKind.Backpack)
                    return Fail("入场绑定标记只能属于真实基础包实例。", out error);

                var state = new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges,
                    item.HasUnlimitedCharges, composition: composition, material: material, attachment: attachment, rolledWandStats: instanceStats,
                    entryBoundBackpack: item.EntryBoundBackpack);
                entries[i] = new InventoryEntry(state, location);
            }
            // TryRestore 先建立所有身份再校验父链，业务 DTO 的排列顺序不决定导入是否成功。
            if (!ItemOwnershipStore.TryRestore(catalog, entries, snapshot.InventoryVersion, out var store, out var reason))
                return Fail("Owned 入局所有权校验失败：" + reason, out error);
            if (!IsCarriedClosure(store, snapshot.PlayerId))
                return Fail("入局资产未完整连接到当前玩家的装备根。", out error);
            ItemLocation selected = default;
            if (!string.IsNullOrEmpty(snapshot.SelectedWandInstanceId))
            {
                if (!store.TryGetLocation(snapshot.SelectedWandInstanceId, out selected)
                    || selected.Kind != ItemLocationKind.EquippedWand || selected.OwnerId != snapshot.PlayerId)
                    return Fail("选择的法杖不属于当前角色装备槽。", out error);
            }
            else foreach (var entry in store)
                if (entry.Location.Kind == ItemLocationKind.EquippedWand)
                    return Fail("仍有装备法杖时必须提供真实选中实例。", out error);

            var profile = new SinglePlayerProfile(snapshot.PlayerId, store, currency: snapshot.Gold);
            var eligibility = new RaidSpellEligibility(profile);
            if (!peacefulPractice && !eligibility.TryBegin(snapshot.RunId, out reason))
                return Fail("入局资格提交失败：" + reason, out error);
            var wands = new WandLoadoutService(profile, snapshot.InventoryId);
            if (!string.IsNullOrEmpty(snapshot.SelectedWandInstanceId) && !wands.TrySelect(selected.SlotIndex))
                return Fail("无法选择入局法杖。", out error);
            var casts = new WandExecutionTransactionService(profile, wands, eligibility, snapshot.InventoryId,
                new WandExecutionReplayArchive(), allowPeacefulExperiments: peacefulPractice);
            // RuntimeItemState 不保存数据库 Revision。为完整导出保留已校验的冻结修订，不能拿 Store
            // 全局 Version 冒充某一物品的数据库版本；当前余额与位置仍从存活工作区实时读取。
            var revisions = new Dictionary<string, uint>(snapshot.Items.Length, StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Items.Length; i++) revisions.Add(snapshot.Items[i].InstanceId, snapshot.Items[i].Revision);
            workspace = new OnlineOwnedWorkspace(snapshot.RunId, snapshot.PlayerId, snapshot.ActorId,
                snapshot.InventoryId, profile, wands, casts, eligibility, snapshot.GameplayConfigHash, revisions, snapshot.CurrencyVersion, peacefulPractice);
            return true;
        }

        /// <summary>
        /// 地面恢复与Owned导入共用同一套受控物品状态解析；只建立实例值，不授予位置或玩家Owner。
        /// 原地面树的父链、重复身份、槽位和占格必须由调用者在完整候选Store内一次验证后发布。
        /// </summary>
        internal static bool TryReadGroundItemState(ItemCatalogIndex catalog, OnlineOwnedItemSnapshot item,
            out RuntimeItemState state, out string error)
        {
            state = null; error = null;
            if (catalog == null || item == null || item.Revision == 0 || string.IsNullOrWhiteSpace(item.InstanceId)
                || item.RemainingCharges < 0 || !catalog.TryGet(item.ContentId, out var definition)
                || !catalog.TryGetRules(item.ContentId, out var rules)) return Fail("地面实例缺少合法身份、目录或修订。", out error);
            // 与Owned导入共用组分/材料/附件解析，不读取Location、更不授予Owner；地面完整树随后原子验证归属。
            WandStats? stats = null;
            if (definition.Kind == ItemKind.Wand)
            {
                if (item.UseTemplateWandStats)
                { if (item.WandStats != null || !catalog.TryGetWandStats(item.ContentId, out _)) return Fail("地面模板杖标记冲突。", out error); }
                else
                {
                    var source = item.WandStats;
                    if (source == null || source.AlwaysCastContentIds == null) return Fail("地面杖缺少固定roll。", out error);
                    var current = new WandStats(source.MaxMana, source.ManaRegenPerSecond, source.BaseCastDelay, source.ProgramCapacity,
                        source.BaseDraws, source.BaseReloadTime, source.BaseSpreadDegrees, source.Shuffle, source.AlwaysCastContentIds);
                    if (!current.IsValid || !catalog.ValidateWandTraits(current)) return Fail("地面杖固定roll非法。", out error);
                    stats = current;
                }
            }
            else if (item.UseTemplateWandStats || item.WandStats != null) return Fail("非地面杖不得夹带法杖固定属性。", out error);
            if (!TryReadComposition(item, rules, out var composition, out error)
                || !TryReadMaterial(item, rules, out var material, out error)
                || !TryReadAttachment(item, definition, out var attachment, out error)) return false;
            if (item.EntryBoundBackpack && definition.Kind != ItemKind.Backpack) return Fail("入场绑定标记类型非法。", out error);
            state = new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges, item.HasUnlimitedCharges,
                composition: composition, material: material, attachment: attachment, rolledWandStats: stats, entryBoundBackpack: item.EntryBoundBackpack);
            return true;
        }

        /// <summary>根位置的空父ID只映射到已认证PlayerId；地面/仓库/在途不能混入执行资产携带闭包。</summary>
        private static bool TryReadLocation(OnlineOwnedItemSnapshot item, string playerId, out ItemLocation location)
        {
            location = default;
            if (item.ParentInstanceId == null) return false;
            ItemLocationKind kind;
            string owner;
            switch (item.LocationKind)
            {
                case (int)ItemLocationKind.EquippedWand:
                case (int)ItemLocationKind.EquippedBackpack:
                case (int)ItemLocationKind.EquippedTool:
                case (int)ItemLocationKind.SpellStorage:
                    if (item.ParentInstanceId.Length != 0) return false;
                    kind = (ItemLocationKind)item.LocationKind; owner = playerId;
                    break;
                case (int)ItemLocationKind.Backpack:
                case (int)ItemLocationKind.WandSlot:
                    if (string.IsNullOrWhiteSpace(item.ParentInstanceId)) return false;
                    kind = (ItemLocationKind)item.LocationKind; owner = item.ParentInstanceId;
                    break;
                default: return false;
            }
            location = new ItemLocation(kind, owner, item.X, item.Y, item.QuarterTurns, item.Slot);
            return true;
        }

        /// <summary>
        /// 容量和兼容材料来自经 Gameplay Hash 校验的 Catalog，网络只声明该实例的实际组分。
        /// 缺少状态不能被 NormalizeContainer 自动补成空瓶；只有 Schema=1 的显式空数组合法。
        /// 新建领域组分并复制每个整数值，既不持有 DTO 引用，也不经 float/夹断改变真实材料数量。
        /// </summary>
        internal static bool TryReadComposition(OnlineOwnedItemSnapshot item, in InventoryItemRules rules,
            out ContainerComposition composition, out string error)
        {
            composition = null; error = null;
            if (rules.Kind != ItemKind.Container)
                return item.ContainerState == null || Fail("非容器物品不能携带容器组分状态。", out error);

            var state = item.ContainerState;
            if (state == null || state.SchemaVersion != 1 || state.Components == null || state.Components.Length > 8)
                return Fail("容器需要当前显式状态和完整的有界组分数组。", out error);
            var amounts = new MaterialAmount[state.Components.Length];
            ulong total = 0;
            ushort seen = 0;
            for (int i = 0; i < amounts.Length; i++)
            {
                var source = state.Components[i];
                if (source == null || !MaterialAmount.IsKnown(source.MaterialKey) || source.Quantity == 0
                    || (seen & 1 << source.MaterialKey) != 0 || (rules.ContainerMaterials & 1 << source.MaterialKey) == 0)
                    return Fail("容器组分含空项、未知/重复/不兼容材料或非正数量。", out error);
                try { total = checked(total + source.Quantity); }
                catch (OverflowException) { return Fail("容器组分总量溢出。", out error); }
                if (total > rules.ContainerCapacity) return Fail("容器实际总量超过可信容量。", out error);
                seen |= (ushort)(1 << source.MaterialKey);
                amounts[i] = new MaterialAmount(source.MaterialKey, source.Quantity);
            }
            var restored = new ContainerComposition(rules.ContainerCapacity, rules.ContainerMaterials);
            if (amounts.Length > 0)
            {
                if (!restored.CanAdd(amounts, amounts.Length)) return Fail("容器组分不满足领域数量约束。", out error);
                restored.AddReserved(amounts, amounts.Length);
            }
            composition = restored;
            return true;
        }

        // TryRestore 已排除缺父项和环；发布前仍明确检查全部节点可达当前玩家，
        // 防止将来扩展 Store 可用位置时意外把仓库或另一上下文变成本局执行资产。
        /// <summary>真实固体数量单独导入；容量是上界，不能用来补充省略/非法数量。非材料禁止混入该状态。</summary>
        internal static bool TryReadMaterial(OnlineOwnedItemSnapshot item, in InventoryItemRules rules,
            out MaterialItemState material, out string error)
        {
            material = null; error = null;
            if (rules.Kind != ItemKind.Material)
                return item.MaterialState == null || Fail("非固体物品不能携带固体实例状态。", out error);
            var state = item.MaterialState;
            if (state == null || state.SchemaVersion != 1 || !MaterialAmount.IsKnown(state.MaterialKey)
                || state.MaterialKey != rules.MaterialKey || state.Quantity == 0 || state.Quantity > rules.MaterialItemCapacity
                || item.ContainerState != null || item.UseTemplateWandStats || item.WandStats != null)
                return Fail("固体状态必须为Schema1、目录固定Key和容量内正数量，且不能混入容器/法杖状态。", out error);
            material = new MaterialItemState(state.MaterialKey, state.Quantity); return true;
        }

        /// <summary>
        /// 附件配置只有卡片身份和剂量，没有材料所有权。失效/已丢出的来源仍需原样保留，
        /// 实际装备包归属与当前余量由原供料服务在每个执行层准备时裁决。
        /// </summary>
        internal static bool TryReadAttachment(OnlineOwnedItemSnapshot item, ItemDefinition definition,
            out SpellAttachmentState attachment, out string error)
        {
            attachment = null; error = null;
            var state = item.AttachmentState;
            if (state == null) return true;
            if (definition.Kind != ItemKind.Spell || definition.SpellDefinition == null
                || definition.SpellDefinition.Kind != Game.Skills.SpellKind.Emit && definition.SpellDefinition.Kind != Game.Skills.SpellKind.StaticProjectile
                || state.SchemaVersion != 1 || string.IsNullOrWhiteSpace(state.ContainerId) || state.Dose == 0)
                return Fail("供料附件必须属于发射/静态发射卡，使用Schema1、非空来源身份和正剂量。", out error);
            attachment = new SpellAttachmentState(state.ContainerId, state.Dose, state.Enabled); return true;
        }

        private static bool IsCarriedClosure(ItemOwnershipStore store, string playerId)
        {
            foreach (var entry in store)
            {
                var root = entry.Location;
                for (int depth = 0; root.HasParentInstance; depth++)
                    if (depth >= store.Count || !store.TryGetLocation(root.OwnerId, out root)) return false;
                if (root.OwnerId != playerId || root.Kind != ItemLocationKind.EquippedWand
                    && root.Kind != ItemLocationKind.EquippedBackpack && root.Kind != ItemLocationKind.EquippedTool
                    && root.Kind != ItemLocationKind.SpellStorage) return false;
            }
            return true;
        }

        private static bool Fail(string message, out string error) { error = message; return false; }
        private static bool ValidHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            for (int i = 0; i < hash.Length; i++)
                if (!(hash[i] >= '0' && hash[i] <= '9') && !(hash[i] >= 'a' && hash[i] <= 'f')) return false;
            return true;
        }
    }
}
