using System;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    public interface IInventoryReadOnly
    {
        string InventoryId { get; }
        string ActorId { get; }
        uint Version { get; }
        int Count { get; }
        int WarehouseCount { get; }
        bool TryGetView(string instanceId, out InventoryItemSnapshot view);
        int CopyWarehouseTo(InventoryItemSnapshot[] buffer);
        int CopyItemsTo(InventoryItemSnapshot[] buffer);
    }
    /// <summary>只读空间提示；不执行资格扣费、采集距离或事务提交，最终仍由命令入口仲裁。</summary>
    public interface IInventoryPlacementPreview
    {
        InventoryFailureReason PreviewPlacement(string itemId,string bagId,in GridPlacement placement,bool wholeStack,uint expectedVersion);
    }
    /// <summary>UI 的不可变投影；不暴露 SO、归属表或资源写入函数。</summary>
    public readonly struct InventoryItemSnapshot
    {
        public readonly string InstanceId, ContentId, DisplayName;
        public readonly ItemKind Kind;
        public readonly ItemCategory Category;
        public readonly Sprite Icon;
        public readonly GameObject WorldModel;
        public readonly int RemainingCharges;
        public readonly bool HasUnlimitedCharges;
        public readonly bool IsQualificationSpell;
        public readonly bool IsStarterEquipment;
        /// <summary>业务入局免费包的可信实例标记；只展示/阻止移动，不从名称或ContentId猜测免费来源。</summary>
        public readonly bool EntryBoundBackpack;
        public readonly byte MaterialKey;
        public readonly ulong MaterialQuantity, ContainerTotal, ContainerCapacity;
        public readonly ItemLocation Location;
        public readonly InventoryItemRules Rules;
        public readonly SpellAttachmentState Attachment;
        // 固定属性随当前实例读模型冻结；UI不再按ContentId重新读取SO，也不把当前Mana/冷却混入模板。
        // WandStats构造时已经复制并只读封装trait，复制这个值不会暴露可写数组。
        public readonly WandStats? FixedWandStats;
        private readonly ItemCatalogIndex _displayCatalog;
        // 保留原公共四参签名，已有只读Adapter/测试反射调用无需因展示字段增加而改变。
        public InventoryItemSnapshot(RuntimeItemState item, in ItemLocation location, ItemDefinition definition, in InventoryItemRules rules)
            : this(item, location, definition, rules, null, null) { }
        internal InventoryItemSnapshot(RuntimeItemState item, in ItemLocation location, ItemDefinition definition, in InventoryItemRules rules,
            WandStats? fixedWandStats, ItemCatalogIndex displayCatalog)
        {
            InstanceId = item.InstanceId; ContentId = item.ContentId; Location = location; Kind = rules.Kind;
            Category = definition.Category;
            DisplayName = string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.ContentId : definition.DisplayName;
            // 法术已有指令Icon时直接复用；具体物品仍可覆盖，避免重复维护两张相同图片。
            Icon = definition.Icon!=null ? definition.Icon : definition.SpellDefinition!=null ? definition.SpellDefinition.Icon : null;
            WorldModel = definition.WorldModel;
            RemainingCharges = item.RemainingCharges; HasUnlimitedCharges = item.HasUnlimitedCharges; Rules = rules;
            IsQualificationSpell = rules.Kind == ItemKind.Spell && RaidSpellEligibility.IsQualification(definition.SpellDefinition);
            IsStarterEquipment = item.IsStarterEquipment;
            EntryBoundBackpack = item.EntryBoundBackpack;
            MaterialKey = item.Material?.MaterialKey ?? 0; MaterialQuantity = item.Material?.Quantity ?? 0;
            ContainerTotal = item.Composition?.Total ?? 0; ContainerCapacity = item.Composition?.Capacity ?? 0;
            Attachment = item.Attachment;
            FixedWandStats = rules.Kind == ItemKind.Wand && fixedWandStats.HasValue && fixedWandStats.Value.IsValid
                ? fixedWandStats : null;
            _displayCatalog = displayCatalog;
        }
        /// <summary>
        /// 属性/顺序仍是原实例ID事实；名称只是当前目录的只读表现标签，不把内部配置键当正常玩家文案。
        /// 仅详情离散打开时解析，不暴露SO或目录写接口、不构造每次查询的名称数组。
        /// </summary>
        public string GetFixedWandTraitDisplayName(int index)
        {
            if (!FixedWandStats.HasValue || index < 0 || index >= FixedWandStats.Value.AlwaysCastContentIds.Count) return string.Empty;
            string id = FixedWandStats.Value.AlwaysCastContentIds[index];
            if (_displayCatalog != null && _displayCatalog.TryGet(id, out var definition) && !string.IsNullOrWhiteSpace(definition.DisplayName))
                return definition.DisplayName;
            return id; // 仅缺失作者名称的旧目录沿用原物品显示fallback；不猜造另一张法术。
        }
    }
    public sealed class InventoryReadModel : IInventoryReadOnly, IInventoryPlacementPreview
    {
        private readonly SinglePlayerProfile _profile;
        public string InventoryId { get; }
        public string ActorId => _profile.ActorId;
        public uint Version => _profile.Items.Version;
        public int Count
        { get { if (_profile.PendingRaid == null) return _profile.Items.Count; int n = 0; foreach (var e in _profile.Items) if (!_profile.PendingRaid.IsHubAsset(e.Item.InstanceId)) n++; return n; } }
        public int WarehouseCount
        {
            get { if (_profile.PendingRaid != null) return 0; int count = 0; foreach (InventoryEntry entry in _profile.Items) if (entry.Location.Kind == ItemLocationKind.Warehouse) count++; return count; }
        }
        public InventoryReadModel(SinglePlayerProfile profile, string inventoryId)
        { _profile = profile ?? throw new ArgumentNullException(nameof(profile)); InventoryId = inventoryId; }
        public bool TryGetView(string instanceId, out InventoryItemSnapshot view)
        {
            view = default;
            if (_profile.PendingRaid != null && _profile.PendingRaid.IsHubAsset(instanceId)) return false;
            if (!_profile.Items.TryGet(instanceId, out RuntimeItemState item)) return false;
            _profile.Items.TryGetLocation(instanceId, out ItemLocation location);
            view = Snapshot(item, location); return true;
        }
        public int CopyWarehouseTo(InventoryItemSnapshot[] buffer) => Copy(buffer, true);
        public int CopyItemsTo(InventoryItemSnapshot[] buffer) => Copy(buffer, false);
        private int Copy(InventoryItemSnapshot[] buffer, bool warehouseOnly)
        {
            int required = warehouseOnly ? WarehouseCount : Count;
            if (buffer == null || buffer.Length < required) throw new ArgumentException("显示 Buffer 必须能容纳全部资产，不能静默截断。");
            int count = 0;
            foreach (InventoryEntry entry in _profile.Items)
                if ((_profile.PendingRaid == null || !_profile.PendingRaid.IsHubAsset(entry.Item.InstanceId))
                    && (!warehouseOnly || entry.Location.Kind == ItemLocationKind.Warehouse)) buffer[count++] = Snapshot(entry.Item, entry.Location);
            return count;
        }
        public InventoryFailureReason PreviewPlacement(string itemId,string bagId,in GridPlacement placement,bool wholeStack,uint expectedVersion)
        {
            var store=_profile.Items;
            if (expectedVersion!=store.Version) return InventoryFailureReason.StaleVersion;
            if (!store.TryGet(itemId,out var source) || !store.TryGetLocation(itemId,out var sourceLocation)
                || !store.TryGet(bagId,out var bag)) return InventoryFailureReason.NotFound;
            store.Catalog.TryGetRules(source.ContentId,out var rules);store.Catalog.TryGetRules(bag.ContentId,out var bagRules);
            if (bagRules.Kind!=ItemKind.Backpack) return InventoryFailureReason.WrongKind;
            var destination=placement.InBackpack(bagId);
            if (placement.X<0 || placement.Y<0 || (long)placement.X+rules.Shape.GetWidth(placement.QuarterTurns)>bagRules.BackpackWidth
                || (long)placement.Y+rules.Shape.GetHeight(placement.QuarterTurns)>bagRules.BackpackHeight) return InventoryFailureReason.OutOfBounds;
            int moving=wholeStack && sourceLocation.Kind==ItemLocationKind.Backpack?ItemStackOps.CountAt(store,sourceLocation):1;
            int members=moving;
            // 遍历真实占用与完整相容状态，空洞放行。具体struct Enumerator不装箱，也不生成候选归属表。
            foreach (var other in store)
            {
                if (other.Item.InstanceId==itemId || wholeStack && sourceLocation.Kind==ItemLocationKind.Backpack && other.Location==sourceLocation
                    || other.Location.Kind!=ItemLocationKind.Backpack || other.Location.OwnerId!=bagId) continue;
                store.Catalog.TryGetRules(other.Item.ContentId,out var otherRules);
                if (!InventoryGrid.Overlaps(rules.Shape,destination,otherRules.Shape,other.Location)) continue;
                // 材料堆按GMU补量，不适用普通实例MaxStack；预览必须与实际TryMove合并裁决一致。
                if(other.Location==destination&&MaterialStackOps.Compatible(source,other.Item))
                {if(other.Item.Material.Quantity>=otherRules.MaterialItemCapacity)return InventoryFailureReason.Capacity;continue;}
                if (other.Location!=destination || !ItemStackOps.AreCompatible(source,other.Item)) return InventoryFailureReason.Occupied;
                if (++members>rules.MaxStack) return InventoryFailureReason.Capacity;
            }
            return InventoryFailureReason.None;
        }
        private InventoryItemSnapshot Snapshot(RuntimeItemState item, in ItemLocation location)
        {
            _profile.Items.Catalog.TryGet(item.ContentId, out ItemDefinition definition);
            _profile.Items.Catalog.TryGetRules(item, out InventoryItemRules rules);
            WandStats? fixedStats = null;
            // 目录优先采用原实例roll，否则返回初始化时已冻结的模板；不在频繁查询时TryFreeze作者SO。
            if (rules.Kind == ItemKind.Wand && _profile.Items.Catalog.TryGetWandStats(item, out var stats)) fixedStats = stats;
            return new InventoryItemSnapshot(item, location, definition, rules, fixedStats, _profile.Items.Catalog);
        }
    }
}
