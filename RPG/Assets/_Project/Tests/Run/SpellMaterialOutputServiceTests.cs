using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class SpellMaterialOutputServiceTests : WandExecutionTransactionTestFixture
    {
        private void Supply(ulong amount, params string[] cards)
        {
            Add(_store, "bag", "bag"); _store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0));
            Add(_store, "source", "bottle"); Assert.That(_store.TryMove("source", Bag()).Success, Is.True);
            _store.TryGet("source", out var source); source.Composition.AddReserved(new[] { new MaterialAmount(1, amount) }, 1);
            var attachments = new SpellAttachmentService(_profile, "scope"); ulong request = 0;
            foreach (string card in cards)
                Assert.That(attachments.Execute(new SpellAttachmentRequestedEvent { InventoryId = "scope", SpellId = card,
                    ContainerId = "source", CallerId = "settings", RequestId = ++request, ExpectedVersion = _store.Version,
                    Dose = 20, Enabled = true }).Success, Is.True);
        }
        private SpellMaterialOutputSnapshot Output(string ticket, int layer = 0, int command = 0)
        { Assert.That(_casts.TryGetMaterialOutput(ticket, layer, command, out var output), Is.True); return output; }
        private ulong Quantity() { _store.TryGet("source", out var source); return source.Composition.Total; }
        private CastResourceRequestedEvent InWorld(CastResourceOperation operation, string ticket = null, int layer = 0)
        { var request = NewRequest(operation, ticket, layer); request.MaterialWorldId = "world"; return request; }

        [Test] public void IndependentOutputsShareActualSupplyAndHaveDistinctOwnership()
        {
            Build(trigger: false, extra: 1, baseDraws: 2); Supply(30, "a", "x0");
            var root = _casts.Execute(InWorld(CastResourceOperation.PrepareRoot)); Assert.That(root.Success, Is.True);
            var a = Output(root.TicketId); var b = Output(root.TicketId, command: 1);
            Assert.That(a.Budget.Total, Is.EqualTo(20)); Assert.That(b.Budget.Total, Is.EqualTo(10)); Assert.That(a.OutputId, Is.Not.EqualTo(b.OutputId));
            Assert.That(Quantity(), Is.EqualTo(30)); Assert.That(_casts.Execute(InWorld(CastResourceOperation.Complete, root.TicketId)).Success, Is.True);
            Assert.That(Quantity(), Is.Zero); Assert.That(_profile.PendingMaterialOutputs, Is.EqualTo(2));
        }
        [Test] public void FutureMaterialsAreNotTakenUntilLayerAndMovingContainerDoesNotUndoPaidSpell()
        {
            Build(); Supply(30, "b"); var root = _casts.Execute(InWorld(CastResourceOperation.PrepareRoot));
            Assert.That(_casts.Execute(InWorld(CastResourceOperation.Complete, root.TicketId)).Success, Is.True);
            Assert.That(Quantity(), Is.EqualTo(30)); Assert.That(State().Mana, Is.EqualTo(83));
            Assert.That(_store.TryMove("source", ItemLocation.Warehouse).Success, Is.True);
            Assert.That(_casts.Execute(InWorld(CastResourceOperation.PrepareLayer, root.TicketId, 1)).Success, Is.True);
            Assert.That(Output(root.TicketId, 1).Budget.Total, Is.Zero);
            Assert.That(_casts.Execute(InWorld(CastResourceOperation.Complete, root.TicketId, 1)).Success, Is.True);
            Assert.That(Quantity(), Is.EqualTo(30)); Assert.That(State().Mana, Is.EqualTo(83)); Assert.That(Uses("b"), Is.EqualTo(2));
        }
        [Test] public void CancelledRootAndAbandonedChildNeverTakeMaterials()
        {
            Build(); Supply(30, "a", "b"); var cancelled = _casts.Execute(InWorld(CastResourceOperation.PrepareRoot));
            _casts.Execute(InWorld(CastResourceOperation.Cancel, cancelled.TicketId)); Assert.That(Quantity(), Is.EqualTo(30));
            var root = _casts.Execute(InWorld(CastResourceOperation.PrepareRoot)); _casts.Execute(InWorld(CastResourceOperation.Complete, root.TicketId));
            Assert.That(Quantity(), Is.EqualTo(10)); _casts.Execute(InWorld(CastResourceOperation.Abandon, root.TicketId, 1));
            Assert.That(Quantity(), Is.EqualTo(10)); Assert.That(_profile.PendingMaterialOutputs, Is.EqualTo(1));
        }
    }
}
