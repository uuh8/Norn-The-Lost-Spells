using System;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class ElementWorldPerformanceScheduleTests
    {
        [Test]
        public void FrameJumpKeepsFifoAndConsumesEachWriteOnce()
        {
            var request = new ElementWriteRequest(Vector3.zero, MaterialId.Water, 2048, 1f,
                false, Vector3.zero, Vector3.up);
            var schedule = new ElementWorldPerformanceSchedule(new[]
            {
                new ElementPerformanceWrite(0, 0.1, in request),
                new ElementPerformanceWrite(1, 0.2, in request)
            });
            Assert.That(schedule.TryDequeueDue(0.05, out _), Is.False);
            Assert.That(schedule.TryDequeueDue(0.3, out var first), Is.True);
            Assert.That(first.Sequence, Is.Zero);
            Assert.That(schedule.TryDequeueDue(0.3, out var second), Is.True);
            Assert.That(second.Sequence, Is.EqualTo(1));
            Assert.That(schedule.TryDequeueDue(1, out _), Is.False);
        }

        [TestCase(8192, 256, 8u, 32, 2048)]
        [TestCase(8193, 256, 4u, 33, 4)]
        public void ParticleBudgetPreservesFinalPartialWrite(int particles, int batch, uint scale,
            int expectedWrites, int expectedLastAmount)
        {
            var schedule = ElementWorldPerformanceSchedule.CreateParticleFill(particles, batch,
                scale, MaterialId.Water, new[] { Vector3.zero }, 1f, 0.1);
            int count = 0;
            ElementPerformanceWrite last = default;
            while (schedule.TryDequeueDue(100, out var item)) { last = item; count++; }
            Assert.That(count, Is.EqualTo(expectedWrites));
            Assert.That(last.Request.TotalAmount, Is.EqualTo(expectedLastAmount));
        }

        [Test]
        public void InvalidTimelineAndAmountOverflowAreRejectedBeforeRunning()
        {
            var request = new ElementWriteRequest(Vector3.zero, MaterialId.Water, 8, 1f,
                false, Vector3.zero, Vector3.up);
            Assert.Throws<ArgumentException>(() => new ElementWorldPerformanceSchedule(new[]
            {
                new ElementPerformanceWrite(0, 1, in request),
                new ElementPerformanceWrite(1, 0, in request)
            }));
            Assert.Throws<ArgumentOutOfRangeException>(() => ElementWorldPerformanceSchedule
                .CreateParticleFill(8192, 8192, 8, MaterialId.Water, new[] { Vector3.zero }, 1f, 0.1));
        }
    }
}
