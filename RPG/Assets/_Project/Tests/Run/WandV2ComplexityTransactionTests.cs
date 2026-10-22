using Game.Core;
using Game.Skills;
using NUnit.Framework;
namespace Game.Run.Tests
{
    public sealed class WandV2ComplexityTransactionTests:WandExecutionTransactionTestFixture
    {
        [Test]public void Legal32SlotWandRejectsSeventeenthTriggerLayerAtomically()
        {
            Build(extra:15,capacity:32,cost:0);_store.Catalog.TryGet("payload",out var definition);definition.SpellDefinition.PayloadTrigger=PayloadTriggerMode.OnImpact;definition.SpellDefinition.ManaCost=0;
            var before=State();uint inventory=_store.Version;
            var result=Request(CastResourceOperation.PrepareRoot);
            Assert.That(result.Success,Is.False);Assert.That(result.Reason,Is.EqualTo(OwnedWandFailureReason.TooComplex));
            Assert.That(State().Draw,Is.SameAs(before.Draw));Assert.That(State().Mana,Is.EqualTo(100));Assert.That(_store.Version,Is.EqualTo(inventory));Assert.That(Uses("a"),Is.EqualTo(3));Assert.That(_casts.Archive.Count,Is.Zero);Assert.That(_casts.IsPending,Is.False);
        }
    }
}
