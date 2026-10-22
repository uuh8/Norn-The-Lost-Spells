using System;
using Game.Materials;

namespace Game.ElementField
{
    public readonly struct LiquidMaterialAmountScale
    {
        public readonly MaterialId Material;
        public readonly uint AmountUnitsPerParticle;

        public LiquidMaterialAmountScale(MaterialId material, uint amountUnitsPerParticle)
        {
            Material = material;
            AmountUnitsPerParticle = amountUnitsPerParticle;
        }
    }

    /// <summary>
    /// 每种 Liquid 的 Gameplay Mass Unit Scale；只保存 Query 单位，不保存 PBF Physics 参数。
    /// </summary>
    public sealed class LiquidMaterialAmountScaleSnapshot
    {
        private readonly uint[] _scales = new uint[byte.MaxValue + 1];
        private readonly bool[] _defined = new bool[byte.MaxValue + 1];

        private LiquidMaterialAmountScaleSnapshot() { }

        public static LiquidMaterialAmountScaleSnapshot Create(LiquidMaterialAmountScale[] entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var snapshot = new LiquidMaterialAmountScaleSnapshot();
            for (int i = 0; i < entries.Length; i++)
            {
                LiquidMaterialAmountScale entry = entries[i];
                // Material 的 Liquid Behavior 已由生产入口 LiquidMaterialSettingsTable 通过 Catalog 验证；
                // Amount Scale 只是初始化后的数值容器，只应拒绝无身份或无质量的数据。
                // 这里不能再维护 Water/Poison/Sticky 白名单，否则每增加一种正式 Liquid 都会令整个 PBF 初始化失败。
                if (entry.Material == MaterialId.Empty || entry.AmountUnitsPerParticle == 0u)
                {
                    throw new InvalidOperationException($"Liquid Amount Scale[{i}] 无效：{entry.Material}/{entry.AmountUnitsPerParticle}。");
                }

                int index = (byte)entry.Material;
                if (snapshot._defined[index])
                    throw new InvalidOperationException($"Liquid Amount Scale 重复：{entry.Material}。");
                snapshot._defined[index] = true;
                snapshot._scales[index] = entry.AmountUnitsPerParticle;
            }
            return snapshot;
        }

        public bool TryGet(MaterialId material, out uint amountUnitsPerParticle)
        {
            int index = (byte)material;
            amountUnitsPerParticle = _scales[index];
            return _defined[index];
        }
    }
}
