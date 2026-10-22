using NUnit.Framework;

namespace Game.Combat.Tests
{
    public sealed class ToxicCombustionProcessTests
    {
        private static readonly ToxicCombustionTuning Tuning = new ToxicCombustionTuning
        {
            FireThreshold = 20f, PoisonThreshold = 20f, WindUpSeconds = 0.2f,
            MaxPoisonConsume = 40f, MinEffectiveConsume = 5f,
            BaseDamage = 8f, DamagePerPoison = 0.4f, Radius = 2.5f,
        };

        [Test]
        public void WindupDoesNotConsumeThenCompletionClampsToLatestPoison()
        {
            var process = new ToxicCombustionProcess();
            Assert.That(process.TryStart(100, 80, in Tuning), Is.True);
            Assert.That(process.Tick(0.1f, 80, in Tuning, out int early, out _, out _), Is.False);
            Assert.That(early, Is.Zero);
            Assert.That(process.Tick(0.1f, 12, in Tuning, out int consumed, out float damage, out float radius), Is.True);
            Assert.That(consumed, Is.EqualTo(12));
            Assert.That(damage, Is.EqualTo(12.8f).Within(1e-5f));
            Assert.That(radius, Is.EqualTo(2.5f));
        }

        [Test]
        public void DuplicateStartIsRejectedAndCancelClearsProcess()
        {
            var process = new ToxicCombustionProcess();
            Assert.That(process.TryStart(100, 80, in Tuning), Is.True);
            Assert.That(process.TryStart(100, 80, in Tuning), Is.False);
            process.Cancel();
            Assert.That(process.IsActive, Is.False);
        }

        [Test]
        public void ProgressiveStatusPathPreservesWindupConsumptionAndSharedDamageFormula()
        {
            var process = new ToxicCombustionProcess();
            Assert.That(process.TryStartFromIntensity(100f, 40f, in Tuning), Is.True);
            Assert.That(process.TickProgressive(
                0.1f, 40f, in Tuning, out float first, out _, out _, out _), Is.False);
            Assert.That(first, Is.EqualTo(20f).Within(1e-5f));
            Assert.That(process.TickProgressive(
                0.1f, 20f, in Tuning, out float second, out bool effective,
                out float damage, out float radius), Is.True);
            Assert.That(second, Is.EqualTo(20f).Within(1e-5f));
            Assert.That(effective, Is.True);
            Assert.That(damage, Is.EqualTo(24f).Within(1e-5f));
            Assert.That(radius, Is.EqualTo(2.5f));
        }
    }
}
