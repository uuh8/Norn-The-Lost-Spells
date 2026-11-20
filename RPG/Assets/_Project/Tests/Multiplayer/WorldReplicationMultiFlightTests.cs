using System;
using System.Collections.Generic;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 锁定同一认证Peer的多Key在途行为；不把多连接、Transport页收据或增加字节预算冒充多Chunk并发。
    /// 只使用真正CommitOwner冻结出的payload，ACK仍需完整Header身份，重传仍应保留原版本和内容。
    /// </summary>
    public sealed class WorldReplicationMultiFlightTests
    {
        private static WorldChunkKey Key(int index)=>new WorldChunkKey(WorldMaterialDomain.Liquid,index,0,0);
        private static WorldReplicationPeerState Peer(int maximum=4,int chunks=8)
        {
            // RED阶段保留源码可编译：旧实现没有capacity参数时走原构造，第二个Key的行为断言会真实失败。
            // 功能落地后选有界capacity构造；不是让缺接口本身替代并发行为验收。
            var constructor=typeof(WorldReplicationPeerState).GetConstructor(new[]{typeof(int),typeof(int),typeof(int),typeof(double),typeof(double),typeof(int)});
            var peer=constructor!=null?(WorldReplicationPeerState)constructor.Invoke(new object[]{chunks,2048,160,18000d,3600d,maximum})
                :new WorldReplicationPeerState(chunks,2048,160,18000,3600);
            peer.Reset(10,3);return peer;
        }
        private static WorldReplicationCommitOwner Owner(out WorldReplicaBuffer source,int chunks=5,int cellsPerChunk=1)
        {
            var owner=new WorldReplicationCommitOwner(7,8,1024,4,16,2048,1000000);
            source=new WorldReplicaBuffer(1024,4,1000000);
            source.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,1,16);
            for(int chunk=0;chunk<chunks;chunk++)for(int cell=0;cell<cellsPerChunk;cell++)
                source.Cells[chunk*cellsPerChunk+cell]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,
                    new Vector3Int(chunk*16+cell%16,cell/16,0),2,10);
            source.Header=new WorldReplicaHeader(1,chunks*cellsPerChunk,0,1);
            Assert.That(owner.TryCaptureCommit(source),Is.True);return owner;
        }
        private static void Acknowledge(WorldReplicationPeerState peer,WorldChunkPayloadHeader header)
            =>Assert.That(peer.TryAcknowledge(10,3,header.Key,header.Generation,header.Revision,header.SnapshotId,header.Checksum),Is.True);
        private static List<WorldChunkPayloadHeader> SendUntilAwaiting(WorldReplicationPeerState peer,double start=0,int charge=200)
        {
            var headers=new List<WorldChunkPayloadHeader>();var seen=new HashSet<WorldChunkKey>();var page=new uint[160];
            for(int round=0;round<100;round++)
            {
                double now=start+round*.02;peer.BeginRound(now);bool sent=false;
                while(peer.TryPeekPage(page,out _,out _))
                {
                    var header=peer.PendingHeader;Assert.That(peer.TryMarkPageSent(now,charge),Is.True);sent=true;
                    if(peer.AwaitingInstallAck && seen.Add(header.Key))headers.Add(header);
                }
                if(!sent)break;
            }
            return headers;
        }

        [Test]
        public void FourDifferentKeysCanFinishSendingBeforeAnyInstallAckAndFifthKeyIsBounded()
        {
            var owner=Owner(out _);var peer=Peer();
            for(int i=0;i<4;i++)Assert.That(peer.TryFreeze(owner,Key(i),0),Is.True,"不同Key不能被前一块的安装ACK串行阻塞："+i);
            Assert.That(peer.TryFreeze(owner,Key(0),0),Is.False,"同Key仍必须串行，不能冻结另一份更新覆盖在途版本。");
            Assert.That(peer.TryFreeze(owner,Key(4),0),Is.False,"最多4份冻结payload，不能因无ACK而增长队列。");
            var completed=SendUntilAwaiting(peer);Assert.That(completed.Count,Is.EqualTo(4));
            Assert.That(peer.HasPending,Is.True,"页已发送不等于安装完成，所有Key仍应等待真实ACK。");
            foreach(var header in completed)Acknowledge(peer,header);
            Assert.That(peer.HasPending,Is.False);
        }

        [Test]
        public void OutOfOrderAckReleasesOnlyItsOwnFrozenKeyAndRejectsForgedOrDuplicateAck()
        {
            var owner=Owner(out var source);var peer=Peer();
            for(int i=0;i<4;i++)Assert.That(peer.TryFreeze(owner,Key(i),0),Is.True);
            var completed=SendUntilAwaiting(peer);Assert.That(completed.Count,Is.EqualTo(4));var last=completed[3];
            Assert.That(peer.TryAcknowledge(11,3,last.Key,last.Generation,last.Revision,last.SnapshotId,last.Checksum),Is.False);
            Assert.That(peer.TryAcknowledge(10,4,last.Key,last.Generation,last.Revision,last.SnapshotId,last.Checksum),Is.False);
            Assert.That(peer.TryAcknowledge(10,3,last.Key,last.Generation,last.Revision,last.SnapshotId,unchecked(last.Checksum+1)),Is.False);
            Acknowledge(peer,last);
            Assert.That(peer.TryAcknowledge(10,3,last.Key,last.Generation,last.Revision,last.SnapshotId,last.Checksum),Is.False);
            Assert.That(peer.HasPending,Is.True);
            // 只让刚ACK的Key再次变动。它可发新版本，其他仍在途的Key继续拒绝重冻。
            source.Cells[3]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,new Vector3Int(48,0,0),2,20);
            source.Header=new WorldReplicaHeader(2,5,0,1);Assert.That(owner.TryCaptureCommit(source),Is.True);
            Assert.That(peer.TryFreeze(owner,Key(0),.1),Is.False);
            Assert.That(peer.TryFreeze(owner,last.Key,.1),Is.True);
            Assert.That(peer.PendingHeader.BaseRevision,Is.EqualTo(last.Revision),"新版本Delta只以该Key真实ACK过的版本为Base。");
            for(int i=2;i>=0;i--)Acknowledge(peer,completed[i]);
            Assert.That(peer.HasPending,Is.True,"最后Key的新版本不能被另一Key的ACK清空。");
        }

        [Test]
        public void FlightsShareTheSameTwoPageRoundLimitAndSingleTokenBucket()
        {
            var owner=Owner(out _,2,120);var peer=Peer();
            Assert.That(peer.TryFreeze(owner,Key(0),0),Is.True);Assert.That(peer.TryFreeze(owner,Key(1),0),Is.True);
            var page=new uint[160];peer.BeginRound(0);
            for(int i=0;i<2;i++){Assert.That(peer.TryPeekPage(page,out _,out _),Is.True);Assert.That(peer.TryMarkPageSent(0,1800),Is.True);}
            Assert.That(peer.PagesSentThisRound,Is.EqualTo(2));Assert.That(peer.Tokens,Is.Zero);
            Assert.That(peer.TryPeekPage(page,out _,out _),Is.False,"每个Flight不能各拿两页，Peer本轮总共仍只有两页。");
            peer.BeginRound(.01);Assert.That(peer.Tokens,Is.EqualTo(180).Within(.000001));
            Assert.That(peer.TryPeekPage(page,out _,out _),Is.True);int cursor=peer.PageCursor;
            Assert.That(peer.TryMarkPageSent(.01,900),Is.False);Assert.That(peer.PageCursor,Is.EqualTo(cursor));
            Assert.That(peer.TryMarkControlSent(.01,900),Is.False,"订阅控制也必须共享同一token桶。");
        }

        [Test]
        public void TimeoutRestartsOnlyTheExpiredKeyWithItsOriginalSnapshotAndWords()
        {
            var owner=Owner(out var source,2);var peer=Peer();var page=new uint[160];
            Assert.That(peer.TryFreeze(owner,Key(0),0),Is.True);peer.BeginRound(0);
            Assert.That(peer.TryPeekPage(page,out int count,out int index),Is.True);var original=peer.PendingHeader;
            var originalWords=new uint[count];Array.Copy(page,originalWords,count);Assert.That(index,Is.Zero);Assert.That(peer.TryMarkPageSent(0,200),Is.True);
            source.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Vector3Int.zero,2,30);
            source.Header=new WorldReplicaHeader(2,2,0,1);Assert.That(owner.TryCaptureCommit(source),Is.True);
            Assert.That(peer.TryFreeze(owner,Key(1),.5),Is.True);peer.BeginRound(.5);
            Assert.That(peer.TryPeekPage(page,out _,out _),Is.True);var younger=peer.PendingHeader;Assert.That(peer.TryMarkPageSent(.5,200),Is.True);
            peer.BeginRound(1);Assert.That(peer.RestartTimedOutTransfer(1,.75),Is.True);
            Assert.That(peer.TryPeekPage(page,out int restartedCount,out int restartedPage),Is.True);
            Assert.That(peer.PendingHeader.Equals(original),Is.True);Assert.That(restartedPage,Is.Zero);Assert.That(restartedCount,Is.EqualTo(count));
            for(int i=0;i<count;i++)Assert.That(page[i],Is.EqualTo(originalWords[i]));
            Assert.That(peer.TryAcknowledge(10,3,original.Key,original.Generation,original.Revision,original.SnapshotId,original.Checksum),Is.False,"重启但尚未重新发完，旧ACK不能清掉重传。");
            Acknowledge(peer,younger);Assert.That(peer.PendingHeader.Equals(original),Is.True,"别Key仍可ACK，不能一起重启或改写旧冻结payload。");
        }

        [Test]
        public void AKeyCannotBeAckedBeforeItsOwnPagesFinishAndBaselineRecoveryIsKeyScoped()
        {
            var owner=Owner(out _,2,120);var peer=Peer();
            Assert.That(peer.TryFreeze(owner,Key(0),0),Is.True);var first=peer.PendingHeader;
            Assert.That(peer.TryFreeze(owner,Key(1),0),Is.True);var second=peer.PendingHeader;
            Assert.That(peer.TryAcknowledge(10,3,first.Key,first.Generation,first.Revision,first.SnapshotId,first.Checksum),Is.False);
            peer.RequireBaseline(first.Key);
            Assert.That(peer.TryForgetReleasedKey(second.Key),Is.False,"尚在途的另一Key不得被恢复或退休误删。");
            Assert.That(peer.TryFreeze(owner,first.Key,0),Is.True);Assert.That(peer.PendingHeader.BaseRevision,Is.Zero);
            var completed=SendUntilAwaiting(peer);Assert.That(completed.Count,Is.EqualTo(2));
            foreach(var header in completed)Acknowledge(peer,header);
            Assert.That(peer.HasPending,Is.False);
        }

        [Test]
        public void DefaultConstructorRetainsTheOriginalSingleFlightContract()
        {
            var owner=Owner(out _,2);var peer=new WorldReplicationPeerState(8,2048,160,18000,3600);peer.Reset(10,3);
            Assert.That(peer.TryFreeze(owner,Key(0),0),Is.True);Assert.That(peer.TryFreeze(owner,Key(1),0),Is.False);
            var completed=SendUntilAwaiting(peer);Assert.That(completed.Count,Is.EqualTo(1));Acknowledge(peer,completed[0]);
            Assert.That(peer.TryFreeze(owner,Key(1),.1),Is.True);
        }
    }
}
