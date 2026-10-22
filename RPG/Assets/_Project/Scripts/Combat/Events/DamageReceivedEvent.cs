using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// 目标受到伤害后同帧派发。表现层（VFX/UI/受击反应）订阅消费。
    /// 攻击者侧能力（吸血/伤害数字）亦可据 AttackerId + Amount 同帧响应。
    /// </summary>
    public struct DamageReceivedEvent : IGameEvent
    {
        public int TargetId;
        public int AttackerId;
        public float Amount;
        public DamageType Type;
        public Vector3 HitPoint;
        public Vector3 HitDirection;
        public float RemainingHp;
        public bool TriggerHitReaction; // false = DoT/环境跳伤：表现层应跳过受击动画、敌人应跳过硬直（仍可闪红/飘字）
    }
}
