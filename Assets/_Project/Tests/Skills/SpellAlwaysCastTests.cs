using NUnit.Framework;
namespace Game.Skills.Tests
{
    public sealed class SpellAlwaysCastTests : SpellProgramTestSupport
    {
        [Test] public void FixedModifierDoesNotTurnOneDrawIntoTwoAndPaysNoPositiveMana()
        {
            Assert.That(SpellProgramCompiler.TryCompile(new[]{Emit(10,5),Emit(20,5)},new SpellProgramCompileInputs(1,alwaysCast:new[]{Modify(6,99)}),out var p,out var d),Is.True,d.Message);
            var b=new SpellBuildBuffer();Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(10),b,out var s),Is.True);
            Assert.That(b.Graph.Outputs.Count,Is.EqualTo(1));Assert.That(b.Graph.Outputs[0].Command.Damage,Is.EqualTo(16));Assert.That(s.ManaAfter,Is.EqualTo(5));Assert.That(b.NextState.Deck,Is.EqualTo(new[]{1}));
        }
        [Test] public void FixedAddManaOnceAndNoIntrinsicCardMovesIntoAnyPile()
        {
            SpellProgramCompiler.TryCompile(new[]{Emit(10,15)},new SpellProgramCompileInputs(1,alwaysCast:new[]{Modify(0,-10)}),out var p,out _);
            var b=new SpellBuildBuffer();Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(5),b,out var s),Is.True);
            Assert.That(s.ManaAfter,Is.Zero);Assert.That(b.Graph.Outputs.Count,Is.EqualTo(1));Assert.That(b.NextState.Deck,Is.EqualTo(new[]{0}));Assert.That(b.NextState.Discard,Is.Empty);
            foreach(var step in b.Trace.Steps)if(step.Kind==SpellTraceKind.CardMoved)Assert.That(step.CardIndex,Is.EqualTo(0));
        }
        [Test] public void MissingFixedDefinitionRejectsCompilation()
        {Assert.That(SpellProgramCompiler.TryCompile(new[]{Emit()},new SpellProgramCompileInputs(1,alwaysCast:new SpellDefinition[]{null}),out _,out _),Is.False);}
    }
}
