using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Game.Rendering.Tests
{
    // 直接驱动实际 Renderer 的生命周期，验证显示预算不再阻塞已写入的近处液体。
    public sealed class GridLiquidVisibilityTests
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        [TestCase(false,false)]
        [TestCase(true,false)]
        [TestCase(true,true)]
        public void AccumulatedPagesDoNotBlockNewWaterNearCamera(bool allVisible,bool fartherImpact)
        {
            if(!SystemInfo.supportsComputeShaders)Assert.Ignore("This test needs the real Compute path.");
            var host=new GameObject("Visibility capacity regression");host.SetActive(false);
            var cameraHost=new GameObject("Visibility regression camera");cameraHost.tag="MainCamera";
            var camera=cameraHost.AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=allVisible?90:12;camera.aspect=1;
            camera.transform.SetPositionAndRotation(new Vector3(0,40,0),Quaternion.Euler(90,0,0));camera.farClipPlane=400;
            var profile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            var rendering=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":256,\"_maximumActiveCells\":1024,\"_enableAutoStableDetection\":false}",profile);
                // Dense case deliberately exceeds the r2 page budget in one view; distant case is behind/outside it.
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",Hidden).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,8,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                for(int i=0;i<120;i++)
                {
                    int x=allVisible?((i%12)-6)*16+4:800+i*16;
                    int z=allVisible?((i/12)-5)*16+4:800;
                    Assert.That(source.TryAddInitial(new Vector3Int(x,0,z),MaterialId.Water,128,false),Is.True);
                }
                var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,rendering);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",Hidden).Invoke(renderer,null);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",Hidden).Invoke(renderer,null);
                var impact=fartherImpact?new Vector3Int(70,0,0):Vector3Int.zero;
                Assert.That(source.TryAddInitial(impact,MaterialId.Water,256,false),Is.True);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",Hidden).Invoke(renderer,null);
                Assert.That(source.TryGetQuantity(impact,MaterialId.Water,out uint q),Is.True);Assert.That(q,Is.EqualTo(256));
                Assert.That(renderer.CpuFallback,Is.False,"不可见旧页或可见超预算页不能让新水整体等待CPU重建。");
                Assert.That(renderer.Frame.Read(impact,0,false).Quantity,Is.EqualTo(256),"当前GPU快照必须包含新命中点，不能继续画旧世界。");
                Assert.That(renderer.SurfacePages,Is.LessThanOrEqualTo(96));
                if(allVisible)Assert.That(renderer.HybridPages,Is.GreaterThan(0),"This case must exercise a real visible GPU overflow, not just distance culling.");
                if(!allVisible)Assert.That(renderer.Frame.Count,Is.LessThan(12),"200m外残留不应占用当前GPU输入页。");
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(cameraHost);UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(rendering);}
        }
        [Test]
        public void CpuSnapshotRetainsOwnersThatMayLeaveGpuDomain()
        {
            using(var setup=new Setup(false))
            {
                var near=Vector3Int.zero;var other=new Vector3Int(40,0,0);
                setup.Source.TryAddInitial(near,MaterialId.Water,256,false);
                setup.Source.TryAddInitial(other,MaterialId.Water,128,false);
                var frame=new GridLiquidFieldFrame(setup.Source,32,64,.8f);
                frame.Capture();
                var snapshot=new GridLiquidFieldFrame(setup.Source,32,64,.8f,1.8f,true);
                snapshot.CopyFrom(frame);int completeOwners=frame.OwnerCount;
                frame.SelectGpuDomain(8,1);
                Assert.That(frame.OwnerCount,Is.EqualTo(1));
                Assert.That(snapshot.OwnerCount,Is.EqualTo(completeOwners));
                Assert.That(snapshot.Read(near,0,false).Quantity,Is.EqualTo(256));
                Assert.That(snapshot.Read(other,0,false).Quantity,Is.EqualTo(128));
                setup.Source.TryAddInitial(near,MaterialId.Water,64,false);
                Assert.That(snapshot.Read(near,0,false).Quantity,Is.EqualTo(256),"CPU分批构建必须读取不可变快照。");
            }
        }
        [Test]
        public void RotatingCameraRevealsSleepingWaterWithoutAnotherWorldWrite()
        {
            using(var setup=new Setup(false))
            {
                setup.Camera.orthographic=false;setup.Camera.fieldOfView=45;
                setup.Camera.transform.SetPositionAndRotation(new Vector3(0,4,0),Quaternion.Euler(10,0,0));
                var front=new Vector3Int(0,0,50);var back=new Vector3Int(0,0,-50);
                Assert.That(setup.Source.TryAddInitial(front,MaterialId.Water,200,false),Is.True);
                Assert.That(setup.Source.TryAddInitial(back,MaterialId.Water,200,false),Is.True);
                setup.Update();Assert.That(setup.Renderer.Frame.Read(front,0,false).Quantity,Is.EqualTo(200));
                Assert.That(setup.Renderer.Frame.Read(back,0,false).Quantity,Is.Zero);
                uint version=setup.Source.SurfaceVersion;
                setup.Camera.transform.rotation=Quaternion.Euler(10,180,0);setup.Update();
                Assert.That(setup.Source.SurfaceVersion,Is.EqualTo(version));
                Assert.That(setup.Renderer.Frame.Read(back,0,false).Quantity,Is.EqualTo(200));
                Assert.That(setup.Renderer.Frame.Read(front,0,false).Quantity,Is.Zero);
                Assert.That(setup.Source.TotalQuantity,Is.EqualTo(400UL));
            }
        }
        [UnityTest]
        public IEnumerator VisibleOverflowProducesGpuGeometryAtNewImpact()
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("Real GPU readback required.");
            using(var setup=new Setup(true))
            {
                for(int i=0;i<120;i++)Assert.That(setup.Source.TryAddInitial(new Vector3Int((i%12-6)*16+4,0,(i/12-5)*16+4),MaterialId.Water,128,false),Is.True);
                setup.Update();setup.Source.TryAddInitial(Vector3Int.zero,MaterialId.Water,256,false);setup.Update();
                Assert.That(setup.Renderer.HybridPages,Is.GreaterThan(0));Assert.That(setup.Renderer.CpuFallback,Is.False);
                // 当前插值本来落后一个Tick；受控地检查alpha=1的真正GPU输出，避免只检查CPU打包量。
                typeof(GridLiquidFieldSurfaceRenderer).GetField("_capturedAt",Hidden).SetValue(setup.Renderer,Time.time-.2f);setup.Update();
                int owner=-1;for(int i=0;i<setup.Renderer.Frame.OwnerCount;i++)if(setup.Renderer.Frame.Owners[i].Equals(new ElementChunkKey(0,0,0))){owner=i;break;}
                Assert.That(owner,Is.GreaterThanOrEqualTo(0));
                int subdivision=(int)typeof(GridLiquidFieldSurfaceRenderer).GetField("_subdivision",Hidden).GetValue(setup.Renderer);
                int cube=setup.Source.ChunkSize*subdivision+6,bytes=cube*cube*cube*GridLiquidFieldGpuLayout.VertexStride;
                var vertices=AsyncGPUReadback.Request(setup.Renderer.DebugVertices,bytes,owner*bytes);var arguments=AsyncGPUReadback.Request(setup.Renderer.DebugArguments);
                float timeout=Time.realtimeSinceStartup+10;
                while(!vertices.done || !arguments.done){Assert.That(Time.realtimeSinceStartup,Is.LessThan(timeout));yield return null;}
                Assert.That(vertices.hasError || arguments.hasError,Is.False);
                var data=vertices.GetData<Vertex>();int impactVertices=0;
                for(int i=0;i<data.Length;i++)if(data[i].Valid>0 && new Vector2(data[i].Position.x,data[i].Position.z).sqrMagnitude<1)impactVertices++;
                uint indices=arguments.GetData<uint>()[owner*5];
                Assert.That(impactVertices,Is.GreaterThan(8));Assert.That(indices,Is.GreaterThan(24));
                File.WriteAllText("Temp/ElementPerformance/liquid-visible-impact-gpu.json",JsonUtility.ToJson(new GeometryReport
                    {Utc=DateTime.UtcNow.ToString("O"),GpuPages=setup.Renderer.SurfacePages,VisiblePages=setup.Renderer.VisibleSurfacePages,HybridPages=setup.Renderer.HybridPages,ImpactVertices=impactVertices,ImpactIndices=indices,Quantity=setup.Source.TotalQuantity},true));
            }
        }
        [Test]
        public void CullingReducesCaptureWorkWithoutChangingCommonFieldOrAllocating()
        {
            using(var setup=new Setup(false))
            {
                for(int i=0;i<120;i++)setup.Source.TryAddInitial(new Vector3Int(800+i*16,0,800),MaterialId.Water,128,false);
                setup.Source.TryAddInitial(Vector3Int.zero,MaterialId.Water,256,false);
                var frame=new GridLiquidFieldFrame(setup.Source,512,1024,.8f);var view=new MaterialPresentationView();
                view.Update(setup.Camera,setup.Camera.transform.position,32,56,8);
                var sampler=new GridLiquidFieldSampler(frame);frame.Capture();int beforePages=frame.Count;float expected=sampler.Evaluate(new Vector3(.25f,.25f,.25f),1);
                for(int i=0;i<3;i++)frame.Capture();long before=System.Diagnostics.Stopwatch.GetTimestamp();
                for(int i=0;i<10;i++)frame.Capture();double full=(System.Diagnostics.Stopwatch.GetTimestamp()-before)*1000.0/System.Diagnostics.Stopwatch.Frequency/10;
                for(int i=0;i<3;i++)frame.Capture(view);long allocated=GC.GetAllocatedBytesForCurrentThread();before=System.Diagnostics.Stopwatch.GetTimestamp();
                for(int i=0;i<10;i++)frame.Capture(view);double visible=(System.Diagnostics.Stopwatch.GetTimestamp()-before)*1000.0/System.Diagnostics.Stopwatch.Frequency/10;
                long gc=GC.GetAllocatedBytesForCurrentThread()-allocated;
                Assert.That(gc,Is.Zero);Assert.That(frame.Count,Is.LessThan(12));Assert.That(sampler.Evaluate(new Vector3(.25f,.25f,.25f),1),Is.EqualTo(expected).Within(1e-6f));
                File.WriteAllText("Temp/ElementPerformance/liquid-visible-capture-ab.json",JsonUtility.ToJson(new CaptureReport
                    {Utc=DateTime.UtcNow.ToString("O"),FullPages=beforePages,VisiblePages=frame.Count,FullMilliseconds=full,VisibleMilliseconds=visible,Gc=gc,Quantity=setup.Source.TotalQuantity},true));
            }
        }
        [StructLayout(LayoutKind.Sequential)]private struct Vertex{public Vector3 Position;public float Valid;public Vector3 Normal;public uint W0,W1,W2,W3,Pad;}
        [Serializable]private sealed class GeometryReport{public string Utc;public int GpuPages,VisiblePages,HybridPages,ImpactVertices;public uint ImpactIndices;public ulong Quantity;}
        [Serializable]private sealed class CaptureReport{public string Utc;public int FullPages,VisiblePages;public double FullMilliseconds,VisibleMilliseconds;public long Gc;public ulong Quantity;}
        private sealed class Setup:IDisposable
        {
            private readonly GameObject _host,_cameraHost;
            private readonly LiquidWorldProfile _profile;
            private readonly GridLiquidFieldRenderProfile _rendering;
            public readonly Camera Camera;
            public readonly LiquidWorldRuntime Source;
            public readonly GridLiquidFieldSurfaceRenderer Renderer;
            public readonly Action Update;
            public Setup(bool wide)
            {
                _host=new GameObject("View regression source");_host.SetActive(false);
                _cameraHost=new GameObject("View regression camera");_cameraHost.tag="MainCamera";Camera=_cameraHost.AddComponent<Camera>();
                Camera.orthographic=true;Camera.orthographicSize=wide?90:12;Camera.aspect=1;Camera.farClipPlane=400;
                Camera.transform.SetPositionAndRotation(new Vector3(0,40,0),Quaternion.Euler(90,0,0));
                _profile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
                _rendering=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":256,\"_maximumActiveCells\":1024,\"_enableAutoStableDetection\":false}",_profile);
                Source=_host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",Hidden).SetValue(Source,_profile);
                Assert.That(Source.TryInitialize(Vector3.zero,.25f,8,_host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                Renderer=_host.AddComponent<GridLiquidFieldSurfaceRenderer>();Renderer.ConfigureForValidation(Source,_rendering);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",Hidden).Invoke(Renderer,null);
                Update=(Action)Delegate.CreateDelegate(typeof(Action),Renderer,typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",Hidden));
            }
            public void Dispose(){UnityEngine.Object.DestroyImmediate(_host);UnityEngine.Object.DestroyImmediate(_cameraHost);UnityEngine.Object.DestroyImmediate(_profile);UnityEngine.Object.DestroyImmediate(_rendering);}
        }
    }
}
