using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 只验证显式诊断的真实Input System排队/设备所有权，不调用Wizard或Driver伪造攻击。
    /// Native场景另证明真实Wizard动作、原Tab输入门和DS收据；此处不把Editor焦点失败算产品RED。
    /// </summary>
    public sealed class SyntheticPointerProbeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string PlayerUpdates = "RUN_PLAYER_UPDATES_IN_EDIT_MODE";
        private GameObject _root;
        private SinglePlayerInventoryRegressionProbe _probe;
        private InputAction _action;
        private Mouse _existing;
        private InputSettings _settings;
        private bool _savedPlayerUpdates;
        private EditorWindow _previousWindow;

        [SetUp]
        public void CreateOnlyFixtureProbe()
        {
            _previousWindow = EditorWindow.focusedWindow;
            _settings = InputSystem.settings;
            var feature = typeof(InputSettings).GetMethod("IsFeatureEnabled", Private);
            Assert.That(feature, Is.Not.Null);
            _savedPlayerUpdates = (bool)feature.Invoke(_settings, new object[] { PlayerUpdates });
            _settings.SetInternalFeatureFlag(PlayerUpdates, true);
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            _root = new GameObject("Explicit synthetic pointer fixture");
            _probe = _root.AddComponent<SinglePlayerInventoryRegressionProbe>();
        }

        private void Configure(bool pointer, bool keyboard = false)
        {
            var type = typeof(SinglePlayerInventoryRegressionProbe).GetNestedType("Config", BindingFlags.NonPublic);
            Assert.That(type, Is.Not.Null);
            var flag = type.GetField("AllowSyntheticPointer", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(flag, Is.Not.Null, "Pointer必须有独立、默认false的显式授权，不能借键盘权限启用。");
            var config = Activator.CreateInstance(type);
            flag.SetValue(config, pointer);
            type.GetField("AllowSyntheticKeyboard").SetValue(config, keyboard);
            typeof(SinglePlayerInventoryRegressionProbe).GetField("_config", Private).SetValue(_probe, config);
        }

        private string Queue(string button, bool pressed)
        {
            var method = typeof(SinglePlayerInventoryRegressionProbe).GetMethod("QueueSyntheticPointer", Private,
                null, new[] { typeof(string), typeof(bool) }, null);
            Assert.That(method, Is.Not.Null, "必须只排队MouseState，不能调用CaptureAttackPress/发施法意图替代鼠标。");
            return (string)method.Invoke(_probe, new object[] { button, pressed });
        }

        private Mouse OwnMouse()
        {
            var field = typeof(SinglePlayerInventoryRegressionProbe).GetField("_syntheticMouse", Private);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(_probe) as Mouse;
        }

        private static void PlayerUpdate()
        {
            InputSystem.Update();
            Assert.That(InputState.currentUpdateType, Is.Not.EqualTo(InputUpdateType.Editor),
                "必须真实更新Player Actions；Editor buffer不构成输入覆盖。");
        }

        [Test]
        public void MissingConfigCannotCreateMouse()
        {
            int count = InputSystem.devices.Count;
            Assert.That(Queue("leftButton", true), Does.StartWith("Rejected:"));
            Assert.That(InputSystem.devices.Count, Is.EqualTo(count));
            Assert.That(OwnMouse(), Is.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void PointerDisabledRejectsEvenWhenKeyboardWasAuthorized(bool keyboard)
        {
            Configure(false, keyboard);
            int count = InputSystem.devices.Count;
            Assert.That(Queue("leftButton", true), Does.StartWith("Rejected:"));
            Assert.That(InputSystem.devices.Count, Is.EqualTo(count));
            Assert.That(OwnMouse(), Is.Null);
        }

        [TestCase(null)] [TestCase("")] [TestCase("rightButton")]
        [TestCase("middleButton")] [TestCase("left")] [TestCase("LeftButton")]
        public void AnythingOutsideExactLeftButtonWhitelistIsRejected(string button)
        {
            Configure(true);
            int count = InputSystem.devices.Count;
            Assert.That(Queue(button, true), Does.StartWith("Rejected:"));
            Assert.That(InputSystem.devices.Count, Is.EqualTo(count));
            Assert.That(OwnMouse(), Is.Null);
        }

        [Test]
        public void LeftButtonFeedsRealActionAndPersistsUntilQueuedRelease()
        {
            Configure(true);
            _action = new InputAction("Probe actual mouse edge", InputActionType.Button, "<Mouse>/leftButton");
            int performed = 0, cancelled = 0;
            _action.performed += _ => performed++;
            _action.canceled += _ => cancelled++;
            _action.Enable();
            Assert.That(Queue("leftButton", true), Does.StartWith("QueuedSyntheticPointer:"));
            Assert.That(performed, Is.Zero, "只排队，不能重入InputSystem.Update或直接触发Action。");
            PlayerUpdate();
            var own = OwnMouse();
            Assert.That(own, Is.Not.Null);
            Assert.That(own.leftButton.isPressed, Is.True);
            Assert.That(_action.activeControl.device, Is.SameAs(own));
            Assert.That(performed, Is.EqualTo(1));
            Assert.That(Vector2.Distance(own.position.ReadValue(), new Vector2(Screen.width * .5f, Screen.height * .5f)), Is.LessThan(.01f));
            Assert.That(own.rightButton.isPressed, Is.False);
            Assert.That(own.middleButton.isPressed, Is.False);
            PlayerUpdate();
            Assert.That(own.leftButton.isPressed, Is.True);
            Assert.That(performed, Is.EqualTo(1));
            Assert.That(Queue("leftButton", false), Does.StartWith("QueuedSyntheticPointer:"));
            Assert.That(own.leftButton.isPressed, Is.True, "松键也等正常Player Input消费，不能写设备当前状态。");
            PlayerUpdate();
            Assert.That(own.leftButton.isPressed, Is.False);
            Assert.That(_action.IsPressed(), Is.False);
            Assert.That(cancelled, Is.EqualTo(1));
        }

        [Test]
        public void DisableRemovesOwnMouseAndPreservesAnotherDeviceAndItsHeldButton()
        {
            Configure(true);
            _existing = InputSystem.AddDevice<Mouse>();
            InputSystem.QueueStateEvent(_existing, new MouseState().WithButton(MouseButton.Right));
            PlayerUpdate();
            Assert.That(_existing.rightButton.isPressed, Is.True);
            int count = InputSystem.devices.Count;
            Assert.That(Queue("leftButton", true), Does.StartWith("QueuedSyntheticPointer:"));
            PlayerUpdate();
            var own = OwnMouse();
            Assert.That(InputSystem.devices.Count, Is.EqualTo(count + 1));
            typeof(SinglePlayerInventoryRegressionProbe).GetMethod("OnDisable", Private).Invoke(_probe, null);
            Assert.That(OwnMouse(), Is.Null);
            Assert.That(own.added, Is.False);
            Assert.That(_existing.added, Is.True);
            Assert.That(_existing.rightButton.isPressed, Is.True);
            Assert.That(InputSystem.devices.Count, Is.EqualTo(count));
        }

        [Test]
        public void RepeatedDisableThenDestroyKeepsCleanupIdempotent()
        {
            Configure(true);
            _existing = InputSystem.AddDevice<Mouse>();
            Assert.That(Queue("leftButton", true), Does.StartWith("QueuedSyntheticPointer:"));
            PlayerUpdate();
            var own = OwnMouse();
            // EditMode中的非ExecuteAlways组件没有经历真正Player激活，DestroyImmediate不会保证
            // 派发其OnDisable。此处显式验证清理入口可重复，再销毁夹具；Native的Destroy-only
            // Unity回调属于实际Player验证，不能为通过此测试给产品加ExecuteAlways或放宽opt-in。
            var disable = typeof(SinglePlayerInventoryRegressionProbe).GetMethod("OnDisable", Private);
            Assert.That(disable, Is.Not.Null);
            disable.Invoke(_probe, null);
            Assert.That(own.added, Is.False);
            Assert.That(OwnMouse(), Is.Null);
            disable.Invoke(_probe, null);
            Object.DestroyImmediate(_root); _root = null; _probe = null;
            Assert.That(own.added, Is.False);
            Assert.That(_existing.added, Is.True);
        }

        [TearDown]
        public void RestoreOnlyFixtureState()
        {
            _action?.Dispose(); _action = null;
            if (_root != null) Object.DestroyImmediate(_root);
            if (_existing != null && _existing.added) InputSystem.RemoveDevice(_existing);
            if (_settings != null) _settings.SetInternalFeatureFlag(PlayerUpdates, _savedPlayerUpdates);
            if (_previousWindow != null) _previousWindow.Focus();
        }
    }
}
