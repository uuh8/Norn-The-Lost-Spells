using System;

namespace Game.Run
{
    /// <summary>纯概率/整数规则；样本显式传入，测试不依赖Unity全局Random的历史状态。</summary>
    public static class DungeonLootRoller
    {
        public static bool ValidChance(double chance)=>!double.IsNaN(chance)&&chance>=0&&chance<=1;
        private static void ValidateSample(double sample)
        {if(double.IsNaN(sample)||sample<0||sample>=1)throw new ArgumentOutOfRangeException(nameof(sample));}
        public static bool ShouldDrop(double chance,double sample)
        {
            if(!ValidChance(chance))throw new ArgumentOutOfRangeException(nameof(chance));
            ValidateSample(sample);return sample<chance;
        }
        public static int Quantity(int min,int max,double sample)
        {
            if(min<0||max<min)throw new ArgumentOutOfRangeException(nameof(min));
            ValidateSample(sample);
            // long计算闭区间长度，避免max=int.MaxValue时max+1先溢出。
            return (int)(min+(long)Math.Floor(sample*((long)max-min+1)));
        }
    }
}
