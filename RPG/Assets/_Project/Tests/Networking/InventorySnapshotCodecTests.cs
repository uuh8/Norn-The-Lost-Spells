using System;
using System.IO;
using System.Reflection;
using System.Text;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;

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

        [Test] public void HomeResearchWirePreservesItsOpenVersionAndCanonicalSubtree()
        {
            var source = ResearchSnapshot(); byte[] bytes = EncodeExact(source);
            Assert.That(Decode(bytes, out var restored), Is.True);
            Assert.That(Get<bool>(restored, "ResearchOpen"), Is.True);
            Assert.That(Get<uint>(restored, "ResearchSessionVersion"), Is.EqualTo(2));
            Assert.That(JsonUtility.ToJson(Get<object>(restored, "Research")), Is.EqualTo(JsonUtility.ToJson(Get<object>(source, "Research"))));
        }

        [Test] public void ClosedResearchOwnerPreservesTheSessionRevisionAndOmitsTheJsonPayload()
        {
            var source = ResearchSnapshot(); Set(source, "ResearchOpen", false); Set(source, "ResearchSessionVersion", 3u);
            Set(source, "Research", Core("OnlineWandResearchStateSnapshot"));
            var bytes = EncodeExact(source); Assert.That(Decode(bytes, out var restored), Is.True);
            Assert.That(Get<uint>(restored, "ResearchSessionVersion"), Is.EqualTo(3)); Assert.That(Get<object>(restored, "Research"), Is.Null);
        }

        [TestCase("version")] [TestCase("closed")] [TestCase("foreign")] [TestCase("resource")]
        public void ResearchEncodeRejectsUnversionedOpenClosedLivePayloadOrDifferentOwner(string bad)
        {
            var source = ResearchSnapshot(); var research = Get<object>(source, "Research");
            if (bad == "version") Set(source, "ResearchSessionVersion", 0u);
            if (bad == "closed") Set(source, "ResearchOpen", false);
            if (bad == "foreign") Set(research, "PlayerId", Id(100));
            if (bad == "resource") Set(research, "ResourceVersion", 1u);
            Assert.That(TryEncodeLarge(source, out var result), Is.False); Assert.That(result, Is.Null);
        }

        [TestCase("closed")] [TestCase("duplicate-wire")] [TestCase("unknown-json")] [TestCase("duplicate-json")]
        [TestCase("utf8")] [TestCase("depth")] [TestCase("oversize")]
        public void ResearchWireRejectsUnknownDuplicateMalformedOrOversizeSubtreeBeforeInstallation(string bad)
        {
            var source = ResearchSnapshot(); string json = JsonUtility.ToJson(Get<object>(source, "Research"));
            Set(source, "ResearchOpen", false); Set(source, "ResearchSessionVersion", 0u); Set(source, "Research", null);
            byte[] bytes = EncodeExact(source);
            if (bad != "closed") bytes = Append(bytes, Field(14, 1));
            bytes = Append(bytes, Field(15, 2));
            if (bad == "unknown-json") json = json.Insert(1, "\"Unknown\":true,");
            if (bad == "duplicate-json") json = json.Insert(1, "\"SchemaVersion\":1,");
            if (bad == "depth") json = new string('[', 16) + "0" + new string(']', 16);
            byte[] payload = bad == "utf8" ? new byte[] { 0xff } : bad == "oversize" ? new byte[24 * 1024 * 1024 + 1] : Encoding.UTF8.GetBytes(json);
            bytes = Append(bytes, LengthField(16, payload));
            if (bad == "duplicate-wire") bytes = Append(bytes, Field(14, 1));
            Assert.That(Decode(bytes, out var rejected), Is.False); Assert.That(rejected, Is.Null);
        }
        private static object ResearchSnapshot()
        {
            var owner = GoldSnapshot(0, 1); var assets = Get<object>(owner, "Assets"); var value = Core("OnlineWandResearchStateSnapshot");
            Set(value, "SchemaVersion", 1u); Set(value, "PlayerId", Get<string>(assets, "PlayerId"));
            Set(value, "InventoryId", Get<string>(assets, "InventoryId")); Set(value, "GameplayConfigHash", Hash);
            Set(value, "InventoryVersion", 7u); Set(value, "ResourceVersion", 2u); Set(value, "SpellUseVersion", 1u);
            Set(owner, "ResearchOpen", true); Set(owner, "ResearchSessionVersion", 2u); Set(owner, "Research", value); return owner;
        }

        [TestCase(0L)] [TestCase(10000L)] [TestCase(long.MaxValue)]
        public void UnifiedGoldRoundTripsFullSignedNonNegativeRange(long gold)
        {
            var source = GoldSnapshot(gold, 7); byte[] encoded = EncodeExact(source);
            Assert.That(Decode(encoded, out var restored), Is.True);
            var assets = Get<object>(restored, "Assets");
            Assert.That(Get<long>(assets, "Gold"), Is.EqualTo(gold));
            Assert.That(Get<uint>(assets, "CurrencyVersion"), Is.EqualTo(7));
        }

        [TestCase(-1L, 1u)] [TestCase(0L, 0u)]
        public void UnifiedGoldRejectsNegativeOrUnversionedBalance(long gold, uint version)
        {
            Assert.That(TryEncodeLarge(GoldSnapshot(gold, version), out var bytes), Is.False);
            Assert.That(bytes, Is.Null);
        }

        [Test] public void UnifiedGoldWireRejectsSignedNegativeWithoutUintTruncation()
        {
            var message = InventorySnapshotMessage.Parser.ParseFrom(EncodeExact(GoldSnapshot(17, 4)));
            var field = typeof(InventoryOwnedAssetMessage).GetProperty("Gold");
            Assert.That(field, Is.Not.Null); field.SetValue(message.Assets, -1L);
            Assert.That(Decode(Google.Protobuf.MessageExtensions.ToByteArray(message), out var rejected), Is.False);
            Assert.That(rejected, Is.Null);
        }

        [Test] public void UnifiedGoldResetClearsRetainedValuesAndUnknownFields()
        {
            var message = InventorySnapshotMessage.Parser.ParseFrom(EncodeExact(GoldSnapshot(long.MaxValue, uint.MaxValue)));
            var assets = message.Assets;
            Google.Protobuf.MessageExtensions.MergeFrom(assets, Field(12, 5));
            message.ResetForReuse();
            Assert.That(assets.GetType().GetProperty("Gold").GetValue(assets), Is.EqualTo(0L));
            Assert.That(assets.GetType().GetProperty("CurrencyVersion").GetValue(assets), Is.EqualTo(0u));
            Assert.That(assets.CalculateSize(), Is.Zero); Assert.That(message.CalculateSize(), Is.Zero);
        }

        private static object GoldSnapshot(long gold, uint version)
        {
            var source = Snapshot(0, 0); var assets = Get<object>(source, "Assets");
            Set(assets, "SelectedWandInstanceId", string.Empty); // 空手合法Owner，不残留原有杖ID夹具。
            var currency = assets.GetType().GetField("Gold"); var revision = assets.GetType().GetField("CurrencyVersion");
            Assert.That(currency, Is.Not.Null, "Owned合同缺少可信总余额，不能用默认0冒充。");
            Assert.That(revision, Is.Not.Null, "金币变化必须有独立修订，不能借ItemStore版本。");
            currency.SetValue(assets, gold); revision.SetValue(assets, version);
            return source;
        }

        [Test] public void EmptyHandsSnapshotPreservesRealBackpackWithoutCreatingWandResources()
        {
            var source = Snapshot(0, 0); var assets = Get<object>(source, "Assets");
            Set(assets, "SelectedWandInstanceId", "");
            var items = Array.CreateInstance(CoreType("OnlineOwnedItemSnapshot"), 2);
            var bag = Item(10000, 4, "", 0); var marker = bag.GetType().GetField("EntryBoundBackpack");
            Assert.That(marker, Is.Not.Null, "保底包资格必须沿可信快照传递，不能只凭ContentId猜。");
            marker.SetValue(bag, true); items.SetValue(bag, 0); items.SetValue(Item(10001, 1, Id(10000), -1), 1);
            Set(assets, "Items", items); byte[] bytes = EncodeExact(source);
            Assert.That(Decode(bytes, out var restored), Is.True);
            Assert.That(Get<Array>(restored, "Wands").Length, Is.Zero);
            Assert.That(Get<string>(Get<object>(restored, "Assets"), "SelectedWandInstanceId"), Is.Empty);
            Assert.That(Get<bool>(Get<Array>(Get<object>(restored, "Assets"), "Items").GetValue(0), "EntryBoundBackpack"), Is.True);
        }

        [Test] public void CompletelyEmptyOwnerIsLegalWithoutInventingDefaultEquipment()
        {
            var source = Snapshot(0, 0); Set(Get<object>(source, "Assets"), "SelectedWandInstanceId", "");
            Assert.That(Decode(EncodeExact(source), out var restored), Is.True);
            Assert.That(Get<Array>(Get<object>(restored, "Assets"), "Items").Length, Is.Zero);
            Assert.That(Get<Array>(restored, "Wands").Length, Is.Zero);
            Assert.That(Get<Array>(restored, "SpellUses").Length, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void BackpackWandsRoundTripRealProgramsAndResourcesWithoutBecomingSelected(bool onlyBackpack)
        {
            var source = MixedSnapshot(); var assets = Get<object>(source, "Assets");
            var wands = Array.CreateInstance(CoreType("OnlineInventoryWandPresentationSnapshot"), 2);
            wands.SetValue(Get<Array>(source, "Wands").GetValue(0), 0);
            var spare = Core("OnlineInventoryWandPresentationSnapshot"); Set(spare, "InstanceId", Id(10003));
            Set(spare, "ProgramRevision", 37u); Set(spare, "CurrentMana", 19.5f); Set(spare, "CooldownRemaining", 1.75f);
            wands.SetValue(spare, 1); Set(source, "Wands", wands);
            if (onlyBackpack)
            {
                var original = Get<Array>(assets, "Items").GetValue(0); Set(original, "LocationKind", 1);
                Set(original, "ParentInstanceId", Id(10000)); Set(original, "Slot", -1); Set(original, "X", 1);
                Set(assets, "SelectedWandInstanceId", "");
            }
            Assert.That(Decode(EncodeExact(source), out var restored), Is.True,
                "包内备用杖已有真实程序和资源状态，不能因三装备槽限制丢掉ProgramRevision或补模板Mana。");
            var current = Get<Array>(restored, "Wands").GetValue(1);
            Assert.That(Get<uint>(current, "ProgramRevision"), Is.EqualTo(37));
            Assert.That(Get<float>(current, "CurrentMana"), Is.EqualTo(19.5f));
            Assert.That(Get<float>(current, "CooldownRemaining"), Is.EqualTo(1.75f));
            Assert.That(Get<string>(Get<object>(restored, "Assets"), "SelectedWandInstanceId"), Is.EqualTo(onlyBackpack ? "" : Id(4)));
            Set(assets, "SelectedWandInstanceId", Id(10003));
            Assert.That(TryEncodeLarge(source, out _), Is.False, "备用杖资源可读不等于获得装备/选中资格。");
        }

        [Test] public void GeneralGroundWandTreeRoundTripsCompleteRootAndInstalledCards()
        {
            var source = GeneralGroundSnapshot(); byte[] bytes = EncodeExact(source);
            Assert.That(Decode(bytes, out var restored), Is.True);
            var ground = Get<Array>(Get<object>(restored, "Ground"), "Items").GetValue(0);
            var item = Get<object>(ground, "Item"); var children = Get<Array>(ground, "Children");
            Assert.That(Get<string>(item, "InstanceId"), Is.EqualTo(Id(9000)));
            Assert.That(Get<bool>(item, "UseTemplateWandStats"), Is.True);
            Assert.That(Get<int>(children.GetValue(0), "RemainingCharges"), Is.EqualTo(17));
            Assert.That(Get<string>(children.GetValue(0), "ParentInstanceId"), Is.EqualTo(Id(9000)));
            Assert.That(ReferenceEquals(item, Get<object>(Get<Array>(Get<object>(source, "Ground"), "Items").GetValue(0), "Item")), Is.False);
        }

        [Test] public void GeneralGroundResetClearsRetainedTreeBoundMarkerAndUnknownFields()
        {
            var parent = new InventoryGroundItemMessage(); var root = new InventoryOwnedItemMessage();
            var itemProperty = parent.GetType().GetProperty("Item"); var childrenProperty = parent.GetType().GetProperty("Children");
            Assert.That(itemProperty, Is.Not.Null); Assert.That(childrenProperty, Is.Not.Null);
            var bound = root.GetType().GetProperty("EntryBoundBackpack"); Assert.That(bound, Is.Not.Null);
            bound.SetValue(root, true); Google.Protobuf.MessageExtensions.MergeFrom(root, Field(18, 9));
            var child = new InventoryOwnedItemMessage { InstanceId = Id(9001), RemainingCharges = 17 };
            Google.Protobuf.MessageExtensions.MergeFrom(child, Field(18, 9)); itemProperty.SetValue(parent, root);
            childrenProperty.GetValue(parent).GetType().GetMethod("Add", new[] { typeof(InventoryOwnedItemMessage) })
                .Invoke(childrenProperty.GetValue(parent), new object[] { child });
            Google.Protobuf.MessageExtensions.MergeFrom(parent, Field(16, 9)); parent.ResetForReuse();
            Assert.That(parent.CalculateSize(), Is.Zero); Assert.That(root.CalculateSize(), Is.Zero); Assert.That(child.CalculateSize(), Is.Zero);
        }

        [TestCase("root-missing")] [TestCase("children-null")] [TestCase("alias-id")] [TestCase("alias-state")]
        [TestCase("root-parent")] [TestCase("root-slot")] [TestCase("root-bound")] [TestCase("not-wand")]
        [TestCase("child-parent")] [TestCase("child-owned")] [TestCase("child-root")]
        [TestCase("child-slot")] [TestCase("child-kind")] [TestCase("child-state")] [TestCase("child-position")]
        [TestCase("duplicate-child")] [TestCase("duplicate-slot")]
        public void GeneralGroundRejectsAliasMismatchAndMalformedWandClosure(string invalid)
        {
            var source = GeneralGroundSnapshot(); var ground = Get<Array>(Get<object>(source, "Ground"), "Items").GetValue(0);
            var root = Get<object>(ground, "Item"); var child = Get<Array>(ground, "Children").GetValue(0);
            if (invalid == "root-missing") Set(ground, "Item", null);
            else if (invalid == "children-null") Set(ground, "Children", null);
            else if (invalid == "alias-id") Set(ground, "InstanceId", Id(9002));
            else if (invalid == "alias-state") Set(ground, "ContainerState", Container());
            else if (invalid == "root-parent") Set(root, "ParentInstanceId", Id(700));
            else if (invalid == "root-slot") Set(root, "Slot", 0);
            else if (invalid == "root-bound") Set(root, "EntryBoundBackpack", true);
            else if (invalid == "not-wand") Set(root, "UseTemplateWandStats", false);
            else if (invalid == "child-parent") Set(child, "ParentInstanceId", Id(9002));
            else if (invalid == "child-owned") Set(child, "InstanceId", Id(5));
            else if (invalid == "child-root") Set(child, "InstanceId", Id(9000));
            else if (invalid == "child-slot") Set(child, "Slot", 32);
            else if (invalid == "child-kind") Set(child, "LocationKind", 1);
            else if (invalid == "child-state") Set(child, "ContainerState", Container());
            else if (invalid == "child-position") Set(child, "X", 1);
            else
            {
                var children = Array.CreateInstance(CoreType("OnlineOwnedItemSnapshot"), 2); children.SetValue(child, 0);
                children.SetValue(Item(invalid == "duplicate-child" ? 9001 : 9002, 2, Id(9000), invalid == "duplicate-child" ? 1 : 0), 1);
                Set(ground, "Children", children);
            }
            Assert.That(TryEncodeLarge(source, out var rejected), Is.False, invalid); Assert.That(rejected, Is.Null);
        }

        [Test] public void GeneralGroundPlainItemPreservesChargesAttachmentAndIndependentRootState()
        {
            var source = GeneralGroundSnapshot(); var ground = Get<Array>(Get<object>(source, "Ground"), "Items").GetValue(0);
            var root = Get<object>(ground, "Item"); Set(root, "UseTemplateWandStats", false); Set(root, "RemainingCharges", 11);
            Set(ground, "Children", Array.CreateInstance(CoreType("OnlineOwnedItemSnapshot"), 0));
            var state = Core("OnlineOwnedSpellAttachmentStateSnapshot"); Set(state, "SchemaVersion", 1u);
            Set(state, "ContainerId", Id(9999)); Set(state, "Dose", ulong.MaxValue); Set(state, "Enabled", false); Set(root, "AttachmentState", state);
            Assert.That(Decode(EncodeExact(source), out var restored), Is.True);
            var copy = Get<object>(Get<Array>(Get<object>(restored, "Ground"), "Items").GetValue(0), "Item");
            Assert.That(Get<int>(copy, "RemainingCharges"), Is.EqualTo(11));
            Assert.That(Get<ulong>(Get<object>(copy, "AttachmentState"), "Dose"), Is.EqualTo(ulong.MaxValue));
        }

        [Test] public void GeneralGroundChildrenConsumeTheExistingTotalInstanceBudget()
        {
            var source = GroundSnapshot(16384); var items = Get<Array>(Get<object>(source, "Ground"), "Items");
            var tree = Get<Array>(Get<object>(GeneralGroundSnapshot(), "Ground"), "Items").GetValue(0); items.SetValue(tree, 0);
            var arguments = new object[] { source, null };
            Assert.That((bool)ExactEncoder().Invoke(_codec, arguments), Is.False, "16384根中的一根再含1张卡，总实例16385必须拒绝。");
            Assert.That(arguments[1], Is.Null);
            var smaller = Array.CreateInstance(CoreType("OnlineGroundItemSnapshot"), 16383); Array.Copy(items, smaller, smaller.Length);
            Set(Get<object>(source, "Ground"), "Items", smaller); byte[] bytes = EncodeExact(source);
            Assert.That(Decode(bytes, out var restored), Is.True, "16383根加1张卡恰好使用原16384总界。");
            Assert.That(Get<Array>(Get<object>(restored, "Ground"), "Items").Length, Is.EqualTo(16383));
            var wireGround = Payload(bytes, 13); var additionalRoot = Payload(Payload(EncodeExact(GroundSnapshot(1)), 13), 2);
            Assert.That(Decode(ReplacePayload(bytes, 13, Append(wireGround, LengthField(2, additionalRoot))), out _), Is.False);
        }

        [Test] public void GeneralGroundRawPrecheckRejectsMissingRootDuplicateScalarAndInvalidChildState()
        {
            byte[] bytes = EncodeExact(GeneralGroundSnapshot()); var world = Payload(bytes, 13); var envelope = Payload(world, 2);
            var root = Payload(envelope, 14); var child = Payload(envelope, 15);
            foreach (byte[] invalid in new[] {
                ReplaceWireField(envelope, 14, Array.Empty<byte>()), Append(envelope, LengthField(14, root)), Append(envelope, Field(16, 1)),
                ReplacePayload(envelope, 14, Append(root, Field(17, 2))), ReplacePayload(envelope, 15, Append(child, Field(18, 1))),
                ReplacePayload(envelope, 15, Append(child, Field(4, 1))), ReplacePayload(envelope, 14, Append(root, LengthField(13, FloatField(1, float.NaN)))) })
                Assert.That(Decode(ReplacePayload(bytes, 13, ReplacePayload(world, 2, invalid)), out _), Is.False);
            var tooMany = envelope;
            for (int i = 0; i < 32; i++) tooMany = Append(tooMany, LengthField(15, child));
            Assert.That(Decode(ReplacePayload(bytes, 13, ReplacePayload(world, 2, tooMany)), out _), Is.False);
        }

        private static object GeneralGroundSnapshot()
        {
            var source = GroundSnapshot(1); var ground = Get<Array>(Get<object>(source, "Ground"), "Items").GetValue(0);
            var root = Item(9000, 5, "", -1); Set(root, "UseTemplateWandStats", true);
            Set(ground, "InstanceId", Id(9000)); Set(ground, "ContentId", "item.from.catalog"); Set(ground, "WorldOwnerId", Id(700));
            Set(ground, "ContainerState", null); Set(ground, "Revision", 1u);
            var rootField = ground.GetType().GetField("Item"); var childrenField = ground.GetType().GetField("Children");
            Assert.That(rootField, Is.Not.Null, "共享地面必须携带完整Owned根状态，不能只保留瓶/固体特例。");
            Assert.That(childrenField, Is.Not.Null, "掉杖必须保留原安装卡的唯一实例和次数。");
            rootField.SetValue(ground, root); var children = Array.CreateInstance(CoreType("OnlineOwnedItemSnapshot"), 1);
            var child = Item(9001, 2, Id(9000), 0); Set(child, "RemainingCharges", 17); children.SetValue(child, 0);
            childrenField.SetValue(ground, children); return source;
        }

        [Test] public void SpellAttachmentPreservesDisabledStaleSourceAndFullUlongIndependently()
        {
            foreach (bool enabled in new[] { false, true })
            {
                var source = AttachmentSnapshot(ulong.MaxValue, enabled); byte[] bytes = EncodeExact(source);
                Assert.That(Decode(bytes, out var restored), Is.True);
                var card = Get<Array>(Get<object>(restored, "Assets"), "Items").GetValue(1);
                var state = Get<object>(card, "AttachmentState"); Assert.That(state, Is.Not.Null,
                    "完整Owner必须保留具体卡的显式附件；来源已消失不代表可丢弃配置。");
                Assert.That(Get<uint>(state, "SchemaVersion"), Is.EqualTo(1));
                Assert.That(Get<string>(state, "ContainerId"), Is.EqualTo(Id(900000)), "来源故意不在闭包内，是否还能供料由Run执行时裁决。");
                Assert.That(Get<ulong>(state, "Dose"), Is.EqualTo(ulong.MaxValue));
                Assert.That(Get<bool>(state, "Enabled"), Is.EqualTo(enabled));
                Set(state, "Dose", 1UL); Assert.That(Decode(bytes, out var independent), Is.True);
                Assert.That(Get<ulong>(Get<object>(Get<Array>(Get<object>(independent, "Assets"), "Items").GetValue(1), "AttachmentState"), "Dose"), Is.EqualTo(ulong.MaxValue));
            }
        }

        [Test] public void SpellAttachmentRejectsInvalidStateAmbiguityAndMalformedWire()
        {
            foreach (string invalid in new[] { "schema", "source", "dose", "container", "material", "wand" })
            {
                var source = AttachmentSnapshot(23, true); var card = Get<Array>(Get<object>(source, "Assets"), "Items").GetValue(1);
                var state = Get<object>(card, "AttachmentState");
                if (invalid == "schema") Set(state, "SchemaVersion", 0u);
                else if (invalid == "source") Set(state, "ContainerId", new string('g', 32));
                else if (invalid == "dose") Set(state, "Dose", 0UL);
                else if (invalid == "container") Set(card, "ContainerState", Container());
                else if (invalid == "material") SetMaterialState(card, 1);
                else Set(card, "UseTemplateWandStats", true);
                Assert.That(TryEncodeLarge(source, out var payload), Is.False, invalid); Assert.That(payload, Is.Null);
            }
            byte[] bytes = Encode(Snapshot()); var assets = Payload(bytes, 9); var cardBytes = Payload(assets, 9, 1);
            byte[] header = Append(Field(1, 1), LengthField(2, System.Text.Encoding.ASCII.GetBytes(Id(900000))));
            byte[] valid = Append(header, Field(3, ulong.MaxValue));
            Assert.That(Decode(ReplacePayload(bytes, 9, ReplacePayload(assets, 9, Append(cardBytes, LengthField(16, valid)), 1)), out _), Is.True,
                "Enabled省略是false但完整schema/source/dose仍是有效显式状态。");
            foreach (byte[] malformed in new[] {
                ReplaceWireField(valid, 1, Array.Empty<byte>()), ReplaceWireField(valid, 2, Array.Empty<byte>()), ReplaceWireField(valid, 3, Array.Empty<byte>()),
                Append(valid, Field(5, 1)), Append(valid, Field(3, 1)), Append(valid, Field(4, 2)),
                ReplaceWireField(valid, 3, FloatField(3, 1)), ReplaceWireField(valid, 3, new byte[] { 24, 129, 0 }),
                ReplaceWireField(valid, 3, new byte[] { 24, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2 }),
                ReplaceScalar(valid, 1, (ulong)uint.MaxValue + 2), ReplacePayload(valid, 2, System.Text.Encoding.ASCII.GetBytes(new string('a', 33))) })
                Assert.That(Decode(ReplacePayload(bytes, 9, ReplacePayload(assets, 9, Append(cardBytes, LengthField(16, malformed)), 1)), out var rejected), Is.False);
        }

        [Test] public void SpellAttachmentResetClearsRetainedChildAndUnknownFields()
        {
            var type = typeof(ProtocolCompatibility).Assembly.GetType("Game.NetworkProtocol.InventorySpellAttachmentStateMessage");
            Assert.That(type, Is.Not.Null); var child = (Google.Protobuf.IMessage)Activator.CreateInstance(type);
            var fields = Append(Append(Field(1, 1), LengthField(2, System.Text.Encoding.ASCII.GetBytes(Id(900000)))), Field(3, ulong.MaxValue));
            Google.Protobuf.MessageExtensions.MergeFrom(child, Append(Append(fields, Field(4, 1)), Field(5, 7)));
            var parent = new InventoryOwnedItemMessage(); var property = parent.GetType().GetProperty("AttachmentState");
            Assert.That(property, Is.Not.Null); property.SetValue(parent, child); parent.ResetForReuse();
            Assert.That(property.GetValue(parent), Is.Null); Assert.That(child.CalculateSize(), Is.Zero);
            Assert.That(parent.CalculateSize(), Is.Zero, "复用必须清掉附件及其unknown fields，不能只断开父引用。");
        }

        private static object AttachmentSnapshot(ulong dose, bool enabled)
        {
            var source = Snapshot(); var card = Get<Array>(Get<object>(source, "Assets"), "Items").GetValue(1);
            var field = card.GetType().GetField("AttachmentState"); Assert.That(field, Is.Not.Null);
            var state = Core("OnlineOwnedSpellAttachmentStateSnapshot"); Set(state, "SchemaVersion", 1u);
            Set(state, "ContainerId", Id(900000)); Set(state, "Dose", dose); Set(state, "Enabled", enabled);
            field.SetValue(card, state); return source;
        }

        [Test] public void SolidMaterialOwnedAndGroundRoundTripFullUlongAsIndependentState()
        {
            // 固体不复用瓶子的组分数组；同一份Wire必须精确保留uint64极值，而且每次Decode拥有独立状态。
            var source = SolidSnapshot(ulong.MaxValue); byte[] bytes = EncodeExact(source);
            Assert.That(Decode(bytes, out var restored), Is.True);
            var owned = Get<object>(Get<Array>(Get<object>(restored, "Assets"), "Items").GetValue(9), "MaterialState");
            var ground = Get<object>(Get<Array>(Get<object>(restored, "Ground"), "Items").GetValue(0), "MaterialState");
            Assert.That(Get<uint>(owned, "SchemaVersion"), Is.EqualTo(1));
            Assert.That(Get<byte>(owned, "MaterialKey"), Is.EqualTo(3));
            Assert.That(Get<ulong>(owned, "Quantity"), Is.EqualTo(ulong.MaxValue));
            Assert.That(Get<ulong>(ground, "Quantity"), Is.EqualTo(ulong.MaxValue));
            Assert.That(ReferenceEquals(owned, ground), Is.False);
            Set(owned, "Quantity", 1UL); Assert.That(Decode(bytes, out var independent), Is.True);
            Assert.That(Get<ulong>(Get<object>(Get<Array>(Get<object>(independent, "Assets"), "Items").GetValue(9), "MaterialState"), "Quantity"), Is.EqualTo(ulong.MaxValue));
        }

        [Test] public void SolidMaterialRejectsInvalidOrAmbiguousStateOnBothWireAndDto()
        {
            foreach (string invalid in new[] { "schema", "key0", "key9", "quantity", "container", "wand" })
            {
                var source = SolidSnapshot(256); var item = Get<Array>(Get<object>(source, "Assets"), "Items").GetValue(9);
                var state = Get<object>(item, "MaterialState");
                if (invalid == "schema") Set(state, "SchemaVersion", 0u);
                else if (invalid == "key0") Set(state, "MaterialKey", (byte)0);
                else if (invalid == "key9") Set(state, "MaterialKey", (byte)9);
                else if (invalid == "quantity") Set(state, "Quantity", 0UL);
                else if (invalid == "container") Set(item, "ContainerState", Container());
                else Set(item, "UseTemplateWandStats", true);
                Assert.That(TryEncodeLarge(source, out var rejected), Is.False, invalid); Assert.That(rejected, Is.Null);
            }
            byte[] bytes = EncodeExact(SolidSnapshot(256)); var assets = Payload(bytes, 9); var owned = Payload(assets, 9, 9);
            var ground = Payload(bytes, 13); var onGround = Payload(ground, 2);
            foreach (byte[] state in new[] { Append(Field(2, 3), Field(3, 256)), Append(Field(1, 1), Field(3, 256)), Append(Field(1, 1), Field(2, 3)) })
            {
                Assert.That(Decode(ReplacePayload(bytes, 9, ReplacePayload(assets, 9, ReplacePayload(owned, 15, state), 9)), out _), Is.False,
                    "MaterialState的schema/key/quantity必须各自显式存在，缺字段不能按模板补量。");
                Assert.That(Decode(ReplacePayload(bytes, 13, ReplacePayload(ground, 2, ReplacePayload(onGround, 13, state))), out _), Is.False);
            }
            Assert.That(Decode(ReplacePayload(bytes, 13, ReplacePayload(ground, 2, Append(onGround, LengthField(12, Field(1, 1))))), out _), Is.False,
                "同一地面实例不能同时声称固体和容器状态。");
        }

        [Test] public void SolidMaterialWireRejectsUnknownDuplicateNarrowingAndNonCanonicalNumbers()
        {
            byte[] bytes = EncodeExact(SolidSnapshot(256)); var assets = Payload(bytes, 9); var item = Payload(assets, 9, 9);
            var valid = Payload(item, 15);
            foreach (byte[] state in new[] {
                Append(valid, Field(4, 1)), Append(valid, Field(3, 256)), ReplaceScalar(valid, 2, 259),
                ReplaceWireField(valid, 3, FloatField(3, 256)), ReplaceWireField(valid, 3, new byte[] { 24, 128, 130, 0 }),
                ReplaceScalar(valid, 1, (ulong)uint.MaxValue + 2), ReplaceWireField(valid, 3, new byte[] { 24, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2 }) })
                Assert.That(Decode(ReplacePayload(bytes, 9, ReplacePayload(assets, 9, ReplacePayload(item, 15, state), 9)), out var result), Is.False);
        }

        [Test] public void SolidMaterialPooledResetClearsRetainedChildrenAndUnknownFields()
        {
            var type = typeof(ProtocolCompatibility).Assembly.GetType("Game.NetworkProtocol.InventoryOwnedMaterialStateMessage");
            Assert.That(type, Is.Not.Null, "固体状态必须有官方生成的独立Protobuf合同。");
            foreach (object parent in new object[] { new InventoryOwnedItemMessage(), new InventoryGroundItemMessage() })
            {
                var child = (Google.Protobuf.IMessage)Activator.CreateInstance(type);
                // Parser真实写入unknown fields；只清父引用会让保留的池对象把上个Owner的数量带到下一次。
                Google.Protobuf.MessageExtensions.MergeFrom(child, Append(Append(Append(Field(1, 1), Field(2, 3)), Field(3, ulong.MaxValue)), Field(4, 7)));
                var property = parent.GetType().GetProperty("MaterialState"); Assert.That(property, Is.Not.Null);
                property.SetValue(parent, child);
                parent.GetType().GetMethod("ResetForReuse").Invoke(parent, null);
                Assert.That(property.GetValue(parent), Is.Null); Assert.That(child.CalculateSize(), Is.Zero);
                Assert.That(((Google.Protobuf.IMessage)parent).CalculateSize(), Is.Zero);
            }
        }

        private static object SolidSnapshot(ulong quantity)
        {
            var source = MixedSnapshot(); var owned = Get<Array>(Get<object>(source, "Assets"), "Items").GetValue(9);
            SetMaterialState(owned, quantity);
            var ground = Get<object>(GroundSnapshot(1), "Ground"); var item = Get<Array>(ground, "Items").GetValue(0);
            Set(item, "ContainerState", null); SetMaterialState(item, quantity); SynchronizeGroundRoot(item); Set(source, "Ground", ground); return source;
        }
        private static void SetMaterialState(object item, ulong quantity)
        {
            var field = item.GetType().GetField("MaterialState"); Assert.That(field, Is.Not.Null, "真实固体必须显式携带MaterialState，不能借ContainerState或模板补量。");
            var state = Core("OnlineOwnedMaterialStateSnapshot"); Set(state, "SchemaVersion", 1u);
            Set(state, "MaterialKey", (byte)3); Set(state, "Quantity", quantity); field.SetValue(item, state);
        }

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
            Assert.That((bool)SegmentEncoder().Invoke(_codec, args), Is.False);
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
            Assert.That(Decode(Append(bytes, Field(14, 1)), out _), Is.False,
                "字段13已用于共享地面；紧邻的未知字段14仍必须拒绝。");
            var assets = Payload(bytes, 9);
            Assert.That(Decode(ReplacePayload(bytes, 9, Append(assets, Field(12, 1))), out _), Is.False); // 10/11已是金币合同，12仍未知。
        }

        [Test] public void DuplicateScalarFieldDoesNotUseProtobufLastWins()
        { Assert.That(Decode(Append(Encode(Snapshot()), Field(1, ProtocolCompatibility.CurrentVersion)), out _), Is.False); }

        [TestCase(1u)] [TestCase(7u)]
        public void SelectedToolSlotRoundTripsWithSharedSelectionVersionAndUnchangedSnapshotSchema(uint slotPlusOne)
        {
            var source = Snapshot(); SetSelectedTool(source, slotPlusOne);
            Assert.That(Decode(Encode(source), out var restored), Is.True);
            Assert.That(SelectedTool(restored), Is.EqualTo(slotPlusOne));
            Assert.That(Get<uint>(restored, "SchemaVersion"), Is.EqualTo(1));
            Assert.That(Get<uint>(restored, "SelectionVersion"), Is.EqualTo(1));
            Assert.That(Get<string>(Get<object>(restored, "Assets"), "SelectedWandInstanceId"), Is.EqualTo(Id(4)),
                "工具选择不抹掉已有装备杖身份；是否真实绑定容器由Run核验。");
        }

        [Test] public void MissingToolSelectionKeepsLegacyWandDerivedDefaultZero()
        {
            Assert.That(Decode(Encode(Snapshot()), out var restored), Is.True);
            Assert.That(SelectedTool(restored), Is.Zero);
            Assert.That(Decode(Append(Encode(Snapshot()), Field(12, 0)), out restored), Is.True);
            Assert.That(SelectedTool(restored), Is.Zero);
        }

        [TestCase(1u)] [TestCase(7u)]
        public void ToolSelectionWireFieldTwelveAcceptsOnlyValueWithoutInventingBinding(uint slotPlusOne)
        {
            // 这里没有EqTool项，Wire仍只校验值域。真实绑定/容器Kind不是Codec可推断的权限。
            Assert.That(Decode(Append(Encode(Snapshot()), Field(12, slotPlusOne)), out var restored), Is.True);
            Assert.That(SelectedTool(restored), Is.EqualTo(slotPlusOne));
        }

        [TestCase(8u)] [TestCase(255u)] [TestCase(257u)] [TestCase(uint.MaxValue)]
        public void ToolSelectionOutOfRangeFailsEncodeAndDecodeWithoutNarrowing(uint slotPlusOne)
        {
            var source = Snapshot(); SetSelectedTool(source, slotPlusOne);
            Assert.That(TryEncode(source, out var bytes), Is.False); Assert.That(bytes, Is.Null);
            Assert.That(Decode(Append(Encode(Snapshot()), Field(12, slotPlusOne)), out var restored), Is.False);
            Assert.That(restored, Is.Null);
        }

        [Test] public void ToolSelectionDuplicateWrongTypeAndOverlongVarintAreRejected()
        {
            var bytes = Encode(Snapshot());
            Assert.That(Decode(Append(Append(bytes, Field(12, 1)), Field(12, 7)), out _), Is.False);
            Assert.That(Decode(Append(bytes, LengthField(12, Array.Empty<byte>())), out _), Is.False);
            Assert.That(Decode(Append(bytes, new byte[] { 96, 129, 0 }), out _), Is.False);
            Assert.That(Decode(Append(bytes, new byte[] { 97, 1, 0, 0, 0, 0, 0, 0, 0 }), out _), Is.False);
        }

        [Test] public void PooledSnapshotResetClearsSelectedToolBeforeReuse()
        {
            var message = new InventorySnapshotMessage();
            var property = message.GetType().GetProperty("SelectedToolSlotPlusOne");
            Assert.That(property, Is.Not.Null, "生成合同必须提供字段12，不能把工具选择藏进unknown fields。");
            property.SetValue(message, 7u); message.ResetForReuse();
            Assert.That(property.GetValue(message), Is.EqualTo(0u), "复用消息不能残留上一位Owner的工具选择。");
            Assert.That(message.CalculateSize(), Is.Zero);
        }

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

        [Test] public void RealBackpackContainersSpareWandAndToolRoundTripWithoutInventingSpellUses()
        {
            var bytes = EncodeLarge(MixedSnapshot());
            Assert.That(Decode(bytes, out var result), Is.True);
            var items = Get<Array>(Get<object>(result, "Assets"), "Items");
            var bottle = items.GetValue(3); var state = Get<object>(bottle, "ContainerState");
            Assert.That(Get<int>(bottle, "Slot"), Is.EqualTo(-1), "真实格位使用canonical int32 -1，不得截成uint32。");
            Assert.That(Get<int>(bottle, "X"), Is.EqualTo(2)); Assert.That(Get<int>(bottle, "Y"), Is.EqualTo(3));
            Assert.That(Get<int>(bottle, "QuarterTurns"), Is.EqualTo(1));
            Assert.That(Get<uint>(state, "SchemaVersion"), Is.EqualTo(1));
            Assert.That(Get<ulong>(Get<Array>(state, "Components").GetValue(0), "Quantity"), Is.EqualTo((ulong)uint.MaxValue + 17));
            Assert.That(Get<Array>(Get<object>(items.GetValue(4), "ContainerState"), "Components").Length, Is.Zero);
            Assert.That(Get<int>(items.GetValue(5), "LocationKind"), Is.EqualTo(1));
            Assert.That(Get<string>(items.GetValue(6), "ParentInstanceId"), Is.EqualTo(Id(10003)));
            Assert.That(Get<int>(items.GetValue(7), "LocationKind"), Is.EqualTo(8));
            Assert.That(Get<Array>(result, "Wands").Length, Is.EqualTo(2), "备用杖也须携带真实程序版本/Mana/CD，仍不授予装备槽。");
            Assert.That(Get<Array>(result, "SpellUses").Length, Is.EqualTo(3), "容器、工具和普通包内物品不被当作卡。");
            Set(Get<Array>(state, "Components").GetValue(0), "Quantity", 1UL);
            Assert.That(Decode(bytes, out var independent), Is.True);
            Assert.That(Get<ulong>(Get<Array>(Get<object>(Get<Array>(Get<object>(independent, "Assets"), "Items").GetValue(3), "ContainerState"), "Components").GetValue(0), "Quantity"), Is.EqualTo((ulong)uint.MaxValue + 17));
        }

        [TestCase(172)] [TestCase(4096)]
        public void LegalOwnedRecordBudgetRoundTripsBeyondOldClosure(int count)
        {
            var source = LargeSnapshot(count); byte[] bytes = EncodeLarge(source);
            Assert.That(Decode(bytes, out var result), Is.True);
            Assert.That(Get<Array>(Get<object>(result, "Assets"), "Items").Length, Is.EqualTo(count));
            if (count == 4096) Assert.That(bytes.Length, Is.GreaterThan(65536));
        }

        [Test] public void MaximumSpareWandTraitsFitConservativePayloadBudget()
        {
            var source = LargeSnapshot(4096); var items = Get<Array>(Get<object>(source, "Assets"), "Items");
            var traits = new string[32]; for (int i = 0; i < traits.Length; i++) traits[i] = new string('a', 128);
            for (int i = 3; i < items.Length; i++) Set(items.GetValue(i), "WandStats", Stats(traits));
            SetSyntheticCarriedWandResources(source);
            byte[] bytes = EncodeExact(source);
            Assert.That(bytes.Length, Is.GreaterThan(16 * 1024 * 1024));
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(20 * 1024 * 1024));
            Assert.That(Decode(bytes, out var result), Is.True);
            Assert.That(Get<Array>(Get<object>(result, "Assets"), "Items").Length, Is.EqualTo(4096));
        }

        [Test] public void ExactEncoderAllocatesOnlyCalculatedBytesAndFailurePublishesNull()
        {
            var source = MixedSnapshot(); byte[] bytes = EncodeExact(source);
            Assert.That(bytes, Is.EqualTo(EncodeLarge(source)));
            var output = new byte[bytes.Length + 2]; output[0] = 61; output[output.Length - 1] = 62;
            var args = new object[] { source, new ArraySegment<byte>(output, 1, bytes.Length), 0 };
            Assert.That((bool)SegmentEncoder().Invoke(_codec, args), Is.True);
            Assert.That(args[2], Is.EqualTo(bytes.Length)); Assert.That(output[0], Is.EqualTo(61)); Assert.That(output[output.Length - 1], Is.EqualTo(62));
            args[1] = new ArraySegment<byte>(output, 1, bytes.Length - 1); args[2] = 12;
            Assert.That((bool)SegmentEncoder().Invoke(_codec, args), Is.False); Assert.That(args[2], Is.EqualTo(0));
            Set(Get<object>(source, "Assets"), "SchemaVersion", 1u);
            args = new object[] { source, new byte[] { 12 } };
            Assert.That((bool)ExactEncoder().Invoke(_codec, args), Is.False); Assert.That(args[1], Is.Null);
        }

        [Test] public void WireAndItemBudgetsAreFiniteAndIncludeFull4096Closure()
        {
            Assert.That(_codecType.GetField("MaxPayloadBytes").GetRawConstantValue(), Is.EqualTo(32 * 1024 * 1024));
            Assert.That(_codecType.GetField("MaxItems").GetRawConstantValue(), Is.EqualTo(4096));
            Assert.That(_codecType.GetField("MaxSpellUses").GetRawConstantValue(), Is.EqualTo(4096));
            Assert.That(TryEncodeLarge(LargeSnapshot(4097), out _), Is.False);
            Assert.That(Decode(new byte[20 * 1024 * 1024 + 1], out var result), Is.False); Assert.That(result, Is.Null);
            Assert.That(Decode(new byte[32 * 1024 * 1024 + 1], out result), Is.False); Assert.That(result, Is.Null);
            var assets = new byte[4097 * 2]; for (int i = 0; i < 4097; i++) assets[i * 2] = 74;
            Assert.That(Decode(ReplacePayload(Encode(Snapshot()), 9, assets), out _), Is.False);
        }

        [TestCase("schema")] [TestCase("null")] [TestCase("zero")] [TestCase("key0")]
        [TestCase("key9")] [TestCase("duplicate")] [TestCase("overflow")] [TestCase("count")]
        public void InvalidContainerStateNeverPublishesPayload(string invalid)
        {
            var source = MixedSnapshot(); var item = Get<Array>(Get<object>(source, "Assets"), "Items").GetValue(3);
            var state = Get<object>(item, "ContainerState"); var components = Get<Array>(state, "Components");
            if (invalid == "schema") Set(state, "SchemaVersion", 2u);
            else if (invalid == "null") Set(state, "Components", null);
            else if (invalid == "zero") Set(components.GetValue(0), "Quantity", 0UL);
            else if (invalid == "key0") Set(components.GetValue(0), "MaterialKey", (byte)0);
            else if (invalid == "key9") Set(components.GetValue(0), "MaterialKey", (byte)9);
            else if (invalid == "duplicate") Set(components.GetValue(1), "MaterialKey", (byte)1);
            else if (invalid == "overflow") Set(components.GetValue(0), "Quantity", ulong.MaxValue);
            else Set(state, "Components", Array.CreateInstance(CoreType("OnlineOwnedMaterialAmountSnapshot"), 9));
            Assert.That(TryEncodeLarge(source, out var bytes), Is.False); Assert.That(bytes, Is.Null);
        }

        [Test] public void SingleUlongMaximumQuantityIsLosslessAndEightKeysAreAllowed()
        {
            var source = MixedSnapshot(); var item = Get<Array>(Get<object>(source, "Assets"), "Items").GetValue(3);
            Set(item, "ContainerState", Container(ulong.MaxValue));
            Assert.That(Decode(EncodeLarge(source), out var result), Is.True);
            var state = Get<object>(Get<Array>(Get<object>(result, "Assets"), "Items").GetValue(3), "ContainerState");
            Assert.That(Get<ulong>(Get<Array>(state, "Components").GetValue(0), "Quantity"), Is.EqualTo(ulong.MaxValue));
            Set(item, "ContainerState", Container(1, 2, 3, 4, 5, 6, 7, 8));
            Assert.That(Decode(EncodeLarge(source), out _), Is.True);
        }

        [TestCase("orphan")] [TestCase("wrong-parent")] [TestCase("slot")]
        [TestCase("root-position")] [TestCase("root-slot-duplicate")]
        [TestCase("wand-slot-duplicate")] [TestCase("wand-container")] [TestCase("container-use")]
        [TestCase("missing-use")] [TestCase("missing-equipped-resource")]
        [TestCase("missing-spare-resource")] [TestCase("duplicate-resource")] [TestCase("non-wand-resource")]
        public void InvalidTopologyAndResourceAssociationsAreRejected(string invalid)
        {
            var source = MixedSnapshot(); var items = Get<Array>(Get<object>(source, "Assets"), "Items");
            if (invalid == "orphan") Set(items.GetValue(3), "ParentInstanceId", Id(123456));
            else if (invalid == "wrong-parent") Set(items.GetValue(3), "ParentInstanceId", Id(4));
            else if (invalid == "slot") Set(items.GetValue(3), "Slot", 0);
            else if (invalid == "root-position") Set(items.GetValue(2), "X", 1);
            else if (invalid == "root-slot-duplicate") { Set(items.GetValue(7), "LocationKind", 4); Set(items.GetValue(7), "Slot", 0); }
            else if (invalid == "wand-slot-duplicate") Set(items.GetValue(6), "ParentInstanceId", Id(4));
            else if (invalid == "wand-container") Set(items.GetValue(3), "UseTemplateWandStats", true);
            else if (invalid == "container-use") Set(Get<Array>(source, "SpellUses").GetValue(2), "InstanceId", Id(10001));
            else if (invalid == "missing-use") Set(source, "SpellUses", Array.CreateInstance(CoreType("OnlineInventorySpellUsePresentationSnapshot"), 0));
            else if (invalid == "missing-equipped-resource")
            {
                var states = Array.CreateInstance(CoreType("OnlineInventoryWandPresentationSnapshot"), 1);
                states.SetValue(Get<Array>(source, "Wands").GetValue(1), 0); Set(source, "Wands", states);
            }
            else if (invalid == "missing-spare-resource")
            {
                var states = Array.CreateInstance(CoreType("OnlineInventoryWandPresentationSnapshot"), 1);
                states.SetValue(Get<Array>(source, "Wands").GetValue(0), 0); Set(source, "Wands", states);
            }
            else Set(Get<Array>(source, "Wands").GetValue(1), "InstanceId", Id(invalid == "duplicate-resource" ? 4 : 10001));
            Assert.That(TryEncodeLarge(source, out _), Is.False);
        }

        [Test] public void SharedBackpackCoordinateDefersStackAndShapeValidationToCatalogOwner()
        {
            var source = MixedSnapshot(); var items = Get<Array>(Get<object>(source, "Assets"), "Items");
            // 真实库存允许相同格位的合法堆叠。Wire没有Stack规则，不能从XY相等推断占格冲突。
            Set(items.GetValue(9), "X", 6); Set(items.GetValue(9), "Y", 3);
            Assert.That(Decode(EncodeLarge(source), out _), Is.True);
        }

        [TestCase("unknown-state")] [TestCase("duplicate-state-schema")] [TestCase("component-count")]
        [TestCase("unknown-amount")] [TestCase("duplicate-quantity")] [TestCase("bad-varint")] [TestCase("key-narrowing")]
        [TestCase("nested-message")] [TestCase("oversized-content")] [TestCase("short-negative-slot")]
        public void MalformedContainerWireIsRejectedBeforeDtoPublication(string invalid)
        {
            byte[] bytes = EncodeLarge(MixedSnapshot()); var assets = Payload(bytes, 9); var item = Payload(assets, 9, 3);
            var state = Payload(item, 14); var amount = Payload(state, 2);
            if (invalid == "unknown-state") state = Append(state, Field(3, 1));
            else if (invalid == "duplicate-state-schema") state = Append(state, Field(1, 1));
            else if (invalid == "component-count") { state = Field(1, 1); for (int i = 0; i < 9; i++) state = Append(state, LengthField(2, Array.Empty<byte>())); }
            else if (invalid == "unknown-amount") state = ReplacePayload(state, 2, Append(amount, Field(3, 1)));
            else if (invalid == "duplicate-quantity") state = ReplacePayload(state, 2, Append(amount, Field(2, 3)));
            else if (invalid == "bad-varint") state = ReplacePayload(state, 2, new byte[] { 8, 1, 16, 128, 0 });
            else if (invalid == "key-narrowing") state = ReplacePayload(state, 2, ReplaceScalar(amount, 1, 257));
            else if (invalid == "nested-message") state = ReplacePayload(state, 2, LengthField(1, amount));
            else if (invalid == "oversized-content") item = ReplacePayload(item, 2, new byte[129]);
            else item = ReplaceScalar(item, 6, uint.MaxValue);
            if (invalid != "oversized-content" && invalid != "short-negative-slot") item = ReplacePayload(item, 14, state);
            bytes = ReplacePayload(bytes, 9, ReplacePayload(assets, 9, item, 3));
            Assert.That(Decode(bytes, out var result), Is.False); Assert.That(result, Is.Null);
        }

        [Test] public void GroundContainerIdentityPoseAndUlongCompositionRoundTripIndependently()
        {
            var source = GroundSnapshot(1); byte[] bytes = EncodeExact(source);
            Assert.That(Decode(bytes, out var result), Is.True);
            var ground = Get<object>(result, "Ground"); Assert.That(Get<uint>(ground, "Version"), Is.EqualTo(3));
            var item = Get<Array>(ground, "Items").GetValue(0);
            Assert.That(Get<string>(item, "InstanceId"), Is.EqualTo(Id(200000))); Assert.That(Get<string>(item, "WorldOwnerId"), Is.EqualTo(Id(300000)));
            Assert.That(Get<Vector3>(item, "Position"), Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(Get<Quaternion>(item, "Rotation"), Is.EqualTo(Quaternion.identity));
            var amount = Get<Array>(Get<object>(item, "ContainerState"), "Components").GetValue(0);
            Assert.That(Get<ulong>(amount, "Quantity"), Is.EqualTo((ulong)uint.MaxValue + 17));
            Set(amount, "Quantity", 1UL); Assert.That(Decode(bytes, out var independent), Is.True);
            var other = Get<Array>(Get<object>(independent, "Ground"), "Items").GetValue(0);
            Assert.That(Get<ulong>(Get<Array>(Get<object>(other, "ContainerState"), "Components").GetValue(0), "Quantity"), Is.EqualTo((ulong)uint.MaxValue + 17));
            Assert.That(Decode(Encode(Snapshot()), out var isolated), Is.True); Assert.That(Get<object>(isolated, "Ground"), Is.Null);
            bytes[1] = 6; Assert.That(Decode(bytes, out _), Is.False, "v6不能忽略共享地面语义后混用旧产物。");
        }

        [TestCase(0)] [TestCase(16384)]
        public void GroundRecordBudgetAllowsEmptyKnownWorldAndFullBoundedCount(int count)
        {
            Assert.That(Decode(EncodeExact(GroundSnapshot(count)), out var result), Is.True);
            Assert.That(Get<Array>(Get<object>(result, "Ground"), "Items").Length, Is.EqualTo(count));
            Assert.That(_codecType.GetField("MaxGroundItems").GetRawConstantValue(), Is.EqualTo(16384));
        }

        [TestCase("version")] [TestCase("null-items")] [TestCase("count")] [TestCase("duplicate")]
        [TestCase("owned-duplicate")] [TestCase("id")] [TestCase("owner")] [TestCase("content")]
        [TestCase("revision")] [TestCase("position")] [TestCase("rotation-zero")] [TestCase("rotation-size")]
        [TestCase("state-missing")] [TestCase("quantity-overflow")]
        public void InvalidGroundIdentityPoseStateOrBudgetFailsWithoutPublishing(string invalid)
        {
            var source = GroundSnapshot(2); var ground = Get<object>(source, "Ground"); var items = Get<Array>(ground, "Items"); var first = items.GetValue(0);
            if (invalid == "version") Set(ground, "Version", 0u);
            else if (invalid == "null-items") Set(ground, "Items", null);
            else if (invalid == "count") Set(ground, "Items", Array.CreateInstance(CoreType("OnlineGroundItemSnapshot"), 16385));
            else if (invalid == "duplicate") Set(items.GetValue(1), "InstanceId", Id(200000));
            else if (invalid == "owned-duplicate") Set(first, "InstanceId", Id(4));
            else if (invalid == "id") Set(first, "InstanceId", new string('g', 32));
            else if (invalid == "owner") Set(first, "WorldOwnerId", "");
            else if (invalid == "content") Set(first, "ContentId", new string('a', 129));
            else if (invalid == "revision") Set(first, "Revision", 0u);
            else if (invalid == "position") Set(first, "Position", new Vector3(float.NaN, 0, 0));
            else if (invalid == "rotation-zero") Set(first, "Rotation", new Quaternion(0, 0, 0, 0));
            else if (invalid == "rotation-size") Set(first, "Rotation", new Quaternion(0, 0, 0, 2));
            else if (invalid == "state-missing") Set(first, "ContainerState", null);
            else Set(first, "ContainerState", Container(ulong.MaxValue, 1));
            var args = new object[] { source, new byte[] { 1 } };
            Assert.That((bool)ExactEncoder().Invoke(_codec, args), Is.False); Assert.That(args[1], Is.Null);
        }

        [Test] public void OwnerWorstTraitsAndFullGroundFitThirtyTwoMiBCombinedBudget()
        {
            var source = LargeSnapshot(4096); var items = Get<Array>(Get<object>(source, "Assets"), "Items");
            var traits = new string[32]; for (int i = 0; i < traits.Length; i++) traits[i] = new string('a', 128);
            for (int i = 3; i < items.Length; i++) Set(items.GetValue(i), "WandStats", Stats(traits));
            SetSyntheticCarriedWandResources(source);
            var ground = Get<object>(GroundSnapshot(16384), "Ground"); var groundItems = Get<Array>(ground, "Items");
            for (int i = 0; i < groundItems.Length; i++)
            { Set(groundItems.GetValue(i), "ContentId", new string('a', 128)); Set(groundItems.GetValue(i), "ContainerState", Container(1, 2, 3, 4, 5, 6, 7, 8)); SynchronizeGroundRoot(groundItems.GetValue(i)); }
            Set(source, "Ground", ground); byte[] bytes = EncodeExact(source);
            Assert.That(bytes.Length, Is.GreaterThan(20 * 1024 * 1024)); Assert.That(bytes.Length, Is.LessThanOrEqualTo(32 * 1024 * 1024));
            Assert.That(Decode(bytes, out _), Is.True);
        }

        [TestCase("ground-unknown")] [TestCase("ground-duplicate")] [TestCase("item-unknown")]
        [TestCase("item-duplicate")] [TestCase("count")] [TestCase("wrong-type")]
        [TestCase("nan-position")] [TestCase("nan-rotation")] [TestCase("zero-rotation")]
        public void GroundWireRejectsUnknownDuplicateOversizedAndWrongTypeBeforePublication(string invalid)
        {
            var bytes = EncodeExact(GroundSnapshot(1)); var ground = Payload(bytes, 13); var item = Payload(ground, 2);
            if (invalid == "ground-unknown") ground = Append(ground, Field(3, 1));
            else if (invalid == "ground-duplicate") ground = Append(ground, Field(1, 3));
            else if (invalid == "count")
            {
                using var stream = new MemoryStream(); var header = Field(1, 3); stream.Write(header, 0, header.Length);
                for (int i = 0; i < 16385; i++) { stream.WriteByte(18); stream.WriteByte(0); }
                ground = stream.ToArray();
            }
            else if (invalid == "item-unknown") ground = ReplacePayload(ground, 2, Append(item, Field(13, 1)));
            else if (invalid == "item-duplicate") ground = ReplacePayload(ground, 2, Append(item, Field(4, 1)));
            else if (invalid == "nan-position") ground = ReplacePayload(ground, 2, ReplaceWireField(item, 5, FloatField(5, float.NaN)));
            else if (invalid == "nan-rotation") ground = ReplacePayload(ground, 2, Append(item, FloatField(8, float.NaN)));
            else if (invalid == "zero-rotation") ground = ReplacePayload(ground, 2, ReplaceWireField(item, 11, FloatField(11, 0)));
            else ground = ReplacePayload(ground, 2, ReplaceWireField(item, 5, Field(5, 1)));
            Assert.That(Decode(ReplacePayload(bytes, 13, ground), out var result), Is.False); Assert.That(result, Is.Null);
        }

        private static object GroundSnapshot(int count)
        {
            var source = Snapshot(); var ground = Core("OnlineGroundInventorySnapshot"); Set(ground, "Version", 3u);
            var items = Array.CreateInstance(CoreType("OnlineGroundItemSnapshot"), count);
            for (int i = 0; i < count; i++)
            {
                var item = Core("OnlineGroundItemSnapshot"); Set(item, "InstanceId", Id(200000 + i)); Set(item, "ContentId", "container.ground");
                Set(item, "WorldOwnerId", Id(300000 + i)); Set(item, "Revision", 1u); Set(item, "Position", new Vector3(1, 2, 3));
                Set(item, "Rotation", Quaternion.identity); Set(item, "ContainerState", Container((ulong)uint.MaxValue + 17, 23)); SynchronizeGroundRoot(item); items.SetValue(item, i);
            }
            Set(ground, "Items", items); Set(source, "Ground", ground); return source;
        }

        private static void SynchronizeGroundRoot(object ground)
        {
            // 旧瓶/固体夹具迁移到显式完整根，复制的都是真实既有值；不根据模板补状态或数量。
            var item = Core("OnlineOwnedItemSnapshot"); Set(item, "InstanceId", Get<string>(ground, "InstanceId"));
            Set(item, "ContentId", Get<string>(ground, "ContentId")); Set(item, "Revision", Get<uint>(ground, "Revision"));
            Set(item, "LocationKind", 5); Set(item, "ParentInstanceId", ""); Set(item, "Slot", -1);
            Set(item, "ContainerState", Get<object>(ground, "ContainerState")); Set(item, "MaterialState", Get<object>(ground, "MaterialState"));
            Set(ground, "Item", item); Set(ground, "Children", Array.CreateInstance(CoreType("OnlineOwnedItemSnapshot"), 0));
        }

        private static object MixedSnapshot()
        {
            var source = Snapshot(); var assets = Get<object>(source, "Assets"); var original = Get<Array>(assets, "Items");
            var items = Array.CreateInstance(CoreType("OnlineOwnedItemSnapshot"), 10); Array.Copy(original, items, original.Length);
            items.SetValue(Item(10000, 4, "", 0), 2);
            var bottle = Item(10001, 1, Id(10000), -1, 2, 3); Set(bottle, "QuarterTurns", 1);
            Set(bottle, "ContainerState", Container((ulong)uint.MaxValue + 17, 23)); items.SetValue(bottle, 3);
            var empty = Item(10002, 1, Id(10000), -1, 4, 3); Set(empty, "ContainerState", Container()); items.SetValue(empty, 4);
            var spare = Item(10003, 1, Id(10000), -1, 5, 3); Set(spare, "UseTemplateWandStats", true); items.SetValue(spare, 5);
            items.SetValue(Item(10004, 2, Id(10003), 0), 6); items.SetValue(Item(10005, 8, "", 6), 7);
            items.SetValue(Item(10006, 1, Id(10000), -1, 6, 3), 8); items.SetValue(Item(10007, 1, Id(10000), -1, 7, 3), 9);
            Set(assets, "Items", items);
            var uses = Array.CreateInstance(CoreType("OnlineInventorySpellUsePresentationSnapshot"), 3);
            uses.SetValue(Get<Array>(source, "SpellUses").GetValue(0), 0);
            for (int i = 1; i < 3; i++) { var use = Core("OnlineInventorySpellUsePresentationSnapshot"); Set(use, "InstanceId", Id(i == 1 ? 10004 : 10006)); uses.SetValue(use, i); }
            Set(source, "SpellUses", uses);
            var wands = Array.CreateInstance(CoreType("OnlineInventoryWandPresentationSnapshot"), 2);
            wands.SetValue(Get<Array>(source, "Wands").GetValue(0), 0);
            var spareState = Core("OnlineInventoryWandPresentationSnapshot"); Set(spareState, "InstanceId", Id(10003));
            Set(spareState, "ProgramRevision", 37u); Set(spareState, "CurrentMana", 19.5f); Set(spareState, "CooldownRemaining", 1.75f);
            wands.SetValue(spareState, 1); Set(source, "Wands", wands); return source;
        }

        private static void SetSyntheticCarriedWandResources(object source)
        {
            // 最大Traits样本有意构造数千根真实备用杖；每根的权威读数也显式给出。
            // 这些固定测试值独立于模板MaxMana，不把缺资源合同默认为满Mana或Revision1。
            var items = Get<Array>(Get<object>(source, "Assets"), "Items");
            var states = Array.CreateInstance(CoreType("OnlineInventoryWandPresentationSnapshot"), items.Length - 2);
            int index = 0;
            for (int i = 0; i < items.Length; i++)
            {
                var item = items.GetValue(i);
                if (!Get<bool>(item, "UseTemplateWandStats") && Get<object>(item, "WandStats") == null) continue;
                var state = Core("OnlineInventoryWandPresentationSnapshot"); Set(state, "InstanceId", Get<string>(item, "InstanceId"));
                Set(state, "ProgramRevision", 37u); Set(state, "CurrentMana", 19.5f); Set(state, "CooldownRemaining", 1.75f);
                states.SetValue(state, index++);
            }
            Assert.That(index, Is.EqualTo(items.Length - 2), "本最大样本仅1张卡与1个包不是法杖。");
            Set(source, "Wands", states);
        }

        private static object LargeSnapshot(int count)
        {
            var source = Snapshot(); var assets = Get<object>(source, "Assets"); var original = Get<Array>(assets, "Items");
            var items = Array.CreateInstance(CoreType("OnlineOwnedItemSnapshot"), count); Array.Copy(original, items, original.Length);
            items.SetValue(Item(10000, 4, "", 0), 2);
            // Wire不拥有目录的网格尺寸；大样本使用独立非负坐标，真实shape/capacity仍须Run复核。
            for (int i = 3; i < count; i++) items.SetValue(Item(10000 + i, 1, Id(10000), -1, i, 0), i);
            Set(assets, "Items", items); return source;
        }

        private static object Item(int id, int kind, string parent, int slot, int x = 0, int y = 0)
        {
            var item = Core("OnlineOwnedItemSnapshot"); Set(item, "InstanceId", Id(id)); Set(item, "ContentId", "item.from.catalog");
            Set(item, "Revision", 1u); Set(item, "LocationKind", kind); Set(item, "ParentInstanceId", parent); Set(item, "Slot", slot);
            Set(item, "X", x); Set(item, "Y", y); return item;
        }

        private static object Container(params ulong[] quantities)
        {
            var state = Core("OnlineOwnedContainerStateSnapshot"); Set(state, "SchemaVersion", 1u);
            var components = Array.CreateInstance(CoreType("OnlineOwnedMaterialAmountSnapshot"), quantities.Length);
            for (int i = 0; i < quantities.Length; i++) { var amount = Core("OnlineOwnedMaterialAmountSnapshot"); Set(amount, "MaterialKey", (byte)(i + 1)); Set(amount, "Quantity", quantities[i]); components.SetValue(amount, i); }
            Set(state, "Components", components); return state;
        }

        private static object Stats(string[] alwaysCast)
        {
            var stats = Core("OnlineOwnedWandStatsSnapshot"); Set(stats, "MaxMana", 80f); Set(stats, "ManaRegenPerSecond", 4f);
            Set(stats, "BaseCastDelay", .2f); Set(stats, "BaseReloadTime", .4f); Set(stats, "BaseSpreadDegrees", 3f);
            Set(stats, "ProgramCapacity", 32); Set(stats, "BaseDraws", 1); Set(stats, "AlwaysCastContentIds", alwaysCast); return stats;
        }

        private byte[] EncodeLarge(object source) { Assert.That(TryEncodeLarge(source, out var bytes), Is.True); return bytes; }
        // 常规4096记录远小于1MiB；最大Traits样本单独走精确分配，测试自身也不反复申请20MiB。
        private bool TryEncodeLarge(object source, out byte[] bytes) => TryEncodeInto(source, 1024 * 1024, out bytes);
        private byte[] EncodeExact(object source)
        { var args = new object[] { source, null }; Assert.That((bool)ExactEncoder().Invoke(_codec, args), Is.True); return (byte[])args[1]; }
        private MethodInfo SegmentEncoder() => _codecType.GetMethod("TryEncode", new[] { CoreType("OnlineInventoryPresentationSnapshot"), typeof(ArraySegment<byte>), typeof(int).MakeByRefType() });
        private MethodInfo ExactEncoder()
        {
            var method = _codecType.GetMethod("TryEncode", new[] { CoreType("OnlineInventoryPresentationSnapshot"), typeof(byte[]).MakeByRefType() });
            Assert.That(method, Is.Not.Null, "有界Owner发送队列需要按CalculateSize精确分配的冷路径，不能逐连接持有20MiB最大缓冲。"); return method;
        }

        private static object Snapshot(int wandCount = 1, int cardsPerWand = 1)
        {
            var source = Core("OnlineInventoryPresentationSnapshot"); var assets = Core("OnlineOwnedAssetSnapshot");
            Set(source, "SchemaVersion", 1u); Set(source, "ConnectionEpoch", 3u); Set(source, "SnapshotSequence", 9UL);
            Set(source, "ResourceVersion", 2u); Set(source, "SelectionVersion", 1u); Set(source, "SpellUseVersion", 1u);
            Set(source, "ServerTick", 50u); Set(source, "Assets", assets);
            Set(assets, "SchemaVersion", assets.GetType().GetField("CurrentSchemaVersion").GetRawConstantValue()); Set(assets, "RunId", Id(1)); Set(assets, "PlayerId", Id(2));
            Set(assets, "InventoryId", Id(1) + "/" + Id(2)); Set(assets, "GameplayConfigHash", Hash);
            Set(assets, "SelectedWandInstanceId", Id(4)); Set(assets, "ActorId", 1UL); Set(assets, "InventoryVersion", 7u);
            // 合法夹具显式声明已知0余额和修订；生产不能为旧包补默认权限。
            Set(assets, "Gold", 0L); Set(assets, "CurrencyVersion", 1u);
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
            return TryEncodeInto(value, 65536, out bytes);
        }
        private bool TryEncodeInto(object value, int capacity, out byte[] bytes)
        {
            var buffer = new byte[capacity]; var args = new object[] { value, new ArraySegment<byte>(buffer), 0 };
            bool accepted = (bool)SegmentEncoder().Invoke(_codec, args);
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
        private static FieldInfo SelectedToolField(object value)
        {
            var field = value.GetType().GetField("SelectedToolSlotPlusOne");
            Assert.That(field, Is.Not.Null, "Core完整快照需要中立SelectedToolSlotPlusOne字段，默认0保持原选杖语义。");
            return field;
        }
        private static void SetSelectedTool(object value, uint slotPlusOne) => SelectedToolField(value).SetValue(value, slotPlusOne);
        private static uint SelectedTool(object value) => (uint)SelectedToolField(value).GetValue(value);

        private static byte[] Field(uint number, ulong value)
        { using var stream = new MemoryStream(); Varint(stream, number << 3); Varint(stream, value); return stream.ToArray(); }
        private static byte[] Append(byte[] first, byte[] second)
        { var bytes = new byte[first.Length + second.Length]; Array.Copy(first, bytes, first.Length); Array.Copy(second, 0, bytes, first.Length, second.Length); return bytes; }
        private static byte[] Payload(byte[] bytes, int number, int occurrence = 0)
        { Locate(bytes, number, out _, out int start, out int end, occurrence); var result = new byte[end - start]; Array.Copy(bytes, start, result, 0, result.Length); return result; }
        private static byte[] ReplacePayload(byte[] bytes, int number, byte[] replacement, int occurrence = 0)
        {
            Locate(bytes, number, out int tag, out _, out int end, occurrence); using var stream = new MemoryStream(); stream.Write(bytes, 0, tag);
            Varint(stream, (uint)(number << 3 | 2)); Varint(stream, (ulong)replacement.Length); stream.Write(replacement, 0, replacement.Length);
            stream.Write(bytes, end, bytes.Length - end); return stream.ToArray();
        }
        private static byte[] LengthField(int number, byte[] payload)
        { using var stream = new MemoryStream(); Varint(stream, (ulong)(number << 3 | 2)); Varint(stream, (ulong)payload.Length); stream.Write(payload, 0, payload.Length); return stream.ToArray(); }
        private static byte[] FloatField(int number, float value)
        {
            using var stream = new MemoryStream(); Varint(stream, (ulong)(number << 3 | 5));
            uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
            for (int shift = 0; shift < 32; shift += 8) stream.WriteByte((byte)(bits >> shift)); return stream.ToArray();
        }
        private static byte[] ReplaceWireField(byte[] bytes, int number, byte[] replacement)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int start = offset; ulong tag = Read(bytes, ref offset); int wire = (int)(tag & 7);
                if (wire == 0) Read(bytes, ref offset);
                else if (wire == 2) { int length = (int)Read(bytes, ref offset); offset += length; }
                else offset += wire == 1 ? 8 : 4;
                if ((int)(tag >> 3) != number) continue;
                using var stream = new MemoryStream(); stream.Write(bytes, 0, start); stream.Write(replacement, 0, replacement.Length);
                stream.Write(bytes, offset, bytes.Length - offset); return stream.ToArray();
            }
            throw new InvalidOperationException("Fixture字段不存在：" + number);
        }
        private static byte[] ReplaceScalar(byte[] bytes, int number, ulong value)
        {
            int offset = 0; while (offset < bytes.Length) { int start = offset; ulong tag = Read(bytes, ref offset); int wire = (int)(tag & 7);
                if (wire == 0) { Read(bytes, ref offset); if ((int)(tag >> 3) == number) { var tail = new byte[bytes.Length - offset]; Array.Copy(bytes, offset, tail, 0, tail.Length); var head = new byte[start]; Array.Copy(bytes, head, start); return Append(Append(head, Field((uint)number, value)), tail); } }
                else if (wire == 2) { int length = (int)Read(bytes, ref offset); offset += length; } else offset += wire == 1 ? 8 : 4; }
            throw new InvalidOperationException("Fixture scalar不存在：" + number);
        }
        private static void Locate(byte[] bytes, int number, out int tagStart, out int payloadStart, out int end, int occurrence = 0)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int start = offset; ulong tag = Read(bytes, ref offset); int wire = (int)(tag & 7);
                if (wire == 2) { int length = (int)Read(bytes, ref offset); if ((int)(tag >> 3) == number && occurrence-- == 0) { tagStart = start; payloadStart = offset; end = offset + length; return; } offset += length; }
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
