using System;
using System.Collections.Generic;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 复制数量到原液体表现的适配器。网络快照已把Stable/Dynamic规范化，因此全部数量作为只读占据，
    /// 不重新识别湖盆或执行出口规则。Renderer仍按原Density分层与同一Shader绘制。
    /// 当前协议不含速度，Previous=Current：允许视觉逐帧塑形，不伪造客户端模拟速度。
    /// </summary>
    public sealed class WorldReplicaLiquidReadModel : ILiquidPresentationReadOnly
    {
        private readonly WorldReplicaStore _store;
        private readonly WorldGridGeometry _geometry;
        private readonly LiquidLayerComposer _layers;
        private readonly uint[] _quantities;
        private readonly ElementFieldSolidBaker _baker;
        private readonly LiquidVolumeSettings _volume;
        private readonly WorldReplicaVolumeReadModel _solids;
        private readonly ElementChunkKey[] _keyScratch;
        private readonly int _transportCapacity;
        private readonly Dictionary<Vector3Int, bool> _staticSolids;
        private readonly Dictionary<Vector3Int, LiquidTerrainSupport> _supports;
        private readonly Vector3Int[] _terrainKeys, _supportKeys;
        private int _terrainNext, _supportNext, _terrainCount, _supportCount;
        private uint _solidVersion;
        public WorldReplicaLiquidReadModel(WorldReplicaStore store, WorldGridGeometry geometry,
            LiquidWorldProfile profile, float worldCellSize, ElementFieldSolidBaker baker,
            WorldReplicaVolumeReadModel solids = null)
        {
            if (store == null || profile == null || geometry.Domain != WorldMaterialDomain.Liquid) throw new ArgumentException();
            profile.Validate(); _volume = profile.CreateVolumeSettings(worldCellSize);
            if (!_volume.CellSize.Equals(geometry.CellSize)) throw new ArgumentException("Replica液体网格与可信Profile不一致。");
            _store = store; _geometry = geometry; _baker = baker; _solids = solids;
            var materials = new MaterialId[profile.MaterialCount]; var densities = new float[profile.MaterialCount];
            for (int i = 0; i < materials.Length; i++) { materials[i] = profile.GetMaterial(i).Material; densities[i] = profile.GetMaterial(i).RelativeDensity; }
            _layers = new LiquidLayerComposer(materials, densities, _volume.CapacityGmu); _quantities = new uint[materials.Length];
            _keyScratch = new ElementChunkKey[store.MaximumChunks]; _transportCapacity = Math.Max(1, Math.Min(store.MaximumCells, profile.MaximumActiveCells));
            int terrainCapacity = Math.Max(128, Math.Min(store.MaximumCells, 8192));
            _staticSolids = new Dictionary<Vector3Int, bool>(terrainCapacity); _supports = new Dictionary<Vector3Int, LiquidTerrainSupport>(terrainCapacity);
            _terrainKeys = new Vector3Int[terrainCapacity]; _supportKeys = new Vector3Int[terrainCapacity];
        }
        public bool IsFluidInitialized => true;
        public Vector3 Origin => _geometry.Origin;
        public float CellSize => _geometry.CellSize;
        public int ChunkSize => _geometry.ChunkSize;
        public int CellCapacityGmu => _volume.CapacityGmu;
        public int MaximumSurfaceCandidateKeys => _store.MaximumChunks;
        public int MaximumStableRegions => 1;
        public int MaximumStableSurfacePatches => 1;
        public int MaximumStableRegionCells => _store.MaximumCells;
        public int MaximumActiveTransportCells => _transportCapacity;
        public int ActiveCellCount => 0;
        public uint SurfaceVersion => unchecked(_store.DomainVersion(WorldMaterialDomain.Liquid)
            + _store.DomainVersion(WorldMaterialDomain.Solid) + (_solids is WorldReplicaSolidReadModel solid ? solid.GeometryVersion : 0u));
        public uint DynamicSurfaceVersion => SurfaceVersion;
        public uint TransportEpoch => SurfaceVersion;
        public float TransportInterval => .1f;
        public ulong TotalQuantity => _store.TotalQuantity(WorldMaterialDomain.Liquid);
        public ulong PendingQuantity => _store.PendingQuantity(WorldMaterialDomain.Liquid);
        public int MaterialCount => _layers.MaterialCount;
        public int DensityGroupCount => _layers.DensityGroupCount;
        public MaterialId GetLiquidMaterial(int slot) => _layers.GetMaterial(slot);
        public int GetDensityGroup(MaterialId material) => _layers.GetDensityGroup(material);
        public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity)
            => _store.TryGetQuantity(WorldMaterialDomain.Liquid, cell, (byte)material, out quantity);
        public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
        { bool found = TryGetQuantity(cell, material, out uint quantity); amount = (byte)Math.Min(255u, (uint)((double)quantity * 255 / CellCapacityGmu)); return found; }
        private void Compose(Vector3Int cell)
        { for (int i = 0; i < MaterialCount; i++) TryGetQuantity(cell, GetLiquidMaterial(i), out _quantities[i]); _layers.Compose(_quantities); }
        public bool TryGetLayer(Vector3Int cell, int group, out LiquidLayerSample layer)
        { Compose(cell); layer = _layers.GetLayer(group); return layer.ThicknessFill > 0; }
        public float GetGroupMaterialShare(Vector3Int cell, MaterialId material)
        { Compose(cell); return _layers.GetShare(material); }
        public float GetTotalFill(Vector3Int cell)
        { ulong sum = 0; for (int i = 0; i < MaterialCount; i++) { TryGetQuantity(cell, GetLiquidMaterial(i), out uint quantity); sum += quantity; } return sum / (float)CellCapacityGmu; }
        public bool TryGetSurfaceFill(Vector3Int cell, MaterialId material, out float fill)
        { fill = GetTotalFill(cell); return IsSurfaceMaterialDominant(cell, material); }
        public bool IsSurfaceMaterialDominant(Vector3Int cell, MaterialId material)
        {
            if (!TryGetQuantity(cell, material, out _)) return false;
            int group = GetDensityGroup(material);
            for (int i = 0; i < MaterialCount; i++) if (GetDensityGroup(GetLiquidMaterial(i)) > group && TryGetQuantity(cell, GetLiquidMaterial(i), out _)) return false;
            return true;
        }
        public int CopyLiquidChunkKeys(ElementChunkKey[] destination) => _store.CopyChunkKeys(WorldMaterialDomain.Liquid, destination);
        public int CopySurfaceChunkKeys(MaterialId material, ElementChunkKey[] destination)
        {
            int count = CopyLiquidChunkKeys(_keyScratch), written = 0;
            for (int i = 0; i < count && written < destination.Length; i++) if (HasDynamicMaterialInChunk(_keyScratch[i], material)) destination[written++] = _keyScratch[i];
            return written;
        }
        public bool HasDynamicMaterialInChunk(ElementChunkKey key, MaterialId material)
            => _store.HasMaterialInChunk(WorldMaterialDomain.Liquid, key, (byte)material);
        public uint GetDynamicSurfaceVersion(MaterialId material) => SurfaceVersion;
        public uint GetDynamicCandidateVersion(MaterialId material) => SurfaceVersion;
        public uint GetSurfaceChunkVersion(ElementChunkKey key, MaterialId material) => _store.GetChunkVersion(WorldMaterialDomain.Liquid, key);
        public bool TryGetDynamicQuantity(Vector3Int cell, MaterialId material, out uint quantity) => TryGetQuantity(cell, material, out quantity);
        public bool TryGetStableSurfaceHeight(Vector3Int cell, MaterialId material, out float heightCells) { heightCells = 0; return false; }
        public uint GetStableSurfaceVersion(MaterialId material) => 0;
        public uint GetStableNeighborhoodVersion(ElementChunkKey key, MaterialId material) => 0;
        public int CopyStableSurfacePatches(MaterialId material, StableLiquidSurfacePatch[] destination) => 0;
        public int CopyAllStableSurfacePatches(StableLiquidSurfacePatch[] destination) => 0;
        public bool IsStableSurfaceHandoffPending(MaterialId material, ElementChunkKey key) => false;
        public bool IsActiveStableSurfaceOverlapping(MaterialId material, ElementChunkKey key) => false;
        public bool IsSolidForSurface(Vector3Int cell)
        {
            // 静态地形只读查询不创建权威Chunk；固体材料取已安装的数量，不调用原FixedSolid Owner。
            if (!_staticSolids.TryGetValue(cell, out bool solid))
            {
                solid = _baker != null && _baker.SampleEnvironmentSolidWorldCell(cell, Origin, CellSize);
                if (_terrainCount == _terrainKeys.Length) _staticSolids.Remove(_terrainKeys[_terrainNext]); else _terrainCount++;
                _terrainKeys[_terrainNext] = cell; _terrainNext = (_terrainNext + 1) % _terrainKeys.Length; _staticSolids.Add(cell, solid);
            }
            if (solid) return true;
            return _solids != null && _solids.HasBlockingVolume(Origin + ((Vector3)cell + Vector3.one * .5f) * CellSize);
        }
        public void CopyPresentationChunkQuantities(ElementChunkKey key, ushort[] destination)
        {
            int required = checked(ChunkSize * ChunkSize * ChunkSize * MaterialCount);
            if (destination == null || destination.Length < required) throw new ArgumentException(nameof(destination));
            for (int z = 0; z < ChunkSize; z++) for (int y = 0; y < ChunkSize; y++) for (int x = 0; x < ChunkSize; x++)
            {
                var cell = new Vector3Int(key.X * ChunkSize + x, key.Y * ChunkSize + y, key.Z * ChunkSize + z);
                int row = ((z * ChunkSize + y) * ChunkSize + x) * MaterialCount;
                for (int m = 0; m < MaterialCount; m++)
                {
                    TryGetQuantity(cell, GetLiquidMaterial(m), out uint quantity);
                    // 此协议保持uint。可信Profile容量不大于ushort；超限必须在安装前拒绝，不能显示时截断。
                    destination[row + m] = checked((ushort)quantity);
                }
            }
        }
        public bool CopyPresentationRow(Vector3Int cell, ushort[] quantities, int offset,
            LiquidCellPresentationSample[] current, LiquidCellPresentationSample[] previous)
        {
            bool wet = false;
            for (int m = 0; m < MaterialCount; m++)
            { uint quantity = quantities[offset + m]; wet |= quantity > 0; current[m] = previous[m] = new LiquidCellPresentationSample(quantity, Vector3.zero, Vector3.zero); }
            return wet;
        }
        public bool TryGetPresentationSample(Vector3Int cell, MaterialId material, bool previous, out LiquidCellPresentationSample sample)
        { bool found = TryGetQuantity(cell, material, out uint quantity); sample = new LiquidCellPresentationSample(quantity, Vector3.zero, Vector3.zero); return found; }
        public int CopyTransportChangedCells(Vector3Int[] destination) => 0;
        public float GetGameplayBaseHeight(Vector3Int cell) => GetPresentationBaseHeight(cell);
        public float GetPresentationBaseHeight(Vector3Int cell)
        {
            Vector3Int floor = cell;
            for (int i = 0; i < 8; i++, floor += Vector3Int.down)
            {
                if (TryGetTerrainSupportForPresentation(floor, out var support)) return support.Height + (cell.y - floor.y) * CellSize;
                if (GetTotalFill(floor + Vector3Int.down) < .999f) break;
            }
            return Origin.y + cell.y * CellSize;
        }
        public bool TryGetTerrainSupport(Vector3Int cell, out LiquidTerrainSupport support) => TryGetTerrainSupportForPresentation(cell, out support);
        public bool TryGetTerrainSupportForPresentation(Vector3Int cell, out LiquidTerrainSupport support)
        {
            support = default;
            uint version = unchecked(_store.DomainVersion(WorldMaterialDomain.Solid) + (_solids is WorldReplicaSolidReadModel solid ? solid.GeometryVersion : 0u));
            if (version != _solidVersion) { _solidVersion = version; _supports.Clear(); _supportCount = _supportNext = 0; }
            if (_baker == null || !IsSolidForSurface(cell + Vector3Int.down) || IsSolidForSurface(cell)) return false;
            if (_supports.TryGetValue(cell, out support)) return support.Valid;
            Vector3 ray = Origin + new Vector3(cell.x + .5f, cell.y + .25f, cell.z + .5f) * CellSize;
            bool valid = _baker.TrySampleLiquidSupport(ray, CellSize * 4, out float height, out Vector3 normal);
            support = new LiquidTerrainSupport(height, normal, valid);
            if (_supportCount == _supportKeys.Length) _supports.Remove(_supportKeys[_supportNext]); else _supportCount++;
            _supportKeys[_supportNext] = cell; _supportNext = (_supportNext + 1) % _supportKeys.Length; _supports.Add(cell, support);
            return valid;
        }
        public bool TryGetRecentPresentationDeposit(out Vector3 position) { position = default; return false; }
    }
}
