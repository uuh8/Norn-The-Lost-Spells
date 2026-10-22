using Game.Combat;
using Game.Core;
using UnityEngine;
namespace Game.Run
{
    /// <summary>单个真实敌人的死亡消费端；概率抽样和发奖只在对应DeathEvent离散发生。</summary>
    public sealed class SpellLootDropAdapter : MonoBehaviour
    {
        public SpellLootTable Table;
        private HealthComponent _health;
        private SinglePlayerRunCoordinator _run;
        private bool _rolled;
        private void Awake() { _health = GetComponent<HealthComponent>(); }
        public void Configure(SinglePlayerRunCoordinator run) { _run = run; }
        private void OnEnable() => EventBus<DeathEvent>.Subscribe(OnDeath);
        private void OnDisable() => EventBus<DeathEvent>.Unsubscribe(OnDeath);
        private void OnDeath(DeathEvent death)
        {
            if (_rolled || _health == null || death.TargetId != _health.Id || _run == null || _health.TeamId == _run.PlayerTeam) return;
            _rolled = true;
            // 敌人死亡就是实际进展，掉落概率为0也应记录；不能用成功掉卡数量冒充击杀数量。
            _run.RecordEnemyDefeat(_health.Id);
            // value可能包含上边界1，将离散测试之外的Unity样本规范到[0,1)。
            double chance = System.Math.Min(UnityEngine.Random.value, .999999999), weight = System.Math.Min(UnityEngine.Random.value, .999999999);
            if (SpellLootRoller.TryRoll(Table, chance, weight, out var drop)) _run.TryDropSpell(_health.Id, drop, death.Position);
        }
    }
}
