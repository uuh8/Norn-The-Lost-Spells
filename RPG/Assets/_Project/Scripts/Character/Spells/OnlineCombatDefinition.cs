using Game.Run;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>两端共同的战斗 Authoring 输入；网络只传此表的索引，不能让 Client 指定任意 Prefab。</summary>
    [CreateAssetMenu(menuName = "Norn/Online/Combat Definition")]
    public sealed class OnlineCombatDefinition : ScriptableObject
    {
        [SerializeField] private ItemCatalog _catalog;
        [SerializeField] private InputActionReference _selectWand1, _selectWand2, _selectWand3;
        [SerializeField] private string _gameplayConfigHash;
        [SerializeField] private float _groundReleaseSeconds, _airReleaseSeconds;
        [SerializeField] private int _groundAnimationHash, _airAnimationHash;
        [SerializeField] private Vector3 _groundSpawnOffset, _airSpawnOffset;
        [SerializeField] private GameObject[] _outputPrefabs;
        [SerializeField] private AudioClip[] _soundClips;
        [SerializeField] private GameObject _rangedEnemyPrefab, _bossPrefab;
        [SerializeField] private float _meleeClipDuration, _rangedClipDuration;
        [SerializeField, Min(.1f)] private float _bossActivationRadius = 10f;
        [SerializeField] private Vector3 _rangedSpawn = new Vector3(8, .1f, 8), _bossSpawn = new Vector3(0, .1f, 12);
        private string _cachedHash;
        private ulong _wireHash;
        public ItemCatalog Catalog => _catalog;
        /// <summary>
        /// Editor从原单机WandActions导入同一引用，不在在线代码硬编码KeyCode。
        /// Wizard生产Prefab本身没有库存Adapter；不能假定运行时AddComponent会自动取得场景里的Inspector字段。
        /// </summary>
        public InputActionReference GetWandSelectionAction(int slot)
            => slot == 0 ? _selectWand1 : slot == 1 ? _selectWand2 : slot == 2 ? _selectWand3 : null;
        public GameObject RangedEnemyPrefab => _rangedEnemyPrefab;
        public GameObject BossPrefab => _bossPrefab;
        public float BossActivationRadius => _bossActivationRadius;
        public float EnemyClipDuration(ulong actorId) => actorId == 5 ? _meleeClipDuration : _rangedClipDuration;
        public Vector3 EnemySpawn(ulong actorId) => actorId == 6 ? _rangedSpawn : _bossSpawn;
        public string GameplayConfigHash => _gameplayConfigHash;
        public ulong WireHash
        {
            get
            {
                if (ReferenceEquals(_cachedHash, _gameplayConfigHash)) return _wireHash;
                _cachedHash = _gameplayConfigHash; _wireHash = 0;
                if (string.IsNullOrEmpty(_gameplayConfigHash) || _gameplayConfigHash.Length != 64) return 0;
                // 只在资源首次读取/Editor 改配置时解析；稳态发送 Snapshot 不创建 Substring。
                ulong.TryParse(_gameplayConfigHash.Substring(0, 16), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out _wireHash);
                return _wireHash;
            }
        }
        public float ReleaseSeconds(bool airborne) => airborne ? _airReleaseSeconds : _groundReleaseSeconds;
        public int AnimationHash(bool airborne) => airborne ? _airAnimationHash : _groundAnimationHash;
        public Vector3 SpawnOffset(bool airborne) => airborne ? _airSpawnOffset : _groundSpawnOffset;
        public GameObject OutputPrefab(uint key) => _outputPrefabs != null && key > 0 && key <= _outputPrefabs.Length ? _outputPrefabs[key - 1] : null;
        public uint FindOutputKey(GameObject prefab)
        {
            if (_outputPrefabs == null || prefab == null) return 0;
            for (int i = 0; i < _outputPrefabs.Length; i++) if (_outputPrefabs[i] == prefab) return (uint)(i + 1);
            return 0;
        }
        public AudioClip SoundClip(uint key) => _soundClips != null && key > 0 && key <= _soundClips.Length ? _soundClips[key - 1] : null;
        public uint FindSoundKey(AudioClip clip)
        {
            // 与输出表一样，0 表示没有声音；只有 Editor 固化的 AudioClip 才能经网络重放。
            if (_soundClips == null || clip == null) return 0;
            for (int i = 0; i < _soundClips.Length; i++) if (_soundClips[i] == clip) return (uint)(i + 1);
            return 0;
        }
    }
}
