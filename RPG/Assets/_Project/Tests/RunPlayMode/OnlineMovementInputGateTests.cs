#if UNITY_EDITOR
using System;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.Multiplayer;
using Mirror;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>
    /// 使用真实 InputSystem_Actions / performed 回调和生产 World UI 门。两个键盘事件之间不跑运动 Tick，
    /// 专门复现“关闭 UI 后、下一次 30Hz SampleInput 前的新按键”，不借反射直接写 pending 来冒充输入。
    /// </summary>
    public sealed class OnlineMovementInputGateTests : InputTestFixture
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _worldRoot, _playerRoot;
        private OnlineWorldBootstrap _world;
        private OnlineMovementInputGateController _player;
        private HealthComponent _health;
        private Keyboard _keyboard;
        private object _previousConnectState;
        private bool _borrowedConnection;
        private float _previousTimeScale;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            _previousTimeScale = Time.timeScale; Time.timeScale = 1;
            Assert.That(NetworkClient.active || NetworkServer.active, Is.False,
                "夹具仅借用空闲连接状态位，不能接管真实 Client/DS。");
            var field = typeof(NetworkClient).GetField("connectState", BindingFlags.Static | BindingFlags.NonPublic);
            _previousConnectState = field.GetValue(null); _borrowedConnection = true;
            field.SetValue(null, Enum.Parse(field.FieldType, "Connected"));
            _keyboard = InputSystem.AddDevice<Keyboard>(); InputSystem.AddDevice<Mouse>();
            Keys();
            _worldRoot = new GameObject("Inactive movement input gate World"); _worldRoot.SetActive(false);
            _world = _worldRoot.AddComponent<OnlineWorldBootstrap>();
            _playerRoot = new GameObject("Owned movement InputSystem actor"); _playerRoot.SetActive(false);
            _playerRoot.AddComponent<Animator>();
            _health = _playerRoot.AddComponent<HealthComponent>();
            _health.ConfigureExecutionMode(CombatExecutionMode.Replica);
            typeof(HealthComponent).GetMethod("Awake", Private).Invoke(_health, null);
            _player = _playerRoot.AddComponent<OnlineMovementInputGateController>();
            _player.ConfigureExecutionMode(PlayerExecutionMode.Prediction); _player.InitializeInput();
            typeof(OnlineWorldBootstrap).GetField("<LocalPlayer>k__BackingField", Private).SetValue(_world, _player);
            // 出生时的 release 栅栏应先看到一次真实“未按住”；后续测试不把初始化门混入 UI 恢复缺陷。
            _player.SampleInput(1, 1);
        }

        [TearDown]
        public override void TearDown()
        {
            try
            {
                if (_player != null) _player.ReleaseInput();
                if (_playerRoot != null) Object.DestroyImmediate(_playerRoot);
                if (_worldRoot != null) Object.DestroyImmediate(_worldRoot);
                if (_borrowedConnection)
                {
                    typeof(NetworkClient).GetField("connectState", BindingFlags.Static | BindingFlags.NonPublic)
                        .SetValue(null, _previousConnectState);
                    _borrowedConnection = false;
                }
                Time.timeScale = _previousTimeScale;
            }
            finally { base.TearDown(); }
        }

        [TestCase(Key.Space)]
        [TestCase(Key.LeftShift)]
        public void FreshPressAfterUiClosesIsCapturedBeforeNextThirtyHzSample(Key key)
        {
            _world.LocalInputSuppressed = true;
            _world.LocalInputSuppressed = false;
            // 完整的新 performed 发生于 UI 门解除之后；此处刻意没有先采样一份“释放帧”。
            Keys(key);
            var frame = _player.SampleInput(2, 1);
            AssertPressed(frame, key, true);
            Assert.That(frame.InputSuppressed, Is.False);
            AssertPressed(_player.SampleInput(3, 1), key, false); // 一个 performed 只能消费一次。
        }

        [TestCase(Key.Space)]
        [TestCase(Key.LeftShift)]
        public void FreshShortPulseAfterUiClosesSurvivesCanceledBeforeNextSample(Key key)
        {
            _world.LocalInputSuppressed = true; _world.LocalInputSuppressed = false;
            Keys(key); Keys();
            var frame = _player.SampleInput(2, 1);
            AssertPressed(frame, key, true);
            Assert.That(frame.JumpHeld, Is.False);
            AssertPressed(_player.SampleInput(3, 1), key, false);
        }

        [TestCase(Key.Space)]
        [TestCase(Key.LeftShift)]
        public void PressHeldInsideUiWaitsForReleaseButNextFreshPressDoesNotNeedAnExtraSample(Key key)
        {
            _world.LocalInputSuppressed = true;
            Keys(key);
            AssertPressed(_player.SampleInput(2, 1, true), key, false);
            _world.LocalInputSuppressed = false;
            var held = _player.SampleInput(3, 1);
            AssertPressed(held, key, false);
            if (key == Key.Space) Assert.That(held.JumpHeld, Is.False, "UI 中的旧按住不能解除 Jump Cut 的 release 栅栏。");
            // release 和新 performed 都是真实事件，可以落在同一个逻辑 Tick 间隙。
            Keys(); Keys(key);
            AssertPressed(_player.SampleInput(4, 1), key, true);
        }

        [TestCase(Key.Space)]
        [TestCase(Key.LeftShift)]
        public void PressReleasedWhileUiWasOpenDoesNotBlockTheNextFreshPress(Key key)
        {
            _world.LocalInputSuppressed = true; Keys(key); Keys();
            _world.LocalInputSuppressed = false;
            Keys(key);
            AssertPressed(_player.SampleInput(2, 1), key, true);
        }

        [TestCase("inventory")]
        [TestCase("focus")]
        [TestCase("death")]
        [TestCase("frozen")]
        [TestCase("disconnected")]
        public void ClosingMenuCannotReleaseAnotherInputGate(string remainingGate)
        {
            _world.LocalInputSuppressed = true;
            switch (remainingGate)
            {
                case "inventory":
                    typeof(OnlineWorldBootstrap).GetMethod("OnInventoryVisibility", Private).Invoke(_world,
                        new object[] { new InventoryPresentationVisibilityEvent { OwnerId = 17, Open = true } });
                    break;
                case "focus":
                    typeof(PlayerControllerBase).GetMethod("OnApplicationFocus", Private).Invoke(_player, new object[] { false });
                    break;
                case "death":
                    _health.ApplyReplicatedState(new HealthReplicaSnapshot { CurrentHp = 0, MaxHp = 100 });
                    break;
                case "frozen":
                    typeof(OnlineWorldBootstrap).GetField("_frozen", Private).SetValue(_world, true);
                    break;
                case "disconnected":
                    var field = typeof(NetworkClient).GetField("connectState", BindingFlags.Static | BindingFlags.NonPublic);
                    field.SetValue(null, Enum.Parse(field.FieldType, "Disconnected"));
                    break;
            }
            _world.LocalInputSuppressed = false;
            Keys(Key.Space, Key.LeftShift);
            bool suppressed = _world.LocalInputSuppressed || _world.Frozen || !NetworkClient.isConnected || !_health.IsAlive;
            var frame = _player.SampleInput(2, 1, suppressed);
            Assert.That(frame.JumpPressed, Is.False); Assert.That(frame.DashPressed, Is.False);
            Assert.That(frame.JumpHeld, Is.False); Assert.That(frame.InputSuppressed, Is.True,
                "关闭一个菜单只能释放该 Owner，其他捕获、失焦、死亡、冻结和断线仍应阻断输入。");
        }

        private void Keys(params Key[] keys)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(keys)); InputSystem.Update();
        }
        private static void AssertPressed(PlayerSimulationFrame frame, Key key, bool expected)
        {
            Assert.That(key == Key.Space ? frame.JumpPressed : frame.DashPressed, Is.EqualTo(expected),
                "新的 Jump/Dash 应由 performed 锁存，不能依赖下一次 SampleInput 才知道 UI 门已解除。");
        }
    }

    /// <summary>只借公开的 PlayerControllerBase 输入能力；不覆写采样、回调、release 或门禁规则。</summary>
    public sealed class OnlineMovementInputGateController : PlayerControllerBase
    {
        public void InitializeInput() { base.Awake(); base.OnEnable(); }
        public void ReleaseInput() { base.OnDisable(); base.OnDestroy(); }
    }
}
#endif
