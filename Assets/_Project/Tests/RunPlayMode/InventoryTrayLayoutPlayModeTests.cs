#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Game.Run.Tests
{
    /// <summary>作者外框保持不动；动态槽必须在内框内居中，横滚不能丢掉真实容量。</summary>
    public sealed class InventoryTrayLayoutPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T16-LayoutPerformance";
        private static T Ref<T>(Object target,string field) where T:Object=>new SerializedObject(target).FindProperty(field).objectReferenceValue as T;
        private void EnterWarehouse()
        {var area=Ref<HubAreaVolume>(Find<HubInteractionController>(),"_warehouseArea");Player.CharacterController.enabled=false;Player.enabled=false;Player.transform.position=area.Center;Physics.SyncTransforms();}
        [UnityTest] public IEnumerator WarehouseStatsUseHorizontalFlowAndDeletedToolRowStaysAbsent()
        {
            EnterWarehouse();Warehouse.OpenWarehouse();yield return null;
            var view=Warehouse.GetComponentInChildren<WandInventoryView>();var stats=Ref<Text>(view,"_stats");
            Assert.That(stats.text,Does.Not.Contain("\n"),"所有现有属性横排，由 Text 根据可用宽度自然换行");
            foreach(var value in new[]{"Mana","冷却","保护","恢复","蓝耗"})Assert.That(stats.text,Does.Contain(value));
            Assert.That(Warehouse.GetComponentsInChildren<ToolbeltSlotView>(true),Is.Empty,"不能恢复作者删除的独立道具栏");
            yield return new WaitForEndOfFrame();Capture("warehouse-horizontal-stats.png");
        }
        [UnityTest] public IEnumerator BackpackAndWarehouseTrayHaveSymmetricPaddingAndReachLastSlot()
        {
            Backpack.OpenBackpack();yield return null;yield return null;CheckTray(Backpack);
            yield return new WaitForEndOfFrame();Capture("backpack-inset-tray.png");
            Backpack.Close();EnterWarehouse();Warehouse.OpenWarehouse();yield return null;yield return null;CheckTray(Warehouse);
            yield return new WaitForEndOfFrame();Capture("warehouse-centered-tray.png");
        }
        [UnityTest] public IEnumerator LastStorageSlotAcceptsActualSpellDropInBothWorkspaces()
        {
            Backpack.OpenBackpack();yield return null;yield return null;
            DropLast(Backpack,"fire");yield return null;
            Session.ReadOnly.TryGetView("fire",out var first);Assert.That(first.Location.Kind,Is.EqualTo(ItemLocationKind.SpellStorage));Assert.That(first.Location.SlotIndex,Is.EqualTo(11));
            // 共用同一真实储存位，先腾出第12格，再从仓库界面的携带区拖入另一法术。
            Assert.That(Session.Profile.Items.TryMove("fire",ItemLocation.Warehouse).Success,Is.True);
            Backpack.Close();EnterWarehouse();Warehouse.OpenWarehouse();yield return null;yield return null;
            DropLast(Warehouse,"ice");yield return null;
            Session.ReadOnly.TryGetView("ice",out var second);Assert.That(second.Location.Kind,Is.EqualTo(ItemLocationKind.SpellStorage));Assert.That(second.Location.SlotIndex,Is.EqualTo(11));
        }
        private void DropLast(SinglePlayerInventoryController ui,string id)
        {
            var storage=ui.GetComponentInChildren<SpellStorageView>();var content=Ref<RectTransform>(storage,"_content");
            content.GetComponentInParent<ScrollRect>().horizontalNormalizedPosition=1;Canvas.ForceUpdateCanvases();
            Assert.That(Session.ReadOnly.TryGetView(id,out var item),Is.True);ui.BeginDrag(item,Vector2.zero,null,false);
            var target=content.GetChild(11).GetComponent<InventoryDropTarget>();target.OnDrop(new PointerEventData(EventSystem.current));
        }
        private static void CheckTray(SinglePlayerInventoryController ui)
        {
            Canvas.ForceUpdateCanvases();var view=ui.GetComponentInChildren<SpellStorageView>();Assert.That(view,Is.Not.Null);
            var content=Ref<RectTransform>(view,"_content");var clip=(RectTransform)content.parent;var scroll=content.GetComponentInParent<ScrollRect>();
            Assert.That(clip.name,Is.EqualTo("SpellStorageRuntime_InnerClip"));Assert.That(scroll.viewport,Is.SameAs(clip));
            var outer=(RectTransform)clip.parent;
            Assert.That(clip.offsetMin.x,Is.GreaterThanOrEqualTo(8));Assert.That(clip.offsetMax.x,Is.LessThanOrEqualTo(-8));
            var slot=(RectTransform)content.GetChild(0);float height=slot.rect.height;
            Assert.That(slot.rect.width,Is.EqualTo(height).Within(.01f));
            float top=-content.anchoredPosition.y,bottom=clip.rect.height-top-height;
            Assert.That(top,Is.GreaterThanOrEqualTo(-.01f));Assert.That(top,Is.EqualTo(bottom).Within(.1f));
            Assert.That(clip.rect.height,Is.LessThan(outer.rect.height));Assert.That(view.VisibleSlotCount,Is.EqualTo(12));
            scroll.horizontalNormalizedPosition=1;Canvas.ForceUpdateCanvases();
            var target=content.GetChild(11).GetComponent<InventoryDropTarget>();Assert.That(target,Is.Not.Null);
            var rect=(RectTransform)target.transform;var corners=new Vector3[4];rect.GetWorldCorners(corners);
            Assert.That(RectTransformUtility.RectangleContainsScreenPoint(clip,RectTransformUtility.WorldToScreenPoint(null,(corners[0]+corners[2])*.5f)),Is.True,"最后第12格应可滚入内框");
        }
    }
}
#endif
