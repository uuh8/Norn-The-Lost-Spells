using NUnit.Framework;
using Game.Core;
using Game.Skills;

namespace Game.Run.Tests
{
    public sealed class RaidSpellEligibilityTests : InventoryTestFixture
    {
        private ItemOwnershipStore Build(int charges, out RaidSpellEligibility eligibility, bool installed = true, bool unlimited = false)
        {
            var wand = Define("wand", ItemKind.Wand); var card = Define("mod", ItemKind.Spell); card.SpellDefinition.Kind = SpellKind.Modify;
            var store = Store(wand, card); Add(store, "wand", "wand");
            Assert.That(store.TryMove("wand", new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("card", "mod", charges, unlimited), installed
                ? new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, 0) : ItemLocation.Warehouse), Is.True);
            eligibility = new RaidSpellEligibility(new SinglePlayerProfile("player", store)); return store;
        }
        private static int Charges(ItemOwnershipStore store) { store.TryGet("card", out var item); return item.RemainingCharges; }
        [Test] public void PracticeSceneResumeDoesNotChargeQualifiedCardTwice()
        {
            var store=Build(1,out var first);Assert.That(first.TryBegin("practice",out _),Is.True);
            var remembered=new System.Collections.Generic.List<string>{"card"};
            var clone=RaidProfileOps.Clone(new SinglePlayerProfile("player",store));
            var second=new RaidSpellEligibility(clone);
            var method=typeof(RaidSpellEligibility).GetMethod("TryResumePractice");Assert.That(method,Is.Not.Null);
            Assert.That((bool)method.Invoke(second,new object[]{"practice",remembered}),Is.True);
            Assert.That(second.IsEligible("card"),Is.True);Assert.That(Charges(clone.Items),Is.Zero);
        }
        [Test]
        public void Eligibility_IsExplicitRaidBoundary()
        {
            Assert.That(typeof(ItemOwnershipStore).Assembly.GetType("Game.Run.RaidSpellEligibility"), Is.Not.Null);
        }
        [Test]
        public void LastEntry_IsEligibleAtZeroAndBeginIsIdempotent()
        {
            var store = Build(1, out var raid);
            Assert.That(raid.TryBegin("r1", out _), Is.True); Assert.That(Charges(store), Is.Zero); Assert.That(raid.IsEligible("card"), Is.True);
            uint version = store.Version; Assert.That(raid.TryBegin("r1", out _), Is.True); Assert.That(store.Version, Is.EqualTo(version));
        }
        [Test]
        public void EndClearsEligibilityAndZeroCannotEnterNextRaid()
        {
            var store = Build(1, out var raid); raid.TryBegin("r1", out _); raid.TryEnd(out _);
            Assert.That(raid.IsEligible("card"), Is.False); Assert.That(raid.TryBegin("r2", out var reason), Is.False);
            Assert.That(reason, Is.EqualTo(InventoryFailureReason.InsufficientUses)); Assert.That(raid.RaidId, Is.Null); Assert.That(Charges(store), Is.Zero);
        }
        [Test]
        public void SpareIsChargedOnlyOnFirstSuccessfulInstall()
        {
            var store = Build(2, out var raid, false); raid.TryBegin("r1", out _); Assert.That(Charges(store), Is.EqualTo(2));
            Assert.That(store.TryMove("card", new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, 1)).Success, Is.False);
            Assert.That(Charges(store), Is.EqualTo(2)); Assert.That(raid.IsEligible("card"), Is.False);
            Assert.That(store.TryMove("card", new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(Charges(store), Is.EqualTo(1)); Assert.That(raid.IsEligible("card"), Is.True);
        }
        [Test]
        public void RemovingAndReinstallingDoesNotChargeAgain()
        {
            var store = Build(1, out var raid); raid.TryBegin("r1", out _);
            Assert.That(store.TryMove("card", ItemLocation.Warehouse).Success, Is.True);
            Assert.That(store.TryMove("card", new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(Charges(store), Is.Zero); Assert.That(raid.IsEligible("card"), Is.True);
        }
        [Test]
        public void ExhaustedSpareCannotInstallAndKeepsOwnership()
        {
            var store = Build(0, out var raid, false); raid.TryBegin("r1", out _); uint version = store.Version;
            var result = store.TryMove("card", new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, 0));
            Assert.That(result.Reason, Is.EqualTo(InventoryFailureReason.InsufficientUses)); Assert.That(store.Version, Is.EqualTo(version)); At(store, "card", ItemLocation.Warehouse);
        }
        [Test]
        public void UnlimitedQualificationNeverLosesCharges()
        {
            var store = Build(0, out var raid, unlimited: true); Assert.That(raid.TryBegin("r1", out _), Is.True);
            Assert.That(raid.IsEligible("card"), Is.True); Assert.That(Charges(store), Is.Zero);
        }
        [Test]
        public void DifferentRaidCannotReplaceActiveEligibility()
        {
            var store = Build(2, out var raid); raid.TryBegin("r1", out _);
            Assert.That(raid.TryBegin("r2", out _), Is.False); Assert.That(raid.RaidId, Is.EqualTo("r1")); Assert.That(Charges(store), Is.EqualTo(1));
        }
        [Test]
        public void MultipleInstalledCardsFailAtomically()
        {
            var store = Build(2, out var raid);
            Add(store, "wand2", "wand"); store.TryMove("wand2", new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 1));
            store.TryAdd(new RuntimeItemState("empty", "mod", 0), new ItemLocation(ItemLocationKind.WandSlot, "wand2", 0, 0, 0, 0));
            uint version = store.Version; Assert.That(raid.TryBegin("r1", out _), Is.False);
            Assert.That(Charges(store), Is.EqualTo(2)); Assert.That(store.Version, Is.EqualTo(version)); Assert.That(raid.IsEligible("card"), Is.False);
        }
    }
}
