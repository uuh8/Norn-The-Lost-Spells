using System;
using System.Net;
using System.Diagnostics;
using Game.Core;
using Game.NetworkProtocol;
using kcp2k;
using Mirror;
using UnityEngine;
using Mirror.Transports.Encryption;

namespace Game.Networking
{
    /// <summary>本地连通性实验的 Mirror 适配器，不创建玩家角色，不接触单机存档。</summary>
    [RequireComponent(typeof(KcpTransport))]
    public sealed class NornNetworkManager : NetworkManager
    {
        private ProtoMessageCodec _codec;
        private ConnectionSession[] _sessions;
        private readonly ProtocolPingRequest _request = new ProtocolPingRequest();
        private readonly ProtocolPingReply _reply = new ProtocolPingReply();
        private byte[] _sendBuffer;
        private KcpTransport _kcp;
        private ulong _sequence;
        private double _nextProbeAt;
        private float _probeInterval = 1f;
        private string _probeText = "你好，Norn";
        private bool _measureCodec;
        private long _maximumCodecTicks;
        private CodecAllocationMeter _allocationMeter;
        private LocalLabSnapshotChannel _snapshotChannel;
        private EncryptionTransport _encryption;
        public bool EncryptionEnabled => _encryption != null;
        public string EncryptionFingerprint => _encryption != null ? _encryption.EncryptionPublicKeyFingerprint : null;
        public bool FingerprintValidationEnabled => _encryption != null && _encryption.ClientValidateServerPubKey == EncryptionTransport.ValidationMode.List;
        private bool _runtimeStateCaptured;
        private bool _priorRunInBackground;
        private int _priorTargetFrameRate;
        public long SentProbes { get; private set; }
        public long ReceivedReplies { get; private set; }
        public long ServedProbes { get; private set; }
        public int ConnectedClients { get; private set; }
        public int MaximumConnectedClients { get; private set; }
        public int DisconnectedClients { get; private set; }
        public long RejectedPreAuthProbes { get; private set; }
        public long SkippedProbes { get; private set; }
        public long CodecOperations { get; private set; }
        public long CodecAllocationEvents { get; private set; }
        public bool CodecAllocationCounterValid => _allocationMeter != null && _allocationMeter.IsValid;
        public long CodecCalibrationEvents => _allocationMeter != null ? _allocationMeter.CalibrationEvents : 0;
        public long DotNetControlBytes => _allocationMeter != null ? _allocationMeter.DotNetControlBytes : -1;
        public bool UsesNativeSocket => _kcp is NornKcpTransport measured && measured.UsesNativeSocket;
        public long DroppedDatagrams => _kcp is NornKcpTransport measured ? measured.DroppedDatagrams : 0;
        public void ConfigureLocalLoss(bool enabled)
        {
            if (!(_kcp is NornKcpTransport measured) || !measured.UsesNativeSocket)
                throw new InvalidOperationException("丢包实验需要 Windows loopback 适配器。");
            measured.ConfigureLabLoss(enabled);
        }
        public bool TransportAllocationCounterValid => _kcp is NornKcpTransport measured && measured.AllocationCounterValid;
        public long TransportFrameAllocationEvents => _kcp is NornKcpTransport measured ? measured.FrameAllocationEvents : -1;
        public long TransportFrameMeasuredCalls => _kcp is NornKcpTransport measured ? measured.FrameMeasuredCalls : 0;
        public double MaximumCodecMicroseconds => _maximumCodecTicks * (1000000d / Stopwatch.Frequency);
        public double LastReplyAt { get; private set; }
        public string Status { get; private set; } = "Stopped";
        public bool IsConfigured { get; private set; }

        public override void Awake()
        {
            // 只做一次分配；KCP 自身的 Awake 也必须在 StartServer/StartClient 前完成。
            _priorRunInBackground = Application.runInBackground;
            _priorTargetFrameRate = Application.targetFrameRate;
            _runtimeStateCaptured = true;
            _kcp = GetComponent<KcpTransport>();
            _snapshotChannel = GetComponent<LocalLabSnapshotChannel>();
            transport = _kcp;
            autoCreatePlayer = false;
            headlessStartMode = HeadlessStartOptions.DoNothing;
            editorAutoStart = false;
            _sendBuffer = new byte[ProtocolCompatibility.MaxPingPayloadBytes];
            base.Awake();
        }

        /// <summary>上层组合层读取 SO 后传入纯配置值，Networking 不反向依赖 Multiplayer。</summary>
        public void ConfigureLocalLab(ushort port, int players, int poolCapacity, float probeInterval, int sendFrequency,
            string probeText = "你好，Norn", double messagesPerSecond = 20, int burstCapacity = 4)
        {
            if (NetworkServer.active || NetworkClient.active) throw new InvalidOperationException("运行中不能重配 Transport。");
            if (port == 0 || players < 1 || players > 4 || !float.IsFinite(probeInterval) || probeInterval < 1f / 60 ||
                sendFrequency < 1 || sendFrequency > 60 || probeText == null)
                throw new ArgumentOutOfRangeException(nameof(port), "无效实验配置。");
            _kcp.Port = port;
            _kcp.DualMode = false;
            networkAddress = "127.0.0.1";
            maxConnections = players;
            sendRate = sendFrequency;
            // Mirror 的时间插值缓存与项目 Snapshot 是不同对象；在启动边界预留其有界容量。
            if (NetworkClient.snapshotSettings.bufferLimit > 64) throw new InvalidOperationException("本地实验最多支持 64 项时间缓存。");
            if (NetworkClient.snapshots.Capacity < 64) NetworkClient.snapshots.Capacity = 64;
            _probeInterval = probeInterval;
            _probeText = probeText;
            _codec = new ProtoMessageCodec(new ProtoMessagePool(poolCapacity));
            _sessions = new ConnectionSession[players];
            for (int i = 0; i < players; i++) _sessions[i] = new ConnectionSession(messagesPerSecond, burstCapacity);
            IsConfigured = true;
            Status = "Configured: loopback only";
        }

        public override void OnStartServer()
        {
            if (!IsConfigured) throw new InvalidOperationException("先装配本地实验配置再启动服务器。");
            // false 只用于当前认证前探测；Mirror 的连接握手不是业务账号认证。
            NetworkServer.RegisterHandler<ProtobufEnvelope>(OnServerEnvelope, false);
            if (_snapshotChannel != null) _snapshotChannel.StartServer();
            ServedProbes = 0;
            ConnectedClients = MaximumConnectedClients = DisconnectedClients = 0;
            RejectedPreAuthProbes = 0;
            Status = "Dedicated server listening";
            GameLog.Info("本地 KCP 实验服务已启动；不是正式账号/副本准入。", "OnlineLab");
        }

        public override void OnServerConnect(NetworkConnectionToClient connection)
        {
            // KCP 默认绑定所有接口；连接准入再次限制回环，不应据此部署公网。
            // 使用公开连接地址，不越过 KcpTransport 对 server 实例的 protected 边界。
            IPAddress.TryParse(connection.address, out IPAddress address);
            // DualMode socket 可能用 IPv4-mapped IPv6 表示 127.0.0.1；先归一化再判断。
            if (address != null && address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address == null || !IPAddress.IsLoopback(address)) { connection.Disconnect(); return; }
            for (int i = 0; i < _sessions.Length; i++)
            {
                if (_sessions[i].ConnectionId >= 0) continue;
                _sessions[i].Attach(connection.connectionId);
                ConnectedClients++;
                MaximumConnectedClients = Math.Max(MaximumConnectedClients, ConnectedClients);
                GameLog.Info("本地实验客户端已连接。", "OnlineLab");
                return;
            }
            connection.Disconnect();
        }

        public override void OnServerDisconnect(NetworkConnectionToClient connection)
        {
            ConnectionSession session = FindSession(connection.connectionId);
            if (session != null) { ConnectedClients--; DisconnectedClients++; }
            session?.Clear();
            base.OnServerDisconnect(connection);
            // 只清理离线连接，绝不因一个客户端退出而调用 StopServer。
            GameLog.Info("一个客户端离线，副本服继续监听。", "OnlineLab");
        }

        public override void OnStartClient()
        {
            if (!IsConfigured) throw new InvalidOperationException("先装配本地实验配置再连接。");
            NetworkClient.RegisterHandler<ProtobufEnvelope>(OnClientEnvelope, false);
            if (_snapshotChannel != null) _snapshotChannel.StartClient();
            Status = "Connecting";
        }

        public override void OnClientConnect()
        {
            base.OnClientConnect();
            _sequence = 0;
            SentProbes = 0;
            ReceivedReplies = 0;
            SkippedProbes = 0;
            _nextProbeAt = Time.realtimeSinceStartupAsDouble;
            Status = "Connected: transport only";
        }

        public override void Update()
        {
            base.Update();
            // 有界补跑维持平均发送频率；卡顿后最多补三条，剩余记录为丢失发送机会，不制造无限追债。
            if (ClientCanProbe && IsConfigured)
            {
                // 本轮调度只观察同一时间快照：若循环退出后再读时钟，刚到期的下一条会被误记为跳过。
                // 超过三条的真实积压仍丢弃并计数，避免卡顿后无限补发；不降低限流或验收条件。
                double now = Time.realtimeSinceStartupAsDouble;
                for (int i = 0; i < 3 && now >= _nextProbeAt; i++) TrySendProbe();
                if (now >= _nextProbeAt)
                {
                    SkippedProbes += (long)((now - _nextProbeAt) / _probeInterval) + 1;
                    _nextProbeAt = now + _probeInterval;
                }
            }
        }

        public void TrySendProbe()
        {
            if (!ClientCanProbe || Time.realtimeSinceStartupAsDouble < _nextProbeAt) return;
            _request.ProtocolVersion = ProtocolCompatibility.CurrentVersion;
            _request.Sequence = ++_sequence;
            _request.Message = _probeText;
            if (!Encode(RealtimeMessageKind.PingRequest, _request, out int size)) return;
            NetworkClient.Send(new ProtobufEnvelope { Kind = RealtimeMessageKind.PingRequest,
                Payload = new ArraySegment<byte>(_sendBuffer, 0, size) }, Channels.Reliable);
            // Mirror Send 在返回前已将字节复制到自己的批处理缓冲，下一次才能安全复用数组。
            SentProbes++;
            _nextProbeAt += _probeInterval;
        }

        private void OnServerEnvelope(NetworkConnectionToClient connection, ProtobufEnvelope envelope)
        {
            ConnectionSession session = FindSession(connection.connectionId);
            if (session == null || envelope.Kind != RealtimeMessageKind.PingRequest ||
                !Decode(envelope, out ProtocolMessageLease lease))
            {
                if (session == null && !connection.isAuthenticated) RejectedPreAuthProbes++;
                connection.Disconnect(); return;
            }
            using (lease)
            {
                ProtocolPingRequest request = lease.Request;
                if (!session.TryAccept(request.Sequence, Time.realtimeSinceStartupAsDouble)) { connection.Disconnect(); return; }
                _reply.ProtocolVersion = ProtocolCompatibility.CurrentVersion;
                _reply.Sequence = request.Sequence;
                _reply.Message = request.Message;
                if (!Encode(RealtimeMessageKind.PingReply, _reply, out int size)) return;
                connection.Send(new ProtobufEnvelope { Kind = RealtimeMessageKind.PingReply,
                    Payload = new ArraySegment<byte>(_sendBuffer, 0, size) }, Channels.Reliable);
                ServedProbes++;
            }
        }

        private void OnClientEnvelope(ProtobufEnvelope envelope)
        {
            if (envelope.Kind != RealtimeMessageKind.PingReply ||
                !Decode(envelope, out ProtocolMessageLease lease))
            { NetworkClient.Disconnect(); return; }
            using (lease)
            {
                if (lease.Reply.Sequence > _sequence || lease.Reply.Message != _request.Message)
                { NetworkClient.Disconnect(); return; }
                ReceivedReplies++;
                LastReplyAt = Time.realtimeSinceStartupAsDouble;
            }
        }

        private ConnectionSession FindSession(int connectionId)
        {
            for (int i = 0; i < _sessions.Length; i++)
                if (_sessions[i].ConnectionId == connectionId) return _sessions[i];
            return null;
        }
        private static bool ClientCanProbe => NetworkClient.isConnected && NetworkClient.connection != null && NetworkClient.connection.isAuthenticated;
        public bool TryGetAcceptedProbeSequence(int connectionId, out ulong sequence)
        {
            ConnectionSession session = FindSession(connectionId);
            sequence = session != null ? session.LastSequence : 0;
            return session != null;
        }
        public void ConfigureLocalEncryption(string expectedFingerprint, bool server)
        {
            if (NetworkServer.active || NetworkClient.active) throw new InvalidOperationException("必须在启动前配置实验加密。");
            if (!server && string.IsNullOrEmpty(expectedFingerprint)) throw new InvalidOperationException("客户端必须获得预期服务器指纹。");
            _encryption = gameObject.AddComponent<EncryptionTransport>();
            _encryption.Inner = _kcp;
            _encryption.ServerLoadKeyPairFromFile = false;
            _encryption.ClientValidateServerPubKey = EncryptionTransport.ValidationMode.List;
            _encryption.ClientTrustedPubKeySignatures = server ? Array.Empty<string>() : new[] { expectedFingerprint };
            transport = _encryption;
            Transport.active = _encryption;
        }

        public void PrepareCodecMeasurement()
        {
            // 校准含一次故意的数组分配，必须在预热前准备，不能污染稳态全帧采样。
            if (_allocationMeter == null) _allocationMeter = new CodecAllocationMeter();
        }

        public void BeginCodecMeasurement()
        {
            if (_allocationMeter == null) throw new InvalidOperationException("先在启动阶段准备并校准 Codec 记录器。");
            CodecOperations = CodecAllocationEvents = _maximumCodecTicks = 0;
            if (_kcp is NornKcpTransport measured) measured.BeginMeasurement();
            _measureCodec = true;
        }

        private bool Encode(RealtimeMessageKind kind, Google.Protobuf.IMessage message, out int size)
        {
            if (!_measureCodec) return _codec.TryEncode(kind, message, new ArraySegment<byte>(_sendBuffer), out size);
            _allocationMeter.Begin();
            long started = Stopwatch.GetTimestamp();
            bool accepted = _codec.TryEncode(kind, message, new ArraySegment<byte>(_sendBuffer), out size);
            RecordCodec(started);
            return accepted;
        }

        private bool Decode(ProtobufEnvelope envelope, out ProtocolMessageLease lease)
        {
            if (!_measureCodec) return _codec.TryDecode(envelope.Kind, envelope.Payload, out lease, out _);
            _allocationMeter.Begin();
            long started = Stopwatch.GetTimestamp();
            bool accepted = _codec.TryDecode(envelope.Kind, envelope.Payload, out lease, out _);
            RecordCodec(started);
            return accepted;
        }

        private void RecordCodec(long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            long events = _allocationMeter.End();
            // 事件只包围 Codec 主线程调用；invalid 用 -1 表示未知，绝不能继续累计成可信零值。
            CodecAllocationEvents = CodecAllocationEvents < 0 || events < 0 || !_allocationMeter.IsValid ? -1 : CodecAllocationEvents + events;
            CodecOperations++;
            _maximumCodecTicks = Math.Max(_maximumCodecTicks, elapsed);
        }

        public override void OnClientDisconnect() { Status = "Disconnected"; base.OnClientDisconnect(); }
        public override void OnClientError(TransportError error, string reason) { Status = "Transport error"; GameLog.Warn(reason, "OnlineLab"); }
        public override void OnStopClient()
        {
            NetworkClient.UnregisterHandler<ProtobufEnvelope>();
            if (_snapshotChannel != null) _snapshotChannel.StopClient();
        }
        public override void OnStopServer()
        {
            NetworkServer.UnregisterHandler<ProtobufEnvelope>();
            if (_snapshotChannel != null) _snapshotChannel.StopServer();
            if (_sessions != null) for (int i = 0; i < _sessions.Length; i++) _sessions[i].Clear();
            Status = "Stopped";
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            _allocationMeter?.Dispose();
            if (!_runtimeStateCaptured) return;
            // Mirror 会设置后台运行和 Headless 帧率；离开实验/退出 Play 时恢复原值，避免污染单机。
            Application.runInBackground = _priorRunInBackground;
            Application.targetFrameRate = _priorTargetFrameRate;
        }
    }
}
