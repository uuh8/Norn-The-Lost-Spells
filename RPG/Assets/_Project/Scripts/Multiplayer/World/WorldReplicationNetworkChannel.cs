using System;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System.IO;
#endif
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 正式 WorldEnvelope 的薄适配器：认证属于 RoomManager/Auth，数量/原子安装属于 Core/ElementField。
    /// Client 接收回调只预检并复制4个固定页槽；每个 Render 轮最多安装处理2页，不能在借用包上跨帧留引用。
    /// DS 只对首个合法输入已证实 SimulationReady 的当前席位发送，避免 Hello 与未装配 World 的竞态。
    /// </summary>
    [RequireComponent(typeof(RoomNetworkManager),typeof(JoinTicketAuthenticator))]
    public sealed class WorldReplicationNetworkChannel : MonoBehaviour
    {
        private RoomNetworkManager _manager;
        private JoinTicketAuthenticator _auth;
        private OnlineWorldBootstrap _world;
        private WorldReplicationCoordinator _coordinator;
        private readonly WorldPageCodec _codec=new WorldPageCodec();
        private readonly byte[] _send=new byte[WorldPageCodec.MaxPayloadBytes];
        private readonly byte[][] _inbox={new byte[WorldPageCodec.MaxPayloadBytes],new byte[WorldPageCodec.MaxPayloadBytes],new byte[WorldPageCodec.MaxPayloadBytes],new byte[WorldPageCodec.MaxPayloadBytes]};
        private readonly int[] _lengths=new int[4];
        private int _head,_count;
        private bool _serverRegistered,_clientRegistered,_reportedFault;
        private JoinConnectionIdentity _clientIdentity;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        // 类型也在Editor编译，避免Native专用分支藏住编译错误；实际创建仍只允许Development DS。
        private RecoveryProbe _recoveryProbe;
        private bool _recoveryProbeInitialized;
#endif
        public int QueuedPages=>_count;
        public long Rejected {get;private set;}
        public long EncodedBytesSent {get;private set;}
        public void Configure(OnlineWorldBootstrap world,WorldReplicationCoordinator coordinator)
        {
            if(ReferenceEquals(_world,world) && ReferenceEquals(_coordinator,coordinator)){EnsureHandlers();return;}
            Clear();_world=world;_coordinator=coordinator;_reportedFault=false;
            _manager=GetComponent<RoomNetworkManager>();_auth=GetComponent<JoinTicketAuthenticator>();EnsureHandlers();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            InitializeRecoveryProbe();
#endif
        }
        private void Update()
        {
            EnsureHandlers();
            if(!NetworkClient.active)
            {
                if(_clientRegistered)NetworkClient.UnregisterHandler<WorldEnvelope>();
                _clientRegistered=false;ClearInbox();_clientIdentity=null;
            }
            if(!NetworkServer.active && _serverRegistered){NetworkServer.UnregisterHandler<WorldEnvelope>();_serverRegistered=false;}
        }
        private void EnsureHandlers()
        {
            if(_coordinator==null || _world==null)return;
            if(_coordinator.IsAuthority && NetworkServer.active && !_serverRegistered)
            {NetworkServer.RegisterHandler<WorldEnvelope>(OnServerControl,true);_serverRegistered=true;}
            if(!_coordinator.IsAuthority && NetworkClient.active && !_clientRegistered)
            {NetworkClient.RegisterHandler<WorldEnvelope>(OnClientPage,true);_clientRegistered=true;}
        }
        private bool Matches(JoinConnectionIdentity id)=>_world!=null && _world.WorldReady && _coordinator!=null &&
            _world.WorldEpoch==_coordinator.WorldEpoch && id!=null && id.RunId==_world.MaterialRunId && id.DungeonId==_world.DungeonId;
        private bool ClientContext(out JoinConnectionIdentity identity)
        {
            identity=_auth!=null && _auth.ClientAuthenticated?_auth.ClientIdentity:null;
            var connection=NetworkClient.connection;
            return _coordinator!=null && !_coordinator.IsAuthority && NetworkClient.isConnected && connection!=null && connection.isAuthenticated &&
                ReferenceEquals(connection.authenticationData,identity) && Matches(identity) && _auth.ClientWorldEpoch==_coordinator.WorldEpoch;
        }
        private bool BindClient(JoinConnectionIdentity identity)
        {
            if(ReferenceEquals(identity,_clientIdentity))return true;
            // 新认证上下文先丢旧连接的在途借用页，再只取消旧候选；已安装 Store/版本由 Coordinator 保留。
            ClearInbox();if(!_coordinator.SetClientConnectionEpoch(identity.ConnectionEpoch))return false;
            _clientIdentity=identity;return true;
        }
        private void OnClientPage(WorldEnvelope envelope)
        {
            // 未通过真实上下文或Codec时没有可信Chunk stamp；保留原Rejected累计，不能伪造目标Key。
            // 显式trace只覆盖能关联的目标包，过滤掉的未知包不算ring溢出，也不能宣称完整拒绝审计。
            if(!ClientContext(out var identity) || !_codec.TryDecode(envelope.Payload,out var page)) {Rejected++;return;}
            if(page.ConnectionEpoch!=identity.ConnectionEpoch || page.WorldEpoch!=_coordinator.WorldEpoch || page.GameplayHash!=_coordinator.GameplayHash ||
                !BindClient(identity)) {ObserveChannelTiming(WorldReplicationTimingStage.PageRejected,page,3,envelope.Payload.Count,actorId:identity.ActorId);Rejected++;return;}
            ObserveChannelTiming(WorldReplicationTimingStage.PageReceived,page,(int)page.Kind,envelope.Payload.Count,actorId:identity.ActorId);
            if(page.Kind==6 || page.Kind==7)
            {
                if(!_coordinator.ReceiveSubscription(page)){ObserveChannelTiming(WorldReplicationTimingStage.PageRejected,page,4,envelope.Payload.Count,actorId:identity.ActorId);Rejected++;return;}
                // 控制在可信主线程回调同步生效，即使四槽已满仍能退出；只压缩已失效的数据，保持其他Key顺序。
                PurgeClosedPages();return;
            }
            if(!_coordinator.AcceptsPage(page)){ObserveChannelTiming(WorldReplicationTimingStage.PageRejected,page,4,envelope.Payload.Count,actorId:identity.ActorId);Rejected++;return;}
            if(_count==_inbox.Length){ObserveChannelTiming(WorldReplicationTimingStage.PageRejected,page,5,envelope.Payload.Count,actorId:identity.ActorId);Rejected++;return;}
            int slot=(_head+_count)%_inbox.Length;
            Array.Copy(envelope.Payload.Array,envelope.Payload.Offset,_inbox[slot],0,envelope.Payload.Count);
            _lengths[slot]=envelope.Payload.Count;_count++;
        }
        private void OnServerControl(NetworkConnectionToClient connection,WorldEnvelope envelope)
        {
            var identity=connection!=null?connection.authenticationData as JoinConnectionIdentity:null;
            // IsCurrentActorConnection 同时检查真实 Session 槽、认证标记和身份对象引用，不能信消息自报身份。
            if(_coordinator==null || !_coordinator.IsAuthority || _manager==null || !_manager.IsCurrentActorConnection(connection,identity) || !Matches(identity))
            {Rejected++;return;}
            if(!_codec.TryDecode(envelope.Payload,out var page) || (page.Kind!=3 && page.Kind!=4)){Rejected++;return;}
            double now=Time.realtimeSinceStartupAsDouble;
            if(!_coordinator.ReceiveControl((int)identity.ActorId-1,connection.connectionId,identity.ConnectionEpoch,page,now)){Rejected++;return;}
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // 只有正式Coordinator已接受的控制才是证据，不能因自报Kind4提前暂停或宣称恢复。
            if(_recoveryProbe!=null)_recoveryProbe.ObserveAcceptedControl(connection,identity,page,now);
#endif
        }
        /// <summary>由唯一 Driver 在既有控制/战斗/进度/移动 Pump 之后调用，不能另加材料发送 Update 时钟。</summary>
        public void Pump(double now)
        {
            EnsureHandlers();if(_world==null || !_world.WorldReady || _coordinator==null || !double.IsFinite(now))return;
            if(_coordinator.Faulted)
            {
                if(!_reportedFault){_reportedFault=true;GameLog.Error("材料复制已停止："+_coordinator.Failure,"WorldReplication");}
                return;
            }
            if(_coordinator.IsAuthority)PumpAuthority(now);else PumpClient(now);
        }
        private void PumpAuthority(double now)
        {
            if(!NetworkServer.active || _manager==null || _world.Registry==null)return;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if(_recoveryProbe!=null)_recoveryProbe.Poll(now);
#endif
            _coordinator.BeginRound(now);
            for(int slot=0;slot<4;slot++)
            {
                var connection=_manager.ActorConnection(slot);
                var identity=connection!=null?connection.authenticationData as JoinConnectionIdentity:null;
                if(!_manager.IsCurrentActorConnection(connection,identity) || !Matches(identity)) {_coordinator.RemovePeer(slot);continue;}
                if(!_coordinator.BindPeer(slot,connection.connectionId,identity.ConnectionEpoch) || !_world.Registry[slot].SimulationReady)continue;
                var actor=_world.Registry[slot];
                if(actor.Body==null || !_coordinator.UpdatePeerPosition(slot,connection.connectionId,identity.ConnectionEpoch,actor.Body.position))continue;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                if(slot==0 && _recoveryProbe!=null && _recoveryProbe.Holding(now))continue;
#endif
                for(int page=0;page<2;page++)
                {
                    if(!_coordinator.TryPreparePage(slot,now,out var message))break;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    var mutation=default(RecoveryMutation);
                    int bytes;
                    if(slot==0 && _recoveryProbe!=null)
                    {
                        mutation=_recoveryProbe.BeforeEncode(connection,identity,message,now);
                        if(_recoveryProbe.Holding(now))break;
                        bool encoded;
                        // outbound只是本次借用的编码输入。无论编码返回false还是抛异常，都必须先恢复，
                        // 再让原MarkPageSent读取准备态；Owner/Peer的冻结数组从未被故障诊断写入。
                        try{encoded=_codec.TryEncode(message,new ArraySegment<byte>(_send),out bytes);}
                        finally{if(mutation.TargetPage)RecoveryProbe.Restore(message,mutation);}
                        if(!encoded){_recoveryProbe.EncodeRejected(mutation,now);break;}
                    }
                    else if(!_codec.TryEncode(message,new ArraySegment<byte>(_send),out bytes))break;
#else
                    if(!_codec.TryEncode(message,new ArraySegment<byte>(_send),out int bytes))break;
#endif
                    // 72B 为 Mirror framing + KCP/IPv4/UDP 的保守预留，实际 batching/socket 成本仍须压测。
                    if(!_coordinator.MarkPageSent(slot,now,bytes+72))break;
                    connection.Send(new WorldEnvelope{Payload=new ArraySegment<byte>(_send,0,bytes)},Channels.Reliable);
                    EncodedBytesSent+=bytes;
                    // Send返回只证明已提交给Mirror可靠发送入口，不等于socket发出/Client收到或GPU可见。
                    // 所有页均写入有界目标trace，PageIndex/Count可还原首末页与同冻结快照重启；不复制payload。
                    ObserveChannelTiming(WorldReplicationTimingStage.PageSent,message,(int)message.Kind,bytes,connection.connectionId,identity.ActorId);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    if(_recoveryProbe!=null)_recoveryProbe.Sent(slot,connection,identity,message,mutation,_send,bytes,now);
#endif
                }
            }
        }
        private void PumpClient(double now)
        {
            if(!ClientContext(out var identity) || !BindClient(identity))return;
            long milliseconds=(long)(now*1000);
            for(int i=0;i<2 && _count>0;i++)
            {
                int at=_head;_head=(_head+1)%_inbox.Length;_count--;
                if(!_codec.TryDecode(new ArraySegment<byte>(_inbox[at],0,_lengths[at]),out var message))
                {Rejected++;continue;}
                if(message.ConnectionEpoch!=identity.ConnectionEpoch || !_coordinator.AcceptsPage(message))
                {ObserveChannelTiming(WorldReplicationTimingStage.PageRejected,message,6,_lengths[at],actorId:identity.ActorId);Rejected++;continue;}
                bool accepted=_coordinator.ReceivePage(message,milliseconds,out var reply);
                if(!accepted)Rejected++;
                if(reply!=null)SendReply(reply);
            }
            if(_coordinator.ExpirePending(milliseconds,out var expired))SendReply(expired);
        }
        private void SendReply(WorldPageMessage message)
        {
            if(!ClientContext(out var identity) || !_codec.TryEncode(message,new ArraySegment<byte>(_send),out int count))return;
            NetworkClient.Send(new WorldEnvelope{Payload=new ArraySegment<byte>(_send,0,count)},Channels.Reliable);EncodedBytesSent+=count;
            ObserveChannelTiming(WorldReplicationTimingStage.ReplySent,message,(int)message.Kind,count,actorId:identity.ActorId);
        }
        /// <summary>
        /// Channel PageRejected.Reason3=已解码身份/世界或Bind拒绝，4=订阅拒绝，5=四槽满，6=延后Pump时身份/订阅已失效；
        /// PageReceived/PageSent/ReplySent.Reason保留实际wire Kind。Count0是此刻真实inbox深度，Count1是编码字节。
        /// GameplayTick单列取World.ServerTick，Header.ServerTick原样保留材料Tick；Frame也不是网络版本。
        /// ActorId来自当前真实认证对象。DS ConnectionId来自真实发送连接；Client不知道DS连接id，填-1，
        /// 跨端通过ActorId/ConnectionEpoch及完整版本stamp关联，不能把Client本地0误认为DS真实连接id。
        /// 默认只做Active空分支，之后才构造值/读取Unity时间；没有逐事件IO、JSON、payload副本或集合增长。
        /// </summary>
        private void ObserveChannelTiming(WorldReplicationTimingStage stage,WorldPageMessage message,int reason,int bytes,int connectionId=-1,ulong actorId=0)
        {
            var timing=WorldReplicationTimingTrace.Active;if(timing==null)return;
            var key=new WorldChunkKey((WorldMaterialDomain)message.Domain,message.ChunkX,message.ChunkY,message.ChunkZ);
            var header=new WorldChunkPayloadHeader(message.WorldEpoch,message.SnapshotId,key,message.Generation,
                message.BaseRevision,message.Revision,message.ServerTick,(int)message.TotalWords,(uint)message.Checksum,message.Kind!=5);
            timing.Record(new WorldReplicationTimingEvent{Stage=stage,Header=header,ConnectionId=connectionId,ActorId=actorId,
                ConnectionEpoch=message.ConnectionEpoch,SubscriptionEpoch=message.SubscriptionEpoch,
                PageIndex=(int)message.PageIndex,PageCount=(int)message.PageCount,Reason=reason,
                Count0=_count,Count1=bytes,Frame=Time.frameCount,GameplayTick=_world!=null?_world.ServerTick:0});
        }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private void InitializeRecoveryProbe()
        {
#if DEVELOPMENT_BUILD && UNITY_SERVER && !UNITY_EDITOR
            if(_recoveryProbeInitialized || _coordinator==null || !_coordinator.IsAuthority)return;
            _recoveryProbeInitialized=true;
            string path=Environment.GetEnvironmentVariable("NORN_N5_RECOVERY_PROBE");
            // ENV为空到此立即结束：不检查文件、不拼request路径、不创建报告/缓存数组。
            // 仅Native Development DS读取开关，Client和Editor虽然编译辅助类型，也不会启用它。
            if(string.IsNullOrWhiteSpace(path))return;
            try
            {
                if(!Path.IsPathFullyQualified(path))throw new ArgumentException("Recovery probe requires an absolute report path");
                _recoveryProbe=new RecoveryProbe(this,Path.GetFullPath(path));
            }
            catch(Exception error){GameLog.Error("显式N5恢复诊断未启用："+error.Message,"WorldReplication");}
#endif
        }
        private bool RecoveryContext(out NetworkConnectionToClient connection,out JoinConnectionIdentity identity)
        {
            connection=_manager!=null?_manager.ActorConnection(0):null;
            identity=connection!=null?connection.authenticationData as JoinConnectionIdentity:null;
            return NetworkServer.active && _manager!=null && _coordinator!=null && _coordinator.IsAuthority && !_coordinator.Faulted &&
                _coordinator.Captures>0 && _world!=null && _world.Registry!=null && _world.Registry[0].SimulationReady &&
                _world.Registry[0].Body!=null && identity!=null && identity.ActorId==1 &&
                _manager.IsCurrentActorConnection(connection,identity) && Matches(identity);
        }
        private struct RecoveryMutation
        {
            internal bool TargetPage,ChangedWord,ChangedBase;
            internal ulong Base;
            internal uint FirstWord;
        }
        [Serializable] private sealed class RecoveryHeader
        {
            public uint protocolVersion,connectionEpoch,kind,domain,pageIndex,pageCount,serverTick,wordOffset,totalWords;
            public int chunkX,chunkY,chunkZ,pageWords;
            public ulong gameplayHash,worldEpoch,subscriptionEpoch,snapshotId,generation,baseRevision,revision,checksum;
            internal void Copy(WorldPageMessage m)
            {
                protocolVersion=m.ProtocolVersion;gameplayHash=m.GameplayHash;connectionEpoch=m.ConnectionEpoch;
                worldEpoch=m.WorldEpoch;subscriptionEpoch=m.SubscriptionEpoch;snapshotId=m.SnapshotId;kind=m.Kind;
                domain=m.Domain;chunkX=m.ChunkX;chunkY=m.ChunkY;chunkZ=m.ChunkZ;generation=m.Generation;
                baseRevision=m.BaseRevision;revision=m.Revision;checksum=m.Checksum;serverTick=m.ServerTick;
                pageIndex=m.PageIndex;pageCount=m.PageCount;wordOffset=m.WordOffset;totalWords=m.TotalWords;pageWords=m.Words.Count;
            }
            internal bool Route(WorldPageMessage m)=>m.WorldEpoch==worldEpoch && m.GameplayHash==gameplayHash &&
                m.ConnectionEpoch==connectionEpoch && m.SubscriptionEpoch==subscriptionEpoch && m.Domain==domain &&
                m.ChunkX==chunkX && m.ChunkY==chunkY && m.ChunkZ==chunkZ;
            internal bool Transfer(WorldPageMessage m)=>Route(m) && m.SnapshotId==snapshotId && m.Generation==generation &&
                m.Revision==revision && m.Checksum==checksum && m.ServerTick==serverTick;
            internal WorldChunkKey Key=>new WorldChunkKey((WorldMaterialDomain)domain,chunkX,chunkY,chunkZ);
        }
        [Serializable] private struct RecoveryStateStamp
        {public string state,utc;public double monotonic;}
        [Serializable] private sealed class RecoveryTransfer
        {
            public bool known;
            public int slot,connectionId,uniquePages,sendEvents;
            public string freezeKind,firstSendUtc,lastSendUtc;
            public double firstSendMonotonic,lastSendMonotonic;
            public ulong pageBits0,pageBits1,pageBits2,pageBits3;
            public RecoveryHeader header=new RecoveryHeader();
            internal bool Same(WorldPageMessage message,int connection)=>connectionId==connection && header.kind==message.Kind &&
                header.baseRevision==message.BaseRevision && header.pageCount==message.PageCount && header.totalWords==message.TotalWords && header.Transfer(message);
            internal void Begin(int peerSlot,int connection,WorldPageMessage message,string reason,double now)
            {
                known=true;slot=peerSlot;connectionId=connection;header.Copy(message);freezeKind=reason;
                uniquePages=sendEvents=0;pageBits0=pageBits1=pageBits2=pageBits3=0;
                firstSendMonotonic=now;firstSendUtc=DateTime.UtcNow.ToString("O");
            }
            internal void Sent(WorldPageMessage message,double now)
            {
                int page=(int)message.PageIndex;ulong bit=1UL<<(page&63);bool added;
                switch(page>>6)
                {
                    case 0:added=(pageBits0&bit)==0;pageBits0|=bit;break;
                    case 1:added=(pageBits1&bit)==0;pageBits1|=bit;break;
                    case 2:added=(pageBits2&bit)==0;pageBits2|=bit;break;
                    default:added=(pageBits3&bit)==0;pageBits3|=bit;break;
                }
                if(added)uniquePages++;sendEvents++;header.Copy(message);
                lastSendMonotonic=now;lastSendUtc=DateTime.UtcNow.ToString("O");
            }
        }
        [Serializable] private sealed class RecoveryReport
        {
            public string state="WaitingForArm",mode,error,completionReason,utc;
            public int targetSlot=0,targetConnection=-1,sentPages,uniqueSentPages,mutatedPages,sourcePageCount,replayAttempts,replayCount;
            public bool selected,done,transferSent,recoveryAcknowledged,replaySent;
            public uint connectionEpoch;
            public ulong worldEpoch,gameplayHash;
            public double monotonic,holdUntil,firstSendMonotonic,lastSendMonotonic,replayMonotonic,recoveryAckMonotonic;
            public string replayUtc,recoveryAckUtc;
            public int firstPageWords;
            public RecoveryHeader originalHeader=new RecoveryHeader(),injectedHeader=new RecoveryHeader(),recoveryHeader=new RecoveryHeader();
            public uint[] originalFirstPageWords=new uint[WorldPageCodec.PageWords],injectedFirstPageWords=new uint[WorldPageCodec.PageWords];
            public string extendedStage,initialHoldUtc,initialReleaseUtc,lastPageHoldUtc,historyHoldUtc,historyReleaseUtc;
            public double extendedDeadline,initialHoldStart,initialHoldUntil,initialReleaseMonotonic,lastPageHoldStart,lastPageHoldUntil,
                historyHoldStart,historyHoldUntil,historyReleaseMonotonic;
            public bool initialSelected,initialAcknowledged,lastPageHoldOnce,oldPageCached,newGenerationAcknowledged;
            public int initialSendEvents,initialUniqueSentPages,latestTransferSlots0,latestTransferSlots1;
            public RecoveryHeader initialHeader=new RecoveryHeader();
            // 每个peer永久Key槽最多128；同Key只保留最新transfer，不把80轮变化写成无限事件日志。
            public RecoveryTransfer[] latestTransfers=new RecoveryTransfer[2*WorldReplicationCoordinator.DefaultChunks];
            public RecoveryStateStamp[] stateChanges=new RecoveryStateStamp[32];
            public int stateChangeCount;
        }
        /// <summary>
        /// 显式隔离网络故障夹具，不拥有材料/Interest/玩家资产权限。只改借用编码输入，随后finally恢复。
        /// 固定缓存最多一份900B故障页，故障选择和重放都只执行一次；请求错误只关闭诊断，不停正常复制。
        /// JSON/文件轮询只存在于ENV启用后的本对象，不可用于正常稳态性能结论。
        /// </summary>
        private sealed class RecoveryProbe
        {
            private readonly WorldReplicationNetworkChannel _channel;
            private readonly string _path,_request;
            private readonly RecoveryReport _report=new RecoveryReport();
            private readonly byte[] _replay=new byte[WorldPageCodec.MaxPayloadBytes];
            private readonly bool[] _sent=new bool[WorldPageCodec.MaxPages];
            private JoinConnectionIdentity _identity;
            private int _replayBytes;
            private double _nextPoll;
            private double _selectionTime;
            private bool _writeFailed;
            private readonly int[] _transferCounts=new int[2];
            internal RecoveryProbe(WorldReplicationNetworkChannel channel,string path)
            {_channel=channel;_path=path;_request=path+".request";SetState("WaitingForArm",Time.realtimeSinceStartupAsDouble);}
            internal bool Holding(double now)=>now<_report.holdUntil;
            private bool Context(NetworkConnectionToClient connection,JoinConnectionIdentity identity)=>
                _channel.RecoveryContext(out var current,out var authenticated) && ReferenceEquals(current,connection) &&
                ReferenceEquals(authenticated,identity) && ReferenceEquals(identity,_identity) &&
                connection.connectionId==_report.targetConnection && identity.ConnectionEpoch==_report.connectionEpoch &&
                _channel._coordinator.WorldEpoch==_report.worldEpoch && _channel._coordinator.GameplayHash==_report.gameplayHash;
            internal void Poll(double now)
            {
                if(_report.mode=="extended")AdvanceExtendedTime(now);
                if(_report.state=="Error" || now<_nextPoll)return;
                _nextPoll=now+.1;
                // 真实控制未回来的异常场景最多等待15秒；不反复重选/重注入/重放，也不阻塞正常发送。
                if(_report.selected && !_report.done && now-_selectionTime>=15)
                {Fail("No associated recovery control within bounded diagnostic deadline",now);return;}
                try
                {
                    if(!File.Exists(_request))return;
                    string command=File.ReadAllText(_request).Trim();
                    File.Delete(_request); // 只消费本报告的精确request文件，不枚举或清理其他诊断。
                    if(command=="extended-hold-initial" || command=="extended-release-initial" || command=="extended-hold-history" ||
                        command=="extended-release-history" || command=="extended-replay-old")ExtendedCommand(command,now);
                    else if(_report.mode=="extended")Fail("Only the five explicit extended commands are accepted in this mode",now);
                    else if(command=="checksum" || command=="missing-base")Arm(command,now);
                    else if(command=="replay")Replay(now);
                    else Fail("Unsupported recovery command",now);
                }
                catch(Exception error){Fail("Recovery request IO: "+error.Message,now);}
            }
            private void Arm(string mode,double now)
            {
                if(_report.state!="WaitingForArm"){Fail("Recovery injection can only be armed once",now);return;}
                if(!_channel.RecoveryContext(out var connection,out var identity)){Fail("Arm requires authenticated slot0 and started Source",now);return;}
                _identity=identity;_report.targetConnection=connection.connectionId;_report.connectionEpoch=identity.ConnectionEpoch;
                _report.worldEpoch=_channel._coordinator.WorldEpoch;_report.gameplayHash=_channel._coordinator.GameplayHash;
                _report.mode=mode;SetState("Armed",now);
            }
            internal RecoveryMutation BeforeEncode(NetworkConnectionToClient connection,JoinConnectionIdentity identity,WorldPageMessage message,double now)
            {
                if(_report.mode=="extended")return BeforeExtendedEncode(connection,identity,message,now);
                if(_report.state!="Armed" && _report.state!="Injecting")return default;
                if(!Context(connection,identity)){Fail("Selected authenticated connection changed",now);return default;}
                if(!_report.selected)
                {
                    // TryPreparePage已保证真实sendable数据；从首页选择才能保证checksum只坏一字、其余页不变。
                    if(message.Kind!=2 || message.PageIndex!=0 || message.BaseRevision==0 || message.Revision<=message.BaseRevision ||
                        message.Words.Count==0 || (_report.mode=="missing-base" && message.BaseRevision<=1))return default;
                    _report.selected=true;_selectionTime=now;_report.originalHeader.Copy(message);_report.injectedHeader.Copy(message);
                    _report.sourcePageCount=(int)message.PageCount;_report.firstPageWords=message.Words.Count;
                    for(int i=0;i<message.Words.Count;i++)_report.originalFirstPageWords[i]=_report.injectedFirstPageWords[i]=message.Words[i];
                    if(_report.mode=="missing-base")_report.injectedHeader.baseRevision=message.BaseRevision-1;
                    else _report.injectedFirstPageWords[0]^=1u;
                    SetState("Injecting",now);
                    if(_report.state=="Error")return default;
                }
                var header=_report.originalHeader;
                if(message.Kind!=2 || !header.Transfer(message) || message.BaseRevision!=header.baseRevision ||
                    message.PageCount!=header.pageCount || message.TotalWords!=header.totalWords)
                {
                    // missing-base可首片即触发正式RequireBaseline；下一份数据已换pending则结束本次选择。
                    // 这里不把原PageCount谎报成已发送，只保留实际成功Send的页数。
                    if(_report.sentPages>0 && (message.Kind==1 || message.Kind==2 || message.Kind==5))Complete("TransferReplaced",now);
                    return default;
                }
                if(_report.transferSent)return default;
                var mutation=new RecoveryMutation{TargetPage=true,Base=message.BaseRevision,FirstWord=message.Words[0]};
                if(_report.mode=="missing-base"){message.BaseRevision=header.baseRevision-1;mutation.ChangedBase=true;}
                else if(message.PageIndex==0){message.Words[0]^=1u;mutation.ChangedWord=true;}
                return mutation;
            }
            internal static void Restore(WorldPageMessage message,RecoveryMutation mutation)
            {if(mutation.ChangedBase)message.BaseRevision=mutation.Base;if(mutation.ChangedWord)message.Words[0]=mutation.FirstWord;}
            internal void EncodeRejected(RecoveryMutation mutation,double now)
            {if(mutation.TargetPage)Fail("Selected fault page failed bounded codec validation",now);}
            internal void Sent(int slot,NetworkConnectionToClient connection,JoinConnectionIdentity identity,WorldPageMessage message,RecoveryMutation mutation,byte[] encoded,int bytes,double now)
            {
                if(_report.mode=="extended")
                {
                    if(_report.state=="Error" || slot>=2)return;
                    if(slot<2 && (message.Kind==1 || message.Kind==2 || message.Kind==5))RecordLatestTransfer(slot,connection,message,now);
                    if(_report.state=="Error")return;
                    if(slot==0)SentExtended(connection,identity,message,encoded,bytes,now);
                    Write(now);
                    return;
                }
                if(!mutation.TargetPage)return;
                if(_report.sentPages==0)_report.firstSendMonotonic=now;
                _report.sentPages++;_report.lastSendMonotonic=now;
                int index=(int)message.PageIndex;
                if(!_sent[index]){_sent[index]=true;_report.uniqueSentPages++;}
                if(mutation.ChangedBase || mutation.ChangedWord)
                {
                    _report.mutatedPages++;
                    // 复制的是编码后的故障wire，而message此时已被finally恢复；只缓存首次成功Send的故障页。
                    if(_replayBytes==0){Array.Copy(encoded,0,_replay,0,bytes);_replayBytes=bytes;}
                }
                if(message.PageIndex+1==message.PageCount)_report.transferSent=true;
                Write(now);
            }
            internal void ObserveAcceptedControl(NetworkConnectionToClient connection,JoinConnectionIdentity identity,WorldPageMessage message,double now)
            {
                if(_report.mode=="extended"){ObserveExtendedControl(connection,identity,message,now);return;}
                if(!_report.selected || _report.state=="Error" || !Context(connection,identity) || !_report.originalHeader.Route(message))return;
                if(message.Kind==4 && !_report.done && _report.sentPages>0 && _report.originalHeader.Transfer(message))
                    Complete("AssociatedResyncAccepted",now);
                else TryObserveRecoveryAcknowledgement(message,now);
            }
            private bool TryObserveRecoveryAcknowledgement(WorldPageMessage message,double now)
            {
                // 身份/认证由调用方正式ReceiveControl路径核验。Generation决定生命周期，Revision只在同代排序；
                // 重建的新代Revision可以从1开始，不能与旧代的大Revision作AND比较。
                if(!_report.selected || _report.state=="Error" || !_report.originalHeader.Route(message) ||
                    message.Kind!=3 || !_report.done || message.BaseRevision!=0 ||
                    !(message.Generation>_report.originalHeader.generation ||
                        (message.Generation==_report.originalHeader.generation && message.Revision>=_report.originalHeader.revision)))return false;
                // 已记录的恢复证据同样单调：旧代迟到ACK不能盖掉新代Header/时间，诊断不得制造版本倒退。
                if(_report.recoveryAcknowledged && (message.Generation<_report.recoveryHeader.generation ||
                    (message.Generation==_report.recoveryHeader.generation && message.Revision<_report.recoveryHeader.revision)))return false;
                _report.recoveryAcknowledged=true;_report.recoveryHeader.Copy(message);
                _report.recoveryAckMonotonic=now;_report.recoveryAckUtc=DateTime.UtcNow.ToString("O");Write(now);return true;
            }
            private void ExtendedCommand(string command,double now)
            {
                if(command=="extended-hold-initial")
                {
                    if(_report.state!="WaitingForArm" || !_channel.RecoveryContext(out var connection,out var identity))
                    {Fail("Extended arm requires waiting state, authenticated slot0 and started Source",now);return;}
                    _identity=identity;_report.targetConnection=connection.connectionId;_report.connectionEpoch=identity.ConnectionEpoch;
                    _report.worldEpoch=_channel._coordinator.WorldEpoch;_report.gameplayHash=_channel._coordinator.GameplayHash;
                    _report.mode="extended";_report.extendedDeadline=now+240;
                    _report.initialHoldStart=now;_report.initialHoldUntil=now+45;_report.initialHoldUtc=DateTime.UtcNow.ToString("O");
                    _report.holdUntil=_report.initialHoldUntil;ExtendedState("InitialHeld",now);return;
                }
                if(_report.mode!="extended" || !_channel.RecoveryContext(out var current,out var authenticated) || !Context(current,authenticated))
                {Fail("Extended command requires the original authenticated slot0 scope",now);return;}
                if(command=="extended-release-initial")
                {
                    if(_report.extendedStage=="InitialHeld")ReleaseInitial(now);
                    else if(_report.initialReleaseUtc==null)Fail("Initial hold has not been armed",now);
                }
                else if(command=="extended-hold-history")
                {
                    if(!_report.initialAcknowledged || _report.historyHoldUtc!=null || _report.oldPageCached)
                    {Fail("History hold requires the completed initial transfer and is limited to once",now);return;}
                    // harness先核对全部Key已准确ACK且无pending；这里仅跳过TryPrepare，绝不清pending或RequireBaseline。
                    _report.historyHoldStart=now;_report.historyHoldUntil=now+45;_report.historyHoldUtc=DateTime.UtcNow.ToString("O");
                    _report.holdUntil=_report.historyHoldUntil;ExtendedState("HistoryHeld",now);
                }
                else if(command=="extended-release-history")
                {
                    if(_report.extendedStage=="HistoryHeld")ReleaseHistory(now);
                    else if(_report.historyReleaseUtc==null)Fail("History hold has not been armed",now);
                }
                else if(command=="extended-replay-old")Replay(now);
            }
            private void AdvanceExtendedTime(double now)
            {
                if(_report.state=="Error" || _report.state=="Replayed")return;
                if(now>=_report.extendedDeadline){Fail("Extended diagnostic reached its 240 second deadline",now);return;}
                // 放在100ms文件轮询节流之前：45s/3s到期后的首个正常Pump就释放，不需要脚本继续命令。
                if(_report.extendedStage=="InitialHeld" && now>=_report.initialHoldUntil)ReleaseInitial(now);
                else if(_report.extendedStage=="HistoryHeld" && now>=_report.historyHoldUntil)ReleaseHistory(now);
                else if(_report.extendedStage=="InitialLastPageHeld" && now>=_report.lastPageHoldUntil)
                    ExtendedState("InitialResumed",now);
            }
            private void ReleaseInitial(double now)
            {
                _report.holdUntil=0;_report.initialReleaseMonotonic=now;_report.initialReleaseUtc=DateTime.UtcNow.ToString("O");
                ExtendedState("InitialReleased",now);
            }
            private void ReleaseHistory(double now)
            {
                _report.holdUntil=0;_report.historyReleaseMonotonic=now;_report.historyReleaseUtc=DateTime.UtcNow.ToString("O");
                ExtendedState("HistoryReleased",now);
            }
            private void ExtendedState(string stage,double now)
            {_report.extendedStage=stage;SetState("Extended"+stage,now);}
            private RecoveryMutation BeforeExtendedEncode(NetworkConnectionToClient connection,JoinConnectionIdentity identity,WorldPageMessage message,double now)
            {
                if(_report.state=="Error" || _report.state=="Replayed")return default;
                if(!Context(connection,identity)){Fail("Extended authenticated connection/world changed",now);return default;}
                if(!_report.initialSelected && _report.extendedStage=="InitialReleased" && message.Kind==1 && message.BaseRevision==0 &&
                    message.Domain==(uint)WorldMaterialDomain.Solid && (message.ChunkX!=0 || message.ChunkY!=0 || message.ChunkZ!=0) &&
                    message.PageIndex==0 && message.PageCount>4)
                {
                    _report.initialSelected=true;_report.initialHeader.Copy(message);Array.Clear(_sent,0,_sent.Length);
                    ExtendedState("InitialReceiving",now);
                }
                if(_report.initialSelected && !_report.lastPageHoldOnce && message.Kind==1 && _report.initialHeader.Transfer(message) &&
                    message.BaseRevision==0 && message.PageCount==_report.initialHeader.pageCount && message.PageIndex+1==message.PageCount)
                {
                    // 只在真实末页尚未编码/Mark之前hold一次。既有2s重传仍运行，恢复可重复旧页；
                    // 记录unique与sendEvents，不能把重复页算成多个材料块或提前安装证据。
                    _report.lastPageHoldOnce=true;_report.lastPageHoldStart=now;_report.lastPageHoldUntil=now+3;
                    _report.lastPageHoldUtc=DateTime.UtcNow.ToString("O");_report.holdUntil=_report.lastPageHoldUntil;
                    ExtendedState("InitialLastPageHeld",now);
                }
                return default; // 扩展场景只门控既有调度，完全不篡改材料Header、Words或CRC。
            }
            private void SentExtended(NetworkConnectionToClient connection,JoinConnectionIdentity identity,WorldPageMessage message,byte[] encoded,int bytes,double now)
            {
                if(!Context(connection,identity))return;
                if(_report.initialSelected && !_report.initialAcknowledged && message.Kind==1 && _report.initialHeader.Transfer(message))
                {
                    _report.initialSendEvents++;int page=(int)message.PageIndex;
                    if(!_sent[page]){_sent[page]=true;_report.initialUniqueSentPages++;}
                }
                if(!_report.oldPageCached && _report.extendedStage=="HistoryReleased" && message.Kind==1 && message.BaseRevision==0 &&
                    message.PageIndex==0 && message.PageCount>4 && _report.initialHeader.Route(message) &&
                    message.Generation==_report.initialHeader.generation && message.Revision>_report.initialHeader.revision)
                {
                    // 仅缓存初始目标同Key、同连接/World/订阅的真实History恢复页，禁止换别的Key凑旧代证明。
                    _report.oldPageCached=true;_report.selected=true;_report.done=true;_report.recoveryAcknowledged=false;
                    _report.originalHeader.Copy(message);_report.injectedHeader.Copy(message);_report.sourcePageCount=(int)message.PageCount;
                    _report.firstPageWords=message.Words.Count;
                    for(int i=0;i<message.Words.Count;i++)_report.originalFirstPageWords[i]=_report.injectedFirstPageWords[i]=message.Words[i];
                    Array.Copy(encoded,0,_replay,0,bytes);_replayBytes=bytes;Array.Clear(_sent,0,_sent.Length);
                    ExtendedState("OldPageCached",now);
                }
                if(_report.oldPageCached && message.Kind==1 && _report.originalHeader.Transfer(message))
                {
                    if(_report.sentPages==0)_report.firstSendMonotonic=now;
                    _report.sentPages++;_report.lastSendMonotonic=now;int page=(int)message.PageIndex;
                    if(!_sent[page]){_sent[page]=true;_report.uniqueSentPages++;}
                    if(message.PageIndex+1==message.PageCount)_report.transferSent=true;
                }
            }
            private void ObserveExtendedControl(NetworkConnectionToClient connection,JoinConnectionIdentity identity,WorldPageMessage message,double now)
            {
                if(_report.state=="Error" || !Context(connection,identity))return;
                if(!_report.initialAcknowledged && _report.initialSelected && message.Kind==3 && message.BaseRevision==0 && _report.initialHeader.Transfer(message))
                {_report.initialAcknowledged=true;ExtendedState("InitialAcknowledged",now);}
                if(_report.oldPageCached && TryObserveRecoveryAcknowledgement(message,now) &&
                    message.Generation>_report.originalHeader.generation && !_report.newGenerationAcknowledged)
                {_report.newGenerationAcknowledged=true;ExtendedState("NewGenerationAcknowledged",now);}
            }
            private void RecordLatestTransfer(int slot,NetworkConnectionToClient connection,WorldPageMessage message,double now)
            {
                int start=slot*WorldReplicationCoordinator.DefaultChunks,count=_transferCounts[slot],index=-1;
                for(int i=0;i<count;i++)
                {
                    var h=_report.latestTransfers[start+i].header;
                    if(h.domain==message.Domain && h.chunkX==message.ChunkX && h.chunkY==message.ChunkY && h.chunkZ==message.ChunkZ){index=start+i;break;}
                }
                if(index<0)
                {
                    if(count==WorldReplicationCoordinator.DefaultChunks){Fail("Extended latest-transfer key capacity",now);return;}
                    index=start+count;_transferCounts[slot]++;_report.latestTransfers[index]=new RecoveryTransfer();
                    _report.latestTransferSlots0=_transferCounts[0];_report.latestTransferSlots1=_transferCounts[1];
                }
                var transfer=_report.latestTransfers[index];
                if(transfer.sendEvents==0 || !transfer.Same(message,connection.connectionId))
                {
                    var frozen=_channel._coordinator.PeerLastFreezeHeader(slot);
                    // 原因只能关联到真正生成本次wire的冻结Header，不能借事后另一Key的last值作证明。
                    if(frozen.WorldEpoch!=message.WorldEpoch || frozen.Key!=new WorldChunkKey((WorldMaterialDomain)message.Domain,message.ChunkX,message.ChunkY,message.ChunkZ) ||
                        frozen.Generation!=message.Generation || frozen.BaseRevision!=message.BaseRevision || frozen.Revision!=message.Revision ||
                        frozen.SnapshotId!=message.SnapshotId || frozen.Checksum!=message.Checksum || frozen.ServerTick!=message.ServerTick ||
                        frozen.WordCount!=message.TotalWords || frozen.IsResident!=(message.Kind!=5))
                    {Fail("Latest transfer could not correlate its actual freeze header",now);return;}
                    transfer.Begin(slot,connection.connectionId,message,_channel._coordinator.PeerLastFreezeKind(slot).ToString(),now);
                }
                transfer.Sent(message,now);
            }
            private void Complete(string reason,double now)
            {
                if(_report.done)return;
                _report.done=true;_report.completionReason=reason;_report.holdUntil=now+3;
                // Resync回调仍按正式路径处理；只暂停slot0发页三秒，自动到期，其他连接和材料模拟照常。
                SetState("Injected",now);
            }
            private void Replay(double now)
            {
                if(_report.replayAttempts!=0){Fail("Replay is limited to one explicit attempt",now);return;}
                _report.replayAttempts=1;_report.replayMonotonic=now;_report.replayUtc=DateTime.UtcNow.ToString("O");
                var h=_report.originalHeader;
                bool extended=_report.mode=="extended";
                bool replayReady=extended?_report.newGenerationAcknowledged && _report.extendedStage=="NewGenerationAcknowledged":_report.state=="Injected";
                if(!replayReady || !_report.recoveryAcknowledged || Holding(now) || _replayBytes==0 ||
                    !_channel.RecoveryContext(out var connection,out var identity) || !Context(connection,identity) ||
                    _channel._coordinator.WorldEpoch!=h.worldEpoch || _channel._coordinator.GameplayHash!=h.gameplayHash ||
                    identity.ConnectionEpoch!=h.connectionEpoch ||
                    !_channel._coordinator.IsCurrentPeerSubscription(0,connection.connectionId,identity.ConnectionEpoch,h.Key,h.subscriptionEpoch))
                {Fail("Replay requires the same recovered authenticated connection/world/subscription",now);return;}
                // 单包网络故障诊断：不调用TryPrepare/MarkPageSent，不移动Peer游标、不制造ACK或恢复任务。
                // Send返回只证明送入Mirror；客户端是否拒绝由真实Client诊断另行核对。
                connection.Send(new WorldEnvelope{Payload=new ArraySegment<byte>(_replay,0,_replayBytes)},Channels.Reliable);
                _channel.EncodedBytesSent+=_replayBytes;_report.replaySent=true;_report.replayCount=1;
                if(extended)_report.extendedStage="Replayed";SetState("Replayed",now);
            }
            private void SetState(string state,double now)
            {
                _report.state=state;
                if(_report.stateChangeCount<_report.stateChanges.Length)
                    _report.stateChanges[_report.stateChangeCount++]=new RecoveryStateStamp{state=state,utc=DateTime.UtcNow.ToString("O"),monotonic=now};
                Write(now);
            }
            private void Fail(string error,double now)
            {_report.error=error;_report.done=true;_report.holdUntil=0;if(_report.mode=="extended")_report.extendedStage="Error";SetState("Error",now);}
            private void Write(double now)
            {
                if(_writeFailed)return;
                _report.monotonic=now;_report.utc=DateTime.UtcNow.ToString("O");
                try{File.WriteAllText(_path,JsonUtility.ToJson(_report,true));}
                catch(Exception error)
                {
                    _writeFailed=true;_report.error="Recovery report IO: "+error.Message;_report.state="Error";_report.done=true;_report.holdUntil=0;
                    if(_report.mode=="extended")_report.extendedStage="Error";
                    GameLog.Error(_report.error,"WorldReplication");
                }
            }
        }
#endif
        private void ClearInbox(){_head=_count=0;Array.Clear(_lengths,0,_lengths.Length);}
        private void PurgeClosedPages()
        {
            int kept=0,original=_count;
            for(int offset=0;offset<original;offset++)
            {
                int source=(_head+offset)%_inbox.Length;
                if(!_codec.TryDecode(new ArraySegment<byte>(_inbox[source],0,_lengths[source]),out var queued) || !_coordinator.AcceptsPage(queued))continue;
                int target=(_head+kept)%_inbox.Length;
                if(target!=source)Array.Copy(_inbox[source],0,_inbox[target],0,_lengths[source]);
                _lengths[target]=_lengths[source];kept++;
            }
            // target在逻辑队列中始终不晚于source，环形物理下标回绕也不会覆盖尚未读到的合法页。
            _count=kept;
        }
        private void Clear()
        {
            if(_serverRegistered)NetworkServer.UnregisterHandler<WorldEnvelope>();
            if(_clientRegistered)NetworkClient.UnregisterHandler<WorldEnvelope>();
            _serverRegistered=_clientRegistered=false;ClearInbox();_clientIdentity=null;
        }
        private void OnDisable()=>Clear();
        private void OnDestroy()=>Clear();
    }
}
