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
    /// <summary>真实三格法杖经过同一拖拽仲裁；目录仍是一格，装备图与参数分区避免相互遮挡。</summary>
    public sealed class WandPresentationPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T12-WandTerminalMenu";
        private static T Ref<T>(Object target,string field) where T:Object => (T)new SerializedObject(target).FindProperty(field).objectReferenceValue;
        [UnityTest] public IEnumerator WholeWandRegionIncludesBackgroundEmptySlotAndOccupiedSpell()
        {
            Backpack.OpenBackpack();yield return null;
            var panels=Backpack.GetComponentsInChildren<WandProgramPanelView>();
            foreach(var panel in panels)
            {
                var program=Ref<RectTransform>(panel,"_program");
                int slot=new SerializedObject(panel).FindProperty("_wandSlot").intValue;
                Assert.That(Session.Profile.Items.TryMove("fire",new ItemLocation(ItemLocationKind.WandSlot,"w"+slot,0,0,0,0)).Success,Is.True);
                Backpack.RefreshPresentation();
                var targets=new[] { panel.gameObject, program.GetChild(3).gameObject, program.GetChild(0).GetComponentInChildren<InventoryItemView>().gameObject };
                foreach(var target in targets)
                {
                    Session.ReadOnly.TryGetView("spare",out var source);
                    Backpack.BeginDrag(source,Vector2.zero,null,true);
                    ExecuteEvents.ExecuteHierarchy(target,new PointerEventData(EventSystem.current),ExecuteEvents.dropHandler);
                    Session.ReadOnly.TryGetView("spare",out var equipped);
                    Assert.That(equipped.Location.Kind,Is.EqualTo(ItemLocationKind.EquippedWand),target.name);
                    Assert.That(equipped.Location.SlotIndex,Is.EqualTo(slot));
                    // 用原子装备交换把备用杖恢复到包里，下一种命中目标独立验证。
                    string original="w"+slot;
                    Assert.That(LoadoutOps.TryEquipWand(Session.Profile.Items,original,Session.Profile.ActorId,slot,Session.Profile.Items.Version).Success,Is.True);
                    Backpack.RefreshPresentation();
                }
            }
        }
        [UnityTest] public IEnumerator EquippedIconDragsAndSwapsWithAnotherEntireRegion()
        {
            Backpack.OpenBackpack();yield return null;
            Assert.That(Session.Profile.Items.TryMove("fire",new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,0)).Success,Is.True);
            Assert.That(Session.Profile.Items.TryMove("ice",new ItemLocation(ItemLocationKind.WandSlot,"w1",0,0,0,0)).Success,Is.True);
            Backpack.RefreshPresentation();
            var panels=Backpack.GetComponentsInChildren<WandProgramPanelView>();
            Session.WandsReadOnly.TryGetState("w0",out var beforeA);Session.WandsReadOnly.TryGetState("w1",out var beforeB);
            WandProgramPanelView first=null,second=null;
            foreach(var panel in panels)
            {int slot=new SerializedObject(panel).FindProperty("_wandSlot").intValue;if(slot==0)first=panel;if(slot==1)second=panel;}
            var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left};
            Assert.That(ExecuteEvents.ExecuteHierarchy(Ref<Image>(first,"_icon").gameObject,data,ExecuteEvents.beginDragHandler),Is.Not.Null,"装备图必须能开始拖动");
            ExecuteEvents.ExecuteHierarchy(second.gameObject,data,ExecuteEvents.dropHandler);
            Session.ReadOnly.TryGetView("w0",out var a);Session.ReadOnly.TryGetView("w1",out var b);
            Assert.That(a.Location.SlotIndex,Is.EqualTo(1));Assert.That(b.Location.SlotIndex,Is.EqualTo(0));
            Assert.That(a.Location.Kind,Is.EqualTo(ItemLocationKind.EquippedWand));Assert.That(b.Location.Kind,Is.EqualTo(ItemLocationKind.EquippedWand));
            Session.ReadOnly.TryGetView("fire",out var fire);Session.ReadOnly.TryGetView("ice",out var ice);
            Assert.That(fire.Location.OwnerId,Is.EqualTo("w0"));Assert.That(ice.Location.OwnerId,Is.EqualTo("w1"));
            Session.WandsReadOnly.TryGetState("w0",out var afterA);Session.WandsReadOnly.TryGetState("w1",out var afterB);
            Assert.That(afterA.CurrentMana,Is.EqualTo(beforeA.CurrentMana));Assert.That(afterB.CurrentMana,Is.EqualTo(beforeB.CurrentMana));
            yield return new WaitForEndOfFrame();Capture("equipped-wand-swap.png");
        }
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile,ItemCatalogIndex index)
        {
            profile.Items.TryGet("w0",out var wand);
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("spare",wand.ContentId,0),new GridPlacement(1,1,0).InBackpack("bag")),Is.True);
            return profile;
        }
        [UnityTest] public IEnumerator AuthoredEquippedIconsAndThreeCellBackpackRotateWithoutChangingAsset()
        {
            var panels=Backpack.GetComponentsInChildren<WandProgramPanelView>(true);
            var sizes=new Vector2[panels.Length];for(int i=0;i<panels.Length;i++)sizes[i]=Ref<Image>(panels[i],"_icon").rectTransform.sizeDelta;
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            for(int i=0;i<panels.Length;i++)
            {
                var panel=panels[i];
                var icon=Ref<Image>(panel,"_icon");var header=Ref<Text>(panel,"_header");
                Assert.That(icon.rectTransform.sizeDelta,Is.EqualTo(sizes[i]),"图标尺寸由作者设置，不固定为200");Assert.That(icon.preserveAspect,Is.True);
                Assert.That(icon.sprite.rect.width/icon.sprite.rect.height,Is.GreaterThan(2));
                Assert.That(header.text,Does.Contain("Mana"));
            }
            Session.ReadOnly.TryGetView("spare",out var spare);
            Assert.That(spare.Rules.Shape.CellCount,Is.EqualTo(3));
            yield return new WaitForEndOfFrame();Capture("backpack-wide-wands.png");
            Backpack.BeginDrag(spare,Vector2.zero,null,true);Backpack.RotateDrag();Backpack.SubmitToBackpack(5,1,"bag");yield return null;
            Assert.That(Session.ReadOnly.TryGetView("spare",out var rotated),Is.True);
            Assert.That(rotated.Location,Is.EqualTo(new GridPlacement(5,1,1).InBackpack("bag")));
            Assert.That(rotated.Rules.Shape.GetWidth(rotated.Location.QuarterTurns),Is.EqualTo(1));
            Assert.That(rotated.Rules.Shape.GetHeight(rotated.Location.QuarterTurns),Is.EqualTo(3));
            yield return new WaitForEndOfFrame();Capture("backpack-rotated-wand.png");
        }
        [UnityTest] public IEnumerator WarehouseKeepsSquareCategoryButDragUsesFullThreeCellShape()
        {
            Assert.That(Session.Profile.Items.TryMove("spare",ItemLocation.Warehouse).Success,Is.True);
            MoveToStation("_warehouseArea");
            Warehouse.OpenWarehouse();yield return null;Canvas.ForceUpdateCanvases();
            InventoryItemView tile=null;
            foreach(var view in Warehouse.GetComponentsInChildren<InventoryItemView>())if(view.Snapshot.InstanceId=="spare")tile=view;
            Assert.That(tile,Is.Not.Null);Assert.That(tile.Rect.rect.width,Is.EqualTo(tile.Rect.rect.height));
            Warehouse.BeginDrag(tile.Snapshot,Vector2.zero,null,true);
            var ghost=Ref<InventoryItemView>(Warehouse,"_ghost");Assert.That(ghost.Rect.rect.width/ghost.Rect.rect.height,Is.EqualTo(3).Within(.01f));
            Warehouse.CancelDrag();yield return new WaitForEndOfFrame();Capture("warehouse-wide-wand.png");
        }
        [UnityTest] public IEnumerator ShopUsesWideWandImageAboveItsPriceText()
        {
            MoveToStation("_shopArea");
            var shop=Find<ShopController>();shop.Open();yield return null;Canvas.ForceUpdateCanvases();
            var offers=Ref<RectTransform>(shop,"_offersRoot");int wands=0;
            foreach(var view in offers.GetComponentsInChildren<ShopEntryView>())
            {
                var icon=view.transform.Find("OfferIcon")?.GetComponent<Image>();if(icon==null || icon.sprite==null || icon.sprite.rect.width/icon.sprite.rect.height<=2)continue;
                wands++;Assert.That(icon.rectTransform.anchorMin.y,Is.GreaterThan(.5f));
            }
            Assert.That(wands,Is.EqualTo(3));yield return new WaitForEndOfFrame();Capture("shop-wide-wands.png");
        }
        private void MoveToStation(string field)
        {
            var area=Ref<HubAreaVolume>(Find<HubInteractionController>(),field);
            Player.CharacterController.enabled=false;Player.transform.position=area.Center;Player.enabled=false;Physics.SyncTransforms();
        }
    }
}
#endif
