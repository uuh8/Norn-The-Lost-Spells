using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Run
{
    /// <summary>堆叠是多个实例共享格位的视图，分组不生成资产 ID 或虚构数量。</summary>
    public static class ItemStackOps
    {
        // 组分归属于具体瓶子。空瓶也不堆叠，否则点击余量无法明确指向哪一个容器。
        public static bool AreCompatible(RuntimeItemState a, RuntimeItemState b) => a != null && b != null
            && a.Composition==null && b.Composition==null && HaveMatchingState(a,b);
        internal static bool HaveMatchingState(RuntimeItemState a, RuntimeItemState b) => a != null && b != null
            && string.Equals(a.ContentId, b.ContentId, StringComparison.Ordinal)
            && a.RemainingCharges == b.RemainingCharges && a.HasUnlimitedCharges == b.HasUnlimitedCharges
            && MatchingWandStats(a,b)
            && string.Equals(a.StackStateKey, b.StackStateKey, StringComparison.Ordinal)
            && (a.Attachment == null ? b.Attachment == null : a.Attachment.SameAs(b.Attachment))
            && (a.Composition == null ? b.Composition == null || b.Composition.Total == 0 : a.Composition.SameAs(b.Composition))
            && (a.Material == null ? b.Material == null : b.Material != null && a.Material.MaterialKey == b.Material.MaterialKey
                && a.Material.Quantity == b.Material.Quantity);
        internal static bool MatchingWandStats(RuntimeItemState a,RuntimeItemState b)=>a.RolledWandStats.HasValue==b.RolledWandStats.HasValue
            &&(!a.RolledWandStats.HasValue||a.RolledWandStats.Value.SameAs(b.RolledWandStats.Value));
        public static InventoryOperationResult TryMerge(ItemOwnershipStore store, string memberId, string targetId, uint expectedVersion)
        {
            if(store.TryGet(memberId,out var material)&&material.Material!=null)return MaterialStackOps.TryMerge(store,memberId,targetId,expectedVersion);
            if (expectedVersion != store.Version) return store.Result(false, InventoryFailureReason.StaleVersion);
            if (!store.TryGet(memberId, out RuntimeItemState member) || !store.TryGet(targetId, out RuntimeItemState target))
                return store.Result(false, InventoryFailureReason.NotFound);
            store.TryGetLocation(targetId, out ItemLocation location);
            if (location.Kind != ItemLocationKind.Backpack) return store.Result(false, InventoryFailureReason.InvalidOwnership);
            if (!AreCompatible(member, target)) return store.Result(false, InventoryFailureReason.InvalidState);
            return store.TryMove(memberId, location, expectedVersion);
        }
        public static InventoryOperationResult TrySplit(ItemOwnershipStore store, string memberId, string backpackId,
            in GridPlacement placement, uint expectedVersion)
        {
            if (expectedVersion != store.Version) return store.Result(false, InventoryFailureReason.StaleVersion);
            if (!store.TryGetLocation(memberId, out ItemLocation source)) return store.Result(false, InventoryFailureReason.NotFound);
            ItemLocation destination = placement.InBackpack(backpackId);
            if (source.Kind != ItemLocationKind.Backpack || source == destination || CountAt(store, source) < 2)
                return store.Result(false, InventoryFailureReason.InvalidState);
            return store.TryMove(memberId, destination, expectedVersion);
        }
        public static InventoryOperationResult TryMoveStack(ItemOwnershipStore store, string memberId, string backpackId,
            in GridPlacement placement, uint expectedVersion)
            => TryMoveStack(store, memberId, placement.InBackpack(backpackId), expectedVersion);

        public static InventoryOperationResult TryMoveStack(ItemOwnershipStore store, string memberId,
            in ItemLocation destination, uint expectedVersion)
        {
            if(store.TryGet(memberId,out var material)&&material.Material!=null)return store.TryMove(memberId,destination,expectedVersion);
            if (expectedVersion != store.Version) return store.Result(false, InventoryFailureReason.StaleVersion);
            if (!store.TryGetLocation(memberId, out ItemLocation source)) return store.Result(false, InventoryFailureReason.NotFound);
            if (source.Kind != ItemLocationKind.Backpack) return store.Result(false, InventoryFailureReason.InvalidOwnership);
            var changes = new List<InventoryEntry>();
            foreach (InventoryEntry entry in store) if (entry.Location == source) changes.Add(new InventoryEntry(entry.Item, destination));
            return store.TryApply(changes, expectedVersion);
        }
        public static InventoryOperationResult TryChangeState(ItemOwnershipStore store, RuntimeItemState changedState,
            in ItemLocation destination, uint expectedVersion)
        {
            // 次数/内容和格位在同一候选表中；空间不足时旧不可变状态仍在表内。
            return store.TryApply(new[] { new InventoryEntry(changedState, destination) }, expectedVersion);
        }
        public static int CountAt(ItemOwnershipStore store, in ItemLocation location)
        {
            int count = 0;
            foreach (InventoryEntry entry in store) if (entry.Location == location) count++;
            return count;
        }
    }
}
