using System;
using System.IO;
using System.Net;
using Game.NetworkProtocol;
using Google.Protobuf;
using Mirror;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>N0 的临时认证上下文；由服务器创建，绝不是持久化账号或客户端自报的身份。</summary>
    public sealed class LocalLabIdentity
    {
        public readonly ulong ActorId;
        public LocalLabIdentity(ulong actorId) => ActorId = actorId;
    }

    public struct LocalLabAuthRequestEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public struct LocalLabAuthReplyEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }
    public struct LocalLabSnapshotEnvelope : NetworkMessage { public ArraySegment<byte> Payload; }

    /// <summary>外壳先限长再借用接收段；Snapshot 最多 64 bytes，认证边界最多 256 bytes。</summary>
    public static class LocalLabEnvelopeSerialization
    {
        public static void WriteLocalLabAuthRequestEnvelope(this NetworkWriter w, LocalLabAuthRequestEnvelope m) => Write(w, m.Payload, 256);
        public static void WriteLocalLabAuthReplyEnvelope(this NetworkWriter w, LocalLabAuthReplyEnvelope m) => Write(w, m.Payload, 256);
        public static void WriteLocalLabSnapshotEnvelope(this NetworkWriter w, LocalLabSnapshotEnvelope m) => Write(w, m.Payload, 64);
        public static LocalLabAuthRequestEnvelope ReadLocalLabAuthRequestEnvelope(this NetworkReader r) => new LocalLabAuthRequestEnvelope { Payload = Read(r, 256) };
        public static LocalLabAuthReplyEnvelope ReadLocalLabAuthReplyEnvelope(this NetworkReader r) => new LocalLabAuthReplyEnvelope { Payload = Read(r, 256) };
        public static LocalLabSnapshotEnvelope ReadLocalLabSnapshotEnvelope(this NetworkReader r) => new LocalLabSnapshotEnvelope { Payload = Read(r, 64) };
        private static void Write(NetworkWriter w, ArraySegment<byte> p, int max)
        {
            if (p.Array == null || p.Count < 1 || p.Count > max) throw new InvalidDataException("实验消息长度越界。");
            w.WriteUInt((uint)p.Count); w.WriteBytes(p.Array, p.Offset, p.Count);
        }
        private static ArraySegment<byte> Read(NetworkReader r, int max)
        {
            if (r.Remaining < 4) throw new InvalidDataException("实验消息头截断。");
            uint count = r.ReadUInt();
            if (count < 1 || count > max || count > r.Remaining) throw new InvalidDataException("实验消息声明长度越界。");
            return r.ReadBytesSegment((int)count);
        }
    }

    /// <summary>本机一次实验的短期认证，不替代 N1 账号或 N2 正式 JoinTicket；凭据不写日志。</summary>
    public sealed class LocalLabAuthenticator : NetworkAuthenticator
    {
        private readonly NetworkConnectionToClient[] _pending = new NetworkConnectionToClient[4];
        private readonly double[] _deadlines = new double[4];
        private readonly ulong[] _usedNonces = new ulong[8];
        private readonly byte[] _sendBuffer = new byte[256];
        private byte[] _credential;
        private string _fault;
        private ulong _nonce, _nextActorId;
        private int _usedNonceCount;
        private double _expiresAt;
        public bool HandshakeAttempted { get; private set; }
        public bool ClientAuthenticated { get; private set; }
        public ulong ClientActorId { get; private set; }
        public long RejectedVersions { get; private set; }
        public long RejectedCredentials { get; private set; }

        public void Configure(byte[] credential, string fault)
        {
            if (credential == null || credential.Length != 32) throw new ArgumentException("实验凭据必须恰好 32 bytes。");
            _credential = credential;
            _fault = fault;
            _expiresAt = Time.realtimeSinceStartupAsDouble + 60;
            _nonce = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
            if (_nonce == 0) _nonce = 1;
        }
        public override void OnStartServer() => NetworkServer.RegisterHandler<LocalLabAuthRequestEnvelope>(OnRequest, false);
        public override void OnStopServer()
        {
            NetworkServer.UnregisterHandler<LocalLabAuthRequestEnvelope>();
            Array.Clear(_pending, 0, _pending.Length);
        }
        public override void OnServerAuthenticate(NetworkConnectionToClient conn)
        {
            if (!IPAddress.TryParse(conn.address, out IPAddress address) || !IPAddress.IsLoopback(address))
            { ServerReject(conn); return; }
            for (int i = 0; i < _pending.Length; i++)
            {
                if (_pending[i] != null) continue;
                _pending[i] = conn; _deadlines[i] = Time.realtimeSinceStartupAsDouble + 5; return;
            }
            ServerReject(conn);
        }
        private void Update()
        {
            if (!NetworkServer.active) return;
            for (int i = 0; i < _pending.Length; i++)
            {
                NetworkConnectionToClient conn = _pending[i];
                if (conn == null) continue;
                if (!NetworkServer.connections.TryGetValue(conn.connectionId, out NetworkConnectionToClient current) || !ReferenceEquals(conn, current))
                { _pending[i] = null; continue; }
                if (Time.realtimeSinceStartupAsDouble <= _deadlines[i]) continue;
                _pending[i] = null; ServerReject(conn);
            }
        }
        private void OnRequest(NetworkConnectionToClient conn, LocalLabAuthRequestEnvelope envelope)
        {
            int slot = -1;
            for (int i = 0; i < _pending.Length; i++) if (ReferenceEquals(_pending[i], conn)) { slot = i; break; }
            if (slot < 0 || conn.isAuthenticated || Time.realtimeSinceStartupAsDouble > _expiresAt ||
                Time.realtimeSinceStartupAsDouble > _deadlines[slot]) { ServerReject(conn); return; }
            try
            {
                var request = LocalLabAuthRequest.Parser.ParseFrom(new ReadOnlySpan<byte>(envelope.Payload.Array, envelope.Payload.Offset, envelope.Payload.Count));
                bool valid = ProtocolCompatibility.IsSupported(request.ProtocolVersion) && request.Nonce != 0 &&
                    request.Credential.Length == 32 && _usedNonceCount < _usedNonces.Length;
                int difference = 0;
                if (request.Credential.Length == 32)
                    for (int i = 0; i < 32; i++) difference |= request.Credential[i] ^ _credential[i];
                if (!ProtocolCompatibility.IsSupported(request.ProtocolVersion)) RejectedVersions++;
                if (request.Credential.Length != 32 || difference != 0) RejectedCredentials++;
                for (int i = 0; i < _usedNonceCount; i++) if (_usedNonces[i] == request.Nonce) valid = false;
                if (!valid || difference != 0) { _pending[slot] = null; ServerReject(conn); return; }
                _usedNonces[_usedNonceCount++] = request.Nonce;
                var identity = new LocalLabIdentity(++_nextActorId);
                conn.authenticationData = identity;
                var reply = new LocalLabAuthReply { ProtocolVersion = ProtocolCompatibility.CurrentVersion, Nonce = request.Nonce, ActorId = identity.ActorId };
                int size = reply.CalculateSize(); reply.WriteTo(new Span<byte>(_sendBuffer, 0, size));
                conn.Send(new LocalLabAuthReplyEnvelope { Payload = new ArraySegment<byte>(_sendBuffer, 0, size) });
                _pending[slot] = null;
                ServerAccept(conn);
            }
            catch (InvalidProtocolBufferException) { _pending[slot] = null; ServerReject(conn); }
        }
        public override void OnStartClient() => NetworkClient.RegisterHandler<LocalLabAuthReplyEnvelope>(OnReply, false);
        public override void OnStopClient() => NetworkClient.UnregisterHandler<LocalLabAuthReplyEnvelope>();
        public override void OnClientAuthenticate()
        {
            HandshakeAttempted = true;
            if (_fault == "preauth")
            {
                var ping = new ProtocolPingRequest { ProtocolVersion = ProtocolCompatibility.CurrentVersion, Sequence = 1 };
                int count = ping.CalculateSize(); ping.WriteTo(new Span<byte>(_sendBuffer, 0, count));
                NetworkClient.Send(new ProtobufEnvelope { Kind = RealtimeMessageKind.PingRequest, Payload = new ArraySegment<byte>(_sendBuffer, 0, count) });
                return;
            }
            byte[] token = (byte[])_credential.Clone();
            if (_fault == "credential") token[0] ^= 1;
            var request = new LocalLabAuthRequest
            {
                ProtocolVersion = _fault == "version" ? ProtocolCompatibility.CurrentVersion + 1 : ProtocolCompatibility.CurrentVersion,
                Nonce = _nonce, Credential = ByteString.CopyFrom(token)
            };
            int size = request.CalculateSize(); request.WriteTo(new Span<byte>(_sendBuffer, 0, size));
            NetworkClient.Send(new LocalLabAuthRequestEnvelope { Payload = new ArraySegment<byte>(_sendBuffer, 0, size) });
        }
        private void OnReply(LocalLabAuthReplyEnvelope envelope)
        {
            try
            {
                var reply = LocalLabAuthReply.Parser.ParseFrom(new ReadOnlySpan<byte>(envelope.Payload.Array, envelope.Payload.Offset, envelope.Payload.Count));
                if (!ProtocolCompatibility.IsSupported(reply.ProtocolVersion) || reply.Nonce != _nonce || reply.ActorId == 0)
                { ClientReject(); return; }
                ClientActorId = reply.ActorId;
                NetworkClient.connection.authenticationData = new LocalLabIdentity(reply.ActorId);
                ClientAuthenticated = true; ClientAccept();
            }
            catch (InvalidProtocolBufferException) { ClientReject(); }
        }
    }
}
