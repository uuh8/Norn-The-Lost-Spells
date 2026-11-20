#if UNITY_EDITOR
using System.Collections;
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 验证实际导出的原Tab在PlayMode中的输入/Scene寿命。使用内存Authority导出当前值，
    /// 不读取单机存档，不建立账号或假装网络验收；真实DS到Client的Wire由独立Native验收覆盖。
    /// </summary>
    public sealed partial class OnlineSharedInventoryPlayModeTests
    {
        private Scene _scene;
        private CombatPresentationScope _scope;
        private CombatPresentationHost _host, _initialHost;
        private OnlineOwnedWorkspace _authority;
        private OnlineInventoryPresentationSource _source;
        private InputTestFixture _input;
        private Keyboard _keyboard;
        private bool _background;
        private float _scale;
        private CursorLockMode _cursorMode;
        private bool _cursorVisible;
        private readonly Dictionary<InputActionAsset, InputActionAsset> _inputAssets = new Dictionary<InputActionAsset, InputActionAsset>();
        private readonly List<InputActionReference> _inputReferences = new List<InputActionReference>();
        private readonly List<InputAction> _enabledProjectActions = new List<InputAction>();
        private object _originalInputManager;
        private PropertyInfo _managerActionsProperty;
        private InputActionAsset _originalManagerActions, _projectActions;
        private bool _projectActionsCaptured;

        [UnityTest]
        public IEnumerator FormalCombatInventoryRaycastsAndOriginalHandlersSubmitSevenOwnedActions()
        {
            BeginIsolatedInput();
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(definition.Catalog.TryCreateIndex(out var catalog, out var error), Is.True, error);
            const string wand = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", card = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string bag = "cccccccccccccccccccccccccccccccc", bagBottle = "dddddddddddddddddddddddddddddddd";
            const string toolBottle = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            var assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
                ActorId = 1, RunId = "formal-drag-run", PlayerId = "formal-drag-player", InventoryId = "formal-drag-inventory",
                InventoryVersion = 1, CurrencyVersion = 1, GameplayConfigHash = definition.GameplayConfigHash, SelectedWandInstanceId = wand,
                Items = new[] {
                    new OnlineOwnedItemSnapshot { InstanceId = wand, ContentId = "wandtest.wand.a", Revision = 1, LocationKind = 3, UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = card, ContentId = "wandtest.fire", Revision = 1, LocationKind = 2, ParentInstanceId = wand, RemainingCharges = 30 },
                    new OnlineOwnedItemSnapshot { InstanceId = bag, ContentId = "wandtest.bag", Revision = 1, LocationKind = 4 },
                    new OnlineOwnedItemSnapshot { InstanceId = bagBottle, ContentId = "containertest.bottle", Revision = 1,
                        LocationKind = 1, ParentInstanceId = bag, Slot = -1,
                        ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = Array.Empty<OnlineOwnedMaterialAmountSnapshot>() } },
                    new OnlineOwnedItemSnapshot { InstanceId = toolBottle, ContentId = "containertest.bottle", Revision = 1,
                        LocationKind = 8, Slot = 6,
                        ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = Array.Empty<OnlineOwnedMaterialAmountSnapshot>() } } } };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(catalog, assets, definition.GameplayConfigHash, out _authority, out error), Is.True, error);
            Assert.That(_authority.TryCapturePresentation(7, 1, 10, out var snapshot), Is.True);
            _scene = SceneManager.CreateScene("FormalCombatInventoryDragRuntime");
            var expected = new OnlineInventoryPresentationIdentity(assets.RunId, assets.PlayerId, 1, assets.InventoryId,
                definition.GameplayConfigHash, 7, _scene.name);
            Assert.That(OnlineInventoryPresentationSource.TryCreate(catalog, expected, snapshot, out _source, out error), Is.True, error);
            var actor = new GameObject("LocalActor"); actor.SetActive(false); SceneManager.MoveGameObjectToScene(actor, _scene); actor.AddComponent<HealthComponent>();
            var root = new GameObject("CombatScope"); root.SetActive(false); SceneManager.MoveGameObjectToScene(root, _scene);
            _scope = root.AddComponent<CombatPresentationScope>();
            typeof(CombatPresentationScope).GetField("_online", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_scope, true);
            var eventRoot = new GameObject("FormalDragEventSystem", typeof(FocusAwareEventSystem)); SceneManager.MoveGameObjectToScene(eventRoot, _scene);
            var events = eventRoot.GetComponent<EventSystem>();
            ActivateScopeWithIsolatedInventoryInput(root, actor);
            yield return null; yield return null; yield return null;
            var inventory = root.GetComponentInChildren<SinglePlayerInventoryController>(true);
            Assert.That(inventory, Is.Not.Null); _host = CombatPresentationHost.Active;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.Tab)); InputSystem.Update(); yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update(); yield return null;
            Assert.That(inventory.IsOpen, Is.True); Assert.That(inventory.PresentationSource, Is.SameAs(_source));
            bool foundCharges = false;
            foreach (var view in inventory.GetComponentsInChildren<InventoryItemView>())
            {
                if (view.Snapshot.InstanceId != card) continue;
                foundCharges = true;
                var charges = (Text)Field(view, "_spellCount");
                Assert.That(charges, Is.Not.Null); Assert.That(charges.gameObject.activeInHierarchy, Is.True);
                Assert.That(charges.text, Is.EqualTo("30"), "生产Emit卡应展示真实有限次数，Qualification卡的隐藏规则不能吞掉它。");
                Assert.That(charges.font, Is.Not.Null, "次数Text必须在原Controller.Start后取得可绘制字体。");
            }
            Assert.That(foundCharges, Is.True, "正式程序槽必须生成该Emit卡的次数View。");

            OnlineInventoryCommandRequestedEvent request = default; int requests = 0;
            Action<OnlineInventoryCommandRequestedEvent> capture = value =>
            { if (value.CallerId == inventory.GetInstanceID()) { request = value; requests++; } };
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(capture);
            try
            {
                // 正式Catalog、原Scope/Tab、运行时美术模板替换和Native Canvas真实注册都参与，
                // 不改depth、不跳过CanvasGroup，也不把EditMode没有渲染的空Raycast解释成玩家故障。
                var itemIds = new[] { wand, card, toolBottle, wand, card, bagBottle, bagBottle };
                var destinations = new[] { "bag", "bag", "bag", "ground", "ground", "ground", "shortcut" };
                var kinds = new[] { OnlineInventoryCommandKind.MoveOwned, OnlineInventoryCommandKind.MoveOwned, OnlineInventoryCommandKind.MoveOwned,
                    OnlineInventoryCommandKind.DropItem, OnlineInventoryCommandKind.DropItem, OnlineInventoryCommandKind.DropContainer, OnlineInventoryCommandKind.BindTool };
                for (int i = 0; i < itemIds.Length; i++)
                {
                    requests = 0; GameObject source = FindDragSource(inventory, itemIds[i], wand, toolBottle);
                    Assert.That(source, Is.Not.Null, "正式界面缺少真实实例的拖拽入口：" + i);
                    var pointer = new PointerEventData(events) { button = PointerEventData.InputButton.Left,
                        position = ScreenCenter((RectTransform)source.transform) };
                    AssertRaycastHandler<IBeginDragHandler>(events, pointer, source, "source " + i);
                    ExecuteEvents.Execute(source, pointer, ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.Execute(source, pointer, ExecuteEvents.beginDragHandler);
                    Assert.That(inventory.IsDragging, Is.True, "原View未进入实际拖拽：" + i);
                    Canvas.ForceUpdateCanvases();
                    GameObject target;
                    if (destinations[i] == "shortcut")
                    {
                        target = FindTool(inventory, 2).gameObject; pointer.position = ScreenCenter((RectTransform)target.transform);
                    }
                    else
                    {
                        var grid = (Component)Field(inventory, destinations[i] == "bag" ? "_gridView" : "_nearbyGrid");
                        var rect = (RectTransform)grid.transform;
                        float cell = (float)grid.GetType().GetProperty("CellSize").GetValue(grid);
                        pointer.position = RectTransformUtility.WorldToScreenPoint(null,
                            rect.TransformPoint(new Vector3(rect.rect.xMin + 2.5f * cell, rect.rect.yMax - 2.5f * cell, 0)));
                        target = destinations[i] == "bag" ? grid.gameObject : ((Component)Field(inventory, "_groundTarget")).gameObject;
                    }
                    ExecuteEvents.Execute(source, pointer, ExecuteEvents.dragHandler);
                    if (destinations[i] == "ground")
                    {
                        // NearbyItemGridView.Refresh会把最近的Viewport DropTarget也初始化成Ground；
                        // UGUI本来就优先派发给它。验证真实命中语义和Controller，再使用该Handler，
                        // 不能把合法内层目标硬判成遮挡，更不能直接向外层派发而跳过玩家实际路径。
                        var hits = new List<RaycastResult>(); events.RaycastAll(pointer, hits);
                        Assert.That(hits, Is.Not.Empty);
                        target = ExecuteEvents.GetEventHandler<IDropHandler>(hits[0].gameObject);
                        var ground = target != null ? target.GetComponent<InventoryDropTarget>() : null;
                        Assert.That(ground, Is.Not.Null, "附近第一命中必须是原Ground DropTarget。");
                        Assert.That(Field(ground, "_kind"), Is.EqualTo(InventoryDropKind.Ground));
                        Assert.That(Field(ground, "_controller"), Is.SameAs(inventory));
                    }
                    else AssertRaycastHandler<IDropHandler>(events, pointer, target, "target " + i);
                    ExecuteEvents.Execute(target, pointer, ExecuteEvents.dropHandler);
                    ExecuteEvents.Execute(source, pointer, ExecuteEvents.endDragHandler);
                    Assert.That(requests, Is.EqualTo(1), "一次原View拖放应提交一份权威意图：" + i);
                    Assert.That(request.ActorId, Is.EqualTo(1ul)); Assert.That(request.Intent.ConnectionEpoch, Is.EqualTo(7u));
                    Assert.That(request.Intent.Kind, Is.EqualTo(kinds[i])); Assert.That(request.Intent.ItemId, Is.EqualTo(itemIds[i]));
                    Assert.That(request.Intent.ExpectedInventoryVersion, Is.EqualTo(snapshot.Assets.InventoryVersion));
                    Assert.That(inventory.IsDragging, Is.False); Assert.That(_source.ReadOnly.Count, Is.EqualTo(5));
                    yield return null;
                }
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(capture); }
        }

        private static object Field(object value, string name)
            => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        private static Vector2 ScreenCenter(RectTransform rect)
            => RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
        private static ToolbeltSlotView FindTool(SinglePlayerInventoryController inventory, int slot)
        {
            foreach (var tool in inventory.GetComponentsInChildren<ToolbeltSlotView>()) if (!tool.Compact && tool.ToolSlot == slot) return tool;
            return null;
        }
        private static GameObject FindDragSource(SinglePlayerInventoryController inventory, string item, string wand, string tool)
        {
            if (item == tool) { var slot = FindTool(inventory, 6); return slot != null ? slot.gameObject : null; }
            if (item == wand)
            {
                foreach (var view in inventory.GetComponentsInChildren<EquippedWandDragView>())
                    if (((InventoryItemSnapshot)Field(view, "_item")).InstanceId == wand) return view.gameObject;
            }
            foreach (var view in inventory.GetComponentsInChildren<InventoryItemView>()) if (view.Snapshot.InstanceId == item) return view.gameObject;
            return null;
        }
        private static void AssertRaycastHandler<T>(EventSystem events, PointerEventData pointer, GameObject expected, string phase)
            where T : IEventSystemHandler
        {
            var hits = new List<RaycastResult>(); events.RaycastAll(pointer, hits);
            Assert.That(hits, Is.Not.Empty, phase + "未命中正式Native Canvas。");
            Assert.That(ExecuteEvents.GetEventHandler<T>(hits[0].gameObject), Is.SameAs(expected),
                phase + "被其他图形遮挡：" + hits[0].gameObject.name);
        }

        private void BeginIsolatedInput()
        {
            _background = Application.runInBackground; Application.runInBackground = true; _scale = Time.timeScale;
            _cursorMode = Cursor.lockState; _cursorVisible = Cursor.visible;
            // 本夹具隔离测试原Inventory Canvas/InputModule与真实指针事务，没有装配
            // OnlineWorldBootstrap→DungeonEntryDialogController这条生产Cursor Owner链。
            // 明确建立UI鼠标前置条件，避免上一Hub夹具的RunPauseCoordinator残留Locked
            // 使结果依赖执行顺序；Cleanup仍恢复进入前状态。完整Tab光标装配由Native合作回归覆盖。
            Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
            _initialHost = CombatPresentationHost.Active;
            Assert.That(_initialHost == null || !_initialHost.IsBound, Is.True, "不能抢占其他 Scene 的有效表现租约。");
            Assert.That(LocalActorPresentation.Actor == null, Is.True, "前一夹具必须释放自己的 Actor。");
            CaptureAndReleaseProjectActionState();
            _input = new InputTestFixture(); _input.Setup(); _keyboard = InputSystem.AddDevice<Keyboard>();
        }

        private void CaptureAndReleaseProjectActionState()
        {
            // Input System 1.19 的 Setup 会先保存全局 ActionState 列表，再切换 InputManager，
            // 并从 EditorBuildSettings 自动 Enable 项目 Actions。已加载 asset 的运行时 state
            // 可能仍指向前一 fixture 的 Keyboard，导致 InitialStateCheck 读取失效的 statePtr。
            // 在旧 runtime 仍有效时 Dispose ActionMap state，Setup 才会解析本次虚拟设备。
            // 仅重建非序列化运行时缓存；保留 asset、bindings、override 和项目配置，不保存/销毁资产。
            var managerField = typeof(InputSystem).GetField("s_Manager", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(managerField, Is.Not.Null, "Input System 升级时需复核 fixture 的 runtime 恢复边界。");
            _originalInputManager = managerField.GetValue(null);
            Assert.That(_originalInputManager, Is.Not.Null);
            _managerActionsProperty = _originalInputManager.GetType().GetProperty("actions");
            Assert.That(_managerActionsProperty, Is.Not.Null);
            _originalManagerActions = InputSystem.actions;
            // 前一个 InputTestFixture.Restore 可能已留下 null manager.actions；下一次 Setup
            // 仍读取此配置，所以必须只读取得配置资产，不能仅清理当前 manager 的引用。
            UnityEditor.EditorBuildSettings.TryGetConfigObject("com.unity.input.settings.actions", out _projectActions);
            Assert.That(_originalManagerActions == null || _originalManagerActions == _projectActions, Is.True,
                "当前 manager 必须沿用项目配置，不能抢占其他测试自行注入的 Actions。");
            _enabledProjectActions.Clear();
            if (_projectActions != null)
                foreach (var action in _projectActions) if (action.enabled) _enabledProjectActions.Add(action);
            _projectActionsCaptured = true;
            if (_projectActions == null) return;
            _projectActions.Disable();
            foreach (var map in _projectActions.actionMaps) map.Dispose();
        }

        private void RestoreProjectActionState()
        {
            if (!_projectActionsCaptured) return;
            try
            {
                var manager = typeof(InputSystem).GetField("s_Manager", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                Assert.That(manager, Is.SameAs(_originalInputManager), "先由 InputTestFixture 恢复原 runtime，再恢复项目 Actions。");
                // Reset 会将旧 manager.actions 置 null，而 Restore 只复用旧 manager，不补回引用。
                // 内部 InputManager.actions setter 仅设置字段并通知观察者；公共 InputSystem.actions
                // setter 会写项目配置且不允许 PlayMode，故这里仅恢复这个已保存的内存对象。
                if (!ReferenceEquals(InputSystem.actions, _originalManagerActions))
                    _managerActionsProperty.SetValue(manager, _originalManagerActions);
                if (_projectActions == null) return;
                // TearDown 已 Dispose 临时 runtime 的全部 ActionState。按原逐 Action enabled 恢复，
                // 不用 asset.Enable() 把调用前禁用的 Action 一并打开；重新解析的 controls 属于旧 runtime。
                _projectActions.Disable();
                foreach (var action in _enabledProjectActions) action.Enable();
                foreach (var action in _projectActions)
                    Assert.That(action.enabled, Is.EqualTo(_enabledProjectActions.Contains(action)), action.name);
                Assert.That(InputSystem.actions, Is.SameAs(_originalManagerActions));
            }
            finally
            {
                _projectActionsCaptured = false; _enabledProjectActions.Clear();
                _originalInputManager = null; _managerActionsProperty = null;
                _originalManagerActions = _projectActions = null;
            }
        }

        private void ActivateScopeWithIsolatedInventoryInput(GameObject root, GameObject actor)
        {
            Assert.That(root.activeInHierarchy, Is.False);
            // InputTestFixture 切换的是全局 InputManager，并不会清除已加载资源中 ActionMap 的 m_State。
            // 库存资源也只为本实例复制正式 Action 配置，使它与测试 runtime 同寿命。
            // 原17例组合的实际异常 Owner 是项目 Actions（见上面的隔离），这里同时避免复用资源缓存；
            // 不 Disable/Dispose 或修改 Resources 中的库存共享资产。正常游戏不切换测试 runtime。
            // 使用 Scope 原装配方法先生成 inactive 子树，保留原 prefab、布局、View 和 Raycast 树。
            var ensure = typeof(CombatPresentationScope).GetMethod("EnsureOnlineInventory", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((bool)ensure.Invoke(_scope, null), Is.True);
            var inventory = root.GetComponentInChildren<SinglePlayerInventoryController>(true);
            Assert.That(inventory, Is.Not.Null); Assert.That(inventory.gameObject.activeInHierarchy, Is.False);
            foreach (var fieldName in new[] { "_toggleAction", "_rotateAction", "_cancelAction", "_splitAction" })
            {
                var field = typeof(SinglePlayerInventoryController).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                var original = (InputActionReference)field.GetValue(inventory);
                Assert.That(original != null && original.asset != null && original.action != null, Is.True, fieldName);
                if (!_inputAssets.TryGetValue(original.asset, out var clone))
                {
                    // 克隆整个 asset 保留正式 action/map ID、binding 和 control scheme，只让设备解析属于本次 runtime。
                    clone = Object.Instantiate(original.asset); _inputAssets.Add(original.asset, clone);
                    clone.LoadBindingOverridesFromJson(original.asset.SaveBindingOverridesAsJson());
                }
                var action = clone.FindAction(original.action.id);
                Assert.That(action, Is.Not.Null);
                var reference = InputActionReference.Create(action); _inputReferences.Add(reference);
                field.SetValue(inventory, reference);
            }
            // 先交付真实 Actor 事实，Scope.OnEnable 会走生产重放；否则空 Actor 会合法 Clear 上面的未绑定子树。
            // Source 仍在启用后通过原事件抵达，UI 的 Awake/OnEnable/Start、Tab Action 与命令链完全由生产执行。
            LocalActorPresentation.Bind(70177, actor, true); root.SetActive(true);
            OnlineInventoryPresentation.Bind(70177, actor, _source);
            _host = CombatPresentationHost.Active; // 在首个 yield 前记住拥有的 Host，断言/输入异常也能正确清理。
        }

        [UnityTest] public IEnumerator OriginalTabUsesOnlineSourceWithoutPausingAndIsReleasedWithItsScene()
        {
            BeginIsolatedInput();
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(definition.Catalog.TryCreateIndex(out var catalog, out var error), Is.True, error);
            const string wand = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", card = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1, RunId = "tab-run", PlayerId = "tab-player", InventoryId = "tab-inventory",
                InventoryVersion = 1, CurrencyVersion = 1, GameplayConfigHash = definition.GameplayConfigHash, SelectedWandInstanceId = wand,
                Items = new[] {
                    new OnlineOwnedItemSnapshot { InstanceId = wand, ContentId = "wandtest.wand.a", Revision = 1, LocationKind = 3, UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = card, ContentId = "wandtest.fire", Revision = 1, LocationKind = 2, ParentInstanceId = wand, RemainingCharges = 30 } } };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(catalog, assets, definition.GameplayConfigHash, out _authority, out error), Is.True, error);
            Assert.That(_authority.TryCapturePresentation(7, 1, 10, out var snapshot), Is.True);
            _scene = SceneManager.CreateScene("OnlineOriginalTabRuntime");
            var expected = new OnlineInventoryPresentationIdentity(assets.RunId, assets.PlayerId, 1, assets.InventoryId, definition.GameplayConfigHash, 7, _scene.name);
            Assert.That(OnlineInventoryPresentationSource.TryCreate(catalog, expected, snapshot, out _source, out error), Is.True, error);
            var actor = new GameObject("LocalActor"); actor.SetActive(false); SceneManager.MoveGameObjectToScene(actor, _scene); actor.AddComponent<HealthComponent>();
            var root = new GameObject("CombatScope"); root.SetActive(false); SceneManager.MoveGameObjectToScene(root, _scene);
            _scope = root.AddComponent<CombatPresentationScope>();
            typeof(CombatPresentationScope).GetField("_online", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_scope, true);
            ActivateScopeWithIsolatedInventoryInput(root, actor);
            yield return null; yield return null; yield return null;
            var inventory = root.GetComponentInChildren<SinglePlayerInventoryController>(true);
            Assert.That(inventory, Is.Not.Null); Assert.That(inventory.Session, Is.Null); Assert.That(inventory.PresentationSource, Is.SameAs(_source));
            _host = CombatPresentationHost.Active; Assert.That(_host.InventorySource, Is.SameAs(_source));
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.Tab)); InputSystem.Update(); yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update(); yield return null;
            Assert.That(inventory.IsOpen, Is.True); Assert.That(Time.timeScale, Is.EqualTo(_scale));
            Assert.That(_source.ReadOnly.Count, Is.EqualTo(2));
            // 晚到的相同Source重放不应关闭当前Tab，Source内部刷新也不会创建第二个Controller。
            OnlineInventoryPresentation.Bind(70177, actor, _source); yield return null;
            Assert.That(inventory.IsOpen, Is.True); Assert.That(root.GetComponentsInChildren<SinglePlayerInventoryController>(true).Length, Is.EqualTo(1));
            _scope.Clear(); yield return null;
            Assert.That(inventory == null, Is.True); Assert.That(_host.InventorySource, Is.Null);
            Assert.That(_host.gameObject.scene.name, Is.EqualTo("DontDestroyOnLoad")); Assert.That(Time.timeScale, Is.EqualTo(_scale));
        }

        [UnityTearDown] public IEnumerator ReleaseOnlyThisTestWorld()
        {
            try
            {
                ReleaseGroundPickupFixture();
                if (_scope != null) _scope.Clear();
                LocalActorPresentation.Clear(70177); OnlineInventoryPresentation.Clear(70177);
                if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
                if (_host != null && _initialHost == null) Object.Destroy(_host.gameObject);
                _source?.Dispose(); _authority?.Dispose();
                // 控件先经真实 OnDisable 退订，再销毁测试独占的引用/asset；其 OnDestroy 必须发生在恢复原 runtime 前。
                foreach (var reference in _inputReferences) if (reference != null) Object.Destroy(reference);
                foreach (var asset in _inputAssets.Values) if (asset != null) Object.Destroy(asset);
                yield return null;
            }
            finally
            {
                _inputReferences.Clear(); _inputAssets.Clear();
                try
                {
                    try { _input?.TearDown(); }
                    finally { RestoreProjectActionState(); }
                }
                finally
                {
                    _input = null; _keyboard = null;
                    _scope = null; _host = _initialHost = null; _scene = default; _source = null; _authority = null;
                    Application.runInBackground = _background; Cursor.lockState = _cursorMode; Cursor.visible = _cursorVisible;
                }
            }
            Assert.That(Time.timeScale, Is.EqualTo(_scale));
        }
    }
}
#endif
