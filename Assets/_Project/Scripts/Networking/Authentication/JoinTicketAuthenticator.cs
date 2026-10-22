using System;
using System.Collections;
using System.Net;
using Game.NetworkProtocol;
using Google.Protobuf;
using Mirror;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Networking
{
    /// <summary>只存服务器确认的连接身份；没有角色控制或资产修改方法。</summary>
    public sealed class JoinConnectionIdentity
    {
        public readonly string PlayerId, RoomId, RunId;
        public readonly uint RoomEpoch, ConnectionEpoch;
        public readonly ulong ActorId;
        public JoinConnectionIdentity(string player, string room, string run, uint epoch, uint connection, ulong actor)
        { PlayerId = player; RoomId = room; RunId = run; RoomEpoch = epoch; ConnectionEpoch = connection; ActorId = actor; }
    }

    /// <summary>Mirror只负责连接；Ticket由业务服原子兑换，通过之前只接受有界握手，不接Gameplay。</summary>
    public sealed class JoinTicketAuthenticator : NetworkAuthenticator
    {
        private readonly NetworkConnectionToClient[] _pending = new NetworkConnectionToClient[4], _actors = new NetworkConnectionToClient[4];
        private readonly UnityWebRequest[] _requests = new UnityWebRequest[4];
        private readonly double[] _deadlines = new double[4];
        private readonly byte[] _buffer = new byte[512];
        private readonly JoinConnectionEpochs _epochs = new JoinConnectionEpochs();
        private InstanceLaunchDescriptor _launch;
        private string _secret, _ticket, _player, _room, _run;
        private uint _roomEpoch;
        private ulong _nonce;
        public bool Frozen { get; set; }
        public bool ClientAuthenticated { get; private set; }
        public bool HandshakeAttempted { get; private set; }
        public JoinConnectionIdentity ClientIdentity { get; private set; }
        public long Accepted { get; private set; }
        public long Rejected { get; private set; }
        public void ConfigureServer(InstanceLaunchDescriptor launch, string secret) { _launch = launch; _secret = secret; }
        public void ConfigureClient(JoinTicketReply ticket)
        {
            if (ticket == null || ticket.Host != "127.0.0.1" || ticket.Ticket == null || ticket.Ticket.Length != 64) throw new ArgumentException("本阶段只允许loopback Ticket。");
            _ticket = ticket.Ticket; _player = ticket.PlayerId; _room = ticket.RoomId; _run = ticket.RunId; _roomEpoch = ticket.RoomEpoch;
            _nonce = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0); if (_nonce == 0) _nonce = 1;
        }
        public override void OnStartServer() => NetworkServer.RegisterHandler<JoinTicketRequestEnvelope>(OnTicket, false);
        public override void OnStartClient() => NetworkClient.RegisterHandler<JoinTicketReplyEnvelope>(OnReply, false);
        public override void OnServerAuthenticate(NetworkConnectionToClient connection)
        {
            if (Frozen || _launch == null || !IPAddress.TryParse(connection.address, out var ip) || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)) { Reject(connection); return; }
            for (int i = 0; i < _pending.Length; i++) if (_pending[i] == null) { _pending[i] = connection; _deadlines[i] = Time.realtimeSinceStartupAsDouble + 10; return; }
            Reject(connection);
        }
        private void Update()
        {
            if (!NetworkServer.active) return;
            for (int i = 0; i < _pending.Length; i++)
            {
                var connection = _pending[i]; if (connection == null) continue;
                if (!NetworkServer.connections.TryGetValue(connection.connectionId, out var current) || !ReferenceEquals(connection, current)) { _pending[i] = null; _requests[i]?.Abort(); continue; }
                if (Time.realtimeSinceStartupAsDouble <= _deadlines[i]) continue;
                _pending[i] = null; _requests[i]?.Abort(); Reject(connection);
            }
        }
        private void OnTicket(NetworkConnectionToClient connection, JoinTicketRequestEnvelope envelope)
        {
            int slot = -1; for (int i = 0; i < _pending.Length; i++) if (ReferenceEquals(_pending[i], connection)) { slot = i; break; }
            if (slot < 0 || _requests[slot] != null || Frozen) { Reject(connection); return; }
            JoinTicketRequest request;
            try { request = JoinTicketRequest.Parser.ParseFrom(new ReadOnlySpan<byte>(envelope.Payload.Array, envelope.Payload.Offset, envelope.Payload.Count)); }
            catch (InvalidProtocolBufferException) { _pending[slot] = null; Reject(connection); return; }
            if (!ProtocolCompatibility.IsSupported(request.ProtocolVersion) || request.Ticket.Length != 64 || request.PlayerId.Length != 32 || request.Nonce == 0) { _pending[slot] = null; Reject(connection); return; }
            StartCoroutine(Consume(slot, connection, request)); // 握手是离散分配路径，不在每Tick创建Coroutine/JSON。
        }
        private IEnumerator Consume(int slot, NetworkConnectionToClient connection, JoinTicketRequest request)
        {
            var http = InstanceControlHttp.Create(_launch, _secret, "admit", JsonUtility.ToJson(new ConsumeJoinTicketRequest { Ticket = request.Ticket, PlayerId = request.PlayerId, RoomEpoch = _launch.RoomEpoch }));
            _requests[slot] = http;
            try
            {
                yield return http.SendWebRequest();
                if (!ReferenceEquals(_pending[slot], connection) || !NetworkServer.connections.TryGetValue(connection.connectionId, out var current) || !ReferenceEquals(connection, current)) yield break;
                // HTTP在途时可能进入Frozen；仍存活的未认证连接要明确拒绝，不能只释放Pending而漏掉断开。
                if (Frozen) { Reject(connection); yield break; }
                if (http.result != UnityWebRequest.Result.Success || !TryAdmission(http.downloadHandler.text, out var reply) || reply.PlayerId != request.PlayerId || reply.RunId != _launch.RunId || reply.RoomId != _launch.RoomId || reply.RoomEpoch != _launch.RoomEpoch || !_epochs.TryAdvance(reply.ActorId, reply.ConnectionEpoch))
                { Reject(connection); yield break; }
                int actor = (int)reply.ActorId - 1;
                var previous = _actors[actor]; _actors[actor] = connection;
                if (previous != null && !ReferenceEquals(previous, connection)) previous.Disconnect();
                var identity = new JoinConnectionIdentity(reply.PlayerId, reply.RoomId, reply.RunId, reply.RoomEpoch, reply.ConnectionEpoch, reply.ActorId);
                connection.authenticationData = identity;
                var response = new JoinTicketReplyMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion, Nonce = request.Nonce, PlayerId = identity.PlayerId,
                    RoomId = identity.RoomId, RunId = identity.RunId, RoomEpoch = identity.RoomEpoch, ConnectionEpoch = identity.ConnectionEpoch, ActorId = identity.ActorId };
                int count = response.CalculateSize(); response.WriteTo(new Span<byte>(_buffer, 0, count));
                connection.Send(new JoinTicketReplyEnvelope { Payload = new ArraySegment<byte>(_buffer, 0, count) }); Accepted++; ServerAccept(connection);
            }
            finally
            {
                if (ReferenceEquals(_pending[slot], connection)) _pending[slot] = null;
                if (ReferenceEquals(_requests[slot], http)) { _requests[slot] = null; http.Dispose(); }
            }
        }
        private static bool TryAdmission(string json, out ConsumeJoinTicketReply reply)
        { try { reply = JsonUtility.FromJson<ConsumeJoinTicketReply>(json); return reply != null; } catch (ArgumentException) { reply = null; return false; } }
        private void Reject(NetworkConnectionToClient connection) { Rejected++; ServerReject(connection); }
        public override void OnClientAuthenticate()
        {
            HandshakeAttempted = true;
            if (_ticket == null) { ClientReject(); return; }
            var request = new JoinTicketRequest { ProtocolVersion = ProtocolCompatibility.CurrentVersion, Ticket = _ticket, PlayerId = _player, Nonce = _nonce };
            int count = request.CalculateSize(); request.WriteTo(new Span<byte>(_buffer, 0, count));
            NetworkClient.Send(new JoinTicketRequestEnvelope { Payload = new ArraySegment<byte>(_buffer, 0, count) }); _ticket = null;
        }
        private void OnReply(JoinTicketReplyEnvelope envelope)
        {
            JoinTicketReplyMessage reply;
            try { reply = JoinTicketReplyMessage.Parser.ParseFrom(new ReadOnlySpan<byte>(envelope.Payload.Array, envelope.Payload.Offset, envelope.Payload.Count)); }
            catch (InvalidProtocolBufferException) { ClientReject(); return; }
            if (!ProtocolCompatibility.IsSupported(reply.ProtocolVersion) || reply.Nonce != _nonce || reply.PlayerId != _player || reply.RunId != _run || reply.RoomId != _room || reply.RoomEpoch != _roomEpoch || reply.ActorId < 1 || reply.ActorId > 4 || reply.ConnectionEpoch == 0) { ClientReject(); return; }
            ClientIdentity = new JoinConnectionIdentity(reply.PlayerId, reply.RoomId, reply.RunId, reply.RoomEpoch, reply.ConnectionEpoch, reply.ActorId);
            NetworkClient.connection.authenticationData = ClientIdentity; ClientAuthenticated = true; ClientAccept();
        }
        public override void OnStopServer()
        {
            NetworkServer.UnregisterHandler<JoinTicketRequestEnvelope>();
            for (int i = 0; i < _requests.Length; i++) { var request = _requests[i]; _requests[i] = null; request?.Abort(); request?.Dispose(); }
            StopAllCoroutines(); Array.Clear(_pending, 0, _pending.Length); Array.Clear(_actors, 0, _actors.Length); _epochs.Clear(); _secret = null;
        }
        public override void OnStopClient() { NetworkClient.UnregisterHandler<JoinTicketReplyEnvelope>(); _ticket = null; ClientAuthenticated = false; ClientIdentity = null; }
    }
}
