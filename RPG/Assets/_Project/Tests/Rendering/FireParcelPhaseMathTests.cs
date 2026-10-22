using System;
using NUnit.Framework;

namespace Game.Rendering.Tests
{
    public sealed class FireParcelPhaseMathTests
    {
        [Test]
        public void LifecycleBlendsContinuouslyFromSurfaceToGasAndThenFadesOut()
        {
            var settings = new FireParcelPhaseSettings(0.8f, 0.2f, 1.2f, 1f, 2f);

            FireParcelPhaseSample young = FireParcelPhaseMath.Evaluate(0f, in settings);
            FireParcelPhaseSample middle = FireParcelPhaseMath.Evaluate(0.9f, in settings);
            FireParcelPhaseSample expired = FireParcelPhaseMath.Evaluate(
                settings.TotalLifetimeSeconds, in settings);

            Assert.That(young.SurfaceWeight, Is.EqualTo(1f));
            Assert.That(young.GasWeight, Is.EqualTo(0f));
            Assert.That(middle.SurfaceWeight, Is.GreaterThan(0f));
            Assert.That(middle.GasWeight, Is.GreaterThan(0f));
            Assert.That(expired.SurfaceWeight, Is.EqualTo(0f));
            Assert.That(expired.GasWeight, Is.EqualTo(0f));
            Assert.That(middle.Radius, Is.InRange(1f, 2f));
        }

        [Test]
        public void ExtendingLiquidLikeHoldAddsLifetimeWithoutShorteningGasTail()
        {
            var shortLiquid = new FireParcelPhaseSettings(0.5f, 0.25f, 1.4f, 1f, 2f);
            var longLiquid = new FireParcelPhaseSettings(1.5f, 0.25f, 1.4f, 1f, 2f);
            float relativeGasAge = 0.6f;

            FireParcelPhaseSample shortSample = FireParcelPhaseMath.Evaluate(
                shortLiquid.GasFullVisibilityStartSeconds + relativeGasAge,
                in shortLiquid);
            FireParcelPhaseSample longSample = FireParcelPhaseMath.Evaluate(
                longLiquid.GasFullVisibilityStartSeconds + relativeGasAge,
                in longLiquid);

            Assert.That(longLiquid.TotalLifetimeSeconds - shortLiquid.TotalLifetimeSeconds,
                Is.EqualTo(1f).Within(1e-6f));
            Assert.That(longSample.GasWeight, Is.EqualTo(shortSample.GasWeight).Within(1e-6f),
                "Liquid-like Hold 延长后，Gas 自己的完整生命周期曲线必须保持不变。");
            Assert.That(longSample.SurfaceWeight, Is.EqualTo(shortSample.SurfaceWeight).Within(1e-6f));
        }

        [Test]
        public void InvalidPhaseSettingsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new FireParcelPhaseSettings(0f, 0.2f, 0.9f, 1f, 2f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new FireParcelPhaseSettings(0.4f, 0.2f, float.NaN, 1f, 2f));
        }
    }
}
