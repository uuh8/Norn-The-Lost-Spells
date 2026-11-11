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
    public sealed class OnlineSharedInventoryPlayModeTests
    {
        private Scene _scene;
        private CombatPresentationScope _scope;
        private CombatPresentationHost _host;
        private OnlineOwnedWorkspace _authority;
        private OnlineInventoryPresentationSource _source;
        private InputTestFixture _input;
        private Keyboard _keyboard;
        private bool _background;
        private float _scale;

        [UnityTest]
        public IEnumerator FormalCombatInventoryRaycastsAndOriginalHandlersSubmitSevenOwnedActions()
        {
            _background = Application.runInBackground; Application.runInBackground = true; _scale = Time.timeScale;
            _input = new InputTestFixture(); _input.Setup(); _keyboard = InputSystem.AddDevice<Keyboard>();
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
            root.SetActive(true); LocalActorPresentation.Bind(70177, actor, true); OnlineInventoryPresentation.Bind(70177, actor, _source);
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

        [UnityTest] public IEnumerator OriginalTabUsesOnlineSourceWithoutPausingAndIsReleasedWithItsScene()
        {
            _background = Application.runInBackground; Application.runInBackground = true; _scale = Time.timeScale;
            _input = new InputTestFixture(); _input.Setup(); _keyboard = InputSystem.AddDevice<Keyboard>();
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(definition.Catalog.TryCreateIndex(out var catalog, out var error), Is.True, error);
            const string wand = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", card = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1, RunId = "tab-run", PlayerId = "tab-player", InventoryId = "tab-inventory",
                InventoryVersion = 1, GameplayConfigHash = definition.GameplayConfigHash, SelectedWandInstanceId = wand,
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
            root.SetActive(true); LocalActorPresentation.Bind(70177, actor, true); OnlineInventoryPresentation.Bind(70177, actor, _source);
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
            if (_scope != null) _scope.Clear();
            LocalActorPresentation.Clear(70177); OnlineInventoryPresentation.Clear(70177);
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            if (_host != null) Object.Destroy(_host.gameObject);
            _source?.Dispose(); _authority?.Dispose(); yield return null;
            _input?.TearDown();
            Application.runInBackground = _background;
            Assert.That(Time.timeScale, Is.EqualTo(_scale));
        }
    }
}
#endif
