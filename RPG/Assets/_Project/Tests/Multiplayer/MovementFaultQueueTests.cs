using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    public sealed class MovementFaultQueueTests
    {
        [Test] public void HalfRttEachDirection_HoldsThenReleasesWithoutChangingPayload()
        {
            var queue = new MovementFaultQueue<int>(); queue.Configure(200, 0, 0, 7);
            Assert.True(queue.Schedule(42, 5)); Assert.False(queue.TryPop(5.099, out _));
            Assert.True(queue.TryPop(5.1, out int value)); Assert.That(value, Is.EqualTo(42));
            Assert.That(queue.Pending, Is.Zero);
        }
        [Test] public void QueueIsBounded_AndReconfigurationClearsOldEpochPayloads()
        {
            var queue = new MovementFaultQueue<int>(); queue.Configure(100, 0, 0, 1);
            for (int i = 0; i < 128; i++) Assert.True(queue.Schedule(i, 0));
            Assert.False(queue.Schedule(129, 0)); Assert.That(queue.Dropped, Is.EqualTo(1));
            queue.Configure(0, 0, 0, 1); Assert.False(queue.TryPop(100, out _)); Assert.False(queue.Enabled);
        }
        [Test] public void LossAndJitter_AreRepeatableAndNeverGrowPastCapacity()
        {
            var a = new MovementFaultQueue<int>(); var b = new MovementFaultQueue<int>();
            a.Configure(200, 40, 3, 81); b.Configure(200, 40, 3, 81);
            for (int i = 0; i < 1000; i++)
            {
                Assert.That(a.Schedule(i, i * .03), Is.EqualTo(b.Schedule(i, i * .03)));
                while (a.TryPop(i * .03, out int x)) { Assert.True(b.TryPop(i * .03, out int y)); Assert.That(x, Is.EqualTo(y)); }
            }
            Assert.That(a.Dropped, Is.GreaterThan(0)); Assert.That(a.Pending, Is.LessThan(128)); Assert.That(a.Dropped, Is.EqualTo(b.Dropped));
        }
    }
}
