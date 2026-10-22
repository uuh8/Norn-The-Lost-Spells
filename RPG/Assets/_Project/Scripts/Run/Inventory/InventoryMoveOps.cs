using Game.Core;

namespace Game.Run
{
    /// <summary>失败返回原状态；迁移不创建资产副本。</summary>
    public static class InventoryMoveOps
    {
        /// <summary>专用法术格交换完整实例，候选表一次验证；失败不改位置、次数或本局资格。</summary>
        public static InventoryOperationResult TryPlaceSpell(ItemOwnershipStore store, string itemId, in ItemLocation destination, uint expectedVersion)
        {
            var result = store.TryMove(itemId, destination, expectedVersion);
            if (result.Reason != InventoryFailureReason.Occupied ||
                (destination.Kind != ItemLocationKind.SpellStorage && destination.Kind != ItemLocationKind.WandSlot)) return result;
            if (!store.TryGet(itemId, out var source) || !store.TryGetLocation(itemId, out var sourceLocation)
                || sourceLocation.Kind == ItemLocationKind.WorldItem || sourceLocation.Kind == ItemLocationKind.ThrownContainer
                || !store.Catalog.TryGetRules(source.ContentId, out var rules) || rules.Kind != ItemKind.Spell) return result;
            foreach (var target in store)
                if (target.Location == destination)
                    return store.TryApply(new[] { new InventoryEntry(source, destination), new InventoryEntry(target.Item, sourceLocation) }, expectedVersion);
            return result;
        }
        public static InventoryOperationResult TryPlace(ItemOwnershipStore store, string itemId, string backpackId,
            in GridPlacement placement, uint expectedVersion) => store.TryMove(itemId, placement.InBackpack(backpackId), expectedVersion);
        public static InventoryOperationResult TryDrop(ItemOwnershipStore store, string itemId, string worldOwner, uint expectedVersion)
        {
            if (expectedVersion != store.Version) return store.Result(false, InventoryFailureReason.StaleVersion);
            if (!store.TryGetLocation(itemId, out ItemLocation source)) return store.Result(false, InventoryFailureReason.NotFound);
            if (source.Kind == ItemLocationKind.WorldItem || source.Kind == ItemLocationKind.ThrownContainer)
                return store.Result(false, InventoryFailureReason.InvalidOwnership);
            // 移动根对象后，装在其中的程序/内容仍通过原父链跟随，不重建其身份。
            return store.TryMove(itemId, new ItemLocation(ItemLocationKind.WorldItem, worldOwner, 0, 0, 0, -1), expectedVersion);
        }
    }
}
