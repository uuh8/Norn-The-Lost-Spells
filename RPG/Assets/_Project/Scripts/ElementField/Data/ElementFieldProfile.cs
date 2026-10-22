using System;
using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 有限元素场的 Gameplay 调参资产。这里保存网格规模与离散模拟预算，
    /// 不保存 Material、ParticleSystem 等视觉资源，以维持 Gameplay 与 Rendering 的单向依赖。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Element Field/Element Field Profile", fileName = "ElementFieldProfile")]
    public sealed class ElementFieldProfile : ScriptableObject
    {
        [Header("Grid")]
        [SerializeField] private Vector3Int _dimensions = new Vector3Int(32, 16, 32);
        [SerializeField, Min(0.01f)] private float _cellSize = 0.25f;
        [SerializeField, Min(1)] private int _chunkSize = 8;
        [SerializeField, Min(1)] private int _maximumCellCount = 131072;

        [Header("Fixed Simulation")]
        [SerializeField, Min(0.1f)] private float _tickRate = 10f;
        [SerializeField, Min(1)] private int _maxPendingWrites = 128;
        [SerializeField, Min(1)] private int _maxCatchUpTicks = 3;
        [SerializeField, Range(0, byte.MaxValue)] private int _maxDownFlowPerTick = 64;
        [SerializeField, Range(0, byte.MaxValue)] private int _maxLateralFlowPerTick = 16;
        [SerializeField, Range(0, byte.MaxValue)] private int _fireDecayPerTick = 1;

        [Header("Shared P2 Reaction Rules")]
        [SerializeField] private ElementReactionProfile _reactionProfile;

        public Vector3Int Dimensions => _dimensions;
        public float CellSize => _cellSize;
        public float TickRate => _tickRate;
        public int ChunkSize => _chunkSize;
        public int MaxPendingWrites => _maxPendingWrites;
        public int MaxCatchUpTicks => _maxCatchUpTicks;
        public byte MaxDownFlowPerTick => (byte)_maxDownFlowPerTick;
        public byte MaxLateralFlowPerTick => (byte)_maxLateralFlowPerTick;
        public byte FireDecayPerTick => (byte)_fireDecayPerTick;
        public int MaximumCellCount => _maximumCellCount;
        public ElementReactionProfile ReactionProfile => _reactionProfile;

        public ElementFieldSimulationSettings CreateSimulationSettings()
        {
            if (_reactionProfile == null)
            {
                throw new InvalidOperationException(
                    "ElementFieldProfile requires an ElementReactionProfile before creating simulation settings.");
            }

            // Runtime 初始化时只调用一次，把可变 ScriptableObject 数据复制成只读 Value Snapshot。
            return new ElementFieldSimulationSettings(
                _cellSize,
                (byte)_maxDownFlowPerTick,
                (byte)_maxLateralFlowPerTick,
                (byte)_fireDecayPerTick,
                _chunkSize,
                _reactionProfile.Extinguish);
        }

        private void OnValidate()
        {
            // OnValidate 只阻止 Inspector 中的非法基础值；总 Cell 数是否超过预算仍由 ElementGrid 构造函数拒绝，
            // 这样不会为了“自动修复”而悄悄扩大 MaximumCellCount，破坏原本的内存安全上限。
            _dimensions.x = Mathf.Max(1, _dimensions.x);
            _dimensions.y = Mathf.Max(1, _dimensions.y);
            _dimensions.z = Mathf.Max(1, _dimensions.z);
            _cellSize = Mathf.Max(0.01f, _cellSize);
            _tickRate = Mathf.Max(0.1f, _tickRate);
            _chunkSize = Mathf.Max(1, _chunkSize);
            _maxPendingWrites = Mathf.Max(1, _maxPendingWrites);
            _maxCatchUpTicks = Mathf.Max(1, _maxCatchUpTicks);
            _maxDownFlowPerTick = Mathf.Clamp(_maxDownFlowPerTick, 0, byte.MaxValue);
            _maxLateralFlowPerTick = Mathf.Clamp(_maxLateralFlowPerTick, 0, byte.MaxValue);
            _fireDecayPerTick = Mathf.Clamp(_fireDecayPerTick, 0, byte.MaxValue);
            _maximumCellCount = Mathf.Max(1, _maximumCellCount);

            if (_reactionProfile != null)
            {
                // Amount 是 byte，低速反应每 Tick 若连 1 个量化单位都达不到，会长期 Round 成 0。
                float minimumByteConsumption =
                    _reactionProfile.Extinguish.LowRatePerSecond / _tickRate * byte.MaxValue / 100f;
                if (_reactionProfile.Extinguish.LowRatePerSecond > 0f && minimumByteConsumption < 1f)
                {
                    GameLog.Warn(
                        "ElementField low Extinguish rate is below the byte quantization threshold at the current TickRate.",
                        "ElementField");
                }
            }
        }
    }
}
