#if UNITY_EDITOR
using System;
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
    public sealed class WorldItemPickupInputPlayModeTests:BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath=>"Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_ItemReview.unity";
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T1-Items";
        private ItemDefinition Definition(string folder,string name)=>AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Items/"+folder+"/"+name+".asset");
        private WorldItemPickup Spawn(string folder="Food",string name="Apple")
        {
            Player.enabled=false;
            var definition=Definition(folder,name);string spawnId=Guid.NewGuid().ToString("N");
            Assert.That(Session.TryRegisterWorldItem(definition,Session.WorldSceneKey,spawnId,Player.transform.position+Vector3.forward,Quaternion.identity),Is.True);
            WorldItemPickup found=null;foreach(var root in Scene.GetRootGameObjects())foreach(var proxy in root.GetComponentsInChildren<WorldItemPickup>(true))
                if(Session.ReadOnly.TryGetView(proxy.ItemId,out var item) && item.ContentId==definition.ContentId)found=proxy;
            Assert.That(found,Is.Not.Null);return found;
        }
        [UnityTest] public IEnumerator FShowsCameraFacingPromptAndCollectsOriginalInstanceWithoutCycling()
        {
            var proxy=Spawn();string id=proxy.ItemId;int selected=Session.ToolbeltReadOnly.SelectedSlot;
            for(int i=0;i<15;i++)yield return null;
            var prompt=Find<WorldPickupPromptView>();var visual=(GameObject)new SerializedObject(prompt).FindProperty("_visual").objectReferenceValue;
            Assert.That(visual.activeSelf,Is.True);Assert.That(visual.GetComponentInChildren<Text>().text,Does.Contain("F 拾取"));
            Assert.That(Vector3.Dot(visual.transform.forward,Camera.main.transform.forward),Is.GreaterThan(.999f));
            yield return new WaitForEndOfFrame();Capture("pickup-prompt.png");
            Keys(Key.F);yield return null;Keys();yield return null;
            Assert.That(Session.ReadOnly.TryGetView(id,out var item),Is.True);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(Session.ToolbeltReadOnly.SelectedSlot,Is.EqualTo(selected));Assert.That(visual.activeSelf,Is.False);
            Assert.That(item.InstanceId,Is.EqualTo(id));
        }
        [UnityTest] public IEnumerator FullBackpackOpensArrangementAndKeepsGroundItemThenDragSucceeds()
        {
            var store=Session.Profile.Items;var apple=Definition("Food","Apple");
            for(int y=0;y<8;y++)for(int x=0;x<8;x++)store.TryAdd(RuntimeItemState.Create(apple.ContentId),new ItemLocation(ItemLocationKind.Backpack,"bag",x,y,0,-1));
            var proxy=Spawn("Food","Pumpkin");string id=proxy.ItemId;uint version=Session.ReadOnly.Version;
            Keys(Key.F);yield return null;Keys();yield return null;
            Assert.That(Backpack.IsOpen,Is.True);Assert.That(proxy.gameObject.activeSelf,Is.True);Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));
            yield return new WaitForEndOfFrame();Capture("full-backpack.png");
            // Spawn采用候选Profile，之后必须读当前Store，不能用上一代引用。
            var moves=new System.Collections.Generic.List<InventoryEntry>();store=Session.Profile.Items;
            foreach(var entry in store)if(entry.Location.Kind==ItemLocationKind.Backpack && entry.Location.OwnerId=="bag" && entry.Location.X<3 && entry.Location.Y<3)moves.Add(new InventoryEntry(entry.Item,ItemLocation.Warehouse));
            Assert.That(store.TryApply(moves,store.Version).Success,Is.True);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent{InventoryId=Session.ReadOnly.InventoryId,Version=Session.ReadOnly.Version});yield return null;
            Session.ReadOnly.TryGetView(id,out var ground);Backpack.BeginDrag(ground,Vector2.zero,null,true);Backpack.SubmitToBackpack(0,0,"bag");yield return null;
            Session.ReadOnly.TryGetView(id,out var collected);Assert.That(collected.Location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
        }
        [UnityTest] public IEnumerator RepeatedRegistrationAndSaveReloadDoNotGenerateAnotherItem()
        {
            var proxy=Spawn();string id=proxy.ItemId;var records=Session.Profile.HubWorldItems;string origin=records[records.Count-1].SpawnId;
            Keys(Key.F);yield return null;Keys();uint version=Session.ReadOnly.Version;int count=Session.ReadOnly.Count;
            Assert.That(Session.Save().Success,Is.True);
            Assert.That(Session.TryRegisterWorldItem(Definition("Food","Apple"),Session.WorldSceneKey,origin,Vector3.one*100,Quaternion.identity),Is.True);
            Assert.That(Session.ReadOnly.Count,Is.EqualTo(count));Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));
            Assert.That(Session.Repository.TryLoad(out var loaded).Success,Is.True);Assert.That(loaded.Items.TryGetLocation(id,out var location),Is.True);Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.Backpack));
            Assert.That(loaded.HubWorldItems.Count,Is.EqualTo(records.Count));
        }
        [UnityTest] public IEnumerator OutOfReachPausedAndInactivePickupsCannotBeCollected()
        {
            var proxy=Spawn();string id=proxy.ItemId;proxy.transform.position+=Vector3.one*100;
            Keys(Key.F);yield return null;Keys();Assert.That(Session.ReadOnly.TryGetView(id,out var item),Is.True);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            proxy.transform.position=Player.transform.position+Vector3.forward;proxy.gameObject.SetActive(false);Keys(Key.F);yield return null;Keys();
            Assert.That(Session.ReadOnly.TryGetView(id,out item),Is.True);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            proxy.gameObject.SetActive(true);Backpack.OpenBackpack();yield return null;Keys(Key.F);yield return null;Keys();
            Session.ReadOnly.TryGetView(id,out item);Assert.That(item.Location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
        }
        [UnityTest] public IEnumerator FailedRegistrationDoesNotPublishItemOrConsumeSpawnIdentity()
        {
            Player.enabled=false;uint version=Session.ReadOnly.Version;int count=Session.ReadOnly.Count,records=Session.Profile.HubWorldItems.Count;
            var blocked=typeof(ProfileSaveRepository).GetField("_blocked",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            blocked.SetValue(Session.Repository,true);
            try{Assert.That(Session.TryRegisterWorldItem(Definition("Food","Apple"),Session.WorldSceneKey,"write.failure",Player.transform.position,Quaternion.identity),Is.False);}
            finally{blocked.SetValue(Session.Repository,false);}
            Assert.That(Session.ReadOnly.Version,Is.EqualTo(version));Assert.That(Session.ReadOnly.Count,Is.EqualTo(count));Assert.That(Session.Profile.HubWorldItems.Count,Is.EqualTo(records));
            Assert.That(Session.TryRegisterWorldItem(Definition("Food","Apple"),Session.WorldSceneKey,"write.failure",Player.transform.position,Quaternion.identity),Is.True);
            Assert.That(Session.ReadOnly.Count,Is.EqualTo(count+1));yield return null;
        }
        [UnityTest] public IEnumerator DropAndRebuildPreserveOriginalModelAndGroundPose()
        {
            var first=Spawn();string id=first.ItemId;Keys(Key.F);yield return null;Keys();
            Player.transform.position+=Vector3.right*4;
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{InventoryId=Session.ReadOnly.InventoryId,ItemId=id,Kind=InventoryCommandKind.Drop,ExpectedVersion=Session.ReadOnly.Version});
            yield return null;
            WorldItemPickup dropped=null;foreach(var root in Scene.GetRootGameObjects())foreach(var proxy in root.GetComponentsInChildren<WorldItemPickup>(true))if(proxy.ItemId==id && proxy.gameObject.activeSelf)dropped=proxy;
            Assert.That(dropped,Is.Not.Null);Assert.That(dropped.transform.Find("Visual"),Is.Not.Null);
            var position=dropped.transform.position;var rotation=dropped.transform.rotation;Assert.That(Session.Save().Success,Is.True);
            Assert.That(Session.Repository.TryLoad(out var loaded).Success,Is.True);loaded.Items.TryGetLocation(id,out var location);Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            // 采用候选档会重建所有代理，验证实际恢复链，非仅DTO字段往返。
            Assert.That(Session.TryRegisterWorldItem(Definition("Food","Beetroot"),Session.WorldSceneKey,"force.rebuild",Player.transform.position,Quaternion.identity),Is.True);yield return null;
            WorldItemPickup rebuilt=null;foreach(var root in Scene.GetRootGameObjects())foreach(var proxy in root.GetComponentsInChildren<WorldItemPickup>(true))if(proxy.ItemId==id && proxy.gameObject.activeSelf)rebuilt=proxy;
            Assert.That(rebuilt,Is.Not.Null);Assert.That(Vector3.Distance(rebuilt.transform.position,position),Is.LessThan(.001f));Assert.That(Quaternion.Angle(rebuilt.transform.rotation,rotation),Is.LessThan(.01f));
            Assert.That(loaded.HubWorldItems[loaded.HubWorldItems.Count-1].InstanceId,Is.EqualTo(id));
        }
        [UnityTest] public IEnumerator AllEighteenTypesRegisterIncludingEmptyContainersAndLShapes()
        {
            Player.enabled=false;int before=Session.ReadOnly.Count;
            foreach(string guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/SinglePlayer/Items"}))
            {
                var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                Assert.That(Session.TryRegisterWorldItem(definition,Session.WorldSceneKey,definition.ContentId,Player.transform.position,Quaternion.identity),Is.True,definition.name);
            }
            Assert.That(Session.ReadOnly.Count,Is.EqualTo(before+18));Assert.That(Session.Save().Success,Is.True);yield return null;
        }
        [UnityTest] public IEnumerator HubGroundItemStaysAtHubDuringRaidAndReturnsAfterRollback()
        {
            var pickup=Spawn();string id=pickup.ItemId;var position=pickup.transform.position;
            var raid=AssetDatabase.LoadAssetAtPath<RaidDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerRaids/Raid.asset");
            var preparation=new RaidPreparationService();Assert.That(preparation.TryPrepare(Session.Profile,Session.Repository,raid,new ProtectedSpellSelection(),out var candidate).Success,Is.True);
            Session.AdoptCommitted(candidate);yield return null;
            foreach(var root in Scene.GetRootGameObjects())foreach(var proxy in root.GetComponentsInChildren<WorldItemPickup>(true))
                Assert.That(proxy.ItemId!=id || !proxy.gameObject.activeSelf,Is.True,"据点未携带的地面物品不能重建到远征世界。");
            // 远征ReadModel本来就隐藏HubAsset；底层账仍应保留它供回程恢复。
            Assert.That(Session.ReadOnly.TryGetView(id,out _),Is.False);
            Assert.That(Session.Profile.Items.TryGetLocation(id,out var retained),Is.True);Assert.That(retained.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            Assert.That(preparation.TryRollback(Session.Profile,Session.Repository,out var restored).Success,Is.True);Session.AdoptCommitted(restored);yield return null;
            WorldItemPickup returned=null;foreach(var root in Scene.GetRootGameObjects())foreach(var proxy in root.GetComponentsInChildren<WorldItemPickup>(true))if(proxy.ItemId==id && proxy.gameObject.activeSelf)returned=proxy;
            Assert.That(returned,Is.Not.Null);Assert.That(Vector3.Distance(returned.transform.position,position),Is.LessThan(.001f));
        }
        [UnityTest] public IEnumerator ShutdownDuringDepartureRollsBackDataWithoutCreatingChildren()
        {
            Spawn();var raid=AssetDatabase.LoadAssetAtPath<RaidDefinition>("Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerRaids/Raid.asset");
            Assert.That(new RaidPreparationService().TryPrepare(Session.Profile,Session.Repository,raid,new ProtectedSpellSelection(),out var candidate).Success,Is.True);
            Session.AdoptCommitted(candidate);yield return null;
            Session.gameObject.SetActive(false);yield return null;
            Assert.That(Session.Profile.PendingRaid,Is.Null);Assert.That(Session.Repository.TryLoad(out var loaded).Success,Is.True);Assert.That(loaded.HubWorldItems.Count,Is.EqualTo(1));
            foreach(var proxy in Session.GetComponentsInChildren<WorldItemPickup>(true))Assert.That(proxy.gameObject.activeSelf,Is.False);
        }
        [UnityTest] public IEnumerator StationaryEdgeDragScrollsAndDropsOnTheCurrentlyVisibleCell()
        {
            Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
            var grid=(InventoryGridView)new SerializedObject(Backpack).FindProperty("_gridView").objectReferenceValue;
            var scroll=grid.GetComponentInParent<ScrollRect>();var viewport=scroll.viewport;scroll.horizontalNormalizedPosition=0;scroll.verticalNormalizedPosition=1;
            var pointer=RectTransformUtility.WorldToScreenPoint(null,viewport.TransformPoint(new Vector3(viewport.rect.xMax-5,viewport.rect.yMin+5)));
            Session.ReadOnly.TryGetView("b0",out var source);Backpack.BeginDrag(source,pointer,null,true);float start=scroll.verticalNormalizedPosition;
            for(int i=0;i<30;i++)yield return null;
            Assert.That(scroll.verticalNormalizedPosition,Is.LessThan(start));Assert.That(scroll.horizontalNormalizedPosition,Is.GreaterThan(0));
            grid.TryGetCell(pointer,null,out var cell);Backpack.SubmitToBackpack(cell.x,cell.y,"bag");yield return null;
            Session.ReadOnly.TryGetView("b0",out var result);Assert.That(result.Location.X,Is.EqualTo(cell.x));Assert.That(result.Location.Y,Is.EqualTo(cell.y));
            float stopped=scroll.verticalNormalizedPosition;for(int i=0;i<10;i++)yield return null;Assert.That(scroll.verticalNormalizedPosition,Is.EqualTo(stopped));
        }
        [UnityTest] public IEnumerator PickupQueryBillboardAndAutoScrollScopesHaveNoGcAlloc()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled;bool cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                Spawn();for(int i=0;i<20;i++)yield return null;
                int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;for(int i=0;i<60;i++)yield return null;
                WriteProfile("pickup-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"Norn.Items.NearbyQuery","Norn.Items.PromptBillboard"},"Stable focus; discrete registration/text update excluded");
                Backpack.OpenBackpack();yield return null;Canvas.ForceUpdateCanvases();
                var grid=(InventoryGridView)new SerializedObject(Backpack).FindProperty("_gridView").objectReferenceValue;
                var viewport=grid.GetComponentInParent<ScrollRect>().viewport;
                var pointer=RectTransformUtility.WorldToScreenPoint(null,viewport.TransformPoint(new Vector3(viewport.rect.xMax-5,viewport.rect.yMin+5)));
                Session.ReadOnly.TryGetView("b0",out var source);Backpack.BeginDrag(source,pointer,null,true);
                for(int i=0;i<10;i++)yield return null;first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<60;i++)yield return null;
                WriteProfile("autoscroll-profiler",first,UnityEditorInternal.ProfilerDriver.lastFrameIndex,new[]{"Norn.Backpack.AutoScroll","Norn.Items.PromptBillboard"},"Paused stationary drag; cached viewport; includes clamped edge frames");
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);}
        }
        [UnityTest] public IEnumerator BehindCameraFocusStaysHiddenWithoutRepeatedActivation()
        {
            var pickup=Spawn();for(int i=0;i<15;i++)yield return null;
            var prompt=Find<WorldPickupPromptView>();var visual=(GameObject)new SerializedObject(prompt).FindProperty("_visual").objectReferenceValue;
            var count=visual.AddComponent<PickupPromptActivationCounter>();
            var anchor=new GameObject("Behind camera anchor");anchor.transform.SetParent(Session.transform,false);anchor.transform.position=Camera.main.transform.position-Camera.main.transform.forward;
            EventBus<NearbyPickupFocusChangedEvent>.Publish(new NearbyPickupFocusChangedEvent{InventoryId=Session.ReadOnly.InventoryId,ItemId=pickup.ItemId,Anchor=anchor.transform,Visible=true});
            yield return null;count.Activations=0;for(int i=0;i<10;i++){yield return null;Assert.That(visual.activeSelf,Is.False);}
            Assert.That(count.Activations,Is.Zero,"隐藏状态不能每帧先启用再关闭Canvas。");
        }
    }
    public sealed class ItemPickupSandboxPlayModeTests:BackpackWorkspacePlayModeTests
    {
        protected override string ReviewPath=>"Assets/_Project/Scenes/SinglePlayer/ItemPickupSandbox.unity";
        protected override string EvidencePath=>"TestResults/SinglePlayer/M6-T1-Items";
        [UnityTest] public IEnumerator SceneSpawnPointsRegisterEighteenUniqueItemsAndUseIsolatedSceneKey()
        {
            Player.enabled=false;for(int i=0;i<12;i++)yield return null;
            Assert.That(Session.WorldSceneKey,Is.EqualTo("items.sandbox"));Assert.That(Session.Profile.HubWorldItems.Count,Is.EqualTo(18));
            var ids=new System.Collections.Generic.HashSet<string>();int points=0,active=0;
            foreach(var root in Scene.GetRootGameObjects())
            {
                foreach(var spawn in root.GetComponentsInChildren<WorldItemSpawnPoint>(true)){points++;Assert.That(ids.Add(spawn.SpawnId),Is.True,spawn.SpawnId);}
                foreach(var pickup in root.GetComponentsInChildren<WorldItemPickup>(true))if(pickup.gameObject.activeSelf)active++;
            }
            Assert.That(points,Is.EqualTo(18));Assert.That(active,Is.EqualTo(18));
            yield return new WaitForEndOfFrame();Capture("items-sandbox.png");
        }
    }
    public sealed class PickupPromptActivationCounter:MonoBehaviour
    {
        public int Activations;
        private void OnEnable()=>Activations++;
    }
}
#endif
