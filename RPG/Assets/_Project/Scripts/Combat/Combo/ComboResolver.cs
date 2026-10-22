namespace Game.Combat
{
    /// <summary>
    /// 纯函数连段判定。不依赖 MonoBehaviour/Animator，可在 EditMode 单测（仿 DamagePipeline）。
    /// 给定当前段进度与是否有缓冲输入，决定本帧维持/推进/结束。
    /// </summary>
    public static class ComboResolver
    {
        /// <param name="comboIndex">当前段索引（0 起）。</param>
        /// <param name="segmentCount">连段总段数。</param>
        /// <param name="normalizedTime">当前段动画归一化进度（0~1）。</param>
        /// <param name="hasBufferedInput">是否有未消耗的攻击缓冲输入。</param>
        /// <param name="inputStart">本段连段输入窗口起点（归一化）。</param>
        /// <param name="inputEnd">本段连段输入窗口终点（归一化）。</param>
        /// <param name="endThreshold">动画进度达到此值且未推进则结束连段。</param>
        public static ComboDecision Resolve(
            int comboIndex, int segmentCount, float normalizedTime, bool hasBufferedInput,
            float inputStart, float inputEnd, float endThreshold)
        {
            bool hasNext = comboIndex + 1 < segmentCount;

            // 1. 输入窗口内 + 有缓冲输入 + 有下一段 → 推进（优先于结束）
            if (hasBufferedInput && hasNext &&
                normalizedTime >= inputStart && normalizedTime <= inputEnd)
            {
                return ComboDecision.Advance;
            }

            // 2. 动画接近播完且未推进 → 结束连段
            if (normalizedTime >= endThreshold)
            {
                return ComboDecision.End;
            }

            // 3. 其它 → 维持当前段
            return ComboDecision.Continue;
        }
    }
}
