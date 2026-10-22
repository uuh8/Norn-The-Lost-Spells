using System;
using Game.NetworkProtocol;
using Google.Protobuf;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>直接测试正式 Codec/Pool 源码；内存往返不冒充 KCP 进程通信。</summary>
    public sealed class ProtoCodecTests
    {
        private static ProtocolPingRequest Request(uint version = 1, string text = "你好，Norn 👋") =>
            new ProtocolPingRequest { ProtocolVersion = version, Sequence = ulong.MaxValue, Message = text };

        private static bool Decode(ProtoMessageCodec codec, ProtocolPingRequest message,
            out ProtocolMessageLease lease, out ProtocolDecodeFailure failure) =>
            codec.TryDecode(RealtimeMessageKind.PingRequest, new ArraySegment<byte>(message.ToByteArray()), out lease, out failure);

        [Test]
        public void UnicodeAndLargeSequence_RoundTrip()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            Assert.That(Decode(codec, Request(), out var lease, out var error), Is.True);
            using (lease)
            {
                Assert.That(error, Is.EqualTo(ProtocolDecodeFailure.None));
                Assert.That(lease.Request.Message, Is.EqualTo(Request().Message));
                Assert.That(lease.Request.Sequence, Is.EqualTo(ulong.MaxValue));
            }
        }

        [Test]
        public void ProtocolVersionZero_IsRejected()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            Assert.That(Decode(codec, Request(0), out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ProtocolDecodeFailure.VersionMismatch));
        }

        [Test]
        public void ProtocolVersionMismatch_IsRejected()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            Assert.That(Decode(codec, Request(2), out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ProtocolDecodeFailure.VersionMismatch));
        }

        [Test]
        public void OversizedPayload_IsRejectedBeforeRenting()
        {
            var pool = new ProtoMessagePool(1);
            var codec = new ProtoMessageCodec(pool);
            Assert.That(codec.TryDecode(RealtimeMessageKind.PingRequest,
                new ArraySegment<byte>(new byte[1025]), out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ProtocolDecodeFailure.InvalidLength));
            Assert.That(pool.TryRent(RealtimeMessageKind.PingRequest, out var lease), Is.True);
            lease.Dispose();
        }

        [Test]
        public void EmptyPayload_IsRejected()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            Assert.That(codec.TryDecode(RealtimeMessageKind.PingRequest, default, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ProtocolDecodeFailure.InvalidLength));
        }

        [Test]
        public void UnknownKind_IsRejected()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            Assert.That(codec.TryDecode((RealtimeMessageKind)999, new ArraySegment<byte>(Request().ToByteArray()), out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ProtocolDecodeFailure.UnknownKind));
        }

        [Test]
        public void TruncatedPayload_IsRejectedAndReturned()
        {
            var pool = new ProtoMessagePool(1);
            var codec = new ProtoMessageCodec(pool);
            Assert.That(codec.TryDecode(RealtimeMessageKind.PingRequest, new ArraySegment<byte>(new byte[] { 0x1A, 0x7F, 0x41 }), out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ProtocolDecodeFailure.MalformedPayload));
            Assert.That(pool.TryRent(RealtimeMessageKind.PingRequest, out var lease), Is.True);
            lease.Dispose();
        }

        [Test]
        public void RejectedVersion_DoesNotLeakPoolSlot()
        {
            var pool = new ProtoMessagePool(1);
            var codec = new ProtoMessageCodec(pool);
            Decode(codec, Request(2), out _, out _);
            Assert.That(pool.TryRent(RealtimeMessageKind.PingReply, out var lease), Is.True);
            lease.Dispose();
        }

        [Test]
        public void PoolExhaustion_IsExplicit()
        {
            var pool = new ProtoMessagePool(1);
            Assert.That(pool.TryRent(RealtimeMessageKind.PingRequest, out var held), Is.True);
            try
            {
                var codec = new ProtoMessageCodec(pool);
                Assert.That(Decode(codec, Request(), out _, out var error), Is.False);
                Assert.That(error, Is.EqualTo(ProtocolDecodeFailure.PoolExhausted));
            }
            finally { held.Dispose(); }
        }

        [Test]
        public void ReusedMessage_DoesNotKeepPreviousFields()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            Assert.That(Decode(codec, Request(), out var first, out _), Is.True);
            first.Dispose();
            // 第二次没有传文本和序号；MergeFrom 本身不会清理第一次的字段。
            Assert.That(Decode(codec, new ProtocolPingRequest { ProtocolVersion = 1 }, out var second, out _), Is.True);
            using (second)
            {
                Assert.That(second.Request.Message, Is.Empty);
                Assert.That(second.Request.Sequence, Is.Zero);
            }
        }

        [Test]
        public void ReusedMessage_DoesNotKeepUnknownFields()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            byte[] original = Request().ToByteArray();
            byte[] unknown = new byte[original.Length + 3];
            Array.Copy(original, unknown, original.Length);
            // 添加编号 100 的未知 varint 字段；重置必须同时清理 UnknownFields。
            unknown[original.Length] = 0xA0; unknown[original.Length + 1] = 0x06; unknown[original.Length + 2] = 1;
            Assert.That(codec.TryDecode(RealtimeMessageKind.PingRequest, new ArraySegment<byte>(unknown), out var first, out _), Is.True);
            first.Dispose();
            var minimal = new ProtocolPingRequest { ProtocolVersion = 1 };
            Assert.That(Decode(codec, minimal, out var second, out _), Is.True);
            using (second) Assert.That(second.Request.CalculateSize(), Is.EqualTo(minimal.CalculateSize()));
        }

        [Test]
        public void OldLease_DoubleDisposeCannotReleaseNewRental()
        {
            var pool = new ProtoMessagePool(1);
            Assert.That(pool.TryRent(RealtimeMessageKind.PingRequest, out var oldLease), Is.True);
            oldLease.Dispose();
            Assert.That(pool.TryRent(RealtimeMessageKind.PingRequest, out var current), Is.True);
            oldLease.Dispose();
            Assert.That(pool.TryRent(RealtimeMessageKind.PingRequest, out _), Is.False);
            current.Dispose();
        }

        [Test]
        public void DisposedLease_CannotReadReusedMessage()
        {
            var pool = new ProtoMessagePool(1);
            Assert.That(pool.TryRent(RealtimeMessageKind.PingRequest, out var lease), Is.True);
            lease.Dispose();
            Assert.Throws<InvalidOperationException>(() => { _ = lease.Message; });
        }

        [Test]
        public void TextBudget_IsUtf8Bytes()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            Assert.That(Decode(codec, Request(1, new string('汉', 100)), out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo(ProtocolDecodeFailure.TextTooLong));
        }

        [Test]
        public void Encode_UsesCallerBufferAndOffset()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            byte[] buffer = new byte[1030];
            Assert.That(codec.TryEncode(RealtimeMessageKind.PingRequest, Request(), new ArraySegment<byte>(buffer, 3, 1024), out int written), Is.True);
            Assert.That(codec.TryDecode(RealtimeMessageKind.PingRequest, new ArraySegment<byte>(buffer, 3, written), out var lease, out _), Is.True);
            using (lease) Assert.That(lease.Request.Message, Is.EqualTo(Request().Message));
            Assert.That(buffer[0], Is.Zero);
        }

        [Test]
        public void Encode_RejectsWrongMessageKind()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            Assert.That(codec.TryEncode(RealtimeMessageKind.PingReply, Request(), new ArraySegment<byte>(new byte[1024]), out _), Is.False);
        }

        [Test]
        public void Encode_RejectsInsufficientDestination()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            Assert.That(codec.TryEncode(RealtimeMessageKind.PingRequest, Request(), new ArraySegment<byte>(new byte[1]), out _), Is.False);
        }

        [Test]
        public void Reply_HasItsOwnResettableType()
        {
            var codec = new ProtoMessageCodec(new ProtoMessagePool(1));
            var reply = new ProtocolPingReply { ProtocolVersion = 1, Sequence = 3, Message = "reply" };
            Assert.That(codec.TryDecode(RealtimeMessageKind.PingReply, new ArraySegment<byte>(reply.ToByteArray()), out var first, out _), Is.True);
            using (first) Assert.That(first.Reply.Message, Is.EqualTo("reply"));
            Assert.That(codec.TryDecode(RealtimeMessageKind.PingReply, new ArraySegment<byte>(new ProtocolPingReply { ProtocolVersion = 1 }.ToByteArray()), out var second, out _), Is.True);
            using (second) Assert.That(second.Reply.Sequence, Is.Zero);
        }
    }
}
