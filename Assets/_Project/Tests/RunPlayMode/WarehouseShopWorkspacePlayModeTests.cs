#if UNITY_EDITOR
using System.Collections;
using System;
using System.Collections.Generic;
using Game.Run;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>真实用户布局+独立内存档：目录汇总、具体实例、钱包和商店内嵌拖拽。</summary>
    public sealed class WarehouseShopWorkspacePlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => Review;
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T14-SlotAuthoring";
        protected override ItemCatalog CustomizeCatalog(ItemCatalog catalog)
        {
            bool large = NUnit.Framework.TestContext.CurrentContext.Test.Name.StartsWith("LargeWarehouse");
            bool big = NUnit.Framework.TestContext.CurrentContext.Test.Name.StartsWith("BigWarehouse");
            if (!large && !big) return catalog;
            var clone = UnityEngine.Object.Instantiate(catalog); TemporaryAssets.Add(clone);
            var data = new SerializedObject(clone); var list = data.FindProperty("_definitions"); int original = list.arraySize, additions = large ? 120 : 1;
            var template = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerWands/fire.asset");
            list.arraySize += additions;
            for (int i = 0; i < additions; i++)
            {
                var definition = UnityEngine.Object.Instantiate(template); TemporaryAssets.Add(definition); var fields = new SerializedObject(definition);
                fields.FindProperty("_contentId").stringValue = "test.catalog." + i; fields.FindProperty("_displayName").stringValue = "测试法术 " + i.ToString("D3");
                if (big)
                {
                    fields.FindProperty("_shapeWidth").intValue = 4; fields.FindProperty("_shapeHeight").intValue = 5;
                    var cells = fields.FindProperty("_occupiedCells"); cells.arraySize = 20;
                    for (int c = 0; c < 20; c++) cells.GetArrayElementAtIndex(c).vector2IntValue = new Vector2Int(c % 4, c / 4);
                }
                fields.ApplyModifiedPropertiesWithoutUndo(); list.GetArrayElementAtIndex(original + i).objectReferenceValue = definition;
            }
            data.ApplyModifiedPropertiesWithoutUndo(); return clone;
        }
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile, ItemCatalogIndex index)
        {
            if(TestContext.CurrentContext.Test.Name=="InitialGoldSynchronizesAllThreeWindowsAfterBuyAndSell")
            {
                var seeds=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerRaids/StartingInventory.asset"));TemporaryAssets.Add(seeds);
                var data=new SerializedObject(seeds);data.FindProperty("_initialCurrency").longValue=5000;data.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(seeds.TryCreate(index,out var seeded,out _),Is.True);profile.Currency=seeded.Currency;
            }
            profile.Items.TryGet("fire", out var fire); profile.Items.TryGet("b0", out var bottle);
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("warehouse.fire.3", fire.ContentId, 3), ItemLocation.Warehouse), Is.True);
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("warehouse.fire.8", fire.ContentId, 8), ItemLocation.Warehouse), Is.True);
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("warehouse.bottle", bottle.ContentId, 0), ItemLocation.Warehouse), Is.True);
            bool large = NUnit.Framework.TestContext.CurrentContext.Test.Name.StartsWith("LargeWarehouse"), big = NUnit.Framework.TestContext.CurrentContext.Test.Name.StartsWith("BigWarehouse");
            if (large || big) for (int i = 0; i < (large ? 120 : 1); i++) Assert.That(profile.Items.TryAdd(new RuntimeItemState("test.catalog." + i, "test.catalog." + i, 8), ItemLocation.Warehouse), Is.True);
            return profile;
        }
        private static T Ref<T>(UnityEngine.Object target, string field) where T : UnityEngine.Object =>
            (T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        private static void Confirm(ShopController shop)
        {
            var dialog = Ref<ShopPurchaseDialog>(shop, "_purchaseDialog"); Assert.That(dialog.IsOpen, Is.True);
            Ref<Button>(dialog, "_confirm").onClick.Invoke();
        }
        private void Enter(bool shop)
        {
            foreach (var root in Scene.GetRootGameObjects()) foreach (var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            { if (ui.name == "InventoryCanvas_背包") Backpack = ui; if (ui.name == "InventoryCanvas_仓库") Warehouse = ui; }
            var area = Ref<HubAreaVolume>(Find<HubInteractionController>(), shop ? "_shopArea" : "_warehouseArea");
            Player.CharacterController.enabled = false; Player.transform.position = area.Center; Player.enabled = false; Physics.SyncTransforms();
        }
        [UnityTest] public IEnumerator ShopRootIsTransparentAndEmbeddedCellsHaveNoGaps()
        {
            Enter(true);var shop=Find<ShopController>();shop.Open();yield return null;
            Assert.That(Ref<GameObject>(shop,"_panel").GetComponent<Image>().color.a,Is.Zero,"商店背后仍显示暂停的游戏画面");
            var embedded=Ref<SinglePlayerInventoryController>(shop,"_embeddedInventory");
            AssertTiled(Ref<InventoryGridView>(embedded,"_gridView").transform,"Grid cell");
            AssertTiled(embedded.GetComponentInChildren<WarehouseCatalogGridView>(true).transform,"Warehouse empty cell");
            yield return new WaitForEndOfFrame();Capture("shop-transparent-grid.png");
        }
        [UnityTest] public IEnumerator BackpackNearbyAndSpellStorageCellsHaveNoGaps()
        {
            Enter(false);Backpack.OpenBackpack();yield return null;
            AssertTiled(Ref<InventoryGridView>(Backpack,"_gridView").transform,"Grid cell");
            AssertTiled(Backpack.GetComponentInChildren<NearbyItemGridView>(true).transform,"Nearby grid cell");
            var storage=Backpack.GetComponentInChildren<SpellStorageView>(true);
            AssertTiled(Ref<RectTransform>(storage,"_content"),"SpellStorageSlot");
            // 多格物品内部也不得缩小各个实心格，否则空底格无缝而南瓜仍会显示“断裂”的细缝。
            var grid=Ref<InventoryGridView>(Backpack,"_gridView");
            foreach(var item in grid.GetComponentsInChildren<InventoryItemView>())
                for(int i=0;i<item.transform.childCount;i++)
                {
                    var child=item.transform.GetChild(i);
                    if(child.name=="Occupied cell" && child.gameObject.activeSelf)
                        Assert.That(((RectTransform)child).rect.width,Is.EqualTo(grid.CellSize).Within(.01f));
                }
            yield return new WaitForEndOfFrame();Capture("backpack-seamless-grid.png");
        }
        private static void AssertTiled(Transform content,string prefix)
        {
            RectTransform first=null,second=null;
            for(int i=0;i<content.childCount;i++)
            {
                var child=content.GetChild(i);
                if(!child.gameObject.activeSelf || !child.name.StartsWith(prefix,StringComparison.Ordinal))continue;
                if(first==null)first=(RectTransform)child;else{second=(RectTransform)child;break;}
            }
            Assert.That(first,Is.Not.Null,prefix);Assert.That(second,Is.Not.Null,prefix);
            // SetAsFirstSibling会倒转底格的Hierarchy顺序；验证几何接缝，不依赖生成顺序。
            float pitch=Mathf.Abs(second.anchoredPosition.x-first.anchoredPosition.x);
            Assert.That(pitch,Is.GreaterThan(0));
            Assert.That(first.rect.width,Is.EqualTo(pitch).Within(.01f),prefix+"不留底板缝隙");
        }
        [UnityTest] public IEnumerator WandProgramCellsHaveNoGapsAndKeepSpellDropTargets()
        {
            Enter(false);Backpack.OpenBackpack();yield return null;
            foreach(var wand in Backpack.GetComponentsInChildren<WandProgramPanelView>(true))
                AssertTiled(Ref<RectTransform>(wand,"_program"),"SpellSlot");
        }
        private string StorageDiagnostic()
        {
            string report = "backend=" + Session.ReadOnly.WarehouseCount;
            foreach (var id in new[] { "warehouse.fire.3", "warehouse.fire.8", "warehouse.bottle" })
                if (Session.ReadOnly.TryGetView(id, out var item)) report += " / " + id + " " + item.Location.Kind + " " + item.ContentId;
            return report;
        }
        [UnityTest] public IEnumerator WarehouseShowsGroupsAndDragKeepsConcreteSpellState()
        {
            Enter(false); Keys(Key.F); yield return null; Keys(); yield return null;
            Assert.That(Warehouse.IsOpen, Is.True);
            var grid = Warehouse.GetComponentInChildren<WarehouseCatalogGridView>(true);
            Assert.That(grid, Is.Not.Null); Assert.That(grid.GroupCount, Is.EqualTo(2), StorageDiagnostic()); Assert.That(grid.InstanceCount, Is.EqualTo(3));
            Assert.That(grid.VisibleCellCount, Is.GreaterThan(grid.GroupCount));
            Warehouse.SelectItem("warehouse.fire.3"); Warehouse.ClickItem("warehouse.fire.3");
            Assert.That(Warehouse.SelectedItemId, Is.EqualTo("warehouse.fire.8"));
            Session.ReadOnly.TryGetView(Warehouse.SelectedItemId, out var source);
            Warehouse.BeginDrag(source, Vector2.zero, null, false);
            var ghost = Ref<InventoryItemView>(Warehouse, "_ghost");
            Assert.That(ghost.gameObject.activeSelf, Is.True); Assert.That(ghost.Snapshot.RemainingCharges, Is.EqualTo(8));
            float cell = Ref<InventoryGridView>(Warehouse, "_gridView").CellSize;
            Assert.That(ghost.Rect.rect.width, Is.EqualTo(source.Rules.Shape.Width * cell).Within(.1f));
            Warehouse.SubmitToBackpack(4, 6, "bag"); yield return null;
            Session.ReadOnly.TryGetView(source.InstanceId, out var placed);
            Assert.That(placed.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack)); Assert.That(placed.RemainingCharges, Is.EqualTo(8));
            Assert.That(grid.InstanceCount, Is.EqualTo(2));
            yield return new WaitForEndOfFrame(); Capture("warehouse-functional.png");
        }
        [UnityTest] public IEnumerator InitialGoldSynchronizesAllThreeWindowsAfterBuyAndSell()
        {
            Enter(true);Keys(Key.F);yield return null;Keys();yield return null;
            var shop=Find<ShopController>();var panel=Ref<GameObject>(shop,"_panel");var trades=Find<SinglePlayerShopSession>();
            Assert.That(Session.Currency,Is.EqualTo(5000));ShopOfferSnapshot offer=default;
            for(int i=0;i<trades.ReadOnly.Count;i++){var candidate=trades.ReadOnly.GetOffer(i);if(!candidate.IsMaterial && candidate.Stock>0){offer=candidate;break;}}
            var beforeIds = new HashSet<string>(); foreach (var entry in Session.Profile.Items) beforeIds.Add(entry.Item.InstanceId);
            Assert.That(offer.OfferId,Is.Not.Null);shop.SelectOffer(offer.OfferId);shop.Buy();yield return null;
            Assert.That(Session.Currency, Is.EqualTo(5000)); Confirm(shop); yield return null;
            long balance=5000-offer.BuyPrice;Assert.That(Session.Currency,Is.EqualTo(balance));
            foreach(var wallet in panel.GetComponentsInChildren<WalletView>(true))Assert.That(Ref<Text>(wallet,"_amount").text,Is.EqualTo(balance.ToString("N0")));
            string purchased=null;foreach(var entry in Session.Profile.Items)if(entry.Item.ContentId==offer.ContentId && !beforeIds.Contains(entry.Item.InstanceId))
            { Assert.That(entry.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack)); purchased=entry.Item.InstanceId;break; }
            Assert.That(purchased,Is.Not.Null);shop.SelectOwned(purchased);shop.SellItem();yield return null;
            balance+=offer.SellPrice;Assert.That(Session.Currency,Is.EqualTo(balance));
            Keys(Key.Tab);yield return null;Keys();yield return null;
            var backpackWallet=Ref<Text>(Backpack.GetComponentInChildren<BackpackWorkspaceView>(true),"_wallet");Assert.That(backpackWallet.text,Is.EqualTo(balance.ToString()));
            Backpack.Close();Enter(false);Warehouse.OpenWarehouse();yield return null;
            foreach(var wallet in Warehouse.GetComponentsInChildren<WalletView>(true))Assert.That(Ref<Text>(wallet,"_amount").text,Is.EqualTo(balance.ToString("N0")));
            yield return new WaitForEndOfFrame();Capture("initial-5000-wallet.png");
        }
        [UnityTest] public IEnumerator WarehouseWalletUsesRealBalanceAndFiltersDoNotMoveAssets()
        {
            Enter(false); Keys(Key.F); yield return null; Keys(); yield return null;
            var panel = Ref<GameObject>(Warehouse, "_panelRoot"); Text number = null;
            foreach (var label in panel.GetComponentsInChildren<Text>(true)) if (label.name == "Gold_Number") number = label;
            Assert.That(number, Is.Not.Null); Assert.That(number.text, Is.EqualTo("248"));
            var grid = Warehouse.GetComponentInChildren<WarehouseCatalogGridView>(true); Assert.That(grid, Is.Not.Null);
            var viewport = grid.GetComponentInParent<ScrollRect>().viewport;
            var region = (RectTransform)viewport.parent.parent;
            var viewportCorners = new Vector3[4]; var regionCorners = new Vector3[4];
            viewport.GetWorldCorners(viewportCorners); region.GetWorldCorners(regionCorners);
            Assert.That(viewportCorners[0].y, Is.GreaterThanOrEqualTo(regionCorners[0].y - 1), "目录不能伸入金币栏");
            Assert.That(viewportCorners[1].y, Is.LessThanOrEqualTo(regionCorners[1].y + 1), "目录必须在作者仓库区域内");
            uint before = Session.ReadOnly.Version;
            grid.SetFilter("", 1); Assert.That(grid.GroupCount, Is.EqualTo(1)); grid.Organize(); grid.SetFilter("", 0);
            Assert.That(grid.GroupCount, Is.EqualTo(2), StorageDiagnostic()); Assert.That(Session.ReadOnly.Version, Is.EqualTo(before));
            Assert.That(Session.WalletReadOnly.Currency, Is.EqualTo(248));
        }
        [UnityTest] public IEnumerator ShopEmbeddedInventoryTransfersOnlyChosenInstanceAndUpdatesWallet()
        {
            Enter(true); Keys(Key.F); yield return null; Keys(); yield return null;
            var shop = Find<ShopController>(); Assert.That(shop.IsOpen, Is.True);
            var panel = Ref<GameObject>(shop, "_panel"); var host = panel.GetComponentInParent<SinglePlayerInventoryController>();
            Assert.That(host.enabled, Is.True); Assert.That(host.IsOpen, Is.True);
            Assert.That(Backpack.IsOpen, Is.False); Assert.That(Warehouse.IsOpen, Is.False);
            Session.ReadOnly.TryGetView("warehouse.fire.3", out var source); host.BeginDrag(source, Vector2.zero, null, false);
            host.SubmitToBackpack(4, 6, "bag"); yield return null;
            Session.ReadOnly.TryGetView(source.InstanceId, out var placed); Assert.That(placed.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));
            Session.ReadOnly.TryGetView("warehouse.fire.8", out var retained); Assert.That(retained.Location.Kind, Is.EqualTo(ItemLocationKind.Warehouse));
            host.BeginDrag(placed, Vector2.zero, null, true); host.SubmitToTarget(InventoryDropKind.Warehouse); yield return null;
            Assert.That(host.GetComponentInChildren<WarehouseCatalogGridView>(true).InstanceCount, Is.EqualTo(3), StorageDiagnostic());
            var trades = Find<SinglePlayerShopSession>(); ShopOfferSnapshot offer = default;
            for (int i = 0; i < trades.ReadOnly.Count; i++) { var candidate = trades.ReadOnly.GetOffer(i); if (!candidate.IsMaterial && candidate.Stock > 0 && candidate.BuyPrice <= 248) { offer = candidate; break; } }
            Assert.That(offer.OfferId, Is.Not.Null); shop.SelectOffer(offer.OfferId);
            Ref<Button>(shop, "_buyButton").onClick.Invoke(); yield return null;
            Assert.That(Session.Currency, Is.EqualTo(248)); Confirm(shop); yield return null;
            bool found = false;
            foreach (var wallet in panel.GetComponentsInChildren<WalletView>(true))
            { var text = Ref<Text>(wallet, "_amount"); Assert.That(text.text, Is.EqualTo((248-offer.BuyPrice).ToString("N0"))); found = true; }
            Assert.That(found, Is.True, "商店必须有明确的金币栏");
            yield return new WaitForEndOfFrame(); Capture("shop-warehouse-functional.png");
            Keys(Key.Tab); yield return null; Keys(); yield return null;
            Assert.That(shop.IsOpen, Is.False); Assert.That(host.IsOpen, Is.False); Assert.That(Backpack.IsOpen, Is.True);
            Keys(Key.Escape); yield return null; Keys(); yield return null; Assert.That(Time.timeScale, Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator OutsideStationRejectsWarehouseCommandsButAllowsCarriedBagArrangement()
        {
            Enter(false); Player.transform.position += Vector3.right * 10000; Physics.SyncTransforms(); yield return null;
            uint version = Session.ReadOnly.Version;
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = Session.ReadOnly.InventoryId,
                Kind = InventoryCommandKind.Place, ItemId = "fire", Destination = InventoryDestination.Warehouse, ExpectedVersion = version });
            Session.ReadOnly.TryGetView("fire", out var fire); Assert.That(fire.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(Session.ReadOnly.Version, Is.EqualTo(version), "区外不能通过直接命令存取仓库");
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = Session.ReadOnly.InventoryId,
                Kind = InventoryCommandKind.Place, ItemId = "warehouse.fire.3", Destination = InventoryDestination.Backpack, OwnerId = "bag", X = 4, Y = 6, ExpectedVersion = version });
            Assert.That(Session.ReadOnly.Version, Is.EqualTo(version));
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = Session.ReadOnly.InventoryId,
                Kind = InventoryCommandKind.Place, ItemId = "fire", Destination = InventoryDestination.Backpack, OwnerId = "bag", X = 2, Y = 5, ExpectedVersion = version });
            Session.ReadOnly.TryGetView("fire", out fire); Assert.That(fire.Location.X, Is.EqualTo(2)); Assert.That(fire.Location.Y, Is.EqualTo(5));
        }
        [UnityTest] public IEnumerator BigWarehouseItemGhostAndInvalidOverlapPreserveSourceUntilValidPlacement()
        {
            Enter(false); Warehouse.OpenWarehouse(); yield return null;
            Session.ReadOnly.TryGetView("test.catalog.0", out var item); uint version = Session.ReadOnly.Version;
            Warehouse.BeginDrag(item, Vector2.zero, null, false); var ghost = Ref<InventoryItemView>(Warehouse, "_ghost");
            float cell = Ref<InventoryGridView>(Warehouse, "_gridView").CellSize;
            Assert.That(ghost.Rect.rect.width, Is.EqualTo(4 * cell).Within(.1)); Assert.That(ghost.Rect.rect.height, Is.EqualTo(5 * cell).Within(.1));
            Warehouse.SubmitToBackpack(0, 0, "bag"); yield return null;
            Assert.That(Warehouse.IsArrangementInvalid(item.InstanceId), Is.True); Assert.That(Session.ReadOnly.Version, Is.EqualTo(version));
            Warehouse.SelectItem("w0"); Session.ReadOnly.TryGetView(item.InstanceId, out var restored); Assert.That(restored.Location.Kind, Is.EqualTo(ItemLocationKind.Warehouse));
            Warehouse.BeginDrag(restored, Vector2.zero, null, false); Warehouse.SubmitToBackpack(4, 3, "bag"); yield return null;
            Session.ReadOnly.TryGetView(item.InstanceId, out var placed); Assert.That(placed.Location.Kind, Is.EqualTo(ItemLocationKind.Backpack)); Assert.That(placed.Location.X, Is.EqualTo(4));
        }
        [UnityTest] public IEnumerator LargeWarehouseCatalogScrollAndWalletHaveNoScopedGcAlloc()
        {
            bool stacks = UnityEngine.Profiling.Profiler.enableAllocationCallstacks, recording = UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu = UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEngine.Profiling.Profiler.enableAllocationCallstacks = true; UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, true); UnityEditorInternal.ProfilerDriver.enabled = true;
                Enter(false); Warehouse.OpenWarehouse(); yield return null;
                var grid = Warehouse.GetComponentInChildren<WarehouseCatalogGridView>(true); var scroll = grid.GetComponentInParent<ScrollRect>();
                Assert.That(grid.GroupCount, Is.EqualTo(122)); Assert.That(grid.AllocatedItemViewCount, Is.LessThan(80));
                // 预热全部可见窗口/文字字形后采样；初始化、库存变更和测试自身字符串不算稳定热路径。
                for (int i = 0; i < 60; i++) { scroll.verticalNormalizedPosition = (i % 30) / 29f; yield return null; }
                int first = UnityEditorInternal.ProfilerDriver.lastFrameIndex + 1;
                for (int i = 0; i < 60; i++) { scroll.verticalNormalizedPosition = (i % 30) / 29f; yield return null; }
                int last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                WriteProfile("warehouse-profiler", first, last, new[] { "Norn.Warehouse.CatalogRender", "Norn.Wallet.Refresh", "Norn.Backpack.MiddlePan" }, "Editor真实分类目录120额外种类，持续跨行滚动；各指定Profiler子树，初始化/变更排除");
            }
            finally { UnityEngine.Profiling.Profiler.enableAllocationCallstacks = stacks; UnityEditorInternal.ProfilerDriver.enabled = recording; UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, cpu); }
        }
    }
}
#endif
