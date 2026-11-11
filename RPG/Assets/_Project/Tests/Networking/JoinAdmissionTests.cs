using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Game.Networking;
using Game.NetworkProtocol;
using Google.Protobuf;
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
            // Networking.Tests 不反向引用 Multiplayer；反射只解决程序集边界与缺类型 RED，
            // 取得后仍对真实生产类型执行 Mirror 同一套消息 ID 算法，不另造测试消息代替。
            foreach (var type in new[] { payloadType, typeof(JoinTicketRequestEnvelope), typeof(JoinTicketReplyEnvelope), typeof(CombatIntentEnvelope), typeof(CombatFactEnvelope), typeof(MovementInputEnvelope), typeof(MovementStateEnvelope), typeof(WorldEnvelope),
                RequireRuntimeType("Game.Networking.WorldIdentityEnvelope"), RequireRuntimeType("Game.Multiplayer.DungeonProgressEnvelope"),
                RequireRuntimeType("Game.Multiplayer.InventoryCommandEnvelope"), RequireRuntimeType("Game.Multiplayer.InventoryReceiptEnvelope"),
                RequireRuntimeType("Game.Multiplayer.InventorySnapshotEnvelope") })
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

        [Test] public void WorldIdentityCodec_RoundtripUsesAuthenticatedIdentityAndExactBorrowedSegments()
        {
            var identity = Identity();
            var output = new byte[270]; output[6] = 0xA5; output[263] = 0x5A;
            Assert.True(EncodeWorldIdentity(identity, ulong.MaxValue, new ArraySegment<byte>(output, 7, 256), out int written));
            Assert.That(written, Is.InRange(1, 256));
            Assert.That(output[6], Is.EqualTo(0xA5)); Assert.That(output[263], Is.EqualTo(0x5A));
            // 期望字节来自手工 Schema fixture，不由被测 Codec 反向计算，能发现字段/类型接错。
            var expected = WorldIdentityWire(ProtocolCompatibility.CurrentVersion, identity.RunId, identity.DungeonId, 7, 11, ulong.MaxValue);
            CollectionAssert.AreEqual(expected, new ArraySegment<byte>(output, 7, written));
            Assert.True(DecodeWorldIdentity(new ArraySegment<byte>(output, 7, written), identity, out ulong epoch));
            Assert.That(epoch, Is.EqualTo(ulong.MaxValue));
        }

        [TestCase("Run")]
        [TestCase("Dungeon")]
        [TestCase("RoomEpoch")]
        [TestCase("ConnectionEpoch")]
        public void WorldIdentityCodec_RejectsIdentityDifferentFromIndependentAuthentication(string field)
        {
            var wire = WorldIdentityWire();
            var expected = Identity(run: field == "Run" ? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" : null,
                dungeon: field == "Dungeon" ? "p8_boss" : null,
                room: field == "RoomEpoch" ? 8u : 7u, connection: field == "ConnectionEpoch" ? 12u : 11u);
            Assert.False(DecodeWorldIdentity(new ArraySegment<byte>(wire), expected, out ulong epoch));
            Assert.That(epoch, Is.Zero, "拒绝候选不能泄漏看似可用的世界身份。");
        }

        [Test] public void WorldIdentityCodec_RejectsZeroWorldEpochOnBothDirections()
        {
            Assert.False(EncodeWorldIdentity(Identity(), 0, new ArraySegment<byte>(new byte[256]), out int written));
            Assert.That(written, Is.Zero);
            Assert.False(DecodeWorldIdentity(new ArraySegment<byte>(WorldIdentityWire(world: 0)), Identity(), out ulong epoch));
            Assert.That(epoch, Is.Zero);
        }

        [TestCase(1u)]
        [TestCase(2u)]
        public void WorldIdentityCodec_RejectsOldApplicationProtocol(uint version)
        {
            Assert.False(DecodeWorldIdentity(new ArraySegment<byte>(WorldIdentityWire(version: version)), Identity(), out ulong epoch));
            Assert.That(epoch, Is.Zero);
        }

        [TestCase("TruncatedFixed64")]
        [TestCase("UnknownField")]
        [TestCase("DuplicateField")]
        [TestCase("WrongWorldWireType")]
        [TestCase("WrongRunWireType")]
        [TestCase("HugeStringLength")]
        [TestCase("InvalidRunCharacter")]
        [TestCase("LongDungeon")]
        [TestCase("InvalidDungeonCharacter")]
        [TestCase("ZeroRoomEpoch")]
        [TestCase("ZeroConnectionEpoch")]
        [TestCase("MissingWorldEpoch")]
        [TestCase("MissingRun")]
        [TestCase("Uint32Overflow")]
        [TestCase("NonCanonicalVarint")]
        public void WorldIdentityCodec_RejectsMalformedWireBeforeAcceptingCandidate(string corruption)
        {
            var wire = new List<byte>(WorldIdentityWire());
            switch (corruption)
            {
                case "TruncatedFixed64": wire.RemoveAt(wire.Count - 1); break;
                case "UnknownField": wire.AddRange(new byte[] { 0x38, 1 }); break;
                case "DuplicateField": wire.AddRange(new byte[] { 0x20, 7 }); break;
                case "WrongWorldWireType": wire[wire.Count - 9] = 0x30; break;
                case "WrongRunWireType": wire[2] = 0x10; break;
                case "HugeStringLength": wire = new List<byte> { 8, (byte)ProtocolCompatibility.CurrentVersion, 0x12, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }; break;
                case "InvalidRunCharacter": wire[4] = (byte)'G'; break;
                case "LongDungeon": wire = new List<byte>(WorldIdentityWire(dungeon: new string('a', 33))); break;
                case "InvalidDungeonCharacter": wire = new List<byte>(WorldIdentityWire(dungeon: "p7/escape")); break;
                case "ZeroRoomEpoch": wire = new List<byte>(WorldIdentityWire(room: 0)); break;
                case "ZeroConnectionEpoch": wire = new List<byte>(WorldIdentityWire(connection: 0)); break;
                case "MissingWorldEpoch": wire.RemoveRange(wire.Count - 9, 9); break;
                case "MissingRun": wire.RemoveRange(2, 34); break;
                case "Uint32Overflow": wire = new List<byte>(WorldIdentityWire(room: (ulong)uint.MaxValue + 1)); break;
                case "NonCanonicalVarint": wire[1] |= 0x80; wire.Insert(2, 0); break;
                default: Assert.Fail("未知测试 fixture。"); break;
            }
            Assert.False(DecodeWorldIdentity(new ArraySegment<byte>(wire.ToArray()), Identity(), out ulong epoch), corruption);
            Assert.That(epoch, Is.Zero, corruption);
        }

        [TestCase("NullIdentity")]
        [TestCase("InvalidRun")]
        [TestCase("InvalidDungeon")]
        [TestCase("ZeroRoomEpoch")]
        [TestCase("ZeroConnectionEpoch")]
        public void WorldIdentityCodec_RejectsInvalidTrustedInputOnEncodeAndDecode(string invalid)
        {
            var identity = invalid == "NullIdentity" ? null : Identity(run: invalid == "InvalidRun" ? "not-a-run" : null,
                dungeon: invalid == "InvalidDungeon" ? "7wrong" : null,
                room: invalid == "ZeroRoomEpoch" ? 0u : 7u, connection: invalid == "ZeroConnectionEpoch" ? 0u : 11u);
            Assert.False(EncodeWorldIdentity(identity, 91, new ArraySegment<byte>(new byte[256]), out int written));
            Assert.That(written, Is.Zero);
            Assert.False(DecodeWorldIdentity(new ArraySegment<byte>(WorldIdentityWire()), identity, out ulong epoch));
            Assert.That(epoch, Is.Zero);
        }

        [Test] public void WorldIdentityCodec_RejectsMissingOrOversizePayloadAndInsufficientOutput()
        {
            Assert.False(DecodeWorldIdentity(default, Identity(), out ulong missing)); Assert.That(missing, Is.Zero);
            Assert.False(DecodeWorldIdentity(new ArraySegment<byte>(Array.Empty<byte>()), Identity(), out ulong empty)); Assert.That(empty, Is.Zero);
            var oversized = new byte[257]; Array.Copy(WorldIdentityWire(), oversized, WorldIdentityWire().Length);
            Assert.False(DecodeWorldIdentity(new ArraySegment<byte>(oversized), Identity(), out ulong large)); Assert.That(large, Is.Zero);
            Assert.False(EncodeWorldIdentity(Identity(), 91, default, out int noArray)); Assert.That(noArray, Is.Zero);
            var tooSmall = new byte[] { 0xA5 };
            Assert.False(EncodeWorldIdentity(Identity(), 91, new ArraySegment<byte>(tooSmall), out int written));
            Assert.That(written, Is.Zero); Assert.That(tooSmall[0], Is.EqualTo(0xA5));
        }

        [TestCase(1)]
        [TestCase(256)]
        public void WorldIdentityEnvelope_RoundtripPreservesExactSegmentAndBothLengthBounds(int count)
        {
            var bytes = new byte[count + 2]; for (int i = 0; i < count; i++) bytes[i + 1] = (byte)(i + 1);
            using var writer = NetworkWriterPool.Get();
            WriteWorldIdentityEnvelope(writer, new ArraySegment<byte>(bytes, 1, count));
            var reader = new NetworkReader(writer.ToArraySegment());
            Assert.That(reader.ReadUInt(), Is.EqualTo((uint)count), "固定四字节长度头必须包含实际借用段长度。");
            Assert.That(reader.Remaining, Is.EqualTo(count));
            reader = new NetworkReader(writer.ToArraySegment());
            var payload = ReadWorldIdentityEnvelope(reader);
            CollectionAssert.AreEqual(new ArraySegment<byte>(bytes, 1, count), payload);
            Assert.That(reader.Remaining, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void WorldIdentityEnvelope_RejectsPartialFourByteHeader(int headerBytes)
        {
            var reader = new NetworkReader(new ArraySegment<byte>(new byte[headerBytes]));
            Assert.Throws<InvalidDataException>(() => ReadWorldIdentityEnvelope(reader));
        }

        [TestCase(0u, 0)]
        [TestCase(257u, 257)]
        [TestCase(uint.MaxValue, 0)]
        [TestCase(3u, 2)]
        public void WorldIdentityEnvelope_RejectsInvalidDeclaredLengthBeforeReadingPayload(uint declared, int remaining)
        {
            using var writer = NetworkWriterPool.Get(); writer.WriteUInt(declared);
            writer.WriteBytes(new byte[remaining], 0, remaining);
            var reader = new NetworkReader(writer.ToArraySegment());
            Assert.Throws<InvalidDataException>(() => ReadWorldIdentityEnvelope(reader));
        }

        [TestCase("Null")]
        [TestCase("Empty")]
        [TestCase("Oversize")]
        public void WorldIdentityEnvelope_RejectsInvalidWriteBeforeEmittingHeader(string invalid)
        {
            var payload = invalid == "Null" ? default : new ArraySegment<byte>(new byte[invalid == "Empty" ? 0 : 257]);
            using var writer = NetworkWriterPool.Get();
            Assert.Throws<InvalidDataException>(() => WriteWorldIdentityEnvelope(writer, payload));
            Assert.That(writer.Position, Is.Zero, "失败写入不能留下看似有效的长度头。");
        }

        [Test] public void WorldIdentityMessage_ResetClearsAllFieldsAndMergedUnknownData()
        {
            var type = RequireRuntimeType("Game.NetworkProtocol.WorldIdentityMessage");
            var message = Activator.CreateInstance(type) as IMessage;
            Assert.NotNull(message, "握手必须使用真实生成的 Protobuf 消息。");
            var knownWire = WorldIdentityWire();
            var wireWithUnknown = new List<byte>(knownWire); wireWithUnknown.AddRange(new byte[] { 0x38, 1 });
            // 此处直接 MergeFrom 故意绕开 Codec 的拒绝栅栏，以模拟池对象曾被其他解析入口使用。
            // 先证明 unknown 实际进入消息，再重置，避免在本来就空的对象上得到虚假绿灯。
            message.MergeFrom(wireWithUnknown.ToArray());
            Assert.That(type.GetProperty("ProtocolVersion").GetValue(message), Is.EqualTo(ProtocolCompatibility.CurrentVersion));
            Assert.That(type.GetProperty("RunId").GetValue(message), Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(type.GetProperty("DungeonId").GetValue(message), Is.EqualTo("p7_escape"));
            Assert.That(type.GetProperty("RoomEpoch").GetValue(message), Is.EqualTo(7u));
            Assert.That(type.GetProperty("ConnectionEpoch").GetValue(message), Is.EqualTo(11u));
            Assert.That(type.GetProperty("WorldEpoch").GetValue(message), Is.EqualTo(91ul));
            Assert.That(message.CalculateSize(), Is.EqualTo(knownWire.Length + 2), "unknown 必须真实存在才能验证其清理。");
            var reset = type.GetMethod("ResetForReuse", Type.EmptyTypes); Assert.NotNull(reset);
            InvokeBoundary(reset, message, Array.Empty<object>());
            Assert.That(type.GetProperty("ProtocolVersion").GetValue(message), Is.EqualTo(0u));
            Assert.That(type.GetProperty("RunId").GetValue(message), Is.EqualTo(string.Empty));
            Assert.That(type.GetProperty("DungeonId").GetValue(message), Is.EqualTo(string.Empty));
            Assert.That(type.GetProperty("RoomEpoch").GetValue(message), Is.EqualTo(0u));
            Assert.That(type.GetProperty("ConnectionEpoch").GetValue(message), Is.EqualTo(0u));
            Assert.That(type.GetProperty("WorldEpoch").GetValue(message), Is.EqualTo(0ul));
            Assert.That(message.CalculateSize(), Is.Zero, "已知字段与 unknown 均不得在归池后重新发出。");
            Assert.That(message.ToByteArray(), Is.Empty);
        }

        private static JoinConnectionIdentity Identity(string run = null, string dungeon = null, uint room = 7, uint connection = 11) =>
            new JoinConnectionIdentity("player", "room", run ?? "0123456789abcdef0123456789abcdef", room, connection, 1, dungeon ?? "p7_escape");

        // 六字段 Wire fixture 直接按已批准 Schema 编写；不依赖尚未生成的类型，
        // 因而缺 Codec 时是可执行断言 RED，而不是编译失败，也不会只测反射空壳存在。
        private static byte[] WorldIdentityWire(uint? version = null, string run = "0123456789abcdef0123456789abcdef",
            string dungeon = "p7_escape", ulong room = 7, uint connection = 11, ulong world = 91)
        {
            var bytes = new List<byte>();
            bytes.Add(8); AddVarint(bytes, version ?? ProtocolCompatibility.CurrentVersion);
            bytes.Add(0x12); var runBytes = Encoding.ASCII.GetBytes(run); AddVarint(bytes, (ulong)runBytes.Length); bytes.AddRange(runBytes);
            bytes.Add(0x1A); var dungeonBytes = Encoding.ASCII.GetBytes(dungeon); AddVarint(bytes, (ulong)dungeonBytes.Length); bytes.AddRange(dungeonBytes);
            bytes.Add(0x20); AddVarint(bytes, room); bytes.Add(0x28); AddVarint(bytes, connection);
            bytes.Add(0x31); for (int i = 0; i < 8; i++) bytes.Add((byte)(world >> (8 * i)));
            return bytes.ToArray();
        }

        private static void AddVarint(List<byte> bytes, ulong value)
        {
            while (value >= 128) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; }
            bytes.Add((byte)value);
        }

        private static Type RequireRuntimeType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(name); if (type != null) return type;
            }
            Assert.Fail("缺少真实生产类型：" + name); return null;
        }

        private static bool EncodeWorldIdentity(JoinConnectionIdentity identity, ulong worldEpoch, ArraySegment<byte> output, out int written)
        {
            var type = RequireRuntimeType("Game.Networking.WorldIdentityCodec");
            var method = type.GetMethod("TryEncode", new[] { typeof(JoinConnectionIdentity), typeof(ulong), typeof(ArraySegment<byte>), typeof(int).MakeByRefType() });
            Assert.NotNull(method, "必须通过真实 Codec 编码认证后的独立世界握手。");
            object[] args = { identity, worldEpoch, output, 0 };
            bool accepted = (bool)InvokeBoundary(method, Activator.CreateInstance(type), args); written = (int)args[3]; return accepted;
        }

        private static bool DecodeWorldIdentity(ArraySegment<byte> payload, JoinConnectionIdentity identity, out ulong worldEpoch)
        {
            var type = RequireRuntimeType("Game.Networking.WorldIdentityCodec");
            var method = type.GetMethod("TryDecode", new[] { typeof(ArraySegment<byte>), typeof(JoinConnectionIdentity), typeof(ulong).MakeByRefType() });
            Assert.NotNull(method, "必须通过真实 Codec 解码，并比对外部认证身份。");
            object[] args = { payload, identity, 123ul };
            bool accepted = (bool)InvokeBoundary(method, Activator.CreateInstance(type), args); worldEpoch = (ulong)args[2]; return accepted;
        }

        private static void WriteWorldIdentityEnvelope(NetworkWriter writer, ArraySegment<byte> payload)
        {
            var type = RequireRuntimeType("Game.Networking.WorldIdentityEnvelope");
            object message = Activator.CreateInstance(type); type.GetField("Payload").SetValue(message, payload);
            var method = RequireRuntimeType("Game.Networking.WorldIdentityEnvelopeSerialization").GetMethod("WriteWorldIdentityEnvelope");
            Assert.NotNull(method); InvokeBoundary(method, null, new[] { (object)writer, message });
        }

        private static ArraySegment<byte> ReadWorldIdentityEnvelope(NetworkReader reader)
        {
            var type = RequireRuntimeType("Game.Networking.WorldIdentityEnvelope");
            var method = RequireRuntimeType("Game.Networking.WorldIdentityEnvelopeSerialization").GetMethod("ReadWorldIdentityEnvelope");
            Assert.NotNull(method); object message = InvokeBoundary(method, null, new[] { (object)reader });
            return (ArraySegment<byte>)type.GetField("Payload").GetValue(message);
        }

        private static object InvokeBoundary(MethodInfo method, object target, object[] args)
        {
            try { return method.Invoke(target, args); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                // 反射包装异常不能伪装成协议拒绝；断言必须看到生产边界实际抛出的异常类型。
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw;
            }
        }
    }
}
