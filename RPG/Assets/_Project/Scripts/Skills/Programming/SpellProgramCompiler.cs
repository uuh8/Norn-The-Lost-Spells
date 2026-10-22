using System.Collections.Generic;
using System;

namespace Game.Skills
{
    /// <summary>V2独立编译边界；规则与资源可生成性检查分开，不切换现有玩家路径。</summary>
    public static class SpellProgramCompiler
    {
        /// <summary>耗尽后的新程序保留已冻结字段；不能重新读取可能被Editor改动过的SO。</summary>
        public static SpellProgram RetainInstances(SpellProgram source, IReadOnlyList<string> retainedIds, long revision)
        {
            if (source == null || retainedIds == null || revision < source.Revision) throw new ArgumentException("过滤程序的来源或版本无效。");
            var ids = new HashSet<string>(retainedIds, StringComparer.Ordinal);
            var retained = new List<SpellInstruction>(source.Count);
            for (int i = 0; i < source.Count; i++) if (source.Instructions[i].IsIntrinsic || ids.Contains(source.Instructions[i].InstanceId)) retained.Add(source.Instructions[i]);
            var inputs = new SpellProgramCompileInputs(source.BaseDraws, programRevision: revision,
                baseCastDelay: source.BaseCastDelay, baseReloadTime: source.BaseReloadTime,
                baseSpreadDegrees: source.BaseSpreadDegrees, shuffle: source.Shuffle, manaRegenPerSecond: source.ManaRegenPerSecond);
            return new SpellProgram(retained.ToArray(), source.SourceSlotCount, inputs);
        }

        public static bool TryCompile(IReadOnlyList<SpellDefinition> definitions,
            in SpellProgramCompileInputs inputs, out SpellProgram program, out SpellProgramDiagnostic diagnostic)
        {
            program = null;
            diagnostic = default;
            int count = definitions == null ? 0 : definitions.Count;
            int fixedCount=inputs.AlwaysCast?.Count??0;
            if(fixedCount>4 || count+fixedCount>SpellProgramLimits.MaxInstructions)
                return Reject(SpellProgramError.InvalidInput,-1,"固定法术或总指令数超过预算。",out diagnostic);
            if (count > SpellProgramLimits.MaxInstructions || inputs.BaseDraws < 1
                || inputs.BaseDraws > SpellProgramLimits.MaxSteps || inputs.ProgramRevision < 1
                || !float.IsFinite(inputs.BaseCastDelay) || inputs.BaseCastDelay < 0
                || !float.IsFinite(inputs.BaseReloadTime) || inputs.BaseReloadTime < 0
                || !float.IsFinite(inputs.BaseSpreadDegrees) || !float.IsFinite(inputs.ManaRegenPerSecond)
                || inputs.ManaRegenPerSecond < 0 || inputs.InstanceIds != null && inputs.InstanceIds.Count != count)
                return Reject(SpellProgramError.InvalidInput, -1, "法杖固定属性、长度或实例ID映射无效。", out diagnostic);

            var instructions = new List<SpellInstruction>(count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int slot = 0; slot < count; slot++)
            {
                var d = definitions[slot];
                // Unity资源使用== null，销毁的Unity对象也必须当作空槽。
                if (d == null) continue;
                if (d.Kind != SpellKind.Emit && d.Kind != SpellKind.StaticProjectile
                    && d.Kind != SpellKind.Modify && d.Kind != SpellKind.Multicast && d.Kind != SpellKind.Copy)
                    return Reject(SpellProgramError.UnsupportedInstruction, slot, "此版本尚未启用该指令。", out diagnostic);
                if (!ValidDefinition(d))
                    return Reject(SpellProgramError.InvalidDefinition, slot, "法术字段包含非有限数值或无效枚举/范围。", out diagnostic);
                string id = inputs.InstanceIds == null ? "slot:" + slot : inputs.InstanceIds[slot];
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    return Reject(SpellProgramError.DuplicateInstance, slot, "实例ID为空或重复。", out diagnostic);
                instructions.Add(new SpellInstruction(d, slot, id));
            }
            for(int i=0;i<fixedCount;i++)
            {
                var d=inputs.AlwaysCast[i];string id="@always:"+i;
                if(d==null||d.Kind>SpellKind.Copy||!ValidDefinition(d)||!ids.Add(id))
                    return Reject(SpellProgramError.InvalidDefinition,-1,"固定法术失效或字段非法。",out diagnostic);
                instructions.Add(new SpellInstruction(d,-1,id));
            }
            // 此处编译只在程序版本变化时执行；最终数组不再向外泄露可写引用。
            program = new SpellProgram(instructions.ToArray(), count, inputs);
            return true;
        }

        private static bool Reject(SpellProgramError error, int slot, string message, out SpellProgramDiagnostic diagnostic)
        { diagnostic = new SpellProgramDiagnostic(error, slot, message); return false; }

        private static bool ValidDefinition(SpellDefinition d)
        {
            return d.CopyMode >= SpellCopyMode.Alpha && d.CopyMode <= SpellCopyMode.Gamma
                && float.IsFinite(d.ManaCost) && float.IsFinite(d.CastDelayDelta) && float.IsFinite(d.ReloadDelta)
                && float.IsFinite(d.BaseDamage) && d.BaseDamage >= 0 && float.IsFinite(d.BaseSpeed)
                && float.IsFinite(d.ExplosionDamage) && d.ExplosionDamage >= 0
                && float.IsFinite(d.FireFieldDamagePerTick) && d.FireFieldDamagePerTick >= 0
                && float.IsFinite(d.FireFieldTickInterval) && d.FireFieldTickInterval > 0
                && float.IsFinite(d.FireFieldDuration) && d.FireFieldDuration >= 0
                && float.IsFinite(d.SkyfallHeight) && d.SkyfallHeight >= 0 && float.IsFinite(d.SkyfallBackOffset)
                && float.IsFinite(d.LandingSiteDuration) && d.LandingSiteDuration >= 0
                && float.IsFinite(d.PayloadDelaySeconds) && d.PayloadDelaySeconds >= 0
                && float.IsFinite(d.ModDamageAddFlat) && float.IsFinite(d.ModDamageMul)
                && float.IsFinite(d.ModSpeedMul) && float.IsFinite(d.ModSpreadAddDegrees)
                && float.IsFinite(d.ModHomingRadius) && d.ModHomingRadius >= 0
                && float.IsFinite(d.ModHomingDuration) && d.ModHomingDuration >= 0
                && float.IsFinite(d.ModHomingTurnRateDegrees) && d.ModHomingTurnRateDegrees >= 0
                && float.IsFinite(d.ModOrbitRadius) && d.ModOrbitRadius >= 0
                && float.IsFinite(d.ModOrbitAngularSpeedDegrees) && float.IsFinite(d.ModOrbitPhaseOffsetDegrees)
                && float.IsFinite(d.ModOrbitPlaneTiltDegrees) && d.ModBounceAdd >= 0 && d.ModBounceAdd <= SpellProgramLimits.MaxSteps
                && d.ExtraDraws >= 0 && d.ExtraDraws < SpellProgramLimits.MaxSteps
                && d.TriggerDrawCount >= 1 && d.TriggerDrawCount <= SpellProgramLimits.MaxSteps
                && d.ShieldReflectCount >= 0
                && float.IsFinite(d.LifetimeSeconds) && (d.LifetimeSeconds==-1||d.LifetimeSeconds>=0) && d.LifetimeSeconds<=600
                && float.IsFinite(d.ModLifetimeAddSeconds) && Math.Abs(d.ModLifetimeAddSeconds)<=600
                && float.IsFinite(d.ModLifetimeMul) && d.ModLifetimeMul>=0 && d.ModLifetimeMul<=600
                && d.PayloadTrigger >= PayloadTriggerMode.None && d.PayloadTrigger <= PayloadTriggerMode.OnTermination
                && d.SpawnMode >= SpellSpawnMode.ForwardProjectile && d.SpawnMode <= SpellSpawnMode.StaticAtPoint
                && d.DamageType >= Game.Combat.DamageType.Physical && d.DamageType <= Game.Combat.DamageType.True;
        }
    }
}
