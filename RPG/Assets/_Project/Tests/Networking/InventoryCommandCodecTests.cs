using System;
using System.Reflection;
using Game.NetworkProtocol;
using Google.Protobuf.Reflection;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>授权库存命令仅携带意图；身份来自连接，本组不创建权威工作区或发送网络请求。</summary>
    public sealed class InventoryCommandCodecTests
    {
        private object _codec;
        private Type _type;
        private const ulong Hash = 12345678;

        [SetUp] public void RequireCommandCodec()
        {
            _type = typeof(MovementCodec).Assembly.GetType("Game.Networking.InventoryCommandCodec");
            Assert.That(_type, Is.Not.Null, "缺少与64KiB完整快照分离的512B命令/收据Codec。");
            _codec = Activator.CreateInstance(_type);
        }

        [Test] public void SelectWandRoundTripsCurrentIdentityAndVersions()
        {
            Assert.That(Decode(Encode(Intent(1), "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Get<ulong>(result, "CommandId"), Is.EqualTo(9));
            Assert.That(Get<uint>(result, "ConnectionEpoch"), Is.EqualTo(3));
            Assert.That(Get<uint>(result, "ExpectedInventoryVersion"), Is.EqualTo(7));
            Assert.That(Get<string>(result, "ItemId"), Is.EqualTo(Id(4)));
            Assert.That(Get<int>(result, "TargetSlot"), Is.EqualTo(1));
        }

        [Test] public void PlaceCardBetweenOwnedWandsPreservesBothProgramRevisions()
        {
            Assert.That(Decode(Encode(Intent(2), "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Get<string>(result, "SourceWandId"), Is.EqualTo(Id(4)));
            Assert.That(Get<string>(result, "TargetWandId"), Is.EqualTo(Id(6)));
            Assert.That(Get<uint>(result, "SourceProgramRevision"), Is.EqualTo(2));
            Assert.That(Get<uint>(result, "TargetProgramRevision"), Is.EqualTo(3));
        }

        [Test] public void PlaceToAndFromSpellStorageUsesExplicitEmptyRoot()
        {
            var value = Intent(2); Set(value, "ToSpellStorage", true); Set(value, "TargetWandId", "");
            Set(value, "TargetProgramRevision", 0u); Set(value, "TargetSlot", 255);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var storage), Is.True);
            Assert.That(Get<bool>(storage, "ToSpellStorage"), Is.True); Assert.That(Get<int>(storage, "TargetSlot"), Is.EqualTo(255));
            value = Intent(2); Set(value, "SourceWandId", ""); Set(value, "SourceProgramRevision", 0u);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var installed), Is.True);
            Assert.That(Get<string>(installed, "SourceWandId"), Is.Empty);
        }

        [Test] public void DefaultNullOptionalStringsCanonicalizeToEmpty()
        {
            var value = Intent(1); Set(value, "SourceWandId", null); Set(value, "TargetWandId", null);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Get<string>(result, "SourceWandId"), Is.Empty); Assert.That(Get<string>(result, "TargetWandId"), Is.Empty);
        }

        [Test] public void ExpectedGameplayHashAndApplicationVersionMustMatch()
        {
            var bytes = Encode(Intent(1), "Intent");
            Assert.That(Decode(bytes, "Intent", Hash + 1, out _), Is.False);
            Assert.That(Decode(bytes, "Intent", 0, out _), Is.False);
            Assert.That(TryEncode(Intent(1), "Intent", 0, out _, out _), Is.False);
            Assert.That(bytes[0], Is.EqualTo(8)); bytes[1] = 1;
            Assert.That(Decode(bytes, "Intent", Hash, out _), Is.False); bytes[1] = 0;
            Assert.That(Decode(bytes, "Intent", Hash, out _), Is.False);
        }

        [TestCase("CommandId")]
        [TestCase("ConnectionEpoch")]
        [TestCase("ExpectedInventoryVersion")]
        public void MissingCommandIdentityOrVersionIsRejected(string field)
        {
            var value = Intent(1); Set(value, field, field == "CommandId" ? (object)0UL : 0u);
            Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
        }

        [Test] public void SelectCannotSmuggleMoveFieldsOrInvalidSlot()
        {
            var value = Intent(1); Set(value, "SourceWandId", Id(4)); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            value = Intent(1); Set(value, "TargetProgramRevision", 1u); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            value = Intent(1); Set(value, "ToSpellStorage", true); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            value = Intent(1); Set(value, "TargetSlot", 3); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
        }

        [Test] public void PlaceRequiresConsistentSourceAndTargetProgramIdentity()
        {
            var value = Intent(2); Set(value, "SourceProgramRevision", 0u); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            value = Intent(2); Set(value, "SourceWandId", ""); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            value = Intent(2); Set(value, "TargetWandId", ""); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            value = Intent(2); Set(value, "ToSpellStorage", true); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            value = Intent(2); Set(value, "TargetSlot", -1); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            Set(value, "TargetSlot", 256); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
        }

        [Test] public void MalformedIdAndUnknownCommandKindAreRejected()
        {
            var value = Intent(1); Set(value, "ItemId", new string('g', 32)); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            value = Intent(1); SetEnum(value, "Kind", 99); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            // 原Wire的uint不能先窄化成Core的byte枚举，257截断后会伪装成合法SelectWand=1。
            Assert.That(Decode(ReplaceVarint(Encode(Intent(1), "Intent"), 6, new byte[] { 129, 2 }), "Intent", Hash, out _), Is.False);
        }

        [Test] public void UnknownOrDuplicateWireFieldsCannotOverrideCommand()
        {
            var bytes = Encode(Intent(1), "Intent");
            Assert.That(Decode(Append(bytes, new byte[] { 112, 1 }), "Intent", Hash, out _), Is.False,
                "不能夹带第14字段作为Caller/Actor等客户端自报身份。");
            Assert.That(Decode(Append(bytes, new byte[] { 25, 9, 0, 0, 0, 0, 0, 0, 0 }), "Intent", Hash, out _), Is.False);
        }

        [Test] public void WrongWireTypeTruncationAndNonMinimalVarintAreRejected()
        {
            Assert.That(Decode(new byte[] { 24, 1 }, "Intent", Hash, out _), Is.False, "command_id必须fixed64，不能varint。");
            Assert.That(Decode(new byte[] { 8, 130, 0 }, "Intent", Hash, out _), Is.False);
            var bytes = Encode(Intent(2), "Intent"); Array.Resize(ref bytes, bytes.Length - 1);
            Assert.That(Decode(bytes, "Intent", Hash, out _), Is.False);
        }

        [Test] public void OversizedPayloadAndSmallOutputFailWithoutPartialLength()
        {
            Assert.That(Decode(new byte[513], "Intent", Hash, out _), Is.False);
            Assert.That(Decode(new byte[513], "Receipt", Hash, out _), Is.False);
            var args = new object[] { Intent(1), Hash, new ArraySegment<byte>(new byte[1]), 123 };
            Assert.That((bool)_type.GetMethod("TryEncodeIntent").Invoke(_codec, args), Is.False);
            Assert.That((int)args[3], Is.Zero);
        }

        [Test] public void ReceiptsRoundTripSuccessAndFailureWithoutClientAuthoredSuccessFlag()
        {
            Assert.That(Decode(Encode(Receipt(0, 8), "Receipt"), "Receipt", Hash, out var success), Is.True);
            Assert.That((bool)success.GetType().GetProperty("Success").GetValue(success), Is.True);
            Assert.That(Decode(Encode(Receipt(8, 7), "Receipt"), "Receipt", Hash, out var busy), Is.True);
            Assert.That((bool)busy.GetType().GetProperty("Success").GetValue(busy), Is.False);
            Assert.That(Convert.ToInt32(Get<object>(busy, "Failure")), Is.EqualTo(8));
            Assert.That(Get<uint>(busy, "InventoryVersion"), Is.EqualTo(7));
            Assert.That(Decode(Encode(Receipt(1, 0), "Receipt"), "Receipt", Hash, out _), Is.True,
                "尚未就绪的拒绝可能没有库存版本，不能连错误收据也悄悄丢失。");
            Assert.That(TryEncode(Receipt(0, 0), "Receipt", Hash, out _, out _), Is.False);
            Assert.That(TryEncode(Receipt(12, 7), "Receipt", Hash, out _, out _), Is.False);
        }

        [Test] public void ReceiptHashUnknownFieldsAndOldApplicationAreRejected()
        {
            var bytes = Encode(Receipt(0, 8), "Receipt");
            Assert.That(Decode(bytes, "Receipt", Hash + 1, out _), Is.False);
            Assert.That(Decode(Append(bytes, new byte[] { 56, 1 }), "Receipt", Hash, out _), Is.False);
            Assert.That(Decode(Append(bytes, new byte[] { 48, 128, 2 }), "Receipt", Hash, out _), Is.False,
                "Failure=256不能截断成byte的None=0而伪装成成功。");
            bytes[1] = 1; Assert.That(Decode(bytes, "Receipt", Hash, out _), Is.False);
        }

        [Test] public void SchemaDoesNotCarryCallerActorPlayerOrIndependentSuccess()
        {
            var type = typeof(ProtocolCompatibility).Assembly.GetType("Game.NetworkProtocol.InventoryCommandMessage");
            Assert.That(type, Is.Not.Null);
            var descriptor = (MessageDescriptor)type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            Assert.That(descriptor.FindFieldByName("caller_id"), Is.Null); Assert.That(descriptor.FindFieldByName("actor_id"), Is.Null);
            Assert.That(descriptor.FindFieldByName("player_id"), Is.Null);
            type = typeof(ProtocolCompatibility).Assembly.GetType("Game.NetworkProtocol.InventoryCommandReceiptMessage"); Assert.That(type, Is.Not.Null);
            descriptor = (MessageDescriptor)type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            Assert.That(descriptor.FindFieldByName("success"), Is.Null);
        }

        private static object Intent(int kind)
        {
            var value = Activator.CreateInstance(Core("OnlineInventoryCommandIntent"));
            Set(value, "CommandId", 9UL); Set(value, "ConnectionEpoch", 3u); Set(value, "ExpectedInventoryVersion", 7u);
            SetEnum(value, "Kind", kind); Set(value, "ItemId", Id(kind == 1 ? 4 : 5)); Set(value, "TargetSlot", 1);
            Set(value, "SourceWandId", kind == 2 ? Id(4) : ""); Set(value, "SourceProgramRevision", kind == 2 ? 2u : 0u);
            Set(value, "TargetWandId", kind == 2 ? Id(6) : ""); Set(value, "TargetProgramRevision", kind == 2 ? 3u : 0u); return value;
        }
        private static object Receipt(int failure, uint version)
        {
            var value = Activator.CreateInstance(Core("OnlineInventoryCommandReceipt"));
            Set(value, "CommandId", 9UL); Set(value, "ConnectionEpoch", 3u); Set(value, "InventoryVersion", version);
            SetEnum(value, "Failure", failure); return value;
        }
        private byte[] Encode(object value, string kind) { Assert.That(TryEncode(value, kind, Hash, out var bytes, out _), Is.True); return bytes; }
        private bool TryEncode(object value, string kind, ulong hash, out byte[] bytes, out int written)
        {
            var buffer = new byte[512]; var args = new object[] { value, hash, new ArraySegment<byte>(buffer), 0 };
            bool result = (bool)_type.GetMethod("TryEncode" + kind).Invoke(_codec, args); written = (int)args[3];
            bytes = result ? new byte[written] : null; if (result) Array.Copy(buffer, bytes, written); return result;
        }
        private bool Decode(byte[] bytes, string kind, ulong hash, out object result)
        {
            var args = new object[] { new ArraySegment<byte>(bytes), hash, null };
            bool valid = (bool)_type.GetMethod("TryDecode" + kind).Invoke(_codec, args); result = args[2]; return valid;
        }
        private static Type Core(string name) { var type = Type.GetType("Game.Core." + name + ", Game.Core"); Assert.That(type, Is.Not.Null); return type; }
        private static void Set(object value, string field, object content) => value.GetType().GetField(field).SetValue(value, content);
        private static void SetEnum(object value, string field, int content)
        { var info = value.GetType().GetField(field); info.SetValue(value, Enum.ToObject(info.FieldType, content)); }
        private static T Get<T>(object value, string field) => (T)value.GetType().GetField(field).GetValue(value);
        private static string Id(int number) => number.ToString("x32");
        private static byte[] Append(byte[] first, byte[] second)
        { var result = new byte[first.Length + second.Length]; Array.Copy(first, result, first.Length); Array.Copy(second, 0, result, first.Length, second.Length); return result; }
        private static byte[] ReplaceVarint(byte[] bytes, int field, byte[] replacement)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int tag = bytes[offset++], wire = tag & 7, start = offset;
                if (wire == 1) { offset += 8; continue; }
                if (wire == 2) { int length = bytes[offset++]; offset += length; continue; }
                if (wire != 0) throw new InvalidOperationException("Fixture不支持此Wire类型。");
                while ((bytes[offset++] & 128) != 0) { }
                if ((tag >> 3) != field) continue;
                var result = new byte[bytes.Length - (offset - start) + replacement.Length];
                Array.Copy(bytes, result, start); Array.Copy(replacement, 0, result, start, replacement.Length);
                Array.Copy(bytes, offset, result, start + replacement.Length, bytes.Length - offset); return result;
            }
            throw new InvalidOperationException("Fixture字段不存在。");
        }
    }
}
