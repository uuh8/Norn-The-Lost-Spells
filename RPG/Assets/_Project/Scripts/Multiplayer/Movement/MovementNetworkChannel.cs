using System;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>正式准入后的有界消息适配；连接绑定先于输入转发，身份不从Client消息读取。</summary>
    [RequireComponent(typeof(RoomNetworkManager))]
    public sealed class MovementNetworkChannel : MonoBehaviour
    {
        private RoomNetworkManager _manager;
        private readonly MovementCodec _codec = new MovementCodec();
        private readonly MovementInputMessage _input = new MovementInputMessage();
        private readonly MovementStateMessage _state = new MovementStateMessage();
        private readonly byte[] _outInput = new byte[512], _outState = new byte[512];
        private bool _serverRegistered, _clientRegistered;
        private readonly uint[] _readyEpochs = new uint[4];
        private readonly MovementFaultQueue<PlayerSimulationFrame> _delayedInputs = new MovementFaultQueue<PlayerSimulationFrame>();
        private readonly MovementFaultQueue<MovementStateFrame> _delayedStates = new MovementFaultQueue<MovementStateFrame>();
        public long DiagnosticDropped => _delayedInputs.Dropped + _delayedStates.Dropped;
        public void ConfigureDiagnosticFaults(int rtt, int jitter, int loss)
        {
            _delayedInputs.Configure(rtt, jitter, loss, 1307); _delayedStates.Configure(rtt, jitter, loss, 2701);
        }
        public ulong SimulationHash { get; set; }
        public long RejectedInputs { get; private set; }
        public event Action<JoinConnectionIdentity, PlayerSimulationFrame> InputReceived;
        public event Action<MovementStateFrame> StateReceived;
        private void Awake() => _manager = GetComponent<RoomNetworkManager>();
        private void Update()
        {
            // 注册是连接生命周期上的离散操作；不能每帧建立Mirror处理器闭包。
            if (NetworkServer.active && !_serverRegistered) { Array.Clear(_readyEpochs, 0, _readyEpochs.Length); NetworkServer.RegisterHandler<MovementInputEnvelope>(OnInput, true); _serverRegistered = true; }
            if (!NetworkServer.active && _serverRegistered) { NetworkServer.UnregisterHandler<MovementInputEnvelope>(); _serverRegistered = false; }
            if (NetworkClient.active && !_clientRegistered) { NetworkClient.RegisterHandler<MovementStateEnvelope>(OnState, true); _clientRegistered = true; }
            if (!NetworkClient.active && _clientRegistered) { NetworkClient.UnregisterHandler<MovementStateEnvelope>(); _clientRegistered = false; }
            double now = Time.realtimeSinceStartupAsDouble;
            while (_delayedInputs.TryPop(now, out var input)) SendInputNow(input, SimulationHash);
            while (_delayedStates.TryPop(now, out var state)) StateReceived?.Invoke(state);
        }
        private void OnDisable()
        {
            if (_serverRegistered) NetworkServer.UnregisterHandler<MovementInputEnvelope>();
            if (_clientRegistered) NetworkClient.UnregisterHandler<MovementStateEnvelope>();
            _serverRegistered = _clientRegistered = false;
            _delayedInputs.Clear(); _delayedStates.Clear();
        }
        private void OnInput(NetworkConnectionToClient connection, MovementInputEnvelope envelope)
        {
            var identity = connection.authenticationData as JoinConnectionIdentity;
            if (!_manager.IsCurrentActorConnection(connection, identity) || !_manager.CanSimulate || SimulationHash == 0 ||
                !_codec.TryDecodeInput(envelope.Payload, out var message) || message.SimulationHash != SimulationHash || message.ConnectionEpoch != identity.ConnectionEpoch)
            { RejectedInputs++; return; }
            // Reliable认证回复与Unreliable快照没有跨通道顺序保证。合法输入证明Client已处理完认证回复；
            // 不能一ServerAccept就发快照，否则快照可能抢先到达，触发Mirror未认证断开。
            _readyEpochs[(int)identity.ActorId - 1] = identity.ConnectionEpoch;
            InputReceived?.Invoke(identity, MovementMessageMapping.Read(message));
        }
        private void OnState(MovementStateEnvelope envelope)
        {
            if (!_codec.TryDecodeState(envelope.Payload, out var state) || SimulationHash == 0 || state.SimulationHash != SimulationHash) return;
            var frame = MovementMessageMapping.Read(state);
            if (_delayedStates.Enabled) _delayedStates.Schedule(frame, Time.realtimeSinceStartupAsDouble);
            else StateReceived?.Invoke(frame);
        }
        public void SendInput(in PlayerSimulationFrame frame, ulong hash)
        {
            if (_delayedInputs.Enabled) { _delayedInputs.Schedule(frame, Time.realtimeSinceStartupAsDouble); return; }
            SendInputNow(frame, hash);
        }
        private void SendInputNow(in PlayerSimulationFrame frame, ulong hash)
        {
            if (!NetworkClient.isConnected || NetworkClient.connection == null || !NetworkClient.connection.isAuthenticated) return;
            MovementMessageMapping.Write(frame, hash, _input);
            if (_codec.TryEncodeInput(_input, new ArraySegment<byte>(_outInput), out int size))
                NetworkClient.Send(new MovementInputEnvelope { Payload = new ArraySegment<byte>(_outInput, 0, size) }, Channels.Unreliable);
        }
        public void SendState(in MovementStateFrame frame)
        {
            if (!NetworkServer.active) return;
            MovementMessageMapping.Write(frame, _state);
            if (!_codec.TryEncodeState(_state, new ArraySegment<byte>(_outState), out int size)) return;
            var envelope = new MovementStateEnvelope { Payload = new ArraySegment<byte>(_outState, 0, size) };
            for (int i = 0; i < 4; i++)
            {
                var connection = _manager.ActorConnection(i);
                if (connection != null && connection.authenticationData is JoinConnectionIdentity identity && _readyEpochs[i] == identity.ConnectionEpoch)
                    connection.Send(envelope, Channels.Unreliable);
            }
        }
    }
}
