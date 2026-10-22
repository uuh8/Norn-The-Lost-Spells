using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using NUnit.Framework;

namespace Game.Skills.Tests
{
    public sealed class CastExecutionRecorderTests
    {
        // 先验证正式记录入口存在，RED 必须来自断言，不用编译错误充当失败测试。
        [Test]
        public void Evaluator_HasIndependentGameplayRecordingEntry()
        {
            Assert.That(typeof(CastEvaluator).GetMethod("EvaluateRecorded", BindingFlags.Public | BindingFlags.Static), Is.Not.Null);
        }
        [Test]
        public void Recorder_SkipsNullBudgetBlockedAndUnpaidInstruction()
        {
            var emit = ScriptableObject.CreateInstance<SpellDefinition>();
            var expensive = ScriptableObject.CreateInstance<SpellDefinition>(); expensive.ManaCost = 20;
            var multi = ScriptableObject.CreateInstance<SpellDefinition>(); multi.Kind = SpellKind.Multicast; multi.ExtraDraws = 1;
            try
            {
                var output = new List<EmitCommand>(); var recorder = new CastExecutionRecorder();
                var summary = CastEvaluator.EvaluateRecorded(new[] { null, emit, emit, multi, expensive }, 1, 5, CastModifierState.Default, output, recorder);
                Assert.That(summary.Fizzled, Is.True); Assert.That(output.Count, Is.EqualTo(1));
                Assert.That(recorder.Count, Is.EqualTo(2)); Assert.That(recorder[0].SlotIndex, Is.EqualTo(1));
                Assert.That(recorder[1].SlotIndex, Is.EqualTo(3));
            }
            finally { Object.DestroyImmediate(emit); Object.DestroyImmediate(expensive); Object.DestroyImmediate(multi); }
        }
        [Test]
        public void Trigger_MapsFilteredPayloadBackToOriginalSlotsAndFreezesCommands()
        {
            var trigger = ScriptableObject.CreateInstance<SpellDefinition>(); trigger.PayloadTrigger = PayloadTriggerMode.OnImpact;
            var emit = ScriptableObject.CreateInstance<SpellDefinition>(); emit.BaseDamage = 12;
            try
            {
                Assert.That(CastExecutionPlan.TryBuild(new[] { trigger, null, emit }, 1, out var plan), Is.True);
                Assert.That(plan.LayerCount, Is.EqualTo(2)); Assert.That(plan.GetLayer(1).GetSlot(0), Is.EqualTo(2));
                emit.BaseDamage = 999; Assert.That(plan.GetLayer(1).GetCommand(0).Damage, Is.EqualTo(12));
            }
            finally { Object.DestroyImmediate(trigger); Object.DestroyImmediate(emit); }
        }
        [Test]
        public void NestedTrigger_LayersHaveDistinctParentBudgets()
        {
            var trigger = ScriptableObject.CreateInstance<SpellDefinition>(); trigger.PayloadTrigger = PayloadTriggerMode.OnImpact;
            var emit = ScriptableObject.CreateInstance<SpellDefinition>();
            try
            {
                Assert.That(CastExecutionPlan.TryBuild(new[] { trigger, trigger, emit }, 1, out var plan), Is.True);
                Assert.That(plan.LayerCount, Is.EqualTo(3)); Assert.That(plan.GetLayer(2).Parent, Is.EqualTo(1));
                Assert.That(plan.GetLayer(2).GetSlot(0), Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(trigger); Object.DestroyImmediate(emit); }
        }
        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(-1f)]
        public void InvalidMana_IsRejectedBeforeEvaluation(float cost)
        {
            var spell = ScriptableObject.CreateInstance<SpellDefinition>(); spell.ManaCost = cost;
            try { Assert.That(CastExecutionPlan.TryBuild(new[] { spell }, 1, out _), Is.False); }
            finally { Object.DestroyImmediate(spell); }
        }
        [Test]
        public void Recorder_ClearsPreviousCastAndMatchesLegacyOutputs()
        {
            var spell = ScriptableObject.CreateInstance<SpellDefinition>();
            try
            {
                var output = new List<EmitCommand>(); var recorder = new CastExecutionRecorder();
                CastEvaluator.EvaluateRecorded(new[] { spell }, 1, 10, CastModifierState.Default, output, recorder);
                Assert.That(recorder.Count, Is.EqualTo(1)); Assert.That(output.Count, Is.EqualTo(1));
                CastEvaluator.EvaluateRecorded(new SpellDefinition[0], 1, 10, CastModifierState.Default, output, recorder);
                Assert.That(recorder.Count, Is.Zero); Assert.That(output.Count, Is.Zero);
            }
            finally { Object.DestroyImmediate(spell); }
        }
    }
}
