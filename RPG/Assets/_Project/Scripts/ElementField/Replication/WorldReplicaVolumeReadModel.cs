using System;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>固体/气体共享的只读数量视图；不包含扩散、浮力、重力、出口或Pending消费。</summary>
    public class WorldReplicaVolumeReadModel : IGasVolumeReadOnly
    {
        protected readonly WorldReplicaStore Store;
        protected readonly WorldGridGeometry Geometry;
        protected readonly MaterialId[] Materials;
        private readonly ElementChunkKey[] _keys;
        private readonly bool[] _blocks = new bool[256];
        private readonly uint _capacity;
        public WorldReplicaVolumeReadModel(WorldReplicaStore store, WorldGridGeometry geometry, uint capacity,
            MaterialId[] materials, bool[] blocking = null)
        {
            if (store == null || capacity == 0 || materials == null || materials.Length == 0) throw new ArgumentException();
            Store = store; Geometry = geometry; _capacity = capacity; Materials = (MaterialId[])materials.Clone();
            _keys = new ElementChunkKey[store.MaximumChunks];
            if (blocking != null) Array.Copy(blocking, _blocks, Math.Min(blocking.Length, _blocks.Length));
        }
        public int ResidentCellCount => Store.ResidentCellCount(Geometry.Domain);
        public uint Version => Store.DomainVersion(Geometry.Domain);
        public uint CellCapacity => _capacity;
        public ulong TotalQuantity => Store.TotalQuantity(Geometry.Domain);
        public int ChunkSize => Geometry.ChunkSize;
        public int MaximumChunks => Store.MaximumChunks;
        public int ResidentChunkCount => Store.CopyChunkKeys(Geometry.Domain, _keys);
        public ElementChunkKey GetChunkKey(int index) { Store.CopyChunkKeys(Geometry.Domain, _keys); return _keys[index]; }
        public uint GetChunkVersion(ElementChunkKey key) => Store.GetChunkVersion(Geometry.Domain, key);
        public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity)
            => Store.TryGetQuantity(Geometry.Domain, cell, (byte)material, out quantity);
        public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
        { bool found = TryGetQuantity(cell, material, out uint quantity); amount = (byte)Math.Min(255, (double)quantity * 255 / CellCapacity); return found; }
        public bool TryGetCell(int index, out MaterialVolumeCell cell)
        {
            cell = default;
            if (!Store.TryGetCell(Geometry.Domain, index, out var sample)) return false;
            cell = new MaterialVolumeCell(sample.Cell, (MaterialId)sample.MaterialId, sample.Quantity); return true;
        }
        public bool HasBlockingVolume(Vector3 position)
        {
            var cell = Vector3Int.FloorToInt((position - Geometry.Origin) / Geometry.CellSize);
            float height = (position.y - Geometry.Origin.y) / Geometry.CellSize - cell.y;
            for (int i = 0; i < Materials.Length; i++)
                if (_blocks[(byte)Materials[i]] && TryGetQuantity(cell, Materials[i], out uint quantity)
                    && height < quantity / (float)CellCapacity) return true;
            return false;
        }
        public void CopyMaterialBox(ElementChunkKey key, Vector3Int localMinimum, Vector3Int size, MaterialId material,
            uint[] destination, int destinationOffset, int rowStride, int sliceStride)
        {
            for (int z = 0; z < size.z; z++) for (int y = 0; y < size.y; y++) for (int x = 0; x < size.x; x++)
            {
                var cell = new Vector3Int(key.X * ChunkSize, key.Y * ChunkSize, key.Z * ChunkSize) + localMinimum + new Vector3Int(x, y, z);
                TryGetQuantity(cell, material, out uint quantity);
                destination[destinationOffset + z * sliceStride + y * rowStride + x] = quantity;
            }
        }
    }
}
