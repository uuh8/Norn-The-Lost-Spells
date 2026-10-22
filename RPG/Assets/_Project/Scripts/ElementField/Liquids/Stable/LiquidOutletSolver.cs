using UnityEngine;

namespace Game.ElementField
{
    /// <summary>有限出流近似；小于 1 GMU 的余量留在出口，不会因取整永久停止。</summary>
    public static class LiquidOutletSolver
    {
        public static uint Compute(float surfaceHeightCells, float outletHeightCells,
            float downstreamFill, float openingAreaCells, float conductance,
            float deltaTime, ref float fractionalRemainder)
        {
            float head = Mathf.Max(0f, surfaceHeightCells - outletHeightCells
                - Mathf.Clamp01(downstreamFill));
            if (head <= 0f || openingAreaCells <= 0f || conductance <= 0f) return 0;
            float requested = conductance * openingAreaCells * Mathf.Sqrt(head)
                * deltaTime + fractionalRemainder;
            if (requested >= ushort.MaxValue)
            {
                // 带宽上限丢弃超额“理想流量”，不把它积成下一帧的虚假欠账。
                fractionalRemainder = 0f;
                return ushort.MaxValue;
            }
            uint whole = (uint)Mathf.FloorToInt(requested);
            fractionalRemainder = requested - whole;
            return whole;
        }
    }
}
