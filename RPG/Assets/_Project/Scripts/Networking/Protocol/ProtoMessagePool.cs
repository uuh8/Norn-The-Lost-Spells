using System;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>消息池的租借凭据；调用方必须在当前回调内使用并释放。</summary>
    public readonly struct ProtocolMessageLease : IDisposable
    {
        private readonly ProtoMessagePool _owner;
        private readonly int _index;
        private readonly ulong _generation;

        internal ProtocolMessageLease(ProtoMessagePool owner, int index, ulong generation)
        {
            _owner = owner;
            _index = index;
            _generation = generation;
        }

        // 代次检查可以阻止已释放 Lease 读到后面租借者的数据；不要缓存返回的消息引用。
        public IMessage Message => _owner == null
            ? throw new InvalidOperationException("无效消息租借。") : _owner.GetMessage(_index, _generation);
        public ProtocolPingRequest Request => (ProtocolPingRequest)Message;
        public ProtocolPingReply Reply => (ProtocolPingReply)Message;
        public void Dispose() => _owner?.Return(_index, _generation);
    }

    /// <summary>固定容量、主线程使用的消息池；容量满时拒绝，不在收包路径扩容。</summary>
    public sealed class ProtoMessagePool
    {
        private readonly ProtocolPingRequest[] _requests;
        private readonly ProtocolPingReply[] _replies;
        private readonly RealtimeMessageKind[] _kinds;
        private readonly bool[] _leased;
        private readonly ulong[] _generations;

        public ProtoMessagePool(int capacity)
        {
            if (capacity < 1 || capacity > 64) throw new ArgumentOutOfRangeException(nameof(capacity));
            _requests = new ProtocolPingRequest[capacity];
            _replies = new ProtocolPingReply[capacity];
            _kinds = new RealtimeMessageKind[capacity];
            _leased = new bool[capacity];
            _generations = new ulong[capacity];
            // 一次性初始化；运行期间租借只切换槽位，不为消息本体 new。
            for (int i = 0; i < capacity; i++)
            {
                _requests[i] = new ProtocolPingRequest();
                _replies[i] = new ProtocolPingReply();
            }
        }

        public bool TryRent(RealtimeMessageKind kind, out ProtocolMessageLease lease)
        {
            lease = default;
            if (kind != RealtimeMessageKind.PingRequest && kind != RealtimeMessageKind.PingReply) return false;
            for (int i = 0; i < _leased.Length; i++)
            {
                // 不让代次溢出回绕；极限情况下这个槽位停用，避免旧凭据重新变得有效。
                if (_leased[i] || _generations[i] == ulong.MaxValue) continue;
                _generations[i]++;
                _leased[i] = true;
                _kinds[i] = kind;
                lease = new ProtocolMessageLease(this, i, _generations[i]);
                return true;
            }
            return false;
        }

        internal IMessage GetMessage(int index, ulong generation)
        {
            if (!_leased[index] || _generations[index] != generation)
                throw new InvalidOperationException("消息租借已释放或过期。不要跨帧保留消息引用。");
            return _kinds[index] == RealtimeMessageKind.PingRequest ? (IMessage)_requests[index] : _replies[index];
        }

        internal void Return(int index, ulong generation)
        {
            // struct 会被复制，旧副本再次 Dispose 必须无害，不能释放新的租借。
            if (!_leased[index] || _generations[index] != generation) return;
            if (_kinds[index] == RealtimeMessageKind.PingRequest) _requests[index].ResetForReuse();
            else _replies[index].ResetForReuse();
            _kinds[index] = RealtimeMessageKind.Unspecified;
            _leased[index] = false;
        }
    }
}
