#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Game.ElementField;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 真正Play中的Solver→History→GPU→Camera像素回归。临时Lab只存在于本次内存Scene，
    /// 不保存正式Scene/资源，不暂停时间、不直接捏造Renderer输入。Reflection仅用于越过测试
    /// Assembly没有Game.Rendering引用的诊断边界，不把它引入生产热路径或另建显示系统。
    /// </summary>
    public sealed class LiquidDynamicPresentationPlayModeTests
    {
        private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        private const int LiquidLayer=30;
        private readonly string _evidence=Path.Combine("TestResults/LiquidDynamicPresentation",
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+"-"+Guid.NewGuid().ToString("N"));
        private Scene _scene;
        private GameObject _host;
        private Component _driver;
        private float _previousTimeScale;
        private bool _previousBackground;
        private readonly ElementChunkKey[] _keys=new ElementChunkKey[64];

        [SetUp]
        public void Setup()
        {
            _previousTimeScale=Time.timeScale;_previousBackground=Application.runInBackground;
            if(!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)
                Assert.Ignore("动态视觉回归需要实际Compute GPU。");
            Directory.CreateDirectory(_evidence);Directory.CreateDirectory("Temp/ElementPerformance");
            Time.timeScale=1;Application.runInBackground=true;
        }
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            // Initialize异常时，Lab的Start尚未把支撑Collider移进Host；也要按本夹具保存的引用清理，
            // 不能让一次失败在随后case里留下额外地板，更不能全局按名字删除用户对象。
            if(_driver!=null)
            {
                var support=(GameObject)Get(_driver,"_support");if(support!=null && support.transform.parent!=_host.transform)UnityEngine.Object.Destroy(support);
                var receiver=(GameObject)Get(_driver,"_receiver");if(receiver!=null && receiver.transform.parent!=_host.transform)UnityEngine.Object.Destroy(receiver);
            }
            if(_host!=null)UnityEngine.Object.Destroy(_host);
            yield return null;
            if(_scene.IsValid() && _scene.isLoaded)
            {var operation=SceneManager.UnloadSceneAsync(_scene);if(operation!=null)yield return operation;}
            Time.timeScale=_previousTimeScale;Application.runInBackground=_previousBackground;
        }

        [UnityTest] public IEnumerator AirborneWaterActuallyFallsAndProducesGpuPixels()=>Run("air-fall");
        [UnityTest] public IEnumerator AirborneStickyActuallyFallsAndProducesGpuPixels()=>Run("air-fall-sticky");
        [UnityTest] public IEnumerator GroundFlowKeepsQuantityAndProducesGpuPixels()=>Run("ground-flow");
        [UnityTest] public IEnumerator PlatformOverflowKeepsQuantityAndProducesGpuPixels()=>Run("platform");
        [UnityTest] public IEnumerator CollectionRemovesRealQuantityAndUpdatesVisibleGpuSurface()=>Run("collection");
        [UnityTest] public IEnumerator PourStreamContinuesFromAirborneNozzleAndMovesRealQuantity()=>Run("pour-stream");

        private IEnumerator Run(string kind)
        {
            // NUnit可能在同一个fixture实例上执行多个case，也可能按过滤器重试同一case。
            // 每次运行独立目录与截图前缀，保留旧失败；不能靠File.Copy(overwrite:true)掩盖取证冲突。
            string runId=Guid.NewGuid().ToString("N").Substring(0,12);
            string evidence=Path.Combine(_evidence,kind+"-"+runId);Directory.CreateDirectory(evidence);
            _scene=SceneManager.CreateScene("Temporary dynamic liquid "+kind);
            _host=new GameObject("PlayMode liquid presentation fixture");_host.layer=LiquidLayer;_host.SetActive(false);
            SceneManager.MoveGameObjectToScene(_host,_scene);
            var type=Type.GetType("Game.Rendering.GridLiquidFieldLabDriver, Game.Rendering",true);
            var driver=(Component)_host.AddComponent(type);_driver=driver;
            type.GetMethod("Initialize").Invoke(driver,new object[]{kind});
            var source=(LiquidWorldRuntime)Get(driver,"_source");
            var renderer=(Component)Get(driver,"_renderer");
            var camera=(Camera)Get(driver,"_camera");
            var target=(RenderTexture)Get(driver,"_target");
            if(kind.StartsWith("air-fall",StringComparison.Ordinal))
            {
                // 新水扩散后，30Tick的实测GPU Bounds已铺到X/Z=-.675..4.675m。
                // 旧镜头盯住Y=3m，地面Bounds底边被投到-118px；这是取景错误，不能
                // 通过放宽Mask边缘断言或改Runtime Bounds掩盖。固定镜头一次容纳预定的
                // 地面[-1.5,5.5]与空中[-.1,6.5]m范围，随后不跟随液体逐帧重新对焦。
                Vector3 focus=source.Origin+new Vector3(2,2.5f,2);
                camera.transform.position=focus+new Vector3(-9,4.5f,-11.25f);camera.transform.LookAt(focus);
                camera.orthographic=false;camera.fieldOfView=45;camera.aspect=(float)target.width/target.height;
                camera.ResetProjectionMatrix();camera.ResetWorldToCameraMatrix();camera.ResetCullingMatrix();
            }
            camera.cullingMask=~0;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.035f,.06f,1);
            var cameraDataType=Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime",true);
            var cameraData=camera.GetComponent(cameraDataType);
            cameraDataType.GetProperty("renderPostProcessing").SetValue(cameraData,false);
            // 定向光只照临时液体层；已有Scene的渲染和资产均不改写。
            var lightObject=new GameObject("Temporary liquid validation light");lightObject.transform.SetParent(_host.transform);
            var light=lightObject.AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.4f;light.cullingMask=1<<LiquidLayer;
            lightObject.transform.rotation=Quaternion.Euler(50,-30,0);
            double initialY=kind=="pour-stream"?source.Origin.y+3.25:CentroidY(source);
            if(kind=="pour-stream")Assert.That(source.PendingQuantity,Is.EqualTo(256UL),"喷口量首先进入真实Pending，不直接造落地点水池。");
            else Assert.That(source.ActiveCellCount,Is.GreaterThan(0),"必须真正wake，不用静态悬浮片冒充下落。");
            _host.SetActive(true);yield return null;yield return null;
            Assert.That(Property<bool>(renderer,"IsReady"),Is.True);
            Assert.That(Property<bool>(renderer,"CpuFallback"),Is.False,"本组验收必须实际走GPU。");
            int[] ticks={5,15,30};var observations=new Observation[ticks.Length];
            double deadline=Time.realtimeSinceStartupAsDouble+120;
            for(int shot=0;shot<ticks.Length;shot++)
            {
                while(Property<int>(driver,"Tick")<ticks[shot] || !Property<bool>(driver,"CanCapture"))
                {
                    Assert.That(Time.realtimeSinceStartupAsDouble,Is.LessThan(deadline),"真实Solver未推进到所需Tick。");
                    AssertConservation(driver,source);yield return null;
                }
                // 一帧正常Camera渲染结束后读取RT，不关闭Solver或把TimeScale设为0。
                yield return new WaitForEndOfFrame();
                string suffix="-play-"+runId+"-tick"+ticks[shot];
                type.GetMethod("Capture").Invoke(driver,new object[]{suffix});
                string prefix="continuous-lab-"+kind+suffix;
                File.Copy(Path.Combine("Temp/ElementPerformance",prefix+".png"),Path.Combine(evidence,prefix+".png"));
                File.Copy(Path.Combine("Temp/ElementPerformance",prefix+".json"),Path.Combine(evidence,prefix+".json"));

                // 仅测试相机额外拍一帧液体专属Layer：地板、天空和UI都不会贡献像素。
                // 因而“有图像”不能被误判成“实际液体已绘制”。这帧与GPU计数共同作证。
                int previousMask=camera.cullingMask;Color previousColor=camera.backgroundColor;
                camera.cullingMask=1<<LiquidLayer;camera.backgroundColor=new Color(.11f,.017f,.11f,1);
                yield return null;yield return new WaitForEndOfFrame();
                var image=Read(target);int pixels=CountLiquidPixels(image,out var minimumPixel,out var maximumPixel);
                File.WriteAllBytes(Path.Combine(evidence,prefix+"-liquid-mask.png"),image.EncodeToPNG());UnityEngine.Object.Destroy(image);
                camera.cullingMask=previousMask;camera.backgroundColor=previousColor;

                ulong triangles=GpuTriangles(renderer);int drops=Property<int>(renderer,"SmallDropCount");
                // 在所有断言之前保存真实GPU几何的8角投影与固定相机参数。失败证据也能明确
                // 区分Camera裁切、Runtime缺几何、或者只有错误颜色的背景，不能只留一张空图。
                var shape=JsonUtility.FromJson<ShotGeometry>(File.ReadAllText(Path.Combine(evidence,prefix+".json")));
                Vector3 viewportMin=Vector3.one*float.PositiveInfinity,viewportMax=Vector3.one*float.NegativeInfinity;
                for(int corner=0;corner<8;corner++)
                {
                    Vector3 point=new Vector3((corner&1)==0?shape.MeshMinimum.x:shape.MeshMaximum.x,
                        (corner&2)==0?shape.MeshMinimum.y:shape.MeshMaximum.y,(corner&4)==0?shape.MeshMinimum.z:shape.MeshMaximum.z);
                    Vector3 viewport=camera.WorldToViewportPoint(source.Origin+point);
                    viewportMin=Vector3.Min(viewportMin,viewport);viewportMax=Vector3.Max(viewportMax,viewport);
                }
                File.WriteAllText(Path.Combine(evidence,prefix+"-framing.json"),JsonUtility.ToJson(new FramingEvidence{
                    CameraPosition=camera.transform.position,CameraForward=camera.transform.forward,FieldOfView=camera.fieldOfView,Aspect=camera.aspect,
                    MeshMinimum=shape.MeshMinimum,MeshMaximum=shape.MeshMaximum,ProjectedMinimum=viewportMin,ProjectedMaximum=viewportMax,
                    MinimumLiquidPixel=minimumPixel,MaximumLiquidPixel=maximumPixel,ActualTick=Property<int>(driver,"Tick"),Frame=Time.frameCount},true));
                Assert.That(triangles>0 || drops>0,Is.True,"需真实Indirect三角形或实际绘制的小滴几何。");
                Assert.That(pixels,Is.GreaterThan(8),"液体专属相机中必须实际有非背景像素。");
                if(kind.StartsWith("air-fall",StringComparison.Ordinal) || kind=="pour-stream")
                {
                    // 单纯Count>0不能发现顶部截断。液团Mask四边都要保留8px背景，
                    // 同时记录实际像素Bounds，便于区分取景失误与真正的形态问题。
                    Assert.That(minimumPixel.x,Is.GreaterThan(8),"空中液团左侧被画面裁切。");
                    Assert.That(minimumPixel.y,Is.GreaterThan(8),"空中液团底部被画面裁切。");
                    Assert.That(maximumPixel.x,Is.LessThan(target.width-9),"空中液团右侧被画面裁切。");
                    Assert.That(maximumPixel.y,Is.LessThan(target.height-9),"空中液团顶部被画面裁切。");
                }
                AssertConservation(driver,source);
                observations[shot]=new Observation{RequestedTick=ticks[shot],ActualTick=Property<int>(driver,"Tick"),
                    Frame=Time.frameCount,Quantity=source.TotalQuantity,Pending=source.PendingQuantity,Collected=(ulong)Get(driver,"_collected"),
                    Injected=(uint)Get(driver,"_injected"),Triangles=triangles,SmallDrops=drops,LiquidPixels=pixels,CentroidY=CentroidY(source),
                    CpuFallback=Property<bool>(renderer,"CpuFallback"),RendererAlpha=Property<float>(renderer,"InterpolationAlpha"),
                    MinimumLiquidPixel=minimumPixel,MaximumLiquidPixel=maximumPixel};
            }
            Assert.That(Property<int>(driver,"Tick"),Is.EqualTo(30),"临时Lab只在真实第30Tick结束注入/推进。");
            if(kind.StartsWith("air-fall",StringComparison.Ordinal))
                Assert.That(observations[2].CentroidY,Is.LessThan(initialY-.25),"真实材料重心必须下落，而不仅是Shader动了。");
            if(kind=="collection")
            {
                ulong before=(ulong)Get(driver,"_beforeCollection"),after=(ulong)Get(driver,"_afterCollection"),collected=(ulong)Get(driver,"_collected");
                Assert.That(collected,Is.GreaterThan(0));Assert.That(after,Is.GreaterThan(0));
                Assert.That(before-after,Is.EqualTo(collected),"收走的量与World减少量一致。");
                Assert.That(observations[2].Quantity+observations[2].Pending,Is.LessThan(observations[0].Quantity+observations[0].Pending));
            }
            if(kind=="pour-stream")
            {
                Assert.That((int)Get(driver,"_pourRequests"),Is.EqualTo(11),"0、2..20共11次有限喷口注入。");
                Assert.That((uint)Get(driver,"_injected"),Is.EqualTo(2816u));
                Assert.That(observations[1].Injected,Is.GreaterThan(observations[0].Injected),"可见窗口内应持续洒出。");
                Assert.That(observations[2].Injected,Is.GreaterThan(observations[1].Injected));
                Assert.That(CentroidAxis(source,0),Is.GreaterThan(source.Origin.x+1.75),"初速度必须推动真实GMU离开喷口，不能仅有Shader拉伸。");
            }
            File.WriteAllText(Path.Combine(evidence,kind+"-summary.json"),JsonUtility.ToJson(new CaseReport{Case=kind,
                Scope="Real PlayMode Solver/GPU/fixed camera, ticks advance normally. Layer-only image rejects floor/sky false positives; collection validates renderer after real TryConsume, not F permissions.",
                InitialCentroidY=initialY,Observations=observations,Passed=true},true));
        }
        private static object Get(object target,string name)=>target.GetType().GetField(name,Private).GetValue(target);
        private static T Property<T>(object target,string name)=>(T)target.GetType().GetProperty(name).GetValue(target);
        private static void AssertConservation(Component driver,LiquidWorldRuntime source)
        {Assert.That(source.TotalQuantity+source.PendingQuantity+(ulong)Get(driver,"_collected"),Is.EqualTo((ulong)(uint)Get(driver,"_injected")),"World+Pending+已收取必须等于实际注入量。");}
        private static Texture2D Read(RenderTexture target)
        {
            var prior=RenderTexture.active;RenderTexture.active=target;var texture=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            try{texture.ReadPixels(new Rect(0,0,target.width,target.height),0,0);texture.Apply();return texture;}
            finally{RenderTexture.active=prior;}
        }
        private static int CountLiquidPixels(Texture2D image,out Vector2Int minimum,out Vector2Int maximum)
        {
            var colors=image.GetPixels32();var background=colors[0];int count=0;
            minimum=new Vector2Int(image.width,image.height);maximum=new Vector2Int(-1,-1);
            for(int i=0;i<colors.Length;i++)
            {
                var c=colors[i];if(Math.Abs(c.r-background.r)+Math.Abs(c.g-background.g)+Math.Abs(c.b-background.b)<=12)continue;
                count++;int x=i%image.width,y=i/image.width;
                minimum=Vector2Int.Min(minimum,new Vector2Int(x,y));maximum=Vector2Int.Max(maximum,new Vector2Int(x,y));
            }
            return count;
        }
        private static ulong GpuTriangles(Component renderer)
        {
            var buffer=Property<GraphicsBuffer>(renderer,"DebugArguments");int pages=Property<int>(renderer,"SurfacePages");
            Assert.That(buffer,Is.Not.Null);if(pages==0)return 0;var arguments=new uint[pages*5];buffer.GetData(arguments,0,0,arguments.Length);
            ulong indices=0;for(int i=0;i<pages;i++)indices+=arguments[i*5];return indices/3;
        }
        private double CentroidY(LiquidWorldRuntime source)=>CentroidAxis(source,1);
        private double CentroidAxis(LiquidWorldRuntime source,int axis)
        {
            int pages=source.CopyLiquidChunkKeys(_keys),k=source.ChunkSize;double weighted=0;ulong total=0;
            for(int page=0;page<pages && page<_keys.Length;page++)for(int i=0;i<k*k*k;i++)for(int m=0;m<source.MaterialCount;m++)
            {
                var key=_keys[page];var cell=new Vector3Int(key.X*k+i%k,key.Y*k+i/k%k,key.Z*k+i/(k*k));
                if(!source.TryGetPresentationSample(cell,source.GetLiquidMaterial(m),false,out var sample) || sample.Quantity==0)continue;
                weighted+=(source.Origin[axis]+(cell[axis]+.5+sample.OffsetCells[axis])*source.CellSize)*sample.Quantity;total+=sample.Quantity;
            }
            Assert.That(total,Is.GreaterThan(0));return weighted/total;
        }
        [Serializable] private sealed class CaseReport{public string Case,Scope;public bool Passed;public double InitialCentroidY;public Observation[] Observations;}
        [Serializable] private sealed class ShotGeometry{public Vector3 MeshMinimum,MeshMaximum;}
        [Serializable] private sealed class FramingEvidence
        {
            public Vector3 CameraPosition,CameraForward,MeshMinimum,MeshMaximum,ProjectedMinimum,ProjectedMaximum;
            public Vector2Int MinimumLiquidPixel,MaximumLiquidPixel;public float FieldOfView,Aspect;public int ActualTick,Frame;
        }
        [Serializable] private struct Observation
        {
            public int RequestedTick,ActualTick,Frame,SmallDrops,LiquidPixels;public ulong Quantity,Pending,Collected,Injected,Triangles;
            public double CentroidY;public float RendererAlpha;public bool CpuFallback;
            public Vector2Int MinimumLiquidPixel,MaximumLiquidPixel;
        }
    }
}
#endif
