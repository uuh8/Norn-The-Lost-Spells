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
        // 四个认证Actor固定槽，每槽当前+最新待传两份；不常驻4份20MiB最大数组。
        private readonly InventorySnapshotSender[] _snapshotSenders = new InventorySnapshotSender[4];
        private readonly JoinConnectionIdentity[] _snapshotOwners = new JoinConnectionIdentity[4];
        private const int PagesPerFrame = 4;
        private int _nextSnapshotSlot;
        private readonly int[] _snapshotFrameReservations = new int[4];
        private object _worldOwner;
        private Func<OnlineInventoryPresentationSnapshot, bool> _validateDomain;
        private readonly InventoryCommandCodec _commands = new InventoryCommandCodec();
        private readonly byte[] _commandBuffer = new byte[InventoryCommandCodec.MaxPayloadBytes];
        private readonly byte[] _receiptBuffer = new byte[InventoryCommandCodec.MaxPayloadBytes];
        private readonly InventoryCommandPendingInbox _pending = new InventoryCommandPendingInbox();
        private readonly InventoryCommandOperationBudget[] _rates = new InventoryCommandOperationBudget[4];
        private readonly InventoryCommandRateBudget[] _ingress = new InventoryCommandRateBudget[4];
        private RoomNetworkManager _manager;
        private JoinTicketAuthenticator _auth;
        private JoinConnectionIdentity _clientIdentity;
        private InventorySnapshotInbox _inbox;
        private InventorySnapshotAssembler _assembler;
        private bool _clientRegistered, _serverRegistered;
        public string GameplayConfigHash { get; set; }
        public ulong GameplayHash { get; set; }
        public long SentSnapshots { get; private set; }
        public long SentSnapshotPages { get; private set; }
        public long RejectedSnapshots { get; private set; }
        public long AcceptedCommands { get; private set; }
        public long RejectedCommands { get; private set; }
        [Serializable] public sealed class OwnerDeliveryDiagnostic
        {
            public int ActorSlot, BufferedSnapshots, BufferedBytes, ReliableQueuedSegments, FragmentPayloadBytes;
            public uint Epoch;
            public ulong CurrentSequence;
            public int CurrentPageIndex, CurrentPageCount, CurrentTotalBytes;
            public bool HasPage, CurrentConnection, MetricsAvailable;
        }
        /// <summary>
        /// 显式稀疏诊断才复制四槽。分别报告“已排队”“实际页发送位置”和KCP队列，不能把生成Snapshot当作Client已安装。
        /// 这里只读现有sender/transport，不推进页、不重发、不改变回压或Gameplay；不接入每帧正式UI。
        /// </summary>
        public OwnerDeliveryDiagnostic[] CaptureOwnerDeliveryDiagnostics()
        {
            var result = new OwnerDeliveryDiagnostic[4]; var metrics = Transport.active as IReliableQueueMetrics;
            for (int i = 0; i < 4; i++)
            {
                var sender = _snapshotSenders[i]; var identity = _snapshotOwners[i];
                var connection = _manager != null ? _manager.ActorConnection(i) : null;
                var row = new OwnerDeliveryDiagnostic { ActorSlot = i, Epoch = identity != null ? identity.ConnectionEpoch : 0,
                    BufferedSnapshots = sender != null ? sender.BufferedCount : 0, BufferedBytes = sender != null ? sender.BufferedBytes : 0,
                    CurrentConnection = identity != null && connection != null && connection.isAuthenticated && ReferenceEquals(connection.authenticationData, identity) };
                if (sender != null && sender.TryPeek(out var page))
                { row.HasPage = true; row.CurrentSequence = page.Sequence; row.CurrentPageIndex = page.Index; row.CurrentPageCount = page.Count; row.CurrentTotalBytes = page.TotalBytes; }
                if (metrics != null && connection != null && metrics.TryGetServerReliableQueue(connection.connectionId, out int queued, out int fragment))
                { row.MetricsAvailable = true; row.ReliableQueuedSegments = queued; row.FragmentPayloadBytes = fragment; }
                result[i] = row;
            }
            return result;
        }
        public event Action<JoinConnectionIdentity, OnlineInventoryCommandIntent, OnlineInventoryCommandFailure> CommandReceived;

        public void ConfigureWorld(object owner, string configHash, ulong wireHash,
            Func<OnlineInventoryPresentationSnapshot, bool> validateDomain)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            ClearOwnerSnapshots(); ClearClient(); ClearBudgets();
            _worldOwner = owner; _validateDomain = validateDomain;
            GameplayConfigHash = configHash; GameplayHash = wireHash;
            EnsureHandlers();
        }

        public void DetachWorld(object owner)
        {
            // Manager可比World活得久；旧World的迟到Dispose不能清掉已经替换的新Owner。
            if (!ReferenceEquals(_worldOwner, owner)) return;
            _worldOwner = null; _validateDomain = null; GameplayConfigHash = null; GameplayHash = 0;
            ClearOwnerSnapshots(); ClearClient(); ClearBudgets();
        }

        public static bool CanQueueOwnerPage(int queuedSegments, int frameReservedSegments, int payloadBytes, int fragmentPayloadBytes)
        {
            if (queuedSegments < 0 || frameReservedSegments < 0 || payloadBytes < 1
                || payloadBytes > InventorySnapshotPaging.MaxPageBytes || fragmentPayloadBytes < 1) return false;
            long additional = ((long)payloadBytes + 64 + fragmentPayloadBytes - 1) / fragmentPayloadBytes;
            // 远低于KCP的10000片拥塞断线；给控制/战斗小包留下余量，不靠抬系统阈值硬发。
            return (long)queuedSegments + frameReservedSegments + additional <= 192;
        }

        private void Awake()
        {
            _manager = GetComponent<RoomNetworkManager>(); _auth = GetComponent<JoinTicketAuthenticator>();
            for (int i = 0; i < 4; i++)
            { _rates[i] = new InventoryCommandOperationBudget(); _ingress[i] = new InventoryCommandRateBudget(64, 32);
                _snapshotSenders[i] = new InventorySnapshotSender(); }
            EnsureHandlers();

        }
        private void OnEnable() => EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(OnLocalRequest);
        private void Update()
        {
            EnsureHandlers();
            if (NetworkServer.active && _worldOwner != null) PumpOwnerSnapshots();
            if (!NetworkClient.active)
            {
                ClearClient();
            }
            if (!NetworkServer.active && _serverRegistered)
            { NetworkServer.UnregisterHandler<InventoryCommandEnvelope>(); _serverRegistered = false; ClearBudgets(); ClearOwnerSnapshots(); }
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
            if (_worldOwner == null || !NetworkServer.active || _manager == null || identity == null || identity.ActorId < 1 || identity.ActorId > 4 ||
                !InventorySnapshotInbox.Matches(identity, snapshot, GameplayConfigHash)) return false;
            var target = _manager.ActorConnection((int)identity.ActorId - 1);
            if (target == null || !target.isAuthenticated || !ReferenceEquals(target.authenticationData, identity) ||
                !_codec.TryEncode(snapshot, out byte[] payload)) return false;
            int index = (int)identity.ActorId - 1;
            if (!ReferenceEquals(_snapshotOwners[index], identity))
            { _snapshotSenders[index].Clear(); _snapshotOwners[index] = identity; }
            return _snapshotSenders[index].TryQueue(identity.ConnectionEpoch, snapshot.SnapshotSequence, payload);
        }

        private void PumpOwnerSnapshots()
        {
            // 离散快照也不能一次把1280页压入KCP。只轮转固定四槽，帧预算至多64KiB正文。
            // 当前快照完整发完才提升最新待传，避免持续施法一直取消大快照导致UI永远装不完。
            Array.Clear(_snapshotFrameReservations, 0, _snapshotFrameReservations.Length);
            int sent = 0, misses = 0;
            while (sent < PagesPerFrame && misses < 4)
            {
                int index = _nextSnapshotSlot; _nextSnapshotSlot = (_nextSnapshotSlot + 1) & 3;
                var sender = _snapshotSenders[index]; var identity = _snapshotOwners[index];
                if (sender == null || !sender.TryPeek(out var page)) { misses++; continue; }
                var target = _manager != null ? _manager.ActorConnection(index) : null;
                if (identity == null || target == null || !target.isAuthenticated || !ReferenceEquals(target.authenticationData, identity))
                { sender.Clear(); _snapshotOwners[index] = null; misses++; continue; }
                var transport = Transport.active as IReliableQueueMetrics;
                if (transport == null || !transport.TryGetServerReliableQueue(target.connectionId, out int queued, out int fragmentBytes)
                    || !CanQueueOwnerPage(queued, _snapshotFrameReservations[index], page.Payload.Count, fragmentBytes))
                { misses++; continue; } // 保留当前页/两份快照，底层排空后再发；不重试或增长队列。
                target.Send(new InventorySnapshotEnvelope { ConnectionEpoch = page.Epoch, SnapshotSequence = page.Sequence,
                    TotalBytes = page.TotalBytes, PageIndex = page.Index, PageCount = page.Count, Checksum = page.Checksum,
                    Payload = page.Payload }, Channels.Reliable);
                _snapshotFrameReservations[index] += (page.Payload.Count + 64 + fragmentBytes - 1) / fragmentBytes;
                if (sender.Advance()) SentSnapshots++;
                SentSnapshotPages++; sent++; misses = 0;
            }
        }

        private void ClearOwnerSnapshots()
        { for (int i = 0; i < 4; i++) { _snapshotSenders[i]?.Clear(); _snapshotOwners[i] = null; } _nextSnapshotSlot = 0; }

        private void OnSnapshot(InventorySnapshotEnvelope envelope)
        {
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            if (_worldOwner == null || _validateDomain == null || identity == null || string.IsNullOrEmpty(GameplayConfigHash))
            { RejectedSnapshots++; return; }
            EnsureClientIdentity(identity);
            if (!_assembler.TryAccept(envelope.Page, out var complete)) { RejectedSnapshots++; return; }
            if (complete.Array == null) return;
            if (!_codec.TryDecode(complete, out var snapshot) || snapshot.ConnectionEpoch != envelope.ConnectionEpoch
                || snapshot.SnapshotSequence != envelope.SnapshotSequence)
            { RejectedSnapshots++; return; }
            if (!_inbox.TryQueue(snapshot)) RejectedSnapshots++;
        }

        private void EnsureClientIdentity(JoinConnectionIdentity identity)
        {
            if (ReferenceEquals(identity, _clientIdentity)) return;
            _pending.Clear(); _clientIdentity = identity;
            _inbox = identity != null ? new InventorySnapshotInbox(identity, GameplayConfigHash, _validateDomain) : null;
            _assembler?.Clear();
            _assembler = identity != null ? new InventorySnapshotAssembler(identity.ConnectionEpoch) : null;
        }

        private void OnLocalRequest(OnlineInventoryCommandRequestedEvent request)
        {
            var identity = _auth != null && _auth.ClientAuthenticated ? _auth.ClientIdentity : null;
            if (_worldOwner == null || identity == null || !NetworkClient.isConnected || NetworkClient.connection == null ||
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
            if (_worldOwner == null || _manager == null || !_manager.IsCurrentActorConnection(connection, identity))
            { RejectedCommands++; return; }
            int index = (int)identity.ActorId - 1; double now = Time.realtimeSinceStartupAsDouble;
            if (!_ingress[index].TryTake(connection.connectionId, identity.ConnectionEpoch, now) ||
                !_commands.TryDecodeIntent(envelope.Payload, GameplayHash, out var intent) || intent.ConnectionEpoch != identity.ConnectionEpoch)
            { RejectedCommands++; return; }
            // Kind必须来自通过Codec验证的意图；原预解码64突发/32每秒总门仍先限制分配与收据放大。
            // F/H各10Hz只调整这里的操作额度，不改变Run数量预算、原采集/倾倒速率或幂等账本。
            var failure = _rates[index].TryTake(connection.connectionId, identity.ConnectionEpoch, intent.Kind, now)
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
            if (_worldOwner == null || identity == null || !NetworkClient.isConnected || !_commands.TryDecodeReceipt(envelope.Payload, GameplayHash, out var receipt) ||
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
            if (_worldOwner == null || _auth == null || !_auth.ClientAuthenticated || !NetworkClient.isConnected ||
                !ReferenceEquals(_clientIdentity, _auth.ClientIdentity) || _inbox == null) return false;
            return _inbox.TryTake(out snapshot);
        }

        public void ConfirmClientSnapshot(ulong sequence, bool installed)
        {
            // 只有Run通过目录/归属/占格/组分的原子安装才提交已安装水位；坏高序号不占住未来。
            if (_inbox != null && _inbox.ConfirmInstalled(sequence, installed)) _assembler?.Confirm(sequence, installed);
        }
        private void ClearClient()
        {
            if (_clientRegistered)
            { NetworkClient.UnregisterHandler<InventorySnapshotEnvelope>(); NetworkClient.UnregisterHandler<InventoryReceiptEnvelope>(); }
            _clientRegistered = false; _inbox = null; _clientIdentity = null; _pending.Clear(); _assembler?.Clear(); _assembler = null;
        }
        private void OnDisable()
        {
            EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(OnLocalRequest); ClearClient();
            if (_serverRegistered) NetworkServer.UnregisterHandler<InventoryCommandEnvelope>();
            _serverRegistered = false; ClearBudgets(); ClearOwnerSnapshots(); _worldOwner = null; _validateDomain = null;
        }
    }
}
