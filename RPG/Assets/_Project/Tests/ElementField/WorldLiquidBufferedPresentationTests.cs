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
    /// 通过真实Store安装/驱逐和Renderer消费的公开两相位样本验证时间槽。
    /// 不写私有队列、不只断言内部count：真实GMU已到新格，画面输入仍必须续播旧曲线；
    /// 采集/Interest/固体/冻结/连接变化则必须撤旧。Runtime改动另在staging，先取得行为RED。
    /// </summary>
    public sealed class WorldLiquidBufferedPresentationTests
    {
        private const ulong World=7;
        private const uint Connection=3;
        private static readonly Vector3Int A=new Vector3Int(1,2,1),B=new Vector3Int(2,2,1),C=new Vector3Int(3,2,1),Still=new Vector3Int(0,2,2);
        private static readonly Vector3Int FarB=new Vector3Int(4,2,1),FarC=new Vector3Int(8,2,1);
        private static readonly WorldChunkKey MainKey=Key(A),FarBKey=Key(FarB),FarCKey=Key(FarC);
        private WorldReplicaStore _store;
        private WorldReplicaLiquidReadModel _source;
        private LiquidWorldProfile _profile;
        private LiquidBehaviorProfile _water;
        [SetUp]public void Setup()
        {
            _profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();_water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":32,\"_maximumActiveCells\":512,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",_profile);
            typeof(LiquidWorldProfile).GetField("_materials",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_profile,new[]{_water});
            _store=new WorldReplicaStore(World,32,1024,0,8192);
            var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Vector3.zero,.5f,4);
            Assert.That(_store.ConfigureGeometry(geometry),Is.True);
            Assert.That(_store.ConfigureMaterials(WorldMaterialDomain.Liquid,510,new[]{(byte)MaterialId.Water}),Is.True);
            Assert.That(_store.ConfigureGeometry(new WorldGridGeometry(WorldMaterialDomain.Solid,Vector3.zero,.5f,4)),Is.True);
            Assert.That(_store.ConfigureMaterials(WorldMaterialDomain.Solid,510,new[]{(byte)MaterialId.Ice}),Is.True);
            _source=new WorldReplicaLiquidReadModel(_store,geometry,_profile,.25f,null);_source.ResetConnection(Connection);
        }
        [TearDown]public void Cleanup(){Object.DestroyImmediate(_profile);Object.DestroyImmediate(_water);}

        [TestCase(.006307526,.000903295)]
        [TestCase(.05,0)]
        public void ValidatedFutureMustNotReplaceAnUnfinishedOldCurve(double futureLead,double timestampJitter)
        {
            Prepare(false);double sample=10.2+timestampJitter,clock=sample-futureLead;
            var next=Move(381,sample,B,C,3);AcceptNext(next,clock,false);
            // 当前Store确实已经到C；表现缓冲不能倒写Store或推迟ACK来等待显示时间。
            Assert.That(_source.TryGetQuantity(C,MaterialId.Water,out uint quantity),Is.True);Assert.That(quantity,Is.EqualTo(20u));
            Assert.That(_source.TotalQuantity,Is.EqualTo(20UL));
            Assert.That(VisibleCentroidX(),Is.EqualTo(ExpectedOldCenter(clock)).Within(1e-5),
                "新窗口的Previous=B不能截掉旧A→B尚未播放的尾段。");
            Assert.That(_source.PresentationWindowId,Is.EqualTo(380UL));
            double start=Math.Max(sample,10.2);_source.AdvancePresentation(start,false);
            Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)).Within(1e-5));
            Assert.That(_source.PresentationWindowId,Is.EqualTo(381UL));
            _source.AdvancePresentation(start+.025,false);
            Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)+(Center(C)-Center(B))*.25).Within(1e-4));
        }

        [Test]public void AcceptedFutureSurvivesAssemblerCommitAndReuseOfTheNextCandidateBuffer()
        {
            Prepare(false);var next=Move(381,10.2,B,C,3);_source.StagePresentationCandidate(next,10.15);Install(MainKey,3,Cell(C,20));
            var assembler=new WorldLiquidMotionAssembler(World,Connection);Decode(assembler,next,.1);
            Assert.That(assembler.TryGetCompletedCandidate(out var borrowed),Is.True);
            Assert.That(_source.TryPublish(borrowed,10.15),Is.True,"完整数量witness允许接受，而显示仍可等待。");
            Assert.That(assembler.CommitCandidate(),Is.True);Assert.That(assembler.AcceptedSequence,Is.EqualTo(381UL));
            var second=Move(382,10.276,C,B,4);_source.StagePresentationCandidate(second,10.176);Install(MainKey,4,Cell(B,20));
            Decode(assembler,second,.11);Assert.That(assembler.TryGetCompletedCandidate(out var secondBorrow),Is.True);
            Assert.That(_source.TryPublish(secondBorrow,10.176),Is.True);Assert.That(assembler.CommitCandidate(),Is.True);
            // 两份Assembler数组轮换：第三次Decode才真正复用最初传给Sink的381数组。
            Decode(assembler,Move(383,10.376,B,A,5),.12);
            Assert.That(assembler.TryGetCompletedCandidate(out var replacement),Is.True);
            // 第二候选再次解码到新内容，不能使Source借用已被复用的第一份数组。
            replacement.Rows[0]=Row(B,20,0,0);
            Assert.That(VisibleCentroidX(),Is.EqualTo(ExpectedOldCenter(10.176)).Within(1e-5));
            _source.AdvancePresentation(10.2,false);
            Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)).Within(1e-5));
            _source.AdvancePresentation(10.225,false);
            Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)+(Center(C)-Center(B))*.25).Within(1e-4));
        }

        [Test]public void EarlierRawTimestampWaitsForThePreviousFullSlotAndStartsAtZero()
        {
            Prepare(false);AcceptNext(Move(381,10.187,B,C,3),10.16,false);
            _source.AdvancePresentation(10.199,false);
            Assert.That(VisibleCentroidX(),Is.EqualTo(ExpectedOldCenter(10.199)).Within(1e-5));
            _source.AdvancePresentation(10.2,false);
            Assert.That(_source.PresentationWindowId,Is.EqualTo(381UL));
            Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)).Within(1e-5),"87ms timestamp不能从下一100ms曲线的.13处起播。");
            _source.AdvancePresentation(10.21,false);
            Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)+(Center(C)-Center(B))*.1).Within(1e-4));
        }
        [Test]public void PromotingFirstFutureDoesNotForgetANewerPartialCandidate()
        {
            Prepare(false);AcceptNext(Move(381,10.2,B,C,3),10.15,false);
            var second=Move(382,10.276,C,B,4);_source.StagePresentationCandidate(second,10.176);Install(MainKey,4,Cell(B,20));
            _source.AdvancePresentation(10.2,false);
            Assert.That(_source.PresentationWindowId,Is.EqualTo(381UL));
            Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)).Within(1e-5));
            Assert.That(_source.TryPublish(second,10.2),Is.True);
            _source.AdvancePresentation(10.225,false);
            Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)+(Center(C)-Center(B))*.25).Within(1e-4));
            Assert.That(_source.TotalQuantity,Is.EqualTo(20UL));
        }
        [Test]public void TwoFutureCapacityHasAnExplicitLatestCurrentFallbackInsteadOfGrowingOrDroppingAMiddleSilently()
        {
            Prepare(false);bool sawTwo=false,sawFallback=false;long before=_source.BufferedPresentationOverflows;
            for(int index=1;index<=18;index++)
            {
                double sample=10.2+(index-1)*.076,clock=sample-.05;
                TracePlayback("beforeAdvance",index,sample);
                // 这里专门压测容量，而不是混入Render帧越过100ms候选租约的另一条撤队边界。
                // 1ms步进会实际经过100ms显示槽；原10ms步进在10.408先触发旧lease，
                // 导致尚未进入cap分支便清队。这两种安全降级不能拿同一个计数冒充。
                while(_source.PresentationServerTime+.001<clock)_source.AdvancePresentation(_source.PresentationServerTime+.001,false);
                _source.AdvancePresentation(clock,false);
                TracePlayback("afterAdvance",index,sample);
                var previous=new Vector3((index-1)*.01f,0,0);var current=new Vector3(index*.01f,0,0);
                var next=Frame((uint)(380+index),sample,new[]{new WorldLiquidMotionAnchor(MainKey,1,(ulong)(index+2),1,true)},
                    new[]{new WorldLiquidMotionSample(B,(byte)MaterialId.Water,20,20,previous,Vector3.zero,current,Vector3.zero,0)});
                _source.StagePresentationCandidate(next,clock);Install(MainKey,(ulong)(index+2),Cell(B,20));
                Assert.That(_source.TryPublish(next,clock),Is.True);Assert.That(_source.BufferedPresentationWindows,Is.LessThanOrEqualTo(2));
                TracePlayback("afterPublish",index,sample);
                sawTwo|=_source.BufferedPresentationWindows==2;
                if(_source.BufferedPresentationOverflows>before)
                {
                    sawFallback=true;Assert.That(_source.PresentationSnapped,Is.True);Assert.That(_source.BufferedPresentationWindows,Is.Zero);
                    Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)+current.x*.5).Within(1e-5));
                    Assert.That(_source.TotalQuantity,Is.EqualTo(20UL));break;
                }
            }
            Assert.That(sawTwo,Is.True,"必须实际走过两个future，而不是测试空队列的容量常量。");
            Assert.That(sawFallback,Is.True,"76ms连续样本累积显示压力应触发有界、可观测降级。");
        }

        private void TracePlayback(string phase,int index,double sample)
        {
            ulong active=_source.PresentationWindowId;_source.TryGetPresentationAlpha(active,out float alpha);
            TestContext.WriteLine("buffered-window "+JsonUtility.ToJson(new PlaybackTrace{Phase=phase,Index=index,
                SampleTime=sample,Clock=_source.PresentationServerTime,Active=active,
                PlaybackStart=_source.PresentationPlaybackStartTime,Alpha=alpha,Queue=_source.BufferedPresentationWindows,
                Overflows=_source.BufferedPresentationOverflows,Discontinuities=_source.BufferedPresentationDiscontinuities,
                InvalidationRevision=_source.PresentationInvalidationRevision,Snapped=_source.PresentationSnapped,
                PublishFailure=_source.LastMotionPublishFailure,AcceptedAttempt=_source.LastAttemptedMotionSequence}));
        }
        [Serializable]private struct PlaybackTrace
        {
            public string Phase;public int Index,Queue;public double SampleTime,Clock,PlaybackStart;public float Alpha;
            public ulong Active,AcceptedAttempt;public long Overflows,Discontinuities;public uint InvalidationRevision;
            public bool Snapped;public byte PublishFailure;
        }
        [TestCase("none")][TestCase("harvest")][TestCase("solid")]
        public void ARenderFrameCrossingTheLeaseDeadlineActivatesADueVerifiedFutureAfterSafetyValidation(string boundary)
        {
            Prepare(false);
            // 76ms捕获间隔会让第382窗从10.300起播；其后继383已完整验证、起点10.400。
            // 当前显示382的lease在10.302开始。真实Render帧从10.398跨到10.408时，
            // 应先安全切到已到期383，而非因旧lease在10.402到期而撤掉已验证的整条链。
            for(int index=1;index<=4;index++)
            {
                double sample=10.2+(index-1)*.076,clock=sample-.05;
                while(_source.PresentationServerTime+.01<clock)_source.AdvancePresentation(_source.PresentationServerTime+.01,false);
                _source.AdvancePresentation(clock,false);
                var next=Frame((uint)(380+index),sample,new[]{new WorldLiquidMotionAnchor(MainKey,1,(ulong)(index+2),1,true)},
                    new[]{new WorldLiquidMotionSample(B,(byte)MaterialId.Water,20,20,new Vector3((index-1)*.01f,0,0),Vector3.zero,new Vector3(index*.01f,0,0),Vector3.zero,0)});
                _source.StagePresentationCandidate(next,clock);Install(MainKey,(ulong)(index+2),Cell(B,20));
                Assert.That(_source.TryPublish(next,clock),Is.True);TracePlayback("deadlineAccepted",index,sample);
            }
            _source.AdvancePresentation(10.398,false);
            Assert.That(_source.PresentationWindowId,Is.EqualTo(382UL));Assert.That(_source.BufferedPresentationWindows,Is.EqualTo(2));
            if(boundary=="harvest")Install(MainKey,7);
            else if(boundary=="solid")InstallSolid(B);
            _source.AdvancePresentation(10.408,false);TracePlayback("deadlineCrossed",5,10.428);
            if(boundary!="none")
            {
                Assert.That(_source.PresentationWindowId,Is.Zero,"到期future也不能跨过真实采集或固体变化继续保留旧壳。");
                Assert.That(_source.BufferedPresentationWindows,Is.Zero);
                _source.TryGetQuantity(B,MaterialId.Water,out uint real);Assert.That(real,Is.EqualTo(boundary=="harvest"?0u:20u));return;
            }
            Assert.That(_source.PresentationWindowId,Is.EqualTo(383UL),"完整future已到期，10ms Render跳时不能先销毁它。");
            Assert.That(_source.BufferedPresentationWindows,Is.EqualTo(1));Assert.That(_source.PresentationSnapped,Is.False);
            Assert.That(VisibleCentroidX(),Is.EqualTo(Center(B)+(.02+.01*.08)*.5).Within(1e-5));
            Assert.That(_source.TotalQuantity,Is.EqualTo(20UL));
        }
        [Test]public void FrozenWithoutFuturePreservesTheExistingPauseAlphaAndResumeSnapContract()
        {
            Prepare(false);_source.AdvancePresentation(10.16,true);double stopped=VisibleCentroidX();
            _source.AdvancePresentation(10.25,true);Assert.That(VisibleCentroidX(),Is.EqualTo(stopped).Within(1e-6));
            Assert.That(_source.PresentationWindowId,Is.EqualTo(380UL));
            _source.AdvancePresentation(10.26,false);Assert.That(_source.PresentationWindowId,Is.Zero);
            Assert.That(_source.TryGetQuantity(B,MaterialId.Water,out uint current),Is.True);Assert.That(current,Is.EqualTo(20u));
        }

        [TestCase("harvest")][TestCase("solid")]
        public void CancelWithAnAcceptedFutureStillValidatesRealChangesOnPublicReadWithoutAdvance(string boundary)
        {
            Prepare(false);AcceptNext(Move(381,10.2,B,C,3),10.15,false);
            Assert.That(_source.BufferedPresentationWindows,Is.EqualTo(1));
            if(boundary=="harvest")Install(MainKey,4);else InstallSolid(B);
            _source.CancelPresentationCandidate();
            // 刻意不调用Advance：Cancel只把accepted future的校验缓存标脏，外部消费者
            // 直接读取播放窗口或两相位，也必须立即看见真实F/固体变更并撤掉旧A/B。
            Assert.That(_source.PresentationWindowId,Is.Zero);
            Assert.That(_source.BufferedPresentationWindows,Is.Zero);
            Assert.That(_source.TryGetPresentationSample(A,MaterialId.Water,true,out var oldA),Is.False);Assert.That(oldA.Quantity,Is.Zero);
            Assert.That(_source.TryGetPresentationSample(B,MaterialId.Water,false,out var oldB),Is.False);Assert.That(oldB.Quantity,Is.Zero);
            bool exists=_source.TryGetPresentationSample(C,MaterialId.Water,false,out var current);
            Assert.That(exists,Is.EqualTo(boundary!="harvest"));Assert.That(current.Quantity,Is.EqualTo(boundary=="harvest"?0u:20u));
            Assert.That(_source.TotalQuantity,Is.EqualTo(boundary=="harvest"?0UL:20UL));
        }

        [Test]public void PartialTargetInstallContinuesOriginalPhasesInsteadOfForcingOldCurrent()
        {
            PrepareAcrossChunks();var next=Across(381,10.2,FarB,FarC,2,1);
            _source.StagePresentationCandidate(next,10.15);Tombstone(FarBKey,2);_source.AdvancePresentation(10.15,false);
            Assert.That(_source.TotalQuantity,Is.Zero,"跨Chunk中间态真实Store暂未有目标；显示不授予GMU。");
            Assert.That(_source.TryGetQuantity(FarC,MaterialId.Water,out uint future),Is.False);Assert.That(future,Is.Zero);
            Assert.That(VisibleCentroidX(A,FarB,FarC),Is.EqualTo((Center(A)+Center(FarB))*.5).Within(1e-5));
            Assert.That(_source.PresentationHolding,Is.True);
            _source.AdvancePresentation(10.27,false);
            Assert.That(_source.PresentationWindowId,Is.Zero,"无完整后继不能延长旧尾段租约。");
            Assert.That(_source.TryGetPresentationSample(FarB,MaterialId.Water,false,out var gone),Is.False);Assert.That(gone.Quantity,Is.Zero);
        }

        [Test]public void MissingUnchangedRowKeepsItsStrictCurrentWitnessAndDoesNotCancelOtherRowsTail()
        {
            Prepare(true);var next=Move(381,10.2,B,C,3);_source.StagePresentationCandidate(next,10.15);
            Install(MainKey,3,Cell(Still,10),Cell(C,20));_source.AdvancePresentation(10.15,false);
            Assert.That(_source.TryGetQuantity(Still,MaterialId.Water,out uint still),Is.True);Assert.That(still,Is.EqualTo(10u));
            Assert.That(_source.PresentationWindowId,Is.EqualTo(380UL),"未改变且witness仍成立的旧Current无需出现在新运动集合。");
            Assert.That(VisibleCentroidX(),Is.EqualTo(ExpectedOldCenter(10.15)).Within(1e-5));
            Assert.That(_source.TryPublish(next,10.15),Is.True);
            Assert.That(VisibleCentroidX(),Is.EqualTo(ExpectedOldCenter(10.15)).Within(1e-5));
            Assert.That(_source.TotalQuantity,Is.EqualTo(30UL));
        }

        [Test]public void MissingChangedRowCannotBorrowTheUnchangedRowException()
        {
            Prepare(true);var next=Move(381,10.2,B,C,3);_source.StagePresentationCandidate(next,10.15);
            Install(MainKey,3,Cell(Still,9),Cell(C,20));_source.AdvancePresentation(10.15,false);
            Assert.That(_source.PresentationWindowId,Is.Zero,"真实采集改量后的缺行仍必须使旧壳失效。");
            Assert.That(_source.TryGetPresentationSample(Still,MaterialId.Water,true,out var current),Is.True);
            Assert.That(current.Quantity,Is.EqualTo(9u));Assert.That(_source.TotalQuantity,Is.EqualTo(29UL));
        }

        [TestCase("harvest")][TestCase("interest")][TestCase("solid")]
        [TestCase("frozen")][TestCase("epoch")][TestCase("generation")]
        public void AVerifiedFutureNeverKeepsOldCurveAfterItsSafetyBoundaryChanges(string boundary)
        {
            Prepare(false);AcceptNext(Move(381,10.2,B,C,3),10.15,false);
            if(boundary=="harvest")Install(MainKey,4);
            else if(boundary=="interest")Assert.That(_store.TryEvictInterest(MainKey),Is.True);
            else if(boundary=="solid")InstallSolid(B);
            else if(boundary=="epoch")_source.ResetConnection(Connection+1);
            else if(boundary=="generation")Install(MainKey,1,2,Cell(C,20));
            _source.AdvancePresentation(10.16,boundary=="frozen");
            Assert.That(_source.PresentationWindowId,Is.Zero,boundary+"必须撤销active及已验证future。");
            // 真实Store被移除时图像输入必须也无量；其余情况回落到当前C，不能复活旧A/B。
            Assert.That(_source.TryGetPresentationSample(A,MaterialId.Water,true,out var oldA),Is.False);Assert.That(oldA.Quantity,Is.Zero);
            Assert.That(_source.TryGetPresentationSample(B,MaterialId.Water,false,out var oldB),Is.False);Assert.That(oldB.Quantity,Is.Zero);
            bool exists=_source.TryGetPresentationSample(C,MaterialId.Water,false,out var latest);
            bool removed=boundary=="harvest" || boundary=="interest";
            Assert.That(exists,Is.EqualTo(!removed));Assert.That(latest.Quantity,Is.EqualTo(removed?0u:20u));
        }

        private void Prepare(bool stationary)
        {
            Install(MainKey,1,stationary?new[]{Cell(A,20),Cell(Still,10)}:new[]{Cell(A,20)});
            var first=Frame(379,10,new[]{new WorldLiquidMotionAnchor(MainKey,1,1,1,true)},
                stationary?new[]{Row(A,20,20,0),Row(Still,10,10,0)}:new[]{Row(A,20,20,0)});
            Assert.That(_source.TryPublish(first,10.01),Is.True);
            var active=Move(380,10.1,A,B,2);
            if(stationary){active.Rows[2]=Row(Still,10,10,0);active.RowCount=3;}
            _source.StagePresentationCandidate(active,10.14);
            Install(MainKey,2,stationary?new[]{Cell(B,20),Cell(Still,10)}:new[]{Cell(B,20)});
            Assert.That(_source.TryPublish(active,10.14),Is.True);
            _source.AdvancePresentation(10.15,false);
        }
        private void PrepareAcrossChunks()
        {
            Install(MainKey,1,Cell(A,20));
            Assert.That(_source.TryPublish(Frame(379,10,new[]{new WorldLiquidMotionAnchor(MainKey,1,1,1,true)},new[]{Row(A,20,20,0)}),10.01),Is.True);
            var active=Across(380,10.1,A,FarB,2,1);_source.StagePresentationCandidate(active,10.14);
            Tombstone(MainKey,2);Install(FarBKey,1,Cell(FarB,20));Assert.That(_source.TryPublish(active,10.14),Is.True);
        }
        private void AcceptNext(WorldLiquidMotionFrame next,double clock,bool stationary)
        {_source.StagePresentationCandidate(next,clock);Install(MainKey,3,stationary?new[]{Cell(Still,10),Cell(C,20)}:new[]{Cell(C,20)});Assert.That(_source.TryPublish(next,clock),Is.True);}
        private double VisibleCentroidX(params Vector3Int[] cells)
        {
            if(cells.Length==0)cells=new[]{A,B,C};ulong window=_source.PresentationWindowId;
            Assert.That(_source.TryGetPresentationAlpha(window,out float alpha),Is.True);
            double mass=0,weighted=0;foreach(var cell in cells)
            {
                _source.TryGetPresentationSample(cell,MaterialId.Water,true,out var old);
                _source.TryGetPresentationSample(cell,MaterialId.Water,false,out var current);
                double previousMass=old.Quantity*(1-alpha),currentMass=current.Quantity*alpha;mass+=previousMass+currentMass;
                weighted+=previousMass*(Center(cell)+old.OffsetCells.x*.5)+currentMass*(Center(cell)+current.OffsetCells.x*.5);
            }
            Assert.That(mass,Is.GreaterThan(0));return weighted/mass;
        }
        private static double ExpectedOldCenter(double clock)=>Center(A)+(Center(B)-Center(A))*Math.Max(0,Math.Min(1,(clock-10.1)/.1));
        private static double Center(Vector3Int cell)=>(cell.x+.5)*.5;
        private static WorldChunkKey Key(Vector3Int cell)=>WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid,cell,4);
        private static WorldLiquidMotionSample Row(Vector3Int cell,uint previous,uint current,int anchor)=>new WorldLiquidMotionSample(cell,(byte)MaterialId.Water,previous,current,Vector3.zero,Vector3.zero,Vector3.zero,Vector3.zero,anchor);
        private static WorldLiquidMotionFrame Frame(uint tick,double sample,WorldLiquidMotionAnchor[] anchors,WorldLiquidMotionSample[] rows)
        {var frame=new WorldLiquidMotionFrame{WorldEpoch=World,ConnectionEpoch=Connection,Sequence=tick,MaterialTick=tick,SampleTime=sample,Interval=.1f,ChunkSize=4,AnchorCount=anchors.Length,RowCount=rows.Length};Array.Copy(anchors,frame.Anchors,anchors.Length);Array.Copy(rows,frame.Rows,rows.Length);return frame;}
        private static WorldLiquidMotionFrame Move(uint tick,double time,Vector3Int from,Vector3Int to,ulong revision)=>Frame(tick,time,new[]{new WorldLiquidMotionAnchor(MainKey,1,revision,1,true)},new[]{Row(from,20,0,0),Row(to,0,20,0)});
        private static WorldLiquidMotionFrame Across(uint tick,double time,Vector3Int from,Vector3Int to,ulong fromRevision,ulong toRevision)=>Frame(tick,time,new[]{new WorldLiquidMotionAnchor(Key(from),1,fromRevision,1,false),new WorldLiquidMotionAnchor(Key(to),1,toRevision,1,true)},new[]{Row(from,20,0,0),Row(to,0,20,1)});
        private static WorldMaterialCellSample Cell(Vector3Int cell,uint quantity)=>new WorldMaterialCellSample(WorldMaterialDomain.Liquid,WorldMaterialStorage.Resident,cell,(byte)MaterialId.Water,quantity);
        private void Install(WorldChunkKey key,ulong revision,params WorldMaterialCellSample[] cells)=>Install(key,revision,1,cells);
        private void Install(WorldChunkKey key,ulong revision,ulong generation,params WorldMaterialCellSample[] cells)
        {
            // 正式Payload按xyz/material严格排序。先规范化夹具，不能让Codec前置失败冒充时间槽RED。
            Array.Sort(cells,(a,b)=>{int compare=a.Cell.x.CompareTo(b.Cell.x);if(compare!=0)return compare;compare=a.Cell.y.CompareTo(b.Cell.y);if(compare!=0)return compare;compare=a.Cell.z.CompareTo(b.Cell.z);return compare!=0?compare:a.MaterialId.CompareTo(b.MaterialId);});
            var words=new uint[11+cells.Length*WorldChunkPayloadCodec.CellWords];words[0]=WorldChunkPayloadCodec.Magic;words[1]=(uint)cells.Length;words[3]=1;
            WorldChunkPayloadCodec.WriteGeometry(words,4,new WorldGridGeometry(key.Domain,Vector3.zero,.5f,4));
            for(int i=0;i<cells.Length;i++)WorldChunkPayloadCodec.WriteCell(words,11+i*WorldChunkPayloadCodec.CellWords,cells[i]);
            Assert.That(_store.TryInstall(new WorldChunkPayloadHeader(World,revision,key,generation,0,revision,381,words.Length,WorldChunkPayloadCodec.ComputeChecksum(words,words.Length),true),words,words.Length),Is.True);
        }
        private void Tombstone(WorldChunkKey key,ulong revision)=>Assert.That(_store.TryInstall(new WorldChunkPayloadHeader(World,revision,key,1,0,revision,381,0,2166136261u,false),null,0),Is.True);
        private void InstallSolid(Vector3Int cell)
        {var key=new WorldChunkKey(WorldMaterialDomain.Solid,0,0,0);Install(key,1,new WorldMaterialCellSample(WorldMaterialDomain.Solid,WorldMaterialStorage.Resident,cell,(byte)MaterialId.Ice,510));}
        private static void Decode(WorldLiquidMotionAssembler assembler,WorldLiquidMotionFrame frame,double now)
        {
            var words=new uint[WorldLiquidMotionCodec.MaxWords];Assert.That(WorldLiquidMotionCodec.TryEncode(frame,words,out int count),Is.True);
            int pages=(count+WorldLiquidMotionAssembler.PageWords-1)/WorldLiquidMotionAssembler.PageWords;
            var header=new WorldLiquidMotionPageHeader(World,Connection,frame.Sequence,frame.MaterialTick,count,pages,WorldLiquidMotionCodec.ComputeChecksum(words,count));
            Assert.That(assembler.TryBegin(header,now),Is.True);
            for(int page=0;page<pages;page++){int offset=page*WorldLiquidMotionAssembler.PageWords,n=Math.Min(WorldLiquidMotionAssembler.PageWords,count-offset);var data=new uint[n];Array.Copy(words,offset,data,0,n);Assert.That(assembler.TryReceivePage(header,page,data,n,now),Is.True);}
        }
    }
}
