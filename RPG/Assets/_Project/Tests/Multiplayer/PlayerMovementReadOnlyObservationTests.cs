using System.Collections.Generic;
using System.Reflection;
using Game.Character;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 诊断只能观察生产 Controller，不能为了读取报告消费输入或推进角色。
    /// 这里通过反射发现新增读口，使 RED 阶段仍可编译；真实 SampleInput 保留为消费边沿的对照。
    /// 测试程序集不为清理输入包装器增加 InputSystem 依赖，输入资产的 EditMode 销毁也只属于夹具。
    /// </summary>
    public sealed class PlayerMovementReadOnlyObservationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameObject> _roots = new List<GameObject>(2);
        private readonly List<ReadOnlyObservationTestController> _players = new List<ReadOnlyObservationTestController>(2);
        private float _previousTimeScale;

        [SetUp]
        public void SetUp()
        {
            _previousTimeScale = Time.timeScale;
            Time.timeScale = 1f;
            Assert.That(PlayerControllerBase.Current == null, Is.True,
                "夹具需要空闲的本机输入入口，不能解绑或接管正在运行的真实玩家。");
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                var player = _players[i];
                if (player == null) continue;
                player.UnbindForObservationTest();
                // generated Dispose 使用延迟 Destroy；EditMode 中对称销毁夹具创建的 asset，
                // 再清引用，让真实 OnDestroy 不会二次 Dispose 已销毁的 Unity Object。
                var field = typeof(PlayerControllerBase).GetField("_inputActions", Private);
                object actions = field.GetValue(player);
                if (actions != null)
                {
                    var asset = actions.GetType().GetProperty("asset").GetValue(actions) as Object;
                    if (asset != null) Object.DestroyImmediate(asset);
                    field.SetValue(player, null);
                }
            }
            for (int i = _roots.Count - 1; i >= 0; i--)
                if (_roots[i] != null) Object.DestroyImmediate(_roots[i]);
            _players.Clear();
            _roots.Clear();
            Time.timeScale = _previousTimeScale;
        }

        [Test]
        public void RepeatedObservationPreservesInputGatesAndPendingUntilRealSampleConsumesOnce()
        {
            var player = Create(PlayerExecutionMode.Prediction);
            // 关闭真实 Action map，排除测试时物理键盘状态；仍保留生产包装器及订阅，
            // 同时验证报告不会把“已订阅”误当成“Action map 已启用”。
            object actions = GetPrivate(player, "_inputActions");
            object map = actions.GetType().GetProperty("Player").GetValue(actions);
            map.GetType().GetMethod("Disable").Invoke(map, null);
            SetPrivate(player, "_hasFocus", false);
            SetPrivate(player, "_inputSuppressed", true);
            SetPrivate(player, "_onlineInputBlocked", true);
            SetPrivate(player, "_waitJumpRelease", true);
            SetPrivate(player, "_waitDashRelease", true);
            SetPrivate(player, "_jumpPressedPending", true);
            SetPrivate(player, "_dashPressedPending", true);
            SetPrivate(player, "_moveInput", new Vector2(.25f, -.75f));
            player.VerticalVelocity = 4f;
            player.JumpBufferCounter = .07f;
            player.DashBufferCounter = .09f;
            player.CoyoteTimeCounter = .11f;
            player.AttackCooldownCounter = .13f;
            Vector3 position = player.transform.position;
            var state = player.StateMachine.CurrentState;

            for (int i = 0; i < 3; i++)
            {
                object view = Capture(player);
                Assert.That(Value<bool>(view, "InputSubscribed"), Is.True);
                Assert.That(Value<bool>(view, "ActionAssetAvailable"), Is.True);
                Assert.That(Value<bool>(view, "PlayerMapEnabled"), Is.False);
                Assert.That(Value<bool>(view, "MoveActionEnabled"), Is.False);
                Assert.That(Value<bool>(view, "JumpActionEnabled"), Is.False);
                Assert.That(Value<Vector2>(view, "MoveActionValue"), Is.EqualTo(Vector2.zero));
                Assert.That(Value<Vector2>(view, "ProcessedMove"), Is.EqualTo(new Vector2(.25f, -.75f)));
                Assert.That(Value<bool>(view, "HasFocus"), Is.False);
                Assert.That(Value<bool>(view, "InputSuppressed"), Is.True);
                Assert.That(Value<bool>(view, "OnlineInputBlocked"), Is.True);
                Assert.That(Value<bool>(view, "WaitJumpRelease"), Is.True);
                Assert.That(Value<bool>(view, "WaitDashRelease"), Is.True);
                Assert.That(Value<bool>(view, "JumpPending"), Is.True);
                Assert.That(Value<bool>(view, "DashPending"), Is.True);
                Assert.That(Value<Vector3>(view, "Position"), Is.EqualTo(position));
                Assert.That(Value<float>(view, "VerticalVelocity"), Is.EqualTo(4f));
            }
            Assert.That(GetPrivate(player, "_inputSuppressed"), Is.EqualTo(true));
            Assert.That(GetPrivate(player, "_onlineInputBlocked"), Is.EqualTo(true));
            Assert.That(GetPrivate(player, "_hasFocus"), Is.EqualTo(false));
            Assert.That(GetPrivate(player, "_waitJumpRelease"), Is.EqualTo(true));
            Assert.That(GetPrivate(player, "_waitDashRelease"), Is.EqualTo(true));
            Assert.That(player.transform.position, Is.EqualTo(position));
            Assert.That(player.StateMachine.CurrentState, Is.SameAs(state));
            Assert.That(player.JumpBufferCounter, Is.EqualTo(.07f));
            Assert.That(player.DashBufferCounter, Is.EqualTo(.09f));
            Assert.That(player.CoyoteTimeCounter, Is.EqualTo(.11f));
            Assert.That(player.AttackCooldownCounter, Is.EqualTo(.13f));

            // 只打开夹具事先设置的门；由真正的生产采样解除已释放键的栅栏并消费 pending。
            SetPrivate(player, "_hasFocus", true);
            SetPrivate(player, "_onlineInputBlocked", false);
            var first = player.SampleInput(1, 1);
            var second = player.SampleInput(2, 1);
            Assert.That(first.InputSuppressed, Is.False);
            Assert.That(first.JumpPressed, Is.True);
            Assert.That(first.DashPressed, Is.True);
            Assert.That(second.JumpPressed, Is.False);
            Assert.That(second.DashPressed, Is.False);
            Assert.That(player.transform.position, Is.EqualTo(position));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplicaWithoutLocalInputCanBeObservedBeforeOrAfterInitialization(bool initialize)
        {
            var player = Create(PlayerExecutionMode.Replica, initialize);
            object view = Capture(player);
            Assert.That(Value<bool>(view, "Initialized"), Is.EqualTo(initialize));
            Assert.That(Value<bool>(view, "ActionAssetAvailable"), Is.False);
            Assert.That(Value<bool>(view, "PlayerMapEnabled"), Is.False);
            Assert.That(Value<bool>(view, "JumpHeld"), Is.False);
            Assert.That(Value<Vector2>(view, "MoveActionValue"), Is.EqualTo(Vector2.zero));
            Assert.That(Value<PlayerExecutionMode>(view, "ExecutionMode"), Is.EqualTo(PlayerExecutionMode.Replica));
            Assert.That(Value<bool>(view, "ControllerAvailable"), Is.EqualTo(initialize));
            Assert.That(CurrentActor(player), Is.Null);
            Assert.That(GetPrivate(player, "_inputActions"), Is.Null,
                "观察远端角色不得补建本机 InputSystem_Actions。");
        }

        [Test]
        public void DisabledActorAndControllerRemainSafeToObserveWithoutRebindingInput()
        {
            var player = Create(PlayerExecutionMode.Prediction);
            player.UnbindForObservationTest();
            player.gameObject.SetActive(false);
            player.CharacterController.enabled = false;
            object view = Capture(player);
            Assert.That(Value<bool>(view, "ActiveAndEnabled"), Is.False);
            Assert.That(Value<bool>(view, "InputSubscribed"), Is.False);
            Assert.That(Value<bool>(view, "PlayerMapEnabled"), Is.False);
            Assert.That(Value<bool>(view, "ControllerAvailable"), Is.True);
            Assert.That(Value<bool>(view, "ControllerEnabled"), Is.False);
            Assert.That(CurrentActor(player), Is.Null);
            Assert.That(PlayerControllerBase.Current == null, Is.True);
        }

        [Test]
        public void CurrentInputActorReadsActualCurrentAcrossBindingAndOldActorUnbinding()
        {
            var first = Create(PlayerExecutionMode.Prediction);
            Assert.That(CurrentActor(first), Is.SameAs(first.transform));
            var second = Create(PlayerExecutionMode.Prediction);
            Assert.That(CurrentActor(first), Is.SameAs(second.transform),
                "旧 Actor 上的读口也必须返回当前 Current，不能保留首次观测的绑定。");
            first.UnbindForObservationTest();
            Assert.That(CurrentActor(first), Is.SameAs(second.transform),
                "旧 Actor 解绑不能抹掉较晚绑定的本机 Actor。");
            second.UnbindForObservationTest();
            Assert.That(CurrentActor(first), Is.Null);
        }

        private ReadOnlyObservationTestController Create(PlayerExecutionMode mode, bool initialize = true)
        {
            var root = new GameObject("Read-only movement observation fixture");
            root.SetActive(false);
            root.transform.position = new Vector3(34000f, 1500f, 29000f);
            _roots.Add(root);
            root.AddComponent<Animator>();
            var player = root.AddComponent<ReadOnlyObservationTestController>();
            _players.Add(player);
            player.ConfigureExecutionMode(mode);
            if (initialize)
            {
                player.InitializeForObservationTest();
                root.SetActive(true);
            }
            return player;
        }

        private static object Capture(PlayerControllerBase player)
        {
            var method = typeof(PlayerControllerBase).GetMethod("CaptureReadOnlyObservation", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "缺少只读观察入口；不能改用会消费边沿的 SampleInput。");
            return method.Invoke(player, null);
        }

        private static Transform CurrentActor(PlayerControllerBase player)
        {
            var property = typeof(PlayerControllerBase).GetProperty("CurrentInputActor", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, "缺少实际 Current 的只读入口。");
            return property.GetValue(player) as Transform;
        }

        private static T Value<T>(object view, string name)
        {
            var field = view.GetType().GetField(name);
            Assert.That(field, Is.Not.Null, "观察快照缺少字段：" + name);
            return (T)field.GetValue(view);
        }

        private static object GetPrivate(PlayerControllerBase player, string name) =>
            typeof(PlayerControllerBase).GetField(name, Private).GetValue(player);

        private static void SetPrivate(PlayerControllerBase player, string name, object value) =>
            typeof(PlayerControllerBase).GetField(name, Private).SetValue(player, value);
    }

    public sealed class ReadOnlyObservationTestController : PlayerControllerBase
    {
        public void InitializeForObservationTest() { base.Awake(); base.OnEnable(); }
        public void UnbindForObservationTest() => base.OnDisable();
    }
}
