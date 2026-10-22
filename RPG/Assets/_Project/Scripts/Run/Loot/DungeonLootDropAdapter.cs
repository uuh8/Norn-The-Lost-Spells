using System;
using System.Collections.Generic;
using Game.Combat;
using Game.Core;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Run
{
    /// <summary>挂在独立战利品节点，敌人尸体销毁后仍能完成资源忙时的重试。</summary>
    public sealed class DungeonLootDropAdapter:MonoBehaviour
    {
        private struct PendingItem
        {public ItemDefinition Definition;public string Key;public Vector3 Position;public int Charges,MaterialQuantity;public bool Unlimited;public WandStats? WandStats;}
        private readonly List<PendingItem> _pending=new List<PendingItem>();
        private HealthComponent _health;
        private DemoEncounterController _encounter;
        private SinglePlayerInventorySession _session;
        private DungeonLootTable _table;
        private WorldCurrencyPickup _coinPrefab;
        private string _key;
        private bool _rolled;
        private int _next;
        private float _retry;
        private int _enemyId;
        private byte _playerTeam;
        private NavMeshPath _groundPath;
        // NavMeshPath持有Unity原生对象，不能在MonoBehaviour字段初始化器/构造函数里创建。
        private void Awake()=>_groundPath=new NavMeshPath();
        private static readonly Unity.Profiling.ProfilerMarker RetryMarker=new Unity.Profiling.ProfilerMarker("Norn.Dungeon.LootRetry");
        public bool HasRolled=>_rolled;
        public int PendingCount=>_pending.Count-_next;
        public int EnemyId=>_enemyId;
        public void Configure(HealthComponent health,DemoEncounterController encounter,SinglePlayerInventorySession session,
            DungeonLootTable table,WorldCurrencyPickup coinPrefab,string key)
        {
            _health=health;_enemyId=health.gameObject.GetInstanceID();_encounter=encounter;_session=session;
            _table=table;_coinPrefab=coinPrefab;_key=key;
            _playerTeam=session.ActorTransform.GetComponent<HealthComponent>().TeamId;
        }
        private void OnEnable()=>EventBus<DeathEvent>.Subscribe(OnDeath);
        private void OnDisable()
        {
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
            if(_session!=null)_session.PendingDungeonDrops-=PendingCount;
            _next=_pending.Count;
        }
        private static double Sample()=>Math.Min(UnityEngine.Random.value,.999999999d);
        internal Vector3 ResolveDropGround(Vector3 position)
        {
            // 最近NavMesh可能是孤立高台；还要验证与玩家所在面连通，才能保证奖励可拾取。
            if(NavMesh.SamplePosition(_session.ActorTransform.position,out var player,8,NavMesh.AllAreas))
            {
                if(Reachable(position,4,player.position,out var reachable))return reachable;
                // 高台远程敌人无需地面追击；把掉落投影到玩家所在楼层，而非强迫修改原敌人布局。
                if(Reachable(new Vector3(position.x,player.position.y,position.z),4,player.position,out reachable))return reachable;
                return player.position;
            }
            if(Physics.Raycast(position+Vector3.up*6,Vector3.down,out var floor,16,(1<<0)|(1<<8),QueryTriggerInteraction.Ignore))
                return floor.point;
            return position;
        }
        private bool Reachable(Vector3 position,float distance,Vector3 player,out Vector3 ground)
        {
            ground=default;
            if(!NavMesh.SamplePosition(position,out var hit,distance,NavMesh.AllAreas)
                ||!NavMesh.CalculatePath(player,hit.position,NavMesh.AllAreas,_groundPath)
                ||_groundPath.status!=NavMeshPathStatus.PathComplete)return false;
            ground=hit.position;return true;
        }
        private void OnDeath(DeathEvent death)
        {
            if(_rolled||_health==null||_session==null||death.TargetId!=_enemyId||_health.IsAlive
                ||_health.TeamId==_playerTeam||_encounter==null
                ||(_encounter.State!=EncounterState.Active&&_encounter.State!=EncounterState.Cleared))return;
            _rolled=true;
            if(_table==null||!_table.IsValid){GameLog.Error("地牢掉落配置非法，未创建奖励。","Dungeon");return;}
            var origin=ResolveDropGround(death.Position);
            // Random只在死亡事实首次消费时调用；资源事务重试永不重新抽奖。
            if(DungeonLootRoller.ShouldDrop(_table.CoinChance,Sample()))
            {
                var coin=Instantiate(_coinPrefab,transform.parent);
                coin.transform.position=origin;
                coin.Configure(_session,DungeonLootRoller.Quantity(_table.MinCoins,_table.MaxCoins,Sample()));
            }
            for(int i=0;i<_table.Items.Length;i++)
            {
                var entry=_table.Items[i];if(!DungeonLootRoller.ShouldDrop(entry.Chance,Sample()))continue;
                var definition=entry.Pool[DungeonLootRoller.Quantity(0,entry.Pool.Length-1,Sample())];
                int count=DungeonLootRoller.Quantity(entry.MinCount,entry.MaxCount,Sample());
                for(int j=0;j<count;j++)
                {
                    var offset=UnityEngine.Random.insideUnitCircle*.9f;
                    _pending.Add(new PendingItem{Definition=definition,Key=_key+"."+i+"."+j,
                        Position=ResolveDropGround(origin+new Vector3(offset.x,0,offset.y)),
                        Charges=definition.Kind==ItemKind.Spell&&entry.MaxSpellCharges>0?DungeonLootRoller.Quantity(entry.SpellCharges,entry.MaxSpellCharges,Sample()):entry.SpellCharges,
                        Unlimited=entry.UnlimitedSpell,MaterialQuantity=entry.MaterialQuantity,
                        WandStats=definition.Kind==ItemKind.Wand&&_table.WandRolls!=null?_table.WandRolls.Roll():(WandStats?)null});
                    _session.PendingDungeonDrops++;
                }
            }
            Flush();
        }
        private void Update()
        {
            using var marker=RetryMarker.Auto();
            if(PendingCount==0)return;
            _retry-=Time.deltaTime;if(_retry>0)return;_retry=.15f;Flush();
        }
        private void Flush()
        {
            while(_next<_pending.Count)
            {
                var item=_pending[_next];
                if(!_session.TryRegisterAwayWorldItem(item.Definition,_session.WorldSceneKey,item.Key,item.Position,
                    Quaternion.identity,item.Charges,item.Unlimited,null,item.MaterialQuantity,item.WandStats))return;
                _next++;_session.PendingDungeonDrops--;
            }
        }
    }
}
