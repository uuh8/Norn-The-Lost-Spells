using System;
using System.IO;
using Game.NetworkProtocol;
using Google.Protobuf;
using Mirror;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    public sealed class LocalLabProtocolTests
    {
        [Test]
        public void ReusedSnapshotCannotKeepIdentityOrUnknownFields()
        {
            byte[] original = new LocalLabSnapshot { ProtocolVersion = 1, ActorId = 7, ServerTick = 9, LastAcceptedProbeSequence = 5 }.ToByteArray();
            byte[] unknown = new byte[original.Length + 3];
            Array.Copy(original, unknown, original.Length);
            unknown[original.Length] = 0xA0; unknown[original.Length + 1] = 0x06; unknown[original.Length + 2] = 1;
            var reused = new LocalLabSnapshot();
            reused.MergeFrom(unknown);
            reused.ResetForReuse();
            var next = new LocalLabSnapshot { ProtocolVersion = 1 };
            reused.MergeFrom(next.ToByteArray());
            Assert.That(reused.ActorId, Is.Zero);
            Assert.That(reused.ServerTick, Is.Zero);
            Assert.That(reused.LastAcceptedProbeSequence, Is.Zero);
            Assert.That(reused.CalculateSize(), Is.EqualTo(next.CalculateSize()));
        }

        [Test]
        public void SnapshotEnvelopeRejectsOversizeBeforeProtobufParsing()
        {
            var writer = new NetworkWriter();
            writer.WriteUInt(65); writer.WriteBytes(new byte[65], 0, 65);
            var reader = new NetworkReader(writer.ToArraySegment());
            Assert.Throws<InvalidDataException>(() => reader.ReadLocalLabSnapshotEnvelope());
        }
    }
}
