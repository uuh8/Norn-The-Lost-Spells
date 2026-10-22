using System;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 卡通冠高的纯数学边界。CPU Tests 与 Compute Shader 镜像同一公式，避免调参语义只存在于 HLSL 中。
    /// 这里只计算表现层 Kernel 的支持度与安全缩放，不读取或修改任何 PBF 粒子状态。
    /// </summary>
    internal static class FluidCrownMath
    {
        internal static float CalculateSupport(
            int neighborCount,
            int edgeNeighborCount,
            int interiorNeighborCount)
        {
            if (neighborCount < 0)
                throw new ArgumentOutOfRangeException(nameof(neighborCount));
            if (edgeNeighborCount < 0)
                throw new ArgumentOutOfRangeException(nameof(edgeNeighborCount));
            if (interiorNeighborCount <= edgeNeighborCount)
                throw new ArgumentOutOfRangeException(nameof(interiorNeighborCount));

            return Mathf.Clamp01(
                (neighborCount - edgeNeighborCount)
                / (float)(interiorNeighborCount - edgeNeighborCount));
        }

        internal static float CalculateAppliedUpScale(
            float support,
            float crownHeightRatio,
            float crownFalloff,
            float baseUpMetric)
        {
            RequireFinite(support, nameof(support));
            RequireFinite(crownHeightRatio, nameof(crownHeightRatio));
            RequireFinite(crownFalloff, nameof(crownFalloff));
            RequireFinite(baseUpMetric, nameof(baseUpMetric));
            if (crownHeightRatio < 0f || crownHeightRatio > 1f)
                throw new ArgumentOutOfRangeException(nameof(crownHeightRatio));
            if (crownFalloff <= 0f)
                throw new ArgumentOutOfRangeException(nameof(crownFalloff));
            if (baseUpMetric <= 0f)
                throw new ArgumentOutOfRangeException(nameof(baseUpMetric));

            float desiredScale = 1f
                + crownHeightRatio * Mathf.Pow(Mathf.Clamp01(support), crownFalloff);
            // A*Up 的长度说明既有 Anisotropy 在世界 Y 方向已经把 Kernel 放大了多少。
            // 将最终支持域限制在 2h 内，Density 仍只需查询最多 5^3 个 Spatial Hash Cell。
            float maximumSafeScale = Mathf.Max(1f, 2f * baseUpMetric);
            return Mathf.Min(desiredScale, maximumSafeScale);
        }

        private static void RequireFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
