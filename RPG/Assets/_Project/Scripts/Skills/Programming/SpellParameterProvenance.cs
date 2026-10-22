using System;
using System.Collections.Generic;

namespace Game.Skills
{
    [Flags]
    public enum SpellParameterChannels : ushort
    { None = 0, Damage = 1, Speed = 2, Spread = 4, Bounce = 8, Gravity = 16, Homing = 32, Orbit = 64, Timing = 128, Lifetime = 256 }

    public readonly struct SpellParameterContribution
    {
        public readonly int CardIndex, StepId;
        public readonly string InstanceId;
        public readonly SpellParameterChannels Channels;
        public readonly CastModifierState Operation;
        public readonly float CastDelayDelta, ReloadDelta;
        internal SpellParameterContribution(int index, int step, in SpellInstruction instruction)
        {
            CardIndex = index; StepId = step; InstanceId = instruction.InstanceId;
            Operation = instruction.Modifier; CastDelayDelta = instruction.CastDelayDelta; ReloadDelta = instruction.ReloadDelta;
            var channels = SpellParameterChannels.None;
            if (instruction.Kind == SpellKind.Modify)
            {
                if (Operation.DamageAddFlat != 0 || Operation.DamageMul != 1) channels |= SpellParameterChannels.Damage;
                if (Operation.SpeedMul != 1) channels |= SpellParameterChannels.Speed;
                if (Operation.SpreadDegrees != 0) channels |= SpellParameterChannels.Spread;
                if (Operation.BounceCount != 0) channels |= SpellParameterChannels.Bounce;
                if (Operation.UseGravity) channels |= SpellParameterChannels.Gravity;
                if (Operation.LifetimeAddSeconds!=0 || Operation.LifetimeMul!=1) channels |= SpellParameterChannels.Lifetime;
                if (Operation.HomingRadius != 0 || Operation.HomingDuration != 0 || Operation.HomingTurnRateDegrees != 0) channels |= SpellParameterChannels.Homing;
                if (Operation.OrbitRadius != 0 || Operation.OrbitAngularSpeedDegrees != 0 || Operation.OrbitPhaseOffsetDegrees != 0 || Operation.OrbitPlaneTiltDegrees != 0) channels |= SpellParameterChannels.Orbit;
            }
            if (CastDelayDelta != 0 || ReloadDelta != 0) channels |= SpellParameterChannels.Timing;
            Channels = channels;
        }
    }

    /// <summary>同Scope共享不可变贡献列表；最终值来自Baker，View只格式化，不再次做伤害运算。</summary>
    public sealed class SpellParameterProvenance
    {
        public int ScopeId { get; }
        public float BaseDamage { get; }
        public float BaseSpeed { get; }
        public CastModifierState FinalModifiers { get; }
        public float FinalDamage { get; }
        public float FinalSpeed { get; }
        public IReadOnlyList<SpellParameterContribution> Contributions { get; }
        internal SpellParameterProvenance(int scope, in SpellEmissionData emission, CastModifierState mods,
            in EmitCommand command, IReadOnlyList<SpellParameterContribution> contributions)
        {
            ScopeId = scope; BaseDamage = emission.BaseDamage; BaseSpeed = emission.BaseSpeed;
            FinalModifiers = mods; FinalDamage = command.Damage; FinalSpeed = command.Speed; Contributions = contributions;
        }
    }
}
