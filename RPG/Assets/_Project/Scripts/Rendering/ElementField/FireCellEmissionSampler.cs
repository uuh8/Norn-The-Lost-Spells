using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// Fire Cell 粒子表现的纯数学内核：负责确定性 Hash、概率门控、样本预算与空间 Jitter。
    ///
    /// 这里不读取 Scene、不创建 Particle，也不修改 ElementField。把这些规则从 MonoBehaviour
    /// 中拆出后，可以用 NUnit 先证明“哪些 Cell 有资格发射”，再由 Renderer 负责 Unity API。
    /// </summary>
    public static class FireCellEmissionSampler
    {
        private const float InverseTwentyFourBitRange = 1f / 16777216f;
        private const float HorizontalJitterCellFraction = 0.35f;

        public static float VisualHeat(byte amount,float minimumHeat) => amount==0?0f:
            Mathf.Lerp(Mathf.Clamp01(minimumHeat),1f,Mathf.Sqrt(NormalizeAmount(amount)));

        public static bool ShouldEmitVisual(Vector3Int cell,uint sequence,uint seed,
            byte amount,float probability,float minimumHeat) => amount>0
            && Hash01(cell,sequence,seed,0u)<Mathf.Clamp01(probability)*VisualHeat(amount,minimumHeat);

        public static float GroundSourceHeight(float cellSize,float minimumHeight) =>
            Mathf.Max(cellSize*.5f,minimumHeight);

        /// <summary>
        /// 把离散输入混合成 [0,1) 的可复现浮点数。
        /// 只保留低 24 bit，是因为 float 尾数可精确表达约 24 bit 随机精度；除数使用 2^24，
        /// 因此最大值是 (2^24-1)/2^24，永远严格小于 1。
        /// </summary>
        public static float Hash01(
            int cellIndex,
            uint visualSequence,
            uint layerSeed,
            uint channel)
        {
            return Hash01(
                unchecked((uint)cellIndex),
                visualSequence,
                layerSeed,
                channel);
        }

        /// <summary>
        /// 连续世界版本直接使用 Global Cell 坐标作为随机身份。
        /// 不能使用 Chunk 的局部 index 或 Dictionary 枚举序号，否则不同 Chunk 会产生同步图案，
        /// Chunk 加载/回收顺序变化时火焰还会突然换一套随机结果。
        /// </summary>
        public static float Hash01(
            Vector3Int globalCell,
            uint visualSequence,
            uint layerSeed,
            uint channel)
        {
            return Hash01(
                HashGlobalCell(globalCell),
                visualSequence,
                layerSeed,
                channel);
        }

        private static float Hash01(
            uint cellIdentity,
            uint visualSequence,
            uint layerSeed,
            uint channel)
        {
            uint value = cellIdentity;
            value ^= unchecked(visualSequence * 0x9E3779B9u);
            value ^= layerSeed;
            value ^= unchecked(channel * 0x85EBCA6Bu);
            value = Avalanche(value);
            return (value & 0x00FFFFFFu) * InverseTwentyFourBitRange;
        }

        /// <summary>
        /// 发射阈值 = LayerProbability × Amount/255。
        /// Amount 越小，某个视觉 Tick 产生粒子的机会越低；Gameplay Amount 本身不被删减。
        /// </summary>
        public static bool ShouldEmit(
            int cellIndex,
            uint visualSequence,
            uint layerSeed,
            byte amount,
            float probabilityAtFullAmount)
        {
            float threshold = Mathf.Clamp01(probabilityAtFullAmount) * NormalizeAmount(amount);
            return threshold > 0f
                && Hash01(cellIndex, visualSequence, layerSeed, channel: 0u) < threshold;
        }

        public static bool ShouldEmit(
            Vector3Int globalCell,
            uint visualSequence,
            uint layerSeed,
            byte amount,
            float probabilityAtFullAmount)
        {
            float threshold = Mathf.Clamp01(probabilityAtFullAmount) * NormalizeAmount(amount);
            return threshold > 0f
                && Hash01(globalCell, visualSequence, layerSeed, channel: 0u) < threshold;
        }

        /// <summary>
        /// 在 Active Fire Ordinal 上使用 Rational Stride 精确选择预算数量。
        ///
        /// bucket(n) = floor(n * budget / activeCount)。当相邻 bucket 发生变化时选中该 Ordinal，
        /// 完整扫描恰好发生 budget 次变化。Hash Phase 只旋转选择位置，不改变总数，避免固定 Cell
        /// 永久被视觉降级排除。这里使用 long 做乘法，防止大计数下 int 溢出。
        /// </summary>
        public static bool ShouldSampleActiveOrdinal(
            int activeOrdinal,
            int activeCount,
            int maxSampled,
            uint visualSequence)
        {
            if (activeCount <= 0
                || maxSampled <= 0
                || activeOrdinal < 0
                || activeOrdinal >= activeCount)
            {
                return false;
            }

            if (activeCount <= maxSampled)
                return true;

            int phase = (int)(Avalanche(visualSequence ^ 0xD1B54A35u) % (uint)activeCount);
            int rotatedOrdinal = (int)(((long)activeOrdinal + phase) % activeCount);
            long previousBucket = (long)rotatedOrdinal * maxSampled / activeCount;
            long nextBucket = (long)(rotatedOrdinal + 1) * maxSampled / activeCount;
            return nextBucket != previousBucket;
        }

        /// <summary>
        /// 返回 Cell Center 周围的确定性位移。XZ 最大偏移为 CellSize 的 35%，不会跨出本 Cell；
        /// Y 只向上偏移，避免火焰发射点被抖到地面下方。不同 Channel 让三个轴互不相关。
        /// </summary>
        public static Vector3 CalculateJitter(
            int cellIndex,
            uint visualSequence,
            uint layerSeed,
            float cellSize,
            float verticalJitter)
        {
            float safeCellSize = IsFinitePositive(cellSize) ? cellSize : 0f;
            float safeVerticalJitter = IsFiniteNonNegative(verticalJitter)
                ? verticalJitter
                : 0f;
            float horizontalRange = safeCellSize * HorizontalJitterCellFraction;
            float x = (Hash01(cellIndex, visualSequence, layerSeed, channel: 1u) * 2f - 1f)
                * horizontalRange;
            float y = Hash01(cellIndex, visualSequence, layerSeed, channel: 2u)
                * safeVerticalJitter;
            float z = (Hash01(cellIndex, visualSequence, layerSeed, channel: 3u) * 2f - 1f)
                * horizontalRange;
            return new Vector3(x, y, z);
        }

        public static Vector3 CalculateJitter(
            Vector3Int globalCell,
            uint visualSequence,
            uint layerSeed,
            float cellSize,
            float verticalJitter)
        {
            float safeCellSize = IsFinitePositive(cellSize) ? cellSize : 0f;
            float safeVerticalJitter = IsFiniteNonNegative(verticalJitter)
                ? verticalJitter
                : 0f;
            float horizontalRange = safeCellSize * HorizontalJitterCellFraction;
            float x = (Hash01(globalCell, visualSequence, layerSeed, channel: 1u) * 2f - 1f)
                * horizontalRange;
            float y = Hash01(globalCell, visualSequence, layerSeed, channel: 2u)
                * safeVerticalJitter;
            float z = (Hash01(globalCell, visualSequence, layerSeed, channel: 3u) * 2f - 1f)
                * horizontalRange;
            return new Vector3(x, y, z);
        }

        public static float NormalizeAmount(byte amount)
        {
            return amount / (float)byte.MaxValue;
        }

        public static float CalculateSizeMultiplier(
            byte amount,
            float minSizeMultiplier,
            float maxSizeMultiplier)
        {
            float safeMin = IsFiniteNonNegative(minSizeMultiplier)
                ? minSizeMultiplier
                : 0f;
            float safeMax = IsFiniteNonNegative(maxSizeMultiplier)
                ? Mathf.Max(safeMin, maxSizeMultiplier)
                : safeMin;
            return Mathf.Lerp(safeMin, safeMax, NormalizeAmount(amount));
        }

        private static uint Avalanche(uint value)
        {
            // 32-bit integer avalanche：让输入只改变一位时，输出的多数 bit 也发生变化。
            // unchecked 明确允许 uint 溢出；这里的 wrap-around 正是 Hash 混合的一部分。
            unchecked
            {
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                value *= 0x846CA68Bu;
                value ^= value >> 16;
                return value;
            }
        }

        private static uint HashGlobalCell(Vector3Int globalCell)
        {
            // FNV-1a 先把三个有符号坐标的 bit pattern 合并，再由 Avalanche 扩散。
            // unchecked 允许负坐标按二进制补码参与混合，也允许乘法溢出形成 32-bit wrap-around。
            unchecked
            {
                uint value = 2166136261u;
                value = (value ^ (uint)globalCell.x) * 16777619u;
                value = (value ^ (uint)globalCell.y) * 16777619u;
                value = (value ^ (uint)globalCell.z) * 16777619u;
                return Avalanche(value);
            }
        }

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFiniteNonNegative(float value)
        {
            return value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
