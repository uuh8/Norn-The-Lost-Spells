using System;
using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 真实Authority Store/History→Motion Source→Codec→数量Owner/Replica Store→ReadModel闭环。
    /// 这里只用成对Store事务控制Solver搬运顺序，沿原Solver scope记录组；不手填正例Previous，
    /// 不反射修改显示时间/队列。前两窗只动A/B，另一Chunk的18 GMU静水已在真实Store，
    /// 第三窗静水才参加搬运；motion子集缺行不能被误解释为整个世界的该格原本没有水。
    /// </summary>
    public sealed class WorldLiquidStationaryJoinPresentationTests
    {
        private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        private static readonly Vector3Int A=new Vector3Int(0,2,0),B=new Vector3Int(1,2,0),
            Still=new Vector3Int(8,0,0),Target=new Vector3Int(9,0,0);
        private static WorldChunkKey Key(Vector3Int cell)=>WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid,cell,4);
        private static void Set(object owner,string field,object value)=>owner.GetType().GetField(field,Hidden).SetValue(owner,value);

        private sealed class Fixture:IDisposable
        {
            private readonly GameObject _host=new GameObject("Real stationary liquid joins a later motion subset");
            private readonly LiquidWorldProfile _profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly LiquidBehaviorProfile _water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            private readonly ElementWorldRuntime _world;
            private readonly LiquidWorldRuntime _liquid;
            private readonly LiquidTransportHistory _history;
            private readonly WorldLiquidMotionSource _motionSource;
            private readonly WorldReplicationCommitOwner _owner=new WorldReplicationCommitOwner(7,16,128,0,16,2048,100000);
            private readonly WorldReplicaBuffer _quantities=new WorldReplicaBuffer(128,0,100000);
            private readonly WorldLiquidMotionCaptureBuffer _capture=new WorldLiquidMotionCaptureBuffer();
            private readonly uint[] _words=new uint[2048],_motionWords=new uint[WorldLiquidMotionCodec.MaxWords];
            private readonly WorldGridGeometry _geometry;
            private readonly bool _wholeCrossChunk;
            private readonly int _joinTick;
            private Vector3Int JoinTarget=>_wholeCrossChunk?new Vector3Int(7,0,0):Target;
            private int _tick;
            internal readonly LiquidVolumeStore Authority;
            internal readonly WorldReplicaStore Store=new WorldReplicaStore(7,16,128,0,2048);
            internal readonly WorldReplicaLiquidReadModel Source;

            internal Fixture(bool wholeCrossChunk=false,int joinTick=3)
            {
                _wholeCrossChunk=wholeCrossChunk;_joinTick=joinTick;
                _host.SetActive(false);_host.hideFlags=HideFlags.DontSave;
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":128,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4,\"_enableAutoStableDetection\":false}",_profile);
                Set(_profile,"_materials",new[]{_water});
                _world=_host.AddComponent<ElementWorldRuntime>();_world.ConfigureExecutionMode(WorldExecutionMode.Authority);
                _liquid=_host.AddComponent<LiquidWorldRuntime>();Set(_liquid,"_profile",_profile);
                var baker=_host.GetComponent<ElementFieldSolidBaker>();Set(baker,"_includeDefaultEnvironment",false);
                Assert.That(_liquid.TryInitialize(Vector3.zero,.25f,4,baker),Is.True);
                Set(_world,"_gridLiquidRuntime",_liquid);Set(_world,"<IsInitialized>k__BackingField",true);
                Authority=(LiquidVolumeStore)typeof(LiquidWorldRuntime).GetField("_store",Hidden).GetValue(_liquid);
                _history=(LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",Hidden).GetValue(_liquid);
                Set(typeof(LiquidWorldRuntime).GetField("_solver",Hidden).GetValue(_liquid),"_deltaTime",.1f);
                _geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,_liquid.CellSize,4);
                Assert.That(Store.ConfigureGeometry(_geometry),Is.True);
                Assert.That(Store.ConfigureMaterials(WorldMaterialDomain.Liquid,(uint)_liquid.CellCapacityGmu,new[]{(byte)MaterialId.Water}),Is.True);
                Source=new WorldReplicaLiquidReadModel(Store,_geometry,_profile,.25f,null);Source.ResetConnection(3);
                _motionSource=new WorldLiquidMotionSource(_world);
                Assert.That(Authority.TryAdd(A,MaterialId.Water,20),Is.True);
                Assert.That(Authority.TryAdd(Still,MaterialId.Water,18),Is.True);
            }

            internal WorldLiquidMotionFrame Next()
            {
                _tick++;_history.BeginTick();bool prior=_history.SetSolverCaptureScope(true);
                try
                {
                    Vector3Int from=(_tick&1)==1?A:B,to=(_tick&1)==1?B:A;
                    Assert.That(Authority.TryTransfer(from,to,MaterialId.Water,20),Is.True);
                    _history.RecordSolverTransfer(from,to);
                    if(_tick==_joinTick)
                    {Assert.That(Authority.TryTransfer(Still,JoinTarget,MaterialId.Water,_wholeCrossChunk?18u:9u),Is.True);_history.RecordSolverTransfer(Still,JoinTarget);}
                }
                finally{_history.SetSolverCaptureScope(prior);}
                CommitQuantities();
                Assert.That(_motionSource.TryCapture(_capture,_quantities),Is.True);
                var frame=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=(ulong)_tick,
                    MaterialTick=_capture.Epoch,SampleTime=10+_tick*.1,Interval=_capture.Interval,ChunkSize=4};
                for(int i=0;i<_capture.Count;i++)
                {
                    var row=_capture.Rows[i];var key=Key(row.Cell);int anchor=-1;
                    for(int j=0;j<frame.AnchorCount;j++)if(frame.Anchors[j].Key==key){anchor=j;break;}
                    if(anchor<0)
                    {
                        Assert.That(_owner.TryCopyChunk(key,_words,out var h),Is.True);anchor=frame.AnchorCount++;
                        frame.Anchors[anchor]=new WorldLiquidMotionAnchor(key,h.Generation,h.Revision,1,h.IsResident);
                    }
                    frame.Rows[frame.RowCount++]=new WorldLiquidMotionSample(row.Cell,row.MaterialId,row.PreviousQuantity,row.CurrentQuantity,
                        row.PreviousOffset,row.PreviousVelocity,row.CurrentOffset,row.CurrentVelocity,anchor);
                }
                Assert.That(WorldLiquidMotionCodec.TryEncode(frame,_motionWords,out int words),Is.True);
                var decoded=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=frame.Sequence,MaterialTick=frame.MaterialTick};
                Assert.That(WorldLiquidMotionCodec.TryDecode(_motionWords,words,decoded),Is.True);return decoded;
            }
            private void CommitQuantities()
            {
                Set(_world,"_worldTick",(long)_tick);int count=0;_quantities.Geometries[0]=_geometry;
                for(int i=0;i<Authority.ResidentCellCount;i++)
                {
                    var cell=Authority.GetResidentCellAt(i);
                    if(Authority.TryGetQuantity(cell,MaterialId.Water,out uint quantity))
                        _quantities.Cells[count++]=new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Dynamic,cell,(byte)MaterialId.Water,quantity);
                }
                _quantities.Header=new WorldReplicaHeader(_tick,count,0,1);
                Assert.That(_owner.TryCaptureCommit(_quantities),Is.True);
            }
            internal void Install(bool skipStillChunk=false,ulong generation=1,bool skipJoinTargetChunk=false,WorldChunkKey? onlyChunk=null)
            {
                for(int i=0;i<_owner.ChunkCount;i++)
                {
                    var key=_owner.GetChunkHeader(i).Key;if(skipStillChunk && key==Key(Still))continue;
                    if(onlyChunk.HasValue && key!=onlyChunk.Value)continue;
                    if(skipJoinTargetChunk && key==Key(JoinTarget))continue;
                    Assert.That(_owner.TryCopyChunk(key,_words,out var h),Is.True);
                    if(generation!=1)h=new WorldChunkPayloadHeader(h.WorldEpoch,h.SnapshotId+100,key,generation,0,1,h.ServerTick,h.WordCount,h.Checksum,h.IsResident);
                    Assert.That(Store.TryInstall(h,_words,h.WordCount),Is.True);
                }
            }
            internal void PrepareActive(bool skipStillChunk=false)
            {
                var first=Next();Assert.That(Find(first,Still),Is.EqualTo(-1),"真实Source首窗确实省略已存在静水。");Install(skipStillChunk);
                Assert.That(Source.TryPublish(first,10.11),Is.True);
                var active=Next();Assert.That(Find(active,Still),Is.EqualTo(-1),"静止的18 GMU没有被当成moving row发送。");
                Source.StagePresentationCandidate(active,10.24);Install(skipStillChunk);
                Assert.That(Source.TryPublish(active,10.24),Is.True);Assert.That(Source.PresentationSnapped,Is.False);
                Assert.That(Source.PresentationWindowId,Is.EqualTo(2UL));
                Assert.That(Store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(skipStillChunk?20UL:38UL));
            }
            internal WorldLiquidMotionFrame Prepare(bool skipStillChunk=false)
            {
                PrepareActive(skipStillChunk);
                var next=Next();int index=Find(next,Still);Assert.That(index,Is.GreaterThanOrEqualTo(0));
                Assert.That(next.Rows[index].PreviousQuantity,Is.EqualTo(18u));Assert.That(next.Rows[index].CurrentQuantity,Is.EqualTo(_wholeCrossChunk?0u:9u));
                Assert.That(next.Rows[index].PreviousOffset,Is.EqualTo(Vector3.zero));Assert.That(next.Rows[index].PreviousVelocity,Is.EqualTo(Vector3.zero));
                return next;
            }
            internal void CollectStillAfterCapture(uint amount=9,bool onlyStillChunk=false)
            {Assert.That(Authority.TryRemove(Still,MaterialId.Water,amount),Is.True);_tick++;CommitQuantities();Install(onlyChunk:onlyStillChunk?Key(Still):(WorldChunkKey?)null);}
            internal static int Find(WorldLiquidMotionFrame frame,Vector3Int cell)
            {for(int i=0;i<frame.RowCount;i++)if(frame.Rows[i].Cell==cell)return i;return -1;}
            public void Dispose(){Object.DestroyImmediate(_host);Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);}
        }

        [Test]
        public void ExistingStaticWaterJoiningALaterFlowKeepsTheUnfinishedOldCurve()
        {
            using var f=new Fixture();var next=f.Prepare();
            Assert.That(f.Source.TryGetPresentationSample(Still,MaterialId.Water,false,out var still),Is.True);
            Assert.That(still.Quantity,Is.EqualTo(18u));
            f.Source.StagePresentationCandidate(next,10.25);f.Install();
            Assert.That(f.Source.PresentationWindowId,Is.EqualTo(2UL),"新Q不能把真实已有的静水误判为ExpectedPrevious=0并撤掉整窗。");
            Assert.That(f.Source.PresentationHolding,Is.True);Assert.That(f.Store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(38UL));
        }

        [Test]
        public void CompleteCurrentWitnessQueuesTheJoinedFlowWithoutSnappingTheWholeWindow()
        {
            using var f=new Fixture();var next=f.Prepare();f.Source.StagePresentationCandidate(next,10.25);f.Install();
            Assert.That(f.Source.TryPublish(next,10.25),Is.True,"完整Current仍由真实Store核对，不能放宽数量witness。");
            Assert.That(f.Source.PresentationWindowId,Is.EqualTo(2UL));Assert.That(f.Source.BufferedPresentationWindows,Is.EqualTo(1));
            Assert.That(f.Source.PresentationSnapped,Is.False);
            f.Source.AdvancePresentation(10.325,false);Assert.That(f.Source.PresentationWindowId,Is.EqualTo(3UL));
            Assert.That(f.Source.TryGetPresentationAlpha(3,out float alpha),Is.True);Assert.That(alpha,Is.EqualTo(.25f).Within(.0001f));
        }

        [Test]
        public void AcceptedFutureOwnsTheStaticPreviousEvidenceAfterCandidateBufferReuse()
        {
            using var f=new Fixture();var next=f.Prepare();f.Source.StagePresentationCandidate(next,10.25);f.Install();
            Assert.That(f.Source.TryPublish(next,10.25),Is.True);Assert.That(f.Source.BufferedPresentationWindows,Is.EqualTo(1));
            f.Source.CancelPresentationCandidate();next.Rows[Fixture.Find(next,Still)]=default;next.RowCount=0;
            f.Source.AdvancePresentation(10.3,false);Assert.That(f.Source.PresentationWindowId,Is.EqualTo(3UL));
            Assert.That(f.Source.TryGetPresentationSample(Still,MaterialId.Water,true,out var old),Is.True);
            Assert.That(old.Quantity,Is.EqualTo(18u));Assert.That(old.OffsetCells,Is.EqualTo(Vector3.zero));
            Assert.That(f.Source.PresentationSnapped,Is.False);Assert.That(f.Store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(38UL));
        }

        [Test]
        public void PreviouslyUnknownWaterCannotBorrowContinuityFromTheQuantityThatArrivesLater()
        {
            using var f=new Fixture();var next=f.Prepare(skipStillChunk:true);
            Assert.That(f.Store.TryGetPublishedChunkFact(Key(Still),out _,out _),Is.False);
            f.Source.StagePresentationCandidate(next,10.25);f.Install();Assert.That(f.Source.TryPublish(next,10.25),Is.True);
            Assert.That(f.Source.PresentationSnapped,Is.True,"只在候选Current到来后才出现的18不能反向授权Previous；必须安全snap。");
            Assert.That(f.Source.TryGetPresentationAlpha(3,out float alpha),Is.True);Assert.That(alpha,Is.EqualTo(1));
            Assert.That(f.Store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(38UL));
        }

        [TestCase("collection")][TestCase("generation")][TestCase("interest")]
        public void CapturedStaticProofCannotSurviveCollectionGenerationOrInterestChanges(string change)
        {
            using var f=new Fixture();var next=f.Prepare();f.Source.StagePresentationCandidate(next,10.25);
            if(change=="collection")f.CollectStillAfterCapture();
            else if(change=="generation")f.Install(generation:2);
            else{f.Install();Assert.That(f.Store.TryEvictInterest(Key(Still)),Is.True);}
            Assert.That(f.Source.TryPublish(next,10.25),Is.False,"捕获过Previous也不能绕过后来的Current/Generation/Interest事实。");
            Assert.That(f.Source.PresentationWindowId,Is.Zero);
            if(change=="collection")Assert.That(f.Store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(29UL));
        }

        [Test]
        public void StaticQuantityWitnessDoesNotAuthorizeAnInventedPreviousPose()
        {
            using var f=new Fixture();var next=f.Prepare();int index=Fixture.Find(next,Still);var row=next.Rows[index];
            next.Rows[index]=new WorldLiquidMotionSample(row.Cell,row.MaterialId,row.PreviousQuantity,row.CurrentQuantity,
                new Vector3(.25f,0,0),Vector3.right,row.CurrentOffset,row.CurrentVelocity,row.AnchorIndex);
            Assert.That(WorldLiquidMotionCodec.Validate(next),Is.True,"负控制为结构合法但没有见证的pose。");
            f.Source.StagePresentationCandidate(next,10.25);f.Install();Assert.That(f.Source.TryPublish(next,10.25),Is.True);
            Assert.That(f.Source.PresentationSnapped,Is.True,"真实静水只能授权其已见证的静止pose，不能只比18GMU就复活任意轨迹。");
        }

        [Test]
        public void HeldStaticSourceStillAppearsInPublicCoverageAfterItsChunkBecomesATombstone()
        {
            using var f=new Fixture(wholeCrossChunk:true);var next=f.Prepare();
            f.Source.StagePresentationCandidate(next,10.25);
            // 源8→目标7为相邻格跨Chunk，并非超距搬运。先装活动Key0和静水源Key2的tombstone，
            // 目标Key1仍等待下一页组：这是正式逐Chunk原子安装可出现的合法中间态。
            f.Install(skipJoinTargetChunk:true);
            Assert.That(f.Source.PresentationHolding,Is.True);
            Assert.That(f.Source.TryGetPresentationSample(Still,MaterialId.Water,true,out var before),Is.True);
            Assert.That(before.Quantity,Is.EqualTo(18u));
            Assert.That(f.Store.TryGetPublishedChunkFact(Key(Still),out var fact,out _),Is.True);
            Assert.That(fact.IsResident,Is.False);Assert.That(f.Store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(20UL));
            var keys=new ElementChunkKey[16];int keyCount=f.Source.CopyLiquidChunkKeys(keys);
            for(int i=0;i<keyCount;i++)Assert.That(keys[i].X==2 && keys[i].Y==0 && keys[i].Z==0,Is.False,
                "真实Store的源Chunk已经不是resident；不能为了表现把它重新放回数量枚举。");
            var changed=new Vector3Int[f.Source.MaximumActiveTransportCells];int count=f.Source.CopyTransportChangedCells(changed);
            Assert.That(Array.IndexOf(changed,Still,0,count),Is.GreaterThanOrEqualTo(0),
                "Renderer只枚举resident与changed范围；既然合法Previous18可读，就必须暴露其源格coverage，不能临时漏整片旧水。");
        }

        [Test]
        public void CancelledStaticCandidateCannotKeepItsTombstonedSourceInPresentationCoverage()
        {
            using var f=new Fixture(wholeCrossChunk:true);var next=f.Prepare();
            f.Source.StagePresentationCandidate(next,10.25);f.Install(skipJoinTargetChunk:true);
            Assert.That(f.Source.PresentationHolding,Is.True);f.Source.CancelPresentationCandidate();
            Assert.That(f.Source.PresentationWindowId,Is.Zero);
            Assert.That(f.Source.TryGetPresentationSample(Still,MaterialId.Water,true,out _),Is.False);
            var changed=new Vector3Int[f.Source.MaximumActiveTransportCells];int count=f.Source.CopyTransportChangedCells(changed);
            Assert.That(Array.IndexOf(changed,Still,0,count),Is.LessThan(0),
                "coverage只属于当前仍合法的candidate/租约；Cancel必须即时撤销，不能把任意旧源页无限留在Renderer。");
            Assert.That(f.Store.TotalQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(20UL));
        }

        [Test]
        public void PublicTransportCapacityIncludesOneHeldCandidateBesideActiveAndTwoFutureWindows()
        {
            using var f=new Fixture();
            Assert.That(f.Source.MaximumActiveTransportCells,Is.EqualTo(512),
                "低profile容量也须给Renderer足量固定scratch：128 active + 256 verified future + 128合法candidate；不能仍按384截断。");
        }

        [Test]
        public void PartialCollectionOfCandidateOnlyStaticWaterDoesNotBorrowAnUnrelatedFuturesHoldingFlag()
        {
            using var f=new Fixture(joinTick:4);f.PrepareActive();
            var future=f.Next();Assert.That(Fixture.Find(future,Still),Is.LessThan(0));
            f.Source.StagePresentationCandidate(future,10.25);f.Install();Assert.That(f.Source.TryPublish(future,10.25),Is.True);
            Assert.That(f.Source.BufferedPresentationWindows,Is.EqualTo(1));Assert.That(f.Source.PresentationWindowId,Is.EqualTo(2UL));
            var candidate=f.Next();candidate.SampleTime=10.39; // 合法10ms捕获抖动，使候选在旧future到期前完整到达。
            Assert.That(candidate.Rows[Fixture.Find(candidate,Still)].PreviousQuantity,Is.EqualTo(18u));
            f.Source.StagePresentationCandidate(candidate,10.29);
            // Static在本轮实际Solver中18→9+9，再由真实Store收走4，剩5。只装该Key的F数量组，
            // 无关A/B的最新已验证future仍与Store一致；不能靠撤掉合法future掩盖candidate读错。
            f.CollectStillAfterCapture(4,onlyStillChunk:true);f.Source.AdvancePresentation(10.29,false);
            Assert.That(f.Source.BufferedPresentationWindows,Is.EqualTo(1));Assert.That(f.Source.PresentationWindowId,Is.EqualTo(2UL));
            Assert.That(f.Store.TryGetQuantity(WorldMaterialDomain.Liquid,Still,(byte)MaterialId.Water,out uint actual),Is.True);
            Assert.That(actual,Is.EqualTo(5u));
            Assert.That(f.Source.TryGetPresentationSample(Still,MaterialId.Water,true,out var old),Is.True);
            Assert.That(old.Quantity,Is.EqualTo(5u),"无关future的Holding不能授权已被F收取的candidate Previous18。");
            Assert.That(f.Source.TryGetPresentationSample(Still,MaterialId.Water,false,out var current),Is.True);
            Assert.That(current.Quantity,Is.EqualTo(5u));Assert.That(f.Source.TotalQuantity,Is.EqualTo(34UL));
        }
    }
}
