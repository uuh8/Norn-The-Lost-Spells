using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Skills
{
    /// <summary>
    /// 可信历史的纯冻结接缝。Run先用真实Catalog重新编译同一个程序，再把原Graph/Trace证据交到这里。
    /// 它只验证索引闭包、预算、贡献和最终数值并构造不可变读对象；不调用Machine.TryBuild，不抽牌、不支付、不发射。
    /// 将构造集中在Skills内部，保留Graph等internal ctor，避免为序列化把全部可变构造权限向上层公开。
    /// </summary>
    public static class SpellExecutionReadModelFactory
    {
        public static bool TryCreate(SpellProgram program, OnlineWandExecutionReplaySnapshot value,
            out ShotExecutionGraph graph, out SpellExecutionTrace trace, out SpellBuildSummary summary, out string error)
        {
            graph = null; trace = null; summary = default; error = null;
            if (program == null || value == null || value.Scopes == null || value.Outputs == null || value.Trace == null
                || value.Summary == null || value.Scopes.Length < 1 || value.Scopes.Length > SpellProgramLimits.MaxOutputs + 1
                || value.Outputs.Length > SpellProgramLimits.MaxOutputs || value.Trace.Length > SpellProgramLimits.MaxTraceEvents)
                return Fail("冻结执行缺少有界Graph或Trace。", out error);
            var scopes = new ShotScope[value.Scopes.Length]; var depths = new int[scopes.Length];
            var contributions = new IReadOnlyList<SpellParameterContribution>[scopes.Length];
            var seen = new bool[value.Outputs.Length]; var owned = new bool[value.Outputs.Length];
            for (int i = 0; i < scopes.Length; i++)
            {
                var source = value.Scopes[i];
                if (source == null || source.Id != i || source.ParentScopeId < -1 || source.ParentScopeId >= i
                    || (i == 0) != (source.ParentScopeId == -1) || source.FinalModifiers == null
                    || source.OutputIds == null || source.Contributions == null || source.Contributions.Length > SpellProgramLimits.MaxSteps)
                    return Fail("Scope父链或贡献预算非法。", out error);
                if (i == 0) { if (source.OwnerOutputId != -1) return Fail("Root不能归属于输出。", out error); }
                else
                {
                    depths[i] = depths[source.ParentScopeId] + 1;
                    if (depths[i] > SpellProgramLimits.MaxScopeDepth || source.OwnerOutputId < 0 || source.OwnerOutputId >= owned.Length
                        || owned[source.OwnerOutputId] || value.Outputs[source.OwnerOutputId] == null
                        || value.Outputs[source.OwnerOutputId].ScopeId != source.ParentScopeId)
                        return Fail("Payload Scope的Owner或深度非法。", out error);
                    owned[source.OwnerOutputId] = true;
                }
                int previous = -1;
                foreach (int output in source.OutputIds)
                {
                    if (output < 0 || output >= seen.Length || seen[output] || output <= previous
                        || value.Outputs[output] == null || value.Outputs[output].ScopeId != i)
                        return Fail("Scope输出集合不是完整有序闭包。", out error);
                    seen[output] = true; previous = output;
                }
                var list = new SpellParameterContribution[source.Contributions.Length];
                var computed = new CastModifierState(0, 1, 1, i == 0 ? program.BaseSpreadDegrees : 0, 0, false, 0, 0, 0, 0, 0, 0, 0, ProjectileMotionMode.None);
                for (int j = 0; j < list.Length; j++)
                {
                    var item = source.Contributions[j];
                    if (item == null || item.CardIndex < 0 || item.CardIndex >= program.Count || item.StepId < 0 || item.StepId >= value.TraceTotalEvents)
                        return Fail("贡献没有对应原ProgramIndex或事件。", out error);
                    var instruction = program.Instructions[item.CardIndex];
                    list[j] = new SpellParameterContribution(item.CardIndex, item.StepId, instruction);
                    if (instruction.Kind == SpellKind.Modify) computed = computed.Apply(instruction.Modifier);
                }
                // 无输出Scope的原Graph没有公开贡献列表；保留收到的有限FinalModifiers，不凭空补造历史。
                if (source.OutputIds.Length != 0 && !SameModifier(source.FinalModifiers, computed))
                    return Fail("Scope最终修正与可信卡片贡献不符。", out error);
                var mods = RestoreModifier(source.FinalModifiers);
                contributions[i] = Array.AsReadOnly(list);
                scopes[i] = new ShotScope(i, source.ParentScopeId, source.OwnerOutputId, source.ClosedAtStep,
                    mods, source.CastDelayDelta, (int[])source.OutputIds.Clone());
            }
            var outputs = new ShotOutput[value.Outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                var source = value.Outputs[i];
                if (source == null || source.Id != i || !seen[i] || source.ScopeId < 0 || source.ScopeId >= scopes.Length
                    || source.SourceCardIndex < 0 || source.SourceCardIndex >= program.Count
                    || source.PayloadScopeId < -1 || source.PayloadScopeId >= scopes.Length)
                    return Fail("输出索引没有完整Scope闭包。", out error);
                var instruction = program.Instructions[source.SourceCardIndex];
                if (source.SourceInstanceId != instruction.InstanceId || instruction.Kind != SpellKind.Emit && instruction.Kind != SpellKind.StaticProjectile)
                    return Fail("输出并非原Emit实例。", out error);
                if (source.PayloadScopeId >= 0)
                {
                    var child = scopes[source.PayloadScopeId];
                    if (child.ParentScopeId != source.ScopeId || child.OwnerOutputId != i || child.OutputIds.Count == 0)
                        return Fail("输出Payload链接非法。", out error);
                }
                // Baker只合成受控Emission和已验证的冻结Modifier；不会重新运行Draw/Copy或改变任何资源。
                var command = SpellCommandBaker.Bake(instruction.Emission, scopes[source.ScopeId].FinalModifiers, graphPayload: source.PayloadScopeId >= 0);
                if (!SameCommand(source.Command, command)) return Fail("输出最终数值与受控Baker不符。", out error);
                var provenance = new SpellParameterProvenance(source.ScopeId, instruction.Emission,
                    scopes[source.ScopeId].FinalModifiers, command, contributions[source.ScopeId]);
                outputs[i] = new ShotOutput(i, source.ScopeId, source.SourceCardIndex, source.SourceInstanceId,
                    source.PayloadScopeId, source.RegisteredAtStep, command, provenance);
            }
            for (int i = 1; i < scopes.Length; i++)
                if (scopes[i].OutputIds.Count != 0 && outputs[scopes[i].OwnerOutputId].PayloadScopeId != i)
                    return Fail("非空Payload Scope没有反向链接。", out error);
            var steps = new SpellExecutionTraceStep[value.Trace.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                var s = value.Trace[i];
                if (s == null || s.StepId != i || s.CardIndex < -1 || s.CardIndex >= program.Count || s.CauseCardIndex < -1 || s.CauseCardIndex >= program.Count
                    || s.ScopeId < -1 || s.ScopeId >= scopes.Length || s.OutputId < -1 || s.OutputId >= outputs.Length)
                    return Fail("Trace索引无法指向同一次冻结执行。", out error);
                steps[i] = new SpellExecutionTraceStep(s.StepId, (SpellTraceKind)s.Kind, s.ScopeId, s.CardIndex,
                    s.RequestId, s.ParentRequestId, s.OutputId, s.ManaBefore, s.ManaAfter, (SpellCardCollection)s.From,
                    (SpellCardCollection)s.To, (SpellProgramError)s.Error, (SpellInvocationMode)s.InvocationMode,
                    s.UsesBefore, s.UsesAfter, s.CauseCardIndex, s.TargetPosition);
            }
            for (int i = 0; i < outputs.Length; i++)
            {
                int step = outputs[i].RegisteredAtStep;
                if (value.TraceWasCaptured && step < steps.Length && (steps[step].Kind != SpellTraceKind.OutputRegistered
                    || steps[step].OutputId != i || steps[step].CardIndex != outputs[i].SourceCardIndex || steps[step].ScopeId != outputs[i].ScopeId))
                    return Fail("输出注册事实与Trace不一致。", out error);
            }
            graph = new ShotExecutionGraph(program, scopes, outputs);
            trace = new SpellExecutionTrace(steps, value.TraceTotalEvents, value.TraceTruncated, value.TraceWasCaptured);
            var sum = value.Summary;
            summary = new SpellBuildSummary((SpellBuildOutcome)sum.Outcome,
                new SpellProgramDiagnostic((SpellProgramError)sum.Error, sum.DiagnosticSourceSlot, sum.DiagnosticMessage),
                sum.ManaAfter, sum.CastInterval, sum.ReloadInterval, sum.NeedsReload, sum.InstructionSteps);
            return true;
        }
        public static OnlineSpellModifierSnapshot CaptureModifier(in CastModifierState m) => new OnlineSpellModifierSnapshot
        { DamageAddFlat = m.DamageAddFlat, DamageMul = m.DamageMul, SpeedMul = m.SpeedMul, SpreadDegrees = m.SpreadDegrees,
            BounceCount = m.BounceCount, UseGravity = m.UseGravity, HomingRadius = m.HomingRadius, HomingDuration = m.HomingDuration,
            HomingTurnRateDegrees = m.HomingTurnRateDegrees, OrbitRadius = m.OrbitRadius, OrbitAngularSpeedDegrees = m.OrbitAngularSpeedDegrees,
            OrbitPhaseOffsetDegrees = m.OrbitPhaseOffsetDegrees, OrbitPlaneTiltDegrees = m.OrbitPlaneTiltDegrees,
            MotionMode = (byte)m.MotionMode, LifetimeAddSeconds = m.LifetimeAddSeconds, LifetimeMul = m.LifetimeMul };
        private static CastModifierState RestoreModifier(OnlineSpellModifierSnapshot m)
            => new CastModifierState(m.DamageAddFlat, m.DamageMul, m.SpeedMul, m.SpreadDegrees, m.BounceCount, m.UseGravity,
                m.HomingRadius, m.HomingDuration, m.HomingTurnRateDegrees, m.OrbitRadius, m.OrbitAngularSpeedDegrees,
                m.OrbitPhaseOffsetDegrees, m.OrbitPlaneTiltDegrees, (ProjectileMotionMode)m.MotionMode, m.LifetimeAddSeconds, m.LifetimeMul);
        private static bool SameModifier(OnlineSpellModifierSnapshot a, in CastModifierState b)
            => a.DamageAddFlat == b.DamageAddFlat && a.DamageMul == b.DamageMul && a.SpeedMul == b.SpeedMul && a.SpreadDegrees == b.SpreadDegrees
                && a.BounceCount == b.BounceCount && a.UseGravity == b.UseGravity && a.HomingRadius == b.HomingRadius && a.HomingDuration == b.HomingDuration
                && a.HomingTurnRateDegrees == b.HomingTurnRateDegrees && a.OrbitRadius == b.OrbitRadius && a.OrbitAngularSpeedDegrees == b.OrbitAngularSpeedDegrees
                && a.OrbitPhaseOffsetDegrees == b.OrbitPhaseOffsetDegrees && a.OrbitPlaneTiltDegrees == b.OrbitPlaneTiltDegrees
                && a.MotionMode == (byte)b.MotionMode && a.LifetimeAddSeconds == b.LifetimeAddSeconds && a.LifetimeMul == b.LifetimeMul;
        public static OnlineRecordedSpellCommandSnapshot CaptureCommand(in EmitCommand c) => new OnlineRecordedSpellCommandSnapshot
        { LifetimeSeconds = c.LifetimeSeconds, LifetimeAddSeconds = c.LifetimeAddSeconds, LifetimeMul = c.LifetimeMul, SpawnMode = (byte)c.SpawnMode,
            SkyfallHeight = c.SkyfallHeight, SkyfallBackOffset = c.SkyfallBackOffset, LandingSiteDuration = c.LandingSiteDuration, ShieldReflectCount = c.ShieldReflectCount,
            Damage = c.Damage, ExplosionDamage = c.ExplosionDamage, FireFieldDamagePerTick = c.FireFieldDamagePerTick,
            FireFieldTickInterval = c.FireFieldTickInterval, FireFieldDuration = c.FireFieldDuration, Speed = c.Speed, DamageType = (int)c.DamageType,
            SpreadDegrees = c.SpreadDegrees, BounceCount = c.BounceCount, UseGravity = c.UseGravity, HomingRadius = c.HomingRadius,
            HomingDuration = c.HomingDuration, HomingTurnRateDegrees = c.HomingTurnRateDegrees, OrbitRadius = c.OrbitRadius,
            OrbitAngularSpeedDegrees = c.OrbitAngularSpeedDegrees, OrbitPhaseOffsetDegrees = c.OrbitPhaseOffsetDegrees,
            OrbitPlaneTiltDegrees = c.OrbitPlaneTiltDegrees, MotionMode = (byte)c.MotionMode, PayloadTrigger = (byte)c.PayloadTrigger, PayloadDelaySeconds = c.PayloadDelaySeconds };
        private static bool SameCommand(OnlineRecordedSpellCommandSnapshot a, in EmitCommand b)
            => a != null && a.LifetimeSeconds == b.LifetimeSeconds && a.LifetimeAddSeconds == b.LifetimeAddSeconds && a.LifetimeMul == b.LifetimeMul
                && a.SpawnMode == (byte)b.SpawnMode && a.SkyfallHeight == b.SkyfallHeight && a.SkyfallBackOffset == b.SkyfallBackOffset
                && a.LandingSiteDuration == b.LandingSiteDuration && a.ShieldReflectCount == b.ShieldReflectCount && a.Damage == b.Damage
                && a.ExplosionDamage == b.ExplosionDamage && a.FireFieldDamagePerTick == b.FireFieldDamagePerTick
                && a.FireFieldTickInterval == b.FireFieldTickInterval && a.FireFieldDuration == b.FireFieldDuration && a.Speed == b.Speed
                && a.DamageType == (int)b.DamageType && a.SpreadDegrees == b.SpreadDegrees && a.BounceCount == b.BounceCount && a.UseGravity == b.UseGravity
                && a.HomingRadius == b.HomingRadius && a.HomingDuration == b.HomingDuration && a.HomingTurnRateDegrees == b.HomingTurnRateDegrees
                && a.OrbitRadius == b.OrbitRadius && a.OrbitAngularSpeedDegrees == b.OrbitAngularSpeedDegrees && a.OrbitPhaseOffsetDegrees == b.OrbitPhaseOffsetDegrees
                && a.OrbitPlaneTiltDegrees == b.OrbitPlaneTiltDegrees && a.MotionMode == (byte)b.MotionMode && a.PayloadTrigger == (byte)b.PayloadTrigger
                && a.PayloadDelaySeconds == b.PayloadDelaySeconds;
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
