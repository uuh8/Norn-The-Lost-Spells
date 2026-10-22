using NUnit.Framework;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 验证 Water Renderer 的 Dirty Chunk 调度规则。
    /// 测试只关心 Version、预算和跨帧游标，不创建 GameObject 或 Mesh，
    /// 从而把“哪些 Chunk 本帧允许重建”与 Unity 渲染资源管理分离。
    /// </summary>
    public sealed class WaterChunkRebuildSchedulerTests
    {
        private readonly uint[] _currentVersions = new uint[4];
        private readonly uint[] _lastSeenVersions = new uint[4];
        private readonly int[] _selectedIndices = new int[4];

        [SetUp]
        public void SetUp()
        {
            for (int i = 0; i < 4; i++)
            {
                _currentVersions[i] = 0;
                _lastSeenVersions[i] = 0;
                _selectedIndices[i] = -1;
            }
        }

        [Test]
        public void DirtySelectionNeverExceedsPerFrameBudget()
        {
            SetAllCurrentVersions(1);

            int selectedCount = WaterChunkRebuildScheduler.SelectDirtyChunks(
                _currentVersions,
                _lastSeenVersions,
                startIndex: 0,
                maxRebuilds: 2,
                _selectedIndices,
                out int nextScanIndex);

            Assert.That(selectedCount, Is.EqualTo(2));
            Assert.That(_selectedIndices[0], Is.EqualTo(0));
            Assert.That(_selectedIndices[1], Is.EqualTo(1));
            Assert.That(nextScanIndex, Is.EqualTo(2));
        }

        [Test]
        public void UnchangedChunksAreSkippedWithoutSpendingBudget()
        {
            _currentVersions[1] = 2;
            _currentVersions[3] = 3;
            _lastSeenVersions[1] = 1;
            _lastSeenVersions[3] = 2;

            int selectedCount = WaterChunkRebuildScheduler.SelectDirtyChunks(
                _currentVersions,
                _lastSeenVersions,
                startIndex: 0,
                maxRebuilds: 4,
                _selectedIndices,
                out int nextScanIndex);

            Assert.That(selectedCount, Is.EqualTo(2));
            Assert.That(_selectedIndices[0], Is.EqualTo(1));
            Assert.That(_selectedIndices[1], Is.EqualTo(3));
            Assert.That(nextScanIndex, Is.Zero,
                "扫描完整个数组后，游标应回到本轮起点，而不是永久跳过某个 Chunk。");
        }

        [Test]
        public void ScanCursorWrapsSoLaterFramesCannotStarveEarlierChunks()
        {
            _currentVersions[0] = 1;
            _currentVersions[3] = 1;

            int firstCount = WaterChunkRebuildScheduler.SelectDirtyChunks(
                _currentVersions,
                _lastSeenVersions,
                startIndex: 3,
                maxRebuilds: 1,
                _selectedIndices,
                out int secondFrameStart);

            Assert.That(firstCount, Is.EqualTo(1));
            Assert.That(_selectedIndices[0], Is.EqualTo(3));
            Assert.That(secondFrameStart, Is.Zero);

            // 模拟 Renderer 在成功重建后确认 Version；下一帧应轮到索引 0。
            _lastSeenVersions[3] = _currentVersions[3];
            int secondCount = WaterChunkRebuildScheduler.SelectDirtyChunks(
                _currentVersions,
                _lastSeenVersions,
                secondFrameStart,
                maxRebuilds: 1,
                _selectedIndices,
                out _);

            Assert.That(secondCount, Is.EqualTo(1));
            Assert.That(_selectedIndices[0], Is.Zero);
        }

        [Test]
        public void ZeroBudgetSelectsNothingAndPreservesNormalizedCursor()
        {
            SetAllCurrentVersions(1);

            int selectedCount = WaterChunkRebuildScheduler.SelectDirtyChunks(
                _currentVersions,
                _lastSeenVersions,
                startIndex: 5,
                maxRebuilds: 0,
                _selectedIndices,
                out int nextScanIndex);

            Assert.That(selectedCount, Is.Zero);
            Assert.That(nextScanIndex, Is.EqualTo(1));
        }

        private void SetAllCurrentVersions(uint version)
        {
            for (int i = 0; i < _currentVersions.Length; i++)
                _currentVersions[i] = version;
        }
    }
}
