using System;
using Game.Combat;
using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 一份由原Scene离线导出的NPC出生记录。ActorId属于本Run的固定席位，不使用Unity InstanceID；
    /// Prefab包含原实例覆盖值，Pose使用原世界坐标，不能按敌人类别给所有副本统一替换出生点。
    /// </summary>
    [Serializable]
    public sealed class OnlineDungeonNpcSpawn
    {
        public ulong ActorId;
        public GameObject Prefab;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public float AttackClipDuration;
        public int EncounterIndex;
        public bool InitiallyVisible;
        // 原DungeonRunContent中的关卡/局部敌人位置，不以Actor InstanceID、排序后的名字或死亡顺序重建。
        // 没有原掉落表的区域保持-1；这不是开局赠送或所有NPC通用的新掉落规则。
        public int LootStageIndex = -1, LootEnemyIndex = -1;
    }

    /// <summary>原Scene固定固体投放的值副本；保持稳定SpawnId和实例世界姿态，不持有单机会话/Scene引用。</summary>
    [Serializable]
    public sealed class OnlineDungeonFixedSolidSpawn
    {
        public string SpawnId, ContentId;
        public byte MaterialKey;
        public ulong Quantity;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
    }

    /// <summary>
    /// 顺序与原UI文字仍来自原关卡。Scene中的入口Collider/Gate引用由Bindings保存；
    /// SO只保存跨两端共同构建的数据，不序列化某次运行中的Scene对象或可变遭遇进度。
    /// </summary>
    [Serializable]
    public sealed class OnlineDungeonEncounterDefinition
    {
        public string DisplayName = "";
        public string ObjectiveText = "";
        public bool ExitToStart;
    }

    /// <summary>
    /// 一张正式在线地图的不可变Authoring输入，交给共同Registry/Encounter Runtime组合。
    /// 地图ID只是本地Catalog中的键；SourceHash标记来源内容一致性，不把任意客户端声明变成可信地图。
    /// 认证连接/LaunchDescriptor核验由Multiplayer负责，数据类不依赖Mirror或业务协议程序集。
    /// </summary>
    [CreateAssetMenu(menuName = "Norn/Online/Dungeon Definition")]
    public sealed class OnlineDungeonDefinition : ScriptableObject
    {
        [SerializeField] private string _dungeonId, _sourceScenePath, _sourceHash;
        // SourceHash现在覆盖掉落规则依赖；另保留原Scene字节基线，窄刷新不能掩盖静态几何/遭遇发生变化。
        [SerializeField] private string _sourceSceneHash;
        [SerializeField] private GameObject _worldPrefab;
        [SerializeField, Tooltip("保留原区域对遭遇HUD的显隐选择；关闭只隐藏区域／目标／剩余敌人，不影响通知或基础战斗UI。")]
        private bool _showEncounterHud = true;
        [SerializeField] private Vector3[] _playerSpawns = new Vector3[OnlineActorLimits.PlayerCount];
        [SerializeField] private OnlineDungeonNpcSpawn[] _npcs = Array.Empty<OnlineDungeonNpcSpawn>();
        [SerializeField] private OnlineDungeonEncounterDefinition[] _encounters = Array.Empty<OnlineDungeonEncounterDefinition>();
        [SerializeField] private DungeonLootTable[] _lootTables = Array.Empty<DungeonLootTable>();
        // 同一原P7 DungeonRunContent的币Prefab引用，保留原视觉子层级/Trigger，不为DS另造近似模型。
        [SerializeField] private GameObject _coinPrefab;
        [SerializeField] private OnlineDungeonFixedSolidSpawn[] _fixedSolidSpawns = Array.Empty<OnlineDungeonFixedSolidSpawn>();
        public const int MaximumContainerLootNpcs = 12;
        public const int MaximumFixedSolidSpawns = 1;
        public string DungeonId => _dungeonId;
        /// <summary>和平Home仍由单人DS运行真实Gameplay，但没有战斗遭遇、掉落或通关奖励。</summary>
        public bool IsPeacefulHome => _dungeonId == "home";
        public string SourceScenePath => _sourceScenePath;
        public string SourceHash => _sourceHash;
        public string SourceSceneHash => _sourceSceneHash;
        public GameObject WorldPrefab => _worldPrefab;
        // 旧配置未序列化该字段时保持常规遭遇 HUD；正式导出再从原场景的 Adapter enabled 明确写入。
        // 这是纯表现 authoring，不影响遭遇推进、Boss 激活、伤害或服务器 Tick。
        public bool ShowEncounterHud => _showEncounterHud;
        public int NpcCount => _npcs != null ? _npcs.Length : 0;
        public int EncounterCount => _encounters != null ? _encounters.Length : 0;
        public int LootTableCount => _lootTables != null ? _lootTables.Length : 0;
        public GameObject CoinPrefab => _coinPrefab;
        public int FixedSolidCount => _fixedSolidSpawns != null ? _fixedSolidSpawns.Length : 0;

        public bool TryValidateCoinSource(out string error)
        {
            error = null;
            if (LootTableCount == 0) return _coinPrefab == null || Fail("无原掉落表的地图不能新增金币Prefab来源。", out error);
            if (_coinPrefab == null || _coinPrefab.GetComponent<WorldCurrencyPickup>() == null)
                return Fail("原P7金币Prefab缺失或缺少原WorldCurrencyPickup。", out error);
            return true;
        }

        public OnlineDungeonFixedSolidSpawn GetFixedSolid(int index)
        {
            if (_fixedSolidSpawns == null || index < 0 || index >= _fixedSolidSpawns.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return _fixedSolidSpawns[index];
        }

        /// <summary>本段只接已批准的原P7冰256GMU；P8和原food不产生新来源，扩来源必须另改明确合同。</summary>
        public bool TryValidateFixedSolidSpawns(out string error)
        {
            error = null;
            if (_fixedSolidSpawns == null || _fixedSolidSpawns.Length > MaximumFixedSolidSpawns)
                return Fail("固定固体来源数组缺失或超过原唯一来源边界。", out error);
            if (_fixedSolidSpawns.Length == 0) return true; // 旧导出兼容空；正式Verify另与原Scene逐项对照。
            var spawn = _fixedSolidSpawns[0];
            if (_dungeonId != "p7" || spawn == null || spawn.SpawnId != "p7.explore.0" || spawn.ContentId != "containertest.ice"
                || spawn.MaterialKey != 5 || spawn.Quantity != 256 || !Finite(spawn.Position) || !ValidRotation(spawn.Rotation))
                return Fail("固定固体必须是原P7冰256GMU及有效世界姿态，不能新增/默认补满来源。", out error);
            return true;
        }

        /// <summary>返回原SO整表，Run按原顺序建立完整抽样计划；线上只提交容器，不能单独重抽该行改变后续样本序列。</summary>
        public DungeonLootTable GetLootTable(int stage)
        {
            if (_lootTables == null || stage < 0 || stage >= _lootTables.Length) throw new ArgumentOutOfRangeException(nameof(stage));
            return _lootTables[stage];
        }

        public bool TryGetContainerDrop(ulong actorId, out DungeonItemDrop entry, out int stageIndex, out int enemyIndex)
        {
            entry = null; stageIndex = enemyIndex = -1;
            if (!TryGetNpc(actorId, out var npc) || npc.LootStageIndex < 0 || npc.LootStageIndex >= LootTableCount) return false;
            var table = _lootTables[npc.LootStageIndex]; if (table == null || table.Items == null) return false;
            foreach (var row in table.Items)
                if (row != null && row.Pool != null && row.Pool.Length > 0 && row.Pool[0] != null && row.Pool[0].Kind == ItemKind.Container)
                { entry = row; stageIndex = npc.LootStageIndex; enemyIndex = npc.LootEnemyIndex; return true; }
            return false;
        }

        /// <summary>
        /// 历史调用名保留；完整Item来源与新入口使用同一个配置验证，不能再按容器Kind过滤原表。
        /// </summary>
        public bool TryValidateContainerDrops(out string error) => TryValidateItemDrops(out error);

        /// <summary>
        /// 纯配置有限证明：最多12个原敌人，原表所有有概率条目的MaxCount按成员汇总，最多48件。
        /// 不要求候选池同Kind、不改变原Chance/Pool/次数/随机杖规则，也不把MaterialQuantity当空瓶填料。
        /// 这个来源界只保护Run内稳定key；共享Ground容量另在登记事务中检查，失败保留原计划重试。
        /// </summary>
        public bool TryValidateItemDrops(out string error)
        {
            error = null;
            if (_lootTables == null || _npcs == null || _encounters == null) return Fail("掉落来源数组不能缺省。", out error);
            if (_lootTables.Length == 0)
            {
                // Unity/JsonUtility读取旧作者数据时，缺失int字段得到0而非initializer的-1。
                // 没有表就绝不产生来源，因此仅兼容成对0/0作为legacy-none；官方刷新会写为-1/-1，
                // 混合表示或其它索引仍拒绝，有表路径仍要求真实连续stage/enemy，不能借兼容默认值发放。
                foreach (var npc in _npcs)
                    if (npc == null || !(npc.LootStageIndex == -1 && npc.LootEnemyIndex == -1
                        || npc.LootStageIndex == 0 && npc.LootEnemyIndex == 0))
                        return Fail("无原掉落表时索引必须是none或旧缺字段表示。", out error);
                return true;
            }
            if (_dungeonId == "p8" || _dungeonId == "p7" && _lootTables.Length != 4
                || _lootTables.Length != _encounters.Length || _npcs.Length == 0 || _npcs.Length > MaximumContainerLootNpcs)
                return Fail("原掉落表与遭遇或最多12敌人的有限来源证明不一致。", out error);
            var maximumByStage = new int[_lootTables.Length];
            for (int stage = 0; stage < _lootTables.Length; stage++)
            {
                var table = _lootTables[stage];
                if (table == null || !table.IsValid) return Fail("原掉落表或法杖范围非法，拒绝改变原抽样计划。", out error);
                foreach (var row in table.Items)
                {
                    if (row.Chance <= 0) continue;
                    if (row.MaxCount > OnlineDungeonContainerLoot.MaximumLootItems - maximumByStage[stage])
                        return Fail("原Item来源超出48件稳定登记预算，拒绝截断抽样或扩大共享地面。", out error);
                    maximumByStage[stage] += row.MaxCount;
                }
            }
            var nextEnemy = new int[_lootTables.Length];
            int maximumItems = 0;
            for (int i = 0; i < _npcs.Length; i++)
            {
                var npc = _npcs[i];
                if (npc == null || npc.ActorId != (ulong)(OnlineActorLimits.FirstNpcActorId + i)
                    || npc.LootStageIndex != npc.EncounterIndex || npc.LootStageIndex < 0 || npc.LootStageIndex >= _lootTables.Length
                    || npc.LootEnemyIndex != nextEnemy[npc.LootStageIndex]++)
                    return Fail("掉落stage/enemy必须对应原关卡连续局部索引。", out error);
                if (maximumByStage[npc.LootStageIndex] > OnlineDungeonContainerLoot.MaximumLootItems - maximumItems)
                    return Fail("原12敌完整Item计划超过48件稳定登记预算。", out error);
                maximumItems += maximumByStage[npc.LootStageIndex];
            }
            foreach (int count in nextEnemy) if (count == 0) return Fail("掉落阶段不能没有原敌人。", out error);
            return true;
        }

        public OnlineDungeonNpcSpawn GetNpc(int index)
        {
            if (_npcs == null || index < 0 || index >= _npcs.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return _npcs[index];
        }
        public bool TryGetNpc(ulong actorId, out OnlineDungeonNpcSpawn spawn)
        {
            spawn = null;
            if (actorId < OnlineActorLimits.FirstNpcActorId || actorId > OnlineActorLimits.MaximumActors || _npcs == null) return false;
            int index = (int)actorId - OnlineActorLimits.FirstNpcActorId;
            // 导出/Validate要求连续席位，查询直接索引，不在每条快照里遍历配置或创建Dictionary。
            if (index >= _npcs.Length || _npcs[index] == null || _npcs[index].ActorId != actorId) return false;
            spawn = _npcs[index]; return true;
        }
        public Vector3 PlayerSpawn(int seat)
        {
            if (_playerSpawns == null || seat < 0 || seat >= _playerSpawns.Length) throw new ArgumentOutOfRangeException(nameof(seat));
            return _playerSpawns[seat];
        }
        public OnlineDungeonEncounterDefinition GetEncounter(int index)
        {
            if (_encounters == null || index < 0 || index >= _encounters.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return _encounters[index];
        }

        /// <summary>
        /// 仅在导出/世界初始化等离散边界验证整表，不能每Tick调用。这里只核对结构与数值；
        /// NavMesh是否加载、World是否混入单机Owner由导出器及世界组合验证，不让纯数据规则依赖编辑器。
        /// </summary>
        public bool TryValidate(out string error)
        {
            error = null;
            if (!IsValidDungeonId(_dungeonId)) return Fail("副本ID格式无效。", out error);
            if (string.IsNullOrEmpty(_sourceScenePath) || !_sourceScenePath.StartsWith("Assets/", StringComparison.Ordinal)
                || !_sourceScenePath.EndsWith(".unity", StringComparison.Ordinal) || _sourceScenePath.Contains("..") || _sourceScenePath.IndexOf('\\') >= 0)
                return Fail("副本缺少规范的原Scene来源。", out error);
            if (!IsHash(_sourceHash) || _worldPrefab == null) return Fail("副本缺少来源Hash或受控世界Prefab。", out error);
            if (_playerSpawns == null || _playerSpawns.Length != OnlineActorLimits.PlayerCount)
                return Fail("副本必须明确提供四个玩家出生位置。", out error);
            for (int i = 0; i < _playerSpawns.Length; i++) if (!Finite(_playerSpawns[i])) return Fail("玩家出生位置包含非有限值。", out error);
            if (IsPeacefulHome)
            {
                // 例外仅限原Home的完整空遭遇配置；不能为适配战斗Runtime凭空制造NPC。
                // 其他地图仍走下方原有严格检查，地图身份也仍须经过业务白名单与Catalog。
                if (_sourceScenePath != "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity"
                    || _npcs == null || _npcs.Length != 0 || _encounters == null || _encounters.Length != 0
                    || _showEncounterHud || _lootTables == null || _lootTables.Length != 0
                    || _fixedSolidSpawns == null || _fixedSolidSpawns.Length != 0 || _coinPrefab != null)
                    return Fail("和平Home必须复用原场景且无NPC、遭遇、掉落和遭遇HUD。", out error);
                return true;
            }
            if (_npcs == null || _npcs.Length == 0 || _npcs.Length > OnlineActorLimits.MaximumNpcs
                || _encounters == null || _encounters.Length == 0 || _encounters.Length > OnlineActorLimits.MaximumEncounters)
                return Fail("副本NPC或遭遇数量超出共同预算。", out error);
            for (int i = 0; i < _encounters.Length; i++)
                if (_encounters[i] == null || string.IsNullOrWhiteSpace(_encounters[i].DisplayName))
                    return Fail("副本遭遇描述缺失。", out error);
            var counts = new int[_encounters.Length];
            for (int i = 0; i < _npcs.Length; i++)
            {
                var npc = _npcs[i];
                if (npc == null || npc.ActorId != (ulong)(OnlineActorLimits.FirstNpcActorId + i)
                    || npc.Prefab == null || !Finite(npc.Position) || !ValidRotation(npc.Rotation)
                    || !float.IsFinite(npc.AttackClipDuration) || npc.AttackClipDuration <= 0
                    || npc.EncounterIndex < 0 || npc.EncounterIndex >= _encounters.Length)
                    return Fail("NPC的Actor、Prefab、原姿态、攻击时长或遭遇归属无效。", out error);
                var enemy = npc.Prefab.GetComponent<EnemyControllerBase>();
                var boss = npc.Prefab.GetComponent<WizardBossController>();
                if ((enemy != null) == (boss != null) || npc.Prefab.GetComponent<HealthComponent>() == null
                    || npc.Prefab.GetComponent<PlayerControllerBase>() != null)
                    return Fail("NPC Prefab必须有唯一敌人Controller及Health，不能混入玩家Owner。", out error);
                counts[npc.EncounterIndex]++;
            }
            for (int i = 0; i < counts.Length; i++) if (counts[i] == 0) return Fail("空遭遇没有可判定的NPC，拒绝初始化。", out error);
            return TryValidateContainerDrops(out error) && TryValidateFixedSolidSpawns(out error) && TryValidateCoinSource(out error);
        }
        internal static bool IsValidDungeonId(string value)
        {
            // 与共享控制面采用同样的格式边界，但本层没有NetworkProtocol依赖。是否登记由Catalog白名单决定，
            // 不把p7/p8硬编码进领域类，新增副本只需导出/注册配置。
            if (string.IsNullOrEmpty(value) || value.Length > 32 || value[0] < 'a' || value[0] > 'z') return false;
            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i]; if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '-')) return false;
            }
            return true;
        }
        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++) if (!(value[i] >= '0' && value[i] <= '9' || value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool ValidRotation(Quaternion value)
        {
            if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z) || !float.IsFinite(value.w)) return false;
            float norm = Quaternion.Dot(value, value); return float.IsFinite(norm) && Mathf.Abs(norm - 1) < .01f;
        }
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
