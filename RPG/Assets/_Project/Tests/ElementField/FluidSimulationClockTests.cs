using System;
using NUnit.Framework;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Clock 是 Frame 时间与固定 Simulation Tick 的纯边界。它不驱动 Unity 生命周期，
    /// 因而可以直接验证长帧丢弃旧债务，避免卡顿后的 Spiral of Death。
    /// </summary>
    public sealed class FluidSimulationClockTests
    {
        [Test]
        public void LongFrameConsumesOnlyMaxCatchUpTicksAndDropsOldDebt()
        {
            var clock = new FluidSimulationClock(tickRate: 10f, maxCatchUpTicks: 3);

            Assert.That(clock.Consume(0.45f), Is.EqualTo(3));
            Assert.That(clock.DroppedDebtLastConsume, Is.True);
            Assert.That(clock.Consume(0.09f), Is.Zero,
                "超长帧剩余的 0.15 秒债务必须被丢弃，不能在恢复帧继续追赶。");
        }

        [Test]
        public void ClockAccumulatesShortFramesUntilOneFixedTickIsReady()
        {
            var clock = new FluidSimulationClock(tickRate: 10f, maxCatchUpTicks: 3);

            Assert.That(clock.Consume(0.05f), Is.Zero);
            Assert.That(clock.Consume(0.05f), Is.EqualTo(1));
            Assert.That(clock.DroppedDebtLastConsume, Is.False);
        }

        [Test]
        public void ClockAcceptsHardCatchUpLimitOfEightTicks()
        {
            var clock = new FluidSimulationClock(
                tickRate: 10f,
                maxCatchUpTicks: FluidSimulationClock.MaxSupportedCatchUpTicks);

            Assert.That(clock.Consume(0.8f), Is.EqualTo(8));
        }

        [Test]
        public void ClockRejectsNonPositiveTickRate()
        {
            Assert.That(
                () => new FluidSimulationClock(tickRate: 0f, maxCatchUpTicks: 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void ClockRejectsCatchUpLimitAboveHardMaximum()
        {
            Assert.That(
                () => new FluidSimulationClock(
                    tickRate: 10f,
                    maxCatchUpTicks: FluidSimulationClock.MaxSupportedCatchUpTicks + 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void ClockRejectsNegativeNanAndInfiniteDeltaTime()
        {
            var clock = new FluidSimulationClock(tickRate: 10f, maxCatchUpTicks: 1);

            Assert.That(
                () => clock.Consume(-0.01f),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => clock.Consume(float.NaN),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => clock.Consume(float.PositiveInfinity),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => clock.Consume(float.NegativeInfinity),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
}
