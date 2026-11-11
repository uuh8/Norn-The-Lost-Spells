using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Inventory;

/// <summary>
/// 业务冻结门：完整验证真实携带闭包。Shared 只描述值，业务服仍须验证父链、占格与组分；
/// DTO 有字段、或者某个 ContentId 已注册，都不等于该实例已取得携带/材料权限。
/// </summary>
public static class OwnedCombatAssetRules
{
    public static void Require(OnlineOwnedAssetSnapshot? assets, string player, string gameplayHash, ContentManifestValidator content, bool frozen)
        => RequireCore(assets, player, gameplayHash, content, frozen, false);

    /// <summary>
    /// 只供已认证DS检查点使用。原Require有意保持通用模板严格门；可信持久Profile/Entry另走RequirePersisted。
    /// 此入口保真原副本Roll，不为客户端开放任意属性写入，也不代替持久模式/侧表的来源验证。
    /// </summary>
    internal static void RequireCheckpoint(OnlineOwnedAssetSnapshot? assets, string player, string gameplayHash, ContentManifestValidator content)
        => RequireCore(assets, player, gameplayHash, content, true, true);

    /// <summary>
    /// 只供原schema1/2检查点的历史值验证。缺币字段是未知事实，不能升级成当前余额零；
    /// 原业务调用/收据/Head仍可处理既有形状，当前入局与DS执行始终使用V3。
    /// </summary>
    internal static void RequirePreCurrencyCheckpoint(OnlineOwnedAssetSnapshot? assets, string player, string gameplayHash, ContentManifestValidator content)
        => RequireCore(assets, player, gameplayHash, content, true, true, true);

    /// <summary>
    /// 只供原持久读取/冻结Entry使用：模式与侧表已由InventoryService在同一数据库快照完整核查。
    /// 这里复用实例属性/容量规则，不放宽通用Require，也不产生客户端属性写入口。
    /// </summary>
    internal static void RequirePersisted(OnlineOwnedAssetSnapshot? assets, string player, string gameplayHash, ContentManifestValidator content)
        => RequireCore(assets, player, gameplayHash, content, true, true);

    /// <summary>账号整备只校验服务端读出的完整父闭包，不虚构Run/Actor；仓库父包/杖保留原子链。</summary>
    internal static void RequireAccount(OnlineOwnedAssetSnapshot assets, string player, string gameplayHash, ContentManifestValidator content)
        => RequireCore(assets, player, gameplayHash, content, false, true, false, true);

    private static void RequireCore(OnlineOwnedAssetSnapshot? assets, string player, string gameplayHash,
        ContentManifestValidator content, bool frozen, bool checkpoint, bool preCurrency = false, bool account = false)
    {
        uint schema = preCurrency ? OnlineOwnedAssetSnapshot.PreCurrencySchemaVersion : OnlineOwnedAssetSnapshot.CurrentSchemaVersion;
        if (assets == null || assets.SchemaVersion != schema || assets.PlayerId != player || assets.InventoryVersion == 0 ||
            (preCurrency ? assets.Gold != 0 || assets.CurrencyVersion != 0 : assets.Gold < 0 || assets.CurrencyVersion == 0) ||
            string.IsNullOrEmpty(assets.InventoryId) || !IsHash(gameplayHash) || assets.GameplayConfigHash != gameplayHash ||
            assets.Items == null || assets.Items.Length > OnlineOwnedAssetSnapshot.MaximumItems
                + (account ? OnlineInventoryAreas.BackpackCapacity + OnlineInventoryAreas.WarehouseCapacity : 0) || assets.SelectedWandInstanceId == null ||
            frozen && (string.IsNullOrEmpty(assets.RunId) || assets.ActorId is < 1 or > 4))
            throw new OnlineFailure(409, "CombatAssetsUnavailable");

        var ids = new Dictionary<string, OnlineOwnedItemSnapshot>(assets.Items.Length, StringComparer.Ordinal);
        var definitions = new Dictionary<string, ContentManifestEntry>(assets.Items.Length, StringComparer.Ordinal);
        var slots = new HashSet<(int Kind, string Parent, int Slot)>();
        foreach (var item in assets.Items)
        {
            if (item == null || !IsId(item.InstanceId) || string.IsNullOrEmpty(item.ContentId) || item.Revision == 0 ||
                item.ParentInstanceId == null || item.RemainingCharges < 0 || !ids.TryAdd(item.InstanceId, item))
                throw new OnlineFailure(409, "InvalidCombatAsset");

            var definition = content.RequireItem(item.ContentId);
            definitions.Add(item.InstanceId, definition);
            if (item.EntryBoundBackpack && (definition.Kind != 2 || item.LocationKind != 4 && !(account && item.LocationKind == 0)))
                throw new OnlineFailure(409, "InvalidCombatAsset");
            if (checkpoint) RequireCheckpointItemState(item, definition, content);
            else RequireItemState(item, definition);
            RequireLocation(item, definition, account);
            // 包内实例的 Slot=-1 不是槽位：同一包中可以有多个网格实例，也可有合法同格堆叠。
            // 装备/法杖程序仍按稳定位置编号与父实例隔离唯一槽，不能因支持网格而放宽旧约束。
            if (item.LocationKind != 1 && !(account && item.LocationKind == 5) && !slots.Add((item.LocationKind, item.ParentInstanceId, item.Slot)))
                throw new OnlineFailure(409, "InvalidCombatAsset");
        }

        // 原单机允许最后一根杖进包/落地，空手是当前事实而不是缺失应补默认杖。
        // 有装备杖仍必须选择其中一根；没有装备杖只能为空选中，不能保留旧ID制造可施法来源。
        bool hasEquippedWand = assets.Items.Any(i => i.LocationKind == 3);
        if (hasEquippedWand ? !ids.TryGetValue(assets.SelectedWandInstanceId, out var selected) || selected.LocationKind != 3
                            : assets.SelectedWandInstanceId.Length != 0)
            throw new OnlineFailure(409, "SelectedWandNotOwned");

        // 先收齐全部身份再验父链，JSON 排列顺序不能影响合法性。类型约束把链限定为
        // 装备包→包内备用杖→安装卡，或装备杖→安装卡，不递归遍历未经验证的任意父图。
        foreach (var item in assets.Items)
        {
            if (item.LocationKind == 1)
            {
                if (!ids.TryGetValue(item.ParentInstanceId, out var bag) || bag.LocationKind != 4 && !(account && bag.LocationKind is 0 or 5)
                    || definitions[bag.InstanceId].Kind != 2)
                    throw new OnlineFailure(409, "CombatParentNotOwned");
            }
            else if (item.LocationKind == 2)
            {
                if (!ids.TryGetValue(item.ParentInstanceId, out var wand) || definitions[wand.InstanceId].Kind != 1 ||
                    wand.LocationKind is not (1 or 3) && !(account && wand.LocationKind is 0 or 5))
                    throw new OnlineFailure(409, "CombatParentNotOwned");
                // 上限来自该父杖的可信规则，不再只看全局32；备用杖的程序也属于同一携带闭包。
                int capacity = checkpoint && !wand.UseTemplateWandStats ? wand.WandStats!.ProgramCapacity : definitions[wand.InstanceId].WandProgramCapacity;
                if (item.Slot >= capacity)
                    throw new OnlineFailure(409, "InvalidCombatCard");
            }
        }
        RequireGrid(assets.Items, ids, definitions);
    }

    internal static void RequireItemState(OnlineOwnedItemSnapshot item, ContentManifestEntry definition)
    {
        RequireMaterialState(item.MaterialState, definition);
        RequireAttachmentState(item.AttachmentState, definition);
        if (definition.Kind == 1)
        {
            // 这是通用模板门，故意拒绝实例roll；可信持久读回须先核查模式/侧表再走RequirePersisted。
            if (item.RemainingCharges != 0 || item.HasUnlimitedCharges || !item.UseTemplateWandStats || item.WandStats != null)
                throw new OnlineFailure(409, "InvalidCombatWand");
        }
        else
        {
            if (item.UseTemplateWandStats || item.WandStats != null)
                throw new OnlineFailure(409, definition.Kind == 0 ? "InvalidCombatCard" : "InvalidCombatAsset");
            if (definition.Kind != 0 && (item.RemainingCharges != 0 || item.HasUnlimitedCharges))
                throw new OnlineFailure(409, "InvalidCombatAsset");
        }
        if (definition.Kind != 3)
        {
            // 不能按Kind在读取时丢弃坏State；完整读出后在权限门拒绝，否则非法数据会伪装为正常卡。
            if (item.ContainerState != null) throw new OnlineFailure(409, "InvalidCombatAsset");
            return;
        }
        var state = item.ContainerState;
        if (state == null || state.SchemaVersion != 1 || state.Components == null || state.Components.Length > 8)
            throw new OnlineFailure(409, "InvalidCombatContainer");
        ushort seen = 0;
        ulong total = 0;
        foreach (var part in state.Components)
        {
            if (part == null || part.MaterialKey is < 1 or > 8 || part.Quantity == 0 ||
                (seen & (1 << part.MaterialKey)) != 0 || (definition.ContainerMaterialsMask & (1 << part.MaterialKey)) == 0)
                throw new OnlineFailure(409, "InvalidCombatContainer");
            seen |= (ushort)(1 << part.MaterialKey);
            try { total = checked(total + part.Quantity); }
            catch (OverflowException) { throw new OnlineFailure(409, "InvalidCombatContainer"); }
            if (total > definition.ContainerCapacityGmu) throw new OnlineFailure(409, "InvalidCombatContainer");
        }
        // 零项是显式空容器；没有State与合法空数组严格区分，不从模板补材料。
    }

    /// <summary>
    /// 原DungeonLootPlanner会把完整WandStats放进真实实例。这里只复用Unity WandStats.IsValid的值域与
    /// Catalog.ValidateWandTraits的可信Spell来源约束，不在业务服重Roll或硬编码某张地图的采样范围。
    /// 具体范围由已匹配GameplayHash的DS原RollProfile裁决，已知实例后续不可改写由检查点归属门验证。
    /// </summary>
    internal static void RequireCheckpointItemState(OnlineOwnedItemSnapshot item, ContentManifestEntry definition,
        ContentManifestValidator content)
    {
        if (definition.Kind != 1 || item.UseTemplateWandStats)
        {
            RequireItemState(item, definition); return;
        }
        RequireMaterialState(item.MaterialState, definition);
        RequireAttachmentState(item.AttachmentState, definition);
        var stats = item.WandStats;
        if (item.RemainingCharges != 0 || item.HasUnlimitedCharges || item.ContainerState != null || stats == null
            || !float.IsFinite(stats.MaxMana) || stats.MaxMana <= 0
            || !NonNegative(stats.ManaRegenPerSecond) || !NonNegative(stats.BaseCastDelay)
            || !NonNegative(stats.BaseReloadTime) || !NonNegative(stats.BaseSpreadDegrees)
            || stats.ProgramCapacity is < 1 or > 32 || stats.BaseDraws < 1 || stats.BaseDraws > stats.ProgramCapacity
            || stats.AlwaysCastContentIds == null || stats.AlwaysCastContentIds.Length > 4)
            throw new OnlineFailure(409, "InvalidCombatWand");
        foreach (string trait in stats.AlwaysCastContentIds)
        {
            if (!ContentManifestRules.IsContentId(trait) || content.RequireItem(trait).Kind != 0)
                throw new OnlineFailure(409, "InvalidCombatWand");
        }
    }

    private static bool NonNegative(float value) => float.IsFinite(value) && value >= 0;

    /// <summary>
    /// Profile读取与入局冻结共用真实固体状态门。Key/容量属于可信目录，Quantity只能来自持有行，
    /// 缺状态不是空材料，也不能从容量/模板/商店历史推导一次新发放；非Material同样不能夹带状态。
    /// </summary>
    internal static void RequireMaterialState(OnlineOwnedMaterialStateSnapshot? state, ContentManifestEntry definition)
    {
        if (definition.Kind != 4)
        {
            if (state != null) throw new OnlineFailure(409, "InvalidCombatAsset");
            return;
        }
        if (state == null || state.SchemaVersion != 1 || state.MaterialKey is < 1 or > 8
            || state.MaterialKey != definition.MaterialKey || state.Quantity == 0 || state.Quantity > definition.MaterialItemCapacityGmu)
            throw new OnlineFailure(409, "InvalidCombatMaterial");
    }

    /// <summary>
    /// 附着只是某张法术卡的可选设置，没有材料数量所有权。来源可能已移走/销毁，不在此查询当前闭包，
    /// 只保留合法身份与精确正剂量；是否实际供料由DS在施法事务中按当前背包瓶裁决。
    /// </summary>
    internal static void RequireAttachmentState(OnlineOwnedSpellAttachmentStateSnapshot? state, ContentManifestEntry definition)
    {
        if (state == null) return;
        if (definition.Kind != 0) throw new OnlineFailure(409, "InvalidCombatAsset");
        if (state.SchemaVersion != 1 || !IsId(state.ContainerId) || state.Dose == 0)
            throw new OnlineFailure(409, "InvalidCombatAttachment");
    }

    private static void RequireLocation(OnlineOwnedItemSnapshot item, ContentManifestEntry definition, bool account = false)
    {
        // 私人据点地面仅在账号完整视图出现；入局携带合同仍严格拒绝位置5。
        if (account && item.LocationKind == 5)
        {
            if (item.ParentInstanceId.Length != 0 || item.Slot != -1 || item.X != 0 || item.Y != 0 || item.QuarterTurns != 0)
                throw new OnlineFailure(409, "InvalidCombatLocation");
            return;
        }
        if (account && item.LocationKind == 0)
        {
            if (item.ParentInstanceId.Length != 0 || item.Slot is < 0 or >= OnlineInventoryAreas.WarehouseCapacity
                || item.X != 0 || item.Y != 0 || item.QuarterTurns != 0) throw new OnlineFailure(409, "InvalidCombatLocation");
            return;
        }
        if (item.LocationKind == 1)
        {
            if (definition.Kind == 2 || !IsId(item.ParentInstanceId) || item.Slot != -1 ||
                item.X < 0 || item.Y < 0 || item.QuarterTurns is < 0 or > 3)
                throw new OnlineFailure(409, "InvalidCombatLocation");
            return;
        }
        if (item.X != 0 || item.Y != 0 || item.QuarterTurns != 0)
            throw new OnlineFailure(409, "InvalidCombatAsset");
        switch (item.LocationKind)
        {
            case 3:
                if (definition.Kind != 1 || item.ParentInstanceId.Length != 0 || item.Slot is < 0 or >= 3)
                    throw new OnlineFailure(409, "InvalidCombatWand");
                break;
            case 2:
                if (definition.Kind != 0 || !IsId(item.ParentInstanceId) || item.Slot is < 0 or >= 32)
                    throw new OnlineFailure(409, "InvalidCombatCard");
                break;
            case 4:
                if (definition.Kind != 2 || item.ParentInstanceId.Length != 0 || item.Slot != 0)
                    throw new OnlineFailure(409, "InvalidCombatLocation");
                break;
            case 7:
                // 原SpellStorage是玩家独立根槽，正式Catalog为12格；Parent字段只表达实例父链。
                // 不把玩家ID或法杖ID当Parent，也不与WandSlot共用槽；零网格和槽唯一性由外层共用门校验。
                if (definition.Kind != 0 || item.ParentInstanceId.Length != 0 || item.Slot is < 0 or >= 12)
                    throw new OnlineFailure(409, "InvalidCombatLocation");
                break;
            case 8:
                if (definition.UseKind == 0 || item.ParentInstanceId.Length != 0 || item.Slot is < 0 or >= 7)
                    throw new OnlineFailure(409, "InvalidCombatLocation");
                break;
            default:
                // 仓库不属于携带闭包；legacy平面Backpack也不能靠空Parent伪装成真实网格。
                throw new OnlineFailure(409, "InvalidCombatLocation");
        }
    }

    private static void RequireGrid(OnlineOwnedItemSnapshot[] items, Dictionary<string, OnlineOwnedItemSnapshot> ids,
        Dictionary<string, ContentManifestEntry> definitions)
    {
        var occupied = new Dictionary<(string Bag, int X, int Y), int>();
        var groups = new List<GridGroup>();
        foreach (var item in items)
        {
            if (item.LocationKind != 1) continue;
            var shape = definitions[item.InstanceId];
            var bag = definitions[ids[item.ParentInstanceId].InstanceId];
            int width = (item.QuarterTurns & 1) == 0 ? shape.ShapeWidth : shape.ShapeHeight;
            int height = (item.QuarterTurns & 1) == 0 ? shape.ShapeHeight : shape.ShapeWidth;
            // 与 Unity InventoryGrid 一样先验旋转后的外界；long防止损坏坐标通过int加法溢出绕过。
            if ((long)item.X + width > bag.BackpackWidth || (long)item.Y + height > bag.BackpackHeight)
                throw new OnlineFailure(409, "InvalidCombatLocation");
            int existing = -1;
            bool touchesEmpty = false;
            foreach (var cell in shape.OccupiedCells)
            {
                var point = RotatedCell(shape, cell, item.QuarterTurns);
                if (!occupied.TryGetValue((item.ParentInstanceId, item.X + point.X, item.Y + point.Y), out int group))
                    touchesEmpty = true;
                else if (existing != -1 && existing != group)
                    throw new OnlineFailure(409, "InvalidCombatAsset");
                else existing = group;
            }
            if (existing != -1)
            {
                var group = groups[existing];
                // 原有有限堆叠只允许同一位置、同配置与相同次数/资格状态；空洞相交不算重叠。
                // Wand/Bag/Container的可信MaxStack本来就是1，不能借相同组分堆叠瓶子。
                if (touchesEmpty || group.Count >= shape.MaxStack || !SameStack(group.Item, item))
                    throw new OnlineFailure(409, "InvalidCombatAsset");
                group.Count++;
                continue;
            }
            int next = groups.Count;
            groups.Add(new GridGroup(item));
            foreach (var cell in shape.OccupiedCells)
            {
                var point = RotatedCell(shape, cell, item.QuarterTurns);
                occupied.Add((item.ParentInstanceId, item.X + point.X, item.Y + point.Y), next);
            }
        }
    }

    private static (int X, int Y) RotatedCell(ContentManifestEntry shape, ContentShapeCell cell, int turns)
    {
        int x = cell.X, y = cell.Y, width = shape.ShapeWidth, height = shape.ShapeHeight;
        for (int i = 0; i < turns; i++)
        {
            (x, y) = (height - 1 - y, x);
            (width, height) = (height, width);
        }
        return (x, y);
    }

    private static bool SameStack(OnlineOwnedItemSnapshot a, OnlineOwnedItemSnapshot b)
        => a.ContentId == b.ContentId && a.ParentInstanceId == b.ParentInstanceId &&
           a.X == b.X && a.Y == b.Y && a.QuarterTurns == b.QuarterTurns &&
           a.RemainingCharges == b.RemainingCharges && a.HasUnlimitedCharges == b.HasUnlimitedCharges &&
           (a.MaterialState == null ? b.MaterialState == null : b.MaterialState != null &&
            a.MaterialState.SchemaVersion == b.MaterialState.SchemaVersion && a.MaterialState.MaterialKey == b.MaterialState.MaterialKey &&
             a.MaterialState.Quantity == b.MaterialState.Quantity) &&
           (a.AttachmentState == null ? b.AttachmentState == null : b.AttachmentState != null &&
            a.AttachmentState.SchemaVersion == b.AttachmentState.SchemaVersion && a.AttachmentState.ContainerId == b.AttachmentState.ContainerId &&
            a.AttachmentState.Dose == b.AttachmentState.Dose && a.AttachmentState.Enabled == b.AttachmentState.Enabled);

    private sealed class GridGroup(OnlineOwnedItemSnapshot item)
    {
        public readonly OnlineOwnedItemSnapshot Item = item;
        public int Count = 1;
    }

    private static bool IsId(string value) => Guid.TryParseExact(value, "N", out var parsed) && parsed.ToString("N") == value;
    private static bool IsHash(string value) => value != null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
