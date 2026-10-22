using System;
using System.Collections.Generic;
using Game.ElementField;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// 一个可复用的 Water Chunk Presentation View。
    /// 它只拥有 Mesh/GameObject 等视觉资源，不拥有 Gameplay Cell；关闭 View 不会删除世界数据。
    /// </summary>
    public sealed class WorldWaterChunkView
    {
        internal WorldWaterChunkView(
            Transform parent,
            Material sharedMaterial,
            int index,
            in WaterVolumeMeshingSettings meshingSettings,
            int chunkSize,
            int vertexCapacity,
            int indexCapacity)
        {
            GameObject = new GameObject($"WorldWaterChunkView_{index}");
            Transform = GameObject.transform;
            Transform.SetParent(parent, false);

            var filter = GameObject.AddComponent<MeshFilter>();
            Renderer = GameObject.AddComponent<MeshRenderer>();
            Renderer.sharedMaterial = sharedMaterial;
            Renderer.shadowCastingMode = ShadowCastingMode.Off;
            Renderer.receiveShadows = false;

            Mesh = new Mesh
            {
                name = $"WorldWaterChunkMesh_{index}",
                indexFormat = vertexCapacity > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16,
            };
            Mesh.MarkDynamic();
            filter.sharedMesh = Mesh;
            Vertices = new List<Vector3>(vertexCapacity);
            Normals = new List<Vector3>(vertexCapacity);
            Uvs = new List<Vector2>(vertexCapacity);
            Indices = new List<int>(indexCapacity);
            MeshingWorkspace = new WaterVolumeMeshingWorkspace(
                chunkSize,
                meshingSettings.SamplesPerCell);
            GameObject.SetActive(false);
        }

        public ElementChunkKey Key { get; internal set; }
        public GameObject GameObject { get; }
        public Transform Transform { get; }
        public Mesh Mesh { get; }
        public MeshRenderer Renderer { get; }
        public List<Vector3> Vertices { get; }
        public List<Vector3> Normals { get; }
        public List<Vector2> Uvs { get; }
        public List<int> Indices { get; }
        public WaterVolumeMeshingWorkspace MeshingWorkspace { get; }
        public bool IsAssigned { get; internal set; }
        public bool HasSynchronizedVersion { get; internal set; }
        public uint LastSeenVisualVersion { get; internal set; }

        internal void Assign(ElementChunkKey key, Vector3 worldPosition)
        {
            Key = key;
            IsAssigned = true;
            HasSynchronizedVersion = false;
            Transform.position = worldPosition;
            Renderer.enabled = false;
            GameObject.SetActive(true);
        }

        internal void Release()
        {
            IsAssigned = false;
            HasSynchronizedVersion = false;
            Key = default;
            Mesh.Clear(keepVertexLayout: false);
            Vertices.Clear();
            Normals.Clear();
            Uvs.Clear();
            Indices.Clear();
            Renderer.enabled = false;
            GameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Water Chunk View 的有界对象池。Chunk Streaming 时复用 Mesh/GameObject，避免反复
    /// Instantiate/Destroy 造成 Main Thread Spike、Native Mesh churn 与 GC 压力。
    /// </summary>
    public sealed class WorldWaterChunkViewPool : IDisposable
    {
        private readonly Transform _parent;
        private readonly Material _sharedMaterial;
        private readonly WorldWaterChunkView[] _views;
        private readonly WaterVolumeMeshingSettings _meshingSettings;
        private readonly int _chunkSize;
        private readonly int _vertexCapacity;
        private readonly int _indexCapacity;
        private int _createdCount;
        private bool _disposed;

        public WorldWaterChunkViewPool(
            Transform parent,
            Material sharedMaterial,
            int maximumViews,
            WaterVolumeMeshingSettings meshingSettings,
            int chunkSize,
            int vertexCapacity = 0,
            int indexCapacity = 0)
        {
            if (parent == null)
                throw new ArgumentNullException(nameof(parent));
            if (maximumViews <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumViews));
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            if (vertexCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(vertexCapacity));
            if (indexCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(indexCapacity));

            _parent = parent;
            _sharedMaterial = sharedMaterial;
            _views = new WorldWaterChunkView[maximumViews];
            _meshingSettings = meshingSettings;
            _chunkSize = chunkSize;
            _vertexCapacity = vertexCapacity;
            _indexCapacity = indexCapacity;
        }

        public int CreatedCount => _createdCount;
        public int MaximumViews => _views.Length;

        public bool TryAcquire(
            ElementChunkKey key,
            Vector3 worldPosition,
            out WorldWaterChunkView view)
        {
            ThrowIfDisposed();

            // 同一 Key 重复请求直接返回既有 View，避免一份 Gameplay Chunk 出现两张重叠水面。
            for (int i = 0; i < _createdCount; i++)
            {
                WorldWaterChunkView candidate = _views[i];
                if (candidate.IsAssigned && candidate.Key == key)
                {
                    candidate.Transform.position = worldPosition;
                    view = candidate;
                    return true;
                }
            }

            for (int i = 0; i < _createdCount; i++)
            {
                WorldWaterChunkView candidate = _views[i];
                if (candidate.IsAssigned)
                    continue;

                candidate.Assign(key, worldPosition);
                view = candidate;
                return true;
            }

            if (_createdCount >= _views.Length)
            {
                view = null;
                return false;
            }

            // 创建只发生在首次扩容到预算上限的离散时刻；后续 Streaming 全部走复用路径。
            view = new WorldWaterChunkView(
                _parent,
                _sharedMaterial,
                _createdCount,
                in _meshingSettings,
                _chunkSize,
                _vertexCapacity,
                _indexCapacity);
            _views[_createdCount++] = view;
            view.Assign(key, worldPosition);
            return true;
        }

        public void Release(ElementChunkKey key)
        {
            ThrowIfDisposed();
            for (int i = 0; i < _createdCount; i++)
            {
                WorldWaterChunkView candidate = _views[i];
                if (!candidate.IsAssigned || candidate.Key != key)
                    continue;

                candidate.Release();
                return;
            }
        }

        public bool TryGetAssigned(ElementChunkKey key, out WorldWaterChunkView view)
        {
            ThrowIfDisposed();
            for (int i = 0; i < _createdCount; i++)
            {
                WorldWaterChunkView candidate = _views[i];
                if (candidate.IsAssigned && candidate.Key == key)
                {
                    view = candidate;
                    return true;
                }
            }

            view = null;
            return false;
        }

        public int CopyAssignedViews(WorldWaterChunkView[] destination)
        {
            ThrowIfDisposed();
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            int count = 0;
            for (int i = 0; i < _createdCount && count < destination.Length; i++)
            {
                if (_views[i].IsAssigned)
                    destination[count++] = _views[i];
            }

            return count;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            for (int i = 0; i < _createdCount; i++)
            {
                WorldWaterChunkView view = _views[i];
                if (view == null)
                    continue;

                DestroyUnityObject(view.Mesh);
                DestroyUnityObject(view.GameObject);
                _views[i] = null;
            }

            _createdCount = 0;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WorldWaterChunkViewPool));
        }

        private static void DestroyUnityObject(UnityEngine.Object target)
        {
            if (target == null)
                return;

            if (Application.isPlaying)
                UnityEngine.Object.Destroy(target);
            else
                UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
