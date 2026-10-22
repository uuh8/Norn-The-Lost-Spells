using System.Collections.Generic;
using UnityEngine;

namespace Game.Skills
{
    /// <summary>Legacy逐Emit和V2按Scope结算共用的最终数值公式，不创建Scene对象。</summary>
    public static class SpellCommandBaker
    {
        public static EmitCommand Bake(in SpellEmissionData spell, CastModifierState mods,
            IReadOnlyList<SpellDefinition> legacyPayload = null, bool graphPayload = false)
        {
            float multiplier = Mathf.Max(0f, mods.DamageMul);
            float damage = Mathf.Max(0f, (spell.BaseDamage + mods.DamageAddFlat) * multiplier);
            // Flat只加直击；倍率同时影响爆炸/DoT，保持Norn既有复合伤害约定。
            float explosion = Mathf.Max(0f, spell.ExplosionDamage * multiplier);
            float field = Mathf.Max(0f, spell.FireFieldDamagePerTick * multiplier);
            bool hasPayload = graphPayload || legacyPayload != null && legacyPayload.Count > 0;
            var trigger = hasPayload ? spell.PayloadTrigger : PayloadTriggerMode.None;
            return new EmitCommand(spell.ProjectilePrefab, spell.SpawnMode, spell.LandingSitePrefab,
                spell.SkyfallHeight, spell.SkyfallBackOffset, spell.LandingSiteDuration, spell.ShieldReflectCount,
                damage, explosion, field, Mathf.Max(.05f, spell.FireFieldTickInterval), Mathf.Max(0f, spell.FireFieldDuration),
                spell.BaseSpeed * mods.SpeedMul, spell.DamageType, mods.SpreadDegrees, mods.BounceCount, mods.UseGravity,
                mods.HomingRadius, mods.HomingDuration, mods.HomingTurnRateDegrees,
                mods.OrbitRadius, mods.OrbitAngularSpeedDegrees, mods.OrbitPhaseOffsetDegrees, mods.OrbitPlaneTiltDegrees,
                mods.MotionMode, spell.CastSfx, legacyPayload, graphPayload ? CastModifierState.Default : mods,
                trigger, trigger == PayloadTriggerMode.AfterDelay ? spell.PayloadDelaySeconds : 0f,
                // Prefab默认寿命只由Combat知道：此处保留倍率，避免先按未知基数结算或Runtime重复乘。
                spell.LifetimeSeconds<0?-1:(float)System.Math.Clamp(((double)spell.LifetimeSeconds+mods.LifetimeAddSeconds)*mods.LifetimeMul,.01d,600d),
                spell.LifetimeSeconds<0?mods.LifetimeAddSeconds:0,spell.LifetimeSeconds<0?mods.LifetimeMul:1);
        }
    }
}
