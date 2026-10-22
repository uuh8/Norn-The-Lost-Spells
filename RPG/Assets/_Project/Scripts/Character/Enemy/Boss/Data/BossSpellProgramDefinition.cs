using Game.Skills;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 一套 Boss Wand Program 的 Utility 元数据。
    /// WandLoadout 仍只描述“程序指令”，AI 选择条件独立放在本资产，避免污染通用 Spell 数据。
    /// </summary>
    [CreateAssetMenu(
        menuName = "Game/Character/Boss Spell Program Definition",
        fileName = "BossSpellProgramDefinition")]
    public sealed class BossSpellProgramDefinition : ScriptableObject
    {
        [Header("Program")]
        public WandLoadout Wand;
        [Min(0f)] public float BaseWeight = 1f;
        public BossPhaseMask PhaseMask = BossPhaseMask.All;
        [Min(0f)] public float Phase1Multiplier = 1f;
        [Min(0f)] public float Phase2Multiplier = 1f;
        [Min(0f)] public float Phase3Multiplier = 1f;

        [Header("Distance Utility")]
        [Min(0f)] public float MinPreferredDistance;
        [Min(0f)] public float MaxPreferredDistance = 12f;
        [Min(0f)]
        [Tooltip("离开 Preferred Range 后，DistanceFit 在线性降为 0 前允许的距离。")]
        public float DistanceFalloff = 4f;
        [Min(0f)] public float DistanceScoreWeight = 1f;

        [Header("Context Utility")]
        [Tooltip("Player Wet 强度为 100 时加入的分数；可为负数。")]
        public float PlayerWetScoreModifier;
        [Min(0f)] public float MovingTargetSpeedThreshold = 4f;
        [Tooltip("Player 速度达到阈值时加入的分数；可为负数。")]
        public float MovingTargetScoreModifier;

        [Header("Runtime Rhythm")]
        [Min(0f)] public float Cooldown = 1f;
        [Min(0f)] public float RecentUsePenalty = 1f;
        [Tooltip("只有正常候选全部不可用时，Fallback 才可以忽略自身 Cooldown。")]
        public bool IsFallback;

        public bool IsAvailableIn(BossPhase phase)
        {
            BossPhaseMask phaseBit = phase switch
            {
                BossPhase.Phase2 => BossPhaseMask.Phase2,
                BossPhase.Phase3 => BossPhaseMask.Phase3,
                _ => BossPhaseMask.Phase1,
            };
            return (PhaseMask & phaseBit) != 0;
        }

        public float GetPhaseMultiplier(BossPhase phase)
        {
            return phase switch
            {
                BossPhase.Phase2 => Mathf.Max(0f, Phase2Multiplier),
                BossPhase.Phase3 => Mathf.Max(0f, Phase3Multiplier),
                _ => Mathf.Max(0f, Phase1Multiplier),
            };
        }
    }
}
