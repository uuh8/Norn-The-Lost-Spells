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
    /// 只测显式 Native 诊断边界的设备所有权和事件传递；不启动 HTTP/KCP，也不直接调用 Gameplay 请求。
    /// 真实生产 Actor 的数字键链由 OnlineCastDriverTests 覆盖，Native 再验证完整收据/Owner/世界结果。
    /// </summary>
    public sealed class SyntheticKeyboardProbeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string PlayerUpdates = "RUN_PLAYER_UPDATES_IN_EDIT_MODE";
        private GameObject _root;
        private SinglePlayerInventoryRegressionProbe _probe;
        private Keyboard _existingKeyboard;
        private InputAction _action;
        private InputSettings _settings;
        private bool _previousPlayerUpdates, _savedPlayerUpdates;
        private EditorWindow _previousWindow;

        [SetUp]
        public void EnableOnlyFixturePlayerUpdates()
        {
            _previousWindow = EditorWindow.focusedWindow;
            _settings = InputSystem.settings;
            var feature = typeof(InputSettings).GetMethod("IsFeatureEnabled", Private);
            Assert.That(feature, Is.Not.Null);
            _previousPlayerUpdates = (bool)feature.Invoke(_settings, new object[] { PlayerUpdates });
            _savedPlayerUpdates = true;
            _settings.SetInternalFeatureFlag(PlayerUpdates, true);
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            // 这里验证设备排队与Player Actions，不以Editor焦点前置失败冒充行为RED。
            // Native场景仍由正式World/Driver自己检查Application.isFocused。
            _root = new GameObject("Explicit synthetic keyboard probe fixture");
            _probe = _root.AddComponent<SinglePlayerInventoryRegressionProbe>();
        }

        private void Configure(bool allowed)
        {
            var config = typeof(SinglePlayerInventoryRegressionProbe).GetNestedType("Config", BindingFlags.NonPublic);
            Assert.That(config, Is.Not.Null);
            var allow = config.GetField("AllowSyntheticKeyboard", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(allow, Is.Not.Null, "仅安装诊断对象不能默认授权模拟键盘；私有配置需显式 opt-in。");
            var value = Activator.CreateInstance(config);
            allow.SetValue(value, allowed);
            typeof(SinglePlayerInventoryRegressionProbe).GetField("_config", Private).SetValue(_probe, value);
        }

        private string QueueKey(string key, bool pressed)
        {
            var method = typeof(SinglePlayerInventoryRegressionProbe).GetMethod("QueueSyntheticKey", Private,
                null, new[] { typeof(string), typeof(bool) }, null);
            Assert.That(method, Is.Not.Null, "Native 探针必须经 QueueStateEvent，不调用 RequestSelection/RequestContainerThrow 冒充键盘。");
            return (string)method.Invoke(_probe, new object[] { key, pressed });
        }

        private Keyboard OwnKeyboard()
        {
            var field = typeof(SinglePlayerInventoryRegressionProbe).GetField("_syntheticKeyboard", Private);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(_probe) as Keyboard;
        }

        private static void PlayerUpdate()
        {
            InputSystem.Update();
            Assert.That(InputState.currentUpdateType, Is.Not.EqualTo(InputUpdateType.Editor),
                "设备事件必须推进 Player Actions，不能只更新 Editor buffer。");
        }

        [Test]
        public void MissingExplicitConfigCannotCreateSyntheticKeyboard()
        {
            int before = InputSystem.devices.Count;
            Assert.That(QueueKey("4", true), Does.StartWith("Rejected:"));
            Assert.That(InputSystem.devices.Count, Is.EqualTo(before));
            Assert.That(OwnKeyboard(), Is.Null);
        }

        [Test]
        public void ExplicitConfigWithSyntheticKeyboardDisabledCannotCreateDevice()
        {
            Configure(false);
            int before = InputSystem.devices.Count;
            Assert.That(QueueKey("4", true), Does.StartWith("Rejected:"));
            Assert.That(InputSystem.devices.Count, Is.EqualTo(before));
            Assert.That(OwnKeyboard(), Is.Null);
        }

        [TestCase("1", Key.Digit1)] [TestCase("2", Key.Digit2)] [TestCase("3", Key.Digit3)]
        [TestCase("4", Key.Digit4)] [TestCase("5", Key.Digit5)] [TestCase("6", Key.Digit6)]
        [TestCase("7", Key.Digit7)] [TestCase("8", Key.Digit8)] [TestCase("9", Key.Digit9)]
        [TestCase("0", Key.Digit0)] [TestCase("F", Key.F)] [TestCase("G", Key.G)]
        [TestCase("Tab", Key.Tab)]
        public void ExplicitSyntheticKeyFeedsRealActionAndStaysHeldUntilRelease(string label, Key key)
        {
            Configure(true);
            _action = new InputAction("Native probe actual key", InputActionType.Button, "<Keyboard>/" + label.ToLowerInvariant());
            int performed = 0, cancelled = 0;
            _action.performed += _ => performed++;
            _action.canceled += _ => cancelled++;
            _action.Enable();
            Assert.That(QueueKey(label, true), Does.StartWith("QueuedSyntheticKey:"));
            Assert.That(performed, Is.Zero, "探针 Update 只排队；不递归调用 InputSystem.Update 或直接触发 Action。");
            PlayerUpdate();
            var own = OwnKeyboard();
            Assert.That(own, Is.Not.Null);
            Assert.That(own[key].isPressed, Is.True);
            Assert.That(_action.activeControl.device, Is.SameAs(own));
            Assert.That(performed, Is.EqualTo(1)); Assert.That(_action.IsPressed(), Is.True);
            PlayerUpdate();
            Assert.That(own[key].isPressed, Is.True, "文件命令之间持续持键，不能在报告写盘时自动松开。");
            Assert.That(performed, Is.EqualTo(1));
            Assert.That(QueueKey(label, false), Does.StartWith("QueuedSyntheticKey:"));
            Assert.That(own[key].isPressed, Is.True, "松开也应等待正常 Player Input 更新。");
            PlayerUpdate();
            Assert.That(own[key].isPressed, Is.False);
            Assert.That(_action.IsPressed(), Is.False); Assert.That(cancelled, Is.EqualTo(1));
        }

        [TestCase(null)] [TestCase("")] [TestCase("W")] [TestCase("leftButton")]
        public void KeysOutsideTheExplicitDiagnosticWhitelistAreRejected(string key)
        {
            Configure(true);
            int before = InputSystem.devices.Count;
            Assert.That(QueueKey(key, true), Does.StartWith("Rejected:"));
            Assert.That(InputSystem.devices.Count, Is.EqualTo(before));
            Assert.That(OwnKeyboard(), Is.Null);
        }

        [Test]
        public void ProbeDisableRemovesOnlyItsOwnKeyboardAndPreservesExistingDeviceState()
        {
            Configure(true);
            _existingKeyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.QueueStateEvent(_existingKeyboard, new KeyboardState(Key.W)); PlayerUpdate();
            Assert.That(_existingKeyboard.wKey.isPressed, Is.True);
            int before = InputSystem.devices.Count;
            Assert.That(QueueKey("G", true), Does.StartWith("QueuedSyntheticKey:")); PlayerUpdate();
            var own = OwnKeyboard();
            Assert.That(InputSystem.devices.Count, Is.EqualTo(before + 1));
            typeof(SinglePlayerInventoryRegressionProbe).GetMethod("OnDisable", Private).Invoke(_probe, null);
            Assert.That(OwnKeyboard(), Is.Null);
            Assert.That(own.added, Is.False);
            Assert.That(_existingKeyboard.added, Is.True);
            Assert.That(_existingKeyboard.wKey.isPressed, Is.True, "清理不得 ResetDevice 或移除其它真实/虚拟设备。");
            Assert.That(InputSystem.devices.Count, Is.EqualTo(before));
        }

        [TearDown]
        public void RestoreOnlyFixtureState()
        {
            _action?.Dispose(); _action = null;
            if (_probe != null)
                typeof(SinglePlayerInventoryRegressionProbe).GetMethod("OnDisable", Private)?.Invoke(_probe, null);
            if (_root != null) Object.DestroyImmediate(_root);
            if (_existingKeyboard != null && _existingKeyboard.added) InputSystem.RemoveDevice(_existingKeyboard);
            if (_savedPlayerUpdates && _settings != null)
                _settings.SetInternalFeatureFlag(PlayerUpdates, _previousPlayerUpdates);
            if (_previousWindow != null) _previousWindow.Focus();
        }
    }
}
