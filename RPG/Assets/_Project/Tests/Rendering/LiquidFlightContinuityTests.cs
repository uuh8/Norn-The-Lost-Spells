#if UNITY_EDITOR
using System;
using System.Reflection;
using System.Collections;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 从真实Solver/History生成相邻Tick，再读取生产小滴采样与Renderer Capture时钟。
    /// 不复写运动公式，不把60FPS或窗口计数当作位置连续的证明。
    /// </summary>
    public sealed class LiquidFlightContinuityTests
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        private sealed class World:IDisposable
        {
            internal readonly GameObject Host=new GameObject("Actual liquid flight continuity");
            internal readonly LiquidWorldRuntime Liquid;
            internal readonly GridLiquidFieldFrame Frame;
            private readonly LiquidWorldProfile _profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly LiquidBehaviorProfile _water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            private readonly GridLiquidSmallDrop[] _drops=new GridLiquidSmallDrop[64];
            internal World(uint quantity=2,Vector3? origin=null)
            {
                Host.SetActive(false);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":128,\"_resolutionScale\":1,\"_unitVolumeMultiplier\":1,\"_enableAutoStableDetection\":false}",_profile);
                typeof(LiquidWorldProfile).GetField("_materials",Hidden).SetValue(_profile,new[]{_water});
                Liquid=Host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",Hidden).SetValue(Liquid,_profile);
                var baker=Host.AddComponent<ElementFieldSolidBaker>();
                Assert.That(Liquid.TryInitialize(origin??new Vector3(30000,0,30000),.25f,4,baker),Is.True);
                Assert.That(Liquid.TryAddInitial(new Vector3Int(0,20,0),MaterialId.Water,quantity,true),Is.True);
                Frame=new GridLiquidFieldFrame(Liquid,32,128,.8f,1.8f);Frame.ConfigureFallingDrops(64);
            }
            internal void Step(int tick)
            {Liquid.Step(.1f,tick*.1f);Frame.Capture();}
            internal Vector3 Center(float alpha)
            {
                int count=Frame.CopyFallingDrops(alpha,_drops);Assert.That(count,Is.GreaterThan(0));
                double x=0,y=0,z=0,mass=0;
                for(int i=0;i<count;i++)
                {
                    double r=_drops[i].Radius,weight=r*r*r;
                    x+=_drops[i].Position.x*weight;y+=_drops[i].Position.y*weight;z+=_drops[i].Position.z*weight;mass+=weight;
                }
                Assert.That(mass,Is.GreaterThan(0));return new Vector3((float)(x/mass),(float)(y/mass),(float)(z/mass));
            }
            public void Dispose(){Object.DestroyImmediate(Host);Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);}
        }

        [Test]
        public void ActualFirstGravityWindowNeverMovesUpBeforeItStartsFalling()
        {
            using var world=new World();world.Step(1);
            float start=world.Center(0).y,end=world.Center(1).y;
            Assert.That(end,Is.LessThan(start));
            float last=start;
            for(int phase=1;phase<=20;phase++)
            {
                float y=world.Center(phase/20f).y;
                Assert.That(y,Is.LessThanOrEqualTo(last+.00005f),"静止释放后的子帧不能先向上回弹、停半个Tick才落下。");
                last=y;
            }
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(2));
        }

        [Test]
        public void ActualAdjacentGravityWindowsDoNotRestartTheirVelocityAtEveryTick()
        {
            using var world=new World();world.Step(1);world.Step(2);world.Step(3);
            const float epsilon=.02f;
            Vector3 end=world.Center(1),before=world.Center(1-epsilon);
            float endVelocity=(end.y-before.y)/(.1f*epsilon);
            world.Step(4);
            Vector3 start=world.Center(0),after=world.Center(epsilon);
            float startVelocity=(after.y-start.y)/(.1f*epsilon);
            Assert.That(Vector3.Distance(end,start),Is.LessThan(.001f),"History相邻位置端点必须接续。");
            Assert.That(Mathf.Abs(startVelocity-endVelocity),Is.LessThan(.1f),
                "连续重力只在两个2ms区间内逐渐加速，不能每0.1s出现约g*dt的速度断层。");
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(2));
        }

        [Test]
        public void ActualTerminalFlightUsesOneConstantSpeedPathAcrossTheEntireTick()
        {
            using var world=new World();
            for(int tick=1;tick<=16;tick++)world.Step(tick);
            Vector3 a=world.Center(0),b=world.Center(1);
            Assert.That(a.y-b.y,Is.EqualTo(1.2f).Within(.001f),"正式12m/s终速每0.1s应移动1.2m。");
            for(int step=1;step<10;step++)
            {
                float alpha=step*.1f;
                Assert.That(world.Center(alpha).y,Is.EqualTo(Mathf.Lerp(a.y,b.y,alpha)).Within(.001f),
                    "0.25m格下终速单Tick走4.8格，不能被旧3格Warp截断，也不能对恒速额外施加g。");
            }
            Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(2));
        }

        [Test]
        public void LocalRecaptureWithinTheSameTransportEpochPreservesPlaybackOrigin()
        {
            using var world=new World();world.Step(1);
            var profile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_budgetMiB\":32,\"_inputChunks\":16,\"_views\":16,\"_fineViews\":0,\"_cullByCamera\":false}",profile);
                var renderer=world.Host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(world.Liquid,profile);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",Hidden).Invoke(renderer,null);
                Assert.That(renderer.IsReady,Is.True);
                var capture=typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Capture",Hidden);
                var clock=typeof(GridLiquidFieldSurfaceRenderer).GetField("_capturedAt",Hidden);
                capture.Invoke(renderer,null);float playbackOrigin=Time.time-.04f;clock.SetValue(renderer,playbackOrigin);
                uint epoch=world.Liquid.TransportEpoch;
                capture.Invoke(renderer,null);
                Assert.That(world.Liquid.TransportEpoch,Is.EqualTo(epoch));
                Assert.That((float)clock.GetValue(renderer),Is.EqualTo(playbackOrigin).Within(.00001f),
                    "相机/数量页重打包不拥有播放时钟，单机也不能因此反复从Alpha0起播。");
            }
            finally{Object.DestroyImmediate(profile);}
        }

        [TestCase(false)] [TestCase(true)]
        public void NearbyWallOrFloorKeepsTheOriginalNonBallisticSafetyPath(bool ground)
        {
            var obstacle=GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var origin=new Vector3(30000,0,30000);var source=new Vector3Int(0,20,0);
                var solid=source+(ground?Vector3Int.down:Vector3Int.right);
                obstacle.transform.position=origin+((Vector3)solid+Vector3.one*.5f)*.25f;
                obstacle.transform.localScale=Vector3.one*.25f;Physics.SyncTransforms();
                using var world=new World();world.Step(1);
                Assert.That(world.Frame.Terrain(source).y,Is.GreaterThanOrEqualTo(0),
                    "接地或紧邻真实Collider时不得用负gravity启用额外二阶前推。");
                Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(2));
            }
            finally{Object.DestroyImmediate(obstacle);}
        }

        [UnityTest]
        public IEnumerator ActualBallisticGpuFieldMatchesCpuAndStaysInsideFrozenBounds()
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("需要真实Compute与GPU readback。");
            using var world=new World(128,new Vector3(80,0,80));world.Step(1);world.Step(2);world.Step(3);
            var profile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_budgetMiB\":32,\"_inputChunks\":32,\"_views\":32,\"_fineViews\":32,\"_cullByCamera\":false}",profile);
                var renderer=world.Host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(world.Liquid,profile);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",Hidden).Invoke(renderer,null);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Capture",Hidden).Invoke(renderer,null);
                Assert.That(renderer.IsReady,Is.True);Assert.That(renderer.CpuFallback,Is.False);Assert.That(renderer.HybridPages,Is.Zero);
                var gpu=(GridLiquidFieldResources)typeof(GridLiquidFieldSurfaceRenderer).GetField("_gpu",Hidden).GetValue(renderer);
                var keys=(GridLiquidChunkLookup[])typeof(GridLiquidFieldSurfaceRenderer).GetField("_ownerKeys",Hidden).GetValue(renderer);
                var alpha=typeof(GridLiquidFieldSurfaceRenderer).GetField("_alpha",Hidden);
                var dispatch=typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Dispatch",Hidden);
                var sampler=new GridLiquidFieldSampler(renderer.Frame);
                int side=renderer.Frame.ChunkEdge*renderer.SurfaceSubdivision+7,slots=side*side*side;
                int compared=0,positive=0;float maximumDifference=0;
                foreach(float phase in new[]{.25f,.75f})
                {
                    alpha.SetValue(renderer,phase);dispatch.Invoke(renderer,null);
                    bool done=false,error=false;float[] field=null;
                    var readback=AsyncGPUReadback.Request(gpu.Field,r=>{error=r.hasError;if(!error)field=r.GetData<float>().ToArray();done=true;});
                    readback.forcePlayerLoopUpdate=true;GL.Flush();double deadline=EditorApplication.timeSinceStartup+10;
                    while(!done){readback.Update();EditorApplication.QueuePlayerLoopUpdate();Assert.That(EditorApplication.timeSinceStartup,Is.LessThan(deadline));yield return null;}
                    Assert.That(error,Is.False);
                    for(int page=0;page<renderer.SurfacePages;page++)for(int i=0;i<slots;i++)
                    {
                        float actual=field[page*slots+i];Assert.That(float.IsFinite(actual),Is.True);
                        if(actual<=-.6f && i%101!=0)continue;
                        var c=new Vector3(i%side,i/side%side,i/(side*side));
                        var key=keys[page];var cell=new Vector3(key.X,key.Y,key.Z)*renderer.Frame.ChunkEdge+(c-Vector3.one*2.5f)/renderer.SurfaceSubdivision;
                        Vector3 point=renderer.Frame.Origin+cell*renderer.Frame.CellSize;
                        float sampledAlpha=(key.Slot&1073741824)!=0?1:phase;
                        // Field Buffer是Surface Nets三角化前的厚代理场；FinishVertices随后才
                        // 压回真实液厚。这里必须对照同一CPU Proxy，不能拿最终real场比较。
                        float expected=sampler.EvaluateGroundProxy(point,sampledAlpha);
                        if(actual<=-.6f){Assert.That(expected,Is.LessThanOrEqualTo(.001f),"冻结Bounds不能裁掉正场液体。");continue;}
                        maximumDifference=Mathf.Max(maximumDifference,Mathf.Abs(actual-expected));compared++;if(actual>0)positive++;
                        Assert.That(actual,Is.EqualTo(expected).Within(.001f),
                            $"生产GPU与CPU必须使用同一冻结gravity和相位。page={page},key={key.X}/{key.Y}/{key.Z},node={c},cell={cell},alpha={sampledAlpha},reach={renderer.Frame.QueryReach}");
                    }
                }
                Assert.That(compared,Is.GreaterThan(16));Assert.That(positive,Is.GreaterThan(0));
                Assert.That(world.Liquid.TotalQuantity,Is.EqualTo(128));
            }
            finally{Object.DestroyImmediate(profile);}
        }
    }
}
#endif
