#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Game.Run;

namespace Game.Run.Tests
{
    /// <summary>复制最新作者布局到独立Review；测试输入和暂停，资产继续使用基类内存档。</summary>
    public sealed class WarehouseDraftPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_WarehouseDraftReview.unity";
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T1-WarehouseDraft";
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile, ItemCatalogIndex index)
        {
            // 作者已删除旧撤离文字但保留HUD组件；隔离无关的空引用，红测试只验证Tab/仓库接线。
            foreach (var root in Scene.GetRootGameObjects())
                foreach (var ui in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (ui is RaidResultView && new SerializedObject(ui).FindProperty("_result").objectReferenceValue == null) ui.enabled = false;
                    if (ui is ExtractionHud && new SerializedObject(ui).FindProperty("_condition").objectReferenceValue == null) ui.enabled = false;
                }
            return profile;
        }
        private void ResolveWindows()
        {
            // Canvas复制后组件类型相同，测试按作者命名确认目标，防止把错误打开的仓库当成背包。
            foreach (var root in Scene.GetRootGameObjects())
                foreach (var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
                {
                    if (ui.name == "InventoryCanvas_背包") Backpack = ui;
                    if (ui.name == "InventoryCanvas_仓库") Warehouse = ui;
                }
        }
        [UnityTest] public IEnumerator TabTargetsEnabledBackpackAndRestoresPause()
        {
            ResolveWindows();
            Keys(Key.Tab); yield return null; Keys(); yield return null;
            Assert.That(Backpack.IsOpen, Is.True, "真正的背包Canvas必须能够接收Tab");
            Assert.That(Warehouse.IsOpen, Is.False, "仓库不能复制订阅Tab");
            Assert.That(Time.timeScale, Is.Zero);
            Keys(Key.Tab); yield return null; Keys(); yield return null;
            Assert.That(Backpack.IsOpen, Is.False); Assert.That(Time.timeScale, Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator WarehouseEOpensAndTabSwitchesBackpack()
        {
            ResolveWindows(); var hub = Find<HubInteractionController>();
            var area = (HubAreaVolume)new SerializedObject(hub).FindProperty("_warehouseArea").objectReferenceValue;
            Player.CharacterController.enabled = false;
            Player.transform.position = area.Center;
            Player.enabled = false; Physics.SyncTransforms();
            Keys(Key.E); yield return null; Keys(); yield return null;
            Assert.That(Warehouse.IsOpen, Is.True); Assert.That(Warehouse.WarehouseMode, Is.True);
            Assert.That(Backpack.IsOpen, Is.False);
            yield return new WaitForEndOfFrame();
            var storePanel=(GameObject)new SerializedObject(Warehouse).FindProperty("_panelRoot").objectReferenceValue;
            var programming=(RectTransform)storePanel.transform.Find("MiddlePart/WandProgrammingRegion");
            foreach(var row in programming.GetComponentsInChildren<WandProgramPanelView>())
            {
                var header=(Text)new SerializedObject(row).FindProperty("_header").objectReferenceValue;
                Assert.That(RectTransformUtility.RectangleContainsScreenPoint(programming,RectTransformUtility.WorldToScreenPoint(null,header.rectTransform.position)),Is.True,"每根法杖标题都应在橙色区域内");
            }
            var hud=Find<HeldItemHud>();Assert.That(hud.GetComponentInParent<Canvas>().enabled,Is.False,"仓库打开时手持HUD不能盖住标题");
            Capture("warehouse-draft.png");
            Keys(Key.Tab); yield return null; Keys(); yield return null;
            Assert.That(Backpack.IsOpen, Is.True); Assert.That(Warehouse.IsOpen, Is.False);
            Assert.That(Time.timeScale, Is.Zero);
            yield return new WaitForEndOfFrame(); Capture("backpack-tab.png");
            Keys(Key.Escape); yield return null; Keys(); yield return null;
            Assert.That(Backpack.IsOpen, Is.False); Assert.That(Time.timeScale, Is.EqualTo(1));
        }
        [UnityTest] public IEnumerator DraftKeepsRealBagAndGhostWithoutDuplicatingAssets()
        {
            ResolveWindows(); uint version = Session.ReadOnly.Version;
            // 布局草稿沿用原有资产UI：格子、法杖和Ghost仍引用同一会话，没有第二份库存。
            var data = new SerializedObject(Warehouse);
            var panel = (GameObject)data.FindProperty("_panelRoot").objectReferenceValue;
            Assert.That(panel.transform.Find("LeftPart/WarehouseRegion_仓库存储"), Is.Not.Null);
            Assert.That(panel.transform.Find("MiddlePart/CarriedBackpackRegion_携带背包"), Is.Not.Null);
            Assert.That(data.FindProperty("_toggleAction").objectReferenceValue, Is.Null);
            var ghost = (InventoryItemView)data.FindProperty("_ghost").objectReferenceValue;
            Assert.That(ghost, Is.Not.Null); Assert.That(ghost.GetComponent<CanvasGroup>().blocksRaycasts, Is.False);
            Backpack.OpenBackpack(); yield return null;
            Session.ReadOnly.TryGetView("b0", out var item);
            Backpack.BeginDrag(item, new Vector2(300,300), null, true); yield return null;
            var bagGhost = (InventoryItemView)new SerializedObject(Backpack).FindProperty("_ghost").objectReferenceValue;
            Assert.That(bagGhost.gameObject.activeInHierarchy, Is.True);
            Backpack.CancelDrag(); Backpack.Close();
            Assert.That(Session.ReadOnly.Version, Is.EqualTo(version));
        }
        [UnityTest] public IEnumerator WarehouseTransfersSameInstanceAndKeepsScrollableBag()
        {
            ResolveWindows(); var hub=Find<HubInteractionController>();
            var area=(HubAreaVolume)new SerializedObject(hub).FindProperty("_warehouseArea").objectReferenceValue;
            Player.CharacterController.enabled=false;Player.transform.position=area.Center;Player.enabled=false;Physics.SyncTransforms();
            Keys(Key.E);yield return null;Keys();yield return null;
            int count=Session.ReadOnly.Count;
            Session.ReadOnly.TryGetView("fire",out var item);Warehouse.BeginDrag(item,Vector2.zero,null,true);
            Warehouse.SubmitToTarget(InventoryDropKind.Warehouse);yield return null;
            Assert.That(Session.ReadOnly.TryGetView("fire",out item),Is.True);
            Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.Warehouse));
            var data=new SerializedObject(Warehouse);var grid=(InventoryGridView)data.FindProperty("_gridView").objectReferenceValue;
            Assert.That(grid.BackpackId,Is.EqualTo("bag"));Assert.That(grid.GetComponentInParent<ScrollRect>(),Is.Not.Null);
            var stock=(RectTransform)data.FindProperty("_warehouseRoot").objectReferenceValue;
            InventoryItemView card=null;
            foreach(var view in stock.GetComponentsInChildren<InventoryItemView>())if(view.Snapshot.InstanceId=="fire")card=view;
            Assert.That(card,Is.Not.Null);Assert.That(card.Rect.rect.width,Is.GreaterThan(90));
            yield return new WaitForEndOfFrame();Capture("warehouse-real-item.png");
            Warehouse.BeginDrag(item,Vector2.zero,null,true);Warehouse.SubmitToBackpack(0,4,"bag");yield return null;
            Assert.That(Session.ReadOnly.TryGetView("fire",out item),Is.True);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(Session.ReadOnly.Count,Is.EqualTo(count));Warehouse.Close();Assert.That(Time.timeScale,Is.EqualTo(1));
        }
    }
}
#endif
