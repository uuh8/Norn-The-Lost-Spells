using Game.Core;
using NUnit.Framework;
using UnityEditor;

namespace Game.Run.Tests
{
    public sealed class WandLoadoutServiceTests : InventoryTestFixture
    {
        private ItemOwnershipStore _store;
        private WandLoadoutService _service;
        private ulong _request;
        [SetUp] public void Setup()
        {
            _store = Store(Define("wand", ItemKind.Wand), Define("spell", ItemKind.Spell), Define("bag", ItemKind.Backpack, bagWidth: 4));
            Add(_store, "a", "wand"); Add(_store, "b", "wand"); Add(_store, "bag", "bag"); Add(_store, "card", "spell", 3);
            Assert.That(LoadoutOps.TryEquipWand(_store, "a", "player", 0, _store.Version).Success, Is.True);
            Assert.That(LoadoutOps.TryEquipWand(_store, "b", "player", 1, _store.Version).Success, Is.True);
            Assert.That(_store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)).Success, Is.True);
            _service = new WandLoadoutService(new SinglePlayerProfile("player", _store), "scope");
        }
        private WandResourceRequestedEvent Request(string source = "a", float cost = 20, bool root = true)
        { _service.ReadOnly.TryGetProgram(source, out WandProgramSnapshot program);
            return new WandResourceRequestedEvent { InventoryId = "scope", CallerId = "caster", RequestId = ++_request,
                SourceWandId = source, ManaCost = cost, IsRootCast = root, ProgramRevision = program.ProgramRevision }; }
        private WandStateSnapshot State(string id)
        { Assert.That(_service.ReadOnly.TryGetState(id, out WandStateSnapshot state), Is.True); return state; }
        [Test] public void Switching_ChangesOnlySelection()
        { Assert.That(_service.Execute(Request()).Success, Is.True); Assert.That(_service.TrySelect(1), Is.True);
            Assert.That(_service.ReadOnly.SelectedWandId, Is.EqualTo("b")); Assert.That(State("a").CurrentMana, Is.EqualTo(80));
            Assert.That(State("a").CooldownRemaining, Is.EqualTo(.8f)); Assert.That(State("b").CurrentMana, Is.EqualTo(100)); }
        [Test] public void UnheldCarriedWand_RecoversWhileSelectedWandIsDifferent()
        { _service.Execute(Request()); _service.TrySelect(1); _service.Tick(.5f);
            Assert.That(State("a").CurrentMana, Is.EqualTo(85)); Assert.That(State("a").CooldownRemaining, Is.EqualTo(.3f).Within(.001f)); }
        [Test] public void Warehouse_FreezesAndReequipDoesNotReset()
        { _service.Execute(Request()); Assert.That(_store.TryMove("a", ItemLocation.Warehouse).Success, Is.True);
            _service.Synchronize(); _service.Tick(10); Assert.That(State("a").CurrentMana, Is.EqualTo(80));
            Assert.That(LoadoutOps.TryEquipWand(_store, "a", "player", 0, _store.Version).Success, Is.True);
            _service.Synchronize(); Assert.That(State("a").CurrentMana, Is.EqualTo(80)); Assert.That(State("a").CooldownRemaining, Is.EqualTo(.8f)); }
        [Test] public void BackpackWand_StillRecovers()
        { _service.Execute(Request()); Assert.That(_store.TryMove("a", Bag()).Success, Is.True);
            _service.Synchronize(); _service.Tick(.5f); Assert.That(State("a").CurrentMana, Is.EqualTo(85)); Assert.That(State("a").SlotIndex, Is.EqualTo(-1)); }
        [Test] public void AllCarriedWandsRecoverBeyondThreeAndWarehouseTransferPreservesStateAndProgram()
        {
            Add(_store,"c","wand");Add(_store,"d","wand");Add(_store,"e","wand");
            Assert.That(_store.TryMove("c",Bag()).Success,Is.True);Assert.That(_store.TryMove("d",Bag(1,0)).Success,Is.True);
            Assert.That(LoadoutOps.TryEquipWand(_store,"e","player",2,_store.Version).Success,Is.True);
            Assert.That(_store.TryMove("card",new ItemLocation(ItemLocationKind.WandSlot,"d",0,0,0,0)).Success,Is.True);
            _service.Synchronize();_service.ReadOnly.TryGetProgram("d",out var program);
            foreach(string id in new[]{"a","b","c","d","e"})Assert.That(_service.Execute(Request(id)).Success,Is.True);
            _service.Tick(.5f);
            foreach(string id in new[]{"a","b","c","d","e"})
            {Assert.That(State(id).CurrentMana,Is.EqualTo(85),id);Assert.That(State(id).CooldownRemaining,Is.EqualTo(.3f).Within(.001f),id);}
            Assert.That(_store.TryMove("d",ItemLocation.Warehouse).Success,Is.True);_service.Synchronize();_service.Tick(1);
            Assert.That(State("d").CurrentMana,Is.EqualTo(85));
            Assert.That(_store.TryMove("d",Bag(1,0)).Success,Is.True);_service.Synchronize();
            Assert.That(State("d").CurrentMana,Is.EqualTo(85));Assert.That(State("d").CooldownRemaining,Is.EqualTo(.3f).Within(.001f));
            _service.ReadOnly.TryGetProgram("d",out var restored);Assert.That(restored.ProgramRevision,Is.EqualTo(program.ProgramRevision));
            Assert.That(restored.Program.GetInstanceId(0),Is.EqualTo("card"));
        }
        [TestCase(-1)] [TestCase(2)] [TestCase(3)] public void InvalidOrEmptySlot_DoesNotChangeSelection(int slot)
        { Assert.That(_service.TrySelect(slot), Is.False); Assert.That(_service.ReadOnly.SelectedWandId, Is.EqualTo("a")); }
        [Test] public void Payload_UsesExplicitSourceAfterSwitch()
        { _service.TrySelect(1); Assert.That(_service.Execute(Request("a", 30, false)).Success, Is.True);
            Assert.That(State("a").CurrentMana, Is.EqualTo(70)); Assert.That(State("b").CurrentMana, Is.EqualTo(100)); Assert.That(State("a").CooldownRemaining, Is.Zero); }
        [Test] public void DuplicateReceipt_DoesNotSpendAgain()
        { var request = Request(); Assert.That(_service.Execute(request).Success, Is.True); Assert.That(_service.Execute(request).Success, Is.True);
            Assert.That(State("a").CurrentMana, Is.EqualTo(80)); request.ManaCost = 10;
            Assert.That(_service.Execute(request).Success, Is.False); Assert.That(State("a").CurrentMana, Is.EqualTo(80)); }
        [Test] public void OlderRequest_IsRejected()
        { var old = Request(); var next = Request("b"); _service.Execute(next);
            Assert.That(_service.Execute(old).Success, Is.False); Assert.That(State("a").CurrentMana, Is.EqualTo(100)); }
        [Test] public void WrongScope_DoesNotSpend()
        { var request = Request(); request.InventoryId = "other"; Assert.That(_service.Execute(request).Success, Is.False); Assert.That(State("a").CurrentMana, Is.EqualTo(100)); }
        [Test] public void OldProjection_RemainsImmutableAndStaleRootIsRejected()
        { _service.ReadOnly.TryGetProgram("a", out WandProgramSnapshot before); var request = Request();
            Assert.That(_store.TryMove("card", new ItemLocation(ItemLocationKind.WandSlot, "a", 0, 0, 0, 0)).Success, Is.True);
            _service.Synchronize(); _service.ReadOnly.TryGetProgram("a", out WandProgramSnapshot after);
            Assert.That(before.Program[0], Is.Null); Assert.That(after.Program.GetInstanceId(0), Is.EqualTo("card"));
            Assert.That(after.ProgramRevision, Is.GreaterThan(before.ProgramRevision));
            Assert.That(_service.Execute(request).Reason, Is.EqualTo(OwnedWandFailureReason.StaleProgram)); Assert.That(State("a").CurrentMana, Is.EqualTo(100)); }
        [Test] public void UnrelatedInventoryMove_PreservesProgramRevision()
        { _service.ReadOnly.TryGetProgram("a", out WandProgramSnapshot before); Assert.That(_store.TryMove("card", Bag()).Success, Is.True);
            _service.Synchronize(); _service.ReadOnly.TryGetProgram("a", out WandProgramSnapshot after);
            Assert.That(after.ProgramRevision, Is.EqualTo(before.ProgramRevision)); Assert.That(after.Program, Is.SameAs(before.Program)); }
        [Test] public void FixedStats_AreFrozenAtCatalogCreation()
        { _store.Catalog.TryGet("wand", out ItemDefinition definition); var data = new SerializedObject(definition.WandDefinition);
            data.FindProperty("_maxMana").floatValue = 999; data.ApplyModifiedPropertiesWithoutUndo();
            _service.Synchronize(); Assert.That(State("a").MaxMana, Is.EqualTo(100)); }
        [Test] public void ReadOnlyView_CannotBeCastToWritableService()
        { Assert.That((object)_service.ReadOnly, Is.Not.InstanceOf<WandLoadoutService>()); }
    }
}
