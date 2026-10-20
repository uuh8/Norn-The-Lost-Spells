#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    /// <summary>直接载入交付 Scene，走真实 UI 回调/InputAction/会话；每例隔离存档，不接触正式资产。</summary>
    public sealed class SinglePlayerInventoryUiTests
    {
        private const string ScenePath = "Assets/_Project/Scenes/Tests/InventorySandbox.unity";
        private const string DataPath = "Assets/_Project/Tests/Fixtures/Run/SinglePlayer/";
        private Scene _scene;
        private string _directory;
        private SinglePlayerInventorySession _session;
        private SinglePlayerInventoryController _ui;
        private InventoryGridView _grid;
        private RunPauseCoordinator _pause;
        private ItemCatalog _catalog;
        private Keyboard _keyboard;
        private float _oldTime;
        private CursorLockMode _oldLock;
        private bool _oldCursor;
        private bool _configured;
        private EditorWindow _previousWindow;
        private bool _unrecognizedProfile;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // 每例建立真实键鼠输入前提，并在TearDown恢复原窗口，不修改项目Input设置。
            _previousWindow = EditorWindow.focusedWindow;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            _oldTime = Time.timeScale; _oldLock = Cursor.lockState; _oldCursor = Cursor.visible;
            _directory = Path.GetFullPath(Path.Combine("Temp/SinglePlayer/UiTests", Guid.NewGuid().ToString("N")));
            _unrecognizedProfile = TestContext.CurrentContext.Test.Name == nameof(UnrecognizedProfileShowsFailureAndCannotSaveNewAssets);
            if (_unrecognizedProfile)
            { Directory.CreateDirectory(_directory); File.WriteAllText(Path.Combine(_directory, "profile.json"), "{\"SchemaVersion\":999}"); }
            _catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Tests/Fixtures/Run/SinglePlayer/Catalog.asset");
            _configured = false;
            SceneManager.sceneLoaded += OnSceneLoaded;
            _scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            // LoadSceneInPlayMode 的对象在下一帧完成加载；sceneLoaded 位于 OnEnable 与 Start 之间。
            yield return null; yield return null; yield return null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Assert.That(_configured, Is.EqualTo(!_unrecognizedProfile), "sceneLoaded 注入隔离 Profile 状态不符。");
            Assert.That(_ui.IsOpen, Is.True);
            if (!_unrecognizedProfile) Assert.That(_session.ReadOnly.Count, Is.EqualTo(13));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_keyboard != null) { InputSystem.RemoveDevice(_keyboard); _keyboard = null; }
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_ui != null) _ui.Close();
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            Time.timeScale = _oldTime; Cursor.lockState = _oldLock; Cursor.visible = _oldCursor;
            if (_previousWindow != null) _previousWindow.Focus();
            string root = Path.GetFullPath("Temp/SinglePlayer/UiTests") + Path.DirectorySeparatorChar;
            if (_directory != null && _directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.path != ScenePath) return;
            _scene = scene;
            _session = Find<SinglePlayerInventorySession>(); _ui = Find<SinglePlayerInventoryController>();
            _grid = Find<InventoryGridView>(); _pause = Find<RunPauseCoordinator>();
            var seeds = AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/Tests/Fixtures/Run/SinglePlayer/StartingInventory.asset");
            _configured = _session.ConfigureAndInitialize(_catalog, seeds, _directory);
        }

        [UnityTest]
        public IEnumerator DirectSceneStartsWithReadableAssetsAndOwnedPause()
        {
            Assert.That(_pause.HasReason(RunPauseReason.Inventory), Is.True);
            Assert.That(Time.timeScale, Is.Zero); Assert.That(_ui.UiFont, Is.Not.Null);
            Assert.That(_grid.BackpackId, Is.EqualTo(Item("hub.bag").InstanceId));
            Assert.That(_session.HasUnsavedChanges, Is.True);
            _ui.Close(); Assert.That(Time.timeScale, Is.EqualTo(1));
            yield return null;
        }

        [UnityTest]
        public IEnumerator UnrecognizedProfileShowsFailureAndCannotSaveNewAssets()
        {
            Assert.That(_session.ReadOnly, Is.Null);
            Assert.That(_session.InitializationMessage, Is.Not.Empty);
            Assert.That(_session.Save().Success, Is.False);
            _ui.Save(); yield return null;
            Assert.That(File.ReadAllText(Path.Combine(_directory, "profile.json")), Is.EqualTo("{\"SchemaVersion\":999}"));
            Assert.That(_pause.HasReason(RunPauseReason.Inventory), Is.True);
        }

        [UnityTest]
        public IEnumerator RotationCancelOutOfBoundsAndStaleDropNeverMoveSource()
        {
            InventoryItemSnapshot bar = Item("hub.bar"); uint version = _session.ReadOnly.Version;
            Begin(bar); _keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.R)); InputSystem.Update();
            Assert.That(Field<int>(_ui, "_turns"), Is.EqualTo(1));
            AssertLocation(bar.InstanceId, bar.Location); Assert.That(_session.ReadOnly.Version, Is.EqualTo(version));
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.Escape)); InputSystem.Update();
            Assert.That(_ui.IsDragging, Is.False); Assert.That(_ui.IsOpen, Is.True);
            Begin(bar); _ui.SubmitToBackpack(3, 0, _grid.BackpackId);
            AssertLocation(bar.InstanceId, bar.Location); Assert.That(_session.ReadOnly.Version, Is.EqualTo(version));
            Begin(bar);
            InventoryItemSnapshot potion = Item("hub.potion");
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent { InventoryId = _session.ReadOnly.InventoryId,
                Kind = InventoryCommandKind.Place, ItemId = potion.InstanceId, OwnerId = _grid.BackpackId,
                X = 3, Y = 3, ExpectedVersion = version });
            uint changed = _session.ReadOnly.Version;
            _ui.SubmitToBackpack(0, 0, _grid.BackpackId);
            AssertLocation(bar.InstanceId, bar.Location); Assert.That(_session.ReadOnly.Version, Is.EqualTo(changed));
            yield return null;
        }

        [UnityTest]
        public IEnumerator PointerDropUsesRealGridCellsAndLShapeHole()
        {
            InventoryItemSnapshot corner = Item("hub.corner");
            Begin(corner);
            var data = new PointerEventData(Find<EventSystem>()) { position = GridPoint(0, 0) };
            ExecuteEvents.Execute(_grid.gameObject, data, ExecuteEvents.dropHandler);
            AssertLocation(corner.InstanceId, new GridPlacement(0, 0, 0).InBackpack(_grid.BackpackId));
            InventoryItemView view = View(corner.InstanceId);
            Assert.That(view.IsRaycastLocationValid(GridPoint(1, 0), null), Is.False, "L 的右上空洞应放行 raycast。");
            Assert.That(view.IsRaycastLocationValid(GridPoint(0, 0), null), Is.True);
            Begin(Item("hub.potion")); data.position = GridPoint(1, 0);
            ExecuteEvents.Execute(_grid.gameObject, data, ExecuteEvents.dropHandler);
            Assert.That(Item("hub.potion").Location.X, Is.EqualTo(1));
            InventoryItemSnapshot bar = Item("hub.bar"); uint before = _session.ReadOnly.Version;
            Begin(bar); _ui.SubmitToBackpack(0, 1, _grid.BackpackId);
            AssertLocation(bar.InstanceId, bar.Location); Assert.That(_session.ReadOnly.Version, Is.EqualTo(before));
            yield return null;
        }

        [UnityTest]
        public IEnumerator StackDragKeepsMembersAndShiftMovesSelectedCopy()
        {
            InventoryItemSnapshot[] water = Items("hub.water");
            Move(water[0], 0, 0); Move(water[1], 0, 0);
            Assert.That(_session.ReadOnly.Count, Is.EqualTo(13));
            string label = View(water[0].InstanceId).GetComponentInChildren<Text>().text;
            // 法术卡只标当前实例有限次数；堆叠成员仍由后续真实拆分/整组移动断言验证。
            Assert.That(label, Is.EqualTo("3"));
            _ui.SelectItem(water[1].InstanceId);
            _keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.LeftShift)); InputSystem.Update();
            Begin(Current(water[0].InstanceId)); _ui.SubmitToBackpack(1, 0, _grid.BackpackId);
            Assert.That(Current(water[0].InstanceId).Location.X, Is.Zero);
            Assert.That(Current(water[1].InstanceId).Location.X, Is.EqualTo(1));
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update();
            Move(Current(water[1].InstanceId), 0, 0);
            uint version = _session.ReadOnly.Version;
            Begin(Current(water[0].InstanceId)); _ui.SubmitToTarget(InventoryDropKind.Warehouse);
            Assert.That(_session.ReadOnly.Version, Is.EqualTo(version + 1));
            Assert.That(Current(water[0].InstanceId).Location.Kind, Is.EqualTo(ItemLocationKind.Warehouse));
            Assert.That(Current(water[1].InstanceId).Location.Kind, Is.EqualTo(ItemLocationKind.Warehouse));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ProgramEquipmentAndBackpackUseIndependentLimits()
        {
            InventoryItemSnapshot wand = Item("hub.wand"), water = Item("hub.water");
            Begin(water); _ui.SubmitToTarget(InventoryDropKind.ProgramSlot, wand.InstanceId, 0);
            Assert.That(Current(water.InstanceId).Location.Kind, Is.EqualTo(ItemLocationKind.WandSlot));
            InventoryItemSnapshot second = Items("hub.water")[1]; uint version = _session.ReadOnly.Version;
            Begin(second); _ui.SubmitToTarget(InventoryDropKind.ProgramSlot, wand.InstanceId, wand.Rules.WandProgramCapacity);
            AssertLocation(second.InstanceId, second.Location); Assert.That(_session.ReadOnly.Version, Is.EqualTo(version));
            InventoryItemSnapshot[] wands = Items("hub.wand");
            Begin(wands[2]); _ui.SubmitToTarget(InventoryDropKind.EquippedWand, _session.ReadOnly.ActorId, 2);
            Assert.That(Current(wands[2].InstanceId).Location.SlotIndex, Is.EqualTo(2));
            Move(Item("hub.potion"), 3, 3);
            InventoryItemSnapshot smallBag = Item("hub.smallbag"); version = _session.ReadOnly.Version;
            Begin(smallBag); _ui.SubmitToTarget(InventoryDropKind.EquippedBackpack);
            AssertLocation(smallBag.InstanceId, smallBag.Location); Assert.That(_session.ReadOnly.Version, Is.EqualTo(version));
            Assert.That(Current(water.InstanceId).Location.OwnerId, Is.EqualTo(wand.InstanceId));
            yield return null;
        }

        [UnityTest]
        public IEnumerator DropPickupAndSaveReloadKeepSameInstance()
        {
            // InventorySandbox是纯UI空场景。新的普通Drop需要真实支撑，测试只补独立Collider，不放松生产校验。
            var ground=GameObject.CreatePrimitive(PrimitiveType.Cube);ground.name="UI test drop support";
            SceneManager.MoveGameObjectToScene(ground,_scene);ground.transform.position=new Vector3(0,-.25f,0);
            ground.transform.localScale=new Vector3(40,.5f,40);Physics.SyncTransforms();
            InventoryItemSnapshot potion = Item("hub.potion");
            Begin(potion); _ui.SubmitToTarget(InventoryDropKind.Ground);
            Assert.That(Current(potion.InstanceId).Location.Kind, Is.EqualTo(ItemLocationKind.WorldItem));
            Assert.That(Find<WorldItemPickup>(), Is.Not.Null);
            Begin(Current(potion.InstanceId)); _ui.SubmitToBackpack(2, 2, _grid.BackpackId);
            Assert.That(Current(potion.InstanceId).Location.Kind, Is.EqualTo(ItemLocationKind.Backpack));
            _ui.Save(); Assert.That(_session.HasUnsavedChanges, Is.False);
            Assert.That(File.Exists(Path.Combine(_directory, "profile.json")), Is.True);
            var root = new GameObject("Reloaded isolated profile"); SceneManager.MoveGameObjectToScene(root, _scene);
            var reload = root.AddComponent<SinglePlayerInventorySession>();
            Assert.That(reload.ConfigureAndInitialize(_catalog, null, _directory), Is.True);
            Assert.That(reload.ReadOnly.TryGetView(potion.InstanceId, out InventoryItemSnapshot restored), Is.True);
            Assert.That(restored.Location, Is.EqualTo(Current(potion.InstanceId).Location));
            Assert.That(reload.ReadOnly.Count, Is.EqualTo(13)); Assert.That(reload.SavedGeneration, Is.EqualTo(1));
            UnityEngine.Object.Destroy(root); yield return null;
        }

        [UnityTest]
        public IEnumerator ClosingInventoryPreservesOtherPauseAndCaptureActualUi()
        {
            InventoryItemSnapshot[] water = Items("hub.water"); Move(water[0], 0, 0); Move(water[1], 0, 0);
            Move(Item("hub.corner"), 1, 1); Move(Item("hub.bar"), 0, 3);
            _ui.SelectItem(Item("hub.wand").InstanceId); Begin(water[2]);
            _ui.SubmitToTarget(InventoryDropKind.ProgramSlot, Item("hub.wand").InstanceId, 0);
            Canvas.ForceUpdateCanvases();
            // Test Runner 可停留在无 GameView 渲染的页签；先请求真实 GameView 重绘再截帧。
            // 结束恢复原页签，不修改 Canvas 的生产渲染模式或保存 Scene。
            Type gameView = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameView != null) EditorWindow.GetWindow(gameView).Focus();
            yield return null; yield return new WaitForSecondsRealtime(.3f);
            ScreenCapture.CaptureScreenshot("Temp/SinglePlayer/inventory-sandbox.png");
            yield return null; yield return new WaitForSecondsRealtime(.3f);
            _pause.RequestPause(RunPauseReason.PauseMenu); _ui.Close();
            Assert.That(_pause.HasReason(RunPauseReason.Inventory), Is.False);
            Assert.That(_pause.HasReason(RunPauseReason.PauseMenu), Is.True); Assert.That(Time.timeScale, Is.Zero);
            _pause.ReleasePause(RunPauseReason.PauseMenu); Assert.That(Time.timeScale, Is.EqualTo(1));
        }

        private void Begin(in InventoryItemSnapshot item)
        {
            InventoryItemView view = View(item.InstanceId);
            var data = new PointerEventData(Find<EventSystem>()) { button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(null, view.Rect.TransformPoint(view.Rect.rect.center)) };
            ExecuteEvents.Execute(view.gameObject, data, ExecuteEvents.beginDragHandler);
            Assert.That(_ui.IsDragging, Is.True);
        }
        private void Move(in InventoryItemSnapshot item, int x, int y)
        {
            // 格子视图抓取点可能在物品中间；此 helper 明确从左上格抓取。
            _ui.BeginDrag(item, Vector2.zero, null, false, Vector2Int.zero);
            _ui.SubmitToBackpack(x, y, _grid.BackpackId);
        }
        private Vector2 GridPoint(int x, int y)
        {
            var rect = (RectTransform)_grid.transform;
            return RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(new Vector3(
                rect.rect.xMin + (x + .5f) * _grid.CellSize, rect.rect.yMax - (y + .5f) * _grid.CellSize, 0)));
        }
        private InventoryItemSnapshot Current(string id)
        { Assert.That(_session.ReadOnly.TryGetView(id, out InventoryItemSnapshot item), Is.True); return item; }
        private void AssertLocation(string id, ItemLocation expected) => Assert.That(Current(id).Location, Is.EqualTo(expected));
        private InventoryItemSnapshot Item(string content) => Items(content)[0];
        private InventoryItemSnapshot[] Items(string content)
        {
            var buffer = new InventoryItemSnapshot[_session.ReadOnly.Count]; _session.ReadOnly.CopyItemsTo(buffer);
            return Array.FindAll(buffer, item => item.ContentId == content);
        }
        private InventoryItemView View(string id)
        {
            foreach (GameObject root in _scene.GetRootGameObjects())
                foreach (InventoryItemView view in root.GetComponentsInChildren<InventoryItemView>(true))
                    if (view.gameObject.activeInHierarchy && view.Snapshot.InstanceId == id) return view;
            throw new AssertionException("没有活跃的物品 View：" + id);
        }
        private T Find<T>() where T : Component
        {
            foreach (GameObject root in _scene.GetRootGameObjects())
            { T value = root.GetComponentInChildren<T>(true); if (value != null) return value; }
            throw new AssertionException("Scene 缺少 " + typeof(T).Name);
        }
        private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
    }
}
#endif
