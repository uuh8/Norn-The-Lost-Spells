using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    public sealed class PredictionInterpolationTests
    {
        private sealed class Motor : IPlayerMovementSimulation
        {
            public int Restores, Replays; public readonly uint[] Sequences = new uint[128];
            public PlayerMovementSnapshot Pose;
            public PlayerMovementSnapshot CaptureMovementSnapshot() => Pose;
            public void RestoreMovementSnapshot(in PlayerMovementSnapshot pose) { Pose = pose; Restores++; }
            public void Simulate(in PlayerSimulationFrame frame, float dt, bool replay = false)
            { Assert.True(replay); Assert.That(dt, Is.EqualTo(FixedStepClock.DeltaTime)); Sequences[Replays++] = frame.Sequence; }
        }
        private static PlayerSimulationFrame Input(uint sequence) => new PlayerSimulationFrame(sequence, 3, Vector2.up, 0, false, false, false, false);
        [Test]
        public void Reconcile_ReplaysOnlyUnacknowledgedInputs()
        {
            var history = new LocalMovementPrediction(); history.Reset(3); var motor = new Motor();
            for (uint i = 1; i <= 3; i++) Assert.True(history.Record(Input(i), default));
            Assert.True(history.Reconcile(motor, default, 1, 3, FixedStepClock.DeltaTime));
            Assert.That(motor.Restores, Is.EqualTo(1)); Assert.That(motor.Replays, Is.EqualTo(2));
            Assert.That(motor.Sequences[0], Is.EqualTo(2)); Assert.That(motor.Sequences[1], Is.EqualTo(3));
            Assert.That(history.Count, Is.EqualTo(2));
        }
        [Test]
        public void Reconcile_WrongEpochOrFutureAckNeverChangesMotor()
        {
            var history = new LocalMovementPrediction(); history.Reset(3); history.Record(Input(1), default); var motor = new Motor();
            Assert.False(history.Reconcile(motor, default, 1, 2, FixedStepClock.DeltaTime));
            Assert.False(history.Reconcile(motor, default, 2, 3, FixedStepClock.DeltaTime));
            Assert.That(motor.Restores, Is.Zero);
        }
        [Test]
        public void HistoryOverflow_HardCorrectsInsteadOfReplayingAnIncompletePast()
        {
            var history = new LocalMovementPrediction(); history.Reset(3); var motor = new Motor();
            for (uint i = 1; i <= 129; i++) history.Record(Input(i), default);
            Assert.That(history.Count, Is.EqualTo(128)); Assert.That(history.OverflowCount, Is.EqualTo(1));
            Assert.True(history.Reconcile(motor, default, 0, 3, FixedStepClock.DeltaTime));
            Assert.That(motor.Restores, Is.EqualTo(1)); Assert.That(motor.Replays, Is.Zero); Assert.That(history.Count, Is.Zero);
        }
        private static MovementStateFrame State(uint tick, uint sequence, float x) => new MovementStateFrame
        { ActorId = 2, ConnectionEpoch = 3, ServerTick = tick, SnapshotSequence = sequence,
            Movement = new PlayerMovementSnapshot { Position = new Vector3(x, 0, 0), Rotation = Quaternion.identity } };
        [Test]
        public void Interpolation_UsesServerTimeAndStopsAtNewestInsteadOfRunningForever()
        {
            var buffer = new RemoteMovementInterpolation();
            Assert.True(buffer.TryAdd(State(30, 1, 0), 5)); Assert.True(buffer.TryAdd(State(33, 2, 10), 5.1));
            Assert.True(buffer.TrySample(5.15, .1f, out var middle)); Assert.That(middle.Position.x, Is.EqualTo(5).Within(.001));
            Assert.True(buffer.TrySample(100, .1f, out var last)); Assert.That(last.Position.x, Is.EqualTo(10));
            Assert.False(buffer.TryAdd(State(31, 1, 999), 6));
            Assert.False(buffer.TryAdd(State(29, 3, 999), 6));
        }
        [Test]
        public void Interpolation_ResumesBufferedTimelineAfterAuthorityFreeze()
        {
            var buffer = new RemoteMovementInterpolation();
            buffer.TryAdd(State(30, 1, 0), 5);
            var frozen = State(30, 2, 0); frozen.Frozen = true;
            buffer.TryAdd(frozen, 65);
            buffer.TryAdd(State(33, 3, 10), 65.1);
            // ServerTick在冻结时不走，本机时钟仍走；恢复后仍应插值，而非一直跳到最新包。
            Assert.True(buffer.TrySample(65.15, .1f, out var middle));
            Assert.That(middle.Position.x, Is.EqualTo(5).Within(.001));
        }

        [Test]
        public void Interpolation_OrdinaryServerHitchDoesNotPermanentlyBecomePacketStepping()
        {
            // DS会有界丢掉过载Tick，但并未进入业务Frozen。把这300ms差额留在固定offset里，
            // 会使之后每一个Render帧都越过最新Snapshot：看起来像15fps平移，永远无法自行恢复。
            var buffer = new RemoteMovementInterpolation();
            uint sequence = 0;
            double received = 5;
            for (uint tick = 30; tick <= 90; tick += 2)
            {
                if (tick == 50) received += .3;
                buffer.TryAdd(State(tick, ++sequence, (tick - 30) / 30f * 5), received);
                buffer.TrySample(received, .1f, out _);
                buffer.TrySample(received + 1d / 60d, .1f, out _);
                buffer.TrySample(received + 2d / 60d, .1f, out _);
                buffer.TrySample(received + 3d / 60d, .1f, out _);
                received += 2d / 30d;
            }
            // 循环最后已采到末包后的第3个Render帧，不能再把本机时间倒回末包到达时。
            double lastSample = received - 2d / 30d + 3d / 60d;
            buffer.TrySample(lastSample, .1f, out var first);
            buffer.TrySample(lastSample + 1d / 60d, .1f, out var second);
            Assert.That(second.Position.x - first.Position.x, Is.GreaterThan(.02f),
                "普通hitch结束后，两个到包之间的Render帧仍应持续移动，不能永久夹在最新包。");
        }

        [Test]
        public void Interpolation_FirstPacketDelayDoesNotRemainAsPermanentExtraLatency()
        {
            var buffer = new RemoteMovementInterpolation();
            buffer.TryAdd(State(30, 1, 0), 5.3); // 首包比后续流多迟到300ms。
            buffer.TrySample(5.3, .1f, out _);
            uint sequence = 1;
            for (uint tick = 42; tick <= 180; tick += 2)
            {
                double now = 5 + (tick - 30) / 30d;
                buffer.TryAdd(State(tick, ++sequence, (tick - 30) / 30f * 5), now);
                for (int frame = 0; frame < 4; frame++) buffer.TrySample(now + frame / 60d, .1f, out _);
            }
            buffer.TrySample(10.05, .1f, out var pose);
            Assert.That(25 - pose.Position.x, Is.LessThan(1.1f),
                "稳定流恢复后播放滞后应有界回到目标附近，不能保留首包额外300ms延迟。");
        }

        [Test]
        public void Interpolation_SegmentSpeedDoesNotDependOnRenderFrameRate_AndStationarySegmentStops()
        {
            var buffer = new RemoteMovementInterpolation();
            buffer.TryAdd(State(30, 1, 0), 5);
            buffer.TryAdd(State(33, 2, .5f), 5.1);
            buffer.TrySample(5.15, .1f, out _);
            Assert.That(buffer.PlanarSpeed, Is.EqualTo(5).Within(.001));
            buffer.TrySample(5.151, .1f, out _);
            Assert.That(buffer.PlanarSpeed, Is.EqualTo(5).Within(.001), "极短Render帧不能把跑步速度误判为静止。");
            buffer.TryAdd(State(36, 3, .5f), 5.2);
            buffer.TrySample(5.25, .1f, out _);
            Assert.That(buffer.PlanarSpeed, Is.Zero, "权威静止段应立即采用0，不沿用上一段速度。");
        }

        [Test]
        public void Interpolation_UnderflowRebuffersWithoutMovingBackwards_AndSilenceStopsAnimation()
        {
            var buffer = new RemoteMovementInterpolation();
            buffer.TryAdd(State(30, 1, 0), 5);
            buffer.TryAdd(State(33, 2, .5f), 5.1);
            buffer.TrySample(5.15, .1f, out _);
            buffer.TrySample(5.21, .1f, out var held);
            Assert.That(buffer.UnderflowCount, Is.EqualTo(1));
            Assert.That(buffer.PlanarSpeed, Is.EqualTo(5).Within(.001));
            buffer.TrySample(5.4, .1f, out var silent);
            Assert.That(silent.Position, Is.EqualTo(held.Position));
            Assert.That(buffer.PlanarSpeed, Is.Zero);
            buffer.TryAdd(State(35, 3, .8333333f), 5.41);
            buffer.TrySample(5.42, .1f, out var stillHeld);
            Assert.That(stillHeld.Position.x, Is.GreaterThanOrEqualTo(held.Position.x));
            buffer.TryAdd(State(37, 4, 1.1666667f), 5.48);
            buffer.TryAdd(State(39, 5, 1.5f), 5.55);
            buffer.TrySample(5.57, .1f, out var resumed);
            Assert.That(resumed.Position.x, Is.GreaterThan(held.Position.x));
            Assert.That(buffer.UnderflowCount, Is.EqualTo(1), "重缓冲中的重复采样不能重复计数或重启暂停。");
        }

        [Test]
        public void Interpolation_DiscreteLocomotionChangesOnlyAtItsSnapshotBoundary()
        {
            var buffer = new RemoteMovementInterpolation();
            var grounded = State(30, 1, 0);
            grounded.Movement.IsGrounded = true;
            var airborne = State(33, 2, .5f);
            airborne.Movement.Locomotion = PlayerLocomotionKind.Airborne;
            buffer.TryAdd(grounded, 5); buffer.TryAdd(airborne, 5.1);
            buffer.TrySample(5.15, .1f, out var middle);
            Assert.True(middle.IsGrounded);
            Assert.That(middle.Locomotion, Is.EqualTo(PlayerLocomotionKind.Grounded));
            buffer.TrySample(5.2, .1f, out var boundary);
            Assert.False(boundary.IsGrounded);
            Assert.That(boundary.Locomotion, Is.EqualTo(PlayerLocomotionKind.Airborne));
        }

        [Test]
        public void Interpolation_JitterAndPacketLossRecoverWithinBoundedHistory()
        {
            var buffer = new RemoteMovementInterpolation();
            buffer.Configure(1f / 15f, .1f, .2f);
            uint sequence = 0;
            double lastSample = 0;
            float previous = 0;
            for (uint tick = 30; tick <= 150; tick += 2)
            {
                if (tick == 50 || tick == 52) continue;
                double now = 5 + (tick - 30) / 30d + (tick < 90 && tick % 4 == 0 ? .02 : 0);
                buffer.TryAdd(State(tick, ++sequence, (tick - 30) / 30f * 5), now);
                // 故障段只采到本次到包后10ms，避免测试人为把时间采到下一份较早到达的包之后。
                lastSample = now + .01;
                buffer.TrySample(lastSample, .1f, out var pose);
                Assert.That(pose.Position.x, Is.GreaterThanOrEqualTo(previous - .0001f));
                Assert.That(buffer.BufferedCount, Is.InRange(1, 16));
                Assert.That(buffer.EffectiveDelay, Is.InRange(.1f, .2f));
                previous = pose.Position.x;
            }
            buffer.TrySample(lastSample + .01, .1f, out var first);
            buffer.TrySample(lastSample + .02, .1f, out var second);
            Assert.That(second.Position.x, Is.GreaterThan(first.Position.x));
            Assert.That(20 - second.Position.x, Is.LessThan(1.2f));
        }

        [Test]
        public void Interpolation_ResetClearsTimelineSpeedAndDiagnosticsButRetainsConfiguration()
        {
            var buffer = new RemoteMovementInterpolation(); buffer.Configure(1f / 30f, .067f, .15f);
            buffer.TryAdd(State(30, 1, 0), 5); buffer.TrySample(10, .067f, out _);
            buffer.Reset();
            Assert.False(buffer.TrySample(11, .067f, out _));
            Assert.That(buffer.BufferedCount, Is.Zero);
            Assert.That(buffer.ReceivedCount, Is.Zero);
            Assert.That(buffer.UnderflowCount, Is.Zero);
            Assert.That(buffer.ResyncCount, Is.Zero);
            Assert.That(buffer.PlanarSpeed, Is.Zero);
            buffer.TryAdd(State(60, 1, 0), 11);
            Assert.That(buffer.EffectiveDelay, Is.EqualTo(.067f).Within(.00001));
        }
    }
}
