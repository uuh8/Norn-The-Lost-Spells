using System;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 正式认证连接上的战斗适配：只处理 Wire、身份、Epoch、Hash 与有界速率；不访问 Run/Skills 或裁决伤害。
    /// FactReceived 的 Protobuf 对象只借用到下一次事实解码；订阅者必须在回调内复制需要保存的值。
    /// </summary>
    [RequireComponent(typeof(RoomNetworkManager), typeof(MovementNetworkChannel))]
    public sealed class CombatNetworkChannel : MonoBehaviour
    {
        private RoomNetworkManager _manager;
        private MovementNetworkChannel _movement;
        private readonly CombatCodec _codec = new CombatCodec();
        private readonly CombatCastIntentMessage _intent = new CombatCastIntentMessage();
        private readonly byte[] _outIntent = new byte[CombatCodec.MaxPayloadBytes];
        private readonly byte[] _outFact = new byte[CombatCodec.MaxPayloadBytes];
        private readonly IntentRateState[] _intentRates = new IntentRateState[4];
        private readonly uint[] _factEpochs = new uint[OnlineActorLimits.MaximumActors];
        private JoinConnectionIdentity _clientIdentity;
        private bool _serverRegistered, _clientRegistered;

        public ulong GameplayHash { get; set; }
        public long RejectedIntents { get; private set; }
        public long RejectedFacts { get; private set; }
        public event Action<JoinConnectionIdentity, OnlineCastIntent> IntentReceived;
        public event Action<CombatFactMessage> FactReceived;

        private struct IntentRateState
        {
            public int ConnectionId;
            public uint Epoch;
            public double Tokens, LastRefill;
        }

        private void Awake()
        {
            _manager = GetComponent<RoomNetworkManager>();
            _movement = GetComponent<MovementNetworkChannel>();
        }

        private void Update()
        {
            // 方法组只在连接生命周期变化时注册；不能在每帧重新创建 handler delegate/闭包。
            if (NetworkServer.active && !_serverRegistered)
            {
                Array.Clear(_intentRates, 0, _intentRates.Length);
                NetworkServer.RegisterHandler<CombatIntentEnvelope>(OnIntent, true);
                _serverRegistered = true;
            }
            if (!NetworkServer.active && _serverRegistered)
            {
                NetworkServer.UnregisterHandler<CombatIntentEnvelope>();
                _serverRegistered = false;
                Array.Clear(_intentRates, 0, _intentRates.Length);
            }
            if (NetworkClient.active && !_clientRegistered)
            {
                ClearClientFence();
                NetworkClient.RegisterHandler<CombatFactEnvelope>(OnFact, true);
                _clientRegistered = true;
            }
            if (!NetworkClient.active && _clientRegistered)
            {
                NetworkClient.UnregisterHandler<CombatFactEnvelope>();
                _clientRegistered = false;
                ClearClientFence();
            }
        }

        private void OnDisable()
        {
            if (_serverRegistered) NetworkServer.UnregisterHandler<CombatIntentEnvelope>();
            if (_clientRegistered) NetworkClient.UnregisterHandler<CombatFactEnvelope>();
            _serverRegistered = _clientRegistered = false;
            Array.Clear(_intentRates, 0, _intentRates.Length);
            ClearClientFence();
        }

        private void ClearClientFence()
        {
            _clientIdentity = null;
            Array.Clear(_factEpochs, 0, _factEpochs.Length);
        }

        private void OnIntent(NetworkConnectionToClient connection, CombatIntentEnvelope envelope)
        {
            var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
            // 先核对当前连接，再读取/修改该 Actor 的速率状态；旧连接迟到包不能污染重连后的槽位。
            if (_manager == null || !_manager.IsCurrentActorConnection(connection, identity) || !_manager.CanSimulate || GameplayHash == 0 ||
                !_codec.TryDecodeIntent(envelope.Payload, out var message) || message.GameplayHash != GameplayHash ||
                message.ConnectionEpoch != identity.ConnectionEpoch || !TakeIntentBudget(connection, identity, Time.realtimeSinceStartupAsDouble))
            {
                RejectedIntents++;
                return;
            }
            // CommandId 的幂等收据属于上层 ledger。这里不能丢弃同 ID 的合法重试，否则收不到原收据。
            IntentReceived?.Invoke(identity, CombatMessageMapping.Read(message));
        }

        private bool TakeIntentBudget(NetworkConnectionToClient connection, JoinConnectionIdentity identity, double now)
        {
            int index = (int)identity.ActorId - 1;
            ref var rate = ref _intentRates[index];
            if (!double.IsFinite(now)) return false;
            if (rate.Epoch != identity.ConnectionEpoch || rate.ConnectionId != connection.connectionId)
            {
                rate.ConnectionId = connection.connectionId;
                rate.Epoch = identity.ConnectionEpoch;
                rate.Tokens = 64;
                rate.LastRefill = now;
            }
            if (now < rate.LastRefill) return false;
            // Token Bucket 容忍 KCP 同帧聚合；长期 32/s，瞬时最多 64，等待不能积攒无限额度。
            rate.Tokens = Math.Min(64d, rate.Tokens + (now - rate.LastRefill) * 32d);
            rate.LastRefill = now;
            if (rate.Tokens < 1d) return false;
            rate.Tokens -= 1d;
            return true;
        }

        private void OnFact(CombatFactEnvelope envelope)
        {
            var connection = NetworkClient.connection;
            var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
            if (connection == null || !connection.isAuthenticated || identity == null || GameplayHash == 0 ||
                !_codec.TryDecodeFact(envelope.Payload, out var message) || message.GameplayHash != GameplayHash)
            {
                RejectedFacts++;
                return;
            }
            if (!ReferenceEquals(_clientIdentity, identity))
            {
                // 同一组件也可能先断线再重连；身份对象变化时重置远端观察，不能沿用上一条连接的 fence。
                ClearClientFence();
                _clientIdentity = identity;
            }
            // Burst属于当前已认证房间的全局可靠事实，ActorId=0不是角色槽。
            // 生命周期由本连接及World Owner限定，事实序号由Bridge统一去重。
            if (message.Kind == 12)
            {
                FactReceived?.Invoke(message);
                return;
            }
            int index = (int)message.ActorId - 1; // Codec与数组共享Core Actor预算，不再另写旧的8席上限。
            if (message.ActorId == identity.ActorId && message.ConnectionEpoch != identity.ConnectionEpoch ||
                message.ConnectionEpoch < _factEpochs[index])
            {
                RejectedFacts++;
                return;
            }
            _factEpochs[index] = message.ConnectionEpoch;
            // Reliable 与 Unreliable 没有跨通道总顺序；各对象/事实去重由 Bridge 做，不能共用一个 lastSequence。
            FactReceived?.Invoke(message);
        }

        public void SendIntent(in OnlineCastIntent intent)
        {
            var connection = NetworkClient.connection;
            var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
            if (!NetworkClient.isConnected || connection == null || !connection.isAuthenticated || identity == null ||
                GameplayHash == 0 || intent.ConnectionEpoch != identity.ConnectionEpoch) return;
            CombatMessageMapping.Write(intent, GameplayHash, _intent);
            if (_codec.TryEncodeIntent(_intent, new ArraySegment<byte>(_outIntent), out int size))
                NetworkClient.Send(new CombatIntentEnvelope { Payload = new ArraySegment<byte>(_outIntent, 0, size) }, Channels.Reliable);
        }

        public void SendFact(CombatFactMessage fact, NetworkConnectionToClient target = null, bool reliable = true)
        {
            if (_manager == null || !_manager.CanSimulate || GameplayHash == 0 || fact == null || fact.GameplayHash != GameplayHash ||
                !_codec.TryEncodeFact(fact, new ArraySegment<byte>(_outFact), out int size)) return;
            var envelope = new CombatFactEnvelope { Payload = new ArraySegment<byte>(_outFact, 0, size) };
            int channel = reliable ? Channels.Reliable : Channels.Unreliable;
            if (target != null)
            {
                if (IsReadyRecipient(target)) target.Send(envelope, channel);
                return;
            }
            for (int i = 0; i < 4; i++)
            {
                var connection = _manager.ActorConnection(i);
                if (IsReadyRecipient(connection)) connection.Send(envelope, channel);
            }
        }

        private bool IsReadyRecipient(NetworkConnectionToClient connection)
        {
            var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
            // 首份可靠 OwnerSetup 也必须等待合法运动输入证明 Client 已处理认证回复，避免抢先触发未认证断开。
            // 栅栏针对接收者；源 Actor 断线后仍在世界中的输出可以继续向其他玩家复制。
            return _movement != null && _manager.IsCurrentActorConnection(connection, identity) && identity.ConnectionEpoch != 0 &&
                _movement.IsActorReady(identity.ActorId, identity.ConnectionEpoch);
        }
    }
}
