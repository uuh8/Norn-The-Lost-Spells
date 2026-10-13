using System;
using System.Collections.Generic;
using Game.Core;
namespace Game.Run
{
    /// <summary>在独立候选上转移资产并持久化；加载Scene失败可回滚，磁盘失败不改变当前携带物。</summary>
    public sealed class RaidPreparationService
    {
        public ProfilePersistenceResult TryPrepare(SinglePlayerProfile profile, ProfileSaveRepository repository, RaidDefinition definition,
            ProtectedSpellSelection selection, out SinglePlayerProfile next)
        {
            next = null;
            if (profile == null || repository == null || definition == null || !definition.IsValid || selection == null
                || profile.PendingRaid != null || profile.Items.IsResourceBusy || profile.Items.HasWorldTransit || profile.PendingMaterialOutputs > 0)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked, "当前不能出发：检查配置、事务和未投放材料。");
            var carried = new List<InventoryEntry>(); var hub = new List<string>(); int wands = 0, bags = 0;
            foreach (var entry in profile.Items)
            {
                if (!RaidProfileOps.IsCarried(profile, entry.Item.InstanceId)) { hub.Add(entry.Item.InstanceId); continue; }
                carried.Add(entry); if (entry.Location.Kind == ItemLocationKind.EquippedWand) wands++;
                if (entry.Location.Kind == ItemLocationKind.EquippedBackpack) bags++;
            }
            string[] protection = selection.CopyIds();
            if (wands < 1 || wands > 3 || bags != 1 || protection.Length > definition.ProtectedSlots)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "出发需1至3杖、一个装备背包；保护名额不能超限。");
            for (int i = 0; i < protection.Length; i++) if (!RaidProfileOps.IsCarried(profile, protection[i]))
                return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "保护对象必须是本次实际携带的原卡。");
            var candidate = RaidProfileOps.Clone(profile); string raid = Guid.NewGuid().ToString("N"), actor = "raid." + raid;
            var changes = new List<InventoryEntry>();
            foreach (var entry in candidate.Items) if (entry.Location.Kind == ItemLocationKind.EquippedWand || entry.Location.Kind == ItemLocationKind.EquippedBackpack || entry.Location.Kind == ItemLocationKind.SpellStorage || entry.Location.Kind == ItemLocationKind.EquippedTool)
                changes.Add(new InventoryEntry(entry.Item, new ItemLocation(entry.Location.Kind, actor, 0, 0, 0, entry.Location.SlotIndex)));
            if (!candidate.Items.TryApply(changes, candidate.Items.Version).Success) return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData);
            candidate.ActorId = actor;
            var eligibility = new RaidSpellEligibility(candidate);
            if (!eligibility.TryBegin(raid, out _)) return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData, "本局资格/空间预检失败，未扣资产。");
            var qualified = new List<string>(); foreach (var entry in candidate.Items) if (eligibility.IsEligible(entry.Item.InstanceId)) qualified.Add(entry.Item.InstanceId);
            // 本局资格已一次性付清；正式会话接纳后按固定名单恢复策略，不再次消耗资格。
            candidate.Items.CommitPolicy = null;
            candidate.PendingRaid = new PendingRaidRecord { RaidId = raid, HubActorId = profile.ActorId, RaidActorId = actor,
                Phase = RaidPhase.Departing, ProtectedIds = protection, HubAssetIds = hub.ToArray(), QualifiedIds = qualified.ToArray(),
                Rollback = ProfileAssetSnapshot.From(ProfileSaveMapper.ToDto(profile, profile.SaveGeneration)) };
            var saved = repository.TrySave(candidate); if (saved.Success) next = candidate; return saved;
        }
        public ProfilePersistenceResult TryRollback(SinglePlayerProfile profile, ProfileSaveRepository repository, out SinglePlayerProfile next)
        {
            next = null; var pending = profile.PendingRaid;
            if (pending == null || pending.Phase != RaidPhase.Departing || pending.Rollback == null)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked);
            var restore = ProfileSaveMapper.TryRestore(pending.Rollback.ToDto(), profile.Items.Catalog, out var candidate); if (!restore.Success) return restore;
            candidate.ConfirmSaved(profile.SaveGeneration);
            var saved = repository.TrySave(candidate); if (saved.Success) next = candidate; return saved;
        }
    }
}
