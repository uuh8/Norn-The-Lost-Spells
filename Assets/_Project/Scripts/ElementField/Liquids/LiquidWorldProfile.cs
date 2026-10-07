using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    [CreateAssetMenu(menuName = "Game/Element Field/Grid Liquid World", fileName = "GridLiquidWorld")]
    public sealed class LiquidWorldProfile : ScriptableObject
    {
        [SerializeField, Min(1)] private int _maximumChunks = 1024;
        [SerializeField, Min(1)] private int _maximumActiveCells = 65536;
        [SerializeField, Min(1)] private int _maximumPendingDeposits = 256;
        [SerializeField, Min(1)] private int _maximumCellsPerTick = 1024;
        [SerializeField, Range(1, 255)] private int _cellCapacityGmu = 255;
        [Header("Liquid Volume Mapping")]
        [SerializeField, Range(1, 4), Tooltip("液体格边长是世界基础格的多少倍。2 倍边长用一个液体格代替八个基础格；火仍保留原分辨率。")]
        private int _resolutionScale = 1;
        [SerializeField, Min(.01f), Tooltip("每 GMU 所代表的体积倍率。4 表示相同容器只需原来的四分之一 GMU；分辨率和此倍率独立。进入 Play 前调整。")]
        private float _unitVolumeMultiplier = 1f;
        [SerializeField, Min(.01f)] private float _fallGravity = 9.81f;
        [SerializeField, Min(.01f)] private float _fallTerminalVelocity = 12f;
        [SerializeField, Min(1)] private int _maximumStableRegions = 32;
        [SerializeField, Min(1)] private int _maximumStableOutlets = 128;
        [SerializeField, Min(1)] private int _maximumStableRegionCells = 262144;
        [SerializeField, Min(1)] private int _maximumStableSurfacePatches = 1024;
        [SerializeField, Min(1)] private int _maximumImmediateExpansionCells = 1024;
        [SerializeField, Min(1)] private int _maximumImmediateExpansionSurfaceChunks = 4;
        [SerializeField] private bool _enableAutoStableDetection;
        [SerializeField, Min(1)] private int _maximumAutoCandidateCells = 8192;
        [SerializeField, Min(1)] private int _autoValidationCellsPerTick = 512;
        // 10 Hz 世界时钟下 10 Tick 约为一秒；期间仍逐 Tick 检查地形和 Chunk 版本，
        // 任何变化都取消候选，避免为多材料湖盆串行等待过长。
        [SerializeField, Min(1)] private int _autoQuietTicks = 10;
        [SerializeField] private LiquidBehaviorProfile[] _materials = Array.Empty<LiquidBehaviorProfile>();

        [SerializeField,Tooltip("远处液体只保存数量，回到玩家／角色／投射物／反应活动范围继续推进；Camera不决定Gameplay。")]
        private bool _sleepDistantLiquids=true;
        public bool SleepDistantLiquids=>_sleepDistantLiquids;
        public int MaximumChunks => _maximumChunks;
        public int MaximumActiveCells => _maximumActiveCells;
        public int MaximumPendingDeposits => _maximumPendingDeposits;
        public int MaximumCellsPerTick => _maximumCellsPerTick;
        public int CellCapacityGmu => _cellCapacityGmu;
        public LiquidVolumeSettings CreateVolumeSettings(float worldCellSize) =>
            new LiquidVolumeSettings(worldCellSize, _resolutionScale, _cellCapacityGmu,
                _unitVolumeMultiplier);
        public float FallGravity => _fallGravity;
        public float FallTerminalVelocity => _fallTerminalVelocity;
        public int MaximumStableRegions => _maximumStableRegions;
        public int MaximumStableOutlets => _maximumStableOutlets;
        public int MaximumStableRegionCells => _maximumStableRegionCells;
        public int MaximumStableSurfacePatches => _maximumStableSurfacePatches;
        public int MaximumImmediateExpansionCells => _maximumImmediateExpansionCells;
        public int MaximumImmediateExpansionSurfaceChunks =>
            _maximumImmediateExpansionSurfaceChunks;
        public bool EnableAutoStableDetection => _enableAutoStableDetection;
        public int MaximumAutoCandidateCells => _maximumAutoCandidateCells;
        public int AutoValidationCellsPerTick => _autoValidationCellsPerTick;
        public int AutoQuietTicks => _autoQuietTicks;
        public int MaterialCount => _materials == null ? 0 : _materials.Length;
        public LiquidBehaviorProfile GetMaterial(int index) => _materials[index];

        public void Validate()
        {
            // 单独验证数量映射，不能静默截断粗格容量使湖盆与注水量失配。
            CreateVolumeSettings(1f);
            if (!float.IsFinite(_fallGravity) || _fallGravity <= 0f
                || !float.IsFinite(_fallTerminalVelocity) || _fallTerminalVelocity <= 0f
                || _maximumChunks < 1 || _maximumActiveCells < 1 || _maximumPendingDeposits < 1
                || _maximumCellsPerTick < 1 || _cellCapacityGmu < 1 || _cellCapacityGmu > 255
                || _maximumStableRegions < 1 || _maximumStableOutlets < 1
                || _maximumStableRegionCells < 1 || _maximumStableSurfacePatches < 1
                || _maximumImmediateExpansionCells < 1
                || _maximumImmediateExpansionSurfaceChunks < 1
                || _maximumAutoCandidateCells < 1 || _maximumAutoCandidateCells > 65536
                || _autoValidationCellsPerTick < 1 || _autoQuietTicks < 1
                || _materials == null || _materials.Length == 0)
                throw new InvalidOperationException("Grid Liquid World 的容量或材料配置无效。");
            int[] seen = new int[byte.MaxValue + 1]; // 初始化期验证，热路径不分配。
            for (int i = 0; i < _materials.Length; i++)
            {
                LiquidBehaviorProfile item = _materials[i];
                if (item == null || item.Material == MaterialId.Empty || item.Material == MaterialId.Fire
                    || !float.IsFinite(item.RelativeDensity) || item.RelativeDensity <= 0f
                    || !float.IsFinite(item.GroundDrag) || item.GroundDrag < 0f
                    || !float.IsFinite(item.MaximumGroundSpeed) || item.MaximumGroundSpeed <= 0f
                    || item.MaximumDownFlowPerTick < 1 || item.MaximumDownFlowPerTick > 255
                    || item.MaximumSideFlowPerTick < 1 || item.MaximumSideFlowPerTick > 255
                    || item.SideFlowDeadbandGmu < 0 || item.SideFlowDeadbandGmu > 32
                    || !float.IsFinite(item.SideSpreadReserveFill)
                    || item.SideSpreadReserveFill < 0f || item.SideSpreadReserveFill > .9f
                    || seen[(byte)item.Material]++ != 0)
                    throw new InvalidOperationException("Grid Liquid World 存在空、重复或非液体材料。");
            }
        }

        private void OnValidate()
        {
            _resolutionScale = Mathf.Clamp(_resolutionScale, 1, 4);
            if (!float.IsFinite(_unitVolumeMultiplier)) _unitVolumeMultiplier = 1f;
            _unitVolumeMultiplier = Mathf.Clamp(_unitVolumeMultiplier,
                _cellCapacityGmu * _resolutionScale * _resolutionScale * _resolutionScale
                    / (float)ushort.MaxValue,
                _cellCapacityGmu * _resolutionScale * _resolutionScale * _resolutionScale);
            _maximumChunks = Mathf.Max(1, _maximumChunks);
            _maximumActiveCells = Mathf.Max(1, _maximumActiveCells);
            _maximumPendingDeposits = Mathf.Max(1, _maximumPendingDeposits);
            _maximumCellsPerTick = Mathf.Max(1, _maximumCellsPerTick);
            _cellCapacityGmu = Mathf.Clamp(_cellCapacityGmu, 1, 255);
            _maximumStableRegions = Mathf.Max(1, _maximumStableRegions);
            _maximumStableOutlets = Mathf.Max(1, _maximumStableOutlets);
            _maximumStableRegionCells = Mathf.Max(1, _maximumStableRegionCells);
            _maximumStableSurfacePatches = Mathf.Max(1, _maximumStableSurfacePatches);
            _maximumImmediateExpansionCells = Mathf.Max(1, _maximumImmediateExpansionCells);
            _maximumImmediateExpansionSurfaceChunks = Mathf.Max(1,
                _maximumImmediateExpansionSurfaceChunks);
            _maximumAutoCandidateCells = Mathf.Clamp(_maximumAutoCandidateCells, 1, 65536);
            _autoValidationCellsPerTick = Mathf.Max(1, _autoValidationCellsPerTick);
            _autoQuietTicks = Mathf.Max(1, _autoQuietTicks);
        }
    }
}
