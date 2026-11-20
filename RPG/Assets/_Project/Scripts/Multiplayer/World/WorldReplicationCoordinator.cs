using System;
using System.Collections.Generic;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 一个 World Owner 的复制装配：DS 唯一捕获/提交，四连接各持一份冻结传输；Client 只组装和安装。
    /// 不拥有材料规则或认证权。Mirror 适配器必须先核验真实连接，再调用这里的可信身份入口。
    /// 发送范围只来自DS当前认证Actor；订阅代次是传输生命周期，与材料Generation/Revision分离。
    /// </summary>
    public sealed partial class WorldReplicationCoordinator
    {
        public const int DefaultChunks=128, DefaultWords=16384;
        // 原构造保留18,000B/s初值，避免技术设施/旧调用被隐式提档。生产入口显式选择经过
        // 固定四Key真实编码吞吐回归的档位；这些是应用层准入预算，不声称等于KCP/Socket带宽。
        public const double ProductionQuantityBytesPerSecond=64*1024, ProductionQuantityBurstBytes=12*1024;
        public const int ProductionQuantityPagesPerRound=4;
        private readonly WorldReplicationCommitOwner _owner;
        private IWorldReplicationSource _committedSource;
        private WorldReplicaBuffer _committedBuffer;
        private readonly WorldReplicationPeerState[] _peers;
        private readonly bool[] _active=new bool[4];
        private readonly int[] _cursor=new int[4],_resyncCount=new int[4];
        private readonly double[] _resyncWindow=new double[4];
        private readonly WorldChunkReplicaAssembler[] _assemblers;
        private readonly Dictionary<WorldChunkKey,int> _clientIndices;
        private readonly IWorldChunkPayloadInstaller _installer;
        private readonly WorldChunkPayloadHeader[] _incoming;
        private readonly IWorldChunkPayloadInterestSink _interestSink;
        private readonly IWorldChunkPayloadRetirementSink _retirementSink;
        private readonly WorldInterestCoordinator _interest=new WorldInterestCoordinator();
        private readonly bool[] _interestCompared=new bool[4];
        private readonly Bounds[] _comparedBounds=new Bounds[4];
        private readonly ulong[] _comparedSequence=new ulong[4];
        private readonly WorldGridGeometry[] _geometry=new WorldGridGeometry[5];
        private readonly bool[] _hasGeometry=new bool[5];
        private struct Subscription { internal bool Known,Desired,Announced,AnnouncementKnown;internal ulong Epoch,AnnouncedEpoch,PendingEpoch; }
        // Owner退休空Key时会交换末槽；共同退休边界同步搬动这些索引，不按探索历史无限追加。
        private readonly Subscription[,] _subscriptions;
        private readonly ulong[] _nextSubscriptionEpoch=new ulong[4];
        private ulong _clientControlFloor;
        private readonly ulong[] _peerAcks=new ulong[4];
        // 与Owner固定Key容量同界；保存每连接每Key最后一次真实安装ACK，防止另一Key遮盖恢复归因。
        private readonly WorldReplicationChunkStamp[,] _peerAckHeaders;
        private readonly WorldReplicationChunkStamp[] _lastPeerAck=new WorldReplicationChunkStamp[4];
        private readonly ulong[] _clientSubscriptions,_incomingEpoch;
        private readonly bool[] _clientActive,_needsBaseline;
        private int _preparedSlot=-1,_preparedIndex=-1;
        private uint _preparedKind;
        private ulong _preparedEpoch;
        private float _referenceSize;
        private int _radiusXZ,_radiusY;
        private bool _interestConfigured;
        private readonly uint[] _page=new uint[WorldPageCodec.PageWords];
        private readonly WorldPageMessage _outbound=WarmMessage(),_reply=WarmMessage();
        private readonly int _maxWords;
        private int _slots;
        private uint _clientEpoch;
        private bool _stopped;
        public ulong WorldEpoch {get;}
        public ulong GameplayHash {get;}
        /// <summary>Adapter每轮发送控制与数量页时必须共用此上限；Motion拥有独立且同样有界的预算。</summary>
        public int QuantityPagesPerRound {get;}
        public bool IsAuthority=>_owner!=null;
        public ulong HistoryRecordsWritten=>_owner!=null?_owner.HistoryRecordsWritten:0;
        public int HistoryCapacity=>_owner!=null?_owner.HistoryCapacity:0;
        /// <summary>只读生命周期计数：区分当前占用槽与累计已退休地址，不以历史写数代替活容量。</summary>
        public int MetadataKeys=>IsAuthority?_owner.ChunkCount:_slots;
        public ulong RetiredKeys {get;private set;}
        public WorldReplicationFreezeKind PeerLastFreezeKind(int slot)=>(IsAuthority && (uint)slot<4)?_peers[slot].LastFreezeKind:WorldReplicationFreezeKind.None;
        public WorldChunkPayloadHeader PeerLastFreezeHeader(int slot)=>(IsAuthority && (uint)slot<4)?_peers[slot].PendingHeader:default;
        public bool Faulted {get;private set;}
        public string Failure {get;private set;}
        /// <summary>保留首个外部退休/发送异常用于诊断；故障后不通过换连接代次清空现场。</summary>
        public Exception LifecycleException {get;private set;}
        private Action _faultHandler;
        // 仅本World组合根订阅，故障当次先撤销真实材料写资格，不能等下个Render帧才冻结Port。
        internal void SetFaultHandler(Action handler)
        {
            if (handler != null && _faultHandler != null && _faultHandler != handler)
                throw new InvalidOperationException("复制故障只能由当前World Owner接管。");
            _faultHandler = handler;
            if (Faulted) handler?.Invoke();
        }

        public long Captures {get;private set;}
        public long PagesSent {get;private set;}
        public long Installed {get;private set;}
        public long Acknowledged {get;private set;}
        public long Rejected {get;private set;}
        // 只读观测，不参与版本/重试裁决；记录的是实际请求/接受/安装，而不是推测成功。
        public long ResyncRequested {get;private set;}
        public long ResyncAccepted {get;private set;}
        public long BaselineInstalled {get;private set;}
        public long DeltaInstalled {get;private set;}
        public WorldReplicaAssemblyFailure LastAssemblyFailure {get;private set;}
        public WorldReplicationChunkStamp LastRejectedCandidate {get;private set;}
        public WorldReplicationChunkStamp LastRejectedInstalled {get;private set;}
        public bool LastRejectedPayloadAvailable {get;private set;}
        public WorldReplicationChunkStamp LastAcceptedResync {get;private set;}
        public int LastResyncActorSlot {get;private set;}=-1;
        public WorldChunkPayloadHeader LastAcknowledged {get;private set;}
        public WorldChunkPayloadHeader LastInstalled {get;private set;}
        // 保留原8参构造的真实签名；已有分层测试/装配会通过Reflection调用，optional参数
        // 不会自动补齐Activator的object[]。新生产装配显式选择4，旧调用仍保持单Key语义。
        public WorldReplicationCoordinator(ulong epoch,ulong hash,int chunks,int maxWords,int cells,int pending,int inspections,IWorldChunkPayloadInstaller installer=null)
            :this(epoch,hash,chunks,maxWords,cells,pending,inspections,installer,1) { }
        public WorldReplicationCoordinator(ulong epoch,ulong hash,int chunks,int maxWords,int cells,int pending,int inspections,IWorldChunkPayloadInstaller installer,int maximumInFlight)
            :this(epoch,hash,chunks,maxWords,cells,pending,inspections,installer,maximumInFlight,18000,3600,2) { }

        /// <summary>
        /// 预算由World组合根选择，不从Client请求读取；旧8/9参签名继续使用18K/3600/2页。
        /// 提高发送吞吐不改变数量Owner、同Key串行、恢复栅栏或原子安装；所有Flight与控制页
        /// 仍共享同一Peer的实际编码字节额度。有限burst只吸收短时排队，不能绕过长期速率。
        /// </summary>
        public WorldReplicationCoordinator(ulong epoch,ulong hash,int chunks,int maxWords,int cells,int pending,int inspections,IWorldChunkPayloadInstaller installer,int maximumInFlight,double quantityBytesPerSecond,double quantityBurstBytes,int maximumPagesPerRound)
            :this(epoch,hash,chunks,maxWords,cells,pending,inspections,installer,maximumInFlight,quantityBytesPerSecond,quantityBurstBytes,maximumPagesPerRound,chunks){ }

        /// <summary>房间目录按实际世界变化持久保留。Delta 的短期历史按变更条数独立设定，缺口由当前 Baseline 恢复。</summary>
        public WorldReplicationCoordinator(ulong epoch,ulong hash,int chunks,int maxWords,int cells,int pending,int inspections,IWorldChunkPayloadInstaller installer,int maximumInFlight,double quantityBytesPerSecond,double quantityBurstBytes,int maximumPagesPerRound,int historyRecords)
        {
            if(epoch==0 || hash==0 || chunks<1 || maxWords<11 || maxWords>WorldPageCodec.MaxWords)throw new ArgumentOutOfRangeException(nameof(epoch));
            if(maximumInFlight<1 || maximumInFlight>4)throw new ArgumentOutOfRangeException(nameof(maximumInFlight));
            if(historyRecords<1)throw new ArgumentOutOfRangeException(nameof(historyRecords));
            if(!double.IsFinite(quantityBytesPerSecond) || quantityBytesPerSecond<=0)throw new ArgumentOutOfRangeException(nameof(quantityBytesPerSecond));
            if(!double.IsFinite(quantityBurstBytes) || quantityBurstBytes<=0)throw new ArgumentOutOfRangeException(nameof(quantityBurstBytes));
            if(maximumPagesPerRound<1 || maximumPagesPerRound>8)throw new ArgumentOutOfRangeException(nameof(maximumPagesPerRound));
            WorldEpoch=epoch;GameplayHash=hash;_maxWords=maxWords;QuantityPagesPerRound=maximumPagesPerRound;
            if(installer==null)
            {
                _owner=new WorldReplicationCommitOwner(epoch,chunks,cells,pending,historyRecords,maxWords,inspections);
                _subscriptions=new Subscription[4,chunks];_peerAckHeaders=new WorldReplicationChunkStamp[4,chunks];
                _liquidMotionRetainUntil=new double[4,chunks];
                _peers=new WorldReplicationPeerState[4];
                for(int i=0;i<4;i++)_peers[i]=new WorldReplicationPeerState(chunks,maxWords,WorldPageCodec.PageWords,quantityBytesPerSecond,quantityBurstBytes,maximumInFlight,maximumPagesPerRound);
            }
            else
            {
                _assemblers=new WorldChunkReplicaAssembler[chunks];_incoming=new WorldChunkPayloadHeader[chunks];
                _installer=installer;_clientIndices=new Dictionary<WorldChunkKey,int>(Math.Min(chunks,32));
                _interestSink=installer as IWorldChunkPayloadInterestSink;
                if(_interestSink==null)throw new ArgumentException("正式Replica Installer必须支持Interest缓存释放。",nameof(installer));
                _retirementSink=installer as IWorldChunkPayloadRetirementSink;
                if(_retirementSink==null)throw new ArgumentException("正式Replica Installer必须支持可信订阅退休。",nameof(installer));
                _clientSubscriptions=new ulong[chunks];_incomingEpoch=new ulong[chunks];
                _clientActive=new bool[chunks];_needsBaseline=new bool[chunks];
            }
        }
        private static WorldPageMessage WarmMessage()
        {var m=new WorldPageMessage();for(int i=0;i<WorldPageCodec.PageWords;i++)m.Words.Add(0);m.Words.Clear();return m;}

        /// <summary>在唯一材料步之后调用一次，不能按连接重复扫描 live Source。失败不发布半份版本。</summary>
        public bool Capture(IWorldReplicationSource source,WorldReplicaBuffer buffer)
        {
            if(!IsAuthority || _stopped || Faulted)return false;
            if(source==null || buffer==null)return Fail("MissingCaptureSource");
            bool unchanged=ReferenceEquals(source,_committedSource) && ReferenceEquals(buffer,_committedBuffer) &&
                source is IWorldUnchangedReplicationSource quiet && quiet.TryAdvanceUnchangedCapture(buffer);
            if(unchanged)
            {
                if(!_owner.TryAdvanceUnchangedCapture(buffer.Header.WorldTick))return Fail("Commit:"+_owner.LastFailure);
            }
            else
            {
                int changed=0;WorldCaptureFailure failure=WorldCaptureFailure.None;
                bool partial=ReferenceEquals(source,_committedSource) && ReferenceEquals(buffer,_committedBuffer) &&
                    source is IWorldCellularIncrementalSource incremental && incremental.TryCaptureChangedChunks(buffer,out changed,out failure);
                if(failure!=WorldCaptureFailure.None || !partial && !source.TryCapture(buffer,out failure))return Fail("Source:"+failure);
                if(!(partial?_owner.TryCaptureChangedChunks(buffer,changed):_owner.TryCaptureCommit(buffer)))return Fail("Commit:"+_owner.LastFailure);
                _committedSource=source;_committedBuffer=buffer;
            }
            // 仅提交成功后复制可信几何，绝不能把失败capture留下的候选几何用于网络判交。
            for(int i=0;i<buffer.Header.GeometryCount;i++){var g=buffer.Geometries[i];_geometry[(int)g.Domain]=g;_hasGeometry[(int)g.Domain]=true;}
            for(int slot=0;slot<4;slot++)if(_active[slot])RefreshSubscriptions(slot);
            Captures++;
            return RetireReleasedAuthorityChunks();
        }
        public bool ConfigureInterest(float referenceChunkSize,int radiusXZ,int radiusY)
        {
            if(_stopped || !WorldInterestCoordinator.TryBuildReferenceBounds(Vector3.zero,Vector3.zero,referenceChunkSize,radiusXZ,radiusY,out _))return false;
            _referenceSize=referenceChunkSize;_radiusXZ=radiusXZ;_radiusY=radiusY;_interestConfigured=true;return true;
        }
        /// <summary>网络适配器先审真实认证对象，此处再核对槽/连接代次；不接收客户端自报位置。</summary>
        public bool UpdatePeerPosition(int slot,int connectionId,uint epoch,Vector3 position)
        {
            if(!_interestConfigured || !_hasGeometry[0] || !WorldInterestCoordinator.TryBuildReferenceBounds(position,_geometry[0].Origin,_referenceSize,_radiusXZ,_radiusY,out var bounds))return false;
            return UpdatePeerInterest(slot,connectionId,epoch,bounds);
        }
        public bool UpdatePeerInterest(int slot,int connectionId,uint epoch,Bounds bounds)
        {
            if(!IsAuthority || _stopped || Faulted || (uint)slot>=4 || !_active[slot] ||
                _peers[slot].ConnectionId!=connectionId || _peers[slot].ConnectionEpoch!=epoch || !_interest.TrySetConnection(slot,connectionId,epoch,bounds))return false;
            return RefreshSubscriptions(slot);
        }
        private bool RefreshSubscriptions(int slot)
        {
            var p=_peers[slot];if(!_interest.TryGetInterest(p.ConnectionId,p.ConnectionEpoch,out var bounds))return true;
            // 角色仍在同一参考窗口、世界未变且没有等待 ACK/延时释放的 tombstone 时，
            // 本次 Capture/发送轮沿用原订阅，静止的整图编辑历史不会每帧重做 Bounds 判交。
            if(_owner.InactiveChunkCount==0 && _interestCompared[slot] && _comparedSequence[slot]==_owner.CommitSequence &&
                _comparedBounds[slot].Equals(bounds))return true;
            for(int i=0;i<_owner.ChunkCount;i++)
            {
                var header=_owner.GetChunkHeader(i);var key=header.Key;int d=(int)key.Domain;
                if(!_hasGeometry[d])return Fail("Missing committed chunk geometry");
                var g=_geometry[d];float edge=g.CellSize*g.ChunkSize;
                Vector3 min=g.Origin+new Vector3(key.X,key.Y,key.Z)*edge;
                // 远处玩家即使只看见原始基底，也必须先获得同一配置身份。其余块照常按权威位置订阅。
                bool inside=key.Domain==WorldMaterialDomain.Cellular && key.X==0 && key.Y==0 && key.Z==0 ||
                    _interest.Intersects(p.ConnectionId,p.ConnectionEpoch,new Bounds(min+Vector3.one*(edge*.5f),Vector3.one*edge));
                ref var s=ref _subscriptions[slot,i];
                // 删除仍需让已经订阅的Peer先安装精确tombstone；ACK后才排Leave。
                // 从未发送过该地址的Peer没有旧量可清，不因一个历史空地址建立新订阅。
                if(!header.IsResident && (!s.Known || TombstoneAcknowledged(slot,i,header) && !LiquidMotionRetains(slot,i)))inside=false;
                if(!s.Known)
                {s.Known=true;s.Epoch=0;s.Desired=inside;s.AnnouncementKnown=!inside;continue;}
                if(s.Desired==inside)continue;
                // 新Enter的身份在发送准备阶段按连接顺序分配。这里置0，保证快速Leave/重入
                // 仍先撤销已公布旧代，而不会在Capture顺序中预分配乱序的全连接水位。
                if(inside)s.Epoch=0;
                s.Desired=inside;
                s.PendingEpoch=0;
                p.RequireBaseline(key);
            }
            _comparedBounds[slot]=bounds;_comparedSequence[slot]=_owner.CommitSequence;_interestCompared[slot]=true;
            return true;
        }
        public bool BindPeer(int slot,int connectionId,uint epoch)
        {
            if(!IsAuthority || _stopped || (uint)slot>=4 || connectionId<0 || epoch==0)return false;
            var p=_peers[slot];
            if(_active[slot] && epoch<p.ConnectionEpoch)return false;
            if(_active[slot] && p.ConnectionId==connectionId && p.ConnectionEpoch==epoch)return true;
            if(_active[slot])_interest.RemoveConnection(p.ConnectionId,p.ConnectionEpoch);
            p.Reset(connectionId,epoch);_active[slot]=true;_cursor[slot]=0;_peerAcks[slot]=0;
            _nextSubscriptionEpoch[slot]=0;
            _lastPeerAck[slot]=default;
            _interestCompared[slot]=false;
            for(int i=0;i<_subscriptions.GetLength(1);i++){_subscriptions[slot,i]=default;_peerAckHeaders[slot,i]=default;_liquidMotionRetainUntil[slot,i]=0;}
            _resyncCount[slot]=0;_resyncWindow[slot]=double.NaN;return true;
        }
        public void RemovePeer(int slot)
        {if(!IsAuthority || (uint)slot>=4)return;CancelPreparedPage(slot);var p=_peers[slot];_interest.RemoveConnection(p.ConnectionId,p.ConnectionEpoch);_active[slot]=false;p.Reset(0,0);}
        public void BeginRound(double now)
        {
            if(!IsAuthority || _stopped || Faulted || !double.IsFinite(now))return;
            _liquidMotionRoundTime=now;
            // 借用只在当前发送轮有效；编码/预算失败不能留下一个阻止全World退休的永久借用。
            CancelPreparedPage(_preparedSlot);
            for(int i=0;i<4;i++)if(_active[i])
            {
                _peers[i].BeginRound(now);
                if(_peers[i].RestartTimedOutTransfer(now,2))ObservePeerTiming(i,5);
            }
        }
        /// <summary>先发有界生命周期，再发冻结页。控制和页共用同一每轮/字节预算，不新造可靠流。</summary>
        public bool TryPreparePage(int slot,double now,out WorldPageMessage message)
        {
            message=null;
            if(!IsAuthority || _stopped || Faulted || (uint)slot>=4 || !_active[slot] || !double.IsFinite(now))return false;
            var p=_peers[slot];if(p.PagesSentThisRound>=p.MaximumPagesPerRound || !_interest.TryGetInterest(p.ConnectionId,p.ConnectionEpoch,out _))return false;
            _preparedSlot=-1;
            for(int i=0;i<_owner.ChunkCount;i++)
            {
                ref var s=ref _subscriptions[slot,i];if(!s.Known)continue;
                uint kind=0;ulong subscription=0;
                if(!s.AnnouncementKnown)
                {
                    if(!s.Desired){s.AnnouncementKnown=true;s.Announced=false;continue;}
                    kind=6;
                }
                else if(s.Announced && (!s.Desired || s.AnnouncedEpoch!=s.Epoch)){kind=7;subscription=s.AnnouncedEpoch;}
                else if(s.Desired && (!s.Announced || s.AnnouncedEpoch!=s.Epoch)){kind=6;}
                if(kind==0)continue;
                if(kind==6)
                {
                    if(_nextSubscriptionEpoch[slot]==ulong.MaxValue)return Fail("Subscription epoch exhausted");
                    // 编码/限流失败允许跳号；重试重新分配，实际进入可靠同通道的Enter严格单调。
                    subscription=++_nextSubscriptionEpoch[slot];s.Epoch=subscription;
                }
                FillLifecycle(_outbound,_owner.GetChunkHeader(i).Key,p.ConnectionEpoch,subscription,kind);
                Prepare(slot,i,kind,subscription);message=_outbound;return true;
            }
            // 每Key各有一份冻结正文；连接/订阅变化只撤销对应flight，不让另一块的ACK
            // 清掉本块或把新订阅号贴到旧正文上。数组固定上限4，不扩张总字节/每轮页预算。
            for(int flight=0;flight<p.MaximumInFlight;flight++)
            {
                if(!p.TryGetPendingHeaderAt(flight,out var pendingHeader))continue;
                int index=OwnerIndex(pendingHeader.Key);
                if(index<0 || !Sendable(slot,index) || _subscriptions[slot,index].PendingEpoch!=_subscriptions[slot,index].Epoch)
                {p.RequireBaseline(pendingHeader.Key);if(index>=0)_subscriptions[slot,index].PendingEpoch=0;}
            }
            if(p.InFlightCount<p.MaximumInFlight && _owner.ChunkCount>0)
            {
                // 固定游标公平轮转，先把同一已提交World的不同Chunk冻结到空槽。
                // 等某块安装ACK期间，其它块仍可发页；同Key直到ACK/恢复前保持串行，
                // 不能为了追新Tick替换已冻结的一半正文。Client原有按Key assembler继续原子安装。
                for(int scan=0;scan<_owner.ChunkCount && p.InFlightCount<p.MaximumInFlight;scan++)
                {
                    if(_cursor[slot]>=_owner.ChunkCount)_cursor[slot]=0;int index=_cursor[slot]++;
                    if(Sendable(slot,index) && p.TryFreeze(_owner,_owner.GetChunkHeader(index).Key,now))
                    {_subscriptions[slot,index].PendingEpoch=_subscriptions[slot,index].Epoch;ObservePeerTiming(slot,1);}
                }
            }
            if(!p.HasPending)return false;
            if(!p.TryPeekPage(_page,out int count,out int pageIndex))
            {if(WorldReplicationTimingTrace.Active!=null && p.AwaitingInstallAck)ObservePeerTiming(slot,3);return false;}
            var h=p.PendingHeader;if(h.ServerTick>uint.MaxValue)return Fail("WorldTick exceeds wire range");
            int pendingIndex=OwnerIndex(h.Key);if(pendingIndex<0 || !Sendable(slot,pendingIndex))return false;
            ulong pendingEpoch=_subscriptions[slot,pendingIndex].PendingEpoch;
            Fill(_outbound,h,p.ConnectionEpoch,h.IsResident?(h.BaseRevision==0?1u:2u):5u);
            _outbound.SubscriptionEpoch=pendingEpoch;
            _outbound.PageIndex=(uint)pageIndex;_outbound.PageCount=(uint)p.PageCount;
            _outbound.TotalWords=(uint)h.WordCount;_outbound.WordOffset=(uint)(pageIndex*WorldPageCodec.PageWords);
            for(int i=0;i<count;i++)_outbound.Words.Add(_page[i]);Prepare(slot,pendingIndex,_outbound.Kind,pendingEpoch);
            ObservePeerTiming(slot,2,pageIndex);message=_outbound;return true;
        }
        private void Prepare(int slot,int index,uint kind,ulong epoch){_preparedSlot=slot;_preparedIndex=index;_preparedKind=kind;_preparedEpoch=epoch;}
        private int OwnerIndex(WorldChunkKey key)=>_owner.IndexOf(key);
        private bool Sendable(int slot,int index){var s=_subscriptions[slot,index];return s.Known && s.Desired && s.Announced && s.AnnouncedEpoch==s.Epoch;}
        /// <summary>隔离诊断的迟到包重放也必须核对当前真实连接及已公布且仍有效的订阅，不借历史ACK授予资格。</summary>
        public bool IsCurrentPeerSubscription(int slot,int connectionId,uint connectionEpoch,WorldChunkKey key,ulong subscriptionEpoch)
        {
            if(!IsAuthority || _stopped || Faulted || (uint)slot>=4 || !_active[slot] || subscriptionEpoch==0)return false;
            var peer=_peers[slot];int index=OwnerIndex(key);
            return peer.ConnectionId==connectionId && peer.ConnectionEpoch==connectionEpoch && index>=0 &&
                Sendable(slot,index) && _subscriptions[slot,index].Epoch==subscriptionEpoch;
        }
        private void FillLifecycle(WorldPageMessage m,WorldChunkKey key,uint connectionEpoch,ulong subscription,uint kind)
        {m.ResetForReuse();m.ProtocolVersion=ProtocolCompatibility.CurrentVersion;m.GameplayHash=GameplayHash;m.WorldEpoch=WorldEpoch;m.ConnectionEpoch=connectionEpoch;m.SubscriptionEpoch=subscription;m.Kind=kind;m.Domain=(uint)key.Domain;m.ChunkX=key.X;m.ChunkY=key.Y;m.ChunkZ=key.Z;}
        /// <summary>必须在进入 Mirror 前预留完整编码/ framing 字节；失败不得发送或推进页游标。</summary>
        public bool MarkPageSent(int slot,double now,int encodedAndFramingBytes)
        {
            if(!IsAuthority || _stopped || (uint)slot>=4 || !_active[slot] || _preparedSlot!=slot || _preparedIndex<0)return false;
            if(_preparedKind==6 || _preparedKind==7)
            {
                if(!_peers[slot].TryMarkControlSent(now,encodedAndFramingBytes))
                {ObservePeerTiming(slot,4,-1,encodedAndFramingBytes);return false;}
                ref var s=ref _subscriptions[slot,_preparedIndex];s.AnnouncementKnown=true;s.Announced=_preparedKind==6;s.AnnouncedEpoch=_preparedEpoch;
            }
            else if(!_peers[slot].TryMarkPageSent(now,encodedAndFramingBytes))
            {ObservePeerTiming(slot,4,-1,encodedAndFramingBytes);return false;}
            _preparedSlot=-1;PagesSent++;return true;
        }
        public bool ReceiveControl(int slot,int actualConnection,uint actualEpoch,WorldPageMessage message,double now)
        {
            if(!IsAuthority || _stopped || Faulted || (uint)slot>=4 || !_active[slot] || !Identity(message,actualEpoch) || !double.IsFinite(now))return RejectControlTiming(message,actualConnection,actualEpoch,20);
            var p=_peers[slot];var key=Key(message);
            int subscribed=OwnerIndex(Key(message));
            if(subscribed<0 || !Sendable(slot,subscribed) || message.SubscriptionEpoch!=_subscriptions[slot,subscribed].PendingEpoch || message.SubscriptionEpoch!=_subscriptions[slot,subscribed].Epoch)return RejectControlTiming(message,actualConnection,actualEpoch,21);
            // 所有身份和真实 pending 字段先核对，伪造高 ID 不消耗合法 ACK/Resync 的预算。
            if(p.ConnectionId!=actualConnection || p.ConnectionEpoch!=actualEpoch || !p.TryGetPendingHeader(key,out var h) ||
                Key(message)!=h.Key || message.Generation!=h.Generation || message.Revision!=h.Revision ||
                message.SnapshotId!=h.SnapshotId || message.Checksum!=h.Checksum)return RejectControlTiming(message,actualConnection,actualEpoch,22);
            if(message.Kind==3)
            {
                // ACK仅响应这个Key已完整发出的flight，前面已逐字段匹配连接/订阅/版本/Checksum。
                // 接受后pending立即清空，重复ACK无法再次通过；吞吐天然受DS发送页预算限制。
                // 额外32/s请求限流会卡住10Hz×多个活动Chunk，导致正常ACK被拒后等待2秒重发。
                // 主动Resync仍沿下方独立4/s预算，不能借此取消客户端可自造请求的保护。
                if(!p.IsAwaitingInstallAck(key))return RejectControlTiming(message,actualConnection,actualEpoch,23);
                if(!p.TryAcknowledge(actualConnection,actualEpoch,h.Key,h.Generation,h.Revision,h.SnapshotId,h.Checksum))return RejectControlTiming(message,actualConnection,actualEpoch,24);
                LastAcknowledged=h;Acknowledged++;_peerAcks[slot]++;
                _subscriptions[slot,subscribed].PendingEpoch=0;
                _lastPeerAck[slot]=Stamp(h,message.SubscriptionEpoch,actualConnection,actualEpoch);
                _peerAckHeaders[slot,subscribed]=_lastPeerAck[slot];ObserveAcceptedControlTiming(h,message,actualConnection,actualEpoch,slot);return true;
            }
            if(message.Kind!=4 || !Budget(now,ref _resyncWindow[slot],ref _resyncCount[slot],4))return RejectControlTiming(message,actualConnection,actualEpoch,25);
            LastAcceptedResync=Stamp(h,message.SubscriptionEpoch,actualConnection,actualEpoch);
            LastResyncActorSlot=slot;ResyncAccepted++;
            p.RequireBaseline(h.Key);
            _subscriptions[slot,subscribed].PendingEpoch=0;
            // 下一次仍从刚刚缺失的 Key 当前 Baseline 恢复，不等一次完整游标轮转。
            for(int i=0;i<_owner.ChunkCount;i++)if(_owner.GetChunkHeader(i).Key==h.Key){_cursor[slot]=i;break;}
            ObserveAcceptedControlTiming(h,message,actualConnection,actualEpoch,slot);
            return true;
        }
        private static bool Budget(double now,ref double start,ref int count,int limit)
        {if(double.IsNaN(start)||now-start>=1){start=now;count=0;}if(now<start||count>=limit)return false;count++;return true;}
        public bool SetClientConnectionEpoch(uint epoch)
        {
            if(IsAuthority || _stopped || Faulted || epoch==0 || epoch<_clientEpoch)return false;
            if(epoch==_clientEpoch)return true;
            // DS可能在断线期间退休旧Key；新连接不能依赖一份不会再出现的旧Leave。
            // 只清客户端只读缓存，DS原材料与玩家资源完全不变，当前Baseline再原子恢复。
            for(int i=_slots-1;i>=0;i--)if(!RetireClientSlot(i))return false;
            for(int i=0;i<_assemblers.Length;i++)
            {
                if(_assemblers[i]!=null && !_assemblers[i].ResetConnectionEpoch(epoch))return Fail("Replica connection reset rejected");
                _clientActive[i]=false;_clientSubscriptions[i]=_incomingEpoch[i]=0;_needsBaseline[i]=true;
            }
            _clientEpoch=epoch;_clientControlFloor=0;return true;
        }
        private int ClientIndex(WorldChunkKey key)=>_clientIndices.TryGetValue(key,out int index)?index:-1;
        public bool ReceiveSubscription(WorldPageMessage message)
        {
            if(IsAuthority || _stopped || Faulted || !Identity(message,_clientEpoch) || message.SubscriptionEpoch==0 || message.Domain>(uint)WorldMaterialDomain.Cellular ||
                (message.Kind!=6 && message.Kind!=7) || message.SnapshotId!=0 || message.Generation!=0 || message.BaseRevision!=0 || message.Revision!=0 ||
                message.PageIndex!=0 || message.PageCount!=0 || message.ServerTick!=0 || message.Checksum!=0 || message.WordOffset!=0 || message.TotalWords!=0 || message.Words.Count!=0)return Reject();
            var key=Key(message);int slot=ClientIndex(key);
            // 已接受Enter留下的全连接水位隔离旧身份。未知Leave只幂等忽略，
            // 不能用一个没有实际订阅的高代次消耗未来真实Enter的水位。
            if(slot<0 && message.Kind==7)return true;
            if(message.Kind==6 && slot>=0 && _clientActive[slot] && message.SubscriptionEpoch==_clientSubscriptions[slot])return true;
            if(message.Kind==6 && message.SubscriptionEpoch<=_clientControlFloor)return Reject();
            if(slot<0)
            {
                if(_slots==_assemblers.Length)return Fail("Replica subscription key capacity");
                slot=_slots;
                // 初次进入视野才创建收件槽。旧槽退休后可复用，无需为房间每个远方洞提前创建分页器。
                if(_assemblers[slot]==null)
                    _assemblers[slot]=new WorldChunkReplicaAssembler(WorldEpoch,key,_clientEpoch,_maxWords,
                        1+(_maxWords-1)/WorldPageCodec.PageWords,WorldPageCodec.PageWords,15000,_installer);
                if(!_assemblers[slot].TryBindUnusedKey(key))return Fail("Replica key binding rejected");
                _clientIndices.Add(key,slot);_slots++;
            }
            ulong current=_clientSubscriptions[slot];var a=_assemblers[slot];
            if(message.Kind==6)
            {
                if(message.SubscriptionEpoch==current)return _clientActive[slot] || Reject();
                if(message.SubscriptionEpoch<current || _clientActive[slot])return Reject();
                a.CancelPending();_incomingEpoch[slot]=0;_clientSubscriptions[slot]=message.SubscriptionEpoch;
                _clientActive[slot]=true;_needsBaseline[slot]=true;
                _clientControlFloor=message.SubscriptionEpoch;return true;
            }
            if(current!=0 && message.SubscriptionEpoch!=current)return Reject();
            if(current!=0 && !_clientActive[slot])return true;
            if(!RetireClientSlot(slot))return false;
            _clientControlFloor=Math.Max(_clientControlFloor,message.SubscriptionEpoch);return true;
        }
        /// <summary>收包回调与延后Pump都检查；旧代页不分配Key、不触发Resync、更不能自激活订阅。</summary>
        public bool AcceptsPage(WorldPageMessage message)
        {
            if(IsAuthority || _stopped || Faulted || !Identity(message,_clientEpoch) || message.SubscriptionEpoch==0 ||
                (message.Kind!=1 && message.Kind!=2 && message.Kind!=5))return false;
            int slot=ClientIndex(Key(message));return slot>=0 && _clientActive[slot] && _clientSubscriptions[slot]==message.SubscriptionEpoch;
        }
        /// <summary>调用方只传已经预检的有界 Codec 消息；返回 ACK 前真实 Installer 已完成原子发布。</summary>
        public bool ReceivePage(WorldPageMessage message,long nowMilliseconds,out WorldPageMessage response)
        {
            response=null;
            if(!AcceptsPage(message) || nowMilliseconds<0 ||
                (message.Kind!=1 && message.Kind!=2 && message.Kind!=5) || message.TotalWords>_maxWords || message.Words.Count>_page.Length)return Reject();
            var key=Key(message);int slot=ClientIndex(key);
            var a=_assemblers[slot];
            if(_needsBaseline[slot] && message.BaseRevision!=0)
            {if(WorldReplicationTimingTrace.Active!=null)ObserveClientTiming(WorldReplicationTimingStage.ResyncRequested,a,HeaderForTiming(message),message.SubscriptionEpoch,1,message.PageIndex,message.PageCount,slot);Reply(message,4,out response);return Reject();}
            var h=new WorldChunkPayloadHeader(WorldEpoch,message.SnapshotId,key,message.Generation,message.BaseRevision,message.Revision,
                message.ServerTick,(int)message.TotalWords,(uint)message.Checksum,message.Kind!=5);
            if(!a.TryBegin(h,nowMilliseconds))
            {
                ObserveRejection(a,h,message.SubscriptionEpoch);
                ObserveClientTiming(WorldReplicationTimingStage.PageRejected,a,h,message.SubscriptionEpoch,12,message.PageIndex,message.PageCount,slot);
                if(a.LastFailure!=WorldReplicaAssemblyFailure.PendingBusy)
                {ObserveClientTiming(WorldReplicationTimingStage.ResyncRequested,a,h,message.SubscriptionEpoch,2,message.PageIndex,message.PageCount,slot);Reply(message,4,out response);}
                return Reject();
            }
            ObserveClientTiming(WorldReplicationTimingStage.ClientReceive,a,h,message.SubscriptionEpoch,1,message.PageIndex,message.PageCount,slot);
            _incoming[slot]=h;_incomingEpoch[slot]=message.SubscriptionEpoch;
            for(int i=0;i<message.Words.Count;i++)_page[i]=message.Words[i];
            if(!a.TryReceivePage(h,(int)message.PageIndex,_page,message.Words.Count,nowMilliseconds))
            {
                ObserveRejection(a,h,message.SubscriptionEpoch);
                if(a.IsInstallerFaulted)Fail("Replica installer faulted");
                ObserveClientTiming(WorldReplicationTimingStage.ResyncRequested,a,h,message.SubscriptionEpoch,3,message.PageIndex,message.PageCount,slot);
                Reply(message,4,out response);return Reject();
            }
            ObserveClientTiming(WorldReplicationTimingStage.ClientReceive,a,h,message.SubscriptionEpoch,2,message.PageIndex,message.PageCount,slot);
            if(!a.HasPending && a.Installed && a.Header.Equals(h))
            {LastInstalled=h;Installed++;if(message.Kind==1)BaselineInstalled++;else if(message.Kind==2)DeltaInstalled++;
                _needsBaseline[slot]=false;Reply(message,3,out response);}
            return true;
        }
        public bool ExpirePending(long nowMilliseconds,out WorldPageMessage response)
        {
            response=null;if(IsAuthority || _stopped || Faulted)return false;
            for(int i=0;i<_slots;i++)if(_clientActive[i] && _incomingEpoch[i]==_clientSubscriptions[i] && _assemblers[i].ExpirePending(nowMilliseconds))
            {ObserveRejection(_assemblers[i],_incoming[i],_incomingEpoch[i]);ResyncRequested++;
                ObserveClientTiming(WorldReplicationTimingStage.ResyncRequested,_assemblers[i],_incoming[i],_incomingEpoch[i],4,0,0,i);
                Fill(_reply,_incoming[i],_clientEpoch,4);_reply.SubscriptionEpoch=_incomingEpoch[i];response=_reply;return true;}
            return false;
        }
        private bool Identity(WorldPageMessage m,uint epoch)=>m!=null && epoch!=0 && m.ConnectionEpoch==epoch &&
            ProtocolCompatibility.IsSupported(m.ProtocolVersion) && m.WorldEpoch==WorldEpoch && m.GameplayHash==GameplayHash;
        private static WorldChunkKey Key(WorldPageMessage m)=>new WorldChunkKey((WorldMaterialDomain)m.Domain,m.ChunkX,m.ChunkY,m.ChunkZ);
        private void Reply(WorldPageMessage input,uint kind,out WorldPageMessage output)
        {
            if(kind==4)ResyncRequested++;
            var h=new WorldChunkPayloadHeader(WorldEpoch,input.SnapshotId,Key(input),input.Generation,input.BaseRevision,input.Revision,input.ServerTick,0,(uint)input.Checksum,true);
            Fill(_reply,h,_clientEpoch,kind);_reply.SubscriptionEpoch=input.SubscriptionEpoch;output=_reply;
        }
        private void Fill(WorldPageMessage m,WorldChunkPayloadHeader h,uint epoch,uint kind)
        {
            m.ResetForReuse();m.ProtocolVersion=ProtocolCompatibility.CurrentVersion;m.GameplayHash=GameplayHash;
            m.ConnectionEpoch=epoch;m.WorldEpoch=WorldEpoch;m.SnapshotId=h.SnapshotId;m.Kind=kind;m.Domain=(uint)h.Key.Domain;
            m.ChunkX=h.Key.X;m.ChunkY=h.Key.Y;m.ChunkZ=h.Key.Z;m.Generation=h.Generation;m.BaseRevision=h.BaseRevision;
            m.Revision=h.Revision;m.ServerTick=(uint)h.ServerTick;m.Checksum=h.Checksum;
        }
        public void Stop()
        {_stopped=true;if(IsAuthority){for(int i=0;i<4;i++)RemovePeer(i);}else for(int i=0;i<_slots;i++)_assemblers[i].CancelPending();}
        private bool Reject(){Rejected++;return false;}
        private bool Fail(string reason)
        {
            if(Faulted)return false;
            Faulted=true;Failure=reason; // 首个故障与已提交版本都保留，Heartbeat不能自动清除。
            _faultHandler?.Invoke();
            return false;
        }
        /// <summary>稀疏诊断读取真实每连接安装ACK计数，不把总ACK增长误归因到某一个连接。</summary>
        public ulong PeerAcknowledged(int slot)=>(IsAuthority && (uint)slot<4)?_peerAcks[slot]:0;

        /// <summary>
        /// PagePrepared.Reason：1冻结成功、2页编码输入就绪、3等待安装ACK、4字节/轮次预留拒绝、5原2秒重启。
        /// Count0是本轮已预留页数；Count1在Reason1为真实FreezeKind，在Reason4为请求的完整framing字节。
        /// Tokens是同一时刻真实余额，不把请求大小或估算余量冒充token。
        /// 本方法绝不把“准备/扣预算”写成已发送，实际PageSent只由Channel的Mirror Send返回后记录。
        /// 默认关闭先返回，再读取冻结Header和token；不为诊断补token、复制payload或改变游标。
        /// </summary>
        private void ObservePeerTiming(int slot,int reason,int pageIndex=-1,int encodedBytes=0)
        {
            var timing=WorldReplicationTimingTrace.Active;if(timing==null)return;
            var peer=_peers[slot];bool lifecycle=reason==4 && (_preparedKind==6 || _preparedKind==7);
            var header=lifecycle?_owner.GetChunkHeader(_preparedIndex):peer.PendingHeader;
            int index=lifecycle?_preparedIndex:OwnerIndex(header.Key);
            timing.Record(new WorldReplicationTimingEvent{Stage=WorldReplicationTimingStage.PagePrepared,Header=header,
                ConnectionId=peer.ConnectionId,ConnectionEpoch=peer.ConnectionEpoch,ActorId=(ulong)slot+1,
                SubscriptionEpoch=lifecycle?_preparedEpoch:index>=0?_subscriptions[slot,index].PendingEpoch:0,Reason=reason,
                PageIndex=pageIndex,PageCount=lifecycle?0:peer.PageCount,
                Count0=peer.PagesSentThisRound,Count1=reason==1?(int)peer.PendingFreezeKind:encodedBytes,Tokens=peer.Tokens});
        }
        /// <summary>
        /// 控制拒绝Reason20身份/生命周期，21订阅，22真实pending匹配，23未收齐或ACK预算，
        /// 24正式ACK提交拒绝，25非Kind4或Resync预算。这里只观察原短路结果，不重跑验证或消耗预算。
        /// 未认证/未解码包可能没有可信Header，目标过滤会丢弃它；累计Rejected仍按原语义保留，
        /// 因而目标trace不是全部恶意包/Codec拒绝的完整审计，不能给未知包伪造目标Key。
        /// </summary>
        private bool RejectControlTiming(WorldPageMessage message,int connection,uint epoch,int reason)
        {
            var timing=WorldReplicationTimingTrace.Active;
            if(timing!=null && message!=null)
                timing.Record(new WorldReplicationTimingEvent{Stage=WorldReplicationTimingStage.PageRejected,
                    Header=HeaderForTiming(message),ConnectionId=connection,ConnectionEpoch=epoch,
                    SubscriptionEpoch=message.SubscriptionEpoch,PageIndex=(int)message.PageIndex,PageCount=(int)message.PageCount,Reason=reason});
            return Reject();
        }
        private static void ObserveAcceptedControlTiming(WorldChunkPayloadHeader pending,WorldPageMessage message,int connection,uint epoch,int slot)
        {
            var timing=WorldReplicationTimingTrace.Active;if(timing==null)return;
            // Header是DS正式匹配的冻结pending；Kind3/4只写Reason，不信Client自报Base去重构DS事实。
            timing.Record(new WorldReplicationTimingEvent{Stage=WorldReplicationTimingStage.ReplyAccepted,Header=pending,
                ConnectionId=connection,ConnectionEpoch=epoch,SubscriptionEpoch=message.SubscriptionEpoch,ActorId=(ulong)slot+1,
                PageIndex=-1,Reason=(int)message.Kind});
        }
        /// <summary>
        /// Resync Reason1=需要Baseline却收到Delta，2=TryBegin失败，3=TryReceivePage失败，4=候选过期。
        /// Count0保留原AssemblyFailure；Reason1没有调用Assembler，明确填None，不借用上次失败码。
        /// Count1 bit0=PayloadAvailable，bit1=needsBaseline；InstalledPresent及G/R来自真实已装Header，
        /// 包含tombstone/退出Interest保留的版本事实，与当前payload可读资格分栏。
        /// ClientReceive Reason1=TryBegin接受，2=TryReceivePage接受；它们均不能冒称Store已安装。
        /// </summary>
        private void ObserveClientTiming(WorldReplicationTimingStage stage,WorldChunkReplicaAssembler assembler,
            WorldChunkPayloadHeader candidate,ulong subscription,int reason,uint pageIndex,uint pageCount,int slot)
        {
            var timing=WorldReplicationTimingTrace.Active;if(timing==null)return;
            bool installed=assembler.Installed;var previous=assembler.Header;
            timing.Record(new WorldReplicationTimingEvent{Stage=stage,Header=candidate,
                ConnectionId=-1,ConnectionEpoch=_clientEpoch,SubscriptionEpoch=subscription,
                PageIndex=(int)pageIndex,PageCount=(int)pageCount,Reason=reason,
                Count0=stage==WorldReplicationTimingStage.ResyncRequested && reason==1?0:(int)assembler.LastFailure,
                Count1=(assembler.PayloadAvailable?1:0)|(_needsBaseline[slot]?2:0),InstalledPresent=installed,
                InstalledGeneration=installed?previous.Generation:0,InstalledRevision=installed?previous.Revision:0});
        }
        private static WorldChunkPayloadHeader HeaderForTiming(WorldPageMessage message)
            =>new WorldChunkPayloadHeader(message.WorldEpoch,message.SnapshotId,Key(message),message.Generation,
                message.BaseRevision,message.Revision,message.ServerTick,(int)message.TotalWords,(uint)message.Checksum,message.Kind!=5);

        private void ObserveRejection(WorldChunkReplicaAssembler assembler,WorldChunkPayloadHeader candidate,ulong subscription)
        {
            LastAssemblyFailure=assembler.LastFailure;
            LastRejectedCandidate=Stamp(candidate,subscription,-1,_clientEpoch);
            LastRejectedInstalled=Stamp(assembler.Header,subscription,-1,_clientEpoch);
            LastRejectedPayloadAvailable=assembler.PayloadAvailable;
        }
        private static WorldReplicationChunkStamp Stamp(WorldChunkPayloadHeader h,ulong subscription,int connection,uint epoch)
            =>new WorldReplicationChunkStamp{Domain=(int)h.Key.Domain,X=h.Key.X,Y=h.Key.Y,Z=h.Key.Z,
                WorldEpoch=h.WorldEpoch,Generation=h.Generation,Revision=h.Revision,BaseRevision=h.BaseRevision,
                SnapshotId=h.SnapshotId,ServerTick=h.ServerTick,SubscriptionEpoch=subscription,Checksum=h.Checksum,
                Words=h.WordCount,Resident=h.IsResident,ConnectionId=connection,ConnectionEpoch=epoch};
        public WorldReplicationChunkStamp PeerAcknowledgement(int slot)=>(IsAuthority && (uint)slot<4)?_lastPeerAck[slot]:default;
        /// <summary>显式诊断稀疏调用才分配；容量受Owner Key硬上限约束，不是探索历史或无限日志。</summary>
        public WorldReplicationChunkStamp[] DescribePeerAcknowledgements(int slot)
        {
            if(!IsAuthority || (uint)slot>=4)return Array.Empty<WorldReplicationChunkStamp>();
            int count=0;for(int i=0;i<_owner.ChunkCount;i++)if(_peerAckHeaders[slot,i].WorldEpoch!=0)count++;
            var result=new WorldReplicationChunkStamp[count];int at=0;
            for(int i=0;i<_owner.ChunkCount;i++)if(_peerAckHeaders[slot,i].WorldEpoch!=0)result[at++]=_peerAckHeaders[slot,i];
            return result;
        }

        /// <summary>仅稀疏诊断调用时分配值副本；不是 Tick 热路径，也不暴露可写 Store 或冻结数组。</summary>
        public WorldReplicationChunkStamp[] DescribeChunks()
        {
            int count=IsAuthority?_owner.ChunkCount:_slots;int installed=0;
            if(!IsAuthority)for(int i=0;i<count;i++)if(_clientActive[i] && !_needsBaseline[i] && _assemblers[i].PayloadAvailable)installed++;
            var result=new WorldReplicationChunkStamp[IsAuthority?count:installed];int at=0;
            for(int i=0;i<count;i++)
            {
                if(!IsAuthority && (!_clientActive[i] || _needsBaseline[i] || !_assemblers[i].PayloadAvailable))continue;
                var h=IsAuthority?_owner.GetChunkHeader(i):_assemblers[i].Header;
                result[at++]=new WorldReplicationChunkStamp{Domain=(int)h.Key.Domain,X=h.Key.X,Y=h.Key.Y,Z=h.Key.Z,
                    WorldEpoch=h.WorldEpoch,Generation=h.Generation,Revision=h.Revision,BaseRevision=h.BaseRevision,
                    SnapshotId=h.SnapshotId,ServerTick=h.ServerTick,SubscriptionEpoch=IsAuthority?0:_clientSubscriptions[i],Checksum=h.Checksum,Words=h.WordCount,Resident=h.IsResident};
            }
            return result;
        }
    }
    [Serializable] public struct WorldReplicationChunkStamp
    {public int Domain,X,Y,Z,Words,ConnectionId;public uint ConnectionEpoch;public ulong WorldEpoch,Generation,Revision,BaseRevision,SnapshotId,SubscriptionEpoch;public long ServerTick;public uint Checksum;public bool Resident;}
}
