using UnityEngine;
namespace Game.Run
{
    /// <summary>范围属于副本数据，模型来自Item Pool；两次抽样互不依赖。</summary>
    [CreateAssetMenu(menuName="Game/Run/Dungeon Wand Rolls")]
    public sealed class DungeonWandRollProfile:ScriptableObject
    {
        public Vector2 Mana=new Vector2(80,240),Regen=new Vector2(8,30),Cooldown=new Vector2(.15f,1);
        public Vector2Int Capacity=new Vector2Int(3,8),Draws=new Vector2Int(1,3);
        public Vector2 Reload = new Vector2(.2f,1.2f), Spread = new Vector2(0,8);
        [Range(0,1)] public float ShuffleProbability = .25f;
        public string[] AlwaysCastCandidates=System.Array.Empty<string>();
        [Range(0,1)]public float AlwaysCastProbability;
        public bool IsValid=>Range(Mana,true)&&Range(Regen,false)&&Range(Cooldown,false)
            &&Capacity.x>=1&&Capacity.y>=Capacity.x&&Capacity.y<=32&&Draws.x>=1&&Draws.y>=Draws.x&&Draws.y<=Capacity.x
            &&Range(Reload,false)&&Range(Spread,false)&&float.IsFinite(ShuffleProbability)&&ShuffleProbability>=0&&ShuffleProbability<=1
            &&float.IsFinite(AlwaysCastProbability)&&AlwaysCastProbability>=0&&AlwaysCastProbability<=1&&ValidCandidates();
        private bool ValidCandidates(){if(AlwaysCastCandidates==null)return false;for(int i=0;i<AlwaysCastCandidates.Length;i++)if(string.IsNullOrWhiteSpace(AlwaysCastCandidates[i]))return false;return true;}
        private static bool Range(Vector2 value,bool positive)=>WandRuntimeState.IsFinite(value.x)&&WandRuntimeState.IsFinite(value.y)
            &&(positive?value.x>0:value.x>=0)&&value.y>=value.x;
        public WandStats Roll()
        {
            if(!IsValid)throw new System.InvalidOperationException("法杖随机范围非法。");
            return new WandStats(Random.Range(Mana.x,Mana.y),Random.Range(Regen.x,Regen.y),Random.Range(Cooldown.x,Cooldown.y),
                Random.Range(Capacity.x,Capacity.y+1),Random.Range(Draws.x,Draws.y+1),Random.Range(Reload.x,Reload.y),
                Random.Range(Spread.x,Spread.y),Random.value<ShuffleProbability,
                AlwaysCastCandidates.Length>0&&Random.value<AlwaysCastProbability?new[]{AlwaysCastCandidates[Random.Range(0,AlwaysCastCandidates.Length)]}:null);
        }
    }
}
