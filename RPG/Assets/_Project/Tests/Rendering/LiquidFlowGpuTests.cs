using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Game.Core;
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
    public sealed class LiquidFlowGpuTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex{public Vector3 Position;public float Valid;public Vector3 Normal;public uint A,B,C,D,Detail;}
        [Serializable]private sealed class Report{public string Utc;public int Vertices,FlowVertices;public Vector3 Minimum,Maximum;public uint Quantity;}

        [UnityTest]
        public IEnumerator ActualGpuSurfaceCarriesTheAuthenticatedVelocityIntoVertexLightingData()
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("需要真实GPU与AsyncGPUReadback。");
            const BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Liquid Flow GPU witness");host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            var render=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
            try
            {
                typeof(LiquidWorldProfile).GetField("_materials",hidden).SetValue(profile,new[]{water});
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4,\"_enableAutoStableDetection\":false}",profile);
                JsonUtility.FromJsonOverwrite("{\"_budgetMiB\":32,\"_inputChunks\":16,\"_views\":32,\"_fineViews\":32,\"_cullByCamera\":false}",render);
                var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,new Vector3(80,20,80),.5f,8);
                var cell=new Vector3Int(2,4,2);var key=new WorldChunkKey(WorldMaterialDomain.Liquid,0,0,0);
                var store=new WorldReplicaStore(7,8,256,0,256);Assert.That(store.ConfigureGeometry(geometry),Is.True);
                Assert.That(store.ConfigureMaterials(WorldMaterialDomain.Liquid,510,new[]{(byte)MaterialId.Water}),Is.True);
                var words=new uint[18];words[0]=WorldChunkPayloadCodec.Magic;words[1]=1;words[3]=1;
                WorldChunkPayloadCodec.WriteGeometry(words,4,geometry);
                WorldChunkPayloadCodec.WriteCell(words,11,new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Resident,cell,(byte)MaterialId.Water,510));
                Assert.That(store.TryInstall(new WorldChunkPayloadHeader(7,1,key,1,0,1,100,18,WorldChunkPayloadCodec.ComputeChecksum(words,18),true),words,18),Is.True);
                var source=new WorldReplicaLiquidReadModel(store,geometry,profile,.25f,null);source.ResetConnection(3);
                for(ulong sequence=1;sequence<=2;sequence++)
                {
                    var frame=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=sequence,MaterialTick=(uint)(100+sequence),SampleTime=10+(sequence-1)*.1,Interval=.1f,ChunkSize=8,AnchorCount=1,RowCount=2};
                    frame.Anchors[0]=new WorldLiquidMotionAnchor(key,1,1,1,true);
                    var offset=new Vector3(.49f,0,0);var velocity=new Vector3(10,0,0);
                    frame.Rows[0]=new WorldLiquidMotionSample(new Vector3Int(0,4,2),(byte)MaterialId.Water,510,0,offset,velocity,Vector3.zero,Vector3.zero,0);
                    frame.Rows[1]=new WorldLiquidMotionSample(cell,(byte)MaterialId.Water,0,510,Vector3.zero,Vector3.zero,offset,velocity,0);
                    Assert.That(source.TryPublish(frame,frame.SampleTime+.1),Is.True);
                }
                source.AdvancePresentation(10.15,false);
                var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,render);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",hidden).Invoke(renderer,null);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",hidden).Invoke(renderer,null);
                Assert.That(renderer.IsReady,Is.True);Assert.That(renderer.CpuFallback,Is.False);
                Assert.That(renderer.Frame.QueryReach.x,Is.GreaterThanOrEqualTo(4),"已捕获的高速横向搬运不能继续使用XZ±2的旧175格查询。");
                var shoulder=geometry.Origin+new Vector3(3.05f,4.5f,2.5f)*geometry.CellSize;
                renderer.Frame.PrepareQuery(shoulder);bool foundPrevious=false;
                for(int i=0;i<renderer.Frame.QueryCellCount;i++)foundPrevious|=renderer.Frame.QueryCell(i)==new Vector3Int(0,4,2);
                Assert.That(foundPrevious,Is.True,"外肩查询必须包含跨两格搬运的Previous端，不能被旧邻域裁掉。");
                Assert.That(new GridLiquidFieldSampler(renderer.Frame).Evaluate(shoulder,.5f),Is.GreaterThan(0));
                Vertex[] vertices=null;bool done=false,error=false;
                var request=AsyncGPUReadback.Request(renderer.DebugVertices,r=>{error=r.hasError;if(!error)vertices=r.GetData<Vertex>().ToArray();done=true;});request.forcePlayerLoopUpdate=true;
                GL.Flush();double deadline=EditorApplication.timeSinceStartup+10;
                while(!done){request.Update();EditorApplication.QueuePlayerLoopUpdate();Assert.That(EditorApplication.timeSinceStartup,Is.LessThan(deadline));yield return null;}
                Assert.That(error,Is.False);int valid=0,flowing=0;Vector3 min=Vector3.one*float.PositiveInfinity,max=Vector3.one*float.NegativeInfinity;
                int subdivision=(int)typeof(GridLiquidFieldSurfaceRenderer).GetField("_subdivision",hidden).GetValue(renderer);
                int side=geometry.ChunkSize*subdivision+6,active=Mathf.Min(vertices.Length,side*side*side*renderer.SurfacePages);
                for(int i=0;i<active;i++)
                {
                    var v=vertices[i];
                    if(v.Valid<=0)continue;valid++;min=Vector3.Min(min,v.Position);max=Vector3.Max(max,v.Position);
                    Vector4 detail=GridLiquidFlowPresentation.Unpack(v.Detail);
                    Assert.That(float.IsNaN(v.Position.x)||float.IsNaN(v.Normal.y),Is.False);
                    if(detail.y>7.8f && Mathf.Abs(detail.z)<.01f)flowing++;
                }
                Assert.That(valid,Is.GreaterThan(8));Assert.That(flowing,Is.GreaterThan(valid*.75f),"实际GPU顶点必须携带已验证运动窗口的流向，不能只有CPU属性或Shader字符串。");
                Assert.That(store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(510UL));
                Directory.CreateDirectory("Temp/ElementPerformance");File.WriteAllText("Temp/ElementPerformance/liquid-flow-gpu.json",JsonUtility.ToJson(new Report{Utc=DateTime.UtcNow.ToString("O"),Vertices=valid,FlowVertices=flowing,Minimum=min,Maximum=max,Quantity=510},true));
            }
            finally{Object.DestroyImmediate(host);Object.DestroyImmediate(profile);Object.DestroyImmediate(water);Object.DestroyImmediate(render);}
        }
    }
}
