using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace Game.ElementField.Tests
{
    /// <summary>只验证诊断门槛与统计定义，不启动Native窗口、不写ENV/文件或注入真实世界。</summary>
    public sealed class OnlineLiquidMotionProbeTests
    {
        private static Type Probe()
        {
            var type = typeof(LiquidWorldRuntime).Assembly.GetType("Game.ElementField.OnlineLiquidMotionProbe");
            Assert.That(type, Is.Not.Null, "显式Native液体诊断入口尚未实现。"); return type;
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("relative/liquid-probe")]
        public void EmptyOrRelativeDirectoryCannotEnableNativeProbe(string directory)
        {
            var args = new object[] { directory, null };
            Assert.That((bool)Probe().GetMethod("TryGetOutputDirectory", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args), Is.False);
            Assert.That(args[1], Is.Null);
        }

        [Test]
        public void AbsoluteDirectoryIsNormalizedWithoutCreatingIt()
        {
            string directory = Path.Combine(Path.GetTempPath(), "Norn-LiquidProbe-" + Guid.NewGuid().ToString("N"));
            var args = new object[] { directory, null };
            Assert.That((bool)Probe().GetMethod("TryGetOutputDirectory", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args), Is.True);
            Assert.That(args[1], Is.EqualTo(Path.GetFullPath(directory)));
            Assert.That(Directory.Exists(directory), Is.False);
        }

        [Test]
        public void FrameDistributionUsesNearestRankAndPreservesInputSamples()
        {
            float[] samples = { 5, 1, 4, 2, 3, 999 };
            var statistics = Probe().GetMethod("SummarizeFrameTimes", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { samples, 5, 2 });
            object Field(string name) => statistics.GetType().GetField(name).GetValue(statistics);
            Assert.That(Field("SampleCount"), Is.EqualTo(5)); Assert.That(Field("DroppedSamples"), Is.EqualTo(2));
            Assert.That(Field("MeanMilliseconds"), Is.EqualTo(3d)); Assert.That(Field("P50Milliseconds"), Is.EqualTo(3f));
            Assert.That(Field("P95Milliseconds"), Is.EqualTo(5f)); Assert.That(Field("P99Milliseconds"), Is.EqualTo(5f));
            Assert.That(Field("MaximumMilliseconds"), Is.EqualTo(5f));
            Assert.That(samples, Is.EqualTo(new float[] { 5, 1, 4, 2, 3, 999 }));
        }
    }
}
