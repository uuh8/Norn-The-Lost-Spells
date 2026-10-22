using System;
using NUnit.Framework;

namespace Game.Rendering.Tests
{
    public sealed class FluidCrownMathTests
    {
        [TestCase(3, 4, 12, 0f)]
        [TestCase(4, 4, 12, 0f)]
        [TestCase(8, 4, 12, 0.5f)]
        [TestCase(12, 4, 12, 1f)]
        [TestCase(20, 4, 12, 1f)]
        public void CalculateSupport_MapsEdgeToInteriorRange(
            int neighborCount,
            int edgeNeighborCount,
            int interiorNeighborCount,
            float expected)
        {
            float result = FluidCrownMath.CalculateSupport(
                neighborCount,
                edgeNeighborCount,
                interiorNeighborCount);

            Assert.That(result, Is.EqualTo(expected).Within(1e-6f));
        }

        [Test]
        public void CalculateAppliedUpScale_UsesSupportAndFalloff()
        {
            float result = FluidCrownMath.CalculateAppliedUpScale(
                0.5f,
                0.8f,
                2f,
                1f);

            Assert.That(result, Is.EqualTo(1.2f).Within(1e-6f));
        }

        [Test]
        public void CalculateAppliedUpScale_NeverExceedsTwoKernelRadii()
        {
            float result = FluidCrownMath.CalculateAppliedUpScale(
                1f,
                1f,
                1f,
                0.6f);

            Assert.That(result, Is.EqualTo(1.2f).Within(1e-6f));
        }

        [Test]
        public void CalculateAppliedUpScale_ZeroHeightPreservesLegacyScale()
        {
            float result = FluidCrownMath.CalculateAppliedUpScale(
                1f,
                0f,
                1.5f,
                1f);

            Assert.That(result, Is.EqualTo(1f));
        }

        [Test]
        public void CrownMath_RejectsInvalidContracts()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidCrownMath.CalculateSupport(-1, 4, 12));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidCrownMath.CalculateSupport(4, 4, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidCrownMath.CalculateAppliedUpScale(1f, float.NaN, 1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidCrownMath.CalculateAppliedUpScale(1f, 0.5f, 0f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FluidCrownMath.CalculateAppliedUpScale(1f, 0.5f, 1f, 0f));
        }
    }
}
