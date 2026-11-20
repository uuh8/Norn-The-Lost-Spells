using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>固定材料体积使用基础格坐标和半开区间。一个均匀岩体只有一条定义，
    /// 体积扩大不增加材料 Tick 的工作。运行时编辑属于世界实例，不回写共享资产。</summary>
    [Serializable]
    public struct ElementTerrainRegion
    {
        public BoundsInt Cells;
        public MaterialId Material;
    }

    [CreateAssetMenu(menuName = "Game/Element Field/Cellular World", fileName = "CellularWorld")]
    public sealed class ElementCellWorldProfile : ScriptableObject
    {
        [SerializeField, Min(1)] private int _capacityPerCell = 255;
        [SerializeField, Min(1)] private int _maximumOccupiedCells = 32768;
        [SerializeField, Min(1)] private int _cellsPerTick = 1024;
        [Tooltip("单次球形物质投放可检查的包围盒格数，超过时在入队前返回失败。")]
        [SerializeField, Min(1)] private int _maximumDepositCandidates = 8192;
        [SerializeField] private ElementCellTransportTuning[] _transport = Array.Empty<ElementCellTransportTuning>();
        [SerializeField] private ElementTerrainRegion[] _terrain = Array.Empty<ElementTerrainRegion>();
        [Tooltip("初始由同种液体充满的基础格范围，运行时只在开口或局部交互附近展开。")]
        [SerializeField] private CellularRestingLiquidRegion[] _restingLiquids = Array.Empty<CellularRestingLiquidRegion>();
        [SerializeField, Min(1)] private int _restingCellsPerTick = 128;
        [SerializeField, Min(1)] private int _restingQueueCapacity = 4096;
        [Tooltip("固定地层的材质。留空时使用 Unity 基本几何的默认材质。")]
        [SerializeField] private Material _rockSurface, _soilSurface;
        [SerializeField, Range(0, 31)] private int _terrainLayer;

        internal int Capacity => _capacityPerCell;
        internal int MaximumCells => _maximumOccupiedCells;
        internal int WorkBudget => _cellsPerTick;
        internal int DepositBudget => _maximumDepositCandidates;
        internal int RestingBudget => _restingCellsPerTick;
        internal int RestingQueueCapacity => _restingQueueCapacity;
        internal CellularRestingLiquidRegion[] RestingRegions => _restingLiquids;
        internal ElementCellTransportTuning[] Transport => _transport;
        internal int TerrainLayer => _terrainLayer;
        internal int RegionCount => _terrain?.Length ?? 0;
        internal ElementTerrainRegion Region(int index) => _terrain[index];
        internal Material Surface(MaterialId id) => id == MaterialId.Soil ? _soilSurface : _rockSurface;
    }
}
