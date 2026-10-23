using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    public sealed class MovementSchedulingTests
    {
        private static PlayerSimulationFrame Frame(uint sequence, uint epoch = 3) =>
            new PlayerSimulationFrame(sequence, epoch, Vector2.up, 90, true, true, true, false);

        [Test]
        public void Input_DuplicateWrongEpochAndNaNDoNotAdvanceConsumedAck()
        {
            var buffer = new AuthorityInputBuffer(); buffer.Reset(3);
            Assert.True(buffer.TryEnqueue(Frame(1), 3, 1));
            Assert.That(buffer.LastAcknowledged, Is.Zero, "收到包不等于已经执行输入");
            Assert.False(buffer.TryEnqueue(Frame(1), 3, 1));
            Assert.False(buffer.TryEnqueue(Frame(2, 2), 3, 1));
            Assert.False(buffer.TryEnqueue(Frame(2), 2, 1));
            var bad = Frame(2); bad.MoveAxis.x = float.NaN;
            Assert.False(buffer.TryEnqueue(bad, 3, 1));
            Assert.That(buffer.Consume(1).Sequence, Is.EqualTo(1));
            Assert.That(buffer.LastAcknowledged, Is.EqualTo(1));
        }

        [Test]
        public void Input_NoNewFrameDoesNotRepeatEdges_AndSilenceBecomesNeutral()
        {
            var buffer = new AuthorityInputBuffer(); buffer.Reset(3);
            Assert.True(buffer.TryEnqueue(Frame(1), 3, 1)); buffer.Consume(1);
            var held = buffer.Consume(1.03);
            Assert.That(held.MoveAxis, Is.EqualTo(Vector2.up));
            Assert.False(held.JumpPressed); Assert.False(held.DashPressed);
            var stopped = buffer.Consume(1.3);
            Assert.That(stopped.MoveAxis, Is.EqualTo(Vector2.zero)); Assert.False(stopped.JumpHeld);
        }

        [Test]
        public void Input_NeutralizeClearsQueueButPreservesOldSequenceFence()
        {
            var buffer = new AuthorityInputBuffer(); buffer.Reset(3);
            Assert.True(buffer.TryEnqueue(Frame(1), 3, 0));
            buffer.Neutralize(); Assert.That(buffer.PendingCount, Is.Zero);
            Assert.False(buffer.TryEnqueue(Frame(1), 3, .1));
            Assert.That(buffer.Consume(.1).MoveAxis, Is.EqualTo(Vector2.zero));
            Assert.True(buffer.TryEnqueue(Frame(2), 3, .1));
        }

        [Test]
        public void Input_FloodIsBounded_AndActorEpochResetClearsOldQueue()
        {
            var buffer = new AuthorityInputBuffer(); buffer.Reset(3); int accepted = 0;
            for (uint i = 1; i <= 1000; i++) if (buffer.TryEnqueue(Frame(i), 3, 0)) accepted++;
            Assert.That(accepted, Is.InRange(1, 32)); Assert.That(buffer.PendingCount, Is.LessThanOrEqualTo(32));
            buffer.Reset(4); Assert.That(buffer.PendingCount, Is.Zero);
            Assert.False(buffer.TryEnqueue(Frame(1001), 3, 0));
            Assert.True(buffer.TryEnqueue(Frame(1, 4), 4, 0));
        }

        [Test]
        public void Tick_FrozenHasNoDebt_AndCatchupIsBounded()
        {
            var clock = new FixedStepClock();
            Assert.That(clock.Advance(.02, true), Is.Zero);
            Assert.That(clock.Advance(60, false), Is.Zero);
            Assert.That(clock.Advance(.02, true), Is.Zero);
            Assert.That(clock.Advance(.02, true), Is.EqualTo(1));
            Assert.That(clock.Advance(1, true), Is.EqualTo(4));
            Assert.That(clock.DroppedSteps, Is.GreaterThan(0));
            Assert.That(clock.Advance(0, true), Is.Zero);
        }
    }
}
