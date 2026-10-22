using System;
using System.Collections.Generic;
using Game.Core;
namespace Game.Run
{
    /// <summary>只在按钮事件运行；候选表全量验证后提交，重排失败保留原布置与全部物品。</summary>
    public static class BackpackOrganizeOps
    {
        private sealed class Group{internal InventoryEntry First;internal InventoryItemRules Rules;internal readonly List<InventoryEntry> Items=new List<InventoryEntry>();}
        public static InventoryOperationResult TryOrganize(ItemOwnershipStore store,string bagId,uint version)
        {
            if(store.IsResourceBusy)return store.Result(false,InventoryFailureReason.ResourceBusy);
            if(version!=store.Version)return store.Result(false,InventoryFailureReason.StaleVersion);
            if(!store.TryGet(bagId,out var bag)||!store.Catalog.TryGetRules(bag.ContentId,out var bagRules)||bagRules.Kind!=ItemKind.Backpack)return store.Result(false,InventoryFailureReason.NotFound);
            var candidate=store.CopyCandidate();var entries=new List<InventoryEntry>();
            foreach(var entry in store)if(entry.Location.Kind==ItemLocationKind.Backpack&&entry.Location.OwnerId==bagId)entries.Add(entry);
            entries.Sort((a,b)=>string.CompareOrdinal(a.Item.InstanceId,b.Item.InstanceId));
            // 旧存档的小GMU碎片先合并。只改候选，不触碰原MaterialItemState的数量。
            for(int i=0;i<entries.Count;i++)
            {
                string targetId=entries[i].Item.InstanceId;if(!candidate.TryGetValue(targetId,out var target)||target.Item.Material==null)continue;
                store.Catalog.TryGetRules(target.Item.ContentId,out var rules);
                for(int j=i+1;j<entries.Count;j++)
                    if(candidate.TryGetValue(entries[j].Item.InstanceId,out var source)&&MaterialStackOps.Compatible(source.Item,candidate[targetId].Item))
                    {ulong amount=Math.Min(source.Item.Material.Quantity,rules.MaterialItemCapacity-candidate[targetId].Item.Material.Quantity);if(amount>0)MaterialStackOps.MergeCandidate(candidate,source.Item.InstanceId,targetId,amount);}
            }
            var groups=new List<Group>();
            foreach(var entry in entries)
            {
                if(!candidate.TryGetValue(entry.Item.InstanceId,out var actual))continue;
                Group group=null;foreach(var existing in groups)if(existing.First.Location==actual.Location){group=existing;break;}
                if(group==null){store.Catalog.TryGetRules(actual.Item.ContentId,out var rules);group=new Group{First=actual,Rules=rules};groups.Add(group);}group.Items.Add(actual);
                candidate.Remove(actual.Item.InstanceId);
            }
            groups.Sort((a,b)=>{int type=Priority(a.Rules.Kind).CompareTo(Priority(b.Rules.Kind));if(type!=0)return type;
                int content=string.CompareOrdinal(a.First.Item.ContentId,b.First.Item.ContentId);if(content!=0)return content;
                int area=b.Rules.Shape.CellCount.CompareTo(a.Rules.Shape.CellCount);return area!=0?area:string.CompareOrdinal(a.First.Item.InstanceId,b.First.Item.InstanceId);});
            foreach(var group in groups)
            {
                bool placed=false;
                for(int y=0;y<bagRules.BackpackHeight&&!placed;y++)for(int x=0;x<bagRules.BackpackWidth&&!placed;x++)for(int turn=0;turn<4&&!placed;turn++)
                {
                    int rotation=(group.First.Location.QuarterTurns+turn)%4;
                    if(x+group.Rules.Shape.GetWidth(rotation)>bagRules.BackpackWidth||y+group.Rules.Shape.GetHeight(rotation)>bagRules.BackpackHeight)continue;
                    var location=new ItemLocation(ItemLocationKind.Backpack,bagId,x,y,rotation,-1);bool overlap=false;
                    foreach(var other in candidate.Values)if(other.Location.Kind==ItemLocationKind.Backpack&&other.Location.OwnerId==bagId)
                    {store.Catalog.TryGetRules(other.Item.ContentId,out var rules);if(InventoryGrid.Overlaps(group.Rules.Shape,location,rules.Shape,other.Location)){overlap=true;break;}}
                    if(overlap)continue;foreach(var item in group.Items)candidate[item.Item.InstanceId]=new InventoryEntry(item.Item,location);placed=true;
                }
                if(!placed)return store.Result(false,InventoryFailureReason.Capacity);
            }
            return store.CommitMaterialLayout(candidate,version);
        }
        private static int Priority(ItemKind kind)=>kind==ItemKind.Material?0:kind==ItemKind.Consumable?1:kind==ItemKind.Alchemy?2:kind==ItemKind.Container?3:kind==ItemKind.Wand?4:5;
    }
}
