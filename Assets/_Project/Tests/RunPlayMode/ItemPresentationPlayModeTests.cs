#if UNITY_EDITOR
using System;
using System.Collections;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using UnityEngine.UI;
namespace Game.Run.Tests
{
    public sealed class ItemPresentationPlayModeTests:BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath=>"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ItemPresentationReview.unity";
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T1-ItemPresentation";
        [UnitySetUp]public IEnumerator IsolateExistingAuthoringItems()
        {
            foreach(var root in Scene.GetRootGameObjects())foreach(var proxy in root.GetComponentsInChildren<WorldItemPickup>(true))proxy.gameObject.SetActive(false);
            Backpack.Close();Warehouse?.Close();Player.enabled=false;yield return null;
        }
        private WorldItemPickup Spawn(string name,int index)
        {
            var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/"+name+".asset");
            Assert.That(Session.TryRegisterWorldItem(definition,Session.WorldSceneKey,"presentation."+index,Player.transform.position+Vector3.right*(.6f+.2f*index),Quaternion.identity),Is.True);
            foreach(var root in Scene.GetRootGameObjects())foreach(var proxy in root.GetComponentsInChildren<WorldItemPickup>(true))
            {bool testItem=false;foreach(var record in Session.Profile.HubWorldItems)if(record.InstanceId==proxy.ItemId && record.SpawnId!=null && record.SpawnId.StartsWith("presentation.",StringComparison.Ordinal)){testItem=true;break;}if(!testItem)proxy.gameObject.SetActive(false);}
            // Profile候选提交会重建已有代理；调用者按ID重新查询，不能保存被销毁的旧组件引用。
            return WorldItemPickup.FindNearby(Session.ReadOnly.InventoryId,null,Player.transform,3);
        }
        [UnityTest]public IEnumerator WheelSelectsSpecificItemAndFCollectsIt()
        {
            Spawn("Item_Food_Apple",0);Spawn("Item_Food_Beetroot",1);var nearby=Find<NearbyPickupController>();nearby.RefreshCandidates();int count=nearby.Count;
            Assert.That(count,Is.GreaterThanOrEqualTo(2));
            InputSystem.QueueStateEvent(Mouse,new MouseState{scroll=new Vector2(0,-120)});InputSystem.Update();yield return null;
            string selected=nearby.FocusedPickup.ItemId;Assert.That(nearby.SelectedIndex,Is.EqualTo(1));
            Keys(Key.F);yield return null;Keys();yield return null;
            Assert.That(Session.ReadOnly.TryGetView(selected,out var item),Is.True);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
        }
        [UnityTest]public IEnumerator MoreThanFiveEntriesRemainSelectableAtFixedScreenPosition()
        {
            for(int i=0;i<7;i++)Spawn("Item_Food_Apple",i);var nearby=Find<NearbyPickupController>();nearby.RefreshCandidates();var view=Find<NearbyPickupListView>();yield return null;
            Assert.That(view.GetComponent<Canvas>().renderMode,Is.EqualTo(RenderMode.ScreenSpaceOverlay));var position=view.Panel.anchoredPosition;
            for(int i=0;i<6;i++)nearby.SelectRelative(1);yield return null;Assert.That(view.VisibleCount,Is.EqualTo(5));Assert.That(view.FirstIndex,Is.GreaterThan(0));
            yield return new WaitForEndOfFrame();Capture("five-row-pickup-list.png");
            Camera.main.transform.Rotate(0,90,0);yield return null;Assert.That(view.Panel.anchoredPosition,Is.EqualTo(position));
        }
        [UnityTest]public IEnumerator SelectedIdentitySurvivesDistanceReorderingAndOutOfRangeIsRejected()
        {
            Spawn("Item_Food_Apple",0);Spawn("Item_Food_Beetroot",1);var nearby=Find<NearbyPickupController>();nearby.RefreshCandidates();nearby.SelectRelative(1);string id=nearby.FocusedPickup.ItemId;
            Player.transform.position+=Vector3.right*.7f;nearby.RefreshCandidates();Assert.That(nearby.FocusedPickup.ItemId,Is.EqualTo(id));
            Player.transform.position+=Vector3.right*20;Assert.That(nearby.TryCollectFocused(),Is.False);Session.ReadOnly.TryGetView(id,out var item);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));yield return null;
        }
        [UnityTest]public IEnumerator NearbyGridShowsActualFootprintsWhilePausedWithoutMovingAssets()
        {
            Spawn("Item_Food_Apple",0);Spawn("Item_Food_Pumpkin",1);Find<NearbyPickupController>().RefreshCandidates();Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            var grid=(NearbyItemGridView)new SerializedObject(Backpack).FindProperty("_nearbyGrid").objectReferenceValue;
            Assert.That(grid.Rows,Is.GreaterThanOrEqualTo(3));int shown=0;
            foreach(var view in grid.GetComponentsInChildren<InventoryItemView>())
            {shown++;Assert.That(view.Rect.rect.width,Is.GreaterThan(0));Assert.That(view.Snapshot.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));Assert.That(view.Snapshot.Icon,Is.Not.Null);
                var icon=(Image)new SerializedObject(view).FindProperty("_icon").objectReferenceValue;Assert.That(icon,Is.Not.Null);Assert.That(icon.enabled,Is.True);Assert.That(icon.sprite,Is.SameAs(view.Snapshot.Icon));}
            Assert.That(shown,Is.GreaterThanOrEqualTo(2));yield return new WaitForEndOfFrame();Capture("nearby-grid.png");
        }
        [UnityTest]public IEnumerator HighlightUsesOwnedMaterialsAndLeavesSourceUntouched()
        {
            var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Apple.asset");
            var original=definition.WorldModel.GetComponentInChildren<Renderer>().sharedMaterial;string before=EditorJsonUtility.ToJson(original);
            var proxy=Spawn("Item_Food_Apple",0);yield return null;
            Assert.That(proxy.GetComponent("WorldItemHighlight"),Is.Not.Null);
            Assert.That(proxy.GetComponentInChildren<Renderer>().sharedMaterial,Is.SameAs(original));Assert.That(EditorJsonUtility.ToJson(original),Is.EqualTo(before));
            if(original.HasProperty("_EmissionColor") && !original.IsKeywordEnabled("_EMISSION"))
                Assert.That(proxy.GetComponentInChildren<Renderer>().sharedMaterial.GetColor("_EmissionColor").maxColorComponent,Is.LessThan(.5f),"关闭Emission的旧白色配置不得突然生效。");
        }
        [UnityTest]public IEnumerator ScrollCaptureReleasesOnPauseAndDisable()
        {
            Spawn("Item_Food_Apple",0);Find<NearbyPickupController>().RefreshCandidates();yield return null;Assert.That(GameplayScrollCapture.IsCaptured,Is.True);
            Backpack.OpenBackpack();yield return null;Assert.That(GameplayScrollCapture.IsCaptured,Is.False);Backpack.Close();yield return null;
            Find<Game.Character.NearbyPickupInput>().enabled=false;Assert.That(GameplayScrollCapture.IsCaptured,Is.False);
        }
        [UnityTest]public IEnumerator StableQueryAndFixedHudHaveZeroScopedGc()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try{UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                Spawn("Item_Food_Apple",0);for(int i=0;i<20;i++)yield return null;int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;for(int i=0;i<60;i++)yield return null;
                WriteProfile("fixed-list-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"Norn.Items.NearbyQuery","Norn.Items.PickupList"},"Stable query/list; registration and text change excluded");yield return new WaitForEndOfFrame();Capture("fixed-pickup-list.png");}
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [UnityTest]public IEnumerator SelectedBorderSurvivesInventoryRefresh()
        {
            Backpack.OpenBackpack();yield return null;Backpack.SelectItem("b0");yield return null;
            InventoryShapeOutlineGraphic outline=null;
            foreach(var view in Backpack.GetComponentInChildren<InventoryGridView>().GetComponentsInChildren<InventoryItemView>())
                if(view.Snapshot.InstanceId=="b0")outline=view.GetComponentInChildren<InventoryShapeOutlineGraphic>();
            Assert.That(outline,Is.Not.Null);var before=outline.color;
            Assert.That(before.r,Is.EqualTo(1));Backpack.RefreshPresentation();yield return null;
            Assert.That(outline.color,Is.EqualTo(before));
        }
    }
}
#endif
