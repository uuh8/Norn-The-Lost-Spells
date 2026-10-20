using System.Collections.Generic;
using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>世界奖励保持可重试；全集可以部分领取，但每个奖励键/法术最多发一次。</summary>
    public sealed class PracticeSpellRewardAdapter:MonoBehaviour
    {
        [SerializeField] private PracticeInventoryWorkspace _workspace;
        private readonly Dictionary<string,HashSet<SpellDefinition>> _granted=new Dictionary<string,HashSet<SpellDefinition>>();
        public static PracticeSpellRewardAdapter FindInScene(UnityEngine.SceneManagement.Scene scene)
        {
            foreach(var root in scene.GetRootGameObjects())
            {var adapter=root.GetComponentInChildren<PracticeSpellRewardAdapter>(true);if(adapter!=null)return adapter;}
            return null;
        }
        public bool TryGrant(string key,SpellDefinition spell)
            =>Grant(key,spell,0,false);
        public bool TryGrantFinite(string key,SpellDefinition spell,int charges)
            =>Grant(key,spell,charges,true);
        private bool Grant(string key,SpellDefinition spell,int charges,bool finite)
        {
            if(!_granted.TryGetValue(key,out var completed)){completed=new HashSet<SpellDefinition>();_granted.Add(key,completed);}
            if(completed.Contains(spell))return true;
            var session=_workspace.Session;
            if(session.Profile==null || !session.Profile.Items.Catalog.TryFindSpell(spell,out _))
            {GameLog.Error("法术奖励缺少唯一的ItemDefinition映射，保留世界奖励。","PracticeReward");return false;}
            if(session.RaidWriteLocked || session.PendingMaterialOutputs>0 || session.PendingContainerFlights>0
                || session.Profile==null || !(finite?PracticeSpellRewardOps.TryGrantFinite(session.Profile,spell,charges,out _):PracticeSpellRewardOps.TryGrant(session.Profile,spell,out _)))
            {
                EventBus<NearbyPickupResultEvent>.Publish(new NearbyPickupResultEvent{InventoryId=session.ReadOnly?.InventoryId,
                    Success=false,RequiresArrangement=true,Reason=InventoryFailureReason.Capacity});return false;
            }
            completed.Add(spell);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent{InventoryId=session.ReadOnly.InventoryId,Version=session.ReadOnly.Version});
            EventBus<SpellRewardGrantedEvent>.Publish(new SpellRewardGrantedEvent{Spell=spell});return true;
        }
    }
}
