#if UNITY_EDITOR
using System.Collections;
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
    /// <summary>使用作者新布局：前三格真的没有Title/Value，后七格真的没有Mana/Cooldown。</summary>
    public sealed class HeldItemHudPresentationPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath => "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_UnifiedItemsReview.unity";
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T8-InventoryAudit";
        private HeldItemHud Hud => Find<HeldItemHud>();
        protected override ItemCatalog CustomizeCatalog(ItemCatalog catalog)
        {
            if (!TestContext.CurrentContext.Test.Name.StartsWith("LargeCapacity")) return catalog;
            var clone = Object.Instantiate(catalog); TemporaryAssets.Add(clone);
            var data = new SerializedObject(clone); var definitions = data.FindProperty("_definitions");
            for (int i = 0; i < definitions.arraySize; i++)
            {
                var original = definitions.GetArrayElementAtIndex(i).objectReferenceValue as ItemDefinition;
                if (original == null || original.Kind != ItemKind.Container) continue;
                var item = Object.Instantiate(original); TemporaryAssets.Add(item); var fields = new SerializedObject(item);
                fields.FindProperty("_containerCapacityGmu").intValue = 8192; fields.ApplyModifiedPropertiesWithoutUndo();
                definitions.GetArrayElementAtIndex(i).objectReferenceValue = item;
            }
            data.ApplyModifiedPropertiesWithoutUndo(); return clone;
        }
        private T Ref<T>(int slot, string name) where T : Object => (T)new SerializedObject(Hud).FindProperty("_cells")
            .GetArrayElementAtIndex(slot).FindPropertyRelative(name).objectReferenceValue;

        [UnityTest] public IEnumerator RefinedTenSlotsShowOnlyTheirOwnResourceType()
        {
            for (int i = 0; i < HeldItemSlots.TotalCount; i++)
            {
                Assert.That(Ref<Image>(i, "Icon"), Is.Not.Null, "Icon " + i);
                if (i < HeldItemSlots.WandCount)
                {
                    Assert.That(Ref<Text>(i, "Title"), Is.Null); Assert.That(Ref<Text>(i, "Value"), Is.Null);
                    Assert.That(Ref<Image>(i, "Mana"), Is.Not.Null); Assert.That(Ref<Image>(i, "Cooldown"), Is.Not.Null);
                    Assert.That(Ref<Image>(i, "Mana").fillAmount, Is.EqualTo(1).Within(.01f));
                }
                else
                {
                    Assert.That(Ref<Image>(i, "Mana"), Is.Null); Assert.That(Ref<Image>(i, "Cooldown"), Is.Null);
                    Assert.That(Ref<Text>(i, "Value").text, Is.Empty);
                }
            }
            Bind(0, "b0"); Bind(6, "b1"); yield return null;
            Assert.That(Ref<Text>(3, "Value").text, Is.Empty); Assert.That(Fill(3).fillAmount, Is.EqualTo(100f/15000).Within(.00001f));
            Assert.That(Ref<Text>(9, "Value").text, Is.Empty); Assert.That(Fill(9).fillAmount, Is.EqualTo(100f/15000).Within(.00001f));
            Keys(Key.Digit0); yield return null; Keys(); yield return null;
            Assert.That(Session.ToolbeltReadOnly.SelectedItemId, Is.EqualTo("b1"));
            yield return new WaitForEndOfFrame();
            Assert.That(Fill(3).enabled,Is.True,"容量用容器本体填充，不再生成数字字形");
            Capture("refined-held-hud.png");
        }

        [UnityTest] public IEnumerator ThrowingConcreteContainerClearsCountAndCapacityWithoutTouchingOtherBottle()
        {
            Bind(0, "b0"); Bind(1, "b1"); MoveToExperiment();
            Keys(Key.Digit4); yield return null; Keys(); yield return null;
            Keys(Key.G); yield return null; Keys(); yield return null;
            Assert.That(Session.ToolbeltReadOnly.TryGetBinding(0, out _), Is.False);
            Assert.That(Ref<Text>(3, "Value").text, Is.Empty);
            Assert.That(Ref<Image>(3, "Icon").enabled, Is.False);
            Assert.That(Ref<Text>(4, "Value").text, Is.Empty); Assert.That(Fill(4).fillAmount, Is.EqualTo(100f/15000).Within(.00001f));
            Backpack.OpenBackpack(); yield return null;
            Assert.That(Hud.GetComponentInParent<Canvas>().enabled, Is.False);
            Backpack.Close(); yield return null;
            Assert.That(Hud.GetComponentInParent<Canvas>().enabled, Is.True);
        }
        [UnityTest] public IEnumerator LargeCapacityFillTracksPourWithoutScopedGcAlloc()
        {
            Bind(0, "b0"); MoveToExperiment(); Keys(Key.Digit4); yield return null; Keys(); yield return null;
            // 路径现在跟随Camera；固定站在实验区不等于瞄准实验域内。域外拒绝撒料应保留原储量。
            AimIntoExperiment();
            var value = Ref<Text>(3, "Value"); Assert.That(value.text, Is.Empty);
            Assert.That(Fill(3).fillAmount, Is.EqualTo(100f/8192).Within(.00001f));
            bool enabled = UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu = UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.enabled = true; UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, true);
                Click(true); for (int i = 0; i < 10; i++) yield return null;
                int first = UnityEditorInternal.ProfilerDriver.lastFrameIndex + 1;
                for (int i = 0; i < 60; i++) yield return null;
                WriteProfile("large-container-hud-profiler", first, UnityEditorInternal.ProfilerDriver.lastFrameIndex,
                    new[] { "Norn.HeldItemHud.Update" }, "8192容量容器持续倒料，Icon比例填充，无配置变化");
                Assert.That(Bottle("b0"), Is.LessThan(100));
                Assert.That(Fill(3).fillAmount, Is.EqualTo((float)Bottle("b0")/8192).Within(.00001f));
                Click(false); yield return new WaitForEndOfFrame(); Capture("large-container-hud.png");
            }
            finally { UnityEditorInternal.ProfilerDriver.enabled = enabled; UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, cpu); }
        }
        private Image Fill(int slot)=>Ref<Image>(slot,"Icon").transform.Find(ContainerIconFill.ChildName).GetComponent<Image>();
        [UnityTest] public IEnumerator ClosingWorkspacesClearsDragGhostsWithoutMovingSource()
        {
            Session.ReadOnly.TryGetView("b0", out var item); uint version = Session.ReadOnly.Version;
            Backpack.OpenBackpack(); yield return null;
            AssertGhostCloses(Backpack, item);
            var hub = Find<HubInteractionController>();
            var area = (HubAreaVolume)new SerializedObject(hub).FindProperty("_warehouseArea").objectReferenceValue;
            Player.CharacterController.enabled = false; Player.enabled = false; Player.transform.position = area.Center; Physics.SyncTransforms();
            Warehouse.OpenWarehouse(); yield return null; Assert.That(Warehouse.IsOpen, Is.True);
            AssertGhostCloses(Warehouse, item);
            area = (HubAreaVolume)new SerializedObject(hub).FindProperty("_shopArea").objectReferenceValue;
            Player.transform.position = area.Center; Physics.SyncTransforms(); Keys(Key.F); yield return null; Keys(); yield return null;
            var shop = Find<ShopController>(); Assert.That(shop.IsOpen, Is.True);
            var embedded = (SinglePlayerInventoryController)new SerializedObject(shop).FindProperty("_embeddedInventory").objectReferenceValue;
            embedded.BeginDrag(item, Vector2.zero, null, true);
            var ghost = (InventoryItemView)new SerializedObject(embedded).FindProperty("_ghost").objectReferenceValue;
            Assert.That(ghost.gameObject.activeSelf, Is.True); shop.Close();
            Assert.That(ghost.gameObject.activeSelf, Is.False); Assert.That(embedded.IsDragging, Is.False);
            Assert.That(Session.ReadOnly.Version, Is.EqualTo(version));
            Session.ReadOnly.TryGetView("b0", out var retained); Assert.That(retained.Location, Is.EqualTo(item.Location));
        }
        private static void AssertGhostCloses(SinglePlayerInventoryController controller, InventoryItemSnapshot item)
        {
            controller.BeginDrag(item, Vector2.zero, null, true);
            var ghost = (InventoryItemView)new SerializedObject(controller).FindProperty("_ghost").objectReferenceValue;
            Assert.That(ghost.gameObject.activeSelf, Is.True); controller.Close();
            Assert.That(ghost.gameObject.activeSelf, Is.False); Assert.That(controller.IsDragging, Is.False);
        }
    }
}
#endif
