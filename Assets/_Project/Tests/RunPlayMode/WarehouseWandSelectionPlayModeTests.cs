#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>仓库独立切换查看，背包仍全展开；用真实槽目标验证编辑没有写入错误法杖。</summary>
    public sealed class WarehouseWandSelectionPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => Review;
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T2-WarehouseShop";
        private WandInventoryView OpenWarehouse()
        {
            foreach (var root in Scene.GetRootGameObjects()) foreach (var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))
            { if (ui.name == "InventoryCanvas_背包") Backpack = ui; if (ui.name == "InventoryCanvas_仓库") Warehouse = ui; }
            var view = (WandInventoryView)new SerializedObject(Warehouse).FindProperty("_wandsView").objectReferenceValue;
            var hub = Find<HubInteractionController>();
            var area = (HubAreaVolume)new SerializedObject(hub).FindProperty("_warehouseArea").objectReferenceValue;
            Player.CharacterController.enabled = false; Player.transform.position = area.Center; Player.enabled = false; Physics.SyncTransforms();
            Warehouse.OpenWarehouse(); return view;
        }
        [UnityTest] public IEnumerator WarehouseCardSwitchesSingleProgramWithoutChangingHeldWand()
        {
            var view = OpenWarehouse(); yield return null;
            var mode = new SerializedObject(view).FindProperty("_selectorLayout");
            Assert.That(mode, Is.Not.Null, "仓库需要独立的选择卡展示配置"); Assert.That(mode.boolValue, Is.True);
            Assert.That(view.GetComponentsInChildren<WandProgramPanelView>().Length, Is.Zero, "仓库不能同时展示三个程序");
            string held = Session.WandsReadOnly.SelectedWandId; uint version = Session.ReadOnly.Version;
            InventoryItemView card = null;
            foreach (var candidate in view.GetComponentsInChildren<InventoryItemView>()) if (candidate.Snapshot.InstanceId == "w1") card = candidate;
            Assert.That(card, Is.Not.Null, "第二根法杖必须具有可点击选择卡");
            card.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }); yield return null;
            var data = new SerializedObject(view); var program = (RectTransform)data.FindProperty("_programRoot").objectReferenceValue;
            var title = (Text)data.FindProperty("_programTitle").objectReferenceValue;
            Assert.That(title.text, Does.Contain("法杖 B")); Assert.That(Session.WandsReadOnly.SelectedWandId, Is.EqualTo(held));
            Assert.That(Session.ReadOnly.Version, Is.EqualTo(version));
            foreach (var target in program.GetComponentsInChildren<InventoryDropTarget>())
                Assert.That(typeof(InventoryDropTarget).GetField("_ownerId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(target), Is.EqualTo("w1"));
            yield return new WaitForEndOfFrame(); Capture("warehouse-wand-selector.png");
            Warehouse.Close(); Backpack.OpenBackpack(); yield return null;
            Assert.That(Backpack.GetComponentsInChildren<WandProgramPanelView>().Length, Is.EqualTo(3), "背包三根程序仍完整展开");
        }
        [UnityTest] public IEnumerator SelectedProgramDropEditsOnlyThatWandAndEmptySlotRemainsSelectable()
        {
            var view = OpenWarehouse(); yield return null;
            Assert.That(new SerializedObject(view).FindProperty("_selectorLayout"), Is.Not.Null);
            Warehouse.SelectItem("w2"); Session.ReadOnly.TryGetView("fire", out var fire);
            Warehouse.BeginDrag(fire, Vector2.zero, null, false); Warehouse.SubmitToTarget(InventoryDropKind.ProgramSlot, "w2", 0); yield return null;
            Session.ReadOnly.TryGetView("fire", out var placed); Assert.That(placed.Location.OwnerId, Is.EqualTo("w2"));
            Warehouse.SelectItem("w0"); yield return null;
            var root = (RectTransform)new SerializedObject(view).FindProperty("_programRoot").objectReferenceValue;
            Assert.That(root.GetComponentsInChildren<InventoryItemView>().Length, Is.Zero, "切到空程序应清除上一根的法术显示");
            Assert.That(root.GetComponentsInChildren<InventoryDropTarget>().Length, Is.GreaterThan(0), "空程序保留可接收法术的槽");
        }
    }
}
#endif
