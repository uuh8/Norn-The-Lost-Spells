using NUnit.Framework;

namespace Game.Character.Tests
{
    public sealed class BossTeleportDecisionTests
    {
        [Test]
        public void ShouldTeleport_WithZeroWeightOrActiveCooldown_ReturnsFalse()
        {
            Assert.That(
                BossActionUtilityEvaluator.ShouldTeleport(
                    0f,
                    0f,
                    1f,
                    0f),
                Is.False);
            Assert.That(
                BossActionUtilityEvaluator.ShouldTeleport(
                    10f,
                    0.1f,
                    1f,
                    0f),
                Is.False);
        }

        [Test]
        public void ShouldTeleport_UsesWeightRelativeToSelectedSpellScore()
        {
            Assert.That(
                BossActionUtilityEvaluator.ShouldTeleport(
                    1f,
                    0f,
                    3f,
                    0.24f),
                Is.True);
            Assert.That(
                BossActionUtilityEvaluator.ShouldTeleport(
                    1f,
                    0f,
                    3f,
                    0.25f),
                Is.False);
        }

        [Test]
        public void ShouldTeleport_WithoutSpellCandidate_UsesAvailableTeleport()
        {
            Assert.That(
                BossActionUtilityEvaluator.ShouldTeleport(
                    1f,
                    0f,
                    float.NegativeInfinity,
                    0.99f),
                Is.True);
        }

        [Test]
        public void TeleportRuntimeState_MarkUsedAndTick_TracksCooldown()
        {
            var state = new BossTeleportRuntimeState();

            state.MarkUsed(5f);
            state.Tick(1.5f);

            Assert.That(state.CooldownRemaining, Is.EqualTo(3.5f));

            state.Tick(10f);

            Assert.That(state.CooldownRemaining, Is.Zero);
        }
    }
}
