using System;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI.Tests
{
    // 复用真实账号/商店夹具。通过原SelectOffer/SelectOwned驱动纸面，
    // 不直接调用formatter，也不为查看信息制造本地Profile或交易结果。
    public sealed partial class OnlineInventoryUiGatewayTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void AuditShopDetailsKeepsOfferAndOwnedSpellTogetherWithTheirRealCounts(bool qualification)
        {
            var controller = CreateShopDetailsController(out var details, qualification);
            controller.SelectOffer("wand-offer"); controller.SelectOwned("stored-card");
            Assert.That(details.text, Does.Contain("所选商品").And.Contain("持有物品"));
            Assert.That(details.text, Does.Contain("刻印法术").And.Contain("单价：10 / 份").And.Contain("剩余供货：3"));
            Assert.That(details.text, Does.Contain(qualification ? "12 次入局" : "12 次施法"));
            Assert.That(details.text, Does.Contain(qualification ? "剩余入局：2 次" : "剩余施法：2 次"));
            Assert.That(details.text, Does.Contain("位置：仓库"));
            controller.SelectOffer("wand-offer");
            Assert.That(details.text, Does.Contain("持有物品"), "重新选商品不能抹掉作为购买目标的独立实例选择。");
            Assert.That(((Button)Get(controller, "_buyButton")).interactable, Is.True);
            Assert.That(((Button)Get(controller, "_sellItemButton")).interactable, Is.True);
            Assert.That(Get(ReadView("stored-card"), "RemainingCharges"), Is.EqualTo(2));
            AssertShopDetailsOnlyRead();
        }

        [Test]
        public void AuditShopDetailsChangesWithTheConcreteContainerAndBackpack()
        {
            var controller = CreateShopDetailsController(out var details);
            controller.SelectOwned("bag-bottle");
            Assert.That(details.text, Does.Contain("旅用瓶").And.Contain("储量：17 / 15000 GMU").And.Contain("位置：携带背包"));
            controller.SelectOwned("tool-bottle");
            Assert.That(details.text, Does.Contain("旅用瓶").And.Contain("储量：0 / 15000 GMU").And.Contain("位置：工具装备位"));
            Assert.That(details.text, Does.Not.Contain("储量：17"), "同类不同实例必须更新为本次选中的真实储量。");
            controller.SelectOwned("bag");
            Assert.That(details.text, Does.Contain("行囊").And.Contain("内部尺寸：8 × 8").And.Contain("位置：背包装备位"));
            Assert.That(details.text, Does.Not.Contain("GMU"));
            Assert.That(Get(ReadView("bag-bottle"), "ContainerTotal"), Is.EqualTo(17ul));
            AssertShopDetailsOnlyRead();
        }

        [Test]
        public void AuditShopDetailsWithoutSelectionShowsCurrentControls()
        {
            var controller = CreateShopDetailsController(out var details);
            var summary = Track(new GameObject("UnchangedShopSummary", typeof(RectTransform), typeof(Text))).GetComponent<Text>();
            var status = Track(new GameObject("UnchangedShopStatus", typeof(RectTransform), typeof(Text))).GetComponent<Text>();
            Color authored = new Color(.8f, .7f, .6f, 1); summary.color = status.color = authored;
            Set(controller, "_summary", summary); Set(controller, "_status", status);
            var font = details.font; int fontSize = details.fontSize;
            controller.SelectOwned(null);
            Assert.That(details.text, Does.Contain("选择").And.Contain("商品").And.Contain("物品"));
            Assert.That(details.text, Does.Contain("F 区域交互").And.Contain("Tab 背包").And.Contain("Esc 关闭"));
            Assert.That(details.text, Does.Not.Contain("待后续开发").And.Not.Contain("E 区域交互"));
            Assert.That(((Button)Get(controller, "_buyButton")).interactable, Is.False);
            Assert.That(((Button)Get(controller, "_sellItemButton")).interactable, Is.False);
            Assert.That(details.color, Is.EqualTo((Color)new Color32(61, 39, 22, 255)));
            Assert.That(summary.color, Is.EqualTo(authored)); Assert.That(status.color, Is.EqualTo(authored));
            Assert.That(details.font, Is.SameAs(font)); Assert.That(details.fontSize, Is.EqualTo(fontSize));
            // 同一原入口保留legacy布局的作者颜色，不把纸面墨色扩散到旧配色。
            Set(controller, "_detailsPlaceholder", false); details.color = authored; controller.SelectOwned(null);
            Assert.That(details.color, Is.EqualTo(authored));
            AssertShopDetailsOnlyRead();
        }

        [Test]
        public void AuditShopDetailsClearsOwnedFactsWhenTheirAccountSourceDisappears()
        {
            var controller = CreateShopDetailsController(out var details);
            controller.SelectOwned("bag-bottle");
            Assert.That(details.text, Does.Contain("储量：17 / 15000 GMU"));
            details.color = Color.white; // 即使等待提示先于正常选择刷新，也必须使用纸面墨色。
            _ui.GetType().GetMethod("BindPresentationSource").Invoke(_ui, new object[] { null });
            // 账号Source可以在鼠标不动时异步失效，不能靠再次点击才撤下上一份资产信息。
            Invoke(controller, "Update");
            Assert.That(details.text, Does.Contain("物品信息暂不可用"));
            Assert.That(details.text, Does.Not.Contain("旅用瓶").And.Not.Contain("17 / 15000"));
            Assert.That(details.color, Is.EqualTo((Color)new Color32(61, 39, 22, 255)));
            Assert.That(((Button)Get(controller, "_sellItemButton")).interactable, Is.False);
            AssertShopDetailsOnlyRead();
        }

        private ShopController CreateShopDetailsController(out Text details, bool qualification = false)
        {
            _attachmentEmitterDefinition = !qualification; UseAccountCommandSource();
            var definitions = new SerializedObject(_bindingCatalogAsset).FindProperty("_definitions");
            for (int i = 0; i < definitions.arraySize; i++)
            {
                var fields = new SerializedObject(definitions.GetArrayElementAtIndex(i).objectReferenceValue);
                string id = fields.FindProperty("_contentId").stringValue;
                string display = id == "ui.online.spell" ? "刻印法术" : id == "ui.online.bottle" ? "旅用瓶" : id == "ui.online.bag" ? "行囊" : null;
                if (display == null) continue;
                fields.FindProperty("_displayName").stringValue = display; fields.ApplyModifiedPropertiesWithoutUndo();
            }
            RebindAccountCommandSource(9);
            var shop = CreateAccountShopFixture();
            var offers = (Array)Get(Get(shop, "_definition"), "Offers"); var offer = offers.GetValue(0);
            Set(offer, "ContentId", "ui.online.spell"); offers.SetValue(offer, 0);
            Subscribe(typeof(OnlineAccountShopOpenRequestedEvent), _ => { });
            Assert.That((bool)shop.GetType().GetMethod("InitializeConfigured").Invoke(shop, null), Is.True);
            shop.GetType().GetMethod("Synchronize").Invoke(shop, null);
            var quote = CreateAccountShopSnapshot(); var stocks = (Array)Get(quote, "Stocks");
            var stock = stocks.GetValue(0); Set(stock, "ContentId", "ui.online.spell"); stocks.SetValue(stock, 0);
            PublishAccountShop(shop, quote);
            Assert.That(Get(shop, "CanTrade"), Is.True);

            var root = Track(new GameObject("OriginalShopPaperDetails")); root.SetActive(false);
            var controller = root.AddComponent<ShopController>(); Invoke(controller, "Awake");
            details = Track(new GameObject("OriginalShopDetails", typeof(RectTransform), typeof(Text))).GetComponent<Text>();
            Set(controller, "_shop", shop); Set(controller, "_inventory", Get(_ui, "_session")); Set(controller, "_inventoryUi", _ui);
            Set(controller, "_details", details); Set(controller, "_detailsPlaceholder", true); Set(controller, "_quickPurchase", true); Set(controller, "_open", true);
            foreach (string field in new[] { "_buyButton", "_sellItemButton", "_sellMaterialButton" })
                Set(controller, field, Track(new GameObject(field, typeof(RectTransform), typeof(Button))).GetComponent<Button>());
            CaptureAccountRequests();
            Subscribe(typeof(ShopTradeRequestedEvent), _ => _localWrites++);
            Subscribe(typeof(OnlineAccountShopTradeRequestedEvent), _ => _localWrites++);
            return controller;
        }

        private void AssertShopDetailsOnlyRead()
        {
            Assert.That(_localWrites, Is.Zero); Assert.That(_requests, Is.Zero); Assert.That(_accountDsRequests, Is.Zero);
            Assert.That(InventoryVersion(), Is.EqualTo(9u));
        }
    }
}
