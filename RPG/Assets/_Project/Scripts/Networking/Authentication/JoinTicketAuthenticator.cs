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
    /// <summary>
    /// 只存服务器确认的连接身份；DungeonId 来自业务兑换并经过两端比对，不接受本地 Portal 作为替代。
    /// 数据在构造后只读，没有角色控制或资产修改方法；世界组合只能消费此身份，不能自行换图。
    /// </summary>
    public sealed class JoinConnectionIdentity
    {
        public readonly string PlayerId, RoomId, RunId, DungeonId;
        public readonly uint RoomEpoch, ConnectionEpoch;
        public readonly ulong ActorId;
        public JoinConnectionIdentity(string player, string room, string run, uint epoch, uint connection, ulong actor, string dungeon)
        { PlayerId = player; RoomId = room; RunId = run; DungeonId = dungeon; RoomEpoch = epoch; ConnectionEpoch = connection; ActorId = actor; }
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
        private string _secret, _ticket;
        private JoinTicketReply _expected;
        private ulong _nonce;
        private readonly WorldIdentityCodec _worldIdentityCodec = new WorldIdentityCodec();
        private double _worldIdentityDeadline;
        public bool Frozen { get; set; }
        public bool ClientAuthenticated { get; private set; }
        public bool HandshakeAttempted { get; private set; }
        public JoinConnectionIdentity ClientIdentity { get; private set; }
        /// <summary>仅当前认证上下文安装的DS世界身份；未收到独立握手时为0，不能据此配置材料世界。</summary>
        public ulong ClientWorldEpoch { get; private set; }
        public long Accepted { get; private set; }
        public long Rejected { get; private set; }
        public void ConfigureServer(InstanceLaunchDescriptor launch, string secret)
        {
            if (launch == null || !DungeonIdentityRules.IsValidId(launch.DungeonId))
                throw new ArgumentException("受信启动描述缺少有效副本身份。");
            _launch = launch; _secret = secret;
        }
        public void ConfigureClient(JoinTicketReply ticket)
        {
            if (ticket == null || ticket.Host != "127.0.0.1" || ticket.Ticket == null || ticket.Ticket.Length != 64 || !DungeonIdentityRules.IsValidId(ticket.DungeonId))
                throw new ArgumentException("本阶段只允许含有效副本身份的 loopback Ticket。");
            _ticket = ticket.Ticket;
            ClientWorldEpoch = 0; _worldIdentityDeadline = 0;
            // 复制期望身份，避免外部随后复用/修改可变 DTO 影响在途握手；副本不保留 Ticket 密钥。
            _expected = new JoinTicketReply { PlayerId = ticket.PlayerId, RoomId = ticket.RoomId,
                RunId = ticket.RunId, RoomEpoch = ticket.RoomEpoch, DungeonId = ticket.DungeonId };
            _nonce = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0); if (_nonce == 0) _nonce = 1;
        }
        public override void OnStartServer() => NetworkServer.RegisterHandler<JoinTicketRequestEnvelope>(OnTicket, false);
        public override void OnStartClient()
        {
            NetworkClient.RegisterHandler<JoinTicketReplyEnvelope>(OnReply, false);
            // 预注册于准入前，但Mirror要求认证后才接收世界身份。不能等在线Scene加载后注册，
            // 因为Server在JoinReply之后可立即发送同一可靠通道的世界握手。
            NetworkClient.RegisterHandler<WorldIdentityEnvelope>(OnWorldIdentity, true);
        }
        public override void OnServerAuthenticate(NetworkConnectionToClient connection)
        {
            if (Frozen || _launch == null || !IPAddress.TryParse(connection.address, out var ip) || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)) { Reject(connection); return; }
            for (int i = 0; i < _pending.Length; i++) if (_pending[i] == null) { _pending[i] = connection; _deadlines[i] = Time.realtimeSinceStartupAsDouble + 10; return; }
            Reject(connection);
        }
        private void Update()
        {
            // 只约束尚未完成的离散世界握手，不产生玩法时间；旧协议已由v3门禁拒绝。
            if (ClientAuthenticated && ClientWorldEpoch == 0 && _worldIdentityDeadline > 0
                && Time.realtimeSinceStartupAsDouble > _worldIdentityDeadline)
            { _worldIdentityDeadline = 0; ClientReject(); }
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
                if (http.result != UnityWebRequest.Result.Success || !TryAdmission(http.downloadHandler.text, out var reply) ||
                    !JoinAdmissionRules.AcceptsServerReply(reply, _launch, request.PlayerId) || !_epochs.TryAdvance(reply.ActorId, reply.ConnectionEpoch))
                { Reject(connection); yield break; }
                int actor = (int)reply.ActorId - 1;
                var previous = _actors[actor]; _actors[actor] = connection;
                if (previous != null && !ReferenceEquals(previous, connection)) previous.Disconnect();
                var identity = new JoinConnectionIdentity(reply.PlayerId, reply.RoomId, reply.RunId, reply.RoomEpoch, reply.ConnectionEpoch, reply.ActorId, reply.DungeonId);
                connection.authenticationData = identity;
                var response = new JoinTicketReplyMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion, Nonce = request.Nonce, PlayerId = identity.PlayerId,
                    RoomId = identity.RoomId, RunId = identity.RunId, DungeonId = identity.DungeonId,
                    RoomEpoch = identity.RoomEpoch, ConnectionEpoch = identity.ConnectionEpoch, ActorId = identity.ActorId };
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
            if (_ticket == null || _expected == null) { ClientReject(); return; }
            var request = new JoinTicketRequest { ProtocolVersion = ProtocolCompatibility.CurrentVersion, Ticket = _ticket, PlayerId = _expected.PlayerId, Nonce = _nonce };
            int count = request.CalculateSize(); request.WriteTo(new Span<byte>(_buffer, 0, count));
            NetworkClient.Send(new JoinTicketRequestEnvelope { Payload = new ArraySegment<byte>(_buffer, 0, count) }); _ticket = null;
        }
        private void OnReply(JoinTicketReplyEnvelope envelope)
        {
            JoinTicketReplyMessage reply;
            try { reply = JoinTicketReplyMessage.Parser.ParseFrom(new ReadOnlySpan<byte>(envelope.Payload.Array, envelope.Payload.Offset, envelope.Payload.Count)); }
            catch (InvalidProtocolBufferException) { ClientReject(); return; }
            if (!JoinAdmissionRules.AcceptsClientReply(reply, _expected, _nonce)) { ClientReject(); return; }
            ClientIdentity = new JoinConnectionIdentity(reply.PlayerId, reply.RoomId, reply.RunId, reply.RoomEpoch, reply.ConnectionEpoch, reply.ActorId, reply.DungeonId);
            ClientWorldEpoch = 0; _worldIdentityDeadline = Time.realtimeSinceStartupAsDouble + 10;
            NetworkClient.connection.authenticationData = ClientIdentity; ClientAuthenticated = true; ClientAccept();
        }
        /// <summary>
        /// 由真实World Owner在ServerActorJoined之后调用。只给当前已认证Actor连接发送元数据，
        /// 仍沿原可靠有序通道位于JoinReply之后；不消费Ticket、不生成第二个Gameplay Owner。
        /// </summary>
        public bool SendWorldIdentity(NetworkConnectionToClient connection, JoinConnectionIdentity identity, ulong worldEpoch)
        {
            if (connection == null || !connection.isAuthenticated || identity == null || _launch == null
                || identity.ActorId < 1 || identity.ActorId > 4 || !ReferenceEquals(connection.authenticationData, identity)
                || !ReferenceEquals(_actors[(int)identity.ActorId - 1], connection)
                || identity.RunId != _launch.RunId || identity.DungeonId != _launch.DungeonId || identity.RoomEpoch != _launch.RoomEpoch
                || !_worldIdentityCodec.TryEncode(identity, worldEpoch, new ArraySegment<byte>(_buffer), out int count)) return false;
            connection.Send(new WorldIdentityEnvelope { Payload = new ArraySegment<byte>(_buffer, 0, count) }, Channels.Reliable);
            return true;
        }
        private void OnWorldIdentity(WorldIdentityEnvelope envelope)
        {
            var connection = NetworkClient.connection;
            if (!ClientAuthenticated || ClientIdentity == null || connection == null || !connection.isAuthenticated
                || !ReferenceEquals(connection.authenticationData, ClientIdentity)
                || !_worldIdentityCodec.TryDecode(envelope.Payload, ClientIdentity, out ulong epoch)
                || ClientWorldEpoch != 0 && ClientWorldEpoch != epoch)
            { Rejected++; return; }
            // 重复同值幂等；任意不同值都不能热换存活世界。新认证上下文由原OnReply重新建立。
            ClientWorldEpoch = epoch; _worldIdentityDeadline = 0;
        }
        public override void OnStopServer()
        {
            NetworkServer.UnregisterHandler<JoinTicketRequestEnvelope>();
            for (int i = 0; i < _requests.Length; i++) { var request = _requests[i]; _requests[i] = null; request?.Abort(); request?.Dispose(); }
            StopAllCoroutines(); Array.Clear(_pending, 0, _pending.Length); Array.Clear(_actors, 0, _actors.Length); _epochs.Clear(); _secret = null; _launch = null;
        }
        public override void OnStopClient()
        {
            NetworkClient.UnregisterHandler<JoinTicketReplyEnvelope>(); _ticket = null; _expected = null; _nonce = 0;
            NetworkClient.UnregisterHandler<WorldIdentityEnvelope>(); ClientWorldEpoch = 0; _worldIdentityDeadline = 0;
            ClientAuthenticated = false; ClientIdentity = null;
        }
    }
}
