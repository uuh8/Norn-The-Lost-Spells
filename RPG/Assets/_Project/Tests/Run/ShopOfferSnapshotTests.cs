using System;
using System.Reflection;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;

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

        [Test]
        public void AccountShopSnapshotUsesConfirmedStockAndWalletWithOriginalQuotePresentation()
        {
            var fixture = AccountFixture();
            Assert.That(CreateAccountRead(fixture.Catalog, fixture.Configuration, fixture.Snapshot, out var read, out var error), Is.True, error);
            Assert.That(read.InventoryId, Is.EqualTo("account-inventory")); Assert.That(read.Version, Is.EqualTo(12L));
            Assert.That(read.Currency, Is.EqualTo(123L)); Assert.That(read.RemainingBudget, Is.EqualTo(40L));
            Assert.That(read.ProgressUntilRefresh, Is.EqualTo(2L)); Assert.That(read.Count, Is.EqualTo(2));
            var spell = read.GetOffer(0);
            Assert.That(spell.OfferId, Is.EqualTo("spell-offer")); Assert.That(spell.ContentId, Is.EqualTo("spell"));
            Assert.That(spell.Stock, Is.EqualTo(3UL)); Assert.That(spell.BuyPrice, Is.EqualTo(10L)); Assert.That(spell.SellPrice, Is.EqualTo(4L));
            Assert.That(spell.InitialCharges, Is.EqualTo(8)); Assert.That(spell.IsSpell, Is.True); Assert.That(spell.IsQualification, Is.False);
            Assert.That(read.GetOffer(1).Icon, Is.SameAs(fixture.MaterialIcon), "材料报价仍使用原ShopDefinition的图标。");
            Assert.That(Read(read, "PlayerId"), Is.EqualTo("account-player")); Assert.That(Read(read, "InventoryVersion"), Is.EqualTo(9u));
            Assert.That(Read(read, "ConfigHash"), Is.EqualTo(new string('a', 64)));
            Assert.That(Read(read, "SceneHandle"), Is.EqualTo(42)); Assert.That(Read(read, "ShopId"), Is.EqualTo("account-shop"));
        }

        [TestCase("ContentId", "different-content")]
        [TestCase("MaterialKey", (byte)1)]
        [TestCase("BuyPrice", 11L)]
        [TestCase("SellPrice", 3L)]
        [TestCase("InitialCharges", 9)]
        [TestCase("Unlimited", true)]
        [TestCase("InitialMaterialQuantity", 2UL)]
        [TestCase("InitialStock", 11UL)]
        public void AccountShopSnapshotRejectsEveryMismatchedAuthoritativeQuoteField(string field, object value)
        {
            var fixture = AccountFixture(); Set(Stocks(fixture.Snapshot).GetValue(0), field, value);
            Assert.That(CreateAccountRead(fixture.Catalog, fixture.Configuration, fixture.Snapshot, out var read, out var error), Is.False,
                "服务器报价和当前冻结ShopDefinition不符时，不能继续按客户端旧报价成交。");
            Assert.That(read, Is.Null); Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("missing")]
        [TestCase("duplicate")]
        [TestCase("unknown")]
        [TestCase("overstock")]
        public void AccountShopSnapshotRequiresAnExactUniqueStockClosure(string corruption)
        {
            var fixture = AccountFixture(); var stocks = Stocks(fixture.Snapshot);
            if (corruption == "missing")
            {
                var incomplete = Array.CreateInstance(stocks.GetType().GetElementType(), 1); incomplete.SetValue(stocks.GetValue(0), 0);
                Set(fixture.Snapshot, "Stocks", incomplete);
            }
            else if (corruption == "duplicate") stocks.SetValue(stocks.GetValue(0), 1);
            else if (corruption == "unknown") Set(stocks.GetValue(1), "OfferId", "unknown-offer");
            else Set(stocks.GetValue(0), "Remaining", 11UL);
            Assert.That(CreateAccountRead(fixture.Catalog, fixture.Configuration, fixture.Snapshot, out var read, out _), Is.False);
            Assert.That(read, Is.Null, "缺失/重复/未知/超出初始量的Stock必须整批拒绝，不能发布部分商品。");
        }

        [TestCase("PlayerId", "")]
        [TestCase("InventoryId", "")]
        [TestCase("ShopId", "wrong-shop")]
        [TestCase("ConfigHash", "unverified")]
        [TestCase("InventoryVersion", 0u)]
        [TestCase("ShopVersion", 0L)]
        [TestCase("Gold", -1L)]
        [TestCase("RemainingBudget", -1L)]
        [TestCase("ProgressUntilRefresh", -1L)]
        public void AccountShopSnapshotRejectsMissingIdentityVersionsAndInvalidBalances(string field, object value)
        {
            var fixture = AccountFixture(); Set(fixture.Snapshot, field, value);
            Assert.That(CreateAccountRead(fixture.Catalog, fixture.Configuration, fixture.Snapshot, out var read, out _), Is.False);
            Assert.That(read, Is.Null);
        }

        [Test]
        public void AccountShopReadModelDoesNotRetainMutableSnapshotOrQuoteAliases()
        {
            var fixture = AccountFixture();
            Assert.That(CreateAccountRead(fixture.Catalog, fixture.Configuration, fixture.Snapshot, out var read, out var error), Is.True, error);
            Set(Stocks(fixture.Snapshot).GetValue(0), "Remaining", 0UL);
            Set(Stocks(fixture.Snapshot).GetValue(0), "BuyPrice", 999L);
            Set(fixture.Snapshot, "Gold", 999L); Set(fixture.Snapshot, "PlayerId", "another-player");
            fixture.Configuration.At(0).BuyPrice = 999L;
            Assert.That(read.GetOffer(0).Stock, Is.EqualTo(3UL)); Assert.That(read.GetOffer(0).BuyPrice, Is.EqualTo(10L));
            Assert.That(read.Currency, Is.EqualTo(123L)); Assert.That(Read(read, "PlayerId"), Is.EqualTo("account-player"));
        }

        private (ItemCatalogIndex Catalog, ShopConfiguration Configuration, object Snapshot, Sprite MaterialIcon) AccountFixture()
        {
            // 使用原SO冻结规则和原IShopReadOnly消费面，不构造Profile、Repository或账号写服务。
            // Reflection只用于让RED阶段在生产合同尚不存在时明确失败，而非把缺类型变成整仓编译错误。
            var spell = Define("spell", ItemKind.Spell); spell.SpellDefinition.Kind = SpellKind.Emit;
            var store = Store(spell); var definition = Track(ScriptableObject.CreateInstance<ShopDefinition>());
            var texture = Track(new Texture2D(1, 1)); var icon = Track(Sprite.Create(texture, new Rect(0, 0, 1, 1), Vector2.zero));
            definition.ShopId = "account-shop"; definition.PurchaseBudget = 50; definition.RefreshEveryProgress = 3;
            definition.Offers = new[] {
                new ShopOffer { OfferId = "spell-offer", ContentId = "spell", BuyPrice = 10, SellPrice = 4, InitialCharges = 8, Stock = 10 },
                new ShopOffer { OfferId = "water-offer", ContentId = "", DisplayName = "Water", Icon = icon,
                    MaterialKey = 1, BuyPrice = 2, SellPrice = 1, InitialCharges = 0, InitialMaterialQuantity = 1, Stock = 100 } };
            Assert.That(definition.TryFreeze(store.Catalog, out var configuration), Is.True);
            var snapshotType = RequireType("Game.Core.OnlineAccountShopPresentationSnapshot");
            var stockType = RequireType("Game.Core.OnlineAccountShopStock"); var snapshot = Activator.CreateInstance(snapshotType);
            Set(snapshot, "PlayerId", "account-player"); Set(snapshot, "InventoryId", "account-inventory");
            Set(snapshot, "ShopId", "account-shop"); Set(snapshot, "ConfigHash", new string('a', 64)); Set(snapshot, "SceneHandle", 42);
            Set(snapshot, "InventoryVersion", 9u); Set(snapshot, "ShopVersion", 12L); Set(snapshot, "Gold", 123L);
            Set(snapshot, "RemainingBudget", 40L); Set(snapshot, "ProgressUntilRefresh", 2L);
            var stocks = Array.CreateInstance(stockType, 2);
            for (int i = 0; i < 2; i++)
            {
                var offer = definition.Offers[i]; var stock = Activator.CreateInstance(stockType);
                Set(stock, "OfferId", offer.OfferId); Set(stock, "ContentId", offer.ContentId); Set(stock, "MaterialKey", offer.MaterialKey);
                Set(stock, "BuyPrice", offer.BuyPrice); Set(stock, "SellPrice", offer.SellPrice); Set(stock, "InitialCharges", offer.InitialCharges);
                Set(stock, "Unlimited", offer.Unlimited); Set(stock, "InitialMaterialQuantity", offer.InitialMaterialQuantity);
                Set(stock, "InitialStock", offer.Stock); Set(stock, "Remaining", i == 0 ? 3UL : 7UL); stocks.SetValue(stock, i);
            }
            Set(snapshot, "Stocks", stocks); return (store.Catalog, configuration, snapshot, icon);
        }

        private static bool CreateAccountRead(ItemCatalogIndex catalog, ShopConfiguration configuration, object snapshot,
            out IShopReadOnly read, out string error)
        {
            var method = RequireType("Game.Run.AccountShopReadModel").GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "账号商店必须先验证完整报价和库存，再发布只读模型。");
            var args = new object[] { catalog, configuration, snapshot, null, null };
            bool result = (bool)method.Invoke(null, args); read = args[3] as IShopReadOnly; error = args[4] as string; return result;
        }
        private static Type RequireType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var type = assembly.GetType(name); if (type != null) return type; }
            Assert.Fail("缺少账号商店合同或只读适配：" + name); return null;
        }
        private static Array Stocks(object snapshot) => (Array)Read(snapshot, "Stocks");
        private static object Read(object value, string field)
        {
            var property = value.GetType().GetProperty(field); if (property != null) return property.GetValue(value);
            return value.GetType().GetField(field).GetValue(value);
        }
        private static void Set(object value, string field, object content) => value.GetType().GetField(field).SetValue(value, content);
    }
}
