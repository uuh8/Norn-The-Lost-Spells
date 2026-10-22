using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    [Serializable]
    public struct FixedSolidMaterialBehavior
    {
        public MaterialId Material;
        public bool BlocksMovement;
        // Collider 按实际高度阻挡角色；薄残留只占部分液体容量，不应变成整格流体墙。
        public bool UsesPartialVolume;
    }
    [CreateAssetMenu(menuName = "Game/Element Field/Fixed Solid World", fileName = "FixedSolidWorld")]
    public sealed class FixedSolidWorldProfile : ScriptableObject
    {
        [SerializeField, Min(1)] private int _maximumChunks = 32;
        [SerializeField, Min(1)] private int _cellCapacityGmu = 1024;
        [SerializeField, Min(1)] private int _maximumPendingDeposits = 128;
        [SerializeField, Range(1, 64)] private int _maximumCellsPerTick = 32;
        [SerializeField, Range(1, 8)] private int _maximumColliderChunksPerTick = 8;
        [SerializeField, Range(1, 32)] private int _maximumNavigationBoxesPerChunk = 16;
        [SerializeField] private FixedSolidMaterialBehavior[] _materials =
        {
            new FixedSolidMaterialBehavior { Material = MaterialId.Ice, BlocksMovement = true },
            new FixedSolidMaterialBehavior { Material = MaterialId.DriedSlime, BlocksMovement = true, UsesPartialVolume = true }
        };
        public int MaximumChunks => _maximumChunks;
        public uint CellCapacityGmu => (uint)_cellCapacityGmu;
        public int MaximumPendingDeposits => _maximumPendingDeposits;
        public int MaximumCellsPerTick => _maximumCellsPerTick;
        public int MaximumColliderChunksPerTick => _maximumColliderChunksPerTick;
        public int MaximumNavigationBoxesPerChunk => _maximumNavigationBoxesPerChunk;
        public int MaterialCount => _materials != null ? _materials.Length : 0;
        public FixedSolidMaterialBehavior GetMaterial(int index) => _materials[index];
    }
}
