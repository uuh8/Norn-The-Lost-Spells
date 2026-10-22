using Game.Core;
using Game.Combat;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>真实库存+真实Machine的服务测试，不创建正式资产、不调用角色生成路径。</summary>
    public abstract class WandExecutionTransactionTestFixture : InventoryTestFixture
    {
        protected ItemOwnershipStore _store;
        protected SinglePlayerProfile _profile;
        protected WandLoadoutService _wands;
        protected WandExecutionTransactionService _casts;
        protected RaidSpellEligibility _raid;
        protected ulong _sequence;

        protected void Build(int root = 3, int child = 3, bool trigger = true, float cost = 10, int extra = 0, int baseDraws = 1,int capacity=16)
        {
            var wand = Define("wand", ItemKind.Wand);
            var parameters = new SerializedObject(wand.WandDefinition);
            parameters.FindProperty("_programCapacity").intValue = capacity;
            parameters.FindProperty("_manaRegenPerSecond").floatValue = 0;
            parameters.FindProperty("_baseDraws").intValue = baseDraws;
            parameters.ApplyModifiedPropertiesWithoutUndo();
            var emit = Define("emit", ItemKind.Spell); var payload = Define("payload", ItemKind.Spell);
            var prefab = Track(new GameObject("V2 test projectile", typeof(Rigidbody), typeof(BoxCollider), typeof(Arrow)));
            emit.SpellDefinition.ProjectilePrefab = prefab; payload.SpellDefinition.ProjectilePrefab = prefab;
            emit.SpellDefinition.ManaCost = cost; payload.SpellDefinition.ManaCost = 7;
            if (trigger) emit.SpellDefinition.PayloadTrigger = PayloadTriggerMode.OnImpact;
            _store = Store(wand, emit, payload, Define("bag", ItemKind.Backpack, bagWidth: 4, bagHeight: 2), Define("bottle", ItemKind.Container)); Add(_store, "wand", "wand");
            Assert.That(_store.TryMove("wand", new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(_store.TryAdd(new RuntimeItemState("a", "emit", root), Slot(0)), Is.True);
            if (trigger) Assert.That(_store.TryAdd(new RuntimeItemState("b", "payload", child), Slot(1)), Is.True);
            for (int i = 0; i < extra; i++) Assert.That(_store.TryAdd(new RuntimeItemState("x" + i, "payload", 3), Slot(i + 2)), Is.True);
            _profile = new SinglePlayerProfile("player", _store); _wands = new WandLoadoutService(_profile, "scope");
            _raid = new RaidSpellEligibility(_profile);
            _casts = new WandExecutionTransactionService(_profile, _wands, _raid, "scope", new WandExecutionReplayArchive());
        }
        protected static ItemLocation Slot(int slot) => new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, slot);
        protected WandExecutionSnapshot State()
        { Assert.That(_casts.TryGetState("wand", out var state), Is.True, "V2服务必须提供真实实例的冻结执行起点"); return state; }
        protected CastResourceRequestedEvent NewRequest(CastResourceOperation operation, string ticket = null, int layer = 0, string caller = "test")
        {
            var state = State();
            long revision = state.Program.Revision;
            if (ticket != null && _casts.TryGetGraph(ticket, out var graph)) revision = graph.Program.Revision;
            return new CastResourceRequestedEvent { InventoryId = "scope", SourceWandId = "wand", CallerId = caller,
                RequestId = ++_sequence, ProgramRevision = (uint)revision, ExecutionRevision = state.Draw.ExecutionRevision,
                ResourceVersion = state.ResourceVersion, InventoryVersion = _store.Version, Operation = operation, TicketId = ticket, LayerIndex = layer };
        }
        protected CastResourceResultEvent Request(CastResourceOperation operation, string ticket = null, int layer = 0, string caller = "test")
            => _casts.Execute(NewRequest(operation, ticket, layer, caller));
        protected int Uses(string id) => _store.TryGet(id, out var item) ? item.RemainingCharges : 0;
        [TearDown] public void ReleaseResources() { _casts?.CancelAll(); _casts?.MaterialOutputs.EndAll(); }
    }

    public sealed class WandExecutionTransactionTests : WandExecutionTransactionTestFixture
    {

        [Test] public void RootPrepaysChildManaButPrepareDoesNotChangeAnyResource()
        {
            Build(); var before = State(); uint version = _store.Version;
            var root = Request(CastResourceOperation.PrepareRoot); Assert.That(root.Success, Is.True);
            Assert.That(State().Mana, Is.EqualTo(100)); Assert.That(State().Draw, Is.SameAs(before.Draw));
            Assert.That(Uses("a"), Is.EqualTo(3)); Assert.That(_store.Version, Is.EqualTo(version));
            Assert.That(_casts.Archive.Count, Is.Zero);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True);
            Assert.That(State().Mana, Is.EqualTo(83)); Assert.That(Uses("a"), Is.EqualTo(2)); Assert.That(Uses("b"), Is.EqualTo(2));
            Assert.That(State().Draw.ExecutionRevision, Is.EqualTo(1)); Assert.That(_casts.Archive.Count, Is.EqualTo(1));
            _casts.CancelAll();
        }
        [Test] public void DuplicateCompleteReturnsReceiptWithoutAdvancingTwice()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); var complete = NewRequest(CastResourceOperation.Complete, root.TicketId);
            Assert.That(_casts.Execute(complete).Success, Is.True); var after = State();
            Assert.That(_casts.Execute(complete).Success, Is.True); Assert.That(State().Draw, Is.SameAs(after.Draw));
            Assert.That(State().Mana, Is.EqualTo(83)); Assert.That(_casts.Archive.Count, Is.EqualTo(1)); _casts.CancelAll();
        }
        [Test] public void CancelRestoresManaUsesDeckRandomAndTime()
        {
            Build(); var before = State(); var root = Request(CastResourceOperation.PrepareRoot);
            Assert.That(Request(CastResourceOperation.Cancel, root.TicketId).Success, Is.True);
            Assert.That(State().Draw, Is.SameAs(before.Draw)); Assert.That(State().Mana, Is.EqualTo(before.Mana));
            Assert.That(State().CastRemaining, Is.Zero); Assert.That(State().ReloadRemaining, Is.Zero);
            Assert.That(Uses("b"), Is.EqualTo(3)); Assert.That(_casts.IsPending, Is.False); Assert.That(_casts.Archive.Count, Is.Zero);
        }
        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void StaleProgramExecutionOrResourcesCannotReserve(int stale)
        {
            Build(); var before = State(); var request = NewRequest(CastResourceOperation.PrepareRoot);
            if (stale == 0) request.ProgramRevision++; else if (stale == 1) request.ExecutionRevision++; else request.ResourceVersion++;
            Assert.That(_casts.Execute(request).Success, Is.False); Assert.That(State().Draw, Is.SameAs(before.Draw));
            Assert.That(State().Mana, Is.EqualTo(100)); Assert.That(_casts.IsPending, Is.False);
        }
        [Test] public void ChildUsesPrepaidGraphAfterSourceManaIsSpentElsewhere()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True);
            Assert.That(_wands.Execute(new WandResourceRequestedEvent { InventoryId = "scope", SourceWandId = "wand", CallerId = "other",
                RequestId = 1, ManaCost = 83 }).Success, Is.True);
            Assert.That(State().Mana, Is.Zero); Assert.That(Request(CastResourceOperation.PrepareLayer, root.TicketId, 1).Success, Is.True);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId, 1).Success, Is.True);
            Assert.That(State().Mana, Is.Zero); Assert.That(Uses("b"), Is.EqualTo(2));
        }
        [Test] public void LastUseRemovesInstanceWithoutResettingRemainingDeck()
        {
            Build(root: 1, child: 1, extra: 1); var root = Request(CastResourceOperation.PrepareRoot);
            Assert.That(_casts.TryGetGraph(root.TicketId, out var graph), Is.True);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True);
            Assert.That(_store.TryGet("a", out _), Is.False); Assert.That(_store.TryGet("b", out _), Is.False);
            var next = State(); Assert.That(next.Draw.Deck.Count, Is.EqualTo(1)); Assert.That(next.Draw.Discard.Count, Is.Zero);
            Assert.That(next.Program.Instructions[next.Draw.Deck[0]].InstanceId, Is.EqualTo("x0"));
            Assert.That(graph.Outputs[1].SourceInstanceId, Is.EqualTo("b"));
            Assert.That(Request(CastResourceOperation.PrepareLayer, root.TicketId, 1).Success, Is.True);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId, 1).Success, Is.True);
            Assert.That(State().Draw, Is.SameAs(next.Draw));
        }
        [Test] public void InventoryAndOtherManaEntrypointsStayLockedAcrossPrepare()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot);
            Assert.That(_store.TryMove("a", ItemLocation.Warehouse).Reason, Is.EqualTo(InventoryFailureReason.ResourceBusy));
            Assert.That(_wands.Execute(new WandResourceRequestedEvent { InventoryId = "scope", SourceWandId = "wand", CallerId = "other", RequestId = 1, ManaCost = 1 }).Reason,
                Is.EqualTo(OwnedWandFailureReason.ResourceBusy));
            _wands.Tick(20); Assert.That(State().Mana, Is.EqualTo(100));
            Request(CastResourceOperation.Cancel, root.TicketId); Assert.That(_store.TryMove("a", ItemLocation.Warehouse).Success, Is.True);
        }
        [Test] public void NoAffordableOutputCanLegallyAdvanceWithoutFakeSpawn()
        {
            Build(trigger: false, cost: 101); var before = State(); var root = Request(CastResourceOperation.PrepareRoot);
            Assert.That(root.Success, Is.True); Assert.That(_casts.TryGetGraph(root.TicketId, out var graph), Is.True);
            Assert.That(graph.RootOutputCount, Is.Zero); Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True);
            Assert.That(State().Mana, Is.EqualTo(100)); Assert.That(Uses("a"), Is.EqualTo(3));
            Assert.That(State().Draw.ExecutionRevision, Is.GreaterThan(before.Draw.ExecutionRevision));
        }
        [Test] public void InvalidPrefabRejectsBeforeReserving()
        {
            Build(); _store.Catalog.TryGet("emit", out var item); item.SpellDefinition.ProjectilePrefab = null;
            Assert.That(Request(CastResourceOperation.PrepareRoot).Reason, Is.EqualTo(OwnedWandFailureReason.InvalidProgram));
            Assert.That(State().Mana, Is.EqualTo(100)); Assert.That(_casts.IsPending, Is.False);
        }
        [Test] public void OldTicketCanReleaseAfterManualEditButCannotAdvanceNewProgram()
        {
            Build(extra: 1); var root = Request(CastResourceOperation.PrepareRoot); Request(CastResourceOperation.Complete, root.TicketId);
            Assert.That(_store.TryMove("x0", ItemLocation.Warehouse).Success, Is.True); var edited = State();
            var prepare = NewRequest(CastResourceOperation.PrepareLayer, root.TicketId, 1);
            prepare.ProgramRevision = 1; Assert.That(_casts.Execute(prepare).Success, Is.True);
            var complete = NewRequest(CastResourceOperation.Complete, root.TicketId, 1); complete.ProgramRevision = 1;
            Assert.That(_casts.Execute(complete).Success, Is.True); Assert.That(State().Draw, Is.SameAs(edited.Draw));
            Assert.That(State().Mana, Is.EqualTo(83));
        }
        [Test] public void CancellingOneCallerDoesNotRemoveAnotherCallersFutureGraph()
        {
            Build(); var a = Request(CastResourceOperation.PrepareRoot, caller: "A"); Request(CastResourceOperation.Complete, a.TicketId, caller: "A");
            _wands.Tick(10); var b = Request(CastResourceOperation.PrepareRoot, caller: "B"); Request(CastResourceOperation.Complete, b.TicketId, caller: "B");
            Request(CastResourceOperation.AbandonCaller, caller: "A");
            Assert.That(_casts.TryGetGraph(a.TicketId, out _), Is.False); Assert.That(_casts.TryGetGraph(b.TicketId, out _), Is.True);
            _casts.CancelAll();
        }
        [Test] public void CompleteRetryAfterAnotherRequestStillReturnsOriginalReceipt()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); var complete = NewRequest(CastResourceOperation.Complete, root.TicketId);
            Assert.That(_casts.Execute(complete).Success, Is.True);
            Assert.That(Request(CastResourceOperation.PrepareLayer, root.TicketId, 1).Success, Is.True);
            Assert.That(_casts.Execute(complete).Success, Is.True, "中间有别的请求也不能把相同Complete改成失败收据");
            Assert.That(State().Mana, Is.EqualTo(83));
        }
        [Test] public void LostWandReservationRejectsCompleteWithoutAnyPayment()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); var before = State();
            _wands.ReleaseExecution("wand", root.TicketId);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.False);
            Assert.That(State().Draw, Is.SameAs(before.Draw)); Assert.That(State().Mana, Is.EqualTo(100));
            Assert.That(Uses("a"), Is.EqualTo(3)); Assert.That(_casts.IsPending, Is.False);
        }
        [Test] public void ChangedInventoryVersionRejectsBeforeReservation()
        {
            Build(); var request = NewRequest(CastResourceOperation.PrepareRoot); request.InventoryVersion++;
            Assert.That(_casts.Execute(request).Reason, Is.EqualTo(OwnedWandFailureReason.StaleResources));
            Assert.That(_casts.IsPending, Is.False);
        }
        [Test] public void SixteenNestedTriggersRemainWithinCurrentOwnedCapacity()
        {
            Build(extra: 14, cost: 0); _store.Catalog.TryGet("payload", out var definition);
            definition.SpellDefinition.PayloadTrigger = PayloadTriggerMode.OnImpact; definition.SpellDefinition.ManaCost = 0;
            var before = State();
            var root = Request(CastResourceOperation.PrepareRoot); Assert.That(root.Success, Is.True);
            Assert.That(_casts.TryGetGraph(root.TicketId, out var graph), Is.True); Assert.That(graph.Outputs.Count, Is.EqualTo(16));
            Assert.That(State().Draw, Is.SameAs(before.Draw)); Assert.That(Uses("a"), Is.EqualTo(3));
            Assert.That(State().Mana, Is.EqualTo(100)); Assert.That(_casts.Archive.Count, Is.Zero);
            Request(CastResourceOperation.Cancel, root.TicketId);
        }
        [Test] public void LostInventoryReservationRejectsBeforeCommittingManaOrMaterials()
        {
            Build(); var root = Request(CastResourceOperation.PrepareRoot); var before = State();
            var field = typeof(ItemOwnershipStore).GetField("_prepared", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var prepared = (ItemOwnershipStore.PreparedInventory)field.GetValue(_store);
            Assert.That(_store.CompletePrepared(prepared, false), Is.True);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.False);
            Assert.That(State().Draw, Is.SameAs(before.Draw)); Assert.That(State().Mana, Is.EqualTo(100));
            Assert.That(_casts.IsPending, Is.False); Assert.That(_casts.Archive.Count, Is.Zero);
        }
        [Test] public void EmptyProgramRejectsWithoutCreatingTickets()
        {
            Build(trigger: false); _store.TryMove("a", ItemLocation.Warehouse);
            Assert.That(Request(CastResourceOperation.PrepareRoot).Reason, Is.EqualTo(OwnedWandFailureReason.EmptyProgram));
            Assert.That(_casts.ActiveReservations, Is.Zero); Assert.That(State().Mana, Is.EqualTo(100));
        }
        [Test] public void DepletionRebindKeepsFrozenValuesInsteadOfRereadingEditedAsset()
        {
            Build(root: 1, extra: 1); var before = State();
            float damage = before.Program.Instructions[2].Emission.BaseDamage;
            var root = Request(CastResourceOperation.PrepareRoot);
            _store.Catalog.TryGet("payload", out var definition); definition.SpellDefinition.BaseDamage = damage + 999;
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True);
            var after = State(); Assert.That(after.Program.Instructions[1].InstanceId, Is.EqualTo("x0"));
            Assert.That(after.Program.Instructions[1].Emission.BaseDamage, Is.EqualTo(damage));
            Assert.That(after.Draw.ExecutionRevision, Is.EqualTo(before.Draw.ExecutionRevision + 1));
        }
        [Test] public void QualificationIsPaidOnRaidEntryAndNegativeManaClampsAtMax()
        {
            Build(trigger: false, extra: 1, cost: -20); _store.Catalog.TryGet("emit", out var definition);
            definition.SpellDefinition.Kind = SpellKind.Modify;
            Assert.That(_raid.TryBegin("test.raid", out _), Is.True); Assert.That(Uses("a"), Is.EqualTo(2));
            _wands.Execute(new WandResourceRequestedEvent { InventoryId = "scope", SourceWandId = "wand", CallerId = "other", RequestId = 1, ManaCost = 10 });
            var root = Request(CastResourceOperation.PrepareRoot); Assert.That(root.Success, Is.True);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True);
            Assert.That(State().Mana, Is.EqualTo(93)); Assert.That(Uses("a"), Is.EqualTo(2)); Assert.That(Uses("x0"), Is.EqualTo(2));
            _wands.Tick(1); var next = Request(CastResourceOperation.PrepareRoot);
            Assert.That(Request(CastResourceOperation.Complete, next.TicketId).Success, Is.True); Assert.That(Uses("a"), Is.EqualTo(2));
        }
        [Test] public void MissingQualificationSkipsCardWithoutPayingItsNegativeMana()
        {
            Build(trigger: false, extra: 1, cost: -20); _store.Catalog.TryGet("emit", out var definition);
            definition.SpellDefinition.Kind = SpellKind.Modify;
            _wands.Execute(new WandResourceRequestedEvent { InventoryId = "scope", SourceWandId = "wand", CallerId = "other", RequestId = 1, ManaCost = 50 });
            var root = Request(CastResourceOperation.PrepareRoot); Assert.That(root.Success, Is.True);
            Assert.That(Request(CastResourceOperation.Complete, root.TicketId).Success, Is.True);
            Assert.That(State().Mana, Is.EqualTo(43)); Assert.That(Uses("a"), Is.EqualTo(3));
        }
    }
}
