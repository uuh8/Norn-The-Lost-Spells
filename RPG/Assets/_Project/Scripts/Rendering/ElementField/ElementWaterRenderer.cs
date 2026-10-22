using System;
using System.Collections.Generic;
using Game.Core;
using Game.ElementField;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// 把 ElementField 中的 Water Cell 只读快照转换成按 Chunk 拆分的 Unity Mesh。
    ///
    /// Gameplay 层决定“哪里有多少水”，WaterChunkMeshBuilder 决定“可见表面在哪里”，
    /// 本组件只管理 Mesh 生命周期、Dirty Version 与每帧重建预算，最后把已有 Water Material
    /// 交给 MeshRenderer。Rendering 不会取得 Grid 写权限，也不会改变 Water/Fire 模拟结果。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementWaterRenderer : MonoBehaviour
    {
        private const int MaximumQuadsPerCell = 5;
        private const int VerticesPerQuad = 4;
        private const int IndicesPerQuad = 6;

        private static readonly ProfilerMarker RebuildChunkMarker =
            new ProfilerMarker("ElementWater.RebuildChunk");

        [Header("Read Only Field")]
        [Tooltip("拖入场景中的 ElementFieldRuntime。本组件只通过 IElementFieldReadOnly 读取它。")]
        [SerializeField] private ElementFieldRuntime _fieldRuntime;

        [Header("Water Presentation")]
        [Tooltip("复用自研 ElementWater.shader 的 Material；使用 sharedMaterial，避免每个 Chunk 克隆材质。")]
        [SerializeField] private Material _waterMaterial;

        [Header("Rebuild Budget")]
        [Tooltip("单帧最多重建多少个 Dirty Chunk。较小值能平摊法术大范围写入造成的 Mesh 尖峰。")]
        [SerializeField, Min(1)] private int _maxChunkRebuildsPerFrame = 4;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private int _chunkCount;
        [SerializeField] private int _lastFrameDirtyCount;
        [SerializeField] private int _lastFrameRebuildCount;

        private IElementFieldReadOnly _field;
        private ChunkRenderState[] _chunks;
        private uint[] _currentVersions;
        private uint[] _lastSeenVersions;
        private int[] _selectedChunkIndices;
        private int _nextScanIndex;
        private bool _isInitialized;

        private void Start()
        {
            // ElementFieldRuntime 在 Awake 创建 Grid；等到 Start 才创建显示层，可避免依赖
            // 同一帧中不同 GameObject 的 Awake 调用顺序。
            if (!TryInitialize())
                enabled = false;
        }

        private void Update()
        {
            if (!_isInitialized || !_field.IsInitialized)
                return;

            CaptureChunkVersions();
            int selectedCount = WaterChunkRebuildScheduler.SelectDirtyChunks(
                _currentVersions,
                _lastSeenVersions,
                _nextScanIndex,
                _maxChunkRebuildsPerFrame,
                _selectedChunkIndices,
                out _nextScanIndex);

            _lastFrameDirtyCount = CountDirtyChunks();
            _lastFrameRebuildCount = selectedCount;

            for (int i = 0; i < selectedCount; i++)
            {
                int chunkIndex = _selectedChunkIndices[i];
                RebuildChunk(_chunks[chunkIndex]);

                // 只有完成 Mesh 上传后才确认 Version。若将来 Rebuild 抛错，旧 Version 会保留，
                // 修复问题后的下一帧仍能再次尝试，而不会误认为该 Chunk 已同步。
                _lastSeenVersions[chunkIndex] = _currentVersions[chunkIndex];
            }
        }

        private void OnDestroy()
        {
            if (_chunks == null)
                return;

            // Mesh 是运行时用 new Mesh 创建的 Native Engine Object；销毁 GameObject 并不等于
            // 自动立即释放这个资源句柄，因此组件销毁时必须显式释放。
            for (int i = 0; i < _chunks.Length; i++)
            {
                Mesh mesh = _chunks[i].Mesh;
                if (mesh == null)
                    continue;

                if (Application.isPlaying)
                    Destroy(mesh);
                else
                    DestroyImmediate(mesh);
            }
        }

        private void OnValidate()
        {
            _maxChunkRebuildsPerFrame = Mathf.Max(1, _maxChunkRebuildsPerFrame);
        }

        private bool TryInitialize()
        {
            if (_isInitialized)
                return true;
            if (_fieldRuntime == null)
            {
                GameLog.Error(
                    "ElementWaterRenderer requires an ElementFieldRuntime reference.",
                    "Rendering");
                return false;
            }
            if (_waterMaterial == null)
            {
                GameLog.Error(
                    "ElementWaterRenderer requires a Water Material.",
                    "Rendering");
                return false;
            }

            _field = _fieldRuntime.ReadOnlyField;
            if (_field == null || !_field.IsInitialized)
            {
                GameLog.Error(
                    "ElementWaterRenderer cannot initialize before ElementFieldRuntime.",
                    "Rendering");
                return false;
            }

            Vector3Int chunkCounts = _field.ChunkCounts;
            try
            {
                _chunkCount = checked(chunkCounts.x * chunkCounts.y * chunkCounts.z);
                int cellsPerChunk = checked(
                    _field.ChunkSize * _field.ChunkSize * _field.ChunkSize);
                int vertexCapacity = checked(
                    cellsPerChunk * MaximumQuadsPerCell * VerticesPerQuad);
                int indexCapacity = checked(
                    cellsPerChunk * MaximumQuadsPerCell * IndicesPerQuad);

                CreateChunkStates(chunkCounts, vertexCapacity, indexCapacity);
            }
            catch (OverflowException)
            {
                GameLog.Error(
                    "ElementWaterRenderer Chunk dimensions are too large for managed Mesh buffers.",
                    "Rendering");
                return false;
            }

            _currentVersions = new uint[_chunkCount];
            _lastSeenVersions = new uint[_chunkCount];
            _selectedChunkIndices = new int[_chunkCount];
            for (int i = 0; i < _lastSeenVersions.Length; i++)
            {
                // 初始 0 Version 也必须生成一次空 Mesh，因此用不可能的初始观察值强制首轮同步。
                _lastSeenVersions[i] = uint.MaxValue;
            }

            _isInitialized = true;
            return true;
        }

        private void CreateChunkStates(
            Vector3Int chunkCounts,
            int vertexCapacity,
            int indexCapacity)
        {
            _chunks = new ChunkRenderState[_chunkCount];
            int index = 0;
            for (int z = 0; z < chunkCounts.z; z++)
            for (int y = 0; y < chunkCounts.y; y++)
            for (int x = 0; x < chunkCounts.x; x++)
            {
                Vector3Int coordinate = new Vector3Int(x, y, z);
                _chunks[index] = CreateChunkState(
                    coordinate,
                    vertexCapacity,
                    indexCapacity);
                index++;
            }
        }

        private ChunkRenderState CreateChunkState(
            Vector3Int coordinate,
            int vertexCapacity,
            int indexCapacity)
        {
            GameObject chunkObject = new GameObject(
                $"WaterChunk_{coordinate.x}_{coordinate.y}_{coordinate.z}");
            chunkObject.layer = gameObject.layer;
            Transform chunkTransform = chunkObject.transform;
            chunkTransform.SetParent(transform, worldPositionStays: false);
            chunkTransform.position = CalculateChunkWorldOrigin(coordinate);
            chunkTransform.rotation = Quaternion.identity;

            MeshFilter meshFilter = chunkObject.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = chunkObject.AddComponent<MeshRenderer>();
            Mesh mesh = new Mesh
            {
                name = $"ElementWater_Chunk_{coordinate.x}_{coordinate.y}_{coordinate.z}",
                indexFormat = vertexCapacity > ushort.MaxValue
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16
            };
            mesh.MarkDynamic();
            meshFilter.sharedMesh = mesh;

            // 所有 Chunk 共用同一个 Material Asset。若使用 renderer.material，Unity 会为每个
            // Renderer 隐式克隆材质，增加内存并破坏后续 Batching/统一调参。
            meshRenderer.sharedMaterial = _waterMaterial;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.enabled = false;

            return new ChunkRenderState(
                coordinate,
                mesh,
                meshRenderer,
                vertexCapacity,
                indexCapacity);
        }

        private Vector3 CalculateChunkWorldOrigin(Vector3Int coordinate)
        {
            float chunkWorldSize = _field.ChunkSize * _field.CellSize;
            return _field.Origin + new Vector3(
                coordinate.x * chunkWorldSize,
                coordinate.y * chunkWorldSize,
                coordinate.z * chunkWorldSize);
        }

        private void CaptureChunkVersions()
        {
            for (int i = 0; i < _chunks.Length; i++)
            {
                Vector3Int coordinate = _chunks[i].Coordinate;
                _currentVersions[i] = _field.GetChunkVersion(
                    coordinate.x,
                    coordinate.y,
                    coordinate.z);
            }
        }

        private int CountDirtyChunks()
        {
            int dirtyCount = 0;
            for (int i = 0; i < _currentVersions.Length; i++)
            {
                if (_currentVersions[i] != _lastSeenVersions[i])
                    dirtyCount++;
            }

            return dirtyCount;
        }

        private void RebuildChunk(ChunkRenderState state)
        {
            using (RebuildChunkMarker.Auto())
            {
                WaterChunkMeshBuilder.Build(
                    _field,
                    state.Coordinate,
                    state.Vertices,
                    state.Uvs,
                    state.Indices);

                Mesh mesh = state.Mesh;
                mesh.Clear(keepVertexLayout: false);
                if (state.Indices.Count == 0)
                {
                    state.Renderer.enabled = false;
                    return;
                }

                mesh.SetVertices(state.Vertices);
                mesh.SetUVs(0, state.Uvs);
                mesh.SetTriangles(state.Indices, submesh: 0, calculateBounds: false);

                // Builder 为每个 Quad 生成独立顶点，因此 RecalculateNormals 得到 Flat Normal；
                // RecalculateTangents 再根据 Position/UV/Normal 建立 Water Shader 所需的 TBN 基底。
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                state.Renderer.enabled = true;
            }
        }

        private sealed class ChunkRenderState
        {
            public ChunkRenderState(
                Vector3Int coordinate,
                Mesh mesh,
                MeshRenderer renderer,
                int vertexCapacity,
                int indexCapacity)
            {
                Coordinate = coordinate;
                Mesh = mesh;
                Renderer = renderer;
                Vertices = new List<Vector3>(vertexCapacity);
                Uvs = new List<Vector2>(vertexCapacity);
                Indices = new List<int>(indexCapacity);
            }

            public Vector3Int Coordinate { get; }
            public Mesh Mesh { get; }
            public MeshRenderer Renderer { get; }
            public List<Vector3> Vertices { get; }
            public List<Vector2> Uvs { get; }
            public List<int> Indices { get; }
        }
    }
}
