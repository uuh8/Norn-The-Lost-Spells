using System;
using Game.Core;
using Game.ElementField;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Game.Materials;

namespace Game.Rendering
{
    /// <summary>开发期只读诊断：指出 Fluid GPU Surface 从哪一个边界开始没有产出。</summary>
    public enum LiquidSurfaceDiagnosticStage : byte
    {
        WaitingForSnapshot,
        WaitingForResources,
        NoSurfaceTriangles,
        IndirectArgsMissing,
        DrawReady
    }

    /// <summary>
    /// 把 GPU Counter 快照转换成稳定、可测试的诊断结论；Renderer 只负责异步取得数据。
    /// </summary>
    internal static class LiquidSurfaceDiagnosticClassifier
    {
        internal static LiquidSurfaceDiagnosticStage Classify(
            bool hasSnapshot,
            bool resourcesReady,
            uint triangleCount,
            uint indirectVertexCount)
        {
            if (!hasSnapshot)
                return LiquidSurfaceDiagnosticStage.WaitingForSnapshot;
            if (!resourcesReady)
                return LiquidSurfaceDiagnosticStage.WaitingForResources;
            if (triangleCount == 0u)
                return LiquidSurfaceDiagnosticStage.NoSurfaceTriangles;
            if (indirectVertexCount == 0u)
                return LiquidSurfaceDiagnosticStage.IndirectArgsMissing;
            return LiquidSurfaceDiagnosticStage.DrawReady;
        }
    }

    /// <summary>第一组三角形的只读诊断，用来区分非法顶点、Bounds Culling 与 Winding 问题。</summary>
    public enum LiquidSurfaceGeometryDiagnosticStage : byte
    {
        WaitingForTriangleSample,
        InvalidTriangle,
        OutsideDrawBounds,
        WindingOpposesNormals,
        GeometryReady
    }

    internal static class LiquidSurfaceGeometryDiagnosticClassifier
    {
        internal static LiquidSurfaceGeometryDiagnosticStage Classify(
            bool hasTriangleSample,
            in Bounds drawBounds,
            Vector3 position0,
            Vector3 position1,
            Vector3 position2,
            Vector3 normal0,
            Vector3 normal1,
            Vector3 normal2)
        {
            if (!hasTriangleSample)
                return LiquidSurfaceGeometryDiagnosticStage.WaitingForTriangleSample;
            if (!IsFinite(position0) || !IsFinite(position1) || !IsFinite(position2)
                || !IsFinite(normal0) || !IsFinite(normal1) || !IsFinite(normal2))
            {
                return LiquidSurfaceGeometryDiagnosticStage.InvalidTriangle;
            }

            Bounds expandedBounds = drawBounds;
            expandedBounds.Expand(0.02f);
            if (!expandedBounds.Contains(position0)
                || !expandedBounds.Contains(position1)
                || !expandedBounds.Contains(position2))
            {
                return LiquidSurfaceGeometryDiagnosticStage.OutsideDrawBounds;
            }

            Vector3 geometricNormal = Vector3.Cross(position1 - position0, position2 - position0);
            Vector3 vertexNormalSum = normal0 + normal1 + normal2;
            if (geometricNormal.sqrMagnitude <= 1e-12f)
                return LiquidSurfaceGeometryDiagnosticStage.InvalidTriangle;
            if (vertexNormalSum.sqrMagnitude > 1e-8f
                && Vector3.Dot(geometricNormal, vertexNormalSum) < 0f)
            {
                return LiquidSurfaceGeometryDiagnosticStage.WindingOpposesNormals;
            }

            return LiquidSurfaceGeometryDiagnosticStage.GeometryReady;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }

    /// <summary>把可抛异常的 Profile snapshot 边界转成显式失败，供 Renderer 与 focused test 共用。</summary>
    internal static class LiquidRenderSettingsFactory
    {
        internal static bool TryCreate(
            LiquidRenderProfile profile,
            out LiquidRenderSettings settings,
            out string error)
        {
            try
            {
                settings = profile.CreateSettings();
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                settings = default;
                error = exception.Message;
                return false;
            }
        }
    }

    /// <summary>把多材质 Surface 的昂贵重建稳定错开；Draw 仍可每帧复用上一份 Triangle Buffer。</summary>
    internal static class LiquidSurfaceRefreshPlanner
    {
        private const uint RenderableMask = (1u << (int)MaterialId.Water)
            | (1u << (int)MaterialId.Poison)
            | (1u << (int)MaterialId.Sticky);

        internal static bool ShouldRebuild(bool hasBuilt, uint lastVersion, uint currentVersion,
            MaterialId material, uint presenceMask, int frame)
        {
            uint materialBit = 1u << (int)material;
            uint active = presenceMask & RenderableMask;
            if ((active & materialBit) == 0u) return false;
            if (!hasBuilt) return true;
            if (lastVersion == currentVersion) return false;
            int count = CountBits(active);
            int rank = CountBits(active & (materialBit - 1u));
            return count <= 1 || (uint)frame % (uint)count == (uint)rank;
        }

        private static int CountBits(uint value)
        {
            int count = 0;
            while (value != 0u) { value &= value - 1u; count++; }
            return count;
        }
    }

    /// <summary>
    /// Presentation-only GPU 编排器：读取 IFluidGpuSource 的 committed particle snapshot，依次提交
    /// Density Field、Marching Cubes、Indirect Args 和 Procedural Draw。它绝不写回 Simulation Truth。
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class GpuLiquidSurfaceRenderer : MonoBehaviour
    {
        public const int SurfaceVertexStride = 32;
        public const int SurfaceTriangleStride = 96;

        private const uint RequiredFluidLayoutVersion = FluidGpuLayout.LayoutVersion;
        private const int DensityThreadGroupSize = 4;
        private const int MarchingCubesThreadGroupSize = 4;

        private static readonly ProfilerMarker DensityMarker =
            new ProfilerMarker("GpuFluid.Surface.Density");
        private static readonly ProfilerMarker AnisotropyMarker =
            new ProfilerMarker("GpuFluid.Surface.Anisotropy");
        private static readonly ProfilerMarker MarchingCubesMarker =
            new ProfilerMarker("GpuFluid.Surface.MarchingCubes");
        private static readonly ProfilerMarker DrawMarker =
            new ProfilerMarker("GpuFluid.Surface.IndirectDraw");

        private static readonly int PredictedPositionsId = Shader.PropertyToID("_PredictedPositions");
        private static readonly int ParticleMetadataId = Shader.PropertyToID("_ParticleMetadata");
        private static readonly int SpatialEntriesId = Shader.PropertyToID("_SpatialEntries");
        private static readonly int CellRangesId = Shader.PropertyToID("_CellRanges");
        private static readonly int SpatialCellsId = Shader.PropertyToID("_SpatialCells");
        private static readonly int DensityTextureId = Shader.PropertyToID("_DensityTexture");
        private static readonly int SurfaceTrianglesId = Shader.PropertyToID("_FluidSurfaceTriangles");
        private static readonly int DebugForceOpaqueFragmentId =
            Shader.PropertyToID("_DebugForceOpaqueFragment");
        private static readonly int TriangleCounterId = Shader.PropertyToID("_TriangleCounter");
        private static readonly int OverflowCounterId = Shader.PropertyToID("_OverflowCounter");
        private static readonly int IndirectArgumentsId = Shader.PropertyToID("_IndirectArguments");
        private static readonly int ParticleCapacityId = Shader.PropertyToID("_ParticleCapacity");
        private static readonly int HashTableCapacityId = Shader.PropertyToID("_HashTableCapacity");
        private static readonly int SmoothingRadiusId = Shader.PropertyToID("_SmoothingRadius");
        private static readonly int ParticleMassId = Shader.PropertyToID("_ParticleMass");
        private static readonly int TargetMaterialId = Shader.PropertyToID("_TargetMaterialId");
        private static readonly int LiquidMaterialParametersId = Shader.PropertyToID("_LiquidMaterialParameters");
        private static readonly int GridResolutionXId = Shader.PropertyToID("_GridResolutionX");
        private static readonly int GridResolutionYId = Shader.PropertyToID("_GridResolutionY");
        private static readonly int GridResolutionZId = Shader.PropertyToID("_GridResolutionZ");
        private static readonly int WorldOriginId = Shader.PropertyToID("_WorldOrigin");
        private static readonly int VoxelSizeId = Shader.PropertyToID("_VoxelSize");
        private static readonly int IsoLevelId = Shader.PropertyToID("_IsoLevel");
        private static readonly int MaximumTriangleCountId = Shader.PropertyToID("_MaximumTriangleCount");
        private static readonly int AnisotropyTransformsId = Shader.PropertyToID("_AnisotropyTransforms");
        private static readonly int AnisotropyCountersId = Shader.PropertyToID("_AnisotropyCounters");
        private static readonly int SurfaceSupportsId = Shader.PropertyToID("_SurfaceSupports");
        private static readonly int NeighborThresholdId = Shader.PropertyToID("_NeighborThreshold");
        private static readonly int CrownEdgeNeighborCountId =
            Shader.PropertyToID("_CrownEdgeNeighborCount");
        private static readonly int CrownInteriorNeighborCountId =
            Shader.PropertyToID("_CrownInteriorNeighborCount");
        private static readonly int MinimumAnisotropyScaleId = Shader.PropertyToID("_MinimumAnisotropyScale");
        private static readonly int MaximumAnisotropyScaleId = Shader.PropertyToID("_MaximumAnisotropyScale");
        private static readonly int MaximumAnisotropyRatioId = Shader.PropertyToID("_MaximumAnisotropyRatio");
        private static readonly int DensityCellRadiusId = Shader.PropertyToID("_DensityCellRadius");
        private static readonly int UseStylizedCrownId = Shader.PropertyToID("_UseStylizedCrown");
        private static readonly int CrownHeightRatioId = Shader.PropertyToID("_CrownHeightRatio");
        private static readonly int CrownFalloffId = Shader.PropertyToID("_CrownFalloff");

        [Tooltip("应引用实现 IFluidGpuSource 的 Runtime；Rendering 只能读取其 GPU Snapshot。")]
        [SerializeField] private MonoBehaviour _fluidSourceComponent;
        [Tooltip("提供初始化时已确定的 effective Water mode；用于在分配 GPU Surface 资源前互斥 Legacy/PBF Renderer。")]
        [SerializeField] private ElementWorldRuntime _worldRuntime;
        [SerializeField] private LiquidRenderProfile _profile;
        [SerializeField] private ComputeShader _densityCompute;
        [SerializeField] private ComputeShader _anisotropyCompute;
        [SerializeField] private ComputeShader _marchingCubesCompute;
        [SerializeField] private Material _material;
        [Tooltip("这个 Presentation Component 只重建一种 Material；多个组件可读取同一个 GPU Pool。")]
        [SerializeField] private MaterialId _targetMaterial = MaterialId.Water;
        private bool _hasBuiltSurface;
        private uint _lastBuiltSimulationVersion;
        private Bounds _lastBuiltBounds;
        private Camera _viewCamera;

        private Bounds ResolveSurfaceBounds(Bounds simulationBounds)
        {
            if (_worldRuntime == null || !_worldRuntime.ActivityExpansionEnabled || _viewCamera == null)
                return simulationBounds;
            // 固定尺寸 Surface 跟随相机观察的水平位置，保持 voxel 尺寸；不会将多个远处 AOI 包进同一栅格。
            Ray ray = _viewCamera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
            Plane plane = new Plane(Vector3.up, new Vector3(0, simulationBounds.center.y, 0));
            Vector3 center = plane.Raycast(ray, out float distance) && distance < 64f
                ? ray.GetPoint(distance) : ray.GetPoint(8f);
            center.x = Mathf.Round(center.x * 2f) * .5f;
            center.z = Mathf.Round(center.z * 2f) * .5f;
            center.y = simulationBounds.center.y;
            return new Bounds(center, simulationBounds.size);
        }

        [Header("Development Fragment Isolation")]
        [Tooltip("临时跳过复杂 Fragment 光照、深度和波纹，只输出材质浅色且完全不透明；用于定位不可见发生在 Rasterization 前还是 Fragment 内。")]
        [SerializeField] private bool _debugForceOpaqueFragment = false;

        [Header("Development Counters (Async <= 2Hz)")]
        [Tooltip("GPU Surface 当前通过到哪一层；计数器轮询一圈最多约需 1.5 秒。")]
        [SerializeField] private LiquidSurfaceDiagnosticStage _debugStage;
        [SerializeField] private bool _debugHasSnapshot;
        [SerializeField] private bool _debugResourcesReady;
        [Tooltip("当前单层 Surface 使用的实际 Density Grid 分辨率。")]
        [SerializeField] private Vector3Int _debugGridResolution;
        [Tooltip("Active Bounds 被实际 Grid 分辨率整除后的世界空间 Voxel 尺寸。")]
        [SerializeField] private Vector3 _debugVoxelSize;
        [Tooltip("Marching Cubes 本帧实际写出的三角形数。")]
        [SerializeField] private uint _debugTriangleCount;
        [Tooltip("Indirect Draw Args 中的顶点数；正常应等于 Triangle Count * 3。")]
        [SerializeField] private uint _debugIndirectVertexCount;
        [Tooltip("Indirect Draw Args 中的实例数；当前单 Surface Draw 必须为 1。")]
        [SerializeField] private uint _debugIndirectInstanceCount;
        [SerializeField] private uint _debugIndirectStartVertex;
        [SerializeField] private uint _debugIndirectStartInstance;
        [Tooltip("超过 Triangle Buffer 容量而被丢弃的三角形数；正常应为 0。")]
        [SerializeField] private uint _debugTriangleOverflow;
        [Header("Development Geometry Sample (Async <= 0.5Hz)")]
        [SerializeField] private LiquidSurfaceGeometryDiagnosticStage _debugGeometryStage;
        [SerializeField] private Vector3 _debugDrawBoundsCenter;
        [SerializeField] private Vector3 _debugDrawBoundsSize;
        [SerializeField] private Vector3 _debugTrianglePosition0;
        [SerializeField] private Vector3 _debugTrianglePosition1;
        [SerializeField] private Vector3 _debugTrianglePosition2;
        [SerializeField] private Vector3 _debugTriangleNormal0;
        [SerializeField] private Vector3 _debugTriangleNormal1;
        [SerializeField] private Vector3 _debugTriangleNormal2;
        [SerializeField] private uint _debugSurfaceRebuildCount;
        [SerializeField] private uint _debugDrawCount;
        [Tooltip("诊断轮询曾观察到的最大三角形数；退出瞬间为0时仍可证明Measure期间是否产出过Surface。")]
        [SerializeField] private uint _debugMaximumTriangleCount;
        [SerializeField] private uint _debugPositiveTriangleReadbackCount;
        [SerializeField] private uint _debugDrawReadyObservationCount;
        [SerializeField] private uint _debugGeometryReadyObservationCount;

        private IFluidGpuSource _fluidSource;
        private LiquidRenderSettings _settings;
        private FluidSurfaceGpuResources _resources;
        private MaterialPropertyBlock _materialPropertyBlock;
        private RenderParams _renderParams;
        private int _densityKernel;
        private int _clearAnisotropyCountersKernel;
        private int _gatherAnisotropyMeanKernel;
        private int _buildAnisotropyTransformKernel;
        private int _gatherSurfaceSupportKernel;
        private int _clearSurfaceCountersKernel;
        private int _extractSurfaceKernel;
        private int _buildIndirectArgsKernel;
        private bool _hasReportedFailure;
        private bool _hasStarted;
        private FluidAnisotropyUpdateSchedule _anisotropySchedule;
        private AsyncGPUReadbackRequest _debugCounterRequest;
        private Action<AsyncGPUReadbackRequest> _debugCounterCallback;
        private float _debugCounterElapsed;
        private bool _debugCounterOutstanding;
        private bool _developmentDiagnosticsEnabled = true;
        private bool _resourceReleasePending;
        private bool _debugHasTriangleSample;
        private DebugCounterReadbackKind _debugNextReadbackKind;
        private DebugCounterReadbackKind _debugOutstandingReadbackKind;

        private enum DebugCounterReadbackKind : byte
        {
            TriangleCount,
            IndirectVertexCount,
            TriangleSample,
            TriangleOverflow
        }

        // 专用性能场景只读这些计数；Production Gameplay 不依赖诊断组件，也不获得写入入口。
        public MaterialId TargetMaterial => _targetMaterial;
        public LiquidSurfaceDiagnosticStage DiagnosticStage => _debugStage;
        public LiquidSurfaceGeometryDiagnosticStage GeometryDiagnosticStage => _debugGeometryStage;
        public uint DiagnosticTriangleCount => _debugTriangleCount;
        public uint DiagnosticTriangleOverflow => _debugTriangleOverflow;
        public uint DiagnosticSurfaceRebuildCount => _debugSurfaceRebuildCount;
        public uint DiagnosticDrawCount => _debugDrawCount;
        public uint DiagnosticMaximumTriangleCount => _debugMaximumTriangleCount;
        public uint DiagnosticPositiveTriangleReadbackCount => _debugPositiveTriangleReadbackCount;
        public uint DiagnosticDrawReadyObservationCount => _debugDrawReadyObservationCount;
        public uint DiagnosticGeometryReadyObservationCount => _debugGeometryReadyObservationCount;
        public Vector3Int DiagnosticGridResolution => _debugGridResolution;

        /// <summary>
        /// 专用Development性能实验可关闭AsyncGPUReadback，避免观察器改变被测Render Thread。
        /// 必须在Renderer首次LateUpdate前调用；生产Gameplay不会调用此入口。
        /// </summary>
        public void SetDevelopmentDiagnosticsEnabled(bool value)
        {
            _developmentDiagnosticsEnabled = value;
        }

        private void Awake()
        {
            _debugCounterCallback = OnDebugCounterReadbackCompleted;
            if (_worldRuntime == null)
                _worldRuntime = GetComponentInParent<ElementWorldRuntime>();
            if (_worldRuntime == null)
            {
                DisableWithFailure("GpuLiquidSurfaceRenderer 缺少 ElementWorldRuntime mode source。");
                return;
            }
            if (_targetMaterial == MaterialId.Empty)
            {
                DisableWithFailure("GpuLiquidSurfaceRenderer Target Material 不能是 Empty。");
                return;
            }
            if (!LiquidBackendAvailabilityPolicy.ShouldRender(
                    _targetMaterial,
                    _worldRuntime.MaterialRoutes))
            {
                // effective Legacy（包括 unsupported Graphics fallback）由 ElementWorldRuntime 统一诊断；
                // 这里在任何 GPU 资源分配前静默退出，避免同一 fallback 原因重复 Warn。
                enabled = false;
                return;
            }

            _fluidSource = _fluidSourceComponent as IFluidGpuSource;
            if (_fluidSource == null
                || _profile == null
                || _densityCompute == null
                || _marchingCubesCompute == null
                || _material == null)
            {
                DisableWithFailure("GpuLiquidSurfaceRenderer 缺少 Source、Profile、Compute 或 Material。");
                return;
            }

            if (!IsSupportedGraphicsDevice())
            {
                DisableWithFailure("GPU Liquid Surface 首版只支持具备 Compute/Indirect/R32 的 D3D11 或 D3D12。");
                return;
            }

            // CreateSettings 只能调用一次且必须在异常边界内；非法序列化 Profile 应禁用 Renderer，不能让 Awake 抛出。
            if (!LiquidRenderSettingsFactory.TryCreate(_profile, out _settings, out string settingsError))
            {
                DisableWithFailure($"GpuLiquidSurfaceRenderer Profile 无效：{settingsError}");
                return;
            }
            bool requiresSurfaceNeighborhood =
                _settings.UseAnisotropy || _settings.UseStylizedCrown;
            if (requiresSurfaceNeighborhood && _anisotropyCompute == null)
            {
                DisableWithFailure("GpuLiquidSurfaceRenderer 启用 Anisotropy/Crown 时缺少邻域 ComputeShader。");
                return;
            }
            if (!_densityCompute.HasKernel("GatherIsotropicDensity")
                || (_settings.UseAnisotropy && !_densityCompute.HasKernel("GatherAnisotropicDensity"))
                || (_settings.UseAnisotropy
                    && (!_anisotropyCompute.HasKernel("ClearAnisotropyCounters")
                        || !_anisotropyCompute.HasKernel("GatherAnisotropyMean")
                        || !_anisotropyCompute.HasKernel("BuildAnisotropyTransform")))
                || (_settings.UseStylizedCrown
                    && !_anisotropyCompute.HasKernel("GatherSurfaceSupport"))
                || !_marchingCubesCompute.HasKernel("ClearSurfaceCounters")
                || !_marchingCubesCompute.HasKernel("ExtractSurface")
                || !_marchingCubesCompute.HasKernel("BuildIndirectArgs"))
            {
                DisableWithFailure("GPU Liquid Surface 的 Compute Kernel Contract 不完整。");
                return;
            }

            _densityKernel = _densityCompute.FindKernel(
                _settings.UseAnisotropy ? "GatherAnisotropicDensity" : "GatherIsotropicDensity");
            _anisotropySchedule = new FluidAnisotropyUpdateSchedule(
                _settings.AnisotropyUpdateIntervalFrames);
            if (_settings.UseAnisotropy)
            {
                _clearAnisotropyCountersKernel = _anisotropyCompute.FindKernel("ClearAnisotropyCounters");
                _gatherAnisotropyMeanKernel = _anisotropyCompute.FindKernel("GatherAnisotropyMean");
                _buildAnisotropyTransformKernel = _anisotropyCompute.FindKernel("BuildAnisotropyTransform");
            }
            if (_settings.UseStylizedCrown)
                _gatherSurfaceSupportKernel = _anisotropyCompute.FindKernel("GatherSurfaceSupport");
            _clearSurfaceCountersKernel = _marchingCubesCompute.FindKernel("ClearSurfaceCounters");
            _extractSurfaceKernel = _marchingCubesCompute.FindKernel("ExtractSurface");
            _buildIndirectArgsKernel = _marchingCubesCompute.FindKernel("BuildIndirectArgs");

            // MaterialPropertyBlock 是初始化时创建并长期复用的 wrapper；LateUpdate 只修改其内容。
            _materialPropertyBlock = new MaterialPropertyBlock();
            _renderParams = new RenderParams(_material)
            {
                matProps = _materialPropertyBlock,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(Vector3.zero, Vector3.one)
            };
        }

        private void OnEnable()
        {
            _resourceReleasePending = false;
            // 再次启用属于 Unity 生命周期初始化边界，不是每帧热路径；资源只在这里重建一次。
            if (_hasStarted && _resources == null && _fluidSource != null)
                InitializeSurfaceResources();
        }

        private void Start()
        {
            _viewCamera = Camera.main;
            _hasStarted = true;
            InitializeSurfaceResources();
        }

        private void LateUpdate()
        {
            if (_fluidSource == null
                || !_fluidSource.TryGetGpuSnapshot(out FluidGpuSnapshot snapshot)
                || snapshot.LayoutVersion != RequiredFluidLayoutVersion)
            {
                _debugHasSnapshot = false;
                RefreshDebugStage();
                return;
            }
            _debugHasSnapshot = true;

            // 生产写入和转换在 CPU 提交时发布单调 Presence Mask。确定从未出现过的材质没有任何
            // Density/Anisotropy/Marching Cubes 结果，提前返回可省掉整套全 Bounds GPU 工作。
            if (!FluidMaterialPresenceMask.MayContain(snapshot.MaterialPresenceMask, (uint)_targetMaterial))
            {
                _debugTriangleCount = 0u;
                _debugIndirectVertexCount = 0u;
                _debugStage = LiquidSurfaceDiagnosticStage.NoSurfaceTriangles;
                return;
            }

            if (_resources == null)
            {
                _debugResourcesReady = false;
                RefreshDebugStage();
                return;
            }
            _debugResourcesReady = true;

            FluidSurfaceGridSettings grid;
            try
            {
                // LOD 前的单 Surface 语义：调用者给出的完整 Active Bounds 直接进入 Density Grid，
                // 不再围绕角色裁剪 Near Box，也没有 Mid/Far 表示切换。
                grid = FluidSurfaceGridPlanner.Plan(ResolveSurfaceBounds(snapshot.ActiveBounds), in _settings);
                _debugGridResolution = grid.Resolution;
                _debugVoxelSize = grid.VoxelSize;
                _debugDrawBoundsCenter = grid.WorldBounds.center;
                _debugDrawBoundsSize = grid.WorldBounds.size;
                if (_resources.ParticleCapacity != snapshot.ParticleCapacity)
                {
                    DisableWithFailure(
                        $"Fluid Particle Capacity 在运行中由 {_resources.ParticleCapacity} "
                        + $"变为 {snapshot.ParticleCapacity}；为避免热路径重分配已禁用 Renderer。");
                    return;
                }
                if (!_resources.TryUpdateGrid(in grid))
                {
                    DisableWithFailure(
                        $"Fluid Surface Resolution 在运行中由 {_resources.GridSettings.Resolution} "
                        + $"变为 {grid.Resolution}；为避免热路径重分配已禁用 Renderer。");
                    return;
                }
            }
            catch (Exception exception)
            {
                // 只有非法 Bounds/Contract 才进入失败分支；正常 LateUpdate 不构造 message/array/closure。
                DisableWithFailure(exception.Message);
                return;
            }

            bool boundsChanged = !_hasBuiltSurface || _lastBuiltBounds != grid.WorldBounds;
            bool rebuild = boundsChanged || LiquidSurfaceRefreshPlanner.ShouldRebuild(
                _hasBuiltSurface, _lastBuiltSimulationVersion, snapshot.SimulationVersion,
                _targetMaterial, snapshot.MaterialPresenceMask, Time.frameCount);
            if (rebuild && (_settings.UseAnisotropy || _settings.UseStylizedCrown)
                && _anisotropySchedule.ShouldUpdateAndAdvance(snapshot.TopologyVersion))
            {
                DispatchSurfaceNeighborhood(in snapshot);
            }
            if (rebuild)
            {
                DispatchDensity(in snapshot, in grid, _resources);
                DispatchMarchingCubes(in grid, _resources);
                _hasBuiltSurface = true;
                _lastBuiltSimulationVersion = snapshot.SimulationVersion;
                _lastBuiltBounds = grid.WorldBounds;
                if (_debugSurfaceRebuildCount < uint.MaxValue)
                    _debugSurfaceRebuildCount++;
            }
            DrawSurface(in grid, _resources);
            if (_debugDrawCount < uint.MaxValue)
                _debugDrawCount++;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_developmentDiagnosticsEnabled)
                ScheduleDebugCounterReadback(Time.unscaledDeltaTime);
#endif
        }

        private void InitializeSurfaceResources()
        {
            if (_resources != null || _fluidSource == null)
                return;
            if (!_fluidSource.TryGetGpuSnapshot(out FluidGpuSnapshot snapshot)
                || snapshot.LayoutVersion != RequiredFluidLayoutVersion)
            {
                DisableWithFailure("GpuLiquidSurfaceRenderer 初始化时未取得兼容的 Fluid GPU Snapshot。");
                return;
            }

            try
            {
                FluidSurfaceGridSettings grid = FluidSurfaceGridPlanner.Plan(
                    snapshot.ActiveBounds,
                    in _settings);
                _resources = new FluidSurfaceGpuResources(in grid, snapshot.ParticleCapacity);
                _debugHasSnapshot = true;
                _debugResourcesReady = true;
                _debugGridResolution = grid.Resolution;
                _debugVoxelSize = grid.VoxelSize;
                _debugDrawBoundsCenter = grid.WorldBounds.center;
                _debugDrawBoundsSize = grid.WorldBounds.size;
                _debugHasTriangleSample = false;
                RefreshDebugGeometryStage();
                RefreshDebugStage();
                _anisotropySchedule.Reset();
            }
            catch (Exception exception)
            {
                DisableWithFailure(exception.Message);
            }
        }

        private void OnDisable()
        {
            _resourceReleasePending = true;
            DisposeResources();
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_debugCounterOutstanding)
            {
                _debugCounterRequest.WaitForCompletion();
                OnDebugCounterReadbackCompleted(_debugCounterRequest);
            }
#endif
            _resourceReleasePending = true;
            DisposeResources();
        }

        private void ScheduleDebugCounterReadback(float deltaTime)
        {
            if (_resources == null || _debugCounterOutstanding)
                return;
            _debugCounterElapsed += deltaTime;
            if (_debugCounterElapsed < 0.5f)
                return;
            _debugCounterElapsed = 0f;
            try
            {
                GraphicsBuffer sourceBuffer;
                int readbackSize = 0;
                switch (_debugNextReadbackKind)
                {
                    case DebugCounterReadbackKind.TriangleCount:
                        sourceBuffer = _resources.TriangleCounter;
                        break;
                    case DebugCounterReadbackKind.IndirectVertexCount:
                        sourceBuffer = _resources.IndirectArguments;
                        break;
                    case DebugCounterReadbackKind.TriangleSample:
                        sourceBuffer = _resources.TriangleBuffer;
                        // 只取第一个 96-byte Triangle，避免开发诊断回读整个大容量 Buffer。
                        readbackSize = SurfaceTriangleStride;
                        break;
                    default:
                        sourceBuffer = _resources.OverflowCounter;
                        break;
                }

                _debugOutstandingReadbackKind = _debugNextReadbackKind;
                _debugCounterOutstanding = true;
                _debugCounterRequest = readbackSize > 0
                    ? AsyncGPUReadback.Request(
                        sourceBuffer,
                        readbackSize,
                        0,
                        _debugCounterCallback)
                    : AsyncGPUReadback.Request(sourceBuffer, _debugCounterCallback);
                _debugNextReadbackKind = _debugNextReadbackKind switch
                {
                    DebugCounterReadbackKind.TriangleCount =>
                        DebugCounterReadbackKind.IndirectVertexCount,
                    DebugCounterReadbackKind.IndirectVertexCount =>
                        DebugCounterReadbackKind.TriangleSample,
                    DebugCounterReadbackKind.TriangleSample =>
                        DebugCounterReadbackKind.TriangleOverflow,
                    _ => DebugCounterReadbackKind.TriangleCount
                };
            }
            catch (Exception)
            {
                _debugCounterOutstanding = false;
            }
        }

        private void OnDebugCounterReadbackCompleted(AsyncGPUReadbackRequest request)
        {
            if (!_debugCounterOutstanding)
                return;
            _debugCounterOutstanding = false;
            if (!request.hasError)
            {
                if (_debugOutstandingReadbackKind == DebugCounterReadbackKind.TriangleSample)
                {
                    var packedTriangle = request.GetData<Vector4>();
                    if (packedTriangle.Length >= 6)
                    {
                        _debugTrianglePosition0 = packedTriangle[0];
                        _debugTriangleNormal0 = packedTriangle[1];
                        _debugTrianglePosition1 = packedTriangle[2];
                        _debugTriangleNormal1 = packedTriangle[3];
                        _debugTrianglePosition2 = packedTriangle[4];
                        _debugTriangleNormal2 = packedTriangle[5];
                        _debugHasTriangleSample = true;
                        RefreshDebugGeometryStage();
                    }
                }
                else
                {
                    var values = request.GetData<uint>();
                    if (values.Length > 0)
                    {
                        switch (_debugOutstandingReadbackKind)
                        {
                            case DebugCounterReadbackKind.TriangleCount:
                                _debugTriangleCount = values[0];
                                _debugMaximumTriangleCount = Math.Max(
                                    _debugMaximumTriangleCount, _debugTriangleCount);
                                if (_debugTriangleCount > 0u)
                                    IncrementSaturating(ref _debugPositiveTriangleReadbackCount);
                                break;
                            case DebugCounterReadbackKind.IndirectVertexCount:
                                _debugIndirectVertexCount = values[0];
                                if (values.Length >= 4)
                                {
                                    _debugIndirectInstanceCount = values[1];
                                    _debugIndirectStartVertex = values[2];
                                    _debugIndirectStartInstance = values[3];
                                }
                                break;
                            default:
                                _debugTriangleOverflow = values[0];
                                break;
                        }
                        RefreshDebugStage();
                    }
                }
            }
            if (_resourceReleasePending)
                DisposeResources();
        }

        private void DispatchDensity(
            in FluidGpuSnapshot snapshot,
            in FluidSurfaceGridSettings grid,
            FluidSurfaceGpuResources targetResources)
        {
            using (DensityMarker.Auto())
            {
                _densityCompute.SetInt(ParticleCapacityId, snapshot.ParticleCapacity);
                _densityCompute.SetInt(HashTableCapacityId, snapshot.HashTableCapacity);
                _densityCompute.SetFloat(SmoothingRadiusId, snapshot.SmoothingRadius);
                _densityCompute.SetFloat(ParticleMassId, snapshot.ParticleMass);
                _densityCompute.SetInt(TargetMaterialId, (byte)_targetMaterial);
                _densityCompute.SetInt(DensityCellRadiusId, _settings.AnisotropyCellRadius);
                _densityCompute.SetInt(
                    UseStylizedCrownId,
                    _settings.UseStylizedCrown ? 1 : 0);
                _densityCompute.SetFloat(CrownHeightRatioId, _settings.CrownHeightRatio);
                _densityCompute.SetFloat(CrownFalloffId, _settings.CrownFalloff);
                SetGridParameters(_densityCompute, in grid);
                _densityCompute.SetBuffer(_densityKernel, PredictedPositionsId, snapshot.PredictedPositions);
                _densityCompute.SetBuffer(_densityKernel, ParticleMetadataId, snapshot.Metadata);
                _densityCompute.SetBuffer(_densityKernel, SpatialEntriesId, snapshot.SpatialEntries);
                _densityCompute.SetBuffer(_densityKernel, CellRangesId, snapshot.CellRanges);
                _densityCompute.SetBuffer(_densityKernel, SpatialCellsId, snapshot.SpatialCells);
                _densityCompute.SetBuffer(
                    _densityKernel,
                    LiquidMaterialParametersId,
                    snapshot.LiquidMaterialParameters);
                _densityCompute.SetBuffer(
                    _densityKernel,
                    SurfaceSupportsId,
                    _resources.SurfaceSupportBuffer);
                if (_settings.UseAnisotropy)
                {
                    _densityCompute.SetBuffer(
                        _densityKernel,
                        AnisotropyTransformsId,
                        _resources.AnisotropyBuffer);
                }
                _densityCompute.SetTexture(_densityKernel, DensityTextureId, targetResources.DensityTexture);
                _densityCompute.Dispatch(
                    _densityKernel,
                    DivideRoundUp(grid.Resolution.x, DensityThreadGroupSize),
                    DivideRoundUp(grid.Resolution.y, DensityThreadGroupSize),
                    DivideRoundUp(grid.Resolution.z, DensityThreadGroupSize));
            }
        }

        private void DispatchSurfaceNeighborhood(in FluidGpuSnapshot snapshot)
        {
            using (AnisotropyMarker.Auto())
            {
                SetAnisotropyParameters(in snapshot);
                if (!_settings.UseAnisotropy)
                {
                    BindAnisotropyNeighborhood(_gatherSurfaceSupportKernel, in snapshot);
                    _anisotropyCompute.SetBuffer(
                        _gatherSurfaceSupportKernel,
                        SurfaceSupportsId,
                        _resources.SurfaceSupportBuffer);
                    _anisotropyCompute.Dispatch(
                        _gatherSurfaceSupportKernel,
                        DivideRoundUp(snapshot.ParticleCapacity, 64),
                        1,
                        1);
                    return;
                }

                _anisotropyCompute.SetBuffer(
                    _clearAnisotropyCountersKernel,
                    AnisotropyCountersId,
                    _resources.AnisotropyCounters);
                _anisotropyCompute.Dispatch(_clearAnisotropyCountersKernel, 1, 1, 1);

                BindAnisotropyNeighborhood(_gatherAnisotropyMeanKernel, in snapshot);
                _anisotropyCompute.SetBuffer(
                    _gatherAnisotropyMeanKernel,
                    AnisotropyTransformsId,
                    _resources.AnisotropyBuffer);
                _anisotropyCompute.SetBuffer(
                    _gatherAnisotropyMeanKernel,
                    SurfaceSupportsId,
                    _resources.SurfaceSupportBuffer);
                _anisotropyCompute.Dispatch(
                    _gatherAnisotropyMeanKernel,
                    DivideRoundUp(snapshot.ParticleCapacity, 64),
                    1,
                    1);

                BindAnisotropyNeighborhood(_buildAnisotropyTransformKernel, in snapshot);
                _anisotropyCompute.SetBuffer(
                    _buildAnisotropyTransformKernel,
                    AnisotropyTransformsId,
                    _resources.AnisotropyBuffer);
                _anisotropyCompute.SetBuffer(
                    _buildAnisotropyTransformKernel,
                    AnisotropyCountersId,
                    _resources.AnisotropyCounters);
                _anisotropyCompute.Dispatch(
                    _buildAnisotropyTransformKernel,
                    DivideRoundUp(snapshot.ParticleCapacity, 64),
                    1,
                    1);
            }
        }

        private void SetAnisotropyParameters(in FluidGpuSnapshot snapshot)
        {
            _anisotropyCompute.SetInt(ParticleCapacityId, snapshot.ParticleCapacity);
            _anisotropyCompute.SetInt(HashTableCapacityId, snapshot.HashTableCapacity);
            _anisotropyCompute.SetInt(NeighborThresholdId, _settings.AnisotropyNeighborThreshold);
            _anisotropyCompute.SetInt(
                CrownEdgeNeighborCountId,
                _settings.CrownEdgeNeighborCount);
            _anisotropyCompute.SetInt(
                CrownInteriorNeighborCountId,
                _settings.CrownInteriorNeighborCount);
            _anisotropyCompute.SetFloat(SmoothingRadiusId, snapshot.SmoothingRadius);
            _anisotropyCompute.SetFloat(MinimumAnisotropyScaleId, _settings.MinimumAnisotropyScale);
            _anisotropyCompute.SetFloat(MaximumAnisotropyScaleId, _settings.MaximumAnisotropyScale);
            _anisotropyCompute.SetFloat(MaximumAnisotropyRatioId, _settings.MaximumAnisotropyRatio);
        }

        private void BindAnisotropyNeighborhood(int kernel, in FluidGpuSnapshot snapshot)
        {
            _anisotropyCompute.SetBuffer(kernel, PredictedPositionsId, snapshot.PredictedPositions);
            _anisotropyCompute.SetBuffer(kernel, ParticleMetadataId, snapshot.Metadata);
            _anisotropyCompute.SetBuffer(kernel, SpatialEntriesId, snapshot.SpatialEntries);
            _anisotropyCompute.SetBuffer(kernel, CellRangesId, snapshot.CellRanges);
            _anisotropyCompute.SetBuffer(kernel, SpatialCellsId, snapshot.SpatialCells);
        }

        private void DispatchMarchingCubes(
            in FluidSurfaceGridSettings grid,
            FluidSurfaceGpuResources targetResources)
        {
            using (MarchingCubesMarker.Auto())
            {
                SetGridParameters(_marchingCubesCompute, in grid);
                _marchingCubesCompute.SetFloat(IsoLevelId, _settings.IsoLevel);
                _marchingCubesCompute.SetInt(MaximumTriangleCountId, grid.MaximumTriangleCount);

                BindSurfaceWriteBuffers(_clearSurfaceCountersKernel, targetResources);
                _marchingCubesCompute.Dispatch(_clearSurfaceCountersKernel, 1, 1, 1);

                _marchingCubesCompute.SetTexture(
                    _extractSurfaceKernel,
                    DensityTextureId,
                    targetResources.DensityTexture);
                BindSurfaceWriteBuffers(_extractSurfaceKernel, targetResources);
                _marchingCubesCompute.Dispatch(
                    _extractSurfaceKernel,
                    DivideRoundUp(grid.Resolution.x - 1, MarchingCubesThreadGroupSize),
                    DivideRoundUp(grid.Resolution.y - 1, MarchingCubesThreadGroupSize),
                    DivideRoundUp(grid.Resolution.z - 1, MarchingCubesThreadGroupSize));

                BindSurfaceWriteBuffers(_buildIndirectArgsKernel, targetResources);
                _marchingCubesCompute.Dispatch(_buildIndirectArgsKernel, 1, 1, 1);
            }
        }

        private void DrawSurface(
            in FluidSurfaceGridSettings grid,
            FluidSurfaceGpuResources targetResources)
        {
            using (DrawMarker.Auto())
            {
                _materialPropertyBlock.SetBuffer(SurfaceTrianglesId, targetResources.TriangleBuffer);
                // 每次 Draw 显式写入 MPB，避免只改 Material/SO 却被共享材质或旧序列化值覆盖。
                _materialPropertyBlock.SetFloat(
                    DebugForceOpaqueFragmentId,
                    _debugForceOpaqueFragment ? 1f : 0f);
                // Bounds 是 padded Density Grid 的真实世界范围；URP 用它进行整批 Culling/透明排序。
                _renderParams.worldBounds = grid.WorldBounds;
                Graphics.RenderPrimitivesIndirect(
                    _renderParams,
                    MeshTopology.Triangles,
                    targetResources.IndirectArguments,
                    1,
                    0);
            }
        }

        private void BindSurfaceWriteBuffers(int kernel, FluidSurfaceGpuResources targetResources)
        {
            _marchingCubesCompute.SetBuffer(
                kernel, SurfaceTrianglesId, targetResources.TriangleBuffer);
            _marchingCubesCompute.SetBuffer(
                kernel, TriangleCounterId, targetResources.TriangleCounter);
            _marchingCubesCompute.SetBuffer(
                kernel, OverflowCounterId, targetResources.OverflowCounter);
            _marchingCubesCompute.SetBuffer(
                kernel, IndirectArgumentsId, targetResources.IndirectArguments);
        }

        private static void SetGridParameters(
            ComputeShader compute,
            in FluidSurfaceGridSettings grid)
        {
            compute.SetInt(GridResolutionXId, grid.Resolution.x);
            compute.SetInt(GridResolutionYId, grid.Resolution.y);
            compute.SetInt(GridResolutionZId, grid.Resolution.z);
            compute.SetVector(WorldOriginId, grid.WorldOrigin);
            compute.SetVector(VoxelSizeId, grid.VoxelSize);
        }

        private static bool IsSupportedGraphicsDevice()
        {
            bool supportedD3d = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            return supportedD3d
                && SystemInfo.supportsComputeShaders
                && SystemInfo.supportsInstancing
                && SystemInfo.supportsIndirectArgumentsBuffer
                && SystemInfo.IsFormatSupported(
                    GraphicsFormat.R32_SFloat,
                    GraphicsFormatUsage.Sample);
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private void DisableWithFailure(string message)
        {
            if (!_hasReportedFailure)
            {
                _hasReportedFailure = true;
                GameLog.Warn(message, "Rendering");
            }

            enabled = false;
        }

        private void RefreshDebugStage()
        {
            _debugStage = LiquidSurfaceDiagnosticClassifier.Classify(
                _debugHasSnapshot,
                _debugResourcesReady,
                _debugTriangleCount,
                _debugIndirectVertexCount);
            if (_debugStage == LiquidSurfaceDiagnosticStage.DrawReady)
                IncrementSaturating(ref _debugDrawReadyObservationCount);
        }

        private void RefreshDebugGeometryStage()
        {
            var drawBounds = new Bounds(_debugDrawBoundsCenter, _debugDrawBoundsSize);
            _debugGeometryStage = LiquidSurfaceGeometryDiagnosticClassifier.Classify(
                _debugHasTriangleSample,
                in drawBounds,
                _debugTrianglePosition0,
                _debugTrianglePosition1,
                _debugTrianglePosition2,
                _debugTriangleNormal0,
                _debugTriangleNormal1,
                _debugTriangleNormal2);
            if (_debugGeometryStage == LiquidSurfaceGeometryDiagnosticStage.GeometryReady)
                IncrementSaturating(ref _debugGeometryReadyObservationCount);
        }

        private static void IncrementSaturating(ref uint value)
        {
            if (value < uint.MaxValue)
                value++;
        }

        private void DisposeResources()
        {
            if (_debugCounterOutstanding)
            {
                _resourceReleasePending = true;
                return;
            }
            _resources?.Dispose();
            _resources = null;
            _debugResourcesReady = false;
            RefreshDebugStage();
            _resourceReleasePending = false;
        }
    }
}
