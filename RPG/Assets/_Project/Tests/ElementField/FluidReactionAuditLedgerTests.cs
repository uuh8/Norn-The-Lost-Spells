using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class FluidReactionAuditLedgerTests
    {
        [TestCase(80ul, -2, 8u, 64ul)]
        [TestCase(80ul, 3, 4u, 92ul)]
        [TestCase(80ul, 0, 8u, 80ul)]
        public void ApplyParticleDelta_UsesMaterialSpecificScale(
            ulong submitted, int delta, uint scale, ulong expected)
        {
            Assert.That(FluidReactionAuditLedger.TryApplyParticleDelta(
                submitted, delta, scale, out ulong actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void ApplyParticleDelta_RejectsUnderflowOverflowAndZeroScale()
        {
            Assert.That(FluidReactionAuditLedger.TryApplyParticleDelta(
                7u, -1, 8u, out _), Is.False);
            Assert.That(FluidReactionAuditLedger.TryApplyParticleDelta(
                ulong.MaxValue, 1, 1u, out _), Is.False);
            Assert.That(FluidReactionAuditLedger.TryApplyParticleDelta(
                1u, 0, 0u, out _), Is.False);
        }
    }
}
