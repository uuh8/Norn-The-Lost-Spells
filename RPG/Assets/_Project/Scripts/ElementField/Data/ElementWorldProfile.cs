using System;
using Game.Combat;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 连续关卡稀疏元素世界的 Gameplay 配置。它只描述 Cell、Streaming 与 Simulation 预算，
    /// 不保存 Water Material、Fire Particle 等 Presentation 资源。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Element Field/Element World Profile", fileName = "ElementWorldProfile")]
    public sealed class ElementWorldProfile : ScriptableObject
    {
        [Header("Cell And Chunk")]
        [SerializeField, Min(0.01f)] private float _cellSize = 0.25f;
        [SerializeField, Min(1)] private int _chunkSize = 8;
        [SerializeField, Min(1)] private int _maximumResidentChunks = 512;

        [Header("Interest Region")]
        [SerializeField, Min(0)] private int _activeRadiusXZChunks = 10;
        [SerializeField, Min(0)] private int _activeRadiusYChunks = 3;
        [SerializeField, Min(0f)] private float _sleepGraceSeconds = 2f;

        [Header("Fixed Simulation")]
        [SerializeField, Min(0.1f)] private float _tickRate = 10f;
        [SerializeField, Min(1)] private int _maxPendingWrites = 256;
        [SerializeField, Min(1)] private int _maxCatchUpTicks = 3;
        [Tooltip("连续多少个 Simulation Tick 没有 Cell 变化后，将非空 Chunk 视为稳定并移出 Solver。")]
        [SerializeField, Min(1)] private int _settleAfterUnchangedTicks = 2;
        [SerializeField, Range(0, byte.MaxValue)] private int _maxDownFlowPerTick = 64;
        [SerializeField, Range(0, byte.MaxValue)] private int _maxLateralFlowPerTick = 16;
        [SerializeField, Range(0, byte.MaxValue)] private int _fireDecayPerTick = 1;
        [Tooltip("真实火格每Tick向下转移的最大强度；独立于Water后端。0关闭，255允许整格每Tick下落一格。")]
        [SerializeField,Range(0,byte.MaxValue)]private int _maxFireDownFlowPerTick;

        [Header("Water Simulation Backend")]
        [Tooltip("Legacy Cell=0、Gpu PBF=1、Grid Liquid=2；后端切换仅在退出并重新进入 Play 后生效。")]
        [SerializeField] private WaterSimulationMode _waterSimulationMode = WaterSimulationMode.LegacyCell;

        [Header("Fluid Chunk Streaming")]
        [Tooltip("完成 Archive/Restore 全链验证前默认关闭；开启后远处流体可转为 RAM Cell 快照并释放 GPU slot。")]
        [SerializeField] private bool _enableFluidChunkStreaming;
        [SerializeField, Min(0)] private int _fluidWarmPaddingChunks = 1;
        [SerializeField, Min(0f)] private float _fluidArchiveGraceSeconds = 2f;
        [SerializeField, Min(1)] private int _maximumArchivedFluidChunks = 512;
        [SerializeField, Min(1)] private int _maximumArchivedFluidCellRecords = 65536;
        [SerializeField, Min(1)] private int _maximumPendingFluidWrites = 256;
        [SerializeField, Min(0)] private int _gameplaySpawnReserveParticles = 512;
        [Tooltip("容量进入预留区时，把 Warm Region 内已 Sleeping 的液体聚合为 Dormant Cell，释放 GPU slot。")]
        [SerializeField] private bool _enableLocalFluidDormancy = true;
        [Tooltip("单帧最多把多少个归档粒子展开到 Restore staging；限制 CPU 主线程尖峰。")]
        [SerializeField, Min(1)] private int _maximumRestoreParticlesPerFrame = 128;

        [Header("Material Semantic And Routing")]
        [Tooltip("Canonical Material 定义目录；Runtime 初始化时复制为不可变 Snapshot。")]
        [SerializeField] private MaterialCatalog _materialCatalog;
        [Tooltip("只保存 Fire/Poison 等非 Water 的 Backend；Water 由 Compatibility Policy 决定。")]
        [SerializeField] private MaterialSimulationRoutingProfile _materialSimulationRouting;
        [Tooltip("Material Occupancy 到角色 Status 的表现投影；不属于 Material 基础定义。")]
        [SerializeField] private MaterialStatusProjectionProfile _materialStatusProjection;

        [Header("Shared P2 Reaction Rules")]
        [SerializeField] private ElementReactionProfile _reactionProfile;
        [Tooltip("Material Pair 到 Reaction Id 的语义绑定；具体数值公式仍由 Reaction Profile 提供。")]
        [SerializeField] private MaterialReactionBindingProfile _materialReactionBindings;

        public float CellSize => _cellSize;
        [Header("World Activity Expansion")]
        [SerializeField] private bool _enableWorldActivityExpansion;
        [SerializeField, Min(0.25f)] private float _unitActivityRadius = 4f;
        [SerializeField, Min(0.25f)] private float _projectileActivityRadius = 2f;
        public bool EnableWorldActivityExpansion => _enableWorldActivityExpansion;
        public float UnitActivityRadius => _unitActivityRadius;
        public float ProjectileActivityRadius => _projectileActivityRadius;
        public int ChunkSize => _chunkSize;
        public int MaximumResidentChunks => _maximumResidentChunks;
        public int ActiveRadiusXZChunks => _activeRadiusXZChunks;
        public int ActiveRadiusYChunks => _activeRadiusYChunks;
        public float SleepGraceSeconds => _sleepGraceSeconds;
        public float TickRate => _tickRate;
        public int MaxPendingWrites => _maxPendingWrites;
        public int MaxCatchUpTicks => _maxCatchUpTicks;
        public int SettleAfterUnchangedTicks => _settleAfterUnchangedTicks;
        public byte MaxDownFlowPerTick => (byte)_maxDownFlowPerTick;
        public byte MaxLateralFlowPerTick => (byte)_maxLateralFlowPerTick;
        public byte FireDecayPerTick => (byte)_fireDecayPerTick;
        public byte MaxFireDownFlowPerTick=>(byte)_maxFireDownFlowPerTick;
        public WaterSimulationMode WaterSimulationMode => _waterSimulationMode;
        public bool EnableFluidChunkStreaming => _enableFluidChunkStreaming;
        public MaterialCatalog MaterialCatalog => _materialCatalog;
        public MaterialSimulationRoutingProfile MaterialSimulationRouting => _materialSimulationRouting;
        public MaterialStatusProjectionProfile MaterialStatusProjection => _materialStatusProjection;
        public ElementReactionProfile ReactionProfile => _reactionProfile;
        public MaterialReactionBindingProfile MaterialReactionBindings => _materialReactionBindings;
        public ToxicCombustionTuning ToxicCombustion => _reactionProfile.ToxicCombustion;
        public IgniteGooTuning IgniteGoo => _reactionProfile.IgniteGoo;
        public AbsorbWaterTuning AbsorbWater => _reactionProfile.AbsorbWater;

        public long SleepGraceTicks
        {
            get
            {
                // Ceiling 保证配置的 Grace 秒数不会因为离散 Tick 向下取整而提前结束。
                return (long)Math.Ceiling(_sleepGraceSeconds * _tickRate);
            }
        }

        public ElementFieldSimulationSettings CreateSimulationSettings()
        {
            if (_reactionProfile == null)
            {
                throw new InvalidOperationException(
                    "ElementWorldProfile requires an ElementReactionProfile before creating simulation settings.");
            }

            return new ElementFieldSimulationSettings(
                _cellSize,
                (byte)_maxDownFlowPerTick,
                (byte)_maxLateralFlowPerTick,
                (byte)_fireDecayPerTick,
                _chunkSize,
                _waterSimulationMode == WaterSimulationMode.LegacyCell,
                _reactionProfile.Extinguish,(byte)_maxFireDownFlowPerTick);
        }

        public FluidChunkStreamingSettings CreateFluidChunkStreamingSettings()
        {
            // 构造函数再次验证，保证手改 YAML 或旧资产中的非法值不能绕过 OnValidate。
            return new FluidChunkStreamingSettings(
                _enableFluidChunkStreaming,
                _fluidWarmPaddingChunks,
                _fluidArchiveGraceSeconds,
                _maximumArchivedFluidChunks,
                _maximumArchivedFluidCellRecords,
                _maximumPendingFluidWrites,
                _gameplaySpawnReserveParticles,
                _enableLocalFluidDormancy,
                _maximumRestoreParticlesPerFrame);
        }

        private void OnValidate()
        {
            _cellSize = Mathf.Max(0.01f, _cellSize);
            // 活动半径会直接参与 Bounds 与 GPU Region 构造；旧资产或手改 YAML 也必须得到合法正值。
            _unitActivityRadius = Mathf.Max(0.25f, _unitActivityRadius);
            _projectileActivityRadius = Mathf.Max(0.25f, _projectileActivityRadius);
            _chunkSize = Mathf.Max(1, _chunkSize);
            _maximumResidentChunks = Mathf.Max(1, _maximumResidentChunks);
            _activeRadiusXZChunks = Mathf.Max(0, _activeRadiusXZChunks);
            _activeRadiusYChunks = Mathf.Max(0, _activeRadiusYChunks);
            _sleepGraceSeconds = Mathf.Max(0f, _sleepGraceSeconds);
            _tickRate = Mathf.Max(0.1f, _tickRate);
            _maxPendingWrites = Mathf.Max(1, _maxPendingWrites);
            _maxCatchUpTicks = Mathf.Max(1, _maxCatchUpTicks);
            _settleAfterUnchangedTicks = Mathf.Max(1, _settleAfterUnchangedTicks);
            _maxDownFlowPerTick = Mathf.Clamp(_maxDownFlowPerTick, 0, byte.MaxValue);
            _maxLateralFlowPerTick = Mathf.Clamp(_maxLateralFlowPerTick, 0, byte.MaxValue);
            _fireDecayPerTick = Mathf.Clamp(_fireDecayPerTick, 0, byte.MaxValue);
            _maxFireDownFlowPerTick=Mathf.Clamp(_maxFireDownFlowPerTick,0,byte.MaxValue);
            _fluidWarmPaddingChunks = Mathf.Max(0, _fluidWarmPaddingChunks);
            _fluidArchiveGraceSeconds = Mathf.Max(0f, _fluidArchiveGraceSeconds);
            _maximumArchivedFluidChunks = Mathf.Max(1, _maximumArchivedFluidChunks);
            _maximumArchivedFluidCellRecords = Mathf.Max(1, _maximumArchivedFluidCellRecords);
            _maximumPendingFluidWrites = Mathf.Max(1, _maximumPendingFluidWrites);
            _gameplaySpawnReserveParticles = Mathf.Max(0, _gameplaySpawnReserveParticles);
            _maximumRestoreParticlesPerFrame = Mathf.Max(1, _maximumRestoreParticlesPerFrame);
        }
    }
}
