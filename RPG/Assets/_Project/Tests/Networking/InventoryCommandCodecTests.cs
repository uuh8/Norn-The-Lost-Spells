using System;
using System.IO;
using System.Reflection;
using System.Text;
using Game.NetworkProtocol;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.Networking.Tests
{
    /// <summary>授权库存命令仅携带意图；身份来自连接，本组不创建权威工作区或发送网络请求。</summary>
    public sealed class InventoryCommandCodecTests
    {
        private object _codec;
        private Type _type;
        private const ulong Hash = 12345678;

        [TestCase(16)] [TestCase(17)]
        public void PeacefulResearchSessionCommandsRoundTripWithoutInventingAnItem(int kind)
        {
            var value = Intent(kind); Set(value, "ItemId", ""); Set(value, "TargetSlot", 0);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var restored), Is.True);
            Assert.That(Get<string>(restored, "ItemId"), Is.Empty);
            Assert.That(Convert.ToInt32(Get<object>(restored, "Kind")), Is.EqualTo(kind));
        }

        [TestCase("ItemId")] [TestCase("TargetLocationKind")] [TestCase("TargetSlot")]
        [TestCase("SourceWandId")] [TestCase("TransferBudget")] [TestCase("AimOrigin")]
        public void PeacefulResearchSessionCommandsRejectAnySmuggledItemOrWorldPayload(string field)
        {
            var value = Intent(16); Set(value, "ItemId", ""); Set(value, "TargetSlot", 0);
            var bytes = Encode(value, "Intent");
            object dirty = field == "ItemId" || field == "SourceWandId" ? Id(1)
                : field == "TransferBudget" ? (object)1UL : field == "AimOrigin" ? Vector3.right : (object)1;
            Set(value, field, dirty);
            Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            var wire = InventoryCommandMessage.Parser.ParseFrom(bytes);
            SetWireProperty(wire, field == "AimOrigin" ? "AimOriginX" : field, field == "AimOrigin" ? 1f : dirty);
            Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False);
        }

        [TestCase(1)] [TestCase(2)] [TestCase(7)] [TestCase(8)]
        public void MoveOwnedRoundTripsExplicitDestinationWithoutLegacyStorageFlag(int location)
        {
            var value = GeneralIntent(12, location);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var restored), Is.True);
            Assert.That(Get<int>(restored, "TargetLocationKind"), Is.EqualTo(location));
            Assert.That(Get<bool>(restored, "ToSpellStorage"), Is.False);
            Assert.That(Get<string>(restored, "ItemId"), Is.EqualTo(Id(5)));
        }

        [Test] public void GeneralEquipDropAndAutomaticPickupRoundTripCanonicalIntent()
        {
            foreach (int kind in new[] { 13, 14, 15 })
            {
                var value = GeneralIntent(kind, kind == 13 ? 3 : 0);
                Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var restored), Is.True, kind.ToString());
                Assert.That(Convert.ToInt32(Get<object>(restored, "Kind")), Is.EqualTo(kind));
                Assert.That(Get<bool>(restored, "WholeStack"), Is.EqualTo(kind == 14));
                Assert.That(Get<bool>(restored, "AutoPlace"), Is.EqualTo(kind == 15));
            }
        }

        [Test] public void GeneralCommandResetClearsDestinationWholeStackAndUnknownFields()
        {
            var wire = new InventoryCommandMessage();
            SetWireProperty(wire, "TargetLocationKind", 8); SetWireProperty(wire, "WholeStack", true);
            wire.MergeFrom(Scalar(32, 7)); wire.ResetForReuse();
            Assert.That(wire.CalculateSize(), Is.Zero);
        }

        [TestCase(12)] [TestCase(14)]
        public void GeneralMoveAndDropPreserveWholeStackAndInstalledCardSourceRevision(int kind)
        {
            var value = GeneralIntent(kind, kind == 12 ? 7 : 0); Set(value, "WholeStack", true);
            Set(value, "SourceWandId", Id(4)); Set(value, "SourceProgramRevision", 17u);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var restored), Is.True);
            Assert.That(Get<bool>(restored, "WholeStack"), Is.True);
            Assert.That(Get<string>(restored, "SourceWandId"), Is.EqualTo(Id(4)));
            Assert.That(Get<uint>(restored, "SourceProgramRevision"), Is.EqualTo(17));
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(7)] [TestCase(8)]
        public void GeneralPickupSupportsEveryManualOriginalInventoryDestination(int location)
        {
            var value = GeneralIntent(15, location); Set(value, "AutoPlace", false);
            if (location == 1) { Set(value, "TargetBackpackId", Id(800)); Set(value, "GridX", 2); }
            if (location == 2) { Set(value, "TargetWandId", Id(4)); Set(value, "TargetProgramRevision", 17u); }
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var restored), Is.True);
            Assert.That(Get<int>(restored, "TargetLocationKind"), Is.EqualTo(location));
            Assert.That(Get<bool>(restored, "AutoPlace"), Is.False);
        }

        [TestCase("storage-flag")] [TestCase("source-revision")] [TestCase("world")] [TestCase("auto")]
        [TestCase("attachment")] [TestCase("quantity")] [TestCase("aim")] [TestCase("kind")]
        [TestCase("destination")] [TestCase("target-wand")] [TestCase("target-slot")]
        public void GeneralMoveRejectsDirtyOrAmbiguousFieldsOnBothSides(string invalid)
        {
            var value = GeneralIntent(12, 1); string field; object dirty;
            if (invalid == "storage-flag") { field = "ToSpellStorage"; dirty = true; }
            else if (invalid == "source-revision") { field = "SourceProgramRevision"; dirty = 1u; }
            else if (invalid == "world") { field = "WorldOwnerId"; dirty = Id(700); }
            else if (invalid == "auto") { field = "AutoPlace"; dirty = true; }
            else if (invalid == "attachment") { field = "AttachmentEnabled"; dirty = true; }
            else if (invalid == "quantity") { field = "TransferBudget"; dirty = 1UL; }
            else if (invalid == "aim") { field = "AimOrigin"; dirty = Vector3.right; }
            else if (invalid == "kind") { field = "TargetLocationKind"; dirty = 5; }
            else if (invalid == "destination") { field = "TargetBackpackId"; dirty = ""; }
            else if (invalid == "target-wand") { field = "TargetWandId"; dirty = Id(4); }
            else { field = "TargetSlot"; dirty = 1; }
            byte[] valid = Encode(value, "Intent"); Set(value, field, dirty);
            Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False, invalid);
            var wire = InventoryCommandMessage.Parser.ParseFrom(valid);
            SetWireProperty(wire, field == "AimOrigin" ? "AimOriginX" : field, field == "AimOrigin" ? 1f : dirty);
            Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False, invalid);
        }

        [Test] public void GeneralCommandRawRejectsDuplicateUnknownBooleanAliasesAndKindNarrowing()
        {
            byte[] valid = Encode(GeneralIntent(12, 1), "Intent");
            foreach (byte[] invalid in new[] { Append(valid, Scalar(30, 1)), Append(valid, Scalar(31, 2)),
                Append(valid, Scalar(32, 1)), Append(Append(valid, Scalar(31, 0)), Scalar(31, 0)),
                ReplaceVarint(valid, 30, Number(257)), ReplaceVarint(valid, 6, Number(268)),
                ReplaceVarint(valid, 30, new byte[] { 129, 0 }) })
                Assert.That(Decode(invalid, "Intent", Hash, out _), Is.False);
            foreach (int kind in new[] { 13, 15 })
            {
                var value = GeneralIntent(kind, kind == 13 ? 3 : 0); Set(value, "WholeStack", true);
                Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            }
            foreach (string field in new[] { "TargetLocationKind", "WholeStack" })
            {
                var old = Intent(1); Set(old, field, field == "WholeStack" ? (object)true : 1);
                Assert.That(TryEncode(old, "Intent", Hash, out _, out _), Is.False, "旧命令不能夹带新字段：" + field);
            }
        }

        private static object GeneralIntent(int kind, int location)
        {
            var value = Intent(kind); Set(value, "TargetSlot", 0);
            foreach (string field in new[] { "TargetLocationKind", "WholeStack" })
                Assert.That(value.GetType().GetField(field), Is.Not.Null, "通用库存意图缺少中立字段：" + field);
            Set(value, "TargetLocationKind", location); Set(value, "WholeStack", kind == 14);
            if (kind == 15) { Set(value, "WorldOwnerId", Id(700)); Set(value, "AutoPlace", true); }
            else if (location == 1) { Set(value, "TargetBackpackId", Id(800)); Set(value, "GridX", 2); }
            else if (location == 2) { Set(value, "TargetWandId", Id(4)); Set(value, "TargetProgramRevision", 2u); }
            return value;
        }

        [Test] public void ConfigureSpellAttachmentRoundTripsFullDoseAndCanonicalClearIntent()
        {
            foreach (bool enabled in new[] { true, false })
            {
                var source = AttachmentIntent(enabled); byte[] bytes = Encode(source, "Intent");
                Assert.That(Decode(bytes, "Intent", Hash, out var restored), Is.True);
                Assert.That(Convert.ToInt32(Get<object>(restored, "Kind")), Is.EqualTo(11));
                Assert.That(Get<string>(restored, "AttachmentContainerId"), Is.EqualTo(enabled ? Id(900000) : ""));
                Assert.That(Get<ulong>(restored, "AttachmentDose"), Is.EqualTo(enabled ? ulong.MaxValue : 0UL));
                Assert.That(Get<bool>(restored, "AttachmentEnabled"), Is.EqualTo(enabled));
                Assert.That(Get<int>(restored, "TargetSlot"), Is.Zero);
            }
        }

        [Test] public void ConfigureSpellAttachmentRejectsDirtyConfigOtherCommandFieldsAndMalformedWire()
        {
            // 正向控制先证明Codec接纳完整配置；不能仅靠拒绝所有Kind11让一组负例假绿。
            byte[] valid = Encode(AttachmentIntent(true), "Intent");
            foreach (string dirty in new[] { "AttachmentContainerId", "AttachmentDose", "AttachmentEnabled", "TargetSlot", "TransferBudget", "AimOrigin",
                "SourceWandId", "TargetWandId", "SourceProgramRevision", "TargetProgramRevision", "ToSpellStorage", "WorldOwnerId", "TargetBackpackId", "GridX", "GridY", "QuarterTurns", "AutoPlace" })
            {
                var source = AttachmentIntent(true);
                object extra = dirty == "AttachmentContainerId" ? "" : dirty == "AttachmentDose" ? (object)0UL : dirty == "AttachmentEnabled" ? false
                    : dirty == "TransferBudget" ? (object)1UL : dirty == "AimOrigin" ? Vector3.right
                    : dirty.EndsWith("Id", StringComparison.Ordinal) ? Id(6) : dirty.EndsWith("Revision", StringComparison.Ordinal) ? (object)1u
                    : dirty == "ToSpellStorage" || dirty == "AutoPlace" ? true : (object)1;
                Set(source, dirty, extra); Assert.That(TryEncode(source, "Intent", Hash, out _, out _), Is.False, dirty);
            }
            foreach (string field in new[] { "AttachmentContainerId", "AttachmentDose", "AttachmentEnabled" })
            {
                var source = Intent(1); object extra = field == "AttachmentContainerId" ? (object)Id(900000) : field == "AttachmentDose" ? (object)1UL : true;
                Set(source, field, extra); Assert.That(TryEncode(source, "Intent", Hash, out _, out _), Is.False, field);
                var wire = InventoryCommandMessage.Parser.ParseFrom(Encode(Intent(1), "Intent"));
                wire.GetType().GetProperty(field).SetValue(wire, extra); Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False, field);
            }
            foreach (byte[] malformed in new[] { Append(valid, Scalar(28, 1)), Append(valid, Scalar(30, 1)),
                Append(Encode(AttachmentIntent(false), "Intent"), Scalar(29, 2)),
                ReplaceVarint(valid, 28, new byte[] { 129, 0 }), ReplaceVarint(valid, 28, new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 2 }),
                ReplaceVarint(valid, 6, Number(267)) })
                Assert.That(Decode(malformed, "Intent", Hash, out _), Is.False);
            var pooled = InventoryCommandMessage.Parser.ParseFrom(valid); pooled.ResetForReuse();
            Assert.That(pooled.CalculateSize(), Is.Zero, "池化命令不能留下上条卡的来源、剂量或启用位。");
        }

        private static object AttachmentIntent(bool enabled)
        {
            var value = Intent(11); Set(value, "TargetSlot", 0);
            foreach (string field in new[] { "AttachmentContainerId", "AttachmentDose", "AttachmentEnabled" })
                Assert.That(value.GetType().GetField(field), Is.Not.Null, "供料意图必须使用中立Core值字段：" + field);
            Set(value, "AttachmentContainerId", enabled ? Id(900000) : "");
            Set(value, "AttachmentDose", enabled ? ulong.MaxValue : 0UL); Set(value, "AttachmentEnabled", enabled); return value;
        }

        [Test] public void ReleaseSolidRoundTripsOnlyCanonicalIdentityWithoutClientQuantityOrGeometry()
        {
            var value = Intent(10); Set(value, "TargetSlot", 0);
            Assert.That(TryEncode(value, "Intent", Hash, out var bytes, out int written), Is.True,
                "K只请求真实包内Material实例；DS选第一份材料并按原落点每次最多100GMU。");
            Assert.That(written, Is.GreaterThan(0)); Assert.That(Decode(bytes, "Intent", Hash, out var restored), Is.True);
            Assert.That(Convert.ToInt32(Get<object>(restored, "Kind")), Is.EqualTo(10));
            Assert.That(Get<string>(restored, "ItemId"), Is.EqualTo(Id(5)));
            foreach (string dirty in new[] { "TransferBudget", "AimOrigin", "AimDirection", "TargetSlot", "SourceWandId", "TargetWandId",
                "SourceProgramRevision", "TargetProgramRevision", "ToSpellStorage", "WorldOwnerId", "TargetBackpackId", "GridX", "GridY", "QuarterTurns", "AutoPlace" })
            {
                value = Intent(10); Set(value, "TargetSlot", 0);
                object extra = dirty == "TransferBudget" ? (object)1UL : dirty.StartsWith("Aim", StringComparison.Ordinal) ? Vector3.right
                    : dirty.EndsWith("Id", StringComparison.Ordinal) ? Id(6) : dirty.EndsWith("Revision", StringComparison.Ordinal) ? (object)1u
                    : dirty == "ToSpellStorage" || dirty == "AutoPlace" ? true : (object)1;
                Set(value, dirty, extra);
                Assert.That(TryEncode(value, "Intent", Hash, out _, out int rejectedLength), Is.False, dirty);
                Assert.That(rejectedLength, Is.Zero);
                var wire = InventoryCommandMessage.Parser.ParseFrom(bytes);
                if (dirty.StartsWith("Aim", StringComparison.Ordinal)) wire.GetType().GetProperty(dirty + "X").SetValue(wire, 1f);
                else wire.GetType().GetProperty(dirty).SetValue(wire, extra);
                Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False, dirty);
            }
            Assert.That(Decode(ReplaceVarint(bytes, 6, Number(266)), "Intent", Hash, out _), Is.False,
                "Kind原uint=266不得截成Core byte的ReleaseSolid=10。");
        }

        [Test]
        public void ThrowContainerRoundTripsAnAimIntentWithoutClientVelocityOrQuantity()
        {
            var value = Intent(9); Set(value, "TargetSlot", 0);
            Set(value, "AimOrigin", new Vector3(1, 2, 3)); Set(value, "AimDirection", Vector3.forward);
            Assert.That(TryEncode(value, "Intent", Hash, out var bytes, out int written), Is.True,
                "G复用已绑定瓶和瞄准意图，真实速度/在途归属由DS计算。");
            Assert.That(written, Is.GreaterThan(0));
            Assert.That(Decode(bytes, "Intent", Hash, out var decoded), Is.True);
            Assert.That(Convert.ToInt32(Get<object>(decoded, "Kind")), Is.EqualTo(9));
            Assert.That(Get<ulong>(decoded, "TransferBudget"), Is.Zero);
            Set(value, "TransferBudget", 1UL);
            Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
        }

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

        [TestCase(0)] [TestCase(6)]
        public void SelectBoundToolRoundTripsRealInstanceAndTargetToolSlot(int slot)
        {
            var value = Intent(3); Set(value, "TargetSlot", slot);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Convert.ToInt32(Get<object>(result, "Kind")), Is.EqualTo(3));
            Assert.That(Get<ulong>(result, "CommandId"), Is.EqualTo(9)); Assert.That(Get<uint>(result, "ConnectionEpoch"), Is.EqualTo(3));
            Assert.That(Get<uint>(result, "ExpectedInventoryVersion"), Is.EqualTo(7));
            Assert.That(Get<string>(result, "ItemId"), Is.EqualTo(Id(5))); Assert.That(Get<int>(result, "TargetSlot"), Is.EqualTo(slot));
            Assert.That(Get<string>(result, "SourceWandId"), Is.Empty); Assert.That(Get<string>(result, "TargetWandId"), Is.Empty);
            Assert.That(Get<uint>(result, "SourceProgramRevision"), Is.Zero); Assert.That(Get<uint>(result, "TargetProgramRevision"), Is.Zero);
            Assert.That(Get<bool>(result, "ToSpellStorage"), Is.False);
        }

        [Test] public void ToolSelectionNullOptionalWandsCanonicalizeToEmpty()
        {
            var value = Intent(3); Set(value, "SourceWandId", null); Set(value, "TargetWandId", null);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Get<string>(result, "SourceWandId"), Is.Empty); Assert.That(Get<string>(result, "TargetWandId"), Is.Empty);
        }

        [TestCase("SourceWandId")] [TestCase("TargetWandId")]
        [TestCase("SourceProgramRevision")] [TestCase("TargetProgramRevision")] [TestCase("ToSpellStorage")]
        public void ToolSelectionCannotSmuggleWandProgramOrStorageFields(string field)
        {
            var value = Intent(3);
            object dirty = field.EndsWith("Id", StringComparison.Ordinal) ? (object)Id(4)
                : field == "ToSpellStorage" ? true : (object)1u;
            Set(value, field, dirty);
            Assert.That(TryEncode(value, "Intent", Hash, out var bytes, out int written), Is.False);
            Assert.That(bytes, Is.Null); Assert.That(written, Is.Zero);
            var wire = InventoryCommandMessage.Parser.ParseFrom(Encode(Intent(1), "Intent")); wire.Kind = 3;
            wire.GetType().GetProperty(field).SetValue(wire, dirty);
            Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False);
        }

        [TestCase(-1)] [TestCase(7)] [TestCase(255)] [TestCase(257)]
        public void ToolSelectionRejectsOutOfEquipmentSlotRangeOnBothSides(int slot)
        {
            var value = Intent(3); Set(value, "TargetSlot", slot);
            Assert.That(TryEncode(value, "Intent", Hash, out _, out int written), Is.False); Assert.That(written, Is.Zero);
            var wire = InventoryCommandMessage.Parser.ParseFrom(Encode(Intent(1), "Intent")); wire.Kind = 3; wire.TargetSlot = slot;
            Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False);
        }

        [Test] public void ToolSelectionRawWireDecodesWithoutTreatingKindAsWandAlias()
        {
            var bytes = ReplaceVarint(Encode(Intent(1), "Intent"), 6, new byte[] { 3 });
            Assert.That(Decode(bytes, "Intent", Hash, out var result), Is.True);
            Assert.That(Convert.ToInt32(Get<object>(result, "Kind")), Is.EqualTo(3));
        }

        [TestCase(0u)] [TestCase(9u)] [TestCase(255u)] [TestCase(257u)] [TestCase(259u)]
        public void UnknownRawCommandKindCannotNarrowIntoKnownByteEnum(uint kind)
        {
            var wire = InventoryCommandMessage.Parser.ParseFrom(Encode(Intent(1), "Intent")); wire.Kind = kind;
            Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False,
                "必须在uint转Core byte枚举之前排除未知值，259不能截断成SelectTool=3。");
        }

        [TestCase(0)] [TestCase(6)]
        public void BindCarriedContainerRoundTripsInstanceAndToolSlotWithoutSelectionOrTransferPayload(int slot)
        {
            var value = Intent(6); Set(value, "TargetSlot", slot);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Convert.ToInt32(Get<object>(result, "Kind")), Is.EqualTo(6));
            Assert.That(Get<string>(result, "ItemId"), Is.EqualTo(Id(5)));
            Assert.That(Get<int>(result, "TargetSlot"), Is.EqualTo(slot));
            Assert.That(Get<uint>(result, "ExpectedInventoryVersion"), Is.EqualTo(7));
            Assert.That(Get<string>(result, "SourceWandId"), Is.Empty); Assert.That(Get<string>(result, "TargetWandId"), Is.Empty);
            Assert.That(Get<uint>(result, "SourceProgramRevision"), Is.Zero); Assert.That(Get<uint>(result, "TargetProgramRevision"), Is.Zero);
            Assert.That(Get<bool>(result, "ToSpellStorage"), Is.False); Assert.That(Get<ulong>(result, "TransferBudget"), Is.Zero);
            Assert.That(Get<Vector3>(result, "AimOrigin"), Is.EqualTo(Vector3.zero));
            Assert.That(Get<Vector3>(result, "AimDirection"), Is.EqualTo(Vector3.zero));
        }

        [Test] public void BindToolRawWireAcceptsCanonicalZeroTransferFields()
        {
            var bytes = ReplaceVarint(Encode(Intent(3), "Intent"), 6, Number(6));
            Assert.That(Decode(bytes, "Intent", Hash, out var result), Is.True);
            Assert.That(Convert.ToInt32(Get<object>(result, "Kind")), Is.EqualTo(6));
            // 显式零与proto默认零同义；绑定不携带射线，但不能错误要求这些已知字段必须完全缺省。
            bytes = Append(bytes, Scalar(14, 0));
            for (int field = 15; field <= 20; field++) bytes = Append(bytes, FloatField(field, 0));
            Assert.That(Decode(bytes, "Intent", Hash, out result), Is.True);
            Assert.That(Get<ulong>(result, "TransferBudget"), Is.Zero);
            Assert.That(Get<Vector3>(result, "AimOrigin"), Is.EqualTo(Vector3.zero));
            Assert.That(Get<Vector3>(result, "AimDirection"), Is.EqualTo(Vector3.zero));
        }

        [Test] public void BindToolCanonicalizesNullOptionalWandIdsToEmpty()
        {
            var value = Intent(6); Set(value, "SourceWandId", null); Set(value, "TargetWandId", null);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Get<string>(result, "SourceWandId"), Is.Empty); Assert.That(Get<string>(result, "TargetWandId"), Is.Empty);
        }

        [TestCase(-1)] [TestCase(7)] [TestCase(255)] [TestCase(257)]
        public void BindToolRejectsSlotsOutsideSevenEquipmentSlots(int slot)
        {
            var value = Intent(6); Set(value, "TargetSlot", slot);
            Assert.That(TryEncode(value, "Intent", Hash, out var bytes, out int written), Is.False);
            Assert.That(bytes, Is.Null); Assert.That(written, Is.Zero);
            var wire = InventoryCommandMessage.Parser.ParseFrom(Encode(Intent(3), "Intent")); wire.Kind = 6; wire.TargetSlot = slot;
            Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False);
        }

        [TestCase("SourceWandId")] [TestCase("TargetWandId")] [TestCase("SourceProgramRevision")]
        [TestCase("TargetProgramRevision")] [TestCase("ToSpellStorage")] [TestCase("TransferBudget")]
        [TestCase("AimOrigin")] [TestCase("AimDirection")]
        public void BindToolCannotSmuggleWandStorageOrMaterialTransferFields(string field)
        {
            var value = Intent(6);
            object dirty = field.EndsWith("Id", StringComparison.Ordinal) ? (object)Id(4)
                : field.EndsWith("Revision", StringComparison.Ordinal) ? 1u : field == "ToSpellStorage" ? true
                : field == "TransferBudget" ? 1UL : (object)Vector3.forward;
            Set(value, field, dirty);
            Assert.That(TryEncode(value, "Intent", Hash, out var bytes, out int written), Is.False);
            Assert.That(bytes, Is.Null); Assert.That(written, Is.Zero);
            var wire = InventoryCommandMessage.Parser.ParseFrom(Encode(Intent(3), "Intent")); wire.Kind = 6;
            bool vector = field == "AimOrigin" || field == "AimDirection";
            SetWireProperty(wire, vector ? field + "X" : field, vector ? 1f : dirty);
            Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False);
        }

        [TestCase("AimOrigin")] [TestCase("AimDirection")]
        public void BindToolRequiresExactZeroVectorsRatherThanUnityApproximateEquality(string field)
        {
            var value = Intent(6); Set(value, field, new Vector3(1e-8f, 0, 0));
            Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            var wire = InventoryCommandMessage.Parser.ParseFrom(Encode(Intent(3), "Intent")); wire.Kind = 6;
            SetWireProperty(wire, field + "X", 1e-8f);
            Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False);
        }

        [Test] public void RawUint262CannotNarrowIntoBindToolByteEnum()
        {
            var wire = InventoryCommandMessage.Parser.ParseFrom(Encode(Intent(3), "Intent")); wire.Kind = 262;
            Assert.That(Decode(wire.ToByteArray(), "Intent", Hash, out _), Is.False,
                "必须在uint转Core byte之前拒绝262，不能截成合法BindTool=6。");
        }

        [TestCase(7, false)] [TestCase(8, true)] [TestCase(8, false)]
        public void GroundContainerIntentRoundTripsCanonicalFields(int kind, bool autoPlace)
        {
            var value = GroundIntent(kind, autoPlace);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Convert.ToInt32(Get<object>(result, "Kind")), Is.EqualTo(kind));
            Assert.That(Get<string>(result, "WorldOwnerId"), Is.EqualTo(kind == 8 ? Id(700) : ""));
            Assert.That(Get<string>(result, "TargetBackpackId"), Is.EqualTo(kind == 8 && !autoPlace ? Id(800) : ""));
            Assert.That(Get<bool>(result, "AutoPlace"), Is.EqualTo(kind == 8 && autoPlace));
            Assert.That(Get<int>(result, "GridX"), Is.EqualTo(kind == 8 && !autoPlace ? 3 : 0));
            Assert.That(Get<int>(result, "GridY"), Is.EqualTo(kind == 8 && !autoPlace ? 4 : 0));
            Assert.That(Get<int>(result, "QuarterTurns"), Is.EqualTo(kind == 8 && !autoPlace ? 1 : 0));
            Assert.That(Get<int>(result, "TargetSlot"), Is.Zero); Assert.That(Get<ulong>(result, "TransferBudget"), Is.Zero);
            Assert.That(Get<Vector3>(result, "AimOrigin"), Is.EqualTo(Vector3.zero)); Assert.That(Get<Vector3>(result, "AimDirection"), Is.EqualTo(Vector3.zero));
        }

        [Test] public void GroundRawWireAcceptsDropAutoPickupAndMaximumManualGrid()
        {
            var drop = ReplaceVarint(Encode(Intent(3), "Intent"), 6, Number(7)); drop = ReplaceVarint(drop, 12, Number(0));
            Assert.That(Decode(drop, "Intent", Hash, out _), Is.True);
            Assert.That(Decode(PickupWire(true), "Intent", Hash, out var result), Is.True); Assert.That(Get<bool>(result, "AutoPlace"), Is.True);
            Assert.That(Decode(PickupWire(false, 255, 255, 3), "Intent", Hash, out result), Is.True);
            Assert.That(Get<int>(result, "GridX"), Is.EqualTo(255)); Assert.That(Get<int>(result, "GridY"), Is.EqualTo(255));
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void NonPickupCommandsRejectNonNeutralGroundFields(int kind)
        {
            foreach (string field in new[] { "WorldOwnerId", "TargetBackpackId", "GridX", "GridY", "QuarterTurns", "AutoPlace" })
            {
                var value = kind == 4 || kind == 5 ? TransferIntent(kind, 1) : kind == 7 ? GroundIntent(7, false) : Intent(kind);
                Set(value, field, field.EndsWith("Id", StringComparison.Ordinal) ? (object)Id(700) : field == "AutoPlace" ? true : (object)1);
                Assert.That(TryEncode(value, "Intent", Hash, out var bytes, out int written), Is.False, field);
                Assert.That(bytes, Is.Null); Assert.That(written, Is.Zero);
            }
        }

        [TestCase(7)] [TestCase(8)]
        public void GroundCommandsRejectLegacyToolProgramAndTransferPayload(int kind)
        {
            foreach (string field in new[] { "TargetSlot", "SourceWandId", "TargetWandId", "SourceProgramRevision", "TargetProgramRevision", "ToSpellStorage", "TransferBudget", "AimOrigin", "AimDirection" })
            {
                var value = GroundIntent(kind, kind == 8);
                object dirty = field.EndsWith("Id", StringComparison.Ordinal) ? (object)Id(4)
                    : field.EndsWith("Revision", StringComparison.Ordinal) ? 1u : field == "ToSpellStorage" ? true
                    : field == "TransferBudget" ? 1UL : field.StartsWith("Aim", StringComparison.Ordinal) ? (object)Vector3.forward : 1;
                Set(value, field, dirty); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False, field);
            }
        }

        [Test] public void PickupRequiresWorldIdAndConsistentAutoOrManualPlacement()
        {
            foreach (string bad in new[] { "", new string('g', 32), new string('a', 33) })
            { var value = GroundIntent(8, true); Set(value, "WorldOwnerId", bad); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False); }
            foreach (string field in new[] { "TargetBackpackId", "GridX", "GridY", "QuarterTurns" })
            { var value = GroundIntent(8, true); Set(value, field, field == "TargetBackpackId" ? (object)Id(800) : 1); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False, field); }
            var missingBag = GroundIntent(8, false); Set(missingBag, "TargetBackpackId", ""); Assert.That(TryEncode(missingBag, "Intent", Hash, out _, out _), Is.False);
            foreach (string field in new[] { "GridX", "GridY", "QuarterTurns" })
            {
                var value = GroundIntent(8, false); Set(value, field, -1); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
                Set(value, field, field == "QuarterTurns" ? 4 : 256); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
            }
            Assert.That(Decode(PickupWire(false, 4294967297UL), "Intent", Hash, out _), Is.False, "GridX不得截断回合法1。");
        }

        [Test] public void GroundWireRejectsDuplicatesWrongTypesAndOverwideKindAliases()
        {
            var bytes = PickupWire(true);
            Assert.That(Decode(Append(bytes, TextField(21, Id(700))), "Intent", Hash, out _), Is.False);
            Assert.That(Decode(Append(bytes, Scalar(26, 1)), "Intent", Hash, out _), Is.False);
            Assert.That(Decode(Append(bytes, Scalar(27, 1)), "Intent", Hash, out _), Is.False);
            var drop = ReplaceVarint(Encode(Intent(3), "Intent"), 6, Number(7)); drop = ReplaceVarint(drop, 12, Number(0));
            Assert.That(Decode(Append(drop, FloatField(26, 0)), "Intent", Hash, out _), Is.False);
            Assert.That(Decode(Append(drop, Scalar(21, 0)), "Intent", Hash, out _), Is.False);
            foreach (ulong kind in new[] { 9UL, 263UL, 264UL }) Assert.That(Decode(ReplaceVarint(drop, 6, Number(kind)), "Intent", Hash, out _), Is.False);
        }

        [TestCase(4, 1)] [TestCase(4, 4096)] [TestCase(5, 1)] [TestCase(5, 1024)]
        public void ContainerTransferIntentRoundTripsExactBudgetAndAim(int kind, int budget)
        {
            var value = TransferIntent(kind, (ulong)budget); Set(value, "TargetSlot", 6);
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Convert.ToInt32(Get<object>(result, "Kind")), Is.EqualTo(kind));
            Assert.That(Get<ulong>(result, "TransferBudget"), Is.EqualTo((ulong)budget));
            Assert.That(Get<Vector3>(result, "AimOrigin"), Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(Get<Vector3>(result, "AimDirection"), Is.EqualTo(Vector3.forward));
            Assert.That(Get<int>(result, "TargetSlot"), Is.EqualTo(6));
            Assert.That(Get<string>(result, "ItemId"), Is.EqualTo(Id(5)));
            Assert.That(Get<string>(result, "SourceWandId"), Is.Empty); Assert.That(Get<string>(result, "TargetWandId"), Is.Empty);
            Assert.That(Get<uint>(result, "SourceProgramRevision"), Is.Zero); Assert.That(Get<uint>(result, "TargetProgramRevision"), Is.Zero);
            Assert.That(Get<bool>(result, "ToSpellStorage"), Is.False);
        }

        [TestCase(4)] [TestCase(5)]
        public void ContainerTransferRawWireIsAcceptedWithoutGeneratedFieldReference(int kind)
        {
            Assert.That(Decode(TransferWire(kind, 1, new Vector3(1, 2, 3), Vector3.forward), "Intent", Hash, out var result), Is.True);
            Assert.That(Get<ulong>(result, "TransferBudget"), Is.EqualTo(1UL));
            Assert.That(Get<Vector3>(result, "AimOrigin"), Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(Get<Vector3>(result, "AimDirection"), Is.EqualTo(Vector3.forward));
        }

        [TestCase(4, 0UL)] [TestCase(4, 4097UL)] [TestCase(5, 0UL)] [TestCase(5, 1025UL)]
        [TestCase(4, 4294967297UL)] [TestCase(5, ulong.MaxValue)]
        public void TransferBudgetCannotBeZeroExceedKindLimitOrNarrowToUint32(int kind, ulong budget)
        {
            Assert.That(TryEncode(TransferIntent(kind, budget), "Intent", Hash, out var bytes, out int written), Is.False);
            Assert.That(bytes, Is.Null); Assert.That(written, Is.Zero);
            Assert.That(Decode(TransferWire(kind, budget, Vector3.zero, Vector3.forward), "Intent", Hash, out _), Is.False);
        }

        [TestCase(4)] [TestCase(5)]
        public void ContainerTransferRejectsWandOrStoragePayloadAndInvalidToolSlot(int kind)
        {
            string[] fields = { "SourceWandId", "TargetWandId", "SourceProgramRevision", "TargetProgramRevision", "ToSpellStorage", "TargetSlot" };
            foreach (string field in fields)
            {
                var value = TransferIntent(kind, 1); object dirty = field.EndsWith("Id", StringComparison.Ordinal) ? (object)Id(4)
                    : field.EndsWith("Revision", StringComparison.Ordinal) ? 1u : field == "ToSpellStorage" ? true : (object)7;
                Set(value, field, dirty); Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False, field);
            }
            var negative = TransferIntent(kind, 1); Set(negative, "TargetSlot", -1);
            Assert.That(TryEncode(negative, "Intent", Hash, out _, out _), Is.False);
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void ExistingCommandsCannotSilentlyDropTransferBudgetOrAim(int kind)
        {
            foreach (string field in new[] { "TransferBudget", "AimOrigin", "AimDirection" })
            {
                var value = Intent(kind); Set(value, field, field == "TransferBudget" ? (object)1UL : Vector3.forward);
                Assert.That(TryEncode(value, "Intent", Hash, out var bytes, out int written), Is.False, field);
                Assert.That(bytes, Is.Null); Assert.That(written, Is.Zero);
            }
            byte[] baseline = Encode(Intent(kind), "Intent");
            Assert.That(Decode(Append(baseline, Scalar(14, 1)), "Intent", Hash, out _), Is.False);
            Assert.That(Decode(Append(baseline, FloatField(20, 1)), "Intent", Hash, out _), Is.False);
        }

        [TestCase(4)] [TestCase(5)]
        public void TransferAimRejectsNonFiniteOriginAndNonUnitOrNonFiniteDirection(int kind)
        {
            foreach (var invalid in new[] { new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0) })
            {
                var value = TransferIntent(kind, 1); Set(value, "AimOrigin", invalid);
                Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
                Assert.That(Decode(TransferWire(kind, 1, invalid, Vector3.forward), "Intent", Hash, out _), Is.False);
            }
            foreach (var invalid in new[] { Vector3.zero, Vector3.forward * .5f, Vector3.forward * 2,
                new Vector3(float.NaN, 0, 1), new Vector3(float.PositiveInfinity, 0, 1), new Vector3(float.MaxValue, 0, 0) })
            {
                var value = TransferIntent(kind, 1); Set(value, "AimDirection", invalid);
                Assert.That(TryEncode(value, "Intent", Hash, out _, out _), Is.False);
                Assert.That(Decode(TransferWire(kind, 1, Vector3.zero, invalid), "Intent", Hash, out _), Is.False);
            }
        }

        [TestCase(.995f)] [TestCase(1.004f)]
        public void TransferAimUsesFiniteApproximateUnitRuleWithoutNewWorldAnglePolicy(float z)
        {
            var value = TransferIntent(4, 1); var origin = new Vector3(float.MaxValue, -float.MaxValue, 0);
            Set(value, "AimOrigin", origin); Set(value, "AimDirection", new Vector3(0, 0, z));
            Assert.That(Decode(Encode(value, "Intent"), "Intent", Hash, out var result), Is.True);
            Assert.That(Get<Vector3>(result, "AimOrigin"), Is.EqualTo(origin));
            Assert.That(Get<Vector3>(result, "AimDirection"), Is.EqualTo(new Vector3(0, 0, z)));
        }

        [Test] public void TransferWireRejectsDuplicateFieldsWrongTypesTruncationAndNonMinimalUlong()
        {
            var bytes = TransferWire(4, 1, Vector3.zero, Vector3.forward);
            Assert.That(Decode(Append(bytes, Scalar(14, 1)), "Intent", Hash, out _), Is.False);
            Assert.That(Decode(Append(bytes, FloatField(20, 1)), "Intent", Hash, out _), Is.False);
            var baseIntent = ReplaceVarint(Encode(Intent(3), "Intent"), 6, Number(4));
            Assert.That(Decode(Append(baseIntent, FloatField(14, 1)), "Intent", Hash, out _), Is.False);
            var wrongAimType = Append(Append(Append(baseIntent, Scalar(14, 1)), Scalar(15, 1)), FloatField(20, 1));
            Assert.That(Decode(wrongAimType, "Intent", Hash, out _), Is.False);
            Assert.That(Decode(Append(bytes, Scalar(21, 1)), "Intent", Hash, out _), Is.False);
            Assert.That(Decode(ReplaceVarint(bytes, 14, new byte[] { 129, 0 }), "Intent", Hash, out _), Is.False);
            Assert.That(Decode(ReplaceVarint(bytes, 14, new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 2 }), "Intent", Hash, out _), Is.False);
            Array.Resize(ref bytes, bytes.Length - 1); Assert.That(Decode(bytes, "Intent", Hash, out _), Is.False);
        }

        [TestCase(9)] [TestCase(260)] [TestCase(261)]
        public void OverwideTransferKindDoesNotTruncateIntoCollectOrPour(int kind)
        { Assert.That(Decode(TransferWire(kind, 1, Vector3.zero, Vector3.forward), "Intent", Hash, out _), Is.False); }

        [TestCase(1UL)] [TestCase(4096UL)] [TestCase(4294967312UL)] [TestCase(ulong.MaxValue)]
        public void SuccessfulTransferReceiptRoundTripsUlongWithoutCodecNarrowing(ulong amount)
        {
            var value = TransferReceipt(0, amount, 0);
            Assert.That(Decode(Encode(value, "Receipt"), "Receipt", Hash, out var result), Is.True);
            Assert.That(Get<ulong>(result, "Transferred"), Is.EqualTo(amount));
            Assert.That(Convert.ToInt32(Get<object>(result, "TransferFailure")), Is.Zero);
            Assert.That(Decode(TransferReceiptWire(0, amount, 0), "Receipt", Hash, out result), Is.True);
            Assert.That(Get<ulong>(result, "Transferred"), Is.EqualTo(amount));
        }

        [Test] public void FailedTransferReceiptPreservesOriginalMaterialFailure()
        {
            var value = TransferReceipt(10, 0, 12);
            Assert.That(Decode(Encode(value, "Receipt"), "Receipt", Hash, out var result), Is.True);
            Assert.That(Get<ulong>(result, "Transferred"), Is.Zero);
            Assert.That(Convert.ToInt32(Get<object>(result, "TransferFailure")), Is.EqualTo(12));
            Assert.That(Decode(TransferReceiptWire(10, 0, 12), "Receipt", Hash, out result), Is.True);
            Assert.That(Convert.ToInt32(Get<object>(result, "TransferFailure")), Is.EqualTo(12));
        }

        [TestCase(10, 1UL, 0)] [TestCase(10, 1UL, 12)] [TestCase(0, 0UL, 1)] [TestCase(0, 1UL, 12)]
        [TestCase(10, 0UL, 13)] [TestCase(10, 0UL, 255)]
        public void TransferReceiptRejectsFailureWithQuantitySuccessWithFailureAndUnknownFailure(int failure, ulong amount, int transferFailure)
        {
            var value = TransferReceipt(failure, amount, transferFailure);
            Assert.That(TryEncode(value, "Receipt", Hash, out var bytes, out int written), Is.False);
            Assert.That(bytes, Is.Null); Assert.That(written, Is.Zero);
            Assert.That(Decode(TransferReceiptWire(failure, amount, (uint)transferFailure), "Receipt", Hash, out _), Is.False);
        }

        [Test] public void TransferReceiptWireRejectsOverwideFailureDuplicateAndWrongFieldTypes()
        {
            Assert.That(Decode(TransferReceiptWire(10, 0, 268), "Receipt", Hash, out _), Is.False,
                "268不能先截成byte的IncompatibleMaterial=12。");
            var bytes = TransferReceiptWire(10, 0, 12);
            Assert.That(Decode(Append(bytes, Scalar(8, 12)), "Receipt", Hash, out _), Is.False);
            Assert.That(Decode(Append(bytes, Scalar(7, 0)), "Receipt", Hash, out _), Is.False);
            Assert.That(Decode(Append(Encode(Receipt(0, 8), "Receipt"), FloatField(7, 1)), "Receipt", Hash, out _), Is.False);
            Assert.That(Decode(Append(bytes, Scalar(9, 1)), "Receipt", Hash, out _), Is.False);
        }

        [Test] public void PooledCommandAndReceiptClearAllTransferFields()
        {
            var command = new InventoryCommandMessage();
            foreach (string field in new[] { "TransferBudget", "AimOriginX", "AimOriginY", "AimOriginZ", "AimDirectionX", "AimDirectionY", "AimDirectionZ" })
                SetWireProperty(command, field, field == "TransferBudget" ? (object)7UL : 1f);
            ResetWireMessage(command); Assert.That(command.CalculateSize(), Is.Zero);
            var receipt = new InventoryCommandReceiptMessage(); SetWireProperty(receipt, "Transferred", 7UL); SetWireProperty(receipt, "TransferFailure", 12u);
            ResetWireMessage(receipt); Assert.That(receipt.CalculateSize(), Is.Zero);
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
            Assert.That(Decode(Append(bytes, Scalar(27, 1)), "Intent", Hash, out _), Is.False,
                "21..26已有地面意图合同；未知27字段仍不能夹带客户端自报身份。");
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
            Assert.That(Decode(Append(bytes, Scalar(9, 1)), "Receipt", Hash, out _), Is.False);
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
        private static object TransferIntent(int kind, ulong budget)
        {
            var value = Intent(kind); Set(value, "TransferBudget", budget); Set(value, "AimOrigin", new Vector3(1, 2, 3));
            Set(value, "AimDirection", Vector3.forward); return value;
        }
        private static object TransferReceipt(int failure, ulong amount, int transferFailure)
        { var value = Receipt(failure, 8); Set(value, "Transferred", amount); SetEnum(value, "TransferFailure", transferFailure); return value; }
        private byte[] TransferWire(int kind, ulong budget, Vector3 origin, Vector3 direction)
        {
            // 手工写尚未生成的字段，RED依赖真实Codec行为而不是缺成员的编译错误。
            var bytes = ReplaceVarint(Encode(Intent(3), "Intent"), 6, Number((ulong)kind));
            bytes = Append(bytes, Scalar(14, budget));
            float[] values = { origin.x, origin.y, origin.z, direction.x, direction.y, direction.z };
            for (int i = 0; i < values.Length; i++) bytes = Append(bytes, FloatField(15 + i, values[i]));
            return bytes;
        }
        private static object GroundIntent(int kind, bool autoPlace)
        {
            var value = Intent(kind); Set(value, "TargetSlot", 0);
            Set(value, "WorldOwnerId", kind == 8 ? Id(700) : ""); Set(value, "AutoPlace", kind == 8 && autoPlace);
            Set(value, "TargetBackpackId", kind == 8 && !autoPlace ? Id(800) : "");
            Set(value, "GridX", kind == 8 && !autoPlace ? 3 : 0); Set(value, "GridY", kind == 8 && !autoPlace ? 4 : 0);
            Set(value, "QuarterTurns", kind == 8 && !autoPlace ? 1 : 0); return value;
        }
        private byte[] PickupWire(bool autoPlace, ulong x = 0, ulong y = 0, ulong turns = 0)
        {
            var bytes = ReplaceVarint(Encode(Intent(3), "Intent"), 6, Number(8)); bytes = ReplaceVarint(bytes, 12, Number(0));
            bytes = Append(bytes, TextField(21, Id(700))); if (!autoPlace) bytes = Append(bytes, TextField(22, Id(800)));
            return Append(Append(Append(Append(bytes, Scalar(23, x)), Scalar(24, y)), Scalar(25, turns)), Scalar(26, autoPlace ? 1UL : 0UL));
        }
        private static byte[] TextField(int field, string value)
        { var bytes = Encoding.ASCII.GetBytes(value); return Append(Append(Number((ulong)(field << 3 | 2)), Number((ulong)bytes.Length)), bytes); }
        private byte[] TransferReceiptWire(int failure, ulong amount, uint transferFailure)
            => Append(Append(Encode(Receipt(failure, 8), "Receipt"), Scalar(7, amount)), Scalar(8, transferFailure));
        private static void SetWireProperty(object message, string name, object value)
        {
            var property = message.GetType().GetProperty(name);
            Assert.That(property, Is.Not.Null, "生成合同必须提供F/H字段：" + name); property.SetValue(message, value);
        }
        private static void ResetWireMessage(object message)
        {
            var method = message.GetType().GetMethod("ResetForReuse");
            Assert.That(method, Is.Not.Null, "命令/收据池化必须提供完整ResetForReuse，不能残留上次F/H数量与Aim。");
            method.Invoke(message, null);
        }
        private static byte[] Number(ulong value)
        {
            using var stream = new MemoryStream();
            while (value >= 128) { stream.WriteByte((byte)(value | 128)); value >>= 7; }
            stream.WriteByte((byte)value); return stream.ToArray();
        }
        private static byte[] Scalar(int field, ulong value) => Append(Number((ulong)field << 3), Number(value));
        private static byte[] FloatField(int field, float value)
        {
            uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
            return Append(Number((ulong)(field << 3 | 5)), new[] { (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24) });
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
                // 新27..29字段的tag本身也要两字节；夹具不能把第二个tag字节误当标量值。
                ulong tag = ReadFixtureVarint(bytes, ref offset); int wire = (int)(tag & 7), start = offset;
                if (wire == 1) { offset += 8; continue; }
                if (wire == 2) { int length = (int)ReadFixtureVarint(bytes, ref offset); offset += length; continue; }
                if (wire == 5) { offset += 4; continue; }
                if (wire != 0) throw new InvalidOperationException("Fixture不支持此Wire类型。");
                ReadFixtureVarint(bytes, ref offset);
                if ((int)(tag >> 3) != field) continue;
                var result = new byte[bytes.Length - (offset - start) + replacement.Length];
                Array.Copy(bytes, result, start); Array.Copy(replacement, 0, result, start, replacement.Length);
                Array.Copy(bytes, offset, result, start + replacement.Length, bytes.Length - offset); return result;
            }
            throw new InvalidOperationException("Fixture字段不存在。");
        }
        private static ulong ReadFixtureVarint(byte[] bytes, ref int offset)
        {
            ulong value = 0;
            for (int shift = 0; shift <= 63 && offset < bytes.Length; shift += 7)
            {
                byte next = bytes[offset++]; value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return value;
            }
            throw new InvalidOperationException("Fixture包含未结束的Varint。");
        }
    }
}
