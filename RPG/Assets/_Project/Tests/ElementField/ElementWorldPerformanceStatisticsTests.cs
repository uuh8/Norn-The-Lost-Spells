using System;
using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class ElementWorldPerformanceStatisticsTests
    {
        [Test]
        public void MissingSamplesDoNotBecomeZeroAndPercentilesUseNearestRank()
        {
            var samples = new[] { new PerformanceMetricSample(0, false),
                new PerformanceMetricSample(10, true), new PerformanceMetricSample(20, true),
                new PerformanceMetricSample(30, true), new PerformanceMetricSample(double.NaN, true) };
            var result = ElementWorldPerformanceStatistics.Calculate(samples, samples.Length, new double[5]);
            Assert.That(result.ValidCount, Is.EqualTo(3));
            Assert.That(result.P50, Is.EqualTo(20));
            Assert.That(result.P95, Is.EqualTo(30));
            Assert.That(result.P99, Is.EqualTo(30));
        }

        [Test]
        public void EmptyAndInvalidInputsAreExplicit()
        {
            var samples = new PerformanceMetricSample[2];
            Assert.That(ElementWorldPerformanceStatistics.Calculate(samples, 2, new double[2]).Valid, Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => ElementWorldPerformanceStatistics.Calculate(samples, 3, new double[3]));
            Assert.Throws<ArgumentException>(() => ElementWorldPerformanceStatistics.Calculate(samples, 2, new double[1]));
        }
    }
}
