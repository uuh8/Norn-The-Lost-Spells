#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>直接独立Scene，真实库存/材料Owner、InputSystem、Rigidbody和销毁链。</summary>
    public sealed class ContainerUsePlayModeTests
    {
        private InputTestFixture _input;
        private const string ScenePath = "Assets/_Project/Scenes/Tests/ContainerSandbox.unity";
        private const string Root = "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/";
        private Scene _scene;
        private SinglePlayerInventorySession _session;
        private MaterialCollectionController _collection;
        private ContainerUseController _use;
        private ElementWorldRuntime _world;
        private ContainerSandboxMaterialAuthoring _supply;
        private string _directory;
        private float _time;
        private CursorLockMode _lock;
        private bool _cursor;
        private Keyboard _keyboard;
        private EditorWindow _previous;
        private bool _background;
        private readonly System.Collections.Generic.List<UnityEngine.Object> _temporary = new System.Collections.Generic.List<UnityEngine.Object>();
        [UnitySetUp] public IEnumerator Setup()
        {
            // 合成输入必须与原生设备隔离，并覆盖完整Scene生命周期。
            _input=new InputTestFixture();_input.Setup();
            // 自动测试的GameView即使OS焦点在Codex也必须绘制；不能把全黑截图解释成材料Shader缺失。
            _background=Application.runInBackground;Application.runInBackground=true;
            _time = Time.timeScale; _lock = Cursor.lockState; _cursor = Cursor.visible; Time.timeScale = 1;
            _directory = Path.GetFullPath(Path.Combine("Temp/SinglePlayer/ContainerUseTests", Guid.NewGuid().ToString("N")));
            SceneManager.sceneLoaded += Loaded;
            _scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null; yield return null; yield return null; SceneManager.sceneLoaded -= Loaded;
            Assert.That(_session.ReadOnly, Is.Not.Null); Assert.That(_supply.Initialized, Is.True); Assert.That(_use, Is.Not.Null);
        }
        private T Find<T>() where T : Component
        { foreach (var root in _scene.GetRootGameObjects()) { var component = root.GetComponentInChildren<T>(true); if (component != null) return component; } return null; }
        private void Loaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != ScenePath) return; _scene = scene;
            _session = Find<SinglePlayerInventorySession>(); _collection = Find<MaterialCollectionController>(); _use = Find<ContainerUseController>();
            _world = Find<ElementWorldRuntime>(); _supply = Find<ContainerSandboxMaterialAuthoring>();
            // 小型Sandbox验证守恒/碰撞，不是力度验收。补真实模型并限制测试投程，生产24m/s由UnifiedItems测试独立证明。
            var catalog = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/Catalog.asset")); _temporary.Add(catalog);
            var catalogData = new SerializedObject(catalog); var definitions = catalogData.FindProperty("_definitions");
            var bottleModel = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Containers/Item_Container_01.asset").WorldModel;
            for (int i = 0; i < definitions.arraySize; i++)
            {
                var source = (ItemDefinition)definitions.GetArrayElementAtIndex(i).objectReferenceValue; if (source.Kind != ItemKind.Container) continue;
                var copy = UnityEngine.Object.Instantiate(source); _temporary.Add(copy); var data = new SerializedObject(copy); data.FindProperty("_worldModel").objectReferenceValue = bottleModel; data.ApplyModifiedPropertiesWithoutUndo();
                definitions.GetArrayElementAtIndex(i).objectReferenceValue = copy;
            }
            catalogData.ApplyModifiedPropertiesWithoutUndo();
            var useData = new SerializedObject(_use); var configuration = UnityEngine.Object.Instantiate((ContainerUseDefinition)useData.FindProperty("_definition").objectReferenceValue); _temporary.Add(configuration);
            var parameters = new SerializedObject(configuration); parameters.FindProperty("_throwSpeed").floatValue = 1.5f; parameters.FindProperty("_throwLift").floatValue = 0; parameters.ApplyModifiedPropertiesWithoutUndo();
            useData.FindProperty("_definition").objectReferenceValue = configuration; useData.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(_session.ConfigureAndInitialize(catalog,
                AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/StartingInventory.asset"), _directory), Is.True);
        }
        private void Fill(ulong budget = 30)
        {
            _collection.transform.position = _supply.MixedSamplePosition + new Vector3(0, .5f, -.5f);
            _collection.SubmitSample(_supply.MixedSamplePosition, false, budget);
            Assert.That(_collection.LastFailure, Is.EqualTo(MaterialTransferFailure.None));
        }
        private ulong Total(string item)
        { _session.TryCopyContainer(item, new MaterialAmount[8], out _, out ulong total, out _); return total; }
        private void AimDown()
        {
            var camera = Find<Camera>(); var brain = camera.GetComponent("CinemachineBrain") as Behaviour; if (brain != null) brain.enabled = false;
            camera.transform.rotation = Quaternion.Euler(90, 0, 0);
        }
        [UnityTest] public IEnumerator EquippedBottleCollectsActualMixedWorldWithoutTakingBagCells()
        {
            Fill(); string id = _collection.SelectedContainerId;
            EventBus<ToolbeltBindingRequestedEvent>.Publish(new ToolbeltBindingRequestedEvent
            { InventoryId = _session.ReadOnly.InventoryId, ToolSlot = 0, ItemId = id, ExpectedVersion = _session.ToolbeltReadOnly.ConfigurationVersion });
            Assert.That(_session.ReadOnly.TryGetView(id, out var item), Is.True);
            Assert.That(item.Location.Kind, Is.EqualTo(ItemLocationKind.EquippedTool));
            Fill(); Assert.That(Total(id), Is.EqualTo(60));
            _use.SubmitUse(ContainerUseKind.Pour, 15, new Vector3(1.25f, .25f, .25f));
            Assert.That(_use.LastResult.Success, Is.True); Assert.That(Total(id), Is.EqualTo(45));
            yield return null;
        }
        [UnityTest] public IEnumerator PourWritesActualMixedWorldQuantityAndFamilyBalances()
        {
            Fill(); string item = _collection.SelectedContainerId; var point = new Vector3(1.25f, .25f, .25f);
            _use.SubmitUse(ContainerUseKind.Pour, 15, point); Assert.That(_use.LastResult.Success, Is.True); Assert.That(Total(item), Is.EqualTo(15));
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(10)); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Poison), Is.EqualTo(5));
            Assert.That(_world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased, out var water), Is.True); Assert.That(water.IsBalanced, Is.True);
            Assert.That(_world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.Toxic, out var toxic), Is.True); Assert.That(toxic.IsBalanced, Is.True);
            Assert.That(_session.Save().Success, Is.True); yield return null;
        }
        [UnityTest] public IEnumerator HeldPourIsRateBoundedAndReleaseOrPauseStops()
        {
            Fill(150); AimDown(); string item = _collection.SelectedContainerId; _keyboard = InputSystem.AddDevice<Keyboard>();
            float start = Time.time; InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.H)); InputSystem.Update();
            yield return new WaitForSeconds(.26f);
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update(); yield return null;
            float elapsed = Time.time - start; ulong after = Total(item);
            Assert.That(after, Is.LessThan(150)); Assert.That(150 - after, Is.LessThanOrEqualTo((ulong)Mathf.Ceil(elapsed * 100) + 1));
            yield return new WaitForSeconds(.2f); Assert.That(Total(item), Is.EqualTo(after));
            Time.timeScale = 0; InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.H)); InputSystem.Update();
            yield return new WaitForSecondsRealtime(.2f); Assert.That(Total(item), Is.EqualTo(after));
        }
        [UnityTest] public IEnumerator RealRigidbodyCollisionBreaksBottleAndDepositsOnlyOnce()
        {
            Fill(); AimDown(); string item = _collection.SelectedContainerId;
            _use.SubmitUse(ContainerUseKind.Throw, 0, _collection.transform.position + Vector3.forward);
            string flight = _use.LastResult.FlightId; Assert.That(_use.LastResult.Success, Is.True);
            Assert.That(_session.ReadOnly.TryGetView(item, out var view), Is.True); Assert.That(view.Location.Kind, Is.EqualTo(ItemLocationKind.ThrownContainer));
            Assert.That(_session.Save().Success, Is.False);
            float deadline = Time.realtimeSinceStartup + 3;
            while (_session.PendingContainerFlights > 0 && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(_session.PendingContainerFlights, Is.Zero); Assert.That(_session.ReadOnly.TryGetView(item, out _), Is.False);
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(20)); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Poison), Is.EqualTo(10));
            EventBus<ContainerImpactEvent>.Publish(new ContainerImpactEvent { InventoryId = _session.ReadOnly.InventoryId, WorldId = "container-world", FlightId = flight });
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(20)); yield return null;
        }
        [UnityTest] public IEnumerator RejectedWorldKeepsVisibleWaitingOwnerAndResumesAfterRecovery()
        {
            Fill(); AimDown(); string item = _collection.SelectedContainerId; var adapter = Find<ContainerDepositAdapter>(); adapter.Configure(_world, "unavailable");
            _use.SubmitUse(ContainerUseKind.Throw, 0, _collection.transform.position + Vector3.forward);
            yield return new WaitForSeconds(1); var projectile = Find<ThrownContainerProjectile>(); Assert.That(projectile, Is.Not.Null);
            Assert.That(projectile.IsWaiting, Is.True); Assert.That(projectile.transform.Find("ThrownContainerModel").GetComponentInChildren<Renderer>().enabled, Is.True);
            Assert.That(Total(item), Is.EqualTo(30)); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.Zero);
            adapter.Configure(_world, "container-world"); yield return new WaitForSeconds(.5f);
            Assert.That(_session.PendingContainerFlights, Is.Zero); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(20));
        }
        [UnityTest] public IEnumerator SolidPartialAndFullReleaseRestoresTrueGeometryAndRemovesInventory()
        {
            _collection.transform.position = _supply.SolidSamplePosition + new Vector3(0, .5f, -.5f);
            _collection.SubmitSample(_supply.SolidSamplePosition, true, 200); Assert.That(_collection.LastFailure, Is.EqualTo(MaterialTransferFailure.None));
            var point = new Vector3(.25f, .25f, 1.25f); _use.SubmitUse(ContainerUseKind.ReleaseSolid, 100, point); Assert.That(_use.LastResult.Success, Is.True);
            var items = new InventoryItemSnapshot[_session.ReadOnly.Count]; int count = _session.ReadOnly.CopyItemsTo(items); string id = null;
            for (int i = 0; i < count; i++) if (items[i].Kind == ItemKind.Material) { id = items[i].InstanceId; Assert.That(items[i].MaterialQuantity, Is.EqualTo(100)); }
            Assert.That(id, Is.Not.Null); var cell = Vector3Int.FloorToInt((point - _world.Origin) / _world.CellSize);
            Assert.That(_world.FixedSolids.TryGetQuantity(cell, MaterialId.Ice, out uint quantity), Is.True); Assert.That(quantity, Is.EqualTo(100));
            _use.SubmitUse(ContainerUseKind.ReleaseSolid, 1000, point); Assert.That(_use.LastResult.Success, Is.True);
            Assert.That(_session.ReadOnly.TryGetView(id, out _), Is.False); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Ice), Is.EqualTo(200)); yield return null;
        }
        [UnityTest] public IEnumerator FullRealWorldCapacityKeepsFlightUntilCollectionCreatesSpace()
        {
            Fill(); AimDown(); string item = _collection.SelectedContainerId;
            var adapter = Find<ContainerDepositAdapter>(); adapter.Configure(_world, "unavailable");
            _use.SubmitUse(ContainerUseKind.Throw, 0, _collection.transform.position + Vector3.forward);
            yield return new WaitForSeconds(1); var projectile = Find<ThrownContainerProjectile>(); Assert.That(projectile.IsWaiting, Is.True);
            Vector3 point = projectile.transform.position; var liquid = _world.GetComponent<LiquidWorldRuntime>();
            Vector3Int cell = Vector3Int.FloorToInt((point - liquid.Origin) / liquid.CellSize);
            // 新投掷落点可能位于格边，必须填满整个投放球涉及的邻格；暂停Solver，防止预填水先流走。
            _world.SetSimulationPausedForDebug(true); int filled = 0;
            for (int z=-1;z<=1;z++) for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++)
            {
                var target=cell+new Vector3Int(x,y,z); liquid.TryGetQuantity(target,MaterialId.Water,out uint water);liquid.TryGetQuantity(target,MaterialId.Poison,out uint poison);
                uint free=(uint)liquid.CellCapacityGmu-water-poison;if(free>0&&liquid.TryAddInitial(target,MaterialId.Water,free))filled++;
            }
            Assert.That(filled,Is.GreaterThan(0));
            adapter.Configure(_world, "container-world"); yield return new WaitForSeconds(.3f);
            Assert.That(_session.PendingContainerFlights, Is.EqualTo(1)); Assert.That(Total(item), Is.EqualTo(30));
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.Zero, "真实容量满时不能继续扣空。");
            // 在途瓶子不能拿回来采集。另建一个实际携带的空瓶腾出世界容量，验证两个所有者的独立事务。
            Assert.That(_session.ReadOnly.TryGetView(item,out var thrown),Is.True);
            var spare=new RuntimeItemState("capacity.collector",thrown.ContentId,0,composition:new ContainerComposition(thrown.ContainerCapacity,thrown.Rules.ContainerMaterials));
            Assert.That(_session.Profile.Items.TryAdd(spare,new GridPlacement(1,0,0).InBackpack(_session.EquippedBackpackId)),Is.True);
            EventBus<ContainerSelectionRequestedEvent>.Publish(new ContainerSelectionRequestedEvent{InventoryId=_session.ReadOnly.InventoryId,ItemId=spare.InstanceId});
            _collection.SubmitSample(point, false, 30); Assert.That(_collection.LastFailure, Is.EqualTo(MaterialTransferFailure.None));
            yield return new WaitForSeconds(.4f); Assert.That(_session.PendingContainerFlights, Is.Zero);
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(20));
            Assert.That(_world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased, out var family), Is.True); Assert.That(family.IsBalanced, Is.True);
        }
        [UnityTest] public IEnumerator SceneUnloadEndsPendingOwnershipOnceWithoutRefund()
        {
            Fill(); AimDown(); var adapter = Find<ContainerDepositAdapter>(); adapter.Configure(_world, "unavailable");
            string inventory = _session.ReadOnly.InventoryId; int ends = 0; ulong ended = 0;
            Action<ContainerTransitResolvedEvent> handler = result => { if (result.InventoryId == inventory) { ends++; ended += result.Quantity; } };
            EventBus<ContainerTransitResolvedEvent>.Subscribe(handler);
            try
            { _use.SubmitUse(ContainerUseKind.Throw, 0, _collection.transform.position + Vector3.forward); yield return new WaitForSeconds(.5f);
                Assert.That(_session.PendingContainerFlights, Is.EqualTo(1)); yield return SceneManager.UnloadSceneAsync(_scene); yield return null;
                Assert.That(ends, Is.EqualTo(1)); Assert.That(ended, Is.EqualTo(30)); }
            finally { EventBus<ContainerTransitResolvedEvent>.Unsubscribe(handler); }
        }
        [UnityTest] public IEnumerator ReleasedMixedLiquidAndSolidAppearInActualGameView()
        {
            Fill(); var poured = new Vector3(1.25f, .25f, .25f);
            _use.SubmitUse(ContainerUseKind.Pour, 15, poured); Assert.That(_use.LastResult.Success, Is.True);
            _collection.transform.position = _supply.SolidSamplePosition + new Vector3(0, .5f, -.5f);
            _collection.SubmitSample(_supply.SolidSamplePosition, true, 200);
            var solid = new Vector3(.25f, .25f, 1.25f); _use.SubmitUse(ContainerUseKind.ReleaseSolid, 100, solid); Assert.That(_use.LastResult.Success, Is.True);
            _collection.transform.position = _supply.GasSamplePosition + new Vector3(0, .5f, -.5f);
            _collection.SubmitSample(_supply.GasSamplePosition, false, 10);
            _use.SubmitUse(ContainerUseKind.Pour, 25, new Vector3(1.25f, .75f, 1.25f)); Assert.That(_use.LastResult.Success, Is.True);
            var camera = Find<Camera>(); var brain = camera.GetComponent("CinemachineBrain") as Behaviour; if (brain != null) brain.enabled = false;
            _collection.transform.position = new Vector3(0, .1f, -2.5f); camera.fieldOfView = 75;
            camera.transform.position = new Vector3(0, 6, -4); camera.transform.rotation = Quaternion.LookRotation(new Vector3(.25f, 0, .25f) - camera.transform.position);
            _previous = EditorWindow.focusedWindow; EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            yield return new WaitForSecondsRealtime(.4f);
            yield return new WaitForEndOfFrame();
            DateTime capture = DateTime.UtcNow; const string output = "Temp/SinglePlayer/container-use-sandbox.png";
            // 在当前EndOfFrame同步ReadPixels，不能把异步截图落在TestRunner卸载Scene后的黑帧。
            var frame = new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
            try{frame.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);frame.Apply();File.WriteAllBytes(output,frame.EncodeToPNG());}
            finally{UnityEngine.Object.Destroy(frame);}
            Assert.That(File.Exists(output) && File.GetLastWriteTimeUtc(output) >= capture, Is.True, "必须读取本次GameView，不能借旧截图。");
            var texture = new Texture2D(2, 2);
            try
            {
                Assert.That(ImageConversion.LoadImage(texture, File.ReadAllBytes(output)), Is.True);
                Assert.That(HasColor(texture, camera, poured, false), Is.True, "撒出的Poison实际像素未出现。");
                Assert.That(HasColor(texture, camera, solid, true), Is.True, "回投的Ice实际像素未出现。");
            }
            finally { UnityEngine.Object.Destroy(texture); }
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Steam), Is.EqualTo(10));
        }
        [UnityTest] public IEnumerator ExternalProxyDestructionReportsEndedOwnershipAndNeverRefundsBottle()
        {
            Fill(); AimDown(); string item = _collection.SelectedContainerId;
            Find<ContainerDepositAdapter>().Configure(_world, "unavailable");
            _use.SubmitUse(ContainerUseKind.Throw, 0, _collection.transform.position + Vector3.forward);
            yield return new WaitForSeconds(1); var projectile = Find<ThrownContainerProjectile>();
            Assert.That(projectile.IsWaiting, Is.True); UnityEngine.Object.Destroy(projectile.gameObject); yield return null; yield return null;
            Assert.That(_session.PendingContainerFlights, Is.Zero); Assert.That(_session.ReadOnly.TryGetView(item, out _), Is.False);
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.Zero);
            var view = Find<Game.UI.ContainerCompositionView>(); var status = view.transform.Find("Status").GetComponent<UnityEngine.UI.Text>();
            Assert.That(status.text, Does.Contain("归属已结束"));
        }
        private static bool HasColor(Texture2D texture, Camera camera, Vector3 point, bool ice)
        {
            Vector3 viewport = camera.WorldToViewportPoint(point); int cx = Mathf.RoundToInt(viewport.x * texture.width), cy = Mathf.RoundToInt(viewport.y * texture.height);
            for (int y = Mathf.Max(0, cy - 28); y <= Mathf.Min(texture.height - 1, cy + 28); y++)
            for (int x = Mathf.Max(0, cx - 28); x <= Mathf.Min(texture.width - 1, cx + 28); x++)
            { Color color = texture.GetPixel(x, y); if (ice ? color.g > color.r + .1f && color.b > color.r + .1f : color.g > color.r + .1f && color.g > color.b + .1f) return true; }
            return false;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            try
            {
            SceneManager.sceneLoaded -= Loaded;
            if (_keyboard != null) { InputSystem.RemoveDevice(_keyboard); _keyboard = null; }
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            yield return null; Time.timeScale = _time; Cursor.lockState = _lock; Cursor.visible = _cursor;
            Application.runInBackground=_background;
            if (_previous != null) _previous.Focus();
            string root = Path.GetFullPath("Temp/SinglePlayer/ContainerUseTests") + Path.DirectorySeparatorChar;
            if (_directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_directory)) Directory.Delete(_directory, true);
            }
            finally {foreach(var asset in _temporary) if(asset!=null) UnityEngine.Object.Destroy(asset);_temporary.Clear();_input?.TearDown();_input=null;}
        }
    }
}
#endif
