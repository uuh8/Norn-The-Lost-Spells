using System;

namespace Game.Core
{
    /// <summary>材料资产真值：稳定 byte 配置键与整数 GMU；不依赖模拟后端或 Renderer。</summary>
    [Serializable]
    public struct MaterialAmount
    {
        public byte MaterialKey;
        public ulong Quantity;
        public MaterialAmount(byte key, ulong quantity) { MaterialKey = key; Quantity = quantity; }
        public static bool IsKnown(byte key) => key >= 1 && key <= 8;
    }

    public static class MaterialAmountOps
    {
        // 最大余数法：先取每份整数下限，再把余量给小数最多者，保证总量且不凭材料名称过滤混样。
        // 乘除使用整数余数：Mono的decimal乘法在部分数量组合下会分配临时数组，值类型不保证内部零GC。
        // 余数Buffer也用ulong，避免decimal比较首次触发DecCalc静态初始化进入热路径。
        public static ulong SplitBudget(MaterialAmount[] source, int count, ulong budget,
            MaterialAmount[] destination, ulong[] remainders, int tieOffset = 0)
        {
            if (source == null || destination == null || remainders == null || count < 0
                || count > source.Length || count > destination.Length || count > remainders.Length)
                throw new ArgumentException("比例分配需要完整 Buffer。");
            ulong total = 0;
            for (int i = 0; i < count; i++) total = checked(total + source[i].Quantity);
            budget = Math.Min(budget, total); ulong used = 0;
            for (int i = 0; i < count; i++)
            {
                ulong remainder = 0;
                ulong quantity = total == 0 ? 0 : budget == total ? source[i].Quantity
                    : MultiplyDivide(source[i].Quantity, budget, total, out remainder);
                destination[i] = new MaterialAmount(source[i].MaterialKey, quantity);
                remainders[i] = remainder; used += quantity;
            }
            while (used < budget)
            {
                int best = -1;
                for (int j = 0; j < count; j++)
                {
                    int i = (j + (tieOffset % count + count) % count) % count;
                    if (remainders[i]!=ulong.MaxValue && destination[i].Quantity < source[i].Quantity
                        && (best < 0 || remainders[i] > remainders[best])) best = i;
                }
                if (best < 0) throw new InvalidOperationException("比例分配余量不变量被破坏。");
                // 真实余数严格小于total，MaxValue可作为已补一份的哨兵，不会与合法余数相撞。
                destination[best].Quantity++; remainders[best] = ulong.MaxValue; used++;
            }
            return used;
        }

        // 普通GMU走单次ulong乘除。超大资产用逐位长除法，保存商和模数，避免128位乘积溢出。
        // a、b均不大于divisor；因此商不大于b。先比较差值再翻倍/相加，所有中间值也不溢出。
        private static ulong MultiplyDivide(ulong a, ulong b, ulong divisor, out ulong remainder)
        {
            if (a == 0 || b == 0) { remainder = 0; return 0; }
            if (a <= ulong.MaxValue / b)
            {
                ulong product = a * b; remainder = product % divisor; return product / divisor;
            }
            ulong quotient = 0; remainder = 0;
            for (int bit = 63; bit >= 0; bit--)
            {
                ulong carry = 0;
                if (remainder >= divisor - remainder) { remainder -= divisor - remainder; carry = 1; }
                else remainder += remainder;
                if (((b >> bit) & 1) != 0)
                {
                    if (remainder >= divisor - a) { remainder -= divisor - a; carry++; }
                    else remainder += a;
                }
                quotient = (quotient << 1) + carry;
            }
            return quotient;
        }
    }
}
