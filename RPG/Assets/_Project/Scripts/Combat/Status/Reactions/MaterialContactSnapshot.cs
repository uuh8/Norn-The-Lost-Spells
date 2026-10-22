using System;
using Game.Materials;

namespace Game.Combat
{
    /// <summary>
    /// 两份 Material 在同一反应步开始时的只读接触快照。Amount 使用统一 Gameplay Material Unit
    /// （GMU，范围 0..255），因此反应公式不需要知道来源是 Cell、PBF Particle 还是未来 Backend。
    /// </summary>
    public readonly struct MaterialContactSnapshot
    {
        public MaterialContactSnapshot(
            MaterialId firstMaterial,
            int firstAmountGmu,
            MaterialId secondMaterial,
            int secondAmountGmu)
        {
            if ((uint)firstAmountGmu > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(firstAmountGmu));
            if ((uint)secondAmountGmu > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(secondAmountGmu));

            FirstMaterial = firstMaterial;
            FirstAmountGmu = firstAmountGmu;
            SecondMaterial = secondMaterial;
            SecondAmountGmu = secondAmountGmu;
        }

        public MaterialId FirstMaterial { get; }
        public int FirstAmountGmu { get; }
        public MaterialId SecondMaterial { get; }
        public int SecondAmountGmu { get; }
    }
}
