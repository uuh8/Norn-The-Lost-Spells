using System;
using System.Text;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>解码失败分类；可用于计数，避免每个坏包创建诊断字符串。</summary>
    public enum ProtocolDecodeFailure
    {
        None, // 结构与当前应用约束均通过，仍不表示账号已认证。
        UnknownKind, // 没有注册这个消息种类，或发送类型与种类不匹配。
        InvalidLength, // 空数据或整个 Payload 超过预算。
        PoolExhausted, // 固定池当前无可用租借，不能偷偷扩容。
        MalformedPayload, // Protobuf 二进制损坏或截断。
        VersionMismatch, // 我们的应用协议版本不兼容。
        TextTooLong // 字符串的 UTF-8 字节超过业务预算。
    }

    /// <summary>有界消息适配层；不处理身份认证、不访问玩法状态，也不负责网络传输。</summary>
    public sealed class ProtoMessageCodec
    {
        private readonly ProtoMessagePool _pool;
        public ProtoMessageCodec(ProtoMessagePool pool) => _pool = pool ?? throw new ArgumentNullException(nameof(pool));

        public bool TryDecode(RealtimeMessageKind kind, ArraySegment<byte> payload, out ProtocolMessageLease lease, out ProtocolDecodeFailure failure)
        {
            lease = default;
            failure = ProtocolDecodeFailure.None;
            if (!IsKnownKind(kind)) { failure = ProtocolDecodeFailure.UnknownKind; return false; }
            if (payload.Array == null || payload.Count == 0 || payload.Count > ProtocolCompatibility.MaxPingPayloadBytes)
            { failure = ProtocolDecodeFailure.InvalidLength; return false; }
            if (!_pool.TryRent(kind, out lease)) { failure = ProtocolDecodeFailure.PoolExhausted; return false; }

            bool accepted = false;
            try
            {
                // MergeFrom 写入池中对象；空缺字段不会自动清除，所以 Return 必须完整重置。
                // Span 只在本次回调使用，不把 Transport 的临时接收缓冲区跨帧保存。
                lease.Message.MergeFrom(new ReadOnlySpan<byte>(payload.Array, payload.Offset, payload.Count));
                if (!Validate(kind, lease.Message, out failure)) return false;
                accepted = true;
                return true;
            }
            catch (InvalidProtocolBufferException)
            {
                failure = ProtocolDecodeFailure.MalformedPayload;
                return false;
            }
            finally
            {
                // 不论畸形数据、版本错误还是业务预算失败，都不泄漏池的容量。
                if (!accepted) { lease.Dispose(); lease = default; }
            }
        }

        public bool TryEncode(RealtimeMessageKind kind, IMessage message, ArraySegment<byte> destination, out int written)
        {
            written = 0;
            if (!Validate(kind, message, out _) || destination.Array == null) return false;
            int size = message.CalculateSize();
            if (size <= 0 || size > ProtocolCompatibility.MaxPingPayloadBytes || size > destination.Count) return false;
            // 使用调用方预分配的缓冲区；Span 精确限定长度，避免写出历史尾部字节。
            message.WriteTo(new Span<byte>(destination.Array, destination.Offset, size));
            written = size;
            return true;
        }

        public static bool IsKnownKind(RealtimeMessageKind kind) =>
            kind == RealtimeMessageKind.PingRequest || kind == RealtimeMessageKind.PingReply;

        private static bool Validate(RealtimeMessageKind kind, IMessage message, out ProtocolDecodeFailure failure)
        {
            uint version;
            string text;
            if (kind == RealtimeMessageKind.PingRequest && message is ProtocolPingRequest request)
            { version = request.ProtocolVersion; text = request.Message; }
            else if (kind == RealtimeMessageKind.PingReply && message is ProtocolPingReply reply)
            { version = reply.ProtocolVersion; text = reply.Message; }
            else { failure = ProtocolDecodeFailure.UnknownKind; return false; }
            if (!ProtocolCompatibility.IsSupported(version)) { failure = ProtocolDecodeFailure.VersionMismatch; return false; }
            if (Encoding.UTF8.GetByteCount(text) > ProtocolCompatibility.MaxPingMessageBytes)
            { failure = ProtocolDecodeFailure.TextTooLong; return false; }
            failure = ProtocolDecodeFailure.None;
            return true;
        }
    }
}
