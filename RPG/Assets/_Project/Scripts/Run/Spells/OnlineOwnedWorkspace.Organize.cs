using System.Collections.Generic;
using Game.Core;

namespace Game.Run
{
    public sealed partial class OnlineOwnedWorkspace
    {
        /// <summary>
        /// DS整理只接受本人真实装备包，不接受客户端布局或实例列表。外层已经通过原命令账本、
        /// Authority/Frozen、版本及施法Prepare/Reservation门；这里再次核对中性字段和资产父链。
        /// 单机与DS都调用BackpackOrganizeOps，布局/GMU合并及Merged退休证明由同一个Store事务裁决。
        /// </summary>
        private OnlineInventoryCommandFailure OrganizeInventoryBackpack(in OnlineInventoryCommandIntent intent)
        {
            if(intent.TargetSlot!=0 || intent.TargetLocationKind!=0 || intent.WholeStack || intent.ToSpellStorage
                || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision!=0 || intent.TargetProgramRevision!=0 || intent.TransferBudget!=0
                || !Zero(intent.AimOrigin) || !Zero(intent.AimDirection) || !string.IsNullOrEmpty(intent.WorldOwnerId)
                || !string.IsNullOrEmpty(intent.TargetBackpackId) || intent.GridX!=0 || intent.GridY!=0
                || intent.QuarterTurns!=0 || intent.AutoPlace || !string.IsNullOrEmpty(intent.AttachmentContainerId)
                || intent.AttachmentDose!=0 || intent.AttachmentEnabled)
                return OnlineInventoryCommandFailure.InvalidInput;
            var store=_profile.Items;
            if(!store.TryGet(intent.ItemId,out var bag) || !store.TryGetLocation(intent.ItemId,out var location)
                || location.Kind!=ItemLocationKind.EquippedBackpack || location.OwnerId!=PlayerId
                || !store.Catalog.TryGet(bag.ContentId,out var definition) || definition.Kind!=ItemKind.Backpack)
                return OnlineInventoryCommandFailure.NotOwned;

            // 只缓存本次可能被耗尽合并的原材料ID。退休原因/数量不从差集推测，原CommitMaterialLayout
            // 已在提交前向IOnlineItemRetirementJournal预约Merged证据；这里仅在成功后清理Owned修订索引。
            List<string> materialIds=null;
            foreach(var entry in store)
                if(entry.Location.Kind==ItemLocationKind.Backpack && entry.Location.OwnerId==intent.ItemId
                    && entry.Item.Material!=null)
                {materialIds??=new List<string>();materialIds.Add(entry.Item.InstanceId);}
            uint before=InventoryVersion;
            var result=BackpackOrganizeOps.TryOrganize(store,intent.ItemId,intent.ExpectedInventoryVersion);
            if(!result.Success)return InventoryFailure(result.Reason);
            if(materialIds!=null)
                foreach(string id in materialIds)if(!store.TryGet(id,out _))_itemRevisions.Remove(id);
            SynchronizeInventorySelection();
            if(InventoryVersion!=before)NotifyGroundInventoryCommitted();
            return OnlineInventoryCommandFailure.None;
        }
    }
}
