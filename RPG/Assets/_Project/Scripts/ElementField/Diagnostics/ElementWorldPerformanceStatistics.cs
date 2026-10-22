using System;

namespace Game.ElementField
{
    public readonly struct PerformanceMetricSample
    {
        public readonly double Value;
        public readonly bool Valid;
        public PerformanceMetricSample(double value, bool valid) { Value = value; Valid = valid; }
    }

    public readonly struct PerformancePercentiles
    {
        public readonly bool Valid;
        public readonly int ValidCount;
        public readonly double P50, P95, P99, Maximum;
        public PerformancePercentiles(int count, double p50, double p95, double p99, double maximum)
        { Valid = count > 0; ValidCount = count; P50 = p50; P95 = p95; P99 = p99; Maximum = maximum; }
    }

    public static class ElementWorldPerformanceStatistics
    {
        /// <summary>仅停止采样后执行；Missing 不能作为 0 ms 混入性能分位数。</summary>
        public static PerformancePercentiles Calculate(PerformanceMetricSample[] samples, int count, double[] scratch)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (count < 0 || count > samples.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (scratch == null || scratch.Length < count) throw new ArgumentException("排序工作区不足。");
            int valid = 0;
            for (int i = 0; i < count; i++)
                if (samples[i].Valid && ElementWorldPerformanceSchedule.Finite(samples[i].Value))
                    scratch[valid++] = samples[i].Value;
            if (valid == 0) return default;
            Array.Sort(scratch, 0, valid);
            return new PerformancePercentiles(valid, scratch[(int)Math.Ceiling(valid * 0.5) - 1],
                scratch[(int)Math.Ceiling(valid * 0.95) - 1], scratch[(int)Math.Ceiling(valid * 0.99) - 1], scratch[valid - 1]);
        }
    }
}
