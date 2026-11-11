using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer.Tests
{
    /// <summary>真实Profile字段必须经过正式账号投影保真；不创建Session、切换Hub源或发放资产。</summary>
    public sealed class OnlineAccountWandProjectionTests
    {
        [Test]
        public void ProfileJsonKeepsEveryPersistedFloatAndOrderedTraitInTheAccountView()
        {
            const string json = "{\"InstanceId\":\"stored-wand\",\"DisplayName\":\"同款杖\",\"Area\":\"Warehouse\",\"Slot\":3,"
                + "\"UseTemplateWandStats\":false,\"WandStats\":{\"MaxMana\":207.125,\"ManaRegenPerSecond\":13.625,"
                + "\"BaseCastDelay\":0.3125,\"BaseReloadTime\":1.5625,\"BaseSpreadDegrees\":4.875,"
                + "\"ProgramCapacity\":32,\"BaseDraws\":3,\"Shuffle\":true,\"AlwaysCastContentIds\":[\"second\",\"first\"]}}";
            object view = Project(JsonUtility.FromJson<Shared.OnlineItemReply>(json));
            Assert.That(Read<string>(view, "InstanceId"), Is.EqualTo("stored-wand"));
            Assert.That(Read<string>(view, "Area"), Is.EqualTo("Warehouse"));
            Assert.That(Read<int>(view, "Slot"), Is.EqualTo(3));
            Assert.That(Read<bool>(view, "UseTemplateWandStats"), Is.False);
            object stats = Field(view, "WandStats"); Assert.That(stats, Is.Not.Null);
            Assert.That(Read<float>(stats, "MaxMana"), Is.EqualTo(207.125f));
            Assert.That(Read<float>(stats, "ManaRegenPerSecond"), Is.EqualTo(13.625f));
            Assert.That(Read<float>(stats, "BaseCastDelay"), Is.EqualTo(.3125f));
            Assert.That(Read<float>(stats, "BaseReloadTime"), Is.EqualTo(1.5625f));
            Assert.That(Read<float>(stats, "BaseSpreadDegrees"), Is.EqualTo(4.875f));
            Assert.That(Read<int>(stats, "ProgramCapacity"), Is.EqualTo(32));
            Assert.That(Read<int>(stats, "BaseDraws"), Is.EqualTo(3));
            Assert.That(Read<bool>(stats, "Shuffle"), Is.True);
            Assert.That(Traits(stats), Is.EqualTo(new[] { "second", "first" }));
        }

        [TestCase("{}")]
        [TestCase("{\"WandStats\":null}")]
        [TestCase("{\"WandStats\":{}}")]
        public void LegacyMissingOrUnityDefaultShellNeverClaimsTemplateOrZeroRoll(string json)
        {
            object view = Project(JsonUtility.FromJson<Shared.OnlineItemReply>(json));
            Assert.That(Read<bool>(view, "UseTemplateWandStats"), Is.False);
            Assert.That(Field(view, "WandStats"), Is.Null);
        }

        [Test]
        public void ExplicitLegacyTemplateRemainsTemplateWithoutInventingInstanceProperties()
        {
            object view = Project(JsonUtility.FromJson<Shared.OnlineItemReply>("{\"UseTemplateWandStats\":true,\"WandStats\":null}"));
            Assert.That(Read<bool>(view, "UseTemplateWandStats"), Is.True);
            Assert.That(Field(view, "WandStats"), Is.Null);
        }

        [Test]
        public void PublishedReadViewDoesNotLendMutableHttpPropertiesOrTraitArrays()
        {
            var item = JsonUtility.FromJson<Shared.OnlineItemReply>("{\"WandStats\":{\"MaxMana\":101.25,\"ProgramCapacity\":4,\"BaseDraws\":1,\"AlwaysCastContentIds\":[\"original\"]}}");
            object first = Project(item);
            var field = typeof(Shared.OnlineItemReply).GetField("WandStats"); Assert.That(field, Is.Not.Null);
            var raw = (Shared.OnlineOwnedWandStatsSnapshot)field.GetValue(item);
            raw.MaxMana = 999; raw.AlwaysCastContentIds[0] = "mutated";
            Assert.That(Read<float>(Field(first, "WandStats"), "MaxMana"), Is.EqualTo(101.25f));
            var traits = Traits(Field(first, "WandStats")); Assert.That(traits[0], Is.EqualTo("original"));
            if (traits is IList<string> list) Assert.Throws<NotSupportedException>(() => list[0] = "outside");
        }

        [Test]
        public void ActualAccountPublishUsesTheFullProfileProjectionWithoutSwitchingAssetSource()
        {
            var root = new GameObject("AccountProjectionOnly"); root.SetActive(false);
            OnlineLobbyChangedEvent observed = default; int count = 0;
            Action<OnlineLobbyChangedEvent> listener = value => { observed = value; count++; };
            EventBus<OnlineLobbyChangedEvent>.Subscribe(listener);
            try
            {
                var client = root.AddComponent<OnlineAccountClient>();
                var reply = JsonUtility.FromJson<Shared.OnlineProfileReply>("{\"Items\":[{\"InstanceId\":\"warehouse-roll\",\"Area\":\"Warehouse\",\"WandStats\":{\"MaxMana\":144.375,\"ProgramCapacity\":8,\"BaseDraws\":1}}]}");
                typeof(OnlineAccountClient).GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, reply);
                typeof(OnlineAccountClient).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client, new object[] { "test", false });
                Assert.That(count, Is.EqualTo(1)); Assert.That(observed.Items.Length, Is.EqualTo(1));
                Assert.That(observed.Items[0].InstanceId, Is.EqualTo("warehouse-roll"));
                Assert.That(Read<float>(Field(observed.Items[0], "WandStats"), "MaxMana"), Is.EqualTo(144.375f));
                Assert.That(observed.Items[0].Area, Is.EqualTo("Warehouse"));
            }
            finally { EventBus<OnlineLobbyChangedEvent>.Unsubscribe(listener); UnityEngine.Object.DestroyImmediate(root); }
        }

        private static object Project(Shared.OnlineItemReply item)
        {
            var method = typeof(OnlineOwnedAssetMapping).GetMethod("ToLobbyItem", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "正式Account Publish必须复用完整只读投影，不能只保留名字和位置。");
            return method.Invoke(null, new object[] { item });
        }
        private static object Field(object value, string name)
        {
            Assert.That(value, Is.Not.Null);
            var field = value.GetType().GetField(name); Assert.That(field, Is.Not.Null, name);
            return field.GetValue(value);
        }
        private static T Read<T>(object value, string name) => (T)Field(value, name);
        private static IReadOnlyList<string> Traits(object value)
        {
            var property = value.GetType().GetProperty("AlwaysCastContentIds"); Assert.That(property, Is.Not.Null);
            return (IReadOnlyList<string>)property.GetValue(value);
        }
    }
}
