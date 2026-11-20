using System;
using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// ReadModel 的公开行为合同：某格上一条已验证 Current pose 不能仅因一个 motion 子集缺行就失忆。
    /// 真实 Solver→Source 的物理成因另由 NativeStaticBridge 夹具覆盖；本夹具专门隔离 Reader 生命周期，
    /// 所有数量先经 CommitOwner/Codec/ReplicaStore，运动也经过真实 wire codec，绝不反射读写 cache。
    /// 非零速度取自 Native 首断点的已解码值，能区分“真实独立 endpoint”与凭数量猜出的零速度静水。
    /// </summary>
    public sealed class WorldLiquidDormantEndpointCacheTests
    {
        private static readonly Vector3Int A=new Vector3Int(0,2,0),OtherA=new Vector3Int(1,2,0),
            Dormant=new Vector3Int(8,0,0),Target=new Vector3Int(9,0,0),Marker=new Vector3Int(16,2,0);
        private static readonly Vector3 EndpointVelocity=new Vector3(.02642822265625f,0,.04541015625f);
        private static readonly Vector3 OlderVelocity=new Vector3(.0625f,0,0);
        private static WorldChunkKey Key(Vector3Int cell)=>WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid,cell,4);
        private static double Sample(int tick)=>10+tick*.1;

        private sealed class Fixture:IDisposable
        {
            private readonly LiquidWorldProfile _profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly LiquidBehaviorProfile _water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            private readonly WorldReplicationCommitOwner _owner=new WorldReplicationCommitOwner(7,16,128,0,32,2048,100000);
            private readonly WorldReplicaBuffer _quantities=new WorldReplicaBuffer(128,0,100000);
            private readonly WorldGridGeometry _geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.25f,4);
            private readonly uint[] _words=new uint[2048],_motionWords=new uint[WorldLiquidMotionCodec.MaxWords];
            private readonly bool _includeMarker;
            private int _quantityTick;
            internal readonly WorldReplicaStore Store=new WorldReplicaStore(7,16,128,0,2048);
            internal readonly WorldReplicaLiquidReadModel Source;

            internal Fixture(bool includeMarker=false)
            {
                _includeMarker=includeMarker;
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":128,\"_resolutionScale\":1,\"_unitVolumeMultiplier\":1}",_profile);
                typeof(LiquidWorldProfile).GetField("_materials",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_profile,new[]{_water});
                Assert.That(Store.ConfigureGeometry(_geometry),Is.True);
                Assert.That(Store.ConfigureMaterials(WorldMaterialDomain.Liquid,(uint)_profile.CreateVolumeSettings(.25f).CapacityGmu,new[]{(byte)MaterialId.Water}),Is.True);
                Source=new WorldReplicaLiquidReadModel(Store,_geometry,_profile,.25f,null);Source.ResetConnection(3);
            }

            private void CaptureQuantities(int tick,uint dormant,uint target=0)
            {
                _quantityTick=Math.Max(_quantityTick+1,tick);int count=0;
                _quantities.Geometries[0]=_geometry;
                Vector3Int active=(tick&1)==1?OtherA:A;
                _quantities.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,active,(byte)MaterialId.Water,20);
                if(dormant>0)_quantities.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Dormant,(byte)MaterialId.Water,dormant);
                if(target>0)_quantities.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Target,(byte)MaterialId.Water,target);
                if(_includeMarker)_quantities.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Marker,(byte)MaterialId.Water,5);
                _quantities.Header=new WorldReplicaHeader(_quantityTick,count,0,1);
                Assert.That(_owner.TryCaptureCommit(_quantities),Is.True);
            }
            internal WorldLiquidMotionFrame Frame(int tick,bool includeDormant=false,bool join=false,
                Vector3? previousVelocity=null,Vector3? currentVelocity=null,uint quantity=18)
            {
                uint part=join?quantity/2:0;CaptureQuantities(tick,quantity-part,part);
                var frame=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=(ulong)tick,
                    MaterialTick=(uint)tick,SampleTime=Sample(tick),Interval=.1f,ChunkSize=4};
                Vector3Int from=(tick&1)==1?A:OtherA,to=(tick&1)==1?OtherA:A;
                Add(frame,from,20,0,Vector3.zero,Vector3.zero);
                Add(frame,to,0,20,Vector3.zero,Vector3.zero);
                if(includeDormant)
                {
                    Add(frame,Dormant,quantity,quantity-part,previousVelocity??EndpointVelocity,currentVelocity??EndpointVelocity);
                    if(part>0)Add(frame,Target,0,part,Vector3.zero,EndpointVelocity);
                }
                Assert.That(WorldLiquidMotionCodec.TryEncode(frame,_motionWords,out int words),Is.True);
                var decoded=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=frame.Sequence,MaterialTick=frame.MaterialTick};
                Assert.That(WorldLiquidMotionCodec.TryDecode(_motionWords,words,decoded),Is.True);return decoded;
            }
            private void Add(WorldLiquidMotionFrame frame,Vector3Int cell,uint before,uint after,Vector3 previous,Vector3 current)
            {
                var key=Key(cell);int anchor=-1;
                for(int i=0;i<frame.AnchorCount;i++)if(frame.Anchors[i].Key==key){anchor=i;break;}
                if(anchor<0)
                {
                    Assert.That(_owner.TryCopyChunk(key,_words,out var h),Is.True);anchor=frame.AnchorCount++;
                    frame.Anchors[anchor]=new WorldLiquidMotionAnchor(key,h.Generation,h.Revision,1,h.IsResident);
                }
                frame.Rows[frame.RowCount++]=new WorldLiquidMotionSample(cell,(byte)MaterialId.Water,before,after,
                    Vector3.zero,previous,Vector3.zero,current,anchor);
            }
            internal void Install(ulong dormantGeneration=1)
            {
                for(int i=0;i<_owner.ChunkCount;i++)
                {
                    var key=_owner.GetChunkHeader(i).Key;
                    Assert.That(_owner.TryCopyChunk(key,_words,out var h),Is.True);
                    if(key==Key(Dormant) && dormantGeneration!=1)
                        h=new WorldChunkPayloadHeader(h.WorldEpoch,h.SnapshotId+100,key,dormantGeneration,0,1,h.ServerTick,h.WordCount,h.Checksum,h.IsResident);
                    Assert.That(Store.TryInstall(h,_words,h.WordCount),Is.True);
                }
            }
            internal void PrepareGap(int missingWindows=1)
            {
                var first=Frame(1,includeDormant:true);Install();Assert.That(Source.TryPublish(first,10.11),Is.True);
                for(int tick=2;tick<=missingWindows+1;tick++)
                {
                    var gap=Frame(tick);Assert.That(Find(gap,Dormant),Is.LessThan(0));
                    Source.StagePresentationCandidate(gap,Sample(tick)+.04);Install();
                    Assert.That(Source.TryPublish(gap,Sample(tick)+.04),Is.True);
                    Assert.That(Source.PresentationSnapped,Is.False,"前置缺行窗的 A 仍是正常连续窗口。");
                }
                Assert.That(Source.TryGetQuantity(Dormant,MaterialId.Water,out uint actual),Is.True);Assert.That(actual,Is.EqualTo(18));
            }
            internal void PrepareAfterUnrelatedWindowInvalidation()
            {
                Assert.That(_includeMarker,Is.True);
                var first=Frame(1,includeDormant:true);Install();Assert.That(Source.TryPublish(first,10.11),Is.True);
                var recovered=Frame(2);
                // motion子集可以只含独立守恒的Marker组而漏掉整组A；这不是伪造半个搬运组。
                // Q2里的A确实从OtherA搬至A，旧显示A缺少续接证据时必须撤销，B则一直18且未变。
                var partial=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=2,MaterialTick=2,
                    SampleTime=10.2,Interval=.1f,ChunkSize=4};
                Add(partial,Marker,5,5,Vector3.zero,Vector3.zero);
                Assert.That(WorldLiquidMotionCodec.TryEncode(partial,_motionWords,out int words),Is.True);
                var decoded=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=2,MaterialTick=2};
                Assert.That(WorldLiquidMotionCodec.TryDecode(_motionWords,words,decoded),Is.True);
                Source.StagePresentationCandidate(decoded,10.19);Install();
                Assert.That(Source.PresentationWindowId,Is.Zero,"A的真实改量缺少continuation，旧曲线仍必须立即失效。");
                Assert.That(Source.LastMotionHoldDiagnostic.Reason,Is.EqualTo((byte)WorldLiquidMotionHoldFailure.MissingContinuationRow));
                // 拒绝续播之后完整Current仍可建立新锚，恢复显示允许snap；本测试不要求保留非法A曲线。
                Source.StagePresentationCandidate(recovered,10.24);Assert.That(Source.TryPublish(recovered,10.24),Is.True);
                Assert.That(Source.PresentationWindowId,Is.EqualTo(2UL));Assert.That(Source.PresentationSnapped,Is.True);
                Assert.That(Find(recovered,Dormant),Is.LessThan(0),"恢复的A窗口仍不含B；不能靠active数组偶然保留B。");
                Assert.That(Source.TryGetQuantity(Dormant,MaterialId.Water,out uint b),Is.True);Assert.That(b,Is.EqualTo(18));
            }
            internal void Collect(uint remaining)
            {
                // 独立数量事务模拟真实 F 结果，只安装该格所在 Chunk；A 的合法运动不应受假数据干扰。
                CaptureQuantities(2,remaining);
                Assert.That(_owner.TryCopyChunk(Key(Dormant),_words,out var h),Is.True);
                Assert.That(Store.TryInstall(h,_words,h.WordCount),Is.True);
                bool found=Source.TryGetPresentationSample(Dormant,MaterialId.Water,false,out var sample);
                Assert.That(found,Is.EqualTo(remaining>0));if(found)Assert.That(sample.Quantity,Is.EqualTo(remaining));
            }
            internal void AssertJoinSnaps(WorldLiquidMotionFrame join,double time)
            {
                Source.StagePresentationCandidate(join,time);Install();Assert.That(Source.TryPublish(join,time),Is.True);
                Assert.That(Source.PresentationSnapped,Is.True,"Current 可以由真实数量发布，但未知/失效的旧 pose 只能安全端点显示。");
            }
            internal static int Find(WorldLiquidMotionFrame frame,Vector3Int cell)
            {for(int i=0;i<frame.RowCount;i++)if(frame.Rows[i].Cell==cell)return i;return -1;}
            public void Dispose(){Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);}
        }

        [Test]
        public void PreviouslyWitnessedNonzeroEndpointSurvivesOneMissingMotionWindow()
        {
            using var f=new Fixture();f.PrepareGap();var next=f.Frame(3,includeDormant:true,join:true);
            int b=Fixture.Find(next,Dormant);Assert.That(next.Rows[b].PreviousVelocity,Is.EqualTo(EndpointVelocity));
            f.Source.StagePresentationCandidate(next,10.25);f.Install();
            Assert.That(f.Source.PresentationWindowId,Is.EqualTo(2UL),"T0 已完整 witness 的旧速度不能因 T1 缺行被误当未知；Q 到来不得撤掉整条旧曲线。");
            Assert.That(f.Source.TryPublish(next,10.25),Is.True);Assert.That(f.Source.PresentationSnapped,Is.False);
            f.Source.AdvancePresentation(10.325,false);Assert.That(f.Source.PresentationWindowId,Is.EqualTo(3UL));
            Assert.That(f.Source.TryGetPresentationSample(Dormant,MaterialId.Water,true,out var old),Is.True);
            Assert.That(old.Quantity,Is.EqualTo(18));Assert.That(old.VelocityWorld,Is.EqualTo(EndpointVelocity));
        }

        [Test]
        public void FullyWitnessedFutureEndpointIsRetainedAfterItLeavesActiveAndFutureFrames()
        {
            using var f=new Fixture();var first=f.Frame(1,includeDormant:true,currentVelocity:OlderVelocity);f.Install();
            Assert.That(f.Source.TryPublish(first,10.11),Is.True);
            var future=f.Frame(2,includeDormant:true,previousVelocity:OlderVelocity);
            f.Source.StagePresentationCandidate(future,10.19);f.Install();Assert.That(f.Source.TryPublish(future,10.19),Is.True);
            Assert.That(f.Source.PresentationWindowId,Is.EqualTo(1UL));Assert.That(f.Source.BufferedPresentationWindows,Is.EqualTo(1));
            // Assembler 的借用帧随后复用；独立缓存只能保存值，不能在稍后读取这份被清空的数组。
            future.Rows[Fixture.Find(future,Dormant)]=default;
            f.Source.AdvancePresentation(10.225,false);Assert.That(f.Source.PresentationWindowId,Is.EqualTo(2UL));
            var missing=f.Frame(3);f.Source.StagePresentationCandidate(missing,10.34);f.Install();
            Assert.That(f.Source.TryPublish(missing,10.34),Is.True);Assert.That(f.Source.PresentationWindowId,Is.EqualTo(3UL));
            var next=f.Frame(4,includeDormant:true,join:true);f.Source.StagePresentationCandidate(next,10.35);f.Install();
            Assert.That(f.Source.PresentationWindowId,Is.EqualTo(3UL),"完整验证过的 future 端点在短暂缺行后仍应可独立取证。");
            Assert.That(f.Source.TryPublish(next,10.35),Is.True);Assert.That(f.Source.PresentationSnapped,Is.False);
        }

        [Test]
        public void NonzeroEndpointCannotAuthorizePreviousAfterMoreThanTwoMaterialTicks()
        {using var f=new Fixture();f.PrepareGap(2);f.AssertJoinSnaps(f.Frame(4,includeDormant:true,join:true),10.35);}

        [TestCase(5u)][TestCase(0u)]
        public void CollectionChangesOrClearsTheIndependentQuantityBeforeTheCandidateArrives(uint remaining)
        {
            using var f=new Fixture();f.PrepareGap();f.Collect(remaining);
            f.AssertJoinSnaps(f.Frame(3,includeDormant:true,join:true),10.25);
        }

        [TestCase("generation")][TestCase("subscription")][TestCase("interest")][TestCase("world")][TestCase("connection")]
        public void DormantEndpointDoesNotCrossItsOriginalIdentityOrInterest(string change)
        {
            using var f=new Fixture();f.PrepareGap();var next=f.Frame(3,includeDormant:true,join:true);
            int index=next.Rows[Fixture.Find(next,Dormant)].AnchorIndex;var old=next.Anchors[index];
            if(change=="world")next.WorldEpoch=8;
            else if(change=="connection")next.ConnectionEpoch=4;
            else if(change=="generation")next.Anchors[index]=new WorldLiquidMotionAnchor(old.Key,2,1,old.SubscriptionEpoch,old.IsResident);
            else if(change=="subscription")next.Anchors[index]=new WorldLiquidMotionAnchor(old.Key,old.Generation,old.Revision,2,old.IsResident);
            else Assert.That(f.Store.TryEvictInterest(Key(Dormant)),Is.True);
            f.Source.StagePresentationCandidate(next,10.25);f.Install(change=="generation"?2UL:1UL);
            bool accepted=f.Source.TryPublish(next,10.25);
            if(change=="world" || change=="connection")Assert.That(accepted,Is.False);
            else{Assert.That(accepted,Is.True);Assert.That(f.Source.PresentationSnapped,Is.True);}
        }

        [TestCase("freeze")][TestCase("reset")]
        public void FrozenOrResetCannotResumeTheOldDormantTrajectory(string change)
        {
            using var f=new Fixture();f.PrepareGap();
            if(change=="freeze"){f.Source.AdvancePresentation(10.245,true);f.Source.AdvancePresentation(10.25,false);}
            else f.Source.ResetConnection(3);
            Assert.That(f.Source.PresentationWindowId,Is.Zero);
            f.AssertJoinSnaps(f.Frame(3,includeDormant:true,join:true),10.25);
        }

        [Test]
        public void SameQuantityDoesNotAuthorizeADifferentNonzeroPreviousPose()
        {
            using var f=new Fixture();f.PrepareGap();
            f.AssertJoinSnaps(f.Frame(3,includeDormant:true,join:true,previousVelocity:Vector3.right),10.25);
        }

        [Test]
        public void MotionFrameThatNeverMatchedCurrentWitnessCannotSeedTheDormantEndpointCache()
        {
            using var f=new Fixture();var first=f.Frame(1,includeDormant:true,currentVelocity:OlderVelocity);f.Install();
            Assert.That(f.Source.TryPublish(first,10.11),Is.True);
            var unwitnessed=f.Frame(2,includeDormant:true,previousVelocity:OlderVelocity);
            f.Source.StagePresentationCandidate(unwitnessed,10.19);
            Assert.That(f.Source.TryPublish(unwitnessed,10.19),Is.False,"故意不安装 Q2，A 的 Current 无见证；结构合法的 M2 不能授予旧 pose。");
            var gap=f.Frame(3);f.Source.StagePresentationCandidate(gap,10.34);f.Install();Assert.That(f.Source.TryPublish(gap,10.34),Is.True);
            f.AssertJoinSnaps(f.Frame(4,includeDormant:true,join:true),10.35);
        }

        [Test]
        public void UnrelatedCurveInvalidationDoesNotEraseAStillWitnessedDormantEndpoint()
        {
            using var f=new Fixture(includeMarker:true);f.PrepareAfterUnrelatedWindowInvalidation();
            var next=f.Frame(3,includeDormant:true,join:true);f.Source.StagePresentationCandidate(next,10.25);f.Install();
            Assert.That(f.Source.PresentationWindowId,Is.EqualTo(2UL),
                "A的旧曲线曾失效，不代表独立B的已验Current事实失效；B仍同量/身份/pose且只过两Tick。");
            Assert.That(f.Source.TryPublish(next,10.25),Is.True);Assert.That(f.Source.BufferedPresentationWindows,Is.EqualTo(1));
            f.Source.AdvancePresentation(10.325,false);Assert.That(f.Source.PresentationWindowId,Is.EqualTo(3UL));
            Assert.That(f.Source.PresentationSnapped,Is.False);
            Assert.That(f.Source.TryGetPresentationSample(Dormant,MaterialId.Water,true,out var old),Is.True);
            Assert.That(old.Quantity,Is.EqualTo(18));Assert.That(old.VelocityWorld,Is.EqualTo(EndpointVelocity));
            Assert.That(f.Source.TotalQuantity,Is.EqualTo(43UL));
        }

        [TestCase(5u)][TestCase(0u)]
        public void PreservingOtherEndpointsAfterCurveInvalidationStillHonorsCollectionOfThatEndpoint(uint remaining)
        {
            using var f=new Fixture(includeMarker:true);f.PrepareAfterUnrelatedWindowInvalidation();f.Collect(remaining);
            f.AssertJoinSnaps(f.Frame(3,includeDormant:true,join:true),10.25);
        }

        [TestCase("generation")][TestCase("subscription")]
        public void PreservedEndpointAfterCurveInvalidationStillCannotCrossIdentity(string change)
        {
            using var f=new Fixture(includeMarker:true);f.PrepareAfterUnrelatedWindowInvalidation();
            var next=f.Frame(3,includeDormant:true,join:true);int index=next.Rows[Fixture.Find(next,Dormant)].AnchorIndex;
            var old=next.Anchors[index];
            next.Anchors[index]=change=="generation"
                ?new WorldLiquidMotionAnchor(old.Key,2,1,old.SubscriptionEpoch,old.IsResident)
                :new WorldLiquidMotionAnchor(old.Key,old.Generation,old.Revision,2,old.IsResident);
            f.Source.StagePresentationCandidate(next,10.25);f.Install(change=="generation"?2UL:1UL);
            Assert.That(f.Source.TryPublish(next,10.25),Is.True);Assert.That(f.Source.PresentationSnapped,Is.True);
        }
    }
}
