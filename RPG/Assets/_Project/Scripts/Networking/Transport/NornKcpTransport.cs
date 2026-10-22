using System;
using System.Net;
using kcp2k;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>保留 kcp2k 协议，只替换 Windows IPv4 loopback Raw IO；其他平台仍走原 KCP。</summary>
    public sealed class NornKcpTransport : KcpTransport
    {
        private CodecAllocationMeter _meter;
        private bool _measuring;
        private readonly DatagramLossGate _loss = new DatagramLossGate();
        public long DroppedDatagrams => _loss.Dropped;
        public void ConfigureLabLoss(bool enabled) => _loss.Enabled = enabled;
        private int _frameIndex = -1;
        public bool UsesNativeSocket { get; private set; }
        public long AllocationEvents { get; private set; }
        public long MeasuredCalls { get; private set; }
        public long FrameAllocationEvents { get; private set; }
        public long FrameMeasuredCalls { get; private set; }
        public bool AllocationCounterValid => _meter != null && _meter.IsValid;

        protected override void Awake()
        {
            base.Awake();
            _meter = new CodecAllocationMeter(4096); // 整个 Transport callback 范围比单条 Codec 更大，仍固定上限。
            UsesNativeSocket = !DualMode && WindowsDatagramIo.SupportsNativePlatform(Application.platform);
            if (!UsesNativeSocket) return;
            client = new NativeClient(() => OnClientConnected.Invoke(),
                (data, channel) => OnClientDataReceived.Invoke(data, FromKcpChannel(channel)),
                () => OnClientDisconnected?.Invoke(),
                (error, reason) => OnClientError?.Invoke(ToTransportError(error), reason), config, _loss);
            server = new NativeServer((id, endpoint) => OnServerConnectedWithAddress.Invoke(id, endpoint.Address.ToString()),
                (id, data, channel) => OnServerDataReceived.Invoke(id, data, FromKcpChannel(channel)),
                id => OnServerDisconnected.Invoke(id),
                (id, error, reason) => OnServerError.Invoke(id, ToTransportError(error), reason), config, _loss);
        }

        public void BeginMeasurement() { AllocationEvents = MeasuredCalls = 0; _measuring = true; }
        // Dedicated Server 有独立 RuntimePlatform 值，不能只枚举 WindowsPlayer 后误走普通 Socket。
        private void BeginScope()
        {
            if (!_measuring) return;
            if (_frameIndex != Time.frameCount)
            {
                _frameIndex = Time.frameCount;
                FrameAllocationEvents = FrameMeasuredCalls = 0;
            }
            _meter.Begin();
        }
        private void EndScope()
        {
            if (!_measuring) return;
            long events = _meter.End();
            AllocationEvents = events < 0 || AllocationEvents < 0 || !_meter.IsValid ? -1 : AllocationEvents + events;
            FrameAllocationEvents = events < 0 || FrameAllocationEvents < 0 || !_meter.IsValid ? -1 : FrameAllocationEvents + events;
            MeasuredCalls++;
            FrameMeasuredCalls++;
        }
        public override void ClientEarlyUpdate() { BeginScope(); base.ClientEarlyUpdate(); EndScope(); }
        public override void ClientLateUpdate() { BeginScope(); base.ClientLateUpdate(); EndScope(); }
        public override void ServerEarlyUpdate() { BeginScope(); base.ServerEarlyUpdate(); EndScope(); }
        public override void ServerLateUpdate() { BeginScope(); base.ServerLateUpdate(); EndScope(); }
        private void OnDestroy() => _meter?.Dispose();

        private sealed class NativeClient : KcpClient
        {
            private readonly WindowsDatagramIo _io;
            private readonly DatagramLossGate _loss;
            public NativeClient(Action connected, Action<ArraySegment<byte>, KcpChannel> data, Action disconnected,
                Action<ErrorCode, string> error, KcpConfig config, DatagramLossGate loss) : base(connected, data, disconnected, error, config)
            { _io = new WindowsDatagramIo(config.Mtu); _loss = loss; }
            protected override bool RawReceive(out ArraySegment<byte> data)
            {
                data = default;
                if (socket == null || !_io.TryReceive(socket, rawReceiveBuffer, out int size)) return false;
                data = new ArraySegment<byte>(rawReceiveBuffer, 0, size); return true;
            }
            protected override void RawSend(ArraySegment<byte> data) { if (socket != null && !_loss.ShouldDrop()) _io.TrySend(socket, data); }
        }

        private sealed class NativeServer : KcpServer
        {
            private readonly WindowsDatagramIo _io;
            private readonly DatagramLossGate _loss;
            private readonly IPEndPoint _lookup = new IPEndPoint(IPAddress.Loopback, 0);
            private readonly IPEndPoint[] _endpoints = new IPEndPoint[8];
            public NativeServer(Action<int, IPEndPoint> connected, Action<int, ArraySegment<byte>, KcpChannel> data,
                Action<int> disconnected, Action<int, ErrorCode, string> error, KcpConfig config, DatagramLossGate loss)
                : base(connected, data, disconnected, error, config)
            {
                _io = new WindowsDatagramIo(config.Mtu);
                _loss = loss;
                for (int i = 0; i < _endpoints.Length; i++) _endpoints[i] = new IPEndPoint(IPAddress.Loopback, 0);
            }
            protected override bool RawReceiveFrom(out ArraySegment<byte> data, out int connectionId)
            {
                data = default; connectionId = 0;
                if (socket == null || !_io.TryReceiveFrom(socket, rawReceiveBuffer, out int size)) return false;
                _lookup.Port = _io.SourcePort;
                connectionId = Common.ConnectionHash(_lookup);
                if (connections.TryGetValue(connectionId, out KcpServerConnection connection))
                {
                    if (((IPEndPoint)connection.remoteEndPoint).Port != _io.SourcePort) return false;
                    newClientEP = connection.remoteEndPoint;
                }
                else
                {
                    IPEndPoint available = null;
                    for (int i = 0; i < _endpoints.Length; i++)
                        if (!connections.ContainsKey(Common.ConnectionHash(_endpoints[i]))) { available = _endpoints[i]; break; }
                    if (available == null) return false;
                    // 仅复用没有活跃连接引用的槽位；不能把所有连接指向同一个可变 EndPoint。
                    available.Port = _io.SourcePort; newClientEP = available;
                }
                data = new ArraySegment<byte>(rawReceiveBuffer, 0, size); return true;
            }
            protected override void RawSend(int connectionId, ArraySegment<byte> data)
            {
                if (socket != null && !_loss.ShouldDrop() && connections.TryGetValue(connectionId, out KcpServerConnection connection))
                    _io.TrySendTo(socket, data, (ushort)((IPEndPoint)connection.remoteEndPoint).Port);
            }
        }
    }
}
