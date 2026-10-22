using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidResidentBoundsTrackerTests
    {
        [Test]
        public void IncludeKeepsHistoricalRegionWhenActiveBoundsMovesAway()
        {
            var tracker = new FluidResidentBoundsTracker();
            tracker.Include(new Bounds(Vector3.zero, new Vector3(10f, 6f, 10f)));
            tracker.Include(new Bounds(new Vector3(20f, 0f, 0f), new Vector3(10f, 6f, 10f)));

            Bounds resident = tracker.Bounds;
            Assert.That(resident.min.x, Is.EqualTo(-5f).Within(0.0001f));
            Assert.That(resident.max.x, Is.EqualTo(25f).Within(0.0001f));
            Assert.That(resident.size.y, Is.EqualTo(6f).Within(0.0001f));
        }

        [Test]
        public void IncludeSpawnCoversRequestedRadiusAndParticleSupport()
        {
            var tracker = new FluidResidentBoundsTracker();
            tracker.IncludeSpawn(new Vector3(3f, 2f, -4f), 1.25f, 0.3f);

            Bounds resident = tracker.Bounds;
            // Bounds 的 center +/- extent 有浮点舍入；验证空间误差，不能要求二进制逐位相等。
            Assert.That(Vector3.Distance(resident.min, new Vector3(1.45f, 0.45f, -5.55f)), Is.LessThan(0.00001f));
            Assert.That(Vector3.Distance(resident.max, new Vector3(4.55f, 3.55f, -2.45f)), Is.LessThan(0.00001f));
        }
    }
}
