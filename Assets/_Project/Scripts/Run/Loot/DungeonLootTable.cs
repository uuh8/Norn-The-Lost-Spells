using System;
using UnityEngine;

namespace Game.Run
{
    [Serializable]
    public sealed class DungeonItemDrop
    {
        public ItemDefinition[] Pool=Array.Empty<ItemDefinition>();
        [Range(0,1)] public float Chance=.2f;
        [Min(1)] public int MinCount=1,MaxCount=1;
        [Min(0)] public int SpellCharges=8;
        [Min(1)] public int MaterialQuantity=256;
        public bool UnlimitedSpell;
        [Min(0)]public int MaxSpellCharges;
    }

    /// <summary>每关独立配置；概率按条目独立抽取，金币不伪装成背包物品。</summary>
    [CreateAssetMenu(menuName="Game/Run/Dungeon Loot Table",fileName="DungeonLoot")]
    public sealed class DungeonLootTable:ScriptableObject
    {
        public DungeonWandRollProfile WandRolls;
        public DungeonItemDrop[] Items=Array.Empty<DungeonItemDrop>();
        [Range(0,1)] public float CoinChance=.7f;
        [Min(1)] public int MinCoins=10,MaxCoins=25;
        public bool IsValid
        {
            get
            {
                if(!DungeonLootRoller.ValidChance(CoinChance)||MinCoins<1||MaxCoins<MinCoins||Items==null||WandRolls!=null&&!WandRolls.IsValid)return false;
                for(int i=0;i<Items.Length;i++)
                {
                    var entry=Items[i];
                    if(entry==null||!DungeonLootRoller.ValidChance(entry.Chance)||entry.Pool==null||entry.Pool.Length==0
                        ||entry.MinCount<1||entry.MaxCount<entry.MinCount||entry.MaxCount>16||entry.SpellCharges<0||entry.MaterialQuantity<1
                        ||entry.MaxSpellCharges!=0&&entry.MaxSpellCharges<entry.SpellCharges)return false;
                    for(int j=0;j<entry.Pool.Length;j++)if(entry.Pool[j]==null)return false;
                }
                return true;
            }
        }
    }
}
