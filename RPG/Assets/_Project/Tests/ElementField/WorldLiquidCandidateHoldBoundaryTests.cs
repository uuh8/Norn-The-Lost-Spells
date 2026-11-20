using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 候选只读hold的边界：真实Store先安装跨Chunk中间态，显示只能借用旧端点；
    /// 无关收量读最新Store，新候选不能续租，错误时间/Grid不能成为保留旧水的理由。
    /// 不通过伪造库存或直接改私有状态通过验证。
    /// </summary>
    public sealed class WorldLiquidCandidateHoldBoundaryTests
    {
        private static readonly WorldChunkKey AKey=new WorldChunkKey(WorldMaterialDomain.Liquid,0,0,0);
        private static readonly WorldChunkKey BKey=new WorldChunkKey(WorldMaterialDomain.Liquid,1,0,0);
        private static readonly WorldChunkKey OtherKey=new WorldChunkKey(WorldMaterialDomain.Liquid,2,0,0);
        private static readonly Vector3Int A=new Vector3Int(1,2,1),B=new Vector3Int(4,2,1),Other=new Vector3Int(8,2,1),SameChunkOther=new Vector3Int(2,2,1);
        private WorldReplicaStore _store;
        private WorldReplicaLiquidReadModel _source;
        private LiquidWorldProfile _profile;
        private LiquidBehaviorProfile _water;
        [SetUp]public void Setup()
        {
            _profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();_water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",_profile);
            typeof(LiquidWorldProfile).GetField("_materials",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_profile,new[]{_water});
            var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,4);
            _store=new WorldReplicaStore(7,8,256,0,2048);
            Assert.That(_store.ConfigureGeometry(geometry),Is.True);
            Assert.That(_store.ConfigureMaterials(WorldMaterialDomain.Liquid,510,new[]{(byte)MaterialId.Water}),Is.True);
            _source=new WorldReplicaLiquidReadModel(_store,geometry,_profile,.25f,null);_source.ResetConnection(3);
        }
        [TearDown]public void Cleanup(){Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);}
        [Test]public void UnknownTargetCanHoldOnlyPreviouslyValidatedEndpointWithoutCreatingQuantity()
        {
            Prepare(false);Hold(false);
            Assert.That(_source.PresentationHolding,Is.True);Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(10UL));
            Assert.That(_source.TryGetPresentationSample(A,MaterialId.Water,false,out var held),Is.True);Assert.That(held.Quantity,Is.EqualTo(20));
            Assert.That(_source.TryGetPresentationSample(B,MaterialId.Water,false,out var target),Is.False);Assert.That(target.Quantity,Is.Zero);
            Assert.That(_store.TryGetPublishedChunkFact(BKey,out _,out _),Is.False,"不能为future target制造已安装身份。");
        }
        [Test]public void WholeUnrelatedPoolTombstoneIsImmediatelyVisibleWhileTransportMayStillHold()
        {
            Prepare(false);Hold(false);Tombstone(OtherKey,2);_source.AdvancePresentation(10.24,false);
            Assert.That(_source.TryGetPresentationSample(Other,MaterialId.Water,false,out var removed),Is.False);Assert.That(removed.Quantity,Is.Zero);
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid),Is.Zero);
            // 这里只验证按row隔离：不要求因另一洼水变化而破坏仍合法的运动组。
            Assert.That(_source.PresentationHolding,Is.True);
        }
        [Test]public void SameAnchorUnrelatedHarvestDoesNotResurrectRemovedCell()
        {
            Prepare(true);var next=Next(true);_source.StagePresentationCandidate(next,10.225);
            // 本提交同时包含候选涉及的搬运与同Chunk另一行的收取，Revision恰等候选引用值。
            // 只检查anchor.Revision不能区分这两行，显示必须分别读正确来源。
            Install(AKey,2);_source.AdvancePresentation(10.23,false);
            Assert.That(_source.PresentationHolding,Is.True);
            Assert.That(_source.TryGetPresentationSample(SameChunkOther,MaterialId.Water,false,out var removed),Is.False);Assert.That(removed.Quantity,Is.Zero);
            Assert.That(_source.TryGetPresentationSample(A,MaterialId.Water,false,out var held),Is.True);Assert.That(held.Quantity,Is.EqualTo(20));
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid),Is.Zero);
        }
        [Test]public void DifferentCandidateCannotRenewTheSameOldEndpointLease()
        {
            Prepare(false);Hold(false);
            var replacement=Next(false);replacement.Sequence=4;
            _source.StagePresentationCandidate(replacement,10.29);_source.AdvancePresentation(10.34,false);
            Assert.That(_source.PresentationHolding,Is.False);Assert.That(_source.PresentationWindowId,Is.Zero,
                "期限属于旧窗口，不能因为换一个候选Sequence而重新保留100ms。");
        }
        [Test]public void FarFutureSampleTimeCannotStageAnOtherwiseMatchingCandidate()
        {
            Prepare(false);var next=Next(false);next.SampleTime=1000;
            _source.StagePresentationCandidate(next,10.225);
            Assert.That(_source.PendingPresentationSequence,Is.Zero,"Stage与Publish都应拒绝超出一Interval的未来时间。");
        }
        [Test]public void CancelledUnknownTargetCandidateImmediatelyReleasesHeldEndpoint()
        {
            Prepare(false);Hold(false);Assert.That(_store.TryGetPublishedChunkFact(BKey,out _,out _),Is.False);
            // 目标没有Store元数据时，Leave只能由认证订阅栅栏通知，不能等待本地数量变化或TTL。
            _source.CancelPresentationCandidate();
            Assert.That(_source.PresentationHolding,Is.False);Assert.That(_source.PresentationWindowId,Is.Zero);
            Assert.That(_source.TryGetPresentationSample(A,MaterialId.Water,false,out var old),Is.False);Assert.That(old.Quantity,Is.Zero);
        }
        [Test]public void DifferentGridChunkSizeCannotStageAnOtherwiseMatchingCandidate()
        {
            Prepare(false);var next=Next(false);next.ChunkSize=8;next.AnchorCount=1;
            next.Anchors[0]=new WorldLiquidMotionAnchor(AKey,1,2,1,true);
            next.Rows[1]=Row(new Vector3Int(3,2,1),0,20,0);
            Assert.That(WorldLiquidMotionCodec.Validate(next),Is.True,"这是结构合法、却与场景Grid不兼容的候选。");
            _source.StagePresentationCandidate(next,10.225);
            Assert.That(_source.PendingPresentationSequence,Is.Zero);
        }
        [Test]public void PublishRevisionFailureKeepsExactWitnessWhenOldMotionIsLaterInvalidated()
        {
            Prepare(false);var next=Next(false);
            Assert.That(_source.TryPublish(next,10.225),Is.False);
            var diagnostic=_source.LastMotionWitnessDiagnostic;
            Assert.That(diagnostic.Reason,Is.EqualTo((byte)WorldLiquidMotionWitnessFailure.AnchorRevisionBehind));
            Assert.That(diagnostic.Sequence,Is.EqualTo(3UL));Assert.That(diagnostic.MaterialTick,Is.EqualTo(103u));
            Assert.That(diagnostic.ExpectedRevision,Is.EqualTo(2UL));Assert.That(diagnostic.ActualRevision,Is.EqualTo(1UL));
            Assert.That(diagnostic.AnchorCount,Is.EqualTo(2));
            // 旧已发布窗口也会因新数量失败；它的只读校验不得把刚刚TryPublish的拒因覆盖。
            Install(AKey,2,Cell(A,19));_source.AdvancePresentation(10.23,false);
            Assert.That(_source.PresentationWindowId,Is.Zero);
            Assert.That(_source.LastMotionWitnessDiagnostic.Reason,Is.EqualTo(diagnostic.Reason));
            Assert.That(_source.LastMotionWitnessDiagnostic.Sequence,Is.EqualTo(3UL));
        }
        [Test]public void PublishQuantityFailureReportsTheExactCellExpectedAndActualWithoutWritingGmu()
        {
            Prepare(false);var next=Next(false);next.AnchorCount=1;next.RowCount=1;
            next.Anchors[0]=new WorldLiquidMotionAnchor(AKey,1,1,1,true);next.Rows[0]=Row(A,20,19,0);
            Assert.That(_source.TryPublish(next,10.225),Is.False);
            var diagnostic=_source.LastMotionWitnessDiagnostic;
            Assert.That(diagnostic.Reason,Is.EqualTo((byte)WorldLiquidMotionWitnessFailure.CurrentQuantity));
            Assert.That(new Vector3Int(diagnostic.CellX,diagnostic.CellY,diagnostic.CellZ),Is.EqualTo(A));
            Assert.That(diagnostic.MaterialId,Is.EqualTo((byte)MaterialId.Water));
            Assert.That(diagnostic.ExpectedQuantity,Is.EqualTo(19u));Assert.That(diagnostic.ActualQuantity,Is.EqualTo(20u));
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(30UL));
            StringAssert.Contains("\"ExpectedQuantity\":19",JsonUtility.ToJson(diagnostic));
        }
        [TestCase(false)][TestCase(true)]public void HoldPoseDiagnosticDistinguishesOffsetAndVelocityWithoutRelaxingEitherGuard(bool velocity)
        {
            Prepare(false);var next=Next(false);Vector3 delta=new Vector3(.01f,0,0);
            next.Rows[0]=new WorldLiquidMotionSample(A,(byte)MaterialId.Water,20,0,
                velocity?Vector3.zero:delta,velocity?delta:Vector3.zero,Vector3.zero,Vector3.zero,0);
            _source.StagePresentationCandidate(next,10.225);Tombstone(AKey,2);_source.AdvancePresentation(10.23,false);
            Assert.That(_source.PresentationHolding,Is.False);Assert.That(_source.MotionHoldChecks,Is.GreaterThan(0));
            var diagnostic=_source.LastMotionHoldDiagnostic;
            Assert.That(diagnostic.Reason,Is.EqualTo((byte)(velocity?WorldLiquidMotionHoldFailure.PreviousVelocity:WorldLiquidMotionHoldFailure.PreviousOffset)));
            Assert.That(diagnostic.Sequence,Is.EqualTo(3UL));Assert.That(diagnostic.PoseDeltaSquared,Is.GreaterThan(.000001f));
            Assert.That(new Vector3Int(diagnostic.CellX,diagnostic.CellY,diagnostic.CellZ),Is.EqualTo(A));
        }
        private void Prepare(bool sameChunk)
        {
            if(sameChunk)Install(AKey,1,Cell(A,20),Cell(SameChunkOther,10));
            else{Install(AKey,1,Cell(A,20));Install(OtherKey,1,Cell(Other,10));}
            var frame=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=1,MaterialTick=101,SampleTime=10,Interval=.1f,ChunkSize=4,AnchorCount=1,RowCount=1};
            frame.Anchors[0]=new WorldLiquidMotionAnchor(AKey,1,1,1,true);frame.Rows[0]=Row(A,20,20,0);
            Assert.That(_source.TryPublish(frame,10.025),Is.True);
            frame.Sequence=2;frame.MaterialTick=102;frame.SampleTime=10.1;
            Assert.That(_source.TryPublish(frame,10.125),Is.True);
        }
        private void Hold(bool sameChunk)
        {
            _source.StagePresentationCandidate(Next(sameChunk),10.225);
            if(sameChunk)Install(AKey,2);else Tombstone(AKey,2);
            _source.AdvancePresentation(10.23,false);Assert.That(_source.PresentationHolding,Is.True);
        }
        private static WorldLiquidMotionFrame Next(bool resident)
        {
            var frame=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=3,MaterialTick=103,SampleTime=10.2,Interval=.1f,ChunkSize=4,AnchorCount=2,RowCount=2};
            frame.Anchors[0]=new WorldLiquidMotionAnchor(AKey,1,2,1,resident);frame.Anchors[1]=new WorldLiquidMotionAnchor(BKey,1,1,1,true);
            frame.Rows[0]=Row(A,20,0,0);frame.Rows[1]=Row(B,0,20,1);return frame;
        }
        private static WorldLiquidMotionSample Row(Vector3Int cell,uint previous,uint current,int anchor)=>new WorldLiquidMotionSample(cell,(byte)MaterialId.Water,previous,current,Vector3.zero,Vector3.zero,Vector3.zero,Vector3.zero,anchor);
        private static WorldMaterialCellSample Cell(Vector3Int cell,uint quantity)=>new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Resident,cell,(byte)MaterialId.Water,quantity);
        private void Install(WorldChunkKey key,ulong revision,params WorldMaterialCellSample[] cells)
        {
            var words=new uint[11+cells.Length*WorldChunkPayloadCodec.CellWords];words[0]=WorldChunkPayloadCodec.Magic;words[1]=(uint)cells.Length;words[3]=1;
            WorldChunkPayloadCodec.WriteGeometry(words,4,new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,4));
            for(int i=0;i<cells.Length;i++)WorldChunkPayloadCodec.WriteCell(words,11+i*WorldChunkPayloadCodec.CellWords,cells[i]);
            Assert.That(_store.TryInstall(new WorldChunkPayloadHeader(7,revision,key,1,0,revision,103,words.Length,WorldChunkPayloadCodec.ComputeChecksum(words,words.Length),true),words,words.Length),Is.True);
        }
        private void Tombstone(WorldChunkKey key,ulong revision)=>Assert.That(_store.TryInstall(new WorldChunkPayloadHeader(7,revision,key,1,0,revision,103,0,2166136261u,false),null,0),Is.True);
    }
}
