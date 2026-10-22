using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class WaterSimulationCompatibilityPolicyTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void LegacyRequestAlwaysUsesCellWithoutFallback(bool supportsCompute)
        {
            WaterSimulationCompatibilityDecision decision =
                WaterSimulationCompatibilityPolicy.Resolve(WaterSimulationMode.LegacyCell, supportsCompute);
            Assert.That(decision.RequestedMode, Is.EqualTo(WaterSimulationMode.LegacyCell));
            Assert.That(decision.EffectiveMode, Is.EqualTo(WaterSimulationMode.LegacyCell));
            Assert.That(decision.WaterBackend, Is.EqualTo(MaterialSimulationBackendKind.ElementCell));
            Assert.That(decision.UsedUnsupportedHardwareFallback, Is.False);
        }

        [Test]
        public void SupportedPbfRequestUsesGpuBackend()
        {
            WaterSimulationCompatibilityDecision decision =
                WaterSimulationCompatibilityPolicy.Resolve(WaterSimulationMode.GpuPbf, true);
            Assert.That(decision.EffectiveMode, Is.EqualTo(WaterSimulationMode.GpuPbf));
            Assert.That(decision.WaterBackend, Is.EqualTo(MaterialSimulationBackendKind.GpuPbfLiquid));
            Assert.That(decision.UsedUnsupportedHardwareFallback, Is.False);
        }

        [Test]
        public void UnsupportedPbfRequestUsesExplicitLegacyFallback()
        {
            WaterSimulationCompatibilityDecision decision =
                WaterSimulationCompatibilityPolicy.Resolve(WaterSimulationMode.GpuPbf, false);
            Assert.That(decision.RequestedMode, Is.EqualTo(WaterSimulationMode.GpuPbf));
            Assert.That(decision.EffectiveMode, Is.EqualTo(WaterSimulationMode.LegacyCell));
            Assert.That(decision.WaterBackend, Is.EqualTo(MaterialSimulationBackendKind.ElementCell));
            Assert.That(decision.UsedUnsupportedHardwareFallback, Is.True);
        }

        [Test]
        public void UndefinedRequestedModeIsRejected()
        {
            Assert.That(
                () => WaterSimulationCompatibilityPolicy.Resolve((WaterSimulationMode)255, true),
                Throws.TypeOf<System.ArgumentOutOfRangeException>());
        }
    }
}
