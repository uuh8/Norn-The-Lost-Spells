using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Game.Combat;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class CastTraceTests
    {
        [Test]
        public void Collector_RecordAndClear_ReusesLogicalBuffer()
        {
            var collector = new CastTraceCollector(4);
            var step = new CastTraceStep(
                CastTraceStepKind.CastStarted,
                -1,
                null,
                1,
                1,
                10f,
                10f,
                CastModifierState.Default,
                CastModifierState.Default);

            collector.Record(step);
            Assert.AreEqual(1, collector.Count);
            Assert.AreEqual(CastTraceStepKind.CastStarted, collector[0].Kind);

            collector.Clear();
            Assert.AreEqual(0, collector.Count);
            Assert.IsFalse(collector.ExpandedDuringLastCollection);
        }

        [Test]
        public void Collector_Constructor_ClampsNegativeCapacity()
        {
            Assert.DoesNotThrow(() => new CastTraceCollector(-10));
        }

        [Test]
        public void Collector_WhenCapacityIsExceeded_ReportsExpansion()
        {
            var collector = new CastTraceCollector(1);
            var step = new CastTraceStep(
                CastTraceStepKind.CastStarted, -1, null,
                1, 1, 10f, 10f,
                CastModifierState.Default, CastModifierState.Default);

            collector.Record(step);
            collector.Record(step);

            Assert.IsTrue(collector.ExpandedDuringLastCollection);
        }

        [Test]
        public void EvaluateWithTrace_SingleEmit_RecordsStartEmitAndComplete()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { Emit(15f) }, 1, 100f,
                CastModifierState.Default, output, trace);

            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(3, trace.Count);
            Assert.AreEqual(CastTraceStepKind.CastStarted, trace[0].Kind);
            Assert.AreEqual(CastTraceStepKind.EmitProduced, trace[1].Kind);
            Assert.AreEqual(15f, trace[1].Emit.Damage, 1e-4f);
            Assert.AreEqual(CastTraceStepKind.CastCompleted, trace[2].Kind);
        }

        [Test]
        public void EvaluateWithTrace_NullSpell_RecordsSkippedSlot()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new SpellDefinition[] { null, Emit() }, 1, 100f,
                CastModifierState.Default, output, trace);

            Assert.AreEqual(4, trace.Count);
            Assert.AreEqual(CastTraceStepKind.NullSpellSkipped, trace[1].Kind);
            Assert.AreEqual(0, trace[1].SpellIndex);
        }

        [Test]
        public void EvaluateWithTrace_ModifyAndMulticast_RecordsBeforeAfterState()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { Multicast(2), DamageMod(2f), Emit(10f) },
                1, 100f, CastModifierState.Default, output, trace);

            Assert.AreEqual(CastTraceStepKind.MulticastApplied, trace[1].Kind);
            Assert.AreEqual(1, trace[1].DrawBudgetBefore);
            Assert.AreEqual(3, trace[1].DrawBudgetAfter);
            Assert.AreEqual(CastTraceStepKind.ModifyApplied, trace[2].Kind);
            Assert.AreEqual(1f, trace[2].ModifiersBefore.DamageMul, 1e-4f);
            Assert.AreEqual(2f, trace[2].ModifiersAfter.DamageMul, 1e-4f);
        }

        [Test]
        public void EvaluateWithTrace_ManaFizzle_RecordsFailingIndex()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastSummary summary = CastEvaluator.EvaluateWithTrace(
                new[] { DamageMod(2f, 5f), Emit() },
                1, 3f, CastModifierState.Default, output, trace);

            Assert.IsTrue(summary.Fizzled);
            Assert.AreEqual(CastTraceStepKind.ManaFizzle, trace[1].Kind);
            Assert.AreEqual(0, trace[1].SpellIndex);
        }

        [Test]
        public void EvaluateWithTrace_ManaFizzle_PreservesRemainingManaAndFailedIndex()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastSummary summary = CastEvaluator.EvaluateWithTrace(
                new[] { DamageMod(2f, 2f), Emit(mana: 5f) },
                1, 6f, CastModifierState.Default, output, trace);

            Assert.IsTrue(summary.Fizzled);
            Assert.AreEqual(2f, summary.ManaSpent, 1e-4f);
            Assert.AreEqual(0, output.Count);
            Assert.AreEqual(CastTraceStepKind.ManaFizzle, trace[2].Kind);
            Assert.AreEqual(1, trace[2].SpellIndex);
            Assert.AreEqual(4f, trace[2].ManaLeftBefore, 1e-4f);
            Assert.AreEqual(4f, trace[2].ManaLeftAfter, 1e-4f);
            Assert.AreEqual(4f, trace[3].ManaLeftAfter, 1e-4f);
        }

        [Test]
        public void EvaluateWithTrace_ZeroBudget_RecordsBlockedEmit()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { Emit() }, 0, 100f,
                CastModifierState.Default, output, trace);

            Assert.AreEqual(0, output.Count);
            Assert.AreEqual(CastTraceStepKind.DrawBudgetBlocked, trace[1].Kind);
        }

        [Test]
        public void EvaluateWithTrace_Trigger_RecordsPayloadCapture()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { Emit(trigger: PayloadTriggerMode.OnImpact), Emit() },
                1, 100f, CastModifierState.Default, output, trace);

            Assert.AreEqual(CastTraceStepKind.PayloadCaptured, trace[2].Kind);
            Assert.AreEqual(1, trace[2].PayloadStartIndex);
            Assert.AreEqual(1, trace[2].PayloadCount);
        }

        [Test]
        public void EvaluateWithTrace_AfterDelayPayload_PreservesTriggerDelayAndSuffix()
        {
            SpellDefinition payloadSpell = Emit(7f);
            SpellDefinition trigger = Emit(trigger: PayloadTriggerMode.AfterDelay);
            trigger.PayloadDelaySeconds = 1.75f;
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { trigger, payloadSpell }, 1, 100f,
                CastModifierState.Default, output, trace);

            Assert.AreEqual(1, output.Count);
            Assert.IsTrue(output[0].HasPayload);
            Assert.AreEqual(PayloadTriggerMode.AfterDelay, output[0].PayloadTrigger);
            Assert.AreEqual(1.75f, output[0].PayloadDelaySeconds, 1e-4f);
            Assert.AreEqual(1, output[0].Payload.Count);
            Assert.AreSame(payloadSpell, output[0].Payload[0]);
            Assert.AreEqual(PayloadTriggerMode.AfterDelay, trace[2].PayloadTrigger);
            AssertEmitCommandEquivalent(output[0], trace[2].Emit);
        }

        [Test]
        public void EvaluateWithTrace_IncomingModifiers_AreBakedAndRecordedAtStart()
        {
            var incoming = new CastModifierState(
                3f, 1.5f, 0.75f, 12f, 2, true,
                8f, 2f, 180f,
                4f, 90f, 30f, 15f,
                ProjectileMotionMode.Orbit);
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { Emit(10f) }, 1, 100f,
                incoming, output, trace);

            AssertModifierStateEquivalent(incoming, trace[0].ModifiersBefore);
            Assert.AreEqual(19.5f, output[0].Damage, 1e-4f);
            Assert.AreEqual(15f, output[0].Speed, 1e-4f);
            Assert.AreEqual(12f, output[0].SpreadDegrees, 1e-4f);
            Assert.AreEqual(ProjectileMotionMode.Orbit, output[0].MotionMode);
            AssertModifierStateEquivalent(incoming, output[0].PayloadMods);
        }

        [Test]
        public void EvaluateWithTrace_NullCollector_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
                CastEvaluator.EvaluateWithTrace(
                    new[] { Emit() }, 1, 100f,
                    CastModifierState.Default, new List<EmitCommand>(), null));
        }

        [Test]
        public void EvaluateAndEvaluateWithTrace_ProduceEquivalentCommands()
        {
            SpellDefinition trigger = Emit(10f, trigger: PayloadTriggerMode.AfterDelay);
            trigger.PayloadDelaySeconds = 2.25f;
            trigger.SpawnMode = SpellSpawnMode.SkyfallAtPoint;
            trigger.SkyfallHeight = 18f;
            trigger.SkyfallBackOffset = 3f;
            trigger.LandingSiteDuration = 0.6f;
            trigger.ShieldReflectCount = 5;
            SpellDefinition[] spells =
            {
                Multicast(2), DamageMod(1.5f), trigger, Emit(12f), Emit(14f)
            };
            var incoming = new CastModifierState(
                2f, 1.25f, 0.8f, 9f, 3, true,
                7f, 1.5f, 120f,
                2.5f, 75f, 20f, 10f,
                ProjectileMotionMode.Homing);
            var normal = new List<EmitCommand>();
            var traced = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastSummary normalSummary = CastEvaluator.Evaluate(
                spells, 1, 100f, incoming, normal);
            CastSummary tracedSummary = CastEvaluator.EvaluateWithTrace(
                spells, 1, 100f, incoming, traced, trace);

            Assert.AreEqual(normalSummary.ManaSpent, tracedSummary.ManaSpent, 1e-4f);
            Assert.AreEqual(normalSummary.Fizzled, tracedSummary.Fizzled);
            Assert.AreEqual(normal.Count, traced.Count);

            for (int i = 0; i < normal.Count; i++)
                AssertEmitCommandEquivalent(normal[i], traced[i]);
        }

        private static void AssertEmitCommandEquivalent(EmitCommand expected, EmitCommand actual)
        {
            Assert.AreSame(expected.ProjectilePrefab, actual.ProjectilePrefab);
            Assert.AreEqual(expected.SpawnMode, actual.SpawnMode);
            Assert.AreSame(expected.LandingSitePrefab, actual.LandingSitePrefab);
            Assert.AreEqual(expected.SkyfallHeight, actual.SkyfallHeight, 1e-4f);
            Assert.AreEqual(expected.SkyfallBackOffset, actual.SkyfallBackOffset, 1e-4f);
            Assert.AreEqual(expected.LandingSiteDuration, actual.LandingSiteDuration, 1e-4f);
            Assert.AreEqual(expected.ShieldReflectCount, actual.ShieldReflectCount);
            Assert.AreEqual(expected.Damage, actual.Damage, 1e-4f);
            Assert.AreEqual(expected.Speed, actual.Speed, 1e-4f);
            Assert.AreEqual(expected.DamageType, actual.DamageType);
            Assert.AreEqual(expected.SpreadDegrees, actual.SpreadDegrees, 1e-4f);
            Assert.AreEqual(expected.BounceCount, actual.BounceCount);
            Assert.AreEqual(expected.UseGravity, actual.UseGravity);
            Assert.AreEqual(expected.HomingRadius, actual.HomingRadius, 1e-4f);
            Assert.AreEqual(expected.HomingDuration, actual.HomingDuration, 1e-4f);
            Assert.AreEqual(expected.HomingTurnRateDegrees, actual.HomingTurnRateDegrees, 1e-4f);
            Assert.AreEqual(expected.OrbitRadius, actual.OrbitRadius, 1e-4f);
            Assert.AreEqual(expected.OrbitAngularSpeedDegrees, actual.OrbitAngularSpeedDegrees, 1e-4f);
            Assert.AreEqual(expected.OrbitPhaseOffsetDegrees, actual.OrbitPhaseOffsetDegrees, 1e-4f);
            Assert.AreEqual(expected.OrbitPlaneTiltDegrees, actual.OrbitPlaneTiltDegrees, 1e-4f);
            Assert.AreEqual(expected.MotionMode, actual.MotionMode);
            Assert.AreSame(expected.CastSfx, actual.CastSfx);
            Assert.AreEqual(expected.PayloadTrigger, actual.PayloadTrigger);
            Assert.AreEqual(expected.PayloadDelaySeconds, actual.PayloadDelaySeconds, 1e-4f);
            AssertModifierStateEquivalent(expected.PayloadMods, actual.PayloadMods);

            if (expected.Payload == null || actual.Payload == null)
            {
                Assert.AreEqual(expected.Payload == null, actual.Payload == null);
                return;
            }

            Assert.AreEqual(expected.Payload.Count, actual.Payload.Count);
            for (int i = 0; i < expected.Payload.Count; i++)
                Assert.AreSame(expected.Payload[i], actual.Payload[i]);
        }

        private static void AssertModifierStateEquivalent(
            CastModifierState expected,
            CastModifierState actual)
        {
            Assert.AreEqual(expected.DamageAddFlat, actual.DamageAddFlat, 1e-4f);
            Assert.AreEqual(expected.DamageMul, actual.DamageMul, 1e-4f);
            Assert.AreEqual(expected.SpeedMul, actual.SpeedMul, 1e-4f);
            Assert.AreEqual(expected.SpreadDegrees, actual.SpreadDegrees, 1e-4f);
            Assert.AreEqual(expected.BounceCount, actual.BounceCount);
            Assert.AreEqual(expected.UseGravity, actual.UseGravity);
            Assert.AreEqual(expected.HomingRadius, actual.HomingRadius, 1e-4f);
            Assert.AreEqual(expected.HomingDuration, actual.HomingDuration, 1e-4f);
            Assert.AreEqual(expected.HomingTurnRateDegrees, actual.HomingTurnRateDegrees, 1e-4f);
            Assert.AreEqual(expected.OrbitRadius, actual.OrbitRadius, 1e-4f);
            Assert.AreEqual(expected.OrbitAngularSpeedDegrees, actual.OrbitAngularSpeedDegrees, 1e-4f);
            Assert.AreEqual(expected.OrbitPhaseOffsetDegrees, actual.OrbitPhaseOffsetDegrees, 1e-4f);
            Assert.AreEqual(expected.OrbitPlaneTiltDegrees, actual.OrbitPlaneTiltDegrees, 1e-4f);
            Assert.AreEqual(expected.MotionMode, actual.MotionMode);
        }

        private static SpellDefinition Emit(
            float damage = 10f,
            float mana = 0f,
            PayloadTriggerMode trigger = PayloadTriggerMode.None)
        {
            var spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.Kind = SpellKind.Emit;
            spell.DisplayName = "Test Fireball";
            spell.BaseDamage = damage;
            spell.BaseSpeed = 20f;
            spell.DamageType = DamageType.Magical;
            spell.ManaCost = mana;
            spell.PayloadTrigger = trigger;
            return spell;
        }

        private static SpellDefinition DamageMod(float multiplier, float mana = 0f)
        {
            var spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.Kind = SpellKind.Modify;
            spell.DisplayName = "Test Damage Modifier";
            spell.ModDamageMul = multiplier;
            spell.ManaCost = mana;
            return spell;
        }

        private static SpellDefinition Multicast(int extraDraws)
        {
            var spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.Kind = SpellKind.Multicast;
            spell.DisplayName = "Test Multicast";
            spell.ExtraDraws = extraDraws;
            return spell;
        }
    }
}
