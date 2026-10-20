#if UNITY_EDITOR
using System.Collections;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace Game.Run.Tests
{
    /// <summary>用真实库存与作者 Review 检查视觉契约；缩减可见列不能缩减真实资产容量。</summary>
    public sealed class InventorySquareStylePlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string EvidencePath => "TestResults/SinglePlayer/M6-T15-SquareStyle";
        private static T Ref<T>(Object target,string field) where T:Object
        { var value=new SerializedObject(target).FindProperty(field); return value==null?null:(T)value.objectReferenceValue; }

        [UnityTest] public IEnumerator SpellCardsShowOnlyLargeBottomCenterFiniteCountAndZeroIsBlank()
        {
            Backpack.OpenBackpack();yield return null;
            Assert.That(Session.Profile.Items.TryMove("fire",new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,0)).Success,Is.True);
            Assert.That(Session.Profile.Items.TryMove("ice",new ItemLocation(ItemLocationKind.SpellStorage,Session.Profile.ActorId,0,0,0,0)).Success,Is.True);
            Session.Profile.Items.TryGet("fire",out var finite);
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("unlimited",finite.ContentId,0,true),
                new ItemLocation(ItemLocationKind.SpellStorage,Session.Profile.ActorId,0,0,0,1)),Is.True);
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("empty-spell",finite.ContentId,0),
                new ItemLocation(ItemLocationKind.SpellStorage,Session.Profile.ActorId,0,0,0,2)),Is.True);
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("sixteen-spell",finite.ContentId,16),
                new ItemLocation(ItemLocationKind.SpellStorage,Session.Profile.ActorId,0,0,0,3)),Is.True);
            Backpack.RefreshPresentation();yield return null;Canvas.ForceUpdateCanvases();
            int seen=0;
            foreach(var card in Backpack.GetComponentsInChildren<InventoryItemView>())
            {
                if(card.Snapshot.Kind!=ItemKind.Spell || string.IsNullOrEmpty(card.Snapshot.InstanceId))continue;
                seen++;var count=Ref<Text>(card,"_spellCount");Assert.That(count,Is.Not.Null,"次数必须有可独立编辑的Text");
                string expected=card.Snapshot.HasUnlimitedCharges || card.Snapshot.RemainingCharges<=0?"":card.Snapshot.RemainingCharges.ToString();
                Assert.That(count.text,Is.EqualTo(expected));Assert.That(Ref<Text>(card,"_label").text,Is.Empty);
                Assert.That(count.alignment,Is.EqualTo(TextAnchor.LowerCenter));Assert.That(count.fontSize,Is.GreaterThanOrEqualTo(24));
                Assert.That(count.rectTransform.anchorMax.y,Is.LessThanOrEqualTo(.55f));
                Assert.That(count.resizeTextForBestFit,Is.False,"不能将次数缩成不可读小字");
                if(expected.Length>0)
                {Assert.That(count.cachedTextGenerator.vertexCount,Is.GreaterThanOrEqualTo(expected.Length*4));Assert.That(count.cachedTextGenerator.characterCountVisible,Is.EqualTo(expected.Length));}
            }
            Assert.That(seen,Is.GreaterThanOrEqualTo(5));
            yield return new WaitForEndOfFrame();Capture("noita-spell-counts.png");
        }
        [UnityTest] public IEnumerator SpellStorageRootsStaySquareAndKeepAllTwelveActualSlots()
        {
            Backpack.OpenBackpack();yield return null;CheckStorage(Backpack);
            Backpack.Close();EnterWarehouse();Warehouse.OpenWarehouse();yield return null;CheckStorage(Warehouse);
            var storage=Warehouse.GetComponentInChildren<SpellStorageView>();var content=Ref<RectTransform>(storage,"_content");
            var viewport=(RectTransform)content.parent;viewport.anchorMin=viewport.anchorMax=viewport.pivot=new Vector2(0,1);viewport.sizeDelta=new Vector2(215,57);
            Warehouse.RefreshPresentation();yield return null;CheckStorage(Warehouse);
            Assert.That(content.rect.width,Is.GreaterThan(viewport.rect.width),"窄区域滚动展示，不能截掉真实槽");
            yield return new WaitForEndOfFrame();Capture("warehouse-square-storage.png");
        }
        private static void CheckStorage(SinglePlayerInventoryController controller)
        {
            var view=controller.GetComponentInChildren<SpellStorageView>();Assert.That(view.VisibleSlotCount,Is.EqualTo(12));
            var content=Ref<RectTransform>(view,"_content");int count=0;
            foreach(Transform child in content)if(child.GetComponent<InventorySlotView>()!=null)
            {var rect=(RectTransform)child;count++;Assert.That(rect.rect.width,Is.EqualTo(rect.rect.height).Within(.01f),child.name);}
            Assert.That(count,Is.EqualTo(12));
        }
        [UnityTest] public IEnumerator RemainingBackpackToolRootsAreSquareAfterWarehouseToolRowRemoved()
        {
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            var tools=Backpack.GetComponentsInChildren<ToolbeltSlotView>();Assert.That(tools.Length,Is.EqualTo(7));
            foreach(var tool in tools)
            {
                var rect=(RectTransform)tool.transform;Assert.That(rect.rect.width,Is.EqualTo(rect.rect.height).Within(.01f));
            }
            Backpack.Close();EnterWarehouse();Warehouse.OpenWarehouse();yield return null;
            Assert.That(Warehouse.GetComponentsInChildren<ToolbeltSlotView>(true),Is.Empty,"保留作者删除仓库工具行的布局");
            yield return new WaitForEndOfFrame();Capture("workspace-tool-squares.png");
        }
        [UnityTest] public IEnumerator SelectedWandFrameUsesAuthorsEliteSprite()
        {
            EnterWarehouse();Warehouse.OpenWarehouse();yield return null;Warehouse.InspectWandSlot(1);yield return null;
            Sprite expected=null;foreach(var asset in AssetDatabase.LoadAllAssetsAtPath("Assets/_Project/Art/UI/Function_UI/icon_frame_square_elite.png"))
                if(asset is Sprite sprite){expected=sprite;break;}
            Assert.That(expected,Is.Not.Null);
            var wands=Warehouse.GetComponentInChildren<WandInventoryView>();var root=Ref<RectTransform>(wands,"_equipmentRoot");
            for(int i=0;i<3;i++)
            {var frame=Ref<Image>(root.GetChild(i).GetComponent<InventorySlotView>(),"_selection");Assert.That(frame.enabled,Is.EqualTo(i==1));if(i==1)Assert.That(frame.sprite,Is.SameAs(expected));}
            yield return new WaitForEndOfFrame();Capture("elite-wand-selection.png");
        }
        [UnityTest] public IEnumerator WarehouseAndShopSpellCountsUseTheSamePresentation()
        {
            Session.Profile.Items.TryGet("fire",out var fire);Session.Profile.Items.TryGet("ice",out var ice);
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("warehouse-sixteen",fire.ContentId,16),ItemLocation.Warehouse),Is.True);
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("warehouse-unlimited",ice.ContentId,0,true),ItemLocation.Warehouse),Is.True);
            EnterWarehouse();Warehouse.OpenWarehouse();yield return null;Canvas.ForceUpdateCanvases();
            int seen=0;var grid=Warehouse.GetComponentInChildren<WarehouseCatalogGridView>();
            foreach(var card in grid.GetComponentsInChildren<InventoryItemView>())
            {
                if(card.Snapshot.Kind!=ItemKind.Spell || string.IsNullOrEmpty(card.Snapshot.InstanceId))continue;
                seen++;Assert.That(Ref<Text>(card,"_spellCount").text,Is.EqualTo(card.Snapshot.HasUnlimitedCharges?"":"16"));
                Assert.That(Ref<Text>(card,"_label").text,Is.Empty);
            }
            Assert.That(seen,Is.EqualTo(2));yield return new WaitForEndOfFrame();Capture("warehouse-only-spell-counts.png");
            Warehouse.Close();Bind(0,"b0");
            var area=Ref<HubAreaVolume>(Find<HubInteractionController>(),"_shopArea");Player.transform.position=area.Center;Physics.SyncTransforms();
            Keys(Key.F);yield return null;Keys();yield return null;
            var shop=Find<ShopController>();Assert.That(shop.IsOpen,Is.True);
            var host=Ref<GameObject>(shop,"_panel").GetComponentInParent<SinglePlayerInventoryController>();
            // 当前作者商店没有道具行，不能为了测试恢复他删掉的GameObject；验证实际存在的内嵌背包法术。
            Assert.That(host.IsOpen,Is.True);int spellCards=0;
            foreach(var card in host.GetComponentsInChildren<InventoryItemView>())
            {
                if(card.Snapshot.Kind!=ItemKind.Spell || string.IsNullOrEmpty(card.Snapshot.InstanceId))continue;
                spellCards++;Assert.That(Ref<Text>(card,"_label").text,Is.Empty);
                Assert.That(Ref<Text>(card,"_spellCount").text,Is.EqualTo(card.Snapshot.HasUnlimitedCharges?"":card.Snapshot.RemainingCharges.ToString()));
            }
            Assert.That(spellCards,Is.GreaterThanOrEqualTo(4));
            yield return new WaitForEndOfFrame();Capture("shop-only-spell-counts.png");
        }
        private void EnterWarehouse()
        {
            var area=Ref<HubAreaVolume>(Find<HubInteractionController>(),"_warehouseArea");
            Player.CharacterController.enabled=false;Player.enabled=false;Player.transform.position=area.Center;Physics.SyncTransforms();
        }
    }
}
#endif
