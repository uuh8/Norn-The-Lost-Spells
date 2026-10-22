using System;

namespace Game.ElementField
{
    /// <summary>
    /// 将不稳定的 Frame delta 转成固定 PBF Tick 数的可复用 Accumulator。
    /// Clock 在 Runtime 初始化时只构造一次；暂停时 Runtime 不调用 Consume，
    /// 因而不会偷偷积累恢复后需要补跑的 Simulation Debt。
    /// </summary>
    public sealed class FluidSimulationClock
    {
        // Catch-up 的上限既是数值稳定策略也是 Frame Budget 合同：若允许任意 int，
        // 单帧 while 循环本身就可能耗尽主线程，反而比丢弃旧 Debt 更容易触发卡顿。
        public const int MaxSupportedCatchUpTicks = 8;

        private readonly double _tickInterval;
        private readonly int _maxCatchUpTicks;
        private double _accumulatedTime;

        public FluidSimulationClock(float tickRate, int maxCatchUpTicks)
        {
            if (tickRate <= 0f || float.IsNaN(tickRate) || float.IsInfinity(tickRate))
                throw new ArgumentOutOfRangeException(nameof(tickRate));
            _maxCatchUpTicks = ValidateMaxCatchUpTicks(maxCatchUpTicks);

            _tickInterval = 1d / tickRate;
        }

        public bool DroppedDebtLastConsume { get; private set; }

        public int Consume(float deltaTime)
        {
            if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
                throw new ArgumentOutOfRangeException(nameof(deltaTime));

            DroppedDebtLastConsume = false;
            if (deltaTime == 0f)
                return 0;

            _accumulatedTime += deltaTime;
            int ticks = 0;
            while (_accumulatedTime >= _tickInterval && ticks < _maxCatchUpTicks)
            {
                _accumulatedTime -= _tickInterval;
                ticks++;
            }

            if (ticks == _maxCatchUpTicks && _accumulatedTime >= _tickInterval)
            {
                // 丢弃极端长帧的旧债务是明确的 trade-off：少量 Simulation 时间被跳过，
                // 换取恢复帧不会继续补跑而陷入 Spiral of Death。
                _accumulatedTime = 0d;
                DroppedDebtLastConsume = true;
            }

            return ticks;
        }

        internal static int ValidateMaxCatchUpTicks(int maxCatchUpTicks)
        {
            if (maxCatchUpTicks < 1 || maxCatchUpTicks > MaxSupportedCatchUpTicks)
                throw new ArgumentOutOfRangeException(nameof(maxCatchUpTicks));

            return maxCatchUpTicks;
        }
    }
}
