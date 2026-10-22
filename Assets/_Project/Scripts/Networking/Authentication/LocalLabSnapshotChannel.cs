using System;
using Game.NetworkProtocol;
using Google.Protobuf;
using Mirror;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>N0 的认证后数值通道；只复制临时身份、实验 Tick 与已接受请求序号，不拥有 RPG 玩法。</summary>
    [RequireComponent(typeof(NornNetworkManager))]
    public sealed class LocalLabSnapshotChannel : MonoBehaviour
    {
        private readonly byte[] _buffer = new byte[64];
        private readonly LocalLabSnapshot _outbound = new LocalLabSnapshot();
        private readonly LocalLabSnapshot _inbound = new LocalLabSnapshot();
        private NornNetworkManager _manager;
        private double _nextAt;
        private ulong _tick, _lastTick, _lastSequence;
        public long ReceivedSnapshots { get; private set; }
        public long InvalidSnapshots { get; private set; }
        public double LastSnapshotAt { get; private set; }
        public int LargestSnapshotBytes { get; private set; }
        public void StartServer()
        {
            _manager = GetComponent<NornNetworkManager>();
            _nextAt = Time.realtimeSinceStartupAsDouble;
            // 客户端永远不能上传服务器快照；认证前由 Mirror 拒绝，认证后也明确断开。
            NetworkServer.RegisterHandler<LocalLabSnapshotEnvelope>((conn, _) => conn.Disconnect(), true);
        }
        public void StopServer() => NetworkServer.UnregisterHandler<LocalLabSnapshotEnvelope>();
        public void StartClient()
        {
            _manager = GetComponent<NornNetworkManager>();
            NetworkClient.RegisterHandler<LocalLabSnapshotEnvelope>(OnSnapshot, true);
        }
        public void StopClient() => NetworkClient.UnregisterHandler<LocalLabSnapshotEnvelope>();
        private void Update()
        {
            if (!NetworkServer.active || _manager == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextAt) return;
            _tick++;
            foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
            {
                // Ready 是客户端已经处理认证应答的屏障；否则 Unreliable 快照可能比 Reliable 认证应答先到。
                if (!conn.isAuthenticated || !conn.isReady || !(conn.authenticationData is LocalLabIdentity identity)) continue;
                if (!_manager.TryGetAcceptedProbeSequence(conn.connectionId, out ulong sequence)) continue;
                _outbound.ProtocolVersion = ProtocolCompatibility.CurrentVersion;
                _outbound.ActorId = identity.ActorId;
                _outbound.ServerTick = _tick;
                _outbound.LastAcceptedProbeSequence = sequence;
                int size = _outbound.CalculateSize();
                if (size < 1 || size > _buffer.Length) { conn.Disconnect(); continue; }
                _outbound.WriteTo(new Span<byte>(_buffer, 0, size));
                conn.Send(new LocalLabSnapshotEnvelope { Payload = new ArraySegment<byte>(_buffer, 0, size) }, Channels.Unreliable);
                LargestSnapshotBytes = Math.Max(LargestSnapshotBytes, size);
            }
            _nextAt += 1d / 15;
            if (_nextAt <= now) _nextAt = now + 1d / 15; // 最多一轮，不以无限补发隐藏卡顿。
        }
        private void OnSnapshot(LocalLabSnapshotEnvelope envelope)
        {
            try
            {
                _inbound.ResetForReuse();
                _inbound.MergeFrom(new ReadOnlySpan<byte>(envelope.Payload.Array, envelope.Payload.Offset, envelope.Payload.Count));
                if (!ProtocolCompatibility.IsSupported(_inbound.ProtocolVersion) ||
                    !(NetworkClient.connection.authenticationData is LocalLabIdentity identity) || _inbound.ActorId != identity.ActorId ||
                    _inbound.ServerTick == 0 || _inbound.LastAcceptedProbeSequence > (ulong)_manager.SentProbes)
                { InvalidSnapshots++; NetworkClient.Disconnect(); return; }
                // Unreliable 允许旧包晚到；忽略旧 Tick，不把它重新应用为新状态。
                if (_inbound.ServerTick <= _lastTick) return;
                if (_inbound.LastAcceptedProbeSequence < _lastSequence)
                { InvalidSnapshots++; NetworkClient.Disconnect(); return; }
                _lastTick = _inbound.ServerTick; _lastSequence = _inbound.LastAcceptedProbeSequence;
                ReceivedSnapshots++; LastSnapshotAt = Time.realtimeSinceStartupAsDouble;
                LargestSnapshotBytes = Math.Max(LargestSnapshotBytes, envelope.Payload.Count);
            }
            catch (InvalidProtocolBufferException) { InvalidSnapshots++; NetworkClient.Disconnect(); }
        }
    }
}
