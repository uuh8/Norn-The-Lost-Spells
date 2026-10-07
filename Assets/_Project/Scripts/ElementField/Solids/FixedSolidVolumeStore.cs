using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>固定固体在同格保留唯一身份；非阻挡残留也保留真实量和占据高度。</summary>
    public sealed class FixedSolidVolumeStore : SparseMaterialVolumeStore
    {
        public FixedSolidVolumeStore(int chunkSize, int maximumChunks, uint capacity,
            MaterialId[] materials) : base(chunkSize, maximumChunks, capacity, materials, true) { }
        public float HeightAt(Vector3Int cell) => OccupancyAt(cell) / (float)CellCapacity;
    }
}
