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
    }
}
