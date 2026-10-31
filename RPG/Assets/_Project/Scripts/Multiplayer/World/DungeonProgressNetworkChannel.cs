using System;
using Game.Core;
using Game.Networking;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 只承载Server到当前Run成员的稀疏进度事实。消息中的Run/Dungeon必须匹配独立的认证身份；
    /// 通道不启动遭遇、不计算死亡，也不把接收序号先写回领域以阻止更早的合法快照。
    /// </summary>
    [RequireComponent(typeof(RoomNetworkManager), typeof(JoinTicketAuthenticator))]
    public sealed class DungeonProgressNetworkChannel : MonoBehaviour
    {
        private RoomNetworkManager _manager;
        private JoinTicketAuthenticator _auth;
        private readonly DungeonProgressCodec _codec = new DungeonProgressCodec();
        private readonly byte[] _buffer = new byte[DungeonProgressCodec.MaxPayloadBytes];
        private string _run, _dungeon;
        private ulong _hash;
        private bool _registered;
        private JoinConnectionIdentity _clientIdentity;
        private readonly DungeonProgressInbox _inbox = new DungeonProgressInbox();
        public long Rejected { get; private set; }
        public long Sent { get; private set; }
        private void Awake()
        { _manager = GetComponent<RoomNetworkManager>(); _auth = GetComponent<JoinTicketAuthenticator>(); EnsureHandler(); }
        public void Configure(string runId, string dungeonId, ulong gameplayHash)
        {
            if (_run == runId && _dungeon == dungeonId && _hash == gameplayHash) return;
            _run = runId; _dungeon = dungeonId; _hash = gameplayHash;
            _inbox.Clear(); _clientIdentity = null;
        }
        private void EnsureHandler()
        {
            if (!NetworkClient.active || _registered) return;
            NetworkClient.RegisterHandler<DungeonProgressEnvelope>(OnProgress, true); _registered = true;
        }
        private void Update()
        {
            EnsureHandler();
            if (!NetworkClient.active) Clear();
        }
        public bool SendToOwner(JoinConnectionIdentity identity, in OnlineDungeonProgress progress)
        {
            if (!NetworkServer.active || identity == null || identity.RunId != _run || identity.DungeonId != _dungeon || _manager == null) return false;
            var connection = _manager.ActorConnection((int)identity.ActorId - 1);
            if (!_manager.IsCurrentActorConnection(connection, identity) ||
                !_codec.TryEncode(progress, _run, _dungeon, _hash, new ArraySegment<byte>(_buffer), out int count)) return false;
            connection.Send(new DungeonProgressEnvelope { Payload = new ArraySegment<byte>(_buffer, 0, count) }, Channels.Reliable); Sent++; return true;
        }
        private void OnProgress(DungeonProgressEnvelope message)
        {
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            if (identity == null || identity.RunId != _run || identity.DungeonId != _dungeon ||
                !_codec.TryDecode(message.Payload, identity.RunId, identity.DungeonId, _hash, out var progress)) { Rejected++; return; }
            if (!ReferenceEquals(_clientIdentity, identity)) { _clientIdentity = identity; _inbox.Clear(); }
            // Wire合法不代表这个区域人数/阶段也合法；不能用未经Runtime审核的高Revision
            // 替换同帧较早的候选。固定队列按到达顺序交给唯一领域Owner，拒绝时不消耗合法版本。
            if (!_inbox.TryEnqueue(progress)) Rejected++;
        }
        public bool TryTake(out OnlineDungeonProgress progress)
        {
            progress = default;
            if (_auth == null || !NetworkClient.isConnected || !_auth.ClientAuthenticated ||
                !ReferenceEquals(_clientIdentity, _auth.ClientIdentity)) return false;
            return _inbox.TryDequeue(out progress);
        }
        private void Clear()
        {
            if (_registered) NetworkClient.UnregisterHandler<DungeonProgressEnvelope>();
            _registered = false; _inbox.Clear(); _clientIdentity = null;
        }
        private void OnDisable() => Clear();
    }

    /// <summary>
    /// 稀疏进度的固定容量候选队列，不拥有“已接受Revision”。每次World.Update最多消费8项，
    /// 满时拒绝新候选而不覆盖旧值；身份更换/退出则清空。数组只在Channel创建时分配一次。
    /// </summary>
    internal sealed class DungeonProgressInbox
    {
        private readonly OnlineDungeonProgress[] _values = new OnlineDungeonProgress[8];
        private int _head, _count;
        public bool TryEnqueue(OnlineDungeonProgress value)
        {
            if (_count == _values.Length) return false;
            _values[(_head + _count) % _values.Length] = value; _count++; return true;
        }
        public bool TryDequeue(out OnlineDungeonProgress value)
        {
            value = default; if (_count == 0) return false;
            value = _values[_head]; _values[_head] = default;
            _head = (_head + 1) % _values.Length; _count--; return true;
        }
        public void Clear() { _head = _count = 0; Array.Clear(_values, 0, _values.Length); }
    }
}
