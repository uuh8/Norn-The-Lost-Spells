using System;
using System.IO;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 显式 Development DS 的一次性材料夹具。沿正式 ElementWriteRequest 入队，不写 Store/Revision/客户端缓存。
    /// 没有指定环境路径时不创建对象；它不是注册赠品、玩家投放指令或生产关卡默认材料。
    /// </summary>
    public sealed class OnlineMaterialReplicationProbe : MonoBehaviour
    {
        private string _output,_replicaOutput;
        private double _nextReplicaSample;
        private ElementWorldRuntime _world;
        private long _seedTick;
        private bool _accepted;
        private bool _deltaSequence, _waitingForDelta;
        private int _phase = 1;
        private double _nextPoll;
        private TimingWindow _timingWindow;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // 与既有材料/副本诊断共用入口；空ENV立即返回，不创建GameObject、缓冲、时钟校准或文件轮询。
            InstallTimingProbe(Environment.GetEnvironmentVariable("NORN_N5_TIMING_PROBE"));
#endif
#if DEVELOPMENT_BUILD && UNITY_SERVER && !UNITY_EDITOR
            string output=Environment.GetEnvironmentVariable("NORN_N5_MATERIAL_PROBE");
            if(string.IsNullOrEmpty(output))return;
            var root=new GameObject("ExplicitNativeMaterialReplicationFixture");
            var probe=root.AddComponent<OnlineMaterialReplicationProbe>();probe._output=output;
            // 第二开关仅隔离Native增量验收；默认保留原单次检查点，不影响正常玩家路径。
            string sequence=Environment.GetEnvironmentVariable("NORN_N5_MATERIAL_SEQUENCE");
            probe._deltaSequence=sequence=="delta";probe._extended=sequence=="recovery-extended";
#elif DEVELOPMENT_BUILD && !UNITY_EDITOR
            // 只在隔离Native Client显式配置后采样真实Store；不向Multiplayer增加ElementField依赖。
            string output=Environment.GetEnvironmentVariable("NORN_N5_REPLICA_PROBE");
            InstallReplicaSampler(output);
#endif
        }

        // 保留显式输出路径这一创建门槛；独立入口也让生命周期测试走正式Client安装流程。
        private static OnlineMaterialReplicationProbe InstallReplicaSampler(string output)
        {
            if(string.IsNullOrEmpty(output))return null;
            var root=new GameObject("ExplicitNativeReplicaStoreSampler");
            // RuntimeInitialize只在进程启动时安装一次；Menu进入Hub的Single加载会卸载启动Scene。
            // 仅让显式开启的只读Sampler跨Scene保活，场景材料世界和Actor仍沿原生命周期释放。
            DontDestroyOnLoad(root);
            var probe=root.AddComponent<OnlineMaterialReplicationProbe>();probe._replicaOutput=output;
            return probe;
        }
        private void Update()
        {
            if(_timingWindow!=null){_timingWindow.Poll(Time.realtimeSinceStartupAsDouble);return;}
            // 计时窗口内暂停额外250ms全Store截图，避免观测本身的JSON/IO污染目标阶段。
            // 只影响显式诊断组件；正式材料模拟、网络与Renderer仍由原Owner推进。
            if(WorldReplicationTimingTrace.Active!=null)return;
            if(!string.IsNullOrEmpty(_replicaOutput)){SampleReplicaStore();return;}
            if(string.IsNullOrEmpty(_output))return;
            if(_world==null)_world=FindFirstObjectByType<ElementWorldRuntime>();
            if(_world==null || !_world.IsInitialized || _world.ExecutionMode!=WorldExecutionMode.Authority)return;
            if(_extended)
            {
                try{UpdateExtendedFixture();}
                catch(Exception error){ExtendedFail("Explicit extended fixture exception: "+error.Message);enabled=false;}
                return;
            }
            if(_waitingForDelta)
            {
                // 外部诊断先保存双Client完整基线/ACK，再发一次性请求。私有文件不属于协议或玩家命令。
                if(Time.realtimeSinceStartupAsDouble<_nextPoll)return;
                _nextPoll=Time.realtimeSinceStartupAsDouble+.25;
                string requestPath=_output+".delta.request";
                if(!File.Exists(requestPath))return;
                File.Delete(requestPath);_waitingForDelta=false;_accepted=false;_phase=2;
            }
            var position=_world.Origin+new Vector3(.125f,2.125f,.125f);
            if(!_accepted)
            {
                var request=new ElementWriteRequest(position,MaterialId.Water,512,.25f,false);
                _accepted=_world.TryEnqueueWrite(in request);_seedTick=_world.WorldTick;
                if(!_accepted){GameLog.Error("显式材料复制夹具的正式写入被拒绝。","WorldReplicationProbe");enabled=false;}
                else
                {
                    if(_phase==2)_world.SetSimulationPausedForDebug(false);
                    return;
                }
            }
            // 在五个真实材料Tick后使用既有Debug暂停，建立确定的同一提交边界；不改控制/战斗时钟。
            // 这是显式QA夹具的稳定检查点，不把它冒充持续反应/压力或动态流体表现验收。
            if(_accepted && _world.WorldTick<_seedTick+5)return;
            if(_accepted)_world.SetSimulationPausedForDebug(true);
            Directory.CreateDirectory(Path.GetDirectoryName(_output));
            File.WriteAllText(_output,JsonUtility.ToJson(new Result{Accepted=_accepted,Phase=_phase,StartTick=_seedTick,Quantity=512,WorldTick=_world.WorldTick,PausedForComparison=_accepted,Position=position,RecordedUtc=DateTime.UtcNow.ToString("O")},true));
            // 两检查点之间恢复唯一材料时钟至少五Tick，最终暂停精确比较；不冒称无限连续压力。
            if(_accepted && _deltaSequence && _phase==1)_waitingForDelta=true;
            else enabled=false;
        }

        private void OnDisable(){if(_timingWindow!=null)_timingWindow.Finish("Interrupted");}
        private void OnApplicationQuit(){if(_timingWindow!=null)_timingWindow.Finish("Interrupted");}

        /// <summary>只安装显式计时会话，收到一次start才挂载Core记录器。报告只允许新文件，不能覆盖旧证据。</summary>
        private static OnlineMaterialReplicationProbe InstallTimingProbe(string configPath)
        {
            if(string.IsNullOrWhiteSpace(configPath))return null;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            try
            {
                if(!Path.IsPathFullyQualified(configPath) || !File.Exists(configPath))throw new ArgumentException("Timing config must be an existing absolute path");
                var config=JsonUtility.FromJson<TimingConfig>(File.ReadAllText(configPath));
                string directory=Path.GetDirectoryName(Path.GetFullPath(configPath));
                if(config==null || config.Capacity<1 || config.Capacity>65536 || config.DurationMilliseconds<1000 || config.DurationMilliseconds>30000 ||
                    config.Domain<0 || config.Domain>3 || !TimingSibling(config.Output,directory,".json") || !TimingSibling(config.Command,directory,".request") ||
                    string.Equals(Path.GetFullPath(config.Output),Path.GetFullPath(configPath),StringComparison.OrdinalIgnoreCase) || File.Exists(config.Output))
                    throw new ArgumentException("Invalid timing bounds/output; preserve existing files");
                var window=new TimingWindow(config);
                var root=new GameObject("ExplicitNativeMaterialTiming");DontDestroyOnLoad(root);
                var probe=root.AddComponent<OnlineMaterialReplicationProbe>();probe._timingWindow=window;
                return probe;
            }
            catch(Exception error){GameLog.Error("显式材料计时未启用："+error.Message,"WorldReplicationTiming");return null;}
#else
            return null;
#endif
        }
        private static bool TimingSibling(string path,string directory,string extension)=>
            !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) &&
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),directory,StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(path),extension,StringComparison.OrdinalIgnoreCase);

        [Serializable] private sealed class TimingConfig
        {
            public string Output,Command;
            public int Capacity,DurationMilliseconds,Domain,X,Y,Z;
            public ulong WorldEpoch;
        }
        [Serializable] private sealed class TimingStatus{public string State,RecordedUtc;public bool TraceStartsAfterThisStatusWrite;}
        [Serializable] private struct TimingFrame{public long Timestamp;public int Frame;public float UnscaledDeltaTime;public bool Focused;}
        [Serializable] private sealed class TimingClock
        {
            public bool WindowsQpcCalibrated,IsHighResolution;
            public long StopwatchFrequency,StopwatchTimestamp,QpcFrequency,QpcBefore,QpcAfter,QpcOffsetLowerTicks,QpcOffsetUpperTicks;
            public TimingClock(WorldReplicationTimingCalibration c)
            {
                WindowsQpcCalibrated=c.WindowsQpcCalibrated;IsHighResolution=c.IsHighResolution;
                StopwatchFrequency=c.StopwatchFrequency;StopwatchTimestamp=c.StopwatchTimestamp;QpcFrequency=c.QpcFrequency;
                QpcBefore=c.QpcBefore;QpcAfter=c.QpcAfter;QpcOffsetLowerTicks=c.QpcOffsetLowerTicks;QpcOffsetUpperTicks=c.QpcOffsetUpperTicks;
            }
        }
        [Serializable] private sealed class TimingEvent
        {
            public string Stage;
            public long Timestamp,Sequence,MaterialTick;
            public int Domain,X,Y,Z,ConnectionId,PageIndex,PageCount,Frame,Reason,Count0,Count1,CellX,CellY,CellZ,Words;
            public uint ConnectionEpoch,GameplayTick,PresentationVersion,Quantity,Checksum;
            public ulong WorldEpoch,ActorId,Generation,Revision,BaseRevision,SnapshotId,SubscriptionEpoch,InstalledGeneration,InstalledRevision;
            public bool Resident,InstalledPresent;
            public float UnscaledDeltaTime;
            public double Tokens;
            public TimingEvent(in WorldReplicationTimingEvent e)
            {
                var h=e.Header;Stage=e.Stage.ToString();Timestamp=e.Timestamp;Sequence=e.Sequence;MaterialTick=h.ServerTick;
                Domain=(int)h.Key.Domain;X=h.Key.X;Y=h.Key.Y;Z=h.Key.Z;ConnectionId=e.ConnectionId;PageIndex=e.PageIndex;PageCount=e.PageCount;
                Frame=e.Frame;Reason=e.Reason;Count0=e.Count0;Count1=e.Count1;CellX=e.CellX;CellY=e.CellY;CellZ=e.CellZ;Words=h.WordCount;
                ConnectionEpoch=e.ConnectionEpoch;GameplayTick=e.GameplayTick;PresentationVersion=e.PresentationVersion;Quantity=e.Quantity;
                Checksum=h.Checksum;WorldEpoch=h.WorldEpoch;ActorId=e.ActorId;Generation=h.Generation;Revision=h.Revision;BaseRevision=h.BaseRevision;
                SnapshotId=h.SnapshotId;SubscriptionEpoch=e.SubscriptionEpoch;Resident=h.IsResident;InstalledPresent=e.InstalledPresent;
                InstalledGeneration=e.InstalledGeneration;InstalledRevision=e.InstalledRevision;UnscaledDeltaTime=e.UnscaledDeltaTime;Tokens=e.Tokens;
            }
        }
        [Serializable] private sealed class TimingReport
        {
            public string State,RecordedUtc,StartedUtc,Scene;
            public int ProcessId,EventCount,Capacity,FrameSamplesDropped,Width,Height,EndWidth,EndHeight,TargetFrameRate,VSyncCount;
            public long ProcessStartUtcTicks,DroppedCount,WindowStartTimestamp,WindowEndTimestamp;
            public bool Focused,EndFocused,DrawIsOnlyCpuSubmission=true;
            public TimingClock ClockStart,ClockEnd;
            public TimingEvent[] Events;
            public TimingFrame[] Frames;
        }
        /// <summary>
        /// 所有JSON/路径/缓冲分配在采样前或Detach后。窗口中只比较单调时间、写预分配Frame值；
        /// 不轮询文件、不逐事件导出。一次会话仅一次start，结束/销毁均按实例解除，不清理别人的Trace。
        /// </summary>
        private sealed class TimingWindow
        {
            private readonly TimingConfig _config;
            private readonly WorldReplicationTimingTrace _trace;
            private readonly TimingFrame[] _frames=new TimingFrame[2048];
            private readonly int _pid;
            private readonly long _processStart;
            private bool _recording,_finished,_publishedSampling;
            private double _nextCommand,_end;
            private int _frameCount,_frameDropped,_width,_height,_targetFps,_vSync;
            private bool _focused;
            private string _startedUtc;
            private long _startTimestamp;
            private TimingClock _clockStart;
            internal TimingWindow(TimingConfig config)
            {
                _config=config;_trace=new WorldReplicationTimingTrace(config.Capacity,
                    new WorldChunkKey((WorldMaterialDomain)config.Domain,config.X,config.Y,config.Z),config.WorldEpoch);
                using(var process=System.Diagnostics.Process.GetCurrentProcess())
                {_pid=process.Id;_processStart=process.StartTime.ToUniversalTime().Ticks;}
                WriteStatus("Armed",false);
            }
            private void WriteStatus(string state,bool starting)=>File.WriteAllText(_config.Output+".status.json",
                JsonUtility.ToJson(new TimingStatus{State=state,RecordedUtc=DateTime.UtcNow.ToString("O"),TraceStartsAfterThisStatusWrite=starting},true));
            internal void Poll(double now)
            {
                if(_finished)return;
                if(_recording)
                {
                    if(now>=_end){Finish("Completed");return;}
                    if(_frameCount<_frames.Length)_frames[_frameCount++]=new TimingFrame{Timestamp=System.Diagnostics.Stopwatch.GetTimestamp(),
                        Frame=Time.frameCount,UnscaledDeltaTime=Time.unscaledDeltaTime,Focused=Application.isFocused};
                    else _frameDropped++;
                    return;
                }
                if(now<_nextCommand)return;_nextCommand=now+.25;
                if(!File.Exists(_config.Command))return;
                string command=File.ReadAllText(_config.Command).Trim();File.Delete(_config.Command);
                if(command!="start"){_finished=true;WriteStatus("RejectedCommand",false);return;}
                _clockStart=new TimingClock(WorldReplicationTimingClock.Calibrate());
                _width=Screen.width;_height=Screen.height;_focused=Application.isFocused;
                _targetFps=Application.targetFrameRate;_vSync=QualitySettings.vSyncCount;
                // UTC字符串只作检索，必须在挂载前格式化；窗口起点仍以下面的raw SW timestamp为准。
                _startedUtc=DateTime.UtcNow.ToString("O");
                // 明确此状态是在窗口前写。外部看到Starting后应等全部进程就绪再实际施法；
                // 不把此文件写入的UTC当采样起点，真实起点使用下面的raw Stopwatch timestamp。
                WriteStatus("Starting",true);
                if(!WorldReplicationTimingTrace.TryAttach(_trace)){_finished=true;WriteStatus("BlockedByOtherTrace",false);return;}
                _startTimestamp=System.Diagnostics.Stopwatch.GetTimestamp();
                _end=Time.realtimeSinceStartupAsDouble+_config.DurationMilliseconds/1000d;_recording=true;
                EventBus<OnlineDiagnosticSamplingEvent>.Publish(new OnlineDiagnosticSamplingEvent{Active=true});_publishedSampling=true;
            }
            internal void Finish(string state)
            {
                if(_finished)return;_finished=true;
                long endTimestamp=System.Diagnostics.Stopwatch.GetTimestamp();
                // 先关闭热路径，再进行校准/JSON和文件分配；即使导出异常也不会残留全局Trace或UI采样禁区。
                WorldReplicationTimingTrace.Detach(_trace);_recording=false;
                if(_publishedSampling){EventBus<OnlineDiagnosticSamplingEvent>.Publish(new OnlineDiagnosticSamplingEvent{Active=false});_publishedSampling=false;}
                try
                {
                    var events=new TimingEvent[_trace.Count];
                    for(int i=0;i<events.Length;i++)if(_trace.TryRead(i,out var value))events[i]=new TimingEvent(value);
                    var frames=new TimingFrame[_frameCount];Array.Copy(_frames,frames,_frameCount);
                    var report=new TimingReport{State=state,RecordedUtc=DateTime.UtcNow.ToString("O"),StartedUtc=_startedUtc,
                        Scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,ProcessId=_pid,ProcessStartUtcTicks=_processStart,
                        EventCount=events.Length,Capacity=_trace.Capacity,DroppedCount=_trace.DroppedCount,Frames=frames,FrameSamplesDropped=_frameDropped,
                        Width=_width,Height=_height,Focused=_focused,EndWidth=Screen.width,EndHeight=Screen.height,EndFocused=Application.isFocused,
                        TargetFrameRate=_targetFps,VSyncCount=_vSync,ClockStart=_clockStart,ClockEnd=new TimingClock(WorldReplicationTimingClock.Calibrate()),
                        WindowStartTimestamp=_startTimestamp,WindowEndTimestamp=endTimestamp,Events=events};
                    using(var stream=new FileStream(_config.Output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                    using(var writer=new StreamWriter(stream))writer.Write(JsonUtility.ToJson(report,true));
                    WriteStatus(state,false);
                }
                catch(Exception error){GameLog.Error("显式材料计时导出失败，原报告不覆盖："+error.Message,"WorldReplicationTiming");}
            }
        }
        // 仅显式recovery-extended序列使用；复用真实Solid Owner，不替换绑定Source或写网络版本。
        private bool _extended,_extendedInitialized;
        private string _extendedState="WaitingForSeed",_extendedError;
        private ExtendedConfig _extendedConfig;
        private double _nextExtendedPoll,_nextExtendedReport,_nextHistoryMutation;
        private long _extendedStartTick;
        private int _historyStep;
        private ulong _removedQuantity;
        private readonly WorldChunkKey[] _extendedKeys=new WorldChunkKey[2];
        private bool _extendedKeysReady;
        private sealed class TargetCount{internal WorldChunkKey Key;internal int Cells;internal ulong Quantity;}
        [Serializable] private sealed class ExtendedConfig
        {
            public Vector3 FirstOffset=new Vector3(5,1,5),SecondOffset=new Vector3(9,1,5);
            public int AmountPerTarget=8192,HistoryRounds=80;
            public float Radius=.9f,HistoryInterval=.25f;
        }
        [Serializable] private sealed class ExtendedTarget
        {public int Domain,X,Y,Z,Cells;public ulong Quantity;}
        [Serializable] private sealed class ExtendedResult
        {
            public string State,Error,RecordedUtc;public double Monotonic;public long WorldTick,StartTick;
            public bool Paused,TargetsReady;public int SolidCells,Pending,HistoryStep,HistoryExpectedChangedRecords;
            public ulong SolidQuantity,RemovedQuantity;public ExtendedTarget[] Targets;
        }
        private void UpdateExtendedFixture()
        {
            double now=Time.realtimeSinceStartupAsDouble;
            if(!_extendedInitialized)
            {
                _extendedInitialized=true;_extendedConfig=new ExtendedConfig();
                string config=Environment.GetEnvironmentVariable("NORN_N5_MATERIAL_EXTENDED_CONFIG");
                if(!string.IsNullOrEmpty(config))
                {
                    if(!Path.IsPathFullyQualified(config) || !File.Exists(config)){ExtendedFail("Missing explicit absolute fixture config");return;}
                    _extendedConfig=JsonUtility.FromJson<ExtendedConfig>(File.ReadAllText(config));
                }
                if(_extendedConfig==null || _extendedConfig.AmountPerTarget<5760 || _extendedConfig.AmountPerTarget>ushort.MaxValue ||
                    _extendedConfig.HistoryRounds<65 || _extendedConfig.HistoryRounds>128 ||
                    !float.IsFinite(_extendedConfig.HistoryInterval) || _extendedConfig.HistoryInterval<.2f || _extendedConfig.HistoryInterval>1 ||
                    !float.IsFinite(_extendedConfig.Radius) || _extendedConfig.Radius<=0 || _extendedConfig.Radius>2 ||
                    _world.FixedSolids==null || !_world.FixedSolids.Supports(MaterialId.Ice))
                {ExtendedFail("Unsupported bounded fixture config or Solid Owner");return;}
            }
            if(_extendedError!=null)return;
            if(now>=_nextExtendedPoll)
            {
                _nextExtendedPoll=now+.1;string request=_output+".extended.request";
                if(File.Exists(request))
                {
                    string command=File.ReadAllText(request).Trim();File.Delete(request);
                    if(command=="seed" && _extendedState=="WaitingForSeed")
                    {
                        if(!EnqueueExtendedSeed(_extendedConfig.FirstOffset) || !EnqueueExtendedSeed(_extendedConfig.SecondOffset))
                        {ExtendedFail("Real initial Solid write rejected");return;}
                        _extendedState="Seeding";_extendedStartTick=_world.WorldTick;_world.SetSimulationPausedForDebug(false);
                    }
                    else if(command=="history" && _extendedState=="Seeded")
                    {
                        // 每步间隔大于正式100ms Source采样；实际日志追加数仍由Owner诊断独立确认。
                        _extendedState="HistoryRunning";_historyStep=0;_nextHistoryMutation=now;
                    }
                    else if(command=="clear-first" && _extendedState=="HistoryComplete")
                    {
                        if(!RemoveExtendedTarget()){ExtendedFail("Real Solid removal rejected");return;}
                        _extendedState="Removed";
                    }
                    else if(command=="rebuild-first" && _extendedState=="Removed")
                    {
                        float edge=_world.CellSize*_world.ChunkSize;var key=_extendedKeys[0];
                        var offset=new Vector3((key.X+.5f)*edge,(key.Y+.5f)*edge,(key.Z+.5f)*edge);
                        if(!EnqueueExtendedSeed(offset)){ExtendedFail("Real rebuild Solid write rejected");return;}
                        _extendedState="Rebuilding";_extendedStartTick=_world.WorldTick;_world.SetSimulationPausedForDebug(false);
                    }
                    else {ExtendedFail("Unexpected or repeated fixture command: "+command);return;}
                    WriteExtendedResult(now);
                }
            }
            if(_extendedState=="Seeding" || _extendedState=="Rebuilding")
            {
                if(_world.FixedSolids.PendingDepositCount==0 && _world.WorldTick>=_extendedStartTick+20 && FindExtendedTargets())
                {_world.SetSimulationPausedForDebug(true);_extendedState=_extendedState=="Seeding"?"Seeded":"Rebuilt";WriteExtendedResult(now);}
                else if(_world.WorldTick>_extendedStartTick+100){ExtendedFail("Physical fixture did not form two >=90-cell non-origin chunks within 100 material ticks");return;}
            }
            if(_extendedState=="HistoryRunning" && now>=_nextHistoryMutation)
            {
                _nextHistoryMutation=now+_extendedConfig.HistoryInterval;
                for(int i=0;i<2;i++)if(!RemoveOneExtendedUnit(_extendedKeys[i])){ExtendedFail("Real history mutation rejected");return;}
                _historyStep++;if(_historyStep==_extendedConfig.HistoryRounds){_extendedState="HistoryComplete";WriteExtendedResult(now);}
            }
            if(now>=_nextExtendedReport){_nextExtendedReport=now+.25;WriteExtendedResult(now);}
        }
        private bool EnqueueExtendedSeed(Vector3 offset)
        {
            var write=new ElementWriteRequest(_world.Origin+offset,MaterialId.Ice,(ushort)_extendedConfig.AmountPerTarget,_extendedConfig.Radius,false);
            return _world.TryEnqueueWrite(in write);
        }
        private WorldChunkKey ExtendedCellKey(Vector3Int cell)
            =>WorldChunkPayloadCodec.CellKey(WorldMaterialDomain.Solid,cell,_world.ChunkSize);
        private bool FindExtendedTargets()
        {
            var counts=new System.Collections.Generic.Dictionary<WorldChunkKey,TargetCount>();
            for(int i=0;i<_world.FixedSolids.ResidentCellCount;i++)
            {
                if(!_world.FixedSolids.TryGetCell(i,out var cell) || cell.Material!=MaterialId.Ice)continue;
                var key=ExtendedCellKey(cell.Cell);
                if(!counts.TryGetValue(key,out var count)){count=new TargetCount{Key=key};counts.Add(key,count);}
                count.Cells++;count.Quantity+=cell.Quantity;
            }
            if(_extendedKeysReady)
            {
                for(int i=0;i<2;i++)if(!counts.TryGetValue(_extendedKeys[i],out var count) || count.Cells<90)return false;
                return true;
            }
            float edge=_world.CellSize*_world.ChunkSize;
            var offsets=new[]{_extendedConfig.FirstOffset,_extendedConfig.SecondOffset};
            for(int i=0;i<2;i++)
            {
                int x=Mathf.FloorToInt(offsets[i].x/edge),z=Mathf.FloorToInt(offsets[i].z/edge);TargetCount best=null;
                foreach(var pair in counts)
                {
                    var key=pair.Key;if(key.X!=x || key.Z!=z || key.X==0 && key.Y==0 && key.Z==0 || pair.Value.Cells<90)continue;
                    if(best==null || pair.Value.Cells>best.Cells)best=pair.Value;
                }
                if(best==null)return false;_extendedKeys[i]=best.Key;
            }
            if(_extendedKeys[0]==_extendedKeys[1])return false;
            _extendedKeysReady=true;return true;
        }
        private bool RemoveOneExtendedUnit(WorldChunkKey key)
        {
            for(int i=0;i<_world.FixedSolids.ResidentCellCount;i++)
            {
                if(!_world.FixedSolids.TryGetCell(i,out var cell) || cell.Material!=MaterialId.Ice || ExtendedCellKey(cell.Cell)!=key)continue;
                if(!_world.FixedSolids.TryRemove(cell.Cell,cell.Material,1))return false;
                _removedQuantity++;return true;
            }
            return false;
        }
        private bool RemoveExtendedTarget()
        {
            // 只删除本fixture选定的非原点Ice块；先保存值副本，不能边删边按正在压缩的索引遍历。
            var cells=new MaterialVolumeCell[_world.FixedSolids.ResidentCellCount];int count=0;
            for(int i=0;i<_world.FixedSolids.ResidentCellCount;i++)
                if(_world.FixedSolids.TryGetCell(i,out var cell) && cell.Material==MaterialId.Ice && ExtendedCellKey(cell.Cell)==_extendedKeys[0])cells[count++]=cell;
            if(count==0 || _world.FixedSolids.PendingDepositCount!=0)return false;
            for(int i=0;i<count;i++)
            {
                var cell=cells[i];if(!_world.FixedSolids.TryRemove(cell.Cell,cell.Material,cell.Quantity))return false;
                _removedQuantity+=cell.Quantity;
            }
            return true;
        }
        private void ExtendedFail(string error)
        {_extendedError=error;_extendedState="Error";WriteExtendedResult(Time.realtimeSinceStartupAsDouble);}
        private void WriteExtendedResult(double now)
        {
            var targets=new ExtendedTarget[2];for(int i=0;i<2;i++){var key=_extendedKeys[i];targets[i]=new ExtendedTarget{Domain=(int)key.Domain,X=key.X,Y=key.Y,Z=key.Z};}
            for(int i=0;i<(_world.FixedSolids!=null?_world.FixedSolids.ResidentCellCount:0);i++)
            {
                if(!_world.FixedSolids.TryGetCell(i,out var cell))continue;var key=ExtendedCellKey(cell.Cell);
                for(int n=0;n<2;n++)if(_extendedKeysReady && key==_extendedKeys[n]){targets[n].Cells++;targets[n].Quantity+=cell.Quantity;}
            }
            var result=new ExtendedResult{State=_extendedState,Error=_extendedError,RecordedUtc=DateTime.UtcNow.ToString("O"),Monotonic=now,
                WorldTick=_world.WorldTick,StartTick=_extendedStartTick,Paused=_world.SimulationPausedForDebug,TargetsReady=_extendedKeysReady,
                SolidCells=_world.FixedSolids!=null?_world.FixedSolids.ResidentCellCount:0,Pending=_world.FixedSolids!=null?_world.FixedSolids.PendingDepositCount:0,SolidQuantity=_world.FixedSolids!=null?_world.FixedSolids.TotalQuantity:0,
                HistoryStep=_historyStep,HistoryExpectedChangedRecords=_extendedConfig!=null?_extendedConfig.HistoryRounds*2:0,RemovedQuantity=_removedQuantity,Targets=targets};
            Directory.CreateDirectory(Path.GetDirectoryName(_output));File.WriteAllText(_output,JsonUtility.ToJson(result,true));
        }

        /// <summary>
        /// 诊断每250ms复制实际Current格，而不是复制网络Header。与3秒有界故障发送暂停对照，
        /// 能保存拒绝后/恢复前的真实数量；这里的数组/JSON/IO分配明确不参加性能验收。
        /// </summary>
        private void SampleReplicaStore()
        {
            double now=Time.realtimeSinceStartupAsDouble;if(now<_nextReplicaSample)return;_nextReplicaSample=now+.25;
            if(_world==null)_world=FindFirstObjectByType<ElementWorldRuntime>();
            if(_world==null || _world.ExecutionMode!=WorldExecutionMode.Replica || _world.ReplicaStore==null)return;
            var store=_world.ReplicaStore;var cells=new CellResult[store.CellCount];
            for(int i=0;i<cells.Length;i++)
            {
                var c=store.GetCellAt(i);store.TryGetGeometry(c.Domain,out var geometry);var key=WorldChunkPayloadCodec.CellKey(c.Domain,c.Cell,geometry.ChunkSize);
                cells[i]=new CellResult{Domain=(int)c.Domain,Storage=(int)c.Storage,ChunkX=key.X,ChunkY=key.Y,ChunkZ=key.Z,
                    X=c.Cell.x,Y=c.Cell.y,Z=c.Cell.z,Material=c.MaterialId,Quantity=c.Quantity};
            }
            var report=new ReplicaResult{WorldEpoch=store.WorldEpoch,WorldTick=store.WorldTick,CellCount=store.CellCount,
                Legacy=store.TotalQuantity(WorldMaterialDomain.LegacyCell),Liquid=store.TotalQuantity(WorldMaterialDomain.Liquid),
                Solid=store.TotalQuantity(WorldMaterialDomain.Solid),Gas=store.TotalQuantity(WorldMaterialDomain.Gas),Cells=cells,
                RecordedUtc=DateTime.UtcNow.ToString("O"),Monotonic=now};
            Directory.CreateDirectory(Path.GetDirectoryName(_replicaOutput));
            File.WriteAllText(_replicaOutput,JsonUtility.ToJson(report,true));
        }
        [Serializable] private struct CellResult{public int Domain,Storage,X,Y,Z,Material,ChunkX,ChunkY,ChunkZ;public uint Quantity;}
        [Serializable] private sealed class ReplicaResult{public ulong WorldEpoch,Legacy,Liquid,Solid,Gas;public long WorldTick;public int CellCount;public CellResult[] Cells;public string RecordedUtc;public double Monotonic;}
        [Serializable] private sealed class Result{public bool Accepted,PausedForComparison;public int Quantity,Phase;public long WorldTick,StartTick;public Vector3 Position;public string RecordedUtc;}
    }
}
