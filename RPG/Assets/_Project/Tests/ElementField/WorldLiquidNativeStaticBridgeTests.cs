using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 复现Native pulse-fall的真实Solver输入，而不是手写一个静止Previous当正例。
    /// P7配置、K8、0.5m液格、100ms窗口、512GMU/400ms、4.125m高、零初速。
    /// World写路由→Liquid Solver→World数量Source/CommitOwner/Codec→Replica Store→Motion Reader。
    /// 只构造隔离Authority宿主，不启用场景Tick/Rendering，不改Store数量、History或播放私有字段。
    /// 取证中的反射只读KnownPrevious，记录拒绝证明的具体条件，不给该证明赋值。
    /// </summary>
    public sealed class WorldLiquidNativeStaticBridgeTests
    {
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        private static readonly Vector3 Origin=new Vector3(100,30,100);
        private static readonly Vector3 NozzleOffset=new Vector3(.125f,4.125f,.125f);
        private static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,Fields).SetValue(owner,value);

        [Serializable]private sealed class RowEvidence
        {
            public int Tick,CellX,CellY,CellZ,Material,LastAuthorityAppearanceTick,LastAuthorityQuantity;
            public bool InPriorMotion,StoreFactExists,StoreResident,StoreInterested,StoreQuantityFound,DisplayFound,KnownProofAfterStage;
            public ulong StoreQuantity,StoreEpoch,StoreGeneration,StoreRevision,ExpectedGeneration,ExpectedRevision;
            public uint PreviousQuantity,CurrentQuantity,DisplayQuantity;
            public Vector3 PreviousOffset,PreviousVelocity,CurrentOffset,CurrentVelocity,DisplayOffset,DisplayVelocity,LastAuthorityOffset,LastAuthorityVelocity;
            public float IndependentOffsetDifferenceSquared,IndependentVelocityDifferenceSquared;
        }
        [Serializable]private sealed class State
        {
            public string Point;
            public ulong Window;
            public bool Snapped,Holding,AlphaAvailable;
            public float Alpha;
            public int Buffered;
            public uint Invalidations;
            public WorldLiquidMotionValidationDiagnostic Hold;
        }
        [Serializable]private sealed class TickEvidence
        {
            public int Tick,MotionRows,SourceMotionRows,SourceAnchorCount,AuthorityQuantityCells,InstalledChunks;
            public uint MaterialTick;
            public ulong Sequence,AuthorityQuantity,AuthorityPending,ReplicaQuantity,ReplicaPending,AcceptedQuantity;
            public double ArrivalClock,SampleTime;
            public bool MotionBudgetSkipped,CurrentWitnessAccepted,ConsecutiveAuthorityWindow,AnchorsCompatible,ContinuousExpected,LostOldCurve,ActivatedSnapped;
            public bool GenerationTransitionExpected,RetainedGenerationChanges,GenerationSafetyVerified;
            public string BudgetSkipReason;
            public State BeforeStage,AfterStage,FirstChangedInstall,AfterInstall,AfterPublish,AfterActivation;
            public RowEvidence[] ReenteredRows;
            public AnchorEvidence[] PreStageAnchors;
        }
        [Serializable]private sealed class AnchorEvidence
        {
            public string Kind;
            public int RetainedWindow,ChunkX,ChunkY,ChunkZ;
            public bool StoreExists,StoreResident,StoreInterested,AuthorityExists,AuthorityResident;
            public ulong AnchorGeneration,AnchorRevision,SubscriptionEpoch,StoreGeneration,StoreRevision,AuthorityGeneration,AuthorityRevision;
        }
        [Serializable]private sealed class Report
        {
            public string Scope="Real P7 Solver and quantity/motion codecs; M Stage before same-window Q. No GPU/pixel or performance claim.",Error;
            public int InjectedBatches,ReenteredRowObservations,ExpectedContinuousWindows,UnexpectedWholeWindowSnaps,FirstUnexpectedWholeWindowSnapTick,BudgetSkippedWindows;
            public int GenerationTransitionWindows,VerifiedGenerationSafetySnaps;
            public ulong AcceptedQuantity;
            public TickEvidence[] Ticks;
        }
        private readonly struct SeenRow
        {
            internal readonly int Tick;
            internal readonly WorldLiquidMotionSample Row;
            internal SeenRow(int tick,WorldLiquidMotionSample row){Tick=tick;Row=row;}
        }

        private sealed class Fixture:IDisposable
        {
            internal readonly GameObject Host,Floor;
            internal readonly LiquidWorldRuntime Liquid;
            internal readonly WorldReplicaStore Store=new WorldReplicaStore(7,128,8192,256,16384);
            internal readonly WorldReplicaLiquidReadModel Reader;
            internal readonly Dictionary<Vector3Int,SeenRow> Seen=new Dictionary<Vector3Int,SeenRow>();
            internal WorldLiquidMotionFrame Prior;
            internal int Tick,Injections;
            internal ulong Accepted;
            internal bool MotionBudgetSkipped;
            internal int SourceAnchorCount;
            internal int SourceMotionRows=>_capture.Count;
            private readonly LiquidWorldProfile _liquidProfile;
            private readonly ElementWorldProfile _worldProfile;
            private readonly ElementWorldRuntime _world;
            private readonly WorldReplicationSource _quantitySource;
            private readonly WorldLiquidMotionSource _motionSource;
            private readonly WorldReplicationCommitOwner _owner=new WorldReplicationCommitOwner(7,128,8192,256,128,16384,2000000);
            private readonly WorldReplicaBuffer _quantities=new WorldReplicaBuffer(8192,256,2000000);
            private readonly WorldLiquidMotionCaptureBuffer _capture=new WorldLiquidMotionCaptureBuffer();
            private readonly uint[] _words=new uint[16384],_motionWords=new uint[WorldLiquidMotionCodec.MaxWords];
            internal int QuantityCellCount=>_quantities.Header.CellCount;

            internal Fixture()
            {
                Host=new GameObject("Native static bridge isolated authority");Host.SetActive(false);Host.hideFlags=HideFlags.DontSave;Host.transform.position=Origin;
                Floor=GameObject.CreatePrimitive(PrimitiveType.Cube);Floor.hideFlags=HideFlags.DontSave;
                Floor.transform.position=Origin+new Vector3(0,-.25f,0);Floor.transform.localScale=new Vector3(32,.5f,32);Physics.SyncTransforms();
                _liquidProfile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
                _worldProfile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<ElementWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset"));
                Assert.That(_liquidProfile,Is.Not.Null);Assert.That(_worldProfile,Is.Not.Null);
                JsonUtility.FromJsonOverwrite("{\"_chunkSize\":8,\"_cellSize\":0.25}",_worldProfile);
                _world=Host.AddComponent<ElementWorldRuntime>();_world.ConfigureExecutionMode(WorldExecutionMode.Authority);
                Set(_world,"_profile",_worldProfile);Set(_world,"_origin",Origin);Set(_world,"_settings",_worldProfile.CreateSimulationSettings());
                Set(_world,"_store",new ElementWorldStore(8,8));
                var baker=Host.GetComponent<ElementFieldSolidBaker>();Assert.That(baker,Is.Not.Null);
                Liquid=Host.AddComponent<LiquidWorldRuntime>();Set(Liquid,"_profile",_liquidProfile);
                Assert.That(Liquid.TryInitialize(Origin,.25f,8,baker),Is.True);
                Assert.That(Liquid.CellSize,Is.EqualTo(.5f));Assert.That(Liquid.ChunkSize,Is.EqualTo(8));Assert.That(Liquid.CellCapacityGmu,Is.EqualTo(510));
                Set(_world,"_gridLiquidRuntime",Liquid);Set(_world,"_solidBaker",baker);Set(_world,"_hasGridLiquidRoute",true);
                Set(_world,"_effectiveWaterSimulationMode",WaterSimulationMode.GridLiquid);
                var backends=new MaterialSimulationBackendKind[256];var present=new bool[256];
                backends[(byte)MaterialId.Water]=MaterialSimulationBackendKind.GridLiquid;present[(byte)MaterialId.Water]=true;
                var routes=(MaterialSimulationRouteSnapshot)Activator.CreateInstance(typeof(MaterialSimulationRouteSnapshot),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{backends,present},null);
                Set(_world,"_materialRoutes",routes);Set(_world,"<IsInitialized>k__BackingField",true);
                var geometry=new WorldGridGeometry(WorldMaterialDomain.Liquid,Origin,Liquid.CellSize,Liquid.ChunkSize);
                Assert.That(Store.ConfigureGeometry(geometry),Is.True);
                var materials=new byte[Liquid.MaterialCount];for(int i=0;i<materials.Length;i++)materials[i]=(byte)Liquid.GetLiquidMaterial(i);
                Assert.That(Store.ConfigureMaterials(WorldMaterialDomain.Liquid,(uint)Liquid.CellCapacityGmu,materials),Is.True);
                Reader=new WorldReplicaLiquidReadModel(Store,geometry,_liquidProfile,.25f,baker);Reader.ResetConnection(3);
                _quantitySource=new WorldReplicationSource(_world);_motionSource=new WorldLiquidMotionSource(_world);
            }
            internal void AdvanceSolver()
            {
                Tick++;
                if(Tick<=37 && (Tick-1)%4==0)
                {
                    var request=new ElementWriteRequest(Origin+NozzleOffset,MaterialId.Water,512,.25f,false,Vector3.zero);
                    Assert.That(_world.TryEnqueueWrite(in request),Is.True,"必须走正式World写路由，不直接添Store数量。");
                    Injections++;Accepted+=512;
                }
                // 宿主不跑Scene Update，只由本测试显式推进现有Liquid Owner 10Hz。
                Liquid.Step(.1f,Tick*.1f);Set(_world,"_worldTick",(long)Tick);
                Assert.That(Liquid.TotalQuantity+Liquid.PendingQuantity,Is.EqualTo(Accepted));
                Assert.That(_quantitySource.TryCapture(_quantities,out var failure),Is.True,"完整数量Source失败："+failure);
                Assert.That(_owner.TryCaptureCommit(_quantities),Is.True);
                Assert.That(_motionSource.TryCapture(_capture,_quantities),Is.True);
            }
            internal WorldLiquidMotionFrame DecodeMotion()
            {
                var frame=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=(ulong)Tick,MaterialTick=_capture.Epoch,SampleTime=10+Tick*.1,Interval=_capture.Interval,ChunkSize=8};
                // Authority Source能导出多个完整组，wire单窗仍严格为128行/32Anchor。
                // 这里不假装实现生产Sender的Interest/整组选择，更不能截断半组制造不守恒Motion。
                // 超出时显式不发送整份M，Q仍完整安装；该窗及紧随的断档恢复不评价连续播放。
                var keys=new HashSet<WorldChunkKey>();for(int i=0;i<_capture.Count;i++)keys.Add(WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid,_capture.Rows[i].Cell,8));
                SourceAnchorCount=keys.Count;MotionBudgetSkipped=_capture.Count>WorldLiquidMotionFrame.MaxRows || keys.Count>WorldLiquidMotionFrame.MaxAnchors;
                if(MotionBudgetSkipped)return frame;
                for(int i=0;i<_capture.Count;i++)
                {
                    var row=_capture.Rows[i];var key=WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Liquid,row.Cell,8);int anchor=-1;
                    for(int j=0;j<frame.AnchorCount;j++)if(frame.Anchors[j].Key==key){anchor=j;break;}
                    if(anchor<0)
                    {
                        Assert.That(frame.AnchorCount,Is.LessThan(WorldLiquidMotionFrame.MaxAnchors));
                        Assert.That(_owner.TryCopyChunk(key,_words,out var header),Is.True);anchor=frame.AnchorCount++;
                        frame.Anchors[anchor]=new WorldLiquidMotionAnchor(key,header.Generation,header.Revision,1,header.IsResident);
                    }
                    frame.Rows[frame.RowCount++]=new WorldLiquidMotionSample(row.Cell,row.MaterialId,row.PreviousQuantity,row.CurrentQuantity,row.PreviousOffset,row.PreviousVelocity,row.CurrentOffset,row.CurrentVelocity,anchor);
                }
                if(frame.RowCount==0)return frame;
                Assert.That(WorldLiquidMotionCodec.TryEncode(frame,_motionWords,out int words),Is.True);
                var decoded=new WorldLiquidMotionFrame{WorldEpoch=7,ConnectionEpoch=3,Sequence=frame.Sequence,MaterialTick=frame.MaterialTick};
                Assert.That(WorldLiquidMotionCodec.TryDecode(_motionWords,words,decoded),Is.True);return decoded;
            }
            internal List<RowEvidence> ObserveBeforeStage(WorldLiquidMotionFrame frame)
            {
                var rows=new List<RowEvidence>();
                for(int i=0;i<frame.RowCount;i++)
                {
                    var row=frame.Rows[i];bool inPrior=Find(Prior,row.Cell,row.MaterialId)>=0;
                    if(row.PreviousQuantity==0 || inPrior)continue;
                    var anchor=frame.Anchors[row.AnchorIndex];bool factFound=Store.TryGetPublishedChunkFact(anchor.Key,out var fact,out bool interested);
                    bool qtyFound=Store.TryGetQuantity(WorldMaterialDomain.Liquid,row.Cell,row.MaterialId,out uint quantity);
                    bool sampleFound=Reader.TryGetPresentationSample(row.Cell,(MaterialId)row.MaterialId,false,out var sample);
                    Seen.TryGetValue(row.Cell,out var seen);
                    rows.Add(new RowEvidence{Tick=Tick,CellX=row.Cell.x,CellY=row.Cell.y,CellZ=row.Cell.z,Material=row.MaterialId,InPriorMotion=inPrior,
                        StoreFactExists=factFound,StoreResident=fact.IsResident,StoreInterested=interested,StoreQuantityFound=qtyFound,StoreQuantity=quantity,
                        StoreEpoch=fact.WorldEpoch,StoreGeneration=fact.Generation,StoreRevision=fact.Revision,ExpectedGeneration=anchor.Generation,ExpectedRevision=anchor.Revision,
                        PreviousQuantity=row.PreviousQuantity,CurrentQuantity=row.CurrentQuantity,PreviousOffset=row.PreviousOffset,PreviousVelocity=row.PreviousVelocity,CurrentOffset=row.CurrentOffset,CurrentVelocity=row.CurrentVelocity,
                        DisplayFound=sampleFound,DisplayQuantity=sample.Quantity,DisplayOffset=sample.OffsetCells,DisplayVelocity=sample.VelocityWorld,
                        IndependentOffsetDifferenceSquared=(row.PreviousOffset-sample.OffsetCells).sqrMagnitude,IndependentVelocityDifferenceSquared=(row.PreviousVelocity-sample.VelocityWorld).sqrMagnitude,
                        LastAuthorityAppearanceTick=seen.Tick,LastAuthorityQuantity=(int)seen.Row.CurrentQuantity,LastAuthorityOffset=seen.Row.CurrentOffset,LastAuthorityVelocity=seen.Row.CurrentVelocity});
                }
                return rows;
            }
            internal void ObserveProof(WorldLiquidMotionFrame frame,List<RowEvidence> rows)
            {
                var proofs=(Array)typeof(WorldReplicaLiquidReadModel).GetField("_candidateKnownPrevious",Fields).GetValue(Reader);
                foreach(var row in rows)
                {int index=Find(frame,new Vector3Int(row.CellX,row.CellY,row.CellZ),(byte)row.Material);object proof=proofs.GetValue(index);row.KnownProofAfterStage=(bool)proof.GetType().GetField("Valid",Fields).GetValue(proof);}
            }
            internal AnchorEvidence[] ObserveAllAnchorFacts(WorldLiquidMotionFrame frame)
            {
                var facts=new List<AnchorEvidence>();
                for(int i=0;i<frame.AnchorCount;i++)AddAnchorFact(facts,"candidate",-1,frame.Anchors[i]);
                for(int window=0;window<Reader.RetainedMotionWindowCount;window++)
                    for(int i=0;i<Reader.GetRetainedMotionAnchorCount(window);i++)
                    {Assert.That(Reader.TryGetRetainedMotionAnchor(window,i,out var anchor),Is.True);AddAnchorFact(facts,"retained",window,anchor);}
                return facts.ToArray();
            }
            private void AddAnchorFact(List<AnchorEvidence> facts,string kind,int window,WorldLiquidMotionAnchor anchor)
            {
                bool hasStore=Store.TryGetPublishedChunkFact(anchor.Key,out var old,out bool interested);
                bool hasAuthority=_owner.TryGetCurrentHeader(anchor.Key,out var current);
                if(kind=="candidate")
                {
                    Assert.That(hasAuthority,Is.True);
                    Assert.That(anchor.Generation,Is.EqualTo(current.Generation),"候选Generation必须真实来自本轮Authority Q，不能以损坏M触发代次安全豁免。");
                }
                facts.Add(new AnchorEvidence{Kind=kind,RetainedWindow=window,ChunkX=anchor.Key.X,ChunkY=anchor.Key.Y,ChunkZ=anchor.Key.Z,
                    AnchorGeneration=anchor.Generation,AnchorRevision=anchor.Revision,SubscriptionEpoch=anchor.SubscriptionEpoch,
                    StoreExists=hasStore,StoreResident=old.IsResident,StoreInterested=interested,StoreGeneration=old.Generation,StoreRevision=old.Revision,
                    AuthorityExists=hasAuthority,AuthorityResident=current.IsResident,AuthorityGeneration=current.Generation,AuthorityRevision=current.Revision});
            }
            internal void InstallQuantities(TickEvidence evidence,ulong expectedOld)
            {
                for(int i=0;i<_owner.ChunkCount;i++)
                {
                    var key=_owner.GetChunkHeader(i).Key;Assert.That(_owner.TryCopyChunk(key,_words,out var header),Is.True);
                    Assert.That(Store.TryInstall(header,_words,header.WordCount),Is.True);evidence.InstalledChunks++;
                    var state=Snapshot(Reader,"after-chunk-"+key.X+","+key.Y+","+key.Z);
                    if(evidence.FirstChangedInstall==null && expectedOld!=0 && state.Window!=expectedOld)evidence.FirstChangedInstall=state;
                }
                Assert.That(Store.TotalQuantity(WorldMaterialDomain.Liquid)+Store.PendingQuantity(WorldMaterialDomain.Liquid),Is.EqualTo(Accepted));
            }
            public void Dispose(){Object.DestroyImmediate(Host);Object.DestroyImmediate(Floor);Object.DestroyImmediate(_liquidProfile);Object.DestroyImmediate(_worldProfile);}
        }
        private static int Find(WorldLiquidMotionFrame frame,Vector3Int cell,byte material)
        {if(frame!=null)for(int i=0;i<frame.RowCount;i++)if(frame.Rows[i].Cell==cell && frame.Rows[i].MaterialId==material)return i;return -1;}
        private static bool CompatibleAnchors(WorldLiquidMotionFrame prior,WorldLiquidMotionFrame next)
        {
            if(prior==null)return false;
            for(int i=0;i<prior.AnchorCount;i++)for(int j=0;j<next.AnchorCount;j++)if(prior.Anchors[i].Key==next.Anchors[j].Key
                && (prior.Anchors[i].Generation!=next.Anchors[j].Generation || prior.Anchors[i].SubscriptionEpoch!=next.Anchors[j].SubscriptionEpoch))return false;
            return true;
        }
        private static bool CompatibleRetainedAnchors(AnchorEvidence[] facts)
        {
            foreach(var old in facts)if(old.Kind=="retained")foreach(var next in facts)if(next.Kind=="candidate"
                && old.ChunkX==next.ChunkX && old.ChunkY==next.ChunkY && old.ChunkZ==next.ChunkZ
                && (old.AnchorGeneration!=next.AnchorGeneration || old.SubscriptionEpoch!=next.SubscriptionEpoch))return false;
            return true;
        }
        private static bool VerifyGenerationSafety(TickEvidence evidence)
        {
            // 真实retained锚已换代时，旧壳必须撤回；候选新Key仍暂见旧Store代次时，
            // 则还要核对实际Generation拒因的Key和两侧代次，不能只见一个Reason12就豁免其它断链。
            if(!evidence.GenerationTransitionExpected || evidence.AfterInstall.Window!=0 || !evidence.AfterActivation.Snapped
                || !evidence.AfterActivation.AlphaAvailable || Math.Abs(evidence.AfterActivation.Alpha-1)>.00001f)return false;
            if(evidence.RetainedGenerationChanges)return true;
            var failure=evidence.AfterInstall.Hold;
            if(failure.Reason!=(byte)WorldLiquidMotionHoldFailure.Generation)return false;
            foreach(var fact in evidence.PreStageAnchors)if(fact.Kind=="candidate" && fact.StoreExists && fact.AuthorityExists
                && fact.ChunkX==failure.ChunkX && fact.ChunkY==failure.ChunkY && fact.ChunkZ==failure.ChunkZ
                && fact.AuthorityGeneration==fact.AnchorGeneration && fact.AnchorGeneration==failure.ExpectedGeneration
                && fact.StoreGeneration==failure.ActualGeneration && fact.StoreGeneration!=fact.AuthorityGeneration)return true;
            return false;
        }
        private static State Snapshot(WorldReplicaLiquidReadModel source,string point)
        {
            ulong window=source.PresentationWindowId;bool alphaAvailable=source.TryGetPresentationAlpha(window,out float alpha);
            return new State{Point=point,Window=window,Snapped=source.PresentationSnapped,Holding=source.PresentationHolding,AlphaAvailable=alphaAvailable,Alpha=alpha,
                Buffered=source.BufferedPresentationWindows,Invalidations=source.PresentationInvalidationRevision,Hold=source.LastMotionHoldDiagnostic};
        }

        [Test]
        public void ProductionPulseFallDoesNotSnapTheWholeWindowWhenKnownGroundWaterReentersMotion()
        {
            string directory=Path.Combine("TestResults/LiquidNativeStaticBridge",DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+"-"+Guid.NewGuid().ToString("N"));
            var report=new Report();var ticks=new List<TickEvidence>();
            try
            {
                using var f=new Fixture();
                for(int tick=1;tick<=60;tick++)
                {
                    f.AdvanceSolver();report.InjectedBatches=f.Injections;report.AcceptedQuantity=f.Accepted;
                    var next=f.DecodeMotion();double arrival=next.SampleTime-.025;
                    f.Reader.AdvancePresentation(arrival,false);
                    var evidence=new TickEvidence{Tick=tick,MaterialTick=next.MaterialTick,Sequence=next.Sequence,SampleTime=next.SampleTime,ArrivalClock=arrival,MotionRows=next.RowCount,SourceMotionRows=f.SourceMotionRows,SourceAnchorCount=f.SourceAnchorCount,
                        MotionBudgetSkipped=f.MotionBudgetSkipped,BudgetSkipReason=f.MotionBudgetSkipped?"Whole Source exceeds fixed wire 128 rows / 32 anchors; no partial group sent":null,AuthorityQuantityCells=f.QuantityCellCount,
                        AuthorityQuantity=f.Liquid.TotalQuantity,AuthorityPending=f.Liquid.PendingQuantity,AcceptedQuantity=f.Accepted,BeforeStage=Snapshot(f.Reader,"before-stage")};ticks.Add(evidence);
                    var rows=f.ObserveBeforeStage(next);evidence.ReenteredRows=rows.ToArray();report.ReenteredRowObservations+=rows.Count;
                    evidence.PreStageAnchors=f.ObserveAllAnchorFacts(next);
                    evidence.ConsecutiveAuthorityWindow=f.Prior!=null && next.MaterialTick==f.Prior.MaterialTick+1;
                    evidence.AnchorsCompatible=CompatibleAnchors(f.Prior,next) && CompatibleRetainedAnchors(evidence.PreStageAnchors);
                    foreach(var fact in evidence.PreStageAnchors)
                    {
                        if(!fact.AuthorityExists)continue;
                        bool retainedChanged=fact.Kind=="retained" && fact.AuthorityGeneration!=fact.AnchorGeneration;
                        evidence.RetainedGenerationChanges|=retainedChanged;
                        evidence.GenerationTransitionExpected|=retainedChanged || fact.StoreExists && fact.StoreGeneration!=fact.AuthorityGeneration;
                    }
                    bool eligibleUnfinishedWindow=!evidence.MotionBudgetSkipped && next.RowCount>0 && evidence.ConsecutiveAuthorityWindow && evidence.BeforeStage.Window!=0 && !evidence.BeforeStage.Snapped;
                    evidence.ContinuousExpected=eligibleUnfinishedWindow && evidence.AnchorsCompatible && !evidence.GenerationTransitionExpected;
                    if(eligibleUnfinishedWindow && evidence.GenerationTransitionExpected)report.GenerationTransitionWindows++;
                    if(next.RowCount>0){f.Reader.StagePresentationCandidate(next,arrival);f.ObserveProof(next,rows);}
                    evidence.AfterStage=Snapshot(f.Reader,"after-stage");f.InstallQuantities(evidence,evidence.BeforeStage.Window);evidence.AfterInstall=Snapshot(f.Reader,"after-all-quantity");
                    if(next.RowCount>0)
                    {
                        evidence.CurrentWitnessAccepted=f.Reader.TryPublish(next,arrival);Assert.That(evidence.CurrentWitnessAccepted,Is.True,"完整本窗Current见证仍必须通过。");
                        evidence.AfterPublish=Snapshot(f.Reader,"after-publish");f.Reader.CancelPresentationCandidate();
                        f.Reader.AdvancePresentation(next.SampleTime+.025,false);evidence.AfterActivation=Snapshot(f.Reader,"after-activation");
                        evidence.LostOldCurve=evidence.BeforeStage.Window!=0 && evidence.AfterInstall.Window!=evidence.BeforeStage.Window;
                        evidence.ActivatedSnapped=evidence.AfterActivation.Snapped;
                        evidence.GenerationSafetyVerified=eligibleUnfinishedWindow && VerifyGenerationSafety(evidence);
                        if(evidence.GenerationSafetyVerified)report.VerifiedGenerationSafetySnaps++;
                        if(evidence.ContinuousExpected)report.ExpectedContinuousWindows++;
                        // 即便本窗存在真实代次变化，若实际是另一个未被证明的断链，也继续计unexpected。
                        // 不能通过减小期望分母把错误的pose/qty拒绝隐藏在Generation标签下面。
                        if(eligibleUnfinishedWindow && (evidence.LostOldCurve || evidence.ActivatedSnapped) && !evidence.GenerationSafetyVerified)
                        {
                            report.UnexpectedWholeWindowSnaps++;if(report.FirstUnexpectedWholeWindowSnapTick==0)report.FirstUnexpectedWholeWindowSnapTick=tick;
                        }
                        for(int i=0;i<next.RowCount;i++)f.Seen[next.Rows[i].Cell]=new SeenRow(tick,next.Rows[i]);f.Prior=next;
                    }
                    else
                    {
                        if(evidence.MotionBudgetSkipped)report.BudgetSkippedWindows++;
                        // 不以更早的发送窗冒充本轮完整M；下一次恢复明确走正常MaterialTick断档规则。
                        f.Prior=null;f.Reader.CancelPresentationCandidate();f.Reader.AdvancePresentation(next.SampleTime+.025,false);
                        evidence.AfterActivation=Snapshot(f.Reader,evidence.MotionBudgetSkipped?"budget-skipped-no-motion":"no-motion-rows");
                    }
                    evidence.ReplicaQuantity=f.Store.TotalQuantity(WorldMaterialDomain.Liquid);evidence.ReplicaPending=f.Store.PendingQuantity(WorldMaterialDomain.Liquid);
                }
                report.InjectedBatches=f.Injections;report.AcceptedQuantity=f.Accepted;
                Assert.That(report.InjectedBatches,Is.EqualTo(10));Assert.That(report.AcceptedQuantity,Is.EqualTo(5120UL));
                Assert.That(report.ReenteredRowObservations,Is.GreaterThan(0),"必须真实观测到Motion子集缺行后再次进入，不能空场假通过。");
                Assert.That(report.ExpectedContinuousWindows,Is.GreaterThan(5));
                Assert.That(report.GenerationTransitionWindows,Is.GreaterThan(0),"生产脉冲必须保留真实Chunk换代控制，不能删去它来凑连续性通过。");
                Assert.That(report.VerifiedGenerationSafetySnaps,Is.GreaterThan(0),"真实Generation冲突应撤回旧壳并安全端点显示，而非放宽代次门继续插值。");
                Assert.That(report.UnexpectedWholeWindowSnaps,Is.Zero,"真实旧量和完整先到Motion不应让其它已验证旧曲线整窗snap；详见逐行proof证据。");
            }
            catch(Exception error){report.Error=error.ToString();throw;}
            finally
            {
                report.Ticks=ticks.ToArray();Directory.CreateDirectory(directory);string path=Path.Combine(directory,"native-static-bridge.json");File.WriteAllText(path,JsonUtility.ToJson(report,true));
                TestContext.WriteLine("Native static bridge evidence: "+path+"; expected="+report.ExpectedContinuousWindows+"; unexpectedWholeSnap="+report.UnexpectedWholeWindowSnaps);
            }
        }
    }
}
