using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 真实数量Owner/Codec/ReplicaStore与只读运动Sink闭环。延迟ACK只延迟回包，不伪造客户端安装。
    /// 新ctor尚未存在时保留旧构造，RED必须失败在真实吞吐/生命周期断言，而不是找不到API。
    /// </summary>
    public sealed class WorldLiquidMotionMultiFlightTests
    {
        private sealed class MaterialSource:IWorldReplicationSource,IWorldLiquidMotionSource,IOnlineLiquidMotionWorld
        {
            // 非排序顺序故意让最高X地址位于Owner中间槽；退休它时必须搬动末槽及该Key的epoch。
            internal static readonly int[] X={0,48,16,32};
            internal uint Tick=1;
            internal int CellsPerChunk=1;
            internal readonly uint[] Current={20,20,20,20},Previous={20,20,20,20};
            internal static Vector3Int Cell(int index,int local=0)=>new Vector3Int(X[index]+local%8,2,2+local/8);
            public IWorldLiquidMotionSource Source=>this;
            public IWorldLiquidMotionSink Sink=>null;
            public bool TryCapture(WorldReplicaBuffer destination,out WorldCaptureFailure failure)
            {
                destination.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);
                int count=0;
                for(int i=0;i<4;i++)if(Current[i]>0)for(int local=0;local<CellsPerChunk;local++)
                    destination.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Cell(i,local),1,Current[i]);
                destination.Header=new WorldReplicaHeader(Tick,count,0,1);failure=default;return true;
            }
            public bool TryCapture(WorldLiquidMotionCaptureBuffer destination,WorldReplicaBuffer quantities)
            {
                destination.Epoch=Tick;destination.Interval=.1f;destination.ChunkSize=16;destination.Count=0;
                for(int i=0;i<4;i++)if(Current[i]>0 || Previous[i]>0)for(int local=0;local<CellsPerChunk;local++)
                {
                    int row=destination.Count++;destination.GroupIds[row]=0;
                    destination.Rows[row]=new WorldLiquidMotionSample(Cell(i,local),1,Previous[i],Current[i],
                        Vector3.zero,Vector3.down,Vector3.zero,Vector3.down);
                }
                return true;
            }
            internal void Next()
            {
                Array.Copy(Current,Previous,4);Tick++;
                for(int i=0;i<4;i++)Current[i]=(uint)(20+(i%2==0?-1:1)*(Tick-1));
            }
        }

        private sealed class Binding:IOnlineLiquidMotionWorld
        {public IWorldLiquidMotionSource Source=>null;public IWorldLiquidMotionSink Sink{get;set;}}

        private sealed class Flight:IDisposable
        {
            internal readonly MaterialSource Source=new MaterialSource();
            internal readonly WorldReplicaBuffer Buffer=new WorldReplicaBuffer(256,0,1000000);
            internal readonly WorldReplicationCoordinator Server,Client;
            internal readonly WorldLiquidMotionReplication MotionOut,MotionIn;
            internal readonly List<WorldPageMessage> Acks=new List<WorldPageMessage>(),Data=new List<WorldPageMessage>(),Controls=new List<WorldPageMessage>();
            internal int Connection=10;internal uint Epoch=3;internal double Now;
            internal double PresentationTime=10.025;
            internal long ChargedBytes;
            internal readonly object Store,ReadModel;
            private readonly ScriptableObject _profile,_water;
            private readonly WorldPageCodec _codec=new WorldPageCodec();
            private readonly byte[] _wire=new byte[WorldPageCodec.MaxPayloadBytes];
            internal Flight(int cellsPerChunk=1)
            {
                Source.CellsPerChunk=cellsPerChunk;
                var storeType=Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField",true);
                var profileType=Type.GetType("Game.ElementField.LiquidWorldProfile, Game.ElementField",true);
                var waterType=Type.GetType("Game.ElementField.LiquidBehaviorProfile, Game.ElementField",true);
                _profile=ScriptableObject.CreateInstance(profileType);_water=ScriptableObject.CreateInstance(waterType);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",_profile);
                var materials=Array.CreateInstance(waterType,1);materials.SetValue(_water,0);
                profileType.GetField("_materials",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_profile,materials);
                Store=Activator.CreateInstance(storeType,new object[]{7UL,8,256,0,2048});
                var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);
                Assert.That(storeType.GetMethod("ConfigureGeometry").Invoke(Store,new object[]{geometry}),Is.True);
                Assert.That(storeType.GetMethod("ConfigureMaterials").Invoke(Store,new object[]{WorldMaterialDomain.Liquid,510u,new byte[]{1},false,8}),Is.True);
                var readType=Type.GetType("Game.ElementField.WorldReplicaLiquidReadModel, Game.ElementField",true);
                ReadModel=Activator.CreateInstance(readType,new object[]{Store,geometry,_profile,.25f,null,null});
                Server=Coordinator(null);Client=Coordinator((IWorldChunkPayloadInstaller)Store);
                Assert.That(Server.BindPeer(0,Connection,Epoch),Is.True);Assert.That(Client.SetClientConnectionEpoch(Epoch),Is.True);
                MotionOut=new WorldLiquidMotionReplication(Server,Source);
                MotionIn=new WorldLiquidMotionReplication(Client,new Binding{Sink=(IWorldLiquidMotionSink)ReadModel});MotionIn.ResetConnection(Epoch);
                Capture();
            }
            private static WorldReplicationCoordinator Coordinator(IWorldChunkPayloadInstaller installer)
            {
                // 此组继续验证四在途但旧18K/2页预算。生产新增预算重载不应改变旧夹具语义，
                // 因此显式选择已实现的9参签名，避免“最长ctor”猜测把Missing填入新int参数。
                return new WorldReplicationCoordinator(7UL,99UL,8,2048,256,0,1000000,installer,4);
            }
            internal ulong Total=>(ulong)Store.GetType().GetMethod("TotalQuantity").Invoke(Store,new object[]{WorldMaterialDomain.Liquid});
            internal void Capture()
            {
                Assert.That(Server.Capture(Source,Buffer),Is.True,Server.Failure);
                Assert.That(Server.UpdatePeerInterest(0,Connection,Epoch,new Bounds(Vector3.zero,Vector3.one*1000)),Is.True);
            }
            internal void Pump(int rounds,bool acknowledge=false)
            {
                for(int round=0;round<rounds;round++)
                {
                    Server.BeginRound(Now);int sent=0;
                    for(int page=0;page<2;page++)
                    {
                        if(!Server.TryPreparePage(0,Now,out var borrowed))break;
                        Assert.That(_codec.TryEncode(borrowed,new ArraySegment<byte>(_wire),out int bytes),Is.True);
                        if(!Server.MarkPageSent(0,Now,bytes+72)){Server.CancelPreparedPage(0);break;}
                        ChargedBytes+=bytes+72;sent++;
                        Assert.That(_codec.TryDecode(new ArraySegment<byte>(_wire,0,bytes),out var decoded),Is.True);
                        if(decoded.Kind==6 || decoded.Kind==7)
                        {Controls.Add(decoded.Clone());Assert.That(Client.ReceiveSubscription(decoded),Is.True);}
                        else
                        {
                            Data.Add(decoded.Clone());Assert.That(Client.ReceivePage(decoded,(long)(Now*1000),out var reply),Is.True);
                            if(reply!=null)Acks.Add(reply.Clone());
                            MotionIn.Advance(Now,PresentationTime,false);
                        }
                    }
                    Assert.That(sent,Is.LessThanOrEqualTo(2),"四Flight仍共享每peer每Pump两页。");
                    Assert.That(Server.CompleteSendRound(),Is.True,Server.Failure);
                    if(acknowledge)AcknowledgeAll();
                    Now+=.02;
                    Assert.That(ChargedBytes,Is.LessThanOrEqualTo(3600+18000*Now+1),"完整编码与framing仍共用18KB/s+3600 burst。");
                }
            }
            internal void AcknowledgeAll()
            {for(int i=Acks.Count-1;i>=0;i--)Assert.That(Server.ReceiveControl(0,Connection,Epoch,Acks[i],Now),Is.True);Acks.Clear();}
            internal void Warm()
            {Pump(16,true);Assert.That(Total,Is.EqualTo(80));Data.Clear();Controls.Clear();Acks.Clear();}
            internal void FourPending()
            {
                Pump(12);
                var keys=new HashSet<int>();foreach(var page in Data)keys.Add(page.ChunkX);
                Assert.That(keys.Count,Is.EqualTo(4),"安装ACK延迟240ms时，四个不同Chunk必须都已开始，不能全peer只等待第一个Key。");
                Assert.That(Acks.Count,Is.EqualTo(4));
            }
            internal void SendMotion(double sampleTime)
            {
                PresentationTime=sampleTime+.025;MotionOut.Capture(Buffer,sampleTime);int sent=0;
                while(MotionOut.TryPreparePage(0,Epoch,Vector3.zero,Now,PresentationTime,out var page))
                {
                    Assert.That(_codec.TryEncode(page,new ArraySegment<byte>(_wire),out int bytes),Is.True);
                    Assert.That(MotionOut.MarkPageSent(0,bytes),Is.True);
                    Assert.That(_codec.TryDecode(new ArraySegment<byte>(_wire,0,bytes),out var decoded),Is.True);
                    Assert.That(MotionIn.ReceivePage(decoded,Now,PresentationTime,false),Is.True);
                    Assert.That(++sent,Is.LessThanOrEqualTo(11));
                }
                Assert.That(sent,Is.GreaterThan(0));
            }
            public void Dispose(){Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);}
        }

        /// <summary>只允许Mirror入批；任何socket/连接操作都使测试失败，不启动真实服务。</summary>
        private sealed class BatchTransport:Transport
        {
            public override bool Available()=>true;
            public override bool ClientConnected()=>false;
            public override void ClientConnect(string address)=>throw new NotSupportedException();
            public override void ClientSend(ArraySegment<byte> segment,int channelId=Channels.Reliable)=>throw new NotSupportedException();
            public override void ClientDisconnect()=>throw new NotSupportedException();
            public override Uri ServerUri()=>throw new NotSupportedException();
            public override bool ServerActive()=>false;
            public override void ServerStart()=>throw new NotSupportedException();
            public override void ServerSend(int connectionId,ArraySegment<byte> segment,int channelId=Channels.Reliable)=>throw new NotSupportedException();
            public override void ServerDisconnect(int connectionId)=>throw new NotSupportedException();
            public override string ServerGetClientAddress(int connectionId)=>"fixture";
            public override void ServerStop()=>throw new NotSupportedException();
            public override int GetMaxPacketSize(int channelId=Channels.Reliable)=>65536;
            public override int GetBatchThreshold(int channelId=Channels.Reliable)=>1400;
            public override void Shutdown(){}
        }

        private sealed class Ingress:IDisposable
        {
            private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
            private readonly GameObject _root=new GameObject("Inactive authenticated burst ingress"),_worldRoot=new GameObject("Inactive burst world");
            private readonly object _channel;
            private readonly Type _type=typeof(WorldReplicationNetworkChannel);
            private readonly WorldPageCodec _codec=new WorldPageCodec();
            private readonly byte[] _wire=new byte[WorldPageCodec.MaxPayloadBytes];
            private readonly PropertyInfo _connectionProperty=typeof(NetworkClient).GetProperty("connection");
            private readonly FieldInfo _state=typeof(NetworkClient).GetField("connectState",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
            private readonly NetworkConnectionToServer _oldConnection;
            private readonly object _oldState;
            private readonly FieldInfo _timeline=typeof(NetworkClient).GetField("localTimelineScaled",BindingFlags.Static|BindingFlags.NonPublic);
            private readonly double _oldTimeline;
            private readonly Transport _oldTransport;
            private readonly Action<NetworkWriter,WorldEnvelope> _oldWriter;
            private readonly System.Collections.IDictionary _handlers;
            private readonly bool _hadHandler;
            private readonly object _oldHandler;
            internal readonly List<WorldPageMessage> Replies=new List<WorldPageMessage>();
            internal int Queued=>(int)_type.GetProperty("QueuedPages").GetValue(_channel);
            internal long Rejected=>(long)_type.GetProperty("Rejected").GetValue(_channel);
            private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,Hidden).SetValue(owner,value);
            internal Ingress(Flight flight)
            {
                Assert.That(NetworkClient.active,Is.False,"不得接管用户已运行的Editor Client。");
                _root.SetActive(false);_worldRoot.SetActive(false);
                Assert.That(_timeline,Is.Not.Null);_oldTimeline=(double)_timeline.GetValue(null);
                _oldConnection=NetworkClient.connection;_oldState=_state.GetValue(null);_oldTransport=Transport.active;_oldWriter=Writer<WorldEnvelope>.write;
                _handlers=(System.Collections.IDictionary)typeof(NetworkClient).GetField("handlers",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).GetValue(null);
                ushort id=NetworkMessageId<WorldEnvelope>.Id;_hadHandler=_handlers.Contains(id);if(_hadHandler)_oldHandler=_handlers[id];
                _root.AddComponent<RoomNetworkManager>();var auth=_root.GetComponent<JoinTicketAuthenticator>();if(auth==null)auth=_root.AddComponent<JoinTicketAuthenticator>();
                var identity=new JoinConnectionIdentity(new string('a',32),new string('b',32),new string('c',32),1,flight.Epoch,1,"p7");
                _connectionProperty.SetValue(null,new NetworkConnectionToServer{isAuthenticated=true,authenticationData=identity});
                _state.SetValue(null,Enum.Parse(_state.FieldType,"Connected"));
                Set(auth,"<ClientAuthenticated>k__BackingField",true);Set(auth,"<ClientIdentity>k__BackingField",identity);Set(auth,"<ClientWorldEpoch>k__BackingField",7UL);
                var world=_worldRoot.AddComponent<OnlineWorldBootstrap>();Set(world,"<WorldReady>k__BackingField",true);Set(world,"<WorldEpoch>k__BackingField",7UL);Set(world,"_runId",identity.RunId);
                var catalog=Resources.Load<Game.Character.OnlineDungeonCatalog>("Online/DungeonCatalog");Assert.That(catalog.TryGet("p7",out var dungeon),Is.True);Set(world,"_dungeonDefinition",dungeon);
                Transport.active=_root.AddComponent<BatchTransport>();Writer<WorldEnvelope>.write=WorldEnvelopeSerialization.WriteWorldEnvelope;
                _channel=_root.AddComponent<WorldReplicationNetworkChannel>();_type.GetMethod("Configure").Invoke(_channel,new object[]{world,flight.Client});
                NetworkDiagnostics.OutMessageEvent+=CaptureReply;
            }
            private void CaptureReply(NetworkDiagnostics.MessageInfo info)
            {
                if(!(info.message is WorldEnvelope envelope))return;
                // SendReply复用输出数组；在真实Mirror发送通知中立即解码并拷贝，不能保存借用缓冲。
                Assert.That(_codec.TryDecode(envelope.Payload,out var reply),Is.True);Replies.Add(reply.Clone());
            }
            internal void Receive(WorldPageMessage page)
            {
                Assert.That(_codec.TryEncode(page,new ArraySegment<byte>(_wire),out int bytes),Is.True);
                _type.GetMethod("OnClientPage",Hidden).Invoke(_channel,new object[]{new WorldEnvelope{Payload=new ArraySegment<byte>(_wire,0,bytes)}});
            }
            internal void Pump(double now)=>_type.GetMethod("PumpClient",Hidden).Invoke(_channel,new object[]{now});
            internal void BindMotion(Flight flight)=>Set(_channel,"_motion",flight.MotionIn);
            internal void PresentationClock(double now)
            {
                // 这是正式Channel读取的同步播放时钟输入。保存/恢复Mirror静态值，避免依赖Editor墙钟；
                // 不修改数量Store、ACK、Published或任何待验counter。
                _timeline.SetValue(null,now);Assert.That(NetworkTime.time,Is.EqualTo(now));
            }
            internal void SendAuthorityIntoInbox(Flight flight,int rounds,bool drain=false,bool acknowledge=false)
            {
                for(int round=0;round<rounds;round++)
                {
                    flight.Server.BeginRound(flight.Now);
                    for(int page=0;page<2;page++)
                    {
                        if(!flight.Server.TryPreparePage(0,flight.Now,out var borrowed))break;
                        Assert.That(_codec.TryEncode(borrowed,new ArraySegment<byte>(_wire),out int bytes),Is.True);
                        if(!flight.Server.MarkPageSent(0,flight.Now,bytes+72)){flight.Server.CancelPreparedPage(0);break;}
                        flight.ChargedBytes+=bytes+72;Receive(borrowed.Clone());
                    }
                    Assert.That(flight.Server.CompleteSendRound(),Is.True);
                    if(drain)Pump(flight.Now);
                    if(acknowledge)AcknowledgeReplies(flight);
                    flight.Now+=.02;
                    Assert.That(flight.ChargedBytes,Is.LessThanOrEqualTo(3600+18000*flight.Now+1));
                }
            }
            internal void AcknowledgeReplies(Flight flight)
            {
                for(int i=Replies.Count-1;i>=0;i--)Assert.That(flight.Server.ReceiveControl(0,flight.Connection,flight.Epoch,Replies[i],flight.Now),Is.True);
                Replies.Clear();
            }
            internal WorldPageMessage ReceiveMotion(Flight flight,double sampleTime)
            {
                flight.MotionOut.Capture(flight.Buffer,sampleTime);WorldPageMessage last=null;int pages=0;
                while(flight.MotionOut.TryPreparePage(0,flight.Epoch,Vector3.zero,flight.Now,NetworkTime.time,out var borrowed))
                {
                    Assert.That(_codec.TryEncode(borrowed,new ArraySegment<byte>(_wire),out int bytes),Is.True);
                    Assert.That(flight.MotionOut.MarkPageSent(0,bytes),Is.True);
                    last=borrowed.Clone();Receive(last);Assert.That(++pages,Is.LessThanOrEqualTo(11));
                }
                Assert.That(pages,Is.GreaterThan(0));return last;
            }
            internal void ReceiveFourTwoPageFlights(Flight flight,int pagesPerChunk=2)
            {
                int data=0;long charged=0;
                // 模拟Client一段时间不Pump后的可靠回调突发；DS仍逐轮两页及同一18KB token。
                // 三页/Chunk夹具的完整编码需超过旧3600B burst；给旧18K桶足够真实轮次补额，
                // 不能把入口原子安装测试误变成0.32秒内必须突破发送带宽的断言。
                for(int round=0;round<32;round++)
                {
                    flight.Server.BeginRound(flight.Now);int sent=0;
                    for(int page=0;page<2;page++)
                    {
                        if(!flight.Server.TryPreparePage(0,flight.Now,out var borrowed))break;
                        Assert.That(_codec.TryEncode(borrowed,new ArraySegment<byte>(_wire),out int bytes),Is.True);
                        if(!flight.Server.MarkPageSent(0,flight.Now,bytes+72)){flight.Server.CancelPreparedPage(0);break;}
                        charged+=bytes+72;sent++;var message=borrowed.Clone();Receive(message);
                        if(message.Kind!=6 && message.Kind!=7){data++;Assert.That(message.PageCount,Is.EqualTo(pagesPerChunk));}
                    }
                    Assert.That(sent,Is.LessThanOrEqualTo(2));Assert.That(flight.Server.CompleteSendRound(),Is.True);flight.Now+=.02;
                    Assert.That(charged,Is.LessThanOrEqualTo(3600+18000*flight.Now+1));
                }
                Assert.That(data,Is.EqualTo(4*pagesPerChunk),"夹具必须真的收到四个不同在途Key的全部页，不能重复同一页灌满队列。");
            }
            public void Dispose()
            {
                NetworkDiagnostics.OutMessageEvent-=CaptureReply;
                _connectionProperty.SetValue(null,_oldConnection);_state.SetValue(null,_oldState);Transport.active=_oldTransport;
                _timeline.SetValue(null,_oldTimeline);
                Object.DestroyImmediate(_root);Object.DestroyImmediate(_worldRoot);Writer<WorldEnvelope>.write=_oldWriter;
                NetworkClient.UnregisterHandler<WorldEnvelope>();if(_hadHandler)_handlers[NetworkMessageId<WorldEnvelope>.Id]=_oldHandler;
            }
        }

        private static void WarmReceiveOrder(Flight flight,Ingress ingress)
        {
            ingress.BindMotion(flight);ingress.PresentationClock(10.025);
            ingress.SendAuthorityIntoInbox(flight,8,true,true);
            Assert.That(flight.Total,Is.EqualTo(80));Assert.That(flight.Server.Acknowledged,Is.EqualTo(4));
            ingress.ReceiveMotion(flight,10);ingress.Pump(flight.Now);
            Assert.That(flight.MotionIn.Published,Is.EqualTo(1));Assert.That(flight.MotionIn.LastPublishedSequence,Is.EqualTo(1UL));
            Assert.That(ingress.Queued,Is.Zero);Assert.That(ingress.Replies,Is.Empty);
        }

        [Test]
        public void QueuedQuantityPublishesTheOlderCompleteCandidateBeforeTheNextMotionCallbackCanReplaceIt()
        {
            using var f=new Flight();using var ingress=new Ingress(f);WarmReceiveOrder(f,ingress);
            f.Source.Next();f.Capture();ingress.PresentationClock(10.125);
            ingress.ReceiveMotion(f,10.1);ingress.Pump(f.Now);
            Assert.That(f.MotionIn.Published,Is.EqualTo(1),"完整seq2尚无四块Current数量事实，不能提前发布。");
            // 实际Codec→认证OnClientPage先收四块qty2，故可靠到包顺序正确；这里只暂停安装Pump。
            ingress.SendAuthorityIntoInbox(f,6);
            Assert.That(ingress.Queued,Is.EqualTo(4));Assert.That(ingress.Replies,Is.Empty);
            f.Source.Next();f.Capture();ingress.PresentationClock(10.15);
            ingress.ReceiveMotion(f,10.2); // 同一EarlyUpdate batch内，下一Seq紧随已排队数量页回调。
            ingress.Pump(f.Now);
            Assert.That(ingress.Replies.Count,Is.EqualTo(4),"必须真的安装四个完整qty2并产生真实ACK。");
            Assert.That(f.MotionIn.Published,Is.EqualTo(2),"qty2先到但未Pump时，下一Motion不能先销毁仍在TTL内的完整seq2候选。");
            Assert.That(f.MotionIn.LastPublishedSequence,Is.EqualTo(2UL));
            object[] phase={2UL,0f};Assert.That(f.ReadModel.GetType().GetMethod("TryGetPresentationAlpha").Invoke(f.ReadModel,phase),Is.True);
            Assert.That((float)phase[1],Is.EqualTo(.5f).Within(.001f),"保留下来的相邻窗口应按真实播放时钟进入中间相位。");
            ingress.AcknowledgeReplies(f);
            ingress.Pump(f.Now+.02); // seq3的Current数量仍未安装，不能用seq2事实给seq3发布资格。
            Assert.That(f.MotionIn.Published,Is.EqualTo(2));Assert.That(f.Total,Is.EqualTo(80));
            Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }

        [Test]
        public void SynchronousLeavePurgesQueuedQuantityAndMotionBeforeEitherCanResurrectTheOldSubscription()
        {
            using var f=new Flight();using var ingress=new Ingress(f);WarmReceiveOrder(f,ingress);
            f.Source.Next();f.Capture();ingress.PresentationClock(10.125);ingress.ReceiveMotion(f,10.1);ingress.Pump(f.Now);
            ingress.SendAuthorityIntoInbox(f,6);Assert.That(ingress.Queued,Is.EqualTo(4));
            f.Source.Next();f.Capture();ingress.PresentationClock(10.15);var oldMotion=ingress.ReceiveMotion(f,10.2);
            Assert.That(f.Server.UpdatePeerInterest(0,f.Connection,f.Epoch,new Bounds(new Vector3(999,0,0),Vector3.one)),Is.True);
            // Leave来自正式Coordinator，而非测试伪造字段；控制回调必须立刻撤销并压缩固定入口队列。
            ingress.SendAuthorityIntoInbox(f,4);
            Assert.That(ingress.Queued,Is.Zero,"Leave不能排到旧数量/运动之后才撤销资格。");
            Assert.That(f.Total,Is.Zero);Assert.That(ingress.Replies,Is.Empty);
            Assert.That(f.ReadModel.GetType().GetProperty("PresentationWindowId").GetValue(f.ReadModel),Is.EqualTo(0UL));
            ingress.Receive(oldMotion);ingress.Pump(f.Now);
            Assert.That(f.MotionIn.Published,Is.EqualTo(1));Assert.That(f.Total,Is.Zero);
            Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }

        [Test]
        public void FourLegalTwoPageFlightsSurviveOneClientReceiveBurstAndAckWithoutRetry()
        {
            using var f=new Flight(32);using var ingress=new Ingress(f);ingress.ReceiveFourTwoPageFlights(f);
            Assert.That(ingress.Queued,Is.EqualTo(8),"四在途产生的合法八页回调不能在固定四槽入口丢掉后半批。");
            Assert.That(ingress.Rejected,Is.Zero);Assert.That(f.Total,Is.Zero,"回调只排队，不提前安装或ACK。");
            for(int pump=0;pump<8 && ingress.Queued>0;pump++)ingress.Pump(f.Now+pump*.02);
            Assert.That(ingress.Queued,Is.Zero);Assert.That(ingress.Replies.Count,Is.EqualTo(4));Assert.That(f.Total,Is.EqualTo(2560));
            foreach(var ack in ingress.Replies){Assert.That(ack.Kind,Is.EqualTo(3));Assert.That(f.Server.ReceiveControl(0,f.Connection,f.Epoch,ack,f.Now+.2),Is.True);}
            Assert.That(f.Server.Acknowledged,Is.EqualTo(4));Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }

        [Test]
        public void ClientDrainsExactlyEightQueuedPagesPerPumpAndKeepsChunkInstallAtomic()
        {
            // 每块三页，前8页只是四个Chunk各自的前两页；增加消费预算后仍须验证半块不安装。
            using var f=new Flight(64);using var ingress=new Ingress(f);ingress.ReceiveFourTwoPageFlights(f,3);
            Assert.That(ingress.Queued,Is.EqualTo(12));Assert.That(ingress.Rejected,Is.Zero);
            ingress.Pump(f.Now);Assert.That(ingress.Queued,Is.EqualTo(4),"Client每Pump最多八页，独立旧DS夹具仍每轮两页。");
            Assert.That(ingress.Replies,Is.Empty);Assert.That(f.Total,Is.Zero,"未齐三页的Chunk不能发布部分数量或提前ACK。");
            ingress.Pump(f.Now+.02);Assert.That(ingress.Queued,Is.Zero);Assert.That(ingress.Replies.Count,Is.EqualTo(4));Assert.That(f.Total,Is.EqualTo(5120));
        }

        [Test]
        public void FourDifferentChunksInstallBeforeAnyAckAndAcceptReverseOrderReplies()
        {
            using var f=new Flight();f.FourPending();Assert.That(f.Server.Acknowledged,Is.Zero);Assert.That(f.Total,Is.EqualTo(80));
            var duplicate=f.Acks[0].Clone();f.AcknowledgeAll();Assert.That(f.Server.Acknowledged,Is.EqualTo(4));
            Assert.That(f.Server.ReceiveControl(0,f.Connection,f.Epoch,duplicate,f.Now),Is.False);
            Assert.That(f.Total,Is.EqualTo(80));Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }

        [Test]
        public void UpdatingOneKeyWaitsForItsOwnAckWithoutBlockingOrReplacingOtherFrozenKeys()
        {
            using var f=new Flight();f.FourPending();var first=f.Acks[0].Clone();
            f.Source.Next();f.Capture();int sent=f.Data.Count;f.Pump(3);
            Assert.That(f.Data.Count,Is.EqualTo(sent),"同Key在途时不得冻结第二份更新，也不能覆盖其冻结正文。");
            Assert.That(f.Server.ReceiveControl(0,f.Connection,f.Epoch,first,f.Now),Is.True);f.Acks.RemoveAt(0);
            f.Pump(5);Assert.That(f.Data.Count,Is.EqualTo(sent+1));Assert.That(f.Data[f.Data.Count-1].ChunkX,Is.EqualTo(first.ChunkX));
            Assert.That(f.Data[f.Data.Count-1].Revision,Is.GreaterThan(first.Revision));
            Assert.That(f.Server.ReceiveControl(0,f.Connection,f.Epoch,first,f.Now),Is.False,"旧同Key ACK不能消费新冻结版本。");
            f.AcknowledgeAll();Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }

        [Test]
        public void ResyncForOneFrozenKeyKeepsTheOtherThreeAckIdentitiesAndForcesOnlyItsBaseline()
        {
            using var f=new Flight();f.FourPending();var resync=f.Acks[1].Clone();resync.Kind=4;
            f.Source.Next();f.Capture();Assert.That(f.Server.ReceiveControl(0,f.Connection,f.Epoch,resync,f.Now),Is.True);
            f.Acks.RemoveAt(1);int before=f.Data.Count;f.Pump(5);
            Assert.That(f.Data.Count,Is.EqualTo(before+1));var forced=f.Data[f.Data.Count-1];
            Assert.That(forced.ChunkX,Is.EqualTo(resync.ChunkX));Assert.That(forced.Kind,Is.EqualTo(1));Assert.That(forced.BaseRevision,Is.Zero);
            f.AcknowledgeAll();Assert.That(f.Server.ResyncAccepted,Is.EqualTo(1));Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }

        [Test]
        public void LeavingAndRetiringAMiddleOwnerSlotDoesNotInvalidateOtherInFlightSubscriptionEpochs()
        {
            using var f=new Flight();f.Warm();f.Source.Next();f.Capture();f.FourPending();
            WorldPageMessage closed=null;var remaining=new List<WorldPageMessage>();
            foreach(var ack in f.Acks)if(ack.ChunkX==3)closed=ack.Clone();else remaining.Add(ack.Clone());
            Assert.That(closed,Is.Not.Null);f.Acks.Clear();
            Array.Copy(f.Source.Current,f.Source.Previous,4);f.Source.Tick++;f.Source.Current[1]=0;f.Capture();
            Assert.That(f.Server.UpdatePeerInterest(0,f.Connection,f.Epoch,new Bounds(new Vector3(10,0,0),new Vector3(22,100,100))),Is.True);
            f.Pump(3);
            Assert.That(f.Server.RetiredKeys,Is.GreaterThan(0));Assert.That(f.Server.MetadataKeys,Is.EqualTo(3));
            Assert.That(f.Server.ReceiveControl(0,f.Connection,f.Epoch,closed,f.Now),Is.False,"旧Leave代的ACK不能命中已搬槽Key。");
            for(int i=remaining.Count-1;i>=0;i--)Assert.That(f.Server.ReceiveControl(0,f.Connection,f.Epoch,remaining[i],f.Now),Is.True,
                "Owner中间槽退休后，保留Flight仍须使用其自身Key的订阅代次。");
            Assert.That(f.Total,Is.EqualTo(59));Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }

        [Test]
        public void FourChunkQuantityWitnessPublishesOneCompleteMotionWindowWhileAcksAreStillDelayed()
        {
            using var f=new Flight();f.Warm();f.SendMotion(10);Assert.That(f.MotionIn.Published,Is.EqualTo(1));
            f.Source.Next();f.Capture();f.SendMotion(10.1);
            Assert.That(f.MotionIn.Published,Is.EqualTo(1),"完整sidecar不能提前授予尚未安装的数量事实。");
            f.FourPending();Assert.That(f.MotionIn.Published,Is.EqualTo(2),"四Key已实际安装时应立即发布，不能继续卡在第一个Key的ACK。");
            object[] phase={2UL,0f};Assert.That(f.ReadModel.GetType().GetMethod("TryGetPresentationAlpha").Invoke(f.ReadModel,phase),Is.True);
            Assert.That((float)phase[1],Is.EqualTo(.25f).Within(.001f));Assert.That(f.Total,Is.EqualTo(80));
            f.AcknowledgeAll();Assert.That(f.Total,Is.EqualTo(80));
        }

        [Test]
        public void ReconnectClearsAllFourFrozenTransfersAndRejectsEveryOldAck()
        {
            using var f=new Flight();f.FourPending();var old=f.Acks.ToArray();f.Acks.Clear();f.Data.Clear();
            f.Connection=11;f.Epoch=4;Assert.That(f.Server.BindPeer(0,f.Connection,f.Epoch),Is.True);
            Assert.That(f.Client.SetClientConnectionEpoch(f.Epoch),Is.True);f.MotionIn.ResetConnection(f.Epoch);
            Assert.That(f.Server.UpdatePeerInterest(0,f.Connection,f.Epoch,new Bounds(Vector3.zero,Vector3.one*1000)),Is.True);
            foreach(var ack in old)Assert.That(f.Server.ReceiveControl(0,10,3,ack,f.Now),Is.False);
            f.Pump(16,true);Assert.That(f.Total,Is.EqualTo(80));
            Assert.That(f.Data.Count,Is.EqualTo(4));foreach(var page in f.Data){Assert.That(page.Kind,Is.EqualTo(1));Assert.That(page.BaseRevision,Is.Zero);}
            Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }
    }
}
