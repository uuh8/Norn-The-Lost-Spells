using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Game.Combat;
using UnityEngine;

namespace Game.Skills
{
    public enum SpellProgramError : byte
    {
        None, InvalidInput, InvalidDefinition, UnsupportedInstruction, DuplicateInstance,
        InvalidState, EmptyProgram, TooComplex, NumericOverflow
    }

    public readonly struct SpellProgramDiagnostic
    {
        public readonly SpellProgramError Error;
        public readonly int SourceSlot;
        public readonly string Message;
        public SpellProgramDiagnostic(SpellProgramError error, int sourceSlot, string message)
        { Error = error; SourceSlot = sourceSlot; Message = message; }
    }

    /// <summary>编辑/编译时捕获固定属性；真实实例ID由Run提供，样例可使用自动槽位ID。</summary>
    public readonly struct SpellProgramCompileInputs
    {
        public readonly int BaseDraws;
        public readonly long ProgramRevision;
        public readonly float BaseCastDelay, BaseReloadTime, BaseSpreadDegrees, ManaRegenPerSecond;
        public readonly bool Shuffle;
        public readonly IReadOnlyList<string> InstanceIds;
        public readonly IReadOnlyList<SpellDefinition> AlwaysCast;

        public SpellProgramCompileInputs(int baseDraws, IReadOnlyList<string> instanceIds = null,
            long programRevision = 1, float baseCastDelay = .15f, float baseReloadTime = .7f,
            float baseSpreadDegrees = 0f, bool shuffle = false, float manaRegenPerSecond = 0f,IReadOnlyList<SpellDefinition> alwaysCast=null)
        {
            BaseDraws = baseDraws; ProgramRevision = programRevision;
            BaseCastDelay = baseCastDelay; BaseReloadTime = baseReloadTime;
            BaseSpreadDegrees = baseSpreadDegrees; Shuffle = shuffle; ManaRegenPerSecond = manaRegenPerSecond;
            if(alwaysCast==null)AlwaysCast=null;else{var fixedCards=new SpellDefinition[alwaysCast.Count];for(int i=0;i<fixedCards.Length;i++)fixedCards[i]=alwaysCast[i];AlwaysCast=Array.AsReadOnly(fixedCards);}
            if (instanceIds == null) { InstanceIds = null; return; }
            var copy = new string[instanceIds.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = instanceIds[i];
            InstanceIds = Array.AsReadOnly(copy);
        }
    }

    /// <summary>只冻结规则值，Prefab/Sprite/AudioClip保留资源引用，不克隆Unity对象。</summary>
    public readonly struct SpellEmissionData
    {
        public readonly GameObject ProjectilePrefab, LandingSitePrefab;
        public readonly AudioClip CastSfx;
        public readonly SpellSpawnMode SpawnMode;
        public readonly DamageType DamageType;
        public readonly PayloadTriggerMode PayloadTrigger;
        public readonly float BaseDamage, BaseSpeed, ExplosionDamage, FireFieldDamagePerTick;
        public readonly float FireFieldTickInterval, FireFieldDuration;
        public readonly float SkyfallHeight, SkyfallBackOffset, LandingSiteDuration, PayloadDelaySeconds;
        public readonly int ShieldReflectCount;
        public readonly float LifetimeSeconds;

        public SpellEmissionData(SpellDefinition source)
        {
            ProjectilePrefab = source.ProjectilePrefab; LandingSitePrefab = source.LandingSitePrefab;
            CastSfx = source.CastSfx; SpawnMode = source.SpawnMode; DamageType = source.DamageType;
            PayloadTrigger = source.PayloadTrigger; BaseDamage = source.BaseDamage; BaseSpeed = source.BaseSpeed;
            ExplosionDamage = source.ExplosionDamage; FireFieldDamagePerTick = source.FireFieldDamagePerTick;
            FireFieldTickInterval = source.FireFieldTickInterval; FireFieldDuration = source.FireFieldDuration;
            SkyfallHeight = source.SkyfallHeight; SkyfallBackOffset = source.SkyfallBackOffset;
            LandingSiteDuration = source.LandingSiteDuration; PayloadDelaySeconds = source.PayloadDelaySeconds;
            ShieldReflectCount = source.ShieldReflectCount;
            LifetimeSeconds=source.LifetimeSeconds;
        }
    }

    public readonly struct SpellInstruction
    {
        public readonly string InstanceId, DisplayName;
        public readonly Sprite Icon;
        public readonly int SourceSlot, DrawCount, TriggerDrawCount;
        public readonly SpellKind Kind;
        public readonly SpellCopyMode CopyMode;
        public bool IsIntrinsic=>SourceSlot<0;
        public readonly float ManaCost, CastDelayDelta, ReloadDelta;
        public readonly CastModifierState Modifier;
        public readonly SpellEmissionData Emission;

        internal SpellInstruction(SpellDefinition source, int sourceSlot, string instanceId)
        {
            InstanceId = instanceId; SourceSlot = sourceSlot; DisplayName = source.DisplayName;
            Icon = source.Icon; Kind = source.Kind; ManaCost = source.ManaCost;
            CopyMode = source.CopyMode;
            CastDelayDelta = source.CastDelayDelta; ReloadDelta = source.ReloadDelta;
            DrawCount = source.Kind == SpellKind.Multicast ? 1 + source.ExtraDraws : 1;
            TriggerDrawCount = source.TriggerDrawCount; Emission = new SpellEmissionData(source);
            // 这里保存的是修正操作的值；没有Scene/SpellDefinition的可变规则引用。
            var mode = ProjectileMotionMode.None;
            if (source.ModHomingRadius > 0 && source.ModHomingDuration > 0 && source.ModHomingTurnRateDegrees > 0)
                mode = ProjectileMotionMode.Homing;
            if (source.ModOrbitRadius > 0 && !Mathf.Approximately(source.ModOrbitAngularSpeedDegrees, 0))
                mode = ProjectileMotionMode.Orbit;
            Modifier = new CastModifierState(source.ModDamageAddFlat, source.ModDamageMul, source.ModSpeedMul,
                source.ModSpreadAddDegrees, source.ModBounceAdd, source.ModUseGravity,
                source.ModHomingRadius, source.ModHomingDuration, source.ModHomingTurnRateDegrees,
                source.ModOrbitRadius, source.ModOrbitAngularSpeedDegrees, source.ModOrbitPhaseOffsetDegrees,
                source.ModOrbitPlaneTiltDegrees, mode,source.ModLifetimeAddSeconds,source.ModLifetimeMul);
        }
    }

    public sealed class SpellProgram
    {
        private readonly ReadOnlyCollection<SpellInstruction> _instructions;
        public IReadOnlyList<SpellInstruction> Instructions => _instructions;
        public int Count => _instructions.Count;
        public int SourceSlotCount { get; }
        public int BaseDraws { get; }
        public long Revision { get; }
        public float BaseCastDelay { get; }
        public float BaseReloadTime { get; }
        public float BaseSpreadDegrees { get; }
        public float ManaRegenPerSecond { get; }
        public bool Shuffle { get; }
        public int IntrinsicCount { get; }

        internal SpellProgram(SpellInstruction[] instructions, int slotCount, in SpellProgramCompileInputs inputs)
        {
            _instructions = Array.AsReadOnly(instructions); SourceSlotCount = slotCount;
            BaseDraws = inputs.BaseDraws; Revision = inputs.ProgramRevision;
            BaseCastDelay = inputs.BaseCastDelay; BaseReloadTime = inputs.BaseReloadTime;
            BaseSpreadDegrees = inputs.BaseSpreadDegrees; Shuffle = inputs.Shuffle;
            ManaRegenPerSecond = inputs.ManaRegenPerSecond;
            int count=0;for(int i=0;i<instructions.Length;i++)if(instructions[i].IsIntrinsic)count++;IntrinsicCount=count;
        }
    }
}
