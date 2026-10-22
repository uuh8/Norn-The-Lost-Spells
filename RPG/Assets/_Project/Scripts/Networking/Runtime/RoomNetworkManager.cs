using System;
using System.Net;
using Game.Core;
using Game.NetworkProtocol;
using kcp2k;
using Mirror;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>正式账户准入的连接适配，不生成Gameplay角色；N3在其上装配权威模拟。N0 Manager不受影响。</summary>
    [RequireComponent(typeof(KcpTransport))]
    public sealed class RoomNetworkManager : NetworkManager
    {
        private readonly ProtocolPingRequest _request = new ProtocolPingRequest();
        private readonly ProtocolPingReply _reply = new ProtocolPingReply();
        private readonly byte[] _buffer = new byte[1024];
        private readonly ConnectionSession[] _sessions = new ConnectionSession[4];
        private ProtoMessageCodec _codec;
        private double _nextPing;
        private ulong _sequence;
        public long Replies { get; private set; }
        public long Served { get; private set; }
        public int AuthenticatedConnections { get; private set; }
        public bool Frozen { get; private set; }
        private bool _authorityReady;
        public bool CanSimulate => _authorityReady && NetworkServer.active && !Frozen; // N3不能在业务Ready之前提前Tick。
        public void SetAuthorityReady(bool ready) => _authorityReady = ready;
        public override void Awake()
        {
            transport = GetComponent<KcpTransport>(); authenticator = GetComponent<JoinTicketAuthenticator>(); autoCreatePlayer = false;
            // 正式房间用UGUI；空OnGUI仍会先进入GUILayout.Begin，关闭两个组件的自动布局消除稳态分配。
            useGUILayout = false; transport.useGUILayout = false;
            timeInterpolationGui = false; GetComponent<KcpTransport>().statisticsGUI = false;
            headlessStartMode = HeadlessStartOptions.DoNothing; editorAutoStart = false;
            _codec = new ProtoMessageCodec(new ProtoMessagePool(8));
            for (int i = 0; i < _sessions.Length; i++) _sessions[i] = new ConnectionSession(10, 4);
            base.Awake();
        }
        public void Configure(int port)
        {
            if (port < 1024 || port > 65535 || NetworkServer.active || NetworkClient.active) throw new ArgumentOutOfRangeException(nameof(port));
            var kcp = GetComponent<KcpTransport>(); kcp.Port = (ushort)port; kcp.DualMode = false;
            networkAddress = "127.0.0.1"; maxConnections = 8; sendRate = 30;
            // 4个已认证席位之外暂留4个握手位置，以便满员时重连替换旧连接；不是允许8人入局。
        }
        public void SetFrozen(bool frozen) { Frozen = frozen; GetComponent<JoinTicketAuthenticator>().Frozen = frozen; }
        public override void OnStartServer() => NetworkServer.RegisterHandler<RoomProtocolEnvelope>(OnPing, true);
        public override void OnStartClient() => NetworkClient.RegisterHandler<RoomProtocolEnvelope>(OnReply, true);
        public override void OnServerConnect(NetworkConnectionToClient connection)
        {
            var identity = connection.authenticationData as JoinConnectionIdentity;
            if (identity == null || !IPAddress.TryParse(connection.address, out var ip) || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)) { connection.Disconnect(); return; }
            _sessions[(int)identity.ActorId - 1].Attach(connection.connectionId); AuthenticatedConnections++;
            GameLog.Info("副本账户准入成功。", "OnlineRooms");
        }
        public override void OnServerDisconnect(NetworkConnectionToClient connection)
        {
            if (connection.authenticationData is JoinConnectionIdentity) AuthenticatedConnections--;
            for (int i = 0; i < _sessions.Length; i++) if (_sessions[i].ConnectionId == connection.connectionId) _sessions[i].Clear();
            base.OnServerDisconnect(connection);
        }
        public override void Update()
        {
            base.Update(); // 保留Mirror本身的Scene/连接生命周期处理，不能用同名Unity回调遮蔽基类。
            if (!NetworkClient.isConnected || NetworkClient.connection == null || !NetworkClient.connection.isAuthenticated || Time.realtimeSinceStartupAsDouble < _nextPing) return;
            _nextPing = Time.realtimeSinceStartupAsDouble + 1; _request.ProtocolVersion = ProtocolCompatibility.CurrentVersion; _request.Sequence = ++_sequence; _request.Message = "";
            if (_codec.TryEncode(RealtimeMessageKind.PingRequest, _request, new ArraySegment<byte>(_buffer), out int size))
                NetworkClient.Send(new RoomProtocolEnvelope { Kind = RealtimeMessageKind.PingRequest, Payload = new ArraySegment<byte>(_buffer, 0, size) });
        }
        private void OnPing(NetworkConnectionToClient connection, RoomProtocolEnvelope envelope)
        {
            if (envelope.Kind != RealtimeMessageKind.PingRequest || !_codec.TryDecode(envelope.Kind, envelope.Payload, out var lease, out _)) { connection.Disconnect(); return; }
            using (lease)
            {
                var identity = connection.authenticationData as JoinConnectionIdentity;
                if (identity == null || !_sessions[(int)identity.ActorId - 1].TryAccept(connection.connectionId, lease.Request.Sequence, Time.realtimeSinceStartupAsDouble)) { connection.Disconnect(); return; }
                _reply.ProtocolVersion = ProtocolCompatibility.CurrentVersion; _reply.Sequence = lease.Request.Sequence; _reply.Message = "";
                if (!_codec.TryEncode(RealtimeMessageKind.PingReply, _reply, new ArraySegment<byte>(_buffer), out int size)) return;
                connection.Send(new RoomProtocolEnvelope { Kind = RealtimeMessageKind.PingReply, Payload = new ArraySegment<byte>(_buffer, 0, size) }); Served++;
            }
        }
        private void OnReply(RoomProtocolEnvelope envelope)
        {
            if (envelope.Kind != RealtimeMessageKind.PingReply || !_codec.TryDecode(envelope.Kind, envelope.Payload, out var lease, out _)) { NetworkClient.Disconnect(); return; }
            using (lease) { if (lease.Reply.Sequence > _sequence) { NetworkClient.Disconnect(); return; } Replies++; }
        }
        public override void OnStopServer() { _authorityReady = false; NetworkServer.UnregisterHandler<RoomProtocolEnvelope>(); for (int i = 0; i < _sessions.Length; i++) _sessions[i].Clear(); }
        public override void OnStopClient() => NetworkClient.UnregisterHandler<RoomProtocolEnvelope>();
    }
}
