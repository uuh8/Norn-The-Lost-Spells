#if UNITY_EDITOR
using System.Collections;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>最新作者Review验证普通Items物理投掷、三处右键、图片与只读资源格式。</summary>
    public sealed class ItemsAndPopupPlayModeTests:BackpackWorkspacePlayModeTests
    {
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T21-ItemsAndPopup";
        private static T Ref<T>(Object target,string field) where T:Object
        {var property=new SerializedObject(target).FindProperty(field);return property!=null?(T)property.objectReferenceValue:(T)target.GetType().GetField(field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(target);}
        protected override ItemCatalog CustomizeCatalog(ItemCatalog catalog)
        {
            if(!NUnit.Framework.TestContext.CurrentContext.Test.Name.StartsWith("PreviewMana"))return catalog;
            var copy=Object.Instantiate(catalog);TemporaryAssets.Add(copy);var data=new SerializedObject(copy);var list=data.FindProperty("_definitions");
            var original=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Spell/Wands/Items/Item_Wand_StarterA.asset");
            for(int i=0;i<list.arraySize;i++)if(list.GetArrayElementAtIndex(i).objectReferenceValue==original)
            {
                var item=Object.Instantiate(original);var wand=Object.Instantiate(original.WandDefinition);TemporaryAssets.Add(item);TemporaryAssets.Add(wand);
                var stats=new SerializedObject(wand);stats.FindProperty("_maxMana").floatValue=239.9f;stats.FindProperty("_manaRegenPerSecond").floatValue=14.2f;
                stats.FindProperty("_castCooldown").floatValue=.69f;stats.ApplyModifiedPropertiesWithoutUndo();
                var def=new SerializedObject(item);def.FindProperty("_wandDefinition").objectReferenceValue=wand;def.ApplyModifiedPropertiesWithoutUndo();
                list.GetArrayElementAtIndex(i).objectReferenceValue=item;
            }
            data.ApplyModifiedPropertiesWithoutUndo();return copy;
        }
        protected override SinglePlayerProfile CustomizeProfile(SinglePlayerProfile profile,ItemCatalogIndex index)
        {
            var pumpkin=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Pumpkin.asset");
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("pumpkin",pumpkin.ContentId,0),new ItemLocation(ItemLocationKind.Backpack,"bag",4,4,0,-1)),Is.True);
            return profile;
        }
        private InventoryItemView Card(SinglePlayerInventoryController ui,string id)
        {foreach(var item in ui.GetComponentsInChildren<InventoryItemView>())if(item.Snapshot.InstanceId==id)return item;return null;}
        private InventoryInfoPopup RightClick(SinglePlayerInventoryController ui,string id)
        {
            var card=Card(ui,id);Assert.That(card,Is.Not.Null);
            ExecuteEvents.Execute(card.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Right,position=new Vector2(Screen.width-2,2)},ExecuteEvents.pointerClickHandler);
            return ui.GetComponentInChildren<InventoryInfoPopup>();
        }
        [UnityTest] public IEnumerator BackpackRemovesDetailsEnlargesLeftAndRightClickWorksInThreeLocations()
        {
            Backpack.OpenBackpack();yield return null;
            var root=Ref<GameObject>(Backpack,"_panelRoot");Assert.That(root.transform.Find("RightPart"),Is.Null);
            var left=(RectTransform)root.transform.Find("LeftPart");Assert.That(left.rect.width,Is.GreaterThan(((RectTransform)root.transform).rect.width*.4f));
            var grid=Backpack.GetComponentInChildren<InventoryGridView>();var scroll=grid.GetComponentInParent<ScrollRect>();
            Assert.That(scroll.viewport.rect.width,Is.GreaterThan(left.rect.width*.8f),"操作视口必须实际变大，不能只扩外框");
            Assert.That(grid.CellSize*8,Is.EqualTo(scroll.viewport.rect.width).Within(.1f));
            uint before=Session.ReadOnly.Version;var popup=RightClick(Backpack,"pumpkin");Assert.That(popup,Is.Not.Null);
            Assert.That(Session.ReadOnly.Version,Is.EqualTo(before));Assert.That(Ref<Text>(popup,"_body").text,Is.EqualTo("详细信息待开发"));
            Keys(Key.Escape);yield return null;Keys();yield return null;Assert.That(Backpack.IsOpen,Is.True);Assert.That(popup.IsOpen,Is.False);
            Session.ReadOnly.TryGetView("pumpkin",out var item);Backpack.BeginDrag(item,Vector2.zero,null,true);Backpack.SubmitToTarget(InventoryDropKind.Ground);yield return null;
            Find<NearbyPickupController>().RefreshCandidates();yield return null;
            Assert.That(RightClick(Backpack,"pumpkin"),Is.Not.Null);popup.Hide();
            // 独立种子验证仓库右键；地面拾取仍留给投掷专项，不让此UI测试绕过拾取所有权。
            Assert.That(Session.Profile.Items.TryAdd(new RuntimeItemState("warehouse-pumpkin",item.ContentId,0),ItemLocation.Warehouse),Is.True);
            Backpack.Close();EnterWarehouse();Warehouse.OpenWarehouse();yield return null;
            Assert.That(RightClick(Warehouse,"warehouse-pumpkin"),Is.Not.Null);
            yield return new WaitForEndOfFrame();Capture("warehouse-right-click-placeholder.png");
            Warehouse.Close();Backpack.OpenBackpack();yield return null;
            yield return new WaitForEndOfFrame();Capture("backpack-two-columns.png");
        }
        private void EnterWarehouse()
        {var area=Ref<HubAreaVolume>(Find<HubInteractionController>(),"_warehouseArea");Player.CharacterController.enabled=false;Player.enabled=false;Player.transform.position=area.Center;Physics.SyncTransforms();}
        [UnityTest] public IEnumerator PreviewManaMaximumsAreIntegersAndQualificationSpellsHaveNoCounter()
        {
            Assert.That(Session.Profile.Items.TryMove("ice",new ItemLocation(ItemLocationKind.WandSlot,"w0",0,0,0,0)).Success,Is.True);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent{InventoryId=Session.ReadOnly.InventoryId,Version=Session.ReadOnly.Version});
            Backpack.OpenBackpack();yield return null;
            var card=Card(Backpack,"ice");Assert.That(card.Snapshot.IsQualificationSpell,Is.True);
            Assert.That(Ref<Text>(card,"_spellCount").text,Is.Empty);
            var panel=Backpack.GetComponentInChildren<WandProgramPanelView>();string text=Ref<Text>(panel,"_header").text;
            Assert.That(text,Does.Contain("Mana 240</color>").And.Contain("恢复 14/s").And.Contain("冷却 0.69s</color>"));
            Assert.That(text,Does.Not.Contain("239.9").And.Not.Contain("14.2").And.Not.Contain("0/0.69"));
            Session.WandsReadOnly.TryGetState("w0",out var actual);Assert.That(actual.MaxMana,Is.EqualTo(239.9f));
            yield return new WaitForEndOfFrame();Capture("qualification-no-counter-max-stats.png");
        }
        [UnityTest] public IEnumerator ShopPreservesAuthoredBagImageAndShowsBriefCapacity()
        {
            var area=Ref<HubAreaVolume>(Find<HubInteractionController>(),"_shopArea");Player.CharacterController.enabled=false;Player.enabled=false;Player.transform.position=area.Center;Physics.SyncTransforms();
            Keys(Key.F);yield return null;Keys();yield return null;
            var shop=Find<ShopController>();Assert.That(shop.IsOpen,Is.True);
            var host=Ref<GameObject>(shop,"_panel").GetComponentInParent<SinglePlayerInventoryController>();var workspace=Ref<WarehouseWorkspaceView>(host,"_warehouseWorkspace");
            var icon=Ref<Image>(workspace,"_bagIcon");Assert.That(icon.sprite,Is.Not.Null);Assert.That(icon.enabled,Is.True);
            Assert.That(Ref<Text>(workspace,"_bagCaption").text,Does.Not.Contain("当前背包"));
            var capacity=Ref<Text>(workspace,"_bagCapacity");if(capacity!=null)Assert.That(capacity.text,Does.Not.Contain("携带背包"));
            yield return new WaitForEndOfFrame();Capture("shop-visible-bag-brief-caption.png");
        }
        [UnityTest] public IEnumerator OrdinaryItemUsesActualGAimModelFlightAndCanBePickedUpAfterLanding()
        {
            Player.enabled=false;Bind(0,"pumpkin");Keys(Key.Digit4);yield return null;Keys();yield return null;yield return null;
            var use=Find<Game.Character.ContainerUseController>();Keys(Key.G);for(int i=0;i<3;i++)yield return null;
            var line=Ref<LineRenderer>(use,"_trajectory");Assert.That(line.enabled,Is.True,"普通物品G也必须显示抛物线");
            Keys();yield return null;Assert.That(use.LastResult.Success,Is.True,use.LastResult.Transfer.Failure.ToString());
            Session.Profile.Items.TryGetLocation("pumpkin",out var location);Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.ThrownContainer));
            var projectile=Object.FindFirstObjectByType<ThrownContainerProjectile>();Assert.That(projectile,Is.Not.Null);
            var model=projectile.transform.Find("ThrownContainerModel");Assert.That(model,Is.Not.Null);Assert.That(model.GetComponentsInChildren<Renderer>(),Is.Not.Empty);
            Assert.That(projectile.GetComponent<Rigidbody>().linearVelocity.magnitude,Is.GreaterThan(5));
            yield return new WaitForEndOfFrame();Capture("ordinary-item-flight-model.png");
            for(int i=0;i<240;i++)
            {Session.Profile.Items.TryGetLocation("pumpkin",out location);if(location.Kind==ItemLocationKind.WorldItem)break;yield return null;}
            Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));var pickup=Session.FindWorldProxy("pumpkin");Assert.That(pickup,Is.Not.Null);
            Player.CharacterController.enabled=false;Player.transform.position=pickup.transform.position+Vector3.back;Physics.SyncTransforms();yield return null;
            Assert.That(Find<NearbyPickupController>().TryCollect("pumpkin"),Is.True);
            Session.Profile.Items.TryGetLocation("pumpkin",out location);Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
        }
        [UnityTest] public IEnumerator StableOrdinaryAimAndOpenPopupHaveNoScopedGc()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                Player.enabled=false;Bind(0,"pumpkin");Keys(Key.Digit4);yield return null;Keys();yield return null;yield return null;Keys(Key.G);
                for(int i=0;i<10;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;
                WriteProfile("ordinary-aim-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"Norn.ContainerThrow.Trajectory"},"Stable G ordinary item aim; no launch/asset changes");
                // 打开暂停界面取消G瞄准，再释放键，避免性能测试额外抛出物品。
                Backpack.OpenBackpack();yield return null;Keys();yield return null;Assert.That(RightClick(Backpack,"fire"),Is.Not.Null);
                for(int i=0;i<10;i++)yield return null;first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;
                WriteProfile("popup-interaction-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"Norn.Inventory.InteractionTick","Norn.Backpack.Update"},"Paused open item-info popup after warmup; discrete Show excluded");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);Keys();}
        }
    }
}
#endif
