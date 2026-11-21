using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Game.Character;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Networking;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 从正式 P7 的 CPU 材料装配持续注入三相材料，并让两个观察者通过正式分页、Protobuf、
    /// 原子 Installer 和 ACK 追赶 DS。固定 30 Hz、双向各一帧延迟，不建立 KCP 或启动图形帧。
    /// 每步计时只包括对应生产调用；版本滞后统计及最终逐格核对另行执行。
    /// </summary>
    public sealed class CellularReplicationScaleDiagnostics
    {
        private const ulong Epoch=53;
        private const int Peers=2, Grid=14, GameplaySteps=90, DrainLimit=5400;
        private static readonly MaterialId[] Materials={MaterialId.Water,MaterialId.Sand,MaterialId.ToxicVapor};
        private static readonly double Milliseconds=1000d/Stopwatch.Frequency;

        [Serializable] private sealed class Sample
        {
            public int gameplayTick,activeCells,occupiedCells,processedCells,changedChunks,capturedChunks,cells,pending,spans,latestResidentPagesAwaitingAck;
            public string captureMode;
            public double simulationMs,captureMs,sourceMs,commitAndInterestMs;
        }
        // 以客户端实际安装而非 DS 收到 ACK 为时刻。区分从未见到材料、显示旧版、玩家脚边三个维度。
        [Serializable] private sealed class DisplaySample
        {
            public int frame,missingCurrent,neverInstalled,oldestMissingFrames,oldestDisplayedStaleFrames,oldestNearMissingFrames;
        }
        [Serializable] private sealed class ColdEvidence
        {
            public int distinctCells,cellRows,allocatedRows,rowLimit,checkpointRows,initialDrainFrames,reconnectDrainFrames,versionMismatches,quantityMismatches;
            public long checkpointGmu,downBytes,ackBytes;
            public double coldCaptureMs,coldSourceMs,deltaCaptureMs,deltaSourceMs;
            public string failure;
        }
        [Serializable] private sealed class Report
        {
            public string map,baseFingerprint,gameplayHash,failure;
            public int peers=Peers,gameplayHz=30,materialHz=10,linkDelayFramesEachWay=1,fixtureWidth=Grid;
            public int capacity,chunkSize,inspectionBudget,captureCells,capturePending,directoryLimit,historyRecords;
            public float cellSize,referenceSize;
            public int radiusXZ,radiusY;
            public int full,incremental,quiet,fullAfterIncrementalFallback,simulatedTicks,drainFrames;
            public int maxChangedChunks,maximumResidentPagesAwaitingAck,pendingAtMotionStop,pendingAfterDrain,verifiedCellCount,quantityMismatches,versionMismatches;
            public int[] writeAttempts=new int[3],acceptedWrites=new int[3];
            public long[] injectedGmu=new long[3],authorityInterestGmu=new long[3],replicaInterestGmu=new long[3];
            public long encodedDownBytes,encodedAckBytes,downPages,controlPages,installAcks,materialCommits;
            public long baselineTransfers,deltaTransfers,tombstoneTransfers,repeatedDataPages,completedSupersededTransfers;
            public long canonicalTransferWords,physicalTransferWords;
            public long motionDownBytes,motionControlPages,motionDataPages,byteBudgetRejections,pageQuotaRounds;
            public int firstMaterialCommitFrame=-1,oldestMissingWhileMovingFrames,oldestDisplayedStaleWhileMovingFrames,oldestNearMissingWhileMovingFrames;
            public int firstFullyVisibleDrainFrame=-1;
            public int[] firstVisibleFrame={-1,-1},firstNearVisibleFrame={-1,-1};
            public List<DisplaySample> displaySamples=new List<DisplaySample>();
            public double captureTotalMs,captureMaxMs,sourceTotalMs,serverSendAndAckMs,replicaReceiveMs;
            public bool captureFault,simulationFault,drained;
            public List<Sample> samples=new List<Sample>();
        }

        // 在真实 Source 外观察协调器选择了哪一个分支。三种接口原样转发，使用同一个冻结 buffer。
        private sealed class MeasuredSource : IWorldReplicationSource,IWorldUnchangedReplicationSource,IWorldCellularIncrementalSource
        {
            private readonly IWorldReplicationSource _source;
            internal string Mode;
            internal long Elapsed;
            internal int Keys;
            internal bool Fallback;
            internal MeasuredSource(IWorldReplicationSource source){_source=source;}
            internal void Reset(){Mode="";Elapsed=0;Keys=0;Fallback=false;}
            public bool TryAdvanceUnchangedCapture(WorldReplicaBuffer b)
            {
                long start=Stopwatch.GetTimestamp();
                bool ok=((IWorldUnchangedReplicationSource)_source).TryAdvanceUnchangedCapture(b);
                Elapsed+=Stopwatch.GetTimestamp()-start;if(ok)Mode="quiet";return ok;
            }
            public bool TryCaptureChangedChunks(WorldReplicaBuffer b,out int count,out WorldCaptureFailure failure)
            {
                long start=Stopwatch.GetTimestamp();
                bool ok=((IWorldCellularIncrementalSource)_source).TryCaptureChangedChunks(b,out count,out failure);
                Elapsed+=Stopwatch.GetTimestamp()-start;Keys=count;Mode="incremental";Fallback=!ok && failure==WorldCaptureFailure.None;return ok;
            }
            public bool TryCapture(WorldReplicaBuffer b,out WorldCaptureFailure failure)
            {
                long start=Stopwatch.GetTimestamp();bool ok=_source.TryCapture(b,out failure);
                Elapsed+=Stopwatch.GetTimestamp()-start;Mode="full";return ok;
            }
        }

        // 延迟的是实际编码字节，避免复用 Codec 返回的借用消息导致包体串写。32 槽远高于两帧在途上界。
        private sealed class Link
        {
            private readonly byte[][] _bytes=new byte[32][];
            private readonly int[] _length=new int[32],_due=new int[32];
            private int _head,_count;
            private int Tail=>(_head+_count)%_bytes.Length;
            internal Link(){for(int i=0;i<_bytes.Length;i++)_bytes[i]=new byte[WorldPageCodec.MaxPayloadBytes];}
            internal ArraySegment<byte> Writable()
            {if(_count==_bytes.Length)throw new InvalidOperationException("诊断链路超出固定在途容量。");return new ArraySegment<byte>(_bytes[Tail]);}
            internal void Queue(int bytes,int due){int tail=Tail;_length[tail]=bytes;_due[tail]=due;_count++;}
            internal bool Read(int tick,out ArraySegment<byte> bytes)
            {
                bytes=default;if(_count==0 || _due[_head]>tick)return false;
                bytes=new ArraySegment<byte>(_bytes[_head],0,_length[_head]);_head=(_head+1)%_bytes.Length;_count--;return true;
            }
            internal bool Empty=>_count==0;
        }

        private sealed class Side
        {
            internal GameObject Root;
            internal OnlineMaterialWorldBinding Binding;
            internal ElementWorldRuntime World;
            internal WorldReplicationCoordinator Network;
            internal readonly Link Down=new Link(),Up=new Link();
            internal readonly WorldPageCodec Codec=new WorldPageCodec();
            internal readonly Dictionary<WorldChunkKey,WorldReplicationChunkStamp> Versions=new Dictionary<WorldChunkKey,WorldReplicationChunkStamp>();
            internal readonly Dictionary<WorldChunkKey,SentTransfer> Sent=new Dictionary<WorldChunkKey,SentTransfer>();
            internal readonly Dictionary<WorldChunkKey,Visibility> Visible=new Dictionary<WorldChunkKey,Visibility>();
            internal Vector3 Observer;
            internal Bounds Interest;
            internal uint ConnectionEpoch=1;
        }

        private struct SentTransfer
        {
            internal ulong Generation,Revision,Snapshot,Subscription,Base;
            internal uint LastPage,Kind;
        }

        private sealed class Visibility
        {
            // Installed 表示第几个 DS 版本已经完整到达客户端，允许 Peer 从初次 Baseline 一次跨过多个中间 Tick。
            internal readonly List<(int Frame,WorldChunkPayloadHeader Header)> Changes=new List<(int,WorldChunkPayloadHeader)>();
            internal int Installed;
            internal bool Near;
        }

        private Report _report;
        private readonly List<Side> _allocated=new List<Side>();
        private readonly Side[] _clients=new Side[Peers];
        private Side _server;
        private WorldReplicationCommitOwner _commits;
        private MeasuredSource _source;
        private WorldReplicaBuffer _buffer;

        [SetUp] public void ResetEvidence(){_report=new Report();_allocated.Clear();}

        [Test]
        public void MovingWaterSandAndGasRecordAuthorityToReplicaPressure()
        {
            string label=Environment.GetEnvironmentVariable("NORN_ELEMENT_NETWORK_SCALE_LABEL")??"before";
            if(label.Length<1 || label.Length>32 || System.Array.Exists(label.ToCharArray(),c=>!char.IsLetterOrDigit(c) && c!='-' && c!='_'))
                throw new ArgumentException("诊断文件标签仅接受字母、数字、短横线和下划线。");
            try
            {
                SetUpWorlds();
                for(int tick=1;tick<=GameplaySteps;tick++)
                {
                    if(tick==1 || tick==7 || tick==13)Inject((tick-1)/6);
                    long start=Stopwatch.GetTimestamp();
                    _server.Binding.AuthoritySimulation.StepAuthority(1f/30f,(uint)tick);
                    double simMs=(Stopwatch.GetTimestamp()-start)*Milliseconds;
                    _report.simulationFault=_server.Binding.Faulted;
                    if(_report.simulationFault)throw new InvalidOperationException("真实 Authority Simulation 已故障。");
                    if(tick%3==0)Capture(tick,simMs);
                    Pump(tick);
                }
                _report.simulatedTicks=(int)_server.World.WorldTick;
                _report.pendingAtMotionStop=CountBacklog();
                // 冻结测量时刻的 DS 正文继续跑发送/安装，不再给 Solver 新 Tick，才能比较同一时刻两端的真实数量。
                for(int i=1;i<=DrainLimit;i++)
                {
                    Pump(GameplaySteps+i);
                    if(i%15!=0)continue;
                    _report.drainFrames=i;
                    if(CountBacklog()==0 && LinksEmpty()){_report.drained=true;break;}
                }
                _report.pendingAfterDrain=CountBacklog();
                VerifyQuantities();
                Assert.That(_report.maxChangedChunks,Is.GreaterThanOrEqualTo(48),"负载未同时改变足够多的真实分块。");
                for(int i=0;i<Materials.Length;i++)Assert.That(_report.acceptedWrites[i],Is.GreaterThan(0),Materials[i].ToString());
                Assert.That(_report.installAcks,Is.GreaterThan(0));
                for(int i=0;i<Peers;i++)
                    Assert.That(_report.firstNearVisibleFrame[i],Is.InRange(_report.firstMaterialCommitFrame,_report.firstMaterialCommitFrame+8),
                        "两个观察者都应在首笔材料提交后 8 个 Gameplay 帧内安装身边第一块，即使远处仍有数百个 Enter。");
                Assert.That(_report.drained,Is.True,"限时内没有安装并确认最终冻结正文。");
                Assert.That(_report.versionMismatches,Is.Zero);
                Assert.That(_report.quantityMismatches,Is.Zero);
            }
            catch(Exception error){_report.failure=error.ToString();throw;}
            finally
            {
                string folder=Path.GetFullPath(Path.Combine("Temp","ElementWorldScale"));
                Directory.CreateDirectory(folder);string path=Path.Combine(folder,"network-"+label+".json");
                File.WriteAllText(path,JsonUtility.ToJson(_report,true));TestContext.WriteLine(path);
                for(int i=_allocated.Count-1;i>=0;i--)if(_allocated[i].Root!=null)Object.DestroyImmediate(_allocated[i].Root);
            }
        }

        [Test]
        public void PagedColdCaptureKeepsMoreThan65536DistinctCellsAcrossCheckpointAndReconnect()
        {
            const int sites=65537;
            var evidence=new ColdEvidence{distinctCells=sites};
            ElementCellSimulation simulation=null;
            Vector3Int Cell(int i)=>new Vector3Int((i&63)-32,236+(i>>12),((i>>6)&63)-32);
            var publish=typeof(ElementWorldRuntime).GetMethod("PublishCellularChanges",BindingFlags.Instance|BindingFlags.NonPublic);
            try
            {
                SetUpWorlds(()=>
                {
                    simulation=(ElementCellSimulation)typeof(ElementWorldRuntime).GetProperty("CellularSimulation",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(_server.World);
                    // 建立冻结时已经存在的大世界；每个点都走正式库存事务。首个 full Capture 之前没有构建网络索引。
                    for(int i=0;i<sites;i++)if(!simulation.TryDeposit(Cell(i),MaterialId.Water,1))Assert.Fail("真实细格投放失败："+i);
                    Assert.That(simulation.TryDeposit(Cell(0),MaterialId.Steam,2),Is.True);
                    Assert.That(simulation.TryDeposit(Cell(0),MaterialId.ToxicVapor,3),Is.True);
                    publish.Invoke(_server.World,null);
                });
                evidence.cellRows=_buffer.Header.CellCount;evidence.allocatedRows=_buffer.AllocatedCellRows;evidence.rowLimit=_buffer.MaximumCellRows;
                evidence.coldCaptureMs=_report.captureTotalMs;evidence.coldSourceMs=_report.sourceTotalMs;
                Assert.That(_buffer.Header.CellCount,Is.EqualTo(sites+2));
                Assert.That(_buffer.AllocatedCellRows,Is.EqualTo(_buffer.Cells.Length+WorldReplicaBuffer.CapturePageRows));
                Assert.That(_server.Network.TryCaptureCheckpoint("p7.paged",out var snapshot),Is.True);
                int durableRows=0;long durableGmu=0;
                foreach(var chunk in snapshot.Chunks)
                {
                    if(chunk.Domain!=(uint)WorldMaterialDomain.Cellular || !chunk.IsResident)continue;
                    int n=(int)chunk.Words[1];durableRows+=n;
                    int offset=WorldChunkPayloadCodec.CellOffset(chunk.Words);
                    for(int row=0;row<n;row++,offset+=WorldChunkPayloadCodec.CellWords)durableGmu+=chunk.Words[offset+6];
                }
                Assert.That(durableRows,Is.EqualTo(sites+2));Assert.That(durableGmu,Is.EqualTo(sites+5));
                evidence.checkpointRows=durableRows;evidence.checkpointGmu=durableGmu;
                // 故意给一份上限恰为 65536 的私有候选，跨页时明确拒绝，旧 Header 和已发布的首行保持原值。
                var small=new WorldReplicaBuffer(32,1,_buffer.InspectionBudget,1,1,65536,1);
                small.Header=new WorldReplicaHeader(7,1,0,0);small.Cells[0]=new WorldMaterialCellSample(WorldMaterialDomain.Cellular,WorldMaterialStorage.Resident,Cell(0),8,99);
                Assert.That(_server.Binding.ReplicationSource.TryCapture(small,out var failure),Is.False);Assert.That(failure,Is.EqualTo(WorldCaptureFailure.CellCapacity));
                Assert.That(small.Header.WorldTick,Is.EqualTo(7));Assert.That(small.Cells[0].Quantity,Is.EqualTo(99));Assert.That(small.AllocatedCellRows,Is.EqualTo(32));
                int frame=0;
                void Drain()
                {
                    int limit=frame+DrainLimit;
                    do{Pump(++frame);if(frame%15==0 && CountBacklog()==0 && LinksEmpty())return;}while(frame<limit);
                    Assert.Fail("在正式每连接字节与页数额度内没有安装所有大世界块。");
                }
                Drain();evidence.initialDrainFrames=frame;VerifyQuantities();Assert.That(_report.quantityMismatches+_report.versionMismatches,Is.Zero);
                var previous=_clients[1];_server.Network.RemovePeer(1);
                var catalog=Resources.Load<OnlineDungeonCatalog>(OnlineDungeonCatalog.ResourcePath);Assert.That(catalog.TryGet("p7",out var dungeon),Is.True);
                var profile=Resources.Load<OnlineWorldReplicationProfile>("Online/WorldReplicationProfile");
                var replacement=_clients[1]=CreateSide(dungeon.WorldPrefab,profile,_server.Network.GameplayHash,false);
                replacement.Observer=previous.Observer;replacement.Interest=previous.Interest;replacement.ConnectionEpoch=2;
                Assert.That(replacement.Network.SetClientConnectionEpoch(2),Is.True);Assert.That(_server.Network.BindPeer(1,101,2),Is.True);
                Assert.That(_server.Network.UpdatePeerPosition(1,101,2,replacement.Observer),Is.True);
                Assert.That(simulation.TryRemove(Cell(sites-1),MaterialId.Water,1),Is.True);publish.Invoke(_server.World,null);
                _source.Reset();long began=Stopwatch.GetTimestamp();bool captured=_server.Network.Capture(_source,_buffer);
                evidence.deltaCaptureMs=(Stopwatch.GetTimestamp()-began)*Milliseconds;evidence.deltaSourceMs=_source.Elapsed*Milliseconds;
                Assert.That(captured,Is.True);Assert.That(_source.Mode,Is.EqualTo("incremental"));
                Drain();evidence.reconnectDrainFrames=frame-evidence.initialDrainFrames;VerifyQuantities();Assert.That(_report.quantityMismatches+_report.versionMismatches,Is.Zero);
                Assert.That(replacement.World.TryGetQuantity(Cell(0),MaterialId.Steam,out uint steam),Is.True);Assert.That(steam,Is.EqualTo(2));
                replacement.World.TryGetQuantity(Cell(sites-1),MaterialId.Water,out uint cleared);Assert.That(cleared,Is.Zero);
            }
            catch(Exception error){evidence.failure=error.ToString();throw;}
            finally
            {
                evidence.versionMismatches=_report.versionMismatches;evidence.quantityMismatches=_report.quantityMismatches;
                evidence.downBytes=_report.encodedDownBytes;evidence.ackBytes=_report.encodedAckBytes;
                string folder=Path.GetFullPath(Path.Combine("Temp","ElementWorldScale"));Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"network-cold.json"),JsonUtility.ToJson(evidence,true));
                for(int i=_allocated.Count-1;i>=0;i--)if(_allocated[i].Root!=null)Object.DestroyImmediate(_allocated[i].Root);
            }
        }

        private void SetUpWorlds(Action beforeFirstCapture=null)
        {
            var catalog=Resources.Load<OnlineDungeonCatalog>(OnlineDungeonCatalog.ResourcePath);
            var profile=Resources.Load<OnlineWorldReplicationProfile>("Online/WorldReplicationProfile");
            var combat=Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(catalog,Is.Not.Null);Assert.That(profile,Is.Not.Null);Assert.That(combat,Is.Not.Null);
            Assert.That(catalog.TryGet("p7",out var dungeon),Is.True);Assert.That(profile.Validate(out var error),Is.True,error);
            _server=CreateSide(dungeon.WorldPrefab,profile,combat.WireHash,true);
            _buffer=_server.Binding.CaptureBuffer;_source=new MeasuredSource(_server.Binding.ReplicationSource);
            _commits=(WorldReplicationCommitOwner)typeof(WorldReplicationCoordinator).GetField("_owner",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(_server.Network);
            _report.map=_server.World.CellularMapContentId;_report.baseFingerprint=_server.World.CellularMapBaseFingerprint.ToString("X16");
            _report.gameplayHash=combat.WireHash.ToString("X16");_report.capacity=(int)_server.World.CellularCellCapacity;
            _report.chunkSize=_server.World.ChunkSize;_report.cellSize=_server.World.CellSize;
            _report.inspectionBudget=_buffer.InspectionBudget;_report.captureCells=_buffer.Cells.Length;_report.capturePending=_buffer.Pending.Length;
            _report.directoryLimit=profile.CellularWorldChunkLimit;_report.historyRecords=profile.CellularHistoryRecords;
            _report.referenceSize=profile.ReferenceChunkWorldSize;_report.radiusXZ=profile.RadiusXZChunks;_report.radiusY=profile.RadiusYChunks;
            beforeFirstCapture?.Invoke();
            Capture(0,0);
            for(int i=0;i<Peers;i++)
            {
                var side=_clients[i]=CreateSide(dungeon.WorldPrefab,profile,combat.WireHash,false);
                // 高空投放隔离作者地形碰撞；两名观察者相隔一个参考块，在重叠窗口内核验同一批材料。
                side.Observer=_server.World.Origin+new Vector3(1+i*profile.ReferenceChunkWorldSize,61,1);
                Assert.That(_server.Binding.TrySetActor(i,(ulong)(i+1),side.Observer),Is.True);
                Assert.That(_server.Network.BindPeer(i,100+i,1),Is.True);
                Assert.That(side.Network.SetClientConnectionEpoch(1),Is.True);
                Assert.That(_server.Network.UpdatePeerPosition(i,100+i,1,side.Observer),Is.True);
                Assert.That(WorldInterestCoordinator.TryBuildReferenceBounds(side.Observer,_server.World.Origin,
                    profile.ReferenceChunkWorldSize,profile.RadiusXZChunks,profile.RadiusYChunks,out side.Interest),Is.True);
            }
        }

        private Side CreateSide(GameObject prefab,OnlineWorldReplicationProfile profile,ulong hash,bool authority)
        {
            Assert.That(prefab.activeSelf,Is.False);
            var side=new Side{Root=Object.Instantiate(prefab)};_allocated.Add(side);
            side.Binding=side.Root.GetComponentInChildren<OnlineMaterialWorldBinding>(true);
            side.World=side.Root.GetComponentInChildren<ElementWorldRuntime>(true);
            Assert.That(side.Binding.TrySetCellularDirectoryLimit(profile.CellularWorldChunkLimit),Is.True);
            Assert.That(side.Binding.TryConfigure(authority?WorldExecutionMode.Authority:WorldExecutionMode.Replica,Epoch),Is.True);
            if(authority)
            {
                // EditMode 中保持作者 AI/镜头/UI 关闭，显式调用其 Awake 使用的同一个材料初始化方法。
                var initialize=typeof(ElementWorldRuntime).GetMethod("TryInitialize",BindingFlags.Instance|BindingFlags.NonPublic);
                Assert.That((bool)initialize.Invoke(side.World,null),Is.True);
            }
            Assert.That(side.Binding.IsReady && side.Binding.UsesCellularReplication,Is.True);
            side.Binding.SetSuspended(false);
            var capture=side.Binding.CaptureBuffer??_buffer;
            side.Network=new WorldReplicationCoordinator(Epoch,hash,profile.CellularWorldChunkLimit,WorldReplicationCoordinator.DefaultWords,
                capture.Cells.Length,capture.Pending.Length,capture.InspectionBudget,side.Binding.ReplicaInstaller,authority?4:1,
                WorldReplicationCoordinator.ProductionQuantityBytesPerSecond,WorldReplicationCoordinator.ProductionQuantityBurstBytes,
                WorldReplicationCoordinator.ProductionQuantityPagesPerRound,profile.CellularHistoryRecords);
            Assert.That(side.Network.ConfigureInterest(profile.ReferenceChunkWorldSize,profile.RadiusXZChunks,profile.RadiusYChunks),Is.True);
            return side;
        }

        private void Inject(int wave)
        {
            var world=_server.World;float edge=world.CellSize*world.ChunkSize;
            ushort amount=checked((ushort)(world.CellularCellCapacity*8));
            for(int z=0;z<Grid;z++)for(int x=0;x<Grid;x++)
            {
                int m=(x+z*Grid+wave)%Materials.Length;
                var position=_clients[0].Observer+new Vector3((x-(Grid-1)*.5f)*edge,wave*world.CellSize,(z-(Grid-1)*.5f)*edge);
                var request=new ElementWriteRequest(position,Materials[m],amount,world.CellSize*2.25f,false);
                _report.writeAttempts[m]++;
                if(!world.TryEnqueueWrite(in request))continue;
                _report.acceptedWrites[m]++;_report.injectedGmu[m]+=amount;
            }
        }

        private void Capture(int tick,double simulationMs)
        {
            var world=_server.World;_source.Reset();
            long start=Stopwatch.GetTimestamp();bool captured=_server.Network.Capture(_source,_buffer);
            double elapsed=(Stopwatch.GetTimestamp()-start)*Milliseconds,sourceMs=_source.Elapsed*Milliseconds;
            if(_source.Mode=="full")_report.full++;else if(_source.Mode=="incremental")_report.incremental++;else if(_source.Mode=="quiet")_report.quiet++;
            if(_source.Fallback && _source.Mode=="full")_report.fullAfterIncrementalFallback++;
            _report.captureTotalMs+=elapsed;_report.sourceTotalMs+=sourceMs;_report.captureMaxMs=Math.Max(_report.captureMaxMs,elapsed);
            _report.captureFault=!captured || _server.Network.Faulted;
            int changed=captured?_commits.LastChangeCount:0;
            _report.maxChangedChunks=Math.Max(_report.maxChangedChunks,changed);_report.materialCommits=(long)_commits.CommitSequence;
            int backlog=tick>0 && tick%15==0?CountBacklog():-1;
            _report.maximumResidentPagesAwaitingAck=Math.Max(_report.maximumResidentPagesAwaitingAck,backlog);
            var h=_buffer.Header;
            _report.samples.Add(new Sample{gameplayTick=tick,captureMode=_source.Mode,activeCells=world.CellularActiveCount,
                occupiedCells=world.CellularOccupiedCount,processedCells=world.CellularCellsProcessedLastTick,changedChunks=changed,capturedChunks=_source.Keys,
                cells=_source.Mode=="quiet"?0:h.CellCount,pending=world.PendingWriteCount,spans=_source.Mode=="quiet"?0:h.SpanCount,
                latestResidentPagesAwaitingAck=backlog,simulationMs=simulationMs,captureMs=elapsed,sourceMs=sourceMs,commitAndInterestMs=elapsed-sourceMs});
            if(_report.captureFault)throw new InvalidOperationException("N5 Capture 失败："+_server.Network.Failure);
            if(tick>0)RecordAuthorityForDisplay(tick);
        }

        private void Pump(int tick)
        {
            double now=tick/30d;
            _server.Network.BeginRound(now);
            for(int i=0;i<Peers;i++)
            {
                var side=_clients[i];long start=Stopwatch.GetTimestamp();
                while(side.Up.Read(tick,out var ackBytes))
                {
                    if(!side.Codec.TryDecode(ackBytes,out var ack) || !_server.Network.ReceiveControl(i,100+i,side.ConnectionEpoch,ack,now))
                        throw new InvalidOperationException("权威拒绝真实 Replica 响应。");
                    if(ack.Kind==3)_report.installAcks++;
                }
                if(!_server.Network.UpdatePeerPosition(i,100+i,side.ConnectionEpoch,side.Observer))throw new InvalidOperationException("认证观察者 Interest 更新失败。");
                int sentThisRound=0;
                for(int p=0;p<_server.Network.QuantityPagesPerRound;p++)
                {
                    if(!_server.Network.TryPreparePage(i,now,out var message))break;
                    if(!side.Codec.TryEncode(message,side.Down.Writable(),out int bytes))throw new InvalidOperationException("生产 Protobuf 编码拒绝权威页。");
                    if(!_server.Network.MarkPageSent(i,now,bytes+72)){_report.byteBudgetRejections++;_server.Network.CancelPreparedPage(i);break;}
                    side.Down.Queue(bytes,tick+1);_report.encodedDownBytes+=bytes;_report.downPages++;
                    sentThisRound++;
                    bool control=message.Kind==6 || message.Kind==7;
                    if(tick<=GameplaySteps)
                    { _report.motionDownBytes+=bytes;if(control)_report.motionControlPages++;else _report.motionDataPages++; }
                    if(control)_report.controlPages++;
                    else ObserveTransfer(side,i,message);
                }
                if(sentThisRound==_server.Network.QuantityPagesPerRound)_report.pageQuotaRounds++;
                _report.serverSendAndAckMs+=(Stopwatch.GetTimestamp()-start)*Milliseconds;
                start=Stopwatch.GetTimestamp();
                while(side.Down.Read(tick,out var payload))
                {
                    if(!side.Codec.TryDecode(payload,out var incoming))throw new InvalidOperationException("接收端 Protobuf 解码失败。");
                    if(incoming.Kind==6 || incoming.Kind==7)
                    {
                        if(!side.Network.ReceiveSubscription(incoming))throw new InvalidOperationException("Replica 拒绝订阅控制。");
                        continue;
                    }
                    if(!side.Network.AcceptsPage(incoming) || !side.Network.ReceivePage(incoming,(long)(now*1000),out var reply))
                        throw new InvalidOperationException("Replica 安装失败："+side.Network.LastAssemblyFailure);
                    if(reply==null)continue;
                    if(reply.Kind==3)RecordInstalledForDisplay(side,i,incoming,tick);
                    if(!side.Codec.TryEncode(reply,side.Up.Writable(),out int length))throw new InvalidOperationException("安装响应编码失败。");
                    side.Up.Queue(length,tick+1);_report.encodedAckBytes+=length;
                }
                _report.replicaReceiveMs+=(Stopwatch.GetTimestamp()-start)*Milliseconds;
            }
            if(!_server.Network.CompleteSendRound())throw new InvalidOperationException("结束可靠发送轮失败："+_server.Network.Failure);
            MeasureDisplay(tick);
        }

        private void RecordAuthorityForDisplay(int frame)
        {
            float edge=_server.World.CellSize*_server.World.ChunkSize;
            for(int i=0;i<_commits.LastChangeCount;i++)
            {
                var key=_commits.GetLastChange(i).Key;
                if(key.Domain!=WorldMaterialDomain.Cellular || key.X==0 && key.Y==0 && key.Z==0 ||
                    !_commits.TryGetCurrentHeader(key,out var header))continue;
                foreach(var side in _clients)
                {
                    if(!InView(side,key))continue;
                    if(!side.Visible.TryGetValue(key,out var state))
                    {
                        if(!header.IsResident)continue;
                        var center=_server.World.Origin+(new Vector3(key.X,key.Y,key.Z)+Vector3.one*.5f)*edge;
                        var box=new Bounds(center,Vector3.one*edge);
                        state=new Visibility{Near=box.SqrDistance(side.Observer)<=16f};side.Visible.Add(key,state);
                    }
                    state.Changes.Add((frame,header));
                    if(_report.firstMaterialCommitFrame<0)_report.firstMaterialCommitFrame=frame;
                }
            }
        }

        private void RecordInstalledForDisplay(Side side,int slot,WorldPageMessage message,int frame)
        {
            var key=new WorldChunkKey((WorldMaterialDomain)message.Domain,message.ChunkX,message.ChunkY,message.ChunkZ);
            if(!side.Visible.TryGetValue(key,out var state))return;
            for(int i=state.Changes.Count-1;i>=state.Installed;i--)
            {
                var h=state.Changes[i].Header;
                if(h.Generation!=message.Generation || h.Revision!=message.Revision || h.Checksum!=message.Checksum)continue;
                state.Installed=i+1;
                if(h.IsResident && _report.firstVisibleFrame[slot]<0)_report.firstVisibleFrame[slot]=frame;
                if(h.IsResident && state.Near && _report.firstNearVisibleFrame[slot]<0)_report.firstNearVisibleFrame[slot]=frame;
                break;
            }
        }

        private void MeasureDisplay(int frame)
        {
            var sample=new DisplaySample{frame=frame};
            foreach(var side in _clients)foreach(var state in side.Visible.Values)
            {
                if(state.Installed==state.Changes.Count)continue;
                bool displayed=state.Installed>0 && state.Changes[state.Installed-1].Header.IsResident;
                if(!displayed && !state.Changes[state.Changes.Count-1].Header.IsResident)continue;
                // 采用最早尚未出现在画面中的真实变更时间。持续不变的老块不产生滞后，ACK 往返也不会抬高结果。
                int age=frame-state.Changes[state.Installed].Frame;
                sample.missingCurrent++;if(!displayed)sample.neverInstalled++;
                sample.oldestMissingFrames=Math.Max(sample.oldestMissingFrames,age);
                if(displayed)sample.oldestDisplayedStaleFrames=Math.Max(sample.oldestDisplayedStaleFrames,age);
                if(state.Near)sample.oldestNearMissingFrames=Math.Max(sample.oldestNearMissingFrames,age);
            }
            if(frame<=GameplaySteps)
            {
                _report.oldestMissingWhileMovingFrames=Math.Max(_report.oldestMissingWhileMovingFrames,sample.oldestMissingFrames);
                _report.oldestDisplayedStaleWhileMovingFrames=Math.Max(_report.oldestDisplayedStaleWhileMovingFrames,sample.oldestDisplayedStaleFrames);
                _report.oldestNearMissingWhileMovingFrames=Math.Max(_report.oldestNearMissingWhileMovingFrames,sample.oldestNearMissingFrames);
            }
            else if(sample.missingCurrent==0 && _report.firstFullyVisibleDrainFrame<0)_report.firstFullyVisibleDrainFrame=frame-GameplaySteps;
            if(frame%15==0)_report.displaySamples.Add(sample);
        }

        // 分清真实重发与已经完整冻结、后续又被新 Tick 改写的合法版本；后者仍须原子完成才可 ACK。
        private void ObserveTransfer(Side side,int slot,WorldPageMessage m)
        {
            var key=new WorldChunkKey((WorldMaterialDomain)m.Domain,m.ChunkX,m.ChunkY,m.ChunkZ);
            bool same=side.Sent.TryGetValue(key,out var sent) && sent.Generation==m.Generation && sent.Revision==m.Revision &&
                sent.Snapshot==m.SnapshotId && sent.Subscription==m.SubscriptionEpoch && sent.Base==m.BaseRevision && sent.Kind==m.Kind;
            if(same && m.PageIndex<=sent.LastPage)_report.repeatedDataPages++;
            if(!same)
            {
                if(m.Kind==1)_report.baselineTransfers++;else if(m.Kind==2)_report.deltaTransfers++;else _report.tombstoneTransfers++;
                _report.canonicalTransferWords+=_server.Network.PeerLastFreezeHeader(slot).WordCount;
                _report.physicalTransferWords+=m.TotalWords;
                sent=new SentTransfer{Generation=m.Generation,Revision=m.Revision,Snapshot=m.SnapshotId,
                    Subscription=m.SubscriptionEpoch,Base=m.BaseRevision,Kind=m.Kind};
            }
            sent.LastPage=Math.Max(sent.LastPage,m.PageIndex);side.Sent[key]=sent;
            if(m.PageIndex+1==m.PageCount && _commits.TryGetCurrentHeader(key,out var current) &&
                (current.Generation!=m.Generation || current.Revision!=m.Revision))_report.completedSupersededTransfers++;
        }

        private bool LinksEmpty()
        {
            foreach(var side in _clients)if(!side.Down.Empty || !side.Up.Empty)return false;
            return true;
        }
        private static WorldChunkKey Key(WorldReplicationChunkStamp h)=>
            new WorldChunkKey((WorldMaterialDomain)h.Domain,h.X,h.Y,h.Z);
        private bool InView(Side side,WorldChunkKey key)
        {
            if(key.Domain==WorldMaterialDomain.Cellular && key.X==0 && key.Y==0 && key.Z==0)return true;
            float edge=_server.World.CellSize*_server.World.ChunkSize;
            Vector3 min=_server.World.Origin+new Vector3(key.X,key.Y,key.Z)*edge,max=min+Vector3.one*edge;
            Vector3 lo=side.Interest.min,hi=side.Interest.max;
            return min.x<hi.x && min.y<hi.y && min.z<hi.z && max.x>lo.x && max.y>lo.y && max.z>lo.z;
        }
        private int CountBacklog()
        {
            int remaining=0;
            for(int i=0;i<Peers;i++)
            {
                var side=_clients[i];side.Versions.Clear();
                foreach(var h in _server.Network.DescribePeerAcknowledgements(i))side.Versions[Key(h)]=h;
                for(int k=0;k<_commits.ChunkCount;k++)
                {
                    var current=_commits.GetChunkHeader(k);
                    if(current.IsResident && InView(side,current.Key) && (!side.Versions.TryGetValue(current.Key,out var ack) ||
                        ack.Generation!=current.Generation || ack.Revision!=current.Revision || ack.Checksum!=current.Checksum))remaining++;
                }
            }
            return remaining;
        }
        private void VerifyQuantities()
        {
            foreach(var side in _clients)
            {
                side.Versions.Clear();
                foreach(var installed in side.Network.DescribeChunks())if(installed.Resident)side.Versions[Key(installed)]=installed;
                for(int i=0;i<_commits.ChunkCount;i++)
                {
                    var current=_commits.GetChunkHeader(i);
                    if(!current.IsResident || !InView(side,current.Key))continue;
                    if(!side.Versions.TryGetValue(current.Key,out var installed) || current.Generation!=installed.Generation ||
                        current.Revision!=installed.Revision || current.Checksum!=installed.Checksum)_report.versionMismatches++;
                    side.Versions.Remove(current.Key);
                }
                _report.versionMismatches+=side.Versions.Count;
                var cells=new HashSet<Vector3Int>();AddOccupied(_server.World,side,cells);AddOccupied(side.World,side,cells);
                foreach(var cell in cells)
                {
                    if(!InView(side,WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Cellular,cell,_server.World.ChunkSize)))continue;
                    _report.verifiedCellCount++;
                    for(int m=0;m<Materials.Length;m++)
                    {
                        _server.World.TryGetQuantity(cell,Materials[m],out uint authority);
                        side.World.TryGetQuantity(cell,Materials[m],out uint replica);
                        _report.authorityInterestGmu[m]+=authority;_report.replicaInterestGmu[m]+=replica;
                        if(authority!=replica)_report.quantityMismatches++;
                    }
                }
            }
            Assert.That(_report.verifiedCellCount,Is.GreaterThan(0),"最终视野内没有真实占据。");
        }
        private void AddOccupied(ElementWorldRuntime world,Side side,HashSet<Vector3Int> cells)
        {
            world.VisitCellularOccupiedCells(c=>cells.Add(c));
            var resting=world.CellularRestingLiquids;
            if(resting==null)return;
            int size=world.ChunkSize;float edge=world.CellSize*size;
            var first=Vector3Int.FloorToInt((side.Interest.min-world.Origin)/edge)*size;
            var last=Vector3Int.CeilToInt((side.Interest.max-world.Origin)/edge)*size;
            void AddRegion(BoundsInt window)
            {
                resting.VisitTouchingRegions(window,r=>resting.VisitOccupiedBoxes(r,(b,quantity)=>
                {
                    var min=Vector3Int.Max(b.min,window.min);var max=Vector3Int.Min(b.max,window.max);
                    for(int z=min.z;z<max.z;z++)for(int y=min.y;y<max.y;y++)for(int x=min.x;x<max.x;x++)cells.Add(new Vector3Int(x,y,z));
                }));
            }
            AddRegion(new BoundsInt(first,last-first));
            AddRegion(new BoundsInt(Vector3Int.zero,Vector3Int.one*size));
        }
    }
}
