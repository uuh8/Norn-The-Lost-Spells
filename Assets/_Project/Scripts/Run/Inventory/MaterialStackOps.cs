using System;
using System.Collections.Generic;
using Game.Core;
namespace Game.Run
{
    /// <summary>固体堆叠合并真实GMU，不用多个有不同数量的实例共享一个格冒充合并。</summary>
    public static class MaterialStackOps
    {
        internal static RuntimeItemState WithQuantity(RuntimeItemState item,ulong quantity)=>new RuntimeItemState(item.InstanceId,item.ContentId,
            item.RemainingCharges,item.HasUnlimitedCharges,item.StackStateKey,item.Composition,new MaterialItemState(item.Material.MaterialKey,quantity),item.Attachment,item.RolledWandStats);
        internal static bool Compatible(RuntimeItemState a,RuntimeItemState b)=>a?.Material!=null&&b?.Material!=null
            &&a.ContentId==b.ContentId&&a.Material.MaterialKey==b.Material.MaterialKey&&a.StackStateKey==b.StackStateKey;
        public static InventoryOperationResult TryMerge(ItemOwnershipStore store,string sourceId,string targetId,uint version)
        {
            if(version!=store.Version)return store.Result(false,InventoryFailureReason.StaleVersion);
            if(store.IsResourceBusy)return store.Result(false,InventoryFailureReason.ResourceBusy);
            if(sourceId==targetId || !store.TryGet(sourceId,out var source)||!store.TryGet(targetId,out var target)||!Compatible(source,target))return store.Result(false,InventoryFailureReason.InvalidState);
            store.TryGetLocation(sourceId,out var from);store.TryGetLocation(targetId,out var to);
            if(from.Kind==ItemLocationKind.ThrownContainer||to.Kind==ItemLocationKind.ThrownContainer||to.Kind==ItemLocationKind.WorldItem)return store.Result(false,InventoryFailureReason.InvalidOwnership);
            store.Catalog.TryGetRules(target.ContentId,out var rules);ulong amount=Math.Min(source.Material.Quantity,rules.MaterialItemCapacity-target.Material.Quantity);
            if(amount==0)return store.Result(false,InventoryFailureReason.Capacity);
            var candidate=store.CopyCandidate();MergeCandidate(candidate,sourceId,targetId,amount);
            return store.CommitMaterialLayout(candidate,version);
        }
        internal static void MergeCandidate(Dictionary<string,InventoryEntry> candidate,string sourceId,string targetId,ulong amount)
        {
            var source=candidate[sourceId];var target=candidate[targetId];
            candidate[targetId]=new InventoryEntry(WithQuantity(target.Item,target.Item.Material.Quantity+amount),target.Location);
            if(amount==source.Item.Material.Quantity)candidate.Remove(sourceId);
            else candidate[sourceId]=new InventoryEntry(WithQuantity(source.Item,source.Item.Material.Quantity-amount),source.Location);
        }
    }
}
