using System.Reflection;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>真实 JsonUtility 边界：inline 普通类的空壳不能伪装成法杖属性，也不能丢掉真正的非法属性。</summary>
    public sealed class OnlineOwnedAssetMappingTests
    {
        [Test] public void EntryBoundBackpackMarkerSurvivesSharedToCoreMapping()
        {
            var item = new OnlineOwnedItemSnapshot(); var field = item.GetType().GetField("EntryBoundBackpack");
            Assert.That(field, Is.Not.Null, "保底包资格由业务服冻结传入，不能由Client命令或模板身份重造。");
            field.SetValue(item, true);
            var mapped = Map(new OnlineOwnedAssetSnapshot { Items = new[] { item } }).Items[0];
            var local = mapped.GetType().GetField("EntryBoundBackpack"); Assert.That(local, Is.Not.Null);
            Assert.That(local.GetValue(mapped), Is.EqualTo(true));
        }

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

        [Test]
        public void SpellAttachmentMapsExactDoseAndIndependentStateWithoutRequiringLiveSource()
        {
            var state = new OnlineOwnedSpellAttachmentStateSnapshot { SchemaVersion = 1,
                ContainerId = new string('d', 32), Dose = 9007199254740993ul, Enabled = true };
            var source = new OnlineOwnedAssetSnapshot { Items = new[] { new OnlineOwnedItemSnapshot { AttachmentState = state } } };
            var mapped = Map(source).Items[0].AttachmentState;
            Assert.That(mapped, Is.Not.Null);
            Assert.That(mapped.ContainerId, Is.EqualTo(new string('d', 32)));
            Assert.That(mapped.Dose, Is.EqualTo(9007199254740993ul));
            state.Dose = 1; Assert.That(mapped.Dose, Is.EqualTo(9007199254740993ul));
            mapped.Enabled = false; Assert.That(state.Enabled, Is.True);
        }

        [Test]
        public void SpellAttachmentDefaultJsonShellIsAbsentButExplicitInvalidSettingIsNotDiscarded()
        {
            var source = new OnlineOwnedAssetSnapshot { Items = new[] {
                new OnlineOwnedItemSnapshot { AttachmentState = new OnlineOwnedSpellAttachmentStateSnapshot() },
                new OnlineOwnedItemSnapshot { AttachmentState = new OnlineOwnedSpellAttachmentStateSnapshot { SchemaVersion = 2 } } } };
            var mapped = Map(source);
            Assert.That(mapped.Items[0].AttachmentState, Is.Null);
            Assert.That(mapped.Items[1].AttachmentState, Is.Not.Null);
            Assert.That(mapped.Items[1].AttachmentState.SchemaVersion, Is.EqualTo(2));
        }

        [Test]
        public void MaterialStateIsDeepCopiedWithoutLosingUnsignedPrecision()
        {
            var state = new OnlineOwnedMaterialStateSnapshot { SchemaVersion = 1, MaterialKey = 5, Quantity = 9007199254740993ul };
            var source = new OnlineOwnedAssetSnapshot { Items = new[] { new OnlineOwnedItemSnapshot { MaterialState = state } } };
            var mapped = Map(source); Assert.That(mapped.Items[0].MaterialState, Is.Not.Null);
            Assert.That(mapped.Items[0].MaterialState.Quantity, Is.EqualTo(9007199254740993ul));
            state.Quantity = 1;
            Assert.That(mapped.Items[0].MaterialState.Quantity, Is.EqualTo(9007199254740993ul));
            mapped.Items[0].MaterialState.MaterialKey = 6;
            Assert.That(state.MaterialKey, Is.EqualTo(5));
        }

        [Test]
        public void MaterialInlineShellIsAbsentButExplicitInvalidStateStillReachesTheFactory()
        {
            var parsed = JsonUtility.FromJson<OnlineOwnedAssetSnapshot>("{\"Items\":[{\"MaterialState\":null},{\"MaterialState\":{\"SchemaVersion\":0,\"MaterialKey\":5,\"Quantity\":7}},{\"MaterialState\":{\"SchemaVersion\":2}},{\"MaterialState\":{\"SchemaVersion\":1,\"MaterialKey\":5,\"Quantity\":0}}]}");
            parsed = JsonUtility.FromJson<OnlineOwnedAssetSnapshot>(JsonUtility.ToJson(parsed));
            var mapped = Map(parsed);
            Assert.That(mapped.Items[0].MaterialState, Is.Null);
            Assert.That(mapped.Items[1].MaterialState, Is.Not.Null, "非法非默认状态不能被映射静默吞掉。");
            Assert.That(mapped.Items[1].MaterialState.Quantity, Is.EqualTo(7ul));
            Assert.That(mapped.Items[2].MaterialState.SchemaVersion, Is.EqualTo(2));
            Assert.That(mapped.Items[3].MaterialState.Quantity, Is.Zero, "零量壳保留到Factory拒绝，不能补成容量。");
        }

        [Test]
        public void ContainerCompositionIsDeepCopiedWithExactUnsignedQuantities()
        {
            var part = new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 9007199254740993ul };
            var source = new OnlineOwnedAssetSnapshot { SchemaVersion = 2, Items = new[] { new OnlineOwnedItemSnapshot
                { ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = new[] { part } } } } };
            var mapped = Map(source);
            Assert.That(mapped.Items[0].ContainerState, Is.Not.Null);
            Assert.That(mapped.Items[0].ContainerState.Components[0].Quantity, Is.EqualTo(9007199254740993ul));
            part.Quantity = 8;
            Assert.That(mapped.Items[0].ContainerState.Components[0].Quantity, Is.EqualTo(9007199254740993ul));
            mapped.Items[0].ContainerState.Components[0].MaterialKey = 3;
            Assert.That(part.MaterialKey, Is.EqualTo(1));
        }

        [Test]
        public void ExplicitEmptyBottleRemainsPresentAcrossUnityJsonRoundTrip()
        {
            var parsed = JsonUtility.FromJson<OnlineOwnedAssetSnapshot>("{\"Items\":[{\"ContainerState\":{\"SchemaVersion\":1,\"Components\":[]}}]}");
            parsed = JsonUtility.FromJson<OnlineOwnedAssetSnapshot>(JsonUtility.ToJson(parsed));
            Assert.That(Map(parsed).Items[0].ContainerState, Is.Not.Null);
            Assert.That(Map(parsed).Items[0].ContainerState.SchemaVersion, Is.EqualTo(1));
            Assert.That(Map(parsed).Items[0].ContainerState.Components, Is.Empty);
        }

        [Test]
        public void MissingComponentsStayUnknownAndJsonUnsignedQuantitiesStayExact()
        {
            var parsed = JsonUtility.FromJson<OnlineOwnedAssetSnapshot>("{\"Items\":[{\"ContainerState\":{\"SchemaVersion\":1}},{\"ContainerState\":{\"SchemaVersion\":1,\"Components\":[{\"MaterialKey\":1,\"Quantity\":9007199254740993}]}}]}");
            var mapped = Map(parsed);
            Assert.That(mapped.Items[0].ContainerState.Components, Is.Null, "缺字段不能被补成已知空瓶。");
            Assert.That(mapped.Items[1].ContainerState.Components[0].Quantity, Is.EqualTo(9007199254740993ul));
        }

        [Test]
        public void InlineDefaultShellIsAbsentButInvalidNonDefaultStateIsRetained()
        {
            var parsed = JsonUtility.FromJson<OnlineOwnedAssetSnapshot>("{\"Items\":[{\"ContainerState\":null},{\"ContainerState\":{\"SchemaVersion\":0,\"Components\":[{\"MaterialKey\":1,\"Quantity\":9}]}},{\"ContainerState\":{\"SchemaVersion\":2,\"Components\":null}}]}");
            parsed = JsonUtility.FromJson<OnlineOwnedAssetSnapshot>(JsonUtility.ToJson(parsed));
            var mapped = Map(parsed);
            Assert.That(mapped.Items[0].ContainerState, Is.Null);
            Assert.That(mapped.Items[1].ContainerState, Is.Not.Null, "非法非默认状态必须交给Factory拒绝，不能静默丢弃。");
            Assert.That(mapped.Items[1].ContainerState.Components[0].Quantity, Is.EqualTo(9ul));
            Assert.That(mapped.Items[2].ContainerState.SchemaVersion, Is.EqualTo(2));
        }
    }
}
