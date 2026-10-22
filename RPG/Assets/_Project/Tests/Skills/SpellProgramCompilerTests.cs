using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Game.Skills.Tests
{
    /// <summary>先锁定可调用的V2边界；独立于Legacy入口，避免验证时替换正式玩法。</summary>
    public sealed class SpellProgramCompilerTests : SpellProgramTestSupport
    {
        [Test]
        public void V2CompilerContract_IsAvailableWithoutReplacingLegacy()
        {
            Assert.That(typeof(CastEvaluator).Assembly.GetType("Game.Skills.SpellProgramCompiler"),
                Is.Not.Null, "V2编译入口尚未实现；阶段一应新增独立接口。");
        }

        [Test] public void ExistingDoubleExtraDrawsOne_BecomesTwoDrawRequests()
        { Assert.That(Compile(Multi()).Instructions[0].DrawCount, Is.EqualTo(2)); }

        [Test] public void AuthoringChangesAfterCompile_DoNotChangeFrozenRules()
        {
            var spell = Emit(10, 5); var cards = new[] { spell };
            var ids = new[] { "owned-card-1" };
            var inputs = new SpellProgramCompileInputs(1, ids);
            ids[0] = "changed-before-compile";
            Assert.That(SpellProgramCompiler.TryCompile(cards, inputs, out var program, out _), Is.True);
            spell.BaseDamage = 999; spell.ManaCost = 100; cards[0] = Emit(123);
            Assert.That(program.Instructions[0].Emission.BaseDamage, Is.EqualTo(10));
            Assert.That(program.Instructions[0].ManaCost, Is.EqualTo(5));
            Assert.That(program.Instructions[0].InstanceId, Is.EqualTo("owned-card-1"));
            Assert.That(program.Instructions, Is.Not.InstanceOf<SpellInstruction[]>());
            Assert.Throws<NotSupportedException>(() => ((IList<SpellInstruction>)program.Instructions)[0] = default);
        }

        [Test] public void ModifiersAndTimeAreFrozen_NotReadFromDefinitionDuringBuild()
        {
            var d = Modify(6); d.CastDelayDelta = -.1f; d.ReloadDelta = -.2f;
            var program = Compile(d); d.ModDamageAddFlat = 900; d.CastDelayDelta = 3;
            Assert.That(program.Instructions[0].Modifier.DamageAddFlat, Is.EqualTo(6));
            Assert.That(program.Instructions[0].CastDelayDelta, Is.EqualTo(-.1f));
            Assert.That(program.Instructions[0].ReloadDelta, Is.EqualTo(-.2f));
        }

        [Test] public void NullSlotsAreSkipped_OriginalSlotAndDistinctInstanceIdsRemain()
        {
            var shared = Emit(); var program = Compile(null, shared, null, shared);
            Assert.That(program.Count, Is.EqualTo(2)); Assert.That(program.SourceSlotCount, Is.EqualTo(4));
            Assert.That(program.Instructions[0].SourceSlot, Is.EqualTo(1));
            Assert.That(program.Instructions[1].SourceSlot, Is.EqualTo(3));
            Assert.That(program.Instructions[0].InstanceId, Is.Not.EqualTo(program.Instructions[1].InstanceId));
        }

        [TestCase(5)] [TestCase(255)]
        public void UnimplementedOpcode_IsRejectedWithSourceSlot(int value)
        {
            var d = Emit(); d.Kind = (SpellKind)value;
            Assert.That(SpellProgramCompiler.TryCompile(new[] { null, d }, new SpellProgramCompileInputs(1),
                out var program, out var diagnostic), Is.False);
            Assert.That(program, Is.Null); Assert.That(diagnostic.SourceSlot, Is.EqualTo(1));
            Assert.That(diagnostic.Error, Is.EqualTo(SpellProgramError.UnsupportedInstruction));
        }

        [Test] public void EveryFloatAuthoringField_RejectsNaNAndInfinity()
        {
            var fields = typeof(SpellDefinition).GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.FieldType != typeof(float)) continue;
                foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                {
                    var d = Emit(); field.SetValue(d, invalid);
                    Assert.That(SpellProgramCompiler.TryCompile(new[] { d }, new SpellProgramCompileInputs(1),
                        out _, out var diagnostic), Is.False, field.Name);
                    Assert.That(diagnostic.SourceSlot, Is.Zero, field.Name);
                }
            }
        }

        [Test] public void FiniteNegativeMana_IsAllowedOnlyInV2Compilation()
        { Assert.That(Compile(Modify(0, -30)).Instructions[0].ManaCost, Is.EqualTo(-30)); }

        [Test] public void DuplicateOwnedId_IsRejected()
        {
            Assert.That(SpellProgramCompiler.TryCompile(new[] { Emit(), Emit() },
                new SpellProgramCompileInputs(1, new[] { "same", "same" }), out _, out var d), Is.False);
            Assert.That(d.Error, Is.EqualTo(SpellProgramError.DuplicateInstance));
        }

        [TestCase(0)] [TestCase(-1)] [TestCase(1025)]
        public void InvalidBaseDraws_IsRejected(int draws)
        { Assert.That(SpellProgramCompiler.TryCompile(new[] { Emit() }, new SpellProgramCompileInputs(draws), out _, out _), Is.False); }

        [Test] public void InvalidTimingAndEnums_AreRejected()
        {
            var d = Emit(); d.PayloadTrigger = (PayloadTriggerMode)255;
            Assert.That(SpellProgramCompiler.TryCompile(new[] { d }, new SpellProgramCompileInputs(1), out _, out _), Is.False);
            d.PayloadTrigger = PayloadTriggerMode.None; d.TriggerDrawCount = 0;
            Assert.That(SpellProgramCompiler.TryCompile(new[] { d }, new SpellProgramCompileInputs(1), out _, out _), Is.False);
            Assert.That(SpellProgramCompiler.TryCompile(new[] { Emit() }, new SpellProgramCompileInputs(1, baseCastDelay: float.NaN), out _, out _), Is.False);
        }

        [Test] public void EmptyProgram_CompilesForAnExplicitEmptyState()
        { Assert.That(Compile().Count, Is.Zero); }
    }
}
