using System.Collections.Generic;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// 归档保持紧凑 Cell；显示使用连续 Surface Nets。稳定 Chunk 复用 Mesh，
    /// 所有 Chunk 共用一套采样工作内存，不为每个休眠页分配密集场。
    /// </summary>
    public sealed class DormantLiquidCellRenderer : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _worldRuntime;
        [SerializeField] private Material _sourceLiquidMaterial;
        [SerializeField] private Shader _dormantShader;
        [SerializeField] private MaterialId _targetMaterial = MaterialId.Water;
        [SerializeField, Range(0f, 1f)] private float _minimumHeightRatio = .08f;
        [SerializeField] private ElementWaterVolumeRenderProfile _volumeProfile;
        [SerializeField, Min(1)] private int _maximumChunkViews = 512;
        [SerializeField, Min(1)] private int _maxChunkRebuildsPerFrame = 1;

        private struct ChunkView
        {
            public Mesh Mesh;
            public ElementChunkKey Key;
            public ulong DesiredHash, BuiltHash;
            public bool Assigned, Built;
            public Matrix4x4 Matrix;
            public Bounds Bounds;
        }
        private IFluidDormantReadOnly _source;
        private LiquidMaterialCellSample[] _samples;
        private DormantLiquidSurfaceSnapshot _snapshot;
        private ChunkView[] _views;
        private Dictionary<ElementChunkKey, int> _viewIndices;
        private WaterVolumeMeshingWorkspace _workspace;
        private WaterVolumeMeshingSettings _settings;
        private List<Vector3> _vertices, _normals;
        private List<Vector2> _uvs;
        private List<int> _indices;
        private Material _runtimeMaterial;
        private RenderParams _renderParams;
        private uint _lastVersion = uint.MaxValue;
        private Bounds _cachedBounds;
        private int _nextRebuild;
        private bool _hasSnapshot, _reportedCapacity;

        private void Start()
        {
            if (_worldRuntime == null || _sourceLiquidMaterial == null || _dormantShader == null
                || _targetMaterial == MaterialId.Empty)
            {
                GameLog.Error("Dormant Liquid 缺少 World、Material、Shader 或 MaterialId。", "Rendering");
                enabled = false;
                return;
            }
            _source = _worldRuntime.DormantLiquids;
            if (_source == null || _source.MaximumDormantCellRecords <= 0 || !_worldRuntime.IsInitialized)
            {
                enabled = false;
                return;
            }
            // 旧场景无需补引用也能显示完整几何；可选 Profile 复用已有体积液体调参。
            _settings = _volumeProfile != null ? _volumeProfile.CreateSettings()
                : new WaterVolumeMeshingSettings(2, .25f, Mathf.Max(.08f, _minimumHeightRatio),
                    .30f, .48f, .20f, 1f / 255f);
            _samples = new LiquidMaterialCellSample[_source.MaximumDormantCellRecords];
            _snapshot = new DormantLiquidSurfaceSnapshot(_worldRuntime, _samples.Length,
                _worldRuntime.GetComponent<ElementFieldSolidBaker>());
            _workspace = new WaterVolumeMeshingWorkspace(_worldRuntime.ChunkSize,
                _settings.SamplesPerCell, allowCrossChunkVisualSpread: true);
            int dimension = _worldRuntime.ChunkSize * _settings.SamplesPerCell + 1;
            int maximumVertices = dimension * dimension * dimension;
            _vertices = new List<Vector3>(maximumVertices);
            _normals = new List<Vector3>(maximumVertices);
            _uvs = new List<Vector2>(maximumVertices);
            _indices = new List<int>(maximumVertices * 18);
            _views = new ChunkView[Mathf.Max(1, _maximumChunkViews)];
            _viewIndices = new Dictionary<ElementChunkKey, int>(_views.Length);
            // 预建轻量 Mesh 句柄，GPU 顶点仅在重建时上传；热路径不分配托管集合。
            for (int i = 0; i < _views.Length; i++)
                _views[i].Mesh = new Mesh { name = "DormantLiquidSurface", indexFormat = IndexFormat.UInt32 };
            _runtimeMaterial = CreateInstancedMaterial(_dormantShader, _sourceLiquidMaterial);
            _renderParams = new RenderParams(_runtimeMaterial)
            {
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false
            };
        }

        internal static Material CreateInstancedMaterial(Shader shader, Material source)
        {
            var material = new Material(shader);
            material.CopyPropertiesFromMaterial(source);
            // 复制后开启，避免源材质覆盖合法状态。新路径已不再逐 Cell 绘制 Cube。
            material.enableInstancing = true;
            return material;
        }

        private void Update()
        {
            if (_views == null) return;
            Bounds visible = _worldRuntime.GetVisibleWorldBounds();
            if (!_hasSnapshot || _lastVersion != _source.DormantVersion
                || !_cachedBounds.Contains(visible.min) || !_cachedBounds.Contains(visible.max))
                RefreshSnapshot(visible);
            // 多种液体错开昂贵重建，正常帧仍绘制缓存；不唤醒 PBF 或修改 Gameplay。
            int phase = _targetMaterial == MaterialId.Water ? 0 : _targetMaterial == MaterialId.Poison ? 1 : 2;
            if (Time.frameCount % 3 == phase)
                RebuildDirtyChunks();
            for (int i = 0; i < _views.Length; i++)
            {
                ref ChunkView view = ref _views[i];
                if (!view.Assigned || !view.Built || view.Mesh.vertexCount == 0) continue;
                _renderParams.worldBounds = view.Bounds;
                Graphics.RenderMesh(in _renderParams, view.Mesh, 0, view.Matrix);
            }
        }

        private void RefreshSnapshot(Bounds visible)
        {
            _lastVersion = _source.DormantVersion;
            _hasSnapshot = true;
            _cachedBounds = visible;
            // 额外一圈完整 Chunk 同时提供平滑 Halo，避免镜头微动导致全量复制。
            _cachedBounds.Expand(_worldRuntime.CellSize * _worldRuntime.ChunkSize * 2f);
            int count = _worldRuntime.ActivityExpansionEnabled && _source is IFluidVisibleArchiveReadOnly archive
                ? archive.CopyVisibleArchiveCells(_targetMaterial, _cachedBounds, _samples)
                : _source.CopyDormantCells(_targetMaterial, _samples);
            _snapshot.Clear();
            for (int i = 0; i < count; i++) _snapshot.Add(_samples[i].GlobalCell, _samples[i].Amount);
            for (int i = 0; i < _views.Length; i++)
            {
                ref ChunkView view = ref _views[i];
                if (!view.Assigned || _snapshot.ChunkHashes.ContainsKey(view.Key)) continue;
                // GPU 恢复后立即撤下已移除的归档，不能为了重建预算继续叠画旧液体。
                _viewIndices.Remove(view.Key);
                view.Assigned = false;
                view.Built = false;
            }
            foreach (var pair in _snapshot.ChunkHashes)
            {
                if (!_viewIndices.TryGetValue(pair.Key, out int slot))
                {
                    slot = 0;
                    while (slot < _views.Length && _views[slot].Assigned) slot++;
                    if (slot == _views.Length)
                    {
                        if (!_reportedCapacity)
                            GameLog.Warn("Dormant Liquid 超出 Mesh 预算，请增大 Maximum Chunk Views。", "Rendering");
                        _reportedCapacity = true;
                        continue;
                    }
                    _viewIndices.Add(pair.Key, slot);
                    _views[slot].Key = pair.Key;
                    _views[slot].Assigned = true;
                    _views[slot].Built = false;
                }
                _views[slot].DesiredHash = _snapshot.NeighborhoodHash(pair.Key);
            }
        }

        private void RebuildDirtyChunks()
        {
            int rebuilt = 0;
            for (int scanned = 0; scanned < _views.Length && rebuilt < _maxChunkRebuildsPerFrame; scanned++)
            {
                int index = _nextRebuild;
                _nextRebuild = (_nextRebuild + 1) % _views.Length;
                ref ChunkView view = ref _views[index];
                if (!view.Assigned || (view.Built && view.BuiltHash == view.DesiredHash)) continue;
                WorldWaterVolumeMeshBuilder.BuildWorldChunk(_snapshot, view.Key, in _settings,
                    _workspace, _vertices, _normals, _uvs, _indices);
                view.Mesh.Clear();
                view.Mesh.SetVertices(_vertices);
                view.Mesh.SetNormals(_normals);
                view.Mesh.SetUVs(0, _uvs);
                // 波纹基底由共享 Fragment 按 World Normal 构造，不再上传另一套 Mesh Tangent。
                view.Mesh.SetTriangles(_indices, 0, true);
                float size = _worldRuntime.CellSize * _worldRuntime.ChunkSize;
                Vector3 origin = _worldRuntime.Origin + new Vector3(view.Key.X, view.Key.Y, view.Key.Z) * size;
                view.Matrix = Matrix4x4.Translate(origin);
                view.Bounds = new Bounds(view.Mesh.bounds.center + origin, view.Mesh.bounds.size);
                view.BuiltHash = view.DesiredHash;
                view.Built = true;
                rebuilt++;
            }
        }

        private void OnDestroy()
        {
            if (_views != null)
                for (int i = 0; i < _views.Length; i++)
                    if (_views[i].Mesh != null) Destroy(_views[i].Mesh);
            if (_runtimeMaterial != null) Destroy(_runtimeMaterial);
        }
    }
}
