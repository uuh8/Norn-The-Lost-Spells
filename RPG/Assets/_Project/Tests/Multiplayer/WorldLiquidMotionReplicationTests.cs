using System;
using System.Reflection;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>真实数量Owner→页Codec→ReplicaStore→运动Sink闭环；Reflection只跨测试asmdef，不替代运行链路。</summary>
    public sealed class WorldLiquidMotionReplicationTests
    {
        private sealed class FixtureSource : IWorldReplicationSource,IWorldLiquidMotionSource,IOnlineLiquidMotionWorld
        {
            internal uint Epoch=1;internal int Count=1;internal bool OneGroup;internal uint MotionQuantity=20,Quantity=20;
            public IWorldLiquidMotionSource Source=>this;
            public IWorldLiquidMotionSink Sink=>null;
            public bool TryCapture(WorldReplicaBuffer b,out WorldCaptureFailure failure) {
                b.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);
                for(int i=0;i<Count;i++)b.Cells[i]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                    WorldMaterialStorage.Dynamic,Cell(i),1,Quantity);
                b.Header=new WorldReplicaHeader(Epoch,Count,0,1);failure=default;return true;
            }
            public bool TryCapture(WorldLiquidMotionCaptureBuffer b,WorldReplicaBuffer quantities) {
                b.Epoch=Epoch;b.Interval=.1f;b.ChunkSize=16;b.Count=Count;
                for(int i=0;i<Count;i++) {b.GroupIds[i]=OneGroup?0:i;
                    b.Rows[i]=new WorldLiquidMotionSample(Cell(i),1,MotionQuantity,MotionQuantity,
                        new Vector3(0,-.1f,0),Vector3.down,new Vector3(0,-.2f,0),Vector3.down);}
                return true;
            }
            private static Vector3Int Cell(int i)=>new Vector3Int(i%16,i/16,0);
        }
        private sealed class Binding : IOnlineLiquidMotionWorld {
            public IWorldLiquidMotionSource Source=>null;public IWorldLiquidMotionSink Sink {get;set;}
        }
        private FixtureSource _source;private WorldReplicaBuffer _buffer;
        private WorldReplicationCoordinator _server,_client;
        private WorldLiquidMotionReplication _out,_in;
        private object _store,_readModel;private ScriptableObject _profile,_water;
        private WorldPageCodec _codec;private byte[] _wire;
        private double _now;
        [SetUp] public void Setup() {
            _source=new FixtureSource();_buffer=new WorldReplicaBuffer(256,0,1000000);
            var storeType=Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField",true);
            var profileType=Type.GetType("Game.ElementField.LiquidWorldProfile, Game.ElementField",true);
            var behaviorType=Type.GetType("Game.ElementField.LiquidBehaviorProfile, Game.ElementField",true);
            _profile=ScriptableObject.CreateInstance(profileType);_water=ScriptableObject.CreateInstance(behaviorType);
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",_profile);
            var materials=Array.CreateInstance(behaviorType,1);materials.SetValue(_water,0);
            profileType.GetField("_materials",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_profile,materials);
            _store=Activator.CreateInstance(storeType,new object[]{7UL,8,256,0,2048});
            var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);
            Assert.True((bool)storeType.GetMethod("ConfigureGeometry").Invoke(_store,new object[]{geometry}));
            Assert.True((bool)storeType.GetMethod("ConfigureMaterials").Invoke(_store,new object[]{WorldMaterialDomain.Liquid,510u,new byte[]{1},false,8}));
            var readType=Type.GetType("Game.ElementField.WorldReplicaLiquidReadModel, Game.ElementField",true);
            _readModel=Activator.CreateInstance(readType,new object[]{_store,geometry,_profile,.25f,null,null});
            _server=new WorldReplicationCoordinator(7,99,8,2048,256,0,1000000);
            _client=new WorldReplicationCoordinator(7,99,8,2048,256,0,1000000,(IWorldChunkPayloadInstaller)_store);
            _client.SetClientConnectionEpoch(3);_server.BindPeer(0,10,3);
            _out=new WorldLiquidMotionReplication(_server,_source);
            _in=new WorldLiquidMotionReplication(_client,new Binding{Sink=(IWorldLiquidMotionSink)_readModel});
            _in.ResetConnection(3);_codec=new WorldPageCodec();_wire=new byte[900];_now=0;
        }
        [TearDown] public void TearDown() {UnityEngine.Object.DestroyImmediate(_profile);UnityEngine.Object.DestroyImmediate(_water);}
        private void CommitQuantity() {
            Assert.True(_server.Capture(_source,_buffer));
            Assert.True(_server.UpdatePeerInterest(0,10,3,new Bounds(Vector3.zero,Vector3.one*100)));
            for(int round=0;round<20;round++) {
                _server.BeginRound(_now);
                for(int i=0;i<2;i++) {
                    if(!_server.TryPreparePage(0,_now,out var p))break;
                    Assert.True(_codec.TryEncode(p,new ArraySegment<byte>(_wire),out int n));
                    if(!_server.MarkPageSent(0,_now,n+72)){_server.CancelPreparedPage(0);break;}
                    Assert.True(_codec.TryDecode(new ArraySegment<byte>(_wire,0,n),out var decoded));
                    if(decoded.Kind==6 || decoded.Kind==7)Assert.True(_client.ReceiveSubscription(decoded));
                    else {Assert.True(_client.ReceivePage(decoded,(long)(_now*1000),out var reply));
                        if(reply!=null)Assert.True(_server.ReceiveControl(0,10,3,reply,_now));}
                }
                _server.CompleteSendRound();_now+=.02;
            }
        }
        private int Deliver(double sampleTime,bool advance=true) {
            _out.Capture(_buffer,sampleTime);int pages=0;
            while(_out.TryPreparePage(0,3,Vector3.zero,_now,sampleTime+.025,out var p)) {
                Assert.True(_codec.TryEncode(p,new ArraySegment<byte>(_wire),out int n));
                Assert.True(_out.MarkPageSent(0,n));
                Assert.True(_codec.TryDecode(new ArraySegment<byte>(_wire,0,n),out var decoded));
                Assert.True(_in.ReceivePage(decoded,_now));pages++;
                Assert.Less(pages,12);
            }
            if(advance)_in.Advance(_now,sampleTime+.025,false);return pages;
        }
        private ulong Total()=>(ulong)_store.GetType().GetMethod("TotalQuantity").Invoke(_store,new object[]{WorldMaterialDomain.Liquid});
        [Test] public void RealQuantityAndMotionWireInstallWithoutCreatingResourcesOrQuantityAcks() {
            CommitQuantity();long acks=_server.Acknowledged;
            Assert.Greater(Deliver(10),0);Assert.AreEqual(1,_in.Published);Assert.AreEqual(20UL,Total());
            _source.Epoch++;CommitQuantity();Deliver(10.1);
            Assert.AreEqual(2,_in.Published);Assert.AreEqual(acks,_server.Acknowledged);
            object[] alpha={2UL,0f};
            Assert.True((bool)_readModel.GetType().GetMethod("TryGetPresentationAlpha").Invoke(_readModel,alpha));
            Assert.That((float)alpha[1],Is.EqualTo(.25f).Within(.001f));
            Assert.AreEqual(20UL,Total());Assert.False(_server.Faulted);Assert.False(_client.Faulted);
        }
        [Test] public void OversizeConnectedGroupIsSkippedWholeAndDoesNotFaultQuantityWorld() {
            _source.Count=129;_source.OneGroup=true;CommitQuantity();
            Assert.AreEqual(0,Deliver(10));Assert.AreEqual(0,_in.Published);
            Assert.AreEqual(2580UL,Total());Assert.False(_server.Faulted);
        }
        [Test] public void NewMotionCallbackPublishesAReadyCandidateBeforeReplacingItsSequence() {
            CommitQuantity();Deliver(10,false);
            Assert.AreEqual(0,_in.Published,"已完整但尚未Pump发布的候选仍在Assembler内。");
            _source.Epoch++;CommitQuantity();_out.Capture(_buffer,10.1);
            Assert.True(_out.TryPreparePage(0,3,Vector3.zero,_now,10.125,out var next));
            // 使用真实接收入口的同步时间与Frozen状态，模拟同一帧中连续两次Mirror回调。
            // 原实现直接CancelPending会丢掉已经满足数量的上一份完整窗口。
            var receive=typeof(WorldLiquidMotionReplication).GetMethod("ReceivePage",new[]{typeof(WorldPageMessage),typeof(double),typeof(double),typeof(bool)});
            Assert.NotNull(receive,"生产回调需要在替换候选前和接收后各尝试发布一次。");
            Assert.True((bool)receive.Invoke(_in,new object[]{next,_now,10.125,false}));
            Assert.AreEqual(2,_in.Published);Assert.AreEqual(2UL,_in.LastPublishedSequence);
            Assert.AreEqual(20UL,Total(),"子帧发布不写回Store数量。");
        }
        [Test] public void MotionCallbackHonorsFrozenBeforeAndAfterReceivingTheCompletePage() {
            CommitQuantity();_out.Capture(_buffer,10);
            Assert.True(_out.TryPreparePage(0,3,Vector3.zero,_now,10.025,out var page));
            var receive=typeof(WorldLiquidMotionReplication).GetMethod("ReceivePage",new[]{typeof(WorldPageMessage),typeof(double),typeof(double),typeof(bool)});
            Assert.NotNull(receive);
            Assert.True((bool)receive.Invoke(_in,new object[]{page,_now,10.025,true}));
            Assert.AreEqual(0,_in.Published);Assert.AreEqual(20UL,Total());
        }
        [Test] public void PartialFrameNeverPublishesAndMotionCannotChangeQuantity() {
            _source.Count=80;CommitQuantity();_out.Capture(_buffer,10);
            Assert.True(_out.TryPreparePage(0,3,Vector3.zero,_now,10.025,out var first));
            Assert.True(_in.ReceivePage(first,_now));_in.Advance(_now,10.025,false);
            Assert.AreEqual(0,_in.Published);Assert.AreEqual(1600UL,Total());
            _in.Advance(_now+1,11,false);Assert.Greater(_in.TimedOut,0);Assert.False(_client.Faulted);
        }
        [Test] public void MismatchedQuantityCandidateDoesNotPoisonNextValidSequence() {
            CommitQuantity();_source.MotionQuantity=19;Deliver(10);
            Assert.AreEqual(0,_in.Published);_in.Advance(_now+.4,10.4,false);
            _source.MotionQuantity=20;_source.Epoch++;CommitQuantity();Deliver(10.5);
            Assert.AreEqual(1,_in.Published);Assert.AreEqual(20UL,Total());
        }
        [Test] public void ReconnectRejectsOldMotionWithoutTouchingInstalledQuantity() {
            CommitQuantity();_out.Capture(_buffer,10);
            Assert.True(_out.TryPreparePage(0,3,Vector3.zero,_now,10.025,out var old));
            _client.SetClientConnectionEpoch(4);ulong afterQuantityReset=Total();_in.ResetConnection(4);
            Assert.False(_in.ReceivePage(old,_now));Assert.AreEqual(afterQuantityReset,Total());Assert.AreEqual(0,_in.Published);
            Assert.AreEqual(20u,_buffer.Cells[0].Quantity,"重连只撤Replica缓存，Authority数量保持。");
        }
        [Test] public void UnvalidatedHighSequenceDoesNotBlockAnImmediatelyValidLowerWindow() {
            CommitQuantity();_source.MotionQuantity=19;_out.Capture(_buffer,10);
            Assert.True(_out.TryPreparePage(0,3,Vector3.zero,_now,10.025,out var invalid));
            invalid.SnapshotId=999;Assert.True(_in.ReceivePage(invalid,_now));_in.Advance(_now,10.025,false);
            Assert.AreEqual(0,_in.Published);
            _source.MotionQuantity=20;_source.Epoch++;CommitQuantity();Deliver(10.1);
            Assert.AreEqual(1,_in.Published);Assert.AreEqual(2UL,_in.LastPublishedSequence);
        }
        [Test] public void SameWorldClearDoesNotRestartTheAcceptedSequenceOrResurrectAnOldPage() {
            CommitQuantity();Deliver(10);Assert.AreEqual(1UL,_in.LastPublishedSequence);
            _out.Clear();_in.Clear();_in.ResetConnection(3);
            _source.Epoch++;CommitQuantity();Deliver(10.1);
            Assert.AreEqual(2UL,_in.LastPublishedSequence);Assert.AreEqual(20UL,Total());
        }
        [Test] public void FrozenCandidateWaitsAndCannotAdvanceTheSinkInTheFirstFrozenFrame() {
            CommitQuantity();Deliver(10,false);_in.Advance(_now,10.025,true);
            Assert.AreEqual(0,_in.Published);Assert.AreEqual(20UL,Total());
            _in.Advance(_now+.01,10.035,false);Assert.AreEqual(1,_in.Published);
        }
        [Test] public void AuthenticatedMatchingAcksCanDrainMoreThan32ChunkCommitsPerSecond() {
            CommitQuantity();long before=_server.Acknowledged;
            // 10Hz世界中四个活动Chunk就会有40个合法ACK/s。每次ACK均对应实际发完并已安装的唯一pending，
            // 不能把它当作可任意自造的客户端请求限为32/s，否则已发完的队列会等2秒超时才恢复。
            for(int i=0;i<40;i++) {
                _source.Epoch++;_source.Quantity++;Assert.True(_server.Capture(_source,_buffer));
                _server.BeginRound(_now);Assert.True(_server.UpdatePeerInterest(0,10,3,new Bounds(Vector3.zero,Vector3.one*100)));
                Assert.True(_server.TryPreparePage(0,_now,out var page));
                Assert.True(_codec.TryEncode(page,new ArraySegment<byte>(_wire),out int bytes));
                Assert.True(_server.MarkPageSent(0,_now,bytes+72));
                Assert.True(_codec.TryDecode(new ArraySegment<byte>(_wire,0,bytes),out var received));
                Assert.True(_client.ReceivePage(received,(long)(_now*1000),out var ack));Assert.NotNull(ack);
                Assert.True(_server.ReceiveControl(0,10,3,ack,_now),"合法第"+(i+1)+"次提交ACK不应被限流卡住。");
                Assert.False(_server.ReceiveControl(0,10,3,ack,_now),"同一ACK重复到达仍须拒绝。");
                _server.CompleteSendRound();_now+=1d/60;
            }
            Assert.AreEqual(before+40,_server.Acknowledged);Assert.AreEqual(_source.Quantity,Total());
        }
    }
}
