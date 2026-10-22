using UnityEngine;

namespace Game.ElementField
{
    /// <summary>仅供独立 Server 实验的采样配置，不作为正式副本或线上资产数据。</summary>
    [CreateAssetMenu(menuName = "Norn/Diagnostics/Headless Grid Probe")]
    public sealed class HeadlessGridProbeProfile : ScriptableObject
    {
        [SerializeField, Min(1)] private int _processFrameRate = 30;
        [SerializeField, Min(0.1f)] private float _expectedTickRate = 10f;
        [SerializeField, Min(0.1f)] private float _warmupSeconds = 2f;
        [SerializeField, Min(1f)] private float _measureSeconds = 4f;
        [SerializeField, Min(0.01f)] private float _tickRateTolerance = 1f;
        [SerializeField] private Vector3 _waterPosition = new Vector3(.75f, 2.75f, .75f);
        [SerializeField, Range(1, ushort.MaxValue)] private int _waterQuantity = 128;
        [SerializeField, Min(0f)] private float _waterRadius = .05f;
        [SerializeField] private Vector3Int _scanMinimum = new Vector3Int(-8, 0, -8);
        [SerializeField] private Vector3Int _scanSize = new Vector3Int(16, 12, 16);

        public int ProcessFrameRate => _processFrameRate;
        public float ExpectedTickRate => _expectedTickRate;
        public float WarmupSeconds => _warmupSeconds;
        public float MeasureSeconds => _measureSeconds;
        public float TickRateTolerance => _tickRateTolerance;
        public Vector3 WaterPosition => _waterPosition;
        // 水量使用已有 GMU 记账单位，不把体积、细格数量和粗液体格数量混为一谈。
        public ushort WaterQuantity => checked((ushort)_waterQuantity);
        public float WaterRadius => _waterRadius;
        public Vector3Int ScanMinimum => _scanMinimum;
        public Vector3Int ScanSize => _scanSize;

        public bool IsValid => _processFrameRate > 0 && float.IsFinite(_expectedTickRate) && _expectedTickRate > 0 &&
            float.IsFinite(_warmupSeconds) && _warmupSeconds > 0 && float.IsFinite(_measureSeconds) && _measureSeconds >= 1 &&
            float.IsFinite(_tickRateTolerance) && _tickRateTolerance > 0 &&
            _waterQuantity > 0 && _waterQuantity <= ushort.MaxValue && float.IsFinite(_waterRadius) && _waterRadius >= 0 &&
            float.IsFinite(_waterPosition.x) && float.IsFinite(_waterPosition.y) && float.IsFinite(_waterPosition.z) &&
            _scanSize.x > 0 && _scanSize.x <= 64 && _scanSize.y > 0 && _scanSize.y <= 64 && _scanSize.z > 0 && _scanSize.z <= 64 &&
            (long)_scanSize.x * _scanSize.y * _scanSize.z <= 65536;
    }
}
