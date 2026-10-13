using System;
using UnityEngine;

namespace Game.UI
{
    /// <summary>表现层的购买边界；后台仍复核供货/资金/容量，不能以 Slider 值直接写资产。</summary>
    public static class ShopPurchaseQuantity
    {
        public static ulong Maximum(ulong stock, long price, long money, bool material, ulong free)
        {
            if (price <= 0 || money < 0) return 0;
            ulong limit = material ? Math.Min(free, (ulong)int.MaxValue) : 128UL;
            return Math.Min(stock, Math.Min((ulong)(money / price), limit));
        }
        public static bool TryCost(long price, ulong quantity, out long total)
        {
            total = 0; if (price <= 0 || quantity == 0 || quantity > long.MaxValue) return false;
            try { total = checked(price * (long)quantity); return true; }
            catch (OverflowException) { return false; }
        }
        // 大容量不能用 float 表示逐个整数。Slider 只存0..1进度；Quantity用ulong，±按钮保留单位精度。
        public static ulong FromProgress(float progress, ulong maximum)
        {
            if (maximum == 0) return 0;
            if (float.IsNaN(progress)) progress = 0;
            return 1UL + (ulong)Math.Round((maximum - 1d) * Mathf.Clamp01(progress), MidpointRounding.AwayFromZero);
        }
        public static float ToProgress(ulong quantity, ulong maximum)
        {
            if (maximum <= 1) return 0;
            quantity = Math.Max(1UL, Math.Min(quantity, maximum));
            return (float)((quantity - 1d) / (maximum - 1d));
        }
    }
}
