#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Game.Run.Tests
{
    /// <summary>通过真实 UGUI Pointer 回调验证点击语义、鼠标弹层与两个成交入口。</summary>
    public sealed class ShopQuickPurchasePlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => Review;
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T7-PurchaseDialog";
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile, ItemCatalogIndex index)
        {
            // 仅快捷购买用例设置测试钱包；继承的背包验收保留其248金币前提。
            if(TestContext.CurrentContext.Test.Name.StartsWith("Quick"))profile.Currency = 2000;
            if (TestContext.CurrentContext.Test.Name.StartsWith("QuickFullBackpack"))
            {
                profile.Items.TryGet("fire", out var template);
                for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
                    profile.Items.TryAdd(new RuntimeItemState("fill." + x + "." + y, template.ContentId, 1), new GridPlacement(x, y, 0).InBackpack("bag"));
            }
            foreach (var root in Scene.GetRootGameObjects()) foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component is RaidResultView && Ref<Text>(component, "_result") == null) component.enabled = false;
                if (component is ExtractionHud && Ref<Text>(component, "_condition") == null) component.enabled = false;
            }
            return profile;
        }
        private static T Ref<T>(UnityEngine.Object target, string field) where T : UnityEngine.Object => (T)new SerializedObject(target).FindProperty(field)?.objectReferenceValue;
        private ShopController Shop => Find<ShopController>();
        private RectTransform Panel => (RectTransform)Ref<GameObject>(Shop, "_panel").transform;
        private Transform Popup => Panel.Find("QuickPurchasePopup_鼠标购买");
        private ShopPurchaseDialog Dialog => Ref<ShopPurchaseDialog>(Shop,"_purchaseDialog");
        private void Confirm()
        {
            Assert.That(Dialog.IsOpen,Is.True);var button=Ref<Button>(Dialog,"_confirm");
            Assert.That(button.interactable,Is.True);PointerClick(button.gameObject,Center(button.transform));
        }
        private IEnumerator OpenShop()
        {
            var hub = Find<HubInteractionController>(); var area = Ref<HubAreaVolume>(hub, "_shopArea");
            Player.CharacterController.enabled = false; Player.transform.position = area.Center; Player.enabled = false; Physics.SyncTransforms();
            Keys(UnityEngine.InputSystem.Key.F); yield return null; Keys(); yield return null;
            Assert.That(Shop.IsOpen, Is.True); Assert.That(Popup, Is.Not.Null, "商店需要复用的鼠标旁购买弹层。");
        }
        private ShopEntryView Offer(out ShopOfferSnapshot offer)
        {
            var read = Find<SinglePlayerShopSession>().ReadOnly; offer = default;
            for (int i = 0; i < read.Count; i++) { var candidate = read.GetOffer(i); if (!candidate.IsMaterial && candidate.IsSpell && candidate.Stock > 0) { offer = candidate; break; } }
            Assert.That(offer.OfferId, Is.Not.Null);
            foreach (var view in Ref<RectTransform>(Shop, "_offersRoot").GetComponentsInChildren<ShopEntryView>())
                if (view.OfferId == offer.OfferId) return view;
            Assert.Fail("商品 View 必须对应真实 OfferId。"); return null;
        }
        private static void PointerClick(GameObject target, Vector2 position)
        {
            var data = new PointerEventData(EventSystem.current) { position = position, button = PointerEventData.InputButton.Left };
            // Button 是唯一 click 消费者；PointerDown 只记录位置，不能把一次点击算两次。
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
        }
        private static Vector2 Center(Transform target) => RectTransformUtility.WorldToScreenPoint(null, ((RectTransform)target).TransformPoint(((RectTransform)target).rect.center));
        private ShopEntryView Water(out ShopOfferSnapshot offer)
        {
            var read=Find<SinglePlayerShopSession>().ReadOnly;offer=default;
            for(int i=0;i<read.Count;i++){var value=read.GetOffer(i);if(value.IsMaterial && value.MaterialKey==1){offer=value;break;}}
            foreach(var row in Ref<RectTransform>(Shop,"_offersRoot").GetComponentsInChildren<ShopEntryView>())if(row.OfferId==offer.OfferId)return row;
            Assert.Fail("缺少水商品");return null;
        }
        private InventoryItemView MiddleItem(string id)
        {
            foreach(var view in Ref<SinglePlayerInventoryController>(Shop,"_embeddedInventory").GetComponentsInChildren<InventoryItemView>())
                if(view.Snapshot.InstanceId==id)return view;
            Assert.Fail("中列缺少实例 "+id);return null;
        }
        [UnityTest] public IEnumerator QuickBottomOpensConfirmationWithoutDebiting()
        {
            yield return OpenShop(); var row = Offer(out _); long money = Session.Currency;
            PointerClick(row.gameObject, Center(row.transform));
            Ref<Button>(Shop,"_buyButton").onClick.Invoke(); yield return null;
            Assert.That(Session.Currency, Is.EqualTo(money), "打开购买框不能提前成交。");
            var dialog = Ref<Component>(Shop,"_purchaseDialog");
            Assert.That(dialog, Is.Not.Null); Assert.That(dialog.gameObject.activeSelf, Is.True);
        }
        [UnityTest] public IEnumerator QuickConfirmationReusesExistingSpellIconAndRevalidatesChangedSupply()
        {
            yield return OpenShop();Offer(out var offer);
            Session.ReadOnly.TryGetView("fire",out var fire);
            Assert.That(fire.ContentId,Is.EqualTo(offer.ContentId));Assert.That(fire.Icon,Is.Not.Null);
            Assert.That(offer.Icon,Is.EqualTo(fire.Icon),"商品应复用已有法术图标，不因ItemDefinition没有覆盖图标而留白。");
            Shop.SelectOffer(offer.OfferId);Shop.Buy();
            Assert.That(Ref<Image>(Dialog,"_icon").sprite,Is.EqualTo(fire.Icon));
            Dialog.SetQuantity(offer.Stock);long money=Session.Currency;
            Session.Profile.Shop.Find(offer.OfferId).Remaining=1;
            Ref<Button>(Dialog,"_confirm").onClick.Invoke();yield return null;
            Assert.That(Dialog.Quantity,Is.EqualTo(1));Assert.That(Session.Currency,Is.EqualTo(money));
            Confirm();yield return null;Assert.That(Session.Currency,Is.EqualTo(money-offer.BuyPrice));
        }
        [UnityTest] public IEnumerator QuickConfirmationSliderButtonsCancelAndActualFinalPurchase()
        {
            yield return OpenShop();var row=Offer(out var offer);long money=Session.Currency;
            PointerClick(row.gameObject,Center(row.transform));Shop.Buy();
            Assert.That(Dialog.Quantity,Is.EqualTo(1));Assert.That(Dialog.Maximum,Is.EqualTo(offer.Stock));
            Canvas.ForceUpdateCanvases();
            var slider=Ref<Slider>(Dialog,"_slider");var handle=slider.handleRect.parent as RectTransform;
            var point=RectTransformUtility.WorldToScreenPoint(null,handle.TransformPoint(new Vector2(handle.rect.xMax,handle.rect.center.y)));
            var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=point};
            slider.OnPointerDown(data);slider.OnDrag(data);slider.OnPointerUp(data);
            Assert.That(Dialog.Quantity,Is.EqualTo(Dialog.Maximum),"真正的Slider Pointer操作应改变购买整数数量。");
            var minus=Ref<Button>(Dialog,"_minus");PointerClick(minus.gameObject,Center(minus.transform));
            Assert.That(Dialog.Quantity,Is.EqualTo(Dialog.Maximum-1));
            var plus=Ref<Button>(Dialog,"_plus");PointerClick(plus.gameObject,Center(plus.transform));
            Assert.That(Dialog.Quantity,Is.EqualTo(Dialog.Maximum));Assert.That(Session.Currency,Is.EqualTo(money));
            Ref<Button>(Dialog,"_cancel").onClick.Invoke();Assert.That(Dialog.IsOpen,Is.False);Assert.That(Session.Currency,Is.EqualTo(money));
            Shop.Buy();Assert.That(Dialog.Quantity,Is.EqualTo(1),"重新打开不能继承上次批量数量。");
            Dialog.SetQuantity(2);yield return new WaitForEndOfFrame();Capture("shop-purchase-confirmation.png");
            var confirm=Ref<Button>(Dialog,"_confirm");Confirm();confirm.onClick.Invoke();yield return null;
            Assert.That(Session.Currency,Is.EqualTo(money-offer.BuyPrice*2));Assert.That(Dialog.IsOpen,Is.False);
        }
        [UnityTest] public IEnumerator QuickConfirmationEscapeBlocksUnderlyingSelectionAndPreservesShop()
        {
            yield return OpenShop();var row=Offer(out _);PointerClick(row.gameObject,Center(row.transform));Shop.Buy();yield return null;
            long money=Session.Currency;string selected=Dialog.OfferId;
            Shop.SelectOwned("fire");Shop.SelectOffer("not.an.offer");Shop.SellItem();Shop.SellMaterial();
            Assert.That(Dialog.OfferId,Is.EqualTo(selected));Assert.That(Session.Currency,Is.EqualTo(money));
            var results=new System.Collections.Generic.List<RaycastResult>();
            var data=new PointerEventData(EventSystem.current){position=Center(row.transform)};EventSystem.current.RaycastAll(data,results);
            Assert.That(results.Count,Is.GreaterThan(0));
            Assert.That(results[0].gameObject.transform.IsChildOf(Dialog.transform),Is.True,"遮罩应截获商品列表上的真实Raycast。");
            Keys(Key.Escape);yield return null;Keys();yield return null;
            Assert.That(Dialog.IsOpen,Is.False);Assert.That(Shop.IsOpen,Is.True);Assert.That(Session.Currency,Is.EqualTo(money));
            Shop.Buy();Keys(Key.Tab);yield return null;Keys();yield return null;
            Assert.That(Dialog.IsOpen,Is.False);Assert.That(Shop.IsOpen,Is.False);Assert.That(Backpack.IsOpen,Is.True);
        }
        [UnityTest] public IEnumerator QuickConfirmationRefreshesLimitsAndInvalidatesMissingContainer()
        {
            yield return OpenShop();Shop.SelectOwned("b0");Water(out var offer);Shop.SelectOffer(offer.OfferId);Shop.Buy();
            Dialog.SetQuantity(50);Assert.That(Dialog.Maximum,Is.EqualTo(50));
            // 模拟另一项可靠资产操作移除目标，确认时必须重读实例而非注入旧快照。
            Assert.That(Session.Profile.Items.TryPrepareMaterialChange("b0",null,ItemLocation.Warehouse,out var removed),Is.True);
            Assert.That(Session.Profile.Items.CompletePrepared(removed,true),Is.True);
            Ref<Button>(Dialog,"_confirm").onClick.Invoke();yield return null;
            Assert.That(Dialog.IsOpen,Is.False);Assert.That(Session.Currency,Is.EqualTo(2000));
            Assert.That(Shop.Status,Does.Contain("容器"));
        }
        [UnityTest] public IEnumerator QuickConfirmationInsufficientWalletAndChangedSupplyNeverOverspend()
        {
            yield return OpenShop();var row=Offer(out var offer);PointerClick(row.gameObject,Center(row.transform));Shop.Buy();
            Dialog.SetQuantity(offer.Stock);Session.Profile.Currency=offer.BuyPrice;
            Ref<Button>(Dialog,"_confirm").onClick.Invoke();yield return null;
            Assert.That(Session.Currency,Is.EqualTo(offer.BuyPrice));Assert.That(Dialog.Quantity,Is.EqualTo(1));
            Confirm();yield return null;Assert.That(Session.Currency,Is.Zero);
            Shop.Buy();Assert.That(Dialog.Maximum,Is.Zero);Assert.That(Ref<Button>(Dialog,"_confirm").interactable,Is.False);
            Ref<Button>(Dialog,"_confirm").onClick.Invoke();Assert.That(Session.Currency,Is.Zero);
        }
        [UnityTest] public IEnumerator QuickStableConfirmationHasNoScopedGcAlloc()
        {
            yield return OpenShop();var row=Offer(out _);PointerClick(row.gameObject,Center(row.transform));Shop.Buy();
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<10;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;
                Assert.That(Dialog.IsOpen,Is.True);
                WriteProfile("purchase-dialog-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,
                    new[]{"Norn.Shop.UiUpdate","Norn.Shop.SessionUpdate","Norn.Wallet.Refresh"},"稳定确认框预热后60帧；数值变化与成交帧不混入稳定scope。");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [UnityTest] public IEnumerator QuickMaterialNeedsExplicitMiddleContainerSelection()
        {
            yield return OpenShop();var offer=Water(out _);long money=Session.Currency;
            PointerClick(offer.gameObject,Center(offer.transform));
            Assert.That(Ref<Button>(Shop,"_buyButton").interactable,Is.False);
            Shop.Buy();yield return null;Assert.That(Session.Currency,Is.EqualTo(money));
            var bottle=MiddleItem("b0");PointerClick(bottle.gameObject,Center(bottle.transform));
            Assert.That(Ref<Button>(Shop,"_buyButton").interactable,Is.True,"选瓶不能清除左侧商品");
            var ordinary=MiddleItem("fire");PointerClick(ordinary.gameObject,Center(ordinary.transform));
            Assert.That(Ref<Button>(Shop,"_buyButton").interactable,Is.False,"非容器选择不能沿用隐藏Dropdown目标");
        }
        private IEnumerator BuyWaterIntoMiddleSelection(bool warehouse,bool popup)
        {
            if(warehouse){Assert.That(Session.Profile.Items.TryMove("b1",ItemLocation.Warehouse).Success,Is.True);}
            yield return OpenShop();var bottle=MiddleItem(warehouse?"b1":"b0");
            PointerClick(bottle.gameObject,Center(bottle.transform));var row=Water(out var offer);long money=Session.Currency;
            PointerClick(row.gameObject,Center(row.transform));
            Assert.That(Shop.SelectedItemId,Is.EqualTo(warehouse?"b1":"b0"),"点商品必须保留中列选中的具体瓶子");
            if(popup){PointerClick(row.gameObject,Center(row.transform));Assert.That(Popup.gameObject.activeSelf,Is.True);Popup.Find("Menu_购买/Buy_购买").GetComponent<Button>().onClick.Invoke();}
            else Ref<Button>(Shop,"_buyButton").onClick.Invoke();
            Assert.That(Session.Currency,Is.EqualTo(money),"两个入口都先打开确认框。");Confirm();
            yield return null;
            Session.ReadOnly.TryGetView(warehouse?"b1":"b0",out var filled);Session.ReadOnly.TryGetView(warehouse?"b0":"b1",out var other);
            Assert.That(filled.ContainerTotal,Is.EqualTo(101));Assert.That(other.ContainerTotal,Is.EqualTo(100));
            Assert.That(Session.Currency,Is.EqualTo(money-offer.BuyPrice));
        }
        [UnityTest] public IEnumerator QuickBackpackSelectedContainerReceivesBottomPurchase(){yield return BuyWaterIntoMiddleSelection(false,false);}
        [UnityTest] public IEnumerator QuickWarehouseSelectedContainerReceivesPopupPurchase(){yield return BuyWaterIntoMiddleSelection(true,true);}
        [UnityTest] public IEnumerator QuickWandPurchaseAndWarehouseDragBothReachBackpack()
        {
            yield return OpenShop();var read=Find<SinglePlayerShopSession>().ReadOnly;ShopOfferSnapshot offer=default;
            for(int i=0;i<read.Count;i++)if(read.GetOffer(i).Kind==ItemKind.Wand){offer=read.GetOffer(i);break;}
            Shop.SelectOffer(offer.OfferId);Ref<Button>(Shop,"_buyButton").onClick.Invoke();Confirm();yield return null;
            string purchased=null;
            foreach(var entry in Session.Profile.Items)if(entry.Item.ContentId==offer.ContentId && entry.Location.Kind==ItemLocationKind.Backpack)purchased=entry.Item.InstanceId;
            Assert.That(purchased,Is.Not.Null,"三根已装备杖不应阻止备用杖入包");
            Assert.That(Session.Profile.Items.TryMove(purchased,ItemLocation.Warehouse).Success,Is.True);
            var embedded=Ref<SinglePlayerInventoryController>(Shop,"_embeddedInventory");embedded.RefreshPresentation();Canvas.ForceUpdateCanvases();
            var view=MiddleItem(purchased);var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=Center(view.transform)};
            view.OnPointerDown(data);view.OnBeginDrag(data);
            var grid=Ref<InventoryGridView>(embedded,"_gridView");var rect=(RectTransform)grid.transform;
            // 目录中央抓住3格法杖的中间格：目标左端4格，鼠标应在5.5格；不改变生产抓取点语义。
            data.position=RectTransformUtility.WorldToScreenPoint(null,rect.TransformPoint(new Vector3(rect.rect.xMin+5.5f*grid.CellSize,rect.rect.yMax-.5f*grid.CellSize)));
            view.OnDrag(data);grid.OnDrop(data);view.OnEndDrag(data);yield return null;
            Session.ReadOnly.TryGetView(purchased,out var moved);Assert.That(moved.Location,Is.EqualTo(new GridPlacement(4,0,0).InBackpack("bag")));
        }
        [UnityTest] public IEnumerator QuickMaterialQuantityUsesOnlySelectedContainerCapacity()
        {
            yield return OpenShop();var bottle=MiddleItem("b0");PointerClick(bottle.gameObject,Center(bottle.transform));var row=Water(out _);PointerClick(row.gameObject,Center(row.transform));
            var buy=Ref<Button>(Shop,"_buyButton");long money=Session.Currency;
            buy.onClick.Invoke();Assert.That(Dialog.Maximum,Is.EqualTo(50));Dialog.SetQuantity(51);
            Assert.That(Dialog.Quantity,Is.EqualTo(50));Assert.That(Session.Currency,Is.EqualTo(money));Confirm();yield return null;
            Session.ReadOnly.TryGetView("b0",out var full);Session.ReadOnly.TryGetView("b1",out var other);
            Assert.That(full.ContainerTotal,Is.EqualTo(150));Assert.That(other.ContainerTotal,Is.EqualTo(100));Assert.That(buy.interactable,Is.False);
            PointerClick(row.gameObject,Center(row.transform));Assert.That(Popup.gameObject.activeSelf,Is.False,"已满容器不能弹出可成交入口");
        }
        [UnityTest] public IEnumerator QuickFirstClickSelectsSecondClickOpensMousePopupWithoutBuying()
        {
            yield return OpenShop(); var view = Offer(out _); long currency = Session.Currency;
            Vector2 position = Center(view.transform); PointerClick(view.gameObject, position);
            Assert.That(Popup.gameObject.activeSelf, Is.False); Assert.That(Session.Currency, Is.EqualTo(currency));
            Assert.That(view.GetComponent<Outline>()?.enabled, Is.True, "选中商品需要可见边框。");
            PointerClick(view.gameObject, position); yield return null;
            Assert.That(Popup.gameObject.activeSelf, Is.True); Assert.That(Session.Currency, Is.EqualTo(currency));
            var menu = (RectTransform)Popup.Find("Menu_购买"); var point = RectTransformUtility.WorldToScreenPoint(null, menu.TransformPoint(new Vector2(menu.rect.xMin, menu.rect.yMax)));
            Assert.That(Vector2.Distance(point, position), Is.LessThan(100), "弹层应靠近点击位置。");
            yield return new WaitForEndOfFrame(); Capture("quick-purchase-popup.png");
        }
        [UnityTest] public IEnumerator QuickPopupAndBottomButtonEachBuyOnceIntoBackpack()
        {
            yield return OpenShop(); var view = Offer(out var offer); long currency = Session.Currency; int count = Session.ReadOnly.Count;
            PointerClick(view.gameObject, Center(view.transform)); PointerClick(view.gameObject, Center(view.transform));
            var button = Popup.Find("Menu_购买/Buy_购买"); PointerClick(button.gameObject, Center(button)); yield return null;
            Assert.That(Session.Currency,Is.EqualTo(currency));Confirm();yield return null;
            Assert.That(Session.Currency, Is.EqualTo(currency - offer.BuyPrice)); Assert.That(Session.ReadOnly.Count, Is.EqualTo(count + 1));
            Assert.That(Popup.gameObject.activeSelf, Is.False); Assert.That(Shop.Status, Does.Contain("背包"));
            Ref<Button>(Shop, "_buyButton").onClick.Invoke(); yield return null;
            Assert.That(Session.Currency,Is.EqualTo(currency-offer.BuyPrice));Confirm();yield return null;
            Assert.That(Session.Currency, Is.EqualTo(currency - 2 * offer.BuyPrice)); Assert.That(Session.ReadOnly.Count, Is.EqualTo(count + 2));
            int purchased = 0; foreach (var entry in Session.Profile.Items) if (entry.Item.ContentId == offer.ContentId && entry.Item.InstanceId != "fire" && entry.Item.InstanceId != "ice")
                { Assert.That(entry.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack)); purchased++; }
            Assert.That(purchased, Is.EqualTo(2));
            yield return new WaitForEndOfFrame(); Capture("shop-quick-purchase.png");
        }
        [UnityTest] public IEnumerator QuickPopupClampsAtScreenEdgeAndEscapeClosesOnlyPopupFirst()
        {
            yield return OpenShop(); var view = Offer(out _); var edge = new Vector2(Screen.width - 2, 2);
            PointerClick(view.gameObject, edge); PointerClick(view.gameObject, edge); yield return null;
            var menu = (RectTransform)Popup.Find("Menu_购买"); var corners = new Vector3[4]; menu.GetWorldCorners(corners);
            foreach (var corner in corners) Assert.That(RectTransformUtility.RectangleContainsScreenPoint(Panel, RectTransformUtility.WorldToScreenPoint(null, corner)), Is.True);
            Keys(UnityEngine.InputSystem.Key.Escape); yield return null; Keys(); yield return null;
            Assert.That(Popup.gameObject.activeSelf, Is.False); Assert.That(Shop.IsOpen, Is.True); Assert.That(Time.timeScale, Is.Zero);
            Keys(UnityEngine.InputSystem.Key.Escape); yield return null; Keys(); yield return null; Assert.That(Shop.IsOpen, Is.False);
        }
        [UnityTest] public IEnumerator QuickPopupRemainsInsidePanelWithDifferentCanvasScale()
        {
            yield return OpenShop(); var view = Offer(out _); var scaler = Panel.GetComponentInParent<CanvasScaler>();
            Vector2 original = scaler.referenceResolution;
            try
            {
                // 改变 UI 与屏幕像素的换算比例，验证不是把屏幕坐标直接写入 anchoredPosition。
                scaler.referenceResolution = new Vector2(1280, 720); yield return null; yield return null;
                var edge = new Vector2(Screen.width - 2, 2); PointerClick(view.gameObject, edge); PointerClick(view.gameObject, edge); yield return null;
                var menu = (RectTransform)Popup.Find("Menu_购买"); var corners = new Vector3[4]; menu.GetWorldCorners(corners);
                foreach (var corner in corners) Assert.That(RectTransformUtility.RectangleContainsScreenPoint(Panel, RectTransformUtility.WorldToScreenPoint(null, corner)), Is.True);
                yield return new WaitForEndOfFrame(); Capture("quick-popup-scaled-edge.png");
            }
            finally { scaler.referenceResolution = original; }
        }
        [UnityTest] public IEnumerator QuickPopupDismissesForOwnedSelectionFilterAndScrolling()
        {
            yield return OpenShop(); var view = Offer(out _); var pos = Center(view.transform);
            PointerClick(view.gameObject, pos); PointerClick(view.gameObject, pos);
            Shop.SelectOwned("fire"); Assert.That(Popup.gameObject.activeSelf, Is.False);
            PointerClick(view.gameObject, pos); PointerClick(view.gameObject, pos); Assert.That(Popup.gameObject.activeSelf, Is.True);
            Ref<RectTransform>(Shop, "_offersRoot").GetComponentInParent<ScrollRect>().onValueChanged.Invoke(Vector2.zero);
            Assert.That(Popup.gameObject.activeSelf, Is.False);
            PointerClick(view.gameObject, pos); Assert.That(Popup.gameObject.activeSelf, Is.True);
            Ref<InputField>(Shop, "_search").text = "不会匹配商品"; Assert.That(Popup.gameObject.activeSelf, Is.False);
            Assert.That(Ref<Button>(Shop, "_buyButton").interactable, Is.False, "筛选掉当前商品后禁止购买隐藏商品。");
        }
        [UnityTest] public IEnumerator QuickFullBackpackShowsWarehouseAndWalletAfterRealButtonPurchase()
        {
            yield return OpenShop(); var view = Offer(out var offer); int count = Session.ReadOnly.Count;
            PointerClick(view.gameObject, Center(view.transform)); Ref<Button>(Shop, "_buyButton").onClick.Invoke(); yield return null;
            Assert.That(Session.ReadOnly.Count,Is.EqualTo(count));Confirm();yield return null;
            Assert.That(Session.ReadOnly.Count, Is.EqualTo(count + 1)); Assert.That(Session.Currency, Is.EqualTo(2000 - offer.BuyPrice));
            Assert.That(Shop.Status, Does.Contain("仓库 1 份"));
            foreach (var wallet in Panel.GetComponentsInChildren<WalletView>(true)) Assert.That(Ref<Text>(wallet, "_amount").text, Is.EqualTo(Session.Currency.ToString("N0")));
            var catalog = Ref<SinglePlayerInventoryController>(Shop, "_embeddedInventory").GetComponentInChildren<WarehouseCatalogGridView>(true);
            Assert.That(catalog.InstanceCount, Is.EqualTo(1)); yield return new WaitForEndOfFrame(); Capture("shop-full-backpack-fallback.png");
        }
        [UnityTest] public IEnumerator QuickActualMouseOutsideClickDismissesPopup()
        {
            yield return OpenShop(); var view = Offer(out _); PointerClick(view.gameObject, Center(view.transform)); PointerClick(view.gameObject, Center(view.transform));
            yield return null;
            // 由真实 PlayerLoop 消费鼠标事件。Coroutine 中额外手动 Update 会提前消耗 press edge，
            // 下一次自动 Input Update 已是另一个 updateStepCount，不能用这种时序测试 Update 轮询。
            InputSystem.QueueStateEvent(Mouse, new MouseState { position = new Vector2(Screen.width - 4, Screen.height / 2), buttons = 1 }); yield return null;
            Assert.That(Popup.gameObject.activeSelf, Is.False); Assert.That(Shop.IsOpen, Is.True);
            InputSystem.QueueStateEvent(Mouse, new MouseState()); yield return null;
        }
        [UnityTest] public IEnumerator QuickStableShopAndOpenPopupHaveNoScopedGcAlloc()
        {
            yield return OpenShop(); var view = Offer(out _); PointerClick(view.gameObject, Center(view.transform)); PointerClick(view.gameObject, Center(view.transform));
            bool recording = UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu = UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, true); UnityEditorInternal.ProfilerDriver.enabled = true;
                for (int i = 0; i < 10; i++) yield return null;
                int first = UnityEditorInternal.ProfilerDriver.lastFrameIndex + 1;
                for (int i = 0; i < 60; i++) yield return null;
                Assert.That(Popup.gameObject.activeSelf, Is.True);
                WriteProfile("quick-shop-profiler", first, UnityEditorInternal.ProfilerDriver.lastFrameIndex,
                    new[] { "Norn.Shop.UiUpdate", "Norn.Shop.SessionUpdate", "Norn.Shop.QuickPopupUpdate", "Norn.Wallet.Refresh" }, "隔离 Review，暂停中的稳定商店及打开的弹层；预热后采样，初始化和交易帧排除。");
            }
            finally { UnityEditorInternal.ProfilerDriver.enabled = recording; UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, cpu); }
        }
        [UnityTest] public IEnumerator QuickLayoutHasNoCartOrRightSubregionsAndKeepsSharedWallet()
        {
            yield return OpenShop();
            Assert.That(Panel.Find("Shop_Left_商品与快捷购买/CartRegion_购买预览"), Is.Null);
            var right = Panel.Find("Shop_Right_物品详情"); Assert.That(right.childCount, Is.EqualTo(1));
            Assert.That(right.GetChild(0).name, Is.EqualTo("DetailsRegion_物品详情"));
            Assert.That(Ref<Text>(Shop, "_details").text, Is.EqualTo("物品详情待后续开发"));
            Assert.That(Ref<Toggle>(Shop, "_toBackpack"), Is.Null);
            Assert.That(Ref<Button>(Shop, "_buyButton").GetComponentInChildren<Text>().text, Is.EqualTo("购买物品"));
            var wallets=Panel.GetComponentsInChildren<WalletView>(true);
            Assert.That(wallets.Length, Is.GreaterThanOrEqualTo(1),"作者已移除旧左栏金币节点，保留当前中列真实钱包即可。");
            foreach(var wallet in wallets)Assert.That(Ref<Text>(wallet,"_amount").text,Is.EqualTo(Session.Currency.ToString("N0")));
            Assert.That(Ref<InputField>(Shop,"_quantity"),Is.Null,"新版不能恢复作者删除的数量输入区。");
        }
        [UnityTest] public IEnumerator QuickUsesAuthoredCloseButtonAndTabReturnsToBackpack()
        {
            yield return OpenShop();
            Assert.That(Ref<Button>(Shop, "_closeButton").name, Is.EqualTo("Close"));
            Assert.That(Panel.Find("Close_关闭商店"), Is.Null, "不能保留两个相互覆盖的关闭按钮。");
            Ref<Button>(Shop, "_closeButton").onClick.Invoke(); yield return null;
            Assert.That(Shop.IsOpen, Is.False); Assert.That(Time.timeScale, Is.EqualTo(1));
            Shop.Open(); yield return null;
            Keys(Key.Tab); yield return null; Keys(); yield return null;
            Assert.That(Shop.IsOpen, Is.False); Assert.That(Backpack.IsOpen, Is.True);
            Keys(Key.Escape); yield return null; Keys(); yield return null;
            Assert.That(Backpack.IsOpen, Is.False); Assert.That(Time.timeScale, Is.EqualTo(1));
        }
    }
}
#endif
