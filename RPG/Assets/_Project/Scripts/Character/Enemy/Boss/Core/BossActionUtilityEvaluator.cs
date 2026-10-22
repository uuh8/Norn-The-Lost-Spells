using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 在“本轮最高分 Spell”和独立 Mobility Action 之间做最终选择。
    /// TeleportWeight 是相对权重而非百分比；外部传入 roll01，使纯逻辑测试可复现。
    /// </summary>
    public static class BossActionUtilityEvaluator
    {
        public static bool ShouldTeleport(
            float teleportWeight,
            float teleportCooldownRemaining,
            float selectedSpellScore,
            float roll01)
        {
            float safeTeleportWeight = Mathf.Max(0f, teleportWeight);
            if (safeTeleportWeight <= 0f ||
                teleportCooldownRemaining > 0f)
            {
                return false;
            }

            if (float.IsNegativeInfinity(selectedSpellScore))
            {
                return true;
            }

            float safeSpellScore = Mathf.Max(0f, selectedSpellScore);
            float totalWeight = safeTeleportWeight + safeSpellScore;
            if (totalWeight <= 0f)
            {
                return false;
            }

            float teleportShare = safeTeleportWeight / totalWeight;
            return Mathf.Clamp01(roll01) < teleportShare;
        }
    }
}
