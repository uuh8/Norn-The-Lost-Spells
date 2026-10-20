using Game.Skills;
using UnityEngine;
namespace Game.Run
{
    public readonly struct DungeonSpellReward
    {
        public readonly SpellDefinition Spell;public readonly int Charges;
        public DungeonSpellReward(SpellDefinition spell,int charges){Spell=spell;Charges=charges;}
    }
    [CreateAssetMenu(menuName="Game/Run/Dungeon Rewards")]
    public sealed class DungeonRewardProfile:ScriptableObject
    {
        public SpellDefinition[] Pool=System.Array.Empty<SpellDefinition>();
        [Min(1)]public int MinCharges=20,MaxCharges=40;
        public bool IsValid
        {
            get
            {
                if(Pool==null||Pool.Length<3||MinCharges<1||MaxCharges<MinCharges||MaxCharges>=int.MaxValue)return false;
                for(int i=0;i<Pool.Length;i++){if(Pool[i]==null)return false;for(int j=0;j<i;j++)if(Pool[j]==Pool[i])return false;}return true;
            }
        }
        public DungeonSpellReward[] Roll(int count)
        {
            if(!IsValid||count<1||count>Pool.Length)throw new System.InvalidOperationException("法术随机奖励配置非法。");
            // 部分Fisher-Yates无放回抽样，三种必定不同；仅清场这一离散时刻复制池。
            var shuffled=(SpellDefinition[])Pool.Clone();var result=new DungeonSpellReward[count];
            for(int i=0;i<count;i++){int pick=Random.Range(i,shuffled.Length);var spell=shuffled[pick];shuffled[pick]=shuffled[i];shuffled[i]=spell;
                result[i]=new DungeonSpellReward(spell,Random.Range(MinCharges,MaxCharges+1));}return result;
        }
    }
}
