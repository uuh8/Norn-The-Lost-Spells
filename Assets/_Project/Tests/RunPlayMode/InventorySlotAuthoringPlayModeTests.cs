#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>从作者局部配置出发验证，不以旧硬编码位置作为正确答案。</summary>
    public sealed class InventorySlotAuthoringPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T14-SlotAuthoring";
        private static T Ref<T>(Object target,string field) where T:Object => (T)new SerializedObject(target).FindProperty(field).objectReferenceValue;

        [UnityTest] public IEnumerator RefreshAndSwapPreserveAuthoredWandChildTransforms()
        {
            var panel=Backpack.GetComponentInChildren<WandProgramPanelView>(true);
            var header=Ref<Text>(panel,"_header").rectTransform;
            var icon=Ref<Image>(panel,"_icon").rectTransform;
            var frame=(RectTransform)icon.parent;
            header.offsetMin=new Vector2(37,11);header.offsetMax=new Vector2(-29,-17);
            icon.anchorMin=new Vector2(.13f,.2f);icon.anchorMax=new Vector2(.72f,.8f);
            icon.pivot=new Vector2(.3f,.7f);icon.anchoredPosition=new Vector2(23,-19);icon.sizeDelta=new Vector2(123,45);
            var headerMin=header.offsetMin;var headerMax=header.offsetMax;var framePos=frame.anchoredPosition;
            Backpack.OpenBackpack();yield return null;
            AssertPose(icon,header,headerMin,headerMax,frame,framePos);
            Assert.That(LoadoutOps.TryEquipWand(Session.Profile.Items,"w0",Session.Profile.ActorId,1,Session.Profile.Items.Version).Success,Is.True);
            for(int i=0;i<3;i++)Backpack.RefreshPresentation();yield return null;
            AssertPose(icon,header,headerMin,headerMax,frame,framePos);
            yield return new WaitForEndOfFrame();Capture("authored-wand-layout.png");
        }
        private static void AssertPose(RectTransform icon,RectTransform header,Vector2 min,Vector2 max,RectTransform frame,Vector2 pos)
        {
            Assert.That(icon.anchorMin,Is.EqualTo(new Vector2(.13f,.2f)),"Runtime不能重写作者Icon anchors");
            Assert.That(icon.anchorMax,Is.EqualTo(new Vector2(.72f,.8f)));
            Assert.That(icon.pivot,Is.EqualTo(new Vector2(.3f,.7f)));
            Assert.That(icon.anchoredPosition,Is.EqualTo(new Vector2(23,-19)));
            Assert.That(icon.sizeDelta,Is.EqualTo(new Vector2(123,45)));
            Assert.That(header.offsetMin,Is.EqualTo(min));Assert.That(header.offsetMax,Is.EqualTo(max));
            Assert.That(frame.anchoredPosition,Is.EqualTo(pos));
        }
        [UnityTest] public IEnumerator EverySlotFamilyHasAnIndependentEditablePrefab()
        {
            foreach(var name in new[]{"BackpackCell","NearbyCell","WarehouseCell","BackpackItem","NearbyItem","WarehouseItem",
                "WandProgramSlot","SpellStorageSlot","WandSelectorSlot","BackpackToolSlot","WorkspaceToolSlot"})
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/UI/InventorySlots/"+name+".prefab"),Is.Not.Null,name);
            Backpack.OpenBackpack();yield return null;
        }
        [UnityTest] public IEnumerator WarehouseSelectorUsesImportedSelectedFrameSprite()
        {
            var asset=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/UI/InventorySlots/WandSelectorSlot.prefab");
            var source=asset.GetComponent<InventorySlotView>();Assert.That(Ref<Sprite>(source,"_selectedSprite"),Is.Not.Null,"Multiple模式Sprite必须读取子资产");
            var area=Ref<HubAreaVolume>(Find<HubInteractionController>(),"_warehouseArea");
            Player.CharacterController.enabled=false;Player.enabled=false;Player.transform.position=area.Center;Physics.SyncTransforms();
            Warehouse.OpenWarehouse();yield return null;Warehouse.InspectWandSlot(1);yield return null;
            var wands=Warehouse.GetComponentInChildren<WandInventoryView>();var equipment=Ref<RectTransform>(wands,"_equipmentRoot");
            for(int i=0;i<3;i++)
            {
                var slot=equipment.GetChild(i).GetComponent<InventorySlotView>();var frame=Ref<Image>(slot,"_selection");
                Assert.That(frame.enabled,Is.EqualTo(i==1));if(i==1)Assert.That(frame.sprite,Is.Not.Null);
            }
        }
        [UnityTest] public IEnumerator FixedSlotsKeepIndependentBackgroundAndChildStylesAcrossRefresh()
        {
            Backpack.OpenBackpack();yield return null;
            Assert.That(Session.Profile.Items.TryMove("fire",new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,0)).Success,Is.True);
            Assert.That(Session.Profile.Items.TryMove("ice",new ItemLocation(ItemLocationKind.SpellStorage,Session.Profile.ActorId,0,0,0,0)).Success,Is.True);
            Backpack.RefreshPresentation();
            var storage=Backpack.GetComponentInChildren<SpellStorageView>();
            var content=Ref<RectTransform>(storage,"_content");
            var slot=content.GetComponentInChildren<InventorySlotView>();Assert.That(slot,Is.Not.Null);
            var image=slot.GetComponent<Image>();image.color=new Color(.64f,.23f,.47f,.88f);
            var card=slot.Card;Assert.That(card.AuthoredLayout,Is.True);
            var icon=Ref<Image>(card,"_icon");var label=Ref<Text>(card,"_spellCount");
            Assert.That(label.font,Is.SameAs(Backpack.UiFont),"次数使用Controller的字体回退");Assert.That(label.text,Is.EqualTo("8"));
            label.transform.SetAsFirstSibling();int captionOrder=label.transform.GetSiblingIndex();
            icon.rectTransform.offsetMin=new Vector2(9,14);icon.rectTransform.offsetMax=new Vector2(-17,-21);
            icon.preserveAspect=false;
            label.color=Color.cyan;label.alignment=TextAnchor.UpperRight;label.resizeTextForBestFit=false;label.fontSize=23;
            for(int i=0;i<3;i++)Backpack.RefreshPresentation();yield return null;
            Assert.That(image.color,Is.EqualTo(new Color(.64f,.23f,.47f,.88f)));
            Assert.That(icon.rectTransform.offsetMin,Is.EqualTo(new Vector2(9,14)));
            Assert.That(icon.rectTransform.offsetMax,Is.EqualTo(new Vector2(-17,-21)));
            Assert.That(icon.preserveAspect,Is.False);
            Assert.That(label.color,Is.EqualTo(Color.cyan));Assert.That(label.fontSize,Is.EqualTo(23));
            Assert.That(label.alignment,Is.EqualTo(TextAnchor.UpperRight));Assert.That(label.resizeTextForBestFit,Is.False);
            Assert.That(label.transform.GetSiblingIndex(),Is.EqualTo(captionOrder),"作者层叠顺序也必须保留");
            var program=Ref<RectTransform>(Backpack.GetComponentInChildren<WandProgramPanelView>(),"_program");
            Assert.That(program.GetComponentInChildren<InventorySlotView>().Card.AuthoredLayout,Is.True);
            Assert.That(program.GetComponentInChildren<InventorySlotView>().GetComponent<Image>().color,Is.Not.EqualTo(image.color));
        }
        [UnityTest] public IEnumerator ProgramAndStorageCountsBuildVisibleDigitGeometry()
        {
            Backpack.OpenBackpack();yield return null;
            Assert.That(Session.Profile.Items.TryMove("fire",new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,0)).Success,Is.True);
            Assert.That(Session.Profile.Items.TryMove("ice",new ItemLocation(ItemLocationKind.SpellStorage,Session.Profile.ActorId,0,0,0,0)).Success,Is.True);
            Backpack.RefreshPresentation();yield return null;Canvas.ForceUpdateCanvases();
            var report=new System.Text.StringBuilder();
            foreach(var card in Backpack.GetComponentsInChildren<InventorySlotView>())if(card.Card!=null && card.Card.gameObject.activeSelf)
            {
                var text=Ref<Text>(card.Card,"_spellCount");
                report.AppendLine(card.name+" caption="+text.text+" font="+text.font.name+" rect="+text.rectTransform.rect+" vertices="+text.cachedTextGenerator.vertexCount);
                Assert.That(text.cachedTextGenerator.vertexCount,Is.GreaterThanOrEqualTo(4),card.name+"至少一个数字quad");
                Assert.That(text.cachedTextGenerator.characterCountVisible,Is.GreaterThanOrEqualTo(1));
            }
            System.IO.File.WriteAllText(EvidencePath+"/caption-geometry.txt",report.ToString());
            yield return new WaitForEndOfFrame();Capture("program-captions.png");
        }
        [UnityTest] public IEnumerator CellPreviewRestoresPrefabColorAndToolSlotsKeepSevenDistinctIndices()
        {
            Backpack.OpenBackpack();yield return null;
            var grid=Backpack.GetComponentInChildren<InventoryGridView>();
            Image cell=null;foreach(Transform child in grid.transform)if(child.name=="Grid cell"){cell=child.GetComponent<Image>();break;}
            Assert.That(cell,Is.Not.Null);var original=cell.color;
            Session.ReadOnly.TryGetView("b0",out var bottle);grid.ShowPreview(bottle.Rules.Shape,0,0,0,true);grid.ClearPreview();
            Assert.That(cell.color,Is.EqualTo(original));
            var slots=Backpack.GetComponentsInChildren<ToolbeltSlotView>();Assert.That(slots.Length,Is.EqualTo(7));
            var seen=new bool[7];foreach(var tool in slots){Assert.That(seen[tool.ToolSlot],Is.False);seen[tool.ToolSlot]=true;}
            foreach(var used in seen)Assert.That(used,Is.True);
            Bind(0,"b0");Backpack.RefreshPresentation();yield return null;
            foreach(var tool in Backpack.GetComponentsInChildren<ToolbeltSlotView>())if(tool.ToolSlot==0)
                Assert.That(Ref<Text>(tool,"_label").text,Is.EqualTo("4"));
        }
    }
}
#endif
