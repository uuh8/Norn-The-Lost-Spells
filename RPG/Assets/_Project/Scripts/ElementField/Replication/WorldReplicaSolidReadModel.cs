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
        private readonly bool[] _bound;
        private readonly bool[] _blocking;
        private readonly bool[] _partialVolume;
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
            _partialVolume = new bool[256];
            for (int i = 0; i < profile.MaterialCount; i++)
            {
                var material = profile.GetMaterial(i);
                // 与Authority现有占据适配器保持同一支持边界，不能默许新partial材料却只在客户端抬高液面。
                if (material.UsesPartialVolume && material.Material != MaterialId.DriedSlime)
                    throw new ArgumentException("UsesPartialVolume 当前只支持固化粘液的体积占据。");
                _partialVolume[(byte)material.Material] = material.UsesPartialVolume;
            }
            // 一格Halo最多涉及27个原生Chunk，初始化时一次预留；绝不初始化FixedSolidWorldRuntime。
            _scratch = new FixedSolidVolumeStore(geometry.ChunkSize, 27, profile.CellCapacityGmu, Materials);
            _builder = new FixedSolidSurfaceBuilder(_scratch, Blocks, profile.MaximumNavigationBoxesPerChunk);
            int cells = checked((geometry.ChunkSize + 2) * (geometry.ChunkSize + 2) * (geometry.ChunkSize + 2) * Materials.Length);
            _scratchCells = new Vector3Int[cells]; _scratchMaterials = new MaterialId[cells]; _scratchAmounts = new uint[cells];
            _visual = new Mesh[slots]; _collision = new Mesh[slots]; _roots = new Transform[slots]; _colliders = new MeshCollider[slots];
            _keys = new ElementChunkKey[store.MaximumChunks]; _boundKeys = new ElementChunkKey[slots]; _versions = new uint[slots];
            _bound = new bool[slots];
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

        // 通用HasBlockingVolume仍按真实高度采样；液体需要独立的整格墙/部分容量语义，不能借点查询放宽碰撞。
        private bool BlocksWholeCell(Vector3Int cell)
        {
            for (int i = 0; i < Materials.Length; i++)
            {
                MaterialId material = Materials[i];
                if (_blocking[(byte)material] && !_partialVolume[(byte)material]
                    && TryGetQuantity(cell, material, out uint quantity) && quantity > 0) return true;
            }
            return false;
        }

        internal bool BlocksLiquidCell(Vector3 minimum, float size)
        {
            CoveredCells(minimum, size, out var min, out var max);
            // 正式Profile的粗细比最多4，每个粗格至多64个细格；包含负坐标，端点采用半开区间。
            for (int z = min.z; z <= max.z; z++) for (int y = min.y; y <= max.y; y++) for (int x = min.x; x <= max.x; x++)
                if (BlocksWholeCell(new Vector3Int(x, y, z))) return true;
            return false;
        }

        internal uint ReadLiquidPartialOccupancy(Vector3 minimum, float size, uint capacity)
        {
            CoveredCells(minimum, size, out var min, out var max);
            ulong total = 0;
            for (int z = min.z; z <= max.z; z++) for (int y = min.y; y <= max.y; y++) for (int x = min.x; x <= max.x; x++)
                for (int m = 0; m < Materials.Length; m++)
                    if (_partialVolume[(byte)Materials[m]] && TryGetQuantity(new Vector3Int(x, y, z), Materials[m], out uint quantity))
                        total += quantity;
            // 与Authority一样只投影GMU占据，不复制、消费或舍弃Store的真实材料数量。
            return (uint)Math.Min((ulong)capacity, total);
        }

        internal bool IsWholeCellSupport(Vector3 hitPoint)
            => BlocksWholeCell(Vector3Int.FloorToInt((hitPoint - Vector3.up * (Geometry.CellSize * .001f)
                - Geometry.Origin) / Geometry.CellSize));

        private void CoveredCells(Vector3 minimum, float size, out Vector3Int min, out Vector3Int max)
        {
            min = Vector3Int.FloorToInt((minimum - Geometry.Origin) / Geometry.CellSize);
            max = Vector3Int.CeilToInt((minimum + Vector3.one * size - Geometry.Origin) / Geometry.CellSize) - Vector3Int.one;
        }
        public void PresentOneChunk()
        {
            int count = Store.CopyChunkKeys(WorldMaterialDomain.Solid, _keys);
            // CopyChunkKeys是紧凑列表，退出Interest会移动后续索引；几何槽必须绑定Key而非列表位置。
            // 只退休真正离开的Key，保留其他Chunk已有的Collider/Mesh，避免一次退出制造多帧碰撞空洞。
            for (int i = 0; i < _roots.Length; i++)
            {
                if (!_bound[i] || Store.GetChunkVersion(WorldMaterialDomain.Solid, _boundKeys[i]) != 0) continue;
                _roots[i].gameObject.SetActive(false); _versions[i] = 0; _bound[i] = false;
                GeometryVersion++;
            }
            // 固定容量内线性查找，不创建Dictionary或临时集合；新Key只能占用空槽，不能挤走仍有效几何。
            for (int i = 0; i < count; i++)
            {
                int free = -1; bool found = false;
                for (int slot = 0; slot < _roots.Length; slot++)
                {
                    if (_bound[slot]) { if (_boundKeys[slot].Equals(_keys[i])) { found = true; break; } }
                    else if (free < 0) free = slot;
                }
                if (!found && free >= 0) { _bound[free] = true; _boundKeys[free] = _keys[i]; }
            }
            for (int attempts = 0; attempts < _roots.Length; attempts++)
            {
                int slot = _cursor; _cursor = (slot + 1) % _roots.Length;
                if (!_bound[slot]) continue;
                var key = _boundKeys[slot]; uint version = NeighborhoodVersion(key);
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
            // 复用Mesh前先解除旧Collider引用；Transform/重新激活也须先完成，最后才发布本次collision Mesh。
            // 否则已停用槽重入时，visual可见但Unity的Collider生命周期会留下空sharedMesh。
            _colliders[slot].sharedMesh = null;
            _roots[slot].position = Geometry.Origin + new Vector3(key.X, key.Y, key.Z) * (ChunkSize * Geometry.CellSize);
            _builder.Build(key, Geometry.CellSize, false); Fill(_visual[slot]);
            _builder.Build(key, Geometry.CellSize, true); Fill(_collision[slot]);
            _boundKeys[slot] = key; _roots[slot].gameObject.SetActive(_visual[slot].vertexCount > 0);
            _colliders[slot].sharedMesh = _collision[slot].vertexCount > 0 ? _collision[slot] : null;
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
