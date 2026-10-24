using System.Reflection;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>真实 JsonUtility 边界：inline 普通类的空壳不能伪装成法杖属性，也不能丢掉真正的非法属性。</summary>
    public sealed class OnlineOwnedAssetMappingTests
    {
        private static Game.Core.OnlineOwnedAssetSnapshot Map(OnlineOwnedAssetSnapshot source)
        {
            var type = typeof(OnlineCombatWorld).Assembly.GetType("Game.Multiplayer.OnlineOwnedAssetMapping");
            Assert.That(type, Is.Not.Null, "Shared→Core 不得用 JsonUtility 二次转换丢失 nullable 语义。");
            return (Game.Core.OnlineOwnedAssetSnapshot)type.GetMethod("ToCore", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { source });
        }
        [Test]
        public void UnityInlineEmptyStatsShellMapsBackToAbsentStats()
        {
            var parsed = JsonUtility.FromJson<OnlineOwnedAssetSnapshot>("{\"SchemaVersion\":1,\"Items\":[{\"LocationKind\":2,\"WandStats\":null}]}");
            // 再经过 Unity 序列化时，null 普通类会被展开为空值结构；原错误就发生在这条实际路径。
            parsed = JsonUtility.FromJson<OnlineOwnedAssetSnapshot>(JsonUtility.ToJson(parsed));
            Assert.That(Map(parsed).Items[0].WandStats, Is.Null);
        }
        [Test]
        public void NonDefaultSpellStatsRemainPresentForFactoryToReject()
        {
            var source = new OnlineOwnedAssetSnapshot { Items = new[] { new OnlineOwnedItemSnapshot
                { LocationKind = 2, WandStats = new OnlineOwnedWandStatsSnapshot { MaxMana = 99 } } } };
            Assert.That(Map(source).Items[0].WandStats.MaxMana, Is.EqualTo(99));
        }
        [Test]
        public void ExplicitWandStatsAndArrayAreCopiedWithoutChangingSchemaDefault()
        {
            var source = new OnlineOwnedAssetSnapshot { Items = new[] { new OnlineOwnedItemSnapshot
                { LocationKind = 3, WandStats = new OnlineOwnedWandStatsSnapshot { MaxMana = 100, ProgramCapacity = 8,
                    AlwaysCastContentIds = new[] { "fire" } } } } };
            var mapped = Map(source);
            Assert.That(mapped.SchemaVersion, Is.Zero);
            Assert.That(mapped.Items[0].WandStats.ProgramCapacity, Is.EqualTo(8));
            mapped.Items[0].WandStats.AlwaysCastContentIds[0] = "other";
            Assert.That(source.Items[0].WandStats.AlwaysCastContentIds[0], Is.EqualTo("fire"));
        }
    }
}
