using System;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>P7作者数据入口；库存工作区完成初始化后绑定，不靠跨对象Start碰巧先执行。</summary>
    public sealed class DungeonRunContent:MonoBehaviour
    {
        [SerializeField] private DemoEncounterController[] _encounters=Array.Empty<DemoEncounterController>();
        [SerializeField] private DungeonLootTable[] _tables=Array.Empty<DungeonLootTable>();
        [SerializeField] private WorldCurrencyPickup _coinPrefab;
        [SerializeField] private Transform[] _routeChecks=Array.Empty<Transform>();
        [SerializeField] private DungeonRewardProfile _rewards;
        private bool _bound;
        private bool _completionRecorded;
        private SinglePlayerInventorySession _session;
        private void OnEnable()=>EventBus<EncounterCompletedEvent>.Subscribe(OnCompleted);
        private void OnDisable()=>EventBus<EncounterCompletedEvent>.Unsubscribe(OnCompleted);
        private void OnCompleted(EncounterCompletedEvent completed)
        {
            if(!_bound||_completionRecorded||completed.EncounterIndex!=3)return;
            for(int i=0;i<_encounters.Length;i++)if(_encounters[i].State!=EncounterState.Cleared)return;
            // 商店已有“完成冒险补货”规则；四个小关只合计一次完整冒险，不重复按小怪/小关补货。
            _completionRecorded=_session.TryRecordDungeonCompletion();
        }
        public int StageCount=>_encounters.Length;
        public int RouteCheckCount=>_routeChecks.Length;
        public Transform GetRouteCheck(int index)=>_routeChecks[index];
        public DungeonLootTable GetTable(int index)=>_tables[index];
        public DemoEncounterController GetEncounter(int index)=>_encounters[index];
        public void Bind(SinglePlayerInventorySession session)
        {
            if(_bound)return;
            if(session==null||_encounters.Length!=4||_tables.Length!=4||_coinPrefab==null)
                throw new InvalidOperationException("P7地牢需要四关、四张掉落表与金币Prefab。");
            for(int stage=0;stage<4;stage++)
            {
                var encounter=_encounters[stage];var table=_tables[stage];
                if(encounter==null||table==null||!table.IsValid)throw new InvalidOperationException("P7地牢配置非法。");
                for(int entry=0;entry<table.Items.Length;entry++)foreach(var definition in table.Items[entry].Pool)
                    if(!session.Profile.Items.Catalog.TryGet(definition.ContentId,out var found)||found!=definition)
                        throw new InvalidOperationException("掉落物必须属于当前唯一ItemCatalog："+definition.ContentId);
            }
            _bound=true;_session=session;
            if(_rewards!=null)
            {
                if(!_rewards.IsValid)throw new InvalidOperationException("P7随机奖励表非法。");
                foreach(var spell in _rewards.Pool)if(!session.Profile.Items.Catalog.TryFindSpell(spell,out _))
                    throw new InvalidOperationException("随机奖励法术未注册到Catalog。");
                foreach(var encounter in _encounters)
                {
                    foreach(var pickup in encounter.GetComponentsInChildren<SpellRewardPickup>(true))pickup.ConfigureRandomRewards(_rewards);
                    foreach(var pickup in encounter.GetComponentsInChildren<DungeonFinalRewardPickup>(true))pickup.ConfigureRandomRewards(_rewards);
                }
            }
            string runKey=Guid.NewGuid().ToString("N");
            for(int stage=0;stage<4;stage++)for(int enemy=0;enemy<_encounters[stage].EnemyCount;enemy++)
            {
                var node=new GameObject("EnemyLoot · "+stage+"."+enemy);node.transform.SetParent(transform,false);
                node.AddComponent<DungeonLootDropAdapter>().Configure(_encounters[stage].GetEnemy(enemy),_encounters[stage],session,
                    _tables[stage],_coinPrefab,"p7."+runKey+"."+stage+"."+enemy);
            }
            GameLog.Info("P7地牢掉落已绑定四关当前敌人。","Dungeon");
        }
    }
}
