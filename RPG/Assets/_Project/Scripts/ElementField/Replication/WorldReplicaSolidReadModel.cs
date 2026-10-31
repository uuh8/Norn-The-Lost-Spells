using System;
using Game.Core;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField
{
    /// <summary>
    /// 只读固体几何缓存：使用既有FixedSolidSurfaceBuilder生成相同外表面与碰撞。
    /// 每帧最多更新一个Chunk；只准备目标Chunk及一格Halo的数量，不复制整世界或调用重力/反应。
    /// 几何滞后于已安装数量的上限由待脏Chunk数决定，装配/性能验收必须单独记录该延迟。
    /// </summary>
    public sealed class WorldReplicaSolidReadModel : WorldReplicaVolumeReadModel, IFixedSolidSurfaceReadOnly, IDisposable
    {
        private readonly FixedSolidVolumeStore _scratch;
        private readonly FixedSolidSurfaceBuilder _builder;
        private readonly Mesh[] _visual, _collision;
        private readonly Transform[] _roots;
        private readonly MeshCollider[] _colliders;
        private readonly ElementChunkKey[] _keys, _boundKeys;
        private readonly uint[] _versions;
        private readonly bool[] _blocking;
        private readonly Vector3Int[] _scratchCells;
        private readonly MaterialId[] _scratchMaterials;
        private readonly uint[] _scratchAmounts;
        private int _scratchCount, _cursor;
        public int SurfaceCapacity => _visual.Length;
        public uint GeometryVersion { get; private set; }
        public WorldReplicaSolidReadModel(WorldReplicaStore store, WorldGridGeometry geometry, FixedSolidWorldProfile profile, Transform parent)
            : base(store, geometry, profile.CellCapacityGmu, GetMaterials(profile), GetBlocking(profile))
        {
            int slots = Math.Min(store.MaximumChunks, profile.MaximumChunks);
            _blocking = GetBlocking(profile);
            // 一格Halo最多涉及27个原生Chunk，初始化时一次预留；绝不初始化FixedSolidWorldRuntime。
            _scratch = new FixedSolidVolumeStore(geometry.ChunkSize, 27, profile.CellCapacityGmu, Materials);
            _builder = new FixedSolidSurfaceBuilder(_scratch, Blocks, profile.MaximumNavigationBoxesPerChunk);
            int cells = checked((geometry.ChunkSize + 2) * (geometry.ChunkSize + 2) * (geometry.ChunkSize + 2) * Materials.Length);
            _scratchCells = new Vector3Int[cells]; _scratchMaterials = new MaterialId[cells]; _scratchAmounts = new uint[cells];
            _visual = new Mesh[slots]; _collision = new Mesh[slots]; _roots = new Transform[slots]; _colliders = new MeshCollider[slots];
            _keys = new ElementChunkKey[store.MaximumChunks]; _boundKeys = new ElementChunkKey[slots]; _versions = new uint[slots];
            for (int i = 0; i < slots; i++)
            {
                var root = new GameObject("Replica Material Solid " + i); root.SetActive(false); root.layer = 8;
                root.transform.SetParent(parent, false); root.AddComponent<GeneratedMaterialSolidCollider>();
                _roots[i] = root.transform; _colliders[i] = root.AddComponent<MeshCollider>();
                _visual[i] = new Mesh { name = "Replica Solid Surface", indexFormat = IndexFormat.UInt32 };
                _collision[i] = new Mesh { name = "Replica Solid Collision", indexFormat = IndexFormat.UInt32 };
            }
        }
        private static MaterialId[] GetMaterials(FixedSolidWorldProfile profile)
        { var values = new MaterialId[profile.MaterialCount]; for (int i = 0; i < values.Length; i++) values[i] = profile.GetMaterial(i).Material; return values; }
        private static bool[] GetBlocking(FixedSolidWorldProfile profile)
        { var values = new bool[256]; for (int i = 0; i < profile.MaterialCount; i++) { var material = profile.GetMaterial(i); values[(byte)material.Material] = material.BlocksMovement; } return values; }
        private bool Blocks(MaterialId material) => _blocking[(byte)material];
        public void PresentOneChunk()
        {
            int count = Store.CopyChunkKeys(WorldMaterialDomain.Solid, _keys);
            // 已离开Interest/已删除的碰撞立即停用，不等轮转重建；仅扫描配置的几何槽上限。
            for (int i = 0; i < _roots.Length; i++)
                if (i >= count) { _roots[i].gameObject.SetActive(false); _versions[i] = 0; }
                else if (_versions[i] != 0 && !_boundKeys[i].Equals(_keys[i])) { _roots[i].gameObject.SetActive(false); _versions[i] = 0; }
            int limit = Math.Min(count, _roots.Length);
            for (int attempts = 0; attempts < limit; attempts++)
            {
                int slot = _cursor % limit; _cursor = (slot + 1) % limit;
                var key = _keys[slot]; uint version = NeighborhoodVersion(key);
                if (_versions[slot] == version) continue;
                Build(slot, key); _versions[slot] = version; GeometryVersion++; return;
            }
        }
        private uint NeighborhoodVersion(ElementChunkKey key)
        {
            uint version = 2166136261;
            // 邻页数量改变也会暴露/遮挡边界面；只看自己的Revision会留下旧侧面。
            unchecked
            {
                for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
                    version = (version ^ Store.GetChunkVersion(WorldMaterialDomain.Solid, new ElementChunkKey(key.X + x, key.Y + y, key.Z + z))) * 16777619;
            }
            return version == 0 ? 1u : version;
        }
        private void Build(int slot, ElementChunkKey key)
        {
            for (int i = 0; i < _scratchCount; i++) _scratch.TryRemove(_scratchCells[i], _scratchMaterials[i], _scratchAmounts[i]);
            _scratch.ReclaimEmptyChunks(); _scratchCount = 0;
            int k = ChunkSize;
            for (int z = -1; z <= k; z++) for (int y = -1; y <= k; y++) for (int x = -1; x <= k; x++)
            {
                var cell = new Vector3Int(key.X * k + x, key.Y * k + y, key.Z * k + z);
                for (int m = 0; m < Materials.Length; m++) if (TryGetQuantity(cell, Materials[m], out uint quantity))
                {
                    if (!_scratch.TryAdd(cell, Materials[m], quantity)) throw new InvalidOperationException("Replica固体超过可信Profile容量。");
                    _scratchCells[_scratchCount] = cell; _scratchMaterials[_scratchCount] = Materials[m]; _scratchAmounts[_scratchCount++] = quantity;
                }
            }
            _builder.Build(key, Geometry.CellSize, false); Fill(_visual[slot]);
            _builder.Build(key, Geometry.CellSize, true); Fill(_collision[slot]);
            _colliders[slot].sharedMesh = null; _colliders[slot].sharedMesh = _collision[slot].vertexCount > 0 ? _collision[slot] : null;
            _roots[slot].position = Geometry.Origin + new Vector3(key.X, key.Y, key.Z) * (ChunkSize * Geometry.CellSize);
            _boundKeys[slot] = key; _roots[slot].gameObject.SetActive(_visual[slot].vertexCount > 0);
        }
        private void Fill(Mesh mesh)
        {
            mesh.Clear(); mesh.SetVertices(_builder.Vertices, 0, _builder.VertexCount);
            mesh.SetNormals(_builder.Normals, 0, _builder.VertexCount); mesh.SetColors(_builder.MaterialIds, 0, _builder.VertexCount);
            mesh.SetIndices(_builder.Indices, 0, _builder.IndexCount, MeshTopology.Triangles, 0, true);
        }
        public bool TryGetSurface(int index, out Mesh mesh, out Vector3 position)
        {
            mesh = _visual[index]; position = _roots[index].position;
            return _versions[index] != 0 && _roots[index].gameObject.activeSelf && mesh.vertexCount > 0;
        }
        public void Dispose()
        {
            for (int i = 0; i < _roots.Length; i++) { DestroyOwned(_visual[i]); DestroyOwned(_collision[i]); if (_roots[i] != null) DestroyOwned(_roots[i].gameObject); }
        }
        private static void DestroyOwned(UnityEngine.Object value)
        { if (value == null) return; if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
    }
}
