using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>两端共同构建的运动世界输入；在线参数属于数据资产，不散落于网络组件。</summary>
    [CreateAssetMenu(menuName = "Norn/Online/Movement Profile")]
    public sealed class OnlineMovementProfile : ScriptableObject
    {
        [SerializeField] private GameObject _playerPrefab, _enemyPrefab, _worldPrefab;
        [SerializeField] private Vector3[] _playerSpawns = { new Vector3(-3, 1, -5), new Vector3(0, 1, -5), new Vector3(3, 1, -5), new Vector3(6, 1, -5) };
        [SerializeField] private Vector3 _enemySpawn = new Vector3(0, 1, 8);
        [SerializeField, Min(.01f)] private float _snapshotInterval = 1f / 15f;
        [SerializeField, Min(0)] private float _interpolationDelay = .1f;
        [SerializeField] private long _simulationHash;
        public GameObject PlayerPrefab => _playerPrefab;
        public GameObject EnemyPrefab => _enemyPrefab;
        public GameObject WorldPrefab => _worldPrefab;
        public Vector3 EnemySpawn => _enemySpawn;
        public float SnapshotInterval => Mathf.Max(.01f, _snapshotInterval);
        public float InterpolationDelay => Mathf.Max(0, _interpolationDelay);
        public ulong SimulationHash => unchecked((ulong)_simulationHash);
        public Vector3 PlayerSpawn(int seat) => _playerSpawns != null && seat >= 0 && seat < _playerSpawns.Length ? _playerSpawns[seat] : Vector3.up;
    }
}
