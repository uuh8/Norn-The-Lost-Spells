using System.Collections.Generic;
using Game.Core;
using Game.Skills;

namespace Game.Character
{
    /// <summary>
    /// SpellCaster 的开发诊断协作者：集中条件编译、Trace 收集和日志格式化。
    /// Gameplay 主流程只调用窄接口，不需要理解 Editor/Development Build 分支。
    /// </summary>
    internal sealed class SpellCastRuntimeDiagnostics
    {
        // castId 只用于把同一次施法及其后续 Payload 的诊断日志串起来，不参与玩法计算。
        private static int s_nextCastId;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Trace 仅在开发构建中存在；固定初始 Capacity 让常见施法不必为诊断步骤频繁扩容。
        private readonly CastTraceCollector _traceCollector = new CastTraceCollector(64);
#endif

        internal int AllocateCastId()
        {
            return ++s_nextCastId;
        }

        /// <summary>
        /// Release Build 强制对外表现为 Off，避免调用方误以为详细 Trace 仍会运行。
        /// </summary>
        internal CastTraceLevel ResolveLevel(CastTraceLevel configuredLevel)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return configuredLevel;
#else
            return CastTraceLevel.Off;
#endif
        }

        /// <summary>
        /// 选择正式求值入口，并在开发构建中按配置附加 Trace；两条入口仍共用 CastEvaluator 的规则核心。
        /// </summary>
        internal CastSummary EvaluateCommands(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            CastModifierState incomingMods,
            List<EmitCommand> output,
            CastTraceLevel configuredLevel,
            int castId,
            int depth,
            float requiredMana)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            CastSummary summary = configuredLevel == CastTraceLevel.Detailed
                ? CastEvaluator.EvaluateWithTrace(
                    spells,
                    baseDraws,
                    float.PositiveInfinity,
                    incomingMods,
                    output,
                    _traceCollector)
                : CastEvaluator.Evaluate(
                    spells,
                    baseDraws,
                    float.PositiveInfinity,
                    incomingMods,
                    output);

            LogTrace(configuredLevel, castId, depth, requiredMana, summary, output);
            return summary;
#else
            return CastEvaluator.Evaluate(
                spells,
                baseDraws,
                float.PositiveInfinity,
                incomingMods,
                output);
#endif
        }

        /// <summary>
        /// Mana 预检失败时补充诊断证据。Detailed 会用真实可用 Mana 再跑一次纯求值，定位具体在哪条指令 Fizzle；
        /// 这次求值只写入诊断 List，不会扣第二次 Mana，也不会生成场景对象。
        /// </summary>
        internal void LogManaPreflightFailure(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            CastModifierState incomingMods,
            List<EmitCommand> output,
            CastTraceLevel configuredLevel,
            int castId,
            int depth,
            float requiredMana,
            float availableMana)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (configuredLevel == CastTraceLevel.Off)
                return;

            if (configuredLevel == CastTraceLevel.Detailed)
            {
                CastSummary summary = CastEvaluator.EvaluateWithTrace(
                    spells,
                    baseDraws,
                    availableMana,
                    incomingMods,
                    output,
                    _traceCollector);
                LogTrace(configuredLevel, castId, depth, requiredMana, summary, output);
                // EvaluateWithTrace 会把失败前的临时 Emit 写入复用 List；清空它，保证失败施法不被后续逻辑误消费。
                output.Clear();
                return;
            }

            GameLog.Info(
                $"Cast #{castId} Depth={depth} PRECHECK FAILED Emits=0 " +
                $"RequiredMana={requiredMana:0.##} AvailableMana={availableMana:0.##} Fizzled=True",
                "SpellTrace");
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// 输出一层施法的 Summary；Detailed 模式再按 CastTraceCollector 的发生顺序展开每一步。
        /// GameLog 调用只在 Editor/Development Build 有效，这整个实现块在 Release 不参与编译。
        /// </summary>
        private void LogTrace(
            CastTraceLevel configuredLevel,
            int castId,
            int depth,
            float requiredMana,
            CastSummary summary,
            List<EmitCommand> output)
        {
            if (configuredLevel == CastTraceLevel.Off)
                return;

            GameLog.Info(
                $"Cast #{castId} Depth={depth} Emits={output.Count} " +
                $"RequiredMana={requiredMana:0.##} EvaluatedMana={summary.ManaSpent:0.##} " +
                $"Fizzled={summary.Fizzled}",
                "SpellTrace");

            if (configuredLevel != CastTraceLevel.Detailed)
                return;

            for (int i = 0; i < _traceCollector.Count; i++)
                LogTraceStep(castId, depth, _traceCollector[i]);

            if (_traceCollector.ExpandedDuringLastCollection)
            {
                GameLog.Warn(
                    $"Cast #{castId} Trace 超出预分配容量，本次诊断发生 List 扩容",
                    "SpellTrace");
            }
        }

        /// <summary>把不可变 CastTraceStep 快照翻译成人能阅读的日志，不反向改变解释器状态。</summary>
        private static void LogTraceStep(int castId, int depth, CastTraceStep step)
        {
            string spellName = step.Spell != null
                ? step.Spell.DisplayName
                : "<none>";

            switch (step.Kind)
            {
                case CastTraceStepKind.CastStarted:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} START Draw={step.DrawBudgetBefore} " +
                        FormatModifiers(step.ModifiersBefore),
                        "SpellTrace");
                    break;
                case CastTraceStepKind.ModifyApplied:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} MODIFY " +
                        $"Before[{FormatModifiers(step.ModifiersBefore)}] " +
                        $"After[{FormatModifiers(step.ModifiersAfter)}]",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.MulticastApplied:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} MULTICAST " +
                        $"Draw {step.DrawBudgetBefore}->{step.DrawBudgetAfter}", "SpellTrace");
                    break;
                case CastTraceStepKind.DrawBudgetBlocked:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} BLOCKED Draw=0",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.EmitProduced:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} EMIT#{step.EmitIndex} " +
                        FormatEmit(step.Emit) + " " +
                        $"Draw {step.DrawBudgetBefore}->{step.DrawBudgetAfter}",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.PayloadCaptured:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] PAYLOAD " +
                        $"Trigger={step.PayloadTrigger} Delay={step.Emit.PayloadDelaySeconds:0.##} " +
                        $"Start={step.PayloadStartIndex} Count={step.PayloadCount}",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.ManaFizzle:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} FIZZLE " +
                        $"ManaLeft={step.ManaLeftBefore:0.##}",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.NullSpellSkipped:
                    GameLog.Info($"Cast #{castId} D{depth} [{step.SpellIndex}] NULL SKIPPED", "SpellTrace");
                    break;
                case CastTraceStepKind.CastCompleted:
                    GameLog.Info($"Cast #{castId} D{depth} COMPLETE DrawLeft={step.DrawBudgetAfter}", "SpellTrace");
                    break;
            }
        }

        // 字符串插值会产生诊断字符串分配，所以格式化函数严格位于开发条件编译块内。
        private static string FormatModifiers(CastModifierState modifiers)
        {
            return $"DamageAddFlat={modifiers.DamageAddFlat:0.##} DamageMul={modifiers.DamageMul:0.##} " +
                   $"SpeedMul={modifiers.SpeedMul:0.##} Spread={modifiers.SpreadDegrees:0.##} " +
                   $"Bounce={modifiers.BounceCount} UseGravity={modifiers.UseGravity} " +
                   $"Homing(R={modifiers.HomingRadius:0.##},Duration={modifiers.HomingDuration:0.##},Turn={modifiers.HomingTurnRateDegrees:0.##}) " +
                   $"Orbit(R={modifiers.OrbitRadius:0.##},Angular={modifiers.OrbitAngularSpeedDegrees:0.##},Phase={modifiers.OrbitPhaseOffsetDegrees:0.##},Tilt={modifiers.OrbitPlaneTiltDegrees:0.##}) " +
                   $"Motion={modifiers.MotionMode}";
        }

        /// <summary>把一条已经烘焙完成的 EmitCommand 展开为日志，便于核对 Authoring Data 最终合成了什么。</summary>
        private static string FormatEmit(EmitCommand emit)
        {
            return $"Projectile={(emit.ProjectilePrefab != null ? emit.ProjectilePrefab.name : "<none>")} " +
                   $"LandingSite={(emit.LandingSitePrefab != null ? emit.LandingSitePrefab.name : "<none>")} " +
                   $"CastSfx={(emit.CastSfx != null ? emit.CastSfx.name : "<none>")} " +
                   $"Damage={emit.Damage:0.##} Speed={emit.Speed:0.##} DamageType={emit.DamageType} " +
                   $"Spread={emit.SpreadDegrees:0.##} Bounce={emit.BounceCount} UseGravity={emit.UseGravity} " +
                   $"Homing(R={emit.HomingRadius:0.##},Duration={emit.HomingDuration:0.##},Turn={emit.HomingTurnRateDegrees:0.##}) " +
                   $"Orbit(R={emit.OrbitRadius:0.##},Angular={emit.OrbitAngularSpeedDegrees:0.##},Phase={emit.OrbitPhaseOffsetDegrees:0.##},Tilt={emit.OrbitPlaneTiltDegrees:0.##}) " +
                   $"Motion={emit.MotionMode} SpawnMode={emit.SpawnMode} " +
                   $"Skyfall(Height={emit.SkyfallHeight:0.##},Back={emit.SkyfallBackOffset:0.##},LandingDuration={emit.LandingSiteDuration:0.##}) " +
                   $"ShieldReflect={emit.ShieldReflectCount} " +
                   $"Payload(Has={emit.HasPayload},Count={(emit.Payload != null ? emit.Payload.Count : 0)},Trigger={emit.PayloadTrigger},Delay={emit.PayloadDelaySeconds:0.##}," +
                   $"Mods=[{FormatModifiers(emit.PayloadMods)}])";
        }
#endif
    }
}
