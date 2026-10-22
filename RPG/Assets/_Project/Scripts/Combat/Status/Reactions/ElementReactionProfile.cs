using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Combat
{
    /// <summary>
    /// 角色元素反应的集中调参资产。StatusController 只在创建 Runtime 时读取快照，
    /// 避免连续 Process 在运行中受到 Inspector 数据变化影响。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Element Reaction Profile", fileName = "ElementReactionProfile")]
    public sealed class ElementReactionProfile : ScriptableObject
    {
        public ExtinguishTuning Extinguish;
        public WetCleanseTuning WetCleanse;
        public ToxicCombustionTuning ToxicCombustion;
        public IgniteGooTuning IgniteGoo;
        [FormerlySerializedAs("DiluteGoo")]
        public AbsorbWaterTuning AbsorbWater;

        public ElementReactionTuningSnapshot CreateSnapshot(
            float poisonWetCleanseMultiplier,
            float gooWetCleanseMultiplier)
        {
            // Wet Cleanse multiplier 属于被清洗状态的 StatusDefinition，其他反应参数属于本 Profile；
            // 在这里把两处数据汇合成 Runtime 只读快照，纯核心不需要认识 ScriptableObject。
            return new ElementReactionTuningSnapshot(
                Extinguish,
                WetCleanse,
                ToxicCombustion,
                IgniteGoo,
                AbsorbWater,
                poisonWetCleanseMultiplier,
                gooWetCleanseMultiplier);
        }
    }
}
