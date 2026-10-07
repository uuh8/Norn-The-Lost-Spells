using System;
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
        [SerializeField] private int _inputPages,_surfacePages,_subdivision,_smallDrops;
        [SerializeField] private long _gpuBytes;
        [SerializeField] private float _alpha,_captureMilliseconds,_displayLagMilliseconds;
        [SerializeField] private bool _cpuFallback,_capacityOverflow;
        [SerializeField] private long _gcBytes;
        [SerializeField] private string _fallbackReason;
        private static readonly ProfilerMarker CaptureMarker=new ProfilerMarker("GridLiquid.Field.Capture");
        private static readonly ProfilerMarker DispatchMarker=new ProfilerMarker("GridLiquid.Field.Dispatch");
        private static readonly ProfilerMarker DrawMarker=new ProfilerMarker("GridLiquid.Field.Draw");
        private static readonly GridLiquidFieldSurfaceRenderer[] Registry=new GridLiquidFieldSurfaceRenderer[32];
        private LiquidWorldRuntime _source;
#if UNITY_EDITOR
        private LiquidWorldRuntime _validationSource;
        private bool _validationFreezeDispatch,_validationHideDraw,_validationForceRebuild;
        // 仅供Editor临时验证场景使用；正式场景仍从世界只读入口取得数据。
        public void ConfigureForValidation(LiquidWorldRuntime source,GridLiquidFieldRenderProfile profile)
        {if(_valid)throw new InvalidOperationException("验证源必须在Start之前配置。");_validationSource=source;_profile=profile;}
        public void SetValidationOpaque(bool value){if(_material!=null)_material.SetFloat("_DebugForceOpaqueFragment",value?1:0);if(_cpu!=null)_cpu.Material.SetFloat("_DebugForceOpaqueFragment",value?1:0);}
        public void SetValidationDiagnostic(string parameter,float value){if(_material!=null)_material.SetFloat(parameter,value);if(_cpu!=null)_cpu.Material.SetFloat(parameter,value);}
        // 显式Profiler隔离同一快照的Compute与Draw，只有Editor诊断能开；不修改世界数量。
        public void SetValidationRenderWork(bool freezeDispatch,bool hideDraw,bool forceRebuild)
        {_validationFreezeDispatch=freezeDispatch;_validationHideDraw=hideDraw;_validationForceRebuild=forceRebuild;}
#endif
        private GridLiquidFieldBudget _budget;
        private GridLiquidFieldResources _gpu;
        private GraphicsBuffer _cpuOwnerBuffer;
        private GridLiquidFieldFrame _frame;
        private GridLiquidFieldSampler _sampler;
        private ComputeShader _compute;
        private Material _material,_dropMaterial;
        private Mesh _dropMesh;
        private RenderParams[] _draws;
        private RenderParams _dropDraw;
        private GridLiquidChunkLookup[] _gpuLookup,_gpuOwnerLookup,_ownerKeys;
        private Vector4[] _ownerBounds;
        private GridLiquidSmallDrop[] _drops;
        private readonly float[] _weights=new float[8];
        private int _fieldKernel,_verticesKernel,_smoothKernel,_finishKernel,_normalsKernel,_facesKernel,_argsKernel,_clearKernel;
        private uint _version=uint.MaxValue,_epoch=uint.MaxValue;
        private float _capturedAt,_lastAlpha=-1;
        private bool _captured,_uploaded,_valid,_cpuDomainUploaded,_cpuDomainPendingUpload,_started;
        private int _gpuSurfacePages;
        private GridLiquidFieldCpuSurface _cpu;
        public bool IsReady=>_valid;
        public LiquidWorldRuntime Source=>_source;
        public GridLiquidFieldFrame Frame=>_captured?_frame:null;
        public long GpuAllocatedBytes=>_gpuBytes;
        public bool CpuFallback=>_cpuFallback;
        public int SurfacePages=>_surfacePages;
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
                // CPU快照保留超预算候选，GPU受128MiB限制时整体转CPU，不裁掉远处液体。
                int frameCapacity=Mathf.Max(_budget.InputChunks,_source.MaximumSurfaceCandidateKeys*4);
                int table=1;while(table<frameCapacity*2)table<<=1;
                _frame=new GridLiquidFieldFrame(_source,frameCapacity,table,_profile.CartoonCrownStrength);_sampler=new GridLiquidFieldSampler(_frame);
                // CPU Mesh仍由GPU光栅化。无Compute时只保留小型Owner表，裁掉Stable与CPU外壳重叠的面。
                long domainBytes=(long)_frame.OwnerLookup.Length*16;
                _budget=_profile.Budget(_source.ChunkSize,_source.MaterialCount,domainBytes);
                _cpuOwnerBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,_frame.OwnerLookup.Length,16);_gpuBytes=domainBytes;
                _gpuLookup=new GridLiquidChunkLookup[_budget.LookupSlots];_gpuOwnerLookup=new GridLiquidChunkLookup[_budget.LookupSlots];
                _ownerKeys=new GridLiquidChunkLookup[_budget.Views];_draws=new RenderParams[_budget.Views];_drops=new GridLiquidSmallDrop[65536];
                _ownerBounds=new Vector4[_budget.Views*2];
                _material=new Material(_profile.Shader);Material first=_profile.GetMaterial(0);if(first!=null)_material.CopyPropertiesFromMaterial(first);
                _material.enableInstancing=true;SetPalette(_material);ConfigureCartoonStyle(_material);
                _dropMaterial=new Material(_material);SetPalette(_dropMaterial);_dropMaterial.EnableKeyword("LIQUID_SMALL_DROP");
                _dropMesh=new Mesh{name="GridLiquid.SharedIcosphere"};_dropMesh.vertices=LiquidDropletTemplate.Vertices;_dropMesh.normals=LiquidDropletTemplate.Vertices;_dropMesh.triangles=LiquidDropletTemplate.Indices;
                _cpu=new GridLiquidFieldCpuSurface(_frame,_sampler,_material,_frame.Owners.Length,gameObject.layer);
                // Material克隆不会复制未在Properties声明的数组；每条实际绘制路径显式绑定Palette。
                SetPalette(_cpu.Material);
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
            using(CaptureMarker.Auto())_frame.Capture();
            _captureMilliseconds=(float)((System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency);
            _version=_source.SurfaceVersion;_epoch=_source.TransportEpoch;_capturedAt=Time.time;_captured=true;
            _inputPages=_frame.Count;_surfacePages=_frame.OwnerCount;_capacityOverflow=_frame.Overflow;
            _cpuFallback=_gpu==null || _frame.Overflow || _frame.Count>_budget.InputChunks || _frame.OwnerCount>_budget.FineViews;
            _lastAlpha=-1;
            if(_cpuFallback)
            {
                // 已完成CPU域在下一批构建期间仍有效；上传请求与可见域有效性不能混为一个Bool。
                _subdivision=2;_cpuDomainPendingUpload=true;
                if(_gpu!=null)_fallbackReason="GPU capacity for minimum r2 surface exceeded; CPU reference surface.";
                _cpu.Begin();return;
            }
            _uploaded=false;_fallbackReason="";
            _cpu.ClearVisible();_cpuDomainUploaded=_cpuDomainPendingUpload=false;
            // 实际Mesh体积校准证明r1会严重低估一层水；保留粗Gameplay Grid，显示至少r2。
            // 预算容不下r2时分批CPU完整显示，不能靠撤掉薄层或裁页满足预算。
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
            int cells=_frame.Count*_source.ChunkSize*_source.ChunkSize*_source.ChunkSize;
            if(cells>0){_gpu.Previous.SetData(_frame.Previous,0,0,cells*_source.MaterialCount);_gpu.Current.SetData(_frame.Current,0,0,cells*_source.MaterialCount);_gpu.Terrain.SetData(_frame.Terrains,0,0,cells);}
            _gpu.Lookup.SetData(_gpuLookup);_gpu.OwnerLookup.SetData(_gpuOwnerLookup);if(_surfacePages>0){_gpu.Owners.SetData(_ownerKeys,0,0,_surfacePages);_gpu.OwnerBounds.SetData(_ownerBounds,0,0,_surfacePages*2);}
            _uploaded=true;_gpuSurfacePages=_surfacePages;
        }
        private void LateUpdate()
        {
            if(!_valid || _source==null || !_source.IsFluidInitialized || _frame==null)return;
            long before=GC.GetAllocatedBytesForCurrentThread();
            if(!IsPresentationPending && (!_captured || _version!=_source.SurfaceVersion
                || _epoch!=_source.TransportEpoch && (_source.ActiveCellCount>0 || _frame.Animated)))Capture();
            _alpha=Mathf.Clamp01((Time.time-_capturedAt)/_frame.Interval);
            // 插值区间正常落后一个Tick；CPU分批构造期间若快照停滞，指标继续增加，不能固定报100ms。
            _displayLagMilliseconds=_frame.Animated || IsPresentationPending
                ?(Time.time-_capturedAt+_frame.Interval*(1-_alpha))*1000:0;
            if(_cpuFallback)
            {
                // 首个CPU批次完成前继续显示上一套GPU结果，超预算并不代表撤掉可见液体。
                // 首次回退保留GPU；已有完整CPU结果时只画那份CPU，避免两份旧壳叠画。
                if(_cpu.IsBuilding && !_cpu.HasVisibleSurface && _uploaded)DrawGpu();
                _cpu.UpdateAndDraw();
                if(!_cpu.IsBuilding && _cpuDomainPendingUpload)
                {_cpuOwnerBuffer.SetData(_frame.OwnerLookup);_cpuDomainUploaded=true;_cpuDomainPendingUpload=false;}
                _gcBytes+=GC.GetAllocatedBytesForCurrentThread()-before;return;
            }
            bool rebuild=(_frame.Animated && _alpha!=_lastAlpha) || _lastAlpha<0;
#if UNITY_EDITOR
            rebuild=(rebuild || _validationForceRebuild) && !_validationFreezeDispatch;
#endif
            if(_uploaded && rebuild)
            {
                using(DispatchMarker.Auto())Dispatch();UpdateDrops();_lastAlpha=_alpha;
            }
            DrawGpu();
            _gcBytes+=GC.GetAllocatedBytesForCurrentThread()-before;
        }
        private void DrawGpu()
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
                if(_smallDrops>0)Graphics.RenderMeshPrimitives(_dropDraw,_dropMesh,0,_smallDrops);
            }
        }
        private void Dispatch()
        {
            if(_surfacePages==0)return;int n=_source.ChunkSize*_subdivision,node=n+7,cube=n+6;
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
        }
        private void UpdateDrops()
        {
            // 用户要求去掉逐格圆珠：GPU/CPU均从同一连续场画扁长流条。
            _smallDrops=0;
        }
        public void BindStableDomain(MaterialPropertyBlock properties)
        {
            bool cpuDomain=_cpuFallback && _cpuDomainUploaded;
            // 裁剪必须跟随“实际正在显示”的完整快照。CPU首批未完成时旧GPU还在画，
            // 后续CPU批次则保留旧CPU域，不能提前裁新域或让Stable在同一处重复铺面。
            bool retainedGpu=_cpuFallback && _cpu!=null && _cpu.IsBuilding && !_cpu.HasVisibleSurface && _uploaded;
            bool active=_cpuFallback?cpuDomain || retainedGpu:_uploaded;
            properties.SetInt("_LiquidDomainEnabled",active?1:0);
            if(!active)return;properties.SetBuffer("_LiquidOwnerLookup",cpuDomain?_cpuOwnerBuffer:_gpu.OwnerLookup);properties.SetInt("_LiquidOwnerLookupMask",cpuDomain?_frame.OwnerLookup.Length-1:_budget.LookupSlots-1);
            properties.SetVector("_LiquidDomainOriginSize",new Vector4(_source.Origin.x,_source.Origin.y,_source.Origin.z,_source.CellSize));properties.SetInt("_LiquidDomainChunkSize",_source.ChunkSize);
        }
        private void Dispose()
        {
            _valid=_captured=_uploaded=_cpuDomainUploaded=_cpuDomainPendingUpload=false;_gpuSurfacePages=_smallDrops=0;
            _gpu?.Dispose();_gpu=null;_cpu?.Dispose();_cpu=null;_cpuOwnerBuffer?.Dispose();_cpuOwnerBuffer=null;_gpuBytes=0;
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
