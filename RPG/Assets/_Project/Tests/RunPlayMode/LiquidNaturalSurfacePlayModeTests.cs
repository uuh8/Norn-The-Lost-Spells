#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 同GMU、同Camera的自然液面实拍，以及实际GPU像素逐帧轨迹。旧对照只恢复旧形状参数，
    /// 材质仍取当前Palette，不能将它写成旧二进制/旧美术截图。无正式资产写入，无时间暂停。
    /// </summary>
    public sealed class LiquidNaturalSurfacePlayModeTests
    {
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private const int LiquidLayer=30;
        private readonly string _evidence=Path.Combine("TestResults/LiquidNaturalSurface",DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+"-"+Guid.NewGuid().ToString("N").Substring(0,12));
        private readonly List<UnityEngine.Object> _owned=new List<UnityEngine.Object>();
        private readonly List<PixelFrame> _trajectory=new List<PixelFrame>(128);
        private Scene _scene;
        private float _scale;
        private bool _background;
        private int _inflight,_skipped,_readbackErrors;
        private Fixture _current;
        private sealed class Fixture
        {
            internal GameObject Host,Floor;
            internal LiquidWorldRuntime Source;
            internal Component Renderer;
            internal Camera Camera;
            internal RenderTexture Target;
        }
        [SetUp]public void Setup()
        {
            _scale=Time.timeScale;_background=Application.runInBackground;
            if(!SystemInfo.supportsComputeShaders||!SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("需要实际GPU和AsyncGPUReadback。");
            Directory.CreateDirectory(_evidence);Time.timeScale=1;Application.runInBackground=true;
            _scene=SceneManager.CreateScene("Temporary natural liquid evidence");
        }
        [UnityTearDown]public IEnumerator Cleanup()
        {
            // 回调还借用RT时先等有界完成，再清理自己创建的对象，不删除已有Scene中的物体。
            double until=Time.realtimeSinceStartupAsDouble+8;
            while(_inflight>0 && Time.realtimeSinceStartupAsDouble<until)yield return null;
            if(_current!=null && _current.Camera!=null)_current.Camera.targetTexture=null;
            foreach(var value in _owned)if(value!=null)UnityEngine.Object.Destroy(value);_owned.Clear();
            yield return null;
            if(_scene.IsValid()&&_scene.isLoaded){var op=SceneManager.UnloadSceneAsync(_scene);if(op!=null)yield return op;}
            Time.timeScale=_scale;Application.runInBackground=_background;
        }

        [UnityTest]public IEnumerator EqualGmuLegacyAndNaturalGpuCpuSurfacesHaveMeasuredShapeAndPixels()
        {
            var results=new SurfaceResult[4];
            for(int mode=0;mode<4;mode++)
            {
                bool natural=mode%2==1,cpu=mode>=2;string name=(natural?"natural":"legacy-parameters")+(cpu?"-cpu":"-gpu");
                var f=CreateGround(natural,cpu);_current=f;f.Host.SetActive(true);yield return null;yield return null;
                Assert.That(Property<bool>(f.Renderer,"IsReady"),Is.True);
                double until=Time.realtimeSinceStartupAsDouble+90;
                while(Property<bool>(f.Renderer,"IsPresentationPending"))
                {Assert.That(Time.realtimeSinceStartupAsDouble,Is.LessThan(until),"CPU表面未在有界时间完成。");yield return null;}
                bool passCompiledBefore=true,passCompiledAfter=true;
                if(cpu)
                {
                    // Editor首次触发CPU_REFERENCE变体时，GPU已算出Mesh并不等于绘制Pass已编译。
                    // 与Unity本地MaterialPreviewRenderingUtils相同，仅在取证夹具同步预热缺失Pass；
                    // 不改正式Runtime绘制，也把预热前状态写入证据，避免把等待误报成液体缺失。
                    var cpuMaterial=(Material)Get(Get(f.Renderer,"_cpu"),"_material");
                    passCompiledBefore=ShaderUtil.IsPassCompiled(cpuMaterial,0);
                    if(!passCompiledBefore)ShaderUtil.CompilePass(cpuMaterial,0,true);
                    passCompiledAfter=ShaderUtil.IsPassCompiled(cpuMaterial,0);
                    yield return null;
                }
                yield return new WaitForEndOfFrame();
                var result=Measure(f,name,cpu);result.ImagePath=Path.Combine(_evidence,name+".png");SaveImage(f.Target,result.ImagePath);
                result.PassCompiledBefore=passCompiledBefore;result.PassCompiledAfter=passCompiledAfter;
                // 即使像素失败也保留实际Mesh/材质/Layer，避免空PNG丢失定位数据。
                File.WriteAllText(Path.Combine(_evidence,name+"-measurement.json"),JsonUtility.ToJson(result,true));
                f.Camera.cullingMask=1<<LiquidLayer;f.Camera.backgroundColor=new Color(.11f,.017f,.11f,1);
                yield return null;yield return new WaitForEndOfFrame();
                var image=Read(f.Target);result.LiquidPixels=Pixels(image.GetPixels32(),image.width,image.height,out _,out _);
                File.WriteAllBytes(Path.Combine(_evidence,name+"-mask.png"),image.EncodeToPNG());UnityEngine.Object.Destroy(image);
                if(cpu && result.LiquidPixels==0)
                {
                    // 只旁路复杂Fragment作诊断；原像素断言不变，不能拿强制不透明当成修复。
                    f.Renderer.GetType().GetMethod("SetValidationDiagnostic").Invoke(f.Renderer,new object[]{"_DebugForceOpaqueFragment",1f});
                    yield return null;yield return new WaitForEndOfFrame();
                    var opaque=Read(f.Target);result.OpaqueDiagnosticPixels=Pixels(opaque.GetPixels32(),opaque.width,opaque.height,out _,out _);
                    File.WriteAllBytes(Path.Combine(_evidence,name+"-opaque-diagnostic.png"),opaque.EncodeToPNG());UnityEngine.Object.Destroy(opaque);
                    f.Renderer.GetType().GetMethod("SetValidationDiagnostic").Invoke(f.Renderer,new object[]{"_DebugForceOpaqueFragment",0f});
                    // 单独检验CPU普通Mesh是否受继承的Instancing配置影响；恢复后再断言原图，
                    // 诊断图不能替代原路径通过，也不使用MeshRenderer绕过正式Draw接口。
                    var cpuMaterial=(Material)Get(Get(f.Renderer,"_cpu"),"_material");bool priorInstancing=cpuMaterial.enableInstancing;
                    cpuMaterial.enableInstancing=false;
                    yield return null;yield return new WaitForEndOfFrame();
                    var noInstancing=Read(f.Target);result.NoInstancingDiagnosticPixels=Pixels(noInstancing.GetPixels32(),noInstancing.width,noInstancing.height,out _,out _);
                    File.WriteAllBytes(Path.Combine(_evidence,name+"-no-instancing-diagnostic.png"),noInstancing.EncodeToPNG());UnityEngine.Object.Destroy(noInstancing);
                    cpuMaterial.enableInstancing=priorInstancing;
                }
                File.WriteAllText(Path.Combine(_evidence,name+"-measurement.json"),JsonUtility.ToJson(result,true));
                Assert.That(result.LiquidPixels,Is.GreaterThan(8));Assert.That(result.Triangles,Is.GreaterThan(0));
                Assert.That(f.Source.TotalQuantity+f.Source.PendingQuantity,Is.EqualTo(256UL));
                results[mode]=result;
                File.WriteAllText(Path.Combine(_evidence,name+".json"),JsonUtility.ToJson(result,true));
                f.Camera.targetTexture=null;UnityEngine.Object.Destroy(f.Camera.gameObject);UnityEngine.Object.Destroy(f.Host);
                if(f.Floor!=null)UnityEngine.Object.Destroy(f.Floor);f.Target.Release();UnityEngine.Object.Destroy(f.Target);
                yield return null;_current=null;
            }
            Assert.That(results[1].Height,Is.LessThan(results[0].Height*.65f),"相同256GMU新水面应明显降低圆拱。");
            Assert.That(results[1].Height,Is.InRange(.12f,.28f),"同量小滩保留适度厚度，不能退回纸片或旧高胶块。");
            Assert.That(results[1].WidthX*results[1].WidthZ,Is.GreaterThan(results[0].WidthX*results[0].WidthZ*1.2f));
            Assert.That(results[1].Volume/(256*Math.Pow(.5,3)/510),Is.InRange(.85,1.15));
            Assert.That(Mathf.Abs(results[1].Height-results[3].Height),Is.LessThan(.005f),"CPU fallback与GPU应使用同一接触形状。");
            Assert.That(Mathf.Abs(results[1].WidthX-results[3].WidthX),Is.LessThan(.01f));
            Assert.That(results[1].Volume/results[3].Volume,Is.InRange(.98,1.02));
            File.WriteAllText(Path.Combine(_evidence,"surface-comparison.json"),JsonUtility.ToJson(new SurfaceReport{Scope="Same 256GMU and current palette; actual Field triangles plus actual GPU instance buffer and shared template geometry. CPU fallback uses the same cap instance Draw. Legacy parameters disable material tension and restore crown .8 / volume 1.8; not old binary footage.",Results=results,Passed=true},true));
        }

        [UnityTest]public IEnumerator AirborneGpuPixelCentroidMovesBetweenRealSolverTicks()
        {
            var host=Own(new GameObject("Temporary airborne trajectory"));host.layer=LiquidLayer;host.SetActive(false);SceneManager.MoveGameObjectToScene(host,_scene);
            var type=Type.GetType("Game.Rendering.GridLiquidFieldLabDriver, Game.Rendering",true);var driver=(Component)host.AddComponent(type);
            type.GetMethod("Initialize").Invoke(driver,new object[]{"air-fall"});
            var f=new Fixture{Host=host,Source=(LiquidWorldRuntime)Get(driver,"_source"),Renderer=(Component)Get(driver,"_renderer"),Camera=(Camera)Get(driver,"_camera"),Target=(RenderTexture)Get(driver,"_target")};_current=f;
            f.Camera.cullingMask=1<<LiquidLayer;f.Camera.clearFlags=CameraClearFlags.SolidColor;f.Camera.backgroundColor=new Color(.11f,.017f,.11f,1);DisablePost(f.Camera);
            AddLight(host);host.SetActive(true);yield return null;yield return null;
            var small=Own(new RenderTexture(256,144,0,RenderTextureFormat.ARGB32));small.Create();
            double until=Time.realtimeSinceStartupAsDouble+30;
            while(Property<int>(driver,"Tick")<=8)
            {
                Assert.That(Time.realtimeSinceStartupAsDouble,Is.LessThan(until));Assert.That(f.Source.TotalQuantity+f.Source.PendingQuantity,Is.EqualTo(2040UL));
                yield return new WaitForEndOfFrame();
                if(_inflight<3)
                {
                    int frame=Time.frameCount,tick=Property<int>(driver,"Tick");float alpha=Property<float>(f.Renderer,"InterpolationAlpha");double now=Time.realtimeSinceStartupAsDouble;
                    Graphics.Blit(f.Target,small);_inflight++;
                    AsyncGPUReadback.Request(small,0,TextureFormat.RGBA32,request=>
                    {
                        _inflight--;if(request.hasError){_readbackErrors++;return;}
                        var data=request.GetData<Color32>();var array=data.ToArray();int count=Pixels(array,256,144,out var centroid,out var bounds);
                        _trajectory.Add(new PixelFrame{Frame=frame,Tick=tick,Time=now,Alpha=alpha,LiquidPixels=count,Centroid=centroid,PixelBounds=bounds});
                    });
                }
                else _skipped++;
                yield return null;
            }
            while(_inflight>0 && Time.realtimeSinceStartupAsDouble<until)yield return null;
            Assert.That(_readbackErrors,Is.Zero);Assert.That(_inflight,Is.Zero);Assert.That(_skipped,Is.Zero,"逐帧视觉轨迹不能静默缺帧。");
            _trajectory.Sort((a,b)=>a.Frame.CompareTo(b.Frame));
            var active=_trajectory.FindAll(p=>p.Tick>=2 && p.Tick<=7 && p.LiquidPixels>8);
            int moving=0,stationary=0,longest=0;float distance=0;
            for(int i=1;i<active.Count;i++)
            {
                float delta=Vector2.Distance(active[i-1].Centroid,active[i].Centroid);distance+=delta;
                if(delta>.05f){moving++;stationary=0;}else{stationary++;longest=Math.Max(longest,stationary);}
            }
            var report=new TrajectoryReport{Scope="Real local Solver and rendered liquid-only pixels, bounded asynchronous per-frame RT readback. No Replica smoothness or frame-time performance claim.",Samples=_trajectory.ToArray(),ActiveSamples=active.Count,MovingTransitions=moving,LongestStationaryTransitions=longest,PixelDistance=distance,Skipped=_skipped,ReadbackErrors=_readbackErrors};
            File.WriteAllText(Path.Combine(_evidence,"airborne-pixel-trajectory.json"),JsonUtility.ToJson(report,true));
            SaveImage(f.Target,Path.Combine(_evidence,"airborne-end-mask.png"));
            Assert.That(active.Count,Is.GreaterThanOrEqualTo(8));Assert.That(distance,Is.GreaterThan(5));
            Assert.That((float)moving/Math.Max(1,active.Count-1),Is.GreaterThan(.6f),"活动下落阶段多数Render帧必须真正移动。");
            Assert.That(longest,Is.LessThanOrEqualTo(2),"下落过程中不能连续多帧只重复一个像素姿态。");
        }

        private Fixture CreateGround(bool natural,bool cpu)
        {
            var host=Own(new GameObject("Ground visual comparison"));host.layer=LiquidLayer;host.SetActive(false);SceneManager.MoveGameObjectToScene(host,_scene);
            var origin=new Vector3(100,30,100);var floor=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));SceneManager.MoveGameObjectToScene(floor,_scene);
            floor.transform.position=origin+new Vector3(1.5f,-.25f,1.5f);floor.transform.localScale=new Vector3(6,.5f,6);Physics.SyncTransforms();
            var profile=Own(UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset")));
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":256,\"_enableAutoStableDetection\":false}",profile);
            var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",Fields).SetValue(source,profile);
            Assert.That(source.TryInitialize(origin,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
            for(int z=2;z<4;z++)for(int x=2;x<4;x++)Assert.That(source.TryAddInitial(new Vector3Int(x,0,z),MaterialId.Water,64,false),Is.True);
            source.Step(.1f);
            var render=Own(UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset")));
            JsonUtility.FromJsonOverwrite(natural?"{\"_budgetMiB\":32,\"_inputChunks\":16,\"_views\":16,\"_fineViews\":16,\"_cullByCamera\":false}":"{\"_budgetMiB\":32,\"_inputChunks\":16,\"_views\":16,\"_fineViews\":16,\"_cullByCamera\":false,\"_useMaterialSurfaceTension\":false,\"_cartoonCrownStrength\":0.8,\"_cartoonVolumeScale\":1.8}",render);
            if(cpu)JsonUtility.FromJsonOverwrite("{\"_fineViews\":0}",render);
            var rendererType=Type.GetType("Game.Rendering.GridLiquidFieldSurfaceRenderer, Game.Rendering",true);var renderer=(Component)host.AddComponent(rendererType);
            rendererType.GetMethod("ConfigureForValidation").Invoke(renderer,new object[]{source,render});
            var camObject=Own(new GameObject("Natural surface evidence camera"));SceneManager.MoveGameObjectToScene(camObject,_scene);var camera=camObject.AddComponent<Camera>();
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.035f,.06f,1);camera.fieldOfView=40;
            var center=origin+new Vector3(1.5f,.05f,1.5f);camObject.transform.position=center+new Vector3(-2.5f,1.6f,-3.5f);camObject.transform.LookAt(center);
            var dataType=Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime",true);var data=camObject.AddComponent(dataType);dataType.GetProperty("requiresDepthTexture").SetValue(data,true);DisablePost(camera);
            var target=Own(new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32));target.Create();camera.targetTexture=target;
            rendererType.GetMethod("SetValidationViewCamera").Invoke(renderer,new object[]{camera});AddLight(host);
            return new Fixture{Host=host,Floor=floor,Source=source,Renderer=renderer,Camera=camera,Target=target};
        }
        private SurfaceResult Measure(Fixture f,string name,bool cpu)
        {
            var result=new SurfaceResult{Name=name,Quantity=f.Source.TotalQuantity,Minimum=Vector3.one*float.PositiveInfinity,Maximum=Vector3.one*float.NegativeInfinity};
            Assert.That(Property<bool>(f.Renderer,"CpuFallback"),Is.EqualTo(cpu));
            if(!cpu)
            {
                var argsBuffer=Property<GraphicsBuffer>(f.Renderer,"DebugArguments");int pages=Property<int>(f.Renderer,"SurfacePages");var args=new uint[pages*5];argsBuffer.GetData(args);
                int end=0;for(int i=0;i<pages;i++)end=Math.Max(end,(int)(args[i*5+2]+args[i*5]));
                if(end>0)
                {
                    var indices=new uint[end];Property<GraphicsBuffer>(f.Renderer,"DebugIndices").GetData(indices,0,0,end);
                    var frame=f.Renderer.GetType().GetProperty("Frame").GetValue(f.Renderer);int side=Property<int>(frame,"ChunkEdge")*Property<int>(f.Renderer,"SurfaceSubdivision")+6;
                    var vertices=new Vertex[pages*side*side*side];Property<GraphicsBuffer>(f.Renderer,"DebugVertices").GetData(vertices,0,0,vertices.Length);
                    for(int page=0;page<pages;page++)for(int at=(int)args[page*5+2],stop=at+(int)args[page*5];at<stop;at+=3)Triangle(vertices[indices[at]].Position,vertices[indices[at+1]].Position,vertices[indices[at+2]].Position,f.Source.Origin,result);
                }
            }
            else
            {
                var surface=Get(f.Renderer,"_cpu");var meshes=(Mesh[])Get(surface,"_meshes");int count=(int)Get(surface,"_visible");
                var material=(Material)Get(surface,"_material");var draw=(RenderParams)Get(surface,"_draw");
                result.VisibleMeshes=count;result.DrawLayer=draw.layer;result.CameraMask=f.Camera.cullingMask;
                result.CpuKeyword=material.IsKeywordEnabled("LIQUID_CPU_REFERENCE");result.PaletteCount=material.GetInt("_PaletteCount");
                // HLSL uniform不一定出现在Shader Properties，直接GetInt会让Unity记录Error并中断测试。
                // -1明确表示Material查询不可用，不能把缺失的诊断读数当作Runtime传入了0。
                result.DomainPropertyDeclared=material.HasProperty("_LiquidDomainEnabled");
                result.DomainEnabled=result.DomainPropertyDeclared?material.GetInt("_LiquidDomainEnabled"):-1;
                result.MaterialInstancing=material.enableInstancing;
                result.ShaderName=material.shader.name;result.Keywords=string.Join(",",material.shaderKeywords);
                var weights=new List<Vector4>();
                for(int i=0;i<count;i++)
                {
                    var vertices=meshes[i].vertices;var indices=meshes[i].triangles;result.MeshVertices+=vertices.Length;
                    meshes[i].GetUVs(0,weights);foreach(var weight in weights)if(weight.x+weight.y+weight.z+weight.w>1e-5f)result.WeightedVertices++;
                    for(int at=0;at<indices.Length;at+=3)Triangle(vertices[indices[at]],vertices[indices[at+1]],vertices[indices[at+2]],f.Source.Origin,result);
                }
            }
            result.FieldTriangles=result.Triangles;AddActualInstances(f,result);
            result.SignedVolume=result.Volume;result.Volume=Math.Abs(result.Volume);
            if(result.Triangles>0){result.Height=result.Maximum.y-result.Minimum.y;result.WidthX=result.Maximum.x-result.Minimum.x;result.WidthZ=result.Maximum.z-result.Minimum.z;}
            else result.Minimum=result.Maximum=Vector3.zero;
            return result;
        }
        private static void AddActualInstances(Fixture f,SurfaceResult result)
        {
            int count=Property<int>(f.Renderer,"SmallDropCount");result.Instances=count;if(count==0)return;
            var gpu=Get(f.Renderer,"_gpu");var buffer=(GraphicsBuffer)(gpu!=null?Get(gpu,"Drops"):Get(f.Renderer,"_fallbackDropBuffer"));
            var instances=new Instance[count];buffer.GetData(instances,0,0,count);
            var type=Type.GetType("Game.Rendering.GridLiquidSmallDrop, Game.Rendering",true);
            var transform=Type.GetType("Game.Rendering.GridLiquidGroundContactShape, Game.Rendering",true).GetMethod("TransformPosition",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
            var template=Type.GetType("Game.Rendering.LiquidDropletTemplate, Game.Rendering",true);var vertices=(Vector3[])template.GetField("Vertices",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);var indices=(int[])template.GetField("Indices",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            foreach(var instance in instances)
            {
                object boxed=Activator.CreateInstance(type);type.GetField("Position").SetValue(boxed,instance.Position);type.GetField("Radius").SetValue(boxed,instance.Radius);type.GetField("Third").SetValue(boxed,instance.Third);type.GetField("Fourth").SetValue(boxed,instance.Fourth);
                for(int at=0;at<indices.Length;at+=3)
                {var a=(Vector3)transform.Invoke(null,new object[]{vertices[indices[at]],boxed});var b=(Vector3)transform.Invoke(null,new object[]{vertices[indices[at+1]],boxed});var c=(Vector3)transform.Invoke(null,new object[]{vertices[indices[at+2]],boxed});Triangle(a,b,c,f.Source.Origin,result);}
            }
            result.InstanceTriangles=result.Triangles-result.FieldTriangles;
        }
        private static void Triangle(Vector3 a,Vector3 b,Vector3 c,Vector3 origin,SurfaceResult result)
        {a-=origin;b-=origin;c-=origin;if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-14f)return;result.Triangles++;result.Volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6d;result.Minimum=Vector3.Min(result.Minimum,Vector3.Min(a,Vector3.Min(b,c)));result.Maximum=Vector3.Max(result.Maximum,Vector3.Max(a,Vector3.Max(b,c)));}
        private T Own<T>(T value)where T:UnityEngine.Object{_owned.Add(value);return value;}
        private static object Get(object target,string name)=>target.GetType().GetField(name,Fields).GetValue(target);
        private static T Property<T>(object target,string name)=>(T)target.GetType().GetProperty(name).GetValue(target);
        private static void AddLight(GameObject host){var child=new GameObject("Liquid evidence light");child.transform.SetParent(host.transform);var light=child.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.4f;light.cullingMask=1<<LiquidLayer;child.transform.rotation=Quaternion.Euler(50,-30,0);}
        private static void DisablePost(Camera camera){var type=Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime",true);type.GetProperty("renderPostProcessing").SetValue(camera.GetComponent(type),false);}
        private static Texture2D Read(RenderTexture target){var prior=RenderTexture.active;RenderTexture.active=target;var image=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);try{image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply();return image;}finally{RenderTexture.active=prior;}}
        private static void SaveImage(RenderTexture target,string path){var image=Read(target);File.WriteAllBytes(path,image.EncodeToPNG());UnityEngine.Object.Destroy(image);}
        private static int Pixels(Color32[] data,int width,int height,out Vector2 centroid,out RectInt bounds)
        {var bg=data[0];int count=0,minX=width,minY=height,maxX=-1,maxY=-1;double x=0,y=0;for(int i=0;i<data.Length;i++){var c=data[i];if(Math.Abs(c.r-bg.r)+Math.Abs(c.g-bg.g)+Math.Abs(c.b-bg.b)<=12)continue;int px=i%width,py=i/width;x+=px;y+=py;count++;minX=Math.Min(minX,px);maxX=Math.Max(maxX,px);minY=Math.Min(minY,py);maxY=Math.Max(maxY,py);}centroid=count>0?new Vector2((float)(x/count),(float)(y/count)):Vector2.zero;bounds=count>0?new RectInt(minX,minY,maxX-minX+1,maxY-minY+1):default;return count;}
        [StructLayout(LayoutKind.Sequential)]private struct Vertex{public Vector3 Position;public float Valid;public Vector3 Normal;public uint A,B,C,D,Pad;}
        [StructLayout(LayoutKind.Sequential)]private struct Instance{public Vector3 Position;public float Radius;public uint First,Second,Third,Fourth;}
        [Serializable]private sealed class SurfaceResult{public string Name,ImagePath,ShaderName,Keywords;public ulong Quantity;public int Triangles,LiquidPixels,VisibleMeshes,MeshVertices,WeightedVertices,DrawLayer,CameraMask,PaletteCount,DomainEnabled,OpaqueDiagnosticPixels,NoInstancingDiagnosticPixels,FieldTriangles,Instances,InstanceTriangles;public bool CpuKeyword,DomainPropertyDeclared,MaterialInstancing,PassCompiledBefore,PassCompiledAfter;public Vector3 Minimum,Maximum;public float Height,WidthX,WidthZ;public double Volume,SignedVolume;}
        [Serializable]private sealed class SurfaceReport{public string Scope;public bool Passed;public SurfaceResult[] Results;}
        [Serializable]private struct PixelFrame{public int Frame,Tick,LiquidPixels;public double Time;public float Alpha;public Vector2 Centroid;public RectInt PixelBounds;}
        [Serializable]private sealed class TrajectoryReport{public string Scope;public PixelFrame[] Samples;public int ActiveSamples,MovingTransitions,LongestStationaryTransitions,Skipped,ReadbackErrors;public float PixelDistance;}
    }
}
#endif
