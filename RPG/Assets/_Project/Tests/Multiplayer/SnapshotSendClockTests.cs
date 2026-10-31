using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 直接给发送时钟喂入可复查的帧时间，检查真正获准发送的次数。
    /// 不依赖实际机器 FPS，也不把“配置填了 15 Hz”当成已达到该频率。
    /// </summary>
    public sealed class SnapshotSendClockTests
    {
        [TestCase(15, 300)]
        [TestCase(30, 600)]
        public void StableThirtyFps_PreservesConfiguredSendRate(int sendRate, int expectedSends)
        {
            var clock = new SnapshotSendClock();
            int sends = 0;

            // 600 个采样点覆盖 [0, 20) 秒。目标 15/30 Hz 应分别发出约 300/600 份快照；
            // 允许一个边界样本误差，但不能因每帧稍晚于 deadline 而持续降为 10/15 Hz。
            for (int frame = 0; frame < 600; frame++)
                if (clock.TrySend(frame / 30d, 1f / sendRate)) sends++;

            Assert.That(sends, Is.EqualTo(expectedSends).Within(1),
                "发送资格必须随真实时间保持配置频率，不能每次从当前帧重新等待完整 interval。");
        }

        [TestCase(15, 300)]
        [TestCase(30, 600)]
        public void SlightFrameJitter_PreservesConfiguredSendRate(int sendRate, int expectedSends)
        {
            var clock = new SnapshotSendClock();
            int sends = 0;
            double now = 0;

            // 帧间隔交替增加/减少 2 ms，每两帧的平均值仍是 30 FPS。
            // 这会暴露“偶尔晚一点就把余数丢掉”的累积误差，而非只检查理想整除时间。
            for (int frame = 0; frame < 600; frame++)
            {
                if (clock.TrySend(now, 1f / sendRate)) sends++;
                now += 1d / 30 + ((frame & 1) == 0 ? .002 : -.002);
            }

            Assert.That(sends, Is.EqualTo(expectedSends).Within(1),
                "轻微帧时间抖动只能改变单份快照何时发出，不应长期削减快照数量。");
        }

        [Test]
        public void LateFrame_SkipsExpiredPeriodsAndSendsOnlyLatestStateOnce()
        {
            var clock = new SnapshotSendClock();
            const float interval = .125f; // 二进制可精确表达，隔离 float 边界与过载行为。
            Assert.True(clock.TrySend(10, interval));

            // 中间错过六个发送周期，也只能发送一份“现在的状态”。
            // 若逐个补发，包中并没有那些历史时刻的新状态，只会制造 burst 与额外排队。
            Assert.True(clock.TrySend(10.8, interval));
            Assert.False(clock.TrySend(10.8, interval));
            Assert.False(clock.TrySend(10.874, interval));

            // 晚帧剩下的 75 ms 已经消耗当前周期；下一资格仍位于原相位的 10.875。
            // 不能从 10.8 再等完整 125 ms，否则之后所有快照都会继续延后。
            Assert.True(clock.TrySend(10.875, interval));
            Assert.False(clock.TrySend(10.875, interval));
        }

        [Test]
        public void Reset_DiscardsPreviousSessionDeadline()
        {
            var clock = new SnapshotSendClock();
            Assert.True(clock.TrySend(100, .125f));
            clock.Reset();
            Assert.True(clock.TrySend(0, .125f));
            Assert.False(clock.TrySend(0, .125f));
        }

        [TestCase(double.NaN, .125f)]
        [TestCase(double.PositiveInfinity, .125f)]
        [TestCase(-1d, .125f)]
        [TestCase(1d, float.NaN)]
        [TestCase(1d, float.PositiveInfinity)]
        [TestCase(1d, 0f)]
        [TestCase(1d, -.125f)]
        [TestCase(1d, float.Epsilon)]
        [TestCase(double.MaxValue, .125f)]
        public void InvalidTimeOrPeriod_IsRejectedWithoutPoisoningNextValidSession(double now, float interval)
        {
            var clock = new SnapshotSendClock();
            Assert.False(clock.TrySend(now, interval));
            Assert.True(clock.TrySend(0, .125f), "无效参数不得把错误 deadline 留给下一次合法调用。");
            Assert.False(clock.TrySend(0, .125f));
        }
    }
}
