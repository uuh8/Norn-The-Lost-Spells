using System;
using Game.Core;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 重现真实单份材料跨越大量地址时的生命周期矛盾：数量和resident块都很少，
    /// 不能因为历史空地址累计而停止整个世界。测试从正式Coordinator入口行走，
    /// 不直接删私有数组、不增大正式128上限，也不伪造ACK来释放预约。
    /// </summary>
    public sealed class WorldReplicationRetirementTests
    {
        private sealed class MovingSource : IWorldReplicationSource
        {
            internal int Step;
            internal Vector3Int Cell;
            public bool TryCapture(WorldReplicaBuffer destination, out WorldCaptureFailure failure)
            {
                destination.Geometries[0] = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 8);
                destination.Cells[0] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                    WorldMaterialStorage.Dynamic, Cell, 2, 10);
                destination.Header = new WorldReplicaHeader(++Step, 1, 0, 1);
                failure = default; return true;
            }
        }

        private static WorldPageMessage Control(uint kind, int x, ulong epoch, uint connection = 3)
            => new WorldPageMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion,
                Kind = kind, WorldEpoch = 7, GameplayHash = 99,
                ConnectionEpoch = connection, Domain = 1, ChunkX = x, SubscriptionEpoch = epoch };

        private static WorldReplicationCoordinator Client(int chunks, out object store,
            Func<IWorldChunkPayloadInstaller,IWorldChunkPayloadInstaller> decorate=null)
        {
            // 本测试程序集维持原依赖边界；通过Core Installer合同进入真正ElementField Store，
            // 不为测试把Gameplay层反向引用到Rendering或复制另一套数量缓存。
            var type = Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField", true);
            store = Activator.CreateInstance(type, new object[] { 7UL, chunks, 8, 0, 128 });
            Assert.That(type.GetMethod("ConfigureGeometry").Invoke(store,
                new object[] { new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, 1, 8) }), Is.True);
            Assert.That(type.GetMethod("ConfigureMaterials").Invoke(store,
                new object[] { WorldMaterialDomain.Liquid, 65535u, new byte[] { 2 }, false, chunks }), Is.True);
            var installer=(IWorldChunkPayloadInstaller)store;
            var client = new WorldReplicationCoordinator(7, 99, chunks, 128, 8, 0, 1000000,
                decorate==null?installer:decorate(installer));
            Assert.That(client.SetClientConnectionEpoch(3), Is.True); return client;
        }

        private static ulong Quantity(object store)
            => (ulong)store.GetType().GetMethod("TotalQuantity").Invoke(store, new object[] { WorldMaterialDomain.Liquid });

        // 首次完整捕获后，每次只给一个原生块的完整替换。负坐标上密集排布数千个持久块，
        // 可以通过真实的 CommitOwner / Peer / Installer 量出工作量，而不依赖特定机器的墙钟。
        private sealed class SparseSource : IWorldReplicationSource, IWorldUnchangedReplicationSource, IWorldCellularIncrementalSource
        {
            internal const int Population=2048;
            internal int ChangedX;
            internal uint Amount;
            private int _tick;
            private static WorldMaterialCellSample Cell(int chunkX,uint amount) =>
                new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,new Vector3Int(chunkX*8,0,0),2,amount);
            public bool TryCapture(WorldReplicaBuffer b,out WorldCaptureFailure failure)
            {
                b.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,1,8);
                for(int i=0;i<Population;i++)b.Cells[i]=Cell(-i-1,10);
                b.Header=new WorldReplicaHeader(++_tick,Population,0,1);failure=default;return true;
            }
            public bool TryAdvanceUnchangedCapture(WorldReplicaBuffer b)
            {
                if(ChangedX!=0)return false;
                var h=b.Header;b.Header=new WorldReplicaHeader(++_tick,h.CellCount,h.PendingCount,h.GeometryCount,h.SpanCount);return true;
            }
            public bool TryCaptureChangedChunks(WorldReplicaBuffer b,out int count,out WorldCaptureFailure failure)
            {
                b.ChangedChunks[0]=new WorldChunkKey(WorldMaterialDomain.Liquid,ChangedX,0,0);
                b.Cells[0]=Cell(ChangedX,Amount);b.Header=new WorldReplicaHeader(++_tick,Amount>0?1:0,0,1);
                ChangedX=0;count=1;failure=default;return true;
            }
        }

        [Test]
        public void FarEditsIdleTicksAndOnePendingTombstoneOnlyVisitLocalWork()
        {
            const int size=SparseSource.Population;
            var server=new WorldReplicationCoordinator(7,99,size+1,128,size,0,2000000);
            var source=new SparseSource();var buffer=new WorldReplicaBuffer(size,0,2000000);
            Assert.That(server.Capture(source,buffer),Is.True);
            Assert.That(server.MetadataKeys,Is.EqualTo(size+1));
            Assert.That(server.BindPeer(0,10,3),Is.True);
            Bounds Window(float x)=>new Bounds(new Vector3(x,4,4),Vector3.one*8);
            Assert.That(server.UpdatePeerInterest(0,10,3,Window(-4)),Is.True);
            var client=Client(4,out var store);
            Deliver(server,client,10,3,0);
            Assert.That(Quantity(store),Is.EqualTo(10uL));

            long checks=server.InterestChunkChecks,send=server.SendCandidateChecks;
            source.ChangedX=-size;source.Amount=13;
            Assert.That(server.Capture(source,buffer),Is.True);
            Assert.That(server.InterestChunkChecks-checks,Is.EqualTo(1),"一个远处的真实修改只需判交这个块。");
            checks=server.InterestChunkChecks;
            for(int i=0;i<32;i++)
            {
                Assert.That(server.Capture(source,buffer),Is.True);
                Assert.That(server.UpdatePeerInterest(0,10,3,Window(-4)),Is.True);
                server.BeginRound(10+i);
                Assert.That(server.TryPreparePage(0,10+i,out _),Is.False);
                Assert.That(server.CompleteSendRound(),Is.True);
            }
            Assert.That(server.InterestChunkChecks,Is.EqualTo(checks));
            Assert.That(server.SendCandidateChecks,Is.EqualTo(send));

            // [-16,-8) 与旧窗口 [-8,0) 只共享面。旧块退出，新块取当前 Baseline；远处 2047 块均无发包。
            Assert.That(server.UpdatePeerInterest(0,10,3,Window(-12)),Is.True);
            Assert.That(server.InterestChunkChecks-checks,Is.LessThanOrEqualTo(10));
            var seen=new System.Collections.Generic.List<WorldPageMessage>();
            Deliver(server,client,10,3,50,sent:seen.Add);
            Assert.That(seen.Count,Is.EqualTo(3));
            Assert.That(seen[0].Kind,Is.EqualTo(7u));Assert.That(seen[0].ChunkX,Is.EqualTo(-1));
            Assert.That(seen[1].Kind,Is.EqualTo(6u));Assert.That(seen[1].ChunkX,Is.EqualTo(-2));
            Assert.That(seen[2].Kind,Is.EqualTo(1u));Assert.That(Quantity(store),Is.EqualTo(10uL));

            source.ChangedX=-2;source.Amount=0;
            Assert.That(server.Capture(source,buffer),Is.True);
            long retirement=server.RetirementChunkChecks;
            for(int i=0;i<16;i++)Assert.That(server.CompleteSendRound(),Is.True);
            Assert.That(server.RetirementChunkChecks-retirement,Is.EqualTo(16));
            seen.Clear();Deliver(server,client,10,3,60,sent:seen.Add);
            Assert.That(seen.Count,Is.EqualTo(1));Assert.That(seen[0].Kind,Is.EqualTo(5u));
            Assert.That(Quantity(store),Is.Zero);
            Assert.That(server.UpdatePeerInterest(0,10,3,Window(-12)),Is.True);
            seen.Clear();Deliver(server,client,10,3,70,sent:seen.Add);
            Assert.That(seen.Count,Is.EqualTo(1));Assert.That(seen[0].Kind,Is.EqualTo(7u));
            Assert.That(server.MetadataKeys,Is.EqualTo(size));Assert.That(server.Faulted,Is.False);
        }

        private static void Deliver(WorldReplicationCoordinator server, WorldReplicationCoordinator client,
            int connection, uint epoch, double start, int slot=0,
            Action<WorldPageMessage> sent=null,Action<WorldPageMessage> acknowledged=null)
        {
            for (int round = 0; round < 40; round++)
            {
                double now = start + round * .2;
                server.BeginRound(now);
                for (int page = 0; page < 2; page++)
                {
                    if (!server.TryPreparePage(slot, now, out var borrowed)) break;
                    var message = borrowed.Clone();
                    sent?.Invoke(message);
                    Assert.That(server.MarkPageSent(slot, now, 200), Is.True);
                    if (message.Kind == 6 || message.Kind == 7)
                        Assert.That(client.ReceiveSubscription(message), Is.True, "可靠生命周期必须先于其分页生效。");
                    else
                    {
                        Assert.That(client.ReceivePage(message, (long)(now * 1000), out var response), Is.True);
                        if (response != null)
                        {
                            acknowledged?.Invoke(response.Clone());
                            Assert.That(server.ReceiveControl(slot, connection, epoch, response, now), Is.True);
                        }
                    }
                }
                Assert.That(server.CompleteSendRound(), Is.True);
            }
        }

        [Test]
        public void OneConservedLiquidSampleCanVisitMoreThan128AddressesWithoutFreezing()
        {
            var server = new WorldReplicationCoordinator(7, 99, 128, 128, 8, 0, 1000000);
            var source = new MovingSource(); var buffer = new WorldReplicaBuffer(8, 0, 1000000);
            for (int i = 0; i < 300; i++)
            {
                source.Cell = new Vector3Int(-16, -8 * i, 56);
                Assert.That(server.Capture(source, buffer), Is.True,
                    "第" + i + "个地址失败：" + server.Failure + "；每轮只有10单位液体，不能把历史空块当永久容量。");
                Assert.That(buffer.Cells[0].Quantity, Is.EqualTo(10UL));
                Assert.That(server.Faulted, Is.False);
            }
            Assert.That(server.DescribeChunks().Length, Is.LessThanOrEqualTo(4));
            Assert.That(server.RetiredKeys,Is.GreaterThan(128UL));
        }

        [Test]
        public void AClientCanLeaveAndVisitMoreThanItsFixedMetadataCapacity()
        {
            var client = Client(4, out _);
            for (int i = 0; i < 32; i++)
            {
                Assert.That(client.ReceiveSubscription(Control(6, i, (ulong)i + 1)), Is.True,
                    "进入第" + i + "个地址不能因先前已经Leave的空槽而失败。");
                Assert.That(client.ReceiveSubscription(Control(7, i, (ulong)i + 1)), Is.True);
                Assert.That(client.Faulted, Is.False);
            }
        }

        [Test]
        public void ANewConnectionEpochClosesOldReplicaQuantitiesUntilCurrentBaseline()
        {
            var server = new WorldReplicationCoordinator(7, 99, 8, 128, 8, 0, 1000000);
            var source = new MovingSource();
            Assert.That(server.Capture(source, new WorldReplicaBuffer(8, 0, 1000000)), Is.True);
            Assert.That(server.BindPeer(0, 10, 3), Is.True);
            Assert.That(server.UpdatePeerInterest(0, 10, 3, new Bounds(Vector3.one * 4, Vector3.one * 32)), Is.True);
            var client = Client(8, out var store);
            Deliver(server, client, 10, 3, 0);
            Assert.That(Quantity(store), Is.EqualTo(10UL));
            Assert.That(client.SetClientConnectionEpoch(4), Is.True);
            Assert.That(Quantity(store), Is.Zero,
                "断线期间DS可能已经退休旧Key，新连接不能把上一个连接的只读数量永远留在画面中。");
            Assert.That(server.BindPeer(0, 11, 4), Is.True);
            Assert.That(server.UpdatePeerInterest(0, 11, 4, new Bounds(Vector3.one * 4, Vector3.one * 32)), Is.True);
            Deliver(server, client, 11, 4, 10);
            Assert.That(Quantity(store), Is.EqualTo(10UL), "当前Baseline恢复原数量，不能补货或重复计数。");
        }

        [Test]
        public void ADelayedEnterCannotReopenAnAlreadyLeftAddress()
        {
            var client = Client(4, out _);
            var old = Control(6, 4, 1);
            Assert.That(client.ReceiveSubscription(old), Is.True);
            Assert.That(client.ReceiveSubscription(Control(7, 4, 1)), Is.True);
            Assert.That(client.ReceiveSubscription(Control(6, 5, 2)), Is.True);
            Assert.That(client.ReceiveSubscription(old), Is.False,
                "即使旧地址元数据已退休，全连接控制高水位也必须阻止旧Enter复活旧材料。");
            Assert.That(client.AcceptsPage(new WorldPageMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion,
                Kind = 1, WorldEpoch = 7, GameplayHash = 99,
                ConnectionEpoch = 3, Domain = 1, ChunkX = 4, SubscriptionEpoch = 1 }), Is.False);
        }

        [Test]
        public void AnUnseenLeaveCannotConsumeTheNextRealEnterWatermark()
        {
            var client=Client(4,out _);
            client.ReceiveSubscription(Control(7,99,ulong.MaxValue));
            Assert.That(client.ReceiveSubscription(Control(6,1,1)),Is.True,
                "未知地址的Leave没有可释放状态，不能消耗真实下一份Enter的全连接水位。");
        }

        [Test]
        public void ADisconnectedPeerCannotLeaveAnUnsentBorrowBlockingAllRetirement()
        {
            var server=new WorldReplicationCoordinator(7,99,8,128,8,0,1000000);
            var source=new MovingSource();var buffer=new WorldReplicaBuffer(8,0,1000000);
            Assert.That(server.Capture(source,buffer),Is.True);
            Assert.That(server.BindPeer(0,10,3),Is.True);
            Assert.That(server.UpdatePeerInterest(0,10,3,new Bounds(Vector3.one*4,Vector3.one*32)),Is.True);
            server.BeginRound(0);Assert.That(server.TryPreparePage(0,0,out _),Is.True);
            // 模拟编码/预算尚未成功时连接退出，未调用Mark，更不能假装已发送。
            server.RemovePeer(0);
            for(int i=1;i<40;i++)
            {
                source.Cell=new Vector3Int(i*8,0,0);
                Assert.That(server.Capture(source,buffer),Is.True,
                    "断线留下的旧借用不能永久阻止其他世界空Key退休："+server.Failure);
            }
        }

        [Test]
        public void AReplicaFaultCannotBeBypassedByRaisingConnectionEpoch()
        {
            var client=Client(1,out _);
            Assert.That(client.ReceiveSubscription(Control(6,1,1)),Is.True);
            Assert.That(client.ReceiveSubscription(Control(6,2,2)),Is.False);
            Assert.That(client.Faulted,Is.True);
            Assert.That(client.SetClientConnectionEpoch(4),Is.False,
                "故障锁存之后不能靠更高连接代次清掉已提交缓存/版本。");
        }

        [Test]
        public void RealInstalledQuantitySurvives300AddressTransfersAndReturnToTheFirstKey()
        {
            var server=new WorldReplicationCoordinator(7,99,8,128,8,0,1000000);
            var source=new MovingSource();var buffer=new WorldReplicaBuffer(8,0,1000000);
            var client=Client(8,out var store);
            Assert.That(server.BindPeer(0,10,3),Is.True);
            ulong firstGeneration=0;
            for(int i=0;i<=300;i++)
            {
                source.Cell=new Vector3Int(i==300?8:(i+1)*8,0,0);
                Assert.That(server.Capture(source,buffer),Is.True,"迁移"+i+":"+server.Failure);
                Assert.That(server.UpdatePeerInterest(0,10,3,new Bounds((Vector3)source.Cell+Vector3.one*4,Vector3.one*12)),Is.True);
                Deliver(server,client,10,3,i*10);
                Assert.That(Quantity(store),Is.EqualTo(10UL),"真实Store只有一份10单位量，不因换地址重复或消失。");
                Assert.That(server.Faulted || client.Faulted,Is.False);
                Assert.That(server.DescribeChunks().Length,Is.LessThanOrEqualTo(4));
                if(i==0)firstGeneration=client.LastInstalled.Generation;
            }
            Assert.That(client.LastInstalled.Generation,Is.GreaterThan(firstGeneration),
                "回到已退休的原地址必须使用更高Generation，不能恢复旧生命周期。");
        }

        private sealed class ThrowingRetirementSink:IWorldChunkPayloadInstaller,IWorldChunkPayloadInterestSink,IWorldChunkPayloadRetirementSink
        {
            internal IWorldChunkPayloadInstaller Inner;
            internal bool ThrowOnEvict,ThrowOnRetire;
            public bool TryInstall(WorldChunkPayloadHeader h,uint[] words,int count)=>Inner.TryInstall(h,words,count);
            public bool TryEvictInterest(WorldChunkKey key)
            {if(ThrowOnEvict)throw new InvalidOperationException("retirement-evict-marker");return ((IWorldChunkPayloadInterestSink)Inner).TryEvictInterest(key);}
            public bool TryRetireInterest(WorldChunkKey key)
            {if(ThrowOnRetire)throw new InvalidOperationException("retirement-sink-marker");return ((IWorldChunkPayloadRetirementSink)Inner).TryRetireInterest(key);}
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnExternalRetirementExceptionLatchesReplicaFault(bool failAfterEviction)
        {
            var sink=new ThrowingRetirementSink();
            var client=Client(8,out var store,inner=>{sink.Inner=inner;return sink;});
            var server=new WorldReplicationCoordinator(7,99,8,128,8,0,1000000);
            Assert.That(server.Capture(new MovingSource(),new WorldReplicaBuffer(8,0,1000000)),Is.True);
            Assert.That(server.BindPeer(0,10,3),Is.True);
            Assert.That(server.UpdatePeerInterest(0,10,3,new Bounds(Vector3.one*4,Vector3.one*32)),Is.True);
            Deliver(server,client,10,3,0);
            Assert.That(Quantity(store),Is.EqualTo(10UL));
            sink.ThrowOnEvict=!failAfterEviction;sink.ThrowOnRetire=failAfterEviction;
            bool result=true;
            Assert.DoesNotThrow(()=>result=client.ReceiveSubscription(Control(7,0,1)));
            Assert.That(result,Is.False);Assert.That(client.Faulted,Is.True);
            Assert.That(client.SetClientConnectionEpoch(4),Is.False);
        }

        [Test]
        public void StartingANewSendRoundCancelsAnUnsentOldBorrow()
        {
            var server=new WorldReplicationCoordinator(7,99,8,128,8,0,1000000);
            Assert.That(server.Capture(new MovingSource(),new WorldReplicaBuffer(8,0,1000000)),Is.True);
            Assert.That(server.BindPeer(0,10,3),Is.True);
            Assert.That(server.UpdatePeerInterest(0,10,3,new Bounds(Vector3.one*4,Vector3.one*32)),Is.True);
            server.BeginRound(0);Assert.That(server.TryPreparePage(0,0,out _),Is.True);
            server.BeginRound(1);
            Assert.That(server.MarkPageSent(0,1,200),Is.False,"上一轮未发送借用已失效，不能借下一轮预算补发。");
        }

        [Test]
        public void FourPeersRetire240AddressesRejectOldPacketsAndRejoinWithoutDuplicatingQuantity()
        {
            var server=new WorldReplicationCoordinator(7,99,8,128,8,0,1000000);
            var source=new MovingSource();var buffer=new WorldReplicaBuffer(8,0,1000000);
            var clients=new WorldReplicationCoordinator[4];var stores=new object[4];
            var epochs=new uint[]{3,3,3,3};
            for(int peer=0;peer<4;peer++)
            {clients[peer]=Client(8,out stores[peer]);Assert.That(server.BindPeer(peer,10+peer,3),Is.True);}
            WorldPageMessage oldEnter=null,oldPage=null,oldAck=null;
            for(int i=0;i<=240;i++)
            {
                source.Cell=new Vector3Int(i==240?8:(i+1)*8,0,0);
                Assert.That(server.Capture(source,buffer),Is.True,"四Peer迁移"+i+":"+server.Failure);
                for(int peer=0;peer<4;peer++)
                {
                    if(i==120 && peer==2)
                    {
                        epochs[peer]=4;Assert.That(clients[peer].SetClientConnectionEpoch(4),Is.True);
                        Assert.That(Quantity(stores[peer]),Is.Zero);
                        Assert.That(server.BindPeer(peer,10+peer,4),Is.True);
                    }
                    Assert.That(server.UpdatePeerInterest(peer,10+peer,epochs[peer],
                        new Bounds((Vector3)source.Cell+Vector3.one*4,Vector3.one*12)),Is.True);
                    bool record=i==0 && peer==0;
                    Deliver(server,clients[peer],10+peer,epochs[peer],i*40+peer*10,peer,
                        message=>{if(record && message.ChunkX==1){if(message.Kind==6)oldEnter=message.Clone();else if(message.Kind==1)oldPage=message.Clone();}},
                        ack=>{if(record && ack.ChunkX==1)oldAck=ack.Clone();});
                    Assert.That(Quantity(stores[peer]),Is.EqualTo(10UL));
                    Assert.That(clients[peer].Faulted,Is.False);
                }
                Assert.That(server.DescribeChunks().Length,Is.LessThanOrEqualTo(4));
            }
            Assert.That(oldEnter,Is.Not.Null);Assert.That(oldPage,Is.Not.Null);Assert.That(oldAck,Is.Not.Null);
            Assert.That(clients[0].ReceiveSubscription(oldEnter),Is.False);
            Assert.That(clients[0].ReceivePage(oldPage,10000001,out _),Is.False);
            Assert.That(server.ReceiveControl(0,10,3,oldAck,10001),Is.False);
            Assert.That(Quantity(stores[0]),Is.EqualTo(10UL));
            Assert.That(server.Faulted,Is.False);
        }

        [Test]
        public void AReliableSendFailureStopsCaptureBeforeReadingAnotherSourceAndKeepsThePrimaryError()
        {
            var server=new WorldReplicationCoordinator(7,99,8,128,8,0,1000000);
            var source=new MovingSource();var buffer=new WorldReplicaBuffer(8,0,1000000);
            Assert.That(server.Capture(source,buffer),Is.True);
            int previousReads=source.Step;
            var primary=new InvalidOperationException("reliable-send-marker");
            server.ReportTransportFailure(primary);
            Assert.That(server.Faulted,Is.True);
            Assert.That(server.LifecycleException,Is.SameAs(primary));
            Assert.That(server.Capture(source,buffer),Is.False);
            Assert.That(source.Step,Is.EqualTo(previousReads),"发送失败后不能捕获/推进下一份材料状态。");
            server.ReportTransportFailure(new Exception("later-error"));
            Assert.That(server.LifecycleException,Is.SameAs(primary));
        }
    }
}
