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
    /// 每个稳定区域把岸线行段合进一张 Mesh；矩阵提供水位，Uniform 提供数量相关圆冠。
    /// 深水无需 GPU 粒子，静水量变化也不重建整片拓扑。
    /// </summary>
    [DefaultExecutionOrder(10)]
    public sealed class StableLiquidSurfaceRenderer : MonoBehaviour
    {
        private static readonly ProfilerMarker SurfaceMarker =
            new ProfilerMarker("GridLiquid.StableSurface.Update");
        private sealed class RegionView
        {
            public Mesh Mesh;
            public int RegionId = -1;
            public int PatchCount;
            public uint TopologyVersion;
            public int LastSeenFrame = -1;
            public float OriginX, OriginZ;
            public bool RoundedShallow;
            public bool Visible;
            public Matrix4x4 LastMatrix;
            public Bounds LastBounds;
            public Vector4 LastShape;
        }

        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private MaterialId _material = MaterialId.Water;
        [SerializeField] private Material _sourceMaterial;
        [SerializeField] private Shader _liquidShader;
        [SerializeField, Min(1)] private int _maximumPatches = 64;
        // 浅滩复用动态表面的卡通厚度；深湖仍按实际水位绘制，不额外模拟湖底材料。
        [SerializeField, Min(.01f), Tooltip("浅滩圆冠参考厚度（米）。按实际液量限制主体高度，少量液体自动变薄；与 Dynamic Surface 保持一致。")]
        private float _minimumPuddleDepth = .32f;
        [Header("Runtime Debug (Read Only)")]
        [SerializeField] private int _visiblePatchCount;
        [SerializeField] private int _submittedDraws;
        [SerializeField] private uint _topologyRebuilds;

        private ILiquidSurfaceReadOnly _source;
#if UNITY_EDITOR
        private ILiquidSurfaceReadOnly _validationSource;
        // 临时验证室使用真正的只读后端，避免建立第二个全局ElementWorld污染正式世界注册表。
        public void ConfigureForValidation(ILiquidSurfaceReadOnly source,Material material,Shader shader)
        {_validationSource=source;_sourceMaterial=material;_liquidShader=shader;}
#endif
        private StableLiquidSurfacePatch[] _patches;
        private RegionView[] _views;
        private List<Vector3> _vertices;
        private List<Vector3> _normals;
        private List<Vector2> _uvs;
        private List<Vector4> _shapeGradients;
        private List<int> _triangles;
        private Material _materialInstance;
        private RenderParams _renderParams;
        private MaterialPropertyBlock _quantityProperties;
        private static readonly int StableShapeId=Shader.PropertyToID("_StableLiquidShape");
        private StableLiquidShoreMesher _shore;
        private const int ShoreVertexBudget=65536;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private int _gcSampledFrames, _gcAllocationFrames;
        private long _gcAllocatedBytes, _gcMaximumFrameBytes;
#endif

        private void Start()
        {
            _source = _world != null ? _world.GridLiquids : null;
#if UNITY_EDITOR
            if(_validationSource!=null)_source=_validationSource;
#endif
            if (_source == null || _source.CellSize <= 0f
                || _sourceMaterial == null || _liquidShader == null)
            {
                enabled = false;
                return;
            }
            // Producer 的总 Patch 预算决定只读快照长度；场景中旧 Inspector 数值不能截断湖面。
            int patchCapacity = Mathf.Max(1, _maximumPatches,
                _source.MaximumStableSurfacePatches);
            _patches = new StableLiquidSurfacePatch[patchCapacity];
            _views = new RegionView[Mathf.Max(1, _source.MaximumStableRegions)];
            for (int i = 0; i < _views.Length; i++)
                _views[i] = new RegionView
                {
                    Mesh = new Mesh
                    {
                        name = "StableLiquidRegionSurface",
                        indexFormat = IndexFormat.UInt32,
                    },
                };
            int vertexCapacity=Mathf.Max(patchCapacity*4,ShoreVertexBudget);
            _vertices = new List<Vector3>(vertexCapacity);
            _normals = new List<Vector3>(vertexCapacity);
            _uvs = new List<Vector2>(vertexCapacity);
            _shapeGradients = new List<Vector4>(vertexCapacity);
            _triangles = new List<int>(Mathf.Max(patchCapacity*6,ShoreVertexBudget*3));
            int maximumColumns=(_source as LiquidWorldRuntime)?.MaximumStableRegionCells ?? 65536;
            _shore=new StableLiquidShoreMesher(maximumColumns,ShoreVertexBudget);
            _materialInstance = new Material(_liquidShader);
            _materialInstance.CopyPropertiesFromMaterial(_sourceMaterial);
            _materialInstance.enableInstancing = true;
            _quantityProperties=new MaterialPropertyBlock();
            _renderParams = new RenderParams(_materialInstance)
            {
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
        }

        private void LateUpdate()
        {
            if (_source == null || _views == null) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long gcBefore = System.GC.GetAllocatedBytesForCurrentThread();
#endif
            using (SurfaceMarker.Auto())
            {
                // 动态批次可能跨多帧。交接期间保留上一次完整静水画面，直到同世界
                // 的所有动态材料已提交，再撤下旧静水；不会向 Gameplay 写回显示状态。
                var continuous=GridLiquidFieldSurfaceRenderer.Find(_source);
                if(continuous!=null)continuous.PresentationView.Bind(_materialInstance);
                if((continuous!=null && continuous.IsPresentationPending) || (continuous==null && LiquidSurfaceRenderer.IsPresentationPending(_source)))DrawFrozenSurface();
                else UpdateSurface();
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - gcBefore;
            _gcSampledFrames++;
            if (allocated > 0)
            {
                _gcAllocationFrames++;
                _gcAllocatedBytes += allocated;
                if (allocated > _gcMaximumFrameBytes) _gcMaximumFrameBytes = allocated;
            }
#endif
        }

        private void UpdateSurface()
        {
            for(int i=0;i<_views.Length;i++)_views[i].Visible=false;
            // 与刚提交的动态 Mesh 使用同一份稳定行段/水位，不能立即读更晚的湖水状态。
            // 无动态 Renderer 的历史原型仍回退到原来的只读源。
            ILiquidSurfaceReadOnly presentation=LiquidSurfaceRenderer.CommittedSurfaceSource(_source,_material);
            var field=GridLiquidFieldSurfaceRenderer.Find(_source);
            int count = field!=null && field.Frame!=null?field.Frame.CopyStablePatches(_material,_patches):presentation.CopyStableSurfacePatches(_material, _patches);
            if(field!=null)field.BindStableDomain(_quantityProperties);
            else _quantityProperties.SetInt("_LiquidDomainEnabled",0);
            _visiblePatchCount = count;
            _submittedDraws = 0;
            int start = 0;
            while (start < count)
            {
                int end = start + 1;
                int regionId = _patches[start].RegionId;
                while (end < count && _patches[end].RegionId == regionId) end++;
                Bounds regionBounds=new Bounds(_patches[start].Center,new Vector3(_patches[start].SizeXZ.x,4,_patches[start].SizeXZ.y));
                for(int i=start+1;i<end;i++)regionBounds.Encapsulate(new Bounds(_patches[i].Center,new Vector3(_patches[i].SizeXZ.x,4,_patches[i].SizeXZ.y)));
                if(field!=null && !field.PresentationView.Intersects(regionBounds)){start=end;continue;}
                RegionView view = FindView(regionId, count);
                if (view == null) { start = end; continue; }
                int patchCount = end - start;
                // 变深湖底跨越一层时可能换岸线但 Patch 数恰好不变，必须比较
                // 独立的拓扑版本；层内水位变化只移动矩阵，不重传 Mesh 顶点。
                if (view.RegionId != regionId || view.PatchCount != patchCount
                    || view.TopologyVersion != _patches[start].TopologyVersion)
                    BuildMesh(view, start, end);
                view.LastSeenFrame = Time.frameCount;
                float height = _patches[start].Center.y;
                float depth=1f;
                float floor=0f;
                bool shallow=view.RoundedShallow && TryGetGroundLevel(_patches[start],out floor);
                if(shallow)
                {
                    depth=LiquidPuddleShape.BodyHeight((height-floor)/_source.CellSize,
                        _minimumPuddleDepth/_source.CellSize)*_source.CellSize;
                    height=floor+depth;
                    depth/=_source.CellSize;
                }
                // UV0/UV2 保存固定轮廓和偏导，Vertex Shader 按同一数量公式重组高度及法线。
                // 接近满层时圆冠回落，整个过程只改每次 Draw 的值，不扫描/重建整湖。
                _quantityProperties.SetVector(StableShapeId,shallow
                    ?new Vector4(depth,LiquidPuddleShape.CrownHeight(depth),_source.CellSize,1f):Vector4.zero);
                _renderParams.matProps=_quantityProperties;
                Matrix4x4 matrix = Matrix4x4.TRS(new Vector3(view.OriginX,height,view.OriginZ),
                    Quaternion.identity,Vector3.one);
                Bounds localBounds = view.Mesh.bounds;
                Vector3 worldCenter = matrix.MultiplyPoint3x4(localBounds.center);
                Vector3 worldSize = localBounds.size;
                worldSize.y = 1f;
                if(shallow)
                {
                    float peak=(depth+LiquidPuddleShape.CrownHeight(depth))*_source.CellSize;
                    worldCenter.y=floor+peak*.5f;
                    // Vertex Shader 会改变 Y，CPU Mesh.bounds 仍是规范化轮廓的旧高度。
                    // 按数量重新给出 Culling 包围盒，额外余量覆盖最大 .08m Shader 起伏。
                    worldSize.y=peak+.2f;
                }
                _renderParams.worldBounds = new Bounds(worldCenter, worldSize);
                view.Visible=true;view.LastMatrix=matrix;view.LastBounds=_renderParams.worldBounds;
                view.LastShape=shallow?new Vector4(depth,LiquidPuddleShape.CrownHeight(depth),_source.CellSize,1f):Vector4.zero;
                Graphics.RenderMesh(in _renderParams, view.Mesh, 0, matrix);
                _submittedDraws++;
                start = end;
            }
        }

        private void DrawFrozenSurface()
        {
            _submittedDraws=0;
            for(int i=0;i<_views.Length;i++)
            {
                RegionView view=_views[i];if(!view.Visible)continue;
                _quantityProperties.SetVector(StableShapeId,view.LastShape);
                _renderParams.matProps=_quantityProperties;_renderParams.worldBounds=view.LastBounds;
                Graphics.RenderMesh(in _renderParams,view.Mesh,0,view.LastMatrix);_submittedDraws++;
            }
        }

        private RegionView FindView(int regionId, int patchCount)
        {
            for (int i = 0; i < _views.Length; i++)
                if (_views[i].RegionId == regionId) return _views[i];
            for (int i = 0; i < _views.Length; i++)
                if (_views[i].RegionId < 0) return _views[i];
            // 新区域只能淘汰本次快照已不存在的 Mesh，不能误删稍后还会画的区域。
            for (int i = 0; i < _views.Length; i++)
                if (_views[i].LastSeenFrame != Time.frameCount
                    && !IsPresentInSnapshot(_views[i].RegionId, patchCount))
                    return _views[i];
            return null;
        }

        private bool IsPresentInSnapshot(int regionId, int count)
        {
            for (int i = 0; i < count; i++)
                if (_patches[i].RegionId == regionId) return true;
            return false;
        }

        private void BuildMesh(RegionView view, int start, int end)
        {
            _vertices.Clear();
            _normals.Clear();
            _uvs.Clear();
            _shapeGradients.Clear();
            _triangles.Clear();
            view.OriginX = _patches[start].Center.x;
            view.OriginZ = _patches[start].Center.z;
            bool shallow=TryGetGroundLevel(_patches[start],out _);
            bool rounded=(end-start>1 || shallow) && _shore.Build(_patches,start,end,_source.Origin,
                _source.CellSize,view.OriginX,view.OriginZ,_vertices,_normals,_uvs,_triangles,
                shallow?1f:0f,_shapeGradients);
            view.RoundedShallow=rounded && shallow;
            if(!rounded) {_vertices.Clear();_normals.Clear();_uvs.Clear();_triangles.Clear();}
            // 单矩形湖盆保留直岸与四顶点路径；不规则岸线用连续覆盖场，超过几何预算
            // 时仍保留完整水体行段，不能为了显示曲线而漏画湖泊。
            for (int i = start; !rounded && i < end; i++)
            {
                StableLiquidSurfacePatch patch = _patches[i];
                float left = patch.Center.x - patch.SizeXZ.x * .5f - view.OriginX;
                float right = patch.Center.x + patch.SizeXZ.x * .5f - view.OriginX;
                float bottom = patch.Center.z - patch.SizeXZ.y * .5f - view.OriginZ;
                float top = patch.Center.z + patch.SizeXZ.y * .5f - view.OriginZ;
                int first = _vertices.Count;
                // 顶点位于湖泊局部 XZ 平面；矩阵仅补 World Space 原点与当前水位。
                // Shader 用 World Space 法线与位置取波纹，相邻行段共享视觉坐标。
                _vertices.Add(new Vector3(left, 0f, bottom));
                _vertices.Add(new Vector3(left, 0f, top));
                _vertices.Add(new Vector3(right, 0f, top));
                _vertices.Add(new Vector3(right, 0f, bottom));
                _normals.Add(Vector3.up);
                _normals.Add(Vector3.up);
                _normals.Add(Vector3.up);
                _normals.Add(Vector3.up);
                _uvs.Add(new Vector2(left, bottom));
                _uvs.Add(new Vector2(left, top));
                _uvs.Add(new Vector2(right, top));
                _uvs.Add(new Vector2(right, bottom));
                _triangles.Add(first);
                _triangles.Add(first + 1);
                _triangles.Add(first + 2);
                _triangles.Add(first);
                _triangles.Add(first + 2);
                _triangles.Add(first + 3);
            }
            view.Mesh.Clear();
            view.Mesh.SetVertices(_vertices);
            view.Mesh.SetNormals(_normals);
            view.Mesh.SetUVs(0, _uvs);
            if(rounded)view.Mesh.SetUVs(2,_shapeGradients);
            view.Mesh.SetTriangles(_triangles, 0, true);
            view.RegionId = _patches[start].RegionId;
            view.PatchCount = end - start;
            view.TopologyVersion = _patches[start].TopologyVersion;
            _topologyRebuilds++;
        }

        private bool TryGetGroundLevel(StableLiquidSurfacePatch patch,out float floor)
        {
            Vector3 point=(patch.Center-_source.Origin)/_source.CellSize;
            // 恰好满一格的自由面属于下方液层；小偏移避免把它误判成深水。
            point.y-=.001f;
            Vector3Int cell=Vector3Int.FloorToInt(point);
            floor=_source.Origin.y+cell.y*_source.CellSize;
            return _source.IsSolidForSurface(cell+Vector3Int.down);
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_gcSampledFrames > 0)
                GameLog.Info($"Grid Stable Surface {_material}: GC {_gcAllocationFrames}/{_gcSampledFrames} frames, "
                    + $"total {_gcAllocatedBytes} B, max {_gcMaximumFrameBytes} B/frame.", "Rendering");
#endif
            if (_views != null)
                for (int i = 0; i < _views.Length; i++)
                    if (_views[i].Mesh != null) Destroy(_views[i].Mesh);
            if (_materialInstance != null) Destroy(_materialInstance);
        }
    }
}
