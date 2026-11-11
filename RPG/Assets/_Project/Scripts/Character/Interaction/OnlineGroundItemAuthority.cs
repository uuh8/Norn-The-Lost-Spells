using System;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Character
{
    /// <summary>
    /// Multiplayer与Run共享地面的装配边界。公开接口只返回Core快照/版本；
    /// Workspace保持在Character内部，Multiplayer不反向依赖Run或取得库存写入口。
    /// </summary>
    public sealed partial class OnlineGroundItemAuthority : IDisposable, IWorldCurrencyCollector
    {
        private readonly OnlineGroundItemWorld _world;
        private OnlineDungeonContainerLoot _dungeonLoot;
        private OnlineDungeonDefinition _lootDefinition;
        private string _lootRunId;
        private bool _sourceConfigurationAttempted, _sourceConfigurationAccepted;
        private string _sourceConfigurationError;
        private Func<Transform, ulong> _resolveCoinActor;

        public OnlineGroundItemAuthority(OnlineCombatDefinition definition, Scene scene)
        {
            if (definition == null || definition.Catalog == null || !definition.Catalog.TryCreateIndex(out var catalog, out var error))
                throw new ArgumentException("共享地面缺少受信Catalog。");
            _world = new OnlineGroundItemWorld(catalog, scene);
        }
        public uint Version => _world.Version;
        public bool Faulted => _world.Faulted;
        public OnlineGroundInventorySnapshot Capture() => _world.Capture();
        public int CopyCoinCheckpointsTo(OnlineCoinCheckpointRecord[] target) => _world.CopyCoinCheckpointsTo(target);
        /// <summary>Multiplayer传入认证Registry解析器；Collider仅提供真实Transform，不能自报Actor或金币数量。</summary>
        public bool ConfigureCoins(string runId, GameObject prefab, Func<bool> canAdvance,
            Func<Transform, ulong> resolveAuthenticatedActor, out string error)
        {
            error = null;
            if (_dungeonLoot == null || resolveAuthenticatedActor == null || !_sourceConfigurationAccepted
                || !string.Equals(runId, _lootRunId, StringComparison.Ordinal)
                || !_world.ConfigureCoins(runId, prefab, canAdvance, this)
                || !_dungeonLoot.ConfigureCoins(_world.Coins))
            { error = "Coin来源、Run、原Prefab或认证收集端口不一致。"; return false; }
            _resolveCoinActor = resolveAuthenticatedActor;
            return true;
        }
        public bool TryCollectWorldCurrency(ulong coinId, Transform actor)
        {
            if (actor == null || _resolveCoinActor == null) return false;
            ulong actorId = _resolveCoinActor(actor);
            return actorId != 0 && _world.TryCollectCoin(coinId, actorId);
        }
        public bool ConfigureCheckpointJournal(IOnlineItemRetirementJournal journal)
            => _world.ConfigureCheckpointJournal(journal);
        internal bool Attach(OnlineOwnedWorkspace workspace) => _world.Attach(workspace);
        internal OnlineInventoryCommandReceipt Execute(OnlineOwnedWorkspace workspace, Transform actor, in OnlineInventoryCommandIntent intent)
            => _world.Execute(workspace, actor, intent);
        /// <summary>
        /// 原P7来源只在同Run配置一次。P8无原表就没有新来源，重连不会重建plan或去重表。
        /// Run内部保存原表完整Item的有限计划，保持原16384地面总条目界与128ChunkKey不变。
        /// </summary>
        public bool ConfigureDungeonLoot(OnlineDungeonDefinition dungeon, string runId, out string error)
        {
            error = null;
            if (_sourceConfigurationAttempted)
            {
                bool same = _lootDefinition == dungeon && string.Equals(_lootRunId, runId, StringComparison.Ordinal);
                error = same ? _sourceConfigurationError : "同World不能替换已配置的原始物品来源。";
                return same && _sourceConfigurationAccepted;
            }
            if (dungeon == null) return _dungeonLoot == null;
            if (string.IsNullOrWhiteSpace(runId) || !dungeon.TryValidateItemDrops(out error)
                || !dungeon.TryValidateFixedSolidSpawns(out error))
            { if (error == null) error = "固定来源缺少真实Run身份。"; return false; }
            _sourceConfigurationAttempted = true; _lootDefinition = dungeon; _lootRunId = runId;
            try
            {
                if (dungeon.LootTableCount != 0) _dungeonLoot = new OnlineDungeonContainerLoot(_world, runId);
                // 对齐原SpawnPoint：初始化只尝试一次，失败保留原位置/现场并显式交根故障入口。
                // 不套用NPC的周期重试/随机散布，也不让重连续配再次生成已拾走的稳定key。
                for (int i = 0; i < dungeon.FixedSolidCount; i++)
                {
                    var spawn = dungeon.GetFixedSolid(i);
                    if (!_world.TryRegisterFixedMaterial("fixed:" + runId + ":" + spawn.SpawnId,
                        spawn.ContentId, spawn.Quantity, spawn.Position, spawn.Rotation))
                    { error = _sourceConfigurationError = "原固定固体初始化被拒绝，保留原世界现场：" + spawn.SpawnId; return false; }
                }
                _sourceConfigurationAccepted = true; return true;
            }
            catch (ArgumentException exception)
            { error = _sourceConfigurationError = exception.Message; return false; }
        }

        /// <summary>上层先核已死NPC/原遭遇门；这里只向Run提交原表与稳定stage/enemy键，不处理击杀归属。</summary>
        public bool TryRecordDungeonNpcDeath(ulong actorId, Vector3 deathPosition)
        {
            if (_dungeonLoot == null || _lootDefinition == null
                || !_lootDefinition.TryGetNpc(actorId, out var npc) || npc.LootStageIndex < 0) return false;
            bool accepted = _dungeonLoot.TryRollOnce(actorId, npc.LootStageIndex, npc.LootEnemyIndex,
                _lootDefinition.GetLootTable(npc.LootStageIndex), deathPosition);
            _world.SynchronizeCoins();
            return accepted;
        }

        // 唯一Authority Tick传dt，Frozen不推进，因此不会按墙钟累积恢复后的重试债务。
        public void StepDungeonLoot(float deltaTime, bool canAdvance)
        {
            _dungeonLoot?.Step(deltaTime, canAdvance);
            if (canAdvance) _world.SynchronizeCoins();
        }
        public void Dispose() { _dungeonLoot?.Dispose(); _dungeonLoot = null; _world.Dispose(); }

    }
}

