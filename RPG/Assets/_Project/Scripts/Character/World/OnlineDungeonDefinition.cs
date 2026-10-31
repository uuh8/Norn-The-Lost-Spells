using System;
using Game.Combat;
using Game.Core;
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
        [SerializeField] private GameObject _worldPrefab;
        [SerializeField, Tooltip("保留原区域对遭遇HUD的显隐选择；关闭只隐藏区域／目标／剩余敌人，不影响通知或基础战斗UI。")]
        private bool _showEncounterHud = true;
        [SerializeField] private Vector3[] _playerSpawns = new Vector3[OnlineActorLimits.PlayerCount];
        [SerializeField] private OnlineDungeonNpcSpawn[] _npcs = Array.Empty<OnlineDungeonNpcSpawn>();
        [SerializeField] private OnlineDungeonEncounterDefinition[] _encounters = Array.Empty<OnlineDungeonEncounterDefinition>();
        public string DungeonId => _dungeonId;
        public string SourceScenePath => _sourceScenePath;
        public string SourceHash => _sourceHash;
        public GameObject WorldPrefab => _worldPrefab;
        // 旧配置未序列化该字段时保持常规遭遇 HUD；正式导出再从原场景的 Adapter enabled 明确写入。
        // 这是纯表现 authoring，不影响遭遇推进、Boss 激活、伤害或服务器 Tick。
        public bool ShowEncounterHud => _showEncounterHud;
        public int NpcCount => _npcs != null ? _npcs.Length : 0;
        public int EncounterCount => _encounters != null ? _encounters.Length : 0;

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
            return true;
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
