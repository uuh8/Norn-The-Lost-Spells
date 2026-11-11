using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Game.Networking;
using Mirror;
using System;
using System.Reflection;
using Game.NetworkProtocol;

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

        [Test] public void WorldHandshakeRequiresCurrentAuthenticatedIdentityAndCannotReplaceInstalledEpoch()
        {
            Assert.That(NetworkClient.active, Is.False, "不得接管正在运行的Client。");
            var property = typeof(JoinTicketAuthenticator).GetProperty("ClientWorldEpoch");
            var receive = typeof(JoinTicketAuthenticator).GetMethod("OnWorldIdentity", BindingFlags.Instance | BindingFlags.NonPublic);
            var codecType = typeof(JoinTicketAuthenticator).Assembly.GetType("Game.Networking.WorldIdentityCodec");
            var envelopeType = typeof(JoinTicketAuthenticator).Assembly.GetType("Game.Networking.WorldIdentityEnvelope");
            Assert.That(property, Is.Not.Null); Assert.That(receive, Is.Not.Null);
            Assert.That(codecType, Is.Not.Null); Assert.That(envelopeType, Is.Not.Null);
            var previous = NetworkClient.connection;
            var host = new GameObject("Inactive authenticated world handshake fixture"); host.SetActive(false);
            try
            {
                var auth = host.AddComponent<JoinTicketAuthenticator>();
                var identity = new JoinConnectionIdentity(new string('a', 32), new string('b', 32), new string('c', 32), 2, 3, 1, "p7");
                var connection = new NetworkConnectionToServer { isAuthenticated = true, authenticationData = identity };
                typeof(NetworkClient).GetProperty("connection").SetValue(null, connection);
                typeof(JoinTicketAuthenticator).GetField("<ClientIdentity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(auth, identity);
                var codec = Activator.CreateInstance(codecType); var bytes = new byte[256];
                Action<ulong> send = epoch =>
                {
                    object[] args = { identity, epoch, new ArraySegment<byte>(bytes), 0 };
                    Assert.That(codecType.GetMethod("TryEncode").Invoke(codec, args), Is.True);
                    var envelope = Activator.CreateInstance(envelopeType);
                    envelopeType.GetField("Payload").SetValue(envelope, new ArraySegment<byte>(bytes, 0, (int)args[3]));
                    receive.Invoke(auth, new[] { envelope });
                };
                send(17); Assert.That(property.GetValue(auth), Is.EqualTo(0UL), "未完成业务准入不能安装世界身份。");
                typeof(JoinTicketAuthenticator).GetField("<ClientAuthenticated>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(auth, true);
                connection.authenticationData = new object();
                send(17); Assert.That(property.GetValue(auth), Is.EqualTo(0UL), "旧连接身份对象不能给新认证上下文安装编号。");
                connection.authenticationData = identity;
                send(17); Assert.That(property.GetValue(auth), Is.EqualTo(17UL));
                send(17); send(18); Assert.That(property.GetValue(auth), Is.EqualTo(17UL), "重复幂等；不能用更大编号替换存活世界。");
                auth.OnStopClient(); Assert.That(property.GetValue(auth), Is.EqualTo(0UL));
            }
            finally { typeof(NetworkClient).GetProperty("connection").SetValue(null, previous); UnityEngine.Object.DestroyImmediate(host); }
        }
    }

    /// <summary>真实 CommitOwner/Codec/Assembler/ReplicaStore 闭环；Reflection 只让缺失的接线先产生行为 RED。</summary>
    public sealed class WorldReplicationCoordinatorTests
    {
        private static readonly WorldChunkKey TimingKey=new WorldChunkKey(WorldMaterialDomain.Liquid,0,0,0);
        private sealed class SnapshotSource : IWorldReplicationSource
        {
            internal readonly WorldReplicaBuffer Value = new WorldReplicaBuffer(200, 4, 1000000);
            internal SnapshotSource(int cells=120)
            {
                Value.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,1,16);
                for(int i=0;i<cells;i++)Value.Cells[i]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,new Vector3Int(i%16,i/16,0),2,10);
                Value.Header=new WorldReplicaHeader(1,cells,0,1);
            }
            public bool TryCapture(WorldReplicaBuffer destination,out WorldCaptureFailure failure)
            {
                Array.Copy(Value.Geometries,destination.Geometries,1);Array.Copy(Value.Cells,destination.Cells,Value.Header.CellCount);
                destination.Header=Value.Header;failure=default;return true;
            }
        }
        private Type _type;
        // 既有闭环用例每次Server建立一条真实可靠控制序列；每个Client各自记录消费位置，不能共享“已投递”。
        // 新增显式订阅用例不使用这两个集合，仍逐条断言Enter/Leave与数据页的先后关系。
        private readonly System.Collections.Generic.List<WorldPageMessage> _legacyControls=new System.Collections.Generic.List<WorldPageMessage>();
        private readonly System.Collections.Generic.Dictionary<object,int> _legacyDelivered=new System.Collections.Generic.Dictionary<object,int>();
        private object Server(out SnapshotSource source)
        {
            _legacyControls.Clear();_legacyDelivered.Clear();
            _type=typeof(WorldReplicationPeerState).Assembly.GetType("Game.Multiplayer.WorldReplicationCoordinator");
            Assert.That(_type,Is.Not.Null,"正式材料Coordinator尚未接入，不能用PeerState单测代替闭环。");
            var server=Activator.CreateInstance(_type,new object[]{7UL,99UL,8,2048,200,4,1000000,null});
            source=new SnapshotSource();Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            Assert.That(Call(server,"BindPeer",0,10,3u),Is.True);return server;
        }
        private object Client(out object store)
        {
            var storeType=Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField");
            Assert.That(storeType,Is.Not.Null);
            store=Activator.CreateInstance(storeType,new object[]{7UL,8,200,4,2048});
            Assert.That(storeType.GetMethod("ConfigureGeometry").Invoke(store,new object[]{new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,1,16)}),Is.True);
            Assert.That(storeType.GetMethod("ConfigureMaterials").Invoke(store,new object[]{WorldMaterialDomain.Liquid,65535u,new byte[]{2},false,8}),Is.True);
            var client=Activator.CreateInstance(_type,new object[]{7UL,99UL,8,2048,200,4,1000000,store});
            Assert.That(Call(client,"SetClientConnectionEpoch",3u),Is.True);return client;
        }
        private object Call(object owner,string method,params object[] args)=>_type.GetMethod(method).Invoke(owner,args);
        private ulong Quantity(object store)=>(ulong)store.GetType().GetMethod("TotalQuantity").Invoke(store,new object[]{WorldMaterialDomain.Liquid});
        private WorldPageMessage[] Pages(object server,double start=0)
        {
            // 旧场景也必须显式声明可信DS范围；重连时读取当前Peer身份，不能继续冒用原connectionId/epoch。
            var peers=(WorldReplicationPeerState[])_type.GetField("_peers",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server);
            Assert.That(RequiredCall(server,"UpdatePeerInterest",0,peers[0].ConnectionId,peers[0].ConnectionEpoch,NearInterest),Is.True);
            var result=new System.Collections.Generic.List<WorldPageMessage>();
            for(int round=0;round<20;round++)
            {
                double now=start+round*.1;Call(server,"BeginRound",now);
                for(int page=0;page<2;page++)
                {
                    object[] args={0,now,null};if(!(bool)Call(server,"TryPreparePage",args))break;
                    var message=((WorldPageMessage)args[2]).Clone();
                    var codec=new WorldPageCodec();var bytes=new byte[900];
                    Assert.That(codec.TryEncode(message,new ArraySegment<byte>(bytes),out int size),Is.True);
                    Assert.That(codec.TryDecode(new ArraySegment<byte>(bytes,0,size),out var decoded),Is.True);
                    Assert.That(Call(server,"MarkPageSent",0,now,size+16),Is.True);
                    if(decoded.Kind==6 || decoded.Kind==7)_legacyControls.Add(decoded.Clone());
                    else result.Add(decoded.Clone());
                }
                if(result.Count>0 && result.Count==result[0].PageCount)break;
            }
            Assert.That(result.Count,Is.GreaterThan(0));Assert.That(result.Count,Is.EqualTo(result[0].PageCount));return result.ToArray();
        }
        private bool Receive(object client,WorldPageMessage page,long now,out WorldPageMessage reply)
        {
            _legacyDelivered.TryGetValue(client,out int delivered);
            while(delivered<_legacyControls.Count)
            {
                Assert.That(Subscribe(client,_legacyControls[delivered]),Is.True,"必须先消费实际发送的可靠控制，再交付材料页。");
                delivered++;
            }
            _legacyDelivered[client]=delivered;
            object[] args={page,now,null};bool accepted=(bool)Call(client,"ReceivePage",args);reply=(WorldPageMessage)args[2];return accepted;
        }
        /// <summary>
        /// 只在测试侧用反射启用诊断。Core类型尚未实现时可先编译并产生明确RED；
        /// 最终断言仍来自真实Owner、分页、Resync和认证入口，不能用“类型存在”代替接线行为。
        /// </summary>
        private sealed class TimingCapture : IDisposable
        {
            private readonly Type _traceType;
            private readonly object _trace;
            internal TimingCapture()
            {
                _traceType=typeof(WorldChunkPayloadHeader).Assembly.GetType("Game.Core.WorldReplicationTimingTrace");
                Assert.That(_traceType,Is.Not.Null,"缺少默认关闭的有界诊断 recorder；不能伪造网络阶段记录。");
                _trace=Activator.CreateInstance(_traceType,new object[]{256,TimingKey,7UL});
                Assert.That(_traceType.GetMethod("TryAttach").Invoke(null,new[]{_trace}),Is.True);
            }
            internal object[] Events(string stage,int reason=-1)
            {
                int count=(int)_traceType.GetProperty("Count").GetValue(_trace);
                var result=new System.Collections.Generic.List<object>();
                for(int i=0;i<count;i++)
                {
                    object[] args={i,null};Assert.That(_traceType.GetMethod("TryRead").Invoke(_trace,args),Is.True);
                    var value=args[1];if(Field(value,"Stage").ToString()==stage && (reason<0 || (int)Field(value,"Reason")==reason))result.Add(value);
                }
                return result.ToArray();
            }
            internal static object Field(object value,string name)=>value.GetType().GetField(name).GetValue(value);
            internal static WorldChunkPayloadHeader Header(object value)=>(WorldChunkPayloadHeader)Field(value,"Header");
            public void Dispose()=>_traceType.GetMethod("Detach").Invoke(null,new[]{_trace});
        }
        [Test] public void TimingOwnerRecordsOnlySuccessfulChangedCommit()
        {
            using var timing=new TimingCapture();
            var owner=new WorldReplicationCommitOwner(7,8,200,4,8,2048,1000000);var source=new SnapshotSource().Value;
            Assert.That(owner.TryCaptureCommit(source),Is.True);
            var changed=timing.Events("Committed");Assert.That(changed.Length,Is.EqualTo(1),"真实发布后必须留下目标Key的提交stamp。");
            Assert.That(TimingCapture.Header(changed[0]).Equals(owner.GetChunkHeader(0)),Is.True);
            source.Header=new WorldReplicaHeader(2,120,0,1);Assert.That(owner.TryCaptureCommit(source),Is.True);
            source.Header=new WorldReplicaHeader(3,201,0,1);Assert.That(owner.TryCaptureCommit(source),Is.False);
            Assert.That(timing.Events("Committed").Length,Is.EqualTo(1),"无变化capture和失败候选不能冒称新提交。");
        }
        [Test] public void TimingPreparedPagesCarryFrozenPeerStampWithoutClaimingTransportSend()
        {
            using var timing=new TimingCapture();var server=Server(out _);var pages=Pages(server);
            var prepared=timing.Events("PagePrepared",2);Assert.That(prepared.Length,Is.EqualTo(pages.Length));
            for(int i=0;i<prepared.Length;i++)
            {
                var h=TimingCapture.Header(prepared[i]);Assert.That(h.Key,Is.EqualTo(TimingKey));
                Assert.That(h.SnapshotId,Is.EqualTo(pages[i].SnapshotId));Assert.That(h.Revision,Is.EqualTo(pages[i].Revision));
                Assert.That(h.ServerTick,Is.EqualTo(1L),"Header.ServerTick是材料Tick，不能填Gameplay 30Hz Tick。");
                Assert.That(TimingCapture.Field(prepared[i],"ConnectionId"),Is.EqualTo(10));
                Assert.That(TimingCapture.Field(prepared[i],"ActorId"),Is.EqualTo(1UL));
                Assert.That(TimingCapture.Field(prepared[i],"ConnectionEpoch"),Is.EqualTo(3u));
                Assert.That(TimingCapture.Field(prepared[i],"SubscriptionEpoch"),Is.EqualTo(pages[i].SubscriptionEpoch));
                Assert.That(TimingCapture.Field(prepared[i],"PageIndex"),Is.EqualTo(i));
                Assert.That(TimingCapture.Field(prepared[i],"PageCount"),Is.EqualTo(pages.Length));
            }
            Assert.That(timing.Events("PageSent"),Is.Empty,"纯Coordinator预算推进尚未实际调用Mirror Send。");
        }
        [Test] public void TimingBudgetRejectionReportsActualTokensAndLeavesPageCursorUnchanged()
        {
            using var timing=new TimingCapture();var server=Server(out _);
            Assert.That(RequiredCall(server,"UpdatePeerInterest",0,10,3u,NearInterest),Is.True);Call(server,"BeginRound",0d);
            object[] args={0,0d,null};Assert.That(Call(server,"TryPreparePage",args),Is.True);
            Assert.That(((WorldPageMessage)args[2]).Kind,Is.EqualTo(6));Assert.That(Call(server,"MarkPageSent",0,0d,100),Is.True);
            args=new object[]{0,0d,null};Assert.That(Call(server,"TryPreparePage",args),Is.True);
            Assert.That(((WorldPageMessage)args[2]).PageIndex,Is.Zero);Assert.That(Call(server,"MarkPageSent",0,0d,4000),Is.False);
            var rejected=timing.Events("PagePrepared",4);Assert.That(rejected.Length,Is.EqualTo(1));
            Assert.That(TimingCapture.Field(rejected[0],"Tokens"),Is.EqualTo(3500d),"3600初始token只实际消耗100B生命周期；不能以请求字节冒充余额。");
            Assert.That(TimingCapture.Field(rejected[0],"Count1"),Is.EqualTo(4000));
            args=new object[]{0,0d,null};Assert.That(Call(server,"TryPreparePage",args),Is.True);
            Assert.That(((WorldPageMessage)args[2]).PageIndex,Is.Zero,"失败预留不能跳过未发第一页。");
            Assert.That(timing.Events("PageSent"),Is.Empty);
        }
        [Test] public void TimingNeedsBaselineResyncAndAcceptedControlRetainCandidateStamp()
        {
            using var timing=new TimingCapture();var server=Server(out _);var client=Client(out var store);var pages=Pages(server);
            var missing=pages[0].Clone();missing.Kind=2;missing.BaseRevision=1;
            Assert.That(Receive(client,missing,100,out var response),Is.False);Assert.That(response.Kind,Is.EqualTo(4));
            Assert.That(Quantity(store),Is.Zero);Assert.That(Call(server,"ReceiveControl",0,10,3u,response,.5),Is.True);
            var requested=timing.Events("ResyncRequested");Assert.That(requested.Length,Is.EqualTo(1));
            Assert.That(TimingCapture.Field(requested[0],"Reason"),Is.EqualTo(1),"必须区分needsBaseline分支，不能沿用上次Assembler失败。");
            Assert.That(TimingCapture.Header(requested[0]).BaseRevision,Is.EqualTo(1UL));
            Assert.That(TimingCapture.Header(requested[0]).SnapshotId,Is.EqualTo(missing.SnapshotId));
            Assert.That(TimingCapture.Field(requested[0],"SubscriptionEpoch"),Is.EqualTo(missing.SubscriptionEpoch));
            var accepted=timing.Events("ReplyAccepted");Assert.That(accepted.Length,Is.EqualTo(1));
            Assert.That(TimingCapture.Field(accepted[0],"Reason"),Is.EqualTo(4),"只有正式校验通过的Kind4才算接受。");
            Assert.That(TimingCapture.Header(accepted[0]).BaseRevision,Is.Zero,"DS关联的是实际pending，不把Client错误Base当成Owner事实。");
            Assert.That(TimingCapture.Field(accepted[0],"ConnectionId"),Is.EqualTo(10));
            Assert.That(TimingCapture.Field(accepted[0],"ActorId"),Is.EqualTo(1UL));
        }
        [TestCase("begin",2,WorldReplicaAssemblyFailure.MissingRevision)]
        [TestCase("receive",3,WorldReplicaAssemblyFailure.Checksum)]
        [TestCase("expire",4,WorldReplicaAssemblyFailure.Timeout)]
        public void TimingResyncBranchesPreserveFailureAndRealInstalledVersion(string branch,int reason,WorldReplicaAssemblyFailure failure)
        {
            var server=Server(out var source);var client=Client(out var store);var baseline=Pages(server);WorldPageMessage response=null;
            foreach(var page in baseline)Assert.That(Receive(client,page,100,out response),Is.True);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,response,.5),Is.True);
            source.Value.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Vector3Int.zero,2,20);
            source.Value.Header=new WorldReplicaHeader(2,120,0,1);Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            var delta=Pages(server,1);using var timing=new TimingCapture();
            if(branch=="begin")
            {
                var missing=delta[0].Clone();missing.BaseRevision=2;missing.Revision=3;
                Assert.That(Receive(client,missing,200,out response),Is.False);
            }
            else if(branch=="receive")
            {
                delta[0].Words[20]^=1;bool accepted=true;foreach(var page in delta)accepted=Receive(client,page,200,out response);
                Assert.That(accepted,Is.False);
            }
            else
            {
                Assert.That(Receive(client,delta[0],200,out response),Is.True);Assert.That(response,Is.Null);
                object[] args={15200L,null};Assert.That(Call(client,"ExpirePending",args),Is.True);response=(WorldPageMessage)args[1];
            }
            Assert.That(response.Kind,Is.EqualTo(4));Assert.That(Quantity(store),Is.EqualTo(1200UL));
            var requested=timing.Events("ResyncRequested");Assert.That(requested.Length,Is.EqualTo(1));
            Assert.That(TimingCapture.Field(requested[0],"Reason"),Is.EqualTo(reason));
            Assert.That(TimingCapture.Field(requested[0],"Count0"),Is.EqualTo((int)failure));
            Assert.That(TimingCapture.Field(requested[0],"Count1"),Is.EqualTo(1),"bit0为实际payload可读，bit1为needsBaseline；失败不清除已装payload。");
            Assert.That(TimingCapture.Field(requested[0],"InstalledPresent"),Is.True);
            Assert.That(TimingCapture.Field(requested[0],"InstalledGeneration"),Is.EqualTo(1UL));
            Assert.That(TimingCapture.Field(requested[0],"InstalledRevision"),Is.EqualTo(1UL));
            Assert.That(TimingCapture.Header(requested[0]).SnapshotId,Is.EqualTo(delta[0].SnapshotId));
        }
        [Test] public void RealReplicaRemainsEmptyUntilCompleteValidatedPayloadAndThenAcknowledges()
        {
            var server=Server(out _);var client=Client(out var store);var pages=Pages(server);
            Assert.That(pages.Length,Is.GreaterThan(4));WorldPageMessage ack=null;
            for(int i=pages.Length-1;i>0;i--){Assert.That(Receive(client,pages[i],100,out ack),Is.True);Assert.That(ack,Is.Null);Assert.That(Quantity(store),Is.Zero);}
            Assert.That(Receive(client,pages[0],101,out ack),Is.True);Assert.That(Quantity(store),Is.EqualTo(1200UL));
            Assert.That(ack.Kind,Is.EqualTo(3));Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,.4),Is.True);
        }
        [Test] public void FrozenTransferSurvivesNewSourceCommitAndNextTransferUsesExactBaseRevision()
        {
            var server=Server(out var source);var client=Client(out var store);var pages=Pages(server);
            for(int i=0;i<source.Value.Header.CellCount;i++){var old=source.Value.Cells[i];source.Value.Cells[i]=new WorldMaterialCellSample(old.Domain,old.Storage,old.Cell,old.MaterialId,20);}
            source.Value.Header=new WorldReplicaHeader(2,120,0,1);
            Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            WorldPageMessage ack=null;foreach(var page in pages)Assert.That(Receive(client,page,100,out ack),Is.True);
            Assert.That(Quantity(store),Is.EqualTo(1200UL));Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,.5),Is.True);
            var delta=Pages(server,1);Assert.That(delta[0].Kind,Is.EqualTo(2));Assert.That(delta[0].BaseRevision,Is.EqualTo(1UL));
            foreach(var page in delta)Assert.That(Receive(client,page,200,out ack),Is.True);Assert.That(Quantity(store),Is.EqualTo(2400UL));
        }
        [Test] public void DiagnosticChunkStampReportsActualInstalledDeltaBasisAndTick()
        {
            var server=Server(out var source);var client=Client(out var store);var pages=Pages(server);
            WorldPageMessage ack=null;foreach(var page in pages)Assert.That(Receive(client,page,100,out ack),Is.True);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,.5),Is.True);
            source.Value.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Vector3Int.zero,2,20);
            source.Value.Header=new WorldReplicaHeader(2,120,0,1);
            Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            var delta=Pages(server,1);foreach(var page in delta)Assert.That(Receive(client,page,200,out ack),Is.True);
            Assert.That(Quantity(store),Is.EqualTo(1210UL));
            var stamps=(Array)Call(client,"DescribeChunks");Assert.That(stamps.Length,Is.EqualTo(1));
            object stamp=stamps.GetValue(0);var stampType=stamp.GetType();
            // 原生JSON报告必须来自实际安装Header，不能只用“版本变大”把Baseline重装误称Delta。
            foreach(string field in new[]{"BaseRevision","SnapshotId","ServerTick"})
                Assert.That(stampType.GetField(field),Is.Not.Null,"诊断缺少实际安装的"+field);
            Assert.That(stampType.GetField("BaseRevision").GetValue(stamp),Is.EqualTo(1UL));
            Assert.That(stampType.GetField("Revision").GetValue(stamp),Is.EqualTo(2UL));
            Assert.That(stampType.GetField("SnapshotId").GetValue(stamp),Is.EqualTo(delta[0].SnapshotId));
            Assert.That(stampType.GetField("ServerTick").GetValue(stamp),Is.EqualTo(2L));
        }
        [TestCase("epoch")][TestCase("world")][TestCase("hash")]
        public void WrongIdentityPageCannotPublishOrAcknowledge(string field)
        {
            var server=Server(out _);var client=Client(out var store);var pages=Pages(server);
            var bad=pages[0].Clone();if(field=="epoch")bad.ConnectionEpoch=4;else if(field=="world")bad.WorldEpoch=8;else bad.GameplayHash=100;
            Assert.That(Receive(client,bad,100,out var ack),Is.False);Assert.That(ack,Is.Null);Assert.That(Quantity(store),Is.Zero);
            foreach(var page in pages)Assert.That(Receive(client,page,101,out ack),Is.True);Assert.That(Quantity(store),Is.EqualTo(1200UL));
        }
        [Test] public void UnrelatedHighSnapshotCannotReplaceLegitimatePendingAssembly()
        {
            var server=Server(out _);var client=Client(out var store);var pages=Pages(server);
            Assert.That(Receive(client,pages[0],100,out _),Is.True);var bad=pages[1].Clone();bad.SnapshotId=ulong.MaxValue;
            Assert.That(Receive(client,bad,101,out var ack),Is.False);Assert.That(ack,Is.Null);
            for(int i=1;i<pages.Length;i++)Assert.That(Receive(client,pages[i],102,out ack),Is.True);Assert.That(Quantity(store),Is.EqualTo(1200UL));
        }
        [Test] public void CorruptCompletePayloadKeepsOldStoreAndRequestsBoundedResync()
        {
            var server=Server(out _);var client=Client(out var store);var pages=Pages(server);pages[0].Words[20]^=1;
            WorldPageMessage reply=null;bool last=true;foreach(var page in pages)last=Receive(client,page,100,out reply);
            Assert.That(last,Is.False);Assert.That(Quantity(store),Is.Zero);Assert.That(reply.Kind,Is.EqualTo(4));
            Assert.That(Call(server,"ReceiveControl",0,10,3u,reply,1d),Is.True);
            var baseline=Pages(server,2);Assert.That(baseline[0].BaseRevision,Is.Zero);
            foreach(var page in baseline)Assert.That(Receive(client,page,200,out reply),Is.True);Assert.That(Quantity(store),Is.EqualTo(1200UL));
        }
        [Test] public void ReconnectRejectsOldAckAndPreservesInstalledReplicaUntilFreshBaseline()
        {
            var server=Server(out _);var client=Client(out var store);var pages=Pages(server);WorldPageMessage ack=null;
            foreach(var page in pages)Assert.That(Receive(client,page,100,out ack),Is.True);
            Assert.That(Call(server,"BindPeer",0,11,4u),Is.True);Assert.That(Call(client,"SetClientConnectionEpoch",4u),Is.True);
            Assert.That(Quantity(store),Is.EqualTo(1200UL));Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,1d),Is.False);
            var fresh=Pages(server,2);Assert.That(fresh[0].ConnectionEpoch,Is.EqualTo(4));Assert.That(fresh[0].BaseRevision,Is.Zero);
            foreach(var page in fresh)Assert.That(Receive(client,page,200,out ack),Is.True);
            Assert.That(Call(server,"ReceiveControl",0,11,4u,ack,3d),Is.True);Assert.That(Quantity(store),Is.EqualTo(1200UL));
        }
        [TestCase("unauthenticated")][TestCase("stale-reference")][TestCase("world-handshake-missing")][TestCase("queue-capacity")][TestCase("destroyed-channel")][TestCase("queue-timing")]
        public void NativeClientIngressChecksRealAuthenticationBeforeBoundedPageQueue(string boundary)
        {
            Assert.That(NetworkClient.active,Is.False,"不得接管用户运行中的Client。");
            var server=Server(out _);var client=Client(out var store);var pages=Pages(server);
            var type=typeof(WorldReplicationPeerState).Assembly.GetType("Game.Multiplayer.WorldReplicationNetworkChannel");
            Assert.That(type,Is.Not.Null,"正式Mirror适配器缺失，纯Coordinator通过不能代替认证接线。");
            var root=new GameObject("Inactive material channel ingress");root.SetActive(false);
            var worldRoot=new GameObject("Inactive trusted material world");worldRoot.SetActive(false);
            var connectionProperty=typeof(NetworkClient).GetProperty("connection");var oldConnection=NetworkClient.connection;
            var connectState=typeof(NetworkClient).GetField("connectState",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
            var oldState=connectState.GetValue(null);
            TimingCapture timing=null;
            try
            {
                if(boundary=="queue-timing")timing=new TimingCapture();
                root.AddComponent<RoomNetworkManager>();var auth=root.GetComponent<JoinTicketAuthenticator>();
                if(auth==null)auth=root.AddComponent<JoinTicketAuthenticator>();
                var identity=new JoinConnectionIdentity(new string('a',32),new string('b',32),new string('c',32),1,3,1,"p7");
                var connection=new NetworkConnectionToServer{isAuthenticated=true,authenticationData=identity};
                connectionProperty.SetValue(null,connection);connectState.SetValue(null,Enum.Parse(connectState.FieldType,"Connected"));
                Set(auth,"<ClientAuthenticated>k__BackingField",true);Set(auth,"<ClientIdentity>k__BackingField",identity);Set(auth,"<ClientWorldEpoch>k__BackingField",7UL);
                var world=worldRoot.AddComponent<OnlineWorldBootstrap>();Set(world,"<WorldReady>k__BackingField",true);Set(world,"<WorldEpoch>k__BackingField",7UL);Set(world,"_runId",identity.RunId);
                var catalog=Resources.Load<Game.Character.OnlineDungeonCatalog>("Online/DungeonCatalog");Assert.That(catalog.TryGet("p7",out var dungeon),Is.True);Set(world,"_dungeonDefinition",dungeon);
                var channel=root.AddComponent(type);type.GetMethod("Configure").Invoke(channel,new object[]{world,client});
                if(boundary=="destroyed-channel")
                {
                    Set(world,"_materialChannel",channel);
                    type.GetMethod("OnDestroy",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(channel,null);
                    UnityEngine.Object.DestroyImmediate(channel);
                    var handlers=(System.Collections.IDictionary)typeof(NetworkClient).GetField("handlers",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).GetValue(null);
                    ushort id=NetworkMessageId<WorldEnvelope>.Id;
                    Assert.That(handlers.Contains(id),Is.False);
                    world.PumpMaterialReplication(1);
                    Assert.That(handlers.Contains(id),Is.False,"Unity已经销毁的旧Channel不能因CLR引用仍存在而重新注册handler。");
                    return;
                }
                var receive=type.GetMethod("OnClientPage",BindingFlags.Instance|BindingFlags.NonPublic);var codec=new WorldPageCodec();var bytes=new byte[900];
                Action<WorldPageMessage> send=page=>{Assert.That(codec.TryEncode(page,new ArraySegment<byte>(bytes),out int size),Is.True);receive.Invoke(channel,new object[]{new WorldEnvelope{Payload=new ArraySegment<byte>(bytes,0,size)}});};
                // 先通过同一真实Codec/Channel建立合法订阅，后续篡改认证状态才能单独检验原有认证边界。
                // 控制消息本身不占材料页收件槽，也不发布Store内容。
                foreach(var control in _legacyControls)send(control);
                Assert.That(type.GetProperty("QueuedPages").GetValue(channel),Is.EqualTo(0));
                if(boundary=="unauthenticated")connection.isAuthenticated=false;
                if(boundary=="stale-reference")connection.authenticationData=new object();
                if(boundary=="world-handshake-missing")Set(auth,"<ClientWorldEpoch>k__BackingField",0UL);
                if(boundary=="queue-capacity" || boundary=="queue-timing")
                {
                    foreach(var page in pages)send(page);
                    Assert.That(type.GetProperty("QueuedPages").GetValue(channel),Is.EqualTo(4));
                    Assert.That(type.GetProperty("Rejected").GetValue(channel),Is.GreaterThanOrEqualTo(2));
                    if(timing!=null)
                    {
                        var rejected=timing.Events("PageRejected");Assert.That(rejected.Length,Is.EqualTo(pages.Length-4));
                        Assert.That(TimingCapture.Field(rejected[0],"Reason"),Is.EqualTo(5),"四槽满必须与身份/Codec拒绝分开。");
                        Assert.That(TimingCapture.Field(rejected[0],"Count0"),Is.EqualTo(4));
                        Assert.That(TimingCapture.Field(rejected[0],"PageIndex"),Is.EqualTo(4));
                        Assert.That(TimingCapture.Field(rejected[0],"ActorId"),Is.EqualTo(1UL));
                        Assert.That(TimingCapture.Header(rejected[0]).SnapshotId,Is.EqualTo(pages[4].SnapshotId));
                        Assert.That(timing.Events("ResyncRequested"),Is.Empty,"入口丢页不等于此刻已经请求Resync。");
                    }
                }
                else {send(pages[0]);Assert.That(type.GetProperty("QueuedPages").GetValue(channel),Is.EqualTo(0));}
                Assert.That(Quantity(store),Is.Zero,"接收回调只复制固定收件箱，不能提前发布材料或ACK。");
                connection.isAuthenticated=true;connection.authenticationData=identity;Set(auth,"<ClientWorldEpoch>k__BackingField",7UL);
                if(boundary!="queue-capacity" && boundary!="queue-timing"){send(pages[0]);Assert.That(type.GetProperty("QueuedPages").GetValue(channel),Is.EqualTo(1));}
            }
            finally
            {
                timing?.Dispose();
                NetworkClient.UnregisterHandler<WorldEnvelope>();
                UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(worldRoot);
                connectionProperty.SetValue(null,oldConnection);connectState.SetValue(null,oldState);
            }
        }
        // 新订阅合同先独立显式收发：控制来自真实DS发送器，不让旧Receive helper偷偷补Enter而掩盖页自激活。
        private static readonly Bounds NearInterest=new Bounds(new Vector3(8,8,8),new Vector3(16,16,16));
        private static readonly Bounds FarInterest=new Bounds(new Vector3(1000,1000,1000),Vector3.one);
        private object RequiredCall(object owner,string method,params object[] args)
        {
            var member=_type.GetMethod(method);
            Assert.That(member,Is.Not.Null,"已批准的订阅生命周期接口缺失："+method);
            return member.Invoke(owner,args);
        }
        private ulong Subscription(WorldPageMessage message)
        {
            var property=typeof(WorldPageMessage).GetProperty("SubscriptionEpoch");
            Assert.That(property,Is.Not.Null,"订阅代次必须独立于连接、Snapshot和材料版本。");
            return (ulong)property.GetValue(message);
        }
        private void SetSubscription(WorldPageMessage message,ulong epoch)
        {
            var property=typeof(WorldPageMessage).GetProperty("SubscriptionEpoch");
            Assert.That(property,Is.Not.Null);property.SetValue(message,epoch);
        }
        private void Interest(object server,Bounds bounds)
        {Assert.That(RequiredCall(server,"UpdatePeerInterest",0,10,3u,bounds),Is.True);}
        private bool Subscribe(object client,WorldPageMessage message)=>(bool)RequiredCall(client,"ReceiveSubscription",message);
        private bool ReceiveExplicit(object client,WorldPageMessage message,long now,out WorldPageMessage reply)
        {object[] args={message,now,null};bool result=(bool)RequiredCall(client,"ReceivePage",args);reply=(WorldPageMessage)args[2];return result;}
        private WorldPageMessage Prepared(object server,double now)
        {
            object[] args={0,now,null};Assert.That(Call(server,"TryPreparePage",args),Is.True);
            return ((WorldPageMessage)args[2]).Clone();
        }
        private WorldPageMessage SendEncoded(object server,WorldPageMessage message,double now)
        {
            var codec=new WorldPageCodec();var bytes=new byte[900];
            Assert.That(codec.TryEncode(message,new ArraySegment<byte>(bytes),out int size),Is.True);
            Assert.That(Call(server,"MarkPageSent",0,now,size+16),Is.True);
            Assert.That(codec.TryDecode(new ArraySegment<byte>(bytes,0,size),out var decoded),Is.True);return decoded.Clone();
        }
        private WorldPageMessage[] SubscriptionTransfer(object server,double start,out WorldPageMessage enter)
        {
            enter=null;var data=new System.Collections.Generic.List<WorldPageMessage>();
            for(int round=0;round<20;round++)
            {
                double now=start+round*.1;Call(server,"BeginRound",now);
                for(int i=0;i<2;i++)
                {
                    object[] args={0,now,null};if(!(bool)Call(server,"TryPreparePage",args))break;
                    var sent=SendEncoded(server,((WorldPageMessage)args[2]).Clone(),now);
                    if(sent.Kind==6){Assert.That(enter,Is.Null);enter=sent;}
                    else {Assert.That(sent.Kind,Is.EqualTo(1u).Or.EqualTo(2u).Or.EqualTo(5u));data.Add(sent);}
                    if(data.Count>0 && data.Count==data[0].PageCount)break;
                }
                if(data.Count>0 && data.Count==data[0].PageCount)break;
            }
            Assert.That(data.Count,Is.GreaterThan(0));Assert.That(data.Count,Is.EqualTo(data[0].PageCount));
            if(enter!=null)foreach(var page in data)Assert.That(Subscription(page),Is.EqualTo(Subscription(enter)));
            return data.ToArray();
        }
        private WorldPageMessage Leave(object server,double now)
        {
            Interest(server,FarInterest);Call(server,"BeginRound",now);
            var leave=SendEncoded(server,Prepared(server,now),now);Assert.That(leave.Kind,Is.EqualTo(7));return leave;
        }
        private WorldPageMessage InstallExplicit(object client,WorldPageMessage enter,WorldPageMessage[] pages,long now)
        {
            Assert.That(enter,Is.Not.Null);Assert.That(Subscribe(client,enter),Is.True);WorldPageMessage ack=null;
            foreach(var page in pages)Assert.That(ReceiveExplicit(client,page,now,out ack),Is.True);
            Assert.That(ack,Is.Not.Null);Assert.That(ack.Kind,Is.EqualTo(3));return ack.Clone();
        }
        // 恢复测试仍走真实Owner/Codec/Assembler/Store；反射只用于让尚未实现的只读观测产生可编译RED。
        // Header断言同时覆盖内容版本与传输/订阅身份，避免把“计数增加”误认成这次候选确已恢复。
        private T RecoveryDiagnostic<T>(object owner,string name)
        {
            var property=_type.GetProperty(name);
            Assert.That(property,Is.Not.Null,"已批准的恢复观测属性缺失："+name);
            return (T)property.GetValue(owner);
        }
        private WorldReplicationChunkStamp InstalledStamp(object client)
        {
            var chunks=(WorldReplicationChunkStamp[])RequiredCall(client,"DescribeChunks");
            Assert.That(chunks.Length,Is.EqualTo(1),"故障不能撤销已安装payload的读取资格。");return chunks[0];
        }
        private void AssertStamp(WorldReplicationChunkStamp actual,WorldPageMessage expected)
        {
            Assert.That(actual.WorldEpoch,Is.EqualTo(expected.WorldEpoch));Assert.That(actual.Domain,Is.EqualTo((int)expected.Domain));
            Assert.That(actual.X,Is.EqualTo(expected.ChunkX));Assert.That(actual.Y,Is.EqualTo(expected.ChunkY));Assert.That(actual.Z,Is.EqualTo(expected.ChunkZ));
            Assert.That(actual.Generation,Is.EqualTo(expected.Generation));Assert.That(actual.Revision,Is.EqualTo(expected.Revision));
            Assert.That(actual.BaseRevision,Is.EqualTo(expected.BaseRevision));Assert.That(actual.SnapshotId,Is.EqualTo(expected.SnapshotId));
            Assert.That(actual.ServerTick,Is.EqualTo((long)expected.ServerTick));Assert.That(actual.Checksum,Is.EqualTo((uint)expected.Checksum));
            Assert.That(actual.Words,Is.EqualTo((int)expected.TotalWords));Assert.That(actual.Resident,Is.EqualTo(expected.Kind!=5));
            Assert.That(actual.SubscriptionEpoch,Is.EqualTo(Subscription(expected)));
        }
        private void AssertFirstQuantity(object store,uint expected)
        {
            object[] args={WorldMaterialDomain.Liquid,Vector3Int.zero,(byte)2,0u};
            Assert.That(store.GetType().GetMethod("TryGetQuantity").Invoke(store,args),Is.True,"旧Store的真实格数量必须仍可读。");
            Assert.That(args[3],Is.EqualTo(expected));
        }
        private WorldPageMessage WireCopy(WorldPageMessage message)
        {
            var codec=new WorldPageCodec();var bytes=new byte[900];
            Assert.That(codec.TryEncode(message,new ArraySegment<byte>(bytes),out int size),Is.True,"故障注入须保持wire形状合法。");
            Assert.That(codec.TryDecode(new ArraySegment<byte>(bytes,0,size),out var decoded),Is.True);return decoded.Clone();
        }
        [Test] public void CorruptDeltaReportsExactRejectedHeadersAndRecoversCurrentBaselineWithPeerAck()
        {
            var server=Server(out var source);var client=Client(out var store);Interest(server,NearInterest);
            var original=SubscriptionTransfer(server,0,out var enter);var ack=InstallExplicit(client,enter,original,100);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(ack),.9),Is.True);
            Assert.That(Quantity(store),Is.EqualTo(1200UL));AssertFirstQuantity(store,10);AssertStamp(InstalledStamp(client),original[0]);
            Assert.That(RecoveryDiagnostic<long>(client,"BaselineInstalled"),Is.EqualTo(1L));
            Assert.That(RecoveryDiagnostic<long>(client,"DeltaInstalled"),Is.Zero);
            AssertStamp((WorldReplicationChunkStamp)RequiredCall(server,"PeerAcknowledgement",0),original[0]);

            source.Value.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Vector3Int.zero,2,20);
            source.Value.Header=new WorldReplicaHeader(2,120,0,1);
            Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            var delta=SubscriptionTransfer(server,1,out var extra);Assert.That(extra,Is.Null);
            Assert.That(delta[0].Kind,Is.EqualTo(2));Assert.That(delta[0].BaseRevision,Is.EqualTo(1UL));Assert.That(delta[0].Revision,Is.EqualTo(2UL));
            // 仅翻转真实payload中的quantity word，保留发送方Checksum和所有Header；Codec必须仍允许该页进入Assembler。
            var corrupt=delta[0].Clone();corrupt.Words[17]^=1;corrupt=WireCopy(corrupt);
            Assert.That(corrupt.Checksum,Is.EqualTo(delta[0].Checksum));WorldPageMessage reply=null;
            for(int i=0;i<delta.Length;i++)
            {
                bool accepted=ReceiveExplicit(client,i==0?corrupt:delta[i],200,out reply);
                Assert.That(accepted,Is.EqualTo(i<delta.Length-1));
                if(i<delta.Length-1)Assert.That(reply,Is.Null,"完整校验前不能发安装ACK或恢复请求。");
            }
            Assert.That(reply,Is.Not.Null);Assert.That(reply.Kind,Is.EqualTo(4));var resync=WireCopy(reply);
            Assert.That(Quantity(store),Is.EqualTo(1200UL));AssertFirstQuantity(store,10);AssertStamp(InstalledStamp(client),original[0]);
            Assert.That(RecoveryDiagnostic<WorldReplicaAssemblyFailure>(client,"LastAssemblyFailure"),Is.EqualTo(WorldReplicaAssemblyFailure.Checksum));
            AssertStamp(RecoveryDiagnostic<WorldReplicationChunkStamp>(client,"LastRejectedCandidate"),delta[0]);
            AssertStamp(RecoveryDiagnostic<WorldReplicationChunkStamp>(client,"LastRejectedInstalled"),original[0]);
            Assert.That(RecoveryDiagnostic<bool>(client,"LastRejectedPayloadAvailable"),Is.True);
            Assert.That(RecoveryDiagnostic<long>(client,"ResyncRequested"),Is.EqualTo(1L));
            Assert.That(RecoveryDiagnostic<long>(client,"BaselineInstalled"),Is.EqualTo(1L));Assert.That(RecoveryDiagnostic<long>(client,"DeltaInstalled"),Is.Zero);

            // Owner可继续提交；Resync必须关联仍在等待的delta2，而恢复内容必须取当前rev3。
            source.Value.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Vector3Int.zero,2,30);
            source.Value.Header=new WorldReplicaHeader(3,120,0,1);
            Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            var unrelated=resync.Clone();unrelated.SnapshotId=ulong.MaxValue;
            Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(unrelated),1.8),Is.False);
            Assert.That(RecoveryDiagnostic<long>(server,"ResyncAccepted"),Is.Zero);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,resync,1.9),Is.True);
            Assert.That(RecoveryDiagnostic<long>(server,"ResyncAccepted"),Is.EqualTo(1L));
            Assert.That(RecoveryDiagnostic<int>(server,"LastResyncActorSlot"),Is.Zero);
            AssertStamp(RecoveryDiagnostic<WorldReplicationChunkStamp>(server,"LastAcceptedResync"),delta[0]);
            AssertStamp((WorldReplicationChunkStamp)RequiredCall(server,"PeerAcknowledgement",0),original[0]);
            var current=SubscriptionTransfer(server,2,out extra);Assert.That(extra,Is.Null);
            Assert.That(current[0].Kind,Is.EqualTo(1));Assert.That(current[0].BaseRevision,Is.Zero);Assert.That(current[0].Revision,Is.EqualTo(3UL));
            foreach(var page in current)Assert.That(ReceiveExplicit(client,page,300,out ack),Is.True);
            Assert.That(ack,Is.Not.Null);Assert.That(ack.Kind,Is.EqualTo(3));
            Assert.That(Quantity(store),Is.EqualTo(1220UL));AssertFirstQuantity(store,30);AssertStamp(InstalledStamp(client),current[0]);
            Assert.That(RecoveryDiagnostic<long>(client,"BaselineInstalled"),Is.EqualTo(2L));Assert.That(RecoveryDiagnostic<long>(client,"DeltaInstalled"),Is.Zero);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(ack),3d),Is.True);
            AssertStamp((WorldReplicationChunkStamp)RequiredCall(server,"PeerAcknowledgement",0),current[0]);
            Assert.That(RequiredCall(server,"PeerAcknowledged",0),Is.EqualTo(2UL));
            Assert.That(RequiredCall(server,"PeerAcknowledgement",1),Is.EqualTo(default(WorldReplicationChunkStamp)));
            Assert.That(RequiredCall(server,"PeerAcknowledged",1),Is.Zero,"未接收恢复ACK的另一席位不能被聚合计数冒认。");
        }
        [Test] public void MissingRevisionReportsPreservedDeltaAndRejectsStaleReplayAfterNewerBaselineRecovery()
        {
            var server=Server(out var source);var client=Client(out var store);Interest(server,NearInterest);
            var original=SubscriptionTransfer(server,0,out var enter);var ack=InstallExplicit(client,enter,original,100);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(ack),.9),Is.True);
            source.Value.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Vector3Int.zero,2,20);
            source.Value.Header=new WorldReplicaHeader(2,120,0,1);
            Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            var installedDelta=SubscriptionTransfer(server,1,out var extra);Assert.That(extra,Is.Null);
            Assert.That(installedDelta[0].Kind,Is.EqualTo(2));Assert.That(installedDelta[0].BaseRevision,Is.EqualTo(1UL));
            foreach(var page in installedDelta)Assert.That(ReceiveExplicit(client,page,200,out ack),Is.True);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(ack),1.9),Is.True);
            Assert.That(Quantity(store),Is.EqualTo(1210UL));AssertFirstQuantity(store,20);AssertStamp(InstalledStamp(client),installedDelta[0]);
            Assert.That(RecoveryDiagnostic<long>(client,"BaselineInstalled"),Is.EqualTo(1L));Assert.That(RecoveryDiagnostic<long>(client,"DeltaInstalled"),Is.EqualTo(1L));

            source.Value.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Vector3Int.zero,2,30);
            source.Value.Header=new WorldReplicaHeader(3,120,0,1);
            Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            var delta=SubscriptionTransfer(server,2,out extra);Assert.That(extra,Is.Null);
            Assert.That(delta[0].Kind,Is.EqualTo(2));Assert.That(delta[0].BaseRevision,Is.EqualTo(2UL));Assert.That(delta[0].Revision,Is.EqualTo(3UL));
            // 改成仍满足wire合法性的base1；客户端已安装rev2，真实版本依赖检查必须在任何Store写入前拒绝。
            var gap=delta[0].Clone();gap.BaseRevision=1;gap=WireCopy(gap);
            Assert.That(ReceiveExplicit(client,gap,300,out var reply),Is.False);Assert.That(reply,Is.Not.Null);Assert.That(reply.Kind,Is.EqualTo(4));var resync=WireCopy(reply);
            Assert.That(Quantity(store),Is.EqualTo(1210UL));AssertFirstQuantity(store,20);AssertStamp(InstalledStamp(client),installedDelta[0]);
            Assert.That(RecoveryDiagnostic<WorldReplicaAssemblyFailure>(client,"LastAssemblyFailure"),Is.EqualTo(WorldReplicaAssemblyFailure.MissingRevision));
            AssertStamp(RecoveryDiagnostic<WorldReplicationChunkStamp>(client,"LastRejectedCandidate"),gap);
            AssertStamp(RecoveryDiagnostic<WorldReplicationChunkStamp>(client,"LastRejectedInstalled"),installedDelta[0]);
            Assert.That(RecoveryDiagnostic<bool>(client,"LastRejectedPayloadAvailable"),Is.True);
            Assert.That(RecoveryDiagnostic<long>(client,"ResyncRequested"),Is.EqualTo(1L));

            // 再提交rev4，令恢复Baseline严格新于被拒delta3；随后重放应是StaleVersion而不是同版本的MissingRevision。
            source.Value.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Vector3Int.zero,2,40);
            source.Value.Header=new WorldReplicaHeader(4,120,0,1);
            Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,resync,2.9),Is.True);
            Assert.That(RecoveryDiagnostic<long>(server,"ResyncAccepted"),Is.EqualTo(1L));Assert.That(RecoveryDiagnostic<int>(server,"LastResyncActorSlot"),Is.Zero);
            AssertStamp(RecoveryDiagnostic<WorldReplicationChunkStamp>(server,"LastAcceptedResync"),delta[0]);
            AssertStamp((WorldReplicationChunkStamp)RequiredCall(server,"PeerAcknowledgement",0),installedDelta[0]);
            var current=SubscriptionTransfer(server,3,out extra);Assert.That(extra,Is.Null);
            Assert.That(current[0].Kind,Is.EqualTo(1));Assert.That(current[0].BaseRevision,Is.Zero);Assert.That(current[0].Revision,Is.EqualTo(4UL));
            foreach(var page in current)Assert.That(ReceiveExplicit(client,page,400,out ack),Is.True);
            Assert.That(ack,Is.Not.Null);Assert.That(ack.Kind,Is.EqualTo(3));
            Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(ack),4d),Is.True);
            Assert.That(Quantity(store),Is.EqualTo(1230UL));AssertFirstQuantity(store,40);AssertStamp(InstalledStamp(client),current[0]);
            Assert.That(RecoveryDiagnostic<long>(client,"BaselineInstalled"),Is.EqualTo(2L));Assert.That(RecoveryDiagnostic<long>(client,"DeltaInstalled"),Is.EqualTo(1L));
            AssertStamp((WorldReplicationChunkStamp)RequiredCall(server,"PeerAcknowledgement",0),current[0]);
            Assert.That(RequiredCall(server,"PeerAcknowledged",0),Is.EqualTo(3UL));

            Assert.That(Call(server,"ReceiveControl",0,10,3u,resync,4.1),Is.False,"已完成恢复后没有关联pending，旧Resync不得启动新恢复。");
            Assert.That(ReceiveExplicit(client,WireCopy(delta[0]),500,out reply),Is.False);
            Assert.That(RecoveryDiagnostic<WorldReplicaAssemblyFailure>(client,"LastAssemblyFailure"),Is.EqualTo(WorldReplicaAssemblyFailure.StaleVersion));
            AssertStamp(RecoveryDiagnostic<WorldReplicationChunkStamp>(client,"LastRejectedCandidate"),delta[0]);
            AssertStamp(RecoveryDiagnostic<WorldReplicationChunkStamp>(client,"LastRejectedInstalled"),current[0]);
            Assert.That(RecoveryDiagnostic<bool>(client,"LastRejectedPayloadAvailable"),Is.True);
            Assert.That(reply,Is.Not.Null);Assert.That(reply.Kind,Is.EqualTo(4));
            Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(reply),4.2),Is.False);
            Assert.That(RecoveryDiagnostic<long>(client,"ResyncRequested"),Is.EqualTo(2L),"计数记录实际产生的请求，不能把被拒旧请求算成DS恢复成功。");
            Assert.That(RecoveryDiagnostic<long>(server,"ResyncAccepted"),Is.EqualTo(1L));
            AssertStamp(RecoveryDiagnostic<WorldReplicationChunkStamp>(server,"LastAcceptedResync"),delta[0]);
            Assert.That(Quantity(store),Is.EqualTo(1230UL));AssertFirstQuantity(store,40);AssertStamp(InstalledStamp(client),current[0]);
            AssertStamp((WorldReplicationChunkStamp)RequiredCall(server,"PeerAcknowledgement",0),current[0]);
            Assert.That(RequiredCall(server,"PeerAcknowledged",0),Is.EqualTo(3UL));
            Call(server,"BeginRound",4.3);object[] pending={0,4.3,null};
            Assert.That(Call(server,"TryPreparePage",pending),Is.False,"被拒旧Resync不能留下重发债务或无限恢复循环。");
        }
        [Test] public void BoundPeerWithoutTrustedInterestCannotPrepareAnyLifecycleOrData()
        {
            var server=Server(out _);Call(server,"BeginRound",0d);object[] args={0,0d,null};
            Assert.That(Call(server,"TryPreparePage",args),Is.False,"认证连接不能隐式订阅整个世界。");Assert.That(args[2],Is.Null);
        }
        [Test] public void DataBeforeEnterCannotActivateSubscriptionOrPublishReplica()
        {
            var server=Server(out _);var client=Client(out var store);Interest(server,NearInterest);
            var pages=SubscriptionTransfer(server,0,out var enter);Assert.That(enter,Is.Not.Null);
            Assert.That(RequiredCall(client,"AcceptsPage",pages[0]),Is.False);
            foreach(var page in pages){Assert.That(ReceiveExplicit(client,page,100,out var reply),Is.False);Assert.That(reply,Is.Null);}
            Assert.That(Quantity(store),Is.Zero);
            InstallExplicit(client,enter,pages,101);Assert.That(Quantity(store),Is.EqualTo(1200UL));
        }
        [Test] public void LeaveReleasesActualQuantityOnlyForItsClientAndSameVersionReentryReinstalls()
        {
            var server=Server(out _);var first=Client(out var firstStore);var second=Client(out var secondStore);Interest(server,NearInterest);
            var pages=SubscriptionTransfer(server,0,out var enter);var ack=InstallExplicit(first,enter,pages,100);
            InstallExplicit(second,enter,pages,100);Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,.9),Is.True);
            var leave=Leave(server,1);Assert.That(Subscribe(first,leave),Is.True);
            Assert.That(Quantity(firstStore),Is.Zero);Assert.That(Quantity(secondStore),Is.EqualTo(1200UL));
            Assert.That(Subscribe(first,leave),Is.True);Assert.That(Quantity(firstStore),Is.Zero,"重复Leave必须幂等。");
            Interest(server,NearInterest);var fresh=SubscriptionTransfer(server,2,out var next);
            Assert.That(Subscription(next),Is.GreaterThan(Subscription(enter)));Assert.That(fresh[0].Revision,Is.EqualTo(pages[0].Revision));
            Assert.That(fresh[0].Kind,Is.EqualTo(1));Assert.That(fresh[0].BaseRevision,Is.Zero);
            InstallExplicit(first,next,fresh,200);Assert.That(Quantity(firstStore),Is.EqualTo(1200UL));
        }
        [Test] public void LeavingOldHighCandidateAllowsLowerSnapshotInNewSubscription()
        {
            var server=Server(out _);var client=Client(out var store);Interest(server,NearInterest);
            var old=SubscriptionTransfer(server,0,out var enter);Assert.That(Subscribe(client,enter),Is.True);
            var high=old[0].Clone();high.SnapshotId=ulong.MaxValue;
            Assert.That(ReceiveExplicit(client,high,100,out var reply),Is.True);Assert.That(reply,Is.Null);
            Assert.That(Subscribe(client,Leave(server,1)),Is.True);Interest(server,NearInterest);
            var fresh=SubscriptionTransfer(server,2,out var next);Assert.That(fresh[0].SnapshotId,Is.LessThan(high.SnapshotId));
            InstallExplicit(client,next,fresh,200);Assert.That(Quantity(store),Is.EqualTo(1200UL));
        }
        [Test] public void OldPagesAndLifecycleCannotAffectNewSubscriptionOrInstalledQuantity()
        {
            var server=Server(out _);var client=Client(out var store);Interest(server,NearInterest);
            var old=SubscriptionTransfer(server,0,out var enter);InstallExplicit(client,enter,old,100);
            var leave=Leave(server,1);Assert.That(Subscribe(client,leave),Is.True);Interest(server,NearInterest);
            var fresh=SubscriptionTransfer(server,2,out var next);Assert.That(Subscribe(client,next),Is.True);
            Assert.That(Subscribe(client,enter),Is.False);Assert.That(Subscribe(client,leave),Is.False);
            foreach(var page in old){Assert.That(RequiredCall(client,"AcceptsPage",page),Is.False);Assert.That(ReceiveExplicit(client,page,150,out var reply),Is.False);Assert.That(reply,Is.Null);}
            WorldPageMessage ack=null;foreach(var page in fresh)Assert.That(ReceiveExplicit(client,page,200,out ack),Is.True);
            Assert.That(Quantity(store),Is.EqualTo(1200UL));Assert.That(Subscription(ack),Is.EqualTo(Subscription(next)));
        }
        [TestCase(3u)][TestCase(4u)] public void OldSubscriptionControlDoesNotConsumeCurrentAckOrResyncBudget(uint kind)
        {
            var server=Server(out _);var client=Client(out _);Interest(server,NearInterest);
            var old=SubscriptionTransfer(server,0,out var enter);InstallExplicit(client,enter,old,100);
            Assert.That(Subscribe(client,Leave(server,1)),Is.True);Interest(server,NearInterest);
            var fresh=SubscriptionTransfer(server,2,out var next);var ack=InstallExplicit(client,next,fresh,200);
            var stale=ack.Clone();stale.Kind=kind;SetSubscription(stale,Subscription(enter));
            for(int i=0;i<40;i++)Assert.That(Call(server,"ReceiveControl",0,10,3u,stale,3d),Is.False);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,3d),Is.True,"旧代控制必须在预算和pending水位之前被拒绝。");
        }
        [Test] public void DuplicateEnterPreservesHalfReceivedBaselineCandidate()
        {
            var server=Server(out _);var client=Client(out var store);Interest(server,NearInterest);
            var pages=SubscriptionTransfer(server,0,out var enter);Assert.That(pages.Length,Is.GreaterThan(2));
            Assert.That(Subscribe(client,enter),Is.True);Assert.That(ReceiveExplicit(client,pages[0],100,out _),Is.True);
            Assert.That(Subscribe(client,enter),Is.True);WorldPageMessage ack=null;
            for(int i=1;i<pages.Length;i++)Assert.That(ReceiveExplicit(client,pages[i],101,out ack),Is.True);
            Assert.That(ack,Is.Not.Null);Assert.That(Quantity(store),Is.EqualTo(1200UL));
        }
        [Test] public void FirstDeltaInNewSubscriptionCannotReuseOldInstalledBasis()
        {
            var server=Server(out _);var client=Client(out var store);Interest(server,NearInterest);
            var pages=SubscriptionTransfer(server,0,out var enter);InstallExplicit(client,enter,pages,100);
            Assert.That(Subscribe(client,Leave(server,1)),Is.True);Interest(server,NearInterest);
            var fresh=SubscriptionTransfer(server,2,out var next);Assert.That(Subscribe(client,next),Is.True);
            var delta=fresh[0].Clone();delta.Kind=2;delta.BaseRevision=delta.Revision;
            Assert.That(ReceiveExplicit(client,delta,200,out _),Is.False);Assert.That(Quantity(store),Is.Zero);
            WorldPageMessage ack=null;foreach(var page in fresh)Assert.That(ReceiveExplicit(client,page,201,out ack),Is.True);
            Assert.That(Quantity(store),Is.EqualTo(1200UL));
        }
        [Test] public void FixedClientKeyCapacityIsNotRecycledByInterestLeave()
        {
            var server=Server(out _);Interest(server,NearInterest);SubscriptionTransfer(server,0,out var enter);
            var storeType=Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField");
            var store=Activator.CreateInstance(storeType,new object[]{7UL,2,200,4,2048});
            var client=Activator.CreateInstance(_type,new object[]{7UL,99UL,2,2048,200,4,1000000,store});
            Assert.That(Call(client,"SetClientConnectionEpoch",3u),Is.True);
            Assert.That(Subscribe(client,enter),Is.True);var other=enter.Clone();other.ChunkX=1;Assert.That(Subscribe(client,other),Is.True);
            var leave=enter.Clone();leave.Kind=7;Assert.That(Subscribe(client,leave),Is.True);
            var overflow=enter.Clone();overflow.ChunkX=2;Assert.That(Subscribe(client,overflow),Is.False,"固定Key元数据不得借Leave成为无限探索缓存。");
            Assert.That(Quantity(store),Is.Zero);
        }
        [Test] public void StopRejectsLifecycleAndDelayedPagesWithoutPublishingCandidate()
        {
            var server=Server(out _);var client=Client(out var store);Interest(server,NearInterest);
            var pages=SubscriptionTransfer(server,0,out var enter);Assert.That(Subscribe(client,enter),Is.True);
            Assert.That(ReceiveExplicit(client,pages[0],100,out _),Is.True);Call(client,"Stop");
            Assert.That(Subscribe(client,enter),Is.False);var leave=enter.Clone();leave.Kind=7;Assert.That(Subscribe(client,leave),Is.False);
            foreach(var page in pages){Assert.That(RequiredCall(client,"AcceptsPage",page),Is.False);Assert.That(ReceiveExplicit(client,page,101,out var reply),Is.False);Assert.That(reply,Is.Null);}
            Assert.That(Quantity(store),Is.Zero);
        }
        [Test] public void LifecycleUsesActualEncodedBytesAndSameTwoMessageRoundBudget()
        {
            var server=Server(out _);Interest(server,NearInterest);Call(server,"BeginRound",0d);
            var enter=Prepared(server,0);Assert.That(enter.Kind,Is.EqualTo(6));
            Assert.That(Call(server,"MarkPageSent",0,0d,3601),Is.False,"Enter也必须先预留真实编码及framing字节。");
            var retry=Prepared(server,0);Assert.That(retry,Is.EqualTo(enter));SendEncoded(server,retry,0);
            SendEncoded(server,Prepared(server,0),0);object[] thirdArgs={0,0d,null};
            bool prepared=(bool)Call(server,"TryPreparePage",thirdArgs);
            var third=prepared?((WorldPageMessage)thirdArgs[2]).Clone():null;
            if(prepared)Assert.That(Call(server,"MarkPageSent",0,0d,100),Is.False,"控制和数据共同占每轮最多两条的预算。");
            Assert.That(_type.GetProperty("PagesSent").GetValue(server),Is.EqualTo(2L));
            Call(server,"BeginRound",.1);var resumed=Prepared(server,.1);
            if(prepared)Assert.That(resumed,Is.EqualTo(third));
            else {Assert.That(resumed.Kind,Is.EqualTo(1));Assert.That(resumed.PageIndex,Is.EqualTo(1));}
        }
        [Test] public void RapidUnsentCrossingsCompressToOldLeaveThenLatestEnterAndCurrentBaseline()
        {
            var server=Server(out var source);var client=Client(out var store);Interest(server,NearInterest);
            var original=SubscriptionTransfer(server,0,out var first);var ack=InstallExplicit(client,first,original,100);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,.9),Is.True);Assert.That(Quantity(store),Is.EqualTo(1200UL));
            // 三次跨界都发生在下一次发送前。只保留最终desired状态和代次，不能为每次跨界堆积可靠事件。
            for(int i=0;i<3;i++){Interest(server,FarInterest);Interest(server,NearInterest);}
            source.Value.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Vector3Int.zero,2,20);
            source.Value.Header=new WorldReplicaHeader(2,120,0,1);
            Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(200,4,1000000)),Is.True);
            Call(server,"BeginRound",1d);var leave=SendEncoded(server,Prepared(server,1),1);
            Assert.That(leave.Kind,Is.EqualTo(7));Assert.That(Subscription(leave),Is.EqualTo(Subscription(first)));
            Assert.That(Subscribe(client,leave),Is.True);Assert.That(Quantity(store),Is.Zero);
            var latest=SendEncoded(server,Prepared(server,1),1);Assert.That(latest.Kind,Is.EqualTo(6));
            Assert.That(Subscription(latest),Is.GreaterThan(Subscription(first)));Assert.That(Subscribe(client,latest),Is.True);
            object[] exhausted={0,1d,null};Assert.That(Call(server,"TryPreparePage",exhausted),Is.False,"Leave和Enter共同用完本轮两条预算。");
            var current=SubscriptionTransfer(server,2,out var unexpected);Assert.That(unexpected,Is.Null,"不得补发已被压缩的中间Enter。");
            Assert.That(current[0].Kind,Is.EqualTo(1));Assert.That(current[0].BaseRevision,Is.Zero);Assert.That(current[0].Revision,Is.EqualTo(2UL));
            foreach(var page in current)
            {Assert.That(Subscription(page),Is.EqualTo(Subscription(latest)));Assert.That(ReceiveExplicit(client,page,200,out ack),Is.True);}
            Assert.That(Quantity(store),Is.EqualTo(1210UL));Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,3d),Is.True);
            Call(server,"BeginRound",3.1);object[] remaining={0,3.1,null};Assert.That(Call(server,"TryPreparePage",remaining),Is.False,"当前Baseline确认后不能残留跨界控制债务。");
        }
        [Test] public void MaximumSubscriptionEpochCanLeaveButReentryFaultsWithoutWrapping()
        {
            var server=Server(out _);var client=Client(out var store);Interest(server,NearInterest);Call(server,"BeginRound",0d);
            var announced=SendEncoded(server,Prepared(server,0),0);Assert.That(announced.Kind,Is.EqualTo(6));
            // 只把固定Key的两个计数推到最后一个合法值，保留真实发送形成的Known/Desired/Announced等状态。
            // 这是耗尽前置条件注入；后续Baseline、Leave、预算及Faulted都走真实生产入口。
            var subscriptions=(Array)_type.GetField("_subscriptions",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server);
            object state=subscriptions.GetValue(0,0);var stateType=state.GetType();
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
            foreach(string flag in new[]{"Known","Desired","Announced","AnnouncementKnown"})
                Assert.That(stateType.GetField(flag,fields).GetValue(state),Is.True);
            stateType.GetField("Epoch",fields).SetValue(state,ulong.MaxValue);
            stateType.GetField("AnnouncedEpoch",fields).SetValue(state,ulong.MaxValue);subscriptions.SetValue(state,0,0);
            var maximum=announced.Clone();SetSubscription(maximum,ulong.MaxValue);Assert.That(Subscribe(client,maximum),Is.True);
            var pages=SubscriptionTransfer(server,.1,out var extra);Assert.That(extra,Is.Null);WorldPageMessage ack=null;
            foreach(var page in pages)
            {Assert.That(Subscription(page),Is.EqualTo(ulong.MaxValue));Assert.That(ReceiveExplicit(client,page,100,out ack),Is.True);}
            Assert.That(Quantity(store),Is.EqualTo(1200UL));Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,.9),Is.True);
            var leave=Leave(server,1);Assert.That(Subscription(leave),Is.EqualTo(ulong.MaxValue));
            Assert.That(Subscribe(client,leave),Is.True);Assert.That(Quantity(store),Is.Zero);
            Assert.That(RequiredCall(server,"UpdatePeerInterest",0,10,3u,NearInterest),Is.False);
            Assert.That(_type.GetProperty("Faulted").GetValue(server),Is.True);
            state=subscriptions.GetValue(0,0);Assert.That(stateType.GetField("Epoch",fields).GetValue(state),Is.EqualTo(ulong.MaxValue));
            Call(server,"BeginRound",2d);object[] next={0,2d,null};Assert.That(Call(server,"TryPreparePage",next),Is.False);Assert.That(next[2],Is.Null);
            Assert.That(Quantity(store),Is.Zero,"耗尽不能通过回绕代次重新激活缓存。");
        }
        /// <summary>同一真实合同Source含两块非原点Chunk；每块120格产生851 words/6页，原点仅保留Geometry元数据。</summary>
        private sealed class TwoChunkSource : IWorldReplicationSource
        {
            internal readonly WorldReplicaBuffer Value=new WorldReplicaBuffer(300,4,1000000);
            internal TwoChunkSource(){Snapshot(1,10,10);}
            internal void Snapshot(long tick,uint firstQuantity,uint secondQuantity)
            {
                Value.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,1,16);int count=0;
                for(int chunk=1;chunk<=2;chunk++)
                {
                    uint quantity=chunk==1?firstQuantity:secondQuantity;if(quantity==0)continue;
                    for(int cell=0;cell<120;cell++)Value.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                        WorldMaterialStorage.Dynamic,new Vector3Int(chunk*16+cell%16,cell/16,0),2,cell==0?quantity:10u);
                }
                Value.Header=new WorldReplicaHeader(tick,count,0,1);
            }
            public bool TryCapture(WorldReplicaBuffer destination,out WorldCaptureFailure failure)
            {
                Array.Copy(Value.Geometries,destination.Geometries,1);Array.Copy(Value.Cells,destination.Cells,Value.Header.CellCount);
                destination.Header=Value.Header;failure=default;return true;
            }
        }
        private object TwoChunkServer(out TwoChunkSource source)
        {
            _type=typeof(WorldReplicationPeerState).Assembly.GetType("Game.Multiplayer.WorldReplicationCoordinator");Assert.That(_type,Is.Not.Null);
            var server=Activator.CreateInstance(_type,new object[]{7UL,99UL,128,2048,300,4,1000000,null});source=new TwoChunkSource();
            Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(300,4,1000000)),Is.True);
            Assert.That(Call(server,"BindPeer",0,10,3u),Is.True);
            // Bounds只覆盖非原点Key1/2。原点可靠Leave仍经真实发送器和Client生命周期入口，不伪造隐式订阅。
            Assert.That(RequiredCall(server,"UpdatePeerInterest",0,10,3u,new Bounds(new Vector3(32,8,8),new Vector3(31,16,16))),Is.True);return server;
        }
        private object TwoChunkClient(out object store)
        {
            var storeType=Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField");Assert.That(storeType,Is.Not.Null);
            store=Activator.CreateInstance(storeType,new object[]{7UL,128,300,4,2048});
            Assert.That(storeType.GetMethod("ConfigureGeometry").Invoke(store,new object[]{new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,1,16)}),Is.True);
            Assert.That(storeType.GetMethod("ConfigureMaterials").Invoke(store,new object[]{WorldMaterialDomain.Liquid,65535u,new byte[]{2},false,128}),Is.True);
            var client=Activator.CreateInstance(_type,new object[]{7UL,99UL,128,2048,300,4,1000000,store});
            Assert.That(Call(client,"SetClientConnectionEpoch",3u),Is.True);return client;
        }
        private WorldPageMessage[] TwoChunkTransfer(object server,object client,double start)
        {
            var pages=new System.Collections.Generic.List<WorldPageMessage>();
            for(int round=0;round<30;round++)
            {
                double now=start+round*.1;Call(server,"BeginRound",now);
                for(int sent=0;sent<2;sent++)
                {
                    object[] args={0,now,null};if(!(bool)Call(server,"TryPreparePage",args))break;
                    var page=SendEncoded(server,((WorldPageMessage)args[2]).Clone(),now);
                    if(page.Kind==6 || page.Kind==7){Assert.That(Subscribe(client,page),Is.True);continue;}
                    Assert.That(page.Kind,Is.EqualTo(1u).Or.EqualTo(2u).Or.EqualTo(5u));
                    if(pages.Count>0)
                    {Assert.That(page.ChunkX,Is.EqualTo(pages[0].ChunkX));Assert.That(page.SnapshotId,Is.EqualTo(pages[0].SnapshotId));Assert.That(Subscription(page),Is.EqualTo(Subscription(pages[0])));}
                    Assert.That(page.PageIndex,Is.EqualTo((uint)pages.Count));pages.Add(page);
                    if(pages.Count==pages[0].PageCount)return pages.ToArray();
                }
            }
            Assert.Fail("真实发送器未在有界轮次内完成一块冻结传输。");return null;
        }
        private WorldReplicationChunkStamp KeyStamp(object owner,string method,WorldPageMessage page,params object[] args)
        {
            foreach(var stamp in (WorldReplicationChunkStamp[])RequiredCall(owner,method,args))
                if(stamp.Domain==(int)page.Domain && stamp.X==page.ChunkX && stamp.Y==page.ChunkY && stamp.Z==page.ChunkZ)return stamp;
            Assert.Fail("实际观测中没有目标Key："+page.ChunkX);return default;
        }
        private void AssertKeyQuantity(object store,WorldPageMessage page,uint quantity)
        {
            object[] args={WorldMaterialDomain.Liquid,new Vector3Int(page.ChunkX*16,0,0),(byte)2,0u};
            Assert.That(store.GetType().GetMethod("TryGetQuantity").Invoke(store,args),Is.EqualTo(quantity!=0));Assert.That(args[3],Is.EqualTo(quantity));
        }
        private WorldPageMessage InstallTwoChunkTransfer(object server,object client,object store,WorldPageMessage[] pages,long now,double ackTime,
            uint beforeFirst,uint afterFirst,ulong beforeTotal,ulong afterTotal)
        {
            WorldPageMessage reply=null;
            for(int i=0;i<pages.Length;i++)
            {
                Assert.That(ReceiveExplicit(client,pages[i],now,out reply),Is.True);
                if(i<pages.Length-1)
                {Assert.That(reply,Is.Null,"不足整块时不得ACK。");Assert.That(Quantity(store),Is.EqualTo(beforeTotal));AssertKeyQuantity(store,pages[0],beforeFirst);}
            }
            Assert.That(reply,Is.Not.Null);Assert.That(reply.Kind,Is.EqualTo(3));var ack=WireCopy(reply);
            Assert.That(Quantity(store),Is.EqualTo(afterTotal));AssertKeyQuantity(store,pages[0],afterFirst);
            AssertStamp(KeyStamp(client,"DescribeChunks",pages[0]),pages[0]);
            Assert.That(Call(server,"ReceiveControl",0,10,3u,ack,ackTime),Is.True);
            var acknowledged=KeyStamp(server,"DescribePeerAcknowledgements",pages[0],0);AssertStamp(acknowledged,pages[0]);
            Assert.That(acknowledged.ConnectionId,Is.EqualTo(10));Assert.That(acknowledged.ConnectionEpoch,Is.EqualTo(3u));return ack;
        }
        private void AssertFreeze(object server,WorldPageMessage page,string kind)
        {
            Assert.That(RequiredCall(server,"PeerLastFreezeKind",0).ToString(),Is.EqualTo(kind));
            var peers=(WorldReplicationPeerState[])_type.GetField("_peers",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server);
            var diagnostic=peers[0].GetType().GetProperty("LastFreezeKind");Assert.That(diagnostic,Is.Not.Null);
            Assert.That(diagnostic.GetValue(peers[0]).ToString(),Is.EqualTo(kind));
            var expected=new WorldChunkPayloadHeader(page.WorldEpoch,page.SnapshotId,new WorldChunkKey((WorldMaterialDomain)page.Domain,page.ChunkX,page.ChunkY,page.ChunkZ),
                page.Generation,page.BaseRevision,page.Revision,page.ServerTick,(int)page.TotalWords,(uint)page.Checksum,page.Kind!=5);
            Assert.That(RequiredCall(server,"PeerLastFreezeHeader",0),Is.EqualTo(expected));
        }
        private long HistoryRecords(WorldReplicationCommitOwner owner)
        {
            var property=owner.GetType().GetProperty("HistoryRecordsWritten");Assert.That(property,Is.Not.Null,"必须观测实际AppendHistory，不能以Tick或Capture数替代变更记录数。");
            return Convert.ToInt64(property.GetValue(owner));
        }
        [Test] public void CoveredHistoryNaturallyFreezesCurrentMultiPageBaselinesForTwoNonOriginKeys()
        {
            var server=TwoChunkServer(out var source);var client=TwoChunkClient(out var store);
            var first=TwoChunkTransfer(server,client,0);Assert.That(first[0].ChunkX,Is.EqualTo(1));Assert.That(first.Length,Is.EqualTo(6));Assert.That(first[0].TotalWords,Is.EqualTo(851u));
            InstallTwoChunkTransfer(server,client,store,first,100,1,0,10,0,1200);
            var second=TwoChunkTransfer(server,client,2);Assert.That(second[0].ChunkX,Is.EqualTo(2));Assert.That(second.Length,Is.EqualTo(6));
            InstallTwoChunkTransfer(server,client,store,second,200,3,0,10,1200,2400);
            var owner=(WorldReplicationCommitOwner)_type.GetField("_owner",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server);
            var capacity=owner.GetType().GetProperty("HistoryCapacity");Assert.That(capacity,Is.Not.Null);Assert.That(capacity.GetValue(owner),Is.EqualTo(128));
            Assert.That(HistoryRecords(owner),Is.EqualTo(3L));
            var peer=((WorldReplicationPeerState[])_type.GetField("_peers",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server))[0];Assert.That(peer.HasPending,Is.False);
            var words=new uint[2048];
            // 停在TryFreeze之前，保留两Key真实旧ACK；每次提交只改两格但对应两块，记录数必须每次加2。
            for(int change=1;change<=130;change++)
            {
                source.Snapshot(change+1,(uint)(change+10),(uint)(change+10));
                Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(300,4,1000000)),Is.True);
                Assert.That(HistoryRecords(owner),Is.EqualTo(3L+2L*change));
                if(change==1)
                    foreach(var original in new[]{first[0],second[0]})
                    {
                        var key=new WorldChunkKey((WorldMaterialDomain)original.Domain,original.ChunkX,0,0);
                        Assert.That(owner.TryCopyDelta(key,1,1,words,out var delta),Is.True);Assert.That(delta.Revision,Is.EqualTo(2UL));
                    }
            }
            Assert.That(HistoryRecords(owner),Is.EqualTo(263L));Assert.That(peer.HasPending,Is.False,"暂停期间不能先冻结旧payload再声称ring失效触发回退。");
            foreach(var original in new[]{first[0],second[0]})
            {
                var key=new WorldChunkKey((WorldMaterialDomain)original.Domain,original.ChunkX,0,0);
                Assert.That(owner.TryCopyDelta(key,1,1,words,out _),Is.False,"旧Base曾真实存在，现在已被128条ring覆盖。");
                Assert.That(owner.LastFailure,Is.EqualTo(WorldCommitOwnerFailure.BaselineRequired));
                AssertStamp(KeyStamp(server,"DescribePeerAcknowledgements",original,0),original);
            }
            Assert.That(Quantity(store),Is.EqualTo(2400UL));
            var recoveredFirst=TwoChunkTransfer(server,client,4);AssertFreeze(server,recoveredFirst[0],"HistoryGapBaseline");
            Assert.That(recoveredFirst[0].ChunkX,Is.EqualTo(1));Assert.That(recoveredFirst.Length,Is.EqualTo(6));
            Assert.That(recoveredFirst[0].BaseRevision,Is.Zero);Assert.That(recoveredFirst[0].Revision,Is.EqualTo(131UL));
            InstallTwoChunkTransfer(server,client,store,recoveredFirst,300,5,10,140,2400,2530);
            var recoveredSecond=TwoChunkTransfer(server,client,6);AssertFreeze(server,recoveredSecond[0],"HistoryGapBaseline");
            Assert.That(recoveredSecond[0].ChunkX,Is.EqualTo(2));Assert.That(recoveredSecond.Length,Is.EqualTo(6));
            Assert.That(recoveredSecond[0].BaseRevision,Is.Zero);Assert.That(recoveredSecond[0].Revision,Is.EqualTo(131UL));
            InstallTwoChunkTransfer(server,client,store,recoveredSecond,400,7,10,140,2530,2660);
            AssertStamp(KeyStamp(server,"DescribePeerAcknowledgements",recoveredFirst[0],0),recoveredFirst[0]);
            AssertStamp(KeyStamp(server,"DescribePeerAcknowledgements",recoveredSecond[0],0),recoveredSecond[0]);
            Assert.That(RequiredCall(server,"PeerAcknowledged",0),Is.EqualTo(4UL));Assert.That(RecoveryDiagnostic<long>(client,"BaselineInstalled"),Is.EqualTo(4L));
            Assert.That(RecoveryDiagnostic<long>(client,"DeltaInstalled"),Is.Zero);Assert.That(RecoveryDiagnostic<long>(server,"ResyncAccepted"),Is.Zero);
        }
        [Test] public void TombstoneAcknowledgementPrecedesNewGenerationBaselineAndOldGenerationCannotRefillStore()
        {
            var server=TwoChunkServer(out var source);var client=TwoChunkClient(out var store);
            var first=TwoChunkTransfer(server,client,0);var oldAck=InstallTwoChunkTransfer(server,client,store,first,100,1,0,10,0,1200);
            var second=TwoChunkTransfer(server,client,2);InstallTwoChunkTransfer(server,client,store,second,200,3,0,10,1200,2400);
            source.Snapshot(2,0,10);Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(300,4,1000000)),Is.True);
            var removed=TwoChunkTransfer(server,client,4);Assert.That(removed.Length,Is.EqualTo(1));Assert.That(removed[0].ChunkX,Is.EqualTo(1));
            Assert.That(removed[0].Kind,Is.EqualTo(5));Assert.That(removed[0].Generation,Is.EqualTo(1UL));Assert.That(removed[0].BaseRevision,Is.EqualTo(1UL));
            Assert.That(removed[0].Revision,Is.EqualTo(2UL));Assert.That(removed[0].TotalWords,Is.Zero);
            var tombstoneAck=InstallTwoChunkTransfer(server,client,store,removed,300,5,10,0,2400,1200);
            AssertStamp(KeyStamp(server,"DescribePeerAcknowledgements",removed[0],0),removed[0]);
            source.Snapshot(3,30,10);Assert.That(Call(server,"Capture",source,new WorldReplicaBuffer(300,4,1000000)),Is.True);
            var rebuilt=TwoChunkTransfer(server,client,6);AssertFreeze(server,rebuilt[0],"GenerationBaseline");
            Assert.That(rebuilt.Length,Is.EqualTo(6));Assert.That(rebuilt[0].ChunkX,Is.EqualTo(1));Assert.That(rebuilt[0].Kind,Is.EqualTo(1));
            Assert.That(rebuilt[0].Generation,Is.EqualTo(2UL));Assert.That(rebuilt[0].BaseRevision,Is.Zero);Assert.That(rebuilt[0].Revision,Is.EqualTo(1UL));
            // 新代pending已真实发送；旧代ACK/Resync即使重复也不能清pending或消耗合法新代控制预算。
            foreach(var stale in new[]{oldAck,tombstoneAck})
                for(int repeat=0;repeat<40;repeat++)
                {
                    Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(stale),6.8),Is.False);
                    var resync=stale.Clone();resync.Kind=4;Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(resync),6.8),Is.False);
                }
            Assert.That(RequiredCall(server,"PeerAcknowledged",0),Is.EqualTo(3UL));
            AssertStamp(KeyStamp(server,"DescribePeerAcknowledgements",removed[0],0),removed[0]);
            InstallTwoChunkTransfer(server,client,store,rebuilt,400,7,0,30,1200,2420);
            foreach(var stale in new[]{first[0],removed[0]})
            {
                Assert.That(ReceiveExplicit(client,WireCopy(stale),500,out var reply),Is.False);
                Assert.That(RecoveryDiagnostic<WorldReplicaAssemblyFailure>(client,"LastAssemblyFailure"),Is.EqualTo(WorldReplicaAssemblyFailure.StaleVersion));
                Assert.That(reply,Is.Not.Null);Assert.That(reply.Kind,Is.EqualTo(4));
                Assert.That(Call(server,"ReceiveControl",0,10,3u,WireCopy(reply),7.2),Is.False,"无当前pending的旧代请求不能重开恢复。");
                Assert.That(Quantity(store),Is.EqualTo(2420UL));AssertKeyQuantity(store,rebuilt[0],30);AssertStamp(KeyStamp(client,"DescribeChunks",rebuilt[0]),rebuilt[0]);
            }
            AssertStamp(KeyStamp(server,"DescribePeerAcknowledgements",rebuilt[0],0),rebuilt[0]);
            AssertStamp(KeyStamp(server,"DescribePeerAcknowledgements",second[0],0),second[0]);
            Assert.That(RequiredCall(server,"PeerAcknowledged",0),Is.EqualTo(4UL));Assert.That(RecoveryDiagnostic<long>(server,"ResyncAccepted"),Is.Zero);
            Call(server,"BeginRound",7.4);object[] pending={0,7.4,null};Assert.That(Call(server,"TryPreparePage",pending),Is.False);
        }
        [TestCase(1UL,2UL)][TestCase(2UL,3UL)]
        public void RecoveryProbeAcknowledgesHigherGenerationRevisionOneWithoutRequiringOldRevision(ulong oldGeneration,ulong newGeneration)
        {
            var channelType=typeof(WorldReplicationPeerState).Assembly.GetType("Game.Multiplayer.WorldReplicationNetworkChannel");
            Assert.That(channelType,Is.Not.Null);var probeType=channelType.GetNestedType("RecoveryProbe",BindingFlags.NonPublic);
            Assert.That(probeType,Is.Not.Null);var observe=probeType.GetMethod("TryObserveRecoveryAcknowledgement",BindingFlags.Instance|BindingFlags.NonPublic);
            Assert.That(observe,Is.Not.Null,"先纯提取真实报告分支，再以行为RED检验原Generation/Revision AND判据。");
            string path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"norn-recovery-ack-"+Guid.NewGuid().ToString("N")+".json");
            Assert.That(System.IO.File.Exists(path),Is.False);const BindingFlags fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
            Func<object,string,object> read=(value,name)=>value.GetType().GetField(name,fields).GetValue(value);
            Action<object,string,object> write=(value,name,data)=>value.GetType().GetField(name,fields).SetValue(value,data);
            try
            {
                var probe=Activator.CreateInstance(probeType,fields,null,new object[]{null,path},null);
                var report=read(probe,"_report");var original=read(report,"originalHeader");
                write(report,"selected",true);write(report,"done",true);write(report,"state","Injected");
                var candidate=new WorldPageMessage{ProtocolVersion=ProtocolCompatibility.CurrentVersion,WorldEpoch=7,GameplayHash=99,
                    ConnectionEpoch=3,SubscriptionEpoch=5,Kind=2,Domain=(uint)WorldMaterialDomain.Liquid,ChunkX=1,
                    Generation=oldGeneration,BaseRevision=8,Revision=9,SnapshotId=17,Checksum=123,ServerTick=9};
                original.GetType().GetMethod("Copy",fields).Invoke(original,new object[]{candidate});
                // 外层真实ReceiveControl负责认证与pending匹配；这里只调用原生产报告分支，保留其真实Route和状态门禁。
                var old=candidate.Clone();old.Kind=3;old.BaseRevision=0;old.Revision=8;
                Assert.That(observe.Invoke(probe,new object[]{old,10d}),Is.False);
                if(oldGeneration>1)
                {
                    old.Generation=oldGeneration-1;old.Revision=100;
                    Assert.That(observe.Invoke(probe,new object[]{old,11d}),Is.False,"旧代的大Revision也不能算恢复。");
                }
                Assert.That(read(report,"recoveryAcknowledged"),Is.False);Assert.That(read(report,"recoveryAckMonotonic"),Is.EqualTo(0d));
                Assert.That(read(read(report,"recoveryHeader"),"snapshotId"),Is.EqualTo(0UL));
                var ack=candidate.Clone();ack.Kind=3;ack.BaseRevision=0;ack.Generation=newGeneration;ack.Revision=1;ack.SnapshotId=23;ack.Checksum=456;ack.ServerTick=12;
                var wrongRoute=ack.Clone();wrongRoute.SubscriptionEpoch++;
                Assert.That(observe.Invoke(probe,new object[]{wrongRoute,12d}),Is.False);Assert.That(read(report,"recoveryAcknowledged"),Is.False);
                Assert.That(observe.Invoke(probe,new object[]{ack,12.5d}),Is.True,"合法新Generation的Revision重置1，诊断必须遵守现有字典序合同。");
                Assert.That(read(report,"recoveryAcknowledged"),Is.True);Assert.That(read(report,"recoveryAckMonotonic"),Is.EqualTo(12.5d));
                var header=read(report,"recoveryHeader");
                Assert.That(read(header,"generation"),Is.EqualTo(newGeneration));Assert.That(read(header,"revision"),Is.EqualTo(1UL));
                Assert.That(read(header,"snapshotId"),Is.EqualTo(23UL));Assert.That(read(header,"checksum"),Is.EqualTo(456UL));
                Assert.That(read(header,"baseRevision"),Is.EqualTo(0UL));Assert.That(read(header,"subscriptionEpoch"),Is.EqualTo(5UL));
                Assert.That(read(report,"recoveryAckUtc"),Is.Not.Null.And.Not.Empty);string acceptedUtc=(string)read(report,"recoveryAckUtc");
                old=candidate.Clone();old.Kind=3;old.BaseRevision=0;old.Revision=8;
                Assert.That(observe.Invoke(probe,new object[]{old,13d}),Is.False);
                Assert.That(read(report,"recoveryAckMonotonic"),Is.EqualTo(12.5d));Assert.That(read(report,"recoveryAckUtc"),Is.EqualTo(acceptedUtc));
                Assert.That(read(header,"snapshotId"),Is.EqualTo(23UL));Assert.That(read(header,"generation"),Is.EqualTo(newGeneration));
            }
            finally{if(System.IO.File.Exists(path))System.IO.File.Delete(path);}
        }
        private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,value);
        [Test] public void BootstrapStopCancelsCoordinatorBeforeADeferredPageCanInstall()
        {
            var server=Server(out _);var client=Client(out var store);var pages=Pages(server);
            Assert.That(Receive(client,pages[0],100,out _),Is.True);
            var root=new GameObject("Inactive material coordinator lifetime");root.SetActive(false);
            try
            {
                var world=root.AddComponent<OnlineWorldBootstrap>();Set(world,"_materialReplication",client);world.StopWorld();
                for(int i=1;i<pages.Length;i++)Assert.That(Receive(client,pages[i],101,out var reply),Is.False,"World停止后不得继续安装旧Manager递交的延迟页。");
                Assert.That(Quantity(store),Is.Zero);
            }
            finally{UnityEngine.Object.DestroyImmediate(root);}
        }
    }
}
