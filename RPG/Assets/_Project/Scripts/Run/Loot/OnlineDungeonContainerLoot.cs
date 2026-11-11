using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 原P7十二个NPC的Run级完整Item掉落计划。上层只在首次权威死亡门调用；本层保留一次抽样事实以抗重复事件。
    /// 历史类名保留以免破坏既有装配；卡片、随机杖、容器、材料和普通物品都提交到同一个共享Ground。
    /// 每项独立记成功/重试，金币沿同一次抽样进入独立Coin世界，不伪装成Item。
    /// </summary>
    public sealed class OnlineDungeonContainerLoot : IDisposable
    {
        public const int MaximumNpcPlans = 12;
        // 当前原P7是12敌、每表4条、每条最多1件：这是完整来源key上界，不扩Ground的16384总条目界。
        public const int MaximumLootItems = MaximumNpcPlans * 4;
        private sealed class NpcPlan
        {
            internal string Key;
            internal DungeonLootPlan Plan;
            internal bool[] Delivered;
            internal float Retry;
            internal int Pending;
            internal ulong ActorId;
            internal Vector3 DeathPosition;
            internal bool CoinPending, CoinDelivered;
        }
        private readonly Dictionary<ulong, NpcPlan> _plans = new Dictionary<ulong, NpcPlan>(MaximumNpcPlans);
        private readonly Func<DungeonLootPlannedItem, bool> _register;
        private readonly OnlineGroundItemWorld _world;
        private readonly string _runKey;
        private OnlineCoinWorld _coins;
        private bool _disposed;
        private int _reservedItemPlans;
        public int PendingCount { get; private set; }
        public long CapacityBound { get; }

        public OnlineDungeonContainerLoot(OnlineGroundItemWorld world, string runKey)
            : this(runKey, world != null ? (Func<DungeonLootPlannedItem, bool>)world.TryRegisterLoot : throw new ArgumentNullException(nameof(world)))
        { _world = world; CapacityBound = world.ValidateContainerLootCapacity(); }

        // 纯计划/重试的定向测试seam；正式装配只能使用上面的world构造，并完成真实Catalog预算证明。
        internal OnlineDungeonContainerLoot(string runKey, Func<DungeonLootPlannedItem, bool> register)
        {
            if (string.IsNullOrWhiteSpace(runKey) || register == null) throw new ArgumentException("P7掉落计划缺Run键或注册端口。");
            _runKey = runKey; _register = register;
        }

        /// <summary>
        /// 正式组合层绑定一次同Run账本；原Item构造/夹具保持可用。若装配晚于已接死亡事实，
        /// 只补投原Plan中的Coin，不重新Roll，也不因为Coin忙而阻塞其它Item登记。
        /// </summary>
        public bool ConfigureCoins(OnlineCoinWorld coins)
        {
            if (_disposed || coins == null || coins.IsDisposed || coins.RunId != _runKey) return false;
            if (_coins != null) return ReferenceEquals(_coins, coins);
            _coins = coins;
            foreach (var state in _plans.Values)
            {
                if (!state.Plan.CoinDropped || state.CoinDelivered || state.CoinPending) continue;
                state.CoinPending = true; state.Pending++; PendingCount++; TryDeliver(state);
            }
            return true;
        }

        /// <summary>true表示该NPC已接收一次plan，正常pending或重复死亡也为true；不表示已落地或已归某个玩家。</summary>
        public bool TryRollOnce(ulong actorId, int stage, int enemy, DungeonLootTable table, Vector3 deathPosition)
        {
            if (_disposed) return false;
            if (_plans.ContainsKey(actorId)) return true;
            if (actorId < OnlineActorLimits.FirstNpcActorId || actorId - OnlineActorLimits.FirstNpcActorId >= MaximumNpcPlans
                || _plans.Count >= MaximumNpcPlans || stage < 0 || stage >= 4 || enemy < 0 || enemy >= MaximumNpcPlans
                || !Finite(deathPosition) || !TryMaximumItems(table, out int maximum)
                || maximum > MaximumLootItems - _reservedItemPlans || _world != null && !_world.IsLootTableInCatalog(table)) return false;
            string key = "p7." + _runKey + "." + stage + "." + enemy;
            foreach (var existing in _plans.Values) if (existing.Key == key) return false;
            // 原随机流只完整抽样一次；不先过滤Kind，也不在失败重试时重抽次数、WandRolls或散布位置。
            var plan = DungeonLootPlanner.Roll(table, key, deathPosition);
            var state = new NpcPlan { Key = key, Plan = plan, Retry = .15f,
                Delivered = new bool[plan.Items.Count], Pending = plan.Items.Count,
                ActorId = actorId, DeathPosition = deathPosition, CoinPending = _coins != null && plan.CoinDropped };
            if (state.CoinPending) state.Pending++;
            _reservedItemPlans += maximum; PendingCount += state.Pending; _plans.Add(actorId, state);
            TryDeliver(state);
            return true;
        }

        public void Step(float deltaTime, bool canAdvance)
        {
            if (_disposed || !canAdvance || !float.IsFinite(deltaTime) || deltaTime <= 0) return;
            foreach (var state in _plans.Values)
            {
                if (state.Pending == 0) continue;
                state.Retry -= deltaTime;
                if (state.Retry > 0) continue;
                state.Retry = .15f; TryDeliver(state); // 一次Tick最多每个计划尝试一次，不累积Frozen追赶债务。
            }
        }
        private void TryDeliver(NpcPlan plan)
        {
            if (plan.CoinPending && _coins != null && _coins.TryRegister(plan.ActorId, plan.Plan.CoinAmount, plan.DeathPosition))
            {
                plan.CoinPending = false; plan.CoinDelivered = true; plan.Pending--; PendingCount--;
            }
            for (int i = 0; i < plan.Plan.Items.Count; i++)
            {
                if (plan.Delivered[i] || !_register(plan.Plan.Items[i])) continue;
                plan.Delivered[i] = true; plan.Pending--; PendingCount--;
            }
        }
        private static bool TryMaximumItems(DungeonLootTable table, out int maximum)
        {
            maximum = 0;
            if (table == null || !table.IsValid) return false;
            foreach (var entry in table.Items)
            {
                if (entry.Chance <= 0) continue;
                if (entry.MaxCount > MaximumLootItems - maximum) return false;
                maximum += entry.MaxCount;
            }
            return true;
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _plans.Clear(); PendingCount = _reservedItemPlans = 0;
            _coins = null; // Coin账本由World持有，销毁Item计划不清金币/墓碑或返还已经获得的余额。
            // 已注册地面属于共享World；本计划销毁不收回玩家拾走的物品，也不替GroundWorld处置生命周期。
        }
    }
}

