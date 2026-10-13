using Game.Skills;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class ShopOfferSnapshotTests : InventoryTestFixture
    {
        [TestCase(SpellKind.Emit,false)]
        [TestCase(SpellKind.StaticProjectile,false)]
        [TestCase(SpellKind.Modify,true)]
        [TestCase(SpellKind.Multicast,true)]
        public void OnlyModifiersAndMulticastAdvertiseRaidQualifications(SpellKind kind,bool qualification)
        {
            var definition=Define("spell",ItemKind.Spell);definition.SpellDefinition.Kind=kind;
            var store=Store(definition);
            var offer=new ShopOffer{OfferId="spell",ContentId="spell",InitialCharges=8};
            var view=new ShopOfferSnapshot(offer,3,store.Catalog);
            Assert.That(view.IsQualification,Is.EqualTo(qualification));
        }
    }
}
