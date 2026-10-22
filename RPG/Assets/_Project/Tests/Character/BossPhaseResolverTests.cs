using NUnit.Framework;

namespace Game.Character.Tests
{
    public sealed class BossPhaseResolverTests
    {
        [TestCase(1f, BossPhase.Phase1)]
        [TestCase(0.7001f, BossPhase.Phase1)]
        [TestCase(0.7f, BossPhase.Phase2)]
        [TestCase(0.3501f, BossPhase.Phase2)]
        [TestCase(0.35f, BossPhase.Phase3)]
        [TestCase(0f, BossPhase.Phase3)]
        public void Resolve_MapsHealthRatioAtInclusiveThresholds(
            float healthRatio,
            BossPhase expected)
        {
            BossPhase result = BossPhaseResolver.Resolve(
                BossPhase.Phase1,
                healthRatio,
                0.7f,
                0.35f);

            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void Resolve_HealthRecoveryCannotMovePhaseBackward()
        {
            Assert.That(
                BossPhaseResolver.Resolve(
                    BossPhase.Phase2,
                    1f,
                    0.7f,
                    0.35f),
                Is.EqualTo(BossPhase.Phase2));
            Assert.That(
                BossPhaseResolver.Resolve(
                    BossPhase.Phase3,
                    1f,
                    0.7f,
                    0.35f),
                Is.EqualTo(BossPhase.Phase3));
        }
    }
}
