using System;
using System.Reflection;
using System.Threading;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 验证正式Replication.Advance的调用顺序，而非直接调用Source.Advance：完整future已经
    /// 通过真实页Codec/数量见证并使Assembler Commit后，下一Pump会先执行no-op Cancel，
    /// 然后才传入新播放时钟。Sleep仅校准真实单调时钟租约，不写counter或私有时间字段。
    /// Reflection仅沿现有测试asmdef边界创建真正ElementField实现，没有替代Store/Sink。
    /// </summary>
    public sealed class WorldLiquidBufferedChannelLifecycleTests
    {
        private static readonly Vector3Int Cell=new Vector3Int(1,2,1);
        private sealed class MaterialSource:IWorldReplicationSource,IWorldLiquidMotionSource,IOnlineLiquidMotionWorld
        {
            internal uint Tick=1,Quantity=20;
            internal bool Ice;
            public IWorldLiquidMotionSource Source=>this;
            public IWorldLiquidMotionSink Sink=>null;
            public bool TryCapture(WorldReplicaBuffer buffer,out WorldCaptureFailure failure)
            {
                buffer.Geometries[0]=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);
                buffer.Geometries[1]=new WorldGridGeometry(WorldMaterialDomain.Solid,Vector3.zero,.5f,16);
                int count=0;
                if(Quantity>0)buffer.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,Cell,1,Quantity);
                if(Ice)buffer.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Solid,WorldMaterialStorage.Resident,Cell,5,510);
                buffer.Header=new WorldReplicaHeader(Tick,count,0,2);failure=default;return true;
            }
            public bool TryCapture(WorldLiquidMotionCaptureBuffer buffer,WorldReplicaBuffer quantities)
            {
                buffer.Epoch=Tick;buffer.Interval=.1f;buffer.ChunkSize=16;buffer.Count=1;buffer.GroupIds[0]=0;
                // 本例隔离生命周期顺序，选Half可精确表示的1/8格，不能把wire的合法
                // .1→.099975586量化误差误判成Source复制/切槽错误，也不放宽姿态容差。
                float current=(Tick-1)*.125f,previous=Tick>1?(Tick-2)*.125f:0;
                buffer.Rows[0]=new WorldLiquidMotionSample(Cell,1,20,20,new Vector3(previous,0,0),Vector3.zero,new Vector3(current,0,0),Vector3.zero);
                return true;
            }
        }
        private sealed class Binding:IOnlineLiquidMotionWorld
        {public IWorldLiquidMotionSource Source=>null;public IWorldLiquidMotionSink Sink{get;set;}}
        private sealed class Fixture:IDisposable
        {
            internal readonly MaterialSource State=new MaterialSource();
            internal readonly WorldReplicaBuffer Buffer=new WorldReplicaBuffer(256,0,1000000);
            internal readonly WorldReplicationCoordinator Server,Client;
            internal readonly WorldLiquidMotionReplication MotionOut,MotionIn;
            internal readonly object Store,ReadModel;
            private readonly ScriptableObject _profile,_water;
            private readonly WorldPageCodec _codec=new WorldPageCodec();
            private readonly byte[] _wire=new byte[WorldPageCodec.MaxPayloadBytes];
            private double _now;
            internal Fixture()
            {
                var storeType=Type.GetType("Game.ElementField.WorldReplicaStore, Game.ElementField",true);
                var profileType=Type.GetType("Game.ElementField.LiquidWorldProfile, Game.ElementField",true);
                var behaviorType=Type.GetType("Game.ElementField.LiquidBehaviorProfile, Game.ElementField",true);
                _profile=ScriptableObject.CreateInstance(profileType);_water=ScriptableObject.CreateInstance(behaviorType);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",_profile);
                var materials=Array.CreateInstance(behaviorType,1);materials.SetValue(_water,0);
                profileType.GetField("_materials",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_profile,materials);
                Store=Activator.CreateInstance(storeType,new object[]{7UL,8,256,0,2048});
                var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,16);
                Assert.That(storeType.GetMethod("ConfigureGeometry").Invoke(Store,new object[]{geometry}),Is.True);
                Assert.That(storeType.GetMethod("ConfigureMaterials").Invoke(Store,new object[]{WorldMaterialDomain.Liquid,510u,new byte[]{1},false,8}),Is.True);
                Assert.That(storeType.GetMethod("ConfigureGeometry").Invoke(Store,new object[]{new WorldGridGeometry(WorldMaterialDomain.Solid,Vector3.zero,.5f,16)}),Is.True);
                Assert.That(storeType.GetMethod("ConfigureMaterials").Invoke(Store,new object[]{WorldMaterialDomain.Solid,510u,new byte[]{5},false,8}),Is.True);
                var readType=Type.GetType("Game.ElementField.WorldReplicaLiquidReadModel, Game.ElementField",true);
                ReadModel=Activator.CreateInstance(readType,new object[]{Store,geometry,_profile,.25f,null,null});
                Server=new WorldReplicationCoordinator(7,99,8,2048,256,0,1000000);
                Client=new WorldReplicationCoordinator(7,99,8,2048,256,0,1000000,(IWorldChunkPayloadInstaller)Store);
                Assert.That(Server.BindPeer(0,10,3),Is.True);Assert.That(Client.SetClientConnectionEpoch(3),Is.True);
                MotionOut=new WorldLiquidMotionReplication(Server,State);
                MotionIn=new WorldLiquidMotionReplication(Client,new Binding{Sink=(IWorldLiquidMotionSink)ReadModel});MotionIn.ResetConnection(3);
            }
            internal T Read<T>(string property)=>(T)ReadModel.GetType().GetProperty(property).GetValue(ReadModel);
            internal ulong Total=>(ulong)Store.GetType().GetMethod("TotalQuantity").Invoke(Store,new object[]{WorldMaterialDomain.Liquid});
            internal void CommitQuantity()
            {
                Assert.That(Server.Capture(State,Buffer),Is.True);
                Assert.That(Server.UpdatePeerInterest(0,10,3,new Bounds(Vector3.zero,Vector3.one*100)),Is.True);
                for(int round=0;round<20;round++)
                {
                    Server.BeginRound(_now);
                    for(int page=0;page<2;page++)
                    {
                        if(!Server.TryPreparePage(0,_now,out var borrowed))break;
                        Assert.That(_codec.TryEncode(borrowed,new ArraySegment<byte>(_wire),out int bytes),Is.True);
                        Assert.That(Server.MarkPageSent(0,_now,bytes+72),Is.True);
                        Assert.That(_codec.TryDecode(new ArraySegment<byte>(_wire,0,bytes),out var decoded),Is.True);
                        if(decoded.Kind==6 || decoded.Kind==7)Assert.That(Client.ReceiveSubscription(decoded),Is.True);
                        else
                        {
                            Assert.That(Client.ReceivePage(decoded,(long)(_now*1000),out var reply),Is.True);
                            if(reply!=null)Assert.That(Server.ReceiveControl(0,10,3,reply,_now),Is.True);
                        }
                    }
                    Assert.That(Server.CompleteSendRound(),Is.True);_now+=.02;
                }
            }
            internal void Publish(double sample,double clock)
            {
                MotionOut.Capture(Buffer,sample);int sent=0;
                while(MotionOut.TryPreparePage(0,3,Vector3.zero,_now,clock,out var page))
                {
                    Assert.That(_codec.TryEncode(page,new ArraySegment<byte>(_wire),out int bytes),Is.True);
                    Assert.That(MotionOut.MarkPageSent(0,bytes),Is.True);
                    Assert.That(_codec.TryDecode(new ArraySegment<byte>(_wire,0,bytes),out var decoded),Is.True);
                    Assert.That(MotionIn.ReceivePage(decoded,_now),Is.True);Assert.That(++sent,Is.LessThanOrEqualTo(11));
                }
                Assert.That(sent,Is.GreaterThan(0));MotionIn.Advance(_now,clock,false);
            }
            internal void Pump(double clock)=>MotionIn.Advance(_now+.125,clock,false);
            internal float Alpha(ulong window)
            {object[] args={window,0f};Assert.That(ReadModel.GetType().GetMethod("TryGetPresentationAlpha").Invoke(ReadModel,args),Is.True);return (float)args[1];}
            internal float PublicOffsetX(bool previous)
            {
                var method=ReadModel.GetType().GetMethod("TryGetPresentationSample");
                object[] args={Cell,Enum.ToObject(method.GetParameters()[1].ParameterType,1),previous,null};
                Assert.That(method.Invoke(ReadModel,args),Is.True);
                return ((Vector3)args[3].GetType().GetField("OffsetCells").GetValue(args[3])).x;
            }
            public void Dispose(){Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);}
        }

        [TestCase("none")][TestCase("harvest")][TestCase("solid")]
        public void CommittedAssemblerNoOpCancelUsesTheNewAdvanceClockBeforeExpiringAcceptedFuture(string boundary)
        {
            using var f=new Fixture();f.CommitQuantity();f.Publish(10,10.01);
            f.State.Tick=2;f.CommitQuantity();f.Publish(10.1,10.15);
            f.State.Tick=3;f.CommitQuantity();f.Publish(10.2,10.15);
            Assert.That(f.MotionIn.Published,Is.EqualTo(3));Assert.That(f.MotionIn.LastPublishedSequence,Is.EqualTo(3UL));
            Assert.That(f.Read<ulong>("PresentationWindowId"),Is.EqualTo(2UL));
            Assert.That(f.Read<int>("BufferedPresentationWindows"),Is.EqualTo(1));
            Assert.That(f.Total,Is.EqualTo(20UL));
            if(boundary!="none")
            {
                f.State.Tick++;
                if(boundary=="harvest")f.State.Quantity=0;else f.State.Ice=true;
                f.CommitQuantity();
            }
            double before=Time.realtimeSinceStartupAsDouble;Thread.Sleep(125);
            double elapsed=Time.realtimeSinceStartupAsDouble-before;
            Assert.That(elapsed,Is.GreaterThanOrEqualTo(.105),"本例必须真的跨过本地100ms lease，不改私有时间字段。");
            // 这里没有新motion页。真实Replication路径会先进入Assembler已Commit后的Cancel，
            // 再Advance(newClock)；旧实现会在Cancel拿旧10.15时钟先撤掉future。
            f.Pump(10.2);
            ulong active=f.Read<ulong>("PresentationWindowId");
            TestContext.WriteLine("committed-future boundary="+boundary+" wallSeconds="+elapsed.ToString("R")+" active="+active
                +" queue="+f.Read<int>("BufferedPresentationWindows")+" accepted="+f.MotionIn.LastPublishedSequence+" total="+f.Total);
            Assert.That(f.MotionIn.Published,Is.EqualTo(3),"no-op Pump不重复接受或Commit运动。");
            if(boundary!="none")
            {
                Assert.That(active,Is.Zero,"已到期future仍不能跨真实采集/固体变化继续显示旧壳。");
                Assert.That(f.Read<int>("BufferedPresentationWindows"),Is.Zero);
                Assert.That(f.Total,Is.EqualTo(boundary=="harvest"?0UL:20UL));return;
            }
            Assert.That(active,Is.EqualTo(3UL),"Assembler正常Commit后的no-op Cancel不能在新clock切槽前撤掉已验证future。");
            Assert.That(f.Read<int>("BufferedPresentationWindows"),Is.Zero);Assert.That(f.Alpha(active),Is.EqualTo(0).Within(1e-5));
            Assert.That(f.PublicOffsetX(true),Is.EqualTo(.125f).Within(1e-5));Assert.That(f.PublicOffsetX(false),Is.EqualTo(.25f).Within(1e-5));
            f.Pump(10.225);Assert.That(f.Alpha(active),Is.EqualTo(.25f).Within(1e-5));
            Assert.That(f.Total,Is.EqualTo(20UL));Assert.That(f.Server.Faulted || f.Client.Faulted,Is.False);
        }
    }
}
