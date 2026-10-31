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

        private static PlayerSimulationFrame Continuous(uint sequence, bool jump = false, bool dash = false,
            bool held = false, bool suppressed = false) => new PlayerSimulationFrame(sequence, 3,
                Vector2.up, 90, jump, held, dash, suppressed);

        [Test]
        public void Input_ContinuousBacklogDoesNotRemainAsPermanentControlLatency()
        {
            var buffer = new AuthorityInputBuffer(); buffer.Reset(3);
            for (uint i = 1; i <= 10; i++) Assert.True(buffer.TryEnqueue(Continuous(i), 3, 1));
            // 模拟一次DS卡顿后持续收包。只检查_lastReceived不能发现这些旧方向已经积压十帧。
            // 连续值必须直接采用最新帧：若消费倒数第二帧并留下一帧，同频收发会把这份旧方向永久带下去。
            var executed = buffer.Consume(1.01);
            Assert.That(executed.Sequence, Is.EqualTo(10), "过期连续方向由最新帧替代，不能留下额外一帧FIFO延迟。");
            Assert.That(buffer.PendingCount, Is.Zero);
            Assert.That(buffer.LastAcknowledged, Is.EqualTo(10));
            Assert.True(buffer.TryEnqueue(Continuous(11), 3, 1.04));
            Assert.That(buffer.Consume(1.04).Sequence, Is.EqualTo(11), "继续同频收包时不应重生一帧积压。");
        }

        [Test]
        public void Input_BacklogCompactionKeepsJumpDashAndJumpReleaseInChronologicalOrder()
        {
            var buffer = new AuthorityInputBuffer(); buffer.Reset(3);
            buffer.TryEnqueue(Continuous(1), 3, 1);
            buffer.TryEnqueue(Continuous(2), 3, 1);
            buffer.TryEnqueue(Continuous(3, jump: true, held: true), 3, 1);
            buffer.TryEnqueue(Continuous(4, held: false), 3, 1); // Jump Cut：释放不能被当作普通方向丢弃。
            buffer.TryEnqueue(Continuous(5, dash: true), 3, 1);
            for (uint i = 6; i <= 10; i++) buffer.TryEnqueue(Continuous(i), 3, 1);
            Assert.That(buffer.Consume(1.01).Sequence, Is.EqualTo(3));
            var released = buffer.Consume(1.04);
            Assert.That(released.Sequence, Is.EqualTo(4)); Assert.False(released.JumpHeld);
            var dash = buffer.Consume(1.07);
            Assert.That(dash.Sequence, Is.EqualTo(5)); Assert.True(dash.DashPressed);
            Assert.That(buffer.Consume(1.10).Sequence, Is.EqualTo(10));
            Assert.That(buffer.PendingCount, Is.Zero);
        }

        [Test]
        public void Input_BacklogCompactionExecutesSuppressionAndReleaseBeforeLatestMovement()
        {
            var buffer = new AuthorityInputBuffer(); buffer.Reset(3);
            buffer.TryEnqueue(Continuous(1), 3, 1);
            buffer.TryEnqueue(Continuous(2), 3, 1);
            buffer.TryEnqueue(Continuous(3, suppressed: true), 3, 1);
            buffer.TryEnqueue(Continuous(4, suppressed: true), 3, 1);
            buffer.TryEnqueue(Continuous(5, suppressed: false), 3, 1);
            buffer.TryEnqueue(Continuous(6), 3, 1);
            buffer.TryEnqueue(Continuous(7), 3, 1);

            // UI/失焦输入门也是离散语义：不能因为队尾已经恢复输入，就跳过中间的阻断与释放。
            // 同一门状态下的重复帧可以替代；每次Consume仍只提交一个语义边界，不合并多个权威步。
            var blocked = buffer.Consume(1.01);
            Assert.That(blocked.Sequence, Is.EqualTo(3)); Assert.True(blocked.InputSuppressed);
            var released = buffer.Consume(1.04);
            Assert.That(released.Sequence, Is.EqualTo(5)); Assert.False(released.InputSuppressed);
            var latest = buffer.Consume(1.07);
            Assert.That(latest.Sequence, Is.EqualTo(7)); Assert.False(latest.InputSuppressed);
            Assert.That(buffer.PendingCount, Is.Zero);
            Assert.That(buffer.LastAcknowledged, Is.EqualTo(7));
        }

        [Test]
        public void Input_BacklogCompactionKeepsShortJumpPulseEvenWhenAlreadyReleased()
        {
            var buffer = new AuthorityInputBuffer(); buffer.Reset(3);
            buffer.TryEnqueue(Continuous(1), 3, 1);
            // performed已锁存Jump边沿，但玩家在下一次30Hz采样前松手时，JumpHeld可能已经为false。
            // 此时只能靠JumpPressed本身保护这一帧，不能让Held变化恰好替边沿检查兜底。
            buffer.TryEnqueue(Continuous(2, jump: true, held: false), 3, 1);
            buffer.TryEnqueue(Continuous(3), 3, 1);
            buffer.TryEnqueue(Continuous(4), 3, 1);
            var pulse = buffer.Consume(1.01);
            Assert.That(pulse.Sequence, Is.EqualTo(2)); Assert.True(pulse.JumpPressed); Assert.False(pulse.JumpHeld);
            Assert.That(buffer.Consume(1.04).Sequence, Is.EqualTo(4));
            Assert.That(buffer.PendingCount, Is.Zero);
        }

        [Test]
        public void Input_BacklogCompactionPreservesHeldChangesWithoutAnotherJumpEdge()
        {
            var buffer = new AuthorityInputBuffer(); buffer.Reset(3);
            buffer.TryEnqueue(Continuous(1), 3, 1);
            buffer.TryEnqueue(Continuous(2, held: true), 3, 1);
            buffer.TryEnqueue(Continuous(3, held: true), 3, 1);
            buffer.TryEnqueue(Continuous(4, held: false), 3, 1);
            buffer.TryEnqueue(Continuous(5), 3, 1);
            buffer.TryEnqueue(Continuous(6), 3, 1);
            // 按住状态独立影响Jump Cut：即使边沿包没在这个队列里，true与false也要按顺序各提交一次。
            var held = buffer.Consume(1.01);
            Assert.That(held.Sequence, Is.EqualTo(2)); Assert.True(held.JumpHeld); Assert.False(held.JumpPressed);
            var released = buffer.Consume(1.04);
            Assert.That(released.Sequence, Is.EqualTo(4)); Assert.False(released.JumpHeld);
            Assert.That(buffer.Consume(1.07).Sequence, Is.EqualTo(6));
            Assert.That(buffer.PendingCount, Is.Zero);
        }
    }
}
