using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class FluidChunkWakeQueueTests
    {
        [Test]
        public void DuplicateAndOverflowCannotReplaceEarlierWake()
        {
            var queue = new FluidChunkWakeQueue(2);
            var a = new ElementChunkKey(1,0,0);
            var b = new ElementChunkKey(2,0,0);
            Assert.That(queue.TryEnqueue(a, FluidDormantWakeReason.Exposure), Is.True);
            Assert.That(queue.TryEnqueue(a, FluidDormantWakeReason.Reaction), Is.True);
            Assert.That(queue.TryEnqueue(b, FluidDormantWakeReason.Write), Is.True);
            Assert.That(queue.TryEnqueue(new ElementChunkKey(3,0,0), FluidDormantWakeReason.Write), Is.False);
            Assert.That(queue.Count, Is.EqualTo(2));
            Assert.That(queue.TryDequeue(out var first, out var reason), Is.True);
            Assert.That(first, Is.EqualTo(a));
            Assert.That(reason, Is.EqualTo(FluidDormantWakeReason.Exposure));
            Assert.That(queue.TryDequeue(out var second, out _), Is.True);
            Assert.That(second, Is.EqualTo(b));
            Assert.That(queue.TryEnqueue(a, FluidDormantWakeReason.Write), Is.True);
            Assert.That(queue.TryDequeue(out first, out _), Is.True);
            Assert.That(first, Is.EqualTo(a));
        }

        [Test]
        public void MoreThanOneFrameOfWakeRequestsRemainFifoUntilAllDrain()
        {
            var queue = new FluidChunkWakeQueue(16);
            for (int i = 0; i < 8; i++)
                Assert.That(queue.TryEnqueue(
                    new ElementChunkKey(i, 0, 0), FluidDormantWakeReason.Write), Is.True);

            // Runtime 每帧只能真正推进有限个 Restore；第 7、8 个请求仍必须留在持久 FIFO，
            // 不能被“当前帧六个 Region/租约”覆盖后永久遗失。
            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryDequeue(out ElementChunkKey key, out FluidDormantWakeReason reason), Is.True);
                Assert.That(key, Is.EqualTo(new ElementChunkKey(i, 0, 0)));
                Assert.That(reason, Is.EqualTo(FluidDormantWakeReason.Write));
            }
            Assert.That(queue.Count, Is.Zero);
        }
    }
}
