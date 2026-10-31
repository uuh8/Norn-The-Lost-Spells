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
        [Tooltip("运动快照发送周期（秒），与渲染FPS不同。过载时跳过旧周期，只发送最新状态。")]
        [SerializeField, Min(.01f)] private float _snapshotInterval = 1f / 30f;
        // 50ms相当于1.5个30Hz快照周期：仍为插值留后继样本，稳定链路不再固定支付两个周期。
        // 网络抖动由播放器在maximumDelay范围内增加headroom；它不是完整输入到屏幕延迟或RTT。
        [Tooltip("稳定链路播放缓冲下限（秒）：50ms约为1.5个30Hz快照周期，保留后继样本供插值。")]
        [SerializeField, Min(0)] private float _interpolationDelay = .05f;
        [Tooltip("短期网络抖动允许增加的缓冲上限（秒）；不是往返延迟RTT，也不允许无限积压。")]
        [SerializeField, Min(.01f)] private float _maximumInterpolationDelay = .2f;
        [SerializeField] private long _simulationHash;
        public GameObject PlayerPrefab => _playerPrefab;
        public GameObject EnemyPrefab => _enemyPrefab;
        public GameObject WorldPrefab => _worldPrefab;
        public Vector3 EnemySpawn => _enemySpawn;
        public float SnapshotInterval => Mathf.Max(.01f, _snapshotInterval);
        public float InterpolationDelay => Mathf.Max(0, _interpolationDelay);
        public float MaximumInterpolationDelay => Mathf.Max(InterpolationDelay, _maximumInterpolationDelay);
        public ulong SimulationHash => unchecked((ulong)_simulationHash);
        public Vector3 PlayerSpawn(int seat) => _playerSpawns != null && seat >= 0 && seat < _playerSpawns.Length ? _playerSpawns[seat] : Vector3.up;
    }
}
