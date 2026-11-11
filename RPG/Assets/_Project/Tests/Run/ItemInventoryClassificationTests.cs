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
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            var entries=new SerializedObject(catalog).FindProperty("_definitions");
            int food=0,herbs=0,tools=0;var report=new StringBuilder("ContentId\tName\tKind\tDisplayCategory\n");
            for(int i=0;i<entries.arraySize;i++)
            {
                var definition=(ItemDefinition)entries.GetArrayElementAtIndex(i).objectReferenceValue;
                bool tool=ItemInventoryClassification.IsTool(definition.Kind,definition.UseKind);
                bool material=ItemInventoryClassification.IsMaterial(definition.Kind) && !tool;
                // 已确认按 UseKind 分类，而非把道具硬编码成 Container；冰仍是 Material，
                // 但 Throw 是实际可用能力。法术、法杖、背包继续各有独立入口，不混入道具位。
                if(definition.Kind==ItemKind.Spell || definition.Kind==ItemKind.Wand || definition.Kind==ItemKind.Backpack)
                    Assert.That(tool,Is.False,definition.ContentId+"有独立入口，不能混入4–0");
                else if(definition.UseKind==ItemUseKind.None)
                    Assert.That(tool,Is.False,definition.ContentId+"没有实际使用能力，不能放入4–0");
                else
                {
                    Assert.That(definition.UseKind,Is.EqualTo(ItemUseKind.Container).Or.EqualTo(ItemUseKind.Throw),definition.ContentId);
                    Assert.That(tool,Is.True,definition.ContentId+"的实际使用能力应进入道具分类");
                    tools++;
                }
                if(definition.Kind==ItemKind.Container)Assert.That(tool,Is.True,definition.ContentId);
                // Food/Herb 保留原资源 Kind；已有 Throw 能力时，道具主类优先，不能同时
                // 在材料目录重复出现。没有使用能力时仍必须回到材料类，而非无条件排除资源。
                if(definition.Category==ItemCategory.Food)
                {
                    Assert.That(definition.Kind,Is.EqualTo(ItemKind.Consumable),definition.ContentId+"的底层食物Kind不能随显示分类变化");
                    Assert.That(material,Is.EqualTo(!tool),definition.ContentId);food++;
                }
                if(definition.Category==ItemCategory.Herb)
                {
                    Assert.That(definition.Kind,Is.EqualTo(ItemKind.Alchemy),definition.ContentId+"的底层草药Kind不能随显示分类变化");
                    Assert.That(material,Is.EqualTo(!tool),definition.ContentId);herbs++;
                }
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
