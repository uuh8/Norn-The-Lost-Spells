using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    [Serializable]
    public struct GasMaterialBehavior
    {
        public MaterialId Material;
        [Range(0f, 10f)] public float DiffusionPerSecond;
        [Range(-5f, 5f)] public float BuoyancyPerSecond;
    }
    [CreateAssetMenu(menuName = "Game/Element Field/Gas World", fileName = "GasWorld")]
    public sealed class GasWorldProfile : ScriptableObject
    {
        [SerializeField, Min(1)] private int _maximumChunks = 256;
        [SerializeField, Min(1)] private int _cellCapacityGmu = 4096;
        [SerializeField, Min(1)] private int _maximumActiveCells = 8192;
        [SerializeField, Min(1)] private int _maximumCellsPerTick = 1024;
        [SerializeField, Min(1)] private int _maximumPendingDeposits = 128;
        [SerializeField, Min(1)] private int _maximumTerrainSamplesPerTick = 128;
        [SerializeField] private GasMaterialBehavior[] _materials =
        {
            new GasMaterialBehavior { Material = MaterialId.Steam, DiffusionPerSecond = 2f, BuoyancyPerSecond = 1f },
            new GasMaterialBehavior { Material = MaterialId.ToxicVapor, DiffusionPerSecond = 1f, BuoyancyPerSecond = 0f }
        };
        public int MaximumChunks => _maximumChunks;
        public uint CellCapacityGmu => (uint)_cellCapacityGmu;
        public int MaximumActiveCells => _maximumActiveCells;
        public int MaximumCellsPerTick => _maximumCellsPerTick;
        public int MaximumPendingDeposits => _maximumPendingDeposits;
        public int MaximumTerrainSamplesPerTick => _maximumTerrainSamplesPerTick;
        public int MaterialCount => _materials != null ? _materials.Length : 0;
        public GasMaterialBehavior GetMaterial(int index) => _materials[index];
    }
}
