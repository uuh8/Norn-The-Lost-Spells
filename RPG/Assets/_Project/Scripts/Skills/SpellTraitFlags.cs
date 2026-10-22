using System;

namespace Game.Skills
{
    /// <summary>
    /// 从 SpellDefinition 的权威行为字段推导出的展示事实。
    /// UI 不手填第二套 Traits 文案，避免 Inspector 描述与 CastEvaluator 真实语义漂移。
    /// [Flags] 表示多个枚举值可以存进同一个整数的不同 bit；例如 Homing | Bounce 同时拥有两种标签。
    /// </summary>
    // FlagsAttribute 主要改善 ToString/Inspector/调试语义；真正的组合仍由 |、& 等位运算完成。
    [Flags]
    public enum SpellTraitFlags : ushort
    {
        None = 0,                    // 没有任何可展示 Trait；0 也便于作为累计起点。
        ImpactPayload = 1 << 0,      // 第 0 bit：命中后释放 Payload。
        TimedPayload = 1 << 1,       // 第 1 bit：延迟到点后释放 Payload。
        Multicast = 1 << 2,          // 增加 Draw Budget。
        StaticProjectile = 1 << 3,   // 产出 StaticProjectile 类指令。
        Shield = 1 << 4,             // Runtime 采用 StaticAtPoint 生成保护盾。
        DamageModifier = 1 << 5,     // 修改伤害加法项或乘法项。
        SpeedModifier = 1 << 6,      // 修改后续投射物速度倍率。
        Spread = 1 << 7,             // 增加扇形散射角。
        Bounce = 1 << 8,             // 增加环境弹跳次数。
        Gravity = 1 << 9,            // 让后续 Rigidbody 投射物启用重力。
        Homing = 1 << 10,            // 让后续投射物使用 Homing 主运动模式。
        Orbit = 1 << 11,             // 让后续投射物使用 Orbit 主运动模式。
    }

    /// <summary>
    /// 把一份 SpellDefinition 的真实配置投影成 UI 可读取的组合标签。
    /// 这是无状态的派生查询：不修改资产，也不参与 CastEvaluator 的 Gameplay 判定。
    /// </summary>
    public static class SpellTraitResolver
    {
        /// <summary>读取权威行为字段并累计 bit flags；null 输入返回 None，方便 UI 安全降级。</summary>
        public static SpellTraitFlags Resolve(SpellDefinition spell)
        {
            if (spell == null)
                return SpellTraitFlags.None;

            SpellTraitFlags flags = SpellTraitFlags.None;
            // |= 是“按位或并赋值”：只把对应 bit 置 1，不会清除前面已经累计的其他 Trait。
            if (spell.PayloadTrigger == PayloadTriggerMode.OnImpact)
                flags |= SpellTraitFlags.ImpactPayload;
            else if (spell.PayloadTrigger == PayloadTriggerMode.AfterDelay)
                flags |= SpellTraitFlags.TimedPayload;

            if (spell.Kind == SpellKind.Multicast)
                flags |= SpellTraitFlags.Multicast;
            if (spell.Kind == SpellKind.StaticProjectile)
                flags |= SpellTraitFlags.StaticProjectile;
            if (spell.SpawnMode == SpellSpawnMode.StaticAtPoint)
                flags |= SpellTraitFlags.Shield;
            if (spell.ModDamageAddFlat != 0f || spell.ModDamageMul != 1f)
                flags |= SpellTraitFlags.DamageModifier;
            if (spell.ModSpeedMul != 1f)
                flags |= SpellTraitFlags.SpeedModifier;
            if (spell.ModSpreadAddDegrees != 0f)
                flags |= SpellTraitFlags.Spread;
            if (spell.ModBounceAdd > 0)
                flags |= SpellTraitFlags.Bounce;
            if (spell.ModUseGravity)
                flags |= SpellTraitFlags.Gravity;
            if (spell.ModHomingRadius > 0f)
                flags |= SpellTraitFlags.Homing;
            if (spell.ModOrbitRadius > 0f)
                flags |= SpellTraitFlags.Orbit;
            return flags;
        }
    }
}
