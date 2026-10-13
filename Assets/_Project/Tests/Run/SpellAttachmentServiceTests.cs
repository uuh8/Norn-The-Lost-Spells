using System;
using Game.Core;
using Game.Combat;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class SpellAttachmentServiceTests : InventoryTestFixture
    {
        private ItemOwnershipStore _store;
        private SinglePlayerProfile _profile;
        private SpellAttachmentService _settings;
        private CastResourceService _casts;
        private WandLoadoutService _wands;
        private ulong _sequence, _settingsSequence;
        private uint _revision;
        private void Build(ulong water = 2, ulong poison = 1, bool trigger = false, bool two = false, bool unlimited = false)
        {
            var wand = Define("wand", ItemKind.Wand); var p = new SerializedObject(wand.WandDefinition);
            p.FindProperty("_programCapacity").intValue = 6; p.FindProperty("_maxMana").floatValue = 100;
            p.FindProperty("_manaRegenPerSecond").floatValue = 0; p.FindProperty("_castCooldown").floatValue = 0; p.ApplyModifiedPropertiesWithoutUndo();
            var card = Define("emit", ItemKind.Spell); var other = Define("other", ItemKind.Spell);
            var prefab = Track(new GameObject("material-test", typeof(Rigidbody), typeof(BoxCollider), typeof(Arrow)));
            card.SpellDefinition.ProjectilePrefab = other.SpellDefinition.ProjectilePrefab = prefab;
            card.SpellDefinition.ManaCost = 10; other.SpellDefinition.ManaCost = 7;
            if (trigger) card.SpellDefinition.PayloadTrigger = PayloadTriggerMode.OnImpact;
            if (two) { p = new SerializedObject(wand.WandDefinition); p.FindProperty("_baseDraws").intValue = 2; p.ApplyModifiedPropertiesWithoutUndo(); }
            _store = Store(wand, card, other, Define("bag", ItemKind.Backpack, bagWidth: 4, bagHeight: 2), Define("bottle", ItemKind.Container));
            Add(_store, "wand", "wand"); _store.TryMove("wand", new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 0));
            Add(_store, "bag", "bag"); _store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0));
            Add(_store, "source", "bottle"); _store.TryMove("source", Bag());
            var amounts = new[] { new MaterialAmount(1, water), new MaterialAmount(3, poison) };
            if (water + poison > 0) Item("source").Composition.AddReserved(amounts, 2);
            Assert.That(_store.TryAdd(new RuntimeItemState("a", "emit", 5, unlimited), Slot(0)), Is.True);
            Assert.That(_store.TryAdd(new RuntimeItemState("b", trigger ? "other" : "emit", 5, unlimited), trigger || two ? Slot(2) : ItemLocation.Warehouse), Is.True);
            _profile = new SinglePlayerProfile("player", _store); _settings = new SpellAttachmentService(_profile, "scope");
            _wands = new WandLoadoutService(_profile, "scope"); _casts = new CastResourceService(_profile, _wands, new RaidSpellEligibility(_profile), "scope");
            _wands.ReadOnly.TryGetProgram("wand", out var program); _revision = program.ProgramRevision;
        }
        private static ItemLocation Slot(int slot) => new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, slot);
        private RuntimeItemState Item(string id) { _store.TryGet(id, out var item); return item; }
        private SpellAttachmentResultEvent Set(string id = "a", ulong dose = 10, bool enabled = true) => _settings.Execute(new SpellAttachmentRequestedEvent
        { InventoryId = "scope", SpellId = id, ContainerId = "source", CallerId = "ui", RequestId = ++_settingsSequence, Dose = dose, Enabled = enabled, ExpectedVersion = _store.Version });
        private CastResourceResultEvent Request(CastResourceOperation op, string ticket = null, int layer = 0) => _casts.Execute(new CastResourceRequestedEvent
        { InventoryId = "scope", SourceWandId = "wand", CallerId = "cast", RequestId = ++_sequence, ProgramRevision = _revision, MaterialWorldId = "world", TicketId = ticket, LayerIndex = layer, Operation = op });
        private SpellMaterialOutputSnapshot Output(string ticket, int layer = 0, int command = 0)
        { Assert.That(_casts.ReadOnly.TryGetMaterialOutput(ticket, layer, command, out var snapshot), Is.True); return snapshot; }
        [TearDown] public void End() { _casts?.CancelAll(); _casts?.MaterialOutputs.EndAll(); EventBus<MaterialWorldDepositRequestedEvent>.Clear(); }
        [Test] public void SameNamedCopiesMoveAndChargeWithoutLosingIndividualConfiguration()
        {
            Build(); Assert.That(Set().Success, Is.True); Assert.That(Item("b").Attachment, Is.Null);
            _store.TryMove("a", ItemLocation.Warehouse); Assert.That(Item("a").Attachment.Dose, Is.EqualTo(10)); _store.TryMove("a", Slot(0));
            var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Assert.That(Item("a").RemainingCharges, Is.EqualTo(4)); Assert.That(Item("a").Attachment.Dose, Is.EqualTo(10)); Assert.That(Item("b").RemainingCharges, Is.EqualTo(5));
        }
        [Test] public void DoseTenSourceThreeProducesThreeOnlyOnCommit()
        {
            Build(); Set(); uint version = _store.Version; var root = Request(CastResourceOperation.PrepareRoot); var output = Output(root.TicketId);
            Assert.That(output.Budget.Total, Is.EqualTo(3)); Assert.That(output.Status, Is.EqualTo(SpellMaterialSupplyStatus.Partial));
            Assert.That(Item("source").Composition.Total, Is.EqualTo(3)); Assert.That(_profile.PendingMaterialOutputs, Is.Zero);
            Request(CastResourceOperation.Complete, root.TicketId); Assert.That(Item("source").Composition.Total, Is.Zero);
            Assert.That(_profile.PendingMaterialOutputs, Is.EqualTo(1)); Assert.That(_store.Version, Is.GreaterThan(version));
        }
        [Test] public void CancelPreservesMaterialsManaAndCharges()
        {
            Build(); Set(); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Cancel, root.TicketId);
            Assert.That(Item("source").Composition.Total, Is.EqualTo(3)); Assert.That(Item("a").RemainingCharges, Is.EqualTo(5));
            _wands.ReadOnly.TryGetState("wand", out var state); Assert.That(state.CurrentMana, Is.EqualTo(100)); Assert.That(_profile.PendingMaterialOutputs, Is.Zero);
        }
        [Test] public void UnlimitedCardMaterialCommitStillChangesInventoryVersion()
        { Build(unlimited: true); Set(); uint version = _store.Version; var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Assert.That(_store.Version, Is.GreaterThan(version)); Assert.That(Item("a").HasUnlimitedCharges, Is.True); }
        [TestCase(true)] [TestCase(false)] public void EmptyOrNoLongerCarriedSourceKeepsBaseCast(bool empty)
        {
            Build(empty ? 0UL : 2, empty ? 0UL : 1); Set(); if (!empty) _store.TryMove("source", ItemLocation.Warehouse);
            var root = Request(CastResourceOperation.PrepareRoot); Assert.That(root.Success, Is.True); var output = Output(root.TicketId);
            Assert.That(output.Budget.Total, Is.Zero); Assert.That(output.Status, Is.EqualTo(empty ? SpellMaterialSupplyStatus.Empty : SpellMaterialSupplyStatus.InvalidSource));
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True); Assert.That(Item("a").RemainingCharges, Is.EqualTo(4));
        }
        [Test] public void UnexecutedSameNameCopyDoesNotTakeMaterials()
        { Build(); Set("b"); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId); Assert.That(Item("source").Composition.Total, Is.EqualTo(3)); }
        [Test] public void FutureTriggerTakesCurrentMaterialsAtExecutionAndUsesFrozenSetting()
        {
            Build(20, 10, trigger: true); Set("b", 10); var root = Request(CastResourceOperation.PrepareRoot);
            Assert.That(Output(root.TicketId).Budget.Total, Is.Zero); Request(CastResourceOperation.Complete, root.TicketId); Assert.That(Item("source").Composition.Total, Is.EqualTo(30));
            Set("b", 25); Assert.That(Request(CastResourceOperation.PrepareLayer, root.TicketId, 1).Success, Is.True);
            Assert.That(Output(root.TicketId, 1).Budget.Total, Is.EqualTo(10)); Request(CastResourceOperation.Complete, root.TicketId, 1);
            Assert.That(Item("source").Composition.Total, Is.EqualTo(20));
        }
        [Test] public void AbandonedTriggerDoesNotTakeItsMaterials()
        { Build(trigger: true); Set("b"); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId); Request(CastResourceOperation.Abandon, root.TicketId, 1);
            Assert.That(Item("source").Composition.Total, Is.EqualTo(3)); Assert.That(Item("b").RemainingCharges, Is.EqualTo(4)); }
        [Test] public void TwoIndependentOutputsShareAvailableSourceWithoutDuplicatingIt()
        {
            Build(20, 10, two: true); Set("a", 20); Set("b", 20); var root = Request(CastResourceOperation.PrepareRoot);
            var a = Output(root.TicketId); var b = Output(root.TicketId, command: 1);
            Assert.That(a.Budget.Total, Is.EqualTo(20)); Assert.That(b.Budget.Total, Is.EqualTo(10)); Assert.That(a.OutputId, Is.Not.EqualTo(b.OutputId));
            Request(CastResourceOperation.Complete, root.TicketId); Assert.That(Item("source").Composition.Total, Is.Zero); Assert.That(_profile.PendingMaterialOutputs, Is.EqualTo(2));
        }
        [Test] public void SplitThreeKeepsTotalThreeAndEveryComponent()
        {
            var original = new SpellMaterialBudget(new[] { new MaterialAmount(1, 2), new MaterialAmount(3, 1) }, 2); var shares = original.Split(3);
            ulong water = 0, poison = 0; foreach (var share in shares) { Assert.That(share.Total, Is.EqualTo(1)); for (int i = 0; i < share.Count; i++) if (share[i].MaterialKey == 1) water += share[i].Quantity; else poison += share[i].Quantity; }
            Assert.That(water, Is.EqualTo(2)); Assert.That(poison, Is.EqualTo(1)); Assert.That(original.Total, Is.EqualTo(3));
        }
        [Test] public void RejectedDepositKeepsOwnerThenSuccessfulRetryResolvesOnlyOnce()
        {
            Build(); Set(); var root = Request(CastResourceOperation.PrepareRoot); var output = Output(root.TicketId); Request(CastResourceOperation.Complete, root.TicketId);
            var request = new SpellMaterialOutputEvent { InventoryId = "scope", OutputId = output.OutputId, Position = Vector3.one };
            Assert.That(_casts.MaterialOutputs.Execute(request).Resolved, Is.False); Assert.That(_profile.PendingMaterialOutputs, Is.EqualTo(1));
            EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(Accept);
            Assert.That(_casts.MaterialOutputs.Execute(request).Quantity, Is.EqualTo(3)); Assert.That(_profile.PendingMaterialOutputs, Is.Zero);
            Assert.That(_casts.MaterialOutputs.Execute(request).Resolved, Is.False);
        }
        private static void Accept(MaterialWorldDepositRequestedEvent request)
        { var e = request.Exchange; e.Handled = true; Assert.That(e.Source.TryReserve(e.Amounts, e.Count, out _), Is.True); e.Source.Commit();
            for (int i = 0; i < e.Count; i++) e.Transferred += e.Amounts[i].Quantity; e.Failure = MaterialTransferFailure.None; }
        [Test] public void ActiveOutputBlocksMapperAndEndsWithoutRefund()
        {
            Build(); Set(); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Assert.Throws<ArgumentException>(() => ProfileSaveMapper.ToDto(_profile, 1)); _casts.MaterialOutputs.EndAll();
            Assert.That(_casts.MaterialOutputs.LostQuantity(1), Is.EqualTo(2)); Assert.That(_casts.MaterialOutputs.LostQuantity(3), Is.EqualTo(1)); Assert.That(Item("source").Composition.Total, Is.Zero);
        }
        [Test] public void ConfigurationPersistsAsSchemaThreeAndOldSchemaTwoStillLoads()
        {
            Build(); Set(); var dto = JsonUtility.FromJson<SinglePlayerSaveDto>(JsonUtility.ToJson(ProfileSaveMapper.ToDto(_profile, 1)));
            Assert.That(ProfileSaveMapper.TryRestore(dto, _store.Catalog, out var restored).Success, Is.True);
            restored.Items.TryGet("a", out var card); Assert.That(card.Attachment.ContainerId, Is.EqualTo("source")); Assert.That(card.Attachment.Dose, Is.EqualTo(10));
            Set(enabled: false); dto = ProfileSaveMapper.ToDto(_profile, 1); dto.SchemaVersion = 2;
            Assert.That(ProfileSaveMapper.TryRestore(dto, _store.Catalog, out _).Success, Is.True);
        }
        [Test] public void EndedOutputCanResolveWhileAnotherCastOwnsInventoryLock()
        {
            Build(20, 10); Set(); var root = Request(CastResourceOperation.PrepareRoot); var output = Output(root.TicketId); Request(CastResourceOperation.Complete, root.TicketId);
            var next = Request(CastResourceOperation.PrepareRoot); Assert.That(next.Success, Is.True);
            Assert.That(_casts.MaterialOutputs.Execute(new SpellMaterialOutputEvent { InventoryId = "scope", OutputId = output.OutputId, Ended = true }).Resolved, Is.True);
            Assert.That(_profile.PendingMaterialOutputs, Is.Zero); Request(CastResourceOperation.Cancel, next.TicketId);
        }
    }
}
