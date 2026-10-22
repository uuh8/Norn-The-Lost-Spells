#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering
{
    public sealed class GridLiquidFieldLabDriver : MonoBehaviour
    {
        private static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
        private LiquidWorldRuntime _source;
        private GridLiquidFieldSurfaceRenderer _renderer;
        private LiquidWorldProfile _profile;
        private LiquidBehaviorProfile[] _behaviors;
        private GridLiquidFieldRenderProfile _renderProfile;
        private Camera _camera;
        private GameObject _support;
        private GameObject _receiver;
        private RenderTexture _target;
        private Vector3Int _inlet;
        private string _kind;
        private float _clock;
        private int _tick;
        private uint _injected;
        public int Tick=>_tick;
        public bool CanCapture=>_renderer!=null && !_renderer.IsPresentationPending;
        public void SetOpaque(bool value)=>_renderer.SetValidationOpaque(value);
        public void Diagnostic(string command)
        {
            if(command=="wave-off")_renderer.SetValidationDiagnostic("_WaveNormalStrength",0);
            if(command=="swell-off")_renderer.SetValidationDiagnostic("_CartoonSwellAmplitude",0);
            if(command=="solid-alpha")_renderer.SetValidationDiagnostic("_DebugForceOpaqueFragment",2);
            if(command=="normals")_renderer.SetValidationDiagnostic("_DebugForceOpaqueFragment",3);
            if(command=="raw-normals")_renderer.SetValidationDiagnostic("_DebugForceOpaqueFragment",4);
            if(command=="shadow-off")
            {var type=Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime",true);type.GetProperty("renderShadows").SetValue(_camera.GetComponent(type),false);}
        }
        public void Initialize(string kind)
        {
            _kind=kind;Vector3 origin=new Vector3(100,30,100);float angle=kind=="slope60" || kind=="toe60" || kind=="join60"?60:kind=="slope30" || kind=="toe30" || kind=="join30"?30:0;
            bool toe=kind.StartsWith("toe",StringComparison.Ordinal) || kind.StartsWith("join",StringComparison.Ordinal);
            var original=AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset");
            bool lakes=kind.StartsWith("lake",StringComparison.Ordinal);
            bool wide=kind=="wide" || kind=="wide-cpu" || kind=="air-wide",platform=kind=="platform";
            _profile=Instantiate(original);JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":64,\"_maximumActiveCells\":2048,\"_maximumCellsPerTick\":256,\"_maximumStableRegions\":8,\"_maximumStableRegionCells\":4096,\"_maximumStableSurfacePatches\":512,\"_enableAutoStableDetection\":false}",_profile);
            _behaviors=new LiquidBehaviorProfile[original.MaterialCount];
            for(int i=0;i<_behaviors.Length;i++){_behaviors[i]=Instantiate(original.GetMaterial(i));if(kind=="same" || kind=="contact")typeof(LiquidBehaviorProfile).GetField("_relativeDensity",Fields).SetValue(_behaviors[i],1f);}
            typeof(LiquidWorldProfile).GetField("_materials",Fields).SetValue(_profile,_behaviors);
            // Host在初始化期间尚未激活。Collider必须先保持独立激活，否则初始化会缓存“没有地板”。
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);_support=floor;floor.name="Temporary Lab Support";
            floor.transform.position=origin+new Vector3(2,-.25f,2);floor.transform.localScale=new Vector3(6,.5f,6);floor.transform.rotation=Quaternion.Euler(0,0,-angle);Physics.SyncTransforms();
            if(toe)
            {
                float rad=angle*Mathf.Deg2Rad;
                floor.transform.position=origin+new Vector3(3,2*Mathf.Sin(rad)-.25f*Mathf.Cos(rad),2);floor.transform.localScale=new Vector3(4,.5f,3);
                _receiver=GameObject.CreatePrimitive(PrimitiveType.Cube);_receiver.name="Temporary Adjoining Floor";
                _receiver.transform.position=origin+new Vector3(4,-.25f,2);_receiver.transform.localScale=new Vector3(16,.5f,8);Physics.SyncTransforms();
            }
            if(lakes){floor.transform.position=origin+new Vector3(19,-.25f,9);floor.transform.localScale=new Vector3(42,.5f,22);Physics.SyncTransforms();}
            if(wide || platform){floor.transform.position=origin+new Vector3(3,-.25f,3);floor.transform.localScale=new Vector3(12,.5f,12);}
            if(platform)
            {
                _receiver=GameObject.CreatePrimitive(PrimitiveType.Cube);_receiver.name="Temporary Overflow Platform";
                _receiver.transform.position=origin+new Vector3(3,1,3);_receiver.transform.localScale=new Vector3(3,2,3);
            }
            Physics.SyncTransforms();
            var baker=gameObject.AddComponent<ElementFieldSolidBaker>();_source=gameObject.AddComponent<LiquidWorldRuntime>();
            typeof(LiquidWorldRuntime).GetField("_profile",Fields).SetValue(_source,_profile);
            if(!_source.TryInitialize(origin,.25f,4,baker))throw new InvalidOperationException("临时液体源初始化失败。");
            var renderOriginal=AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset");
            // 视觉实拍沿用正式128MiB/96页预算；降低到32MiB会把平台场景强制降为r2，
            // 从而把验证器自己的低细分折角误当成正式效果。CPU用独立案例显式触发。
            _renderProfile=Instantiate(renderOriginal);
            if(kind=="bulk-calibrated")JsonUtility.FromJsonOverwrite("{\"_cartoonCrownStrength\":0.15}",_renderProfile);
            _renderer=gameObject.AddComponent<GridLiquidFieldSurfaceRenderer>();_renderer.ConfigureForValidation(_source,_renderProfile);
            if(lakes)
            {
                var footprint=new bool[16*16];int wet=0;
                for(int z=0;z<16;z++)for(int x=0;x<16;x++)if((x-7.5f)*(x-7.5f)+(z-7.5f)*(z-7.5f)<56.25f){footprint[x+16*z]=true;wet++;}
                for(int i=0;i<8;i++)
                {
                    var min=new Vector3Int(i%4*20,0,i/4*20);ulong amount=(ulong)wet*4*510*3/4;
                    if(!_source.TryRegisterStableFootprint(MaterialId.Water,min,min+new Vector3Int(16,4,16),footprint,amount,out _))throw new InvalidOperationException("多湖泊稳定区域注册失败。");
                    _injected+=(uint)amount;
                }
                var stable=gameObject.AddComponent<StableLiquidSurfaceRenderer>();
                stable.ConfigureForValidation(_source,_renderProfile.GetMaterial(0),Shader.Find("Game/Elemental/Cartoon Liquid"));
                _inlet=new Vector3Int(7,3,7);
            }
            else if(wide || platform)
            {
                int min=platform?3:0,max=platform?9:12,y=platform?4:kind=="air-wide"?4:0;
                for(int z=min;z<max;z++)for(int x=min;x<max;x++)
                {uint amount=platform?510u:128u;AssertAdd(new Vector3Int(x,y,z),amount);}
                _inlet=platform?new Vector3Int(1,6,4):new Vector3Int(6,4,6);
            }
            else if(angle==0)
            {
                for(int z=0;z<4;z++)for(int x=0;x<4;x++)
                {
                    if(kind=="blob" && (x==0 || x==3) && (z==0 || z==3))continue;
                    var c=new Vector3Int(x,0,z);
                    if(kind=="water" || kind=="poison" || kind=="sticky" || kind=="thin" || kind=="thin-cpu" || kind=="blob" || kind.StartsWith("bulk",StringComparison.Ordinal))
                    {uint amount=kind.StartsWith("thin",StringComparison.Ordinal)?32u:kind.StartsWith("bulk",StringComparison.Ordinal)?128u:kind=="blob"?255u:510u;MaterialId id=kind=="poison"?MaterialId.Poison:kind=="sticky"?MaterialId.Sticky:MaterialId.Water;_source.TryAddInitial(c,id,amount,false);_injected+=amount;}
                    else if(kind=="contact")
                    {_source.TryAddInitial(c,x<2?MaterialId.Water:MaterialId.Poison,480,false);_injected+=480;}
                    else
                    {uint water=kind=="same"?120u:306u,poison=kind=="same"?360u:204u;_source.TryAddInitial(c,MaterialId.Water,water,false);_source.TryAddInitial(c,MaterialId.Poison,poison,false);_injected+=water+poison;}
                }
            }
            else if(toe)
            {
                float rad=angle*Mathf.Deg2Rad;int outside=Mathf.CeilToInt((3+2*Mathf.Cos(rad)+.25f*Mathf.Sin(rad))/.5f);
                int x=outside-3,y=0;while(_source.IsSolidForSurface(new Vector3Int(x,y,3)))y++;
                _inlet=new Vector3Int(x,y,3);
                uint amount=kind.StartsWith("join",StringComparison.Ordinal)?510u:128u;
                if(!_source.TryAddInitial(_inlet,MaterialId.Water,amount,true))throw new InvalidOperationException("坡脚注入口不可用。");_injected=amount;
            }
            else
            {
                float rad=angle*Mathf.Deg2Rad;int y=Mathf.CeilToInt((-.25f+.25f/Mathf.Cos(rad)+Mathf.Tan(rad))/.5f);
                // Grid占用覆盖整个Cell盒，因此注入口底面取该盒的最高坡面角点，避免初始格与Cube相交。
                _inlet=new Vector3Int(2,y,3);
                if(!_source.TryAddInitial(_inlet,MaterialId.Water,510,true))throw new InvalidOperationException("坡面注入口落入Solid格。");_injected=510;
            }
            var cameraObject=new GameObject("Temporary Lab Camera");cameraObject.transform.SetParent(transform);_camera=cameraObject.AddComponent<Camera>();
            _renderer.SetValidationViewCamera(_camera);
            if(Camera.main!=null)_camera.CopyFrom(Camera.main);_camera.fieldOfView=45;
            var cameraDataType=Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime",true);
            var cameraData=cameraObject.AddComponent(cameraDataType);cameraDataType.GetProperty("requiresDepthTexture").SetValue(cameraData,true);
            Vector3 center=origin+new Vector3(angle==0?1:2,angle==0?.25f:0,1.5f);
            // 60°坡面法线朝+X；镜头必须位于坡面上方，不能从Cube背面判断流体不可见。
            cameraObject.transform.position=center+new Vector3(angle==0?-3:4,angle==0?1.8f:5,-4);cameraObject.transform.LookAt(center);
            if(kind.StartsWith("bulk",StringComparison.Ordinal) || kind=="blob"){cameraObject.transform.position=center+new Vector3(-2.5f,1.2f,-3.2f);cameraObject.transform.LookAt(center);_camera.fieldOfView=40;}
            if(toe){center=origin+new Vector3(3+2*Mathf.Cos(angle*Mathf.Deg2Rad),.3f,1.75f);cameraObject.transform.position=center+new Vector3(3.5f,1.7f,-3.8f);cameraObject.transform.LookAt(center);_camera.fieldOfView=40;}
            if(lakes){center=origin+new Vector3(19,1.5f,9);cameraObject.transform.position=center+new Vector3(-18,28,-28);cameraObject.transform.LookAt(center);_camera.fieldOfView=55;}
            if(wide || platform){center=origin+new Vector3(3,platform?1:kind=="air-wide"?2.2f:.25f,3);cameraObject.transform.position=center+new Vector3(-6,2.4f,-7);cameraObject.transform.LookAt(center);_camera.fieldOfView=45;}
            _target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);_target.Create();_camera.targetTexture=_target;_camera.enabled=true;
            if(kind=="thin-cpu" || kind=="wide-cpu")JsonUtility.FromJsonOverwrite("{\"_fineViews\":0}",_renderProfile);
        }
        private void AssertAdd(Vector3Int cell,uint quantity)
        {if(!_source.TryAddInitial(cell,MaterialId.Water,quantity,_kind=="platform"))throw new InvalidOperationException("验证室注入失败："+cell);_injected+=quantity;}
        private void Start(){if(_support!=null)_support.transform.SetParent(transform,true);if(_receiver!=null)_receiver.transform.SetParent(transform,true);}
        private void Update()
        {
            // 诊断固定在第30个提交，避免切换材质前后液体已流出镜头，导致比较没有共同基准。
            // Runtime正常时间与Renderer插值继续；只冻结这个临时验证源的后续写入。
            if(_tick>=30)return;
            // 固定0.1s模拟，Renderer仍每帧插值；复用对象/数组，临时验证也不能污染GC测量。
            _clock+=Time.deltaTime;if(_clock<.1f)return;_clock-=.1f;_tick++;
            if(_kind=="wide" || _kind=="wide-cpu" || _kind=="air-wide")return;
            if(_kind=="platform" && _source.TryAddInitial(_inlet,MaterialId.Water,510,true))_injected+=510;
            if((_kind.StartsWith("slope",StringComparison.Ordinal) || _kind.StartsWith("join",StringComparison.Ordinal)) && _tick%3==0 && _injected<6120 && _source.TryAddInitial(_inlet,MaterialId.Water,170,true))_injected+=170;
            if(_kind=="lake-contact" && _tick==15 && _source.TryAddInitial(_inlet,MaterialId.Poison,510,true))_injected+=510;
            _source.Step(.1f);
        }
        public void Capture(string suffix="")
        {
            var previous=RenderTexture.active;RenderTexture.active=_target;var texture=new Texture2D(_target.width,_target.height,TextureFormat.RGB24,false);
            texture.ReadPixels(new Rect(0,0,_target.width,_target.height),0,0);texture.Apply();
            File.WriteAllBytes("Temp/ElementPerformance/continuous-lab-"+_kind+suffix+".png",texture.EncodeToPNG());RenderTexture.active=previous;DestroyImmediate(texture);
            var data=new SerializedObject(_renderer);
            var result=new Result{Utc=DateTime.UtcNow.ToString("O"),Case=_kind,Ready=_renderer.IsReady,CpuFallback=_renderer.CpuFallback,Overflow=data.FindProperty("_capacityOverflow").boolValue,
                Injected=_injected,Quantity=_source.TotalQuantity,GpuBytes=_renderer.GpuAllocatedBytes,GcBytes=data.FindProperty("_gcBytes").longValue,Pages=_renderer.SurfacePages,Tick=_tick,
                CaptureMs=data.FindProperty("_captureMilliseconds").floatValue,Alpha=_renderer.InterpolationAlpha,Device=SystemInfo.graphicsDeviceName,TimeScale=Time.timeScale,CrownStrength=_renderProfile.CartoonCrownStrength,VolumeScale=_renderProfile.CartoonVolumeScale};
            result.StableQuantity=_source.StableQuantity;result.StableRegions=_source.StableRegionCount;result.ActiveCells=_source.ActiveCellCount;result.ResidentChunks=_source.ResidentChunkCount;
            result.DisplayLagMs=data.FindProperty("_displayLagMilliseconds").floatValue;result.FallbackReason=_renderer.FallbackReason;
            var cpuTiming=(GridLiquidFieldCpuSurface)typeof(GridLiquidFieldSurfaceRenderer).GetField("_cpu",Fields).GetValue(_renderer);
            if(cpuTiming!=null){result.CpuBuildFrames=cpuTiming.BuildFrames;result.CpuBuildMs=cpuTiming.BuildMilliseconds;}
            result.SmallDrops=_renderer.SmallDropCount;
            var inputs=new List<Input>();var frame=_renderer.Frame;
            if(frame!=null)for(int page=0;page<frame.Count;page++)for(int local=0;local<frame.ChunkEdge*frame.ChunkEdge*frame.ChunkEdge;local++)for(int phase=0;phase<2;phase++)
            {
                int k=frame.ChunkEdge;var key=frame.Keys[page];var c=new Vector3Int(key.X*k+local%k,key.Y*k+local/k%k,key.Z*k+local/(k*k));
                var s=frame.Read(c,0,phase==0);if(s.Quantity==0)continue;
                inputs.Add(new Input{Cell=c,Previous=phase==0,Quantity=s.Quantity,Ground=frame.Supported(c,phase==0),Solid=frame.SolidSupported(c,phase==0),Terrain=frame.Terrain(c),Offset=s.OffsetCells,Velocity=s.VelocityWorld,Scale=frame.ShapeScale(c,phase==0)});
            }
            result.Inputs=inputs.ToArray();
            result.Subdivision=data.FindProperty("_subdivision").intValue;
            MeasureGeometry(result);
            if(_kind=="join60" && !_renderer.CpuFallback)
            {
                // 显式诊断同步读取真实GPU节点，与同一快照/同一细分率的CPU数学逐点对照。
                // GetData会等待GPU，分配/同步成本不计入生产Capture/GC，禁止放到正常帧路径。
                var gpu=(GridLiquidFieldResources)typeof(GridLiquidFieldSurfaceRenderer).GetField("_gpu",Fields).GetValue(_renderer);
                int side=frame.ChunkEdge*result.Subdivision+7,slots=side*side*side;
                var values=new float[_renderer.SurfacePages*slots];gpu.Field.GetData(values,0,0,values.Length);
                var sampler=new GridLiquidFieldSampler(frame,result.Subdivision);var slice=new List<FieldProbe>();
                for(int page=0;page<_renderer.SurfacePages;page++)for(int i=0;i<slots;i++)
                {
                    var key=frame.Owners[page];var p=new Vector3(key.X*frame.ChunkEdge+(i%side-2.5f)/result.Subdivision,
                        key.Y*frame.ChunkEdge+(i/side%side-2.5f)/result.Subdivision,key.Z*frame.ChunkEdge+(i/(side*side)-2.5f)/result.Subdivision);
                    if(Mathf.Abs(p.z-3.5f)>.001f || p.x<6.3f || p.x>9.5f || p.y<-.5f || p.y>5)continue;
                    float referenceValue=frame.OwnerBounds(page).Contains(p)?sampler.EvaluateGroundProxy(frame.Origin+p*frame.CellSize,_renderer.InterpolationAlpha):-.65f;
                    float actual=values[page*slots+i];result.MaximumFieldError=Mathf.Max(result.MaximumFieldError,Mathf.Abs(actual-referenceValue));
                    slice.Add(new FieldProbe{Position=p,Cpu=referenceValue,Gpu=actual});
                }
                result.FieldSlice=slice.ToArray();
            }
            if(_kind.StartsWith("toe",StringComparison.Ordinal) || _kind.StartsWith("join",StringComparison.Ordinal))
            {
                float rad=(_kind=="toe60" || _kind=="join60"?60:30)*Mathf.Deg2Rad;int outside=Mathf.CeilToInt((3+2*Mathf.Cos(rad)+.25f*Mathf.Sin(rad))/.5f);
                for(int z=0;z<8;z++)for(int x=outside;x<outside+8;x++){_source.TryGetDynamicQuantity(new Vector3Int(x,0,z),MaterialId.Water,out uint q);result.ReceivedOnFloor+=q;}
            }
            if(_kind=="platform")for(int z=-8;z<20;z++)for(int x=-8;x<20;x++)
            {_source.TryGetDynamicQuantity(new Vector3Int(x,0,z),MaterialId.Water,out uint q);result.ReceivedOnFloor+=q;}
            var material=(Material)typeof(GridLiquidFieldSurfaceRenderer).GetField("_material",Fields).GetValue(_renderer);
            var dropMaterial=(Material)typeof(GridLiquidFieldSurfaceRenderer).GetField("_dropMaterial",Fields).GetValue(_renderer);
            result.PaletteCount=material.GetInt("_PaletteCount");result.DropPaletteCount=dropMaterial.GetInt("_PaletteCount");
            result.Palette=material.GetVectorArray("_PaletteShallow");result.DropPalette=dropMaterial.GetVectorArray("_PaletteShallow");
            var cpu=typeof(GridLiquidFieldSurfaceRenderer).GetField("_cpu",Fields).GetValue(_renderer);
            var cpuMaterial=(Material)cpu.GetType().GetField("_material",Fields).GetValue(cpu);
            result.CpuPaletteCount=cpuMaterial.GetInt("_PaletteCount");result.CpuPalette=cpuMaterial.GetVectorArray("_PaletteShallow");
            _source.TryGetTerrainSupport(_inlet,out var support);result.SupportValid=support.Valid;result.SupportNormal=support.Normal;
            File.WriteAllText("Temp/ElementPerformance/continuous-lab-"+_kind+suffix+".json",JsonUtility.ToJson(result,true));
        }
        private void MeasureGeometry(Result result)
        {
            // 仅显式截图诊断同步读Mesh；生产帧仍没有GetData/分配。先减Origin再做四面体体积，
            // 避免100m处的绝对坐标相减放大Float误差。这里只统计动态连续场，不统计Dormant湖面。
            double volume=0;Vector3 minimum=Vector3.one*float.PositiveInfinity,maximum=Vector3.one*float.NegativeInfinity;
            if(!_renderer.CpuFallback)
            {
                var args=new uint[_renderer.SurfacePages*5];_renderer.DebugArguments.GetData(args,0,0,args.Length);int count=0;
                for(int i=0;i<_renderer.SurfacePages;i++)count=Mathf.Max(count,(int)(args[i*5]+args[i*5+2]));
                if(count==0)return;
                var indices=new uint[count];_renderer.DebugIndices.GetData(indices,0,0,count);
                int side=_source.ChunkSize*result.Subdivision+6;var vertices=new MeshVertex[_renderer.SurfacePages*side*side*side];
                _renderer.DebugVertices.GetData(vertices,0,0,vertices.Length);
                for(int page=0;page<_renderer.SurfacePages;page++)for(int at=(int)args[page*5+2],end=at+(int)args[page*5];at<end;at+=3)
                    MeasureTriangle(vertices[(int)indices[at]].Position,vertices[(int)indices[at+1]].Position,vertices[(int)indices[at+2]].Position,ref volume,ref minimum,ref maximum,result);
            }
            else
            {
                var cpu=typeof(GridLiquidFieldSurfaceRenderer).GetField("_cpu",Fields).GetValue(_renderer);
                var meshes=(Mesh[])cpu.GetType().GetField("_meshes",Fields).GetValue(cpu);int visible=(int)cpu.GetType().GetField("_visible",Fields).GetValue(cpu);
                for(int i=0;i<visible;i++){var vertices=meshes[i].vertices;var indices=meshes[i].triangles;
                    for(int at=0;at<indices.Length;at+=3)MeasureTriangle(vertices[indices[at]],vertices[indices[at+1]],vertices[indices[at+2]],ref volume,ref minimum,ref maximum,result);}
            }
            result.FieldMeshVolume=Math.Abs(volume);if(result.Triangles>0){result.MeshMinimum=minimum;result.MeshMaximum=maximum;}
        }
        private void MeasureTriangle(Vector3 a,Vector3 b,Vector3 c,ref double volume,ref Vector3 minimum,ref Vector3 maximum,Result result)
        {
            a-=_source.Origin;b-=_source.Origin;c-=_source.Origin;if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-14f)return;
            volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6.0;result.Triangles++;
            minimum=Vector3.Min(minimum,Vector3.Min(a,Vector3.Min(b,c)));maximum=Vector3.Max(maximum,Vector3.Max(a,Vector3.Max(b,c)));
        }
        [StructLayout(LayoutKind.Sequential)]private struct MeshVertex{public Vector3 Position;public float Valid;public Vector3 Normal;public uint A,B,C,D,Pad;}
        private void OnDestroy()
        {
            if(_support!=null && _support.transform.parent!=transform)DestroyImmediate(_support);
            if(_receiver!=null && _receiver.transform.parent!=transform)DestroyImmediate(_receiver);
            // 相机先解绑目标，Unity才允许安全释放RenderTexture，避免临时验证产生释放警告。
            if(_camera!=null)_camera.targetTexture=null;
            if(_target!=null){_target.Release();DestroyImmediate(_target);}if(_profile!=null)DestroyImmediate(_profile);
            if(_renderProfile!=null)DestroyImmediate(_renderProfile);if(_behaviors!=null)foreach(var b in _behaviors)if(b!=null)DestroyImmediate(b);
        }
        [Serializable]private sealed class Input{public Vector3Int Cell;public bool Previous,Ground,Solid;public uint Quantity;public Vector4 Terrain;public Vector3 Offset,Velocity;public float Scale;}
        [Serializable]private sealed class FieldProbe{public Vector3 Position;public float Cpu,Gpu;}
        [Serializable]private sealed class Result{public string Utc,Case,Device,FallbackReason;public bool Ready,CpuFallback,Overflow,SupportValid;public uint Injected;public ulong Quantity,StableQuantity,ReceivedOnFloor;public long GpuBytes,GcBytes;public int Pages,Tick,StableRegions,ActiveCells,ResidentChunks,PaletteCount,DropPaletteCount,CpuPaletteCount,SmallDrops,Subdivision,Triangles,CpuBuildFrames;public Vector4[] Palette,DropPalette,CpuPalette;public float CaptureMs,Alpha,DisplayLagMs,TimeScale,CrownStrength,VolumeScale,MaximumFieldError;public double FieldMeshVolume,CpuBuildMs;public Vector3 SupportNormal,MeshMinimum,MeshMaximum;public Input[] Inputs;public FieldProbe[] FieldSlice;}
    }
}


#endif

