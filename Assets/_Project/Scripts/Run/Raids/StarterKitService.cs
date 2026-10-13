using Game.Core;
namespace Game.Run
{
    /// <summary>只补基础配置的缺项，保留已有装备；免费身份写在实例上，M5交易权限必须继续消费该标记。</summary>
    public sealed class StarterKitService
    {
        public const string FreeStateKey = "starter-kit.v1";
        public ProfilePersistenceResult TrySupply(SinglePlayerProfile profile, ProfileSaveRepository repository, StarterKitDefinition kit,
            out SinglePlayerProfile next)
        {
            next = null;
            if (profile.PendingRaid != null || profile.Items.IsResourceBusy || profile.Items.HasWorldTransit || profile.PendingMaterialOutputs > 0 || kit == null || kit.Entries == null)
                return new ProfilePersistenceResult(ProfilePersistenceStatus.WriteBlocked);
            var candidate = RaidProfileOps.Clone(profile);
            for (int i = 0; i < kit.Entries.Length; i++)
            {
                var e = kit.Entries[i]; bool found = false;
                if (!candidate.Items.Catalog.TryGet(e.ContentId, out _)) return new ProfilePersistenceResult(ProfilePersistenceStatus.UnknownContent);
                foreach (var item in candidate.Items) if (item.Item.ContentId == e.ContentId && (!e.Unlimited || item.Item.HasUnlimitedCharges)
                    && (item.Item.HasUnlimitedCharges || item.Item.RemainingCharges > 0 || !candidate.Items.Catalog.TryGetRules(e.ContentId, out var r) || r.Kind != ItemKind.Spell)) { found = true; break; }
                if (found) continue;
                var itemState = RuntimeItemState.Create(e.ContentId, e.Charges, e.Unlimited);
                itemState = new RuntimeItemState(itemState.InstanceId, e.ContentId, e.Charges, e.Unlimited, FreeStateKey);
                if (!candidate.Items.TryAdd(itemState, ItemLocation.Warehouse)) return new ProfilePersistenceResult(ProfilePersistenceStatus.InvalidData);
            }
            AutoEquip(candidate, kit);
            var result = repository.TrySave(candidate); if (result.Success) next = candidate; return result;
        }
        private static void AutoEquip(SinglePlayerProfile profile, StarterKitDefinition kit)
        {
            string bag = null, wand = null;
            foreach (var entry in profile.Items)
            { if (entry.Location.Kind == ItemLocationKind.EquippedBackpack) bag = entry.Item.InstanceId;
                if (entry.Location.Kind == ItemLocationKind.EquippedWand && wand == null) wand = entry.Item.InstanceId; }
            // 只补空装备位，已有高级装备与内容保持原位。枚举期间不修改Store。
            if (bag == null) foreach (var entry in profile.Items) if (profile.Items.Catalog.TryGetRules(entry.Item.ContentId, out var r) && r.Kind == ItemKind.Backpack)
            { bag = entry.Item.InstanceId; break; }
            if (wand == null) foreach (var entry in profile.Items) if (profile.Items.Catalog.TryGetRules(entry.Item.ContentId, out var r) && r.Kind == ItemKind.Wand)
            { wand = entry.Item.InstanceId; break; }
            if (bag != null && profile.Items.TryGetLocation(bag, out var bl) && bl.Kind == ItemLocationKind.Warehouse)
                LoadoutOps.TryReplaceBackpack(profile.Items, bag, profile.ActorId, profile.Items.Version);
            if (wand != null && profile.Items.TryGetLocation(wand, out var wl) && wl.Kind == ItemLocationKind.Warehouse)
                LoadoutOps.TryEquipWand(profile.Items, wand, profile.ActorId, 0, profile.Items.Version);
            var ids = new System.Collections.Generic.List<string>(); foreach (var entry in profile.Items)
                if (entry.Location.Kind == ItemLocationKind.Warehouse)
                    for (int i = 0; i < kit.Entries.Length; i++) if (entry.Item.ContentId == kit.Entries[i].ContentId
                        && (!kit.Entries[i].Unlimited || entry.Item.HasUnlimitedCharges)) { ids.Add(entry.Item.InstanceId); break; }
            for (int i = 0; i < ids.Count; i++)
            {
                profile.Items.TryGet(ids[i], out var item); profile.Items.Catalog.TryGetRules(item.ContentId, out var rules);
                if (rules.Kind == ItemKind.Spell && wand != null)
                    profile.Items.TryMove(ids[i], new ItemLocation(ItemLocationKind.WandSlot, wand, 0, 0, 0, 0));
                else if (rules.Kind == ItemKind.Container && bag != null && profile.Items.TryGet(bag, out var bagItem)
                    && profile.Items.Catalog.TryGetRules(bagItem.ContentId, out var bagRules))
                {
                    bool moved = false;
                    for (int y = 0; y < bagRules.BackpackHeight && !moved; y++) for (int x = 0; x < bagRules.BackpackWidth && !moved; x++)
                        moved = profile.Items.TryMove(ids[i], new GridPlacement(x, y, 0).InBackpack(bag)).Success;
                }
            }
        }
    }
}
