using System.Collections.Generic;
using Game.Core;
namespace Game.Run
{
    /// <summary>仅裁决真实携带与保护原卡；先可靠保存候选，Unity结果展示/世界卸载由协调者随后执行。</summary>
    public sealed class RaidSettlementService
    {
        public ProfilePersistenceResult TrySettle(SinglePlayerProfile profile, ProfileSaveRepository repository, in RaidSettlementRequest request,
            out SinglePlayerProfile next, out RaidSettlementReceipt receipt)
        {
            next = null; receipt = null;
            if (profile.LastRaidReceipt != null && profile.LastRaidReceipt.RaidId == request.RaidId)
            { next = profile; receipt = profile.LastRaidReceipt; return new ProfilePersistenceResult(ProfilePersistenceStatus.Success); }
            var pending = profile.PendingRaid;
            if (pending == null || pending.RaidId != request.RaidId || request.Outcome > RaidOutcome.Crash || request.DefeatedEnemies < 0 || profile.Items.IsResourceBusy)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "结算范围/资源状态非法。");
            var kept = new List<InventoryEntry>(); int returned = 0, lost = 0;
            foreach (var entry in profile.Items)
            {
                if (pending.IsHubAsset(entry.Item.InstanceId)) { kept.Add(entry); continue; }
                // 实例仍在世界中不等于最终携带；D05采用丢弃未拾回不寄回，名单不替换成新同名卡。
                bool carried = RaidProfileOps.IsCarried(profile, entry.Item.InstanceId);
                profile.Items.Catalog.TryGetRules(entry.Item.ContentId, out var rules);
                bool exists = rules.Kind != ItemKind.Spell || entry.Item.HasUnlimitedCharges || entry.Item.RemainingCharges > 0;
                bool equippedBackpack=entry.Location.Kind==ItemLocationKind.EquippedBackpack;
                if (carried && exists && (request.Outcome == RaidOutcome.Success || equippedBackpack || pending.Protects(entry.Item.InstanceId)))
                {
                    ItemLocation location = entry.Location;
                    // 死亡只保留背包本体并恢复Hub角色归属；内容仍逐条裁决，不能随父亲一起被保护。
                    if (request.Outcome != RaidOutcome.Success && !equippedBackpack) location = ItemLocation.Warehouse;
                    else if (location.Kind == ItemLocationKind.EquippedWand || location.Kind == ItemLocationKind.EquippedBackpack || location.Kind == ItemLocationKind.SpellStorage || location.Kind == ItemLocationKind.EquippedTool)
                        location = new ItemLocation(location.Kind, pending.HubActorId, 0, 0, 0, location.SlotIndex);
                    kept.Add(new InventoryEntry(entry.Item, location)); returned++;
                }
                else lost++;
            }
            if (!RaidProfileOps.RestoreEntries(profile, kept, pending.HubActorId, out var candidate))
                return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "结算候选不合法，未提交。");
            receipt = new RaidSettlementReceipt { RaidId = request.RaidId, Outcome = request.Outcome,
                Returned = returned, Lost = lost, Generation = profile.SaveGeneration + 1 };
            candidate.LastRaidReceipt = receipt;
            // D07推荐配置：真实进展随可靠成功结算一次保存；失败/重复收据/空跑不造补货机会。
            if (request.Outcome == RaidOutcome.Success && (request.CollectedGmu > 0 || request.DefeatedEnemies > 0))
            {
                if (candidate.AdventureProgress == long.MaxValue) return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "经济进展溢出，未提交。");
                candidate.AdventureProgress++;
            }
            var result = repository.TrySave(candidate); if (result.Success) next = candidate; else receipt = null; return result;
        }
    }
}
