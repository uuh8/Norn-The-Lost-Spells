#if UNITY_EDITOR
using System.Collections;
using Game.Core;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>真实资产和作者 Review：验证比例、图层布局、报价资源及单一 F 提示路径。</summary>
    public sealed class ContainerIconFillPlayModeTests : BackpackWorkspacePlayModeTests
    {
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T17-IconFill";
        private static T Ref<T>(Object target,string name)where T:Object=>(T)new SerializedObject(target).FindProperty(name).objectReferenceValue;
        private static Image Fill(Image source)=>source.transform.Find(ContainerIconFill.ChildName).GetComponent<Image>();
        [UnityTest]public IEnumerator ContainersShowBottomFillInBackpackCatalogAndToolsWithoutCapacityText()
        {
            Session.Profile.Items.TryGet("b0",out var bottle);bottle.Composition.AddReserved(new[]{new MaterialAmount(1,7400)},1);
            Session.Profile.Items.TryGet("b1",out var full);full.Composition.AddReserved(new[]{new MaterialAmount(1,14900)},1);
            Backpack.OpenBackpack();yield return null;
            int seen=0;
            foreach(var view in Backpack.GetComponentsInChildren<InventoryItemView>())
            {
                if(view.Snapshot.Kind!=ItemKind.Container || string.IsNullOrEmpty(view.Snapshot.InstanceId))continue;
                var icon=Ref<Image>(view,"_icon");var fill=Fill(icon);seen++;
                Assert.That(fill.sprite,Is.SameAs(icon.sprite));Assert.That(fill.fillAmount,Is.EqualTo(view.Snapshot.InstanceId=="b0"?.5f:1f).Within(.0001f));
                Assert.That(fill.fillMethod,Is.EqualTo(Image.FillMethod.Vertical));Assert.That(fill.fillOrigin,Is.EqualTo((int)Image.OriginVertical.Bottom));
                Assert.That(fill.raycastTarget,Is.False);Assert.That(Ref<Text>(view,"_label").text,Is.Empty);
                Assert.That(fill.rectTransform.anchorMin,Is.EqualTo(Vector2.zero));Assert.That(fill.rectTransform.anchorMax,Is.EqualTo(Vector2.one));
                // 父 Icon 在编辑器的位置不因容量改变；旋转/缩放仍继承作者的原有层级。
                var position=icon.rectTransform.anchoredPosition;var size=icon.rectTransform.sizeDelta;
                ContainerIconFill.Set(fill,icon,true,0,15000);Assert.That(fill.enabled,Is.False);
                ContainerIconFill.Set(fill,icon,true,7500,15000);Assert.That(icon.rectTransform.anchoredPosition,Is.EqualTo(position));Assert.That(icon.rectTransform.sizeDelta,Is.EqualTo(size));
            }
            Assert.That(seen,Is.EqualTo(2));Backpack.RefreshPresentation();yield return null;
            yield return new WaitForEndOfFrame();Capture("container-half-full-backpack.png");
            Bind(0,"b0");Backpack.RefreshPresentation();yield return null;
            foreach(var tool in Backpack.GetComponentsInChildren<ToolbeltSlotView>())if(tool.ToolSlot==0)
            {Assert.That(Ref<Text>(tool,"_label").text,Is.EqualTo("4"));Assert.That(Fill(Ref<Image>(tool,"_icon")).fillAmount,Is.EqualTo(.5f));}
            Backpack.Close();yield return null;
            var hud=Find<HeldItemHud>();var cell=new SerializedObject(hud).FindProperty("_cells").GetArrayElementAtIndex(3);
            var heldIcon=(Image)cell.FindPropertyRelative("Icon").objectReferenceValue;
            Assert.That(Fill(heldIcon).fillAmount,Is.EqualTo(.5f));Assert.That(((Text)cell.FindPropertyRelative("Value").objectReferenceValue).text,Is.Empty);
            Assert.That(((Text)cell.FindPropertyRelative("Title").objectReferenceValue).text,Is.EqualTo("4"));
            Session.ReadOnly.TryGetView("b1",out var snapshot);
            var card=Object.Instantiate(Backpack.SlotPrefabs.WarehouseItem,Warehouse.transform);card.PrepareCatalog();
            card.BindCatalog(Warehouse,snapshot,112,"采集瓶\n15000/15000");
            Assert.That(Ref<Text>(card,"_label").text,Is.EqualTo("采集瓶"));Assert.That(Fill(Ref<Image>(card,"_icon")).fillAmount,Is.EqualTo(1));Object.Destroy(card.gameObject);
        }
        [UnityTest]public IEnumerator AllElementOffersAndIceSpellResolveRealImportedSprite()
        {
            // 必须先进入商店区域；权威交易权限不能为了只读图标测试绕过。
            var ui=Find<ShopController>();var shop=Ref<SinglePlayerShopSession>(ui,"_shop");int materials=0;
            var hub=Find<HubInteractionController>();var area=Ref<HubAreaVolume>(hub,"_shopArea");
            Player.CharacterController.enabled=false;Player.transform.position=area.Center;Player.enabled=false;Physics.SyncTransforms();
            Keys(Key.F);yield return null;Keys();yield return null;Assert.That(ui.IsOpen,Is.True);
            Assert.That(shop.ReadOnly,Is.Not.Null,shop.Message);
            for(int i=0;i<shop.ReadOnly.Count;i++)
            {var offer=shop.ReadOnly.GetOffer(i);if(offer.IsMaterial){materials++;Assert.That(offer.Icon,Is.Not.Null,offer.DisplayName);Assert.That(AssetDatabase.GetAssetPath(offer.Icon),Does.Contain("Funtion_UI2"));}}
            Assert.That(materials,Is.EqualTo(8));
            var spell=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/Spell_Emit_Iceball.asset");Assert.That(spell.Icon,Is.Not.Null);
            Session.ReadOnly.TryGetView("ice",out var ice);Assert.That(ice.Icon,Is.SameAs(spell.Icon));
            ui.Close();Backpack.OpenBackpack();yield return null;bool found=false;
            foreach(var view in Backpack.GetComponentsInChildren<InventoryItemView>())if(view.Snapshot.InstanceId=="ice")
            {found=true;Assert.That(Ref<Image>(view,"_icon").sprite,Is.SameAs(spell.Icon));Assert.That(Ref<Image>(view,"_icon").enabled,Is.True);}
            Assert.That(found,Is.True);Backpack.Close();
        }
        [UnityTest]public IEnumerator UnifiedFPromptPreservesAuthorRectAndUsesExistingEntryForShop()
        {
            var view=Find<NearbyPickupListView>();var data=new SerializedObject(view);var row=(GameObject)data.FindProperty("_rows").GetArrayElementAtIndex(0).objectReferenceValue;
            var label=(Text)data.FindProperty("_labels").GetArrayElementAtIndex(0).objectReferenceValue;
            view.Panel.anchorMin=view.Panel.anchorMax=new Vector2(.61f,.42f);view.Panel.anchoredPosition=new Vector2(37,19);
            label.rectTransform.anchoredPosition=new Vector2(62,11);var rect=label.rectTransform.sizeDelta;
            var hub=Find<HubInteractionController>();var area=Ref<HubAreaVolume>(hub,"_shopArea");
            Player.CharacterController.enabled=false;Player.transform.position=area.Center;Player.enabled=false;Physics.SyncTransforms();yield return null;yield return null;
            Assert.That(row.activeSelf,Is.True);Assert.That(label.text,Is.EqualTo("F 商店"));Assert.That(label.alignByGeometry,Is.True);
            Assert.That(view.Panel.anchorMin,Is.EqualTo(new Vector2(.61f,.42f)));Assert.That(view.Panel.anchoredPosition,Is.EqualTo(new Vector2(37,19)));
            Assert.That(label.rectTransform.anchoredPosition,Is.EqualTo(new Vector2(62,11)));Assert.That(label.rectTransform.sizeDelta,Is.EqualTo(rect));
            foreach(Transform child in view.Panel)Assert.That(child.name,Does.Not.StartWith("Station entry"));
            Keys(Key.E);yield return null;Keys();yield return null;Assert.That(Find<ShopController>().IsOpen,Is.False);
            Keys(Key.F);yield return null;Keys();yield return null;Assert.That(Find<ShopController>().IsOpen,Is.True);Assert.That(Backpack.IsOpen,Is.False);
            yield return new WaitForEndOfFrame();Capture("elements-and-ice-shop.png");
        }
    }
}
#endif
