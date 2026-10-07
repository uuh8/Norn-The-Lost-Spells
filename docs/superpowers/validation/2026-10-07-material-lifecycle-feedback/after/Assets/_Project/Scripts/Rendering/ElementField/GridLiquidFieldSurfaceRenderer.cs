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
    /// 全材料共享一个外壳。Gameplay只读；提交两帧数量后，GPU每帧重建插值等值面，
    /// 旧三个动态组件保留引用，但此组件可工作时不再绘制重叠的透明壳。
    /// </summary>
    [DefaultExecutionOrder(5)]
    public sealed class GridLiquidFieldSurfaceRenderer : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private GridLiquidFieldRenderProfile _profile;
        [Header("Runtime Debug (Read Only)")]
        [SerializeField] private int _inputPages,_surfacePages,_subdivision,_smallDrops,_rebuiltSurfacePages;
        [SerializeField] private long _gpuBytes;
        [SerializeField] private float _alpha,_captureMilliseconds,_displayLagMilliseconds;
        [SerializeField] private bool _cpuFallback,_capacityOverflow;
        [SerializeField] private long _gcBytes;
        [SerializeField] private string _fallbackReason;
        [SerializeField] private int _visibleSurfacePages,_hybridPages,_culledSourcePages;
        private readonly MaterialPresentationView _view=new MaterialPresentationView();
        private Camera _viewCamera;
        private Vector3 _capturedViewCenter;
        private static readonly ProfilerMarker CaptureMarker=new ProfilerMarker("GridLiquid.Field.Capture");
        private static readonly ProfilerMarker DispatchMarker=new ProfilerMarker("GridLiquid.Field.Dispatch");
        private static readonly ProfilerMarker DrawMarker=new ProfilerMarker("GridLiquid.Field.Draw");
        private static readonly ProfilerMarker PageReuseMarker=new ProfilerMarker("GridLiquid.Field.PageReuse");
        private static readonly GridLiquidFieldSurfaceRenderer[] Registry=new GridLiquidFieldSurfaceRenderer[32];
        private LiquidWorldRuntime _source;
#if UNITY_EDITOR
        private LiquidWorldRuntime _validationSource;
        private bool _validationFreezeDispatch,_validationHideDraw,_validationForceRebuild;
        // 仅供Editor临时验证场景使用；正式场景仍从世界只读入口取得数据。
        public void ConfigureForValidation(LiquidWorldRuntime source,GridLiquidFieldRenderProfile profile)
        {if(_valid)throw new InvalidOperationException("验证源必须在Start之前配置。");_validationSource=source;_profile=profile;}
        public void SetValidationViewCamera(Camera camera){_viewCamera=camera;}
        public void SetValidationOpaque(bool value)=>SetValidationDiagnostic("_DebugForceOpaqueFragment",value?1:0);
        public void SetValidationDiagnostic(string parameter,float value)
        {if(_material!=null)_material.SetFloat(parameter,value);if(_cpu!=null)_cpu.Material.SetFloat(parameter,value);if(_overflowCpu!=null)_overflowCpu.Material.SetFloat(parameter,value);}
        // 显式Profiler隔离同一快照的Compute与Draw，只有Editor诊断能开；不修改世界数量。
        public void SetValidationRenderWork(bool freezeDispatch,bool hideDraw,bool forceRebuild)
        {_validationFreezeDispatch=freezeDispatch;_validationHideDraw=hideDraw;_validationForceRebuild=forceRebuild;}
        public void SetValidationAirSkip(bool value){if(_compute!=null)_compute.SetInt("_SkipAirRows",value?1:0);}
#endif
        private GridLiquidFieldBudget _budget;
        private GridLiquidFieldResources _gpu;
        private GraphicsBuffer _cpuOwnerBuffer,_overflowOwnerBuffer;
        private GraphicsBuffer _fallbackDropBuffer;
        private GridLiquidFieldFrame _frame;
        private GridLiquidFieldFrame _overflowFrame;
        private GridLiquidFieldSampler _sampler;
        private ComputeShader _compute;
        private Material _material,_dropMaterial;
        private Mesh _dropMesh;
        private RenderParams[] _draws;
        private RenderParams _dropDraw;
        private GridLiquidChunkLookup[] _gpuLookup,_gpuOwnerLookup,_ownerKeys;
        private Vector4[] _ownerBounds;
        // 几何Buffer长寿命，但旧全域Dispatch会被任意一团运动液体唤醒。
        // 按世界Key精确比较上一快照的数量/姿态/地形，保留不变页；不用Hash碰撞猜测相等。
        private Dictionary<ElementChunkKey,int> _cachedInputPages;
        private GridLiquidPackedCell[] _cachedCells;
        private Vector4[] _cachedTerrains,_cachedOwnerBounds,_gpuTerrains;
        private ElementChunkKey[] _cachedOwners;
        private bool[] _inputChanged,_inputAnimated,_ownerDirty,_ownerAnimated;
        private int _cachedSubdivision,_cachedOwnerCount,_animatedSurfacePages;
        private GridLiquidSmallDrop[] _drops;
        private GridLiquidFallingMotion[] _fallingMotion;
        private int _fallingMotionCount;
        private readonly float[] _weights=new float[8];
        private int _fieldKernel,_verticesKernel,_smoothKernel,_finishKernel,_normalsKernel,_facesKernel,_argsKernel,_clearKernel;
        private uint _version=uint.MaxValue,_epoch=uint.MaxValue;
        private float _capturedAt,_lastAlpha=-1;
        private bool _captured,_uploaded,_valid,_cpuDomainUploaded,_cpuDomainPendingUpload,_started;
        private int _gpuSurfacePages;
        private GridLiquidFieldCpuSurface _cpu;
        private GridLiquidFieldCpuSurface _overflowCpu;
        private bool _overflowDomainUploaded,_overflowPendingUpload,_hybrid;
        private float _overflowCapturedAt,_overflowVisibleAt,_hybridDisplayLagMilliseconds;
        public float HybridDisplayLagMilliseconds=>_hybridDisplayLagMilliseconds;
        public bool HasVisibleHybridCache=>_overflowCpu!=null && _overflowCpu.HasVisibleSurface;
        public bool IsReady=>_valid;
        public LiquidWorldRuntime Source=>_source;
        public GridLiquidFieldFrame Frame=>_captured?_frame:null;
        public long GpuAllocatedBytes=>_gpuBytes;
        public bool CpuFallback=>_cpuFallback;
        public int SurfacePages=>_surfacePages;
        public int RebuiltSurfacePages=>_rebuiltSurfacePages;
        public int VisibleSurfacePages=>_visibleSurfacePages;
        public int HybridPages=>_hybridPages;
        public int CulledSourcePages=>_culledSourcePages;
        public MaterialPresentationView PresentationView=>_view;
        public float InterpolationAlpha=>_alpha;
        public GraphicsBuffer DebugArguments=>_gpu?.Arguments;
        public GraphicsBuffer DebugVertices=>_gpu?.Vertices;
        public GraphicsBuffer DebugIndices=>_gpu?.Indices;
        public int SmallDropCount=>_smallDrops;
        public string FallbackReason=>_fallbackReason;
        public bool IsPresentationPending=>_cpuFallback && _cpu!=null && _cpu.IsBuilding;
        public static GridLiquidFieldSurfaceRenderer Find(ILiquidSurfaceReadOnly source)
        {for(int i=0;i<Registry.Length;i++){var r=Registry[i];if(r!=null && r.isActiveAndEnabled && r._valid && ReferenceEquals(r._source,source))return r;}return null;}
        private void Start()
        {
            if(_valid)return;_started=true;
            _source=_world!=null?_world.GridLiquids as LiquidWorldRuntime:null;
#if UNITY_EDITOR
            if(_validationSource!=null)_source=_validationSource;
#endif
            if(_source==null || _profile==null || _profile.Shader==null){enabled=false;return;}
            try
            {
                _budget=_profile.Budget(_source.ChunkSize,_source.MaterialCount);
                // 可见场保留全部候选；GPU预算只选择近处输出，远页有独立CPU快照，不阻塞新水。
                int frameCapacity=Mathf.Max(_budget.InputChunks,_source.MaximumSurfaceCandidateKeys*4);
                int table=1;while(table<frameCapacity*2)table<<=1;
                _frame=new GridLiquidFieldFrame(_source,frameCapacity,table,_profile.CartoonCrownStrength,_profile.CartoonVolumeScale);_sampler=new GridLiquidFieldSampler(_frame);
                _overflowFrame=new GridLiquidFieldFrame(_source,frameCapacity,table,_profile.CartoonCrownStrength,_profile.CartoonVolumeScale,true);
                // CPU Mesh仍由GPU光栅化。无Compute时只保留小型Owner表，裁掉Stable与CPU外壳重叠的面。
                long domainBytes=(long)_frame.OwnerLookup.Length*32;
                _budget=_profile.Budget(_source.ChunkSize,_source.MaterialCount,domainBytes);
                _cpuOwnerBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,_frame.OwnerLookup.Length,16);
                _overflowOwnerBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,_frame.OwnerLookup.Length,16);_gpuBytes=domainBytes;
                _gpuLookup=new GridLiquidChunkLookup[_budget.LookupSlots];_gpuOwnerLookup=new GridLiquidChunkLookup[_budget.LookupSlots];
                _ownerKeys=new GridLiquidChunkLookup[_budget.Views];_draws=new RenderParams[_budget.Views];_drops=new GridLiquidSmallDrop[65536];
                _frame.ConfigureFallingDrops(_drops.Length);
                _overflowFrame.ConfigureFallingDrops(_drops.Length);
                _fallingMotion=new GridLiquidFallingMotion[_drops.Length];
                _ownerBounds=new Vector4[_budget.Views*2];
                int cachedRows=_budget.InputChunks*_source.ChunkSize*_source.ChunkSize*_source.ChunkSize;
                _cachedInputPages=new Dictionary<ElementChunkKey,int>(_budget.InputChunks);
                _cachedCells=new GridLiquidPackedCell[cachedRows*_source.MaterialCount];_cachedTerrains=new Vector4[cachedRows];
                _gpuTerrains=new Vector4[cachedRows];
                _cachedOwners=new ElementChunkKey[_budget.Views];_cachedOwnerBounds=new Vector4[_budget.Views*2];
                _inputChanged=new bool[_budget.InputChunks];_inputAnimated=new bool[_budget.InputChunks];
                _ownerDirty=new bool[_budget.Views];_ownerAnimated=new bool[_budget.Views];
                _material=new Material(_profile.Shader);Material first=_profile.GetMaterial(0);if(first!=null)_material.CopyPropertiesFromMaterial(first);
                _material.enableInstancing=true;SetPalette(_material);ConfigureCartoonStyle(_material);
                _dropMaterial=new Material(_material);SetPalette(_dropMaterial);_dropMaterial.EnableKeyword("LIQUID_SMALL_DROP");
                _dropMesh=new Mesh{name="GridLiquid.SharedIcosphere"};_dropMesh.vertices=LiquidDropletTemplate.Vertices;_dropMesh.normals=LiquidDropletTemplate.Vertices;_dropMesh.triangles=LiquidDropletTemplate.Indices;
                _cpu=new GridLiquidFieldCpuSurface(_frame,_sampler,_material,_frame.Owners.Length,gameObject.layer);
                _overflowCpu=new GridLiquidFieldCpuSurface(_overflowFrame,new GridLiquidFieldSampler(_overflowFrame),_material,_overflowFrame.Owners.Length,gameObject.layer);
                // Material克隆不会复制未在Properties声明的数组；每条实际绘制路径显式绑定Palette。
                SetPalette(_cpu.Material);
                SetPalette(_overflowCpu.Material);
                if(SystemInfo.supportsComputeShaders && _profile.Compute!=null)
                {
                    try
                    {
                    _compute=Instantiate(_profile.Compute);_gpu=new GridLiquidFieldResources(_budget);_gpuBytes+=_gpu.AllocatedBytes;
                    _fieldKernel=_compute.FindKernel("SampleField");_verticesKernel=_compute.FindKernel("BuildVertices");_smoothKernel=_compute.FindKernel("SmoothVertices");
                    _finishKernel=_compute.FindKernel("FinishVertices");_normalsKernel=_compute.FindKernel("FinishNormals");_facesKernel=_compute.FindKernel("BuildFaces");_argsKernel=_compute.FindKernel("FinishArguments");_clearKernel=_compute.FindKernel("ClearCounters");
                    // 平台支持Compute不代表每个Kernel都满足设备要求；初始化失败仍保留CPU完整显示路径。
                    if(!_compute.IsSupported(_fieldKernel) || !_compute.IsSupported(_verticesKernel) || !_compute.IsSupported(_smoothKernel)
                        || !_compute.IsSupported(_finishKernel) || !_compute.IsSupported(_normalsKernel) || !_compute.IsSupported(_facesKernel) || !_compute.IsSupported(_argsKernel) || !_compute.IsSupported(_clearKernel))
                        throw new InvalidOperationException("A continuous liquid Compute kernel is unsupported.");
                    _gpu.Bind(_compute,_fieldKernel);_gpu.Bind(_compute,_verticesKernel);_gpu.Bind(_compute,_smoothKernel);_gpu.Bind(_compute,_finishKernel);
                    _gpu.Bind(_compute,_normalsKernel);_gpu.Bind(_compute,_facesKernel);_gpu.Bind(_compute,_argsKernel);_gpu.Bind(_compute,_clearKernel);
                    _compute.SetInt("_K",_source.ChunkSize);_compute.SetInt("_MaterialCount",_source.MaterialCount);_compute.SetInt("_GroupCount",_source.DensityGroupCount);
                    var groups0=new int[4];var groups1=new int[4];Array.Copy(_frame.Groups,0,groups0,0,4);Array.Copy(_frame.Groups,4,groups1,0,4);
                    _compute.SetInts("_Groups0",groups0);_compute.SetInts("_Groups1",groups1);_compute.SetInt("_LookupMask",_budget.LookupSlots-1);
                    _compute.SetVector("_OriginSize",new Vector4(_source.Origin.x,_source.Origin.y,_source.Origin.z,_source.CellSize));_compute.SetFloat("_Capacity",_source.CellCapacityGmu);
                    _compute.SetFloat("_CrownStrength",_frame.CrownStrength);
                    _compute.SetFloat("_CartoonVolumeScale",_frame.CartoonVolumeScale);
                    _compute.SetInt("_SkipAirRows",1);
                    for(int i=0;i<_draws.Length;i++)
                    {
                        var props=new MaterialPropertyBlock();props.SetBuffer("_FieldVertices",_gpu.Vertices);
                        _draws[i]=new RenderParams(_material){layer=gameObject.layer,shadowCastingMode=ShadowCastingMode.Off,receiveShadows=true,matProps=props};
                    }
                    var dropProps=new MaterialPropertyBlock();dropProps.SetBuffer("_SmallDrops",_gpu.Drops);
                    _dropDraw=new RenderParams(_dropMaterial){layer=gameObject.layer,shadowCastingMode=ShadowCastingMode.Off,receiveShadows=true,matProps=dropProps};
                    }
                    catch(Exception exception)
                    {
                        // Compute资源失败只切换Presentation，不能关闭Renderer让世界液量突然消失。
                        _gpu?.Dispose();_gpu=null;_gpuBytes=domainBytes;_fallbackReason="GPU resource initialization: "+exception.Message;
                        if(_compute!=null){Release(_compute);_compute=null;}
                        GameLog.Warn(_fallbackReason,"GridLiquidField");
                    }
                }
                else _fallbackReason=SystemInfo.supportsComputeShaders?"Compute reference missing; CPU reference surface.":"Compute unsupported; CPU reference surface.";
                // 少量水滴只需 Vertex Shader 读取实例缓冲；CPU 表面回退也必须显示它们。
                // GPU 路径复用预算中已有 Drops；只有无 Compute 路径额外分配同样固定大小的缓冲。
                if(_gpu==null){_fallbackDropBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,_drops.Length,32);_gpuBytes+=(long)_drops.Length*32;}
                var fallingProps=new MaterialPropertyBlock();fallingProps.SetBuffer("_SmallDrops",_gpu!=null?_gpu.Drops:_fallbackDropBuffer);
                _dropDraw=new RenderParams(_dropMaterial){layer=gameObject.layer,shadowCastingMode=ShadowCastingMode.Off,receiveShadows=true,matProps=fallingProps};
                for(int i=0;i<Registry.Length;i++)if(Registry[i]==null){Registry[i]=this;break;}
                _valid=true;
            }
            catch(Exception e){GameLog.Error(e.Message,"GridLiquidField");Dispose();enabled=false;}
        }
        private void SetPalette(Material target)
        {
            var shallow=new Vector4[8];var deep=new Vector4[8];var fresnel=new Vector4[8];var foam=new Vector4[8];var alpha=new Vector4[8];var surface=new Vector4[8];
            for(int i=0;i<_source.MaterialCount;i++)
            {
                Material m=_profile.GetMaterial(i);if(m==null)m=target;
                shallow[i]=m.GetColor("_ShallowColor");deep[i]=m.GetColor("_DeepColor");fresnel[i]=m.GetColor("_FresnelColor");foam[i]=m.GetColor("_FoamColor");
                alpha[i]=new Vector4(m.GetFloat("_ShallowAlpha"),m.GetFloat("_DeepAlpha"),m.GetFloat("_FresnelAlphaStrength"),m.GetFloat("_WaterAlpha"));surface[i].x=m.GetFloat("_Smoothness");
                float art=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.15f,.8f,_profile.CartoonCrownStrength));
                // 宽白Fresnel会吞掉圆冠的体色；保留各材料Palette身份，压缩白边并让高光更集中。
                fresnel[i]=Vector4.Lerp(fresnel[i],shallow[i],.4f*art);surface[i].x=Mathf.Lerp(surface[i].x,.78f,art);
                // 卡通液团保留主体色和侧壁；大量透明/白泡沫会把真正厚的Mesh照成贴地薄膜。
                alpha[i].x=Mathf.Lerp(alpha[i].x,.94f,art);alpha[i].y=Mathf.Lerp(alpha[i].y,.94f,art);
            }
            target.SetVectorArray("_PaletteShallow",shallow);target.SetVectorArray("_PaletteDeep",deep);target.SetVectorArray("_PaletteFresnel",fresnel);
            target.SetVectorArray("_PaletteFoam",foam);target.SetVectorArray("_PaletteAlpha",alpha);target.SetVectorArray("_PaletteSurface",surface);target.SetInt("_PaletteCount",_source.MaterialCount);
            target.SetVector("_PaletteGroups0",new Vector4(_frame.Groups[0],_frame.Groups[1],_frame.Groups[2],_frame.Groups[3]));
            target.SetVector("_PaletteGroups1",new Vector4(_frame.Groups[4],_frame.Groups[5],_frame.Groups[6],_frame.Groups[7]));
            target.SetInt("_PaletteGroupCount",_source.DensityGroupCount);
        }
        private void ConfigureCartoonStyle(Material target)
        {
            float art=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.15f,.8f,_profile.CartoonCrownStrength));
            target.SetFloat("_WaveScaleA",Mathf.Lerp(target.GetFloat("_WaveScaleA"),3,art));
            target.SetFloat("_WaveScaleB",Mathf.Lerp(target.GetFloat("_WaveScaleB"),4,art));
            target.SetFloat("_WaveNormalStrength",Mathf.Lerp(target.GetFloat("_WaveNormalStrength"),.04f,art));
            target.SetFloat("_FoamStrength",Mathf.Lerp(target.GetFloat("_FoamStrength"),.18f,art));
            target.SetFloat("_DepthFadeDistance",Mathf.Lerp(target.GetFloat("_DepthFadeDistance"),.6f,art));
            target.SetFloat("_CartoonSwellAmplitude",.015f*art);
        }
        private static void Insert(GridLiquidChunkLookup[] table,ElementChunkKey key,int slot)
        {
            int mask=table.Length-1,h=(int)(GridLiquidFieldGpuLayout.Hash(key.X,key.Y,key.Z)&(uint)mask);
            while(table[h].Slot!=0)h=(h+1)&mask;table[h]=new GridLiquidChunkLookup(key.X,key.Y,key.Z,slot);
        }
        private void Capture()
        {
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            using(CaptureMarker.Auto())_frame.Capture(_view.Enabled?_view:null);
            // 在 GPU 页面压紧前保留整个可见域的水滴；CPU 远页也跳过相同标记，只由此实例批次绘制一次。
            _fallingMotionCount=_frame.CopyFallingMotion(_fallingMotion);
            _captureMilliseconds=(float)((System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency);
            _version=_source.SurfaceVersion;_epoch=_source.TransportEpoch;_capturedAt=Time.time;_captured=true;
            _visibleSurfacePages=_frame.OwnerCount;_culledSourcePages=_frame.CulledSourcePages;_capacityOverflow=_frame.Overflow;
            _capturedViewCenter=_view.Center;
            _cpuFallback=_gpu==null || _frame.Overflow || _budget.FineViews==0;
            _lastAlpha=-1;
            if(_cpuFallback)
            {
                // 已完成CPU域在下一批构建期间仍有效；上传请求与可见域有效性不能混为一个Bool。
                _inputPages=_frame.Count;_surfacePages=_frame.OwnerCount;_subdivision=2;_cpuDomainPendingUpload=true;
                _hybrid=false;_hybridPages=0;_overflowCpu.Cancel();_overflowDomainUploaded=_overflowPendingUpload=false;
                if(_gpu!=null)_fallbackReason="Presentation snapshot capacity exceeded; CPU reference surface.";
                _cpu.Begin();return;
            }
            bool copiedOverflow=!_overflowCpu.IsBuilding && (_frame.Count>_budget.InputChunks || _frame.OwnerCount>_budget.FineViews);
            if(copiedOverflow){_overflowFrame.CopyFrom(_frame);_overflowCapturedAt=Time.time;}
            _frame.SelectGpuDomain(_budget.InputChunks,_budget.FineViews);
            _inputPages=_frame.Count;_surfacePages=_frame.OwnerCount;
            _hybridPages=_visibleSurfacePages-_surfacePages;_hybrid=_hybridPages>0;
            if(_hybrid && copiedOverflow)
            {
                // 缓存完整可见快照，Fragment实时裁GPU域。若只构建当前远页，玩家移动后
                // 原GPU页降为CPU页却从未缓存，会产生新的隐形边界。
                _overflowCpu.Begin();_overflowPendingUpload=true;
            }
            if(!_hybrid){_overflowCpu.Cancel();_overflowDomainUploaded=_overflowPendingUpload=false;}
            _uploaded=false;_fallbackReason="";
            _cpu.ClearVisible();_cpuDomainUploaded=_cpuDomainPendingUpload=false;
            // 实际Mesh体积校准证明r1会严重低估一层水；保留粗Gameplay Grid，显示至少r2。
            // 预算容不下全部r2时独立CPU补足远页，不能靠撤掉薄层满足预算。
            _subdivision=2;
            // 小液团收拢后，r2边缘只有少量三角形。复用已分配Buffer的空闲容量升到r3；
            // 大湖/大量活动页仍用r2，既不细化Gameplay Grid，也不扩大显存预算。
            int k=_source.ChunkSize,n3=k*3,cube3=(n3+6)*(n3+6)*(n3+6),node3=(n3+7)*(n3+7)*(n3+7);
            int capacity3=Math.Min(_budget.MaximumCubeSlots/cube3,Math.Min(_budget.MaximumNodeSlots/node3,_budget.MaximumIndices/(18*n3*n3*n3)));
            if(_frame.CrownStrength>.15f && _surfacePages<=capacity3)_subdivision=3;
            Array.Clear(_gpuLookup,0,_gpuLookup.Length);Array.Clear(_gpuOwnerLookup,0,_gpuOwnerLookup.Length);
            for(int i=0;i<_frame.Count;i++)Insert(_gpuLookup,_frame.Keys[i],i+1);
            for(int i=0;i<_frame.OwnerCount;i++){var key=_frame.Owners[i];_ownerKeys[i]=new GridLiquidChunkLookup(key.X,key.Y,key.Z,i+1);Insert(_gpuOwnerLookup,key,i+1);
                var b=_frame.OwnerBounds(i);_ownerBounds[i*2]=new Vector4(b.min.x,b.min.y,b.min.z,0);_ownerBounds[i*2+1]=new Vector4(b.max.x,b.max.y,b.max.z,0);}
            using(PageReuseMarker.Auto())PreparePageReuse();
            int cells=_frame.Count*_source.ChunkSize*_source.ChunkSize*_source.ChunkSize;
            if(cells>0){_gpu.Previous.SetData(_frame.Previous,0,0,cells*_source.MaterialCount);_gpu.Current.SetData(_frame.Current,0,0,cells*_source.MaterialCount);_gpu.Terrain.SetData(_gpuTerrains,0,0,cells);}
            _gpu.Lookup.SetData(_gpuLookup);_gpu.OwnerLookup.SetData(_gpuOwnerLookup);if(_surfacePages>0){_gpu.Owners.SetData(_ownerKeys,0,0,_surfacePages);_gpu.OwnerBounds.SetData(_ownerBounds,0,0,_surfacePages*2);}
            _uploaded=true;_gpuSurfacePages=_surfacePages;
        }
        private void LateUpdate()
        {
            if(!_valid || _source==null || !_source.IsFluidInitialized || _frame==null)return;
            _rebuiltSurfacePages=0;
            long before=GC.GetAllocatedBytesForCurrentThread();
            // Camera.main只在引用缺失时解析，避免Scene查找热路径。Unity销毁相机时会呈现null。
            if(_viewCamera==null || !_viewCamera.isActiveAndEnabled)_viewCamera=Camera.main;
            Vector3 viewCenter=_world!=null?_world.InterestPosition:_viewCamera!=null?_viewCamera.transform.position:transform.position;
            _view.Update(_viewCamera,viewCenter,_profile.VisibilityFadeStart,_profile.VisibilityFadeEnd,_profile.ViewPadding,_profile.CullByCamera);
            _view.Bind(_material);_view.Bind(_cpu.Material);_view.Bind(_overflowCpu.Material);
            _view.Bind(_dropMaterial);
            bool viewChanged=_captured && (_frame.ViewSignature(_view.Enabled?_view:null)!=_frame.SelectionSignature
                || _hybrid && (viewCenter-_capturedViewCenter).sqrMagnitude>_source.CellSize*_source.CellSize);
            if(viewChanged && IsPresentationPending)_cpu.CancelBuild();
            if(!IsPresentationPending && (!_captured || _version!=_source.SurfaceVersion
                || viewChanged || _epoch!=_source.TransportEpoch && (_source.ActiveCellCount>0 || _frame.Animated)))Capture();
            _alpha=Mathf.Clamp01((Time.time-_capturedAt)/_frame.Interval);
            // 插值区间正常落后一个Tick；CPU分批构造期间若快照停滞，指标继续增加，不能固定报100ms。
            _displayLagMilliseconds=_frame.Animated || IsPresentationPending
                ?(Time.time-_capturedAt+_frame.Interval*(1-_alpha))*1000:0;
            if(_cpuFallback)
            {
                // 首个CPU批次完成前继续显示上一套GPU结果，超预算并不代表撤掉可见液体。
                // 首次回退保留GPU；已有完整CPU结果时只画那份CPU，避免两份旧壳叠画。
                // 首次 CPU 回退保留旧连续 Mesh；水滴统一在下面更新并画一次，防止旧/新实例重复叠画。
                if(_cpu.IsBuilding && !_cpu.HasVisibleSurface && _uploaded)DrawGpu(false);
                _cpu.UpdateAndDraw();
                UpdateDrops();DrawDrops();
                if(!_cpu.IsBuilding && _cpuDomainPendingUpload)
                {_cpuOwnerBuffer.SetData(_frame.OwnerLookup);_cpuDomainUploaded=true;_cpuDomainPendingUpload=false;}
                _gcBytes+=GC.GetAllocatedBytesForCurrentThread()-before;return;
            }
            bool rebuild=(_animatedSurfacePages>0 && _alpha!=_lastAlpha) || _lastAlpha<0;
#if UNITY_EDITOR
            rebuild=(rebuild || _validationForceRebuild) && !_validationFreezeDispatch;
#endif
            if(_uploaded && rebuild)
            {
                using(DispatchMarker.Auto())Dispatch();_lastAlpha=_alpha;
            }
            UpdateDrops();
            DrawGpu();
            if(_hybrid)
            {
                // GPU域实时裁掉旧CPU快照的重复部分，角色周围的颜色/反应显示始终来自新Tick。
                BindGpuDomain(_overflowCpu.Material);
                _overflowCpu.UpdateAndDraw();
                if(!_overflowCpu.IsBuilding && _overflowPendingUpload)
                {_overflowOwnerBuffer.SetData(_overflowFrame.OwnerLookup);_overflowDomainUploaded=true;_overflowPendingUpload=false;_overflowVisibleAt=_overflowCapturedAt;}
            }
            _hybridDisplayLagMilliseconds=_hybrid?(Time.time-(_overflowCpu.HasVisibleSurface?_overflowVisibleAt:_overflowCapturedAt))*1000:0;
            _gcBytes+=GC.GetAllocatedBytesForCurrentThread()-before;
        }
        private void DrawGpu(bool drawDrops=true)
        {
#if UNITY_EDITOR
            if(_validationHideDraw)return;
#endif
            using(DrawMarker.Auto())
            {
                int k=_source.ChunkSize;float span=k*_source.CellSize;
                for(int i=0;i<_gpuSurfacePages;i++)
                {
                    var key=_ownerKeys[i];_draws[i].worldBounds=new Bounds(_source.Origin+new Vector3((key.X+.5f)*span,(key.Y+.5f)*span,(key.Z+.5f)*span),Vector3.one*(span+_source.CellSize*8));
                    Graphics.RenderPrimitivesIndexedIndirect(_draws[i],MeshTopology.Triangles,_gpu.Indices,_gpu.Arguments,1,i);
                }
                if(drawDrops)DrawDrops();
            }
        }
        private void Dispatch()
        {
            if(_surfacePages==0)return;int n=_source.ChunkSize*_subdivision,node=n+7,cube=n+6;
            for(int i=0;i<_surfacePages;i++)
            {
                bool changed=_ownerDirty[i] || _ownerAnimated[i] && _alpha!=_lastAlpha;
#if UNITY_EDITOR
                changed|=_validationForceRebuild;
#endif
                // OwnerKeys.w原先只是冗余序号（WorldCell只读xyz）。复用为工作Mask，
                // 不增加显存/UAV；所有Kernel共用Mask，尤其不能清掉未更新页的Indirect Args。
                // 相同两帧且没有速度的页固定取Current端点，消除Alpha分支造成的ULP/Half舍入抖动。
                // 高位标记静态页；普通显式Compute输入的低位序号仍沿原Alpha插值。
                _ownerKeys[i].Slot=changed?(_ownerAnimated[i]?1:1073741825):0;if(changed)_rebuiltSurfacePages++;
            }
            if(_rebuiltSurfacePages==0)return;
            _gpu.Owners.SetData(_ownerKeys,0,0,_surfacePages);
            _compute.SetInt("_OwnerCount",_surfacePages);_compute.SetInt("_Resolution",_subdivision);_compute.SetInt("_NodeSide",node);_compute.SetInt("_CubeSide",cube);
            _compute.SetInt("_IndexCapacity",18*n*n*n);_compute.SetFloat("_Alpha",_alpha);_compute.SetFloat("_Interval",_frame.Interval);
            _compute.Dispatch(_clearKernel,(_surfacePages+63)/64,1,1);
            _compute.Dispatch(_fieldKernel,(node*node*node*_surfacePages+63)/64,1,1);
            int cubes=(cube*cube*cube*_surfacePages+63)/64;_compute.Dispatch(_verticesKernel,cubes,1,1);
            _compute.SetInt("_SmoothPass",0);_compute.Dispatch(_smoothKernel,cubes,1,1);_compute.SetInt("_SmoothPass",1);_compute.Dispatch(_smoothKernel,cubes,1,1);
            _compute.Dispatch(_finishKernel,cubes,1,1);
            // 法线必须在代理形变/接地完成后重建，不能拿粗节点的底面梯度给顶面打光。
            _compute.Dispatch(_normalsKernel,cubes,1,1);
            _compute.Dispatch(_facesKernel,(n*n*n*_surfacePages+63)/64,1,1);_compute.Dispatch(_argsKernel,(_surfacePages+63)/64,1,1);
            Array.Clear(_ownerDirty,0,_surfacePages);
        }
        private static bool SameCell(in GridLiquidPackedCell a,in GridLiquidPackedCell b)
        {
            // 空格的显示Flag/姿态没有场贡献；湿格逐位比较，包含支撑、圆冠、Stable标记和材料量。
            if(((a.QuantityFlags|b.QuantityFlags)&65535u)==0)return true;
            return a.QuantityFlags==b.QuantityFlags && a.VelocityXY==b.VelocityXY && a.VelocityZOffsetX==b.VelocityZOffsetX && a.OffsetYZ==b.OffsetYZ;
        }
        private static bool SameVector(in Vector4 a,in Vector4 b)=>a.x==b.x && a.y==b.y && a.z==b.z && a.w==b.w;
        private void PreparePageReuse()
        {
            int cells=_source.ChunkSize*_source.ChunkSize*_source.ChunkSize,materials=_source.MaterialCount;
            for(int page=0;page<_inputPages;page++)
            {
                bool known=_cachedInputPages.TryGetValue(_frame.Keys[page],out int oldPage),changed=!known,animated=false;
                for(int local=0;local<cells;local++)
                {
                    int row=(page*cells+local)*materials,oldRow=(oldPage*cells+local)*materials;bool wet=false,anyPhaseWet=false;
                    for(int m=0;m<materials;m++)
                    {
                        ref readonly var current=ref _frame.Current[row+m];
                        anyPhaseWet|=((current.QuantityFlags|_frame.Previous[row+m].QuantityFlags)&65535u)!=0;
                        animated|=!SameCell(in current,in _frame.Previous[row+m]);
                        // 即使两帧Packed数据相等，非零速度仍在Previous/Current两侧前推；
                        // 这种页必须继续插值，不能把“数量不变”当成“几何不动”。
                        animated|=(current.QuantityFlags&65535u)!=0 && (current.VelocityXY!=0 || (current.VelocityZOffsetX&65535u)!=0);
                        if(known){ref readonly var old=ref _cachedCells[oldRow+m];wet|=((current.QuantityFlags|old.QuantityFlags)&65535u)!=0;changed|=!SameCell(in current,in old);}
                    }
                    if(known && wet)changed|=!SameVector(in _frame.Terrains[page*cells+local],in _cachedTerrains[oldPage*cells+local]);
                    // 空气没有支撑面。只在GPU上传副本的w放+Infinity哨兵，先读一个标量
                    // 就能跳过两帧皆空的整行，省去各材料的Packed读取；原Frame地形完全不改。
                    // Previous-only必须保留真实Terrain，不能按Current数量判断是否为空气。
                    var terrain=_frame.Terrains[page*cells+local];if(!anyPhaseWet)terrain.w=float.PositiveInfinity;_gpuTerrains[page*cells+local]=terrain;
                }
                _inputChanged[page]=changed;_inputAnimated[page]=animated;
            }
            int halo=Mathf.CeilToInt(5f/_source.ChunkSize);_animatedSurfacePages=0;
            for(int owner=0;owner<_surfacePages;owner++)
            {
                var key=_frame.Owners[owner];
                // 世界页换槽、r2/r3切换、Bounds变化都改变Buffer寻址或裁剪，必须重新生成。
                // 上一批正在插值的页转为静止时还需生成Current端点，不能冻结在半途形态。
                bool dirty=owner>=_cachedOwnerCount || _cachedSubdivision!=_subdivision || !_cachedOwners[owner].Equals(key) || _ownerDirty[owner] || _ownerAnimated[owner]
                    || !SameVector(in _ownerBounds[owner*2],in _cachedOwnerBounds[owner*2]) || !SameVector(in _ownerBounds[owner*2+1],in _cachedOwnerBounds[owner*2+1]);
                bool animated=false;
                for(int z=-halo;z<=halo;z++)for(int y=-halo;y<=halo;y++)for(int x=-halo;x<=halo;x++)
                {var neighbor=new ElementChunkKey(key.X+x,key.Y+y,key.Z+z);int page=_frame.InputPage(neighbor);if(page<0){dirty|=_cachedInputPages.ContainsKey(neighbor);continue;}dirty|=_inputChanged[page];animated|=_inputAnimated[page];}
                _ownerDirty[owner]=dirty;_ownerAnimated[owner]=animated;if(animated)_animatedSurfacePages++;_cachedOwners[owner]=key;
            }
            // 先完成全部旧槽比较再覆盖缓存，避免页压紧/排序时提前覆盖另一个页的参照。
            int rows=_inputPages*cells;Array.Copy(_frame.Current,_cachedCells,rows*materials);Array.Copy(_frame.Terrains,_cachedTerrains,rows);
            Array.Copy(_ownerBounds,_cachedOwnerBounds,_surfacePages*2);_cachedSubdivision=_subdivision;_cachedOwnerCount=_surfacePages;
            _cachedInputPages.Clear();for(int page=0;page<_inputPages;page++)_cachedInputPages.Add(_frame.Keys[page],page);
        }
        private void UpdateDrops()
        {
            _smallDrops=0;
            for(int i=0;i<_fallingMotionCount;i++)
            {var drop=_fallingMotion[i].Sample(_alpha,_frame.Interval,_frame.CellSize,_frame.CartoonVolumeScale/_frame.Capacity);if(drop.Radius>0)_drops[_smallDrops++]=drop;}
            if(_smallDrops==0)return;
            (_gpu!=null?_gpu.Drops:_fallbackDropBuffer).SetData(_drops,0,0,_smallDrops);
            // RenderMeshPrimitives 不知道程序化实例的位置；正确 World Bounds 才不会在转镜头时被误剔除。
            var first=_drops[0];var bounds=new Bounds(first.Position,Vector3.one*(first.Radius*3));
            for(int i=1;i<_smallDrops;i++) bounds.Encapsulate(new Bounds(_drops[i].Position,Vector3.one*(_drops[i].Radius*3)));
            _dropDraw.worldBounds=bounds;
        }
        private void DrawDrops()
        {if(_smallDrops>0)Graphics.RenderMeshPrimitives(_dropDraw,_dropMesh,0,_smallDrops);}
        public void BindStableDomain(MaterialPropertyBlock properties)
        {
            bool cpuDomain=_cpuFallback && _cpuDomainUploaded;
            // 裁剪必须跟随“实际正在显示”的完整快照。CPU首批未完成时旧GPU还在画，
            // 后续CPU批次则保留旧CPU域，不能提前裁新域或让Stable在同一处重复铺面。
            bool retainedGpu=_cpuFallback && _cpu!=null && _cpu.IsBuilding && !_cpu.HasVisibleSurface && _uploaded;
            bool active=_cpuFallback?cpuDomain || retainedGpu:_uploaded;
            properties.SetInt("_LiquidDomainEnabled",active?1:0);
            properties.SetInt("_LiquidSecondaryDomainEnabled",_hybrid && _overflowDomainUploaded?1:0);
            if(_hybrid && _overflowDomainUploaded)
            {properties.SetBuffer("_LiquidSecondaryOwnerLookup",_overflowOwnerBuffer);properties.SetInt("_LiquidSecondaryOwnerLookupMask",_overflowFrame.OwnerLookup.Length-1);}
            if(!active)return;properties.SetBuffer("_LiquidOwnerLookup",cpuDomain?_cpuOwnerBuffer:_gpu.OwnerLookup);properties.SetInt("_LiquidOwnerLookupMask",cpuDomain?_frame.OwnerLookup.Length-1:_budget.LookupSlots-1);
            properties.SetVector("_LiquidDomainOriginSize",new Vector4(_source.Origin.x,_source.Origin.y,_source.Origin.z,_source.CellSize));properties.SetInt("_LiquidDomainChunkSize",_source.ChunkSize);
        }
        private void BindGpuDomain(Material material)
        {
            material.SetInt("_LiquidDomainEnabled",_uploaded?1:0);material.SetInt("_LiquidSecondaryDomainEnabled",0);
            if(!_uploaded)return;
            material.SetBuffer("_LiquidOwnerLookup",_gpu.OwnerLookup);material.SetInt("_LiquidOwnerLookupMask",_budget.LookupSlots-1);
            material.SetVector("_LiquidDomainOriginSize",new Vector4(_source.Origin.x,_source.Origin.y,_source.Origin.z,_source.CellSize));material.SetInt("_LiquidDomainChunkSize",_source.ChunkSize);
        }
        private void Dispose()
        {
            _valid=_captured=_uploaded=_cpuDomainUploaded=_cpuDomainPendingUpload=false;_gpuSurfacePages=_smallDrops=0;
            _gpu?.Dispose();_gpu=null;_cpu?.Dispose();_cpu=null;_overflowCpu?.Dispose();_overflowCpu=null;
            _fallbackDropBuffer?.Dispose();_fallbackDropBuffer=null;
            _cpuOwnerBuffer?.Dispose();_cpuOwnerBuffer=null;_overflowOwnerBuffer?.Dispose();_overflowOwnerBuffer=null;_gpuBytes=0;
            _hybrid=_overflowDomainUploaded=_overflowPendingUpload=false;_hybridPages=0;
            _cachedSubdivision=_cachedOwnerCount=_animatedSurfacePages=0;_cachedInputPages?.Clear();
            if(_material!=null){Release(_material);_material=null;}if(_dropMaterial!=null){Release(_dropMaterial);_dropMaterial=null;}if(_dropMesh!=null){Release(_dropMesh);_dropMesh=null;}if(_compute!=null){Release(_compute);_compute=null;}
            for(int i=0;i<Registry.Length;i++)if(Registry[i]==this)Registry[i]=null;
        }
        private void OnEnable(){if(_started && !_valid)Start();}
        private static void Release(UnityEngine.Object value)
        {if(value==null)return;if(Application.isPlaying)Destroy(value);else DestroyImmediate(value);}
        private void OnDisable()=>Dispose();
        private void OnDestroy()=>Dispose();
    }
}
