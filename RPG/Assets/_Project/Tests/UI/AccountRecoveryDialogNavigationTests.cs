using System;
using System.Reflection;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>
    /// 冷恢复保留原 World 供同账号重连，但原 World 的迟到事件不能盖住登录菜单。
    /// 使用正式 Dialog prefab、真实 EventBus 与隔离的 InputSystem；只替代账户 HTTP 边界的 Observe 快照。
    /// 不加载正式 Scene、不保存资源、不建立网络连接，结束时只清理夹具拥有的对象与订阅。
    /// </summary>
    public sealed class AccountRecoveryDialogNavigationTests : InputTestFixture
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _root, _panel;
        private DungeonEntryDialogController _dialog;
        private Keyboard _keyboard;
        private InputAction _cancelAction;
        private bool _enabled, _menuVisible, _cursorVisible;
        private CursorLockMode _cursorMode;
        private int _observeRequests, _menuRequests, _cancelActions;
        private bool _requestedMenuOpen;
        private Action _onCancel;

        [SetUp]
        public void CreateOriginalDialogAndIsolatedInput()
        {
            _enabled = _menuVisible = false;
            _observeRequests = _menuRequests = _cancelActions = 0;
            _requestedMenuOpen = false; _onCancel = null;
            _cursorMode = Cursor.lockState; _cursorVisible = Cursor.visible;
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _cancelAction = new InputAction("RecoveryFixtureCancel", InputActionType.Button, "<Keyboard>/escape");
            _cancelAction.performed += OnCancelAction;
            _cancelAction.Enable();
            EventBus<OnlineLobbyRequestEvent>.Subscribe(OnAccountRequest);
            EventBus<OnlineMovementMenuRequestEvent>.Subscribe(OnMenuRequest);

            var prefab = Resources.Load<GameObject>("Online/DungeonEntryDialog");
            Assert.That(prefab, Is.Not.Null);
            _root = Object.Instantiate(prefab);
            _dialog = _root.GetComponent<DungeonEntryDialogController>();
            Assert.That(_dialog, Is.Not.Null);
            // EditMode 不自动保证普通 MonoBehaviour 的运行时生命周期；这里只显式调用真实生命周期方法。
            Invoke("Awake"); Invoke("OnDisable");
            _panel = (GameObject)typeof(DungeonEntryDialogController).GetField("_panel", PrivateInstance).GetValue(_dialog);
            Assert.That(_panel, Is.Not.Null);
        }

        [TearDown]
        public void ReleaseOnlyOwnedDialogInputAndSubscriptions()
        {
            if (_dialog != null && _enabled) DisableDialog();
            EventBus<OnlineLobbyRequestEvent>.Unsubscribe(OnAccountRequest);
            EventBus<OnlineMovementMenuRequestEvent>.Unsubscribe(OnMenuRequest);
            if (_cancelAction != null)
            {
                _cancelAction.performed -= OnCancelAction;
                _cancelAction.Dispose(); _cancelAction = null;
            }
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null; _dialog = null; _panel = null;
            // 基类 InputTestFixture 恢复原生设备、运行时和全局 InputSystem 设置。
            Cursor.lockState = _cursorMode; Cursor.visible = _cursorVisible;
        }

        [Test]
        public void AccountMenuOwnershipImmediatelyHidesAnAlreadyOpenDisconnectedDialog()
        {
            EnableDialog();
            PublishRoom(OnlineRoomPhase.Disconnected);
            PublishGameplay(false, true, true);
            Assert.That(_panel.activeSelf, Is.True, "先建立实际断线面板，避免仅证明默认隐藏状态。");

            SetAccountMenuVisible(true);

            Assert.That(_panel.activeSelf, Is.False, "恢复主菜单必须立即撤销原副本 Dialog，不能等待下一条房间消息。");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RetainedWorldEventsCannotReopenOrRelockAccountMenu(bool connected)
        {
            EnableDialog(); SetAccountMenuVisible(true);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;

            PublishRoom(connected ? OnlineRoomPhase.Connected : OnlineRoomPhase.Disconnected);
            bool panelAfterRoom = _panel.activeSelf;
            // 断线应尝试打开 Dialog；仍连上的迟到 View 则尝试锁鼠标。两条都必须受导航 Owner 限制。
            PublishGameplay(connected, !connected, !connected);

            Assert.That(panelAfterRoom, Is.False, "Room 事件不能盖住主菜单。");
            Assert.That(_panel.activeSelf, Is.False, "保留 World 的 Gameplay 事件不能重新打开副本菜单。");
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None), "主菜单鼠标不能被旧 World 锁住。");
            Assert.That(Cursor.visible, Is.True);
        }

        [Test]
        public void AccountMenuEscapeCannotLeakThroughRetainedWorldCancelAction()
        {
            EnableDialog(); PublishGameplay(true, false, false);
            SetAccountMenuVisible(true);
            // Cancel Action 发生在 Dialog.Update 前：旧库存可能刚释放 Owner，旧 World 随之重放断线视图。
            // 不能只在导航切换瞬间隐藏一次，而放行同一个真实按键后续的 Gameplay/Update。
            _onCancel = () =>
            {
                EventBus<InventoryPresentationVisibilityEvent>.Publish(
                    new InventoryPresentationVisibilityEvent { OwnerId = 87321, Open = false });
                PublishGameplay(false, true, true);
            };

            PressEscape();

            Assert.That(_cancelActions, Is.EqualTo(1), "夹具必须真正经过 InputAction，而非直接调用菜单方法。");
            Assert.That(_menuRequests, Is.Zero, "主菜单的 Esc 不能向保留 World 发送菜单开关命令。");
            Assert.That(_panel.activeSelf, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EnableReplaysAccountNavigationBeforeAcceptingRetainedWorld(bool previouslyEnabled)
        {
            if (previouslyEnabled)
            {
                EnableDialog(); PublishGameplay(true, false, false); DisableDialog();
            }
            // 模拟迟订阅或停用期间错过通知：只更新账户拥有的快照，不主动给 Dialog 发事件。
            _menuVisible = true;
            int previousObservations = _observeRequests;
            EnableDialog();
            PublishRoom(OnlineRoomPhase.Disconnected); PublishGameplay(false, true, true);

            Assert.That(_observeRequests, Is.GreaterThan(previousObservations), "启用应主动 Observe，EventBus 不缓存导航状态。");
            Assert.That(_panel.activeSelf, Is.False, "晚启用或重启不能丢掉主菜单的当前占用。");
        }

        [Test]
        public void ReleasingAccountMenuRestoresEscapeWithoutOldInventoryOwners()
        {
            EnableDialog();
            EventBus<InventoryPresentationVisibilityEvent>.Publish(
                new InventoryPresentationVisibilityEvent { OwnerId = 87321, Open = true });
            PublishGameplay(true, false, true);
            SetAccountMenuVisible(true);
            // 原 Scene 仍活着，可能继续给出另一个库存 Owner；它不能污染回到 Gameplay 后的捕获集合。
            EventBus<InventoryPresentationVisibilityEvent>.Publish(
                new InventoryPresentationVisibilityEvent { OwnerId = 87322, Open = true });
            PublishGameplay(false, false, true);

            SetAccountMenuVisible(false);
            PublishGameplay(true, false, false);
            PressEscape();

            Assert.That(_menuRequests, Is.EqualTo(1), "恢复副本后的新 Esc 不能被上一段主菜单期间的库存 Owner 吞掉。");
            Assert.That(_requestedMenuOpen, Is.True);
            Assert.That(_panel.activeSelf, Is.True);
        }

        [Test]
        public void ReleasingAccountMenuRestoresActualDisconnectPresentation()
        {
            EnableDialog(); SetAccountMenuVisible(true); SetAccountMenuVisible(false);
            Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;

            PublishRoom(OnlineRoomPhase.Disconnected); PublishGameplay(false, true, true);

            Assert.That(_panel.activeSelf, Is.True, "保护结束后仍应展示真实断线，不能永久静音副本 UI。");
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
            Assert.That(Cursor.visible, Is.True);
        }

        private void EnableDialog() { Invoke("OnEnable"); _enabled = true; }
        private void DisableDialog() { Invoke("OnDisable"); _enabled = false; }
        private void Invoke(string method)
            => typeof(DungeonEntryDialogController).GetMethod(method, PrivateInstance).Invoke(_dialog, null);
        private void SetAccountMenuVisible(bool value)
        { _menuVisible = value; PublishAccountSnapshot(); }
        private void OnAccountRequest(OnlineLobbyRequestEvent request)
        {
            if (request.Action != OnlineLobbyAction.Observe) return;
            _observeRequests++; PublishAccountSnapshot();
        }
        private void PublishAccountSnapshot()
        {
            object value = new OnlineLobbyChangedEvent { Authenticated = true, Username = "recovery-fixture" };
            // RED 阶段旧 Core 尚无新增字段也能编译；仍发真实事件，让断言落在可见面板/Esc 行为。
            // GREEN 必须由账户生产 Owner 发布此字段，测试不通过反射直接改 Dialog 的私有状态。
            typeof(OnlineLobbyChangedEvent).GetField("RecoveryMenuVisible")?.SetValue(value, _menuVisible);
            EventBus<OnlineLobbyChangedEvent>.Publish((OnlineLobbyChangedEvent)value);
        }
        private static void PublishRoom(OnlineRoomPhase phase)
            => EventBus<OnlineRoomChangedEvent>.Publish(new OnlineRoomChangedEvent
            { PortalId = 87320, Phase = phase, SinglePlayer = true, Members = Array.Empty<OnlineRoomMemberView>() });
        private static void PublishGameplay(bool connected, bool menuOpen, bool captured)
            => EventBus<OnlineMovementPresentationEvent>.Publish(new OnlineMovementPresentationEvent
            { Active = true, Connected = connected, MenuOpen = menuOpen, InputCaptured = captured });
        private void OnMenuRequest(OnlineMovementMenuRequestEvent request)
        { _menuRequests++; _requestedMenuOpen = request.Open; }
        private void OnCancelAction(InputAction.CallbackContext context) { _cancelActions++; _onCancel?.Invoke(); }
        private void PressEscape()
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update();
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.Escape)); InputSystem.Update();
            Assert.That(_keyboard.escapeKey.wasPressedThisFrame, Is.True);
            Invoke("Update");
        }
    }
}
