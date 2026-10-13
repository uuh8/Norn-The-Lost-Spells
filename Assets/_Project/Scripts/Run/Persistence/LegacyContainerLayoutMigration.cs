using System.Collections.Generic;

namespace Game.Run
{
    /// <summary>只迁移旧档的同格容器；在恢复候选内拆开，不改写文件、不复制材料或实例。</summary>
    internal static class LegacyContainerLayoutMigration
    {
        internal static bool TryRestore(ItemCatalogIndex catalog,InventoryEntry[] entries,uint version,out ItemOwnershipStore store)
        {
            store=null;var separated=new List<int>();var candidate=(InventoryEntry[])entries.Clone();
            for(int i=0;i<entries.Length;i++)
            {
                var item=entries[i];
                if(item.Location.Kind!=ItemLocationKind.Backpack || !catalog.TryGetRules(item.Item.ContentId,out var rules) || rules.Kind!=ItemKind.Container)continue;
                int members=1;bool duplicate=false;
                for(int j=0;j<i;j++)
                {
                    if(entries[j].Location!=item.Location)continue;
                    // 旧采集瓶最多三份；不同内容/形状或超过旧上限的重叠仍是坏档，不能借迁移洗掉错误。
                    if(!ItemStackOps.HaveMatchingState(item.Item,entries[j].Item) || ++members>3)return false;
                    duplicate=true;
                }
                if(!duplicate)continue;
                separated.Add(i);candidate[i]=new InventoryEntry(item.Item,ItemLocation.Warehouse);
            }
            if(separated.Count==0 || !ItemOwnershipStore.TryRestore(catalog,candidate,version,out var restored,out _))return false;
            foreach(int index in separated)
            {
                var source=entries[index];
                if(!restored.TryGet(source.Location.OwnerId,out var bag) || !catalog.TryGetRules(bag.ContentId,out var bagRules))return false;
                bool placed=false;
                for(int turn=0;turn<4 && !placed;turn++)for(int y=0;y<bagRules.BackpackHeight && !placed;y++)for(int x=0;x<bagRules.BackpackWidth && !placed;x++)
                    placed=restored.TryMove(source.Item.InstanceId,new GridPlacement(x,y,turn).InBackpack(bag.InstanceId)).Success;
                // 没有完整形状空位就保留在无限仓库；数量、组分和附着 ID 均来自原实例。
            }
            store=restored;return true;
        }
    }
}
