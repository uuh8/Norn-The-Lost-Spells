using System;
using System.IO;
using NUnit.Framework;

namespace Game.ElementField.Tests
{
    public sealed class ElementWorldPerformanceExporterTests
    {
        [Test]
        public void ExportPreservesMissingValuesEscapesTextAndRefusesOverwriteOrTraversal()
        {
            using var capture = new ElementWorldPerformanceCapture(2, 1);
            var exporter = new ElementWorldPerformanceExporter(capture, "water,\"probe\"\nline", "{}", "test");
            string parentDirectory = TestContext.CurrentContext.WorkDirectory ?? Path.GetTempPath();
            string run = "ewperf-" + Guid.NewGuid().ToString("N");
            Assert.That(exporter.TryExport(parentDirectory, run, out string directory, out string error), Is.True, error);
            StringAssert.Contains("\"mainMs\":null", File.ReadAllText(Path.Combine(directory, "summary.json")));
            StringAssert.Contains("water,\\\"probe\\\"\\nline", File.ReadAllText(Path.Combine(directory, "manifest.json")));
            StringAssert.Contains("\"validGpuTimingCount\":0", File.ReadAllText(Path.Combine(directory, "manifest.json")));
            StringAssert.StartsWith("frameStartTimestamp,firstSubmitTimestamp,cpuTimePresentCalled,cpuTimeFrameComplete",
                File.ReadAllText(Path.Combine(directory, "gpu-frames.csv")));
            Assert.That(exporter.TryExport(parentDirectory, run, out _, out _), Is.False);
            Assert.That(exporter.TryExport(parentDirectory, "../outside", out _, out _), Is.False);
            Assert.That(ElementWorldPerformanceExporter.Csv("a,\"b\"\nc"), Is.EqualTo("\"a,\"\"b\"\"\nc\""));
        }

        [Test]
        public void FullCaptureStopsWithoutOverwritingFirstEvent()
        {
            using var capture = new ElementWorldPerformanceCapture(2, 1);
            capture.Start(false);
            var first = new ElementPerformanceWriteResult { At = 10 };
            var second = new ElementPerformanceWriteResult { At = 20 };
            capture.RecordWrite(in first);
            capture.RecordWrite(in second);
            Assert.That(capture.Incomplete, Is.True);
            Assert.That(capture.Running, Is.False);
            Assert.That(capture.WriteCount, Is.EqualTo(1));
            Assert.That(capture.Writes[0].At, Is.EqualTo(10));
        }
    }
}
