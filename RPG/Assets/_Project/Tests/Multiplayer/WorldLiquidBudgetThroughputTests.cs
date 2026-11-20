// 固定128格的真实数量/运动网络链吞吐回归；虚拟30Hz调度不代表Native性能。
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    public sealed class WorldLiquidBudgetThroughputTests
    {
        private sealed class Load:IWorldReplicationSource,IWorldLiquidMotionSource,IOnlineLiquidMotionWorld
        {
            internal uint Tick=1;
            private static readonly int[] X={0,48,16,32};
            private static Vector3Int Cell(int key,int n)=>new Vector3Int(X[key]+n%8,2,2+n/8);
            private static uint Q(uint tick,int key)=>tick<=1 || (tick&1)!=0?20u:(key%2==0?19u:21u);
            public IWorldLiquidMotionSource Source=>this;
            public IWorldLiquidMotionSink Sink=>null;
            public bool TryCapture(WorldReplicaBuffer b,out WorldCaptureFailure failure)
            {
                b.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);
                int write=0;
                for(int key=0;key<4;key++)for(int n=0;n<32;n++)
                    b.Cells[write++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Cell(key,n),1,Q(Tick,key));
                b.Header=new WorldReplicaHeader(Tick,write,0,1);failure=default;return true;
            }
            public bool TryCapture(WorldLiquidMotionCaptureBuffer b,WorldReplicaBuffer committed)
            {
                b.Epoch=Tick;b.Interval=.1f;b.ChunkSize=16;b.Count=128;int write=0;
                for(int key=0;key<4;key++)for(int n=0;n<32;n++)
                {
                    b.GroupIds[write]=0;
                    b.Rows[write++]=new WorldLiquidMotionSample(Cell(key,n),1,Q(Tick-1,key),Q(Tick,key),
                        Vector3.zero,Vector3.down,Vector3.zero,Vector3.down);
                }
                return true;
            }
        }
        private sealed class Binding:IOnlineLiquidMotionWorld
        {public IWorldLiquidMotionSource Source=>null;public IWorldLiquidMotionSink Sink{get;set;}}

        private sealed class ObserveSink:IWorldLiquidMotionSink,IWorldLiquidMotionCandidateSink
        {
            internal IWorldLiquidMotionSink Inner;
            internal Func<ulong> Total;
            internal readonly HashSet<uint> OnTime=new HashSet<uint>(),Accepted=new HashSet<uint>();
            public bool TryPublish(WorldLiquidMotionFrame frame,double now)
            {
                bool result=Inner.TryPublish(frame,now);
                if(result)
                {
                    Assert.That(Total(),Is.EqualTo(2560),"完整数量见证发布时四Key总量必须守恒。");
                    if(frame.MaterialTick>=2)
                    {Accepted.Add(frame.MaterialTick);if(now<=frame.SampleTime+.100001)OnTime.Add(frame.MaterialTick);}
                }
                return result;
            }
            public void AdvancePresentation(double time,bool frozen)=>Inner.AdvancePresentation(time,frozen);
            public void ResetConnection(uint epoch)=>Inner.ResetConnection(epoch);
            public void StagePresentationCandidate(WorldLiquidMotionFrame frame,double time)
            {if(Inner is IWorldLiquidMotionCandidateSink candidate)candidate.StagePresentationCandidate(frame,time);}
            public void CancelPresentationCandidate()
            {if(Inner is IWorldLiquidMotionCandidateSink candidate)candidate.CancelPresentationCandidate();}
        }

        private readonly struct Budget
        {
            internal readonly double Rate,Burst;
            internal readonly int Quantity,Motion,Client;
            internal Budget(double rate,double burst,int quantity,int motion,int client)
            {Rate=rate;Burst=burst;Quantity=quantity;Motion=motion;Client=client;}
        }
        private sealed class Rig:IDisposable
        {
            internal readonly Load Source=new Load();
            private readonly WorldReplicaBuffer _buffer=new WorldReplicaBuffer(256,0,1000000);
            internal readonly WorldReplicationCoordinator Server,Client;
            private readonly WorldLiquidMotionReplication _out,_in;
            private readonly ObserveSink _sink=new ObserveSink();
            private readonly Budget _budget;
            private readonly Queue<WorldPageMessage> _fifo=new Queue<WorldPageMessage>(16);
            private readonly WorldPageCodec _codec=new WorldPageCodec();
            private readonly byte[] _wire=new byte[900];
            private readonly object _store;
            private readonly ScriptableObject _profile,_water;
            internal double Now=10;
            internal long QuantityCharged,MotionCharged,QuantityPages,MotionPages,Drops;
            internal int PeakQueue;
            internal int OnTime=>_sink.OnTime.Count;
            internal int Accepted=>_sink.Accepted.Count;
            internal ulong Total=>(ulong)_store.GetType().GetMethod("TotalQuantity").Invoke(_store,new object[]{WorldMaterialDomain.Liquid});
            internal Rig(Budget budget)
            {
                _budget=budget;
                var storeType=Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField",true);
                var profileType=Type.GetType("Game.ElementField.LiquidWorldProfile, Game.ElementField",true);
                var materialType=Type.GetType("Game.ElementField.LiquidBehaviorProfile, Game.ElementField",true);
                _profile=ScriptableObject.CreateInstance(profileType);_water=ScriptableObject.CreateInstance(materialType);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",_profile);
                var materials=Array.CreateInstance(materialType,1);materials.SetValue(_water,0);
                profileType.GetField("_materials",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_profile,materials);
                _store=Activator.CreateInstance(storeType,new object[]{7UL,8,256,0,2048});
                var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);
                Assert.That(storeType.GetMethod("ConfigureGeometry").Invoke(_store,new object[]{geometry}),Is.True);
                Assert.That(storeType.GetMethod("ConfigureMaterials").Invoke(_store,new object[]{WorldMaterialDomain.Liquid,510u,new byte[]{1},false,8}),Is.True);
                var readType=Type.GetType("Game.ElementField.WorldReplicaLiquidReadModel, Game.ElementField",true);
                _sink.Inner=(IWorldLiquidMotionSink)Activator.CreateInstance(readType,new object[]{_store,geometry,_profile,.25f,null,null});_sink.Total=()=>Total;
                Server=Create(null,budget);Client=Create((IWorldChunkPayloadInstaller)_store,budget);
                Assert.That(Server.BindPeer(0,10,3),Is.True);Assert.That(Client.SetClientConnectionEpoch(3),Is.True);
                _out=new WorldLiquidMotionReplication(Server,Source);_in=new WorldLiquidMotionReplication(Client,new Binding{Sink=_sink});_in.ResetConnection(3);
                Capture();for(int warm=0;warm<60;warm++)Frame();
                Assert.That(Total,Is.EqualTo(2560));Assert.That(Server.Faulted || Client.Faulted,Is.False);
            }
            private static WorldReplicationCoordinator Create(IWorldChunkPayloadInstaller installer,Budget budget)
            {
                // 无新预算API时回退现有构造，以真实吞吐不足产生RED，不以缺签名/编译错误冒充复现。
                ConstructorInfo chosen=null;
                foreach(var c in typeof(WorldReplicationCoordinator).GetConstructors())if(chosen==null || c.GetParameters().Length>chosen.GetParameters().Length)chosen=c;
                object[] original={7UL,99UL,8,2048,256,0,1000000,installer,4};var parameters=chosen.GetParameters();var values=new object[parameters.Length];
                for(int i=0;i<parameters.Length;i++)
                {
                    if(i<original.Length){values[i]=original[i];continue;}
                    string name=parameters[i].Name.ToLowerInvariant();object value;
                    if(name.Contains("burst"))value=budget.Burst;
                    else if(name.Contains("bytespersecond") || name.Contains("rate"))value=budget.Rate;
                    else if(name.Contains("page"))value=budget.Quantity;
                    else value=parameters[i].DefaultValue;
                    values[i]=Convert.ChangeType(value,parameters[i].ParameterType);
                }
                TestContext.Out.WriteLine("Budget constructor: "+chosen);
                return (WorldReplicationCoordinator)chosen.Invoke(values);
            }
            private void Capture()
            {
                Assert.That(Server.Capture(Source,_buffer),Is.True,Server.Failure);
                Assert.That(Server.UpdatePeerInterest(0,10,3,new Bounds(Vector3.zero,Vector3.one*1000)),Is.True);
                _out.Capture(_buffer,Now);
            }
            private WorldPageMessage CopyWire(WorldPageMessage value,out int bytes)
            {
                Assert.That(_codec.TryEncode(value,new ArraySegment<byte>(_wire),out bytes),Is.True);
                Assert.That(_codec.TryDecode(new ArraySegment<byte>(_wire,0,bytes),out var decoded),Is.True);return decoded.Clone();
            }
            private void Queue(WorldPageMessage message)
            {
                if(message.Kind==6 || message.Kind==7){Assert.That(Client.ReceiveSubscription(message),Is.True);return;}
                if(_fifo.Count==16){Drops++;return;}_fifo.Enqueue(message);PeakQueue=Math.Max(PeakQueue,_fifo.Count);
            }
            private void Frame()
            {
                Server.BeginRound(Now);int sent=0;
                for(int i=0;i<_budget.Quantity;i++)
                {
                    if(!Server.TryPreparePage(0,Now,out var borrowed))break;
                    var message=CopyWire(borrowed,out int bytes);
                    if(!Server.MarkPageSent(0,Now,bytes+72)){Server.CancelPreparedPage(0);break;}
                    QuantityCharged+=bytes+72;sent++;if(message.Kind!=6 && message.Kind!=7)QuantityPages++;Queue(message);
                }
                Assert.That(sent,Is.LessThanOrEqualTo(_budget.Quantity));
                for(int i=0;i<_budget.Motion;i++)
                {
                    if(!_out.TryPreparePage(0,3,Vector3.zero,Now,Now,out var borrowed))break;
                    var message=CopyWire(borrowed,out int bytes);if(!_out.MarkPageSent(0,bytes))break;
                    MotionCharged+=bytes+72;MotionPages++;Queue(message);
                }
                Assert.That(Server.CompleteSendRound(),Is.True);
                for(int i=0;i<_budget.Client && _fifo.Count>0;i++)
                {
                    var page=_fifo.Dequeue();
                    if(page.Kind==8){Assert.That(_in.ReceivePage(page,Now,Now,false),Is.True);continue;}
                    if(!Client.AcceptsPage(page))continue;
                    bool accepted=Client.ReceivePage(page,(long)(Now*1000),out var reply);
                    // 故意过载的正控制可进入原Resync；只转交真实Assembler返回的响应，不造ACK。
                    if(reply!=null)Server.ReceiveControl(0,10,3,reply,Now);
                    if(!accepted)Assert.That(reply==null || reply.Kind==4,Is.True);
                    _in.Advance(Now,Now,false);
                }
                _in.Advance(Now,Now,false);Now+=1d/30;
                Assert.That(Server.Faulted || Client.Faulted,Is.False);
            }
            internal void Run50()
            {
                long before=QuantityCharged;double start=Now;
                for(int frame=0;frame<150;frame++)
                {if(frame%3==0){Source.Tick++;Capture();}Frame();}
                // 峰值burst可一次借用，不允许随窗口或Key重复补满。
                Assert.That(QuantityCharged-before,Is.LessThanOrEqualTo(_budget.Burst+_budget.Rate*(Now-start)+1));
                TestContext.Out.WriteLine($"requested quantity={_budget.Rate}/{_budget.Burst}/{_budget.Quantity},motion={_budget.Motion},client={_budget.Client}; onTime={OnTime}/50,accepted={Accepted},drops={Drops},peak={PeakQueue},quantityCharged={QuantityCharged-before},quantityPages={QuantityPages},motionPages={MotionPages}");
            }
            public void Dispose(){Server.Stop();Client.Stop();Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);}
        }

        [Test]
        public void ConfiguredFourPageRoundSharesExactEncodedChargesAndKeepsKeyRecoveryIndependent()
        {
            var source=new Load();var buffer=new WorldReplicaBuffer(256,0,1000000);
            Assert.That(source.TryCapture(buffer,out _),Is.True);
            var owner=new WorldReplicationCommitOwner(7,8,256,0,8,2048,1000000);Assert.That(owner.TryCaptureCommit(buffer),Is.True);
            var seven=typeof(WorldReplicationPeerState).GetConstructor(new[]{typeof(int),typeof(int),typeof(int),typeof(double),typeof(double),typeof(int),typeof(int)});
            var peer=seven!=null?(WorldReplicationPeerState)seven.Invoke(new object[]{8,2048,160,65536d,12288d,4,4}):new WorldReplicationPeerState(8,2048,160,65536,12288,4);
            peer.Reset(10,3);peer.BeginRound(0);
            var keys=new[]{new WorldChunkKey(WorldMaterialDomain.Liquid,0,0,0),new WorldChunkKey(WorldMaterialDomain.Liquid,3,0,0),
                new WorldChunkKey(WorldMaterialDomain.Liquid,1,0,0),new WorldChunkKey(WorldMaterialDomain.Liquid,2,0,0)};
            Assert.That(peer.TryFreeze(owner,keys[0],0),Is.True);Assert.That(peer.TryFreeze(owner,keys[0],0),Is.False,"空槽仍足够时，同Key也必须串行。");
            for(int i=1;i<4;i++)Assert.That(peer.TryFreeze(owner,keys[i],0),Is.True);
            var codec=new WorldPageCodec();var bytes=new byte[900];long charged=0;
            for(int i=0;i<2;i++)
            {
                var control=new WorldPageMessage{ProtocolVersion=ProtocolCompatibility.CurrentVersion,GameplayHash=99,WorldEpoch=7,
                    ConnectionEpoch=3,Kind=6,Domain=1,ChunkX=i,SubscriptionEpoch=(ulong)i+1};
                Assert.That(codec.TryEncode(control,new ArraySegment<byte>(bytes),out int encoded),Is.True);
                Assert.That(peer.TryMarkControlSent(0,encoded+72),Is.True);charged+=encoded+72;
            }
            int pages=0;var words=new uint[160];
            while(peer.TryPeekPage(words,out int count,out int index))
            {
                var h=peer.PendingHeader;
                var page=new WorldPageMessage{ProtocolVersion=ProtocolCompatibility.CurrentVersion,GameplayHash=99,WorldEpoch=h.WorldEpoch,
                    ConnectionEpoch=3,Kind=1,Domain=(uint)h.Key.Domain,ChunkX=h.Key.X,ChunkY=h.Key.Y,ChunkZ=h.Key.Z,
                    Generation=h.Generation,Revision=h.Revision,BaseRevision=h.BaseRevision,SnapshotId=h.SnapshotId,
                    ServerTick=(uint)h.ServerTick,Checksum=h.Checksum,SubscriptionEpoch=1,PageIndex=(uint)index,PageCount=(uint)peer.PageCount,
                    TotalWords=(uint)h.WordCount,WordOffset=(uint)(index*160)};
                for(int i=0;i<count;i++)page.Words.Add(words[i]);
                Assert.That(codec.TryEncode(page,new ArraySegment<byte>(bytes),out int encoded),Is.True);
                Assert.That(peer.TryMarkPageSent(0,encoded+72),Is.True);charged+=encoded+72;pages++;
                Assert.That(pages,Is.LessThanOrEqualTo(2));
            }
            Assert.That(pages,Is.EqualTo(2),"2个控制已经使用同一轮预算，还应容许恰好2个数据页；旧硬编码2会在真实Peek处停止。");
            Assert.That(peer.PagesSentThisRound,Is.EqualTo(4));Assert.That(peer.Tokens,Is.EqualTo(12288-charged).Within(.0001));
            Assert.That(peer.TryMarkControlSent(0,1),Is.False,"控制和数量共享总4页，不能各自4页。");
            peer.RequireBaseline(keys[1]);Assert.That(peer.HasPendingFor(keys[1]),Is.False);
            source.Tick++;Assert.That(source.TryCapture(buffer,out _),Is.True);Assert.That(owner.TryCaptureCommit(buffer),Is.True);
            Assert.That(peer.RestartTimedOutTransfer(3,2),Is.True);
            foreach(var key in new[]{keys[0],keys[2],keys[3]})
            {Assert.That(peer.TryGetPendingHeader(key,out var h),Is.True);Assert.That(h.SnapshotId,Is.EqualTo(1));Assert.That(h.Revision,Is.EqualTo(1),"超时仍使用原冻结版本，不借预算升级重捕新版。");}
            Assert.That(peer.Tokens,Is.EqualTo(12288-charged).Within(.0001),"恢复不额外补token。");
            var legacy=new WorldReplicationPeerState(8,2048,160,18000,3600);legacy.Reset(10,3);legacy.BeginRound(0);
            Assert.That(legacy.TryMarkControlSent(0,100),Is.True);Assert.That(legacy.TryMarkControlSent(0,100),Is.True);Assert.That(legacy.TryMarkControlSent(0,100),Is.False);
            Assert.That(legacy.MaximumInFlight,Is.EqualTo(1));Assert.That(legacy.Tokens,Is.EqualTo(3400));
        }

        [Test]
        public void Existing18KQuantityAndFourPageClientCannotSustainTheFixed10HzWindowLoad()
        {
            using var rig=new Rig(new Budget(18000,3600,2,4,4));rig.Run50();
            Assert.That(rig.OnTime,Is.LessThan(48),"负载必须真实暴露旧量桶/混合FIFO不足，不能用未变化的数量做吞吐正控制。");
        }
        [Test]
        public void Requested64KiBBoundedProductionBudgetPublishesAtLeast48Of50StrictWindows()
        {
            using var rig=new Rig(new Budget(64*1024,12*1024,4,8,8));rig.Run50();
            Assert.That(rig.OnTime,Is.GreaterThanOrEqualTo(48),"配置未接入时应在真实完整witness吞吐断言RED，而不是放松Quantity验证。");
            Assert.That(rig.Drops,Is.Zero);
        }
        [Test]
        public void FourPageClientRemainsABottleneckEvenWhenQuantitySendingIsRaised()
        {
            using var rig=new Rig(new Budget(64*1024,12*1024,4,8,4));rig.Run50();
            Assert.That(rig.OnTime,Is.LessThan(48),"170页/s无法被30fps×4页消费，不能仅升发送速率。");
        }
    }
}


