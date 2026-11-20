#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
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

    /// <summary>
    /// 只验证 Rendering 对 Core 视觉生命周期的接线，不创建账号、资产或第二套拾取逻辑。
    /// 独立的内存 Hub/副本 Scene 重现正式入局时 Hub 被挂起的边界；正式 Profile 和模型只读。
    /// 此测试程序集没有 Rendering/URP 依赖，因此通过已加载类型调用真实组件，而不增加 Runtime 反向依赖。
    /// </summary>
    public sealed class WorldItemHighlightLifecyclePlayModeTests
    {
        private const string ProfilePath = "Assets/_Project/ScriptableObjects/Rendering/Items/PickupHighlight.asset";
        private const string ApplePath = "Assets/_Project/ScriptableObjects/Items/Food/Item_Food_Apple.asset";
        private const string ContainerPath = "Assets/_Project/ScriptableObjects/Items/Containers/Item_Container_01.asset";
        private readonly List<GameObject> _ownedRoots = new List<GameObject>();
        private readonly List<ScriptableObject> _features = new List<ScriptableObject>();
        private Scene _hub, _dungeon;
        private ScriptableObject _profile;
        private Type _binderType, _highlightType, _featureType;
        private string _profileBefore;

        [SetUp]
        public void BeginRenderingLifetime()
        {
            _binderType = Type.GetType("Game.Rendering.WorldItemHighlightBinder, Game.Rendering", true);
            _highlightType = Type.GetType("Game.Rendering.WorldItemHighlight, Game.Rendering", true);
            _featureType = Type.GetType("Game.Rendering.WorldItemGlowRendererFeature, Game.Rendering", true);
            _profile = AssetDatabase.LoadAssetAtPath<ScriptableObject>(ProfilePath);
            Assert.That(_profile, Is.Not.Null);
            _profileBefore = EditorJsonUtility.ToJson(_profile);
            Assert.That((float)_profile.GetType().GetField("Strength").GetValue(_profile), Is.EqualTo(.2f),
                "回归复用用户目前的轻微发光参数，不用更亮的测试配置掩盖接线缺失。");

            // 模拟一个新 Player 会话，防止上一用例或 Editor 的 Renderer 预热留下全局 Profile。
            // 只执行该表现领域实际声明的 Unity 初始化入口；旧代码没有入口时直接继续，
            // RED 必须来自可拾取物没有轮廓，而不能来自测试要求一个尚不存在的方法。
            ResetRenderingRuntime(RuntimeInitializeLoadType.SubsystemRegistration);
            ResetRenderingRuntime(RuntimeInitializeLoadType.BeforeSceneLoad);
            _hub = SceneManager.CreateScene("HighlightLifecycleHub");
            _dungeon = SceneManager.CreateScene("HighlightLifecycleDungeon");
        }

        [UnityTest]
        public IEnumerator DisabledHubBinderStillAllowsDungeonFoodAndContainerToUseSharedProfile()
        {
            CreateFeature(_profile);
            var hubRoot = new GameObject("Suspended Hub rendering systems");
            SceneManager.MoveGameObjectToScene(hubRoot, _hub); _ownedRoots.Add(hubRoot);
            hubRoot.SetActive(false);
            var binder = hubRoot.AddComponent(_binderType);
            _binderType.GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(binder, _profile);
            hubRoot.SetActive(true);
            var hubItem = CreateModel(ApplePath, _hub);
            Publish(hubItem, true);
            Assert.That(hubItem.GetComponent(_highlightType), Is.Not.Null, "先确认原 Hub 接线实际工作。");

            // 正式 OnlineRoomClient.SuspendHub 会关闭整个根，从而触发 Binder.OnDisable 退订。
            hubRoot.SetActive(false);
            var food = CreateModel(ApplePath, _dungeon);
            var container = CreateModel(ContainerPath, _dungeon);
            Publish(food, true); Publish(container, true);
            yield return null;
            Assert.That(food.GetComponent(_highlightType), Is.Not.Null,
                "Hub 被挂起后，普通副本 Item 仍必须接入原 WorldItemHighlight。");
            Assert.That(container.GetComponent(_highlightType), Is.Not.Null,
                "容器必须与普通 Item 共用视觉生命周期，不依赖另一个副本专用系统。");
        }

        [UnityTest]
        public IEnumerator FirstVisualBindingBeforeFeaturePreparationIsReplayedWithTheExistingProfile()
        {
            var food = CreateModel(ApplePath, _dungeon);
            Publish(food, true);
            Assert.That(food.GetComponent(_highlightType), Is.Null,
                "Profile 尚未由 RendererFeature 提供时不能临时发明材质或参数。");
            CreateFeature(_profile);
            yield return null;
            Assert.That(food.GetComponent(_highlightType), Is.Not.Null,
                "首个地面快照早于 Renderer 准备时，已确认的视觉绑定不能丢失。");
        }

        [UnityTest]
        public IEnumerator RepeatedVisualBindingKeepsOneHighlightAndLeavesOriginalMaterialsUntouched()
        {
            CreateFeature(_profile);
            var food = CreateModel(ApplePath, _dungeon);
            var originals = food.GetComponentsInChildren<MeshRenderer>(true);
            var sourceMaterials = new Material[originals.Length][];
            var serializedMaterials = new string[originals.Length][];
            int sourceSlots = 0;
            for (int i = 0; i < originals.Length; i++)
            {
                sourceMaterials[i] = originals[i].sharedMaterials;
                serializedMaterials[i] = new string[sourceMaterials[i].Length];
                sourceSlots += sourceMaterials[i].Length;
                for (int j = 0; j < sourceMaterials[i].Length; j++)
                    serializedMaterials[i][j] = EditorJsonUtility.ToJson(sourceMaterials[i][j]);
            }
            Publish(food, true);
            var highlight = food.GetComponent(_highlightType);
            Assert.That(highlight, Is.Not.Null, "跨 Scene 的已确认 Item 必须先取得原高亮。");
            int rendererCount = food.GetComponentsInChildren<MeshRenderer>(true).Length;
            for (int repeat = 0; repeat < 4; repeat++) Publish(food, true);
            yield return null;
            Assert.That(food.GetComponents(_highlightType).Length, Is.EqualTo(1));
            Assert.That(food.GetComponentsInChildren<MeshRenderer>(true).Length, Is.EqualTo(rendererCount));
            Assert.That((int)_highlightType.GetProperty("MaterialCount").GetValue(highlight), Is.EqualTo(sourceSlots * 2),
                "每个原材质只增加现有 Mask/Outline 两层；重绑不得把附层再次包裹。");
            for (int i = 0; i < originals.Length; i++)
            {
                CollectionAssert.AreEqual(sourceMaterials[i], originals[i].sharedMaterials);
                for (int j = 0; j < sourceMaterials[i].Length; j++)
                    Assert.That(EditorJsonUtility.ToJson(sourceMaterials[i][j]), Is.EqualTo(serializedMaterials[i][j]));
            }
        }

        [UnityTest]
        public IEnumerator DestroyingDungeonVisualReleasesItsHighlightRegistrationAndOwnedMaterials()
        {
            CreateFeature(_profile);
            int before = (int)_highlightType.GetProperty("ActiveCount").GetValue(null);
            var food = CreateModel(ApplePath, _dungeon);
            Publish(food, true);
            Assert.That(food.GetComponent(_highlightType), Is.Not.Null);
            var layers = new List<Material>();
            foreach (var renderer in food.GetComponentsInChildren<MeshRenderer>(true))
                if (renderer.gameObject.name == "Pickup silhouette mask" || renderer.gameObject.name == "Pickup outline")
                    layers.AddRange(renderer.sharedMaterials);
            Assert.That(layers, Is.Not.Empty, "必须实际创建原轮廓材质，不能用一个空标记组件满足测试。");
            Publish(food, false);
            UnityEngine.Object.Destroy(food);
            yield return null; yield return null;
            Assert.That((int)_highlightType.GetProperty("ActiveCount").GetValue(null), Is.EqualTo(before));
            foreach (var layer in layers) Assert.That(layer == null, Is.True, "代理销毁必须释放自己创建的材质。");
        }

        [UnityTest]
        public IEnumerator RemovedBindingBeforeFeaturePreparationDoesNotTurnDecorationIntoPickup()
        {
            var removed = CreateModel(ApplePath, _dungeon);
            var decoration = CreateModel(ContainerPath, _dungeon);
            Publish(removed, true); Publish(removed, false);
            CreateFeature(_profile);
            yield return null;
            Assert.That(removed.GetComponent(_highlightType), Is.Null, "已解除绑定的排队来源必须被移除。");
            Assert.That(decoration.GetComponent(_highlightType), Is.Null, "未收到真实绑定事件的美术装饰不能被全图扫描描边。");
        }

        private void CreateFeature(ScriptableObject profile)
        {
            var feature = ScriptableObject.CreateInstance(_featureType); _features.Add(feature);
            _featureType.GetField("Profile").SetValue(feature, profile);
            _featureType.GetMethod("Create", BindingFlags.Instance | BindingFlags.Public).Invoke(feature, null);
        }

        private GameObject CreateModel(string itemPath, Scene scene)
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(itemPath);
            Assert.That(definition, Is.Not.Null); Assert.That(definition.WorldModel, Is.Not.Null);
            var root = new GameObject("Confirmed Ground Visual"); root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, scene); _ownedRoots.Add(root);
            UnityEngine.Object.Instantiate(definition.WorldModel, root.transform, false);
            root.SetActive(true);
            return root;
        }

        private static void Publish(GameObject root, bool added)
            => EventBus<WorldItemVisualBoundEvent>.Publish(new WorldItemVisualBoundEvent
                { Root = root.transform, InventoryId = "rendering-lifecycle-inventory", RegisteredCount = added ? 1 : 0, Added = added });

        private void ResetRenderingRuntime(RuntimeInitializeLoadType phase)
        {
            foreach (var type in _binderType.Assembly.GetTypes())
            {
                if (type.Namespace != "Game.Rendering" || !type.Name.StartsWith("WorldItemHighlight", StringComparison.Ordinal)) continue;
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var attribute = method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>();
                    if (attribute != null && attribute.loadType == phase) method.Invoke(null, null);
                }
            }
        }

        [UnityTearDown]
        public IEnumerator EndRenderingLifetime()
        {
            foreach (var root in _ownedRoots)
                if (root != null) { Publish(root, false); UnityEngine.Object.Destroy(root); }
            _ownedRoots.Clear();
            foreach (var feature in _features)
                if (feature != null)
                {
                    _featureType.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(feature, new object[] { true });
                    UnityEngine.Object.Destroy(feature);
                }
            _features.Clear();
            yield return null; yield return null;
            if (_dungeon.IsValid() && _dungeon.isLoaded) yield return SceneManager.UnloadSceneAsync(_dungeon);
            if (_hub.IsValid() && _hub.isLoaded) yield return SceneManager.UnloadSceneAsync(_hub);
            Assert.That(EditorJsonUtility.ToJson(_profile), Is.EqualTo(_profileBefore), "测试及Runtime表现不得修改用户的共享Profile。");
        }
    }
}
#endif
