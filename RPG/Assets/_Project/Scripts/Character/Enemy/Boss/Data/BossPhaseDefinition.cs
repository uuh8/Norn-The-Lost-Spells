using System;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 一个 Boss Phase 的 Authoring 数据。它是 BossDefinition 内嵌值，不单独创建 Asset。
    /// Program 的个体权重保存在 ProgramDefinition，这里只拥有阶段全局节奏。
    /// </summary>
    [Serializable]
    public sealed class BossPhaseDefinition
    {
        public BossPhase Phase = BossPhase.Phase1;

        [Range(0f, 1f)]
        [Tooltip("HP Ratio 小于等于该值时允许进入本阶段。Phase 1 通常填 1。")]
        public float EnterAtOrBelowHealthRatio = 1f;

        [Min(0.05f)]
        [Tooltip("完成一个动作后再次进行 Utility 决策的最短间隔。")]
        public float CastInterval = 1.2f;

        [Min(0f)]
        public float TeleportCooldown = 8f;

        [Min(0f)]
        public float TeleportWeight = 0f;
    }
}
