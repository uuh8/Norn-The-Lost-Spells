using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Game.Networking;
using Game.NetworkProtocol;
using Google.Protobuf;
using Mirror;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>只测个人退出小包的真实字节/复用边界，不创建连接、Scene或资产。反射使缺实现RED仍可编译。</summary>
    public sealed class RunExitCodecTests
    {
        private const string Command = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const ulong GameplayHash = 123456789;

        [Test] public void ReturnPortalIntentSurvivesCodecButDoesNotGrantSuccessfulExtraction()
        {
            var value = Intent(); Set(value, "ThroughReturnPortal", true);
            var codec = Codec(); Assert.That(Encode(codec, "Intent", value, out var payload), Is.True);
            Assert.That(Decode(codec, "Intent", payload, GameplayHash, out var decoded), Is.True);
            Assert.That(Get(decoded, "ThroughReturnPortal"), Is.True);
            var descriptor = RunExitIntentMessage.Descriptor;
            Assert.That(descriptor.FindFieldByName("successful_extraction"), Is.Null,
                "Client只说明按F使用原门；成功、敌人数量与GMU仍由DS当前世界裁决。");
            var message = RunExitIntentMessage.Parser.ParseFrom(payload.Array, payload.Offset, payload.Count);
            message.ResetForReuse(); Assert.That(message.CalculateSize(), Is.Zero);
        }
        private static Type NetworkType(string name) => typeof(RoomProtocolEnvelope).Assembly.GetType("Game.Networking." + name);
        private static Type CoreType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var type = assembly.GetType("Game.Core." + name); if (type != null) return type; }
            Assert.Fail("缺少中立合同：" + name); return null;
        }
        private static object Codec()
        {
            var type = NetworkType("RunExitCodec"); Assert.That(type, Is.Not.Null, "退出Codec尚未接入，不能把断线当可靠保存。");
            return Activator.CreateInstance(type);
        }
        private static void Set(object value, string field, object content)
        { var found = value.GetType().GetField(field); Assert.That(found, Is.Not.Null, field); found.SetValue(value, content); }
        private static object Get(object value, string field) => value.GetType().GetField(field).GetValue(value);
        private static object Intent(string command = Command, uint epoch = 7)
        {
            object value = Activator.CreateInstance(CoreType("OnlineRunExitIntent"));
            Set(value, "CommandId", command); Set(value, "ConnectionEpoch", epoch); return value;
        }
        private static object Status(string phase, uint version = 0, string hash = "", string failure = "")
        {
            object value = Activator.CreateInstance(CoreType("OnlineRunExitStatus"));
            Set(value, "CommandId", Command); Set(value, "ConnectionEpoch", 7u);
            var field = value.GetType().GetField("Phase"); field.SetValue(value, Enum.Parse(field.FieldType, phase));
            var reason = value.GetType().GetField("Reason"); reason.SetValue(value, Enum.Parse(reason.FieldType, "VoluntaryExit"));
            Set(value, "CommittedVersion", version); Set(value, "PayloadHash", hash); Set(value, "Failure", failure); return value;
        }
        private static bool Encode(object codec, string kind, object value, out ArraySegment<byte> payload, int offset = 0)
        {
            var method = codec.GetType().GetMethod("TryEncode" + kind); Assert.That(method, Is.Not.Null);
            var buffer = new byte[256 + offset]; if (offset > 0) buffer[0] = 0x7f;
            object[] args = { value, GameplayHash, new ArraySegment<byte>(buffer, offset, 256), 0 };
            bool success = (bool)method.Invoke(codec, args); payload = new ArraySegment<byte>(buffer, offset, (int)args[3]);
            if (offset > 0) Assert.That(buffer[0], Is.EqualTo(0x7f)); return success;
        }
        private static bool Decode(object codec, string kind, ArraySegment<byte> payload, ulong hash, out object value)
        {
            var method = codec.GetType().GetMethod("TryDecode" + kind); Assert.That(method, Is.Not.Null);
            object[] args = { payload, hash, null }; bool result = (bool)method.Invoke(codec, args); value = args[2]; return result;
        }

        [Test] public void IntentRoundtripKeepsStableCommandAndSegmentOffset()
        {
            var codec = Codec(); Assert.That(Encode(codec, "Intent", Intent(), out var payload, 17), Is.True);
            Assert.That(payload.Count, Is.InRange(1, 256)); Assert.That(Decode(codec, "Intent", payload, GameplayHash, out var result), Is.True);
            Assert.That(Get(result, "CommandId"), Is.EqualTo(Command)); Assert.That(Get(result, "ConnectionEpoch"), Is.EqualTo(7u));
        }
        [TestCase(null)] [TestCase("")] [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        public void IntentRejectsMissingOrNonCanonicalId(string id)
        { Assert.That(Encode(Codec(), "Intent", Intent(id), out var payload), Is.False); Assert.That(payload.Count, Is.Zero); }
        [Test] public void IntentRejectsDefaultEpoch()
        { Assert.That(Encode(Codec(), "Intent", Intent(epoch: 0), out _), Is.False); }
        [Test] public void HashOrProtocolMismatchCannotDecode()
        {
            var codec = Codec(); Assert.That(Encode(codec, "Intent", Intent(), out var payload), Is.True);
            Assert.That(Decode(codec, "Intent", payload, GameplayHash + 1, out _), Is.False);
            Assert.That(Decode(codec, "Intent", new ArraySegment<byte>(Wire(false, 0, (ulong)ProtocolCompatibility.CurrentVersion + 1)), GameplayHash, out _), Is.False);
        }
        [TestCase("phase-wide")] [TestCase("epoch-wide")] [TestCase("duplicate")] [TestCase("truncated")] [TestCase("oversize")]
        public void MalformedRawStatusIsRejectedBeforeNarrowing(string defect)
        {
            byte[] raw;
            if (defect == "oversize") raw = new byte[257];
            else
            {
                raw = Wire(true, defect == "phase-wide" ? 257ul : 1, ProtocolCompatibility.CurrentVersion,
                    defect == "epoch-wide" ? (ulong)uint.MaxValue + 1 : 7ul);
                if (defect == "duplicate") { var copy = new List<byte>(raw); copy.Add(40); copy.Add(1); raw = copy.ToArray(); }
                if (defect == "truncated") Array.Resize(ref raw, raw.Length - 1);
            }
            Assert.That(Decode(Codec(), "Status", new ArraySegment<byte>(raw), GameplayHash, out _), Is.False);
        }
        [Test] public void CommittedRequiresFullReceiptWithoutFailure()
        {
            var codec = Codec();
            foreach (var status in new[] { Status("Committed", 0, Hash), Status("Committed", 9, ""), Status("Committed", 9, Hash, "Rejected") })
                Assert.That(Encode(codec, "Status", status, out _), Is.False);
        }
        [Test] public void NonCommittedCannotCarryInventedCommitMetadata()
        {
            var codec = Codec();
            foreach (string phase in new[] { "Draining", "Submitting", "Uncertain", "Rejected" })
                Assert.That(Encode(codec, "Status", Status(phase, 9, Hash, phase == "Rejected" ? "Rejected" : ""), out _), Is.False);
        }
        [Test] public void EachActualStageRoundtripsItsOriginalValues()
        {
            var codec = Codec();
            foreach (string phase in new[] { "Draining", "Submitting", "Uncertain", "Committed", "Rejected" })
            {
                bool committed = phase == "Committed";
                var source = Status(phase, committed ? 9u : 0, committed ? Hash : "",
                    phase == "Rejected" ? "RunMemberNotActive" : phase == "Uncertain" ? "BusinessUnavailable" : "");
                Assert.That(Encode(codec, "Status", source, out var payload), Is.True, phase);
                Assert.That(Decode(codec, "Status", payload, GameplayHash, out var result), Is.True, phase);
                foreach (string field in new[] { "CommandId", "ConnectionEpoch", "Phase", "Reason", "Failure", "CommittedVersion", "PayloadHash" })
                    Assert.That(Get(result, field), Is.EqualTo(Get(source, field)), field);
            }
        }
        [Test] public void FailureIsBoundedAsciiCodeNotArbitraryText()
        {
            var codec = Codec();
            foreach (string failure in new[] { new string('x', 97), "错误", "Code\nsecret" })
                Assert.That(Encode(codec, "Status", Status("Rejected", failure: failure), out _), Is.False);
        }
        [TestCase("RunExitIntentMessage")] [TestCase("RunExitStatusMessage")]
        public void ResetClearsKnownValuesAndUnknownFields(string name)
        {
            var type = typeof(ProtocolCompatibility).Assembly.GetType("Game.NetworkProtocol." + name);
            Assert.That(type, Is.Not.Null); var message = (IMessage)Activator.CreateInstance(type);
            var bytes = new List<byte>(); Text(bytes, 4, Command); Varint(bytes, 99u << 3); Varint(bytes, 1); message.MergeFrom(bytes.ToArray());
            if (name == "RunExitStatusMessage") type.GetProperty("Reason").SetValue(message, 2u);
            var reset = type.GetMethod("ResetForReuse"); Assert.That(reset, Is.Not.Null); reset.Invoke(message, null);
            Assert.That(message.ToByteArray(), Is.Empty);
        }
        [Test] public void MirrorEnvelopeIdsCannotCollideWithExistingNetworkingOrBuiltinMessages()
        {
            var intent = NetworkType("RunExitIntentEnvelope"); var status = NetworkType("RunExitStatusEnvelope");
            Assert.That(intent, Is.Not.Null); Assert.That(status, Is.Not.Null);
            ushort a = Id(intent), b = Id(status); Assert.That(a, Is.Not.EqualTo(b));
            foreach (var assembly in new[] { typeof(RoomProtocolEnvelope).Assembly, typeof(NetworkMessage).Assembly })
                foreach (var type in assembly.GetTypes())
                    if (type != intent && type != status && type.IsValueType && typeof(NetworkMessage).IsAssignableFrom(type))
                    { ushort other = Id(type); Assert.That(a, Is.Not.EqualTo(other), type.FullName); Assert.That(b, Is.Not.EqualTo(other), type.FullName); }
        }
        private static ushort Id(Type type)
        {
            var method = typeof(NetworkMessages).GetMethod("GetId", BindingFlags.Public | BindingFlags.Static); Assert.That(method, Is.Not.Null);
            return (ushort)method.MakeGenericMethod(type).Invoke(null, null);
        }
        private static byte[] Wire(bool status, ulong phase, ulong protocol, ulong epoch = 7)
        {
            var bytes = new List<byte>(); Varint(bytes, 8); Varint(bytes, protocol); Varint(bytes, 17);
            for (int i = 0; i < 8; i++) bytes.Add((byte)(GameplayHash >> (8 * i)));
            Varint(bytes, 24); Varint(bytes, epoch); Text(bytes, 4, Command);
            if (status) { Varint(bytes, 40); Varint(bytes, phase); Varint(bytes, 72); Varint(bytes, 1); }
            return bytes.ToArray();
        }
        private static void Text(List<byte> bytes, uint field, string text)
        { byte[] value = Encoding.ASCII.GetBytes(text); Varint(bytes, field << 3 | 2); Varint(bytes, (ulong)value.Length); bytes.AddRange(value); }
        private static void Varint(List<byte> bytes, ulong value)
        { while (value >= 128) { bytes.Add((byte)(value | 128)); value >>= 7; } bytes.Add((byte)value); }
    }
}
