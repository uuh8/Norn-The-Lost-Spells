using NUnit.Framework;

namespace Game.Character.Tests
{
    public sealed class BossProgramRuntimeStateTests
    {
        [Test]
        public void TickCooldowns_DecrementsInPlaceAndClampsAtZero()
        {
            var state = new BossProgramRuntimeState(2, 3);
            state.MarkUsed(1, 0.5f);

            state.TickCooldowns(0.2f);
            Assert.That(state.GetCooldownRemaining(1), Is.EqualTo(0.3f).Within(0.0001f));

            state.TickCooldowns(1f);
            Assert.That(state.GetCooldownRemaining(1), Is.Zero);
        }

        [Test]
        public void MarkUsed_FixedRingBufferOverwritesOldestHistory()
        {
            var state = new BossProgramRuntimeState(3, 2);

            state.MarkUsed(1, 1f);
            state.MarkUsed(2, 1f);
            state.MarkUsed(1, 1f);

            Assert.That(state.CountRecentUses(1), Is.EqualTo(1));
            Assert.That(state.CountRecentUses(2), Is.EqualTo(1));
            Assert.That(state.CountRecentUses(0), Is.Zero);
        }
    }
}
