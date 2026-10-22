using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 固定容量 Queue 是 Projectile/Runtime 与固定 Tick Simulator 之间的 Command Buffer。
    /// 测试关注容量和顺序，不依赖 MonoBehaviour 或场景生命周期。
    /// </summary>
    public sealed class ElementWriteQueueTests
    {
        [Test]
        public void QueueRejectsWriteWhenCapacityReached()
        {
            var queue = new ElementWriteQueue(capacity: 2);

            Assert.That(queue.TryEnqueue(Request(10)), Is.True);
            Assert.That(queue.TryEnqueue(Request(20)), Is.True);
            Assert.That(queue.TryEnqueue(Request(30)), Is.False);
            Assert.That(queue.Count, Is.EqualTo(2));
        }

        [Test]
        public void RingBufferPreservesFifoOrderAfterWrapAround()
        {
            var queue = new ElementWriteQueue(capacity: 2);
            queue.TryEnqueue(Request(10));
            queue.TryEnqueue(Request(20));
            Assert.That(queue.TryDequeue(out ElementWriteRequest first), Is.True);
            Assert.That(first.TotalAmount, Is.EqualTo(10));

            Assert.That(queue.TryEnqueue(Request(30)), Is.True);
            Assert.That(queue.TryDequeue(out ElementWriteRequest second), Is.True);
            Assert.That(queue.TryDequeue(out ElementWriteRequest third), Is.True);
            Assert.That(second.TotalAmount, Is.EqualTo(20));
            Assert.That(third.TotalAmount, Is.EqualTo(30));
            Assert.That(queue.TryDequeue(out _), Is.False);
        }

        private static ElementWriteRequest Request(ushort amount)
        {
            return new ElementWriteRequest(
                Vector3.zero,
                MaterialId.Water,
                amount,
                radius: 0f,
                useLinearFalloff: false);
        }
    }
}
