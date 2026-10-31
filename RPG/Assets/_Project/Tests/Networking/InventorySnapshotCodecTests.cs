using System;
using System.IO;
using System.Reflection;
using Game.NetworkProtocol;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>
    /// 只验证离散完整库存快照的 Wire 边界；不创建库存权威、连接、存档或场景。
    /// Reflection 让缺 Codec 的第一次 RED 是明确断言，而不是用编译失败冒充测试。
    /// </summary>
    public sealed class InventorySnapshotCodecTests
    {
        private object _codec;
        private Type _codecType;
        private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        [SetUp] public void RequireCodec()
        {
            _codecType = typeof(MovementCodec).Assembly.GetType("Game.Networking.InventorySnapshotCodec");
            Assert.That(_codecType, Is.Not.Null, "缺少完整库存的独立有界 Codec，不能把大背包塞进512B CombatFact。");
            _codec = Activator.CreateInstance(_codecType);
        }

        [Test] public void CurrentResourceAndOwnedIdentityRoundTrip()
        {
            object source = Snapshot(); byte[] bytes = Encode(source);
            Assert.That(Decode(bytes, out var result), Is.True);
            Assert.That(Get<uint>(result, "ConnectionEpoch"), Is.EqualTo(3));
            Assert.That(Get<uint>(result, "ServerTick"), Is.EqualTo(50));
            Assert.That(Get<ulong>(result, "SnapshotSequence"), Is.EqualTo(9));
            var assets = Get<object>(result, "Assets");
            Assert.That(Get<string>(assets, "InventoryId"), Is.EqualTo(Id(1) + "/" + Id(2)),
                "真实InventoryId是Run/Player组合，不是32字符的物品实例ID。");
            Assert.That(Get<uint>(assets, "InventoryVersion"), Is.EqualTo(7));
            var wand = Get<Array>(result, "Wands").GetValue(0);
            Assert.That(Get<float>(wand, "CurrentMana"), Is.EqualTo(12.5f));
            Assert.That(Get<float>(wand, "CooldownRemaining"), Is.EqualTo(.75f));
            Assert.That(Get<uint>(wand, "ProgramRevision"), Is.EqualTo(5));
            Assert.That(Get<int>(Get<Array>(assets, "Items").GetValue(1), "RemainingCharges"), Is.EqualTo(23));
            Assert.That(Get<bool>(Get<Array>(result, "SpellUses").GetValue(0), "Eligible"), Is.True);
        }

        [Test] public void MaximumOwnedClosureIsLargerThanCombatFactAndStillRoundTrips()
        {
            byte[] bytes = Encode(Snapshot(3, 32));
            Assert.That(bytes.Length, Is.GreaterThan(512));
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(65536));
            Assert.That(Decode(bytes, out var result), Is.True);
            Assert.That(Get<Array>(Get<object>(result, "Assets"), "Items").Length, Is.EqualTo(99));
            Assert.That(Get<Array>(result, "SpellUses").Length, Is.EqualTo(96));
        }

        [Test] public void DecodeReturnsOwnedDtoRatherThanBorrowingNextDecode()
        {
            var first = Snapshot(); Assert.That(Decode(Encode(first), out var retained), Is.True);
            Set(Get<Array>(Get<object>(first, "Assets"), "Items").GetValue(1), "RemainingCharges", 7);
            Assert.That(Decode(Encode(first), out var next), Is.True);
            Assert.That(ReferenceEquals(retained, next), Is.False);
            Assert.That(Get<int>(Get<Array>(Get<object>(retained, "Assets"), "Items").GetValue(1), "RemainingCharges"), Is.EqualTo(23));
        }

        [Test] public void InsufficientOutputDoesNotPublishPartialLength()
        {
            var args = new object[] { Snapshot(), new ArraySegment<byte>(new byte[1]), 123 };
            Assert.That((bool)_codecType.GetMethod("TryEncode").Invoke(_codec, args), Is.False);
            Assert.That((int)args[2], Is.Zero);
        }

        [Test] public void OversizedPayloadIsRejectedBeforeMessageConstruction()
        { Assert.That(Decode(new byte[65537], out var value), Is.False); Assert.That(value, Is.Null); }

        [Test] public void MissingAndOldApplicationVersionCannotBecomeCurrentSnapshot()
        {
            var bytes = Encode(Snapshot()); Assert.That(bytes[0], Is.EqualTo(8));
            bytes[1] = 0; Assert.That(Decode(bytes, out _), Is.False);
            bytes[1] = 1; Assert.That(Decode(bytes, out _), Is.False);
        }

        [Test] public void UnknownRootAndNestedFieldsAreRejected()
        {
            var bytes = Encode(Snapshot());
            Assert.That(Decode(Append(bytes, Field(12, 1)), out _), Is.False);
            var assets = Payload(bytes, 9);
            Assert.That(Decode(ReplacePayload(bytes, 9, Append(assets, Field(10, 1))), out _), Is.False);
        }

        [Test] public void DuplicateScalarFieldDoesNotUseProtobufLastWins()
        { Assert.That(Decode(Append(Encode(Snapshot()), Field(1, ProtocolCompatibility.CurrentVersion)), out _), Is.False); }

        [Test] public void TooManyEmptyNestedItemsAreRejectedByWireBudget()
        {
            // 每个空item只占两字节；仅64KiB字节限制不能阻止数万对象分配，必须先数嵌套项。
            var assets = new byte[100 * 2]; for (int i = 0; i < 100; i++) assets[i * 2] = 74;
            Assert.That(Decode(ReplacePayload(Encode(Snapshot()), 9, assets), out _), Is.False);
        }

        [Test] public void TruncatedLengthAndWrongWireTypeAreRejected()
        {
            var bytes = Encode(Snapshot()); Array.Resize(ref bytes, bytes.Length - 1);
            Assert.That(Decode(bytes, out _), Is.False);
            Assert.That(Decode(new byte[] { 10, 0 }, out _), Is.False, "protocol_version需要varint，不能读取成length-delimited。");
        }

        [Test] public void NonFiniteAndNegativeCurrentResourcesCannotEncode()
        {
            var source = Snapshot(); var wand = Get<Array>(source, "Wands").GetValue(0);
            Set(wand, "CurrentMana", float.NaN); Assert.That(TryEncode(source, out _), Is.False);
            Set(wand, "CurrentMana", -1f); Assert.That(TryEncode(source, out _), Is.False);
            Set(wand, "CurrentMana", 12.5f); Set(wand, "CooldownRemaining", -1f);
            Assert.That(TryEncode(source, out _), Is.False);
        }

        [Test] public void ItemCountAndContentStringBudgetsApplyToOutgoingSnapshots()
        {
            var source = Snapshot(); var assets = Get<object>(source, "Assets");
            Set(Get<Array>(assets, "Items").GetValue(0), "ContentId", new string('a', 129));
            Assert.That(TryEncode(source, out _), Is.False);
            source = Snapshot(); assets = Get<object>(source, "Assets");
            var items = Get<Array>(assets, "Items"); var oversized = Array.CreateInstance(items.GetType().GetElementType(), 100);
            Set(assets, "Items", oversized); Assert.That(TryEncode(source, out _), Is.False);
        }

        [Test] public void InstanceStatsAndAlwaysCastTraitsSurviveWithoutTemplateFallback()
        {
            var source = Snapshot(); var item = Get<Array>(Get<object>(source, "Assets"), "Items").GetValue(0);
            var stats = Core("OnlineOwnedWandStatsSnapshot");
            Set(stats, "MaxMana", 80f); Set(stats, "ManaRegenPerSecond", 4f); Set(stats, "BaseCastDelay", .2f);
            Set(stats, "BaseReloadTime", .4f); Set(stats, "BaseSpreadDegrees", 3f); Set(stats, "ProgramCapacity", 8);
            Set(stats, "BaseDraws", 1); Set(stats, "AlwaysCastContentIds", new[] { "spell.emit.fireball" });
            Set(item, "UseTemplateWandStats", false); Set(item, "WandStats", stats);
            Assert.That(Decode(Encode(source), out var result), Is.True);
            var copied = Get<object>(Get<Array>(Get<object>(result, "Assets"), "Items").GetValue(0), "WandStats");
            Assert.That(Get<float>(copied, "MaxMana"), Is.EqualTo(80));
            Assert.That(Get<string[]>(copied, "AlwaysCastContentIds"), Is.EqualTo(new[] { "spell.emit.fireball" }));
        }

        [Test] public void RuntimeSpellStorageRoundTripsAsAnExplicitRootInsteadOfInventingBag()
        {
            var source = Snapshot(); var item = Get<Array>(Get<object>(source, "Assets"), "Items").GetValue(1);
            Set(item, "LocationKind", 7); Set(item, "ParentInstanceId", ""); Set(item, "Slot", 255);
            Assert.That(Decode(Encode(source), out var result), Is.True);
            var restored = Get<Array>(Get<object>(result, "Assets"), "Items").GetValue(1);
            Assert.That(Get<int>(restored, "LocationKind"), Is.EqualTo(7)); Assert.That(Get<string>(restored, "ParentInstanceId"), Is.Empty);
            Assert.That(Get<int>(restored, "Slot"), Is.EqualTo(255));
        }

        [Test] public void RuntimeSpellStorageRejectsParentAliasAndOutOfWireSlotBudget()
        {
            var source = Snapshot(); var item = Get<Array>(Get<object>(source, "Assets"), "Items").GetValue(1);
            Set(item, "LocationKind", 7); Set(item, "ParentInstanceId", Id(4));
            Assert.That(TryEncode(source, out _), Is.False);
            Set(item, "ParentInstanceId", ""); Set(item, "Slot", 256); Assert.That(TryEncode(source, out _), Is.False);
        }

        private static object Snapshot(int wandCount = 1, int cardsPerWand = 1)
        {
            var source = Core("OnlineInventoryPresentationSnapshot"); var assets = Core("OnlineOwnedAssetSnapshot");
            Set(source, "SchemaVersion", 1u); Set(source, "ConnectionEpoch", 3u); Set(source, "SnapshotSequence", 9UL);
            Set(source, "ResourceVersion", 2u); Set(source, "SelectionVersion", 1u); Set(source, "SpellUseVersion", 1u);
            Set(source, "ServerTick", 50u); Set(source, "Assets", assets);
            Set(assets, "SchemaVersion", 1u); Set(assets, "RunId", Id(1)); Set(assets, "PlayerId", Id(2));
            Set(assets, "InventoryId", Id(1) + "/" + Id(2)); Set(assets, "GameplayConfigHash", Hash);
            Set(assets, "SelectedWandInstanceId", Id(4)); Set(assets, "ActorId", 1UL); Set(assets, "InventoryVersion", 7u);
            var items = Array.CreateInstance(CoreType("OnlineOwnedItemSnapshot"), wandCount * (cardsPerWand + 1));
            var wands = Array.CreateInstance(CoreType("OnlineInventoryWandPresentationSnapshot"), wandCount);
            var uses = Array.CreateInstance(CoreType("OnlineInventorySpellUsePresentationSnapshot"), wandCount * cardsPerWand);
            int index = 0, card = 0;
            for (int w = 0; w < wandCount; w++)
            {
                string wandId = Id(4 + w * 33); var item = Core("OnlineOwnedItemSnapshot");
                Set(item, "InstanceId", wandId); Set(item, "ContentId", "wand.basic"); Set(item, "ParentInstanceId", "");
                Set(item, "Revision", 1u); Set(item, "LocationKind", 3); Set(item, "Slot", w); Set(item, "UseTemplateWandStats", true);
                items.SetValue(item, index++); var current = Core("OnlineInventoryWandPresentationSnapshot");
                Set(current, "InstanceId", wandId); Set(current, "ProgramRevision", 5u); Set(current, "CurrentMana", 12.5f);
                Set(current, "CooldownRemaining", .75f); wands.SetValue(current, w);
                for (int c = 0; c < cardsPerWand; c++)
                {
                    var installed = Core("OnlineOwnedItemSnapshot"); string id = Id(5 + w * 33 + c);
                    Set(installed, "InstanceId", id); Set(installed, "ContentId", "spell.emit.fireball"); Set(installed, "ParentInstanceId", wandId);
                    Set(installed, "Revision", 1u); Set(installed, "LocationKind", 2); Set(installed, "Slot", c); Set(installed, "RemainingCharges", 23);
                    items.SetValue(installed, index++); var use = Core("OnlineInventorySpellUsePresentationSnapshot");
                    Set(use, "InstanceId", id); Set(use, "Eligible", true); uses.SetValue(use, card++);
                }
            }
            Set(assets, "Items", items); Set(source, "Wands", wands); Set(source, "SpellUses", uses); return source;
        }

        private byte[] Encode(object value) { Assert.That(TryEncode(value, out var bytes), Is.True); return bytes; }
        private bool TryEncode(object value, out byte[] bytes)
        {
            var buffer = new byte[65536]; var args = new object[] { value, new ArraySegment<byte>(buffer), 0 };
            bool accepted = (bool)_codecType.GetMethod("TryEncode").Invoke(_codec, args);
            bytes = accepted ? new byte[(int)args[2]] : null; if (accepted) Array.Copy(buffer, bytes, bytes.Length); return accepted;
        }
        private bool Decode(byte[] bytes, out object value)
        { var args = new object[] { new ArraySegment<byte>(bytes), null }; bool ok = (bool)_codecType.GetMethod("TryDecode").Invoke(_codec, args); value = args[1]; return ok; }
        private static Type CoreType(string name)
        { var type = Type.GetType("Game.Core." + name + ", Game.Core"); Assert.That(type, Is.Not.Null, "基础值合同应位于Core：" + name); return type; }
        private static object Core(string name) => Activator.CreateInstance(CoreType(name));
        private static string Id(int n) => n.ToString("x32");
        private static void Set(object value, string field, object content) => value.GetType().GetField(field).SetValue(value, content);
        private static T Get<T>(object value, string field) => (T)value.GetType().GetField(field).GetValue(value);

        private static byte[] Field(uint number, ulong value)
        { using var stream = new MemoryStream(); Varint(stream, number << 3); Varint(stream, value); return stream.ToArray(); }
        private static byte[] Append(byte[] first, byte[] second)
        { var bytes = new byte[first.Length + second.Length]; Array.Copy(first, bytes, first.Length); Array.Copy(second, 0, bytes, first.Length, second.Length); return bytes; }
        private static byte[] Payload(byte[] bytes, int number)
        { Locate(bytes, number, out _, out int start, out int end); var result = new byte[end - start]; Array.Copy(bytes, start, result, 0, result.Length); return result; }
        private static byte[] ReplacePayload(byte[] bytes, int number, byte[] replacement)
        {
            Locate(bytes, number, out int tag, out _, out int end); using var stream = new MemoryStream(); stream.Write(bytes, 0, tag);
            Varint(stream, (uint)(number << 3 | 2)); Varint(stream, (ulong)replacement.Length); stream.Write(replacement, 0, replacement.Length);
            stream.Write(bytes, end, bytes.Length - end); return stream.ToArray();
        }
        private static void Locate(byte[] bytes, int number, out int tagStart, out int payloadStart, out int end)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int start = offset; ulong tag = Read(bytes, ref offset); int wire = (int)(tag & 7);
                if (wire == 2) { int length = (int)Read(bytes, ref offset); if ((int)(tag >> 3) == number) { tagStart = start; payloadStart = offset; end = offset + length; return; } offset += length; }
                else if (wire == 0) Read(bytes, ref offset); else offset += wire == 1 ? 8 : 4;
            }
            throw new InvalidOperationException("Fixture字段不存在：" + number);
        }
        private static ulong Read(byte[] bytes, ref int offset)
        { ulong value = 0; for (int bit = 0; bit < 64; bit += 7) { byte b = bytes[offset++]; value |= (ulong)(b & 127) << bit; if ((b & 128) == 0) return value; } throw new InvalidOperationException(); }
        private static void Varint(Stream stream, ulong value)
        { while (value >= 128) { stream.WriteByte((byte)(value | 128)); value >>= 7; } stream.WriteByte((byte)value); }
    }
}
