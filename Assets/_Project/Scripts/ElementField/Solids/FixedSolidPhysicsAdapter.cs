using System;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace Game.ElementField
{
    /// <summary>池化 Chunk Collider 与导航段；数量提交和几何发布在一个固定 Tick 内完成。</summary>
    public sealed class FixedSolidPhysicsAdapter
    {
        private static readonly ProfilerMarker PublishMarker = new ProfilerMarker("FixedSolid.PublishGeometry");
        private readonly Mesh[] _visual, _physics;
        private readonly MeshCollider[] _colliders;
        private readonly Transform[] _roots;
        private readonly NavMeshObstacle[,] _obstacles;
        private readonly ElementChunkKey[] _keys;
        private readonly bool[] _bound;
        private readonly int _boxes;
        private readonly FixedSolidSurfaceBuilder _builder;
        private readonly Vector3 _origin;
        private readonly float _cellSize;
        private readonly int _chunkSize;
        public int Capacity => _visual.Length;
        public uint RebuildCount { get; private set; }
        public FixedSolidPhysicsAdapter(Transform parent, FixedSolidVolumeStore store,
            Func<Game.Materials.MaterialId, bool> blocks, int boxes, Vector3 origin, float cellSize)
        {
            _origin = origin; _cellSize = cellSize; _chunkSize = store.ChunkSize; _boxes = boxes;
            int capacity = store.MaximumChunks;
            _visual = new Mesh[capacity]; _physics = new Mesh[capacity]; _colliders = new MeshCollider[capacity];
            _roots = new Transform[capacity]; _keys = new ElementChunkKey[capacity]; _bound = new bool[capacity];
            _obstacles = new NavMeshObstacle[capacity, boxes];
            _builder = new FixedSolidSurfaceBuilder(store, blocks, boxes);
            for (int i = 0; i < capacity; i++)
            {
                var root = new GameObject("Material Solid Chunk " + i); root.SetActive(false);
                root.layer = 8; root.transform.SetParent(parent, false); _roots[i] = root.transform;
                root.AddComponent<GeneratedMaterialSolidCollider>();
                _colliders[i] = root.AddComponent<MeshCollider>();
                _visual[i] = new Mesh { name = "Fixed Solid Surface " + i, indexFormat = IndexFormat.UInt32 };
                _physics[i] = new Mesh { name = "Fixed Solid Collision " + i, indexFormat = IndexFormat.UInt32 };
                for (int box = 0; box < boxes; box++)
                {
                    var obj = new GameObject("Navigation Segment " + box); obj.transform.SetParent(root.transform, false);
                    var obstacle = obj.AddComponent<NavMeshObstacle>(); obstacle.shape = NavMeshObstacleShape.Box;
                    obstacle.carving = true; obstacle.carveOnlyStationary = false; obstacle.enabled = false;
                    _obstacles[i, box] = obstacle;
                }
            }
        }
        public bool CanPrepare(ElementChunkKey key) => _builder.BuildNavigationBoxes(key, _cellSize);
        public void Publish(ElementChunkKey key)
        {
            using var publicationScope = PublishMarker.Auto();
            int slot = Find(key); if (slot < 0) throw new InvalidOperationException("固体几何池不足。");
            _keys[slot] = key; _bound[slot] = true;
            _roots[slot].position = _origin + new Vector3(key.X, key.Y, key.Z) * (_chunkSize * _cellSize);
            _builder.Build(key, _cellSize, false); Fill(_visual[slot]);
            _builder.Build(key, _cellSize, true); Fill(_physics[slot]);
            _colliders[slot].sharedMesh = null;
            _colliders[slot].sharedMesh = _physics[slot].vertexCount > 0 ? _physics[slot] : null;
            _builder.BuildNavigationBoxes(key, _cellSize);
            for (int i = 0; i < _boxes; i++)
            {
                bool enabled = i < _builder.NavigationBoxCount;
                if (enabled) { _obstacles[slot, i].center = _builder.NavigationBoxes[i].center; _obstacles[slot, i].size = _builder.NavigationBoxes[i].size; }
                _obstacles[slot, i].enabled = enabled;
            }
            _roots[slot].gameObject.SetActive(_visual[slot].vertexCount > 0);
            RebuildCount++;
        }
        public bool TryGetSurface(int index, out Mesh mesh, out Vector3 position)
        {
            mesh = _visual[index]; position = _roots[index].position;
            return _bound[index] && mesh.vertexCount > 0;
        }
        public void ReleaseUnused(SparseMaterialVolumeStore store)
        {
            for (int i = 0; i < Capacity; i++)
                if (_bound[i] && store.GetChunkVersion(_keys[i]) == 0)
                { _bound[i] = false; _roots[i].gameObject.SetActive(false); }
        }
        public void Dispose()
        {
            for (int i = 0; i < Capacity; i++)
            { DestroyOwned(_visual[i]); DestroyOwned(_physics[i]); DestroyOwned(_roots[i].gameObject); }
        }
        private static void DestroyOwned(UnityEngine.Object value)
        { if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
        private int Find(ElementChunkKey key)
        {
            for (int i = 0; i < Capacity; i++) if (_bound[i] && _keys[i].Equals(key)) return i;
            for (int i = 0; i < Capacity; i++) if (!_bound[i]) return i;
            return -1;
        }
        private void Fill(Mesh mesh)
        {
            mesh.Clear();
            mesh.SetVertices(_builder.Vertices, 0, _builder.VertexCount);
            mesh.SetNormals(_builder.Normals, 0, _builder.VertexCount);
            mesh.SetColors(_builder.MaterialIds, 0, _builder.VertexCount);
            mesh.SetIndices(_builder.Indices, 0, _builder.IndexCount, MeshTopology.Triangles, 0, true);
        }
    }
}
