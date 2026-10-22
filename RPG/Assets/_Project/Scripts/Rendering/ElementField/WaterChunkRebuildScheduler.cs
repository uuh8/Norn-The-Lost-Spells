using System;

namespace Game.Rendering
{
    /// <summary>
    /// 从 Chunk Version Snapshot 中挑选本帧允许重建的 Dirty Chunk。
    ///
    /// 这个类型不读取 Unity Scene，也不创建集合；Renderer 在初始化时分配数组，之后每帧复用。
    /// 把调度从 MonoBehaviour.Update 中抽离后，“预算限制”和“跨帧公平性”可以被纯 NUnit 测试验证。
    /// </summary>
    public static class WaterChunkRebuildScheduler
    {
        /// <summary>
        /// 按环形顺序扫描 Chunk，把 currentVersion != lastSeenVersion 的索引写入 selectedIndices。
        /// 返回写入数量，并通过 nextScanIndex 告诉下一帧从哪里继续，避免高频变化的前几个 Chunk
        /// 长期占满预算而让后面的 Chunk 饥饿（Starvation）。
        /// </summary>
        public static int SelectDirtyChunks(
            uint[] currentVersions,
            uint[] lastSeenVersions,
            int startIndex,
            int maxRebuilds,
            int[] selectedIndices,
            out int nextScanIndex)
        {
            if (currentVersions == null)
                throw new ArgumentNullException(nameof(currentVersions));
            if (lastSeenVersions == null)
                throw new ArgumentNullException(nameof(lastSeenVersions));
            if (selectedIndices == null)
                throw new ArgumentNullException(nameof(selectedIndices));
            if (currentVersions.Length != lastSeenVersions.Length)
            {
                throw new ArgumentException(
                    "Current and last-seen Chunk Version arrays must have the same length.");
            }

            int chunkCount = currentVersions.Length;
            if (chunkCount == 0)
            {
                nextScanIndex = 0;
                return 0;
            }

            int cursor = NormalizeIndex(startIndex, chunkCount);
            nextScanIndex = cursor;
            int budget = Math.Min(Math.Max(0, maxRebuilds), selectedIndices.Length);
            if (budget == 0)
                return 0;

            int inspectedCount = 0;
            int selectedCount = 0;
            while (inspectedCount < chunkCount && selectedCount < budget)
            {
                int inspectedIndex = cursor;
                cursor++;
                if (cursor == chunkCount)
                    cursor = 0;
                inspectedCount++;

                if (currentVersions[inspectedIndex] == lastSeenVersions[inspectedIndex])
                    continue;

                selectedIndices[selectedCount] = inspectedIndex;
                selectedCount++;
            }

            nextScanIndex = cursor;
            return selectedCount;
        }

        private static int NormalizeIndex(int index, int count)
        {
            int normalized = index % count;
            return normalized < 0 ? normalized + count : normalized;
        }
    }
}
