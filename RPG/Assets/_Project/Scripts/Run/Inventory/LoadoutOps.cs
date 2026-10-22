using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Run
{
    /// <summary>装备迁移只管理归属；不驱动 Wizard 或复制法杖 Mana/程序状态。</summary>
    public static class LoadoutOps
    {
        public static InventoryOperationResult TryEquipWand(ItemOwnershipStore store, string itemId, string actorId, int slot, uint expectedVersion)
        {
            if (expectedVersion != store.Version) return store.Result(false, InventoryFailureReason.StaleVersion);
            if (!store.TryGet(itemId, out RuntimeItemState item)) return store.Result(false, InventoryFailureReason.NotFound);
            store.TryGetLocation(itemId, out ItemLocation source);
            // 世界物必须经代理拾取；否则交换装备会留下无法对应代理的地面资产。
            if (source.Kind != ItemLocationKind.Warehouse && source.Kind != ItemLocationKind.Backpack && source.Kind != ItemLocationKind.EquippedWand)
                return store.Result(false, InventoryFailureReason.InvalidOwnership);
            var destination = new ItemLocation(ItemLocationKind.EquippedWand, actorId, 0, 0, 0, slot);
            var changes = new List<InventoryEntry> { new InventoryEntry(item, destination) };
            foreach (InventoryEntry entry in store)
                if (entry.Location == destination && !string.Equals(entry.Item.InstanceId, itemId, StringComparison.Ordinal))
                    changes.Add(new InventoryEntry(entry.Item, source));
            return store.TryApply(changes, expectedVersion);
        }
        public static InventoryOperationResult TryReplaceBackpack(ItemOwnershipStore store, string itemId, string actorId, uint expectedVersion)
        {
            if (expectedVersion != store.Version) return store.Result(false, InventoryFailureReason.StaleVersion);
            if (!store.TryGet(itemId, out RuntimeItemState replacement)) return store.Result(false, InventoryFailureReason.NotFound);
            store.Catalog.TryGetRules(replacement.ContentId, out InventoryItemRules rules);
            if (rules.Kind != ItemKind.Backpack) return store.Result(false, InventoryFailureReason.WrongKind);
            store.TryGetLocation(itemId, out ItemLocation source);
            var equipped = new ItemLocation(ItemLocationKind.EquippedBackpack, actorId, 0, 0, 0, 0);
            if (source == equipped) return store.Result(true, InventoryFailureReason.None);
            if (source.Kind != ItemLocationKind.Warehouse) return store.Result(false, InventoryFailureReason.InvalidOwnership);
            string oldId = null;
            foreach (InventoryEntry entry in store) if (entry.Location == equipped) { oldId = entry.Item.InstanceId; break; }
            var changes = new List<InventoryEntry> { new InventoryEntry(replacement, equipped) };
            foreach (InventoryEntry entry in store)
            {
                if (string.Equals(entry.Item.InstanceId, oldId, StringComparison.Ordinal)) changes.Add(new InventoryEntry(entry.Item, ItemLocation.Warehouse));
                else if (entry.Location.Kind == ItemLocationKind.Backpack && string.Equals(entry.Location.OwnerId, oldId, StringComparison.Ordinal))
                {
                    ItemLocation location = entry.Location;
                    changes.Add(new InventoryEntry(entry.Item, new ItemLocation(ItemLocationKind.Backpack, itemId, location.X, location.Y, location.QuarterTurns, -1)));
                }
            }
            // 较小背包放不下时整批失败，不能先脱包或只搬一半内容。
            return store.TryApply(changes, expectedVersion);
        }
    }
}
