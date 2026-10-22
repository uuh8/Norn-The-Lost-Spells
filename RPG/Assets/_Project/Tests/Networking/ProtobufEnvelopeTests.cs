using System;
using System.IO;
using Game.NetworkProtocol;
using Mirror;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>测试真实 Mirror Writer/Reader 的边界，确认恶意长度不先进入数组分配。</summary>
    public sealed class ProtobufEnvelopeTests
    {
        [Test]
        public void Envelope_RoundTripsOnlyDeclaredSegment()
        {
            var writer = new NetworkWriter();
            writer.WriteProtobufEnvelope(new ProtobufEnvelope { Kind = RealtimeMessageKind.PingRequest,
                Payload = new ArraySegment<byte>(new byte[] { 9, 1, 2, 9 }, 1, 2) });
            var reader = new NetworkReader(writer.ToArraySegment());
            ProtobufEnvelope decoded = reader.ReadProtobufEnvelope();
            Assert.That(decoded.Kind, Is.EqualTo(RealtimeMessageKind.PingRequest));
            Assert.That(decoded.Payload.Count, Is.EqualTo(2));
            Assert.That(decoded.Payload.Array[decoded.Payload.Offset], Is.EqualTo(1));
            Assert.That(reader.Remaining, Is.Zero);
        }

        [Test]
        public void Envelope_RejectsOversizedDeclaredLength()
        {
            var writer = new NetworkWriter();
            writer.WriteUShort((ushort)RealtimeMessageKind.PingRequest); writer.WriteUInt(uint.MaxValue);
            Assert.Throws<InvalidDataException>(() => new NetworkReader(writer.ToArraySegment()).ReadProtobufEnvelope());
        }

        [Test]
        public void Envelope_RejectsTruncatedHeader()
        {
            Assert.Throws<InvalidDataException>(() => new NetworkReader(new ArraySegment<byte>(new byte[5])).ReadProtobufEnvelope());
        }

        [Test]
        public void Envelope_RejectsTruncatedBody()
        {
            var writer = new NetworkWriter();
            writer.WriteUShort((ushort)RealtimeMessageKind.PingRequest); writer.WriteUInt(10);
            Assert.Throws<InvalidDataException>(() => new NetworkReader(writer.ToArraySegment()).ReadProtobufEnvelope());
        }

        [Test]
        public void Envelope_RejectsUnknownKind()
        {
            var writer = new NetworkWriter(); writer.WriteUShort(999); writer.WriteUInt(1); writer.WriteByte(1);
            Assert.Throws<InvalidDataException>(() => new NetworkReader(writer.ToArraySegment()).ReadProtobufEnvelope());
        }
    }
}
