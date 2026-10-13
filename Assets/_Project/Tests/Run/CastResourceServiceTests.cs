using Game.Core;
using Game.Skills;
using Game.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class CastResourceServiceTests : InventoryTestFixture
    {
        private ItemOwnershipStore _store;
        private WandLoadoutService _wands;
        private RaidSpellEligibility _raid;
        private CastResourceService _casts;
        private ulong _sequence;
        private uint _revision;
        private void Build(int root = 2, int child = 2, bool trigger = true, float cost = 10)
        {
            var wand = Define("wand", ItemKind.Wand); var parameters = new SerializedObject(wand.WandDefinition);
            parameters.FindProperty("_programCapacity").intValue = 6; parameters.FindProperty("_maxMana").floatValue = 100;
            parameters.FindProperty("_manaRegenPerSecond").floatValue = 0; parameters.ApplyModifiedPropertiesWithoutUndo();
            var spell = Define("emit", ItemKind.Spell); var payload = Define("payload", ItemKind.Spell);
            var prefab = Track(new GameObject("test projectile", typeof(Rigidbody), typeof(BoxCollider), typeof(Arrow)));
            spell.SpellDefinition.ProjectilePrefab = prefab; payload.SpellDefinition.ProjectilePrefab = prefab;
            spell.SpellDefinition.ManaCost = cost; payload.SpellDefinition.ManaCost = 7;
            if (trigger) spell.SpellDefinition.PayloadTrigger = PayloadTriggerMode.OnImpact;
            _store = Store(wand, spell, payload); Add(_store, "wand", "wand");
            _store.TryMove("wand", new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 0));
            _store.TryAdd(new RuntimeItemState("a", "emit", root), new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, 0));
            if (trigger) _store.TryAdd(new RuntimeItemState("b", "payload", child), new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, 2));
            var profile = new SinglePlayerProfile("player", _store); _wands = new WandLoadoutService(profile, "scope");
            _raid = new RaidSpellEligibility(profile); _casts = new CastResourceService(profile, _wands, _raid, "scope");
            _wands.ReadOnly.TryGetProgram("wand", out var program); _revision = program.ProgramRevision;
        }
        private CastResourceResultEvent Request(CastResourceOperation operation, string ticket = null, int layer = 0)
        { return _casts.Execute(new CastResourceRequestedEvent { InventoryId = "scope", SourceWandId = "wand", CallerId = "test",
            RequestId = ++_sequence, ProgramRevision = _revision, Operation = operation, TicketId = ticket, LayerIndex = layer }); }
        private int Charges(string id) { _store.TryGet(id, out var item); return item.RemainingCharges; }
        private float Mana() { _wands.ReadOnly.TryGetState("wand", out var state); return state.CurrentMana; }
        [Test]
        public void RootSuccessPrepaysAllFutureUsesButOnlyImmediateMana()
        {
            Build(); var prepared = Request(CastResourceOperation.PrepareRoot); Assert.That(prepared.Success, Is.True);
            Assert.That(Charges("a"), Is.EqualTo(2)); Assert.That(Mana(), Is.EqualTo(100));
            Assert.That(Request(CastResourceOperation.Complete, prepared.TicketId).Success, Is.True);
            Assert.That(Charges("a"), Is.EqualTo(1)); Assert.That(Charges("b"), Is.EqualTo(1)); Assert.That(Mana(), Is.EqualTo(90));
        }
        [Test]
        public void CancelRootLeavesChargesManaCooldownAndVersionUntouched()
        {
            Build(); uint before = _store.Version; var prepared = Request(CastResourceOperation.PrepareRoot);
            Request(CastResourceOperation.Cancel, prepared.TicketId); Assert.That(Charges("a"), Is.EqualTo(2));
            Assert.That(Charges("b"), Is.EqualTo(2)); Assert.That(Mana(), Is.EqualTo(100)); Assert.That(_store.Version, Is.EqualTo(before));
            _wands.ReadOnly.TryGetState("wand", out var state); Assert.That(state.CooldownRemaining, Is.Zero);
            Assert.That(_casts.ReadOnly.ActiveReservations, Is.Zero);
        }
        [Test]
        public void InsufficientFutureUsesRejectsEntireRoot()
        {
            Build(child: 0); uint before = _store.Version;
            Assert.That(Request(CastResourceOperation.PrepareRoot).Reason, Is.EqualTo(OwnedWandFailureReason.InsufficientUses));
            Assert.That(Charges("a"), Is.EqualTo(2)); Assert.That(Mana(), Is.EqualTo(100)); Assert.That(_store.Version, Is.EqualTo(before));
        }
        [Test]
        public void RootManaFailureDoesNotChargeFiniteUses()
        {
            Build(cost: 101); Assert.That(Request(CastResourceOperation.PrepareRoot).Reason, Is.EqualTo(OwnedWandFailureReason.InsufficientMana));
            Assert.That(Charges("a"), Is.EqualTo(2)); Assert.That(Charges("b"), Is.EqualTo(2));
        }
        [Test]
        public void FutureLayerChargesOriginalManaOnceAndDoesNotChargeUsesAgain()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Assert.That(Request(CastResourceOperation.PrepareLayer, root.TicketId, 1).Success, Is.True);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId, 1).Success, Is.True); Assert.That(Mana(), Is.EqualTo(83));
            Assert.That(Charges("b"), Is.EqualTo(1)); Assert.That(Request(CastResourceOperation.PrepareLayer, root.TicketId, 1).Success, Is.False);
            Assert.That(_casts.ReadOnly.ActiveReservations, Is.Zero);
        }
        [Test]
        public void AbandonUnusedPayloadNeverRefundsPrepaidCharges()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Request(CastResourceOperation.Abandon, root.TicketId, 1); Assert.That(Charges("b"), Is.EqualTo(1));
            Assert.That(Mana(), Is.EqualTo(90)); Assert.That(_casts.ReadOnly.ActiveReservations, Is.Zero);
        }
        [Test]
        public void CancelPayloadDoesNotSpendManaOrRefundRootUses()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Request(CastResourceOperation.PrepareLayer, root.TicketId, 1); Request(CastResourceOperation.Cancel, root.TicketId, 1);
            Assert.That(Mana(), Is.EqualTo(90)); Assert.That(Charges("b"), Is.EqualTo(1));
            Assert.That(Request(CastResourceOperation.PrepareLayer, root.TicketId, 1).Success, Is.False);
        }
        [Test]
        public void ReservationLocksInventoryUntilCancel()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot);
            Assert.That(_store.TryMove("a", ItemLocation.Warehouse).Reason, Is.EqualTo(InventoryFailureReason.ResourceBusy));
            Assert.That(Request(CastResourceOperation.PrepareRoot).Reason, Is.EqualTo(OwnedWandFailureReason.ResourceBusy));
            Request(CastResourceOperation.Cancel, root.TicketId); Assert.That(_store.TryMove("a", ItemLocation.Warehouse).Success, Is.True);
        }
        [Test]
        public void NoPayloadReservationIsCollectedAfterSuccess()
        {
            Build(trigger: false); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Assert.That(Charges("a"), Is.EqualTo(1)); Assert.That(_casts.ReadOnly.ActiveReservations, Is.Zero);
        }
        [Test]
        public void SessionCancellationInvalidatesOutstandingFutureBudgets()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            _casts.CancelAll(); Assert.That(Request(CastResourceOperation.PrepareLayer, root.TicketId, 1).Success, Is.False);
            Assert.That(Charges("b"), Is.EqualTo(1));
        }
        [Test]
        public void InvalidPrefabFailsWithoutResourceMutation()
        {
            Build(); _store.Catalog.TryGet("payload", out var definition); definition.SpellDefinition.ProjectilePrefab = null;
            Assert.That(Request(CastResourceOperation.PrepareRoot).Reason, Is.EqualTo(OwnedWandFailureReason.InvalidProgram)); Assert.That(Mana(), Is.EqualTo(100));
        }
        [Test]
        public void IdenticalRequestRetriesReturnSameReservation()
        {
            Build(); var request = new CastResourceRequestedEvent { InventoryId = "scope", SourceWandId = "wand", CallerId = "test",
                RequestId = 1, ProgramRevision = _revision, Operation = CastResourceOperation.PrepareRoot };
            var a = _casts.Execute(request); var b = _casts.Execute(request);
            Assert.That(a.Success, Is.True); Assert.That(b.TicketId, Is.EqualTo(a.TicketId)); Assert.That(_casts.ReadOnly.ActiveReservations, Is.EqualTo(1)); _casts.CancelAll();
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void NonFiniteVelocityConfigurationRejectsCast(float speed)
        {
            Build(); _store.Catalog.TryGet("emit", out var definition); definition.SpellDefinition.BaseSpeed = speed;
            var result = Request(CastResourceOperation.PrepareRoot); _casts.CancelAll();
            Assert.That(result.Reason, Is.EqualTo(OwnedWandFailureReason.InvalidProgram)); Assert.That(Mana(), Is.EqualTo(100));
        }
        [Test]
        public void LastPayloadUseAtZeroStillExecutesPrepaidLayer()
        {
            Build(child: 1); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Assert.That(Charges("b"), Is.Zero); Assert.That(Request(CastResourceOperation.PrepareLayer, root.TicketId, 1).Success, Is.True);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId, 1).Success, Is.True); Assert.That(Mana(), Is.EqualTo(83));
        }
        [Test]
        public void DifferentScopeAndStaleProgramCannotPrepareResources()
        {
            Build(); var request = new CastResourceRequestedEvent { InventoryId = "foreign", SourceWandId = "wand", CallerId = "test",
                RequestId = 1, ProgramRevision = _revision, Operation = CastResourceOperation.PrepareRoot };
            Assert.That(_casts.Execute(request).Success, Is.False); request.InventoryId = "scope"; request.ProgramRevision++;
            Assert.That(_casts.Execute(request).Reason, Is.EqualTo(OwnedWandFailureReason.StaleProgram)); Assert.That(Mana(), Is.EqualTo(100));
        }
    }
}
