using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>
    /// 使用原库存 Controller 与真实线上 Source，验证 UI 只发意图、不改资产。
    /// UI 测试程序集不依赖 Run/Skills；Reflection 仅用于测试装配和新合同 RED，生产不使用这种查找。
    /// </summary>
    public sealed partial class OnlineInventoryUiGatewayTests : InputTestFixture
    {
        private readonly List<Object> _objects = new List<Object>();
        private readonly List<Action> _unsubscribers = new List<Action>();
        private readonly List<Component> _nearbyLifecycles = new List<Component>();
        private readonly List<SpellAttachmentView> _attachmentViews = new List<SpellAttachmentView>();
        private SinglePlayerInventoryController _ui;
        private Text _status;
        private object _source;
        private object _read;
        private object _request;
        private object _bindingCatalog;
        private Object _bindingCatalogAsset;
        private Transform _accountWarehouseActor;
        private Object _bindingBottleDefinition;
        private bool _includeSolidDefinition;
        private bool _attachmentEmitterDefinition, _attachmentFontSet;
        private int _accountDsRequests;
        private int _requests, _localWrites;
        private bool _enabled;
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [SetUp]
        public void CreateRealReadSourceAndInactiveOriginalController()
        {
            ResetOwnedAccountScope();
            _request = null; _requests = _localWrites = 0; _enabled = false; _source = _read = null; _includeSolidDefinition = false;
            _attachmentEmitterDefinition = _attachmentFontSet = false;
            _accountDsRequests = 0;
            var wand = Definition("ui.online.wand", "Wand");
            var card = Definition("ui.online.spell", "Spell");
            var catalog = Asset("Game.Run.ItemCatalog");
            var catalogData = new SerializedObject(catalog);
            var entries = catalogData.FindProperty("_definitions"); entries.arraySize = 2;
            entries.GetArrayElementAtIndex(0).objectReferenceValue = wand;
            entries.GetArrayElementAtIndex(1).objectReferenceValue = card;
            catalogData.ApplyModifiedPropertiesWithoutUndo();
            var indexArgs = new object[] { null, null };
            Assert.That((bool)catalog.GetType().GetMethod("TryCreateIndex").Invoke(catalog, indexArgs), Is.True, indexArgs[1] as string);
            var identity = Activator.CreateInstance(Type("Game.Core.OnlineInventoryPresentationIdentity"), "run", "player", 1ul,
                "inventory", Hash, 7u, "DungeonWorld");
            var args = new[] { indexArgs[0], identity, Snapshot(), null, null };
            Assert.That((bool)Type("Game.Run.OnlineInventoryPresentationSource").GetMethod("TryCreate").Invoke(null, args), Is.True, args[4] as string);
            _source = args[3]; _read = Get(_source, "ReadOnly");
            var root = Track(new GameObject("OriginalInventoryGateway")); root.SetActive(false);
            _ui = root.AddComponent<SinglePlayerInventoryController>();
            var statusRoot = new GameObject("Status", typeof(RectTransform), typeof(Text));
            statusRoot.transform.SetParent(root.transform); _status = statusRoot.GetComponent<Text>();
            Set(_ui, "_status", _status);
            _ui.GetType().GetMethod("BindPresentationSource").Invoke(_ui, new[] { _source });
            Set(_ui, "_open", true); _status.text = "正在查看";
            Invoke(_ui, "OnEnable"); _enabled = true;
            Subscribe(typeof(InventoryCommandEvent), _ => _localWrites++);
        }

        [TearDown]
        public void DisposeOnlyTestObjectsAndSubscriptions()
        {
            if (_enabled && _ui != null) Invoke(_ui, "OnDisable");
            foreach (var view in _attachmentViews) if (view != null) Invoke(view, "OnDisable");
            _attachmentViews.Clear();
            // 测试借用内置字体，不让原Controller的运行时字体释放逻辑销毁该共享资产。
            if (_attachmentFontSet && _ui != null) _ui.GetType().GetProperty("UiFont").SetValue(_ui, null);
            // ConfigureOnline在纯EditMode显式建立了事件观察者；夹具同样精确结束自己创建的生命周期。
            // 非ExecuteAlways的Mono不能假定DestroyImmediate会自然补发全部运行时回调，更不能全局清EventBus。
            for (int i = _nearbyLifecycles.Count - 1; i >= 0; i--)
                if (_nearbyLifecycles[i] != null) Invoke(_nearbyLifecycles[i], "OnDisable");
            _nearbyLifecycles.Clear();
            for (int i = _unsubscribers.Count - 1; i >= 0; i--) _unsubscribers[i]();
            _unsubscribers.Clear();
            (_source as IDisposable)?.Dispose();
            for (int i = _objects.Count - 1; i >= 0; i--) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            ResetOwnedAccountScope();
        }
        private static void ResetOwnedAccountScope()
        {
            // RunnerScene会被多fixture复用；只重置本测试的静态作用域，不能修改生产Clear的防回落合同。
            typeof(OnlineAccountInventoryHubScope).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }

        [Test]
        public void RepeatedWandAndCardInspectionDoesNotReportARejectedWrite()
        {
            _ui.SelectItem("wand"); _ui.ClickItem("wand");
            Assert.That(_status.text, Is.EqualTo("正在查看"), "重复查看法杖不应先撞上写权限检查。");
            _ui.SelectItem("card"); _ui.ClickItem("card"); _ui.InspectWandSlot(0);
            Assert.That(_status.text, Is.EqualTo("正在查看")); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void AccountHubTabUnlocksCursorAndPausesLocalInputWithoutDungeonMovementWorld()
        {
            float scale = Time.timeScale; var cursor = Cursor.lockState; bool visible = Cursor.visible;
            try
            {
                UseAccountCommandSource(); _ui.Close();
                var pauseRoot = Track(new GameObject("AccountHubInventoryPause")); pauseRoot.SetActive(false);
                var pause = pauseRoot.AddComponent(Type("Game.Run.RunPauseCoordinator"));
                Set(_ui, "_pauseCoordinator", pause); Time.timeScale = 1f; Invoke(pause, "Awake");
                int dungeonCaptures = 0;
                Subscribe(typeof(InventoryPresentationVisibilityEvent), _ => dungeonCaptures++);

                // 账号快照来自服务器，但Hub角色仍是本地Scene。这里故意不造OnlineWorldBootstrap或
                // OnlineMovementPresentationEvent，避免用不存在的副本事件掩盖Tab本身未取得鼠标的问题。
                _ui.OpenBackpack();
                Assert.That(_ui.IsOpen, Is.True);
                Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None), "账号Hub的原Tab必须独立取得可操作鼠标。");
                Assert.That(Cursor.visible, Is.True);
                Assert.That(Time.timeScale, Is.Zero, "Hub库存仍由原Pause Owner阻止本地角色穿透操作。");
                Assert.That(dungeonCaptures, Is.Zero, "账号读模型不代表已进入需要多人输入捕获的DS副本。");
                Assert.That(Get(Get(_ui, "_session"), "ReadOnly"), Is.Null, "申请本地UI暂停不能解除账号资产的旧档写锁。");

                _ui.Close();
                Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked));
                Assert.That(Cursor.visible, Is.False); Assert.That(Time.timeScale, Is.EqualTo(1f));
                Assert.That(_localWrites, Is.Zero);
            }
            finally { _ui.Close(); Time.timeScale = scale; Cursor.lockState = cursor; Cursor.visible = visible; }
        }

        [Test]
        public void AccountHubInventoryCloseRetainsAnotherLocalPauseOwner()
        {
            float scale = Time.timeScale; var cursor = Cursor.lockState; bool visible = Cursor.visible;
            try
            {
                UseAccountCommandSource(); _ui.Close();
                var pauseRoot = Track(new GameObject("AccountHubCombinedPause")); pauseRoot.SetActive(false);
                var pause = pauseRoot.AddComponent(Type("Game.Run.RunPauseCoordinator"));
                Set(_ui, "_pauseCoordinator", pause); Time.timeScale = 1f; Invoke(pause, "Awake");
                object menuReason = Enum.Parse(Type("Game.Run.RunPauseReason"), "PauseMenu");
                object inventoryReason = Enum.Parse(Type("Game.Run.RunPauseReason"), "Inventory");
                pause.GetType().GetMethod("RequestPause").Invoke(pause, new[] { menuReason });

                _ui.OpenBackpack();
                Assert.That((bool)pause.GetType().GetMethod("HasReason").Invoke(pause, new[] { inventoryReason }), Is.True,
                    "Tab应申请自己的Inventory reason，不能仅依赖恰好已经打开的Pause Menu。");
                _ui.Close();
                Assert.That((bool)pause.GetType().GetMethod("HasReason").Invoke(pause, new[] { inventoryReason }), Is.False);
                Assert.That((bool)pause.GetType().GetMethod("HasReason").Invoke(pause, new[] { menuReason }), Is.True);
                Assert.That(Time.timeScale, Is.Zero); Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
                Assert.That(Cursor.visible, Is.True, "关闭Tab只释放自己的输入，不能从另一个菜单手中收回鼠标。");
                pause.GetType().GetMethod("ReleasePause").Invoke(pause, new[] { menuReason });
                Assert.That(Time.timeScale, Is.EqualTo(1f)); Assert.That(Cursor.visible, Is.False);
            }
            finally { _ui.Close(); Time.timeScale = scale; Cursor.lockState = cursor; Cursor.visible = visible; }
        }

        [Test]
        public void DungeonInventoryCapturesOnlyLocalInputAndKeepsSharedWorldRunning()
        {
            float scale = Time.timeScale;
            try
            {
                _ui.Close(); Time.timeScale = 1f; int opens = 0, closes = 0;
                Subscribe(typeof(InventoryPresentationVisibilityEvent), value =>
                {
                    var change = (InventoryPresentationVisibilityEvent)value;
                    if (change.OwnerId != _ui.GetInstanceID()) return;
                    if (change.Open) opens++; else closes++;
                });
                _ui.OpenBackpack();
                Assert.That(opens, Is.EqualTo(1)); Assert.That(Time.timeScale, Is.EqualTo(1f));
                _ui.Close();
                Assert.That(closes, Is.EqualTo(1)); Assert.That(Time.timeScale, Is.EqualTo(1f));
            }
            finally { _ui.Close(); Time.timeScale = scale; }
        }

        [Test]
        public void AccountShopSessionRequestsServerPresentationWithoutCreatingLocalAssets()
        {
            UseAccountCommandSource(); var shop = CreateAccountShopFixture();
            int opens = 0; object opened = null;
            Subscribe(Type("Game.Core.OnlineAccountShopOpenRequestedEvent"), value => { opens++; opened = value; });
            Assert.That((bool)shop.GetType().GetMethod("InitializeConfigured").Invoke(shop, null), Is.True);
            shop.GetType().GetMethod("Synchronize").Invoke(shop, null);
            Assert.That(opens, Is.EqualTo(1), "账号商店只请求一份服务器读模型，不能用旧Profile生成供货。");
            Assert.That(Get(opened, "SceneHandle"), Is.EqualTo((int)_ui.gameObject.scene.handle));
            Assert.That(Get(opened, "CallerId"), Is.EqualTo(shop.GetInstanceID()));
            Assert.That(Get(opened, "InventoryId"), Is.EqualTo(Get(_read, "InventoryId")));
            var inventory = Get(_ui, "_session");
            Assert.That(Get(inventory, "ReadOnly"), Is.Null); Assert.That(Get(inventory, "Profile"), Is.Null);
            Assert.That(Get(shop, "ReadOnly"), Is.Null, "尚未收到服务器报价不能显示本地生成的stock。");
        }

        [Test]
        public void AccountShopForwardsConfirmedVersionedTradeWithoutLocalProfileOrWalletMutation()
        {
            UseAccountCommandSource(); var shop = CreateAccountShopFixture();
            Subscribe(Type("Game.Core.OnlineAccountShopOpenRequestedEvent"), _ => { });
            Assert.That((bool)shop.GetType().GetMethod("InitializeConfigured").Invoke(shop, null), Is.True);
            shop.GetType().GetMethod("Synchronize").Invoke(shop, null);
            PublishAccountShop(shop, CreateAccountShopSnapshot());
            Assert.That(Get(shop, "CanTrade"), Is.True);
            int trades = 0; object forwarded = null;
            Subscribe(Type("Game.Core.OnlineAccountShopTradeRequestedEvent"), value => { trades++; forwarded = value; });
            var request = new ShopTradeRequestedEvent { InventoryId = (string)Get(_read, "InventoryId"),
                RequestId = "account-shop-original-request", InventoryVersion = 9, ShopVersion = 6,
                OfferId = "wand-offer", Kind = ShopTradeKind.BuyItem, Quantity = 1 };
            EventBus<ShopTradeRequestedEvent>.Publish(request);
            Assert.That(trades, Is.EqualTo(1)); Assert.That(Get(forwarded, "CallerId"), Is.EqualTo(shop.GetInstanceID()));
            Assert.That(Get(forwarded, "SceneHandle"), Is.EqualTo((int)_ui.gameObject.scene.handle));
            var actual = (ShopTradeRequestedEvent)Get(forwarded, "Request");
            Assert.That(actual.RequestId, Is.EqualTo("account-shop-original-request"));
            Assert.That(actual.InventoryVersion, Is.EqualTo(9u)); Assert.That(actual.ShopVersion, Is.EqualTo(6));
            Assert.That(actual.OfferId, Is.EqualTo("wand-offer")); Assert.That(actual.Quantity, Is.EqualTo(1ul));
            Assert.That(Get(Get(_source, "WalletReadOnly"), "Currency"), Is.EqualTo(10000L));
            Assert.That(Get(Get(shop, "ReadOnly"), "Currency"), Is.EqualTo(10000L));
            Assert.That(Get(Get(_ui, "_session"), "Profile"), Is.Null); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void AccountShopStartUsesConfirmedUiReadSourceWhileLocalSessionReadRemainsUnavailable()
        {
            UseAccountCommandSource(); var shop = CreateAccountShopFixture();
            var root = Track(new GameObject("AccountOriginalShopController")); root.SetActive(false);
            var controller = root.AddComponent<ShopController>(); Invoke(controller, "Awake");
            var panel = Track(new GameObject("OriginalShopPanel", typeof(RectTransform)));
            panel.transform.SetParent(root.transform, false);
            Set(controller, "_shop", shop); Set(controller, "_inventory", Get(_ui, "_session"));
            Set(controller, "_inventoryUi", _ui); Set(controller, "_panel", panel); Set(controller, "_openOnStart", false);
            _ui.GetType().GetProperty("UiFont").SetValue(_ui, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")); _attachmentFontSet = true;
            var start = (System.Collections.IEnumerator)typeof(ShopController).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);
            Assert.That(start.MoveNext(), Is.False,
                "账号Hub已有合法UI资产和字体时，商店不能永久等待刻意禁用的旧_inventory.ReadOnly。");
            Assert.That(Get(Get(_ui, "_session"), "ReadOnly"), Is.Null);
        }

        [Test]
        public void AccountEmbeddedInventoryRetainsOriginalHttpMoveGatewayWithoutLocalWrites()
        {
            UseAccountCommandSource(); _ui.Close(); Set(_ui, "_embedded", true); CaptureAccountRequests();
            _ui.SetEmbeddedOpen(true);
            Assert.That(_ui.IsOpen, Is.True, "账号商店嵌入原库存必须可查看/提交账号命令，不能被DS只读门一并拦截。");
            Begin("card"); Assert.That(_ui.IsDragging, Is.True); Submit("SpellStorage", "player", 1);
            Assert.That(_requests, Is.EqualTo(1));
            Assert.That(((OnlineAccountInventoryCommandRequestedEvent)_request).Intent.Kind, Is.EqualTo(OnlineAccountInventoryCommandKind.Move));
            Assert.That(_accountDsRequests, Is.Zero); Assert.That(_localWrites, Is.Zero);
            Assert.That(Get(Get(_ui, "_session"), "ReadOnly"), Is.Null);
        }

        [Test]
        public void DungeonSourceCannotAcquireTheHubShopEmbeddedInventory()
        {
            _ui.Close(); Set(_ui, "_embedded", true); _ui.SetEmbeddedOpen(true);
            Assert.That(_ui.IsOpen, Is.False, "DS副本来源继续不能借商店嵌入入口开放据点资产操作。");
            Assert.That(_localWrites, Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AccountShopInvalidQuoteRevokesOnlyItsMatchingPreparedSource(bool matchingCaller)
        {
            UseAccountCommandSource(); var shop = CreateAccountShopFixture();
            Subscribe(Type("Game.Core.OnlineAccountShopOpenRequestedEvent"), _ => { });
            Assert.That((bool)shop.GetType().GetMethod("InitializeConfigured").Invoke(shop, null), Is.True);
            shop.GetType().GetMethod("Synchronize").Invoke(shop, null);
            PublishAccountShop(shop, CreateAccountShopSnapshot());
            Assert.That(Get(shop, "CanTrade"), Is.True);
            var invalid = CreateAccountShopSnapshot();
            Set(((Array)Get(invalid, "Stocks")).GetValue(0), "BuyPrice", 11L);
            PublishAccountShop(shop, invalid, matchingCaller ? 0 : 1);
            Assert.That(Get(shop, "CanTrade"), Is.EqualTo(!matchingCaller),
                "当前商店的错误报价必须撤销旧ready；别的Caller回包不能破坏当前合法商店。");
            if (matchingCaller) Assert.That(Get(shop, "ReadOnly"), Is.Null);
            else Assert.That(Get(shop, "ReadOnly"), Is.Not.Null);
        }

        private Component CreateAccountShopFixture()
        {
            var inventory = (Component)Get(_ui, "_session");
            var hub = (Component)Get(_ui, "_hubAccess"); var hubDefinition = Get(hub, "_definition");
            var stations = (Array)Get(hubDefinition, "Stations"); var station = stations.GetValue(0);
            Set(station, "Kind", Enum.Parse(Type("Game.Run.HubStationKind"), "Shop")); stations.SetValue(station, 0);
            Set(hub, "_shopArea", Get(hub, "_warehouseArea"));
            var definition = Asset("Game.Run.ShopDefinition");
            Set(definition, "ShopId", "hub.shop"); Set(definition, "PurchaseBudget", 200L); Set(definition, "RefreshEveryProgress", 2);
            var offerType = Type("Game.Run.ShopOffer"); var offer = Activator.CreateInstance(offerType);
            Set(offer, "OfferId", "wand-offer"); Set(offer, "ContentId", "ui.online.wand");
            Set(offer, "BuyPrice", 10L); Set(offer, "SellPrice", 4L); Set(offer, "Stock", 10ul); Set(offer, "InitialCharges", 12);
            var offers = Array.CreateInstance(offerType, 1); offers.SetValue(offer, 0); Set(definition, "Offers", offers);
            var shop = inventory.gameObject.AddComponent(Type("Game.Run.SinglePlayerShopSession"));
            Set(shop, "_inventory", inventory); Set(shop, "_definition", definition);
            inventory.gameObject.SetActive(true);
            Invoke(shop, "OnEnable"); _nearbyLifecycles.Add(shop); return shop;
        }

        private object CreateAccountShopSnapshot()
        {
            var snapshot = Activator.CreateInstance(Type("Game.Core.OnlineAccountShopPresentationSnapshot"));
            Set(snapshot, "PlayerId", "player"); Set(snapshot, "InventoryId", Get(_read, "InventoryId"));
            Set(snapshot, "ShopId", "hub.shop"); Set(snapshot, "ConfigHash", Hash);
            Set(snapshot, "SceneHandle", (int)_ui.gameObject.scene.handle); Set(snapshot, "InventoryVersion", 9u);
            Set(snapshot, "ShopVersion", 6L); Set(snapshot, "Gold", 10000L); Set(snapshot, "RemainingBudget", 200L);
            Set(snapshot, "ProgressUntilRefresh", 1L);
            var stockType = Type("Game.Core.OnlineAccountShopStock"); var stock = Activator.CreateInstance(stockType);
            Set(stock, "OfferId", "wand-offer"); Set(stock, "ContentId", "ui.online.wand");
            Set(stock, "BuyPrice", 10L); Set(stock, "SellPrice", 4L); Set(stock, "InitialCharges", 12);
            Set(stock, "InitialMaterialQuantity", 1ul); Set(stock, "InitialStock", 10ul); Set(stock, "Remaining", 3ul);
            var stocks = Array.CreateInstance(stockType, 1); stocks.SetValue(stock, 0); Set(snapshot, "Stocks", stocks); return snapshot;
        }

        private void PublishAccountShop(Component shop, object snapshot, int callerOffset = 0)
        {
            var changedType = Type("Game.Core.OnlineAccountShopChangedEvent"); var changed = Activator.CreateInstance(changedType);
            Set(changed, "CallerId", shop.GetInstanceID() + callerOffset); Set(changed, "SceneHandle", (int)_ui.gameObject.scene.handle);
            Set(changed, "Success", true); Set(changed, "Snapshot", snapshot);
            typeof(EventBus<>).MakeGenericType(changedType).GetMethod("Publish").Invoke(null, new[] { changed });
        }

        [Test]
        public void AccountSourceHasAProfileContextWithoutPretendingToBeADungeonConnection()
        {
            UseAccountCommandSource();
            Assert.That(Type("Game.Run.IAccountInventoryCommandContext").IsInstanceOfType(_source), Is.True);
            Assert.That(Type("Game.Run.IOnlineInventoryCommandContext").IsInstanceOfType(_source), Is.False);
            Assert.That(Get(_source, "PlayerId"), Is.EqualTo("player"));
            Assert.That(Get(_source, "ProfileVersion"), Is.EqualTo(9u));
            Assert.That(Get(_source, "WorldSceneKey"), Is.EqualTo(""));
        }

        [TestCase("bag-bottle", "Warehouse", 0, null, 0)]
        [TestCase("card", "SpellStorage", 7, null, 1)]
        [TestCase("stored-card", "ProgramSlot", 2, "wand", 1)]
        [TestCase("wand", "EquippedWand", 3, null, 1)]
        [TestCase("backup-bag", "EquippedBackpack", 4, null, 0)]
        public void AccountDragUsesOneAuthoritativeMoveWithoutLocalOrDungeonWrites(string item, string target, int location, string parent, int slot)
        {
            UseAccountCommandSource(); CaptureAccountRequests();
            Begin(item); Assert.That(_ui.IsDragging, Is.True, "真实账号实例应复用原拖拽表现，不应被旧只读总门拒绝。");
            Submit(target, parent, slot);
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(_ui.IsDragging, Is.False);
            var request = (OnlineAccountInventoryCommandRequestedEvent)_request; var intent = request.Intent;
            Assert.That(request.CallerId, Is.EqualTo(_ui.GetInstanceID()));
            Assert.That(intent.CommandId, Does.Match("^[0-9a-f]{32}$"));
            // Unity Scene句柄包装与Core传输的有符号int先按公开转换对齐；负句柄仍逐值比较，绝不窄化到uint。
            Assert.That(intent.PlayerId, Is.EqualTo("player")); Assert.That(intent.SceneHandle, Is.EqualTo((int)_ui.gameObject.scene.handle));
            Assert.That(intent.ExpectedVersion, Is.EqualTo(9u)); Assert.That(intent.Kind, Is.EqualTo(OnlineAccountInventoryCommandKind.Move));
            Assert.That(intent.ItemId, Is.EqualTo(item)); Assert.That(intent.TargetLocationKind, Is.EqualTo(location));
            Assert.That(intent.TargetParentInstanceId ?? "", Is.EqualTo(parent ?? ""));
            Assert.That(intent.TargetSlot, Is.EqualTo(location == 0 ? -1 : slot));
            Assert.That(typeof(OnlineAccountInventoryCommandIntent).GetField("ConnectionEpoch"), Is.Null);
            Assert.That(typeof(OnlineAccountInventoryCommandIntent).GetField("ActorId"), Is.Null);
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero); Assert.That(_accountDsRequests, Is.Zero);
        }

        [Test]
        public void AccountContainerToolBindingIsAnAccountMoveAndDoesNotChangeItsConfirmedComposition()
        {
            UseAccountCommandSource(); CaptureAccountRequests(); CaptureLocalToolWrites();
            Begin("bag-bottle"); Assert.That(_ui.IsDragging, Is.True);
            SubmitShortcut(2);
            Assert.That(_requests, Is.EqualTo(1));
            var intent = ((OnlineAccountInventoryCommandRequestedEvent)_request).Intent;
            Assert.That(intent.TargetLocationKind, Is.EqualTo(8)); Assert.That(intent.TargetSlot, Is.EqualTo(2));
            Assert.That(intent.TargetParentInstanceId ?? "", Is.Empty); Assert.That(intent.WholeStack, Is.False);
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero); Assert.That(_accountDsRequests, Is.Zero);
        }

        [Test]
        public void AccountBoundBackpackCanUseTheOriginalAtomicEquipmentMoveFromWarehouse()
        {
            UseAccountCommandSource("backup-bag"); CaptureAccountRequests();
            Begin("backup-bag"); Assert.That(_ui.IsDragging, Is.True,
                "绑定仅禁止脱离账号归属；存于本人仓库的基础包仍须允许原子换回装备并迁移内容。");
            Submit("EquippedBackpack", null, 0);
            Assert.That(_requests, Is.EqualTo(1)); var intent=((OnlineAccountInventoryCommandRequestedEvent)_request).Intent;
            Assert.That(intent.Kind, Is.EqualTo(OnlineAccountInventoryCommandKind.Move)); Assert.That(intent.ItemId, Is.EqualTo("backup-bag"));
            Assert.That(intent.TargetLocationKind, Is.EqualTo(4)); Assert.That(intent.ExpectedVersion, Is.EqualTo(9u));
            Assert.That(Get(ReadView("backup-bag"), "EntryBoundBackpack"), Is.True);
            Assert.That(Get(Get(ReadView("backup-bag"), "Location"), "Kind").ToString(), Is.EqualTo("Warehouse"));
            Assert.That(_localWrites, Is.Zero); Assert.That(_accountDsRequests, Is.Zero);
        }

        [Test]
        public void AccountBoundBackpackStillCannotBeDroppedOrBorrowWarehouseRange()
        {
            UseAccountCommandSource("bag"); CaptureAccountRequests(); Begin("bag");
            Assert.That(_ui.IsDragging, Is.True); Submit("Ground", null, 0);
            Assert.That(_requests, Is.Zero); Assert.That(_ui.IsDragging, Is.False);
            Assert.That(Get(ReadView("bag"), "EntryBoundBackpack"), Is.True);
            _accountWarehouseActor.position=new Vector3(10,0,0);
            Begin("backup-bag"); Assert.That(_ui.IsDragging, Is.False);
            Assert.That(_localWrites, Is.Zero); Assert.That(_accountDsRequests, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void AccountBoundBackpackCannotOfferOrSubmitItsGuaranteedShellForSale(bool placeholderDetails)
        {
            UseAccountCommandSource("bag"); var shop = CreateAccountShopFixture();
            Subscribe(Type("Game.Core.OnlineAccountShopOpenRequestedEvent"), _ => { });
            Assert.That((bool)shop.GetType().GetMethod("InitializeConfigured").Invoke(shop, null), Is.True);
            shop.GetType().GetMethod("Synchronize").Invoke(shop, null); PublishAccountShop(shop, CreateAccountShopSnapshot());
            var root = Track(new GameObject("BoundBackpackOriginalShopController")); root.SetActive(false);
            var controller = root.AddComponent<ShopController>(); Invoke(controller, "Awake");
            var details = Track(new GameObject("BoundBackpackShopDetails", typeof(RectTransform), typeof(Text))).GetComponent<Text>();
            var button = Track(new GameObject("BoundBackpackSellButton", typeof(RectTransform), typeof(Button))).GetComponent<Button>();
            Set(controller, "_shop", shop); Set(controller, "_inventory", Get(_ui, "_session")); Set(controller, "_inventoryUi", _ui);
            Set(controller, "_details", details); Set(controller, "_sellItemButton", button); Set(controller, "_open", true);
            Set(controller, "_detailsPlaceholder", placeholderDetails);
            int requests = 0; Subscribe(typeof(ShopTradeRequestedEvent), _ => requests++);
            controller.SelectOwned("bag");
            Assert.That(button.interactable, Is.False, "绑定基础包与旧免费Starter来源是不同字段，仍须在原出售按钮前保护。");
            controller.SellItem(); Assert.That(requests, Is.Zero);
            StringAssert.Contains("基础背包", controller.Status);
            Assert.That(Get(ReadView("bag"), "EntryBoundBackpack"), Is.True); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void AccountUnknownKeepsItsOriginalIntentForSaveRetryAndBlocksAnotherItem()
        {
            UseAccountCommandSource(); CaptureAccountRequests();
            Subscribe(typeof(InventorySaveRequestedEvent), _ => _localWrites++);
            Begin("card"); Assert.That(_ui.IsDragging, Is.True); Submit("SpellStorage", null, 1);
            var original = ((OnlineAccountInventoryCommandRequestedEvent)_request).Intent;
            EventBus<OnlineAccountInventoryCommandResultEvent>.Publish(new OnlineAccountInventoryCommandResultEvent
            { CallerId = _ui.GetInstanceID(), PlayerId = original.PlayerId, SceneHandle = original.SceneHandle,
                CommandId = original.CommandId, State = OnlineAccountInventoryCommandState.Unknown, Message = "尚未确认" });
            Begin("ordinary"); Assert.That(_ui.IsDragging, Is.False, "未知结果不能由另一件新拖拽覆盖。");
            _ui.Save();
            Assert.That(_requests, Is.EqualTo(2));
            var retry = ((OnlineAccountInventoryCommandRequestedEvent)_request).Intent;
            Assert.That(retry.CommandId, Is.EqualTo(original.CommandId)); Assert.That(retry.ExpectedVersion, Is.EqualTo(original.ExpectedVersion));
            Assert.That(retry.ItemId, Is.EqualTo(original.ItemId)); Assert.That(retry.TargetLocationKind, Is.EqualTo(original.TargetLocationKind));
            Assert.That(retry.TargetSlot, Is.EqualTo(original.TargetSlot)); Assert.That(_localWrites, Is.Zero); Assert.That(_accountDsRequests, Is.Zero);
            Assert.That(InventoryVersion(), Is.EqualTo(9u));
        }

        [Test]
        public void AccountSourceReplacementCancelsAnOldDragInsteadOfBorrowingTheNewProfileVersion()
        {
            UseAccountCommandSource(); CaptureAccountRequests(); Begin("card"); Assert.That(_ui.IsDragging, Is.True);
            RebindAccountCommandSource(10); Submit("SpellStorage", null, 1);
            Assert.That(_requests, Is.Zero); Assert.That(InventoryVersion(), Is.EqualTo(10u));
            Assert.That(_localWrites, Is.Zero); Assert.That(_accountDsRequests, Is.Zero);
        }

        [Test]
        public void AccountWarehouseTargetRechecksTheOriginalStationRangeWhenReleased()
        {
            UseAccountCommandSource(); CaptureAccountRequests();
            Begin("bag-bottle"); Assert.That(_ui.IsDragging, Is.True);
            _accountWarehouseActor.position = new Vector3(10, 0, 0);
            Submit("Warehouse", null, 0);
            Assert.That(_requests, Is.Zero, "走出作者的仓库区域后松手，不能凭WarehouseMode远程存入。");
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }
        [Test]
        public void AccountWarehouseOriginCannotBeDraggedOutsideTheOriginalStation()
        {
            UseAccountCommandSource(); CaptureAccountRequests();
            _accountWarehouseActor.position = new Vector3(10, 0, 0);
            Begin("stored-card");
            Assert.That(_ui.IsDragging, Is.False, "完整Profile拥有仓库读值不等于能在据点任意位置取物。");
            Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void AccountWarehouseInteractionOpensTheOriginalWarehouseCanvasAndKeepsTabSeparate()
        {
            UseAccountCommandSource();
            var warehouseRows = Track(new GameObject("OriginalWarehouseRows", typeof(RectTransform)));
            Set(_ui, "_warehouseRoot", warehouseRows.GetComponent<RectTransform>());
            var tabRoot = Track(new GameObject("OriginalTabWorkspace")); tabRoot.SetActive(false);
            var tab = tabRoot.AddComponent<SinglePlayerInventoryController>();
            Set(tab, "_session", Get(_ui, "_session")); Set(tab, "_hubAccess", Get(_ui, "_hubAccess"));
            Set(tab, "_backpackWorkspace", tabRoot.AddComponent<BackpackWorkspaceView>());
            Set(tab, "_otherWorkspace", _ui); Set(_ui, "_otherWorkspace", tab);
            tab.GetType().GetMethod("BindPresentationSource").Invoke(tab, new[] { _source });
            Invoke(tab, "OnEnable"); _nearbyLifecycles.Add(tab);
            _ui.Close(); tab.Close();

            // 使用原F交互结果事件；仅带仓库区域的原Canvas消费，Tab背包不能抢开第二份界面。
            EventBus<HubInteractionResultEvent>.Publish(new HubInteractionResultEvent
            { InventoryId = "account.player", Kind = HubInteractionKind.Warehouse, Success = true });
            Assert.That(_ui.IsOpen, Is.True, "合法账号仓库F事件必须穿过原UI消费入口。");
            Assert.That(_ui.WarehouseMode, Is.True); Assert.That(tab.IsOpen, Is.False);
            _ui.Close();
            tab.OpenWarehouse();
            Assert.That(_ui.IsOpen, Is.True, "从随身控制器调用仓库仍转交作者原仓库Canvas。");
            Assert.That(tab.IsOpen, Is.False); Assert.That(_localWrites, Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AccountHeldHudShowsConfirmedWandIdentityAndSelectionWithoutInventingResources(bool explicitScopeBinding)
        {
            UseAccountCommandSource();
            var texture = Track(new Texture2D(1, 1));
            var sprite = Track(Sprite.Create(texture, new Rect(0, 0, 1, 1), Vector2.zero));
            var catalog = new SerializedObject(_bindingCatalogAsset);
            var wand = catalog.FindProperty("_definitions").GetArrayElementAtIndex(0).objectReferenceValue;
            var definition = new SerializedObject(wand); definition.FindProperty("_icon").objectReferenceValue = sprite;
            definition.ApplyModifiedPropertiesWithoutUndo(); RebindAccountCommandSource(9);
            var bottleDefinition = new SerializedObject(_bindingBottleDefinition);
            bottleDefinition.FindProperty("_icon").objectReferenceValue = sprite; bottleDefinition.ApplyModifiedPropertiesWithoutUndo();
            var root = Track(new GameObject("OriginalAccountHeldHud")); root.SetActive(false);
            var hud = root.AddComponent<HeldItemHud>();
            var icon = Track(new GameObject("OriginalWandIcon", typeof(RectTransform), typeof(Image))).GetComponent<Image>();
            var frame = Track(new GameObject("OriginalSelectedFrame", typeof(RectTransform), typeof(Image))).GetComponent<Image>();
            var value = Track(new GameObject("OriginalManaValue", typeof(RectTransform), typeof(Text))).GetComponent<Text>();
            var mana = Track(new GameObject("OriginalManaBar", typeof(RectTransform), typeof(Image))).GetComponent<Image>();
            var cooldown = Track(new GameObject("OriginalCooldownBar", typeof(RectTransform), typeof(Image))).GetComponent<Image>();
            var toolIcon = Track(new GameObject("OriginalToolIcon", typeof(RectTransform), typeof(Image))).GetComponent<Image>();
            var cell = new HeldItemHud.Cell { Icon = icon, Value = value, Mana = mana, Cooldown = cooldown };
            var toolCell = new HeldItemHud.Cell { Icon = toolIcon };
            Set(cell, "Frame", frame);
            Set(cell, "Display", Activator.CreateInstance(Type("Game.UI.HeldItemHudValues"),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { value }, null));
            var cells = new HeldItemHud.Cell[10]; cells[0] = cell; cells[9] = toolCell;
            Set(hud, "_cells", cells); Set(hud, "_selectedFrame", sprite); Set(hud, "_ready", true);
            // Hub 原 HUD 只配置背包引用；副本由 Scope 显式 Bind。两条真实装配都必须读同一账号事实。
            if (explicitScopeBinding) hud.GetType().GetMethod("BindPresentationSource").Invoke(hud, new[] { _source });
            else hud.ConfigureVisibility(_ui, null, null, null);
            Invoke(hud, "Update");
            Assert.That(cell.ItemId, Is.EqualTo("wand")); Assert.That(icon.sprite, Is.SameAs(sprite)); Assert.That(icon.enabled, Is.True);
            Assert.That(toolCell.ItemId, Is.EqualTo("tool-bottle")); Assert.That(toolIcon.sprite, Is.SameAs(sprite));
            Assert.That(frame.sprite, Is.SameAs(sprite), "选中框必须跟随确认Profile的真实槽位。");
            Assert.That(value.text, Is.Empty); Assert.That(mana.enabled, Is.False); Assert.That(cooldown.enabled, Is.False);
            if (!explicitScopeBinding)
            {
                var previous = _source;
                RebindAccountCommandSource(10); Invoke(hud, "Update");
                Assert.That(Get(hud, "PresentationSource"), Is.SameAs(_source), "确认新Profile后必须跟随原背包换源，不能持有已释放的旧投影。");
                Assert.That(Get(hud, "PresentationSource"), Is.Not.SameAs(previous));
                Assert.That(cell.ItemId, Is.EqualTo("wand"));
                _ui.GetType().GetMethod("BindPresentationSource").Invoke(_ui, new object[] { null }); Invoke(hud, "Update");
                Assert.That(cell.ItemId, Is.Null); Assert.That(icon.enabled, Is.False, "账号源清空后不得回退旧单机Session或保留上一账号图标。");
                Assert.That(toolCell.ItemId, Is.Null); Assert.That(toolIcon.enabled, Is.False);
            }
            Assert.That(_localWrites, Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void OriginalContainerDetailsDescribeTheCurrentAreaCapabilities(bool accountHub)
        {
            if (accountHub) UseAccountCommandSource(); else UseContainerBindingSource();
            _ui.SelectItem("tool-bottle");
            var root = Track(new GameObject("OriginalContainerDetails", typeof(RectTransform))); root.SetActive(false);
            var view = root.AddComponent<InventoryWorkspaceView>(); Set(view, "_inventory", _ui);
            var palette = new GameObject("Palette", typeof(RectTransform)); palette.transform.SetParent(root.transform, false);
            Set(view, "_paletteRoot", palette.GetComponent<RectTransform>());
            foreach (string field in new[] { "_capacity", "_bagEquipment", "_containerDetails" })
            {
                var label = new GameObject(field, typeof(RectTransform), typeof(Text)); label.transform.SetParent(root.transform, false);
                Set(view, field, label.GetComponent<Text>());
            }
            view.GetType().GetMethod("Refresh").Invoke(view,
                new object[] { Array.CreateInstance(Type("Game.Run.InventoryItemSnapshot"), 0), 0, null });
            string text = ((Text)Get(view, "_containerDetails")).text;
            if (accountHub)
            {
                Assert.That(text, Does.Contain("据点仅整备/丢弃"));
                Assert.That(text, Does.Contain("实验场或副本"));
                Assert.That(text, Does.Not.Contain("E 采集").And.Not.Contain("H 持续"));
            }
            else Assert.That(text, Does.Contain("F 采集").And.Contain("H 持续倾倒"));
            Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void AccountFocusedPickupUsesTheRealNearbySourceAndKeepsUnknownIntentWithoutLocalWrites()
        {
            UseAccountCommandSource();
            var snapshot=_accountCommandSnapshot.CopyValue();snapshot.Version=10;
            OnlineOwnedItemSnapshot ground=null;
            foreach(var item in _accountCommandSnapshot.CopyValue().Items)if(item.InstanceId=="bag-bottle")ground=item;
            Assert.That(ground,Is.Not.Null);
            const string spawn="11111111111111111111111111111111";
            // 业务Profile的根物品无Parent；落地身份只在WorldItems中，Source才映射成运行时WorldOwner。
            ground.InstanceId="ground-bottle";ground.LocationKind=5;ground.ParentInstanceId="";
            ground.Slot=-1;ground.X=ground.Y=ground.QuarterTurns=0;
            var items=new List<OnlineOwnedItemSnapshot>(snapshot.Items);items.Add(ground);snapshot.Items=items.ToArray();
            snapshot.WorldItems=new[] {new OnlineAccountWorldItemSnapshot {InstanceId=ground.InstanceId,WorldOwnerId=spawn,
                Pose=new OnlineAccountWorldItemPose {SceneKey="test.hub",WorldSceneKey="test.hub",PositionX=.5f,RotationW=1,PoseKind=1}}};
            var args=new object[] {_bindingCatalog,snapshot,null,null};
            Assert.That((bool)Type("Game.Run.AccountInventoryPresentationSource").GetMethod("TryCreate").Invoke(null,args),Is.True,args[3] as string);
            var center=args[2];_unsubscribers.Add(()=>((IDisposable)center).Dispose());
            var actor=_accountWarehouseActor;var session=Get(_ui,"_session");
            var nearby=actor.gameObject.AddComponent(Type("Game.Run.NearbyPickupController"));_nearbyLifecycles.Add(nearby);
            Set(session,"_accountGroundSource",center);Set(session,"_accountNearby",nearby);
            Set(session,"WorldActor",actor);Set(session,"_worldSceneKey","test.hub");
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(_ui.GetInstanceID(),snapshot,out var error),Is.True,error);
            _accountCommandSnapshot=snapshot;RebindAccountCommandSource(10);
            var proxyRoot=Track(new GameObject("ConfirmedAccountGroundItem"));
            var proxy=proxyRoot.AddComponent(Type("Game.Run.WorldItemPickup"));
            Assert.That((bool)proxy.GetType().GetMethod("BindAccount").Invoke(proxy,new[] {center,(object)ground.InstanceId,actor}),Is.True);
            nearby.GetType().GetMethod("RefreshCandidates").Invoke(nearby,null);CaptureAccountRequests();
            Assert.That(Get(nearby,"Count"),Is.EqualTo(1));
            Assert.That((bool)nearby.GetType().GetMethod("TryCollectFocused").Invoke(nearby,null),Is.True);
            var first=((OnlineAccountInventoryCommandRequestedEvent)_request).Intent;
            Assert.That(first.Kind,Is.EqualTo(OnlineAccountInventoryCommandKind.Pickup));Assert.That(first.WorldOwnerId,Is.EqualTo(spawn));
            Assert.That(first.ExpectedVersion,Is.EqualTo(10u));Assert.That(first.WorldPose,Is.Null);
            EventBus<OnlineAccountInventoryCommandResultEvent>.Publish(new OnlineAccountInventoryCommandResultEvent
            {CallerId=nearby.GetInstanceID(),PlayerId="player",SceneHandle=(int)actor.gameObject.scene.handle,
                CommandId=first.CommandId,State=OnlineAccountInventoryCommandState.Unknown});
            Assert.That((bool)nearby.GetType().GetMethod("TryCollectFocused").Invoke(nearby,null),Is.True);
            Assert.That(_requests,Is.EqualTo(2));Assert.That(((OnlineAccountInventoryCommandRequestedEvent)_request).Intent.CommandId,Is.EqualTo(first.CommandId));
            var view=new object[] {ground.InstanceId,null};Assert.That((bool)_read.GetType().GetMethod("TryGetView").Invoke(_read,view),Is.True);
            Assert.That(Get(view[1],"ContainerTotal"),Is.EqualTo(17ul));Assert.That(Get(Get(view[1],"Location"),"Kind").ToString(),Is.EqualTo("WorldItem"));
            Assert.That(proxyRoot.activeSelf,Is.True);Assert.That(_localWrites,Is.Zero);Assert.That(_accountDsRequests,Is.Zero);
        }

        [TestCase(9u, true)]
        [TestCase(10u, false)]
        public void AccountCommittedResultRequiresItsInstalledFloorButAllowsAnUnchangedVersionNoOp(uint committedVersion, bool canBeginAnother)
        {
            UseAccountCommandSource(); CaptureAccountRequests();
            Begin("card"); Assert.That(_ui.IsDragging, Is.True);
            Submit("ProgramSlot", "wand", 0); // 原卡本来就在这个位置：合法no-op不应强增资产版本。
            var intent = ((OnlineAccountInventoryCommandRequestedEvent)_request).Intent;
            EventBus<OnlineAccountInventoryCommandResultEvent>.Publish(new OnlineAccountInventoryCommandResultEvent
            { CallerId = _ui.GetInstanceID(), PlayerId = intent.PlayerId, SceneHandle = intent.SceneHandle,
                CommandId = intent.CommandId, State = OnlineAccountInventoryCommandState.Committed, CommittedVersion = committedVersion });
            Begin("ordinary");
            Assert.That(_ui.IsDragging, Is.EqualTo(canBeginAnother), "只有原请求确认且Source实际达到收据水位才释放下一操作；同版本no-op也有效。");
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_requests, Is.EqualTo(1));
            Assert.That(_localWrites, Is.Zero); Assert.That(_accountDsRequests, Is.Zero);
        }

        [Test]
        public void OwnedCardDragPublishesVersionedPlaceIntentWithoutEditingReadModel()
        {
            CaptureRequests(); Begin("card");
            Assert.That(_ui.IsDragging, Is.True, "线上自有法术卡可使用原拖拽表现；提交仍必须交给 DS。");
            Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(_ui.IsDragging, Is.False);
            Assert.That(Get(_request, "ActorId"), Is.EqualTo(1ul)); Assert.That(Get(_request, "CallerId"), Is.EqualTo(_ui.GetInstanceID()));
            var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "CommandId"), Is.Not.EqualTo(0ul));
            Assert.That(Get(intent, "ConnectionEpoch"), Is.EqualTo(7u));
            Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(9u));
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(17u));
            Assert.That(Get(intent, "TargetProgramRevision"), Is.EqualTo(17u));
            Assert.That(Get(intent, "ItemId"), Is.EqualTo("card")); Assert.That(Get(intent, "SourceWandId"), Is.EqualTo("wand"));
            Assert.That(Get(intent, "TargetWandId"), Is.EqualTo("wand")); Assert.That(Get(intent, "TargetSlot"), Is.EqualTo(1));
            Assert.That(Get(intent, "ToSpellStorage"), Is.False);
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void InstalledCardGroundDropPublishesVersionedIntentWithoutRemovingTheCard()
        {
            CaptureRequests(); Begin("card"); Submit("Ground", null, -1);
            Assert.That(_ui.IsDragging, Is.False); Assert.That(_requests, Is.EqualTo(1)); Assert.That(_localWrites, Is.Zero);
            var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("DropItem"));
            Assert.That(Get(intent, "SourceWandId"), Is.EqualTo("wand"));
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(17u));
            Assert.That(CardSlot(), Is.Zero); Assert.That(InventoryVersion(), Is.EqualTo(9u));
        }

        [Test]
        public void InstalledCardCanRequestRealBackpackGridWithoutEditingTheProgram()
        {
            UseContainerBindingSource(); CaptureRequests(); Begin("card");
            _ui.SubmitToBackpack(2, 3, "bag");
            Assert.That(_requests, Is.EqualTo(1), "原法杖卡拖到包必须进入同一权威命令网关。");
            var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("MoveOwned"));
            Assert.That(Get(intent, "TargetBackpackId"), Is.EqualTo("bag"));
            Assert.That(Get(intent, "GridX"), Is.EqualTo(2)); Assert.That(Get(intent, "GridY"), Is.EqualTo(3));
            Assert.That(Get(intent, "SourceWandId"), Is.EqualTo("wand"));
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(17u));
            Assert.That(CardSlot(), Is.Zero); Assert.That(_localWrites, Is.Zero);
        }

        [TestCase("card")]
        [TestCase("wand")]
        [TestCase("ordinary")]
        public void RealBackpackCardsWandsAndItemsCanEnterOriginalDrag(string id)
        {
            UseContainerBindingSource();
            if (id != "ordinary")
            {
                var next = ContainerBindingSnapshot(10, 2); var assets = (OnlineOwnedAssetSnapshot)Get(next, "Assets");
                var copy = id == "card"
                    ? new OnlineOwnedItemSnapshot { InstanceId = "bag-card", ContentId = "ui.online.spell", Revision = 1,
                        LocationKind = 1, ParentInstanceId = "bag", Slot = -1, X = 3, RemainingCharges = 2 }
                    : new OnlineOwnedItemSnapshot { InstanceId = "bag-wand", ContentId = "ui.online.wand", Revision = 1,
                        LocationKind = 1, ParentInstanceId = "bag", Slot = -1, X = 3, UseTemplateWandStats = true };
                assets.Items = new List<OnlineOwnedItemSnapshot>(assets.Items) { copy }.ToArray();
                if (id == "card")
                {
                    var old = (Array)Get(next, "SpellUses"); var uses = Array.CreateInstance(old.GetType().GetElementType(), 2);
                    uses.SetValue(old.GetValue(0), 0); var use = Activator.CreateInstance(old.GetType().GetElementType());
                    Set(use, "InstanceId", copy.InstanceId); Set(use, "Eligible", false); uses.SetValue(use, 1); Set(next, "SpellUses", uses);
                }
                else
                {
                    var old = (Array)Get(next, "Wands"); var wandType = old.GetType().GetElementType();
                    var wands = Array.CreateInstance(wandType, 2); wands.SetValue(old.GetValue(0), 0);
                    var wand = Activator.CreateInstance(wandType); Set(wand, "InstanceId", copy.InstanceId);
                    Set(wand, "ProgramRevision", 31u); Set(wand, "CurrentMana", 13f); Set(wand, "CooldownRemaining", .5f);
                    wands.SetValue(wand, 1); Set(next, "Wands", wands);
                }
                InstallContainerBindingSnapshot(next); id = copy.InstanceId;
            }
            CaptureRequests(); Begin(id);
            Assert.That(_ui.IsDragging, Is.True, "包内真实物品不应再被容器/卡槽白名单拦在抓起阶段。");
            Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void OccupiedProgramSlotUsesTheOriginalCardSwapEntry()
        {
            CaptureRequests(); Begin("card"); Submit("ProgramSlot", "wand", 0);
            Assert.That(_requests, Is.EqualTo(1));
            Assert.That(Get(Get(_request, "Intent"), "TargetSlot"), Is.Zero);
            Assert.That(CardSlot(), Is.Zero); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void BackpackWandProgramUsesAuthoritativeRevisionWhenItsInstalledCardMovesToBag()
        {
            UseContainerBindingSource();
            var snapshot = ContainerBindingSnapshot(10, 2); var assets = (OnlineOwnedAssetSnapshot)Get(snapshot, "Assets");
            assets.Items = new List<OnlineOwnedItemSnapshot>(assets.Items)
            {
                new OnlineOwnedItemSnapshot { InstanceId = "spare-wand", ContentId = "ui.online.wand", Revision = 1,
                    LocationKind = 1, ParentInstanceId = "bag", Slot = -1, X = 3, UseTemplateWandStats = true },
                new OnlineOwnedItemSnapshot { InstanceId = "spare-card", ContentId = "ui.online.spell", Revision = 1,
                    LocationKind = 2, ParentInstanceId = "spare-wand", Slot = 1, RemainingCharges = 5 }
            }.ToArray();
            // 备用杖同样携带服务器真实程序/资源读数；73刻意远离初始1，防导入端用默认版本伪装成功。
            var previousWands = (Array)Get(snapshot, "Wands"); var wandType = previousWands.GetType().GetElementType();
            var wands = Array.CreateInstance(wandType, 2); wands.SetValue(previousWands.GetValue(0), 0);
            var spareWand = Activator.CreateInstance(wandType); Set(spareWand, "InstanceId", "spare-wand");
            Set(spareWand, "ProgramRevision", 73u); Set(spareWand, "CurrentMana", 19f); Set(spareWand, "CooldownRemaining", 2.5f);
            wands.SetValue(spareWand, 1); Set(snapshot, "Wands", wands);
            var previousUses = (Array)Get(snapshot, "SpellUses"); var useType = previousUses.GetType().GetElementType();
            var uses = Array.CreateInstance(useType, 2); uses.SetValue(previousUses.GetValue(0), 0);
            var spareUse = Activator.CreateInstance(useType); Set(spareUse, "InstanceId", "spare-card"); Set(spareUse, "Eligible", false);
            uses.SetValue(spareUse, 1); Set(snapshot, "SpellUses", uses);
            InstallContainerBindingSnapshot(snapshot);

            var read = Get(_source, "WandsReadOnly"); var programArgs = new object[] { "spare-wand", null };
            Assert.That((bool)Type("Game.Run.IWandLoadoutReadOnly").GetMethod("TryGetProgram").Invoke(read, programArgs), Is.True);
            Assert.That(Get(programArgs[1], "ProgramRevision"), Is.EqualTo(73u));
            var stateArgs = new object[] { "spare-wand", null };
            Assert.That((bool)Type("Game.Run.IWandLoadoutReadOnly").GetMethod("TryGetState").Invoke(read, stateArgs), Is.True);
            Assert.That(Get(stateArgs[1], "SlotIndex"), Is.EqualTo(-1)); Assert.That(Get(stateArgs[1], "Selected"), Is.False);
            Assert.That(Get(stateArgs[1], "Carried"), Is.True); Assert.That(Get(stateArgs[1], "CurrentMana"), Is.EqualTo(19f));
            Assert.That(Get(stateArgs[1], "CooldownRemaining"), Is.EqualTo(2.5f));
            var slotArgs = new object[] { -1, null };
            Assert.That((bool)Type("Game.Run.IWandLoadoutReadOnly").GetMethod("TryGetSlot").Invoke(read, slotArgs), Is.False,
                "Slot=-1只表达包内携带，不能变成第四个装备/选中槽。");

            CaptureRequests(); Begin("spare-card"); Assert.That(_ui.IsDragging, Is.True);
            _ui.SubmitToBackpack(4, 3, "bag"); Assert.That(_requests, Is.EqualTo(1));
            var intent = Get(_request, "Intent"); Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("MoveOwned"));
            Assert.That(Get(intent, "SourceWandId"), Is.EqualTo("spare-wand"));
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(73u));
            Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(10u));
            Assert.That(Get(ReadView("spare-card"), "RemainingCharges"), Is.EqualTo(5));
            Assert.That(Get(Get(ReadView("spare-card"), "Location"), "OwnerId"), Is.EqualTo("spare-wand"));
            Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void CardToSpellStorageUsesRealStorageTargetAndKeepsAssetsUntilSnapshot()
        {
            CaptureRequests(); Begin("card"); Submit("SpellStorage", "player", 0);
            Assert.That(_requests, Is.EqualTo(1)); var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "ToSpellStorage"), Is.False);
            Assert.That(Get(intent, "TargetLocationKind"), Is.EqualTo(7));
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(17u));
            Assert.That(Get(intent, "TargetProgramRevision"), Is.EqualTo(0u));
            Assert.That(Get(intent, "TargetWandId"), Is.Null.Or.Empty);
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void MidDragSnapshotDoesNotRewriteCapturedSourceVersionButTargetUsesCurrentProgram()
        {
            CaptureRequests(); Begin("card");
            var newer = Snapshot(); Set(newer, "SnapshotSequence", 2ul);
            ((OnlineOwnedAssetSnapshot)Get(newer, "Assets")).InventoryVersion = 10u;
            Set(((Array)Get(newer, "Wands")).GetValue(0), "ProgramRevision", 21u);
            var args = new[] { newer, null };
            Assert.That((bool)_source.GetType().GetMethod("TryInstall").Invoke(_source, args), Is.True, args[1] as string);
            Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.EqualTo(1)); var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(9u));
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(17u));
            Assert.That(Get(intent, "TargetProgramRevision"), Is.EqualTo(21u));
            Assert.That(InventoryVersion(), Is.EqualTo(10u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void EquippedWandDragRequestsItsRealEquipmentSlotWithoutLocalInventoryCommand()
        {
            CaptureRequests(); Begin("wand");
            Assert.That(_ui.IsDragging, Is.True); Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(_localWrites, Is.Zero);
            Assert.That(Get(Get(_request, "Intent"), "Kind").ToString(), Is.EqualTo("EquipWand"));
            Assert.That(Get(Get(_request, "Intent"), "TargetSlot"), Is.Zero);
        }

        [TestCase("bag-bottle", 2)]
        [TestCase("tool-bottle", 0)]
        public void RealContainerDragToToolSlotPublishesOnlyVersionedBindIntent(string itemId, int slot)
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites();
            Begin(itemId);
            Assert.That(_ui.IsDragging, Is.True, "真实装备包内或已装备的容器必须能进入原拖拽表现。");
            SubmitShortcut(slot);
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(_ui.IsDragging, Is.False);
            Assert.That(Get(_request, "ActorId"), Is.EqualTo(1ul));
            Assert.That(Get(_request, "CallerId"), Is.EqualTo(_ui.GetInstanceID()));
            var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("BindTool"));
            Assert.That(Get(intent, "CommandId"), Is.Not.EqualTo(0ul));
            Assert.That(Get(intent, "ConnectionEpoch"), Is.EqualTo(7u));
            Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(9u));
            Assert.That(Get(intent, "ItemId"), Is.EqualTo(itemId)); Assert.That(Get(intent, "TargetSlot"), Is.EqualTo(slot));
            Assert.That(Get(intent, "SourceWandId"), Is.Null.Or.Empty); Assert.That(Get(intent, "TargetWandId"), Is.Null.Or.Empty);
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(0u)); Assert.That(Get(intent, "TargetProgramRevision"), Is.EqualTo(0u));
            Assert.That(Get(intent, "ToSpellStorage"), Is.False); Assert.That(Get(intent, "TransferBudget"), Is.EqualTo(0ul));
            Assert.That(Get(intent, "AimOrigin"), Is.EqualTo(Vector3.zero)); Assert.That(Get(intent, "AimDirection"), Is.EqualTo(Vector3.zero));
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
            AssertToolBinding(slot, null); AssertToolBinding(6, "tool-bottle");
            Assert.That(Get(Get(ReadView("bag-bottle"), "Location"), "Kind").ToString(), Is.EqualTo("Backpack"));
            Assert.That(Get(ReadView("bag-bottle"), "ContainerTotal"), Is.EqualTo(17ul));
            Assert.That(Get(Get(_source, "ToolbeltReadOnly"), "SelectedSlot"), Is.EqualTo(0), "绑定不等于选中容器，仍需后续数字键选择。");
        }

        [Test]
        public void ContainerInspectionDoesNotBindOrSelectTheBottle()
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites();
            _ui.SelectItem("bag-bottle"); _ui.ClickItem("bag-bottle");
            Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
            AssertToolBinding(2, null); AssertToolBinding(6, "tool-bottle");
            Assert.That(InventoryVersion(), Is.EqualTo(9u));
            Assert.That(Get(Get(_source, "ToolbeltReadOnly"), "SelectedSlot"), Is.EqualTo(0));
        }

        [TestCase(-1)]
        [TestCase(7)]
        [TestCase(255)]
        public void InvalidToolSlotCancelsContainerDragWithoutEitherWritePath(int slot)
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites(); Begin("bag-bottle");
            Assert.That(_ui.IsDragging, Is.True);
            SubmitShortcut(slot);
            Assert.That(_ui.IsDragging, Is.False); Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
            AssertToolBinding(2, null); Assert.That(InventoryVersion(), Is.EqualTo(9u));
        }

        [TestCase("card")]
        [TestCase("wand")]
        public void ToolBindingGatewayNeverPromotesCardsWandsOrOtherUsableItems(string itemId)
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites(); Begin(itemId);
            SubmitShortcut(2);
            Assert.That(_ui.IsDragging, Is.False); Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
            AssertToolBinding(2, null); Assert.That(InventoryVersion(), Is.EqualTo(9u));
        }

        [Test]
        public void OrdinaryUsableItemRequestsTheOriginalToolSlotThroughMoveOwned()
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites(); Begin("ordinary"); SubmitShortcut(2);
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(_localWrites, Is.Zero);
            var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("MoveOwned"));
            Assert.That(Get(intent, "TargetLocationKind"), Is.EqualTo(8)); Assert.That(Get(intent, "TargetSlot"), Is.EqualTo(2));
            Assert.That(Get(Get(ReadView("ordinary"), "Location"), "Kind").ToString(), Is.EqualTo("Backpack"));
        }

        [TestCase("Warehouse", "warehouse")]
        [TestCase("EquippedTool", "other-player")]
        [TestCase("WorldItem", "ground")]
        public void StaleWarehouseForeignOrGroundViewsCannotGrantAnOnlineToolBinding(string locationKind, string owner)
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites();
            // 模拟尚未回收的旧View值；该实例不在当前真实Source中，不能凭种类/能力字段取得写资格。
            BeginView(UnownedContainerView(locationKind, owner)); SubmitShortcut(2);
            Assert.That(_ui.IsDragging, Is.False); Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
            AssertToolBinding(2, null); Assert.That(InventoryVersion(), Is.EqualTo(9u));
        }

        [TestCase("same-identity")]
        [TestCase("new-epoch")]
        [TestCase("old-epoch")]
        [TestCase("other-actor")]
        [TestCase("no-source")]
        public void RebindingPresentationSourceEndsTheCapturedContainerDrag(string replacement)
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites(); Begin("bag-bottle");
            Assert.That(_ui.IsDragging, Is.True);
            object next = replacement == "no-source" ? null : CreateContainerBindingSource(
                replacement == "new-epoch" ? 8u : replacement == "old-epoch" ? 6u : 7u,
                replacement == "other-actor" ? 2ul : 1ul, replacement == "other-actor" ? "other-player" : "player");
            var previous = _source;
            _ui.GetType().GetMethod("BindPresentationSource").Invoke(_ui, new[] { next });
            _source = next; _read = next != null ? Get(next, "ReadOnly") : null;
            (previous as IDisposable)?.Dispose(); Set(_ui, "_open", true);
            SubmitShortcut(2);
            Assert.That(_ui.IsDragging, Is.False); Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero,
                "新的相同身份Source也不能继承上一次拖拽，更不能让旧Actor/Epoch操作新源。");
        }

        [Test]
        public void ContainerBindingKeepsTheVersionCapturedWhenDraggingStarted()
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites(); Begin("bag-bottle");
            Assert.That(_ui.IsDragging, Is.True);
            InstallContainerBindingSnapshot(ContainerBindingSnapshot(version: 10u, sequence: 2ul));
            SubmitShortcut(2);
            Assert.That(_requests, Is.EqualTo(1));
            Assert.That(Get(Get(_request, "Intent"), "ExpectedInventoryVersion"), Is.EqualTo(9u),
                "不能在拖动期间偷换为新版本；是否仍可绑定由DS按原版本裁决。");
            Assert.That(Get(Get(_request, "Intent"), "ConnectionEpoch"), Is.EqualTo(7u));
            Assert.That(InventoryVersion(), Is.EqualTo(10u)); Assert.That(_localWrites, Is.Zero); AssertToolBinding(2, null);
        }

        [Test]
        public void ContainerBindingReceiptWaitsForConfirmedSourceWithoutOptimisticToolChanges()
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites(); Begin("bag-bottle");
            Assert.That(_ui.IsDragging, Is.True); SubmitShortcut(2);
            Assert.That(_requests, Is.EqualTo(1));
            Receipt((ulong)Get(Get(_request, "Intent"), "CommandId"), 7u, _ui.GetInstanceID(), 1ul);
            AssertToolBinding(2, null); Assert.That(InventoryVersion(), Is.EqualTo(9u));
            Assert.That(_status.text, Does.Contain("等待"));
            InstallContainerBindingSnapshot(ContainerBindingSnapshot(version: 10u, sequence: 2ul, bindBagBottle: true));
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = "inventory", Version = 10u });
            Invoke(_ui, "LateUpdate");
            AssertToolBinding(2, "bag-bottle"); Assert.That(InventoryVersion(), Is.EqualTo(10u));
            Assert.That(Get(ReadView("bag-bottle"), "ContainerTotal"), Is.EqualTo(17ul));
            Assert.That(Get(Get(_source, "ToolbeltReadOnly"), "SelectedSlot"), Is.EqualTo(0));
            Assert.That(_status.text, Is.EqualTo("库存已更新。")); Assert.That(_localWrites, Is.Zero);
        }

        [TestCase("Warehouse", null, -1)]
        [TestCase("ProgramSlot", "wand", 1)]
        public void SupportingContainerDragDoesNotOpenUnapprovedDropDestinations(string kind, string owner, int slot)
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites(); Begin("bag-bottle");
            Assert.That(_ui.IsDragging, Is.True); Submit(kind, owner, slot);
            Assert.That(_ui.IsDragging, Is.False); Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
            Assert.That(Get(Get(ReadView("bag-bottle"), "Location"), "Kind").ToString(), Is.EqualTo("Backpack"));
            AssertToolBinding(2, null);
        }

        [TestCase("bag-bottle")]
        [TestCase("tool-bottle")]
        public void CarriedContainerGroundDropUsesOwnerIntentAndWaitsForConfirmedAssets(string itemId)
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites(); Begin(itemId);
            Assert.That(_ui.IsDragging, Is.True); Submit("Ground", null, -1);
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(_ui.IsDragging, Is.False);
            var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("DropContainer"));
            Assert.That(Get(intent, "ItemId"), Is.EqualTo(itemId)); Assert.That(Get(intent, "TargetSlot"), Is.EqualTo(0));
            Assert.That(Get(intent, "ConnectionEpoch"), Is.EqualTo(7u));
            Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(9u));
            Assert.That(Get(intent, "WorldOwnerId"), Is.Null.Or.Empty); Assert.That(Get(intent, "TargetBackpackId"), Is.Null.Or.Empty);
            Assert.That(Get(intent, "GridX"), Is.EqualTo(0)); Assert.That(Get(intent, "GridY"), Is.EqualTo(0));
            Assert.That(Get(intent, "QuarterTurns"), Is.EqualTo(0)); Assert.That(Get(intent, "AutoPlace"), Is.False);
            AssertNeutralTransferFields(intent);
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
            Assert.That(Get(Get(ReadView(itemId), "Location"), "Kind").ToString(), Is.EqualTo(itemId == "bag-bottle" ? "Backpack" : "EquippedTool"));
        }

        [Test]
        public void SelectedContainerDropReusesTheOriginalDropSelectedEntry()
        {
            UseContainerBindingSource(); CaptureRequests(); CaptureLocalToolWrites();
            _ui.SelectItem("bag-bottle"); _ui.DropSelected();
            Assert.That(_requests, Is.EqualTo(1));
            Assert.That(Get(Get(_request, "Intent"), "Kind").ToString(), Is.EqualTo("DropContainer"));
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void SharedGroundContainerDragTargetsOnlyTheRealEquippedBackpackGrid()
        {
            UseContainerBindingSource(); ConfigureGroundNearby(); CaptureRequests(); CaptureLocalToolWrites();
            Begin(GroundA); Assert.That(_ui.IsDragging, Is.True); _ui.RotateDrag();
            _ui.GetType().GetMethod("SubmitToBackpack").Invoke(_ui, new object[] { 2, 3, "bag" });
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(_ui.IsDragging, Is.False);
            var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("PickupContainer"));
            Assert.That(Get(intent, "ItemId"), Is.EqualTo(GroundA)); Assert.That(Get(intent, "WorldOwnerId"), Is.EqualTo(GroundOwnerA));
            Assert.That(Get(intent, "AutoPlace"), Is.False); Assert.That(Get(intent, "TargetBackpackId"), Is.EqualTo("bag"));
            Assert.That(Get(intent, "GridX"), Is.EqualTo(2)); Assert.That(Get(intent, "GridY"), Is.EqualTo(3));
            Assert.That(Get(intent, "QuarterTurns"), Is.EqualTo(1)); Assert.That(Get(intent, "TargetSlot"), Is.EqualTo(0));
            Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(9u)); AssertNeutralTransferFields(intent);
            Assert.That(Get(Get(ReadView(GroundA), "Location"), "Kind").ToString(), Is.EqualTo("WorldItem"));
            Assert.That(Get(ReadView(GroundA), "ContainerTotal"), Is.EqualTo(23ul));
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }

        [TestCase("warehouse")]
        [TestCase("other-bag")]
        public void SharedContainerCannotBeDraggedIntoAnUnownedBackpack(string target)
        {
            UseContainerBindingSource(); ConfigureGroundNearby(); CaptureRequests(); CaptureLocalToolWrites();
            Begin(GroundA); Assert.That(_ui.IsDragging, Is.True);
            _ui.GetType().GetMethod("SubmitToBackpack").Invoke(_ui, new object[] { 0, 0, target });
            Assert.That(_ui.IsDragging, Is.False); Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
        }

        [TestCase("far")]
        [TestCase("missing-proxy")]
        public void ASharedGroundReadValueAloneDoesNotGrantNearbyPickup(string defect)
        {
            UseContainerBindingSource(); var nearby = ConfigureGroundNearby(); CaptureRequests(); CaptureLocalToolWrites();
            if (defect == "far") _groundProxyA.transform.position = Vector3.right * 4;
            else Object.DestroyImmediate(_groundProxyA.gameObject);
            Invoke(nearby, "RefreshCandidates"); Begin(GroundA);
            Assert.That(_ui.IsDragging, Is.False); _ui.PickupSelected();
            Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void OriginalTabPickupButtonUsesAutoPlaceWithoutPretendingItCollectedTheItem()
        {
            UseContainerBindingSource(); ConfigureGroundNearby(); CaptureRequests(); CaptureLocalToolWrites();
            _ui.SelectItem(GroundA); _ui.PickupSelected();
            Assert.That(_requests, Is.EqualTo(1)); var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("PickupContainer"));
            Assert.That(Get(intent, "AutoPlace"), Is.True); Assert.That(Get(intent, "TargetBackpackId"), Is.Null.Or.Empty);
            Assert.That(Get(intent, "GridX"), Is.EqualTo(0)); Assert.That(Get(intent, "GridY"), Is.EqualTo(0));
            Assert.That(Get(intent, "QuarterTurns"), Is.EqualTo(0)); Assert.That(Get(intent, "WorldOwnerId"), Is.EqualTo(GroundOwnerA));
            AssertNeutralTransferFields(intent); Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
            Assert.That(_groundProxyA.gameObject.activeSelf, Is.True, "请求发送不能隐藏代理或先搬动只读源。");
        }

        [Test]
        public void OnlineNearbyBatchWaitsForTheExactReceiptAndOwnerBeforeSendingNextVersion()
        {
            UseContainerBindingSource(); var nearby = ConfigureGroundNearby(); CaptureRequests(); CaptureLocalToolWrites();
            var args = new object[] { 0 };
            Assert.That(nearby.GetType().GetMethod("CollectAll").Invoke(nearby, args), Is.EqualTo(0), "排队不等于资产已经拾取。");
            Assert.That(_requests, Is.EqualTo(1)); var first = _request; var intent = Get(first, "Intent");
            Assert.That(Get(intent, "ItemId"), Is.EqualTo(GroundA));
            Receipt((ulong)Get(intent, "CommandId"), 7u, (int)Get(first, "CallerId") + 1, 1ul);
            Invoke(nearby, "Update"); Assert.That(_requests, Is.EqualTo(1), "他人的收据不能推进批量队列。");
            Receipt((ulong)Get(intent, "CommandId"), 7u, (int)Get(first, "CallerId"), 1ul);
            Invoke(nearby, "Update"); Assert.That(_requests, Is.EqualTo(1), "成功收据仍须等待真实Owner取得该实例。");
            InstallContainerBindingSnapshot(GroundBindingSnapshot(version: 10, sequence: 3, pickedFirst: true));
            Invoke(nearby, "Update");
            Assert.That(_requests, Is.EqualTo(2));
            Assert.That(Get(Get(_request, "Intent"), "ItemId"), Is.EqualTo(GroundB));
            Assert.That(Get(Get(_request, "Intent"), "ExpectedInventoryVersion"), Is.EqualTo(10u));
            Assert.That(Get(Get(ReadView(GroundA), "Location"), "Kind").ToString(), Is.EqualTo("Backpack"));
            Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void OnlineNearbyBatchEndsOnSourceReleaseAndIgnoresItsLateReceipt()
        {
            UseContainerBindingSource(); var nearby = ConfigureGroundNearby(); CaptureRequests();
            nearby.GetType().GetMethod("CollectAll").Invoke(nearby, new object[] { 0 });
            Assert.That(_requests, Is.EqualTo(1)); var first = _request;
            nearby.GetType().GetMethod("ConfigureOnline").Invoke(nearby, new object[] { null, Get(nearby, "Player") });
            Assert.That(Get(nearby, "HasPendingOnlinePickup"), Is.False);
            Receipt((ulong)Get(Get(first, "Intent"), "CommandId"), 7u, (int)Get(first, "CallerId"), 1ul);
            Invoke(nearby, "Update");
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(Get(nearby, "Count"), Is.EqualTo(0));
        }

        [Test]
        public void FailedOnlineBatchItemNeverPublishesAFalsePickupSuccess()
        {
            UseContainerBindingSource(); var nearby = ConfigureGroundNearby(); CaptureRequests();
            int successes = 0;
            Subscribe(typeof(NearbyPickupResultEvent), result => { if ((bool)Get(result, "Success")) successes++; });
            nearby.GetType().GetMethod("CollectAll").Invoke(nearby, new object[] { 0 });
            Assert.That(_requests, Is.EqualTo(1)); var first = _request;
            Receipt((ulong)Get(Get(first, "Intent"), "CommandId"), 7u, (int)Get(first, "CallerId"), 1ul, OnlineInventoryCommandFailure.NoCapacity);
            Invoke(nearby, "Update"); Assert.That(_requests, Is.EqualTo(1), "失败收据中的更新版本也不能被跳过后立即沿旧版本洪发。");
            InstallContainerBindingSnapshot(GroundBindingSnapshot(version: 10, sequence: 3));
            Invoke(nearby, "Update");
            Assert.That(successes, Is.Zero); Assert.That(_requests, Is.EqualTo(2));
            Assert.That(Get(Get(_request, "Intent"), "ExpectedInventoryVersion"), Is.EqualTo(10u));
            Assert.That(Get(Get(ReadView(GroundA), "Location"), "Kind").ToString(), Is.EqualTo("WorldItem"));
        }

        [Test]
        public void GroundRevisionRefreshesNearbyEvenWhenPersonalInventoryVersionStaysUnchanged()
        {
            UseContainerBindingSource(); var nearby = ConfigureGroundNearby(); int changed = 0;
            Subscribe(typeof(NearbyPickupListChangedEvent), result => { if ((string)Get(result, "InventoryId") == "inventory") changed++; });
            var snapshot = GroundBindingSnapshot(sequence: 3); var ground = Get(snapshot, "Ground");
            Set(ground, "Version", 2u);
            var changedItem = ((Array)Get(ground, "Items")).GetValue(0); Set(changedItem, "Revision", 2u);
            Set(Get(changedItem, "Item"), "Revision", 2u);
            var component = ((OnlineOwnedContainerStateSnapshot)Get(changedItem, "ContainerState")).Components[0];
            component.Quantity = 24;
            InstallContainerBindingSnapshot(snapshot); Invoke(nearby, "RefreshCandidates");
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(changed, Is.EqualTo(1));
            Assert.That(Get(ReadView(GroundA), "ContainerTotal"), Is.EqualTo(24ul));
        }

        [Test]
        public void OriginalTabSolidPickupPreservesExactQuantityUntilConfirmedSnapshot()
        {
            UseSolidBindingSource(); ConfigureSolidNearby(); CaptureRequests(); CaptureLocalToolWrites();
            var material = ReadView(GroundA);
            Assert.That(Get(material, "MaterialKey"), Is.EqualTo((byte)5));
            Assert.That(Get(material, "MaterialQuantity"), Is.EqualTo(256ul), "原P7冰源必须显示真实256GMU，不能从1024容量补满。");
            // 复用原通用物品图验证玩家看到的数量；测试不新建一套线上材料UI。
            var root = Track(new GameObject("Solid quantity view", typeof(RectTransform), typeof(Image)));
            var view = root.AddComponent<InventoryItemView>();
            var captionRoot = new GameObject("Caption", typeof(RectTransform), typeof(Text));
            captionRoot.transform.SetParent(root.transform, false); var caption = captionRoot.GetComponent<Text>(); Set(view, "_label", caption);
            view.GetType().GetMethod("Bind").Invoke(view, new[] { _ui, material, (object)InventoryItemDisplay.Row, 64f, 1, -1 });
            Assert.That(caption.text, Does.Contain("256")); Assert.That(caption.text, Does.Not.Contain("1024"));
            _ui.SelectItem(GroundA); _ui.PickupSelected();
            Assert.That(_requests, Is.EqualTo(1)); var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("PickupContainer"), "沿用已批准实物拾取的历史Kind8。");
            Assert.That(Get(intent, "ItemId"), Is.EqualTo(GroundA)); Assert.That(Get(intent, "WorldOwnerId"), Is.EqualTo(GroundOwnerA));
            Assert.That(Get(intent, "AutoPlace"), Is.True); Assert.That(InventoryVersion(), Is.EqualTo(9u));
            Assert.That(Get(ReadView(GroundA), "MaterialQuantity"), Is.EqualTo(256ul));
            Assert.That(_groundProxyA.gameObject.activeSelf, Is.True); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void OriginalSolidGroundDragOnlyRequestsItsRealEquippedBackpackGrid()
        {
            UseSolidBindingSource(); ConfigureSolidNearby(); CaptureRequests(); CaptureLocalToolWrites();
            Begin(GroundA); Assert.That(_ui.IsDragging, Is.True, "已批准固体应复用原附近到包内拖拽。");
            _ui.GetType().GetMethod("SubmitToBackpack").Invoke(_ui, new object[] { 2, 3, "bag" });
            Assert.That(_requests, Is.EqualTo(1)); var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("PickupContainer"));
            Assert.That(Get(intent, "TargetBackpackId"), Is.EqualTo("bag")); Assert.That(Get(intent, "AutoPlace"), Is.False);
            Assert.That(Get(intent, "GridX"), Is.EqualTo(2)); Assert.That(Get(intent, "GridY"), Is.EqualTo(3));
            AssertNeutralTransferFields(intent); Assert.That(Get(ReadView(GroundA), "MaterialQuantity"), Is.EqualTo(256ul));
            Assert.That(_localWrites, Is.Zero); Assert.That(_ui.IsDragging, Is.False);
            // 新授权复用单机真实Item的丢地规则；这第二条应是独立DropItem，不能是重复拾取或工具绑定。
            Begin(SolidStack); Submit("Ground", null, -1);
            Assert.That(_requests, Is.EqualTo(2)); var drop = Get(_request, "Intent");
            Assert.That(Get(drop, "Kind").ToString(), Is.EqualTo("DropItem"));
            Assert.That(Get(drop, "ItemId"), Is.EqualTo(SolidStack)); Assert.That(Get(drop, "ExpectedInventoryVersion"), Is.EqualTo(9u));
            Assert.That(Get(drop, "WholeStack"), Is.True); Assert.That(Get(drop, "WorldOwnerId"), Is.Null.Or.Empty);
            Assert.That(Get(drop, "TargetLocationKind"), Is.Zero); AssertNeutralTransferFields(drop);
            Assert.That(Get(ReadView(SolidStack), "MaterialQuantity"), Is.EqualTo(990ul));
            Begin(SolidStack); SubmitShortcut(0);
            Assert.That(_requests, Is.EqualTo(2)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void SolidPartialMergeWaitsForBothOwnerGainAndMatchingGroundRemainder()
        {
            UseSolidBindingSource(); var nearby = ConfigureSolidNearby(); CaptureRequests();
            int successes = 0; Subscribe(typeof(NearbyPickupResultEvent), result => { if ((bool)Get(result, "Success")) successes++; });
            Assert.That(nearby.GetType().GetMethod("TryCollectFocused").Invoke(nearby, null), Is.EqualTo(true));
            var request = _request; var intent = Get(request, "Intent");
            Receipt((ulong)Get(intent, "CommandId"), 7u, (int)Get(request, "CallerId"), 1ul, transferred: 34);
            InstallContainerBindingSnapshot(SolidBindingSnapshot(10, 3, 990, 256, 2)); Invoke(nearby, "Update");
            Assert.That(successes, Is.Zero, "仅收据和无关版本变化不证明材料已入包。");
            InstallContainerBindingSnapshot(SolidBindingSnapshot(11, 4, 1024, 256, 3)); Invoke(nearby, "Update");
            Assert.That(successes, Is.Zero, "别处增加同类材料也不能在原地面256未减时冒充拾取。");
            InstallContainerBindingSnapshot(SolidBindingSnapshot(12, 5, 1024, 222, 4)); Invoke(nearby, "Update");
            Assert.That(successes, Is.EqualTo(1)); Assert.That(Get(nearby, "HasPendingOnlinePickup"), Is.False);
            Assert.That(Get(Get(ReadView(GroundA), "Location"), "Kind").ToString(), Is.EqualTo("WorldItem"));
            Assert.That(Get(ReadView(GroundA), "MaterialQuantity"), Is.EqualTo(222ul));
            Assert.That(Get(ReadView(SolidStack), "MaterialQuantity"), Is.EqualTo(1024ul)); Assert.That(_requests, Is.EqualTo(1));
        }

        [Test]
        public void SolidCompleteMergeConfirmsWhenOriginalGroundIdentityWasConsumed()
        {
            UseSolidBindingSource(); var nearby = ConfigureSolidNearby(700); CaptureRequests();
            int successes = 0; Subscribe(typeof(NearbyPickupResultEvent), result => { if ((bool)Get(result, "Success")) successes++; });
            Assert.That(nearby.GetType().GetMethod("TryCollectFocused").Invoke(nearby, null), Is.EqualTo(true));
            var request = _request; var intent = Get(request, "Intent");
            Receipt((ulong)Get(intent, "CommandId"), 7u, (int)Get(request, "CallerId"), 1ul, transferred: 256);
            InstallContainerBindingSnapshot(SolidBindingSnapshot(10, 3, 956, 0, 2)); Invoke(nearby, "Update");
            Assert.That(successes, Is.EqualTo(1), "完整合并会消耗地面实例；不能永久等待原ID出现在包内。");
            Assert.That(Get(nearby, "HasPendingOnlinePickup"), Is.False);
            Assert.That(Get(ReadView(SolidStack), "MaterialQuantity"), Is.EqualTo(956ul));
            var args = new object[] { GroundA, null };
            Assert.That((bool)_read.GetType().GetMethod("TryGetView").Invoke(_read, args), Is.False); Assert.That(_requests, Is.EqualTo(1));
        }

        [Test]
        public void LaterOwnerSnapshotAfterSolidPickupAndKConsumptionDoesNotWaitForTheOldNetGain()
        {
            UseSolidBindingSource(); var nearby = ConfigureSolidNearby(700); CaptureRequests();
            int successes = 0; Subscribe(typeof(NearbyPickupResultEvent), result => { if ((bool)Get(result, "Success")) successes++; });
            Assert.That(nearby.GetType().GetMethod("TryCollectFocused").Invoke(nearby, null), Is.EqualTo(true));
            var request = _request; var intent = Get(request, "Intent");
            Receipt((ulong)Get(intent, "CommandId"), 7u, (int)Get(request, "CallerId"), 1ul, transferred: 256);
            // 收据对应版本10已拾取256，版本11又由原K消费100。网络可只安装后一个完整快照；
            // 成功的原收据与地面消失仍成立，不能要求余额倒回956才让pending结束。
            InstallContainerBindingSnapshot(SolidBindingSnapshot(11, 3, 856, 0, 2)); Invoke(nearby, "Update");
            Assert.That(successes, Is.EqualTo(1)); Assert.That(Get(nearby, "HasPendingOnlinePickup"), Is.False);
            Assert.That(Get(ReadView(SolidStack), "MaterialQuantity"), Is.EqualTo(856ul)); Assert.That(_requests, Is.EqualTo(1));
        }

        private const string SolidStack = "solid-existing-stack";
        private void UseSolidBindingSource()
        { _includeSolidDefinition = true; UseContainerBindingSource(); }
        private Component ConfigureSolidNearby(ulong carried = 990)
        {
            InstallContainerBindingSnapshot(SolidBindingSnapshot(9, 2, carried, 256, 1));
            var actor = Track(new GameObject("Online solid nearby actor")); var nearby = actor.AddComponent(Type("Game.Run.NearbyPickupController"));
            _nearbyLifecycles.Add(nearby); nearby.GetType().GetMethod("ConfigureOnline").Invoke(nearby, new object[] { _source, actor.transform });
            _groundProxyA = BindGroundProxy(GroundA, GroundOwnerA, actor.transform, .5f); Invoke(nearby, "RefreshCandidates");
            _ui.GetType().GetMethod("ConfigureOnlineNearby").Invoke(_ui, new object[] { nearby }); return nearby;
        }
        private object SolidBindingSnapshot(uint version, ulong sequence, ulong carried, ulong groundQuantity, uint groundVersion)
        {
            var snapshot = ContainerBindingSnapshot(version, sequence); var assets = (OnlineOwnedAssetSnapshot)Get(snapshot, "Assets");
            var item = new OnlineOwnedItemSnapshot { InstanceId = SolidStack, ContentId = "ui.online.ice", Revision = groundVersion,
                LocationKind = 1, ParentInstanceId = "bag", Slot = -1, X = 4 };
            Set(item, "MaterialState", SolidState(carried)); var owned = new List<OnlineOwnedItemSnapshot>(assets.Items) { item }; assets.Items = owned.ToArray();
            var ground = Activator.CreateInstance(Type("Game.Core.OnlineGroundInventorySnapshot")); Set(ground, "Version", groundVersion);
            var groundItems = Array.CreateInstance(Type("Game.Core.OnlineGroundItemSnapshot"), groundQuantity == 0 ? 0 : 1);
            if (groundQuantity != 0)
            {
                var source = Activator.CreateInstance(Type("Game.Core.OnlineGroundItemSnapshot"));
                Set(source, "InstanceId", GroundA); Set(source, "ContentId", "ui.online.ice"); Set(source, "WorldOwnerId", GroundOwnerA);
                Set(source, "Revision", groundVersion); Set(source, "Position", Vector3.right * .5f); Set(source, "Rotation", Quaternion.identity);
                var material = SolidState(groundQuantity); Set(source, "MaterialState", material);
                var root = new OnlineOwnedItemSnapshot { InstanceId = GroundA, ContentId = "ui.online.ice", Revision = groundVersion,
                    LocationKind = 5, ParentInstanceId = "", Slot = -1 };
                Set(root, "MaterialState", material); Set(source, "Item", root); groundItems.SetValue(source, 0);
            }
            Set(ground, "Items", groundItems); Set(snapshot, "Ground", ground); return snapshot;
        }
        private static object SolidState(ulong quantity)
        {
            // 新DTO通过反射装配，使尚未合入的合同产生明确行为RED，而不是整个测试程序集编译Blocked。
            var state = Activator.CreateInstance(Type("Game.Core.OnlineOwnedMaterialStateSnapshot"));
            Set(state, "SchemaVersion", 1u); Set(state, "MaterialKey", (byte)5); Set(state, "Quantity", quantity); return state;
        }

        [Test]
        public void OriginalNearbyPromptShowsOnlineFocusHidesForTabAndClearsReleasedSource()
        {
            UseContainerBindingSource(); var nearby = ConfigureGroundNearby();
            Set(_ui, "_open", false);
            var prefab = Resources.Load<GameObject>("Presentation/CombatInventory");
            Assert.That(prefab, Is.Not.Null);
            var root = Track(Object.Instantiate(prefab));
            var prompt = root.GetComponentInChildren<NearbyPickupListView>(true);
            Assert.That(prompt, Is.Not.Null, "测试必须读取正式导出资源里的原列表，不能用测试自造UI补缺失装配。");
            prompt.GetType().GetMethod("ConfigureOnline").Invoke(prompt, new object[] { _ui, nearby, null });
            var visual = (GameObject)Get(prompt, "_visual"); var labels = (Text[])Get(prompt, "_labels");
            Invoke(prompt, "LateUpdate");
            Assert.That(visual.activeSelf, Is.True); Assert.That(prompt.VisibleCount, Is.EqualTo(2));
            Assert.That(labels[0].text, Does.StartWith("F  "));
            nearby.GetType().GetMethod("SelectRelative").Invoke(nearby, new object[] { 1 }); Invoke(prompt, "LateUpdate");
            Assert.That(labels[0].text, Does.Not.StartWith("F  ")); Assert.That(labels[1].text, Does.StartWith("F  "));
            Set(_ui, "_open", true); Invoke(prompt, "LateUpdate"); Assert.That(visual.activeSelf, Is.False);
            Set(_ui, "_open", false); Invoke(prompt, "LateUpdate"); Assert.That(visual.activeSelf, Is.True);
            nearby.GetType().GetMethod("ConfigureOnline").Invoke(nearby, new object[] { null, Get(nearby, "Player") });
            _ui.GetType().GetMethod("BindPresentationSource").Invoke(_ui, new object[] { null }); Invoke(prompt, "LateUpdate");
            Assert.That(visual.activeSelf, Is.False, "退出/换源不能继续显示旧世界候选。");
            Assert.That(_localWrites, Is.Zero);
        }

        private const string GroundA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", GroundB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string GroundOwnerA = "cccccccccccccccccccccccccccccccc", GroundOwnerB = "dddddddddddddddddddddddddddddddd";
        private Component _groundProxyA;
        private Component ConfigureGroundNearby()
        {
            InstallContainerBindingSnapshot(GroundBindingSnapshot());
            var actor = Track(new GameObject("Online nearby fixture actor"));
            var nearby = actor.AddComponent(Type("Game.Run.NearbyPickupController"));
            _nearbyLifecycles.Add(nearby);
            var configure = nearby.GetType().GetMethod("ConfigureOnline"); Assert.That(configure, Is.Not.Null);
            configure.Invoke(nearby, new object[] { _source, actor.transform });
            _groundProxyA = BindGroundProxy(GroundA, GroundOwnerA, actor.transform, .5f);
            BindGroundProxy(GroundB, GroundOwnerB, actor.transform, 1f);
            Invoke(nearby, "RefreshCandidates");
            var bind = _ui.GetType().GetMethod("ConfigureOnlineNearby"); Assert.That(bind, Is.Not.Null);
            bind.Invoke(_ui, new object[] { nearby }); return nearby;
        }
        private Component BindGroundProxy(string id, string owner, Transform actor, float distance)
        {
            var root = Track(new GameObject("Online ground " + id)); root.transform.position = Vector3.right * distance;
            var proxy = root.AddComponent(Type("Game.Run.WorldItemPickup"));
            var bind = proxy.GetType().GetMethod("BindOnline"); Assert.That(bind, Is.Not.Null);
            Assert.That(bind.Invoke(proxy, new object[] { _source, id, owner, actor }), Is.EqualTo(true)); return proxy;
        }
        private object GroundBindingSnapshot(uint version = 9, ulong sequence = 2, bool pickedFirst = false)
        {
            var snapshot = ContainerBindingSnapshot(version, sequence);
            var ground = Activator.CreateInstance(Type("Game.Core.OnlineGroundInventorySnapshot")); Set(ground, "Version", pickedFirst ? 2u : 1u);
            var groundItems = Array.CreateInstance(Type("Game.Core.OnlineGroundItemSnapshot"), pickedFirst ? 1 : 2);
            int index = 0;
            if (!pickedFirst) groundItems.SetValue(GroundItem(GroundA, GroundOwnerA, .5f, 23ul), index++);
            groundItems.SetValue(GroundItem(GroundB, GroundOwnerB, 1f, 29ul), index);
            Set(ground, "Items", groundItems); Set(snapshot, "Ground", ground);
            if (pickedFirst)
            {
                var assets = (OnlineOwnedAssetSnapshot)Get(snapshot, "Assets"); var items = new List<OnlineOwnedItemSnapshot>(assets.Items)
                { new OnlineOwnedItemSnapshot { InstanceId = GroundA, ContentId = "ui.online.bottle", Revision = 2,
                    LocationKind = 1, ParentInstanceId = "bag", Slot = -1, X = 2,
                    ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = new[]
                        { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 23 } } } } };
                assets.Items = items.ToArray();
            }
            return snapshot;
        }
        private object GroundItem(string id, string owner, float distance, ulong quantity)
        {
            var item = Activator.CreateInstance(Type("Game.Core.OnlineGroundItemSnapshot"));
            Set(item, "InstanceId", id); Set(item, "ContentId", "ui.online.bottle"); Set(item, "WorldOwnerId", owner); Set(item, "Revision", 1u);
            Set(item, "Position", Vector3.right * distance); Set(item, "Rotation", Quaternion.identity);
            var state = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = new[]
                { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = quantity } } };
            Set(item, "ContainerState", state);
            Set(item, "Item", new OnlineOwnedItemSnapshot { InstanceId = id, ContentId = "ui.online.bottle", Revision = 1,
                LocationKind = 5, ParentInstanceId = "", Slot = -1, ContainerState = state }); return item;
        }
        private static void AssertNeutralTransferFields(object intent)
        {
            Assert.That(Get(intent, "SourceWandId"), Is.Null.Or.Empty); Assert.That(Get(intent, "TargetWandId"), Is.Null.Or.Empty);
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(0u)); Assert.That(Get(intent, "TargetProgramRevision"), Is.EqualTo(0u));
            Assert.That(Get(intent, "ToSpellStorage"), Is.False); Assert.That(Get(intent, "TransferBudget"), Is.EqualTo(0ul));
            Assert.That(Get(intent, "AimOrigin"), Is.EqualTo(Vector3.zero)); Assert.That(Get(intent, "AimDirection"), Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void RejectedMoveRemainsObservableWithoutAnAuthoredStatusText()
        {
            Set(_ui, "_status", null); ulong command = SubmitCardMove();
            Assert.That(Get(_ui, "LastInteractionStatus"), Is.Not.Null.And.Not.Empty);
            Assert.That(Get(_ui, "PendingOnlineCommandId"), Is.EqualTo(command));
            Assert.That(Get(_ui, "LastOnlineCommandReceipt"), Is.Null);
            Receipt(command, 7u, _ui.GetInstanceID() + 1, 1ul, OnlineInventoryCommandFailure.NotReady);
            Assert.That(Get(_ui, "LastOnlineCommandReceipt"), Is.Null, "其他Caller的收据不能污染这次拖放诊断。");
            Receipt(command, 7u, _ui.GetInstanceID(), 1ul, OnlineInventoryCommandFailure.NotReady);
            var receipt = (OnlineInventoryCommandReceipt)Get(_ui, "LastOnlineCommandReceipt");
            Assert.That(receipt.CommandId, Is.EqualTo(command)); Assert.That(receipt.Failure, Is.EqualTo(OnlineInventoryCommandFailure.NotReady));
            Assert.That(Get(_ui, "PendingOnlineCommandId"), Is.EqualTo(0ul));
            Assert.That((string)Get(_ui, "LastInteractionStatus"), Does.Contain("尚未就绪"));
            Assert.That(CardSlot(), Is.Zero); Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void AcceptedMoveRemainsObservableUntilItsOwnerSnapshotIsInstalledWithoutStatusText()
        {
            Set(_ui, "_status", null); ulong command = SubmitCardMove();
            Receipt(command, 7u, _ui.GetInstanceID(), 1ul);
            Assert.That(Get(_ui, "PendingOnlineCommandId"), Is.EqualTo(0ul));
            Assert.That(Get(_ui, "PendingOnlineInventoryVersion"), Is.EqualTo(10u));
            Assert.That((string)Get(_ui, "LastInteractionStatus"), Does.Contain("等待"));
            Assert.That(CardSlot(), Is.Zero, "成功收据不等于Owner快照安装，更不能让诊断旁路改归属。");
            InstallSnapshot(10u, 2ul, 1);
            Assert.That(Get(_ui, "PendingOnlineInventoryVersion"), Is.EqualTo(0u));
            Assert.That((string)Get(_ui, "LastInteractionStatus"), Is.EqualTo("库存已更新。"));
            Assert.That(CardSlot(), Is.EqualTo(1)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void ReceiptsMustMatchActorCallerEpochAndLatestCommandAndNeverMutateAssets()
        {
            CaptureRequests(); Begin("card"); Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.EqualTo(1));
            ulong id = (ulong)Get(Get(_request, "Intent"), "CommandId"); string pending = _status.text;
            Receipt(id, 7u, _ui.GetInstanceID() + 1, 1ul); Assert.That(_status.text, Is.EqualTo(pending));
            Receipt(id, 6u, _ui.GetInstanceID(), 1ul); Assert.That(_status.text, Is.EqualTo(pending));
            Receipt(id, 7u, _ui.GetInstanceID(), 2ul); Assert.That(_status.text, Is.EqualTo(pending));
            Receipt(id - 1, 7u, _ui.GetInstanceID(), 1ul); Assert.That(_status.text, Is.EqualTo(pending));
            Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            Assert.That(_status.text, Is.Not.EqualTo(pending), "有效收据仅反馈已受理/失败，资产仍等完整快照。");
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void EscapeAfterMenuThenOriginalTabClosesInventoryWithoutClosingConnectionMenu()
        {
            var cursor = Cursor.lockState; bool visible = Cursor.visible;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var root = Object.Instantiate(Resources.Load<GameObject>("Online/DungeonEntryDialog"));
            var dialog = root.GetComponent<DungeonEntryDialogController>();
            bool menuOpen = false, inventoryOpen = false;
            int menuRequests = 0;
            Action publishView = () => typeof(DungeonEntryDialogController).GetMethod("OnGameplay", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(dialog, new object[] { new OnlineMovementPresentationEvent
                { Active = true, Connected = true, MenuOpen = menuOpen, InputCaptured = menuOpen || inventoryOpen } });
            Action<OnlineMovementMenuRequestEvent> menu = change => { menuOpen = change.Open; menuRequests++; publishView(); };
            Action<InventoryPresentationVisibilityEvent> inventory = change =>
            { if (change.OwnerId == _ui.GetInstanceID()) { inventoryOpen = change.Open; publishView(); } };
            try
            {
                // 使用原 Controller、真实线上读模型及项目原 Tab/Cancel Action。仅用只读视图回放代替
                // 不属于 UI 单元测试的网络 World；按键仍走 InputSystem，而非直接 Invoke OnCancel。
                _ui.Close(); Invoke(_ui, "OnDisable"); _enabled = false;
                Set(_ui, "_toggleAction", AssetDatabase.LoadAssetAtPath<InputActionReference>("Assets/_Project/ScriptableObjects/Input/Inventory/Action_Toggle.asset"));
                Set(_ui, "_cancelAction", AssetDatabase.LoadAssetAtPath<InputActionReference>("Assets/_Project/ScriptableObjects/Input/Inventory/Action_Cancel.asset"));
                Invoke(_ui, "OnEnable"); _enabled = true;
                AssertActionKeyboardBelongsToThisFixture((InputActionReference)Get(_ui,"_cancelAction"),keyboard);
                AssertActionKeyboardBelongsToThisFixture((InputActionReference)Get(_ui,"_toggleAction"),keyboard);
                Invoke(dialog, "Awake"); Invoke(dialog, "OnDisable"); Invoke(dialog, "OnEnable");
                EventBus<OnlineMovementMenuRequestEvent>.Subscribe(menu);
                EventBus<InventoryPresentationVisibilityEvent>.Subscribe(inventory);
                publishView();
                PressKeys(Key.Escape); Invoke(dialog, "Update"); PressKeys();
                Assert.That(menuOpen, Is.True); Assert.That(menuRequests, Is.EqualTo(1));
                PressKeys(Key.Tab); PressKeys();
                Assert.That(_ui.IsOpen && inventoryOpen, Is.True, "连接菜单打开后，原 Tab 功能仍可打开库存。");
                PressKeys(Key.Escape); Invoke(dialog, "Update");
                Assert.That(_ui.IsOpen || inventoryOpen, Is.False, "第一下 Esc 由原库存 Cancel Action 正常关闭库存。");
                Assert.That(menuOpen, Is.True, "同一 Esc 不能从库存穿透并关闭背后的连接菜单。");
                Assert.That(menuRequests, Is.EqualTo(1));
                Assert.That(Time.timeScale, Is.EqualTo(1f));
            }
            finally
            {
                EventBus<OnlineMovementMenuRequestEvent>.Unsubscribe(menu);
                EventBus<InventoryPresentationVisibilityEvent>.Unsubscribe(inventory);
                Invoke(dialog, "OnDisable"); Object.DestroyImmediate(root);
                InputSystem.RemoveDevice(keyboard);
                Cursor.lockState = cursor; Cursor.visible = visible;
            }
            void PressKeys(params Key[] keys)
            { InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys)); InputSystem.Update(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LateDialogRecoversAlreadyOpenOriginalInventoryBeforeEscape(bool menuOpen)
        { ExerciseDialogResubscriptionWithOpenInventory(menuOpen, false); }

        [TestCase(false)]
        [TestCase(true)]
        public void RestartedDialogRecoversAlreadyOpenOriginalInventoryBeforeEscape(bool menuOpen)
        { ExerciseDialogResubscriptionWithOpenInventory(menuOpen, true); }

        private void ExerciseDialogResubscriptionWithOpenInventory(bool menuOpen, bool restart)
        {
            var cursor = Cursor.lockState; bool visible = Cursor.visible;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var root = Object.Instantiate(Resources.Load<GameObject>("Online/DungeonEntryDialog"));
            var dialog = root.GetComponent<DungeonEntryDialogController>();
            bool inventoryOpen = false; int menuRequests = 0;
            Action publishView = () => EventBus<OnlineMovementPresentationEvent>.Publish(new OnlineMovementPresentationEvent
            { Active = true, Connected = true, MenuOpen = menuOpen, InputCaptured = menuOpen || inventoryOpen });
            Action<InventoryPresentationVisibilityEvent> inventory = change =>
            { if (change.OwnerId == _ui.GetInstanceID()) { inventoryOpen = change.Open; publishView(); } };
            Action<OnlineMovementMenuRequestEvent> menu = change => { menuOpen = change.Open; menuRequests++; publishView(); };
            try
            {
                Invoke(dialog, "Awake"); Invoke(dialog, "OnDisable");
                _ui.Close(); Invoke(_ui, "OnDisable"); _enabled = false;
                Set(_ui, "_cancelAction", AssetDatabase.LoadAssetAtPath<InputActionReference>("Assets/_Project/ScriptableObjects/Input/Inventory/Action_Cancel.asset"));
                // 用真正活跃的原 Controller 和真实只读 Source：恢复必须从已存在的 UI 事实取得，
                // 不能让测试在 Dialog 重启后再补发一次 Visibility，掩盖生产中的漏订阅。
                _ui.gameObject.SetActive(true); Invoke(_ui, "OnEnable"); _enabled = true;
                AssertActionKeyboardBelongsToThisFixture((InputActionReference)Get(_ui,"_cancelAction"),keyboard);
                EventBus<InventoryPresentationVisibilityEvent>.Subscribe(inventory);
                EventBus<OnlineMovementMenuRequestEvent>.Subscribe(menu);
                if (restart) { Invoke(dialog, "OnEnable"); publishView(); }
                _ui.OpenBackpack(); Assert.That(_ui.IsOpen && inventoryOpen, Is.True);
                if (restart) Invoke(dialog, "OnDisable");
                Invoke(dialog, "OnEnable"); publishView();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape)); InputSystem.Update();
                Invoke(dialog, "Update");
                Assert.That(_ui.IsOpen || inventoryOpen, Is.False, "Esc 仍由真实原库存 Cancel Action 关闭面板。");
                Assert.That(menuRequests, Is.Zero, "晚启用或重启 Dialog 必须恢复旧库存 Owner，同次 Esc 不能切换连接菜单。");
            }
            finally
            {
                EventBus<InventoryPresentationVisibilityEvent>.Unsubscribe(inventory);
                EventBus<OnlineMovementMenuRequestEvent>.Unsubscribe(menu);
                Invoke(dialog, "OnDisable"); Object.DestroyImmediate(root);
                InputSystem.RemoveDevice(keyboard); Cursor.lockState = cursor; Cursor.visible = visible;
            }
        }

        private static void AssertActionKeyboardBelongsToThisFixture(InputActionReference reference,Keyboard keyboard)
        {
            Assert.That(reference,Is.Not.Null);bool found=false;
            // SaveAndReset隔离Manager后，共享正式ActionAsset可能仍持有旧解析状态。只读controls身份，
            // 不先读Button value，不清缓存，也不把SDK的paranoid错误吞成“允许日志”。
            foreach(var control in reference.action.controls)
                if(control.device is Keyboard)
                { found=true;Assert.That(control.device,Is.SameAs(keyboard),"原Action仍绑定旧InputManager的Keyboard，测试必须复制自身Action状态。"); }
            Assert.That(found,Is.True,"原测试Action应实际解析到本例Keyboard，不能用无绑定对照掩盖输入失效。");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SuccessfulReceiptAndInstalledSnapshotFinishStatusInEitherArrivalOrder(bool snapshotFirst)
        {
            ulong id = SubmitCardMove();
            if (snapshotFirst) InstallSnapshot(10u, 2ul, 1);
            Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            if (!snapshotFirst)
            {
                Assert.That(_status.text, Does.Contain("等待"));
                Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(CardSlot(), Is.EqualTo(0), "收据不能乐观移动资产。");
                InstallSnapshot(10u, 2ul, 1);
            }
            Assert.That(_status.text, Is.EqualTo("库存已更新。"), "成功收据与对应真实库存均已到达后，不能永久显示等待快照。");
            Assert.That(InventoryVersion(), Is.EqualTo(10u)); Assert.That(CardSlot(), Is.EqualTo(1)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void InventoryChangedNotificationCannotFinishReceiptBeforeRealReadModelReachesVersion()
        {
            ulong id = SubmitCardMove(); Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = "inventory", Version = 10u });
            Invoke(_ui, "LateUpdate");
            Assert.That(_status.text, Does.Contain("等待"), "事件仅为刷新通知，不能取代真实只读模型中的版本。");
            Assert.That(InventoryVersion(), Is.EqualTo(9u));
            InstallSnapshot(10u, 2ul, 1);
            Assert.That(_status.text, Is.EqualTo("库存已更新。"));
        }

        [Test]
        public void FailedReceiptIsNotTurnedIntoSuccessByAnUnrelatedInventorySnapshot()
        {
            ulong id = SubmitCardMove(); Receipt(id, 7u, _ui.GetInstanceID(), 1ul, OnlineInventoryCommandFailure.NoCapacity);
            string failure = _status.text;
            InstallSnapshot(10u, 2ul, 0);
            Assert.That(_status.text, Is.EqualTo(failure)); Assert.That(_status.text, Does.Contain("目标位置空间不足或不可用"));
            Assert.That(_ui.LastOnlineCommandReceipt.Value.Failure, Is.EqualTo(OnlineInventoryCommandFailure.NoCapacity));
            Assert.That(CardSlot(), Is.EqualTo(0)); Assert.That(_localWrites, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ClosingAndReopeningInventoryDiscardsOldReceiptAndSnapshotStatus(bool receiptBeforeClose)
        {
            ulong id = SubmitCardMove();
            if (receiptBeforeClose) Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            _ui.Close(); _ui.Open(); string reopened = _status.text;
            if (!receiptBeforeClose) Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            InstallSnapshot(10u, 2ul, 1);
            Assert.That(_status.text, Is.EqualTo(reopened), "关闭结束本次交互；重开后的提示不接收上次操作迟到的收据/完成状态。");
            Assert.That(CardSlot(), Is.EqualTo(1), "关闭 UI 不阻止安装真实资产快照，只结束旧提示关联。");
        }

        private ulong SubmitCardMove()
        {
            CaptureRequests(); Begin("card"); Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.EqualTo(1));
            return (ulong)Get(Get(_request, "Intent"), "CommandId");
        }

        [Test]
        public void OriginalAttachmentViewReadsOnlineBagSourcesAndPreservesInvalidConfiguredSource()
        {
            UseAttachmentBindingSource(); var view = CreateAttachmentView();
            var ids = (List<string>)Get(view, "_sourceIds"); var dropdown = (Dropdown)Get(view, "_sources");
            Assert.That(ids, Does.Contain("bag-bottle")); Assert.That(ids, Does.Not.Contain("tool-bottle"));
            Assert.That(ids, Does.Not.Contain("ordinary")); Assert.That(dropdown.options.Count, Is.EqualTo(2));
            Assert.That(dropdown.options[ids.IndexOf("bag-bottle")].text, Does.Contain("17"), "供料来源显示已安装的真实GMU。");
            InstallContainerBindingSnapshot(AttachmentBindingSnapshot(10, 2, "lost-bottle", 73)); Invoke(view, "Update");
            Assert.That(((InputField)Get(view, "_dose")).text, Is.EqualTo("73"));
            Assert.That(((Text)Get(view, "_description")).text, Does.Contain("73"));
            Assert.That(Get(Get(ReadView("card"), "Attachment"), "ContainerId"), Is.EqualTo("lost-bottle"));
            Assert.That(((List<string>)Get(view, "_sourceIds")), Does.Contain("lost-bottle"), "失效来源要保留为只读配置提示，不能静默换成第一瓶。");
            Assert.That(_requests, Is.Zero); Assert.That(Get(ReadView("bag-bottle"), "ContainerTotal"), Is.EqualTo(17ul));
        }

        [Test]
        public void OriginalAttachmentApplyPublishesCanonicalOnlineIntentWithoutSpendingSource()
        {
            UseAttachmentBindingSource(); var view = CreateAttachmentView(); CaptureRequests(); CaptureLocalAttachmentWrites();
            ChooseAttachmentSource(view, "bag-bottle"); ((InputField)Get(view, "_dose")).text = "4294967297"; view.Apply();
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(Get(_request, "ActorId"), Is.EqualTo(1ul));
            Assert.That(Get(_request, "CallerId"), Is.EqualTo(view.GetInstanceID())); var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("ConfigureSpellAttachment"));
            Assert.That(Get(intent, "ItemId"), Is.EqualTo("card")); Assert.That(Get(intent, "ConnectionEpoch"), Is.EqualTo(7u));
            Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(9u)); Assert.That(Get(intent, "CommandId"), Is.Not.EqualTo(0ul));
            Assert.That(Get(intent, "AttachmentContainerId"), Is.EqualTo("bag-bottle"));
            Assert.That(Get(intent, "AttachmentDose"), Is.EqualTo(4294967297ul)); Assert.That(Get(intent, "AttachmentEnabled"), Is.True);
            AssertNeutralAttachmentFields(intent); Assert.That(Get(ReadView("card"), "Attachment"), Is.Null);
            Assert.That(Get(ReadView("bag-bottle"), "ContainerTotal"), Is.EqualTo(17ul)); Assert.That(InventoryVersion(), Is.EqualTo(9u));
            Assert.That(_localWrites, Is.Zero); Assert.That(view.Feedback, Does.Contain("等待"));
        }

        [TestCase("card", "tool-bottle", 10ul)]
        [TestCase("card", "ordinary", 10ul)]
        [TestCase("card", "missing-bottle", 10ul)]
        [TestCase("card", "bag-bottle", 0ul)]
        [TestCase("wand", "bag-bottle", 10ul)]
        public void OnlineAttachmentRejectsNonBagSourceOrInvalidCardDoseBeforeSending(string card, string source, ulong dose)
        {
            UseAttachmentBindingSource(); var view = CreateAttachmentView(); CaptureRequests(); CaptureLocalAttachmentWrites();
            view.Submit(card, source, dose, true);
            Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
            Assert.That(Get(ReadView("card"), "Attachment"), Is.Null); Assert.That(Get(ReadView("bag-bottle"), "ContainerTotal"), Is.EqualTo(17ul));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OnlineAttachmentSuccessWaitsForExactReceiptAndInstalledOwnerInEitherOrder(bool snapshotFirst)
        {
            UseAttachmentBindingSource(); var view = CreateAttachmentView(); CaptureRequests();
            view.Submit("card", "bag-bottle", 23, true); Assert.That(_requests, Is.EqualTo(1));
            ulong command = (ulong)Get(Get(_request, "Intent"), "CommandId"); string pending = view.Feedback;
            Receipt(command, 7, view.GetInstanceID() + 1, 1); Receipt(command, 6, view.GetInstanceID(), 1);
            Receipt(command, 7, view.GetInstanceID(), 2); Receipt(command - 1, 7, view.GetInstanceID(), 1);
            Assert.That(view.Feedback, Is.EqualTo(pending), "其他Actor/Caller/Epoch/命令的收据不能完成此设置。");
            if (snapshotFirst) { InstallContainerBindingSnapshot(AttachmentBindingSnapshot(10, 2, "bag-bottle", 23)); Invoke(view, "Update"); }
            Receipt(command, 7, view.GetInstanceID(), 1);
            if (!snapshotFirst)
            {
                Assert.That(view.Feedback, Does.Contain("等待")); Assert.That(Get(ReadView("card"), "Attachment"), Is.Null);
                InstallContainerBindingSnapshot(AttachmentBindingSnapshot(10, 2, "bag-bottle", 23)); Invoke(view, "Update");
            }
            Assert.That(view.Feedback, Does.Contain("已更新")); Assert.That(view.Feedback, Does.Contain("未扣材料"));
            Assert.That(Get(Get(ReadView("card"), "Attachment"), "Dose"), Is.EqualTo(23ul));
            Assert.That(Get(ReadView("bag-bottle"), "ContainerTotal"), Is.EqualTo(17ul)); Assert.That(_requests, Is.EqualTo(1));
        }

        [Test]
        public void OriginalAttachmentClearUsesEmptyZeroDisabledIntentAndWaitsForOwner()
        {
            UseAttachmentBindingSource(); InstallContainerBindingSnapshot(AttachmentBindingSnapshot(10, 2, "bag-bottle", 23));
            var view = CreateAttachmentView(); CaptureRequests(); CaptureLocalAttachmentWrites(); view.Clear();
            Assert.That(_requests, Is.EqualTo(1)); var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "Kind").ToString(), Is.EqualTo("ConfigureSpellAttachment"));
            Assert.That(Get(intent, "AttachmentContainerId"), Is.Null.Or.Empty); Assert.That(Get(intent, "AttachmentDose"), Is.EqualTo(0ul));
            Assert.That(Get(intent, "AttachmentEnabled"), Is.False); Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(10u));
            AssertNeutralAttachmentFields(intent); Assert.That(Get(Get(ReadView("card"), "Attachment"), "Dose"), Is.EqualTo(23ul));
            Assert.That(Get(ReadView("bag-bottle"), "ContainerTotal"), Is.EqualTo(17ul)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void FailedOnlineAttachmentReceiptKeepsConfirmedSettingAndCannotBeTurnedIntoSuccess()
        {
            UseAttachmentBindingSource(); var view = CreateAttachmentView(); CaptureRequests();
            view.Submit("card", "bag-bottle", 23, true); Assert.That(_requests, Is.EqualTo(1));
            var intent = Get(_request, "Intent"); Receipt((ulong)Get(intent, "CommandId"), 7, view.GetInstanceID(), 1, OnlineInventoryCommandFailure.Busy);
            string failure = view.Feedback; Assert.That(failure, Does.Contain("拒绝"));
            InstallContainerBindingSnapshot(AttachmentBindingSnapshot(10, 2, null, 0)); Invoke(view, "Update");
            Assert.That(view.Feedback, Is.EqualTo(failure)); Assert.That(Get(ReadView("card"), "Attachment"), Is.Null);
            Assert.That(Get(ReadView("bag-bottle"), "ContainerTotal"), Is.EqualTo(17ul));
        }

        private void UseAttachmentBindingSource()
        { _attachmentEmitterDefinition = true; UseContainerBindingSource(); }
        private SpellAttachmentView CreateAttachmentView()
        {
            // 原视图在运行时等待库存字体准备；夹具借用真实内置字体，使Update能走实际控件投影。
            _ui.GetType().GetProperty("UiFont").SetValue(_ui, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")); _attachmentFontSet = true;
            _ui.SelectItem("card"); var root = Track(new GameObject("Original online attachment view")); root.SetActive(false);
            var view = root.AddComponent<SpellAttachmentView>(); var controls = new GameObject("Controls"); controls.transform.SetParent(root.transform);
            Set(view, "_inventory", _ui); Set(view, "_controls", controls); Set(view, "_contextualDetails", true);
            Set(view, "_sources", AttachmentControl<Dropdown>(controls.transform, "Sources"));
            Set(view, "_dose", AttachmentControl<InputField>(controls.transform, "Dose"));
            Set(view, "_apply", AttachmentControl<Button>(controls.transform, "Apply")); Set(view, "_clear", AttachmentControl<Button>(controls.transform, "Clear"));
            Set(view, "_description", AttachmentControl<Text>(controls.transform, "Description")); Set(view, "_feedback", AttachmentControl<Text>(controls.transform, "Feedback"));
            Invoke(view, "Awake"); Invoke(view, "OnEnable"); _attachmentViews.Add(view); Invoke(view, "Update"); return view;
        }
        private static T AttachmentControl<T>(Transform parent, string name) where T : Component
        { var root = new GameObject(name, typeof(RectTransform), typeof(T)); root.transform.SetParent(parent, false); return root.GetComponent<T>(); }
        private static void ChooseAttachmentSource(SpellAttachmentView view, string id)
        {
            var ids = (List<string>)Get(view, "_sourceIds"); int index = ids.IndexOf(id); Assert.That(index, Is.GreaterThan(0));
            ((Dropdown)Get(view, "_sources")).SetValueWithoutNotify(index);
        }
        private object AttachmentBindingSnapshot(uint version, ulong sequence, string container, ulong dose)
        {
            var snapshot = ContainerBindingSnapshot(version, sequence); var assets = (OnlineOwnedAssetSnapshot)Get(snapshot, "Assets");
            foreach (var item in assets.Items) if (item.InstanceId == "card")
            {
                item.Revision = version == 9 ? 1u : version - 8;
                if (container != null)
                {
                    var state = Activator.CreateInstance(Type("Game.Core.OnlineOwnedSpellAttachmentStateSnapshot"));
                    Set(state, "SchemaVersion", 1u); Set(state, "ContainerId", container); Set(state, "Dose", dose); Set(state, "Enabled", true);
                    Set(item, "AttachmentState", state);
                }
            }
            var wands = (Array)Get(snapshot, "Wands"); Set(wands.GetValue(0), "ProgramRevision", version + 8); return snapshot;
        }
        private void CaptureLocalAttachmentWrites() => Subscribe(typeof(SpellAttachmentRequestedEvent), _ => _localWrites++);
        private static void AssertNeutralAttachmentFields(object intent)
        {
            AssertNeutralTransferFields(intent); Assert.That(Get(intent, "WorldOwnerId"), Is.Null.Or.Empty);
            Assert.That(Get(intent, "TargetBackpackId"), Is.Null.Or.Empty); Assert.That(Get(intent, "GridX"), Is.EqualTo(0));
            Assert.That(Get(intent, "GridY"), Is.EqualTo(0)); Assert.That(Get(intent, "QuarterTurns"), Is.EqualTo(0));
            Assert.That(Get(intent, "TargetSlot"), Is.EqualTo(0)); Assert.That(Get(intent, "AutoPlace"), Is.False);
        }
        private void InstallSnapshot(uint version, ulong sequence, int cardSlot)
        {
            var snapshot = Snapshot(); Set(snapshot, "SnapshotSequence", sequence);
            var assets = (OnlineOwnedAssetSnapshot)Get(snapshot, "Assets"); assets.InventoryVersion = version; assets.Items[1].Slot = cardSlot;
            var args = new[] { snapshot, null };
            Assert.That((bool)_source.GetType().GetMethod("TryInstall").Invoke(_source, args), Is.True, args[1] as string);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = "inventory", Version = version });
            Invoke(_ui, "LateUpdate");
        }
        private int CardSlot()
        {
            var args = new object[] { "card", null };
            Assert.That((bool)_read.GetType().GetMethod("TryGetView").Invoke(_read, args), Is.True);
            return (int)Get(Get(args[1], "Location"), "SlotIndex");
        }

        private void CaptureRequests() => Subscribe(Type("Game.Core.OnlineInventoryCommandRequestedEvent"), e => { _request = e; _requests++; });
        private void CaptureLocalToolWrites()
        {
            Subscribe(typeof(ToolbeltBindingRequestedEvent), _ => _localWrites++);
            Subscribe(typeof(ContainerSelectionRequestedEvent), _ => _localWrites++);
            Subscribe(typeof(WandSelectionRequestedEvent), _ => _localWrites++);
        }
        private OnlineAccountInventorySnapshot _accountCommandSnapshot;
        private void UseAccountCommandSource(string entryBoundBackpack = null)
        {
            UseContainerBindingSource();
            var assets = (OnlineOwnedAssetSnapshot)Get(ContainerBindingSnapshot(), "Assets");
            var items = new List<OnlineOwnedItemSnapshot>(assets.Items);
            for (int i = 0; i < items.Count; i++) items[i].ParentInstanceId = items[i].ParentInstanceId ?? "";
            items.Add(new OnlineOwnedItemSnapshot { InstanceId = "stored-card", ContentId = "ui.online.spell", Revision = 1,
                LocationKind = 0, ParentInstanceId = "", Slot = 0, RemainingCharges = 2 });
            items.Add(new OnlineOwnedItemSnapshot { InstanceId = "backup-bag", ContentId = "ui.online.bag", Revision = 1,
                LocationKind = 0, ParentInstanceId = "", Slot = 1 });
            foreach (var item in items) if (item.InstanceId==entryBoundBackpack) item.EntryBoundBackpack=true;
            _accountCommandSnapshot = new OnlineAccountInventorySnapshot { SchemaVersion = OnlineAccountInventorySnapshot.CurrentSchemaVersion,
                PlayerId = "player", ContentManifestHash = Hash, Version = 9, Gold = 10000,
                SelectedWandInstanceId = "wand", Items = items.ToArray() };
            ConfigureAccountWarehouseFixture();
            RebindAccountCommandSource(9);
        }
        private void ConfigureAccountWarehouseFixture()
        {
            // 账号Hub只提供确认metadata与真实区域；不造旧本地Profile、旧ReadModel或假RunCoordinator。
            var sessionRoot = Track(new GameObject("AccountWarehouseSession")); sessionRoot.SetActive(false);
            var session = sessionRoot.AddComponent(Type("Game.Run.SinglePlayerInventorySession"));
            Set(session, "_catalog", _bindingCatalogAsset); Set(session, "_allowWarehouse", true);
            var actor = Track(new GameObject("AccountWarehouseActor")); _accountWarehouseActor = actor.transform;
            var volumeRoot = Track(new GameObject("OriginalWarehouseArea", typeof(BoxCollider)));
            var box = volumeRoot.GetComponent<BoxCollider>(); box.isTrigger = true; box.size = new Vector3(4, 4, 4);
            var volume = volumeRoot.AddComponent(Type("Game.Run.HubAreaVolume"));
            var definition = Asset("Game.Run.HubDefinition");
            var stationType = Type("Game.Run.HubDefinition+Station"); var station = Activator.CreateInstance(stationType);
            Set(station, "Kind", Enum.Parse(Type("Game.Run.HubStationKind"), "Warehouse")); Set(station, "Position", Vector3.zero);
            var stations = Array.CreateInstance(stationType, 1); stations.SetValue(station, 0); Set(definition, "Stations", stations);
            var hubRoot = Track(new GameObject("AccountWarehouseInteraction")); hubRoot.SetActive(false);
            var hub = hubRoot.AddComponent(Type("Game.Run.HubInteractionController"));
            Set(hub, "_session", session); Set(hub, "_definition", definition); Set(hub, "_player", actor.transform); Set(hub, "_warehouseArea", volume);
            Set(_ui, "_session", session); Set(_ui, "_hubAccess", hub);
            hubRoot.SetActive(true); Invoke(hub, "OnDisable"); Invoke(hub, "Awake"); Invoke(hub, "OnEnable"); _nearbyLifecycles.Add(hub);
            int owner = _ui.GetInstanceID();
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(owner, _accountCommandSnapshot, out var error), Is.True, error);
            Assert.That(OnlineAccountInventoryHubScope.TryRequireScene(owner, (int)_ui.gameObject.scene.handle, out error), Is.True, error);
            Assert.That(Get(session, "ReadOnly"), Is.Null); Assert.That(Get(session, "RunCoordinator"), Is.Null);
        }
        private void RebindAccountCommandSource(uint version)
        {
            var snapshot = _accountCommandSnapshot.CopyValue(); snapshot.Version = version;
            var args = new object[] { _bindingCatalog, snapshot, null, null };
            Assert.That((bool)Type("Game.Run.AccountInventoryPresentationSource").GetMethod("TryCreate").Invoke(null, args), Is.True, args[3] as string);
            var previous = _source; _source = args[2]; _read = Get(_source, "ReadOnly");
            _ui.GetType().GetMethod("BindPresentationSource").Invoke(_ui, new[] { _source });
            Set(_ui, "_accountSource", _source); Set(_ui, "_warehouseMode", true); Set(_ui, "_open", true);
            (previous as IDisposable)?.Dispose(); _status.text = "正在查看";
        }
        private void CaptureAccountRequests()
        {
            Subscribe(typeof(OnlineAccountInventoryCommandRequestedEvent), e => { _request = e; _requests++; });
            Subscribe(typeof(OnlineInventoryCommandRequestedEvent), _ => _accountDsRequests++);
        }

        private void UseContainerBindingSource()
        {
            // 新行为单独装配真实Catalog/Source；原card/wand测试继续使用原来的两物品种子。
            var definitions = new List<Object> { Definition("ui.online.wand", "Wand"), Definition("ui.online.spell", "Spell", _attachmentEmitterDefinition),
                BindingDefinition("ui.online.bag", "Backpack"), BindingDefinition("ui.online.bottle", "Container"),
                BindingDefinition("ui.online.ordinary", "Consumable") };
            if (_includeSolidDefinition) definitions.Add(BindingDefinition("ui.online.ice", "Material"));
            _bindingBottleDefinition = definitions[3];
            var catalog = Asset("Game.Run.ItemCatalog"); var data = new SerializedObject(catalog);
            _bindingCatalogAsset = catalog;
            var entries = data.FindProperty("_definitions"); entries.arraySize = definitions.Count;
            for (int i = 0; i < definitions.Count; i++) entries.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
            data.ApplyModifiedPropertiesWithoutUndo(); var index = new object[] { null, null };
            Assert.That((bool)catalog.GetType().GetMethod("TryCreateIndex").Invoke(catalog, index), Is.True, index[1] as string);
            _bindingCatalog = index[0]; var next = CreateContainerBindingSource(); var previous = _source;
            _ui.GetType().GetMethod("BindPresentationSource").Invoke(_ui, new[] { next });
            _source = next; _read = Get(next, "ReadOnly"); (previous as IDisposable)?.Dispose();
            Set(_ui, "_open", true); _status.text = "正在查看";
        }
        private Object BindingDefinition(string id, string kind)
        {
            var asset = Asset("Game.Run.ItemDefinition"); var data = new SerializedObject(asset);
            data.FindProperty("_contentId").stringValue = id;
            data.FindProperty("_kind").intValue = Convert.ToInt32(Enum.Parse(Type("Game.Run.ItemKind"), kind));
            if (kind == "Backpack")
            { data.FindProperty("_backpackWidth").intValue = 8; data.FindProperty("_backpackHeight").intValue = 8; }
            if (kind == "Container") data.FindProperty("_containerCapacityGmu").intValue = 15000;
            if (kind == "Material")
            { data.FindProperty("_materialKey").intValue = 5; data.FindProperty("_materialItemCapacityGmu").intValue = 1024; }
            if (kind == "Consumable") data.FindProperty("_useKind").intValue = 2; // 原领域支持Throw，线上Bind仍只开放Container。
            data.ApplyModifiedPropertiesWithoutUndo(); return asset;
        }
        private object CreateContainerBindingSource(uint epoch = 7u, ulong actor = 1ul, string player = "player")
        {
            var identity = Activator.CreateInstance(Type("Game.Core.OnlineInventoryPresentationIdentity"), "run", player, actor,
                "inventory", Hash, epoch, "DungeonWorld");
            // 固体夹具下一份完整v9加入真实Material；起始v8避免把同版本篡改Owner当合法安装。
            // 旧容器夹具仍从原v9开始，保持其既有收据/版本前置。
            var args = new[] { _bindingCatalog, identity, ContainerBindingSnapshot(version: _includeSolidDefinition ? 8u : 9u,
                epoch: epoch, actor: actor, player: player), null, null };
            Assert.That((bool)Type("Game.Run.OnlineInventoryPresentationSource").GetMethod("TryCreate").Invoke(null, args), Is.True, args[4] as string);
            return args[3];
        }
        private object ContainerBindingSnapshot(uint version = 9u, ulong sequence = 1ul, uint epoch = 7u,
            ulong actor = 1ul, string player = "player", bool bindBagBottle = false)
        {
            var snapshot = Snapshot(); Set(snapshot, "SnapshotSequence", sequence); Set(snapshot, "ConnectionEpoch", epoch);
            var assets = (OnlineOwnedAssetSnapshot)Get(snapshot, "Assets"); assets.ActorId = actor; assets.PlayerId = player; assets.InventoryVersion = version;
            var items = new List<OnlineOwnedItemSnapshot>(assets.Items)
            {
                new OnlineOwnedItemSnapshot { InstanceId = "bag", ContentId = "ui.online.bag", Revision = 1,
                    LocationKind = 4, ParentInstanceId = "", Slot = 0 },
                new OnlineOwnedItemSnapshot { InstanceId = "bag-bottle", ContentId = "ui.online.bottle", Revision = 1,
                    LocationKind = bindBagBottle ? 8 : 1, ParentInstanceId = bindBagBottle ? "" : "bag", Slot = bindBagBottle ? 2 : -1,
                    ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = new[]
                        { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 17 } } } },
                new OnlineOwnedItemSnapshot { InstanceId = "tool-bottle", ContentId = "ui.online.bottle", Revision = 1,
                    LocationKind = 8, ParentInstanceId = "", Slot = 6,
                    ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = Array.Empty<OnlineOwnedMaterialAmountSnapshot>() } },
                new OnlineOwnedItemSnapshot { InstanceId = "ordinary", ContentId = "ui.online.ordinary", Revision = 1,
                    LocationKind = 1, ParentInstanceId = "bag", Slot = -1, X = 1 }
            };
            assets.Items = items.ToArray(); return snapshot;
        }
        private void InstallContainerBindingSnapshot(object snapshot)
        {
            var args = new[] { snapshot, null };
            Assert.That((bool)_source.GetType().GetMethod("TryInstall").Invoke(_source, args), Is.True, args[1] as string);
        }
        private object ReadView(string id)
        {
            var args = new object[] { id, null };
            Assert.That((bool)_read.GetType().GetMethod("TryGetView").Invoke(_read, args), Is.True); return args[1];
        }
        private object UnownedContainerView(string kind, string owner)
        {
            var item = Type("Game.Run.RuntimeItemState").GetConstructors()[0].Invoke(new object[]
                { "stale-bottle", "ui.online.bottle", 0, false, "", null, null, null, null, false });
            var location = Activator.CreateInstance(Type("Game.Run.ItemLocation"), Enum.Parse(Type("Game.Run.ItemLocationKind"), kind), owner,
                0, 0, 0, kind == "EquippedTool" ? 6 : -1);
            return Type("Game.Run.InventoryItemSnapshot").GetConstructors()[0].Invoke(new[]
                { item, location, _bindingBottleDefinition, Get(ReadView("bag-bottle"), "Rules") });
        }
        private void AssertToolBinding(int slot, string expected)
        {
            var args = new object[] { slot, null }; var tools = Get(_source, "ToolbeltReadOnly");
            bool exists = (bool)tools.GetType().GetMethod("TryGetBinding").Invoke(tools, args);
            Assert.That(exists, Is.EqualTo(expected != null)); Assert.That(args[1], Is.EqualTo(expected));
        }
        private void SubmitShortcut(int slot) => _ui.GetType().GetMethod("SubmitToShortcut").Invoke(_ui, new object[] { slot });
        private uint InventoryVersion() => (uint)Type("Game.Run.IInventoryReadOnly").GetProperty("Version").GetValue(_read);
        private void Begin(string itemId)
        {
            var args = new object[] { itemId, null };
            Assert.That((bool)_read.GetType().GetMethod("TryGetView").Invoke(_read, args), Is.True);
            BeginView(args[1]);
        }
        private void BeginView(object view)
        {
            _ui.GetType().GetMethod("BeginDrag").Invoke(_ui, new[] { view, (object)Vector2.zero, null, false,
                default(Vector2Int), -1, null, null, 0f });
        }
        private void Submit(string kind, string owner, int slot) => _ui.GetType().GetMethod("SubmitToTarget").Invoke(_ui,
            new[] { Enum.Parse(Type("Game.UI.InventoryDropKind"), kind), owner, (object)slot });
        private void Receipt(ulong id, uint epoch, int caller, ulong actor, OnlineInventoryCommandFailure failure = OnlineInventoryCommandFailure.None,
            ulong transferred = 0)
        {
            var result = Activator.CreateInstance(Type("Game.Core.OnlineInventoryCommandResultEvent"));
            var receipt = Activator.CreateInstance(Type("Game.Core.OnlineInventoryCommandReceipt"));
            Set(receipt, "CommandId", id); Set(receipt, "ConnectionEpoch", epoch); Set(receipt, "InventoryVersion", 10u);
            Set(receipt, "Failure", failure); Set(receipt, "Transferred", transferred);
            Set(result, "ActorId", actor); Set(result, "CallerId", caller); Set(result, "Receipt", receipt);
            typeof(EventBus<>).MakeGenericType(result.GetType()).GetMethod("Publish").Invoke(null, new[] { result });
        }
        private void Subscribe(System.Type type, Action<object> receive)
        {
            var helper = new Capture(receive);
            var callback = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(type), helper,
                typeof(Capture).GetMethod(nameof(Capture.Receive)).MakeGenericMethod(type));
            var bus = typeof(EventBus<>).MakeGenericType(type); bus.GetMethod("Subscribe").Invoke(null, new object[] { callback });
            _unsubscribers.Add(() => bus.GetMethod("Unsubscribe").Invoke(null, new object[] { callback }));
        }
        private sealed class Capture
        {
            private readonly Action<object> _receive;
            public Capture(Action<object> receive) => _receive = receive;
            public void Receive<T>(T value) where T : struct, IGameEvent => _receive(value);
        }
        private Object Definition(string id, string kind, bool emit = false)
        {
            var asset = Asset("Game.Run.ItemDefinition"); var data = new SerializedObject(asset);
            data.FindProperty("_contentId").stringValue = id;
            data.FindProperty("_kind").intValue = Convert.ToInt32(Enum.Parse(Type("Game.Run.ItemKind"), kind));
            string childType = kind == "Wand" ? "Game.Run.WandDefinition" : "Game.Skills.SpellDefinition";
            var child = Asset(childType); var childData = new SerializedObject(child);
            if (kind == "Wand") childData.FindProperty("_programCapacity").intValue = 2;
            else
            {
                childData.FindProperty("Kind").intValue = Convert.ToInt32(Enum.Parse(Type("Game.Skills.SpellKind"), emit ? "Emit" : "Modify"));
                if (emit)
                {
                    var projectile = Track(new GameObject("Online attachment emitter fixture", typeof(Rigidbody), typeof(BoxCollider), Type("Game.Combat.Arrow")));
                    projectile.SetActive(false); childData.FindProperty("ProjectilePrefab").objectReferenceValue = projectile;
                }
            }
            childData.ApplyModifiedPropertiesWithoutUndo();
            data.FindProperty(kind == "Wand" ? "_wandDefinition" : "_spellDefinition").objectReferenceValue = child;
            data.ApplyModifiedPropertiesWithoutUndo(); return asset;
        }
        private object Snapshot()
        {
            var value = Activator.CreateInstance(Type("Game.Core.OnlineInventoryPresentationSnapshot"));
            Set(value, "SchemaVersion", 1u); Set(value, "ConnectionEpoch", 7u); Set(value, "SnapshotSequence", 1ul);
            Set(value, "ResourceVersion", 12u); Set(value, "SelectionVersion", 5u); Set(value, "SpellUseVersion", 4u);
            // Owned3必须提供DS实际余额及非零货币修订；本组只操作库存，后续快照沿用同一笔测试金币事实。
            Set(value, "Assets", new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1, RunId = "run", PlayerId = "player",
                InventoryId = "inventory", GameplayConfigHash = Hash, InventoryVersion = 9, Gold = 10000, CurrencyVersion = 1,
                SelectedWandInstanceId = "wand", Items = new[] {
                    new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "ui.online.wand", Revision = 1, LocationKind = 3, Slot = 0, UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "ui.online.spell", Revision = 1, LocationKind = 2, Slot = 0, ParentInstanceId = "wand", RemainingCharges = 3 } } });
            var wand = Activator.CreateInstance(Type("Game.Core.OnlineInventoryWandPresentationSnapshot"));
            Set(wand, "InstanceId", "wand"); Set(wand, "ProgramRevision", 17u); Set(wand, "CurrentMana", 37f); Set(wand, "CooldownRemaining", 1.25f);
            SetArray(value, "Wands", wand);
            var use = Activator.CreateInstance(Type("Game.Core.OnlineInventorySpellUsePresentationSnapshot"));
            Set(use, "InstanceId", "card"); Set(use, "Eligible", true); SetArray(value, "SpellUses", use); return value;
        }
        private static void SetArray(object target, string name, object item)
        { var array = Array.CreateInstance(item.GetType(), 1); array.SetValue(item, 0); Set(target, name, array); }
        private T Track<T>(T value) where T : Object { _objects.Add(value); return value; }
        private Object Asset(string type) => Track(ScriptableObject.CreateInstance(Type(type)));
        private static System.Type Type(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { var found = assembly.GetType(name); if (found != null) return found; }
            Assert.Fail("缺少测试所需合同/类型：" + name); return null;
        }
        private static object Get(object target, string name)
        {
            var type = target.GetType(); var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null) return property.GetValue(target);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);
            foreach (var contract in type.GetInterfaces())
            { property = contract.GetProperty(name); if (property != null) return property.GetValue(target); }
            Assert.Fail("缺少字段/属性：" + type.FullName + "." + name); return null;
        }
        private static void Set(object target, string name, object value) => target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value);
        private static void Invoke(object target, string name) => target.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(target, null);
    }
}
