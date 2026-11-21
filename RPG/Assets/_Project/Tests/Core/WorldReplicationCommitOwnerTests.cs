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

        [Test]
        public void CellularCountingOrderMatchesGenericCanonicalWordsAndRejectsDuplicateOrOverflowAtomically()
        {
            const WorldMaterialDomain domain=WorldMaterialDomain.Cellular;
            var key=new WorldChunkKey(domain,-1,-2,1);
            var dense=new WorldReplicaBuffer(96,0,100000);var generic=new WorldReplicaBuffer(96,0,100000);
            dense.Geometries[0]=generic.Geometries[0]=new WorldGridGeometry(domain,Vector3.zero,1,8,77,255);
            for(int n=0;n<96;n++)
            {
                int bits=((n/3)*73)&511;var cell=new Vector3Int(-8+(bits>>6),-16+((bits>>3)&7),8+(bits&7));
                byte material=n%3==0?(byte)1:n%3==1?(byte)7:(byte)8;
                int shuffled=(n*53)%96;
                dense.Cells[shuffled]=generic.Cells[shuffled]=new WorldMaterialCellSample(domain,WorldMaterialStorage.Resident,cell,material,10);
            }
            dense.Header=new WorldReplicaHeader(1,96,0,1);generic.Header=new WorldReplicaHeader(1,96,0,1,1);
            // 空地形覆盖触发通用 HeapSort。剔除该额外盒后应逐字取得完全相同的 canonical W2 和 checksum。
            generic.Spans[0]=new WorldCellularSpan(WorldCellularSpanKind.Terrain,new BoundsInt(new Vector3Int(-1,-9,15),Vector3Int.one),0,0,0);
            var fastOwner=new WorldReplicationCommitOwner(7,8,96,0,8,2048,100000);
            var genericOwner=new WorldReplicationCommitOwner(7,8,96,0,8,2048,100000);
            Assert.That(fastOwner.TryCaptureCommit(dense),Is.True);Assert.That(genericOwner.TryCaptureCommit(generic),Is.True);
            var actual=new uint[2048];var expected=new uint[2048];
            Assert.That(fastOwner.TryCopyChunk(key,actual,out var first),Is.True);
            Assert.That(genericOwner.TryCopyChunk(key,expected,out var other),Is.True);
            Assert.That(first.WordCount+WorldChunkPayloadCodec.SpanWords,Is.EqualTo(other.WordCount));
            expected[14]=0;
            for(int i=0;i<first.WordCount;i++)Assert.That(actual[i],Is.EqualTo(expected[i]),"W2 word "+i);
            Assert.That(first.Checksum,Is.EqualTo(WorldChunkPayloadCodec.ComputeChecksum(expected,first.WordCount)));

            var old=dense.Cells[1];dense.Cells[1]=generic.Cells[1]=dense.Cells[0];
            Assert.That(fastOwner.TryCaptureCommit(dense),Is.False);Assert.That(genericOwner.TryCaptureCommit(generic),Is.False);
            Assert.That(fastOwner.LastFailure,Is.EqualTo(WorldCommitOwnerFailure.Occupancy));Assert.That(genericOwner.LastFailure,Is.EqualTo(fastOwner.LastFailure));
            dense.Cells[1]=generic.Cells[1]=new WorldMaterialCellSample(domain,WorldMaterialStorage.Resident,old.Cell,old.MaterialId,256);
            Assert.That(fastOwner.TryCaptureCommit(dense),Is.False);Assert.That(genericOwner.TryCaptureCommit(generic),Is.False);
            Assert.That(fastOwner.LastFailure,Is.EqualTo(WorldCommitOwnerFailure.InvalidInput));Assert.That(genericOwner.LastFailure,Is.EqualTo(fastOwner.LastFailure));
            Assert.That(fastOwner.TryGetCurrentHeader(key,out var retained),Is.True);
            Assert.That(retained.Revision,Is.EqualTo(first.Revision));Assert.That(retained.Checksum,Is.EqualTo(first.Checksum));
        }

        [Test]
        public void CompactCellularWireExpandsExactlyAndKeepsCanonicalVersionOnBadOrOversizedPage()
        {
            const WorldMaterialDomain domain=WorldMaterialDomain.Cellular;
            var key=new WorldChunkKey(domain,-1,0,0);
            var source=new WorldReplicaBuffer(8,1,8192);
            source.Geometries[0]=new WorldGridGeometry(domain,Vector3.zero,1f,8,763,255);
            var cell=new Vector3Int(-1,7,2);
            source.Cells[0]=new WorldMaterialCellSample(domain,WorldMaterialStorage.Resident,cell,1,122);
            source.Cells[1]=new WorldMaterialCellSample(domain,WorldMaterialStorage.Resident,cell,8,100);
            source.Spans[0]=new WorldCellularSpan(WorldCellularSpanKind.Terrain,new BoundsInt(cell,Vector3Int.one),0,0,0);
            source.Pending[0]=new WorldPendingDepositSample(domain,0,1,new Vector3(-.5f,7.5f,2.5f),4,0,false,
                Vector3.zero,Vector3.up,4,0,0,(1L<<34)+10);
            source.Header=new WorldReplicaHeader(4,2,1,1,1);
            var owner=Owner();Assert.That(owner.TryCaptureCommit(source),Is.True);
            var canonical=new uint[128];Assert.That(owner.TryCopyChunk(key,canonical,out var header),Is.True);
            var wire=(uint[])canonical.Clone();
            int packed=WorldChunkPayloadCodec.PackCellularForWire(key,wire,header.WordCount);
            Assert.That(packed,Is.EqualTo(header.WordCount-12));
            WorldChunkPayloadHeader HeaderFor(WorldChunkPayloadHeader logical,int length)=>
                new WorldChunkPayloadHeader(logical.WorldEpoch,logical.SnapshotId,key,logical.Generation,logical.BaseRevision,logical.Revision,
                    logical.ServerTick,length,logical.Checksum,logical.IsResident);
            var physical=HeaderFor(header,packed);
            var receiver=new WorldChunkReplicaAssembler(7,key,2,128,2,128,100);
            Assert.That(receiver.TryBegin(physical,0),Is.True);
            Assert.That(receiver.TryReceivePage(physical,0,wire,packed,1),Is.True);
            Assert.That(receiver.InstalledWireTransfer(physical),Is.True);
            var read=new uint[header.WordCount];Assert.That(receiver.TryCopyInstalled(read,out var installed),Is.True);
            Assert.That(installed,Is.EqualTo(header));
            for(int i=0;i<read.Length;i++)Assert.That(read[i],Is.EqualTo(canonical[i]));

            // 较短的压缩页仍受到解压后最大逻辑字数限制，不得按外部声明扩增内存或取得安装 ACK。
            var small=new WorldChunkReplicaAssembler(7,key,2,packed,1,128,100);
            Assert.That(small.TryBegin(physical,0),Is.True);Assert.That(small.TryReceivePage(physical,0,wire,packed,1),Is.False);
            Assert.That(small.Installed,Is.False);
            source.Cells[0]=new WorldMaterialCellSample(domain,WorldMaterialStorage.Resident,cell,1,123);
            source.Header=new WorldReplicaHeader(5,2,1,1,1);
            Assert.That(owner.TryCaptureCommit(source),Is.True);
            Assert.That(owner.TryCopyDelta(key,header.Generation,header.Revision,wire,out var delta),Is.True);
            int length=WorldChunkPayloadCodec.PackCellularForWire(key,wire,delta.WordCount);
            physical=HeaderFor(delta,length);wire[WorldChunkPayloadCodec.CellularPrefixWords]^=1u<<16;
            Assert.That(receiver.TryBegin(physical,2),Is.True);Assert.That(receiver.TryReceivePage(physical,0,wire,length,3),Is.False);
            Assert.That(receiver.LastFailure,Is.EqualTo(WorldReplicaAssemblyFailure.Checksum));
            Assert.That(receiver.Header,Is.EqualTo(header));
            // 可信地图允许更大 GMU 时按原始 W2 原样透传；新接收器也接受同版标准 Baseline。
            var fallback=(uint[])canonical.Clone();fallback[13]=ushort.MaxValue+1u;
            Assert.That(WorldChunkPayloadCodec.PackCellularForWire(key,fallback,header.WordCount),Is.EqualTo(header.WordCount));
            Assert.That(fallback[0],Is.EqualTo(WorldChunkPayloadCodec.CellularMagic));
            Assert.That(receiver.TryBegin(header,4),Is.True);Assert.That(receiver.TryReceivePage(header,0,canonical,header.WordCount,5),Is.True);
        }

        [Test]
        public void SparseCellularDirectoryKeeps4096BaselinesAndAllocatesOnlyActualPayloads()
        {
            const int limit=4096, chunkSize=8;
            const WorldMaterialDomain domain=WorldMaterialDomain.Cellular;
            var owner=new WorldReplicationCommitOwner(7,limit,1,0,64,16384,2000000);
            var source=new WorldReplicaBuffer(1,0,2000000,limit,1);
            source.Geometries[0]=new WorldGridGeometry(domain,Vector3.zero,.25f,chunkSize,125,16);
            for(int x=1;x<limit;x++)
                source.Spans[x-1]=new WorldCellularSpan(WorldCellularSpanKind.Terrain,
                    new BoundsInt(new Vector3Int(x*chunkSize,0,0),Vector3Int.one),0,0,0);
            source.Header=new WorldReplicaHeader(1,0,0,1,limit-1);
            Assert.That(owner.PayloadCapacityWords,Is.Zero);
            Assert.That(owner.TryCaptureCommit(source),Is.True,owner.LastFailure.ToString());
            Assert.That(owner.ChunkCount,Is.EqualTo(limit));
            // 15 个几何头字 + 每处 10 个跨度字。这个断言可发现按 16384 字为每个地址预分配的回归。
            Assert.That(owner.PayloadCapacityWords,Is.LessThan(32L*limit));
            var far=new WorldChunkKey(domain,limit-1,0,0);
            var near=new WorldChunkKey(domain,1,0,0);
            var words=new uint[64];
            Assert.That(owner.TryCopyChunk(far,words,out var distant),Is.True);
            Assert.That(WorldChunkPayloadCodec.ReadSpan(words,WorldChunkPayloadCodec.SpanOffset(words)).Cells.position.x,
                Is.EqualTo((limit-1)*chunkSize));
            Assert.That(owner.TryCopyChunk(near,words,out var first),Is.True);

            var nearCell=new BoundsInt(new Vector3Int(chunkSize,0,0),Vector3Int.one);
            source.Spans[0]=new WorldCellularSpan(WorldCellularSpanKind.Terrain,nearCell,10,0,0);
            source.ChangedChunks[0]=near;source.Header=new WorldReplicaHeader(2,0,0,1,1);
            Assert.That(owner.TryCaptureChangedChunks(source,1),Is.True);
            Assert.That(owner.TryCopyDelta(near,first.Generation,first.Revision,words,out var delta),Is.True);
            Assert.That(delta.Revision,Is.EqualTo(first.Revision+1));
            Assert.That(owner.TryGetCurrentHeader(far,out var unchanged),Is.True);
            Assert.That(unchanged.Revision,Is.EqualTo(distant.Revision));
            Assert.That(unchanged.Checksum,Is.EqualTo(distant.Checksum));

            source.ChangedChunks[0]=new WorldChunkKey(domain,limit,0,0);
            source.Spans[0]=new WorldCellularSpan(WorldCellularSpanKind.Terrain,
                new BoundsInt(new Vector3Int(limit*chunkSize,0,0),Vector3Int.one),0,0,0);
            Assert.That(owner.TryCaptureChangedChunks(source,1),Is.False);
            Assert.That(owner.LastFailure,Is.EqualTo(WorldCommitOwnerFailure.Capacity));
            Assert.That(owner.ChunkCount,Is.EqualTo(limit));
            Assert.That(owner.TryCopyChunk(far,words,out var retained),Is.True);
            Assert.That(retained.Checksum,Is.EqualTo(distant.Checksum));
        }

        [Test]
        public void CellularSpansRetryTicksAndLocalRetirementRoundTripWithoutChangingRemoteRevision()
        {
            const WorldMaterialDomain domain = WorldMaterialDomain.Cellular;
            const long retryTick = (1L << 35) + 7;
            var source = new WorldReplicaBuffer(8, 2, 4096);
            source.Geometries[0] = new WorldGridGeometry(domain, Vector3.zero, 1, 4, 123, 16);
            source.Cells[0] = new WorldMaterialCellSample(domain, WorldMaterialStorage.Resident, Vector3Int.zero, 1, 9);
            source.Cells[1] = new WorldMaterialCellSample(domain, WorldMaterialStorage.Resident, Vector3Int.zero, 7, 7);
            source.Pending[0] = new WorldPendingDepositSample(domain, 0, 12, Vector3.zero, 5, 0, false,
                Vector3.zero, Vector3.up, 5, 0, 0, retryTick);
            var here = new BoundsInt(Vector3Int.zero, Vector3Int.one);
            source.Spans[0] = new WorldCellularSpan(WorldCellularSpanKind.AuthoredLiquid, here, 1, 16, 0);
            source.Spans[1] = new WorldCellularSpan(WorldCellularSpanKind.Terrain, here, 0, 0, 0);
            source.Spans[2] = new WorldCellularSpan(WorldCellularSpanKind.ColdMaterial,
                new BoundsInt(new Vector3Int(-4,0,0),new Vector3Int(4,4,4)), 11, 0, 16);
            source.Header = new WorldReplicaHeader(2,2,1,1,3);
            var owner = Owner(); var words = new uint[128];
            Assert.That(owner.TryCaptureCommit(source), Is.True);
            var origin = new WorldChunkKey(domain,0,0,0); var remote = new WorldChunkKey(domain,-1,0,0);
            Assert.That(owner.TryCopyChunk(origin,words,out var first),Is.True);
            var decoded = new WorldReplicaBuffer(8,2,4096);
            Assert.That(WorldChunkPayloadCodec.TryDecode(words,first.WordCount,origin,2,decoded),Is.True);
            Assert.That(decoded.Geometries[0].BaseVersion,Is.EqualTo(123ul));
            Assert.That(decoded.Pending[0].RetryTick,Is.EqualTo(retryTick));
            Assert.That(decoded.Spans[1].OriginalQuantity,Is.EqualTo(16u));
            Assert.That(decoded.Spans[1].Quantity,Is.EqualTo(0u));

            source.Header = new WorldReplicaHeader(3,0,0,1);
            source.ChangedChunks[0] = remote;
            Assert.That(owner.TryCaptureChangedChunks(source,1),Is.True);
            Assert.That(owner.TryCopyChunk(remote,words,out var retired) && !retired.IsResident,Is.True);
            Assert.That(owner.TryCopyChunk(origin,words,out var same),Is.True);
            Assert.That(same.Checksum,Is.EqualTo(first.Checksum));
            Assert.That(same.Revision,Is.EqualTo(first.Revision));
            source.Geometries[0] = new WorldGridGeometry(domain,Vector3.zero,1,4,124,16);
            Assert.That(owner.TryCaptureChangedChunks(source,1),Is.False);
        }

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
        [Test] public void ContiguousSameGenerationDeltaCoalescesToTheLatestCompleteAfterImage()
        {
            var owner = Owner(8); var source = Sample(); var firstWords = new uint[128];
            Assert.That(owner.TryCaptureCommit(source), Is.True);
            Assert.That(owner.TryCopyChunk(Key, firstWords, out var first), Is.True);
            for (uint revision = 2; revision <= 4; revision++)
            {
                source.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                    WorldMaterialStorage.Dynamic, new Vector3Int(-1, 0, 0), 2, revision * 10);
                source.Header = new WorldReplicaHeader(revision, 1, 0, 1);
                Assert.That(owner.TryCaptureCommit(source), Is.True);
            }
            var currentWords = new uint[128]; var deltaWords = new uint[128];
            Assert.That(owner.TryCopyChunk(Key, currentWords, out var current), Is.True);
            Assert.That(owner.TryCopyDelta(Key, first.Generation, first.Revision, deltaWords, out var merged), Is.True);
            Assert.That(merged.Generation, Is.EqualTo(current.Generation)); Assert.That(merged.BaseRevision, Is.EqualTo(first.Revision));
            Assert.That(merged.Revision, Is.EqualTo(current.Revision), "ACK R1时Owner已有R4，不能继续排队交付旧R2。");
            Assert.That(merged.SnapshotId, Is.EqualTo(current.SnapshotId)); Assert.That(merged.ServerTick, Is.EqualTo(current.ServerTick));
            Assert.That(merged.WordCount, Is.EqualTo(current.WordCount)); Assert.That(merged.Checksum, Is.EqualTo(current.Checksum));
            for (int i = 0; i < merged.WordCount; i++) Assert.That(deltaWords[i], Is.EqualTo(currentWords[i]));
            Assert.That(firstWords[first.WordCount - 1], Is.EqualTo(10), "合并不能改写早先借出的冻结正文。");
        }

        [Test] public void ContiguousDeltaCanCloseDirectlyAtTheActualTerminalTombstone()
        {
            var owner = Owner(8); var source = Sample(); var words = new uint[128];
            Assert.That(owner.TryCaptureCommit(source), Is.True); Assert.That(owner.TryCopyChunk(Key, words, out var first), Is.True);
            for (uint tick = 2; tick <= 3; tick++)
            {
                source.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                    WorldMaterialStorage.Dynamic, new Vector3Int(-1, 0, 0), 2, tick * 10);
                source.Header = new WorldReplicaHeader(tick, 1, 0, 1); Assert.That(owner.TryCaptureCommit(source), Is.True);
            }
            source.Header = new WorldReplicaHeader(4, 0, 0, 1); Assert.That(owner.TryCaptureCommit(source), Is.True);
            Assert.That(owner.TryGetCurrentHeader(Key, out var terminal), Is.True);
            Assert.That(owner.TryCopyDelta(Key, first.Generation, first.Revision, words, out var merged), Is.True);
            Assert.That(merged.IsResident, Is.False); Assert.That(merged.BaseRevision, Is.EqualTo(first.Revision));
            Assert.That(merged.Generation, Is.EqualTo(terminal.Generation)); Assert.That(merged.Revision, Is.EqualTo(terminal.Revision));
            Assert.That(merged.SnapshotId, Is.EqualTo(terminal.SnapshotId)); Assert.That(merged.ServerTick, Is.EqualTo(terminal.ServerTick));
            Assert.That(merged.WordCount, Is.Zero); Assert.That(merged.Checksum, Is.EqualTo(2166136261u));
        }

        [Test] public void CoalescedDeltaReplacesAllCellsInsteadOfOnlyTheLastChangedCell()
        {
            var owner = Owner(8); var source = Sample();
            source.Cells[1] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Dynamic, new Vector3Int(-2, 0, 0), 2, 11);
            source.Header = new WorldReplicaHeader(1, 2, 0, 1);
            var words = new uint[128]; Assert.That(owner.TryCaptureCommit(source), Is.True);
            Assert.That(owner.TryCopyChunk(Key, words, out var first), Is.True);
            source.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Dynamic, new Vector3Int(-1, 0, 0), 2, 20);
            source.Header = new WorldReplicaHeader(2, 2, 0, 1); Assert.That(owner.TryCaptureCommit(source), Is.True);
            source.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Dynamic, new Vector3Int(-2, 0, 0), 2, 31);
            source.Cells[1] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Dynamic, new Vector3Int(-3, 0, 0), 2, 41);
            source.Header = new WorldReplicaHeader(3, 2, 0, 1); Assert.That(owner.TryCaptureCommit(source), Is.True);
            Assert.That(owner.TryCopyDelta(Key, first.Generation, first.Revision, words, out var merged), Is.True);
            Assert.That(merged.Revision, Is.EqualTo(3)); Assert.That(merged.BaseRevision, Is.EqualTo(1));
            var decoded = new WorldReplicaBuffer(8, 4, 4096);
            Assert.That(WorldChunkPayloadCodec.TryDecode(words, merged.WordCount, Key, merged.ServerTick, decoded), Is.True);
            Assert.That(decoded.Header.CellCount, Is.EqualTo(2));
            Assert.That(decoded.Cells[0].Cell, Is.EqualTo(new Vector3Int(-3, 0, 0))); Assert.That(decoded.Cells[0].Quantity, Is.EqualTo(41));
            Assert.That(decoded.Cells[1].Cell, Is.EqualTo(new Vector3Int(-2, 0, 0))); Assert.That(decoded.Cells[1].Quantity, Is.EqualTo(31));
        }

        [Test] public void CoalescedAfterImageChecksItsActualDestinationSizeBeforeCopyingAnything()
        {
            var owner = Owner(8); var source = Sample(); var first = new uint[128];
            Assert.That(owner.TryCaptureCommit(source), Is.True); Assert.That(owner.TryCopyChunk(Key, first, out var original), Is.True);
            source.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Dynamic, new Vector3Int(-1, 0, 0), 2, 20);
            source.Header = new WorldReplicaHeader(2, 1, 0, 1); Assert.That(owner.TryCaptureCommit(source), Is.True);
            source.Cells[1] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Dynamic, new Vector3Int(-2, 0, 0), 2, 30);
            source.Header = new WorldReplicaHeader(3, 2, 0, 1); Assert.That(owner.TryCaptureCommit(source), Is.True);
            var small = new uint[18]; for (int i = 0; i < small.Length; i++) small[i] = 123;
            Assert.That(owner.TryCopyDelta(Key, original.Generation, original.Revision, small, out _), Is.False);
            Assert.That(owner.LastFailure, Is.EqualTo(WorldCommitOwnerFailure.DestinationCapacity));
            foreach (uint word in small) Assert.That(word, Is.EqualTo(123));
        }

        [Test] public void CoalescedDeltaKeepsTheKeyCommitStampAfterAnUnrelatedGlobalCommit()
        {
            var owner = Owner(8); var source = Sample(); var words = new uint[128];
            Assert.That(owner.TryCaptureCommit(source), Is.True); Assert.That(owner.TryCopyChunk(Key, words, out var original), Is.True);
            for (uint tick = 2; tick <= 4; tick++)
            {
                source.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                    WorldMaterialStorage.Dynamic, new Vector3Int(-1, 0, 0), 2, tick * 10);
                source.Header = new WorldReplicaHeader(tick, 1, 0, 1); Assert.That(owner.TryCaptureCommit(source), Is.True);
            }
            Assert.That(owner.TryGetCurrentHeader(Key, out var actualCommit), Is.True);
            source.Cells[1] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                WorldMaterialStorage.Dynamic, new Vector3Int(5, 0, 0), 2, 70);
            source.Header = new WorldReplicaHeader(5, 2, 0, 1); Assert.That(owner.TryCaptureCommit(source), Is.True);
            Assert.That(owner.CommitSequence, Is.GreaterThan(actualCommit.SnapshotId));
            Assert.That(owner.TryCopyDelta(Key, original.Generation, original.Revision, words, out var merged), Is.True);
            Assert.That(merged.Revision, Is.EqualTo(actualCommit.Revision)); Assert.That(merged.BaseRevision, Is.EqualTo(original.Revision));
            Assert.That(merged.SnapshotId, Is.EqualTo(actualCommit.SnapshotId)); Assert.That(merged.ServerTick, Is.EqualTo(actualCommit.ServerTick));
            Assert.That(merged.Checksum, Is.EqualTo(actualCommit.Checksum));
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
        [Test] public void RetiringInactiveChunkCompactsLastPayloadWithoutChangingCommittedFacts()
        {
            var o = new WorldReplicationCommitOwner(7, 3, 8, 4, 16, 128, 100000);
            var b = Sample();
            var movedKey = new WorldChunkKey(WorldMaterialDomain.Liquid, 1, 0, 0);
            b.Cells[1] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic,
                new Vector3Int(4, 0, 0), 2, 23);
            b.Header = new WorldReplicaHeader(1, 2, 0, 1);
            Assert.That(o.TryCaptureCommit(b), Is.True);
            var frozen = new uint[128];
            Assert.That(o.TryCopyChunk(Key, frozen, out var original), Is.True);
            b.Cells[0] = b.Cells[1]; b.Header = new WorldReplicaHeader(2, 1, 0, 1);
            Assert.That(o.TryCaptureCommit(b), Is.True);
            Assert.That(o.TryCopyChunk(movedKey, frozen, out var movedBefore), Is.True);
            var frozenBefore = (uint[])frozen.Clone();
            ulong sequence = o.CommitSequence, history = o.HistoryRecordsWritten;

            // 协调器先完成 Peer/Leave 栅栏，Core 只执行独立的已提交 metadata 退休。
            // 它不能把末槽的真实数量、Generation 或 payload 身份留在旧数组索引。
            Assert.That(Retire(o, 1, out int movedFrom), Is.True);
            Assert.That(movedFrom, Is.EqualTo(2)); Assert.That(o.ChunkCount, Is.EqualTo(2));
            Assert.That(o.GetChunkHeader(1).Key, Is.EqualTo(movedKey));
            Assert.That(o.TryCopyChunk(movedKey, frozen, out var movedAfter), Is.True);
            Assert.That(movedAfter.Generation, Is.EqualTo(movedBefore.Generation));
            Assert.That(movedAfter.Revision, Is.EqualTo(movedBefore.Revision));
            Assert.That(movedAfter.Checksum, Is.EqualTo(movedBefore.Checksum));
            CollectionAssert.AreEqual(frozenBefore, frozen);
            Assert.That(o.CommitSequence, Is.EqualTo(sequence));
            Assert.That(o.HistoryRecordsWritten, Is.EqualTo(history));
            Assert.That(o.LastCapturedWorldTick, Is.EqualTo(2));
            Assert.That(o.TryCopyChunk(Key, frozen, out _), Is.False);
            Assert.That(o.TryCopyDelta(Key, original.Generation, original.Revision, frozen, out _), Is.False,
                "旧 tombstone 仍可能在 ring 内，但退休地址已不可作为当前 Delta 来源。");
        }
        [Test] public void RetiredTraversalReusesBoundedSlotsAndPreservesLiquidQuantity()
        {
            var o = new WorldReplicationCommitOwner(7, 3, 8, 4, 16, 128, 100000);
            var b = Sample(19); var words = new uint[128];
            var decoded = new WorldReplicaBuffer(8, 4, 4096);
            Assert.That(o.TryCaptureCommit(b), Is.True);
            Assert.That(o.TryCopyChunk(Key, words, out var first), Is.True);
            var previous = Key;
            for (int step = 1; step <= 20; step++)
            {
                var next = new WorldChunkKey(WorldMaterialDomain.Liquid, -1, -step, 0);
                b.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic,
                    new Vector3Int(-1, -4 * step, 0), 2, 19);
                b.Header = new WorldReplicaHeader(step + 1, 1, 0, 1);
                Assert.That(o.TryCaptureCommit(b), Is.True, "单份液体穿越新地址不应耗尽已退休槽。");
                int inactive = Index(o, previous);
                Assert.That(inactive, Is.GreaterThanOrEqualTo(0));
                Assert.That(Retire(o, inactive, out _), Is.True);
                Assert.That(o.ChunkCount, Is.EqualTo(2));
                Assert.That(o.TryCopyChunk(next, words, out var current), Is.True);
                Assert.That(WorldChunkPayloadCodec.TryDecode(words, current.WordCount, next, current.ServerTick, decoded), Is.True);
                Assert.That(decoded.Header.CellCount, Is.EqualTo(1));
                Assert.That(decoded.Cells[0].Quantity, Is.EqualTo(19)); previous = next;
            }
            b.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic,
                new Vector3Int(-1, 0, 0), 2, 19);
            b.Header = new WorldReplicaHeader(22, 1, 0, 1);
            Assert.That(o.TryCaptureCommit(b), Is.True);
            Assert.That(o.TryCopyChunk(Key, words, out var recreated), Is.True);
            Assert.That(recreated.Generation, Is.GreaterThan(first.Generation));
            Assert.That(o.TryCopyDelta(Key, first.Generation, first.Revision, words, out _), Is.False);
        }
        [Test] public void FailedCaptureNeverRetiresTombstoneAsAnImplicitSideEffect()
        {
            var o = new WorldReplicationCommitOwner(7, 3, 8, 4, 16, 128, 100000);
            var b = Sample();
            b.Cells[1] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic,
                new Vector3Int(4, 0, 0), 2, 23);
            b.Header = new WorldReplicaHeader(1, 2, 0, 1); Assert.That(o.TryCaptureCommit(b), Is.True);
            b.Cells[0] = b.Cells[1]; b.Header = new WorldReplicaHeader(2, 1, 0, 1);
            Assert.That(o.TryCaptureCommit(b), Is.True);
            ulong sequence = o.CommitSequence, history = o.HistoryRecordsWritten;
            var tombstone = o.GetChunkHeader(1);
            b.Cells[1] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic,
                new Vector3Int(8, 0, 0), 2, 7);
            b.Header = new WorldReplicaHeader(3, 2, 0, 1);
            Assert.That(o.TryCaptureCommit(b), Is.False);
            Assert.That(o.LastFailure, Is.EqualTo(WorldCommitOwnerFailure.Capacity));
            Assert.That(o.ChunkCount, Is.EqualTo(3)); Assert.That(o.GetChunkHeader(1), Is.EqualTo(tombstone));
            Assert.That(o.CommitSequence, Is.EqualTo(sequence)); Assert.That(o.HistoryRecordsWritten, Is.EqualTo(history));
            Assert.That(o.LastCapturedWorldTick, Is.EqualTo(2));
            Assert.That(Retire(o, 1, out _), Is.True);
            Assert.That(o.TryCaptureCommit(b), Is.True);
        }
        [Test] public void ResidentAndInvalidRetirementRejectWithoutChangingPublishedState()
        {
            var o = Owner(); var b = Sample(); Assert.That(o.TryCaptureCommit(b), Is.True);
            ulong sequence = o.CommitSequence, history = o.HistoryRecordsWritten;
            var first = o.GetChunkHeader(0); var second = o.GetChunkHeader(1);
            foreach (int invalid in new[] { -1, 0, 1, 2 })
            {
                Assert.That(Retire(o, invalid, out int movedFrom), Is.False);
                Assert.That(movedFrom, Is.EqualTo(-1)); Assert.That(o.ChunkCount, Is.EqualTo(2));
                Assert.That(o.GetChunkHeader(0), Is.EqualTo(first)); Assert.That(o.GetChunkHeader(1), Is.EqualTo(second));
                Assert.That(o.CommitSequence, Is.EqualTo(sequence)); Assert.That(o.HistoryRecordsWritten, Is.EqualTo(history));
            }
            b.Header = new WorldReplicaHeader(2, 0, 0, 1); Assert.That(o.TryCaptureCommit(b), Is.True);
            Assert.That(Retire(o, 1, out int lastMove), Is.True); Assert.That(lastMove, Is.EqualTo(-1));
        }
        private static int Index(WorldReplicationCommitOwner owner, WorldChunkKey key)
        {
            for (int i = 0; i < owner.ChunkCount; i++) if (owner.GetChunkHeader(i).Key == key) return i;
            return -1;
        }
        private static bool Retire(WorldReplicationCommitOwner owner, int index, out int movedFromIndex)
        {
            // 缺少新 API 应形成可读 RED，而不是阻止整套 Unity 测试编译。
            var method = typeof(WorldReplicationCommitOwner).GetMethod("TryRetireInactiveChunk",
                new[] { typeof(int), typeof(int).MakeByRefType() });
            Assert.That(method, Is.Not.Null, "缺少带末槽搬动结果的显式 Chunk 退休 API。");
            object[] arguments = { index, -1 };
            bool result = (bool)method.Invoke(owner, arguments); movedFromIndex = (int)arguments[1]; return result;
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
