using NUnit.Framework;
using System;
using Unity.Profiling;
using UnityEngine;

namespace Game.Core.Tests
{
    public sealed class WorldReplicationCommitOwnerTests
    {
        private static WorldReplicaBuffer Sample(uint amount = 10, WorldMaterialStorage storage = WorldMaterialStorage.Dynamic)
        {
            var b = new WorldReplicaBuffer(8, 4, 4096);
            b.Geometries[0] = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 4);
            b.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, storage, new Vector3Int(-1, 0, 0), 2, amount);
            b.Header = new WorldReplicaHeader(1, 1, 0, 1);
            return b;
        }
        private static WorldReplicationCommitOwner Owner(int history = 4, int words = 128)
            => new WorldReplicationCommitOwner(7, 8, 8, 4, history, words, 100000);
        private static readonly WorldChunkKey Key = new WorldChunkKey(WorldMaterialDomain.Liquid, -1, 0, 0);

        [Test] public void StorageTransferDoesNotAdvanceRevisionButQuantityDoes()
        {
            var o = Owner(); var b = Sample(); var words = new uint[128];
            Assert.That(o.TryCaptureCommit(b), Is.True);
            Assert.That(o.TryCopyChunk(Key, words, out var first), Is.True);
            b.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Stable, new Vector3Int(-1,0,0),2,10);
            Assert.That(o.TryCaptureCommit(b), Is.True);
            Assert.That(o.TryCopyChunk(Key, words, out var same), Is.True);
            Assert.That(same.Revision, Is.EqualTo(first.Revision));
            b.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Stable, new Vector3Int(-1,0,0),2,11);
            Assert.That(o.TryCaptureCommit(b), Is.True);
            Assert.That(o.TryCopyDelta(Key, first.Generation, first.Revision, words, out var delta), Is.True);
            Assert.That(delta.Revision, Is.EqualTo(first.Revision + 1));
        }
        [Test] public void FrozenCopySurvivesLiveMutationAndRemovalCreatesTombstone()
        {
            var o=Owner(); var b=Sample(); var frozen=new uint[128];
            Assert.That(o.TryCaptureCommit(b), Is.True); Assert.That(o.TryCopyChunk(Key,frozen,out var h),Is.True);
            uint checksum=WorldChunkPayloadCodec.ComputeChecksum(frozen,h.WordCount);
            b.Header=new WorldReplicaHeader(2,0,0,1); Assert.That(o.TryCaptureCommit(b),Is.True);
            var removed=new uint[128]; Assert.That(o.TryCopyDelta(Key,h.Generation,h.Revision,removed,out var d),Is.True);
            Assert.That(d.IsResident,Is.False); Assert.That(WorldChunkPayloadCodec.ComputeChecksum(frozen,h.WordCount),Is.EqualTo(checksum));
            b.Header=new WorldReplicaHeader(3,1,0,1); Assert.That(o.TryCaptureCommit(b),Is.True);
            Assert.That(o.TryCopyChunk(Key,removed,out var rebuilt),Is.True); Assert.That(rebuilt.Generation,Is.EqualTo(h.Generation+1));
        }
        [Test] public void InvalidCaptureIsAtomicAndHistoryGapRequiresBaseline()
        {
            var o=Owner(2); var b=Sample(); var words=new uint[128]; Assert.That(o.TryCaptureCommit(b),Is.True);
            Assert.That(o.TryCopyChunk(Key,words,out var first),Is.True);
            for(uint i=11;i<15;i++){ b.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,new Vector3Int(-1,0,0),2,i); Assert.That(o.TryCaptureCommit(b),Is.True); }
            Assert.That(o.TryCopyDelta(Key,first.Generation,first.Revision,words,out _),Is.False);
            Assert.That(o.TryCopyChunk(Key,words,out var current),Is.True);
            b.Header=new WorldReplicaHeader(99,9,0,1); Assert.That(o.TryCaptureCommit(b),Is.False);
            Assert.That(o.TryCopyChunk(Key,words,out var after),Is.True); Assert.That(after.Revision,Is.EqualTo(current.Revision));
        }
        [Test] public void GeometryAndPendingChangesAreRealCommits()
        {
            var o=Owner(); var b=Sample(); var w=new uint[128]; Assert.That(o.TryCaptureCommit(b),Is.True);
            Assert.That(o.TryCopyChunk(Key,w,out var a),Is.True);
            b.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.one,1,4);
            Assert.That(o.TryCaptureCommit(b),Is.True); Assert.That(o.TryCopyChunk(Key,w,out var c),Is.True); Assert.That(c.Revision,Is.GreaterThan(a.Revision));
            b.Pending[0]=new WorldPendingDepositSample(WorldMaterialDomain.Liquid,0,2,new Vector3(-1,1,1),10,1,false,Vector3.zero,Vector3.up,10,0,0);
            b.Header=new WorldReplicaHeader(3,1,1,1); Assert.That(o.TryCaptureCommit(b),Is.True);
            Assert.That(o.TryCopyChunk(Key,w,out var d),Is.True); Assert.That(d.Revision,Is.GreaterThan(c.Revision));
        }
        [Test] public void EveryDomainIncludingPendingRoundTripsWithoutNarrowingQuantity()
        {
            var o=Owner();var b=new WorldReplicaBuffer(8,4,100000);
            for(int d=0;d<4;d++)
            {
                var domain=(WorldMaterialDomain)d;b.Geometries[d]=new WorldGridGeometry(domain,Vector3.zero,1,4);
                b.Cells[d]=new WorldMaterialCellSample(domain,d==1?WorldMaterialStorage.Dynamic:WorldMaterialStorage.Resident,Vector3Int.zero,2,d==0?255:uint.MaxValue);
                b.Pending[d]=new WorldPendingDepositSample(domain,0,2,Vector3.zero,10,1,false,Vector3.one,Vector3.up,9,1,3);
            }
            b.Header=new WorldReplicaHeader(1,4,4,4);Assert.That(o.TryCaptureCommit(b),Is.True);
            for(int d=0;d<4;d++)
            {
                var key=new WorldChunkKey((WorldMaterialDomain)d,0,0,0);var w=new uint[128];var decoded=new WorldReplicaBuffer(2,2,4096);
                Assert.That(o.TryCopyChunk(key,w,out var h),Is.True);Assert.That(WorldChunkPayloadCodec.TryDecode(w,h.WordCount,key,h.ServerTick,decoded),Is.True);
                Assert.That(decoded.Header.PendingCount,Is.EqualTo(1));Assert.That(decoded.Cells[0].Quantity,Is.EqualTo(d==0?255:uint.MaxValue));
                Assert.That(decoded.Pending[0].WaitingTicks,Is.EqualTo(3));Assert.That(h.BaseRevision,Is.Zero);
            }
        }
        [Test] public void PayloadCapacityFailurePreservesSequenceAndPublishedWords()
        {
            var o=Owner(words:18);var b=Sample();var w=new uint[18];Assert.That(o.TryCaptureCommit(b),Is.True);
            Assert.That(o.TryCopyChunk(Key,w,out var h),Is.True);ulong sequence=o.CommitSequence;
            b.Cells[1]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,new Vector3Int(-2,0,0),2,2);
            b.Header=new WorldReplicaHeader(2,2,0,1);Assert.That(o.TryCaptureCommit(b),Is.False);Assert.That(o.CommitSequence,Is.EqualTo(sequence));
            Assert.That(o.TryCopyChunk(Key,w,out var after),Is.True);Assert.That(after.Checksum,Is.EqualTo(h.Checksum));
        }
        [Test] public void IdenticalCaptureDoesNotCommitAndCrossOwnerSumIsCanonical()
        {
            var o=Owner();var b=Sample(10);Assert.That(o.TryCaptureCommit(b),Is.True);ulong sequence=o.CommitSequence;
            b.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,new Vector3Int(-1,0,0),2,3);
            b.Cells[1]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Stable,new Vector3Int(-1,0,0),2,7);
            b.Header=new WorldReplicaHeader(2,2,0,1);Assert.That(o.TryCaptureCommit(b),Is.True);Assert.That(o.CommitSequence,Is.EqualTo(sequence));
            b.Cells[1]=b.Cells[0];Assert.That(o.TryCaptureCommit(b),Is.False);Assert.That(o.CommitSequence,Is.EqualTo(sequence));
        }
        [Test] public void DecodeFailureDoesNotChangeDestination()
        {
            var o=Owner();var b=Sample();var w=new uint[128];Assert.That(o.TryCaptureCommit(b),Is.True);Assert.That(o.TryCopyChunk(Key,w,out var h),Is.True);
            var destination=new WorldReplicaBuffer(8,4,4096);destination.Header=new WorldReplicaHeader(77,0,0,0);
            w[17]=0;Assert.That(WorldChunkPayloadCodec.TryDecode(w,h.WordCount,Key,1,destination),Is.False);Assert.That(destination.Header.WorldTick,Is.EqualTo(77));
        }
        [Test] public void WarmCommitFreezeAndAssemblyHaveZeroPositivelyCalibratedAllocationEvents()
        {
            var o=Owner();var b=Sample();var w=new uint[128];var replica=new WorldChunkReplicaAssembler(7,Key,3,128,4,128,30);
            bool ok=true;
            for(int i=0;i<16;i++)ok&=Cycle(o,b,w,replica,i);
            Assert.That(ok,Is.True);
            using var recorder=ProfilerRecorder.StartNew(ProfilerCategory.Internal,"GC.Alloc",128,ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            Assert.That(recorder.Valid,Is.True);recorder.Stop();recorder.Reset();recorder.Start();
            var positiveControl=new byte[1024];GC.KeepAlive(positiveControl);recorder.Stop();
            int calibration=recorder.Count;Assert.That(calibration,Is.GreaterThan(0));Assert.That(calibration,Is.LessThan(recorder.Capacity));
            recorder.Reset();recorder.Start();
            for(int i=16;i<272;i++)ok&=Cycle(o,b,w,replica,i);
            recorder.Stop();int events=recorder.Count;
            Assert.That(ok,Is.True);Assert.That(events,Is.LessThan(recorder.Capacity));Assert.That(events,Is.Zero);
            TestContext.WriteLine("GC.Alloc calibration="+calibration+", warmed owner/freeze/assembly events="+events+"; excludes Native full frame and construction.");
        }
        private static bool Cycle(WorldReplicationCommitOwner owner,WorldReplicaBuffer source,uint[] words,WorldChunkReplicaAssembler replica,int i)
        {
            source.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,new Vector3Int(-1,0,0),2,(uint)i+10);
            bool ok=owner.TryCaptureCommit(source);ok&=owner.TryCopyChunk(Key,words,out var h);
            ok&=replica.TryBegin(h,i*2);ok&=replica.TryReceivePage(h,0,words,h.WordCount,i*2+1);return ok;
        }
    }
}
