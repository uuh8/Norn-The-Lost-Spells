using System;
using System.Collections.Generic;
using Game.Core;
namespace Game.Run
{
    internal static class RaidProfileOps
    {
        internal static bool IsCarried(SinglePlayerProfile profile, string id)
        {
            if (!profile.Items.TryGetLocation(id, out var root)) return false;
            for (int i = 0; root.HasParentInstance && i < profile.Items.Count; i++)
                if (!profile.Items.TryGetLocation(root.OwnerId, out root)) return false;
            return root.OwnerId == profile.ActorId && (root.Kind == ItemLocationKind.EquippedWand || root.Kind == ItemLocationKind.EquippedBackpack || root.Kind == ItemLocationKind.SpellStorage || root.Kind == ItemLocationKind.EquippedTool);
        }
        internal static SinglePlayerProfile Clone(SinglePlayerProfile profile)
        {
            var dto = ProfileSaveMapper.ToDto(profile, profile.SaveGeneration);
            if (!ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out var clone).Success) throw new InvalidOperationException("合法Profile候选复制失败。");
            return clone;
        }
        internal static bool RestoreEntries(SinglePlayerProfile original, List<InventoryEntry> entries, string actor,
            out SinglePlayerProfile next)
        {
            next = null;
            if (!ItemOwnershipStore.TryRestore(original.Items.Catalog, entries, original.Items.Version + 1, out var store, out _)) return false;
            next = new SinglePlayerProfile(actor, store, original.SaveGeneration, original.Currency);
            next.LastRaidReceipt = original.LastRaidReceipt; next.AdventureProgress = original.AdventureProgress; next.Shop = original.Shop?.Copy();
            next.HubWorldItems=new List<HubWorldItemRecord>(HubWorldItemRecord.CopyAll(original.HubWorldItems));
            next.ToolBindings = (string[])original.ToolBindings.Clone(); ToolbeltService.NormalizeProfile(next); return true;
        }
    }
}
