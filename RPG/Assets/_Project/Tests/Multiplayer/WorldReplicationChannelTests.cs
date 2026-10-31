using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    public sealed class WorldReplicationChannelTests
    {
        private static readonly WorldChunkKey Key=new WorldChunkKey(WorldMaterialDomain.Liquid,0,0,0);
        private static WorldReplicationCommitOwner Owner(int cells=1)
        {
            var owner=new WorldReplicationCommitOwner(7,8,200,4,8,2048,1000000);
            var source=new WorldReplicaBuffer(200,4,1000000);source.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,1,16);
            for(int i=0;i<cells;i++)source.Cells[i]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,new Vector3Int(i%16,i/16,0),2,10);
            source.Header=new WorldReplicaHeader(1,cells,0,1);Assert.That(owner.TryCaptureCommit(source),Is.True);return owner;
        }
        [Test] public void BaselineLongerThanFourPagesContinuesBeforeInstallAck()
        {
            var p=new WorldReplicationPeerState(8,2048,160,18000,3600);p.Reset(10,3);var o=Owner(120);
            Assert.That(p.TryFreeze(o,Key,0),Is.True);var page=new uint[160];int pages=0;
            for(int round=0;round<6;round++)
            {
                p.BeginRound(round*0.1);
                while(p.TryPeekPage(page,out _,out _)){Assert.That(p.TryMarkPageSent(round*0.1,900),Is.True);pages++;if(p.PagesSentThisRound==2)break;}
            }
            Assert.That(pages,Is.GreaterThan(4));Assert.That(p.AwaitingInstallAck,Is.True);
            var h=p.PendingHeader;Assert.That(p.TryAcknowledge(10,3,h.Key,h.Generation,h.Revision,h.SnapshotId,h.Checksum),Is.True);
            Assert.That(p.HasPending,Is.False);Assert.That(p.TryFreeze(o,Key,1),Is.False);
        }
        [Test] public void ReconnectionRejectsOldAckAndRequiresNewBaseline()
        {
            var p=new WorldReplicationPeerState(8,2048,160,18000,3600);var o=Owner();p.Reset(10,3);p.BeginRound(0);
            Assert.That(p.TryFreeze(o,Key,0),Is.True);Assert.That(p.TryMarkPageSent(0,200),Is.True);var old=p.PendingHeader;
            p.Reset(11,4);Assert.That(p.TryFreeze(o,Key,1),Is.True);Assert.That(p.PendingHeader.BaseRevision,Is.Zero);
            Assert.That(p.TryAcknowledge(10,3,old.Key,old.Generation,old.Revision,old.SnapshotId,old.Checksum),Is.False);
            Assert.That(p.HasPending,Is.True);Assert.That(p.TryMarkPageSent(1,200),Is.True);
            Assert.That(p.TryAcknowledge(11,4,old.Key,old.Generation,old.Revision,old.SnapshotId,old.Checksum),Is.True);
        }
        [Test] public void BurstAndTwoPageRoundLimitNeverAdvanceUnsentPage()
        {
            var p=new WorldReplicationPeerState(8,2048,160,900,900);p.Reset(10,3);var o=Owner(120);p.BeginRound(0);
            Assert.That(p.TryFreeze(o,Key,0),Is.True);Assert.That(p.TryMarkPageSent(0,900),Is.True);
            int cursor=p.PageCursor;Assert.That(p.TryMarkPageSent(0,900),Is.False);Assert.That(p.PageCursor,Is.EqualTo(cursor));
            p.BeginRound(1);Assert.That(p.TryMarkPageSent(1,900),Is.True);
        }
        [Test] public void AckBeforeCompleteAndUnrelatedHighIdCannotClearFrozenPayload()
        {
            var p=new WorldReplicationPeerState(8,2048,160,18000,3600);p.Reset(10,3);var o=Owner(120);p.BeginRound(0);
            Assert.That(p.TryFreeze(o,Key,0),Is.True);var h=p.PendingHeader;
            Assert.That(p.TryAcknowledge(10,3,h.Key,h.Generation,h.Revision,ulong.MaxValue,h.Checksum),Is.False);
            Assert.That(p.TryAcknowledge(10,3,h.Key,h.Generation,h.Revision,h.SnapshotId,h.Checksum),Is.False);
            Assert.That(p.HasPending,Is.True);Assert.That(p.PendingHeader.Equals(h),Is.True);
        }
        [Test] public void RetransmitUsesSameFrozenWordsAndSnapshot()
        {
            var p=new WorldReplicationPeerState(8,2048,160,18000,3600);p.Reset(10,3);var o=Owner();p.BeginRound(0);Assert.That(p.TryFreeze(o,Key,0),Is.True);
            var h=p.PendingHeader;Assert.That(p.TryMarkPageSent(0,300),Is.True);Assert.That(p.RestartTimedOutTransfer(20,10),Is.True);
            Assert.That(p.PageCursor,Is.Zero);Assert.That(p.PendingHeader.Equals(h),Is.True);
        }
    }
}
