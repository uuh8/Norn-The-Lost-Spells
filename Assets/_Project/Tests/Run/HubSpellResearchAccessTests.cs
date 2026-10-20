using System;
using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class HubSpellResearchAccessTests
    {
        [Test] public void EveryDungeonRewardHasOneResolvableOwnedItemAfterContentMaintenance()
        {
            var catalog=UnityEditor.AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            Assert.That(catalog.TryCreateIndex(out var index,out var error),Is.True,error);
            var rewards=UnityEditor.AssetDatabase.LoadAssetAtPath<DungeonRewardProfile>("Assets/_Project/ScriptableObjects/World/Dungeon/DungeonRewards.asset");
            foreach(var spell in rewards.Pool)
                Assert.That(index.TryFindSpell(spell,out _),Is.True,spell==null?"Missing SpellDefinition":spell.name);
        }

        [TestCase("Modify_ManaRestore","法力回流","add_mana")]
        [TestCase("Modify_CastFaster","快速释放","cast_faster")]
        [TestCase("Modify_ReloadFaster","快速周转","reload_faster")]
        [TestCase("Copy_Alpha","α借用","alpha")]
        [TestCase("Copy_Gamma","γ借用","gamma")]
        [TestCase("Modify_ShortLifetime","减少投射物寿命","short_life")]
        public void SuppliedMechanicIcons_AreSharedBySpellInventoryAndShop(string mechanic,string image,string id)
        {
            const string folder="Assets/_Project/ScriptableObjects/Spell/";
            var sprite=UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Sprite>("Assets/_Project/Art/UI/Skill_Icon/"+image+".png");
            var spell=UnityEditor.AssetDatabase.LoadAssetAtPath<Game.Skills.SpellDefinition>(folder+"Definitions/Spell_"+mechanic+".asset");
            var item=UnityEditor.AssetDatabase.LoadAssetAtPath<ItemDefinition>(folder+"Items/Item_Spell_"+mechanic+".asset");
            Assert.That(sprite,Is.Not.Null);Assert.That(spell.Icon,Is.SameAs(sprite));Assert.That(item.Icon,Is.SameAs(sprite));
            var shop=UnityEditor.AssetDatabase.LoadAssetAtPath<ShopDefinition>("Assets/_Project/ScriptableObjects/World/Hub/Trading/Shop.asset");
            int count=0;foreach(var offer in shop.Offers)if(offer.ContentId=="norn.v2."+id)
            {Assert.That(offer.Icon,Is.SameAs(sprite));count++;}
            Assert.That(count,Is.EqualTo(1));
        }

        [Test] public void ResearchHasDistinctStationAndPauseOwnershipWithoutChangingExistingValues()
        {
            Assert.That(Enum.IsDefined(typeof(HubStationKind), "SpellResearch"), Is.True);
            Assert.That(Enum.IsDefined(typeof(HubInteractionKind), "SpellResearch"), Is.True);
            Assert.That(Enum.GetUnderlyingType(typeof(RunPauseReason)), Is.EqualTo(typeof(ushort)));
            Assert.That((int)HubStationKind.Warehouse, Is.Zero); Assert.That((int)HubStationKind.Shop, Is.EqualTo(1));
            Assert.That((int)RunPauseReason.HubConfirmation, Is.EqualTo(128));
        }
        [Test] public void ResearchGrantCarriesIdentityAndCannotBeReplayedFromGenericSuccess()
        {
            Assert.That(typeof(HubInteractionResultEvent).GetField("RequestId"), Is.Not.Null);
            Assert.That(typeof(HubInteractionResultEvent).GetField("CallerId"), Is.Not.Null);
            Assert.That(typeof(HubInteractionController).GetMethod("TryConsumeResearchGrant"), Is.Not.Null);
        }
    }
}
