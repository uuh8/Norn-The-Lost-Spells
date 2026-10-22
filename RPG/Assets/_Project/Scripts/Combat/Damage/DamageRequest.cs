using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 一次命中的输入值快照：记录“谁造成、造成多少、什么类型、在哪里命中、朝哪个方向命中”。
    /// readonly struct 是值类型且字段不可改；创建后语义固定，结算期间也不必回查可能已经销毁的攻击者对象。
    /// </summary>
    public readonly struct DamageRequest
    {
        // Id/Team 用于归属、阵营过滤和事件消费者识别，不保存对攻击者 GameObject 的强引用。
        public readonly int AttackerId;
        public readonly byte AttackerTeam;
        public readonly float BaseAmount;   // 基础伤害
        public readonly DamageType Type;
        // 命中点和方向供受击表现、击退或后续系统使用；伤害计算本身不依赖场景对象。
        public readonly Vector3 HitPoint;
        public readonly Vector3 HitDirection;
        // 是否触发"受击反应"（受击动画 + 敌人硬直）。直击=true；DoT/环境跳伤(燃烧/火场)=false：
        // 仍正常扣血与发事件（可闪红/飘字），但不打断动作、不站桩。解决"持续伤害时角色一直播受击、无法移动"。
        public readonly bool TriggerHitReaction;

        /// <summary>构造一次完整命中快照；可选参数默认把直接命中视为需要触发受击反应。</summary>
        public DamageRequest(int attackerId, byte attackerTeam, float baseAmount,
                             DamageType type, Vector3 hitPoint, Vector3 hitDirection,
                             bool triggerHitReaction = true)
        {
            AttackerId         = attackerId;
            AttackerTeam       = attackerTeam;
            BaseAmount         = baseAmount;
            Type               = type;
            HitPoint           = hitPoint;
            HitDirection       = hitDirection;
            TriggerHitReaction = triggerHitReaction;
        }
    }
}
