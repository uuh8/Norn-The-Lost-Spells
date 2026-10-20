using Game.Skills;

namespace Game.Run
{
    /// <summary>容量以真实Store为准。失败候选不占格；只发一份实例，尝试不同位置不创建不同身份。</summary>
    public static class PracticeSpellRewardOps
    {
        public static bool TryGrant(SinglePlayerProfile profile,SpellDefinition spell,out string id)
            =>Grant(profile,spell,0,true,out id);
        public static bool TryGrantFinite(SinglePlayerProfile profile,SpellDefinition spell,int charges,out string id)
            =>Grant(profile,spell,charges,false,out id);
        private static bool Grant(SinglePlayerProfile profile,SpellDefinition spell,int charges,bool unlimited,out string id)
        {
            id=null;var store=profile?.Items;
            if(store==null || charges<0 || profile.RaidActionsFrozen || !store.Catalog.TryFindSpell(spell,out var definition))return false;
            var item=RuntimeItemState.Create(definition.ContentId,charges,unlimited);
            for(int slot=0;slot<store.Catalog.SpellStorageCapacity;slot++)
                if(store.TryAdd(item,new ItemLocation(ItemLocationKind.SpellStorage,profile.ActorId,0,0,0,slot)))
                {id=item.InstanceId;return true;}
            string bagId=null;InventoryItemRules rules=default;
            foreach(var entry in store)if(entry.Location.Kind==ItemLocationKind.EquippedBackpack && entry.Location.OwnerId==profile.ActorId)
            {bagId=entry.Item.InstanceId;store.Catalog.TryGetRules(entry.Item.ContentId,out rules);break;}
            if(bagId==null)return false;
            for(int turn=0;turn<4;turn++)for(int y=0;y<rules.BackpackHeight;y++)for(int x=0;x<rules.BackpackWidth;x++)
                if(store.TryAdd(item,new GridPlacement(x,y,turn).InBackpack(bagId))){id=item.InstanceId;return true;}
            return false;
        }
    }
}
