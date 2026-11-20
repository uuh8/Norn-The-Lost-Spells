using System;
using System.Reflection;
using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>
    /// 真实Keyboard F经过Character Input、Run NearbyController与WorldItemPickup发布在线意图。
    /// 仅替换网络之后的订阅者，不直接调用OnPickup/TryCollect，也不启动HTTP/KCP或触碰存档。
    /// </summary>
    public sealed class NearbyPickupKeyboardTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string PlayerUpdates = "RUN_PLAYER_UPDATES_IN_EDIT_MODE";
        private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string GroundItemId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string GroundOwnerId = "cccccccccccccccccccccccccccccccc";
        private GameObject _actor, _ground;
        private ItemDefinition _item;
        private ItemCatalog _catalog;
        private OnlineInventoryPresentationSource _source;
        private NearbyPickupController _controller;
        private NearbyPickupInput _input;
        private WorldItemPickup _proxy;
        private Keyboard _keyboard;
        private InputSettings _settings;
        private bool _previousPlayerUpdates, _savedStatics, _savedPlayerUpdates;
        private float _timeScale;
        private EditorWindow _previousWindow;
        private object _previousPickup, _previousHub, _previousPortal;
        private int _previousPickupFrame;
        private int _requests;
        private OnlineInventoryCommandRequestedEvent _observed;

        [SetUp] public void SetupIsolatedPlayerInput()
        {
            _requests = 0; _observed = default; _savedStatics = _savedPlayerUpdates = false;
            _keyboard = null; _source = null; _input = null; _controller = null; _proxy = null;
            _actor = _ground = null; _catalog = null; _item = null;
            _timeScale = Time.timeScale; Time.timeScale = 1;
            _previousWindow = EditorWindow.focusedWindow;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            Assert.That(Application.isFocused, Is.True, "正例需真实GameView焦点，不能删除产品Focus门制造通过。");
            _settings = InputSystem.settings;
            _previousPlayerUpdates = (bool)typeof(InputSettings).GetMethod("IsFeatureEnabled", Private)
                .Invoke(_settings, new object[] { PlayerUpdates });
            _savedPlayerUpdates = true;
            // EditMode默认更新Editor buffer，Action不会执行。只改包的内存feature flag，结束时原值恢复。
            _settings.SetInternalFeatureFlag(PlayerUpdates, true);
            _previousPickup = Current(typeof(NearbyPickupInput)).GetValue(null);
            _previousHub = Current(typeof(HubInteractInput)).GetValue(null);
            _previousPortal = Current(typeof(PortalInteractInput)).GetValue(null);
            Current(typeof(HubInteractInput)).SetValue(null, null);
            Current(typeof(PortalInteractInput)).SetValue(null, null);
            _previousPickupFrame = (int)typeof(NearbyPickupInput).GetField("<LastPickupFrame>k__BackingField",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            _savedStatics = true;
            _keyboard = InputSystem.AddDevice<Keyboard>();
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(Observe);
        }

        private static PropertyInfo Current(Type type) => type.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic);
        private static void Call(object target, string method, params object[] args)
            => target.GetType().GetMethod(method, Private).Invoke(target, args);
        private static void Set(object target, string field, object value)
            => target.GetType().GetField(field, Private).SetValue(target, value);
        private void Observe(OnlineInventoryCommandRequestedEvent request) { _requests++; _observed = request; }

        private void CreateOnlinePickup(bool controllerArrivesLate = false)
        {
            _item = ScriptableObject.CreateInstance<ItemDefinition>();
            Set(_item, "_contentId", "pickup.keyboard.item"); Set(_item, "_kind", ItemKind.Consumable);
            _catalog = ScriptableObject.CreateInstance<ItemCatalog>(); Set(_catalog, "_definitions", new[] { _item });
            Assert.That(_catalog.TryCreateIndex(out var catalog, out var error), Is.True, error);
            _actor = new GameObject("Keyboard pickup local Actor");
            _actor.transform.position = new Vector3(24001, 24001, 24001);
            var snapshot = new OnlineInventoryPresentationSnapshot
            {
                SchemaVersion = 1, ConnectionEpoch = 7, SnapshotSequence = 1, ResourceVersion = 1, SelectionVersion = 1, SpellUseVersion = 1,
                Assets = new OnlineOwnedAssetSnapshot { SchemaVersion = 3, RunId = "run", PlayerId = "player", ActorId = 1,
                    InventoryId = "keyboard-inventory", GameplayConfigHash = Hash, InventoryVersion = 9, CurrencyVersion = 1 },
                Ground = new OnlineGroundInventorySnapshot { Version = 1, Items = new[] { new OnlineGroundItemSnapshot
                {
                    InstanceId = GroundItemId, ContentId = _item.ContentId, WorldOwnerId = GroundOwnerId, Revision = 1,
                    Position = _actor.transform.position + Vector3.forward, Rotation = Quaternion.identity,
                    Item = new OnlineOwnedItemSnapshot { InstanceId = GroundItemId, ContentId = _item.ContentId,
                        Revision = 1, LocationKind = (int)ItemLocationKind.WorldItem, Slot = -1 }
                } } }
            };
            var identity = new OnlineInventoryPresentationIdentity("run", "player", 1, "keyboard-inventory", Hash, 7, "KeyboardFixture");
            Assert.That(OnlineInventoryPresentationSource.TryCreate(catalog, identity, snapshot, out _source, out error), Is.True, error);
            _ground = new GameObject("Confirmed ground proxy"); _proxy = _ground.AddComponent<WorldItemPickup>();
            Assert.That(_proxy.BindOnline(_source, GroundItemId, GroundOwnerId, _actor.transform), Is.True);

            if (!controllerArrivesLate) BindController();
            _input = _actor.AddComponent<NearbyPickupInput>();
            Call(_input, "Awake"); _input.ConfigureOnline(_controller); Call(_input, "OnEnable");
            if (controllerArrivesLate) BindController(); // 原ListChanged必须把同Actor输入补绑到UI晚建的Controller。
            Assert.That(_controller.Count, Is.EqualTo(1));
            Assert.That(_controller.FocusedPickup, Is.SameAs(_proxy));
            Keys();
        }
        private void BindController()
        {
            _controller = _actor.AddComponent<NearbyPickupController>();
            _controller.ConfigureOnline(_source, _actor.transform);
        }
        private void Keys(params Key[] keys)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(keys)); InputSystem.Update();
            Assert.That(InputState.currentUpdateType, Is.Not.EqualTo(InputUpdateType.Editor));
            Assert.That(_keyboard.fKey.isPressed, Is.EqualTo(Array.IndexOf(keys, Key.F) >= 0));
        }
        private void AssertOneOriginalPickupRequest()
        {
            Assert.That(_requests, Is.EqualTo(1), "真实F应到达WorldItemPickup的网络意图发布边界。");
            Assert.That(_observed.ActorId, Is.EqualTo(1ul));
            Assert.That(_observed.CallerId, Is.EqualTo(_proxy.GetInstanceID()));
            Assert.That(_observed.Intent.ConnectionEpoch, Is.EqualTo(7u));
            Assert.That(_observed.Intent.ExpectedInventoryVersion, Is.EqualTo(9u));
            Assert.That(_observed.Intent.Kind, Is.EqualTo(OnlineInventoryCommandKind.PickupItem));
            Assert.That(_observed.Intent.ItemId, Is.EqualTo(GroundItemId));
            Assert.That(_observed.Intent.WorldOwnerId, Is.EqualTo(GroundOwnerId));
            Assert.That(_observed.Intent.AutoPlace, Is.True);
            Assert.That(_controller.HasPendingOnlinePickup, Is.True);
            Assert.That(_proxy.gameObject.activeSelf, Is.True, "请求发布不冒充DS收据，不能提前隐藏地面资产。");
            Assert.That(_source.ReadOnly.Version, Is.EqualTo(9u));
        }

        [TestCase(false)] [TestCase(true)]
        public void RealKeyboardFUsesTheSameActorSourceWithEarlyOrLateController(bool controllerArrivesLate)
        {
            CreateOnlinePickup(controllerArrivesLate);
            Keys(Key.F); AssertOneOriginalPickupRequest();
            Keys(); Keys(Key.F);
            Assert.That(_requests, Is.EqualTo(1), "收据未到时再次F不能创建第二条同物请求。");
        }

        [Test] public void ReenabledPickupResamplesFocusAndAcceptsTheFirstNewKeyboardF()
        {
            CreateOnlinePickup();
            Call(_input, "OnApplicationFocus", false);
            // 与OnlineCastDriver.OnDisable中的真实入口相同；这里不需要创建战斗/网络Owner。
            Call(_input, "ReleaseOnline", false);
            Assert.That(_input.enabled, Is.False);
            // 停用阶段没有收到回焦事件。恢复时窗口确实已有焦点，不能要求玩家再Alt-Tab一次。
            Assert.That(Application.isFocused, Is.True);
            _input.enabled = true; Call(_input, "OnEnable");
            Keys(); Keys(Key.F); AssertOneOriginalPickupRequest();
        }

        [Test] public void ActivePickupRejectsRealKeyboardFWhileFocusRemainsFalse()
        {
            CreateOnlinePickup(); Call(_input, "OnApplicationFocus", false);
            Keys(Key.F);
            Assert.That(_requests, Is.Zero, "保留失焦边沿；不能只靠最终Application.isFocused覆盖此门。");
            Assert.That(_controller.HasPendingOnlinePickup, Is.False);
            Keys(); Call(_input, "OnApplicationFocus", true); Keys(Key.F); AssertOneOriginalPickupRequest();
        }

        [TearDown] public void RestoreOnlyFixtureState()
        {
            EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(Observe);
            if (_input != null) Call(_input, "OnDestroy");
            if (_controller != null) Call(_controller, "OnDisable");
            if (_proxy != null) Call(_proxy, "OnDestroy");
            if (_ground != null) Object.DestroyImmediate(_ground);
            if (_actor != null) Object.DestroyImmediate(_actor);
            _source?.Dispose();
            if (_catalog != null) Object.DestroyImmediate(_catalog);
            if (_item != null) Object.DestroyImmediate(_item);
            if (_keyboard != null) InputSystem.RemoveDevice(_keyboard);
            if (_savedPlayerUpdates && _settings != null) _settings.SetInternalFeatureFlag(PlayerUpdates, _previousPlayerUpdates);
            if (_savedStatics)
            {
                Current(typeof(NearbyPickupInput)).SetValue(null, _previousPickup);
                Current(typeof(HubInteractInput)).SetValue(null, _previousHub);
                Current(typeof(PortalInteractInput)).SetValue(null, _previousPortal);
                typeof(NearbyPickupInput).GetField("<LastPickupFrame>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, _previousPickupFrame);
            }
            Time.timeScale = _timeScale;
            if (_previousWindow != null) _previousWindow.Focus();
        }
    }
}
