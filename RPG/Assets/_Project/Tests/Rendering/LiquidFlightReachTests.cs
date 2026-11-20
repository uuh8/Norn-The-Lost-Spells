#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
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
    /// <summary>
    /// 让Codec合法的最大Offset与高速Warp实际走过Replica→Frame→CPU/GPU。
    /// 较宽参考只放宽测试查询域，仍调用生产Sampler，不另抄一套液体场公式。
    /// </summary>
    public sealed class LiquidFlightReachTests
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        private sealed class Flight:IDisposable
        {
            internal readonly GameObject Host=new GameObject("Legal bounded liquid flight reach");
            internal readonly WorldReplicaStore Store;
            internal readonly WorldReplicaLiquidReadModel Source;
            internal readonly GridLiquidFieldRenderProfile Render;
            internal readonly WorldGridGeometry Geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,new Vector3(80,20,80),.5f,8);
            private readonly LiquidWorldProfile _profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly LiquidBehaviorProfile _water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            internal Flight()
            {
                Host.SetActive(false);
                typeof(LiquidWorldProfile).GetField("_materials",Hidden).SetValue(_profile,new[]{_water});
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",_profile);
                Render=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
                JsonUtility.FromJsonOverwrite("{\"_budgetMiB\":32,\"_inputChunks\":16,\"_views\":32,\"_fineViews\":32,\"_cullByCamera\":false}",Render);
                Store=new WorldReplicaStore(7,8,256,0,256);
                Assert.That(Store.ConfigureGeometry(Geometry),Is.True);
                Assert.That(Store.ConfigureMaterials(WorldMaterialDomain.Liquid,510,new[]{(byte)MaterialId.Water}),Is.True);
                var sourceKey=new WorldChunkKey(WorldMaterialDomain.Liquid,0,0,0);
                var targetKey=new WorldChunkKey(WorldMaterialDomain.Liquid,1,0,0);
                Install(sourceKey);Install(targetKey,new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Resident,new Vector3Int(12,4,2),(byte)MaterialId.Water,510));
                Source=new WorldReplicaLiquidReadModel(Store,Geometry,_profile,.25f,null);Source.ResetConnection(3);
                for(ulong sequence=1;sequence<=2;sequence++)
                {
                    var motion=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=sequence,MaterialTick=(uint)(100+sequence),
                        SampleTime=10+(sequence-1)*.1,Interval=.1f,ChunkSize=8,AnchorCount=2,RowCount=2};
                    motion.Anchors[0]=new WorldLiquidMotionAnchor(sourceKey,1,1,1,true);
                    motion.Anchors[1]=new WorldLiquidMotionAnchor(targetKey,1,1,1,true);
                    var offset=Vector3.right*WorldLiquidMotionCodec.MaxOffsetCells;var velocity=Vector3.right*WorldLiquidMotionCodec.MaxVelocityWorld;
                    motion.Rows[0]=new WorldLiquidMotionSample(new Vector3Int(0,4,2),(byte)MaterialId.Water,510,0,offset,velocity,Vector3.zero,Vector3.zero,0);
                    motion.Rows[1]=new WorldLiquidMotionSample(new Vector3Int(12,4,2),(byte)MaterialId.Water,0,510,Vector3.zero,Vector3.zero,offset,velocity,1);
                    Assert.That(WorldLiquidMotionCodec.Validate(motion),Is.True,"本回归必须属于既有合法sidecar合同。");
                    Assert.That(Source.TryPublish(motion,motion.SampleTime+.05),Is.True);
                }
            }
            private void Install(WorldChunkKey key,params WorldMaterialCellSample[] cells)
            {
                var words=new uint[11+7*cells.Length];words[0]=WorldChunkPayloadCodec.Magic;words[1]=(uint)cells.Length;words[3]=1;
                WorldChunkPayloadCodec.WriteGeometry(words,4,Geometry);
                for(int i=0;i<cells.Length;i++)WorldChunkPayloadCodec.WriteCell(words,11+7*i,cells[i]);
                Assert.That(Store.TryInstall(new WorldChunkPayloadHeader(7,1,key,1,0,1,100,words.Length,
                    WorldChunkPayloadCodec.ComputeChecksum(words,words.Length),true),words,words.Length),Is.True);
            }
            internal GridLiquidFieldFrame Frame()
            {var result=new GridLiquidFieldFrame(Source,16,64,.8f,1);result.Capture();return result;}
            public void Dispose(){Object.DestroyImmediate(Host);Object.DestroyImmediate(Render);Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);}
        }

        private static float Reference(GridLiquidFieldFrame frame,Vector3 point,float alpha)
        {
            var reach=typeof(GridLiquidFieldFrame).GetProperty("QueryReach",Hidden);var old=frame.QueryReach;
            try
            {
                reach.SetValue(frame,new Vector3Int(14,14,14));
                typeof(GridLiquidFieldFrame).GetField("_queryCellsReady",Hidden).SetValue(frame,false);
                var sampler=new GridLiquidFieldSampler(frame);sampler.SetValidationFullScan(true);
                return sampler.EvaluateGroundProxy(point,alpha);
            }
            finally
            {
                reach.SetValue(frame,old);
                typeof(GridLiquidFieldFrame).GetField("_queryCellsReady",Hidden).SetValue(frame,false);
            }
        }

        [Test]
        public void LegalMaximumOffsetAndBoundedWarpKeepThePreviousOuterShoulderInCpuField()
        {
            using var flight=new Flight();var frame=flight.Frame();
            Vector3 point=flight.Geometry.Origin+new Vector3(11.75f,4.75f,2.75f)*flight.Geometry.CellSize;
            float reference=Reference(frame,point,.5f);Assert.That(reference,Is.GreaterThan(.01f),"宽域参考必须确实位于两端重合的液体正场中。");
            float actual=new GridLiquidFieldSampler(frame).EvaluateGroundProxy(point,.5f);
            Assert.That(actual,Is.GreaterThan(0),$"旧reach={frame.QueryReach}把cell0的Previous肩部裁掉，不能靠Current半权重维持等值面。");
            Assert.That(actual,Is.EqualTo(reference).Within(.001f));
            Assert.That(frame.QueryReach.x,Is.GreaterThanOrEqualTo(13));
            Assert.That(flight.Store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(510));
        }

        [UnityTest]
        public IEnumerator LegalMaximumOffsetAndBoundedWarpKeepTheGpuPositiveFieldAndCpuAgreement()
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("需要真实Compute与GPU readback。");
            using var flight=new Flight();
            var renderer=flight.Host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(flight.Source,flight.Render);
            typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",Hidden).Invoke(renderer,null);
            typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Capture",Hidden).Invoke(renderer,null);
            Assert.That(renderer.IsReady,Is.True);Assert.That(renderer.CpuFallback,Is.False);Assert.That(renderer.HybridPages,Is.Zero);
            typeof(GridLiquidFieldSurfaceRenderer).GetField("_alpha",Hidden).SetValue(renderer,.5f);
            typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Dispatch",Hidden).Invoke(renderer,null);
            var gpu=(GridLiquidFieldResources)typeof(GridLiquidFieldSurfaceRenderer).GetField("_gpu",Hidden).GetValue(renderer);
            var keys=(GridLiquidChunkLookup[])typeof(GridLiquidFieldSurfaceRenderer).GetField("_ownerKeys",Hidden).GetValue(renderer);
            bool done=false,error=false;float[] values=null;
            var readback=AsyncGPUReadback.Request(gpu.Field,r=>{error=r.hasError;if(!error)values=r.GetData<float>().ToArray();done=true;});
            readback.forcePlayerLoopUpdate=true;GL.Flush();double deadline=EditorApplication.timeSinceStartup+10;
            while(!done){readback.Update();EditorApplication.QueuePlayerLoopUpdate();Assert.That(EditorApplication.timeSinceStartup,Is.LessThan(deadline));yield return null;}
            Assert.That(error,Is.False);int side=renderer.Frame.ChunkEdge*renderer.SurfaceSubdivision+7,slots=side*side*side;bool found=false;
            // r3在半格中心采样，r2最近节点偏移0.25格；两者均留在同一正场肩部。
            var target=new Vector3(11.5f,4.5f,2.5f);
            for(int page=0;page<renderer.SurfacePages;page++)
            {
                var key=keys[page];var baseCell=new Vector3(key.X,key.Y,key.Z)*renderer.Frame.ChunkEdge;
                var node=Vector3Int.RoundToInt((target-baseCell)*renderer.SurfaceSubdivision+Vector3.one*2.5f);
                if(node.x<0 || node.y<0 || node.z<0 || node.x>=side || node.y>=side || node.z>=side)continue;
                var cell=baseCell+((Vector3)node-Vector3.one*2.5f)/renderer.SurfaceSubdivision;
                found=true;Vector3 point=flight.Geometry.Origin+cell*flight.Geometry.CellSize;
                float expected=Reference(renderer.Frame,point,.5f),actual=values[page*slots+node.x+side*(node.y+side*node.z)];
                Assert.That(expected,Is.GreaterThan(.01f));Assert.That(actual,Is.GreaterThan(0),"合法Offset4+Warp6的正场不能在生产GPU邻域被裁掉。");
                Assert.That(actual,Is.EqualTo(expected).Within(.001f));
            }
            Assert.That(found,Is.True,"输出Owner/Bounds必须覆盖这处外肩节点。");
            Assert.That(flight.Store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(510));
        }
    }
}
#endif
