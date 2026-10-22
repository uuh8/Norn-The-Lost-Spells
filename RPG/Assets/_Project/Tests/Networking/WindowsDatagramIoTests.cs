using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Game.Networking.Tests
{
    /// <summary>真实内核 UDP 往返，不用 mock 代替 Socket；预热后验证收发路径的分配事件。</summary>
    public sealed class WindowsDatagramIoTests
    {
        [Test]
        public void WindowsServerPlatformEnablesNativeIo()
        {
            Assert.That(WindowsDatagramIo.SupportsNativePlatform(RuntimePlatform.WindowsServer), Is.True);
            Assert.That(WindowsDatagramIo.SupportsNativePlatform(RuntimePlatform.WindowsPlayer), Is.True);
            Assert.That(WindowsDatagramIo.SupportsNativePlatform(RuntimePlatform.LinuxServer), Is.False);
        }
        [Test]
        public void LoopbackRoundTripUsesBoundedBuffersWithoutSteadyAllocations()
        {
            using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            client.Connect(server.LocalEndPoint);
            server.Blocking = client.Blocking = false;
            var io = new WindowsDatagramIo(1200);
            var send = new ArraySegment<byte>(new byte[] { 9, 42, 17, 9 }, 1, 2);
            byte[] receive = new byte[1200];
            for (int i = 0; i < 32; i++) Assert.That(RoundTrip(io, server, client, send, receive), Is.True);
            using var meter = new CodecAllocationMeter();
            meter.Begin();
            bool all = true;
            for (int i = 0; i < 128; i++) all &= RoundTrip(io, server, client, send, receive);
            long events = meter.End();
            Assert.That(all, Is.True);
            Assert.That(meter.IsValid, Is.True, "单次 128 次往返若持续分配，会先超出事件容量，属于失败证据。");
            Assert.That(events, Is.Zero);
        }

        private static bool RoundTrip(WindowsDatagramIo io, Socket server, Socket client, ArraySegment<byte> send, byte[] receive)
        {
            if (!io.TrySend(client, send)) return false;
            int count = 0;
            bool arrived = false;
            for (int i = 0; i < 10000 && !arrived; i++)
            { arrived = io.TryReceiveFrom(server, receive, out count); if (!arrived) Thread.SpinWait(20); }
            if (!arrived || count != 2 || receive[0] != 42 || receive[1] != 17) return false;
            if (!io.TrySendTo(server, new ArraySegment<byte>(receive, 0, count), io.SourcePort)) return false;
            arrived = false;
            for (int i = 0; i < 10000 && !arrived; i++)
            { arrived = io.TryReceive(client, receive, out count); if (!arrived) Thread.SpinWait(20); }
            return arrived && count == 2 && receive[0] == 42 && receive[1] == 17;
        }
    }
}
