#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 只比较clone Profile的.2/.3/.4三档，volume恒1，不写正式资源、Solver或History。
    /// 每档五行（1GMU微滴、256GMU小滩、2040GMU深水、同量黏液、真实Solver空中水），
    /// 左列GPU、右列CPU。同一case在所有档位使用同GMU/Camera/灯/Palette。
    /// PNG与实际Draw Triangle的H/W/闭合体积是不同证据，分别保存；同步Readback不作性能证明。
    /// </summary>
    public sealed class LiquidSurfaceTensionCalibrationPlayModeTests
    {
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private const int LiquidLayer=30,Width=1280,Height=720,PanelWidth=640,PanelHeight=360;
        private readonly string _evidence=Path.Combine("TestResults/LiquidTensionCalibration",DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+"-"+Guid.NewGuid().ToString("N").Substring(0,12));
        private readonly List<Object> _owned=new List<Object>();
        private readonly List<Shot> _shots=new List<Shot>();
        private readonly List<string> _failures=new List<string>();
        private Scene _scene;
        private float _oldScale;
        private bool _oldBackground;
        private sealed class Fixture
        {
            internal GameObject Host,Floor;
            internal ILiquidPresentationReadOnly Source;
            internal Component Renderer;
            internal Camera Camera;
            internal RenderTexture Target;
        }
        private sealed class Case
        {
            internal readonly string Name;
            internal readonly MaterialId Material;
            internal readonly uint PerCell;
            internal readonly int Side;
            internal readonly bool Air;
            internal Case(string name,MaterialId material,uint perCell,int side,bool air=false)
            {Name=name;Material=material;PerCell=perCell;Side=side;Air=air;}
            internal ulong Quantity=>(ulong)PerCell*(ulong)Side*(ulong)Side;
        }
        private static readonly Case[] Cases={
            new Case("micro-drop-1gmu",MaterialId.Water,1,1),
            new Case("puddle-256gmu",MaterialId.Water,64,2),
            new Case("deeper-water-2040gmu",MaterialId.Water,510,2),
            new Case("sticky-256gmu",MaterialId.Sticky,64,2),
            new Case("airborne-water-256gmu",MaterialId.Water,256,1,true)};

        [SetUp]public void Setup()
        {
            if(!SystemInfo.supportsComputeShaders)Assert.Ignore("校准要求真实Compute GPU，不能用CPU冒充左列。");
            _oldScale=Time.timeScale;_oldBackground=Application.runInBackground;
            Time.timeScale=1;Application.runInBackground=true;
            Directory.CreateDirectory(_evidence);_scene=SceneManager.CreateScene("Temporary liquid tension comparison");
        }
        [UnityTearDown]public IEnumerator Cleanup()
        {
            foreach(var value in _owned)if(value!=null)Object.Destroy(value);_owned.Clear();yield return null;
            if(_scene.IsValid() && _scene.isLoaded){var operation=SceneManager.UnloadSceneAsync(_scene);if(operation!=null)yield return operation;}
            Time.timeScale=_oldScale;Application.runInBackground=_oldBackground;
        }

        [UnityTest]public IEnumerator ThreeModerateTensionsProduceComparableCpuGpuImagesAndActualGeometry()
        {
            foreach(float tension in new[]{.2f,.3f,.4f})
            {
                string tag="tension-"+Mathf.RoundToInt(tension*100).ToString("D3");
                var board=Own(new Texture2D(PanelWidth*2,PanelHeight*Cases.Length,TextureFormat.RGB24,false));
                for(int row=0;row<Cases.Length;row++)for(int mode=0;mode<2;mode++)
                {
                    bool cpu=mode==1;var entry=Cases[row];string name=tag+"-"+entry.Name+(cpu?"-cpu":"-gpu");
                    var f=Create(entry,tension,cpu);f.Host.SetActive(true);yield return null;yield return null;
                    double until=Time.realtimeSinceStartupAsDouble+12;
                    while(!Property<bool>(f.Renderer,"IsReady") || Property<bool>(f.Renderer,"IsPresentationPending"))
                    {Assert.That(Time.realtimeSinceStartupAsDouble,Is.LessThan(until),name+"未完成真实Renderer构建。");yield return null;}
                    // 仅把测试显示时钟固定到Current。空中位置来自真实3次Solver，不由测试重写数量/History。
                    Set(f.Renderer,"_capturedAt",Time.time-.25f);yield return null;yield return new WaitForEndOfFrame();
                    var shot=Measure(f,name,cpu);shot.Case=entry.Name;shot.Tension=tension;
                    shot.RequestedGmu=entry.Quantity;shot.ImagePath=Path.Combine(_evidence,name+".png");
                    shot.CameraPosition=f.Camera.transform.position;shot.CameraForward=f.Camera.transform.forward;shot.FieldOfView=f.Camera.fieldOfView;
                    var picture=Read(f.Target);File.WriteAllBytes(shot.ImagePath,picture.EncodeToPNG());CopyPanel(board,picture,mode,row);Object.Destroy(picture);
                    // liquid-only第二张图排除地板、天空或另一Scene画面产生假阳性。
                    f.Camera.cullingMask=1<<LiquidLayer;f.Camera.backgroundColor=new Color(.11f,.017f,.11f,1);
                    yield return null;yield return new WaitForEndOfFrame();
                    var mask=Read(f.Target);shot.LiquidPixels=CountPixels(mask.GetPixels32());
                    File.WriteAllBytes(Path.Combine(_evidence,name+"-mask.png"),mask.EncodeToPNG());Object.Destroy(mask);
                    shot.Quantity=f.Source.TotalQuantity;shot.Pending=f.Source.PendingQuantity;
                    File.WriteAllText(Path.Combine(_evidence,name+".json"),JsonUtility.ToJson(shot,true));_shots.Add(shot);
                    if(shot.Triangles==0)_failures.Add(name+": actual drawn Triangle empty");
                    if(shot.LiquidPixels==0)_failures.Add(name+": liquid-only pixels empty");
                    Assert.That(shot.Quantity+shot.Pending,Is.EqualTo(entry.Quantity),"表现校准不能改GMU。");
                    f.Camera.targetTexture=null;Object.Destroy(f.Camera.gameObject);Object.Destroy(f.Host);Object.Destroy(f.Floor);
                    f.Target.Release();Object.Destroy(f.Target);yield return null;
                }
                board.Apply();File.WriteAllBytes(Path.Combine(_evidence,tag+"-comparison.png"),board.EncodeToPNG());Object.Destroy(board);
            }
            var report=new Report{Scope="3 cloned water tension values, volume 1, crown .25; rows micro-drop/puddle/deep/sticky/air; columns GPU/CPU. All panels use actual rendered textures. Same case uses identical GMU/camera/light/palette. Closed mesh volume is reconstruction evidence, not exact physical volume or a Solver write. Synchronous GPU readbacks and PNG IO are not performance measurements.",Shots=_shots.ToArray(),Failures=_failures.ToArray()};
            File.WriteAllText(Path.Combine(_evidence,"calibration-summary.json"),JsonUtility.ToJson(report,true));
            File.WriteAllText(Path.Combine(_evidence,"图像说明.md"),"每图左GPU、右CPU；从上到下：1GMU微滴、256GMU小滩、2040GMU深水、256GMU黏液、真实Solver推进的空中256GMU水。每个case在三档中同镜头、同GMU、同光照。量化见calibration-summary.json。没有以变薄为成功条件。\n");
            Assert.That(_failures,Is.Empty,"保留所有30shot后统一报告不可见项，不能只留下第一档或用场景背景当液体。");
        }

        [UnityTest]public IEnumerator ContactMicrodropShaderPixelsMatchItsVolumeGeometryAcrossSubcellPhasesAndCpuFallback()
        {
            var phases=new[]{new Vector2(0,0),new Vector2(.125f,.25f),new Vector2(.25f,.375f),new Vector2(.375f,.125f),new Vector2(-.25f,.375f)};
            for(int mode=0;mode<2;mode++)for(int phase=0;phase<phases.Length;phase++)
            {
                bool cpu=mode==1;var f=Create(Cases[0],.3f,cpu,phases[phase]);f.Camera.cullingMask=1<<LiquidLayer;f.Camera.backgroundColor=new Color(.11f,.017f,.11f,1);f.Host.SetActive(true);
                yield return null;yield return null;double until=Time.realtimeSinceStartupAsDouble+12;
                while(!Property<bool>(f.Renderer,"IsReady") || Property<bool>(f.Renderer,"IsPresentationPending")){Assert.That(Time.realtimeSinceStartupAsDouble,Is.LessThan(until));yield return null;}
                yield return new WaitForEndOfFrame();
                string name="subcell-"+phase+(cpu?"-cpu":"-gpu");var shot=Measure(f,name,cpu);var image=Read(f.Target);
                shot.LiquidPixels=PixelBounds(image.GetPixels32(),Width,Height,out RectInt bounds);shot.PixelBounds=bounds;
                shot.ImagePath=Path.Combine(_evidence,name+".png");File.WriteAllBytes(shot.ImagePath,image.EncodeToPNG());Object.Destroy(image);
                File.WriteAllText(Path.Combine(_evidence,name+".json"),JsonUtility.ToJson(shot,true));
                Assert.That(shot.Instances,Is.EqualTo(1));Assert.That(shot.FieldTriangles,Is.Zero,"同相位不能field和cap双算。");
                Assert.That(shot.LiquidPixels,Is.GreaterThan(8),"任意采样相位都必须实际可见，不能只检查Draw调用。");
                Assert.That(shot.Height,Is.GreaterThan(.01f));Assert.That(shot.WidthX,Is.LessThan(.25f));
                Assert.That(shot.Volume/(Math.Pow(.5,3)/510),Is.InRange(.85,1.15));
                // 从实际上传GPU的实例数据与共享模板投影得到包围盒；实际Camera像素必须匹配。
                // 若Shader仍画旧球/薄片、中心或负code解码有误，此处会失败，不能用CPU数学冒充画面。
                Assert.That(bounds.xMin,Is.EqualTo(shot.ProjectedMinimum.x).Within(6));Assert.That(bounds.xMax,Is.EqualTo(shot.ProjectedMaximum.x).Within(6));
                Assert.That(bounds.yMin,Is.EqualTo(shot.ProjectedMinimum.y).Within(6));Assert.That(bounds.yMax,Is.EqualTo(shot.ProjectedMaximum.y).Within(6));
                Assert.That(f.Source.TotalQuantity+f.Source.PendingQuantity,Is.EqualTo(1UL));
                f.Camera.targetTexture=null;Object.Destroy(f.Camera.gameObject);Object.Destroy(f.Host);Object.Destroy(f.Floor);f.Target.Release();Object.Destroy(f.Target);yield return null;
            }
        }

        private Fixture Create(Case entry,float tension,bool cpu,Vector2? subcell=null)
        {
            var origin=new Vector3(100,30,100);
            var host=Own(new GameObject("Tension visual calibration"));host.layer=LiquidLayer;host.SetActive(false);SceneManager.MoveGameObjectToScene(host,_scene);
            var floor=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));SceneManager.MoveGameObjectToScene(floor,_scene);
            floor.transform.position=origin+new Vector3(1.5f,-.25f,1.5f);floor.transform.localScale=new Vector3(6,.5f,6);Physics.SyncTransforms();
            var worldProfile=Own(Object.Instantiate(AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset")));
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":256,\"_enableAutoStableDetection\":false}",worldProfile);
            var source=host.AddComponent<LiquidWorldRuntime>();Set(source,"_profile",worldProfile);var baker=host.AddComponent<ElementFieldSolidBaker>();
            Assert.That(source.TryInitialize(origin,.25f,4,baker),Is.True);ILiquidPresentationReadOnly view=source;
            if(subcell.HasValue)view=CreateReplicaSubcellSource(origin,worldProfile,baker,subcell.Value);
            else
            {
                for(int z=2;z<2+entry.Side;z++)for(int x=2;x<2+entry.Side;x++)Assert.That(source.TryAddInitial(new Vector3Int(x,entry.Air?6:0,z),entry.Material,entry.PerCell,entry.Air),Is.True);
                int steps=entry.Air?3:1;for(int tick=1;tick<=steps;tick++)source.Step(.1f,tick*.1f);
            }
            Assert.That(view.TotalQuantity+view.PendingQuantity,Is.EqualTo(entry.Quantity));
            var renderProfile=Own(Object.Instantiate(AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset")));
            JsonUtility.FromJsonOverwrite("{\"_budgetMiB\":32,\"_inputChunks\":16,\"_views\":16,\"_fineViews\":"+(cpu?"0":"16")+",\"_cullByCamera\":false,\"_cartoonCrownStrength\":0.25,\"_cartoonVolumeScale\":1,\"_useMaterialSurfaceTension\":true,\"_waterSurfaceTension\":"+tension.ToString("R",CultureInfo.InvariantCulture)+"}",renderProfile);
            var rendererType=Type.GetType("Game.Rendering.GridLiquidFieldSurfaceRenderer, Game.Rendering",true);var renderer=(Component)host.AddComponent(rendererType);
            rendererType.GetMethod("ConfigureForValidation").Invoke(renderer,new object[]{view,renderProfile});
            var cameraHost=Own(new GameObject("Tension fixed evidence camera"));SceneManager.MoveGameObjectToScene(cameraHost,_scene);var camera=cameraHost.AddComponent<Camera>();
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.035f,.06f,1);camera.fieldOfView=40;camera.aspect=(float)Width/Height;
            var center=origin+new Vector3(entry.Side==1?1.25f:1.5f,entry.Air?2.75f:entry.PerCell==510?.25f:.05f,entry.Side==1?1.25f:1.5f);
            var offset=entry.PerCell==1?new Vector3(-1.25f,.8f,-1.75f):new Vector3(-2.5f,1.6f,-3.5f);
            cameraHost.transform.position=center+offset;cameraHost.transform.LookAt(center);
            var additionalType=Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime",true);
            var additional=cameraHost.AddComponent(additionalType);additionalType.GetProperty("requiresDepthTexture").SetValue(additional,true);additionalType.GetProperty("renderPostProcessing").SetValue(additional,false);
            var target=Own(new RenderTexture(Width,Height,24,RenderTextureFormat.ARGB32));target.Create();camera.targetTexture=target;
            rendererType.GetMethod("SetValidationViewCamera").Invoke(renderer,new object[]{camera});
            var lightHost=new GameObject("Fixed comparison light");lightHost.transform.SetParent(host.transform);var light=lightHost.AddComponent<Light>();
            light.type=LightType.Directional;light.intensity=1.4f;light.cullingMask=1<<LiquidLayer;lightHost.transform.rotation=Quaternion.Euler(50,-30,0);
            return new Fixture{Host=host,Floor=floor,Source=view,Renderer=renderer,Camera=camera,Target=target};
        }

        private static WorldReplicaLiquidReadModel CreateReplicaSubcellSource(Vector3 origin,LiquidWorldProfile profile,ElementFieldSolidBaker baker,Vector2 offset)
        {
            var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,origin,.5f,4);var store=new WorldReplicaStore(7,16,256,0,2048);
            Assert.That(store.ConfigureGeometry(geometry),Is.True);Assert.That(store.ConfigureMaterials(WorldMaterialDomain.Liquid,510,new[]{(byte)MaterialId.Water}),Is.True);
            var cell=new Vector3Int(2,0,2);var key=new WorldChunkKey(WorldMaterialDomain.Liquid,0,0,0);var words=new uint[11+WorldChunkPayloadCodec.CellWords];
            words[0]=WorldChunkPayloadCodec.Magic;words[1]=1;words[3]=1;WorldChunkPayloadCodec.WriteGeometry(words,4,geometry);
            WorldChunkPayloadCodec.WriteCell(words,11,new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Resident,cell,(byte)MaterialId.Water,1));
            Assert.That(store.TryInstall(new WorldChunkPayloadHeader(7,1,key,1,0,1,1,words.Length,WorldChunkPayloadCodec.ComputeChecksum(words,words.Length),true),words,words.Length),Is.True);
            var read=new WorldReplicaLiquidReadModel(store,geometry,profile,.25f,baker);read.ResetConnection(3);
            var motion=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=1,MaterialTick=1,SampleTime=10,Interval=.1f,ChunkSize=4,AnchorCount=1,RowCount=1};motion.Anchors[0]=new WorldLiquidMotionAnchor(key,1,1,1,true);
            var pose=new Vector3(offset.x,0,offset.y);motion.Rows[0]=new WorldLiquidMotionSample(cell,(byte)MaterialId.Water,1,1,pose,Vector3.zero,pose,Vector3.zero,0);
            Assert.That(read.TryPublish(motion,10.01),Is.True);return read;
        }

        private Shot Measure(Fixture f,string name,bool cpu)
        {
            var result=new Shot{Name=name,Cpu=cpu,Minimum=Vector3.one*float.PositiveInfinity,Maximum=Vector3.one*float.NegativeInfinity};
            Assert.That(Property<bool>(f.Renderer,"CpuFallback"),Is.EqualTo(cpu));
            Assert.That(Property<int>(f.Renderer,"HybridPages"),Is.Zero,"不能漏掉另一种绘制路径后宣称几何一致。");
            if(!cpu)
            {
                int pages=Property<int>(f.Renderer,"SurfacePages");var arguments=new uint[pages*5];Property<GraphicsBuffer>(f.Renderer,"DebugArguments").GetData(arguments);
                int end=0;for(int page=0;page<pages;page++)end=Math.Max(end,(int)(arguments[page*5+2]+arguments[page*5]));
                if(end>0)
                {
                    var indices=new uint[end];Property<GraphicsBuffer>(f.Renderer,"DebugIndices").GetData(indices,0,0,end);
                    var frame=f.Renderer.GetType().GetProperty("Frame").GetValue(f.Renderer);int side=Property<int>(frame,"ChunkEdge")*Property<int>(f.Renderer,"SurfaceSubdivision")+6;
                    var vertices=new Vertex[pages*side*side*side];Property<GraphicsBuffer>(f.Renderer,"DebugVertices").GetData(vertices,0,0,vertices.Length);
                    for(int page=0;page<pages;page++)for(int at=(int)arguments[page*5+2],stop=at+(int)arguments[page*5];at<stop;at+=3)
                        Triangle(vertices[indices[at]].Position,vertices[indices[at+1]].Position,vertices[indices[at+2]].Position,f.Source.Origin,result);
                }
            }
            else
            {
                var cpuSurface=Get(f.Renderer,"_cpu");var meshes=(Mesh[])Get(cpuSurface,"_meshes");int count=(int)Get(cpuSurface,"_visible");
                for(int mesh=0;mesh<count;mesh++){var vertices=meshes[mesh].vertices;var indices=meshes[mesh].triangles;
                    for(int at=0;at<indices.Length;at+=3)Triangle(vertices[indices[at]],vertices[indices[at+1]],vertices[indices[at+2]],f.Source.Origin,result);}
            }
            result.FieldTriangles=result.Triangles;AddActualInstanceGeometry(f,result);
            result.SignedVolume=result.Volume;result.Volume=Math.Abs(result.Volume);
            if(result.Triangles>0){result.Height=result.Maximum.y-result.Minimum.y;result.WidthX=result.Maximum.x-result.Minimum.x;result.WidthZ=result.Maximum.z-result.Minimum.z;}
            else result.Minimum=result.Maximum=Vector3.zero;
            return result;
        }
        private static void AddActualInstanceGeometry(Fixture f,Shot result)
        {
            int count=Property<int>(f.Renderer,"SmallDropCount");result.Instances=count;if(count==0){result.ProjectedMinimum=result.ProjectedMaximum=Vector2.zero;return;}
            var gpu=Get(f.Renderer,"_gpu");var buffer=(GraphicsBuffer)(gpu!=null?Get(gpu,"Drops"):Get(f.Renderer,"_fallbackDropBuffer"));
            var instances=new Instance[count];buffer.GetData(instances,0,0,count);
            var dropType=Type.GetType("Game.Rendering.GridLiquidSmallDrop, Game.Rendering",true);
            var shape=Type.GetType("Game.Rendering.GridLiquidGroundContactShape, Game.Rendering",true).GetMethod("TransformPosition",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
            var template=Type.GetType("Game.Rendering.LiquidDropletTemplate, Game.Rendering",true);
            var directions=(Vector3[])template.GetField("Vertices",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            var indices=(int[])template.GetField("Indices",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            foreach(var instance in instances)
            {
                object boxed=Activator.CreateInstance(dropType);dropType.GetField("Position").SetValue(boxed,instance.Position);dropType.GetField("Radius").SetValue(boxed,instance.Radius);dropType.GetField("Third").SetValue(boxed,instance.Third);dropType.GetField("Fourth").SetValue(boxed,instance.Fourth);
                for(int at=0;at<indices.Length;at+=3)
                {
                    var a=(Vector3)shape.Invoke(null,new object[]{directions[indices[at]],boxed});var b=(Vector3)shape.Invoke(null,new object[]{directions[indices[at+1]],boxed});var c=(Vector3)shape.Invoke(null,new object[]{directions[indices[at+2]],boxed});
                    Triangle(a,b,c,f.Source.Origin,result);Project(a,f.Camera,result);Project(b,f.Camera,result);Project(c,f.Camera,result);
                }
            }
            result.InstanceTriangles=result.Triangles-result.FieldTriangles;
        }
        private static void Project(Vector3 position,Camera camera,Shot result)
        {var point=camera.WorldToScreenPoint(position);var p=new Vector2(point.x,point.y);result.ProjectedMinimum=Vector2.Min(result.ProjectedMinimum,p);result.ProjectedMaximum=Vector2.Max(result.ProjectedMaximum,p);}
        private static void Triangle(Vector3 a,Vector3 b,Vector3 c,Vector3 origin,Shot result)
        {a-=origin;b-=origin;c-=origin;if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-14f)return;result.Triangles++;result.Volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6d;result.Minimum=Vector3.Min(result.Minimum,Vector3.Min(a,Vector3.Min(b,c)));result.Maximum=Vector3.Max(result.Maximum,Vector3.Max(a,Vector3.Max(b,c)));}
        private static void CopyPanel(Texture2D board,Texture2D image,int column,int row)
        {
            var source=image.GetPixels32();var panel=new Color32[PanelWidth*PanelHeight];
            for(int y=0;y<PanelHeight;y++)for(int x=0;x<PanelWidth;x++)panel[y*PanelWidth+x]=source[(y*2)*Width+x*2];
            board.SetPixels32(column*PanelWidth,(Cases.Length-1-row)*PanelHeight,PanelWidth,PanelHeight,panel);
        }
        private static Texture2D Read(RenderTexture target)
        {var prior=RenderTexture.active;RenderTexture.active=target;var picture=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);try{picture.ReadPixels(new Rect(0,0,target.width,target.height),0,0);picture.Apply();return picture;}finally{RenderTexture.active=prior;}}
        private static int CountPixels(Color32[] pixels)=>PixelBounds(pixels,Width,Height,out _);
        private static int PixelBounds(Color32[] pixels,int width,int height,out RectInt bounds)
        {
            var background=pixels[0];int count=0,minX=width,minY=height,maxX=-1,maxY=-1;
            for(int i=0;i<pixels.Length;i++)if(Math.Abs(pixels[i].r-background.r)+Math.Abs(pixels[i].g-background.g)+Math.Abs(pixels[i].b-background.b)>12)
            {int x=i%width,y=i/width;count++;minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}
            bounds=count>0?new RectInt(minX,minY,maxX-minX+1,maxY-minY+1):default;return count;
        }
        private T Own<T>(T value)where T:Object{_owned.Add(value);return value;}
        private static object Get(object target,string field)=>target.GetType().GetField(field,Fields).GetValue(target);
        private static void Set(object target,string field,object value)=>target.GetType().GetField(field,Fields).SetValue(target,value);
        private static T Property<T>(object target,string property)=>(T)target.GetType().GetProperty(property).GetValue(target);
        [StructLayout(LayoutKind.Sequential)]private struct Vertex{public Vector3 Position;public float Valid;public Vector3 Normal;public uint A,B,C,D,Pad;}
        [StructLayout(LayoutKind.Sequential)]private struct Instance{public Vector3 Position;public float Radius;public uint First,Second,Third,Fourth;}
        [Serializable]private sealed class Shot
        {
            public string Name,Case,ImagePath;public bool Cpu;public float Tension,Height,WidthX,WidthZ,FieldOfView;
            public ulong RequestedGmu,Quantity,Pending;public int Triangles,LiquidPixels,FieldTriangles,Instances,InstanceTriangles;public double Volume,SignedVolume;
            public Vector3 Minimum,Maximum,CameraPosition,CameraForward;
            public Vector2 ProjectedMinimum=Vector2.one*float.PositiveInfinity,ProjectedMaximum=Vector2.one*float.NegativeInfinity;
            public RectInt PixelBounds;
        }
        [Serializable]private sealed class Report{public string Scope;public Shot[] Shots;public string[] Failures;}
    }
}
#endif
