using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>同步非阻塞的 loopback Datagram 边界；Socket 的创建/关闭仍由 kcp2k 负责。</summary>
    public sealed class WindowsDatagramIo
    {
        public static bool SupportsNativePlatform(RuntimePlatform platform) => platform == RuntimePlatform.WindowsEditor ||
            platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.WindowsServer;
        private readonly byte[] _sendScratch;
        private readonly byte[] _sourceAddress = new byte[16];
        private readonly byte[] _destinationAddress = new byte[16];
        public ushort SourcePort { get; private set; }
        public long Errors { get; private set; }
        public WindowsDatagramIo(int maximumDatagram)
        {
            if (maximumDatagram < 1 || maximumDatagram > 65507) throw new ArgumentOutOfRangeException(nameof(maximumDatagram));
            _sendScratch = new byte[maximumDatagram];
            // Windows sockaddr_in：family 为主机序，port 为网络大端，IPv4 按网络字节顺序存放。
            _destinationAddress[0] = 2;
            _destinationAddress[4] = 127; _destinationAddress[7] = 1;
        }
        public bool TryReceiveFrom(Socket socket, byte[] buffer, out int received)
        {
            received = 0;
            int size = _sourceAddress.Length;
            int result = ReceiveFrom(socket.Handle, buffer, buffer.Length, 0, _sourceAddress, ref size);
            if (!AcceptResult(result) || result == 0 || size != 16 || _sourceAddress[0] != 2 || _sourceAddress[1] != 0 ||
                _sourceAddress[4] != 127 || _sourceAddress[5] != 0 || _sourceAddress[6] != 0 || _sourceAddress[7] != 1) return false;
            received = result;
            SourcePort = (ushort)((_sourceAddress[2] << 8) | _sourceAddress[3]);
            return true;
        }
        public bool TryReceive(Socket socket, byte[] buffer, out int received)
        {
            received = 0;
            int result = Receive(socket.Handle, buffer, buffer.Length, 0);
            if (!AcceptResult(result) || result == 0) return false;
            received = result;
            return true;
        }
        public bool TrySendTo(Socket socket, ArraySegment<byte> data, ushort port)
        {
            if (!PrepareSend(data)) return false;
            _destinationAddress[2] = (byte)(port >> 8); _destinationAddress[3] = (byte)port;
            int result = SendTo(socket.Handle, _sendScratch, data.Count, 0, _destinationAddress, 16);
            return AcceptResult(result) && result == data.Count;
        }
        public bool TrySend(Socket socket, ArraySegment<byte> data)
        {
            if (!PrepareSend(data)) return false;
            int result = Send(socket.Handle, _sendScratch, data.Count, 0);
            return AcceptResult(result) && result == data.Count;
        }
        private bool PrepareSend(ArraySegment<byte> data)
        {
            if (data.Array == null || data.Count <= 0 || data.Count > _sendScratch.Length) return false;
            // 明确支持非零 offset；用固定 MTU 缓冲换取安全的 blittable 数组封送，不暴露裸指针。
            Buffer.BlockCopy(data.Array, data.Offset, _sendScratch, 0, data.Count);
            return true;
        }
        private bool AcceptResult(int result)
        {
            if (result >= 0) return true;
            // 非阻塞无数据是正常状态，不通过创建 SocketException 表达，也不逐包拼错误日志。
            if (GetLastError() != 10035) Errors++;
            return false;
        }
        // Socket 已由 .NET/kcp2k 初始化 Winsock；这里只同步借用 Handle，绝不另行关闭或缓存它。
        [DllImport("ws2_32.dll", EntryPoint = "recvfrom", CallingConvention = CallingConvention.Winapi)]
        private static extern int ReceiveFrom(IntPtr socket, [Out] byte[] buffer, int length, int flags, [Out] byte[] source, ref int sourceLength);
        [DllImport("ws2_32.dll", EntryPoint = "recv", CallingConvention = CallingConvention.Winapi)]
        private static extern int Receive(IntPtr socket, [Out] byte[] buffer, int length, int flags);
        [DllImport("ws2_32.dll", EntryPoint = "sendto", CallingConvention = CallingConvention.Winapi)]
        private static extern int SendTo(IntPtr socket, byte[] buffer, int length, int flags, byte[] destination, int destinationLength);
        [DllImport("ws2_32.dll", EntryPoint = "send", CallingConvention = CallingConvention.Winapi)]
        private static extern int Send(IntPtr socket, byte[] buffer, int length, int flags);
        [DllImport("ws2_32.dll", EntryPoint = "WSAGetLastError", CallingConvention = CallingConvention.Winapi)]
        private static extern int GetLastError();
    }
}
