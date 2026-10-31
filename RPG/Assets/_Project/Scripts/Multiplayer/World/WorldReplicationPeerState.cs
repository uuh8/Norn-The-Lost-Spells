using System;
using Game.Core;

namespace Game.Multiplayer
{
    /// <summary>
    /// 一个认证连接的有界发送状态。只有一块冻结payload，页全部进入可靠Transport后才等待安装ACK。
    /// 安装ACK不是Transport页收据，因此绝不能把“四页在途”误写成发四页后等待整块ACK。
    /// 本类限制入Mirror的bytes/s、burst及每轮两页；它不声称能限制Mirror/KCP内部所有队列或证明HOL。
    /// </summary>
    public sealed class WorldReplicationPeerState
    {
        private readonly uint[] _frozen;
        private readonly WorldChunkKey[] _keys;
        private readonly ulong[] _generations,_revisions;
        private readonly int _pageWords;
        private readonly double _bytesPerSecond,_burst;
        private double _tokens,_lastRefill=double.NaN,_lastSend;
        private int _slots;
        public int ConnectionId {get;private set;}
        public uint ConnectionEpoch {get;private set;}
        public bool HasPending {get;private set;}
        public WorldChunkPayloadHeader PendingHeader {get;private set;}
        public int PageCursor {get;private set;}
        public int PagesSentThisRound {get;private set;}
        public int PageCount=>PendingHeader.WordCount==0?1:1+(PendingHeader.WordCount-1)/_pageWords;
        public bool AwaitingInstallAck=>HasPending && PageCursor==PageCount;
        public WorldReplicationPeerState(int chunks,int maxWords,int pageWords,double bytesPerSecond,double burst)
        {
            if(chunks<=0 || maxWords<11 || pageWords<=0 || !double.IsFinite(bytesPerSecond) || bytesPerSecond<=0 || !double.IsFinite(burst) || burst<=0)throw new ArgumentOutOfRangeException(nameof(chunks));
            _keys=new WorldChunkKey[chunks];_generations=new ulong[chunks];_revisions=new ulong[chunks];_frozen=new uint[maxWords];
            _pageWords=pageWords;_bytesPerSecond=bytesPerSecond;_burst=burst;_tokens=burst;
        }
        public void Reset(int connectionId,uint epoch)
        {
            ConnectionId=connectionId;ConnectionEpoch=epoch;_slots=0;HasPending=false;PageCursor=0;PagesSentThisRound=0;
            _tokens=_burst;_lastRefill=double.NaN;PendingHeader=default;
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
            if(HasPending || owner==null || ConnectionEpoch==0 || !double.IsFinite(now))return false;
            if(!owner.TryCopyChunk(key,_frozen,out var current))return false;
            int slot=Find(key);
            if(slot>=0 && _generations[slot]==current.Generation && _revisions[slot]==current.Revision)return false;
            if(slot<0 && _slots==_keys.Length)return false;
            if(slot>=0 && _generations[slot]==current.Generation && _revisions[slot]!=0)
            {
                // 历史缺口明确退回当前Baseline；不是把一条缺Base的Delta当成合法覆盖。
                if(owner.TryCopyDelta(key,_generations[slot],_revisions[slot],_frozen,out var delta))current=delta;
                else if(!owner.TryCopyChunk(key,_frozen,out current))return false;
            }
            PendingHeader=current;PageCursor=0;HasPending=true;_lastSend=now;return true;
        }
        public bool TryPeekPage(uint[] destination,out int count,out int pageIndex)
        {
            count=0;pageIndex=PageCursor;
            if(!HasPending || AwaitingInstallAck || PagesSentThisRound>=2)return false;
            int offset=PageCursor*_pageWords;count=Math.Min(_pageWords,PendingHeader.WordCount-offset);
            if(destination==null || destination.Length<count){count=0;return false;}
            Array.Copy(_frozen,offset,destination,0,count);return true;
        }
        public bool TryMarkPageSent(double now,int encodedBytes)
        {
            if(!HasPending || AwaitingInstallAck || PagesSentThisRound>=2 || encodedBytes<1 || !double.IsFinite(now) || now<_lastRefill || _tokens<encodedBytes)return false;
            _tokens-=encodedBytes;PageCursor++;PagesSentThisRound++;_lastSend=now;return true;
        }
        public bool TryAcknowledge(int connectionId,uint epoch,WorldChunkKey key,ulong generation,ulong revision,ulong snapshotId,uint checksum)
        {
            if(connectionId!=ConnectionId || epoch!=ConnectionEpoch || !AwaitingInstallAck)return false;
            var h=PendingHeader;
            if(key!=h.Key || generation!=h.Generation || revision!=h.Revision || snapshotId!=h.SnapshotId || checksum!=h.Checksum)return false;
            int slot=Find(key);if(slot<0){if(_slots==_keys.Length)return false;slot=_slots++;_keys[slot]=key;}
            _generations[slot]=generation;_revisions[slot]=revision;HasPending=false;PageCursor=0;return true;
        }
        public bool RestartTimedOutTransfer(double now,double timeout)
        {
            if(!HasPending || !double.IsFinite(now) || timeout<=0 || now<_lastSend || now-_lastSend<timeout)return false;
            // 相同冻结快照重传，不能中途拼上更新后的Current。重试仍受下一轮token预算限制。
            PageCursor=0;_lastSend=now;return true;
        }
        public void RequireBaseline(WorldChunkKey key)
        {int slot=Find(key);if(slot>=0){_generations[slot]=0;_revisions[slot]=0;}if(HasPending && PendingHeader.Key==key){HasPending=false;PageCursor=0;}}
        private int Find(WorldChunkKey key){for(int i=0;i<_slots;i++)if(_keys[i]==key)return i;return -1;}
    }
}
