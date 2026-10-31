using System;
using System.Collections.Generic;
using System.IO;
using Game.Networking;
using Mirror;
using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    /// <summary>测量实际Mirror消息身份与边界；协议Codec通过不代表外层长度读写和消息ID一定安全。</summary>
    public sealed class DungeonProgressEnvelopeTests
    {
        [Test]
        public void ProgressMessageIdDoesNotCollideWithBuiltinsOrProductionEnvelopes()
        {
            var ids = new Dictionary<ushort, Type>();
            foreach (var assembly in new[] { typeof(NetworkManager).Assembly, typeof(RoomNetworkManager).Assembly,
                         typeof(DungeonProgressEnvelope).Assembly })
                foreach (var type in assembly.GetTypes())
                    if (type.IsValueType && !type.ContainsGenericParameters && typeof(NetworkMessage).IsAssignableFrom(type) && type != typeof(DungeonProgressEnvelope))
                        ids[type.FullName.GetStableHashCode16()] = type;
            ushort id = typeof(DungeonProgressEnvelope).FullName.GetStableHashCode16();
            Assert.That(ids.TryGetValue(id, out var other), Is.False, "副本进度ID碰撞：" + other?.FullName);
        }

        [Test]
        public void EnvelopeBorrowsOnlyTheDeclaredBoundedSegment()
        {
            byte[] bytes = { 9, 1, 2, 3, 9 }; using var writer = NetworkWriterPool.Get();
            writer.WriteDungeonProgressEnvelope(new DungeonProgressEnvelope { Payload = new ArraySegment<byte>(bytes, 1, 3) });
            var reader = new NetworkReader(writer.ToArraySegment()); var result = reader.ReadDungeonProgressEnvelope();
            Assert.That(result.Payload.Count, Is.EqualTo(3)); Assert.That(result.Payload.Array[result.Payload.Offset], Is.EqualTo(1));
            Assert.That(reader.Remaining, Is.Zero);
        }

        [TestCase(0u, 0)] [TestCase(257u, 0)] [TestCase(3u, 1)]
        public void EnvelopeRejectsInvalidLengthBeforeBorrowingPayload(uint declared, int available)
        {
            using var writer = NetworkWriterPool.Get(); writer.WriteUInt(declared);
            for (int i = 0; i < available; i++) writer.WriteByte(1);
            var reader = new NetworkReader(writer.ToArraySegment());
            Assert.Throws<InvalidDataException>(() => reader.ReadDungeonProgressEnvelope());
        }
    }
}
