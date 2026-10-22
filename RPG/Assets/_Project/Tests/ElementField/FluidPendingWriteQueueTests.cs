using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidPendingWriteQueueTests
    {
        [Test]
        public void Queue_PreservesFullRequestAndRejectsWithoutOverwritingOldest()
        {
            var queue = new FluidPendingWriteQueue(1);
            var first = new ElementWriteRequest(Vector3.one, MaterialId.Poison, 1600, 1.2f,
                true, new Vector3(1f, 2f, 3f), Vector3.up);
            var second = new ElementWriteRequest(Vector3.zero, MaterialId.Water, 8, .2f, false);
            Assert.That(queue.TryEnqueue(in first), Is.True);
            Assert.That(queue.TryEnqueue(in second), Is.False);
            Assert.That(queue.RejectedCount, Is.EqualTo(1u));
            Assert.That(queue.TryDequeue(out ElementWriteRequest restored), Is.True);
            Assert.That(restored.WorldPosition, Is.EqualTo(first.WorldPosition));
            Assert.That(restored.MaterialKind, Is.EqualTo(first.MaterialKind));
            Assert.That(restored.TotalAmount, Is.EqualTo(first.TotalAmount));
            Assert.That(restored.InitialVelocity, Is.EqualTo(first.InitialVelocity));
            Assert.That(restored.SurfaceNormal, Is.EqualTo(first.SurfaceNormal));
        }

        [Test]
        public void Queue_WrapsAndMaintainsFifoOrder()
        {
            var queue = new FluidPendingWriteQueue(2);
            var first = Request(1f);
            var second = Request(2f);
            var third = Request(3f);
            Assert.That(queue.TryEnqueue(in first), Is.True);
            Assert.That(queue.TryEnqueue(in second), Is.True);
            Assert.That(queue.TryDequeue(out ElementWriteRequest removed), Is.True);
            Assert.That(removed.WorldPosition.x, Is.EqualTo(1f));
            Assert.That(queue.TryEnqueue(in third), Is.True);
            Assert.That(queue.TryDequeue(out removed), Is.True);
            Assert.That(removed.WorldPosition.x, Is.EqualTo(2f));
            Assert.That(queue.TryDequeue(out removed), Is.True);
            Assert.That(removed.WorldPosition.x, Is.EqualTo(3f));
        }

        private static ElementWriteRequest Request(float x) =>
            new ElementWriteRequest(new Vector3(x, 0f, 0f), MaterialId.Water, 8, .1f, false);
    }
}
