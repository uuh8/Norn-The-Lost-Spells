using System;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// Fire Cell 的只读 GPU Projection。CPU 只低频抽取 Cell Seed；已出生 Parcel 的运动、生命周期、
    /// Surface/Gas 相位都留在固定 GPU Pool。Renderer 从不回写 Fire Amount 或创建 Gameplay Gas。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GpuFireParcelRenderer : MonoBehaviour
    {
        private const uint SeedJitter = 0x9E3779B9u;
        private static readonly ProfilerMarker SeedMarker = new ProfilerMarker("GpuFire.SeedUpload");
        private static readonly ProfilerMarker SimulateMarker = new ProfilerMarker("GpuFire.ParcelLifecycle");
        private static readonly ProfilerMarker SurfaceMarker = new ProfilerMarker("GpuFire.ReconstructSurface");
        private static readonly ProfilerMarker DrawMarker = new ProfilerMarker("GpuFire.Draw");
        private static readonly ProfilerMarker CollectMarker = new ProfilerMarker("GpuFire.CollectSeeds");
        private static readonly ProfilerMarker UploadMarker = new ProfilerMarker("GpuFire.UploadSeeds");
#if UNITY_EDITOR
        private bool _validationHideDraw;
        // 固定快照的Draw隔离保留粒子Buffer和生命周期，恢复时不会重新生成火焰。
        public void SetValidationHideDraw(bool value)=>_validationHideDraw=value;
#endif
        private static readonly int StatesId = Shader.PropertyToID("_FireParcelStates");
        private static readonly int SpawnsId = Shader.PropertyToID("_FireParcelSpawns");

        [SerializeField] private ElementWorldRuntime _worldRuntime;
        [SerializeField] private Transform _interestCenter;
        [SerializeField] private GpuFireParcelRenderProfile _profile;
        [SerializeField] private ComputeShader _lifecycleCompute;
        [SerializeField] private ComputeShader _surfaceDensityCompute;
        [SerializeField] private ComputeShader _surfaceMarchingCubesCompute;
        [SerializeField] private Material _surfaceMaterial;
        [SerializeField] private Material _gasMaterial;

        private IElementWorldReadOnly _world;
        private IElementWorldChunkReadOnly _chunkReader;
        private ElementCell[] _chunkCpu;
        private ElementChunkKey[] _visibleKeys;
        private FireParcelSpawnGpu[] _seedCpu;
        private GraphicsBuffer _spawnBuffer;
        private GraphicsBuffer _stateBuffer;
        private GraphicsBuffer _drawArgs;
        private FireParcelSurfaceResources _surfaceResources;
        private MaterialPropertyBlock _surfaceProperties;
        private MaterialPropertyBlock _surfaceSplatProperties;
        private MaterialPropertyBlock _gasProperties;
        private RenderParams _surfaceRenderParams;
        private RenderParams _surfaceDepthRenderParams;
        private Material _surfaceDepthMaterial;
        private RenderParams _surfaceSplatRenderParams;
        private RenderParams _gasRenderParams;
        private FireParcelPhaseSettings _phase;
        private int _clearKernel;
        private int _spawnKernel;
        private int _simulateKernel;
        private int _densityClearKernel, _densityScatterKernel, _densityResolveKernel;
        private int _surfaceClearKernel;
        private int _surfaceExtractKernel;
        private int _surfaceArgsKernel;
        private uint _spawnSequence;
        private uint _visualSequence;
        private float _spawnAccumulator;
        private bool _initialized;
        private bool _useLocalSurface;
        private float _visualTime;
        private int _seedScanChunkCursor;
        private readonly MaterialPresentationView _view=new MaterialPresentationView();
        private Camera _viewCamera;
        public bool IsReady => _initialized;
        public bool UsesLocalSurface => _useLocalSurface;
        public Bounds LastDrawBounds { get; private set; }
        public int LastSeedCount { get; private set; }
        public uint TotalSeeds => _spawnSequence;
        public long LastGcBytes { get; private set; }
        public long PeakGcBytes { get; private set; }

        private void Start()
        {
            if (!TryInitialize())
                enabled = false;
        }

        private bool TryInitialize()
        {
            if (_worldRuntime == null || _profile == null || _lifecycleCompute == null
                || _gasMaterial == null)
            {
                GameLog.Error("GpuFireParcelRenderer 缺少 Runtime/Profile/Compute/Material。", "Rendering");
                return false;
            }
            _world = _worldRuntime;
            if (!_world.IsInitialized)
                return false;
            // Profile在启动边界决定GPU资源布局。统一模式只保留生命周期池与两个Splat Draw，
            // 不要求局部Mesh资源，更不能分配后逐帧重建一个不会显示的Density场。
            _useLocalSurface = !_profile.UnifiedSoftFlames;
            if (_useLocalSurface && (_surfaceDensityCompute == null
                || _surfaceMarchingCubesCompute == null || _surfaceMaterial == null))
            {
                GameLog.Error("局部GPU Fire Surface缺少Compute/Material。", "Rendering");
                return false;
            }
            if (!_lifecycleCompute.HasKernel("ClearParcels")
                || !_lifecycleCompute.HasKernel("SpawnParcels")
                || !_lifecycleCompute.HasKernel("SimulateParcels")
                || (_useLocalSurface && (!_surfaceDensityCompute.HasKernel("ClearFireDensity")
                || !_surfaceDensityCompute.HasKernel("ScatterFireDensity")
                || !_surfaceDensityCompute.HasKernel("ResolveFireDensity")
                || !_surfaceMarchingCubesCompute.HasKernel("ClearFireSurface")
                || !_surfaceMarchingCubesCompute.HasKernel("ExtractFireSurface")
                || !_surfaceMarchingCubesCompute.HasKernel("BuildFireDrawArgs"))))
            {
                // Shader Import 失败时 HasKernel=false。初始化前停止，避免每帧 Dispatch kernel 0 刷屏。
                GameLog.Error("GPU Fire Compute Kernel Contract 不完整，请先检查 Shader Import Error。", "Rendering");
                return false;
            }
            _interestCenter ??= transform;
            _phase = _profile.CreatePhaseSettings();
            _visibleKeys = new ElementChunkKey[_world.MaximumResidentChunkCount];
            _chunkReader = _world as IElementWorldChunkReadOnly;
            int chunkSize = _world.ChunkSize;
            _chunkCpu = new ElementCell[chunkSize * chunkSize * chunkSize];
            _seedCpu = new FireParcelSpawnGpu[_profile.MaximumSeedsPerTick];
            _spawnBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                _profile.MaximumSeedsPerTick, FireParcelGpuLayout.SpawnStride);
            _stateBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                _profile.Capacity, FireParcelGpuLayout.StateStride);
            _drawArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1,
                GraphicsBuffer.IndirectDrawArgs.size);
            _drawArgs.SetData(new uint[] { 6u, (uint)_profile.Capacity, 0u, 0u });

            _clearKernel = _lifecycleCompute.FindKernel("ClearParcels");
            _spawnKernel = _lifecycleCompute.FindKernel("SpawnParcels");
            _simulateKernel = _lifecycleCompute.FindKernel("SimulateParcels");
            BindCommon(_clearKernel);
            _lifecycleCompute.Dispatch(_clearKernel, DivideRoundUp(_profile.Capacity, 64), 1, 1);

            _surfaceProperties = new MaterialPropertyBlock();
            _surfaceSplatProperties = new MaterialPropertyBlock();
            _gasProperties = new MaterialPropertyBlock();
            _surfaceProperties.SetColor("_CoreColor", _profile.CoreColor);
            _surfaceProperties.SetColor("_EdgeColor", _profile.EdgeColor);
            // 连续火焰主体完整覆盖背景，避免地砖/墙面的三角形纹理透成“火焰裂片”。
            // LOD 边缘仍交叉淡化，柔软飘散的透明轮廓由 Gas 阶段承担。
            _surfaceProperties.SetFloat("_Alpha", 1f);
            _surfaceSplatProperties.SetBuffer(StatesId, _stateBuffer);
            _surfaceSplatProperties.SetFloat("_PhaseMode", 0f);
            _surfaceSplatProperties.SetFloat("_UseLocalSurface", _useLocalSurface ? 1f : 0f);
            _surfaceSplatProperties.SetColor("_CoreColor", _profile.CoreColor);
            _surfaceSplatProperties.SetColor("_EdgeColor", _profile.EdgeColor);
            _surfaceSplatProperties.SetFloat(
                "_SurfaceRadiusMultiplier",
                _profile.SurfaceSplatRadiusMultiplier);
            _surfaceSplatProperties.SetFloat(
                "_SurfaceAlphaScale",
                _profile.SurfaceSplatAlphaScale);
            _surfaceSplatProperties.SetFloat("_EdgeIrregularity", _profile.GasEdgeIrregularity * 0.5f);
            _surfaceSplatProperties.SetFloat("_VerticalStretch", _profile.SurfaceVerticalStretch);
            _surfaceSplatProperties.SetFloat("_CohesionRadius", _profile.SurfaceCohesionRadius);
            _surfaceSplatProperties.SetFloat("_Softness", .72f);
            _gasProperties.SetBuffer(StatesId, _stateBuffer);
            _gasProperties.SetFloat("_PhaseMode", 1f);
            _gasProperties.SetColor("_CoreColor", _profile.CoreColor);
            _gasProperties.SetColor("_EdgeColor", _profile.EdgeColor);
            _gasProperties.SetFloat("_GasRadiusMultiplier", _profile.GasRadiusMultiplier);
            _gasProperties.SetFloat("_GasAlphaScale", _profile.GasAlphaScale);
            _gasProperties.SetFloat("_EdgeIrregularity", _profile.GasEdgeIrregularity);
            _gasProperties.SetFloat("_VerticalStretch", _profile.GasVerticalStretch);
            _gasProperties.SetFloat("_Softness", .72f);
            if (_useLocalSurface) InitializeLocalSurface();
            // 复用现有 Gas Material/Shader，但用独立 PropertyBlock 选择 PhaseMode=0；
            // Presentation Draw 彼此独立，不会缩短或改写 Gas 生命周期。
            _surfaceSplatRenderParams = CreateRenderParams(_gasMaterial, _surfaceSplatProperties);
            _gasRenderParams = CreateRenderParams(_gasMaterial, _gasProperties);
            _initialized = true;
            return true;
        }

        private void InitializeLocalSurface()
        {
            FireParcelSurfaceGridSettings initialGrid = FireParcelSurfaceGridPlanner.Plan(
                _interestCenter.position, _profile.SurfaceBoundsRadius, _profile.SurfaceVoxelSize);
            _surfaceResources = new FireParcelSurfaceResources(in initialGrid, _profile.MaximumSurfaceTriangleCount);
            _densityClearKernel = _surfaceDensityCompute.FindKernel("ClearFireDensity");
            _densityScatterKernel = _surfaceDensityCompute.FindKernel("ScatterFireDensity");
            _densityResolveKernel = _surfaceDensityCompute.FindKernel("ResolveFireDensity");
            _surfaceClearKernel = _surfaceMarchingCubesCompute.FindKernel("ClearFireSurface");
            _surfaceExtractKernel = _surfaceMarchingCubesCompute.FindKernel("ExtractFireSurface");
            _surfaceArgsKernel = _surfaceMarchingCubesCompute.FindKernel("BuildFireDrawArgs");
            _surfaceProperties.SetBuffer("_FireSurfaceTriangles", _surfaceResources.Triangles);
            _surfaceRenderParams = CreateRenderParams(_surfaceMaterial, _surfaceProperties);
            // 仅在局部Mesh对照模式创建Depth Material，归Renderer所有，不修改用户资产。
            _surfaceDepthMaterial = new Material(_surfaceMaterial) { hideFlags = HideFlags.DontSave };
            _surfaceDepthMaterial.SetFloat("_DepthOnly",1);
            _surfaceDepthMaterial.SetFloat("_DepthWrite",1);
            _surfaceDepthMaterial.SetFloat("_ColorWriteMask",0);
            _surfaceDepthMaterial.renderQueue = _surfaceMaterial.renderQueue - 1;
            _surfaceDepthRenderParams = CreateRenderParams(_surfaceDepthMaterial, _surfaceProperties);
        }

        private static RenderParams CreateRenderParams(Material material, MaterialPropertyBlock properties)
        {
            return new RenderParams(material)
            {
                matProps = properties,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(Vector3.zero, Vector3.one)
            };
        }

        private void Update()
        {
            LastGcBytes = 0;
            // Scene卸载/死亡可先销毁兴趣中心，再销毁Renderer；Unity的假null必须用==检查，不能读已销毁Transform。
            if (!_initialized || _interestCenter == null || Time.timeScale <= 0f)
                return;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            float dt = Mathf.Min(Time.deltaTime, _profile.MaximumVisualDeltaTime);
            _visualTime += dt;
            _spawnAccumulator += dt;
            if (_spawnAccumulator >= _profile.SpawnInterval)
            {
                _spawnAccumulator = 0f;
                UploadAndSpawnSeeds();
            }
            Simulate(dt);
            LastGcBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        private void LateUpdate()
        {
            if (!_initialized || _interestCenter == null)
                return;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Bounds bounds = new Bounds(_interestCenter.position,
                Vector3.one * (_profile.DrawBoundsRadius * 2f));
            LastDrawBounds = bounds;
            _surfaceSplatRenderParams.worldBounds = bounds;
            _gasRenderParams.worldBounds = bounds;
            _surfaceSplatProperties.SetFloat("_VisualTime", _visualTime);
            _gasProperties.SetFloat("_VisualTime", _visualTime);
            SetVisibility(_surfaceSplatProperties);
            SetVisibility(_gasProperties);
            if (_useLocalSurface)
            {
                FireParcelSurfaceGridSettings grid = FireParcelSurfaceGridPlanner.Plan(
                    _interestCenter.position, _profile.SurfaceBoundsRadius, _profile.SurfaceVoxelSize);
                using (SurfaceMarker.Auto()) ReconstructSurface(in grid);
                _surfaceRenderParams.worldBounds = bounds;
                _surfaceDepthRenderParams.worldBounds = bounds;
                Vector3 surfaceHalfSize = Vector3.one * _profile.SurfaceBoundsRadius;
                _surfaceProperties.SetFloat("_VisualTime", _visualTime);
                _surfaceProperties.SetVector("_SurfaceLodCenter", grid.Bounds.center);
                _surfaceProperties.SetVector("_SurfaceLodHalfSize", surfaceHalfSize);
                _surfaceProperties.SetFloat("_SurfaceLodBlendWidth", _profile.SurfaceSplatBlendWidth);
                _surfaceSplatProperties.SetVector("_SurfaceLodCenter", grid.Bounds.center);
                _surfaceSplatProperties.SetVector("_SurfaceLodHalfSize", surfaceHalfSize);
                _surfaceSplatProperties.SetFloat("_SurfaceLodBlendWidth", _profile.SurfaceSplatBlendWidth);
            }
            // 统一模式没有距离驱动的形态选择；两次Splat Draw仍使用原来的Surface/Gas相位交叉。
            using (DrawMarker.Auto())
            {
#if UNITY_EDITOR
                if(!_validationHideDraw)
                {
#endif
                if (_useLocalSurface)
                    Graphics.RenderPrimitivesIndirect(_surfaceDepthRenderParams, MeshTopology.Triangles, _surfaceResources.DrawArgs, 1, 0);
                Graphics.RenderPrimitivesIndirect(_surfaceSplatRenderParams, MeshTopology.Triangles, _drawArgs, 1, 0);
                if (_useLocalSurface)
                    Graphics.RenderPrimitivesIndirect(_surfaceRenderParams, MeshTopology.Triangles, _surfaceResources.DrawArgs, 1, 0);
                Graphics.RenderPrimitivesIndirect(_gasRenderParams, MeshTopology.Triangles, _drawArgs, 1, 0);
#if UNITY_EDITOR
                }
#endif
            }
            LastGcBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
            PeakGcBytes = Math.Max(PeakGcBytes, LastGcBytes);
        }

        private void SetVisibility(MaterialPropertyBlock properties)
        {
            properties.SetVector("_VisibilityCenter", _interestCenter.position);
            properties.SetFloat("_VisibilityFadeStart", _profile.VisibilityFadeStart);
            properties.SetFloat("_VisibilityFadeEnd", _profile.VisibilityFadeEnd);
        }

        private void ReconstructSurface(in FireParcelSurfaceGridSettings grid)
        {
            SetSurfaceGrid(_surfaceDensityCompute, in grid);
            _surfaceDensityCompute.SetInt("_Capacity", _profile.Capacity);
            _surfaceDensityCompute.SetFloat("_SurfaceKernelScale", _profile.SurfaceKernelScale);
            _surfaceDensityCompute.SetFloat(
                "_LiquidLikeRadiusMultiplier",
                _profile.LiquidLikeRadiusMultiplier);
            _surfaceDensityCompute.SetFloat("_CohesionRadius", _profile.SurfaceCohesionRadius);
            _surfaceDensityCompute.SetFloat("_SurfaceVerticalStretch", _profile.SurfaceVerticalStretch);
            int voxels = grid.Resolution.x * grid.Resolution.y * grid.Resolution.z;
            _surfaceDensityCompute.SetInt("_VoxelCount", voxels);
            // Dispatch 之间由 Unity 的资源依赖保证顺序；Atomic Add 本身不等于全局 Barrier。
            // 所有活跃 Parcel 都参与，免去旧版“只取最新512个”的密度突然掉块。
            _surfaceDensityCompute.SetBuffer(_densityClearKernel, "_DensityAccumulation", _surfaceResources.DensityAccumulation);
            _surfaceDensityCompute.Dispatch(_densityClearKernel, DivideRoundUp(voxels,64),1,1);
            _surfaceDensityCompute.SetBuffer(_densityScatterKernel, "_DensityAccumulation", _surfaceResources.DensityAccumulation);
            _surfaceDensityCompute.SetBuffer(_densityScatterKernel, StatesId, _stateBuffer);
            _surfaceDensityCompute.Dispatch(_densityScatterKernel, DivideRoundUp(_profile.Capacity,64),1,1);
            _surfaceDensityCompute.SetBuffer(_densityResolveKernel, "_DensityAccumulation", _surfaceResources.DensityAccumulation);
            _surfaceDensityCompute.SetTexture(_densityResolveKernel, "_FireDensity", _surfaceResources.Density);
            _surfaceDensityCompute.Dispatch(_densityResolveKernel,
                DivideRoundUp(grid.Resolution.x,4), DivideRoundUp(grid.Resolution.y,4), DivideRoundUp(grid.Resolution.z,4));

            SetSurfaceGrid(_surfaceMarchingCubesCompute, in grid);
            _surfaceMarchingCubesCompute.SetFloat("_IsoLevel", _profile.SurfaceIsoLevel);
            _surfaceMarchingCubesCompute.SetInt("_MaximumTriangleCount", _profile.MaximumSurfaceTriangleCount);
            BindSurface(_surfaceClearKernel);
            _surfaceMarchingCubesCompute.Dispatch(_surfaceClearKernel,1,1,1);
            _surfaceMarchingCubesCompute.SetTexture(_surfaceExtractKernel,"_FireDensity",_surfaceResources.Density);
            BindSurface(_surfaceExtractKernel);
            _surfaceMarchingCubesCompute.Dispatch(_surfaceExtractKernel,
                DivideRoundUp(grid.Resolution.x-1,4),DivideRoundUp(grid.Resolution.y-1,4),DivideRoundUp(grid.Resolution.z-1,4));
            BindSurface(_surfaceArgsKernel);
            _surfaceMarchingCubesCompute.Dispatch(_surfaceArgsKernel,1,1,1);
        }

        private void BindSurface(int kernel)
        {
            _surfaceMarchingCubesCompute.SetBuffer(kernel,"_FireSurfaceTriangles",_surfaceResources.Triangles);
            _surfaceMarchingCubesCompute.SetBuffer(kernel,"_TriangleCounter",_surfaceResources.Counter);
            _surfaceMarchingCubesCompute.SetBuffer(kernel,"_OverflowCounter",_surfaceResources.Overflow);
            _surfaceMarchingCubesCompute.SetBuffer(kernel,"_IndirectArguments",_surfaceResources.DrawArgs);
        }

        private static void SetSurfaceGrid(ComputeShader compute, in FireParcelSurfaceGridSettings grid)
        {
            compute.SetInt("_GridResolutionX",grid.Resolution.x); compute.SetInt("_GridResolutionY",grid.Resolution.y); compute.SetInt("_GridResolutionZ",grid.Resolution.z);
            compute.SetVector("_WorldOrigin",grid.Origin); compute.SetVector("_VoxelSize",grid.VoxelSize);
        }

        private void UploadAndSpawnSeeds()
        {
            using (SeedMarker.Auto())
            {
                int seedCount;
                using (CollectMarker.Auto()) seedCount = CollectSeeds();
                if (seedCount == 0) return;
                using (UploadMarker.Auto()) _spawnBuffer.SetData(_seedCpu, 0, 0, seedCount);
                BindCommon(_spawnKernel);
                _lifecycleCompute.SetBuffer(_spawnKernel, SpawnsId, _spawnBuffer);
                _lifecycleCompute.SetInt("_SpawnCount", seedCount);
                _lifecycleCompute.SetInt("_SpawnStartSlot",
                    FireParcelPoolMath.ResolveRingSlot(_spawnSequence, _profile.Capacity));
                _lifecycleCompute.Dispatch(_spawnKernel, DivideRoundUp(seedCount, 64), 1, 1);
                _spawnSequence += (uint)seedCount;
            }
        }

        private int CollectSeeds()
        {
            if(_viewCamera==null || !_viewCamera.isActiveAndEnabled)_viewCamera=Camera.main;
            _view.Update(_viewCamera,_interestCenter.position,_profile.VisibilityFadeStart,_profile.VisibilityFadeEnd,8);
            int keyCount = _world.CopyVisibleChunkKeys(_visibleKeys);
            int seedCount = 0;
            int chunkSize = _world.ChunkSize;
            float cellSize = _world.CellSize;
            Vector3 origin = _world.Origin;
            float probability = _profile.SpawnProbabilityAtFullAmount;
            float minimumHeat = _profile.MinimumVisualHeat;
            int startKey = keyCount > 0 ? _seedScanChunkCursor % keyCount : 0;
            int scannedKeys = 0;
            for (; scannedKeys < keyCount && seedCount < _seedCpu.Length; scannedKeys++)
            {
                ElementChunkKey key = _visibleKeys[(startKey + scannedKeys) % keyCount];
                float span=chunkSize*cellSize;
                Bounds bounds=new Bounds(origin+new Vector3(key.X+.5f,key.Y+.5f,key.Z+.5f)*span,Vector3.one*span);
                // 在复制Cell／概率发射前排除镜头外来源。火焰Gameplay仍存在，
                // 原固定Pool继续完成已出生Parcel的两阶段；转头后下一Spawn Tick可恢复。
                if(!_view.Intersects(bounds,3))continue;
                bool snapshot = _chunkReader != null;
                if (snapshot && _chunkReader.CopyChunkCells(key, _chunkCpu) == 0) continue;
                Vector3Int firstCell = ElementWorldCoordinates.ComposeGlobalCell(key, Vector3Int.zero, chunkSize);
                for (int z = 0; z < chunkSize && seedCount < _seedCpu.Length; z++)
                for (int y = 0; y < chunkSize && seedCount < _seedCpu.Length; y++)
                for (int x = 0; x < chunkSize && seedCount < _seedCpu.Length; x++)
                {
                    // Buffer、索引顺序、Cell身份与旧查询一致；Snapshot 属于消费者，绝不把它写回 World。
                    Vector3Int cellPosition = new Vector3Int(checked(firstCell.x+x), checked(firstCell.y+y), checked(firstCell.z+z));
                    ElementCell cell;
                    if (snapshot) cell = _chunkCpu[(z*chunkSize+y)*chunkSize+x];
                    else if (!_world.TryGetCell(cellPosition, out cell)) continue;
                    if (cell.IsEmpty || cell.MaterialKind != MaterialId.Fire
                        || !FireCellEmissionSampler.ShouldEmitVisual(cellPosition, _visualSequence, SeedJitter,
                            cell.Amount, probability, minimumHeat)) continue;
                    Vector3 center = origin + new Vector3(
                        (cellPosition.x + 0.5f) * cellSize,
                        (cellPosition.y + 0.5f) * cellSize,
                        (cellPosition.z + 0.5f) * cellSize);
                    ILiquidSurfaceReadOnly liquids=_worldRuntime.GridLiquids;
                    // 火保持细格分辨率；Solid 查询必须先转入液体网格，不能复用火的格编号。
                    Vector3Int liquidBelow=liquids!=null
                        ?ElementWorldCoordinates.WorldToGlobalCell(center-Vector3.up*cellSize,
                            liquids.Origin,liquids.CellSize):default;
                    if(liquids!=null && liquids.IsSolidForSurface(liquidBelow))
                        center.y=origin.y+cellPosition.y*cellSize
                            +FireCellEmissionSampler.GroundSourceHeight(cellSize,_profile.MinimumGroundSourceHeight);
                    _seedCpu[seedCount++] = new FireParcelSpawnGpu
                    {
                        Position = center + FireCellEmissionSampler.CalculateJitter(
                            cellPosition, _visualSequence, SeedJitter, cellSize, cellSize * 0.3f),
                        // Phase 在初始化时形成不可变快照；Spawn Lifetime 必须使用同一快照，
                        // 避免 Play Mode 中编辑 Profile 造成 CPU Seed 与 GPU Uniform 时间轴分叉。
                        Lifetime = _phase.TotalLifetimeSeconds,
                        InitialVelocity = Vector3.up * 0.25f,
                        Heat = FireCellEmissionSampler.VisualHeat(cell.Amount,minimumHeat)
                    };
                }
            }
            // 饱和时轮转起点，避免 Chunk 枚举靠后的火源永久只有 Gas/没有新 Surface。
            _seedScanChunkCursor = keyCount > 0 ? (startKey + Mathf.Max(1, scannedKeys)) % keyCount : 0;
            _visualSequence++;
            LastSeedCount = seedCount;
            return seedCount;
        }

        private void Simulate(float deltaTime)
        {
            using (SimulateMarker.Auto())
            {
                BindCommon(_simulateKernel);
                _lifecycleCompute.SetFloat("_DeltaTime", deltaTime);
                _lifecycleCompute.SetFloat("_Buoyancy", _profile.Buoyancy);
                _lifecycleCompute.SetFloat("_Drag", _profile.Drag);
                _lifecycleCompute.SetFloat("_SourceTether", _profile.SourceTether);
                _lifecycleCompute.SetFloat("_NoiseStrength", _profile.NoiseStrength);
                _lifecycleCompute.SetFloat("_NoiseScale", _profile.NoiseScale);
                _lifecycleCompute.Dispatch(_simulateKernel, DivideRoundUp(_profile.Capacity, 64), 1, 1);
            }
        }

        private void BindCommon(int kernel)
        {
            _lifecycleCompute.SetInt("_Capacity", _profile.Capacity);
            _lifecycleCompute.SetFloat("_LiquidLikeHoldSeconds", _phase.LiquidLikeHoldSeconds);
            _lifecycleCompute.SetFloat(
                "_SurfaceToGasBlendSeconds",
                _phase.SurfaceToGasBlendSeconds);
            _lifecycleCompute.SetFloat("_GasLifetimeSeconds", _phase.GasLifetimeSeconds);
            _lifecycleCompute.SetFloat("_InitialRadius", _phase.InitialRadius);
            _lifecycleCompute.SetFloat("_FinalRadius", _phase.FinalRadius);
            _lifecycleCompute.SetBuffer(kernel, StatesId, _stateBuffer);
        }

        private void OnDestroy()
        {
            _spawnBuffer?.Dispose();
            _stateBuffer?.Dispose();
            _drawArgs?.Dispose();
            _surfaceResources?.Dispose();
            if (_surfaceDepthMaterial != null)
            {
                if (Application.isPlaying) Destroy(_surfaceDepthMaterial);
                else DestroyImmediate(_surfaceDepthMaterial);
            }
        }

        private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
    }
}
