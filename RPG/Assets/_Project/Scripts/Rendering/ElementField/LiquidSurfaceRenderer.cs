using System.Collections.Generic;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// Grid Liquid 的统一表面：活动与稳定格子读取同一数量源，Chunk 只管理 Mesh 缓存。
    /// 初始化 Mesh 与工作数组后，LateUpdate 仅在数量版本改变时筛选候选。
    /// </summary>
    [DefaultExecutionOrder(6)]
    public sealed class LiquidSurfaceRenderer : MonoBehaviour
    {
        // 所有材质共享 CPU 时间预算；水、毒液、粘液不能各自抢四张昂贵网格。
        private static readonly LiquidSurfaceRenderer[] SharedRenderers=new LiquidSurfaceRenderer[256];
        private static int _sharedCount,_budgetFrame=-1;
        private static long _sharedBuildTicks;
        private static readonly int MotionHorizonId=Shader.PropertyToID("_LiquidMotionHorizon");
        private static readonly ProfilerMarker CandidateMarker =
            new ProfilerMarker("GridLiquid.Surface.Candidates");
        private static readonly ProfilerMarker MeshMarker =
            new ProfilerMarker("GridLiquid.Surface.MeshRebuild");
        private static readonly ProfilerMarker SnapshotMarker =
            new ProfilerMarker("GridLiquid.Surface.Snapshot");
        private static readonly ProfilerMarker DrawMarker =
            new ProfilerMarker("GridLiquid.Surface.DrawSubmit");
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private MaterialId _material = MaterialId.Water;
        [SerializeField] private Material _sourceMaterial;
        [SerializeField] private Shader _liquidShader;
        [SerializeField, Min(1)] private int _maximumChunkViews = 128;
        [SerializeField, Min(1)] private int _maximumRebuildsPerFrame = 4;
        [SerializeField, Min(.1f)] private float _meshBudgetMilliseconds=2f;
        // 卡通浅滩的参考厚度受实际液量限制，少量液体不会获得相同的完整厚度。
        // 与稳定表面的默认值保持一致，避免液体休眠后剪影突然变薄。
        [SerializeField, Min(.01f), Tooltip("浅滩圆冠参考厚度（米）。主体最多放大实际深度15%，液量减少时自动变薄；与 Stable Surface 保持一致。")]
        private float _minimumPuddleDepth=.32f;
        [Header("Runtime Debug (Read Only)")]
        [SerializeField] private int _pendingNewViews;
        [SerializeField] private int _pendingDirtyViews;
        [SerializeField] private int _oldestQueuedFrames;
        [SerializeField] private int _omittedCandidateViews;
        [SerializeField] private int _drawnViews, _drawnVertices, _drawnTriangles;
        [SerializeField] private int _rebuiltSourceSamples;
        [SerializeField] private int _mergedTopCells, _flatTopPatches;
        [SerializeField] private float _meshRebuildMilliseconds;

        private struct View
        {
            public ElementChunkKey Key;
            public Mesh Mesh, PendingMesh;
            public uint DesiredVersion, BuiltVersion;
            public bool Assigned, Built, ContainsLiquid, Falling;
            public bool Retire, Staged, PendingFalling;
            public int PendingVertexCount, PendingTriangleCount;
            public Bounds PendingBounds;
            public int QueuedFrame;
            public long Distance;
            public Matrix4x4 Matrix;
            public Bounds Bounds;
            public int VertexCount, TriangleCount;
        }

        private struct Candidate
        {
            public ElementChunkKey Key;
            public long Distance;
            public bool ContainsLiquid;
        }

        private ILiquidSurfaceReadOnly _source;
        private GraphicsBuffer _emptyDomainLookup;
        private LiquidSurfaceFrameSnapshot _snapshot;
        private bool _batchActive;
        private int _processedFrame=-1;
        private LiquidSurfaceMeshBuilder _input;
        private View[] _views;
        private Dictionary<ElementChunkKey, int> _indices;
        private HashSet<ElementChunkKey> _candidateKeys;
        private Candidate[] _nearest;
        private ElementChunkKey[] _sourceKeys;
        private Camera _camera;
        private ElementChunkKey _lastCameraChunk;
        private List<Vector3> _vertices, _normals;
        private List<Vector2> _uvs;
        private List<Vector4> _motionData;
        private ILiquidMotionReadOnly _motionSource;
        private List<int> _triangles;
        private Material _materialInstance;
        private RenderParams _renderParams;
        private uint _lastSourceVersion = uint.MaxValue;
        private uint _lastStableSurfaceVersion = uint.MaxValue;
        private uint _lastCandidateVersion = uint.MaxValue;
        private bool _warnedCapacity;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private int _gcSampledFrames, _gcAllocationFrames;
        private long _gcBefore;
        private long _gcAllocatedBytes, _gcMaximumFrameBytes;
        private int _peakNewViews, _peakDirtyViews, _peakQueuedFrames;
        private int _peakOmittedViews, _framesWithPendingViews;
#endif

        private void Start()
        {
            _source = _world != null ? _world.GridLiquids : null;
            if(GridLiquidFieldSurfaceRenderer.Find(_source)!=null)return;
            _motionSource = _source as ILiquidMotionReadOnly;
            if (_source == null || _source.CellSize <= 0f || _source.ChunkSize <= 0
                || _sourceMaterial == null || _liquidShader == null)
            {
                enabled = false;
                return;
            }
            _snapshot=new LiquidSurfaceFrameSnapshot(_source,_material);
            _input = new LiquidSurfaceMeshBuilder(_snapshot, _material,
                Mathf.Max(1, _source.MaximumSurfaceCandidateKeys),_minimumPuddleDepth/_source.CellSize);
            // Surface Nets 含接缝 Halo；每个采样 Cube 最多三张 Quad。
            // 工作数组在 Start 预留最坏情况，持续落水时无需 List 扩容。
            int maximumCells = _source.ChunkSize * _source.ChunkSize * _source.ChunkSize;
            int cubeSide = (_source.ChunkSize + 12) * 2;
            // 每个边沿小面最多八个顶点、四段裙边；纳入二维 Halo 的最坏情况，
            // 小 Chunk 也不能在流动更新时让 List 扩容。完整内部仍用合并条带。
            int groundSide=_source.ChunkSize+2;
            int maximumVertices = Mathf.Max(maximumCells * 41,
                Mathf.Max(groundSide*groundSide*16*24,cubeSide*cubeSide*cubeSide));
            _vertices = new List<Vector3>(maximumVertices);
            _normals = new List<Vector3>(maximumVertices);
            _uvs = new List<Vector2>(maximumVertices);
            _motionData = new List<Vector4>(maximumVertices);
            _triangles = new List<int>(Mathf.Max(maximumCells * 72, maximumVertices * 18));
            _views = new View[Mathf.Max(1, _maximumChunkViews)];
            _nearest = new Candidate[_views.Length];
            _sourceKeys=new ElementChunkKey[Mathf.Max(1,_source.MaximumSurfaceCandidateKeys)];
            _indices = new Dictionary<ElementChunkKey, int>(_views.Length);
            _candidateKeys = new HashSet<ElementChunkKey>(_views.Length);
            _camera = Camera.main;
            for (int i = 0; i < _views.Length; i++)
            {
                _views[i].Mesh = new Mesh { name = "GridLiquidSurface", indexFormat = IndexFormat.UInt32 };
                _views[i].Mesh.MarkDynamic();
                _views[i].PendingMesh=new Mesh {name="GridLiquidSurface.Pending",indexFormat=IndexFormat.UInt32};
                _views[i].PendingMesh.MarkDynamic();
            }
            for(int i=0;i<SharedRenderers.Length;i++)
                if(SharedRenderers[i]==null){SharedRenderers[i]=this;_sharedCount=Mathf.Max(_sharedCount,i+1);break;}
            _materialInstance = new Material(_liquidShader);
            _materialInstance.CopyPropertiesFromMaterial(_sourceMaterial);
            _materialInstance.enableInstancing = true;
            _emptyDomainLookup = LiquidSurfaceDomainBindings.CreateEmpty(_materialInstance);
            _renderParams = new RenderParams(_materialInstance)
            {
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
        }

        private bool SharesFrame(LiquidSurfaceRenderer renderer) => renderer!=null
            && renderer.isActiveAndEnabled && renderer._views!=null && renderer._source==_source;

        private void LateUpdate()
        {
            if(GridLiquidFieldSurfaceRenderer.Find(_source)!=null)return;
            if(_views==null || _processedFrame==Time.frameCount)return;
            bool active=false;
            for(int i=0;i<_sharedCount;i++)
            {
                LiquidSurfaceRenderer renderer=SharedRenderers[i];if(!SharesFrame(renderer))continue;
                active|=renderer._batchActive;
            }
            // 同一世界的所有材料捕获/提交同一批次，避免 Water 已撤下面、Poison 还没出现。
            // LateUpdate 在世界 Update 后读取，Renderer 的组件执行次序不会混入新 Solver Tick。
            for(int i=0;i<_sharedCount;i++)
            {
                LiquidSurfaceRenderer renderer=SharedRenderers[i];if(!SharesFrame(renderer))continue;
                renderer._processedFrame=Time.frameCount;
                renderer.PrepareFrame(!active);
            }
            for(int i=0;i<_sharedCount;i++)
            {
                LiquidSurfaceRenderer renderer=SharedRenderers[i];if(!SharesFrame(renderer))continue;
                using(MeshMarker.Auto())renderer.RebuildVisible();
            }
            TryCommitPresentationGroup(_source);
            for(int i=0;i<_sharedCount;i++)
                if(SharesFrame(SharedRenderers[i]))SharedRenderers[i].DrawFrame();
        }

        private void PrepareFrame(bool beginAllowed)
        {
            if (_motionSource != null) _materialInstance.SetFloat(MotionHorizonId, _motionSource.MotionHorizon);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 只在开发版逐组件计量托管分配；全局 GC Alloc 无法区分液体与角色／战斗。
            _gcBefore = System.GC.GetAllocatedBytesForCurrentThread();
#endif
            ElementChunkKey cameraChunk = CameraChunk();
            // 动态数量和稳定边界都已冻结，后续写入留给下一批。排水/注水每 Tick
            // 改水位也不能重启正在构建的批次，否则有限预算下会永久等待。
            if (beginAllowed && !_batchActive && (_source.GetDynamicCandidateVersion(_material) != _lastCandidateVersion
                || cameraChunk != _lastCameraChunk
                || _source.GetDynamicSurfaceVersion(_material)!=_lastSourceVersion
                || _source.GetStableSurfaceVersion(_material)!=_lastStableSurfaceVersion))
            {
                using (CandidateMarker.Auto()) RefreshCandidates(cameraChunk);
                using(SnapshotMarker.Auto())_snapshot.Capture(_candidateKeys);
                _lastSourceVersion=_snapshot.GetDynamicSurfaceVersion(_material);
                _lastStableSurfaceVersion=_snapshot.GetStableSurfaceVersion(_material);
                for(int i=0;i<_views.Length;i++)
                {
                    ref View view=ref _views[i];if(!view.Assigned)continue;
                    uint version=NeighborhoodVersion(view.Key);
                    if(view.Built && view.BuiltVersion==view.DesiredVersion && view.DesiredVersion!=version)
                        view.QueuedFrame=Time.frameCount;
                    view.DesiredVersion=version;
                    view.Staged=view.Retire || view.Built && view.BuiltVersion==view.DesiredVersion;
                }
                _batchActive=true;
            }
        }

        private void DrawFrame()
        {
            _pendingNewViews = _pendingDirtyViews = _oldestQueuedFrames = 0;
            _drawnViews = _drawnVertices = _drawnTriangles = 0;
            using (DrawMarker.Auto())
            for (int i = 0; i < _views.Length; i++)
            {
                ref View view = ref _views[i];
                if (!view.Assigned) continue;
                if (!view.Built) _pendingNewViews++;
                else if (view.BuiltVersion != view.DesiredVersion) _pendingDirtyViews++;
                if (!view.Built || view.BuiltVersion != view.DesiredVersion)
                    _oldestQueuedFrames = Mathf.Max(_oldestQueuedFrames,
                        Time.frameCount - view.QueuedFrame);
                // Stable Renderer 在整个批次等待时也冻结旧画面；不能因 Gameplay
                // 已压缩新区域就先隐藏旧动态页，否则会再次露出方形空洞。
                if (!view.Built || view.VertexCount == 0)
                    continue;
                // 统计提交给 Unity 的几何，避免使用 Readable Mesh 的 vertices getter
                // 复制整份顶点数组；可见性剔除后的实际 GPU 数量仍以 Profiler 为准。
                _drawnViews++;
                _drawnVertices += view.VertexCount;
                _drawnTriangles += view.TriangleCount;
                _renderParams.worldBounds = view.Bounds;
                Graphics.RenderMesh(in _renderParams, view.Mesh, 0, view.Matrix);
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_pendingNewViews > _peakNewViews) _peakNewViews = _pendingNewViews;
            if (_pendingDirtyViews > _peakDirtyViews) _peakDirtyViews = _pendingDirtyViews;
            if (_oldestQueuedFrames > _peakQueuedFrames) _peakQueuedFrames = _oldestQueuedFrames;
            if (_omittedCandidateViews > _peakOmittedViews)
                _peakOmittedViews = _omittedCandidateViews;
            if (_pendingNewViews > 0 || _pendingDirtyViews > 0) _framesWithPendingViews++;
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - _gcBefore;
            _gcSampledFrames++;
            if (allocated > 0)
            {
                _gcAllocationFrames++;
                _gcAllocatedBytes += allocated;
                if (allocated > _gcMaximumFrameBytes) _gcMaximumFrameBytes = allocated;
            }
#endif
        }

        private ElementChunkKey CameraChunk()
        {
            if (_camera == null) return default;
            Vector3Int cell = ElementWorldCoordinates.WorldToGlobalCell(
                _camera.transform.position, _source.Origin, _source.CellSize);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _source.ChunkSize,
                out ElementChunkKey key, out _);
            return key;
        }

        private void RefreshCandidates(ElementChunkKey cameraChunk)
        {
            _lastCandidateVersion = _source.GetDynamicCandidateVersion(_material);
            _lastCameraChunk = cameraChunk;
            _candidateKeys.Clear();
            int count = _source.CopySurfaceChunkKeys(_material,_sourceKeys);
            int selected = 0;
            // Builder 已筛掉只供顶点平滑读取的空邻页；预算只花在真实含液体的页。
            for (int i = 0; i < count; i++)
            {
                ElementChunkKey key = _sourceKeys[i];
                long dx = (long)key.X - cameraChunk.X;
                long dy = (long)key.Y - cameraChunk.Y;
                long dz = (long)key.Z - cameraChunk.Z;
                var candidate = new Candidate { Key = key,
                    Distance = dx * dx + dy * dy + dz * dz,
                    ContainsLiquid = _source.HasDynamicMaterialInChunk(key, _material) };
                if (selected < _nearest.Length)
                {
                    int at = selected++;
                    _nearest[at] = candidate;
                    while (at > 0)
                    {
                        int parent = (at - 1) / 2;
                        if (Compare(_nearest[parent], _nearest[at]) >= 0) break;
                        Candidate swap = _nearest[parent];
                        _nearest[parent] = _nearest[at];
                        _nearest[at] = swap;
                        at = parent;
                    }
                }
                else if (Compare(candidate, _nearest[0]) < 0)
                {
                    _nearest[0] = candidate;
                    int at = 0;
                    while (true)
                    {
                        int left = at * 2 + 1;
                        if (left >= selected) break;
                        int right = left + 1;
                        int child = right < selected
                            && Compare(_nearest[right], _nearest[left]) > 0 ? right : left;
                        if (Compare(_nearest[at], _nearest[child]) >= 0) break;
                        Candidate swap = _nearest[at];
                        _nearest[at] = _nearest[child];
                        _nearest[child] = swap;
                        at = child;
                    }
                }
            }
            _omittedCandidateViews = count - selected;
            if (_omittedCandidateViews > 0 && !_warnedCapacity)
            {
                GameLog.Warn("Grid Liquid 表面 View 达到上限；优先保留有液体且靠近相机的 Chunk。",
                    "Rendering");
                _warnedCapacity = true;
            }
            for (int i = 0; i < selected; i++) _candidateKeys.Add(_nearest[i].Key);
            for (int i = 0; i < _views.Length; i++)
            {
                ref View view = ref _views[i];
                if (!view.Assigned) continue;
                // 源页暂不撤去。跨页迁移的目标 Mesh 建好后，Commit 才同时换掉两页。
                if(!view.Retire && !_candidateKeys.Contains(view.Key))view.QueuedFrame=Time.frameCount;
                view.Retire=!_candidateKeys.Contains(view.Key);
            }
            for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < selected; i++)
            {
                Candidate candidate = _nearest[i];
                if (candidate.ContainsLiquid != (pass == 0)) continue;
                ElementChunkKey key = candidate.Key;
                if (!_indices.TryGetValue(key, out int slot))
                {
                    slot = 0;
                    while (slot < _views.Length && _views[slot].Assigned) slot++;
                    if (slot == _views.Length)
                    {
                        if (!_warnedCapacity)
                            GameLog.Warn("Grid Liquid 表面 View 容量不足，部分液体暂时不可见。", "Rendering");
                        _warnedCapacity = true;
                        _lastCandidateVersion=uint.MaxValue; // 旧页本批撤下后，下一批重试空槽。
                        continue;
                    }
                    _indices.Add(key, slot);
                    _views[slot].Assigned = true;
                    _views[slot].Built = false;
                    _views[slot].Key = key;
                    _views[slot].QueuedFrame = Time.frameCount;
                }
                ref View view = ref _views[slot];
                view.Retire=false;
                view.ContainsLiquid = candidate.ContainsLiquid;
                view.Distance = candidate.Distance;
            }
        }

        private static int Compare(Candidate left, Candidate right)
        {
            if (left.ContainsLiquid != right.ContainsLiquid)
                return left.ContainsLiquid ? -1 : 1;
            if (left.Distance != right.Distance)
                return left.Distance < right.Distance ? -1 : 1;
            if (left.Key.X != right.Key.X) return left.Key.X < right.Key.X ? -1 : 1;
            if (left.Key.Y != right.Key.Y) return left.Key.Y < right.Key.Y ? -1 : 1;
            if (left.Key.Z != right.Key.Z) return left.Key.Z < right.Key.Z ? -1 : 1;
            return 0;
        }

        private uint NeighborhoodVersion(ElementChunkKey key)
        {
            // 27 个版本作为邻域失效摘要；一个边界 Cell 改动会同时刷新接缝两侧。
            uint hash = 2166136261u;
            unchecked
            {
                int radius=Mathf.CeilToInt(7f/_source.ChunkSize);
                for (int z = -radius; z <= radius; z++)
                for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                    hash = (hash ^ _snapshot.GetSurfaceChunkVersion(
                        new ElementChunkKey(key.X + x, key.Y + y, key.Z + z), _material))
                        * 16777619u;
                // 只有本 Chunk 一格邻域内的稳定湖发生变化，动态接缝 Mesh 才失效；
                // 远处湖面的水位更新不会强迫场上所有水滩重建。
                hash = (hash ^ _snapshot.GetStableNeighborhoodVersion(key, _material))
                    * 16777619u;
            }
            return hash;
        }

        private void RebuildVisible()
        {
            if(_budgetFrame!=Time.frameCount){_budgetFrame=Time.frameCount;_sharedBuildTicks=0;}
            _rebuiltSourceSamples = _mergedTopCells = _flatTopPatches = 0;
            _meshRebuildMilliseconds=0f;
            if(!_batchActive)return;
            long timeBudget=(long)(_meshBudgetMilliseconds*.001d*System.Diagnostics.Stopwatch.Frequency);
            bool handoff=HasPendingHandoff();
            if(!handoff && (_sharedBuildTicks>=timeBudget || !HasBuildTurn()))return;
            // Gameplay 的稳定→动态快照仅保留一个交接帧。该帧最多补齐四张正文页，
            // 避免普通时间预算让旧湖面撤下后出现空白；这次有界突发优先于细节刷新。
            int limit=handoff?4:_maximumRebuildsPerFrame;
            for (int rebuilt = 0; rebuilt < limit; rebuilt++)
            {
                int index = SelectNextBuild(handoff);
                if (index < 0) break;
                long started=System.Diagnostics.Stopwatch.GetTimestamp();
                ref View view = ref _views[index];
                _input.BuildWorldChunk(view.Key, _vertices, _normals, _uvs, _triangles, _motionData);
                view.PendingFalling=false;
                for(int vertex=0;vertex<_motionData.Count;vertex++)
                    if(_motionData[vertex].x>0f) {view.PendingFalling=true;break;}
                _rebuiltSourceSamples += _input.LastBuildSourceSamples;
                _mergedTopCells += _input.LastBuildMergedTopCells;
                _flatTopPatches += _input.LastBuildFlatPatches;
                view.PendingMesh.Clear();
                view.PendingMesh.SetVertices(_vertices);
                view.PendingMesh.SetNormals(_normals);
                view.PendingMesh.SetUVs(0, _uvs);
                view.PendingMesh.SetUVs(1, _motionData);
                view.PendingMesh.SetTriangles(_triangles, 0, true);
                float size = _source.ChunkSize * _source.CellSize;
                Vector3 origin = _source.Origin + new Vector3(view.Key.X,
                    view.Key.Y, view.Key.Z) * size;
                view.Matrix = Matrix4x4.Translate(origin);
                view.PendingBounds = new Bounds(view.PendingMesh.bounds.center + origin, view.PendingMesh.bounds.size);
                // Shader 连续下落时 CPU Bounds 也必须包含运动，防止瀑布被提前剔除。
                view.PendingBounds.Expand(new Vector3(0f, _source.CellSize * 16f, 0f));
                view.Staged=true;
                view.PendingVertexCount = _vertices.Count;
                view.PendingTriangleCount = _triangles.Count / 3;
                long elapsed=System.Diagnostics.Stopwatch.GetTimestamp()-started;
                _sharedBuildTicks+=elapsed;
                _meshRebuildMilliseconds+=(float)(elapsed*1000d/System.Diagnostics.Stopwatch.Frequency);
                // 本页先留在备用 Mesh。整批完成才交换；预算仍在页边界检查，
                // 一张页本身超时仍可能超过2ms，但不会继续同帧连建其余材质/十六页。
                if(!handoff && _sharedBuildTicks>=timeBudget)break;
            }
        }

        private void CommitCompletedBatch()
        {
            if(!_batchActive || !CanCommitBatch())return;
            for(int i=0;i<_views.Length;i++)
            {
                ref View view=ref _views[i];if(!view.Assigned)continue;
                if(view.Retire)
                {_indices.Remove(view.Key);view.Assigned=false;view.Built=false;continue;}
                if(view.Built && view.BuiltVersion==view.DesiredVersion)continue;
                Mesh previous=view.Mesh;view.Mesh=view.PendingMesh;view.PendingMesh=previous;
                view.Bounds=view.PendingBounds;view.VertexCount=view.PendingVertexCount;
                view.TriangleCount=view.PendingTriangleCount;view.Falling=view.PendingFalling;
                view.BuiltVersion=view.DesiredVersion;view.Built=true;
            }
            _batchActive=false;
        }

        private bool CanCommitBatch()
        {
            if(!_batchActive)return true;
            for(int i=0;i<_views.Length;i++)
                if(_views[i].Assigned && !_views[i].Staged)return false;
            return true;
        }

        internal static bool IsPresentationPending(ILiquidSurfaceReadOnly source)
        {
            for(int i=0;i<_sharedCount;i++)
            {
                LiquidSurfaceRenderer renderer=SharedRenderers[i];
                if(renderer!=null && renderer.isActiveAndEnabled && renderer._source==source
                    && renderer._batchActive)return true;
            }
            return false;
        }

        internal static ILiquidSurfaceReadOnly CommittedSurfaceSource(ILiquidSurfaceReadOnly source,MaterialId material)
        {
            for(int i=0;i<_sharedCount;i++)
            {
                LiquidSurfaceRenderer renderer=SharedRenderers[i];
                if(renderer!=null && renderer.isActiveAndEnabled && renderer._source==source
                    && renderer._material==material && !renderer._batchActive && renderer._snapshot!=null)
                    return renderer._snapshot;
            }
            return source;
        }

        internal static bool TryCommitPresentationGroup(ILiquidSurfaceReadOnly source)
        {
            // 先验证整组，再交换任何一个 Mesh；不能在遍历中边查边 Commit。
            for(int i=0;i<_sharedCount;i++)
            {
                LiquidSurfaceRenderer renderer=SharedRenderers[i];
                if(renderer!=null && renderer.isActiveAndEnabled && renderer._source==source
                    && !renderer.CanCommitBatch())return false;
            }
            for(int i=0;i<_sharedCount;i++)
            {
                LiquidSurfaceRenderer renderer=SharedRenderers[i];
                if(renderer!=null && renderer.isActiveAndEnabled && renderer._source==source)renderer.CommitCompletedBatch();
            }
            return true;
        }

        private bool HasPendingHandoff()
        {
            for(int i=0;i<_views.Length;i++)
            {
                ref View view=ref _views[i];
                if(view.Assigned && !view.Staged && !view.Retire && view.ContainsLiquid
                    && _source.IsStableSurfaceHandoffPending(_material,view.Key))return true;
            }
            return false;
        }

        private bool HasBuildTurn()
        {
            // 每帧轮换首选材质；跳过无待办的材质，避免固定 Update 顺序饿死毒液/粘液。
            for(int offset=0;offset<_sharedCount;offset++)
            {
                LiquidSurfaceRenderer candidate=SharedRenderers[(Time.frameCount+offset)%_sharedCount];
                if(candidate!=null && candidate.isActiveAndEnabled && candidate._views!=null
                    && candidate.SelectNextBuild()>=0)return candidate==this;
            }
            return true;
        }

        private int SelectNextBuild(bool handoffOnly=false)
        {
            int chosen = -1, bestPriority = int.MaxValue;
            int oldestFrame = int.MaxValue;
            long closestDistance = long.MaxValue;
            for (int i = 0; i < _views.Length; i++)
            {
                ref View view = ref _views[i];
                if (!_batchActive || !view.Assigned || view.Retire || view.Staged)
                    continue;
                if(handoffOnly && (!view.ContainsLiquid || !_source.IsStableSurfaceHandoffPending(_material,view.Key)))continue;
                // 新正文、可能残留的旧 Halo、新 Halo、普通细节更新依次处理。
                int priority = !view.Built ? (view.ContainsLiquid ? 0 : 2)
                    : (view.ContainsLiquid ? 3 : 1);
                // 运动 Mesh 的旧姿态只适合前推一个 Tick，不能排在大量静止岸线更新之后。
                if(view.Falling && view.ContainsLiquid)priority=-3;
                if (view.ContainsLiquid
                    && _source.IsStableSurfaceHandoffPending(_material, view.Key))
                    priority = -2;
                else if (view.ContainsLiquid && view.Built
                    && _source.IsActiveStableSurfaceOverlapping(_material, view.Key))
                    priority = -2;
                // 持续施法时也不能让旧边界永久保留残影；等待超过 12 帧后按年龄抢先。
                if (priority > -2 && Time.frameCount - view.QueuedFrame >= 12)
                    priority = -1;
                if (priority > bestPriority || (priority == bestPriority
                    && (view.QueuedFrame > oldestFrame
                        || (view.QueuedFrame == oldestFrame
                            && view.Distance >= closestDistance)))) continue;
                chosen = i;
                bestPriority = priority;
                oldestFrame = view.QueuedFrame;
                closestDistance = view.Distance;
            }
            return chosen;
        }

        private void OnDestroy()
        {
            for(int i=0;i<_sharedCount;i++)if(SharedRenderers[i]==this)SharedRenderers[i]=null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_gcSampledFrames > 0)
                GameLog.Info($"Grid Surface {_material}: GC {_gcAllocationFrames}/{_gcSampledFrames} frames, "
                    + $"total {_gcAllocatedBytes} B, max {_gcMaximumFrameBytes} B/frame; "
                    + $"queue peak new={_peakNewViews}, dirty={_peakDirtyViews}, "
                    + $"age={_peakQueuedFrames} frames, omitted={_peakOmittedViews}, "
                    + $"pending frames={_framesWithPendingViews}.", "Rendering");
#endif
            if (_views != null)
                for (int i = 0; i < _views.Length; i++)
                {
                    if (_views[i].Mesh != null) Destroy(_views[i].Mesh);
                    if (_views[i].PendingMesh != null) Destroy(_views[i].PendingMesh);
                }
            if (_materialInstance != null) Destroy(_materialInstance);
            _emptyDomainLookup?.Dispose();
            _emptyDomainLookup = null;
        }
    }
}
