using NUnit.Framework;

namespace Game.Character.Tests
{
    public sealed class PlayerAirActionBudgetTests
    {
        [Test]
        public void ExtraJump_CanOnlyBeConsumedOnceBeforeLandingReset()
        {
            PlayerAirActionBudget budget = new PlayerAirActionBudget(1, 1);

            Assert.That(budget.TryConsumeExtraJump(), Is.True);
            Assert.That(budget.TryConsumeExtraJump(), Is.False);

            budget.ResetForGrounded();
            Assert.That(budget.TryConsumeExtraJump(), Is.True);
        }

        [Test]
        public void AirDash_HasIndependentBudget()
        {
            PlayerAirActionBudget budget = new PlayerAirActionBudget(1, 1);

            Assert.That(budget.TryConsumeAirDash(), Is.True);
            Assert.That(budget.TryConsumeAirDash(), Is.False);
            Assert.That(budget.TryConsumeExtraJump(), Is.True);
        }

        [TestCase(-20f, 0f, 0f)]
        [TestCase(-20f, 0.25f, -5f)]
        [TestCase(10f, 0.5f, 5f)]
        [TestCase(-20f, -1f, 0f)]
        [TestCase(-20f, 2f, -20f)]
        public void AirDashVerticalVelocityRetention_ScalesAndClamps(
            float incomingVelocity,
            float retention,
            float expected)
        {
            float result = AirDashVerticalVelocityPolicy.Apply(incomingVelocity, retention);

            Assert.That(result, Is.EqualTo(expected).Within(0.0001f));
        }
    }
}
