using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// HP 只允许推动 Boss 进入更高阶段；治疗或测试恢复血量不能让 Phase 倒退。
    /// </summary>
    public static class BossPhaseResolver
    {
        public static BossPhase Resolve(
            BossPhase currentPhase,
            float healthRatio,
            float phase2Threshold,
            float phase3Threshold)
        {
            float phase2 = Mathf.Clamp01(phase2Threshold);
            float phase3 = Mathf.Min(phase2, Mathf.Clamp01(phase3Threshold));
            float health = Mathf.Clamp01(healthRatio);

            BossPhase healthPhase = health <= phase3
                ? BossPhase.Phase3
                : health <= phase2
                    ? BossPhase.Phase2
                    : BossPhase.Phase1;

            return healthPhase > currentPhase ? healthPhase : currentPhase;
        }
    }
}
