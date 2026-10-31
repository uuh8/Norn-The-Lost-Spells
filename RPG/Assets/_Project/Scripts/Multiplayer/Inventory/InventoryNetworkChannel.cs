using System;
using Game.Core;
using Game.Networking;
using Mirror;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 认证连接上的owner-only库存通道。此层只处理传输、连接身份与去重，不创建库存工作区，
    /// 不判断法术是否可施放。完整值仅在装备/程序离散变化时发送，可靠KCP负责分片与重传。
    /// </summary>
    [RequireComponent(typeof(RoomNetworkManager), typeof(JoinTicketAuthenticator))]
    public sealed class InventoryNetworkChannel : MonoBehaviour
    {
        private readonly InventorySnapshotCodec _codec = new InventorySnapshotCodec();
        private readonly byte[] _sendBuffer = new byte[InventorySnapshotCodec.MaxPayloadBytes];
        private readonly InventoryCommandCodec _commands = new InventoryCommandCodec();
        private readonly byte[] _commandBuffer = new byte[InventoryCommandCodec.MaxPayloadBytes];
        private readonly byte[] _receiptBuffer = new byte[InventoryCommandCodec.MaxPayloadBytes];
        private readonly InventoryCommandPendingInbox _pending = new InventoryCommandPendingInbox();
        private readonly InventoryCommandRateBudget[] _rates = new InventoryCommandRateBudget[4], _ingress = new InventoryCommandRateBudget[4];
        private RoomNetworkManager _manager;
        private JoinTicketAuthenticator _auth;
        private JoinConnectionIdentity _clientIdentity;
        private InventorySnapshotInbox _inbox;
        private bool _clientRegistered, _serverRegistered;
        public string GameplayConfigHash { get; set; }
        public ulong GameplayHash { get; set; }
        public long SentSnapshots { get; private set; }
        public long RejectedSnapshots { get; private set; }
        public long AcceptedCommands { get; private set; }
        public long RejectedCommands { get; private set; }
        public event Action<JoinConnectionIdentity, OnlineInventoryCommandIntent, OnlineInventoryCommandFailure> CommandReceived;

        private void Awake()
        {
            _manager = GetComponent<RoomNetworkManager>(); _auth = GetComponent<JoinTicketAuthenticator>();
            for (int i = 0; i < 4; i++)
            { _rates[i] = new InventoryCommandRateBudget(16, 8); _ingress[i] = new InventoryCommandRateBudget(64, 32); }
            EnsureHandlers();
        }
        private void OnEnable() => EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(OnLocalRequest);
        private void Update()
        {
            EnsureHandlers();
            if (!NetworkClient.active)
            {
                ClearClient();
            }
            if (!NetworkServer.active && _serverRegistered)
            { NetworkServer.UnregisterHandler<InventoryCommandEnvelope>(); _serverRegistered = false; ClearBudgets(); }
        }
        private void EnsureHandlers()
        {
            if (NetworkServer.active && !_serverRegistered)
            { NetworkServer.RegisterHandler<InventoryCommandEnvelope>(OnCommand, true); _serverRegistered = true; }
            if (!NetworkClient.active || _clientRegistered) return;
            // 世界可在认证后才加载；创建组件时立即注册，而不是等待下一帧第一次库存已到达后再补。
            NetworkClient.RegisterHandler<InventorySnapshotEnvelope>(OnSnapshot, true);
            NetworkClient.RegisterHandler<InventoryReceiptEnvelope>(OnReceipt, true); _clientRegistered = true;
        }

        /// <summary>
        /// 指定所属Actor的当前连接，绝不Broadcast。再检查同一个authenticationData对象，
        /// 防止重连后旧回调拿着旧Identity把完整物品清单发给已替换的连接。
        /// </summary>
        public bool SendToOwner(JoinConnectionIdentity identity, OnlineInventoryPresentationSnapshot snapshot)
        {
            if (!NetworkServer.active || _manager == null || identity == null || identity.ActorId < 1 || identity.ActorId > 4 ||
                !InventorySnapshotInbox.Matches(identity, snapshot, GameplayConfigHash)) return false;
            var target = _manager.ActorConnection((int)identity.ActorId - 1);
            if (target == null || !target.isAuthenticated || !ReferenceEquals(target.authenticationData, identity) ||
                !_codec.TryEncode(snapshot, new ArraySegment<byte>(_sendBuffer), out int count)) return false;
            target.Send(new InventorySnapshotEnvelope { Payload = new ArraySegment<byte>(_sendBuffer, 0, count) }, Channels.Reliable);
            SentSnapshots++; return true;
        }

        private void OnSnapshot(InventorySnapshotEnvelope envelope)
        {
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            if (identity == null || string.IsNullOrEmpty(GameplayConfigHash) || !_codec.TryDecode(envelope.Payload, out var snapshot))
            { RejectedSnapshots++; return; }
            EnsureClientIdentity(identity);
            if (!_inbox.TryQueue(snapshot)) RejectedSnapshots++;
        }

        private void EnsureClientIdentity(JoinConnectionIdentity identity)
        {
            if (ReferenceEquals(identity, _clientIdentity)) return;
            _pending.Clear(); _clientIdentity = identity;
            _inbox = identity != null ? new InventorySnapshotInbox(identity, GameplayConfigHash) : null;
        }

        private void OnLocalRequest(OnlineInventoryCommandRequestedEvent request)
        {
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            if (identity == null || !NetworkClient.isConnected || NetworkClient.connection == null ||
                !NetworkClient.connection.isAuthenticated || request.ActorId != identity.ActorId ||
                request.Intent.ConnectionEpoch != identity.ConnectionEpoch) return;
            EnsureClientIdentity(identity);
            var failure = OnlineInventoryCommandFailure.None;
            if (!_commands.TryEncodeIntent(request.Intent, GameplayHash, new ArraySegment<byte>(_commandBuffer), out int count))
                failure = OnlineInventoryCommandFailure.InvalidInput;
            else if (!_pending.TryAdd(request.Intent.CommandId, request.Intent.ConnectionEpoch, request.CallerId))
                failure = OnlineInventoryCommandFailure.RateLimited;
            if (failure != OnlineInventoryCommandFailure.None)
            {
                // 本地拒绝也只反馈给原Caller，不修改Source。UI使用同一个收据入口清理等待状态。
                EventBus<OnlineInventoryCommandResultEvent>.Publish(new OnlineInventoryCommandResultEvent
                { ActorId = identity.ActorId, CallerId = request.CallerId, Receipt = new OnlineInventoryCommandReceipt
                    { CommandId = request.Intent.CommandId, ConnectionEpoch = identity.ConnectionEpoch, Failure = failure } });
                return;
            }
            NetworkClient.Send(new InventoryCommandEnvelope { Payload = new ArraySegment<byte>(_commandBuffer, 0, count) }, Channels.Reliable);
        }

        private void OnCommand(NetworkConnectionToClient connection, InventoryCommandEnvelope envelope)
        {
            var identity = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
            // 连接栅栏先于速率槽：旧连接不能消耗新代次的额度，更不能用高Epoch重置领域去重账本。
            if (_manager == null || !_manager.IsCurrentActorConnection(connection, identity))
            { RejectedCommands++; return; }
            int index = (int)identity.ActorId - 1; double now = Time.realtimeSinceStartupAsDouble;
            if (!_ingress[index].TryTake(connection.connectionId, identity.ConnectionEpoch, now) ||
                !_commands.TryDecodeIntent(envelope.Payload, GameplayHash, out var intent) || intent.ConnectionEpoch != identity.ConnectionEpoch)
            { RejectedCommands++; return; }
            var failure = _rates[index].TryTake(connection.connectionId, identity.ConnectionEpoch, now)
                ? OnlineInventoryCommandFailure.None : OnlineInventoryCommandFailure.RateLimited;
            if (failure == OnlineInventoryCommandFailure.None) AcceptedCommands++; else RejectedCommands++;
            // 包括限流拒绝也交给同一Authority ledger；相同ID的结果不能在下一次重试时改变。
            CommandReceived?.Invoke(identity, intent, failure);
        }

        private void ClearBudgets()
        { for (int i = 0; i < 4; i++) { _rates[i]?.Clear(); _ingress[i]?.Clear(); } }

        public bool SendReceipt(JoinConnectionIdentity identity, in OnlineInventoryCommandReceipt receipt)
        {
            if (identity == null || receipt.ConnectionEpoch != identity.ConnectionEpoch || _manager == null) return false;
            var connection = _manager.ActorConnection((int)identity.ActorId - 1);
            if (!_manager.IsCurrentActorConnection(connection, identity) ||
                !_commands.TryEncodeReceipt(receipt, GameplayHash, new ArraySegment<byte>(_receiptBuffer), out int count)) return false;
            connection.Send(new InventoryReceiptEnvelope { Payload = new ArraySegment<byte>(_receiptBuffer, 0, count) }, Channels.Reliable); return true;
        }

        private void OnReceipt(InventoryReceiptEnvelope envelope)
        {
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            if (identity == null || !NetworkClient.isConnected || !_commands.TryDecodeReceipt(envelope.Payload, GameplayHash, out var receipt) ||
                receipt.ConnectionEpoch != identity.ConnectionEpoch) return;
            EnsureClientIdentity(identity);
            if (!_pending.TryTake(receipt.CommandId, receipt.ConnectionEpoch, out int caller)) return;
            EventBus<OnlineInventoryCommandResultEvent>.Publish(new OnlineInventoryCommandResultEvent
                { ActorId = identity.ActorId, CallerId = caller, Receipt = receipt });
        }

        public bool TryTakeClientSnapshot(out OnlineInventoryPresentationSnapshot snapshot)
        {
            snapshot = null;
            // 明确拒绝连接已切换但世界尚未重建的一帧；不能把旧收件箱的值安装到新Actor。
            if (_auth == null || !_auth.ClientAuthenticated || !NetworkClient.isConnected ||
                !ReferenceEquals(_clientIdentity, _auth.ClientIdentity) || _inbox == null) return false;
            return _inbox.TryTake(out snapshot);
        }
        private void ClearClient()
        {
            if (_clientRegistered)
            { NetworkClient.UnregisterHandler<InventorySnapshotEnvelope>(); NetworkClient.UnregisterHandler<InventoryReceiptEnvelope>(); }
            _clientRegistered = false; _inbox = null; _clientIdentity = null; _pending.Clear();
        }
        private void OnDisable()
        {
            EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(OnLocalRequest); ClearClient();
            if (_serverRegistered) NetworkServer.UnregisterHandler<InventoryCommandEnvelope>();
            _serverRegistered = false; ClearBudgets();
        }
    }
}
