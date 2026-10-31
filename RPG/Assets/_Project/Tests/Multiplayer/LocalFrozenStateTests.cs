using System;
using System.Reflection;
using Game.Character;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 直接投递生产 OnState，但保留真实 Registry、角色、CC、预测历史与视觉校正。
    /// 不建立网络或修改正式资源；只把收包和表现时钟作为受控输入，检查 Frozen 的完整状态安装责任。
    /// </summary>
    public sealed class LocalFrozenStateTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Vector3 Anchor = new Vector3(31000, 2000, 27000);
        private GameObject _worldRoot, _actorsRoot;
        private OnlineWorldBootstrap _world;
        private PlayerControllerBase _player;
        private MovementPresentationSmoother _smoother;
        private LocalMovementPrediction _history;
        private OnlineMovementProfile _profile;

        [SetUp]
        public void SetUp()
        {
            _worldRoot = new GameObject("Inactive local Frozen receive fixture"); _worldRoot.SetActive(false);
            _actorsRoot = new GameObject("Owned local Frozen actor");
            _world = _worldRoot.AddComponent<OnlineWorldBootstrap>();
            _profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            Assert.That(_profile, Is.Not.Null);
            var registry = new NetworkActorRegistry(_profile, _actorsRoot.transform);
            _player = registry.EnsurePlayer(0, PlayerExecutionMode.Prediction).Player;
            if (_player.CharacterController == null) Invoke(_player, "Awake");
            Invoke(_player.GroundChecker, "Awake");
            _smoother = _player.gameObject.AddComponent<MovementPresentationSmoother>(); _smoother.Bind(_player);
            Set(_world, "_profile", _profile); Set(_world, "<Registry>k__BackingField", registry);
            Set(_world, "<WorldReady>k__BackingField", true); Set(_world, "<LocalPlayer>k__BackingField", _player);
            Set(_world, "_localActor", 1UL); Set(_world, "_localEpoch", 3u); Set(_world, "_smoother", _smoother);
            _history = (LocalMovementPrediction)typeof(OnlineWorldBootstrap).GetField("_prediction", Private).GetValue(_world);
            _history.Reset(3);
            var predicted = Pose(Anchor + Vector3.right); _player.RestoreMovementSnapshot(predicted);
            Assert.That(_history.Record(Input(1, Vector2.zero), predicted), Is.True);
            Assert.That(_history.Record(Input(2, Vector2.zero), predicted), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            // EditMode 手工触发过 Awake，需要对称释放本机 InputAction。先清 Actor 再停 World，
            // 避免把 Registry 正常运行时使用的延迟 Destroy 语义误用到本夹具。
            if (_player != null)
            {
                Invoke(_player, "OnDisable");
                var field = typeof(PlayerControllerBase).GetField("_inputActions", Private);
                object actions = field.GetValue(_player);
                if (actions != null)
                {
                    var asset = (Object)actions.GetType().GetProperty("asset").GetValue(actions);
                    if (asset != null) Object.DestroyImmediate(asset);
                    field.SetValue(_player, null);
                }
                Invoke(_player, "OnDestroy");
            }
            if (_actorsRoot != null) Object.DestroyImmediate(_actorsRoot);
            if (_world != null) _world.StopWorld();
            if (_worldRoot != null) Object.DestroyImmediate(_worldRoot);
        }

        [Test]
        public void FrozenInstallsCompleteAuthorityPoseAndClearsUnacknowledgedPrediction()
        {
            var authority = Pose(Anchor);
            authority.Locomotion = PlayerLocomotionKind.Dash; authority.VerticalVelocity = 7;
            authority.DashDirection = Vector3.forward; authority.DashElapsed = .12f;
            authority.DashCooldownCounter = .31f; authority.ExtraJumpsRemaining = 0;
            authority.AirDashesRemaining = 0; authority.JumpCutArmed = true;
            Receive(State(1, true, authority));
            var actual = _player.CaptureMovementSnapshot();
            Assert.That(actual.Position, Is.EqualTo(Anchor), "冻结时 CC 必须立即采用权威位置，不能保留未 Ack 的预测位移。");
            Assert.That(actual.Locomotion, Is.EqualTo(PlayerLocomotionKind.Dash));
            Assert.That(actual.VerticalVelocity, Is.EqualTo(7));
            Assert.That(actual.DashDirection, Is.EqualTo(Vector3.forward));
            Assert.That(actual.DashElapsed, Is.EqualTo(.12f));
            Assert.That(actual.DashCooldownCounter, Is.EqualTo(.31f));
            Assert.That(actual.ExtraJumpsRemaining, Is.Zero); Assert.That(actual.AirDashesRemaining, Is.Zero);
            Assert.That(actual.JumpCutArmed, Is.True); Assert.That(_history.Count, Is.Zero);
            Assert.That(_world.Frozen, Is.True);
        }

        [Test]
        public void RepeatedFrozenPoseDoesNotRestartFiniteVisualCorrection()
        {
            var state = State(1, true, Pose(Anchor)); Receive(state);
            Assert.That(_player.transform.position, Is.EqualTo(Anchor));
            Assert.That(_smoother.VisualPosition, Is.EqualTo(Anchor + Vector3.right));
            AdvanceVisual(.05f);
            state.SnapshotSequence = 2; Receive(state);
            AdvanceVisual(.05f);
            Assert.That(_smoother.VisualPosition, Is.EqualTo(Anchor),
                "相同 Frozen Pose 的新序号只确认事实，不能每份快照把剩余视觉误差重新延长 100ms。");
        }

        [Test]
        public void NewFrozenPoseStillUpdatesAuthorityRootWhileAlreadyFrozen()
        {
            Receive(State(1, true, Pose(Anchor)));
            var next = Pose(Anchor + Vector3.forward * .25f); next.VerticalVelocity = -3;
            Receive(State(2, true, next));
            Assert.That(_player.transform.position, Is.EqualTo(next.Position));
            Assert.That(_player.VerticalVelocity, Is.EqualTo(-3));
            Assert.That(_history.Count, Is.Zero);
        }

        [Test]
        public void WrongEpochAndOldSequenceCannotInstallFrozenPose()
        {
            Set(_world, "_lastLocalSnapshot", 10u);
            var invalid = State(11, true, Pose(Anchor + Vector3.forward * 20)); invalid.ConnectionEpoch = 4;
            Receive(invalid);
            Assert.That(_world.Frozen, Is.False);
            Assert.That(_player.transform.position, Is.EqualTo(Anchor + Vector3.right));
            Receive(State(10, true, invalid.Movement));
            Assert.That(_world.Frozen, Is.False);
            Assert.That(_player.transform.position, Is.EqualTo(Anchor + Vector3.right));
            Assert.That(_history.Count, Is.EqualTo(2));
        }

        [Test]
        public void UnfreezeReplaysOnlyNewUnacknowledgedInputAfterFrozenHistoryWasDiscarded()
        {
            Receive(State(1, true, Pose(Anchor)));
            var resumed = State(2, false, Pose(Anchor)); resumed.AcknowledgedInput = 2; Receive(resumed);
            Assert.That(_world.Frozen, Is.False); Assert.That(_history.Count, Is.Zero);
            var third = Input(3, Vector2.up); _player.Simulate(third, FixedStepClock.DeltaTime);
            var thirdPose = _player.CaptureMovementSnapshot(); Assert.That(_history.Record(third, thirdPose), Is.True);
            var fourth = Input(4, Vector2.right); _player.Simulate(fourth, FixedStepClock.DeltaTime);
            Assert.That(_history.Record(fourth, _player.CaptureMovementSnapshot()), Is.True);
            var correction = State(3, false, thirdPose); correction.AcknowledgedInput = 3; Receive(correction);
            Assert.That(_history.LastReplayCount, Is.EqualTo(1)); Assert.That(_history.Count, Is.EqualTo(1));
            float step = _player.MoveSpeed * FixedStepClock.DeltaTime;
            Assert.That(_player.transform.position.x, Is.EqualTo(Anchor.x + step).Within(.005f));
            Assert.That(_player.transform.position.z, Is.EqualTo(Anchor.z + step).Within(.005f));
        }

        private MovementStateFrame State(uint sequence, bool frozen, PlayerMovementSnapshot pose) => new MovementStateFrame
        {
            ActorId = 1, ConnectionEpoch = 3, SimulationHash = _profile.SimulationHash, SnapshotSequence = sequence,
            ServerTick = 100, AcknowledgedInput = 1, Connected = true, Targetable = true, Frozen = frozen, Movement = pose
        };
        private static PlayerMovementSnapshot Pose(Vector3 position) => new PlayerMovementSnapshot
        {
            Position = position, Rotation = Quaternion.identity, TargetFacing = Vector3.forward,
            GroundNormal = Vector3.up, Locomotion = PlayerLocomotionKind.Airborne, ExtraJumpsRemaining = 1, AirDashesRemaining = 1
        };
        private static PlayerSimulationFrame Input(uint sequence, Vector2 axis) =>
            new PlayerSimulationFrame(sequence, 3, axis, 0, false, false, false, false);
        private void Receive(MovementStateFrame state) => Invoke(_world, "OnState", state);
        private void AdvanceVisual(float elapsed)
        {
            // 只替代 LateUpdate 的 elapsed 输入；Advance/Offset 和 OnState 的 Preserve 调用仍是生产实现。
            var field = typeof(MovementPresentationSmoother).GetField("_blend", Private);
            var blend = (MovementCorrectionBlend)field.GetValue(_smoother); blend.Advance(elapsed); field.SetValue(_smoother, blend);
            Invoke(_smoother, "Apply");
        }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        private static void Invoke(object target, string method, params object[] args)
        {
            Type type = target.GetType(); MethodInfo callback = null;
            while (type != null && callback == null) { callback = type.GetMethod(method, Private | BindingFlags.DeclaredOnly); type = type.BaseType; }
            Assert.That(callback, Is.Not.Null, method); callback.Invoke(target, args);
        }
    }
}
