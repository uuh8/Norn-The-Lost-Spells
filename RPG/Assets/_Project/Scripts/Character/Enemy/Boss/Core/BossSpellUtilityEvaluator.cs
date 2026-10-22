using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 无 Scene 查询、无随机数、无每次决策分配的纯 Utility 选择器。
    /// 相同 Program 顺序与 Context 会稳定得到相同 Index，便于回放和 Debug。
    /// </summary>
    public static class BossSpellUtilityEvaluator
    {
        public static int SelectProgram(
            BossSpellProgramDefinition[] programs,
            BossProgramRuntimeState runtimeState,
            in BossDecisionContext context,
            out float selectedScore)
        {
            selectedScore = float.NegativeInfinity;
            if (programs == null || runtimeState == null)
            {
                return -1;
            }

            int selectedIndex = SelectPass(
                programs,
                runtimeState,
                in context,
                allowFallbackCooldownBypass: false,
                out selectedScore);
            if (selectedIndex >= 0)
            {
                return selectedIndex;
            }

            return SelectPass(
                programs,
                runtimeState,
                in context,
                allowFallbackCooldownBypass: true,
                out selectedScore);
        }

        private static int SelectPass(
            BossSpellProgramDefinition[] programs,
            BossProgramRuntimeState runtimeState,
            in BossDecisionContext context,
            bool allowFallbackCooldownBypass,
            out float selectedScore)
        {
            int selectedIndex = -1;
            selectedScore = float.NegativeInfinity;

            for (int i = 0; i < programs.Length; i++)
            {
                BossSpellProgramDefinition program = programs[i];
                if (program == null ||
                    !program.IsAvailableIn(context.Phase))
                {
                    continue;
                }

                if (allowFallbackCooldownBypass)
                {
                    if (!program.IsFallback)
                    {
                        continue;
                    }
                }
                else if (runtimeState.GetCooldownRemaining(i) > 0f)
                {
                    continue;
                }

                float score = ScoreProgram(
                    program,
                    runtimeState.CountRecentUses(i),
                    in context);
                // 只接受严格更高分：同分时保留较小 Index，保证确定性。
                if (score > selectedScore)
                {
                    selectedScore = score;
                    selectedIndex = i;
                }
            }

            return selectedIndex;
        }

        private static float ScoreProgram(
            BossSpellProgramDefinition program,
            int recentUseCount,
            in BossDecisionContext context)
        {
            float score =
                Mathf.Max(0f, program.BaseWeight) *
                program.GetPhaseMultiplier(context.Phase);
            score +=
                Mathf.Max(0f, program.DistanceScoreWeight) *
                EvaluateDistanceFit(
                    context.DistanceToPlayer,
                    program.MinPreferredDistance,
                    program.MaxPreferredDistance,
                    program.DistanceFalloff);

            float wetRatio =
                Mathf.Clamp01(context.PlayerWetIntensity / 100f);
            score += program.PlayerWetScoreModifier * wetRatio;

            if (context.PlayerMoveSpeed >=
                Mathf.Max(0f, program.MovingTargetSpeedThreshold))
            {
                score += program.MovingTargetScoreModifier;
            }

            score -=
                Mathf.Max(0f, program.RecentUsePenalty) *
                Mathf.Max(0, recentUseCount);
            return score;
        }

        private static float EvaluateDistanceFit(
            float distance,
            float minPreferredDistance,
            float maxPreferredDistance,
            float falloff)
        {
            float minDistance = Mathf.Max(0f, minPreferredDistance);
            float maxDistance = Mathf.Max(
                minDistance,
                maxPreferredDistance);
            float safeDistance = Mathf.Max(0f, distance);
            if (safeDistance >= minDistance &&
                safeDistance <= maxDistance)
            {
                return 1f;
            }

            float safeFalloff = Mathf.Max(0f, falloff);
            if (safeFalloff <= 0f)
            {
                return 0f;
            }

            float outsideDistance = safeDistance < minDistance
                ? minDistance - safeDistance
                : safeDistance - maxDistance;
            return Mathf.Clamp01(1f - outsideDistance / safeFalloff);
        }
    }
}
