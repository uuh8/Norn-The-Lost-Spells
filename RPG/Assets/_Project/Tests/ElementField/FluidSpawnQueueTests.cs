using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Spawn Queue 是 Water 写入与后续 GPU Upload 之间的固定容量 Command Buffer。
    /// 测试只验证可见顺序和满队列行为，避免把容器内部实现细节当成契约。
    /// </summary>
    public sealed class FluidSpawnQueueTests
    {
        [Test]
        public void FullQueueRejectsNewRequestWithoutChangingFifoOrder()
        {
            var queue = new FluidSpawnQueue(capacity: 2);
            var destination = new FluidSpawnRequest[2];

            Assert.That(queue.TryEnqueue(Request(particleCount: 10)), Is.True);
            Assert.That(queue.TryEnqueue(Request(particleCount: 20)), Is.True);
            Assert.That(queue.TryEnqueue(Request(particleCount: 30)), Is.False);
            Assert.That(queue.RejectedRequestCount, Is.EqualTo(1u));

            Assert.That(queue.CopyAndClear(destination), Is.EqualTo(2));
            Assert.That(destination[0].ParticleCount, Is.EqualTo(10u));
            Assert.That(destination[1].ParticleCount, Is.EqualTo(20u));
            Assert.That(queue.Count, Is.Zero);
        }

        [Test]
        public void QueuePreservesFifoOrderAfterCopyClearsAndReusesItsFixedBuffer()
        {
            var queue = new FluidSpawnQueue(capacity: 3);
            var destination = new FluidSpawnRequest[3];

            queue.TryEnqueue(Request(particleCount: 10));
            queue.TryEnqueue(Request(particleCount: 20));
            Assert.That(queue.CopyAndClear(destination), Is.EqualTo(2));

            queue.TryEnqueue(Request(particleCount: 30));
            queue.TryEnqueue(Request(particleCount: 40));
            Assert.That(queue.CopyAndClear(destination), Is.EqualTo(2));
            Assert.That(destination[0].ParticleCount, Is.EqualTo(30u));
            Assert.That(destination[1].ParticleCount, Is.EqualTo(40u));
        }

        [Test]
        public void QueueRejectsNonPositiveCapacity()
        {
            Assert.That(
                () => new FluidSpawnQueue(capacity: 0),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void NullDestinationThrowsWithoutConsumingQueuedRequests()
        {
            var queue = new FluidSpawnQueue(capacity: 2);
            queue.TryEnqueue(Request(particleCount: 10));

            Assert.That(
                () => queue.CopyAndClear(null),
                Throws.TypeOf<ArgumentNullException>());
            Assert.That(queue.Count, Is.EqualTo(1));

            var destination = new FluidSpawnRequest[1];
            Assert.That(queue.CopyAndClear(destination), Is.EqualTo(1));
            Assert.That(destination[0].ParticleCount, Is.EqualTo(10u));
        }

        [Test]
        public void TooSmallDestinationThrowsWithoutConsumingQueuedRequests()
        {
            var queue = new FluidSpawnQueue(capacity: 2);
            queue.TryEnqueue(Request(particleCount: 10));
            queue.TryEnqueue(Request(particleCount: 20));

            Assert.That(
                () => queue.CopyAndClear(new FluidSpawnRequest[1]),
                Throws.TypeOf<ArgumentException>());
            Assert.That(queue.Count, Is.EqualTo(2));

            var destination = new FluidSpawnRequest[2];
            Assert.That(queue.CopyAndClear(destination), Is.EqualTo(2));
            Assert.That(destination[0].ParticleCount, Is.EqualTo(10u));
            Assert.That(destination[1].ParticleCount, Is.EqualTo(20u));
        }

        private static FluidSpawnRequest Request(uint particleCount)
        {
            return new FluidSpawnRequest(
                worldPosition: Vector3.zero,
                initialVelocity: Vector3.zero,
                radius: 0.5f,
                particleCount: particleCount,
                materialId: 0u,
                seed: particleCount,
                flags: 0u);
        }
    }
}
