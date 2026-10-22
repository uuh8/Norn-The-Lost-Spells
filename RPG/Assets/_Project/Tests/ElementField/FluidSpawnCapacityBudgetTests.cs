using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class FluidSpawnCapacityBudgetTests
    {
        [Test]
        public void StaleReadback_SubtractsRequestsAcceptedAfterSubmittedWatermark()
        {
            var budget = new FluidSpawnCapacityBudget(8u);
            Assert.That(budget.TryReserve(4u), Is.True);
            Assert.That(budget.ApplyPoolReadback(4u, 4u), Is.True);
            Assert.That(budget.AvailableParticles, Is.EqualTo(4u));

            Assert.That(budget.TryReserve(3u), Is.True);
            Assert.That(budget.ApplyPoolReadback(4u, 4u), Is.True,
                "旧快照看不到刚接受的 3 个粒子，Budget 必须把它们再次扣除。");
            Assert.That(budget.AvailableParticles, Is.EqualTo(1u));
            Assert.That(budget.TryReserve(2u), Is.False);
        }

        [Test]
        public void Invalidate_BlocksSpawnUntilNextPoolReadback()
        {
            var budget = new FluidSpawnCapacityBudget(8u);
            budget.InvalidateUntilNextReadback();
            Assert.That(budget.TryReserve(1u), Is.False);
            Assert.That(budget.ApplyPoolReadback(6u, 0u), Is.True);
            Assert.That(budget.AvailableParticles, Is.EqualTo(6u));
        }
    }
}
