using System.Reflection;
using Game.Multiplayer;
using NUnit.Framework;
using Core = Game.Core;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer.Tests
{
    public sealed class UnifiedGoldMappingTests
    {
        [TestCase(0L)] [TestCase(10000L)] [TestCase(long.MaxValue)]
        public void ExplicitControlMappingPreservesGoldAndCurrencyRevisionBothWays(long gold)
        {
            var source = new Shared.OnlineOwnedAssetSnapshot(); Set(source, "Gold", gold); Set(source, "CurrencyVersion", 9u);
            var local = OnlineOwnedAssetMapping.ToCore(source);
            Assert.That(Get<long>(local, "Gold"), Is.EqualTo(gold));
            Assert.That(Get<uint>(local, "CurrencyVersion"), Is.EqualTo(9));
            var restored = OnlineOwnedAssetMapping.ToShared(local);
            Assert.That(Get<long>(restored, "Gold"), Is.EqualTo(gold));
            Assert.That(Get<uint>(restored, "CurrencyVersion"), Is.EqualTo(9));
        }

        [Test] public void FrozenProfileCannotDisagreeWithCombatBalance()
        {
            var assets = new Shared.OnlineOwnedAssetSnapshot(); Set(assets, "Gold", 500L); Set(assets, "CurrencyVersion", 1u);
            var ready = new Shared.InstanceReadyReply { Members = new[]
            {
                new Shared.InstanceEntryMember { ActorId = 1, Profile = new Shared.OnlineProfileReply { PlayerId = "one", Gold = 10000, ContentManifestHash = "manifest", CombatAssets = assets } },
                new Shared.InstanceEntryMember { ActorId = 2, Profile = new Shared.OnlineProfileReply { PlayerId = "two", Gold = 0, ContentManifestHash = "manifest", CombatAssets = assets } }
            } };
            Assert.Throws<System.ArgumentException>(() => new OnlineInventorySession(ready, "manifest"),
                "完整可信Profile与执行闭包币值必须交叉验证，不允许两套余额。");
        }

        private static void Set(object value, string name, object fieldValue)
        {
            var field = value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, "新增统一余额合同缺字段：" + name); field.SetValue(value, fieldValue);
        }
        private static T Get<T>(object value, string name)
        {
            var field = value.GetType().GetField(name); Assert.That(field, Is.Not.Null, name); return (T)field.GetValue(value);
        }
    }
}
