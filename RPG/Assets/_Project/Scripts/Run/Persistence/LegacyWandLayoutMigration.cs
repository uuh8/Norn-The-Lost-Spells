using System.Collections.Generic;

namespace Game.Run
{
    /// <summary>法杖从1格变3格时只迁移合法旧布局；新档损坏不能借此自动洗成合法数据。</summary>
    internal static class LegacyWandLayoutMigration
    {
        internal static bool TryRestore(ItemCatalogIndex catalog, InventoryEntry[] entries, uint version, int schema, out ItemOwnershipStore store)
        {
            store = null;
            var legacy = catalog.WithLegacySingleCellWands();
            if (!ItemOwnershipStore.TryRestore(legacy,entries,version,out var old,out _)
                && !(schema <= 9 && LegacyContainerLayoutMigration.TryRestore(legacy,entries,version,out old))) return false;
            var candidate = new List<InventoryEntry>(); var wands = new List<InventoryEntry>();
            foreach (var entry in old)
            {
                catalog.TryGetRules(entry.Item.ContentId,out var rules);
                bool expand = entry.Location.Kind == ItemLocationKind.Backpack && rules.Kind == ItemKind.Wand
                    && rules.Shape.Width == 3 && rules.Shape.Height == 1 && rules.Shape.CellCount == 3;
                if (expand) wands.Add(entry);
                candidate.Add(expand ? new InventoryEntry(entry.Item,ItemLocation.Warehouse) : entry);
            }
            if (wands.Count == 0 || !ItemOwnershipStore.TryRestore(catalog,candidate,version,out var restored,out _)) return false;
            foreach (var entry in wands)
            {
                if (restored.TryMove(entry.Item.InstanceId,entry.Location).Success) continue;
                if (!restored.TryGet(entry.Location.OwnerId,out var bag) || !catalog.TryGetRules(bag.ContentId,out var bagRules)) return false;
                bool placed = false;
                for (int turn=0;turn<4 && !placed;turn++)
                    for (int y=0;y<bagRules.BackpackHeight && !placed;y++)
                        for (int x=0;x<bagRules.BackpackWidth && !placed;x++)
                            placed=restored.TryMove(entry.Item.InstanceId,new GridPlacement(x,y,turn).InBackpack(bag.InstanceId)).Success;
                // 满包时同一个实例留在无限仓库，程序子项不拆散，不能吞掉或复制备用法杖。
            }
            store=restored;return true;
        }
    }
}
