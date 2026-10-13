using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class StartingInventoryCurrencyTests : InventoryTestFixture
    {
        [TestCase(10000UL,true)] [TestCase(15001UL,false)] [TestCase(0UL,false)]
        public void InitialContainerMaterialsRespectCapacityAndDoNotInventInvalidAmounts(ulong quantity,bool valid)
        {
            var bottle=Define("bottle",ItemKind.Container);var definition=new SerializedObject(bottle);definition.FindProperty("_containerCapacityGmu").longValue=15000;definition.ApplyModifiedPropertiesWithoutUndo();
            var seeds=Track(ScriptableObject.CreateInstance<SinglePlayerStartingInventory>());var data=new SerializedObject(seeds);var entries=data.FindProperty("_items");entries.arraySize=1;
            var entry=entries.GetArrayElementAtIndex(0);entry.FindPropertyRelative("Definition").objectReferenceValue=bottle;entry.FindPropertyRelative("Location").intValue=(int)ItemLocationKind.Warehouse;
            var contents=entry.FindPropertyRelative("Materials");contents.arraySize=1;contents.GetArrayElementAtIndex(0).FindPropertyRelative("MaterialKey").intValue=1;contents.GetArrayElementAtIndex(0).FindPropertyRelative("Quantity").ulongValue=quantity;data.ApplyModifiedPropertiesWithoutUndo();
            var catalog=Store(bottle).Catalog;Assert.That(seeds.TryCreate(catalog,out var profile,out _),Is.EqualTo(valid));
            if(!valid)return;foreach(var item in profile.Items)Assert.That(item.Item.Composition.Total,Is.EqualTo(quantity));
            contents.GetArrayElementAtIndex(0).FindPropertyRelative("Quantity").ulongValue=20;data.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(seeds.TryCreate(catalog,out var next,out _),Is.True);foreach(var item in profile.Items)Assert.That(item.Item.Composition.Total,Is.EqualTo(quantity),"修改新档模板不能改旧实例");
            foreach(var item in next.Items)Assert.That(item.Item.Composition.Total,Is.EqualTo(20));
        }
        [Test] public void ProductionNewProfileHasLargeTestContentsAndMaterialSupply()
        {
            const string root="Assets/_Project/ScriptableObjects/SinglePlayer/Hub/";
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>(root+"SinglePlayerRaids/Catalog.asset");Assert.That(catalog.TryCreateIndex(out var index,out _),Is.True);
            var seeds=AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>(root+"SinglePlayerRaids/StartingInventory.asset");Assert.That(seeds.TryCreate(index,out var profile,out _),Is.True);int bottles=0;
            foreach(var item in profile.Items)if(item.Item.Composition!=null){bottles++;Assert.That(item.Item.Composition.Total,Is.EqualTo(item.Item.Composition.Capacity*2/3));}
            Assert.That(bottles,Is.GreaterThan(0));var shop=AssetDatabase.LoadAssetAtPath<ShopDefinition>(root+"SinglePlayerTrading/Shop.asset");foreach(var offer in shop.Offers)if(offer.IsMaterial)Assert.That(offer.Stock,Is.EqualTo(60000));
        }
        [TestCase(0L, true)]
        [TestCase(5000L, true)]
        [TestCase(-1L, false)]
        public void InitialCurrencyIsSeededAndNegativeValuesAreRejected(long amount, bool valid)
        {
            var seeds = Track(ScriptableObject.CreateInstance<SinglePlayerStartingInventory>());
            var serialized = new SerializedObject(seeds);
            var currency = serialized.FindProperty("_initialCurrency");
            Assert.That(currency, Is.Not.Null, "缺少可配置的初始测试金币");
            currency.longValue = amount;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var catalog = Store(Define("apple", ItemKind.Consumable)).Catalog;
            Assert.That(seeds.TryCreate(catalog, out var profile, out _), Is.EqualTo(valid));
            if (valid) Assert.That(profile.Currency, Is.EqualTo(amount));
        }
    }
}
