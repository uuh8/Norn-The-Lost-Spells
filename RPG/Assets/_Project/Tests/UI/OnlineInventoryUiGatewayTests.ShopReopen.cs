using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    public sealed partial class OnlineInventoryUiGatewayTests
    {
        /// <summary>
        /// 使用原Hub完整ShopCanvas克隆，保留真实行模板、inactive Panel与嵌入库存层级。
        /// 成功响应的顺序与Accounts组合层相同：安装Profile/准备消费者，再发布商店报价。
        /// 不把请求中的暂时隐藏当作最终失败；断言发生在第二份合法报价到达、UI Update之后。
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void AccountShopOriginalCanvasReopensRowsAfterTheConfirmedReplyArrives(bool advanceProfile)
        {
            UseAccountCommandSource(); var shop = CreateAccountShopFixture();
            int opens = 0;
            Subscribe(typeof(OnlineAccountShopOpenRequestedEvent), _ => opens++);
            Scene preview = default; GameObject clone = null; ShopController controller = null;
            SinglePlayerInventoryController embedded = null;
            try
            {
                controller = CloneOriginalAccountShopCanvas(shop, out preview, out clone, out embedded);
                controller.Open();
                Assert.That(opens, Is.EqualTo(1));
                DeliverOriginalShopReply(shop, controller, embedded, _accountCommandSnapshot.CopyValue(), CreateAccountShopSnapshot());
                AssertOriginalShopRowsVisible(controller, embedded, 10000L);

                controller.Close(); controller.Open();
                Assert.That(controller.IsOpen, Is.True);
                Assert.That(opens, Is.EqualTo(2), "一次重新打开只发起一份供货请求。");
                var next = _accountCommandSnapshot.CopyValue(); var quote = CreateAccountShopSnapshot();
                if (advanceProfile)
                {
                    // 模拟首次成交后的新账号/供货版本；不调用本地交易或写库存凑结果。
                    next.Version = 10; next.Gold = 9820;
                    Set(quote, "InventoryVersion", 10u); Set(quote, "ShopVersion", 7L); Set(quote, "Gold", 9820L);
                    Set(((Array)Get(quote, "Stocks")).GetValue(0), "Remaining", 2ul);
                }
                DeliverOriginalShopReply(shop, controller, embedded, next, quote);
                AssertOriginalShopRowsVisible(controller, embedded, next.Gold);
                Assert.That(Get(shop, "CanTrade"), Is.True);
                Assert.That(opens, Is.EqualTo(advanceProfile ? 3 : 2),
                    "同版回复不重复读取；新Profile版本仍发一次替换水位的意图，由当前HTTP响应满足。");
                Assert.That(Get(Get(_ui, "_session"), "Profile"), Is.Null);
                Assert.That(_localWrites, Is.Zero);
            }
            finally
            {
                if (controller != null) Invoke(controller, "OnDisable");
                if (embedded != null) { Invoke(embedded, "OnDisable"); embedded.GetType().GetProperty("UiFont").SetValue(embedded, null); }
                if (clone != null) foreach (var wallet in clone.GetComponentsInChildren<WalletView>(true)) DisableShopWallet(wallet);
                if (clone != null) Object.DestroyImmediate(clone);
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        [Test]
        public void AccountShopSameVersionHubNotificationDoesNotRestartAConfirmedRead()
        {
            UseAccountCommandSource(); var shop = CreateAccountShopFixture();
            int opens = 0; Subscribe(typeof(OnlineAccountShopOpenRequestedEvent), _ => opens++);
            Assert.That((bool)shop.GetType().GetMethod("InitializeConfigured").Invoke(shop, null), Is.True);
            shop.GetType().GetMethod("Synchronize").Invoke(shop, null);
            PublishAccountShop(shop, CreateAccountShopSnapshot());
            object confirmed = Get(shop, "ReadOnly");
            Assert.That(Get(shop, "CanTrade"), Is.True); Assert.That(opens, Is.EqualTo(1));

            Assert.That(OnlineAccountInventoryHubScope.TryPublish(_ui.GetInstanceID(), _accountCommandSnapshot.CopyValue(), out var error), Is.True, error);
            Assert.That(opens, Is.EqualTo(1), "相同账号/版本的重复Scope通知不是新的交易或用户重新打开，不能把已确认商店改为pending。");
            Assert.That(Get(shop, "ReadOnly"), Is.SameAs(confirmed)); Assert.That(Get(shop, "CanTrade"), Is.True);

            var changed = _accountCommandSnapshot.CopyValue(); changed.Version = 10;
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(_ui.GetInstanceID(), changed, out error), Is.True, error);
            Assert.That(opens, Is.EqualTo(2), "真实新库存版本仍须撤销旧报价并重新向服务器读取。");
            Assert.That(Get(shop, "ReadOnly"), Is.Null); Assert.That(Get(shop, "CanTrade"), Is.False);
        }

        [Test]
        public void AccountWalletVisibleTextUsesConfirmedGoldWithoutCreatingLocalProfile()
        {
            UseAccountCommandSource();
            var confirmed = _accountCommandSnapshot.CopyValue(); confirmed.Version = 10; confirmed.Gold = 9820;
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(_ui.GetInstanceID(), confirmed, out var error), Is.True, error);
            var root = Track(new GameObject("OriginalAccountWallet", typeof(RectTransform))); root.SetActive(false);
            var amount = Track(new GameObject("OriginalWalletAmount", typeof(RectTransform), typeof(Text))).GetComponent<Text>();
            amount.transform.SetParent(root.transform, false);
            var view = root.AddComponent<WalletView>(); Set(view, "_session", Get(_ui, "_session")); Set(view, "_amount", amount);
            try
            {
                root.SetActive(true); Invoke(view, "OnEnable"); Invoke(view, "Refresh");
                Assert.That(amount.gameObject.activeInHierarchy, Is.True);
                Assert.That(amount.text, Is.EqualTo("9,820"), "原WalletView必须显示本次账号确认的9820，不能把不存在的本地Profile解释成金币0。");
                Assert.That(Get(Get(_ui, "_session"), "Profile"), Is.Null); Assert.That(_localWrites, Is.Zero);
            }
            finally { DisableShopWallet(view); }
        }

        [Test]
        public void AccountWalletInactivePreparationSurvivesCloseAndRejectsStaleOrChangedGold()
        {
            UseAccountCommandSource();
            var root = Track(new GameObject("InactiveAccountWallet", typeof(RectTransform))); root.SetActive(false);
            var amount = Track(new GameObject("ConfirmedWalletAmount", typeof(RectTransform), typeof(Text))).GetComponent<Text>();
            amount.transform.SetParent(root.transform, false);
            var view = root.AddComponent<WalletView>(); Set(view, "_session", Get(_ui, "_session")); Set(view, "_amount", amount);
            var consumer = (object)view as IAccountInventoryHubConsumer;
            Assert.That(consumer, Is.Not.Null, "关闭的原钱包也须通过账号消费者合同预安装，不能要求窗口先启用。");
            var current = _accountCommandSnapshot.CopyValue();
            Assert.That(consumer.TryPrepareAccountInventory(current, out var error), Is.True, error);
            Assert.That(consumer.HasPreparedAccountInventory(current.PlayerId, current.Version), Is.True);
            Assert.That(amount.text, Is.EqualTo("10,000"));
            try
            {
                Invoke(view, "OnEnable"); DisableShopWallet(view);
                Assert.That(consumer.HasPreparedAccountInventory(current.PlayerId, current.Version), Is.True,
                    "正常关窗只结束事件订阅，不撤销已确认且仍匹配Scope的准备状态。");
                var next = current.CopyValue(); next.Version = 10; next.Gold = 9820;
                Assert.That(OnlineAccountInventoryHubScope.TryPublish(_ui.GetInstanceID(), next, out error), Is.True, error);
                Assert.That(consumer.HasPreparedAccountInventory(current.PlayerId, current.Version), Is.False);
                Assert.That(consumer.TryPrepareAccountInventory(current, out error), Is.False, "旧版本钱包不能满足新Scope。");
                Assert.That(consumer.TryPrepareAccountInventory(next, out error), Is.True, error);
                Invoke(view, "Refresh"); Assert.That(amount.text, Is.EqualTo("9,820"));

                var fabricated = next.CopyValue(); fabricated.Gold = 1;
                Assert.That(consumer.TryPrepareAccountInventory(fabricated, out error), Is.False, "同版本不同余额也必须与唯一Scope核验。");
                Assert.That(consumer.HasPreparedAccountInventory(next.PlayerId, next.Version), Is.False);
                Assert.That(consumer.TryPrepareAccountInventory(next, out error), Is.True, error);
                OnlineAccountInventoryHubScope.Clear(_ui.GetInstanceID());
                Invoke(view, "Refresh");
                Assert.That(consumer.HasPreparedAccountInventory(next.PlayerId, next.Version), Is.False);
                Assert.That(amount.text, Is.Not.EqualTo("9,820"), "账号失效后不得留下旧账号余额冒充已确认读数。");
                Assert.That(Get(Get(_ui, "_session"), "Profile"), Is.Null); Assert.That(_localWrites, Is.Zero);
            }
            finally { DisableShopWallet(view); }
        }

        private ShopController CloneOriginalAccountShopCanvas(Component shop, out Scene preview, out GameObject clone,
            out SinglePlayerInventoryController embedded)
        {
            preview = EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
            var originals = new List<ShopController>();
            foreach (var root in preview.GetRootGameObjects()) originals.AddRange(root.GetComponentsInChildren<ShopController>(true));
            Assert.That(originals.Count, Is.EqualTo(1), "必须克隆原正式Hub的唯一商店，不另造一套控件布局。");
            // 原作者把ShopController/行模板和商店嵌入库存放在两个兄弟Canvas。
            // 只克隆前者会保留指向Preview中嵌入Controller/Panel的外部引用，Scene门正确拒绝它。
            // 在未保存的Preview临时组成同一个克隆图，让Unity连同内部FileID引用一起重映射。
            var sourceEmbedded = (SinglePlayerInventoryController)Get(originals[0], "_embeddedInventory");
            var sourceAssembly = new GameObject("OriginalShopCanvasPair"); sourceAssembly.SetActive(false);
            SceneManager.MoveGameObjectToScene(sourceAssembly, preview);
            originals[0].transform.SetParent(sourceAssembly.transform, true);
            sourceEmbedded.transform.SetParent(sourceAssembly.transform, true);
            clone = Object.Instantiate(sourceAssembly);
            SceneManager.MoveGameObjectToScene(clone, _ui.gameObject.scene);
            var controller = clone.GetComponentInChildren<ShopController>(true);
            embedded = (SinglePlayerInventoryController)Get(controller, "_embeddedInventory");
            Assert.That(embedded, Is.Not.Null); Assert.That(Get(embedded, "_embedded"), Is.True);
            Assert.That(Get(controller, "_entryPrefab"), Is.Not.Null);
            Assert.That(embedded.gameObject.scene.handle, Is.EqualTo(_ui.gameObject.scene.handle));
            Assert.That(((GameObject)Get(controller, "_panel")).scene.handle, Is.EqualTo(_ui.gameObject.scene.handle));
            Assert.That(((Component)Get(controller, "_entryPrefab")).gameObject.scene.handle, Is.EqualTo(_ui.gameObject.scene.handle));
            Invoke(controller, "Awake");
            Set(controller, "_shop", shop); Set(controller, "_inventory", Get(_ui, "_session"));
            Set(controller, "_inventoryUi", _ui); Set(controller, "_hubAccess", Get(_ui, "_hubAccess")); Set(controller, "_pause", null);
            Set(embedded, "_session", Get(_ui, "_session")); Set(embedded, "_hubAccess", Get(_ui, "_hubAccess")); Set(embedded, "_pauseCoordinator", null);
            _ui.GetType().GetProperty("UiFont").SetValue(_ui, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")); _attachmentFontSet = true;
            embedded.GetType().GetProperty("UiFont").SetValue(embedded, _ui.UiFont);
            Assert.That(embedded.TryPrepareAccountInventory(_accountCommandSnapshot.CopyValue(), out var error), Is.True, error);
            foreach (var wallet in clone.GetComponentsInChildren<WalletView>(true))
            { Set(wallet, "_session", Get(_ui, "_session")); Invoke(wallet, "OnEnable"); }
            clone.SetActive(true); Invoke(controller, "OnEnable"); Invoke(embedded, "OnEnable");
            // 生产Session.Update先冻结目录并BindAccountHub，F站点才会出现。纯EditMode不自动跑Update，
            // 需补这一真实初始化步骤；不能删ShopController的空间/能力前置门来迁就夹具。
            Assert.That((bool)shop.GetType().GetMethod("InitializeConfigured").Invoke(shop, null), Is.True);
            Assert.That(Get(shop, "AccountPanelAvailable"), Is.True, "fixture角色必须实际位于已注册的账号商店区。");
            Assert.That(Get(controller, "_hubAccess"), Is.SameAs(Get(_ui, "_hubAccess")));
            return controller;
        }

        private void DeliverOriginalShopReply(Component shop, ShopController controller, SinglePlayerInventoryController embedded,
            OnlineAccountInventorySnapshot snapshot, object quote)
        {
            int owner = _ui.GetInstanceID();
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(owner, snapshot, out var error), Is.True, error);
            Assert.That(OnlineAccountInventoryHubScope.TryRequireScene(owner, _ui.gameObject.scene.handle, out error), Is.True, error);
            Assert.That(_ui.TryPrepareAccountInventory(snapshot, out error), Is.True, error);
            _source = Get(_ui, "PresentationSource"); _read = Get(_source, "ReadOnly"); _accountCommandSnapshot = snapshot;
            Assert.That(embedded.TryPrepareAccountInventory(snapshot, out error), Is.True, error);
            foreach (var wallet in controller.transform.root.GetComponentsInChildren<WalletView>(true))
            {
                // 修复前仍执行真实Refresh取得0的RED；修复后沿组合层同一消费者接口准备inactive钱包。
                if ((object)wallet is IAccountInventoryHubConsumer consumer)
                    Assert.That(consumer.TryPrepareAccountInventory(snapshot, out error), Is.True, error);
                Invoke(wallet, "Refresh");
            }
            PublishAccountShop(shop, quote);
            Invoke(controller, "Update"); Invoke(controller, "Update");
        }

        private static void AssertOriginalShopRowsVisible(ShopController controller, SinglePlayerInventoryController embedded, long gold)
        {
            Assert.That(controller.IsOpen, Is.True); Assert.That(((GameObject)Get(controller, "_panel")).activeInHierarchy, Is.True);
            var rows = (List<ShopEntryView>)Get(controller, "_offerViews");
            Assert.That(rows.Count, Is.EqualTo(1)); Assert.That(rows[0].gameObject.activeInHierarchy, Is.True);
            Assert.That(rows[0].OfferId, Is.EqualTo("wand-offer"));
            Assert.That(((RectTransform)Get(controller, "_offersRoot")).gameObject.activeInHierarchy, Is.True);
            Assert.That(embedded.IsOpen, Is.True);
            Assert.That(Get(Get(Get(embedded, "PresentationSource"), "WalletReadOnly"), "Currency"), Is.EqualTo(gold));
            var wallets = controller.transform.root.GetComponentsInChildren<WalletView>(true);
            Assert.That(wallets.Length, Is.GreaterThan(0), "必须核对原Shop实际WalletView，而非只断言数据源。");
            foreach (var wallet in wallets)
            {
                Invoke(wallet, "Refresh"); var amount = (Text)Get(wallet, "_amount");
                Assert.That(amount.gameObject.activeInHierarchy, Is.True);
                Assert.That(amount.text, Is.EqualTo(gold.ToString("N0", CultureInfo.InvariantCulture)));
            }
        }

        private static void DisableShopWallet(WalletView view)
            => view.GetType().GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(view, null);
    }
}
