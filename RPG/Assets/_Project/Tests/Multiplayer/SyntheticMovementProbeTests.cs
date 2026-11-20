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
    /// 显式 Native 移动诊断的有限输入边界：真实 Action 接收 down/up，时限只负责排队松键，
    /// 不直接推进角色、不消费 SampleInput，不把排队状态或报告时钟当成原 PlayerLoop 的执行。
    /// </summary>
    public sealed class SyntheticMovementProbeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string PlayerUpdates = "RUN_PLAYER_UPDATES_IN_EDIT_MODE";
        private GameObject _root;
        private SinglePlayerInventoryRegressionProbe _probe;
        private InputAction _action;
        private Keyboard _other;
        private InputSettings _settings;
        private bool _oldPlayerUpdates;
        private EditorWindow _window;

        [SetUp] public void CreateFixture()
        {
            _settings = InputSystem.settings;
            _oldPlayerUpdates = (bool)typeof(InputSettings).GetMethod("IsFeatureEnabled", Private).Invoke(_settings, new object[] { PlayerUpdates });
            _settings.SetInternalFeatureFlag(PlayerUpdates, true);
            _window = EditorWindow.focusedWindow;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            _root = new GameObject("Explicit bounded move probe"); _probe = _root.AddComponent<SinglePlayerInventoryRegressionProbe>();
        }
        private void Configure(bool keyboard, bool movement, bool pointer = false)
        {
            var type = typeof(SinglePlayerInventoryRegressionProbe).GetNestedType("Config", BindingFlags.NonPublic);
            var config = Activator.CreateInstance(type);
            type.GetField("AllowSyntheticKeyboard").SetValue(config, keyboard);
            type.GetField("AllowSyntheticPointer").SetValue(config, pointer);
            var field = type.GetField("AllowSyntheticMovement");
            Assert.That(field, Is.Not.Null, "移动必须有独立且默认关闭的私有 opt-in。");
            field.SetValue(config, movement); Set("_config", config);
        }
        private object Call(string name, Type[] types, params object[] values)
        {
            var method = typeof(SinglePlayerInventoryRegressionProbe).GetMethod(name, Private, null, types, null);
            Assert.That(method, Is.Not.Null, "缺少真实 Input System 有界诊断入口：" + name);
            return method.Invoke(_probe, values);
        }
        private string Move(string key, float seconds) => (string)Call("QueueSyntheticMove", new[] { typeof(string), typeof(float) }, key, seconds);
        private void Tick(double now) => Call("ProgressSyntheticMove", new[] { typeof(double) }, now);
        private Keyboard Own => (Keyboard)typeof(SinglePlayerInventoryRegressionProbe).GetField("_syntheticKeyboard", Private).GetValue(_probe);
        private object Report => Call("Capture", Type.EmptyTypes);
        private static T Value<T>(object value, string field) => (T)value.GetType().GetField(field).GetValue(value);
        private void Set(string field, object value) => typeof(SinglePlayerInventoryRegressionProbe).GetField(field, Private).SetValue(_probe, value);
        private static void PlayerUpdate()
        { InputSystem.Update(); Assert.That(InputState.currentUpdateType, Is.Not.EqualTo(InputUpdateType.Editor)); }

        [Test] public void MissingConfigRejectsMovementWithoutCreatingADevice()
        { int count = InputSystem.devices.Count; Assert.That(Move("W", .2f), Does.StartWith("Rejected:")); Assert.That(Own, Is.Null); Assert.That(InputSystem.devices.Count, Is.EqualTo(count)); }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)]
        public void MovementRequiresBothExplicitPermissions(bool keyboard, bool movement)
        { Configure(keyboard, movement); Assert.That(Move("W", .2f), Does.StartWith("Rejected:")); Assert.That(Own, Is.Null); }

        [TestCase(null)] [TestCase("")] [TestCase("w")] [TestCase("WA")] [TestCase("F")]
        public void MovementOnlyAcceptsOneExactWasdKey(string key)
        { Configure(true, true); Assert.That(Move(key, .2f), Does.StartWith("Rejected:")); Assert.That(Own, Is.Null); }

        [TestCase(0f)] [TestCase(.049f)] [TestCase(1.01f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void MovementRejectsUnboundedOrInvalidDurations(float duration)
        { Configure(true, true); Assert.That(Move("W", duration), Does.StartWith("Rejected:")); Assert.That(Own, Is.Null); }

        [TestCase("W", Key.W)] [TestCase("A", Key.A)] [TestCase("S", Key.S)] [TestCase("D", Key.D)]
        public void BoundedPulseQueuesBothRealActionEdgesAndCompletesOnlyAfterProcessedRelease(string label, Key key)
        {
            Configure(true, true);
            _action = new InputAction("Bounded native movement", InputActionType.Button, "<Keyboard>/" + label.ToLowerInvariant());
            int down = 0, up = 0; _action.performed += _ => down++; _action.canceled += _ => up++; _action.Enable();
            Assert.That(Move(label, .2f), Does.StartWith("QueuedSyntheticMove:"));
            Assert.That(down, Is.Zero, "诊断不能重入 InputSystem.Update。");
            PlayerUpdate(); Assert.That(Own[key].isPressed, Is.True); Assert.That(down, Is.EqualTo(1));
            Assert.That(Value<uint>(Report, "SyntheticMoveCompletedSequence"), Is.Zero);
            Tick(Time.realtimeSinceStartupAsDouble + 2);
            Assert.That(Own[key].isPressed, Is.True, "到期只排 KeyUp，尚不能报告设备已释放。");
            Assert.That(Value<uint>(Report, "SyntheticMoveCompletedSequence"), Is.Zero);
            PlayerUpdate(); Assert.That(Own[key].isPressed, Is.False); Assert.That(up, Is.EqualTo(1));
            Tick(Time.realtimeSinceStartupAsDouble + 3);
            var report = Report;
            Assert.That(Value<uint>(report, "SyntheticMoveCompletedSequence"), Is.EqualTo(1u));
            Assert.That(Value<bool>(report, "SyntheticMovePulseActive"), Is.False);
            Assert.That(Value<string>(report, "SyntheticMovePulseKey"), Is.EqualTo(label));
        }

        [Test] public void OverlappingPulseCannotReplaceTheActiveKeyOrExtendItsDeadline()
        {
            Configure(true, true); Assert.That(Move("W", .2f), Does.StartWith("QueuedSyntheticMove:")); PlayerUpdate();
            Assert.That(Move("D", 1f), Does.StartWith("Rejected:")); Assert.That(Own.dKey.isPressed, Is.False);
            Tick(Time.realtimeSinceStartupAsDouble + 2); PlayerUpdate(); Tick(Time.realtimeSinceStartupAsDouble + 3);
            Assert.That(Own.wKey.isPressed, Is.False); Assert.That(Value<uint>(Report, "SyntheticMovePulseSequence"), Is.EqualTo(1u));
        }

        [Test] public void UpdateReleasesExpiredPulseEvenWhenColdReportIsNotDue()
        {
            Configure(true, true); Move("W", .2f); PlayerUpdate();
            Set("_next", double.MaxValue); Set("_syntheticMoveReleaseAt", Time.realtimeSinceStartupAsDouble - 1);
            Call("Update", Type.EmptyTypes); PlayerUpdate(); Call("Update", Type.EmptyTypes);
            Assert.That(Own.wKey.isPressed, Is.False, "自动释放不能被 1Hz 文件报告节流挡住。");
            Assert.That(Value<uint>(Report, "SyntheticMoveCompletedSequence"), Is.EqualTo(1u));
        }

        [Test] public void DisableRemovesOnlyItsOwnMovingKeyboard()
        {
            Configure(true, true); _other = InputSystem.AddDevice<Keyboard>();
            InputSystem.QueueStateEvent(_other, new KeyboardState(Key.D)); PlayerUpdate();
            Move("W", .2f); PlayerUpdate(); var own = Own; Call("OnDisable", Type.EmptyTypes);
            Assert.That(own.added, Is.False); Assert.That(_other.added, Is.True); Assert.That(_other.dKey.isPressed, Is.True);
        }

        [Test] public void ExplicitPointerPositionFeedsActualPositionAndButtonActions()
        {
            Configure(false, false, true);
            var point = new Vector2(Screen.width * .25f, Screen.height * .6f);
            _action = new InputAction("Original pointer position", InputActionType.PassThrough, "<Mouse>/position"); _action.Enable();
            Assert.That((string)Call("QueueSyntheticPointerAt", new[] { typeof(string), typeof(Vector2), typeof(bool) }, "leftButton", point, true), Does.StartWith("QueuedSyntheticPointer:"));
            PlayerUpdate(); Assert.That(_action.ReadValue<Vector2>(), Is.EqualTo(point));
            var mouse = (Mouse)typeof(SinglePlayerInventoryRegressionProbe).GetField("_syntheticMouse", Private).GetValue(_probe);
            Assert.That(mouse.leftButton.isPressed, Is.True);
            Call("QueueSyntheticPointerAt", new[] { typeof(string), typeof(Vector2), typeof(bool) }, "leftButton", point, false);
            PlayerUpdate(); Assert.That(mouse.leftButton.isPressed, Is.False);
        }

        [TestCase(-1f, 10f)] [TestCase(10f, -1f)] [TestCase(float.NaN, 10f)] [TestCase(float.PositiveInfinity, 10f)]
        public void PointerRejectsNonFiniteOrOutsideScreenPositionBeforeCreatingDevice(float x, float y)
        {
            Configure(false, false, true);
            Assert.That((string)Call("QueueSyntheticPointerAt", new[] { typeof(string), typeof(Vector2), typeof(bool) }, "leftButton", new Vector2(x, y), false), Does.StartWith("Rejected:"));
            Assert.That(typeof(SinglePlayerInventoryRegressionProbe).GetField("_syntheticMouse", Private).GetValue(_probe), Is.Null);
        }

        [TearDown] public void Cleanup()
        {
            _action?.Dispose(); _action = null;
            if (_probe != null) Call("OnDisable", Type.EmptyTypes);
            if (_root != null) Object.DestroyImmediate(_root);
            if (_other != null && _other.added) InputSystem.RemoveDevice(_other);
            if (_settings != null) _settings.SetInternalFeatureFlag(PlayerUpdates, _oldPlayerUpdates);
            if (_window != null) _window.Focus();
        }
    }
}


