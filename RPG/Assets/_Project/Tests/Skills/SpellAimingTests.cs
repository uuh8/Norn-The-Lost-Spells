using NUnit.Framework;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class SpellAimingTests
    {
        [Test]
        public void SingleProjectile_NoOffset()
        {
            Assert.AreEqual(0f, SpellAiming.SpreadOffsetDegrees(0, 1, 30f), 1e-4f);
        }

        [Test]
        public void ZeroSpread_NoOffset()
        {
            Assert.AreEqual(0f, SpellAiming.SpreadOffsetDegrees(1, 3, 0f), 1e-4f);
        }

        [Test]
        public void Three_Spread30_FansEvenly()
        {
            Assert.AreEqual(-15f, SpellAiming.SpreadOffsetDegrees(0, 3, 30f), 1e-4f);
            Assert.AreEqual(0f, SpellAiming.SpreadOffsetDegrees(1, 3, 30f), 1e-4f);
            Assert.AreEqual(15f, SpellAiming.SpreadOffsetDegrees(2, 3, 30f), 1e-4f);
        }

        [Test]
        public void Two_Spread20_SymmetricEdges()
        {
            Assert.AreEqual(-10f, SpellAiming.SpreadOffsetDegrees(0, 2, 20f), 1e-4f);
            Assert.AreEqual(10f, SpellAiming.SpreadOffsetDegrees(1, 2, 20f), 1e-4f);
        }

        [Test]
        public void SingleProjectile_PhaseOffsetIsZero()
        {
            Assert.AreEqual(0f, SpellAiming.PhaseOffsetDegrees(0, 1), 1e-4f);
        }

        [Test]
        public void ThreeProjectiles_PhaseOffsetsAreEvenlyDistributed()
        {
            Assert.AreEqual(0f, SpellAiming.PhaseOffsetDegrees(0, 3), 1e-4f);
            Assert.AreEqual(120f, SpellAiming.PhaseOffsetDegrees(1, 3), 1e-4f);
            Assert.AreEqual(240f, SpellAiming.PhaseOffsetDegrees(2, 3), 1e-4f);
        }

        [Test]
        public void FourProjectiles_PhaseOffsetsAreEvenlyDistributed()
        {
            Assert.AreEqual(0f, SpellAiming.PhaseOffsetDegrees(0, 4), 1e-4f);
            Assert.AreEqual(90f, SpellAiming.PhaseOffsetDegrees(1, 4), 1e-4f);
            Assert.AreEqual(180f, SpellAiming.PhaseOffsetDegrees(2, 4), 1e-4f);
            Assert.AreEqual(270f, SpellAiming.PhaseOffsetDegrees(3, 4), 1e-4f);
        }

        [Test]
        public void PlaneTilt_SingleProjectile_IsZero()
        {
            Assert.AreEqual(0f, SpellAiming.PlaneTiltDegrees(0, 1, 35f), 1e-4f);
        }

        [Test]
        public void PlaneTilt_ThreeProjectiles_AlternatesAroundBaseTilt()
        {
            Assert.AreEqual(0f, SpellAiming.PlaneTiltDegrees(0, 3, 35f), 1e-4f);
            Assert.AreEqual(35f, SpellAiming.PlaneTiltDegrees(1, 3, 35f), 1e-4f);
            Assert.AreEqual(-35f, SpellAiming.PlaneTiltDegrees(2, 3, 35f), 1e-4f);
        }

        [Test]
        public void PlaneTilt_FiveProjectiles_ExpandsAlternatingTiltRings()
        {
            Assert.AreEqual(0f, SpellAiming.PlaneTiltDegrees(0, 5, 25f), 1e-4f);
            Assert.AreEqual(25f, SpellAiming.PlaneTiltDegrees(1, 5, 25f), 1e-4f);
            Assert.AreEqual(-25f, SpellAiming.PlaneTiltDegrees(2, 5, 25f), 1e-4f);
            Assert.AreEqual(50f, SpellAiming.PlaneTiltDegrees(3, 5, 25f), 1e-4f);
            Assert.AreEqual(-50f, SpellAiming.PlaneTiltDegrees(4, 5, 25f), 1e-4f);
        }
    }
}
