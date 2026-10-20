using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class LegacyWandLayoutMigrationTests : InventoryTestFixture
    {
        private SinglePlayerSaveDto Legacy(out ItemCatalogIndex expanded, int bagWidth=4)
        {
            var bag=Define("bag",ItemKind.Backpack,bagWidth:bagWidth,bagHeight:1);
            var wand=Define("wand",ItemKind.Wand);var old=Store(bag,wand);
            Assert.That(old.TryAdd(new RuntimeItemState("bag","bag",0),new ItemLocation(ItemLocationKind.EquippedBackpack,"actor",0,0,0,0)),Is.True);
            Assert.That(old.TryAdd(new RuntimeItemState("a","wand",0),Bag(0)),Is.True);
            Assert.That(old.TryAdd(new RuntimeItemState("b","wand",0),Bag(1)),Is.True);
            var dto=ProfileSaveMapper.ToDto(new SinglePlayerProfile("actor",old,currency:77),0);dto.SchemaVersion=11;
            var data=new SerializedObject(wand);data.FindProperty("_shapeWidth").intValue=3;
            var cells=data.FindProperty("_occupiedCells");cells.arraySize=3;
            for(int i=0;i<3;i++)cells.GetArrayElementAtIndex(i).vector2IntValue=new Vector2Int(i,0);
            data.ApplyModifiedPropertiesWithoutUndo();expanded=Store(bag,wand).Catalog;return dto;
        }
        [Test]public void OldSingleCellWandsReflowWithoutLosingIdsOrCurrency()
        {
            var dto=Legacy(out var catalog,6);
            Assert.That(ProfileSaveMapper.TryRestore(dto,catalog,out var restored).Success,Is.True);
            Assert.That(restored.Currency,Is.EqualTo(77));Assert.That(restored.Items.Count,Is.EqualTo(3));
            At(restored.Items,"a",Bag(0));At(restored.Items,"b",Bag(3));
        }
        [Test]public void OldFullBagKeepsSecondWandInWarehouse()
        {
            var dto=Legacy(out var catalog);
            Assert.That(ProfileSaveMapper.TryRestore(dto,catalog,out var restored).Success,Is.True);
            At(restored.Items,"a",Bag(0));At(restored.Items,"b",ItemLocation.Warehouse);
        }
        [Test]public void NewSchemaOrAlreadyInvalidOldLayoutIsNotRepaired()
        {
            var dto=Legacy(out var catalog);dto.SchemaVersion=12;
            Assert.That(ProfileSaveMapper.TryRestore(dto,catalog,out _).Success,Is.False);
            dto.SchemaVersion=11;dto.Items[2].X=0;
            Assert.That(ProfileSaveMapper.TryRestore(dto,catalog,out _).Success,Is.False);
        }
        [Test]public void ProductionWandsHaveThreeHorizontalCells()
        {
            foreach(var name in new[]{"Item_Wand_StarterA","Item_Wand_StarterB","Item_Wand_StarterC"})
            {
                var wand=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Wands/Items/"+name+".asset");
                Assert.That(wand.TryCreateRules(out var rules,out var error),Is.True,error);
                Assert.That(rules.Shape.Width,Is.EqualTo(3));Assert.That(rules.Shape.Height,Is.EqualTo(1));Assert.That(rules.Shape.CellCount,Is.EqualTo(3));
            }
        }
    }
}
