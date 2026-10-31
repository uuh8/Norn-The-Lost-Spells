using NUnit.Framework;
using UnityEngine;

namespace Game.Core.Tests
{
    public sealed class WorldChunkReplicaAssemblerTests
    {
        private static readonly WorldChunkKey Key = new WorldChunkKey(WorldMaterialDomain.Liquid,0,0,0);
        private static uint[] Payload()
        {
            var w=new uint[11]; w[0]=WorldChunkPayloadCodec.Magic; w[3]=1;
            w[4]=1; w[5]=1; w[9]=WorldChunkPayloadCodec.FloatWord(1); w[10]=4;
            return w;
        }
        private static WorldChunkPayloadHeader Header(uint[] w,ulong id=1,ulong revision=1,ulong baseRevision=0)
            => new WorldChunkPayloadHeader(7,id,Key,1,baseRevision,revision,9,w.Length,WorldChunkPayloadCodec.ComputeChecksum(w,w.Length),true);
        [Test] public void OutOfOrderPagesPublishOnlyAfterCompletionAndHighIdCannotPreempt()
        {
            var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,8,30); var w=Payload(); var h=Header(w);
            Assert.That(a.TryBegin(h,0),Is.True);
            Assert.That(a.TryReceivePage(Header(w,ulong.MaxValue),0,new uint[8],8,1),Is.False);
            var last=new uint[]{w[8],w[9],w[10]}; Assert.That(a.TryReceivePage(h,1,last,3,1),Is.True); Assert.That(a.Installed,Is.False);
            Assert.That(a.TryReceivePage(h,0,w,8,1),Is.True); Assert.That(a.Installed,Is.True); Assert.That(a.Header.Revision,Is.EqualTo(1));
            Assert.That(a.GetWord(9),Is.EqualTo(w[9]));
        }
        [Test] public void ChecksumFailureAndMissingBasePreserveInstalledData()
        {
            var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,64,30); var w=Payload(); var h=Header(w);
            Assert.That(a.TryBegin(h,0),Is.True); Assert.That(a.TryReceivePage(h,0,w,w.Length,1),Is.True);
            var bad=Header(w,2,2,1); Assert.That(a.TryBegin(bad,2),Is.True); w[10]=5;
            Assert.That(a.TryReceivePage(bad,0,w,w.Length,3),Is.False); Assert.That(a.Header.Revision,Is.EqualTo(1));
            Assert.That(a.TryBegin(Header(w,3,4,3),4),Is.False); Assert.That(a.Header.Revision,Is.EqualTo(1));
            Assert.That(a.TryBegin(Header(w,4,2,1),4),Is.True); Assert.That(a.TryReceivePage(Header(w,4,2,1),0,w,w.Length,5),Is.True);
            Assert.That(a.Header.Revision,Is.EqualTo(2));
        }
        [Test] public void ConflictingDuplicateAndTimeoutNeverInstallPartialData()
        {
            var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,8,3); var w=Payload(); var h=Header(w);
            Assert.That(a.TryBegin(h,0),Is.True); Assert.That(a.TryReceivePage(h,0,w,8,1),Is.True);
            w[0]++; Assert.That(a.TryReceivePage(h,0,w,8,1),Is.False); Assert.That(a.Installed,Is.False);
            a.CancelPending(); Assert.That(a.TryBegin(h,2),Is.True);
            Assert.That(a.TryReceivePage(h,0,w,8,6),Is.False); Assert.That(a.Installed,Is.False);
        }
        [Test] public void DuplicatePageIsIdempotentAndOldWorldCannotEstablishCandidate()
        {
            var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,8,30);var w=Payload();var h=Header(w);
            var wrong=new WorldChunkPayloadHeader(8,1,Key,1,0,1,1,w.Length,h.Checksum,true);
            Assert.That(a.TryBegin(wrong,0),Is.False);Assert.That(a.TryBegin(h,0),Is.True);
            Assert.That(a.TryReceivePage(h,0,w,8,1),Is.True);Assert.That(a.TryReceivePage(h,0,w,8,2),Is.True);Assert.That(a.Installed,Is.False);
            Assert.That(a.TryReceivePage(h,1,new[]{w[8],w[9],w[10]},3,3),Is.True);Assert.That(a.Installed,Is.True);
        }
        [Test] public void DeletionRequiresNewGenerationBeforeRecreation()
        {
            var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,64,30);var w=Payload();var h=Header(w);
            Assert.That(a.TryBegin(h,0),Is.True);Assert.That(a.TryReceivePage(h,0,w,w.Length,1),Is.True);
            var empty=new uint[0];var remove=new WorldChunkPayloadHeader(7,2,Key,1,1,2,10,0,WorldChunkPayloadCodec.ComputeChecksum(empty,0),false);
            Assert.That(a.TryBegin(remove,2),Is.True);Assert.That(a.TryReceivePage(remove,0,empty,0,3),Is.True);Assert.That(a.Header.IsResident,Is.False);
            Assert.That(a.TryBegin(Header(w,3,3),4),Is.False);
            var rebuild=new WorldChunkPayloadHeader(7,4,Key,2,0,1,11,w.Length,h.Checksum,true);
            Assert.That(a.TryBegin(rebuild,4),Is.True);Assert.That(a.TryReceivePage(rebuild,0,w,w.Length,5),Is.True);Assert.That(a.Header.Generation,Is.EqualTo(2));
        }
        [Test] public void InvalidPayloadWithValidChecksumDoesNotBlockLaterLowerRevision()
        {
            var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,64,30);var w=Payload();w[10]=0;var bad=Header(w,ulong.MaxValue,ulong.MaxValue);
            Assert.That(a.TryBegin(bad,0),Is.True);Assert.That(a.TryReceivePage(bad,0,w,w.Length,1),Is.False);Assert.That(a.Installed,Is.False);
            w[10]=4;var good=Header(w);Assert.That(a.TryBegin(good,2),Is.True);Assert.That(a.TryReceivePage(good,0,w,w.Length,3),Is.True);Assert.That(a.Header.Revision,Is.EqualTo(1));
        }
        /// <summary>真实有界小Store；容量拒绝在任何写入之前，不用mock调用次数冒充原子状态断言。</summary>
        private sealed class SmallAtomicStore : IWorldChunkPayloadInstaller
        {
            private readonly uint[] _words=new uint[11];
            public System.Action BeforeInstall;
            public WorldChunkPayloadHeader Header {get;private set;}
            public uint FirstWord=>_words[0];
            public bool TryInstall(WorldChunkPayloadHeader header,uint[] words,int count)
            {
                BeforeInstall?.Invoke();
                if(count>_words.Length)return false;
                System.Array.Copy(words,_words,count);Header=header;return true;
            }
        }
        [Test] public void InstallerCapacityRejectionKeepsBothStoresAndAllowsLegalLowerRevision()
        {
            var store=new SmallAtomicStore();var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,64,30,store);
            var w=Payload();var h=Header(w);Assert.That(a.InstallerConfigured,Is.True);
            Assert.That(a.TryBegin(h,0),Is.True);Assert.That(a.TryReceivePage(h,0,w,w.Length,1),Is.True);
            var large=new uint[18];System.Array.Copy(w,large,w.Length);large[1]=1;large[11]=1;large[16]=2;large[17]=10;
            var rejected=Header(large,ulong.MaxValue,ulong.MaxValue);
            Assert.That(a.TryBegin(rejected,2),Is.True);Assert.That(a.TryReceivePage(rejected,0,large,large.Length,3),Is.False);
            Assert.That(a.Header.Revision,Is.EqualTo(1));Assert.That(store.Header.Revision,Is.EqualTo(1));Assert.That(store.FirstWord,Is.EqualTo(WorldChunkPayloadCodec.Magic));
            Assert.That(a.HasPending,Is.False);
            w[9]=WorldChunkPayloadCodec.FloatWord(2);var legal=Header(w,2,2,1);
            Assert.That(a.TryBegin(legal,4),Is.True);Assert.That(a.TryReceivePage(legal,0,w,w.Length,5),Is.True);
            Assert.That(a.Header.Revision,Is.EqualTo(2));Assert.That(store.Header.Revision,Is.EqualTo(2));
        }
        [Test] public void InstallerCannotReenterOrCancelPreparedCandidate()
        {
            var store=new SmallAtomicStore();var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,64,30,store);var w=Payload();var h=Header(w);
            bool refused=false,observedOld=false;
            store.BeforeInstall=()=>
            {
                observedOld=!a.Installed;refused=!a.TryBegin(h,2) && !a.TryReceivePage(h,0,w,w.Length,2);
                a.CancelPending();refused&=a.HasPending && !a.ExpirePending(999);
            };
            Assert.That(a.TryBegin(h,0),Is.True);Assert.That(a.TryReceivePage(h,0,w,w.Length,1),Is.True);
            Assert.That(observedOld && refused,Is.True);Assert.That(a.Header.Revision,Is.EqualTo(1));Assert.That(store.Header.Revision,Is.EqualTo(1));
        }
        [Test] public void InstallerExceptionLatchesFaultAndNeverAdvancesVersion()
        {
            var store=new SmallAtomicStore();var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,64,30,store);var w=Payload();var h=Header(w);
            var failure=new System.InvalidOperationException("test installer fault");store.BeforeInstall=()=>throw failure;
            Assert.That(a.TryBegin(h,0),Is.True);Assert.That(a.TryReceivePage(h,0,w,w.Length,1),Is.False);
            Assert.That(a.Installed,Is.False);Assert.That(a.HasPending,Is.False);Assert.That(a.IsInstallerFaulted,Is.True);Assert.That(a.InstallerException,Is.SameAs(failure));
            store.BeforeInstall=null;Assert.That(a.TryBegin(h,2),Is.False);
        }
        [Test] public void UnusedPoolSlotCanBindButInstalledSlotCannotChangeChunk()
        {
            var method=typeof(WorldChunkReplicaAssembler).GetMethod("TryBindUnusedKey");Assert.That(method,Is.Not.Null);
            var a=new WorldChunkReplicaAssembler(7,new WorldChunkKey(WorldMaterialDomain.Gas,8,0,0),3,64,8,64,30);
            Assert.That((bool)method.Invoke(a,new object[]{Key}),Is.True);
            var w=Payload();var h=Header(w);Assert.That(a.TryBegin(h,0),Is.True);Assert.That(a.TryReceivePage(h,0,w,w.Length,1),Is.True);
            Assert.That((bool)method.Invoke(a,new object[]{new WorldChunkKey(WorldMaterialDomain.Gas,9,0,0)}),Is.False);Assert.That(a.Key,Is.EqualTo(Key));
        }
        [Test] public void ConnectionResetCancelsCandidateWithoutResettingInstalledWorldRevision()
        {
            var method=typeof(WorldChunkReplicaAssembler).GetMethod("ResetConnectionEpoch");Assert.That(method,Is.Not.Null);
            var a=new WorldChunkReplicaAssembler(7,Key,3,64,8,64,30);var w=Payload();var h=Header(w);
            Assert.That(a.TryBegin(h,0),Is.True);Assert.That(a.TryReceivePage(h,0,w,w.Length,1),Is.True);
            Assert.That(a.TryBegin(Header(w,2,2,1),2),Is.True);method.Invoke(a,new object[]{4UL});
            Assert.That(a.HasPending,Is.False);Assert.That(a.Header.Revision,Is.EqualTo(1));Assert.That(a.ConnectionEpoch,Is.EqualTo(4));
        }
    }
}
