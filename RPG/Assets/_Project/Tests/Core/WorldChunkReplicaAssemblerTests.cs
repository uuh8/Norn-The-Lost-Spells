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

        // 以下只补充原两项API的生命周期边界，不替换/放宽前面的既有断言。
        // Reflection用于方法尚不存在时得到明确RED；断言观察真实分页与版本行为，不依赖mock调用次数。
        private static bool BindUnused(WorldChunkReplicaAssembler assembler, WorldChunkKey key)
        {
            var method = typeof(WorldChunkReplicaAssembler).GetMethod("TryBindUnusedKey");
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(assembler, new object[] { key });
        }

        private static bool ResetEpoch(WorldChunkReplicaAssembler assembler, ulong epoch)
        {
            var method = typeof(WorldChunkReplicaAssembler).GetMethod("ResetConnectionEpoch");
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(assembler, new object[] { epoch });
        }

        [Test] public void UnusedBindingRejectsInvalidDomainAndPendingTransferWithoutLosingPages()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 8, 30);
            Assert.That(BindUnused(a, new WorldChunkKey((WorldMaterialDomain)255, 0, 0, 0)), Is.False);
            Assert.That(a.Key, Is.EqualTo(Key));
            var w = Payload(); var h = Header(w);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, 8, 1), Is.True);
            Assert.That(BindUnused(a, new WorldChunkKey(WorldMaterialDomain.Gas, 8, 0, 0)), Is.False);
            Assert.That(a.Key, Is.EqualTo(Key)); Assert.That(a.HasPending, Is.True);
            Assert.That(a.TryReceivePage(h, 1, new[] { w[8], w[9], w[10] }, 3, 2), Is.True);
            Assert.That(a.Installed, Is.True);
        }

        [Test] public void ZeroAndOlderConnectionEpochPreservePendingTransfer()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 8, 30);
            var w = Payload(); var h = Header(w);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, 8, 1), Is.True);
            Assert.That(ResetEpoch(a, 0), Is.False); Assert.That(ResetEpoch(a, 2), Is.False);
            Assert.That(a.ConnectionEpoch, Is.EqualTo(3)); Assert.That(a.HasPending, Is.True);
            Assert.That(a.TryReceivePage(h, 1, new[] { w[8], w[9], w[10] }, 3, 2), Is.True);
            Assert.That(a.Installed, Is.True);
        }

        [Test] public void SameConnectionEpochDoesNotRestartOrDiscardPartialTransfer()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 8, 30);
            var w = Payload(); var h = Header(w);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, 8, 1), Is.True);
            Assert.That(ResetEpoch(a, 3), Is.True); Assert.That(a.HasPending, Is.True);
            Assert.That(a.TryReceivePage(h, 1, new[] { w[8], w[9], w[10] }, 3, 2), Is.True);
            Assert.That(a.Installed, Is.True);
        }

        [Test] public void NewConnectionEpochRequiresAllPagesAgainBeforeInstallation()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 8, 30);
            var w = Payload(); var h = Header(w); var last = new[] { w[8], w[9], w[10] };
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, 8, 1), Is.True);
            Assert.That(ResetEpoch(a, 4), Is.True); Assert.That(a.HasPending, Is.False);
            Assert.That(a.TryReceivePage(h, 1, last, 3, 2), Is.False);
            var current = Header(w, 2);
            Assert.That(a.TryBegin(current, 3), Is.True);
            Assert.That(a.TryReceivePage(current, 1, last, 3, 4), Is.True);
            Assert.That(a.Installed, Is.False, "新连接不能复用上一候选的已收页标志。");
            Assert.That(a.TryReceivePage(current, 0, w, 8, 5), Is.True);
            Assert.That(a.Installed, Is.True); Assert.That(a.ConnectionEpoch, Is.EqualTo(4));
        }

        [Test] public void ReconnectionPreservesInstalledPayloadAndRejectsVersionRewindOrGap()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30);
            var w = Payload(); var installed = Header(w, 1, 5);
            Assert.That(a.TryBegin(installed, 0), Is.True);
            Assert.That(a.TryReceivePage(installed, 0, w, w.Length, 1), Is.True);
            Assert.That(ResetEpoch(a, 4), Is.True);
            Assert.That(a.Installed, Is.True); Assert.That(a.Header, Is.EqualTo(installed));
            Assert.That(a.GetWord(9), Is.EqualTo(w[9]));
            Assert.That(a.TryBegin(Header(w, 2, 4), 2), Is.False);
            Assert.That(a.TryBegin(Header(w, 3, 9, 8), 3), Is.False);
            var next = Header(w, 4, 6, 5);
            Assert.That(a.TryBegin(next, 4), Is.True);
            Assert.That(a.TryReceivePage(next, 0, w, w.Length, 5), Is.True);
            Assert.That(a.Header.Revision, Is.EqualTo(6));
        }

        [Test] public void InstallerCannotChangeKeyOrConnectionEpochDuringAtomicPublication()
        {
            var store = new SmallAtomicStore();
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30, store);
            var w = Payload(); var h = Header(w); bool refused = false;
            store.BeforeInstall = () =>
            {
                refused = !BindUnused(a, new WorldChunkKey(WorldMaterialDomain.Gas, 8, 0, 0))
                    && !ResetEpoch(a, 4) && !ResetEpoch(a, 3);
                refused &= a.Key == Key && a.ConnectionEpoch == 3 && a.HasPending;
            };
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.True);
            Assert.That(refused, Is.True); Assert.That(a.Header, Is.EqualTo(h));
            Assert.That(store.Header, Is.EqualTo(h)); Assert.That(a.ConnectionEpoch, Is.EqualTo(3));
        }

        [Test] public void FaultedInstallerCannotBeRevivedByBindingOrConnectionReset()
        {
            var store = new SmallAtomicStore();
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30, store);
            var failure = new System.InvalidOperationException("lifecycle installer fault");
            store.BeforeInstall = () => throw failure;
            var w = Payload(); var h = Header(w);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.False);
            Assert.That(ResetEpoch(a, 4), Is.False);
            Assert.That(BindUnused(a, new WorldChunkKey(WorldMaterialDomain.Gas, 8, 0, 0)), Is.False);
            Assert.That(a.IsInstallerFaulted, Is.True); Assert.That(a.InstallerException, Is.SameAs(failure));
            Assert.That(a.ConnectionEpoch, Is.EqualTo(3)); Assert.That(a.Key, Is.EqualTo(Key));
            store.BeforeInstall = null; Assert.That(a.TryBegin(h, 2), Is.False);
        }

        [Test] public void ConnectionEpochMaximumNeverWrapsToAnOlderIdentity()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, ulong.MaxValue - 1, 64, 8, 64, 30);
            Assert.That(ResetEpoch(a, ulong.MaxValue), Is.True);
            Assert.That(ResetEpoch(a, 1), Is.False); Assert.That(ResetEpoch(a, 0), Is.False);
            Assert.That(ResetEpoch(a, ulong.MaxValue), Is.True);
            Assert.That(a.ConnectionEpoch, Is.EqualTo(ulong.MaxValue));
        }

        // 退出Interest只撤销payload读取资格，不能抹掉历史版本；新订阅由协调器先校验，
        // Assembler只承担候选取消和Baseline恢复。Reflection使缺失API成为行为RED，避免编译失败。
        private static bool ReleaseInterest(WorldChunkReplicaAssembler assembler)
        {
            var method = typeof(WorldChunkReplicaAssembler).GetMethod("TryReleaseInterest");
            Assert.That(method, Is.Not.Null, "Assembler需要显式释放Interest，而非将Installed清零复用历史槽。");
            return (bool)method.Invoke(assembler, null);
        }

        private static bool PayloadReadable(WorldChunkReplicaAssembler assembler)
        {
            var property = typeof(WorldChunkReplicaAssembler).GetProperty("PayloadAvailable");
            Assert.That(property, Is.Not.Null, "已安装版本事实与当前payload读取资格必须分离。");
            return (bool)property.GetValue(assembler);
        }

        [Test] public void InterestReleaseHidesQuantityWordsWithoutErasingInstalledFacts()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30);
            var w = Payload(); var h = Header(w, 1, 5);
            Assert.That(PayloadReadable(a), Is.False);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.True);
            Assert.That(PayloadReadable(a), Is.True); Assert.That(a.GetWord(10), Is.EqualTo(4));

            Assert.That(ReleaseInterest(a), Is.True); Assert.That(ReleaseInterest(a), Is.True);
            Assert.That(PayloadReadable(a), Is.False);
            Assert.That(a.Installed, Is.True); Assert.That(a.Key, Is.EqualTo(Key));
            Assert.That(a.Header, Is.EqualTo(h)); Assert.That(a.ConnectionEpoch, Is.EqualTo(3));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => a.GetWord(10));
            var destination = new uint[11]; destination[10] = 99;
            Assert.That(a.TryCopyInstalled(destination, out var copiedHeader), Is.False);
            Assert.That(destination[10], Is.EqualTo(99), "退出后不能向表现方泄漏保留数组中的旧数量。");
            Assert.That(copiedHeader, Is.EqualTo(default(WorldChunkPayloadHeader)));
        }

        [Test] public void ReleasedInstalledSlotStillCannotBindAnotherChunk()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30);
            var w = Payload(); var h = Header(w);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.True);
            Assert.That(ReleaseInterest(a), Is.True);
            Assert.That(BindUnused(a, new WorldChunkKey(WorldMaterialDomain.Gas, 8, 0, 0)), Is.False);
            Assert.That(a.Installed, Is.True); Assert.That(a.Key, Is.EqualTo(Key));
            Assert.That(a.Header, Is.EqualTo(h)); Assert.That(PayloadReadable(a), Is.False);
        }

        [Test] public void InterestReleaseDiscardsHighCandidateBeforeCurrentBaselineRestartsAllPages()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 8, 30);
            var w = Payload(); var h = Header(w, 1, 5); var last = new[] { w[8], w[9], w[10] };
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, 8, 1), Is.True);
            Assert.That(a.TryReceivePage(h, 1, last, 3, 2), Is.True);
            var high = new WorldChunkPayloadHeader(7, ulong.MaxValue, Key, ulong.MaxValue, 0,
                ulong.MaxValue, 10, w.Length, h.Checksum, true);
            Assert.That(a.TryBegin(high, 3), Is.True);
            Assert.That(a.TryReceivePage(high, 0, w, 8, 4), Is.True);
            Assert.That(ReleaseInterest(a), Is.True); Assert.That(a.HasPending, Is.False);
            Assert.That(a.TryReceivePage(high, 1, last, 3, 5), Is.False);
            Assert.That(a.Header, Is.EqualTo(h));

            var current = Header(w, 2, 5);
            Assert.That(a.TryBegin(current, 6), Is.True);
            Assert.That(a.TryReceivePage(current, 1, last, 3, 7), Is.True);
            Assert.That(PayloadReadable(a), Is.False, "新候选不能复用被释放候选的已收页标记。");
            Assert.That(a.TryReceivePage(current, 0, w, 8, 8), Is.True);
            Assert.That(PayloadReadable(a), Is.True); Assert.That(a.Header, Is.EqualTo(current));
        }

        [Test] public void InterestReleaseAlsoCancelsAnUninstalledPartialCandidate()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 8, 30);
            var w = Payload(); var high = Header(w, ulong.MaxValue, ulong.MaxValue);
            Assert.That(a.TryBegin(high, 0), Is.True);
            Assert.That(a.TryReceivePage(high, 0, w, 8, 1), Is.True);
            Assert.That(ReleaseInterest(a), Is.True); Assert.That(ReleaseInterest(a), Is.True);
            Assert.That(a.Installed, Is.False); Assert.That(a.HasPending, Is.False);
            Assert.That(PayloadReadable(a), Is.False);
            var current = Header(w);
            Assert.That(a.TryBegin(current, 2), Is.True);
            Assert.That(a.TryReceivePage(current, 1, new[] { w[8], w[9], w[10] }, 3, 3), Is.True);
            Assert.That(a.Installed, Is.False);
            Assert.That(a.TryReceivePage(current, 0, w, 8, 4), Is.True);
            Assert.That(a.Installed, Is.True); Assert.That(PayloadReadable(a), Is.True);
        }

        [Test] public void ReleasedPayloadRequiresBaselineBeforeAcceptingAnotherDelta()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30);
            var w = Payload(); var h = Header(w, 1, 5);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.True);
            Assert.That(ReleaseInterest(a), Is.True);
            Assert.That(a.TryBegin(Header(w, 2, 6, 5), 2), Is.False);
            Assert.That(a.LastFailure, Is.EqualTo(WorldReplicaAssemblyFailure.MissingRevision));
            Assert.That(a.HasPending, Is.False); Assert.That(a.Header, Is.EqualTo(h));
            var baseline = Header(w, 3, 5);
            Assert.That(a.TryBegin(baseline, 3), Is.True);
            Assert.That(PayloadReadable(a), Is.False);
            Assert.That(a.TryReceivePage(baseline, 0, w, w.Length, 4), Is.True);
            var delta = Header(w, 4, 6, 5);
            Assert.That(a.TryBegin(delta, 5), Is.True);
            Assert.That(a.TryReceivePage(delta, 0, w, w.Length, 6), Is.True);
            Assert.That(a.Header.Revision, Is.EqualTo(6));
        }

        [Test] public void ReleasedPayloadRetainsGenerationRevisionAndSameVersionContentFence()
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30);
            var w = Payload(); var h = new WorldChunkPayloadHeader(7, 1, Key, 2, 0, 5, 9,
                w.Length, WorldChunkPayloadCodec.ComputeChecksum(w, w.Length), true);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.True);
            Assert.That(ReleaseInterest(a), Is.True);
            Assert.That(a.TryBegin(Header(w, 2, 99), 2), Is.False);
            Assert.That(a.LastFailure, Is.EqualTo(WorldReplicaAssemblyFailure.StaleVersion));
            var older = new WorldChunkPayloadHeader(7, 3, Key, 2, 0, 4, 10, w.Length, h.Checksum, true);
            Assert.That(a.TryBegin(older, 3), Is.False);
            Assert.That(a.LastFailure, Is.EqualTo(WorldReplicaAssemblyFailure.StaleVersion));
            w[10] = 5;
            var conflict = new WorldChunkPayloadHeader(7, 4, Key, 2, 0, 5, 11,
                w.Length, WorldChunkPayloadCodec.ComputeChecksum(w, w.Length), true);
            Assert.That(a.TryBegin(conflict, 4), Is.False);
            Assert.That(a.LastFailure, Is.EqualTo(WorldReplicaAssemblyFailure.Payload));
            Assert.That(a.Header, Is.EqualTo(h)); Assert.That(a.Installed, Is.True);
            Assert.That(PayloadReadable(a), Is.False); Assert.That(a.HasPending, Is.False);
        }

        [Test] public void SameVersionBaselineRunsActualInstallerBeforeRestoringPayloadReads()
        {
            var store = new SmallAtomicStore();
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30, store);
            var w = Payload(); var h = Header(w, 1, 5);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.True);
            Assert.That(ReleaseInterest(a), Is.True);
            bool observedUnpublished = false;
            store.BeforeInstall = () => observedUnpublished = !PayloadReadable(a) && a.HasPending
                && a.Installed && a.Header.Equals(h) && store.Header.Equals(h);
            var reentered = Header(w, 8, 5);
            Assert.That(a.TryBegin(reentered, 2), Is.True);
            Assert.That(a.TryReceivePage(reentered, 0, w, w.Length, 3), Is.True);
            Assert.That(observedUnpublished, Is.True);
            Assert.That(store.Header, Is.EqualTo(reentered), "真实Store安装新的冻结身份后才恢复Assembler读取。");
            Assert.That(PayloadReadable(a), Is.True); Assert.That(a.Header, Is.EqualTo(reentered));
            Assert.That(a.GetWord(10), Is.EqualTo(4));
            var destination = new uint[11];
            Assert.That(a.TryCopyInstalled(destination, out var copiedHeader), Is.True);
            Assert.That(destination[10], Is.EqualTo(4)); Assert.That(copiedHeader, Is.EqualTo(reentered));
        }

        [Test] public void RejectedInstallerDoesNotRestoreReleasedPayloadOrAdvanceVersion()
        {
            var store = new SmallAtomicStore();
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30, store);
            var w = Payload(); var h = Header(w, 1, 5);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.True);
            Assert.That(ReleaseInterest(a), Is.True);
            var large = new uint[18]; System.Array.Copy(w, large, w.Length);
            large[1] = 1; large[11] = 1; large[16] = 2; large[17] = 10;
            var rejected = Header(large, 2, 6);
            Assert.That(a.TryBegin(rejected, 2), Is.True);
            Assert.That(a.TryReceivePage(rejected, 0, large, large.Length, 3), Is.False);
            Assert.That(a.LastFailure, Is.EqualTo(WorldReplicaAssemblyFailure.InstallRejected));
            Assert.That(a.Header, Is.EqualTo(h)); Assert.That(store.Header, Is.EqualTo(h));
            Assert.That(a.HasPending, Is.False); Assert.That(PayloadReadable(a), Is.False);
            var legal = Header(w, 3, 5);
            Assert.That(a.TryBegin(legal, 4), Is.True);
            Assert.That(a.TryReceivePage(legal, 0, w, w.Length, 5), Is.True);
            Assert.That(PayloadReadable(a), Is.True); Assert.That(a.Header, Is.EqualTo(legal));
        }

        [TestCase(false)] [TestCase(true)]
        public void InstallerCannotReleaseInterestDuringAtomicPublication(bool releaseBeforeInstall)
        {
            var store = new SmallAtomicStore();
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30, store);
            var w = Payload(); var h = Header(w);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.True);
            if (releaseBeforeInstall) Assert.That(ReleaseInterest(a), Is.True);
            bool refused = false, preserved = false;
            store.BeforeInstall = () =>
            {
                refused = !ReleaseInterest(a) && a.LastFailure == WorldReplicaAssemblyFailure.Installing;
                preserved = a.HasPending && a.Installed && a.Key == Key && a.Header.Equals(h)
                    && PayloadReadable(a) == !releaseBeforeInstall;
            };
            var next = Header(w, 2, 2);
            Assert.That(a.TryBegin(next, 2), Is.True);
            Assert.That(a.TryReceivePage(next, 0, w, w.Length, 3), Is.True);
            Assert.That(refused, Is.True); Assert.That(preserved, Is.True);
            Assert.That(a.Header, Is.EqualTo(next)); Assert.That(store.Header, Is.EqualTo(next));
            Assert.That(PayloadReadable(a), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void FaultedInstallerCannotReleaseOrRevivePayload(bool releasedBeforeFault)
        {
            var store = new SmallAtomicStore();
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 64, 30, store);
            var w = Payload(); var h = Header(w);
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, w.Length, 1), Is.True);
            if (releasedBeforeFault) Assert.That(ReleaseInterest(a), Is.True);
            var failure = new System.InvalidOperationException("interest installer fault");
            store.BeforeInstall = () => throw failure;
            var next = Header(w, 2, 2);
            Assert.That(a.TryBegin(next, 2), Is.True);
            Assert.That(a.TryReceivePage(next, 0, w, w.Length, 3), Is.False);
            Assert.That(ReleaseInterest(a), Is.False);
            Assert.That(a.LastFailure, Is.EqualTo(WorldReplicaAssemblyFailure.InstallerFaulted));
            Assert.That(a.IsInstallerFaulted, Is.True); Assert.That(a.InstallerException, Is.SameAs(failure));
            Assert.That(a.Installed, Is.True); Assert.That(a.Header, Is.EqualTo(h));
            Assert.That(PayloadReadable(a), Is.EqualTo(!releasedBeforeFault));
            Assert.That(a.Key, Is.EqualTo(Key)); Assert.That(a.HasPending, Is.False);
            store.BeforeInstall = null;
            Assert.That(a.TryBegin(Header(w, 3, 2), 4), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConnectionEpochResetPreservesExistingPayloadReadability(bool releasedBeforeReset)
        {
            var a = new WorldChunkReplicaAssembler(7, Key, 3, 64, 8, 8, 30);
            var w = Payload(); var h = Header(w, 1, 5); var last = new[] { w[8], w[9], w[10] };
            Assert.That(a.TryBegin(h, 0), Is.True);
            Assert.That(a.TryReceivePage(h, 0, w, 8, 1), Is.True);
            Assert.That(a.TryReceivePage(h, 1, last, 3, 2), Is.True);
            if (releasedBeforeReset) Assert.That(ReleaseInterest(a), Is.True);
            var candidate = Header(w, 2, 9);
            Assert.That(a.TryBegin(candidate, 3), Is.True);
            Assert.That(a.TryReceivePage(candidate, 0, w, 8, 4), Is.True);
            Assert.That(ResetEpoch(a, 4), Is.True);
            Assert.That(a.HasPending, Is.False); Assert.That(a.ConnectionEpoch, Is.EqualTo(4));
            Assert.That(a.Installed, Is.True); Assert.That(a.Header, Is.EqualTo(h));
            Assert.That(PayloadReadable(a), Is.EqualTo(!releasedBeforeReset));
            if (releasedBeforeReset) Assert.Throws<System.ArgumentOutOfRangeException>(() => a.GetWord(10));
            else Assert.That(a.GetWord(10), Is.EqualTo(4));
            Assert.That(a.TryBegin(Header(w, 3, 4), 5), Is.False);
            Assert.That(a.LastFailure, Is.EqualTo(WorldReplicaAssemblyFailure.StaleVersion));
        }
    }
}
