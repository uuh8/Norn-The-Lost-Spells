using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Character;
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
    /// 实际Authority Channel.Pump→Mirror入批→Codec→已认证Client Channel→Store/ACK闭环。
    /// Transport只提供包长，不监听端口；测试不复刻待验发送loop，也不修改Published/ACK/token计数。
    /// </summary>
    public sealed class WorldLiquidMotionSendOrderTests
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,Hidden).SetValue(owner,value);
        private sealed class MaterialSource:IWorldReplicationSource,IWorldLiquidMotionSource,IOnlineLiquidMotionWorld
        {
            internal uint Tick=1;internal int Shift,PreviousShift;internal bool Collected;
            internal static Vector3Int Cell(int key,int local)=>new Vector3Int(key*16+local%8,2,2+local/8);
            public IWorldLiquidMotionSource Source=>this;
            public IWorldLiquidMotionSink Sink=>null;
            private uint Quantity(int local,int shift)=>(uint)(20+(local%2==0?-shift:shift));
            public bool TryCapture(WorldReplicaBuffer target,out WorldCaptureFailure failure)
            {
                target.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.LegacyCell,Vector3.zero,.25f,16);
                target.Geometries[1]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);int count=0;
                for(int key=0;key<4;key++)if(!Collected || key!=0)for(int local=0;local<32;local++)
                    target.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Cell(key,local),1,Quantity(local,Shift));
                target.Header=new WorldReplicaHeader(Tick,count,0,2);failure=default;return true;
            }
            public bool TryCapture(WorldLiquidMotionCaptureBuffer target,WorldReplicaBuffer quantities)
            {
                target.Epoch=Tick;target.Interval=.1f;target.ChunkSize=16;target.Count=0;
                // 真实采集不是Solver搬运组：不为外部扣量伪造守恒运动，数量仍须即时发送。
                if(Collected)return true;
                for(int key=0;key<4;key++)for(int local=0;local<32;local++)
                {int at=target.Count++;target.GroupIds[at]=key;target.Rows[at]=new WorldLiquidMotionSample(Cell(key,local),1,
                    Quantity(local,PreviousShift),Quantity(local,Shift),Vector3.zero,Vector3.down,Vector3.zero,Vector3.down);}
                return true;
            }
            internal void Next(){PreviousShift=Shift;Shift++;Tick++;}
        }
        private sealed class Binding:IOnlineLiquidMotionWorld
        {public IWorldLiquidMotionSource Source=>null;public IWorldLiquidMotionSink Sink{get;set;}}
        private sealed class BatchTransport:Transport
        {
            public override bool Available()=>true;public override bool ClientConnected()=>false;
            public override void ClientConnect(string address)=>throw new NotSupportedException();
            public override void ClientSend(ArraySegment<byte> segment,int channelId=Channels.Reliable)=>throw new NotSupportedException();
            public override void ClientDisconnect()=>throw new NotSupportedException();
            public override Uri ServerUri()=>throw new NotSupportedException();public override bool ServerActive()=>false;
            public override void ServerStart()=>throw new NotSupportedException();
            public override void ServerSend(int id,ArraySegment<byte> segment,int channelId=Channels.Reliable)=>throw new NotSupportedException();
            public override void ServerDisconnect(int id)=>throw new NotSupportedException();public override string ServerGetClientAddress(int id)=>"fixture";
            public override void ServerStop()=>throw new NotSupportedException();public override void Shutdown(){}
            public override int GetMaxPacketSize(int channelId=Channels.Reliable)=>65536;
            public override int GetBatchThreshold(int channelId=Channels.Reliable)=>1400;
        }
        private sealed class Fixture:IDisposable
        {
            private const int Connection=65241;
            private readonly GameObject _sendRoot=new GameObject("Inactive true material sender"),_receiveRoot=new GameObject("Inactive true material receiver"),
                _serverWorldRoot=new GameObject("Inactive sender world"),_clientWorldRoot=new GameObject("Inactive receiver world");
            private readonly PropertyInfo _serverActive=typeof(NetworkServer).GetProperty("active"),_clientConnection=typeof(NetworkClient).GetProperty("connection");
            private readonly FieldInfo _clientState=typeof(NetworkClient).GetField("connectState",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public),
                _timeline=typeof(NetworkClient).GetField("localTimelineScaled",BindingFlags.Static|BindingFlags.NonPublic);
            private readonly object _oldClientState;private readonly double _oldTimeline;private readonly NetworkConnectionToServer _oldClientConnection;
            private readonly Transport _oldTransport;private readonly Action<NetworkWriter,WorldEnvelope> _oldWriter;
            private readonly System.Collections.IDictionary _clientHandlers;private readonly bool _hadHandler;private readonly object _oldHandler;
            private readonly ScriptableObject _profile,_water;private readonly OnlineWorldBootstrap _serverWorld,_clientWorld;
            private readonly NetworkConnectionToClient _connection;
            private readonly WorldReplicationNetworkChannel _sender,_receiver;
            private readonly WorldPageCodec _codec=new WorldPageCodec();private readonly byte[] _wire=new byte[900];
            internal readonly MaterialSource State=new MaterialSource();internal readonly WorldReplicaBuffer Buffer=new WorldReplicaBuffer(256,0,1000000);
            internal readonly WorldReplicationCoordinator Server,Client;internal readonly WorldLiquidMotionReplication MotionOut,MotionIn;
            internal readonly object Store,ReadModel;internal readonly List<WorldPageMessage> Outgoing=new List<WorldPageMessage>(),Replies=new List<WorldPageMessage>();
            internal double Now;internal double SampleTime;
            internal int Queued=>_receiver.QueuedPages;
            internal ulong Total=>(ulong)Store.GetType().GetMethod("TotalQuantity").Invoke(Store,new object[]{WorldMaterialDomain.Liquid});
            internal Fixture()
            {
                Assert.That(NetworkServer.active || NetworkClient.active,Is.False,"不接管用户现有Server/Client。");
                Assert.That(NetworkServer.connections.ContainsKey(Connection),Is.False);
                _sendRoot.SetActive(false);_receiveRoot.SetActive(false);_serverWorldRoot.SetActive(false);_clientWorldRoot.SetActive(false);
                _oldTransport=Transport.active;_oldWriter=Writer<WorldEnvelope>.write;_oldClientConnection=NetworkClient.connection;
                _oldClientState=_clientState.GetValue(null);_oldTimeline=(double)_timeline.GetValue(null);
                _clientHandlers=(System.Collections.IDictionary)typeof(NetworkClient).GetField("handlers",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).GetValue(null);
                ushort messageId=NetworkMessageId<WorldEnvelope>.Id;_hadHandler=_clientHandlers.Contains(messageId);if(_hadHandler)_oldHandler=_clientHandlers[messageId];
                try
                {
                var storeType=Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField",true);
                var profileType=Type.GetType("Game.ElementField.LiquidWorldProfile, Game.ElementField",true);
                var waterType=Type.GetType("Game.ElementField.LiquidBehaviorProfile, Game.ElementField",true);
                _profile=ScriptableObject.CreateInstance(profileType);_water=ScriptableObject.CreateInstance(waterType);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",_profile);
                var materials=Array.CreateInstance(waterType,1);materials.SetValue(_water,0);profileType.GetField("_materials",Hidden).SetValue(_profile,materials);
                Store=Activator.CreateInstance(storeType,new object[]{7UL,8,256,0,2048});
                var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);
                Assert.That(storeType.GetMethod("ConfigureGeometry").Invoke(Store,new object[]{geometry}),Is.True);
                Assert.That(storeType.GetMethod("ConfigureMaterials").Invoke(Store,new object[]{WorldMaterialDomain.Liquid,510u,new byte[]{1},false,8}),Is.True);
                var readType=Type.GetType("Game.ElementField.WorldReplicaLiquidReadModel, Game.ElementField",true);
                ReadModel=Activator.CreateInstance(readType,new object[]{Store,geometry,_profile,.25f,null,null});
                Server=Coordinator(null);Client=Coordinator((IWorldChunkPayloadInstaller)Store);Assert.That(Client.SetClientConnectionEpoch(3),Is.True);
                MotionOut=new WorldLiquidMotionReplication(Server,State);MotionIn=new WorldLiquidMotionReplication(Client,new Binding{Sink=(IWorldLiquidMotionSink)ReadModel});MotionIn.ResetConnection(3);
                var manager=_sendRoot.AddComponent<RoomNetworkManager>();var sessions=(ConnectionSession[])typeof(RoomNetworkManager).GetField("_sessions",Hidden).GetValue(manager);
                for(int i=0;i<sessions.Length;i++)sessions[i]=new ConnectionSession(10,4);sessions[0].Attach(Connection);
                var identity=new JoinConnectionIdentity(new string('a',32),new string('b',32),new string('c',32),1,3,1,"p7");
                _connection=new NetworkConnectionToClient(Connection){isAuthenticated=true,authenticationData=identity};NetworkServer.connections.Add(Connection,_connection);
                _serverWorld=MakeWorld(_serverWorldRoot,identity);_clientWorld=MakeWorld(_clientWorldRoot,identity);
                var movement=Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
                var registry=new NetworkActorRegistry(movement,_serverWorldRoot.transform);var actor=registry.EnsurePlayer(0,PlayerExecutionMode.Authority);
                actor.Body.position=Vector3.zero;actor.Connected=actor.SimulationReady=true;actor.Epoch=3;actor.ConnectionId=Connection;
                Set(_serverWorld,"<Registry>k__BackingField",registry);Set(_serverWorld,"<IsAuthority>k__BackingField",true);
                Assert.That(Server.ConfigureInterest(4,8,8),Is.True);
                _receiveRoot.AddComponent<RoomNetworkManager>();var auth=_receiveRoot.GetComponent<JoinTicketAuthenticator>();
                if(auth==null)auth=_receiveRoot.AddComponent<JoinTicketAuthenticator>();
                Set(auth,"<ClientAuthenticated>k__BackingField",true);Set(auth,"<ClientIdentity>k__BackingField",identity);Set(auth,"<ClientWorldEpoch>k__BackingField",7UL);
                _clientConnection.SetValue(null,new NetworkConnectionToServer{isAuthenticated=true,authenticationData=identity});
                _clientState.SetValue(null,Enum.Parse(_clientState.FieldType,"Connected"));
                Transport.active=_sendRoot.AddComponent<BatchTransport>();Writer<WorldEnvelope>.write=WorldEnvelopeSerialization.WriteWorldEnvelope;
                _sender=_sendRoot.AddComponent<WorldReplicationNetworkChannel>();_sender.Configure(_serverWorld,Server);Set(_sender,"_motion",MotionOut);
                _receiver=_receiveRoot.AddComponent<WorldReplicationNetworkChannel>();_receiver.Configure(_clientWorld,Client);Set(_receiver,"_motion",MotionIn);
                NetworkDiagnostics.OutMessageEvent+=CaptureOutgoing;Capture();
                }
                catch{Dispose();throw;}
            }
            private static WorldReplicationCoordinator Coordinator(IWorldChunkPayloadInstaller installer)
            {
                ConstructorInfo best=null;foreach(var ctor in typeof(WorldReplicationCoordinator).GetConstructors())if(best==null || ctor.GetParameters().Length>best.GetParameters().Length)best=ctor;
                var parameters=best.GetParameters();var args=new object[parameters.Length];object[] original={7UL,99UL,8,2048,256,0,1000000,installer};
                for(int i=0;i<parameters.Length;i++)
                {
                    if(i<8){args[i]=original[i];continue;}
                    string name=parameters[i].Name.ToLowerInvariant();object value=name.Contains("flight")?4:name.Contains("burst")?12288:name.Contains("byte")?65536:name.Contains("page")?4:parameters[i].DefaultValue;
                    args[i]=Convert.ChangeType(value,parameters[i].ParameterType);
                }
                return (WorldReplicationCoordinator)best.Invoke(args);
            }
            private static OnlineWorldBootstrap MakeWorld(GameObject root,JoinConnectionIdentity identity)
            {
                var world=root.AddComponent<OnlineWorldBootstrap>();Set(world,"<WorldReady>k__BackingField",true);Set(world,"<WorldEpoch>k__BackingField",7UL);Set(world,"_runId",identity.RunId);
                var catalog=Resources.Load<OnlineDungeonCatalog>("Online/DungeonCatalog");Assert.That(catalog.TryGet("p7",out var dungeon),Is.True);Set(world,"_dungeonDefinition",dungeon);return world;
            }
            private void ActiveServer(bool value)=>_serverActive.GetSetMethod(true).Invoke(null,new object[]{value});
            private void CaptureOutgoing(NetworkDiagnostics.MessageInfo info)
            {
                if(!(info.message is WorldEnvelope envelope))return;
                Assert.That(_codec.TryDecode(envelope.Payload,out var page),Is.True);
                if(page.Kind==3 || page.Kind==4)Replies.Add(page.Clone());else Outgoing.Add(page.Clone());
            }
            internal void Capture()
            {Assert.That(Server.Capture(State,Buffer),Is.True,Server.Failure);}
            internal void CaptureMotion()
            {
                // 本夹具隔离发送顺序。EditMode的Unity时钟可不前进，不据此宣称相邻窗口Alpha；
                // hold/数量事实和正式发送路径仍全部真实执行，连续时钟由Native验收。
                ActiveServer(true);SampleTime=NetworkTime.time;MotionOut.Capture(Buffer,SampleTime);ActiveServer(false);
            }
            internal void PumpSender()
            {
                ActiveServer(true);typeof(WorldReplicationNetworkChannel).GetMethod("PumpAuthority",Hidden).Invoke(_sender,new object[]{Now});ActiveServer(false);Now+=1d/30;
            }
            internal void DeliverOutgoing()
            {
                ActiveServer(false);_timeline.SetValue(null,SampleTime+.025);
                foreach(var page in Outgoing)
                {Assert.That(_codec.TryEncode(page,new ArraySegment<byte>(_wire),out int size),Is.True);typeof(WorldReplicationNetworkChannel).GetMethod("OnClientPage",Hidden).Invoke(_receiver,new object[]{new WorldEnvelope{Payload=new ArraySegment<byte>(_wire,0,size)}});}
                Outgoing.Clear();
            }
            internal void PumpClient()
            {ActiveServer(false);typeof(WorldReplicationNetworkChannel).GetMethod("PumpClient",Hidden).Invoke(_receiver,new object[]{Now});}
            internal void Ack()
            {
                ActiveServer(true);
                foreach(var reply in Replies){Assert.That(_codec.TryEncode(reply,new ArraySegment<byte>(_wire),out int size),Is.True);typeof(WorldReplicationNetworkChannel).GetMethod("OnServerControl",Hidden).Invoke(_sender,new object[]{_connection,new WorldEnvelope{Payload=new ArraySegment<byte>(_wire,0,size)}});}
                Replies.Clear();ActiveServer(false);
            }
            internal void Warm()
            {
                for(int i=0;i<30;i++){PumpSender();DeliverOutgoing();while(Queued>0)PumpClient();Ack();}
                Assert.That(Total,Is.EqualTo(2560UL));CaptureMotion();
                for(int i=0;i<4;i++){PumpSender();DeliverOutgoing();while(Queued>0)PumpClient();}
                Assert.That(MotionIn.Published,Is.EqualTo(1));Outgoing.Clear();Replies.Clear();
                Now+=.2;
            }
            internal void MoveOutOfHighestChunk()=>_serverWorld.Registry[0].Body.position=new Vector3(-20,0,0);
            internal WorldPageMessage SendOneMotionPage()
            {
                ActiveServer(true);
                Assert.That(MotionOut.TryPreparePage(0,3,_serverWorld.Registry[0].Body.position,Now,NetworkTime.time,out var page),Is.True);
                Assert.That(_codec.TryEncode(page,new ArraySegment<byte>(_wire),out int bytes),Is.True);Assert.That(MotionOut.MarkPageSent(0,bytes),Is.True);
                var copy=page.Clone();_connection.Send(new WorldEnvelope{Payload=new ArraySegment<byte>(_wire,0,bytes)},Channels.Reliable);ActiveServer(false);return copy;
            }
            internal void DrainMotionBudget()
            {
                long before=MotionOut.ChargedBytesSent;int sent=0;
                // 通过合法编码/Mark/Send消耗同一时刻真实token；不反射写Tokens或页游标。
                for(int window=0;window<2;window++)
                {
                    State.Next();Capture();CaptureMotion();ActiveServer(true);
                    while(MotionOut.TryPreparePage(0,3,Vector3.zero,Now,NetworkTime.time,out var page))
                    {Assert.That(_codec.TryEncode(page,new ArraySegment<byte>(_wire),out int bytes),Is.True);Assert.That(MotionOut.MarkPageSent(0,bytes),Is.True);
                        _connection.Send(new WorldEnvelope{Payload=new ArraySegment<byte>(_wire,0,bytes)},Channels.Reliable);Assert.That(++sent,Is.LessThan(30));}
                    ActiveServer(false);
                }
                Assert.That(sent,Is.GreaterThan(0));Assert.That(MotionOut.ChargedBytesSent-before,Is.LessThanOrEqualTo(WorldLiquidMotionReplication.BurstBytes));Outgoing.Clear();
            }
            internal bool Holding=>(bool)ReadModel.GetType().GetProperty("PresentationHolding").GetValue(ReadModel);
            public void Dispose()
            {
                NetworkDiagnostics.OutMessageEvent-=CaptureOutgoing;ActiveServer(false);NetworkServer.connections.Remove(Connection);
                if(_serverWorld!=null)Set(_serverWorld,"_stopped",true);if(_clientWorld!=null)Set(_clientWorld,"_stopped",true);
                Object.DestroyImmediate(_sendRoot);Object.DestroyImmediate(_receiveRoot);Object.DestroyImmediate(_serverWorldRoot);Object.DestroyImmediate(_clientWorldRoot);
                Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);
                _clientConnection.SetValue(null,_oldClientConnection);_clientState.SetValue(null,_oldClientState);_timeline.SetValue(null,_oldTimeline);Transport.active=_oldTransport;Writer<WorldEnvelope>.write=_oldWriter;
                NetworkClient.UnregisterHandler<WorldEnvelope>();if(_hadHandler)_clientHandlers[NetworkMessageId<WorldEnvelope>.Id]=_oldHandler;
            }
        }

        [Test]
        public void RealAuthorityPumpPlacesTheWholeMultiPageMotionBeforeItsFirstQuantityData()
        {
            using var f=new Fixture();f.Warm();f.State.Next();f.Capture();f.CaptureMotion();f.PumpSender();
            int motion=0,expected=0,firstQuantity=-1,lastMotion=-1;ulong sequence=0;
            for(int i=0;i<f.Outgoing.Count;i++)
            {var page=f.Outgoing[i];if(page.Kind==8){motion++;expected=(int)page.PageCount;lastMotion=i;sequence=page.SnapshotId;}else if(page.Kind<=2 || page.Kind==5){if(firstQuantity<0)firstQuantity=i;}}
            Assert.That(expected,Is.GreaterThan(4),"必须覆盖真实多于4页的完整运动窗口，不能拿单页输入假过。");
            Assert.That(motion,Is.EqualTo(expected),"原M4上限会把完整候选拆到数量之后。");
            Assert.That(firstQuantity,Is.GreaterThan(lastMotion),"新量改变旧Current之前，完整motion必须先进入同一可靠流。");
            Assert.That(sequence,Is.GreaterThan(0));Assert.That(f.Server.Faulted,Is.False);
        }

        [Test]
        public void TwoSenderRoundsFitOneBurstAndStageBeforeQuantityWithoutLosingTheOldPose()
        {
            using var f=new Fixture();f.Warm();f.State.Next();f.Capture();f.CaptureMotion();
            f.PumpSender();f.PumpSender();f.DeliverOutgoing();
            Assert.That(f.Queued,Is.GreaterThan(16),"实际两轮完整M加八页Q应超出旧16槽，不能靠重复页灌队列。");
            f.PumpClient();Assert.That(f.Replies,Is.Empty);Assert.That(f.Total,Is.EqualTo(2560UL));
            f.PumpClient();Assert.That(f.Holding,Is.True,"候选应先完整Stage；前三块Current改变时仍能验证并hold旧pose。");
            Assert.That(f.Replies.Count,Is.EqualTo(3));
            f.PumpClient();Assert.That(f.MotionIn.Published,Is.EqualTo(2));Assert.That(f.Replies.Count,Is.EqualTo(4));
            f.Ack();Assert.That(f.Total,Is.EqualTo(2560UL));Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }

        [Test]
        public void ExistingLeaveHasPriorityOverMotionForOtherStillSubscribedChunks()
        {
            using var f=new Fixture();f.Warm();f.MoveOutOfHighestChunk();f.State.Next();f.Capture();f.CaptureMotion();f.PumpSender();
            int firstLeave=-1,firstMotion=-1;
            for(int i=0;i<f.Outgoing.Count;i++){if(f.Outgoing[i].Kind==7 && firstLeave<0)firstLeave=i;if(f.Outgoing[i].Kind==8 && firstMotion<0)firstMotion=i;}
            Assert.That(firstLeave,Is.GreaterThanOrEqualTo(0));Assert.That(firstMotion,Is.GreaterThan(firstLeave));
            f.DeliverOutgoing();Assert.That(f.Total,Is.LessThan(2560UL),"Leave必须在入站回调同步撤销，不等待旧FIFO数据。");
        }

        [Test]
        public void CollectionWithoutASolverMotionGroupStillSendsAndRemovesQuantity()
        {
            using var f=new Fixture();f.Warm();f.DrainMotionBudget();f.State.Collected=true;f.State.Tick++;f.Capture();f.CaptureMotion();
            for(int round=0;round<6;round++){f.PumpSender();f.DeliverOutgoing();while(f.Queued>0)f.PumpClient();f.Ack();}
            Assert.That(f.Total,Is.EqualTo(1920UL));Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }

        [Test]
        public void FinishingAnOldMotionTailDoesNotStartAnotherWindowInTheSamePump()
        {
            using var f=new Fixture();f.Warm();f.State.Next();f.Capture();f.CaptureMotion();var old=f.SendOneMotionPage();
            Assert.That(old.PageCount,Is.GreaterThan(4));f.Outgoing.Clear();f.State.Next();f.Capture();f.CaptureMotion();
            f.PumpSender();int oldPages=0;
            foreach(var page in f.Outgoing)if(page.Kind==8)
            {Assert.That(page.SnapshotId,Is.EqualTo(old.SnapshotId),"补完旧尾页后，本轮不能再发下一窗口的半帧。");oldPages++;}
            Assert.That(oldPages,Is.EqualTo((int)old.PageCount-1));
        }
    }
}
