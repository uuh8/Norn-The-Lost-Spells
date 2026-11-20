using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 真实库存DepositPort→Solver→WorldLiquidMotionSource→word Codec→Replica Store/ReadModel。
    /// 每批200 GMU / 100ms及(8,2,0)初速对应正式Container Use，而非单滴/每400ms无速度诊断。
    /// 只读旧水曲线和真实数量；不手填Motion Previous、不修改私有播放时间/队列或放松Current witness。
    /// born表示仅来自原Port的真实Commit，Rollback、未知改量和收取均不授予特例。
    /// </summary>
    public sealed class WorldLiquidContinuousPourPresentationTests
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        private static readonly Vector3Int Nozzle=new Vector3Int(0,8,0);
        private static readonly Vector3 InitialVelocity=new Vector3(8,2,0);
        private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Hidden).SetValue(target,value);

        private sealed class Bottle:IMaterialTransferSource
        {
            internal ulong Remaining=4000,Committed;
            internal bool ThrowOnCommit;
            private ulong _reserved;
            public bool TryReserve(MaterialAmount[] amounts,int count,out MaterialTransferFailure failure)
            {
                failure=MaterialTransferFailure.None;
                if(count!=1 || amounts[0].MaterialKey!=(byte)MaterialId.Water || amounts[0].Quantity>Remaining)
                {failure=MaterialTransferFailure.NoMaterial;return false;}
                _reserved=amounts[0].Quantity;return true;
            }
            public void Commit(){if(ThrowOnCommit)throw new InvalidOperationException("explicit bottle commit failure");Remaining-=_reserved;Committed+=_reserved;_reserved=0;}
            public void Cancel()=>_reserved=0;
        }
        private sealed class CollectionBottle:IMaterialTransferDestination
        {
            public ushort AllowedMaterials=>1<<(byte)MaterialId.Water;
            public ulong FreeCapacity=>10;
            internal ulong Committed;
            private ulong _reserved;
            public bool TryReserve(MaterialAmount[] amounts,int count,out MaterialTransferFailure failure)
            {failure=MaterialTransferFailure.None;_reserved=0;for(int i=0;i<count;i++)_reserved+=amounts[i].Quantity;return _reserved<=FreeCapacity;}
            public void Commit(){Committed+=_reserved;_reserved=0;}
            public void Cancel()=>_reserved=0;
        }
        private sealed class Fixture:IDisposable
        {
            internal readonly GameObject Host=new GameObject("Continuous real container pour");
            internal readonly ElementWorldRuntime World;
            internal readonly LiquidWorldRuntime Liquid;
            internal readonly WorldReplicaLiquidReadModel Replica;
            internal readonly WorldReplicaStore ReplicaStore=new WorldReplicaStore(7,128,2048,0,8192);
            internal readonly Bottle Source=new Bottle();
            internal WorldLiquidMotionFrame Last;
            private readonly LiquidWorldProfile _profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly ElementWorldProfile _worldProfile=ScriptableObject.CreateInstance<ElementWorldProfile>();
            private readonly LiquidBehaviorProfile _water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            private readonly WorldReplicaBuffer _quantities=new WorldReplicaBuffer(2048,0,1000000);
            private readonly WorldLiquidMotionCaptureBuffer _capture=new WorldLiquidMotionCaptureBuffer();
            private readonly WorldReplicationCommitOwner _owner=new WorldReplicationCommitOwner(7,128,2048,0,128,8192,1000000);
            private readonly uint[] _words=new uint[8192],_motionWords=new uint[WorldLiquidMotionCodec.MaxWords];
            private readonly MaterialInventoryDepositPort _deposit;
            private readonly LiquidVolumeStore _store;
            private readonly WorldGridGeometry _geometry;
            private readonly WorldLiquidMotionSource _motionSource;
            private int _tick;
            internal Fixture(int chunkSize=4)
            {
                Host.SetActive(false);Host.hideFlags=HideFlags.DontSave;
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":128,\"_maximumActiveCells\":512,\"_maximumCellsPerTick\":512,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4,\"_enableAutoStableDetection\":false}",_profile);
                Set(_profile,"_materials",new[]{_water});
                JsonUtility.FromJsonOverwrite("{\"_cellSize\":0.25,\"_chunkSize\":4,\"_activeRadiusXZChunks\":32,\"_activeRadiusYChunks\":32}",_worldProfile);
                World=Host.AddComponent<ElementWorldRuntime>();World.ConfigureExecutionMode(WorldExecutionMode.Authority);
                Set(World,"_profile",_worldProfile);Set(World,"_origin",Vector3.one*100);Set(World,"_hasInterestChunk",true);
                var backends=new MaterialSimulationBackendKind[256];var present=new bool[256];
                backends[(byte)MaterialId.Water]=MaterialSimulationBackendKind.GridLiquid;present[(byte)MaterialId.Water]=true;
                var routes=(MaterialSimulationRouteSnapshot)Activator.CreateInstance(typeof(MaterialSimulationRouteSnapshot),Hidden,null,new object[]{backends,present},null);
                Set(World,"_materialRoutes",routes);
                var baker=Host.GetComponent<ElementFieldSolidBaker>();Set(baker,"_includeDefaultEnvironment",false);
                Liquid=Host.AddComponent<LiquidWorldRuntime>();Set(Liquid,"_profile",_profile);
                Assert.That(Liquid.TryInitialize(Vector3.one*100,.25f,chunkSize,baker),Is.True);
                Set(World,"_gridLiquidRuntime",Liquid);Set(World,"_solidBaker",baker);Set(World,"<IsInitialized>k__BackingField",true);
                _store=(LiquidVolumeStore)typeof(LiquidWorldRuntime).GetField("_store",Hidden).GetValue(Liquid);
                _geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Liquid.Origin,Liquid.CellSize,Liquid.ChunkSize);
                Assert.That(ReplicaStore.ConfigureGeometry(_geometry),Is.True);
                Assert.That(ReplicaStore.ConfigureMaterials(WorldMaterialDomain.Liquid,(uint)Liquid.CellCapacityGmu,new[]{(byte)MaterialId.Water}),Is.True);
                Replica=new WorldReplicaLiquidReadModel(ReplicaStore,_geometry,_profile,.25f,baker);Replica.ResetConnection(3);
                _deposit=new MaterialInventoryDepositPort(World);_motionSource=new WorldLiquidMotionSource(World);
            }
            internal void Pour(Vector3Int cell)
            {
                ulong before=Source.Committed;
                _store.TryGetQuantity(cell,MaterialId.Water,out uint oldQuantity);
                int freeBefore=_store.FreeCapacity(cell),slotsBefore=Liquid.AvailableInjectionMotionSlots;
                var exchange=new MaterialDepositExchange{Position=Liquid.Origin+((Vector3)cell+Vector3.one*.5f)*Liquid.CellSize,
                    Radius=.2f,Source=Source,Count=1,Amounts=new[]{new MaterialAmount((byte)MaterialId.Water,200)},
                    HasInitialMotion=true,InitialVelocity=InitialVelocity};
                var failure=_deposit.Deposit(exchange,out ulong transferred);
                if(failure!=MaterialTransferFailure.None)
                {
                    TestContext.WriteLine("pour-capacity tick="+(_tick+1)+" cell="+cell+" oldQuantity="+oldQuantity+" free="+freeBefore
                        +" cellCapacity="+Liquid.CellCapacityGmu+" fallingSlots="+slotsBefore+" chunks="+Liquid.ResidentChunkCount
                        +" total="+Liquid.TotalQuantity+" bottle="+Source.Remaining+" failure="+failure);
                    for(int i=0;i<_store.ResidentCellCount;i++)
                    {
                        var wet=_store.GetResidentCellAt(i);_store.TryGetQuantity(wet,MaterialId.Water,out uint q);
                        if(q==0)continue;
                        Liquid.TryGetFallingMotion(wet,MaterialId.Water,out var motion);
                        TestContext.WriteLine("pour-live cell="+wet+" q="+q+" offset="+motion.HorizontalOffsetCells+" vy="+motion.Velocity+" vx="+motion.HorizontalVelocityWorld);
                    }
                }
                Assert.That(failure,Is.EqualTo(MaterialTransferFailure.None));
                Assert.That(transferred,Is.EqualTo(200UL));Assert.That(Source.Committed-before,Is.EqualTo(200UL));
                Assert.That(World.ImportedMaterialQuantity(MaterialId.Water),Is.EqualTo(Source.Committed));
            }
            internal void RejectOversizePour(Vector3Int cell)
            {
                ulong committed=Source.Committed,remaining=Source.Remaining;
                var exchange=new MaterialDepositExchange{Position=Liquid.Origin+((Vector3)cell+Vector3.one*.5f)*Liquid.CellSize,
                    Radius=.2f,Source=Source,Count=1,Amounts=new[]{new MaterialAmount((byte)MaterialId.Water,600)},
                    HasInitialMotion=true,InitialVelocity=InitialVelocity};
                Assert.That(_deposit.Deposit(exchange,out ulong transferred),Is.EqualTo(MaterialTransferFailure.Capacity));
                Assert.That(transferred,Is.Zero);Assert.That(Source.Committed,Is.EqualTo(committed));Assert.That(Source.Remaining,Is.EqualTo(remaining));
            }
            internal void UnknownRemoveAndRestore(Vector3Int cell)
            {
                Assert.That(_store.TryRemove(cell,MaterialId.Water,1),Is.True);
                Assert.That(_store.TryAdd(cell,MaterialId.Water,1),Is.True);
            }
            internal void FailBottleCommit(Vector3Int cell)
            {
                Source.ThrowOnCommit=true;
                var exchange=new MaterialDepositExchange{Position=Liquid.Origin+((Vector3)cell+Vector3.one*.5f)*Liquid.CellSize,
                    Radius=.2f,Source=Source,Count=1,Amounts=new[]{new MaterialAmount((byte)MaterialId.Water,200)},
                    HasInitialMotion=true,InitialVelocity=InitialVelocity};
                Assert.Throws<InvalidOperationException>(()=>_deposit.Deposit(exchange,out _));
                Assert.That(World.IsSimulationFaulted,Is.True);Assert.That(Source.Committed,Is.Zero);
                Assert.That(_motionSource.TryCapture(_capture,_quantities),Is.False);
                Assert.That(_capture.Count,Is.Zero,"Commit异常后的不确定世界不能发布运动或出生特例。");
            }
            internal WorldLiquidMotionFrame StepAndCapture()
            {
                _tick++;Liquid.Step(.1f,_tick*.1f);Set(World,"_worldTick",(long)_tick);
                CaptureQuantities();Assert.That(_owner.TryCaptureCommit(_quantities),Is.True);
                Assert.That(_motionSource.TryCapture(_capture,_quantities),Is.True);Assert.That(_capture.Count,Is.GreaterThan(0));
                Assert.That(_capture.Count,Is.LessThanOrEqualTo(WorldLiquidMotionFrame.MaxRows),"本最小RED不能让128行截断掩盖出生窗口。");
                var frame=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=(ulong)_tick,MaterialTick=_capture.Epoch,
                    SampleTime=10+_tick*.1,Interval=_capture.Interval,ChunkSize=_capture.ChunkSize};
                for(int i=0;i<_capture.Count;i++)
                {
                    var row=_capture.Rows[i];var key=WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid,row.Cell,Liquid.ChunkSize);
                    int anchor=-1;for(int a=0;a<frame.AnchorCount;a++)if(frame.Anchors[a].Key==key){anchor=a;break;}
                    if(anchor<0)
                    {
                        Assert.That(_owner.TryCopyChunk(key,_words,out var header),Is.True);anchor=frame.AnchorCount++;
                        frame.Anchors[anchor]=new WorldLiquidMotionAnchor(key,header.Generation,header.Revision,1,header.IsResident);
                    }
                    frame.Rows[frame.RowCount++]=new WorldLiquidMotionSample(row.Cell,row.MaterialId,row.PreviousQuantity,row.CurrentQuantity,
                        row.PreviousOffset,row.PreviousVelocity,row.CurrentOffset,row.CurrentVelocity,anchor);
                }
                Assert.That(WorldLiquidMotionCodec.TryEncode(frame,_motionWords,out int words),Is.True);
                var decoded=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=frame.Sequence,MaterialTick=frame.MaterialTick};
                Assert.That(WorldLiquidMotionCodec.TryDecode(_motionWords,words,decoded),Is.True);return decoded;
            }
            internal void Publish(WorldLiquidMotionFrame frame,double clock,ulong exported=0)
            {
                Replica.StagePresentationCandidate(frame,clock);InstallCurrentQuantities();
                Assert.That(Replica.TryPublish(frame,clock),Is.True,"真实Current witness必须完整，不用snap拒绝代替成功接收。");
                Last=frame;AssertRealQuantity(exported);
            }
            private void CaptureQuantities()
            {
                int count=0;_quantities.Geometries[0]=_geometry;
                for(int i=0;i<_store.ResidentCellCount;i++)
                {
                    var cell=_store.GetResidentCellAt(i);
                    if(_store.TryGetQuantity(cell,MaterialId.Water,out uint quantity))
                        _quantities.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,cell,(byte)MaterialId.Water,quantity);
                }
                _quantities.Header=new WorldReplicaHeader(_tick,count,0,1);
            }
            private void InstallCurrentQuantities()
            {
                for(int i=0;i<_owner.ChunkCount;i++)
                {
                    var key=_owner.GetChunkHeader(i).Key;Assert.That(_owner.TryCopyChunk(key,_words,out var header),Is.True);
                    Assert.That(ReplicaStore.TryInstall(header,_words,header.WordCount),Is.True);
                }
            }
            internal void CommitQuantityOnly()
            {
                _tick++;Set(World,"_worldTick",(long)_tick);CaptureQuantities();
                Assert.That(_owner.TryCaptureCommit(_quantities),Is.True);InstallCurrentQuantities();
            }
            internal void CollectTen(Vector3Int cell)
            {
                var bottle=new CollectionBottle();Vector3 point=Liquid.Origin+((Vector3)cell+Vector3.one*.5f)*Liquid.CellSize;
                var request=new MaterialTransferRequestedEvent{WorldId="continuous-pour",InventoryId="collector",SourceId="F",
                    RequestId=1,CollectorPosition=point,SampleCenter=point,Radius=.1f,Budget=10};
                Assert.That(new MaterialInventoryWorldPort(World).Transfer(request,bottle,out ulong moved),Is.EqualTo(MaterialTransferFailure.None));
                Assert.That(moved,Is.EqualTo(10UL));Assert.That(bottle.Committed,Is.EqualTo(10UL));
                Assert.That(World.ExportedMaterialQuantity(MaterialId.Water),Is.EqualTo(10UL));
            }
            internal void AssertRealQuantity(ulong exported=0)
            {
                Assert.That(Liquid.TotalQuantity+Liquid.PendingQuantity+exported,Is.EqualTo(Source.Committed));
                Assert.That(ReplicaStore.TotalQuantity(WorldMaterialDomain.Liquid)+exported,Is.EqualTo(Source.Committed));
            }
            internal Vector3Int WetCell()
            {
                for(int i=0;i<_store.ResidentCellCount;i++)
                {var c=_store.GetResidentCellAt(i);if(_store.TryGetQuantity(c,MaterialId.Water,out uint q) && q>0 && q<=310)return c;}
                Assert.Fail("本夹具必须有容纳下一200GMU的真实湿格。");return default;
            }
            internal LiquidCellPresentationSample Read(Vector3Int cell,bool previous)
            {Replica.TryGetPresentationSample(cell,MaterialId.Water,previous,out var sample);return sample;}
            public void Dispose(){Object.DestroyImmediate(Host);Object.DestroyImmediate(_profile);Object.DestroyImmediate(_worldProfile);Object.DestroyImmediate(_water);}
        }

        private static WorldLiquidMotionFrame WarmOldCurve(Fixture f)
        {
            f.Pour(Nozzle);var first=f.StepAndCapture();f.Publish(first,first.SampleTime+.01);
            var active=f.StepAndCapture();f.Publish(active,active.SampleTime+.05);
            Assert.That(f.Replica.PresentationSnapped,Is.False);Assert.That(f.Replica.PresentationWindowId,Is.EqualTo(active.Sequence));
            return active;
        }
        private static void AssertPreserved(Fixture f,WorldLiquidMotionFrame old,double clock,LiquidCellPresentationSample[] before)
        {
            Assert.That(f.Replica.PresentationWindowId,Is.EqualTo(old.Sequence),"200GMU新出生不能截掉其它已验证旧水的尾段。");
            Assert.That(f.Replica.PresentationSnapped,Is.False);
            Assert.That(f.Replica.TryGetPresentationAlpha(old.Sequence,out float alpha),Is.True);
            Assert.That(alpha,Is.EqualTo((clock-old.SampleTime)/old.Interval).Within(.0001));
            for(int i=0;i<old.RowCount;i++)
            {
                var after=f.Read(old.Rows[i].Cell,false);Assert.That(after.Quantity,Is.EqualTo(before[i].Quantity));
                Assert.That(after.OffsetCells,Is.EqualTo(before[i].OffsetCells));Assert.That(after.VelocityWorld,Is.EqualTo(before[i].VelocityWorld));
            }
        }
        private static LiquidCellPresentationSample[] Before(Fixture f,WorldLiquidMotionFrame frame)
        {var samples=new LiquidCellPresentationSample[frame.RowCount];for(int i=0;i<samples.Length;i++)samples[i]=f.Read(frame.Rows[i].Cell,false);return samples;}
        private static ulong Sum(WorldLiquidMotionFrame frame,bool previous)
        {ulong total=0;for(int i=0;i<frame.RowCount;i++)total+=previous?frame.Rows[i].PreviousQuantity:frame.Rows[i].CurrentQuantity;return total;}
        private static void TraceSeam(Fixture f,WorldLiquidMotionFrame old,WorldLiquidMotionFrame next,double clock,string stage)
        {
            TestContext.WriteLine("pour-seam "+stage+" old="+old.Sequence+" new="+next.Sequence+" clock="+clock.ToString("R")
                +" active="+f.Replica.PresentationWindowId+" snapped="+f.Replica.PresentationSnapped+" queued="+f.Replica.BufferedPresentationWindows
                +" discontinuities="+f.Replica.BufferedPresentationDiscontinuities+" hold="+JsonUtility.ToJson(f.Replica.LastMotionHoldDiagnostic)
                +" oldRows="+old.RowCount+" newRows="+next.RowCount+" visualBirth="+((long)Sum(next,false)-(long)Sum(next,true)));
            for(int a=0;a<old.AnchorCount;a++)for(int b=0;b<next.AnchorCount;b++)
                if(old.Anchors[a].Key==next.Anchors[b].Key && (old.Anchors[a].Generation!=next.Anchors[b].Generation || old.Anchors[a].SubscriptionEpoch!=next.Anchors[b].SubscriptionEpoch))
                    TestContext.WriteLine("pour-anchor xyz="+old.Anchors[a].Key.X+","+old.Anchors[a].Key.Y+","+old.Anchors[a].Key.Z+" generations="+old.Anchors[a].Generation+"->"+next.Anchors[b].Generation
                        +" subscriptions="+old.Anchors[a].SubscriptionEpoch+"->"+next.Anchors[b].SubscriptionEpoch);
            for(int i=0;i<next.RowCount;i++)
            {
                var row=next.Rows[i];WorldLiquidMotionSample prior=default;bool found=false;
                for(int j=0;j<old.RowCount;j++)if(old.Rows[j].Cell==row.Cell && old.Rows[j].MaterialId==row.MaterialId){prior=old.Rows[j];found=true;break;}
                if(row.PreviousQuantity!=prior.CurrentQuantity || (row.PreviousOffset-prior.CurrentOffset).sqrMagnitude>.000001f
                    || (row.PreviousVelocity-prior.CurrentVelocity).sqrMagnitude>.000001f)
                    TestContext.WriteLine("pour-row cell="+row.Cell+" found="+found+" oldQ="+prior.CurrentQuantity+" prevQ="+row.PreviousQuantity
                        +" oldOffset="+prior.CurrentOffset.ToString("F6")+" prevOffset="+row.PreviousOffset.ToString("F6")
                        +" oldVelocity="+prior.CurrentVelocity.ToString("F6")+" prevVelocity="+row.PreviousVelocity.ToString("F6"));
            }
            for(int i=0;i<old.RowCount;i++)if(old.Rows[i].CurrentQuantity>0)
            {
                var row=old.Rows[i];bool found=false;
                for(int j=0;j<next.RowCount;j++)if(next.Rows[j].Cell==row.Cell && next.Rows[j].MaterialId==row.MaterialId){found=true;break;}
                if(!found)
                {f.Liquid.TryGetQuantity(row.Cell,(MaterialId)row.MaterialId,out uint real);TestContext.WriteLine("pour-missing cell="+row.Cell+" oldCurrent="+row.CurrentQuantity+" nowWorld="+real);}
            }
        }

        [Test]
        public void RealContinuous200GmuPourKeepsTheOldCurveAndDoesNotSnapEveryBirthTick()
        {
            // 8m宽的真实Chunk隔离“持续出生”的接缝主题。默认4格小Chunk另有专门的
            // tombstone→新Generation压力控制，不能把合法代次切换误判为出生表示失败。
            using var f=new Fixture(chunkSize:16);var old=WarmOldCurve(f);
            for(int tick=3;tick<=12;tick++)
            {
                double clock=10+tick*.1-.025;f.Replica.AdvancePresentation(clock,false);
                // 真正的自由射流：每100ms按5m/s横移半米喷口，避免粗格中上一批尚未离开
                // 喷口时的真实回压。固定喷口400/510GMU的Capacity是数量Owner正确拒绝，
                // 不能通过扩大格容量或吞掉批次来冒充表示连续；同湿格合并另有独立控制。
                Vector3Int emission=Nozzle+Vector3Int.forward*(tick-2);
                var before=Before(f,old);f.Pour(emission);var next=f.StepAndCapture();
                TraceSeam(f,old,next,clock,"beforePublish");f.Publish(next,clock);TraceSeam(f,old,next,clock,"afterPublish");AssertPreserved(f,old,clock,before);
                f.Replica.AdvancePresentation(next.SampleTime,false);
                Assert.That(f.Replica.PresentationWindowId,Is.EqualTo(next.Sequence));
                Assert.That(f.Replica.TryGetPresentationAlpha(next.Sequence,out float start),Is.True);Assert.That(start,Is.Zero.Within(.0001));
                old=next;
            }
            Assert.That(f.Source.Committed,Is.EqualTo(2200UL));f.AssertRealQuantity();
        }
        [Test]
        public void RealSameWetCellPourPreservesOldPoseBeforeTheCommittedAddedMomentumTakesOver()
        {
            using var f=new Fixture();var old=WarmOldCurve(f);double clock=10.275;
            f.Replica.AdvancePresentation(clock,false);var before=Before(f,old);var wet=f.WetCell();
            Assert.That(f.Liquid.TryGetQuantity(wet,MaterialId.Water,out uint quantity),Is.True);Assert.That(quantity,Is.GreaterThan(0));
            f.Pour(wet);Assert.That(f.Liquid.TryGetQuantity(wet,MaterialId.Water,out uint merged),Is.True);
            Assert.That(merged,Is.EqualTo(quantity+200),"此例必须真实进入同一湿格，不能由邻格出生代替动量合并。");
            var next=f.StepAndCapture();f.Publish(next,clock);AssertPreserved(f,old,clock,before);f.AssertRealQuantity();
        }
        [Test]
        public void ActualCollectionWithoutBirthStillCancelsAnAcceptedFutureImmediately()
        {
            using var f=new Fixture();WarmOldCurve(f);var next=f.StepAndCapture();f.Publish(next,10.275);
            Assert.That(f.Replica.BufferedPresentationWindows,Is.EqualTo(1));
            var cell=f.WetCell();f.CollectTen(cell);
            f.CommitQuantityOnly();f.Replica.AdvancePresentation(10.28,false);
            Assert.That(f.Replica.PresentationWindowId,Is.Zero);Assert.That(f.Replica.BufferedPresentationWindows,Is.Zero);
            f.AssertRealQuantity(10);
        }

        [Test]
        public void TwoCommittedBatchesBeforeBeginTickKeepTheFirstVisualPoseAndAddOnlyTheirRealQuantity()
        {
            using var f=new Fixture();var old=WarmOldCurve(f);const double clock=10.275;
            f.Replica.AdvancePresentation(clock,false);var before=Before(f,old);
            f.Pour(Nozzle);f.Pour(Nozzle);var next=f.StepAndCapture();
            Assert.That(Sum(next,false)-Sum(next,true),Is.EqualTo(400UL));
            f.Publish(next,clock);AssertPreserved(f,old,clock,before);f.AssertRealQuantity();
        }
        [Test]
        public void RolledBackDepositDoesNotCreateABirthOrChangeTheOldQuantityCurve()
        {
            using var f=new Fixture();var old=WarmOldCurve(f);const double clock=10.275;
            f.Replica.AdvancePresentation(clock,false);var before=Before(f,old);f.RejectOversizePour(f.WetCell());
            var next=f.StepAndCapture();Assert.That(Sum(next,true),Is.EqualTo(Sum(next,false)));
            f.Publish(next,clock);AssertPreserved(f,old,clock,before);Assert.That(f.Source.Committed,Is.EqualTo(200UL));
        }
        [Test]
        public void UnknownRemoveAndRestoreCannotRecoverCommittedBirthPrivilegeByMatchingTheFinalQuantity()
        {
            using var f=new Fixture();WarmOldCurve(f);f.Pour(Nozzle);f.UnknownRemoveAndRestore(Nozzle);
            var next=f.StepAndCapture();Assert.That(Sum(next,true),Is.EqualTo(Sum(next,false)),
                "未知减量/恢复经过原Store回调，必须取消出生特例，不能仅凭最终量相等重授予。");
            f.Publish(next,10.275);Assert.That(f.Replica.PresentationSnapped,Is.True);f.AssertRealQuantity();
        }
        [Test]
        public void CommittedBirthEvidenceIsConsumedByExactlyOneSolverWindow()
        {
            using var f=new Fixture();f.Pour(Nozzle);var first=f.StepAndCapture();f.Publish(first,first.SampleTime+.01);
            Assert.That(Sum(first,true),Is.Zero);Assert.That(Sum(first,false),Is.EqualTo(200UL));
            var next=f.StepAndCapture();Assert.That(Sum(next,true),Is.EqualTo(Sum(next,false)));
            f.Publish(next,next.SampleTime+.025);f.AssertRealQuantity();
        }
        [Test]
        public void CollectionCommittedBeforeBeginTickCancelsThePendingBirthEvidence()
        {
            using var f=new Fixture();f.Pour(Nozzle);f.CollectTen(Nozzle);var next=f.StepAndCapture();
            Assert.That(Sum(next,true),Is.EqualTo(Sum(next,false)));Assert.That(Sum(next,false),Is.EqualTo(190UL));
            f.Publish(next,next.SampleTime+.01,10);f.AssertRealQuantity(10);
        }
        [Test]
        public void BirthCurrentPoseAndVelocityReconstructTheRealNozzleTrajectoryAtThreeSubframes()
        {
            using var f=new Fixture();f.Pour(Nozzle);var frame=f.StepAndCapture();
            Assert.That(Sum(frame,true),Is.Zero);Assert.That(Sum(frame,false),Is.EqualTo(200UL));
            Vector3 spawn=f.Liquid.Origin+((Vector3)Nozzle+Vector3.one*.5f)*f.Liquid.CellSize;
            foreach(float alpha in new[]{.25f,.5f,.75f})
            {
                Vector3 actual=Vector3.zero;uint amount=0;float seconds=(alpha-1)*frame.Interval;
                for(int r=0;r<frame.RowCount;r++)
                {
                    var row=frame.Rows[r];if(row.CurrentQuantity==0)continue;
                    Vector3 current=f.Liquid.Origin+((Vector3)row.Cell+Vector3.one*.5f+row.CurrentOffset)*f.Liquid.CellSize;
                    actual+=(current+row.CurrentVelocity*seconds+Vector3.down*(.5f*9.81f*seconds*seconds))*row.CurrentQuantity;
                    amount+=row.CurrentQuantity;
                }
                actual/=amount;float elapsed=frame.Interval*alpha;
                Vector3 expected=spawn+InitialVelocity*elapsed+Vector3.down*(.5f*9.81f*elapsed*elapsed);
                Assert.That(Vector3.Distance(actual,expected),Is.LessThan(.002f),
                    "真实Source+Half wire的Current回推必须从喷口连续走到落点，不能只在整Tick后的位置原地fade。");
            }
        }
        [Test]
        public void FailedBottleCommitFaultsTheWorldAndCannotPublishABirthSidecar()
        {using var f=new Fixture();f.FailBottleCommit(Nozzle);}
        [Test]
        public void RealSmallChunkGenerationRebirthSnapsSafelyWithoutReplayingTheOldOwnership()
        {
            using var f=new Fixture();var old=WarmOldCurve(f);bool observed=false;
            for(int tick=3;tick<=12;tick++)
            {
                double clock=10+tick*.1-.025;f.Replica.AdvancePresentation(clock,false);
                f.Pour(Nozzle);var next=f.StepAndCapture();
                var changed=new List<WorldChunkKey>();
                for(int a=0;a<old.AnchorCount;a++)for(int b=0;b<next.AnchorCount;b++)
                    if(old.Anchors[a].Key==next.Anchors[b].Key && old.Anchors[a].Generation!=next.Anchors[b].Generation)
                    {
                        changed.Add(old.Anchors[a].Key);
                        TestContext.WriteLine("real-generation-rebirth xyz="+old.Anchors[a].Key.X+","+old.Anchors[a].Key.Y+","+old.Anchors[a].Key.Z
                            +" generation="+old.Anchors[a].Generation+"->"+next.Anchors[b].Generation+" old="+old.Sequence+" next="+next.Sequence);
                    }
                f.Publish(next,clock);
                if(changed.Count>0)
                {
                    observed=true;
                    Assert.That(f.Replica.PresentationWindowId,Is.EqualTo(next.Sequence));Assert.That(f.Replica.PresentationSnapped,Is.True);
                    Assert.That(f.Replica.BufferedPresentationWindows,Is.Zero);
                    Assert.That(f.Replica.TryGetPresentationAlpha(next.Sequence,out float alpha),Is.True);Assert.That(alpha,Is.EqualTo(1));
                    for(int r=0;r<old.RowCount;r++)
                    {
                        var row=old.Rows[r];var key=old.Anchors[row.AnchorIndex].Key;if(!changed.Contains(key))continue;
                        f.Replica.TryGetQuantity(row.Cell,(MaterialId)row.MaterialId,out uint real);
                        Assert.That(f.Read(row.Cell,false).Quantity,Is.EqualTo(real),"新代次终点必须读真实Current，不复活旧Generation尾水。");
                    }
                    f.AssertRealQuantity();break;
                }
                f.Replica.AdvancePresentation(next.SampleTime,false);old=next;
            }
            Assert.That(observed,Is.True,"控制必须真的出现已退休空Chunk重新驻留的新Generation，不能只断言空循环。");
        }
    }
}
