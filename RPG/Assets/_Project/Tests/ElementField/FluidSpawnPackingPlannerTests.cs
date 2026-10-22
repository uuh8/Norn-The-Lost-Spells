using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Utils;

namespace Game.ElementField.Tests
{
    public sealed class FluidSpawnPackingPlannerTests
    {
        [Test]
        public void TwoHundredWaterParticles_ProduceDensityAwarePacking()
        {
            bool created = FluidSpawnPackingPlanner.TryCreate(
                200u, 1f, 1000f, 0.05f, 0.75f,
                new Vector3(1f, 2f, 3f), Vector3.up,
                out FluidSpawnPacking packing);

            Assert.That(created, Is.True);
            Assert.That(packing.RestSpacing, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(packing.LatticeSide, Is.EqualTo(6));
            Assert.That(packing.RequiredRadius, Is.EqualTo(0.4330127f).Within(1e-6f));
            Assert.That(packing.Center, Is.EqualTo(new Vector3(1f, 2.4840127f, 3f)).Using(Vector3ComparerWithEqualsOperator.Instance));
        }

        [Test]
        public void ZeroRequestedRadius_AcceptsComputedRadius()
        {
            Assert.That(TryCreate(0f, out FluidSpawnPacking packing), Is.True);
            Assert.That(packing.RequiredRadius, Is.EqualTo(0.4330127f).Within(1e-6f));
        }

        [Test]
        public void LargerRequestedRadius_DoesNotDilutePackingRadius()
        {
            Assert.That(TryCreate(0.75f, out FluidSpawnPacking packing), Is.True);
            Assert.That(packing.RequiredRadius, Is.EqualTo(0.4330127f).Within(1e-6f));
        }

        [Test]
        public void PositiveRadiusBelowRequiredRadius_IsRejectedWithDefaultOutput()
        {
            Assert.That(TryCreate(0.4f, out FluidSpawnPacking packing), Is.False);
            Assert.That(packing, Is.EqualTo(default(FluidSpawnPacking)));
        }

        [TestCase(0u)]
        [TestCase(65537u)]
        public void InvalidParticleCount_IsRejected(uint particleCount)
        {
            Assert.That(FluidSpawnPackingPlanner.TryCreate(
                particleCount, 1f, 1000f, 0.05f, 0f,
                Vector3.zero, Vector3.up, out FluidSpawnPacking packing), Is.False);
            Assert.That(packing, Is.EqualTo(default(FluidSpawnPacking)));
        }

        [TestCase(0f, 1000f)]
        [TestCase(-1f, 1000f)]
        [TestCase(1f, 0f)]
        [TestCase(1f, -1000f)]
        [TestCase(float.NaN, 1000f)]
        [TestCase(1f, float.PositiveInfinity)]
        public void InvalidMassOrDensity_IsRejected(float mass, float density)
        {
            Assert.That(FluidSpawnPackingPlanner.TryCreate(
                200u, mass, density, 0.05f, 0f,
                Vector3.zero, Vector3.up, out _), Is.False);
        }

        [Test]
        public void InvalidGeometryInputs_AreRejected()
        {
            AssertRejected(-0.01f, 0f, Vector3.zero, Vector3.up);
            AssertRejected(0.05f, -0.01f, Vector3.zero, Vector3.up);
            AssertRejected(0.05f, float.NaN, Vector3.zero, Vector3.up);
            AssertRejected(0.05f, 0f, new Vector3(float.PositiveInfinity, 0f, 0f), Vector3.up);
            AssertRejected(0.05f, 0f, Vector3.zero, Vector3.zero);
            AssertRejected(0.05f, 0f, Vector3.zero, new Vector3(float.NaN, 0f, 0f));
        }

        private static bool TryCreate(float requestedRadius, out FluidSpawnPacking packing)
        {
            return FluidSpawnPackingPlanner.TryCreate(
                200u, 1f, 1000f, 0.05f, requestedRadius,
                new Vector3(1f, 2f, 3f), Vector3.up, out packing);
        }

        private static void AssertRejected(
            float particleRadius,
            float requestedRadius,
            Vector3 hitPoint,
            Vector3 surfaceNormal)
        {
            Assert.That(FluidSpawnPackingPlanner.TryCreate(
                200u, 1f, 1000f, particleRadius, requestedRadius,
                hitPoint, surfaceNormal, out FluidSpawnPacking packing), Is.False);
            Assert.That(packing, Is.EqualTo(default(FluidSpawnPacking)));
        }
    }
}
