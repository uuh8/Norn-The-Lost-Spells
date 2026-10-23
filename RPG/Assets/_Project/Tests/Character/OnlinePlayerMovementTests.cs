using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    /// <summary>使用真实 Controller／State／CC；伪造的只有输入 Frame，避免把网络协议测试当成角色测试。</summary>
    public sealed class OnlinePlayerMovementTests
    {
        private GameObject _root;
        private OnlineMovementTestController _player;

        [TearDown]
        public void Cleanup()
        {
            if (_player != null) _player.ReleaseForEditMode();
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private void Create(PlayerExecutionMode mode)
        {
            _root = new GameObject("Online movement real controller");
            _root.SetActive(false);
            _root.transform.position = new Vector3(1000, 100, 1000);
            _root.AddComponent<Animator>();
            _player = _root.AddComponent<OnlineMovementTestController>();
            _player.ConfigureExecutionMode(mode);
            _player.InitializeForEditMode();
            _root.SetActive(true);
        }

        private static PlayerMovementSnapshot Airborne()
        {
            return new PlayerMovementSnapshot
            {
                Position = new Vector3(1000, 100, 1000), Rotation = Quaternion.identity,
                TargetFacing = Vector3.forward, Locomotion = PlayerLocomotionKind.Airborne,
                GroundNormal = Vector3.up, ExtraJumpsRemaining = 1, AirDashesRemaining = 1
            };
        }

        [Test]
        public void PredictionInputUsesOwnedOrbitYawAfterConnectionCameraIsDisabled()
        {
            Create(PlayerExecutionMode.Prediction);
            var oldCamera = new GameObject("Disabled connection camera");
            try
            {
                var camera = oldCamera.AddComponent<Camera>(); camera.enabled = false;
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(PlayerControllerBase).GetField("_mainCamera", flags).SetValue(_player, camera);
                typeof(PlayerControllerBase).GetField("_cameraYaw", flags).SetValue(_player, 90f);
                Assert.That(_player.SampleInput(1, 1).ViewYaw, Is.EqualTo(90), "在线W前进应跟随自己的轨道相机，不能沿用已禁用连接相机的朝向。");
            }
            finally { Object.DestroyImmediate(oldCamera); }
        }

        [TestCase(PlayerExecutionMode.Authority)]
        [TestCase(PlayerExecutionMode.Replica)]
        public void RemoteActor_DoesNotCreateLocalInputOrRegisterCurrent(PlayerExecutionMode mode)
        {
            Create(mode);
            Assert.That(_player.ExecutionMode, Is.EqualTo(mode));
            Assert.That(typeof(PlayerControllerBase).GetField("_inputActions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_player), Is.Null);
            Assert.That(_player.MainCamera, Is.Null);
            Assert.That(PlayerControllerBase.Current, Is.Not.SameAs(_player));
        }

        [Test]
        public void Authority_UsesSuppliedYawAndDeltaTimeWithoutCamera()
        {
            Create(PlayerExecutionMode.Authority);
            _player.RestoreMovementSnapshot(Airborne());
            var frame = new PlayerSimulationFrame(1, 1, Vector2.up, 90, false, false, false, false);
            _player.Simulate(frame, .1f);
            Assert.That(_root.transform.position.x, Is.EqualTo(1000.5f).Within(.01f));
            Assert.That(_root.transform.position.z, Is.EqualTo(1000).Within(.01f));
            Assert.That(_player.VerticalVelocity, Is.LessThan(0));
        }

        [Test]
        public void OnlineAutomaticUpdate_DoesNotAdvanceAnExplicitTickAgain()
        {
            Create(PlayerExecutionMode.Authority);
            _player.RestoreMovementSnapshot(Airborne());
            var frame = new PlayerSimulationFrame(1, 1, Vector2.up, 0, false, false, false, false);
            _player.Simulate(frame, .1f);
            Vector3 after = _root.transform.position;
            typeof(PlayerControllerBase).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_player, null);
            Assert.That(_root.transform.position, Is.EqualTo(after));
            Assert.That(after.z, Is.GreaterThan(1000));
        }

        [Test]
        public void Replica_SimulateDoesNotMoveEvenWithValidInput()
        {
            Create(PlayerExecutionMode.Replica);
            var snapshot = Airborne();
            _player.RestoreMovementSnapshot(snapshot);
            var frame = new PlayerSimulationFrame(1, 1, Vector2.up, 0, true, true, true, false);
            _player.Simulate(frame, .1f);
            Assert.That(_root.transform.position, Is.EqualTo(snapshot.Position));
            Assert.That(_player.ExecutionMode, Is.EqualTo(PlayerExecutionMode.Replica));
        }

        [Test]
        public void Snapshot_RoundTripsDashTimersBudgetAndJumpCutWithoutEnterExit()
        {
            Create(PlayerExecutionMode.Authority);
            var snapshot = Airborne();
            snapshot.Locomotion = PlayerLocomotionKind.Dash;
            snapshot.VerticalVelocity = 7;
            snapshot.IsAirborneForDash = true;
            snapshot.DashDirection = Vector3.right;
            snapshot.DashElapsed = .12f;
            snapshot.DashCooldownCounter = .31f;
            snapshot.DashBufferCounter = .04f;
            snapshot.CoyoteTimeCounter = .02f;
            snapshot.JumpBufferCounter = .08f;
            snapshot.AttackBufferCounter = .09f;
            snapshot.AttackCooldownCounter = .14f;
            snapshot.ExtraJumpsRemaining = 0;
            snapshot.AirDashesRemaining = 0;
            snapshot.JumpCutArmed = true;
            _player.RestoreMovementSnapshot(snapshot);
            var actual = _player.CaptureMovementSnapshot();
            Assert.That(actual.Locomotion, Is.EqualTo(snapshot.Locomotion));
            Assert.That(actual.VerticalVelocity, Is.EqualTo(7));
            Assert.That(actual.DashDirection, Is.EqualTo(Vector3.right));
            Assert.That(actual.DashElapsed, Is.EqualTo(.12f));
            Assert.That(actual.DashCooldownCounter, Is.EqualTo(.31f), "Restore 不能调用 Dash.Exit 重新启动冷却");
            Assert.That(actual.DashBufferCounter, Is.EqualTo(.04f), "Restore 不能调用 Dash.Enter 消费缓冲");
            Assert.That(actual.JumpBufferCounter, Is.EqualTo(.08f));
            Assert.That(actual.CoyoteTimeCounter, Is.EqualTo(.02f));
            Assert.That(actual.AttackBufferCounter, Is.EqualTo(.09f));
            Assert.That(actual.AttackCooldownCounter, Is.EqualTo(.14f));
            Assert.That(actual.ExtraJumpsRemaining, Is.Zero);
            Assert.That(actual.AirDashesRemaining, Is.Zero);
            Assert.That(actual.JumpCutArmed, Is.True);
        }

        [Test]
        public void RestoreExhaustedAirBudget_DoesNotGrantAnotherJump()
        {
            Create(PlayerExecutionMode.Authority);
            var snapshot = Airborne(); snapshot.ExtraJumpsRemaining = 0;
            _player.RestoreMovementSnapshot(snapshot);
            var frame = new PlayerSimulationFrame(1, 1, Vector2.zero, 0, true, true, false, false);
            _player.Simulate(frame, .05f);
            Assert.That(_player.VerticalVelocity, Is.LessThanOrEqualTo(0));
            Assert.That(_player.CaptureMovementSnapshot().ExtraJumpsRemaining, Is.Zero);
        }

        [Test]
        public void Replay_DoesNotRepeatJumpPresentation()
        {
            Create(PlayerExecutionMode.Prediction);
            _player.RestoreMovementSnapshot(Airborne());
            var frame = new PlayerSimulationFrame(1, 1, Vector2.zero, 0, true, true, false, false);
            _player.Simulate(frame, .05f, true);
            Assert.That(_player.VerticalVelocity, Is.GreaterThan(0), "真实二段跳规则仍需执行");
            Assert.That(_player.FeedbackCount, Is.Zero, "重放只恢复运动，不能再次生成起跳特效");
        }

        [Test]
        public void ClearOnlineInput_DiscardsPendingJumpDashAndAttack()
        {
            Create(PlayerExecutionMode.Authority);
            var snapshot = Airborne();
            snapshot.JumpBufferCounter = .1f; snapshot.DashBufferCounter = .1f; snapshot.AttackBufferCounter = .1f;
            _player.RestoreMovementSnapshot(snapshot);
            _player.ClearOnlineInput();
            var actual = _player.CaptureMovementSnapshot();
            Assert.That(actual.JumpBufferCounter, Is.Zero);
            Assert.That(actual.DashBufferCounter, Is.Zero);
            Assert.That(actual.AttackBufferCounter, Is.Zero);
            Assert.That(actual.Position, Is.EqualTo(snapshot.Position));
        }

        [Test]
        public void SuppressedInput_StillAppliesGravityButCannotSpendAirActions()
        {
            Create(PlayerExecutionMode.Authority);
            _player.RestoreMovementSnapshot(Airborne());
            var frame = new PlayerSimulationFrame(1, 1, Vector2.up, 0, true, true, true, true);
            _player.Simulate(frame, .05f);
            var actual = _player.CaptureMovementSnapshot();
            Assert.That(actual.Position.z, Is.EqualTo(1000).Within(.001f));
            Assert.That(actual.VerticalVelocity, Is.LessThan(0));
            Assert.That(actual.ExtraJumpsRemaining, Is.EqualTo(1));
            Assert.That(actual.AirDashesRemaining, Is.EqualTo(1));
        }
    }

    public sealed class OnlineMovementTestController : PlayerControllerBase
    {
        public int FeedbackCount { get; private set; }
        public void InitializeForEditMode()
        {
            if (StateMachine == null) base.Awake();
            // EditMode 不自动运行新挂载组件的 Awake；RED 旧实现添加的 F 输入也需正确初始化后释放。
            var portal = GetComponent<PortalInteractInput>();
            if (portal != null && typeof(PortalInteractInput).GetField("_interact", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(portal) == null)
                typeof(PortalInteractInput).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(portal, null);
        }
        public void ReleaseForEditMode() => base.OnDisable();
        public override void PlayJumpStartAnimation() { }
        protected override void PlayJumpVfx() { FeedbackCount++; }
    }
}
