using System;
using NUnit.Framework;
using Unity.Profiling;

namespace Game.Core.Tests
{
    /// <summary>
    /// 只量Journal预热后的Commit/查询/日志复制，不把测试对象构造或NUnit断言放进采样窗口。
    /// Unity当前运行时的GC.GetAllocatedBytesForCurrentThread曾对真实分配返回0，因此这里先用
    /// 1KiB数组正校准GC.Alloc事件，再测方法范围。事件数不是字节，也不能替代Native整帧Profiler。
    /// </summary>
    public sealed class WorldChunkJournalAllocationTests
    {
        [Test]
        public void WarmCommitLookupAndWrappedHistoryCopyHaveZeroPositivelyCalibratedAllocationEvents()
        {
            var key = new WorldChunkKey(WorldMaterialDomain.Liquid, -1, 0, 2);
            var journal = new WorldChunkCommitJournal(7, 4, 8, 128);
            var create = new[] { new WorldChunkChange(key, WorldChunkChangeKind.Create) };
            var update = new[] { new WorldChunkChange(key, WorldChunkChangeKind.Update) };
            var output = new WorldChunkCommitRecord[8];
            Assert.That(journal.TryCommit(create, 1), Is.True);
            // 先越过ring容量，覆盖包含整批淘汰和回绕的实际分支，而不是只测首条记录。
            for (int i = 0; i < 16; i++)
            {
                Assert.That(journal.TryCommit(update, 1), Is.True);
                Assert.That(journal.TryGetState(key, out _), Is.True);
                Assert.That(journal.TryCopyAfter(journal.HistoryFloorSequence, output, out _), Is.True);
            }

            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 128,
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            Assert.That(recorder.Valid, Is.True, "记录器不可用必须失败，不能把不可测当成零分配。");
            recorder.Stop(); recorder.Reset(); recorder.Start();
            var positiveControl = new byte[1024];
            GC.KeepAlive(positiveControl);
            recorder.Stop();
            int calibrationEvents = recorder.Count;
            Assert.That(calibrationEvents, Is.GreaterThan(0), "正校准必须能看到真实分配事件。");
            Assert.That(calibrationEvents, Is.LessThan(recorder.Capacity), "采样溢出不能作为有效计量。");

            bool allSucceeded = true;
            recorder.Reset(); recorder.Start();
            for (int i = 0; i < 256; i++)
            {
                allSucceeded &= journal.TryCommit(update, 1);
                allSucceeded &= journal.TryGetState(key, out _);
                allSucceeded &= journal.TryCopyAfter(journal.HistoryFloorSequence, output, out _);
            }
            recorder.Stop();
            int measuredEvents = recorder.Count;
            Assert.That(allSucceeded, Is.True, "不能用全部拒绝的空路径冒充真实日志热路径。");
            Assert.That(measuredEvents, Is.LessThan(recorder.Capacity));
            Assert.That(measuredEvents, Is.Zero, "仅限预热后的Journal方法范围；不代表材料模拟或Native全帧零GC。");
            TestContext.WriteLine("GC.Alloc calibration events=" + calibrationEvents + ", journal events=" + measuredEvents);
        }
    }
}
