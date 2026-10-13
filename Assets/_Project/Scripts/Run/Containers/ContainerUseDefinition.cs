using UnityEngine;

namespace Game.Run
{
    /// <summary>测试与正式交互共用的数据配置；Prefab只作为资源，不引入Combat类型依赖。</summary>
    [CreateAssetMenu(menuName = "Game/Inventory/Container Use")]
    public sealed class ContainerUseDefinition : ScriptableObject
    {
        [SerializeField, Min(1)] private float _pourRate = 100;
        [SerializeField, Min(1)] private float _collectionRate = 2000;
        [SerializeField, Min(.1f)] private float _forwardDistance = 1.5f;
        [SerializeField, Min(.1f)] private float _pourSpeed = 8;
        [SerializeField, Min(0)] private float _pourLift = 2;
        [SerializeField, Range(.01f, 1)] private float _depositRadius = .2f;
        [SerializeField, Min(1)] private float _throwSpeed = 24;
        [SerializeField, Min(0)] private float _throwLift = 6;
        [SerializeField, Min(1)] private float _flightSeconds = 8;
        [SerializeField, Min(1)] private float _maximumFlightDistance = 80;
        [SerializeField] private GameObject _throwPrefab;
        public float PourRate => _pourRate;
        public float CollectionRate => _collectionRate;
        public float ForwardDistance => _forwardDistance;
        public float PourSpeed => _pourSpeed;
        public float PourLift => _pourLift;
        public float DepositRadius => _depositRadius;
        public float ThrowSpeed => _throwSpeed;
        public float ThrowLift => _throwLift;
        public float FlightSeconds => _flightSeconds;
        public float MaximumFlightDistance => _maximumFlightDistance;
        public GameObject ThrowPrefab => _throwPrefab;
    }
}
