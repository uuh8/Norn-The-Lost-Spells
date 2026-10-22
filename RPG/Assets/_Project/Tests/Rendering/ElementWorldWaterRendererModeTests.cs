using Game.ElementField;
using NUnit.Framework;

namespace Game.Rendering.Tests
{
    public sealed class ElementWorldWaterRendererModeTests
    {
        [TestCase(WaterSimulationMode.LegacyCell, true, false)]
        [TestCase(WaterSimulationMode.GpuPbf, false, true)]
        public void LegacyAndGpuRenderersAreMutuallyExclusive(
            WaterSimulationMode mode,
            bool expectedLegacy,
            bool expectedGpu)
        {
            Assert.That(WaterRendererModePolicy.ShouldRenderLegacy(mode), Is.EqualTo(expectedLegacy));
            Assert.That(WaterRendererModePolicy.ShouldRenderGpuPbf(mode), Is.EqualTo(expectedGpu));
        }
    }
}
