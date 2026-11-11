using System;
using Game.Core;
using Mirror;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// 只做当前认证连接的个人退出传输。DS组合层负责个人Fence/捕获/最终收据；本层无资产写入口。
    /// 固定四Actor槽和两份256B缓冲，不持有无限命令历史，不建立第二个重试/结算算法。
    /// </summary>
    [RequireComponent(typeof(RoomNetworkManager), typeof(JoinTicketAuthenticator))]
    public sealed class RunExitNetworkChannel : MonoBehaviour
    {
        private readonly RunExitCodec _codec = new RunExitCodec();
        private readonly byte[] _intentBytes = new byte[RunExitCodec.MaxPayloadBytes], _statusBytes = new byte[RunExitCodec.MaxPayloadBytes];
        private readonly JoinConnectionIdentity[] _rateOwners = new JoinConnectionIdentity[4];
        private readonly double[] _nextRequest = new double[4];
        private RoomNetworkManager _manager;
        private JoinTicketAuthenticator _auth;
        private object _worldOwner;
        private ulong _gameplayHash;
        private bool _serverRegistered, _clientRegistered;
        public ulong GameplayHash => _gameplayHash;
        public long SentIntents { get; private set; }
        public long SentStatuses { get; private set; }
        public long AcceptedIntents { get; private set; }
        public long RejectedPackets { get; private set; }
        public event Action<JoinConnectionIdentity, OnlineRunExitIntent> IntentReceived;
        private bool HasWorld => _worldOwner != null && (!(_worldOwner is UnityEngine.Object value) || value != null);

        public void ConfigureWorld(object owner, ulong gameplayHash)
        {
            if (owner == null || gameplayHash == 0) throw new ArgumentException("个人退出通道缺当前World或玩法摘要。");
            _worldOwner = owner; _gameplayHash = gameplayHash; ClearRates(); Resolve(); EnsureHandlers();
        }
        public void ClearWorld(object owner)
        {
            // 旧World迟到清理不能抹去新World的绑定，和原库存/材料通道相同。
            if (!ReferenceEquals(_worldOwner, owner)) return;
            _worldOwner = null; _gameplayHash = 0; ClearRates();
        }
        private void Resolve()
        {
            if (_manager == null) _manager = GetComponent<RoomNetworkManager>();
            if (_auth == null) _auth = GetComponent<JoinTicketAuthenticator>();
        }
        private void Awake() { Resolve(); EnsureHandlers(); }
        private void OnEnable() => EventBus<OnlineRunExitRequestedEvent>.Subscribe(OnLocalRequest);
        private void Update()
        {
            EnsureHandlers();
            if (!NetworkServer.active && _serverRegistered)
            { NetworkServer.UnregisterHandler<RunExitIntentEnvelope>(); _serverRegistered = false; ClearRates(); }
            if (!NetworkClient.active && _clientRegistered)
            { NetworkClient.UnregisterHandler<RunExitStatusEnvelope>(); _clientRegistered = false; }
        }
        private void EnsureHandlers()
        {
            if (NetworkServer.active && !_serverRegistered)
            { NetworkServer.RegisterHandler<RunExitIntentEnvelope>(OnIntent, true); _serverRegistered = true; }
            if (NetworkClient.active && !_clientRegistered)
            { NetworkClient.RegisterHandler<RunExitStatusEnvelope>(OnStatus, true); _clientRegistered = true; }
        }
        private void OnLocalRequest(OnlineRunExitRequestedEvent request)
        {
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            if (identity == null) return;
            SendIntent(new OnlineRunExitIntent { CommandId = request.CommandId, ConnectionEpoch = identity.ConnectionEpoch,
                ThroughReturnPortal = request.ThroughReturnPortal });
        }
        public bool SendIntent(in OnlineRunExitIntent intent)
        {
            Resolve(); EnsureHandlers();
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            if (!HasWorld || identity == null || !NetworkClient.isConnected || NetworkClient.connection == null
                || !NetworkClient.connection.isAuthenticated || intent.ConnectionEpoch != identity.ConnectionEpoch
                || !_codec.TryEncodeIntent(intent, _gameplayHash, new ArraySegment<byte>(_intentBytes), out int count)) return false;
            NetworkClient.Send(new RunExitIntentEnvelope { Payload = new ArraySegment<byte>(_intentBytes, 0, count) }, Channels.Reliable);
            SentIntents++; return true;
        }
        private void OnIntent(NetworkConnectionToClient connection, RunExitIntentEnvelope envelope)
        {
            var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
            if (!HasWorld || _manager == null || !_manager.IsCurrentActorConnection(connection, identity))
            { RejectedPackets++; return; }
            int slot = (int)identity.ActorId - 1; double now = Time.realtimeSinceStartupAsDouble;
            // 先身份栅栏再预算，旧连接不能偷走新连接额度；每个认证Actor最多4次/秒的离散查询/重发。
            // 限流只丢此小包，不伪造最终拒绝收据，真正固定命令由DS状态机判断，合法低频重试仍可达。
            if (!ReferenceEquals(_rateOwners[slot], identity)) { _rateOwners[slot] = identity; _nextRequest[slot] = 0; }
            if (now < _nextRequest[slot]) { RejectedPackets++; return; }
            _nextRequest[slot] = now + .25;
            if (!_codec.TryDecodeIntent(envelope.Payload, _gameplayHash, out var intent) || intent.ConnectionEpoch != identity.ConnectionEpoch)
            { RejectedPackets++; return; }
            AcceptedIntents++; IntentReceived?.Invoke(identity, intent);
        }
        public bool SendStatus(JoinConnectionIdentity identity, in OnlineRunExitStatus status)
        {
            if (!HasWorld || !NetworkServer.active || _manager == null || identity == null || identity.ActorId < 1 || identity.ActorId > 4
                || status.ConnectionEpoch != identity.ConnectionEpoch) return false;
            var connection = _manager.ActorConnection((int)identity.ActorId - 1);
            if (!_manager.IsCurrentActorConnection(connection, identity)
                || !_codec.TryEncodeStatus(status, _gameplayHash, new ArraySegment<byte>(_statusBytes), out int count)) return false;
            connection.Send(new RunExitStatusEnvelope { Payload = new ArraySegment<byte>(_statusBytes, 0, count) }, Channels.Reliable);
            SentStatuses++; return true;
        }
        private void OnStatus(RunExitStatusEnvelope envelope)
        {
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            if (!HasWorld || identity == null || !NetworkClient.isConnected || NetworkClient.connection == null
                || !NetworkClient.connection.isAuthenticated || !_codec.TryDecodeStatus(envelope.Payload, _gameplayHash, out var status)
                || status.ConnectionEpoch != identity.ConnectionEpoch) { RejectedPackets++; return; }
            // commandId关联与阶段不倒退由RoomClient处理；这里只声明本当前DS认证连接确实收到此值。
            EventBus<OnlineRunExitStatusChangedEvent>.Publish(new OnlineRunExitStatusChangedEvent { Status = status });
        }
        private void ClearRates() { Array.Clear(_rateOwners, 0, _rateOwners.Length); Array.Clear(_nextRequest, 0, _nextRequest.Length); }
        private void OnDisable()
        {
            EventBus<OnlineRunExitRequestedEvent>.Unsubscribe(OnLocalRequest);
            if (_serverRegistered) NetworkServer.UnregisterHandler<RunExitIntentEnvelope>();
            if (_clientRegistered) NetworkClient.UnregisterHandler<RunExitStatusEnvelope>();
            _serverRegistered = _clientRegistered = false; _worldOwner = null; _gameplayHash = 0;
            ClearRates(); IntentReceived = null;
        }
    }
}
