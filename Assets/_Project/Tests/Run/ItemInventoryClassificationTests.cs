using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;

namespace Game.Run.Tests
{
    public sealed class ItemInventoryClassificationTests
    {
        [Test]
        public void EveryCurrentHubDefinitionHasTheExpectedCategoryWithoutChangingKind()
        {
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerRaids/Catalog.asset");
            var entries=new SerializedObject(catalog).FindProperty("_definitions");
            int food=0,herbs=0,tools=0;var report=new StringBuilder("ContentId\tName\tKind\tDisplayCategory\n");
            for(int i=0;i<entries.arraySize;i++)
            {
                var definition=(ItemDefinition)entries.GetArrayElementAtIndex(i).objectReferenceValue;
                bool tool=ItemInventoryClassification.IsTool(definition.Kind,definition.UseKind);
                bool material=ItemInventoryClassification.IsMaterial(definition.Kind) && !tool;
                if(definition.Kind==ItemKind.Container){Assert.That(tool,Is.True);tools++;}
                else Assert.That(tool,Is.False,definition.ContentId+"不能放入4–0");
                if(definition.Category==ItemCategory.Food){Assert.That(material,Is.True,definition.ContentId);food++;}
                if(definition.Category==ItemCategory.Herb){Assert.That(material,Is.True,definition.ContentId);herbs++;}
                if(definition.Kind==ItemKind.Spell || definition.Kind==ItemKind.Wand || definition.Kind==ItemKind.Backpack)
                    Assert.That(material,Is.False,definition.ContentId);
                report.AppendLine(definition.ContentId+"\t"+definition.DisplayName+"\t"+definition.Kind+"\t"+(tool?"道具":material?"材料":definition.Kind.ToString()));
            }
            Assert.That(food,Is.EqualTo(10));Assert.That(herbs,Is.EqualTo(5));Assert.That(tools,Is.GreaterThanOrEqualTo(3));
            Directory.CreateDirectory("TestResults/SinglePlayer/M6-T3-ItemCarry");
            File.WriteAllText("TestResults/SinglePlayer/M6-T3-ItemCarry/catalog-classification.tsv",report.ToString());
        }
    }
}
