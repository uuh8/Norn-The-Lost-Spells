using System;
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
        private readonly WorldReplicationCommitOwner _owner;
        private readonly WorldReplicationPeerState[] _peers;
        private readonly bool[] _active=new bool[4];
        private readonly int[] _cursor=new int[4],_controlCount=new int[4],_resyncCount=new int[4];
        private readonly double[] _controlWindow=new double[4],_resyncWindow=new double[4];
        private readonly WorldChunkReplicaAssembler[] _assemblers;
        private readonly WorldChunkPayloadHeader[] _incoming;
        private readonly IWorldChunkPayloadInterestSink _interestSink;
        private readonly WorldInterestCoordinator _interest=new WorldInterestCoordinator();
        private readonly WorldGridGeometry[] _geometry=new WorldGridGeometry[4];
        private readonly bool[] _hasGeometry=new bool[4];
        private struct Subscription { internal bool Known,Desired,Announced,AnnouncementKnown;internal ulong Epoch,AnnouncedEpoch; }
        // Owner的Key顺序只追加、不遗忘；每连接按相同索引保留有限状态，快速跨界也不追加事件队列。
        private readonly Subscription[,] _subscriptions;
        private readonly ulong[] _pendingEpoch=new ulong[4],_peerAcks=new ulong[4];
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
        public bool IsAuthority=>_owner!=null;
        public ulong HistoryRecordsWritten=>_owner!=null?_owner.HistoryRecordsWritten:0;
        public int HistoryCapacity=>_owner!=null?_owner.HistoryCapacity:0;
        public WorldReplicationFreezeKind PeerLastFreezeKind(int slot)=>(IsAuthority && (uint)slot<4)?_peers[slot].LastFreezeKind:WorldReplicationFreezeKind.None;
        public WorldChunkPayloadHeader PeerLastFreezeHeader(int slot)=>(IsAuthority && (uint)slot<4)?_peers[slot].PendingHeader:default;
        public bool Faulted {get;private set;}
        public string Failure {get;private set;}
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
        public WorldReplicationCoordinator(ulong epoch,ulong hash,int chunks,int maxWords,int cells,int pending,int inspections,IWorldChunkPayloadInstaller installer=null)
        {
            if(epoch==0 || hash==0 || chunks<1 || maxWords<11 || maxWords>WorldPageCodec.MaxWords)throw new ArgumentOutOfRangeException(nameof(epoch));
            WorldEpoch=epoch;GameplayHash=hash;_maxWords=maxWords;
            if(installer==null)
            {
                _owner=new WorldReplicationCommitOwner(epoch,chunks,cells,pending,chunks,maxWords,inspections);
                _subscriptions=new Subscription[4,chunks];_peerAckHeaders=new WorldReplicationChunkStamp[4,chunks];
                _peers=new WorldReplicationPeerState[4];
                for(int i=0;i<4;i++)_peers[i]=new WorldReplicationPeerState(chunks,maxWords,WorldPageCodec.PageWords,18000,3600);
            }
            else
            {
                _assemblers=new WorldChunkReplicaAssembler[chunks];_incoming=new WorldChunkPayloadHeader[chunks];
                _interestSink=installer as IWorldChunkPayloadInterestSink;
                if(_interestSink==null)throw new ArgumentException("正式Replica Installer必须支持Interest缓存释放。",nameof(installer));
                _clientSubscriptions=new ulong[chunks];_incomingEpoch=new ulong[chunks];
                _clientActive=new bool[chunks];_needsBaseline=new bool[chunks];
                int pages=1+(maxWords-1)/WorldPageCodec.PageWords;
                for(int i=0;i<chunks;i++)_assemblers[i]=new WorldChunkReplicaAssembler(epoch,default,1,maxWords,pages,WorldPageCodec.PageWords,15000,installer);
            }
        }
        private static WorldPageMessage WarmMessage()
        {var m=new WorldPageMessage();for(int i=0;i<WorldPageCodec.PageWords;i++)m.Words.Add(0);m.Words.Clear();return m;}

        /// <summary>在唯一材料步之后调用一次，不能按连接重复扫描 live Source。失败不发布半份版本。</summary>
        public bool Capture(IWorldReplicationSource source,WorldReplicaBuffer buffer)
        {
            if(!IsAuthority || _stopped || Faulted)return false;
            if(source==null || buffer==null)return Fail("MissingCaptureSource");
            if(!source.TryCapture(buffer,out var failure))return Fail("Source:"+failure);
            if(!_owner.TryCaptureCommit(buffer))return Fail("Commit:"+_owner.LastFailure);
            // 仅提交成功后复制可信几何，绝不能把失败capture留下的候选几何用于网络判交。
            for(int i=0;i<buffer.Header.GeometryCount;i++){var g=buffer.Geometries[i];_geometry[(int)g.Domain]=g;_hasGeometry[(int)g.Domain]=true;}
            for(int slot=0;slot<4;slot++)if(_active[slot])RefreshSubscriptions(slot);
            Captures++;return true;
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
            var p=_peers[slot];if(!_interest.TryGetInterest(p.ConnectionId,p.ConnectionEpoch,out _))return true;
            for(int i=0;i<_owner.ChunkCount;i++)
            {
                var key=_owner.GetChunkHeader(i).Key;int d=(int)key.Domain;
                if(!_hasGeometry[d])return Fail("Missing committed chunk geometry");
                var g=_geometry[d];float edge=g.CellSize*g.ChunkSize;
                Vector3 min=g.Origin+new Vector3(key.X,key.Y,key.Z)*edge;
                bool inside=_interest.Intersects(p.ConnectionId,p.ConnectionEpoch,new Bounds(min+Vector3.one*(edge*.5f),Vector3.one*edge));
                ref var s=ref _subscriptions[slot,i];
                if(!s.Known){s.Known=true;s.Epoch=1;s.Desired=inside;continue;}
                if(s.Desired==inside)continue;
                if(inside){if(s.Epoch==ulong.MaxValue)return Fail("Subscription epoch exhausted");s.Epoch++;}
                s.Desired=inside;
                if(p.HasPending && p.PendingHeader.Key==key)_pendingEpoch[slot]=0;
                p.RequireBaseline(key);
            }
            return true;
        }
        public bool BindPeer(int slot,int connectionId,uint epoch)
        {
            if(!IsAuthority || _stopped || (uint)slot>=4 || connectionId<0 || epoch==0)return false;
            var p=_peers[slot];
            if(_active[slot] && epoch<p.ConnectionEpoch)return false;
            if(_active[slot] && p.ConnectionId==connectionId && p.ConnectionEpoch==epoch)return true;
            if(_active[slot])_interest.RemoveConnection(p.ConnectionId,p.ConnectionEpoch);
            p.Reset(connectionId,epoch);_active[slot]=true;_cursor[slot]=0;_pendingEpoch[slot]=_peerAcks[slot]=0;
            _lastPeerAck[slot]=default;
            for(int i=0;i<_subscriptions.GetLength(1);i++){_subscriptions[slot,i]=default;_peerAckHeaders[slot,i]=default;}
            _controlCount[slot]=_resyncCount[slot]=0;_controlWindow[slot]=_resyncWindow[slot]=double.NaN;return true;
        }
        public void RemovePeer(int slot)
        {if(!IsAuthority || (uint)slot>=4)return;var p=_peers[slot];_interest.RemoveConnection(p.ConnectionId,p.ConnectionEpoch);_active[slot]=false;p.Reset(0,0);}
        public void BeginRound(double now)
        {
            if(!IsAuthority || _stopped || Faulted || !double.IsFinite(now))return;
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
            var p=_peers[slot];if(p.PagesSentThisRound>=2 || !_interest.TryGetInterest(p.ConnectionId,p.ConnectionEpoch,out _))return false;
            _preparedSlot=-1;
            for(int i=0;i<_owner.ChunkCount;i++)
            {
                ref var s=ref _subscriptions[slot,i];if(!s.Known)continue;
                uint kind=0;ulong subscription=0;
                if(!s.AnnouncementKnown){kind=s.Desired?6u:7u;subscription=s.Epoch;}
                else if(s.Announced && (!s.Desired || s.AnnouncedEpoch!=s.Epoch)){kind=7;subscription=s.AnnouncedEpoch;}
                else if(s.Desired && (!s.Announced || s.AnnouncedEpoch!=s.Epoch)){kind=6;subscription=s.Epoch;}
                if(kind==0)continue;
                FillLifecycle(_outbound,_owner.GetChunkHeader(i).Key,p.ConnectionEpoch,subscription,kind);
                Prepare(slot,i,kind,subscription);message=_outbound;return true;
            }
            if(p.HasPending)
            {
                int index=OwnerIndex(p.PendingHeader.Key);
                if(index<0 || !Sendable(slot,index) || _pendingEpoch[slot]!=_subscriptions[slot,index].Epoch)
                {p.RequireBaseline(p.PendingHeader.Key);_pendingEpoch[slot]=0;}
            }
            if(!p.HasPending)
            {
                if(_owner.ChunkCount==0)return false;
                for(int scan=0;scan<_owner.ChunkCount;scan++)
                {
                    if(_cursor[slot]>=_owner.ChunkCount)_cursor[slot]=0;int index=_cursor[slot]++;
                    if(Sendable(slot,index) && p.TryFreeze(_owner,_owner.GetChunkHeader(index).Key,now))
                    {_pendingEpoch[slot]=_subscriptions[slot,index].Epoch;ObservePeerTiming(slot,1);break;}
                }
                if(!p.HasPending)return false;
            }
            if(!p.TryPeekPage(_page,out int count,out int pageIndex))
            {if(WorldReplicationTimingTrace.Active!=null && p.AwaitingInstallAck)ObservePeerTiming(slot,3);return false;}
            var h=p.PendingHeader;if(h.ServerTick>uint.MaxValue)return Fail("WorldTick exceeds wire range");
            Fill(_outbound,h,p.ConnectionEpoch,h.IsResident?(h.BaseRevision==0?1u:2u):5u);
            _outbound.SubscriptionEpoch=_pendingEpoch[slot];
            _outbound.PageIndex=(uint)pageIndex;_outbound.PageCount=(uint)p.PageCount;
            _outbound.TotalWords=(uint)h.WordCount;_outbound.WordOffset=(uint)(pageIndex*WorldPageCodec.PageWords);
            for(int i=0;i<count;i++)_outbound.Words.Add(_page[i]);Prepare(slot,OwnerIndex(h.Key),_outbound.Kind,_pendingEpoch[slot]);
            ObservePeerTiming(slot,2,pageIndex);message=_outbound;return true;
        }
        private void Prepare(int slot,int index,uint kind,ulong epoch){_preparedSlot=slot;_preparedIndex=index;_preparedKind=kind;_preparedEpoch=epoch;}
        private int OwnerIndex(WorldChunkKey key){for(int i=0;i<_owner.ChunkCount;i++)if(_owner.GetChunkHeader(i).Key==key)return i;return -1;}
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
            var p=_peers[slot];var h=p.PendingHeader;
            int subscribed=OwnerIndex(Key(message));
            if(subscribed<0 || !Sendable(slot,subscribed) || message.SubscriptionEpoch!=_pendingEpoch[slot] || message.SubscriptionEpoch!=_subscriptions[slot,subscribed].Epoch)return RejectControlTiming(message,actualConnection,actualEpoch,21);
            // 所有身份和真实 pending 字段先核对，伪造高 ID 不消耗合法 ACK/Resync 的预算。
            if(p.ConnectionId!=actualConnection || p.ConnectionEpoch!=actualEpoch || !p.HasPending ||
                Key(message)!=h.Key || message.Generation!=h.Generation || message.Revision!=h.Revision ||
                message.SnapshotId!=h.SnapshotId || message.Checksum!=h.Checksum)return RejectControlTiming(message,actualConnection,actualEpoch,22);
            if(message.Kind==3)
            {
                if(!p.AwaitingInstallAck || !Budget(now,ref _controlWindow[slot],ref _controlCount[slot],32))return RejectControlTiming(message,actualConnection,actualEpoch,23);
                if(!p.TryAcknowledge(actualConnection,actualEpoch,h.Key,h.Generation,h.Revision,h.SnapshotId,h.Checksum))return RejectControlTiming(message,actualConnection,actualEpoch,24);
                LastAcknowledged=h;Acknowledged++;_peerAcks[slot]++;
                _lastPeerAck[slot]=Stamp(h,message.SubscriptionEpoch,actualConnection,actualEpoch);
                _peerAckHeaders[slot,subscribed]=_lastPeerAck[slot];ObserveAcceptedControlTiming(h,message,actualConnection,actualEpoch,slot);return true;
            }
            if(message.Kind!=4 || !Budget(now,ref _resyncWindow[slot],ref _resyncCount[slot],4))return RejectControlTiming(message,actualConnection,actualEpoch,25);
            LastAcceptedResync=Stamp(h,message.SubscriptionEpoch,actualConnection,actualEpoch);
            LastResyncActorSlot=slot;ResyncAccepted++;
            p.RequireBaseline(h.Key);
            // 下一次仍从刚刚缺失的 Key 当前 Baseline 恢复，不等一次完整游标轮转。
            for(int i=0;i<_owner.ChunkCount;i++)if(_owner.GetChunkHeader(i).Key==h.Key){_cursor[slot]=i;break;}
            ObserveAcceptedControlTiming(h,message,actualConnection,actualEpoch,slot);
            return true;
        }
        private static bool Budget(double now,ref double start,ref int count,int limit)
        {if(double.IsNaN(start)||now-start>=1){start=now;count=0;}if(now<start||count>=limit)return false;count++;return true;}
        public bool SetClientConnectionEpoch(uint epoch)
        {
            if(IsAuthority || _stopped || epoch==0 || epoch<_clientEpoch)return false;
            if(epoch==_clientEpoch)return true;
            for(int i=0;i<_assemblers.Length;i++)
            {
                if(!_assemblers[i].ResetConnectionEpoch(epoch))return Fail("Replica connection reset rejected");
                // 原合同保留已安装payload/世界版本；只关闭旧连接订阅资格，等待新认证DS生命周期重新核对。
                _clientActive[i]=false;_clientSubscriptions[i]=_incomingEpoch[i]=0;_needsBaseline[i]=true;
            }
            _clientEpoch=epoch;return true;
        }
        private int ClientIndex(WorldChunkKey key){for(int i=0;i<_slots;i++)if(_assemblers[i].Key==key)return i;return -1;}
        public bool ReceiveSubscription(WorldPageMessage message)
        {
            if(IsAuthority || _stopped || Faulted || !Identity(message,_clientEpoch) || message.SubscriptionEpoch==0 || message.Domain>3 ||
                (message.Kind!=6 && message.Kind!=7) || message.SnapshotId!=0 || message.Generation!=0 || message.BaseRevision!=0 || message.Revision!=0 ||
                message.PageIndex!=0 || message.PageCount!=0 || message.ServerTick!=0 || message.Checksum!=0 || message.WordOffset!=0 || message.TotalWords!=0 || message.Words.Count!=0)return Reject();
            var key=Key(message);int slot=ClientIndex(key);
            // 初始范围外的Leave只用于清理重连保留的历史Key；首次从未见过的Key无缓存，不占元数据槽。
            if(slot<0 && message.Kind==7)return true;
            if(slot<0)
            {
                if(_slots==_assemblers.Length)return Fail("Replica subscription key capacity");
                slot=_slots;if(!_assemblers[slot].TryBindUnusedKey(key))return Fail("Replica key binding rejected");_slots++;
            }
            ulong current=_clientSubscriptions[slot];var a=_assemblers[slot];
            if(message.Kind==6)
            {
                if(message.SubscriptionEpoch==current)return _clientActive[slot] || Reject();
                if(message.SubscriptionEpoch<current || _clientActive[slot])return Reject();
                a.CancelPending();_incomingEpoch[slot]=0;_clientSubscriptions[slot]=message.SubscriptionEpoch;
                _clientActive[slot]=true;_needsBaseline[slot]=true;return true;
            }
            if(current!=0 && message.SubscriptionEpoch!=current)return Reject();
            if(current!=0 && !_clientActive[slot])return true;
            _clientActive[slot]=false;_clientSubscriptions[slot]=message.SubscriptionEpoch;_needsBaseline[slot]=true;_incomingEpoch[slot]=0;
            if(a.Installed && !_interestSink.TryEvictInterest(key))return Fail("Replica interest eviction rejected");
            if(!a.TryReleaseInterest())return Fail("Replica payload release rejected");
            return true;
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
            timing.Record(new WorldReplicationTimingEvent{Stage=WorldReplicationTimingStage.PagePrepared,Header=header,
                ConnectionId=peer.ConnectionId,ConnectionEpoch=peer.ConnectionEpoch,ActorId=(ulong)slot+1,
                SubscriptionEpoch=lifecycle?_preparedEpoch:_pendingEpoch[slot],Reason=reason,
                PageIndex=pageIndex,PageCount=lifecycle?0:peer.PageCount,
                Count0=peer.PagesSentThisRound,Count1=reason==1?(int)peer.LastFreezeKind:encodedBytes,Tokens=peer.Tokens});
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
