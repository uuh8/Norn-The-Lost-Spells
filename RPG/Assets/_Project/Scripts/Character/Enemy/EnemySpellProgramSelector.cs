namespace Game.Character
{
    /// <summary>
    /// 为远程敌人选择下一套法术程序。该规则保持为纯 C#，让随机策略可以脱离 Unity Runtime 单独测试。
    /// </summary>
    public static class EnemySpellProgramSelector
    {
        /// <summary>
        /// 从法术程序索引中等概率取一个候选；存在有效上次索引时，将其排除以避免连续重复。
        /// </summary>
        public static int SelectNextIndex(int programCount, int previousIndex, float sample01)
        {
            if (programCount <= 0)
            {
                return -1;
            }

            if (programCount == 1)
            {
                return 0;
            }

            float sample = SanitizeSample(sample01);
            bool hasValidPrevious = previousIndex >= 0 && previousIndex < programCount;
            int candidateCount = hasValidPrevious ? programCount - 1 : programCount;

            int compactIndex = (int)(sample * candidateCount);
            if (compactIndex >= candidateCount)
            {
                compactIndex = candidateCount - 1;
            }

            // 候选集合在逻辑上跳过 previousIndex，无需创建临时数组，也不会产生 GC Alloc。
            return hasValidPrevious && compactIndex >= previousIndex
                ? compactIndex + 1
                : compactIndex;
        }

        private static float SanitizeSample(float sample)
        {
            if (float.IsNaN(sample) || sample <= 0f)
            {
                return 0f;
            }

            return sample >= 1f ? 1f : sample;
        }
    }
}
