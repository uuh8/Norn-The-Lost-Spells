using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Multiplayer
{
    /// <summary>只读诊断说明本次成功冻结的真实分支，不改变Baseline/Delta选择。</summary>
    public enum WorldReplicationFreezeKind {None,InitialBaseline,ForcedBaseline,GenerationBaseline,HistoryGapBaseline,Delta}

    /// <summary>
    /// 一个认证连接的有界发送状态。默认保留单Key合同；正式装配可显式选择最多4个不同Key在途，
    /// 让一块等待安装ACK时，其他Chunk仍能把同一材料Tick的数量事实送到Client。
    /// 每个Key拥有独立的冻结payload/页游标/超时；同Key始终串行，不能把新版拼进旧快照。
    /// token桶、burst和每轮页数仍由整个Peer共享，增加在途并不隐式增加应用层带宽或授权预算。
    /// 安装ACK不是Transport页收据；本类也不声称能约束Mirror/KCP内部全部队列或证明无HOL。
    /// </summary>
    public sealed class WorldReplicationPeerState
    {
        private sealed class Flight
        {
            internal readonly uint[] Frozen;
            internal WorldChunkPayloadHeader Header;
            internal WorldReplicationFreezeKind Kind;
            internal int Cursor;
            internal double LastSend;
            internal bool Active;
            internal Flight(int words){Frozen=new uint[words];}
        }
        private readonly Flight[] _flights;
        private readonly WorldChunkKey[] _keys;
        private readonly Dictionary<WorldChunkKey,int> _indices;
        private readonly ulong[] _generations,_revisions;
        private readonly int _pageWords;
        private readonly double _bytesPerSecond,_burst;
        private double _tokens,_lastRefill=double.NaN;
        private int _slots,_inFlightCount,_selectedFlight,_nextSendFlight;
        private Flight Selected=>_flights[_selectedFlight];
        public int ConnectionId {get;private set;}
        public uint ConnectionEpoch {get;private set;}
        public bool HasPending=>_inFlightCount!=0;
        public int InFlightCount=>_inFlightCount;
        public int MaximumInFlight=>_flights.Length;
        /// <summary>整个Peer的控制页与数量页共用这个上限；不能按Flight分别获得一份额度。</summary>
        public int MaximumPagesPerRound {get;}
        public WorldReplicationFreezeKind LastFreezeKind {get;private set;}
        /// <summary>
        /// 兼容旧单Key调用方的当前借用view：成功Freeze或Peek会选择一条Flight；Mark后仍保留它，
        /// 直到下一次Peek。多Key裁决必须用下面的按Key查询，不能拿这个view代表全部在途状态。
        /// ACK/按Key查询不切换view，防止无关Key的ACK令已准备的页被记到另一条Flight。
        /// </summary>
        public WorldChunkPayloadHeader PendingHeader=>Selected.Header;
        public WorldReplicationFreezeKind PendingFreezeKind=>Selected.Kind;
        public int PageCursor=>Selected.Cursor;
        public int PagesSentThisRound {get;private set;}
        /// <summary>显式计时诊断只读实际余额；读取不会补充、预留或消耗token，也不参与发送裁决。</summary>
        public double Tokens=>_tokens;
        private int Pages(Flight flight)=>flight.Header.WordCount==0?1:1+(flight.Header.WordCount-1)/_pageWords;
        public int PageCount=>Pages(Selected);
        public bool AwaitingInstallAck=>Selected.Active && Selected.Cursor==PageCount;
        public WorldReplicationPeerState(int chunks,int maxWords,int pageWords,double bytesPerSecond,double burst)
            :this(chunks,maxWords,pageWords,bytesPerSecond,burst,1){}

        public WorldReplicationPeerState(int chunks,int maxWords,int pageWords,double bytesPerSecond,double burst,int maximumInFlight)
            :this(chunks,maxWords,pageWords,bytesPerSecond,burst,maximumInFlight,2){}

        /// <summary>
        /// 保留旧5/6参构造的每轮两页语义。正式装配可根据真实编码负载显式选择更高的有界页数，
        /// 字节仍在实际编码成功后由同一token桶扣除，不能用固定最大页大小估计或给每Key另发预算。
        /// 页数只限制一次Pump的工作量，不表示Transport已发送，更不替代完整安装ACK。
        /// </summary>
        public WorldReplicationPeerState(int chunks,int maxWords,int pageWords,double bytesPerSecond,double burst,int maximumInFlight,int maximumPagesPerRound)
        {
            if(chunks<=0 || maxWords<11 || pageWords<=0 || !double.IsFinite(bytesPerSecond) || bytesPerSecond<=0 || !double.IsFinite(burst) || burst<=0)throw new ArgumentOutOfRangeException(nameof(chunks));
            if(maximumInFlight<1 || maximumInFlight>4)throw new ArgumentOutOfRangeException(nameof(maximumInFlight));
            if(maximumPagesPerRound<1 || maximumPagesPerRound>8)throw new ArgumentOutOfRangeException(nameof(maximumPagesPerRound));
            MaximumPagesPerRound=maximumPagesPerRound;
            _keys=new WorldChunkKey[chunks];_generations=new ulong[chunks];_revisions=new ulong[chunks];
            _indices=new Dictionary<WorldChunkKey,int>(Math.Min(chunks,32));
            // 仅构造时分配固定payload缓冲。热路径不创建集合、复制整世界或按ACK速度扩容队列。
            _flights=new Flight[maximumInFlight];for(int i=0;i<_flights.Length;i++)_flights[i]=new Flight(maxWords);
            _pageWords=pageWords;_bytesPerSecond=bytesPerSecond;_burst=burst;_tokens=burst;
        }
        public void Reset(int connectionId,uint epoch)
        {
            ConnectionId=connectionId;ConnectionEpoch=epoch;_slots=0;_inFlightCount=0;_selectedFlight=_nextSendFlight=0;PagesSentThisRound=0;
            _indices.Clear();
            _tokens=_burst;_lastRefill=double.NaN;LastFreezeKind=WorldReplicationFreezeKind.None;
            for(int i=0;i<_flights.Length;i++)
            {var flight=_flights[i];flight.Active=false;flight.Cursor=0;flight.Header=default;flight.Kind=WorldReplicationFreezeKind.None;flight.LastSend=0;}
        }
        public void BeginRound(double now)
        {
            PagesSentThisRound=0;
            if(!double.IsFinite(now))return;
            if(double.IsNaN(_lastRefill))_lastRefill=now;
            if(now<_lastRefill)return;
            _tokens=Math.Min(_burst,_tokens+(now-_lastRefill)*_bytesPerSecond);_lastRefill=now;
        }
        public bool TryFreeze(WorldReplicationCommitOwner owner,WorldChunkKey key,double now)
        {
            if(_inFlightCount==_flights.Length || FindFlight(key)>=0 || owner==null || ConnectionEpoch==0 || !double.IsFinite(now))return false;
            int free=0;while(_flights[free].Active)free++;
            var flight=_flights[free];
            // 扫描已安装列表时只读轻量 Header；真正入选本轮的 Key 才复制一次冻结正文。
            if(!owner.TryGetCurrentHeader(key,out var current))return false;
            int slot=Find(key);
            if(slot>=0 && _generations[slot]==current.Generation && _revisions[slot]==current.Revision)return false;
            if(slot<0)
            {
                // 尚未ACK的初次地址也占用未来ledger名额，否则4块乱序ACK可能到最后一块才发现容量不足。
                // 地址只在真实ACK时写入ledger；这里只预检固定上限，不授予任何已安装版本。
                int reserved=_slots;
                for(int i=0;i<_flights.Length;i++)if(_flights[i].Active && Find(_flights[i].Header.Key)<0)reserved++;
                if(reserved==_keys.Length)return false;
            }
            var reason=slot<0?WorldReplicationFreezeKind.InitialBaseline:
                _generations[slot]==0?WorldReplicationFreezeKind.ForcedBaseline:WorldReplicationFreezeKind.GenerationBaseline;
            if(slot>=0 && _generations[slot]==current.Generation && _revisions[slot]!=0)
            {
                // 历史缺口明确退回当前Baseline；不是把一条缺Base的Delta当成合法覆盖。
                if(owner.TryCopyDelta(key,_generations[slot],_revisions[slot],flight.Frozen,out var delta))
                {current=delta;reason=WorldReplicationFreezeKind.Delta;}
                else
                {
                    // 必须在TryCopyDelta真正失败的此处记录；后续成功Baseline copy会清Owner.LastFailure。
                    if(!owner.TryCopyChunk(key,flight.Frozen,out current))return false;
                    reason=WorldReplicationFreezeKind.HistoryGapBaseline;
                }
            }
            else if(!owner.TryCopyChunk(key,flight.Frozen,out current))return false;
            flight.Header=current;flight.Cursor=0;flight.Active=true;flight.LastSend=now;flight.Kind=reason;
            _selectedFlight=free;_inFlightCount++;LastFreezeKind=reason;return true;
        }
        public bool TryPeekPage(uint[] destination,out int count,out int pageIndex)
        {
            count=0;pageIndex=PageCursor;
            if(!HasPending || PagesSentThisRound>=MaximumPagesPerRound)return false;
            // 按页轮询不同Key，等待ACK的Flight不占住发送头。只有Mark成功才推进下一起点，
            // 编码/预算失败后的再次Peek会借到同一页，不会因轮询跳过未发送的数据。
            for(int scan=0;scan<_flights.Length;scan++)
            {
                int index=_nextSendFlight+scan;if(index>=_flights.Length)index-=_flights.Length;
                var flight=_flights[index];if(!flight.Active || flight.Cursor==Pages(flight))continue;
                _selectedFlight=index;pageIndex=flight.Cursor;
                int offset=flight.Cursor*_pageWords;count=Math.Min(_pageWords,flight.Header.WordCount-offset);
                if(destination==null || destination.Length<count){count=0;return false;}
                Array.Copy(flight.Frozen,offset,destination,0,count);return true;
            }
            // 兼容诊断仍可看到某条真实等待ACK的Flight，不能把刚被另一Key ACK清掉的view当全局状态。
            if(!Selected.Active)for(int i=0;i<_flights.Length;i++)if(_flights[i].Active){_selectedFlight=i;break;}
            return false;
        }
        public bool TryMarkPageSent(double now,int encodedBytes)
        {
            var flight=Selected;
            if(!flight.Active || flight.Cursor==Pages(flight) || !TryMarkControlSent(now,encodedBytes))return false;
            flight.Cursor++;flight.LastSend=now;
            _nextSendFlight=_selectedFlight+1;if(_nextSendFlight==_flights.Length)_nextSendFlight=0;
            return true;
        }
        /// <summary>订阅控制与材料页共用同一字节/突发/每轮预算；控制不是绕过限流的额外可靠流。</summary>
        public bool TryMarkControlSent(double now,int encodedBytes)
        {
            if(PagesSentThisRound>=MaximumPagesPerRound || encodedBytes<1 || !double.IsFinite(now) || now<_lastRefill || _tokens<encodedBytes)return false;
            _tokens-=encodedBytes;PagesSentThisRound++;return true;
        }
        public bool TryAcknowledge(int connectionId,uint epoch,WorldChunkKey key,ulong generation,ulong revision,ulong snapshotId,uint checksum)
        {
            if(connectionId!=ConnectionId || epoch!=ConnectionEpoch)return false;
            int pending=FindFlight(key);if(pending<0)return false;
            var flight=_flights[pending];if(flight.Cursor!=Pages(flight))return false;
            var h=flight.Header;
            if(key!=h.Key || generation!=h.Generation || revision!=h.Revision || snapshotId!=h.SnapshotId || checksum!=h.Checksum)return false;
            int slot=Find(key);if(slot<0){if(_slots==_keys.Length)return false;slot=_slots++;_keys[slot]=key;_indices.Add(key,slot);}
            _generations[slot]=generation;_revisions[slot]=revision;ReleaseFlight(pending);return true;
        }
        public bool RestartTimedOutTransfer(double now,double timeout)
        {
            if(!HasPending || !double.IsFinite(now) || !double.IsFinite(timeout) || timeout<=0)return false;
            bool restarted=false;
            for(int i=0;i<_flights.Length;i++)
            {
                var flight=_flights[i];if(!flight.Active || now<flight.LastSend || now-flight.LastSend<timeout)continue;
                // 只重启本Key同一份冻结快照；另一Key的新进展不能延期它，也不能被它一并重置。
                // 不补token、不重捕Current，重传仍受同一Peer下一轮页数/字节预算限制。
                flight.Cursor=0;flight.LastSend=now;if(!restarted)_selectedFlight=i;restarted=true;
            }
            return restarted;
        }
        public void RequireBaseline(WorldChunkKey key)
        {int slot=Find(key);if(slot>=0){_generations[slot]=0;_revisions[slot]=0;}int pending=FindFlight(key);if(pending>=0)ReleaseFlight(pending);}
        public bool HasPendingFor(WorldChunkKey key)=>FindFlight(key)>=0;
        public bool TryGetPendingHeader(WorldChunkKey key,out WorldChunkPayloadHeader header)
        {int slot=FindFlight(key);header=slot>=0?_flights[slot].Header:default;return slot>=0;}
        public bool IsAwaitingInstallAck(WorldChunkKey key)
        {int slot=FindFlight(key);return slot>=0 && _flights[slot].Cursor==Pages(_flights[slot]);}
        /// <summary>物理槽固定在[0,MaximumInFlight)，取消/ACK不搬槽；只读枚举不会切换当前借用页。</summary>
        public bool TryGetPendingHeaderAt(int slot,out WorldChunkPayloadHeader header)
        {bool valid=(uint)slot<(uint)_flights.Length && _flights[slot].Active;header=valid?_flights[slot].Header:default;return valid;}
        /// <summary>仅在可信Coordinator确认旧订阅已关闭且无冻结引用后归还空地址，不能取消在途页来腾容量。</summary>
        public bool TryForgetReleasedKey(WorldChunkKey key)
        {
            if(HasPendingFor(key))return false;
            int slot=Find(key);if(slot<0)return true;
            int last=--_slots;
            _indices.Remove(key);
            if(slot!=last){_keys[slot]=_keys[last];_generations[slot]=_generations[last];_revisions[slot]=_revisions[last];_indices[_keys[slot]]=slot;}
            _keys[last]=default;_generations[last]=_revisions[last]=0;return true;
        }
        private void ReleaseFlight(int index)
        {_flights[index].Active=false;_flights[index].Cursor=0;_inFlightCount--;}
        private int FindFlight(WorldChunkKey key)
        {for(int i=0;i<_flights.Length;i++)if(_flights[i].Active && _flights[i].Header.Key==key)return i;return -1;}
        private int Find(WorldChunkKey key)=>_indices.TryGetValue(key,out int index)?index:-1;
    }
}
