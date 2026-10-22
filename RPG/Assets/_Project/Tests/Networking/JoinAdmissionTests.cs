using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using Game.Networking;
using Mirror;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    public sealed class JoinAdmissionTests
    {
        [Test] public void ReplacedConnection_CannotAdvanceNewSlotSequenceOrConsumeItsBudget()
        {
            var session = new ConnectionSession(10, 4); session.Attach(11); Assert.True(session.TryAccept(11, 1, 1));
            session.Attach(12);
            Assert.False(session.TryAccept(11, 99, 2), "旧连接的迟到包不能污染新连接的序号或速率预算。");
            Assert.AreEqual(0ul, session.LastSequence); Assert.True(session.TryAccept(12, 1, 2));
        }
        [Test] public void RoomMessageIds_DoNotCollideWithMirrorBuiltins()
        {
            var ids = new Dictionary<ushort, Type>();
            foreach (var type in typeof(NetworkManager).Assembly.GetTypes())
                if (type.IsValueType && !type.ContainsGenericParameters && typeof(NetworkMessage).IsAssignableFrom(type)) ids[type.FullName.GetStableHashCode16()] = type;
            // 从实际接收处理器取得Envelope类型，避免测试手写另一个安全类型却漏掉生产注册的类型。
            var payloadType = typeof(RoomNetworkManager).GetMethod("OnPing", BindingFlags.Instance | BindingFlags.NonPublic).GetParameters()[1].ParameterType;
            foreach (var type in new[] { payloadType, typeof(JoinTicketRequestEnvelope), typeof(JoinTicketReplyEnvelope) })
            {
                ushort id = type.FullName.GetStableHashCode16();
                Assert.False(ids.TryGetValue(id, out var existing), type.FullName + " conflicts with " + existing?.FullName + " at " + id);
                ids.Add(id, type);
            }
        }
        [Test] public void OldConnectionEpochCannotReplaceNewAdmission()
        {
            var gate = new JoinConnectionEpochs(); Assert.True(gate.TryAdvance(1, 2));
            Assert.False(gate.TryAdvance(1, 1)); Assert.False(gate.TryAdvance(1, 2)); Assert.True(gate.TryAdvance(1, 3));
            Assert.False(gate.TryAdvance(0, 1)); Assert.False(gate.TryAdvance(5, 1)); Assert.False(gate.TryAdvance(2, 0));
            gate.Clear(); Assert.True(gate.TryAdvance(1, 1));
        }
        [Test] public void EnvelopeRejectsOversizeAndTruncationBeforeAllocatingPayload()
        {
            using var writer = NetworkWriterPool.Get(); writer.WriteUInt(513);
            var reader = new NetworkReader(writer.ToArraySegment()); Assert.Throws<InvalidDataException>(() => reader.ReadJoinTicketRequestEnvelope());
            writer.Reset(); writer.WriteUInt(3); writer.WriteByte(1);
            reader = new NetworkReader(writer.ToArraySegment()); Assert.Throws<InvalidDataException>(() => reader.ReadJoinTicketRequestEnvelope());
        }
        [Test] public void EnvelopeRoundtripPreservesBoundedSegment()
        {
            byte[] bytes = { 9, 1, 2, 3, 9 }; using var writer = NetworkWriterPool.Get();
            writer.WriteJoinTicketRequestEnvelope(new JoinTicketRequestEnvelope { Payload = new ArraySegment<byte>(bytes, 1, 3) });
            var read = new NetworkReader(writer.ToArraySegment()).ReadJoinTicketRequestEnvelope();
            Assert.That(read.Payload.Count, Is.EqualTo(3)); Assert.That(read.Payload.Array[read.Payload.Offset], Is.EqualTo(1));
        }
    }
}
