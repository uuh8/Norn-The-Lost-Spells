#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>独立Physics Scene布局与内存档复现真实丢弃；不读写用户正式资产或Scene。</summary>
    public sealed class WorldItemLifecyclePlayModeTests
    {
        private Scene _scene, _previous;
        private SinglePlayerInventorySession _session;
        private ItemCatalogIndex _index;
        private ItemOwnershipStore _store;
        private GameObject _ground;
        private ItemDefinition _apple;
        private Transform _actor;
        private MemoryFiles _files;

        [UnitySetUp] public IEnumerator Setup()
        {
            _files=new MemoryFiles();
            _previous=SceneManager.GetActiveScene();_scene=SceneManager.CreateScene("ItemLifecycle_"+Guid.NewGuid().ToString("N"));
            SceneManager.SetActiveScene(_scene);
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            Assert.That(catalog.TryCreateIndex(out _index,out var error),Is.True,error);
            _apple=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Apple.asset");
            var bag=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Backpacks/Item_Backpack_Bag.asset");
            _store=new ItemOwnershipStore(_index);
            Assert.That(_store.TryAdd(new RuntimeItemState("bag",bag.ContentId,0),new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0)),Is.True);
            Assert.That(_store.TryAdd(new RuntimeItemState("apple",_apple.ContentId,0),new ItemLocation(ItemLocationKind.Backpack,"bag",0,0,0,-1)),Is.True);
            _actor=new GameObject("Player placement reference").transform;_actor.position=Vector3.up;
            _ground=GameObject.CreatePrimitive(PrimitiveType.Cube);_ground.name="Ground";_ground.transform.position=new Vector3(0,-.25f,0);_ground.transform.localScale=new Vector3(40,.5f,40);
            _session=new GameObject("Inventory").AddComponent<SinglePlayerInventorySession>();
            var data=new SerializedObject(_session);data.FindProperty("_worldSceneKey").stringValue="lifecycle";data.ApplyModifiedPropertiesWithoutUndo();
            _session.WorldActor=_actor;
            Assert.That(_session.Initialize(new SinglePlayerProfile("player",_store),new ProfileSaveRepository("Temp/SinglePlayer/Lifecycle",_index,_files)),Is.True);
            Physics.SyncTransforms();yield return null;
        }
        private InventoryCommandResultEvent Drop(string id="apple")
        {
            InventoryCommandResultEvent result=default;
            void Capture(InventoryCommandResultEvent e){if(e.InventoryId==_session.ReadOnly.InventoryId)result=e;}
            EventBus<InventoryCommandResultEvent>.Subscribe(Capture);
            try{EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent{Kind=InventoryCommandKind.Drop,InventoryId=_session.ReadOnly.InventoryId,ItemId=id,ExpectedVersion=_session.Profile.Items.Version});}
            finally{EventBus<InventoryCommandResultEvent>.Unsubscribe(Capture);}return result;
        }
        private WorldItemPickup Proxy(string id)
        {
            foreach(var root in _scene.GetRootGameObjects())foreach(var pickup in root.GetComponentsInChildren<WorldItemPickup>(true))
                if(pickup.ItemId==id && pickup.gameObject.activeSelf)return pickup;
            return null;
        }
        private static Bounds BoundsOf(GameObject root)
        {
            var renderers=root.GetComponentsInChildren<Renderer>(true);Assert.That(renderers.Length,Is.GreaterThan(0));
            var bounds=renderers[0].bounds;for(int i=1;i<renderers.Length;i++)bounds.Encapsulate(renderers[i].bounds);return bounds;
        }
        [UnityTest] public IEnumerator DropPlacesActualModelBottomOnGroundAndKeepsIdentity()
        {
            Assert.That(Drop().Success,Is.True);yield return null;
            var proxy=Proxy("apple");Assert.That(proxy,Is.Not.Null);
            Assert.That(BoundsOf(proxy.gameObject).min.y,Is.EqualTo(0).Within(.025f),"Drop必须按模型真实底边落地，不能保存玩家腰部高度。");
            Assert.That(_store.TryGetLocation("apple",out var location),Is.True);Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.WorldItem));
            Assert.That(_session.Profile.HubWorldItems.Find(r=>r.InstanceId=="apple").Position,Is.EqualTo(proxy.transform.position));
        }
        [UnityTest] public IEnumerator MissingSupportDoesNotConsumeBackpackAsset()
        {
            _ground.SetActive(false);Physics.SyncTransforms();uint version=_store.Version;
            Assert.That(Drop().Success,Is.False,"没有合法地面时必须保留背包资产。");
            Assert.That(_store.Version,Is.EqualTo(version));Assert.That(_store.TryGetLocation("apple",out var location),Is.True);
            Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.Backpack));Assert.That(Proxy("apple"),Is.Null);yield return null;
        }
        [UnityTest] public IEnumerator WorldModelDimensionsAreNotNormalizedToPointFourFive()
        {
            var reference=UnityEngine.Object.Instantiate(_apple.PickupPrefab!=null?_apple.PickupPrefab:_apple.WorldModel);
            foreach(var point in reference.GetComponentsInChildren<WorldItemSpawnPoint>(true))point.enabled=false;
            var expected=BoundsOf(reference).size;UnityEngine.Object.Destroy(reference);
            Assert.That(Drop().Success,Is.True);yield return null;var actual=BoundsOf(Proxy("apple").gameObject).size;
            Assert.That(Vector3.Distance(actual,expected),Is.LessThan(.002f),"设计者配置的美术尺寸不能被统一缩放到0.45米。");
        }
        [UnityTest] public IEnumerator PumpkinUsesUserPickupPrefabScaleAndRuntimeHierarchy()
        {
            var definition=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Pumpkin.asset");
            Assert.That(definition.PickupPrefab,Is.Not.Null);Assert.That(definition.PickupPrefab.transform.localScale,Is.EqualTo(Vector3.one*2.5f));
            Assert.That(_store.TryAdd(new RuntimeItemState("pumpkin",definition.ContentId,0),new ItemLocation(ItemLocationKind.Backpack,"bag",3,3,0,-1)),Is.True);
            Assert.That(Drop("pumpkin").Success,Is.True);yield return null;var proxy=Proxy("pumpkin");
            Assert.That(proxy.transform.GetChild(0).localScale,Is.EqualTo(Vector3.one*2.5f));
            Assert.That(proxy.transform.parent.name,Is.EqualTo("WorldItems_Runtime"));
            Assert.That(BoundsOf(proxy.gameObject).min.y,Is.EqualTo(0).Within(.025f));
            foreach(var point in proxy.GetComponentsInChildren<WorldItemSpawnPoint>(true))Assert.That(point.enabled,Is.False);
        }
        [UnityTest] public IEnumerator TriggerAndPlayerColliderDoNotBecomeGroundSupport()
        {
            var trigger=GameObject.CreatePrimitive(PrimitiveType.Cube);trigger.transform.position=new Vector3(0,.4f,1.2f);trigger.transform.localScale=new Vector3(4,.2f,4);trigger.GetComponent<Collider>().isTrigger=true;
            var playerCollider=_actor.gameObject.AddComponent<CharacterController>();playerCollider.center=Vector3.forward*1.2f;
            Physics.SyncTransforms();Assert.That(Drop().Success,Is.True);yield return null;
            Assert.That(BoundsOf(Proxy("apple").gameObject).min.y,Is.EqualTo(0).Within(.025f));
        }
        [UnityTest] public IEnumerator OffsetPivotAndRaisedTableUseVisibleBottomInsteadOfRootHeight()
        {
            _ground.transform.position=new Vector3(0,.3f,0); // 支撑顶面0.55米，高于原水平地面。
            var author=new GameObject("Offset pivot author");var model=GameObject.CreatePrimitive(PrimitiveType.Cube);
            model.transform.SetParent(author.transform,false);model.transform.localPosition=Vector3.up*1.7f;model.transform.localScale=Vector3.one*.4f;
            var point=Author(author,"offset.apple");yield return null;yield return null;
            Assert.That(Proxy(point.RuntimeInstanceId).TryCollect("bag",new GridPlacement(3,0,0),_session.Profile.Items.Version).Success,Is.True);
            Physics.SyncTransforms();Assert.That(Drop(point.RuntimeInstanceId).Success,Is.True);yield return null;
            Assert.That(BoundsOf(Proxy(point.RuntimeInstanceId).gameObject).min.y,Is.EqualTo(.55f).Within(.025f));
            Assert.That(Proxy(point.RuntimeInstanceId).transform.position.y,Is.LessThan(0),"偏心Pivot可以在地面以下；可见模型底边才是落地依据。");
        }
        [UnityTest] public IEnumerator SlopedSupportKeepsBottomAboveHighestFootprintProbe()
        {
            _ground.transform.rotation=Quaternion.Euler(0,0,15);Physics.SyncTransforms();
            Assert.That(Drop().Success,Is.True);yield return null;var bounds=BoundsOf(Proxy("apple").gameObject);
            float highest=float.NegativeInfinity;var collider=_ground.GetComponent<Collider>();
            for(int x=0;x<2;x++)for(int z=0;z<2;z++)
            {
                var origin=new Vector3(x==0?bounds.min.x:bounds.max.x,10,z==0?bounds.min.z:bounds.max.z);
                Assert.That(collider.Raycast(new Ray(origin,Vector3.down),out var hit,30),Is.True);highest=Mathf.Max(highest,hit.point.y);
            }
            Assert.That(bounds.min.y,Is.EqualTo(highest+.01f).Within(.002f));
        }
        [UnityTest] public IEnumerator LegacyFloatingRecordIsRepairedWithoutSecondInstance()
        {
            Assert.That(_store.TryMove("apple",new ItemLocation(ItemLocationKind.WorldItem,"saved.apple",0,0,0,-1)).Success,Is.True);
            _session.Profile.HubWorldItems.Add(new HubWorldItemRecord{SceneKey="lifecycle",WorldSceneKey="lifecycle",InstanceId="apple",Position=new Vector3(0,1.4f,1.2f),Rotation=Quaternion.identity});
            _session.AdoptCommitted(RaidProfileOps.Clone(_session.Profile));yield return null;
            Assert.That(BoundsOf(Proxy("apple").gameObject).min.y,Is.EqualTo(0).Within(.025f));
            Assert.That(_session.Profile.Items.Count,Is.EqualTo(2));Assert.That(_session.Profile.HubWorldItems.Count,Is.EqualTo(1));
            Assert.That(_session.Profile.HubWorldItems[0].PoseKind,Is.EqualTo(WorldItemPoseKind.Grounded));
        }
        [UnityTest] public IEnumerator SessionDisableStopsWorldCommandsAndCollectedProxyLeavesRuntimeList()
        {
            Assert.That(Drop().Success,Is.True);yield return null;var proxy=Proxy("apple");
            _session.gameObject.SetActive(false);yield return null;
            Assert.That(WorldItemPickup.FindNearby(_session.ReadOnly.InventoryId,"apple",_actor,3),Is.Null,"独立Runtime根不能在Session关闭后继续暴露拾取权限。");
            _session.gameObject.SetActive(true);yield return null;
            Assert.That(proxy.TryCollect("bag",new GridPlacement(0,0,0),_session.Profile.Items.Version).Success,Is.True);
            Assert.That(_session.FindWorldProxy("apple"),Is.Null,"拾取后的代理要从Session追踪列表即时移除。");
        }
        [UnityTest] public IEnumerator RepairedGroundMetadataRemainsUnsavedUntilPersisted()
        {
            Assert.That(_store.TryMove("apple",new ItemLocation(ItemLocationKind.WorldItem,"saved.apple",0,0,0,-1)).Success,Is.True);
            _session.Profile.HubWorldItems.Add(new HubWorldItemRecord{SceneKey="lifecycle",WorldSceneKey="lifecycle",InstanceId="apple",Position=new Vector3(0,1.4f,1.2f),Rotation=Quaternion.identity});
            Assert.That(_session.Save().Success,Is.True);Assert.That(_session.HasUnsavedChanges,Is.False);
            _session.AdoptCommitted(RaidProfileOps.Clone(_session.Profile));yield return null;
            Assert.That(_session.HasUnsavedChanges,Is.True,"旧悬空记录修正不能只改内存而被保存脏标记遗漏。");
            Assert.That(_session.Save().Success,Is.True);Assert.That(_session.Repository.TryLoad(out var saved).Success,Is.True);
            Assert.That(saved.HubWorldItems[0].Position,Is.EqualTo(Proxy("apple").transform.position));Assert.That(saved.HubWorldItems[0].PoseKind,Is.EqualTo(WorldItemPoseKind.Grounded));
        }
        [UnityTest] public IEnumerator SceneParentAndInstanceScaleSurviveCollectionAndDrop()
        {
            var parent=new GameObject("Test").transform;parent.localScale=Vector3.one*2;
            var source=new GameObject("Scene apple");source.transform.SetParent(parent,false);source.transform.localScale=Vector3.one*2.5f;
            var model=UnityEngine.Object.Instantiate(_apple.WorldModel,source.transform,false);
            var expected=BoundsOf(model).size;
            var point=source.AddComponent<WorldItemSpawnPoint>();var data=new SerializedObject(point);
            data.FindProperty("_session").objectReferenceValue=_session;data.FindProperty("_definition").objectReferenceValue=_apple;
            data.FindProperty("_sceneKey").stringValue="lifecycle";data.FindProperty("_spawnId").stringValue="scaled.apple";data.ApplyModifiedPropertiesWithoutUndo();
            yield return null;yield return null;
            var record=_session.Profile.HubWorldItems.Find(r=>r.SpawnId=="scaled.apple");Assert.That(record,Is.Not.Null);
            var proxy=Proxy(record.InstanceId);Assert.That(proxy,Is.Not.Null);
            Assert.That(Vector3.Distance(BoundsOf(proxy.gameObject).size,expected),Is.LessThan(.002f),"Scene父Scale与单件Override必须参与视觉生成。");
            Assert.That(proxy.TryCollect("bag",new GridPlacement(3,0,0),_session.Profile.Items.Version).Success,Is.True);
            Assert.That(Drop(record.InstanceId).Success,Is.True);yield return null;
            Assert.That(Vector3.Distance(BoundsOf(Proxy(record.InstanceId).gameObject).size,expected),Is.LessThan(.002f));
        }
        [UnityTest] public IEnumerator InvalidSpawnIdentityLeavesAuthorModelVisible()
        {
            var source=new GameObject("Invalid authoring");var model=UnityEngine.Object.Instantiate(_apple.WorldModel,source.transform,false);
            var point=source.AddComponent<WorldItemSpawnPoint>();var data=new SerializedObject(point);
            data.FindProperty("_session").objectReferenceValue=_session;data.FindProperty("_definition").objectReferenceValue=_apple;
            data.FindProperty("_sceneKey").stringValue="lifecycle";data.ApplyModifiedPropertiesWithoutUndo();
            yield return null;yield return null;
            Assert.That(model.activeInHierarchy,Is.True,"校验失败不能先隐藏作者物体。");
            Assert.That(model.GetComponentInChildren<Renderer>().enabled,Is.True);
        }
        private WorldItemSpawnPoint Author(GameObject root,string id)
        {
            var point=root.AddComponent<WorldItemSpawnPoint>();var data=new SerializedObject(point);
            data.FindProperty("_session").objectReferenceValue=_session;data.FindProperty("_definition").objectReferenceValue=_apple;
            data.FindProperty("_sceneKey").stringValue="lifecycle";data.FindProperty("_spawnId").stringValue=id;
            data.ApplyModifiedPropertiesWithoutUndo();return point;
        }
        [UnityTest] public IEnumerator SceneRootRotationSurvivesCollectionAndDrop()
        {
            var author=new GameObject("Rotated author");author.transform.rotation=Quaternion.Euler(0,67,0);
            UnityEngine.Object.Instantiate(_apple.WorldModel,author.transform,false);var point=Author(author,"rotated.apple");
            yield return null;yield return null;var proxy=Proxy(point.RuntimeInstanceId);
            Assert.That(Quaternion.Angle(proxy.transform.rotation,author.transform.rotation),Is.LessThan(.001f));
            Assert.That(proxy.TryCollect("bag",new GridPlacement(3,0,0),_session.Profile.Items.Version).Success,Is.True);
            Assert.That(Drop(point.RuntimeInstanceId).Success,Is.True);yield return null;
            Assert.That(Quaternion.Angle(Proxy(point.RuntimeInstanceId).transform.rotation,author.transform.rotation),Is.LessThan(.001f),"Drop不能把场景作者的根Rotation重置成Identity。");
        }
        [UnityTest] public IEnumerator HiddenModelVariantsCannotRaiseVisibleDropAboveGround()
        {
            var author=new GameObject("Author with hidden variant");
            var visible=GameObject.CreatePrimitive(PrimitiveType.Cube);visible.name="Visible model";visible.transform.SetParent(author.transform,false);visible.transform.localScale=Vector3.one*.4f;
            var hidden=GameObject.CreatePrimitive(PrimitiveType.Cube);hidden.transform.SetParent(author.transform,false);hidden.transform.localPosition=Vector3.down*8;hidden.SetActive(false);
            var point=Author(author,"variant.apple");yield return null;yield return null;
            Assert.That(Proxy(point.RuntimeInstanceId).TryCollect("bag",new GridPlacement(3,0,0),_session.Profile.Items.Version).Success,Is.True);
            Assert.That(Drop(point.RuntimeInstanceId).Success,Is.True);yield return null;
            var renderer=Proxy(point.RuntimeInstanceId).transform.Find("Visual/Visible model").GetComponent<Renderer>();
            Assert.That(renderer.bounds.min.y,Is.EqualTo(0).Within(.025f),"隐藏的替换模型不能参与可见物体底边计算。");
        }
        [UnityTest] public IEnumerator EmptyAuthoringRootFallsBackToConfiguredPickupPrefab()
        {
            var point=Author(new GameObject("Empty spawn point"),"empty.apple");yield return null;yield return null;
            var proxy=Proxy(point.RuntimeInstanceId);Assert.That(proxy,Is.Not.Null);
            Assert.That(proxy.GetComponentsInChildren<Renderer>().Length,Is.GreaterThan(0),"只有投放点的Empty GameObject也必须生成定义中配置的可拾取模型。");
            Assert.That(proxy.TryCollect("bag",new GridPlacement(3,0,0),_session.Profile.Items.Version).Success,Is.True);
            Assert.That(Drop(point.RuntimeInstanceId).Success,Is.True);yield return null;
            Assert.That(BoundsOf(Proxy(point.RuntimeInstanceId).gameObject).min.y,Is.EqualTo(0).Within(.025f));
        }
        [UnityTearDown] public IEnumerator TearDown()
        {
            Time.timeScale=1;SceneManager.SetActiveScene(_previous);yield return SceneManager.UnloadSceneAsync(_scene);
        }
        private sealed class MemoryFiles:IProfileFileSystem
        {
            private readonly Dictionary<string,string> _files=new Dictionary<string,string>();
            public bool Exists(string p)=>_files.ContainsKey(p);public string Read(string p)=>_files[p];
            public void WriteFlushed(string p,string text)=>_files[p]=text;
            public void Move(string a,string b){_files[b]=_files[a];_files.Remove(a);}
            public void Replace(string a,string b,string backup){_files[backup]=_files[b];Move(a,b);}
        }
    }
}
#endif
