using NUnit.Framework;

namespace Game.Skills.Tests
{
    public sealed class SpellProgramPreviewTests : SpellProgramTestSupport
    {
        [Test] public void PreviewContract_UsesAnIndependentReadonlySimulation()
        { Assert.That(typeof(CastEvaluator).Assembly.GetType("Game.Skills.SpellProgramPreview"),Is.Not.Null,"V2只读预览尚未实现。"); }

        [Test] public void RepeatedPreview_NeverAdvancesOriginalDeckOrRandom()
        {
            var p=Compile(Emit(),Emit()); var state=WandDrawSnapshot.CreateInitial(p,42);
            var input=new SpellExecutionInputs(80); var buffer=new SpellPreviewBuffer();
            for(int i=0;i<100;i++) Assert.That(SpellProgramPreview.TryPreview(p,state,input,new SpellPreviewOptions(2),buffer),Is.True);
            Assert.That(state.ExecutionRevision,Is.Zero); Assert.That(state.Deck,Is.EqualTo(new[]{0,1}));
            Assert.That(state.RandomState,Is.EqualTo(42u)); Assert.That(buffer.Roots.Count,Is.EqualTo(2));
            Assert.That(buffer.Roots[1].Graph.Outputs[0].SourceCardIndex,Is.EqualTo(1));
        }
        [Test] public void VirtualClock_RecoversManaOnlyBetweenRoots()
        {
            var a=Emit(cost:10);
            Assert.That(SpellProgramCompiler.TryCompile(new[]{a,a},new SpellProgramCompileInputs(1,
                baseCastDelay:.5f,manaRegenPerSecond:4),out var p,out _),Is.True);
            var b=new SpellPreviewBuffer();
            Assert.That(SpellProgramPreview.TryPreview(p,WandDrawSnapshot.CreateInitial(p),
                new SpellExecutionInputs(30),new SpellPreviewOptions(2),b),Is.True);
            Assert.That(b.Roots[0].Summary.ManaAfter,Is.EqualTo(20));
            Assert.That(b.Roots[1].ManaBefore,Is.EqualTo(22));
            Assert.That(b.Roots[1].SimulatedStartSeconds,Is.EqualTo(.5));
        }
        [Test] public void FromBeginning_IsExplicitlyDifferentFromActualNextRoot()
        {
            var p=Compile(Emit(10),Emit(20)); var state=new WandDrawSnapshot(new[]{1},new[]{0},p.Revision);
            var b=new SpellPreviewBuffer(); var input=new SpellExecutionInputs(100);
            Assert.That(SpellProgramPreview.TryPreview(p,state,input,new SpellPreviewOptions(1),b),Is.True);
            Assert.That(b.Roots[0].Graph.Outputs[0].Command.Damage,Is.EqualTo(20));
            Assert.That(SpellProgramPreview.TryPreview(p,state,input,new SpellPreviewOptions(1,true),b),Is.True);
            Assert.That(b.Roots[0].Graph.Outputs[0].Command.Damage,Is.EqualTo(10));
        }
        [Test] public void ExhaustedFiniteUses_StopWithoutLoopingEmptyState()
        {
            var p=Compile(Emit()); var b=new SpellPreviewBuffer();
            Assert.That(SpellProgramPreview.TryPreview(p,WandDrawSnapshot.CreateInitial(p),
                new SpellExecutionInputs(100,remainingUses:new[]{1}),new SpellPreviewOptions(32,false,false),b),Is.True);
            Assert.That(b.Roots.Count,Is.EqualTo(1)); Assert.That(b.Truncated,Is.False);
        }
        [Test] public void RootBudget_IsVisibleTruncationAndNeverChangesMachineBudget()
        {
            var p=Compile(Emit(),Emit(),Emit());var b=new SpellPreviewBuffer();
            Assert.That(SpellProgramPreview.TryPreview(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),
                new SpellPreviewOptions(2,false,false),b),Is.True);
            Assert.That(b.Roots.Count,Is.EqualTo(2)); Assert.That(b.Truncated,Is.True);
        }
        [TestCase(0)][TestCase(33)] public void InvalidPreviewBudget_IsRejected(int roots)
        {
            var p=Compile(Emit()); var b=new SpellPreviewBuffer();
            Assert.That(SpellProgramPreview.TryPreview(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),
                new SpellPreviewOptions(roots),b),Is.False);
            Assert.That(b.Diagnostic.Error,Is.EqualTo(SpellProgramError.InvalidInput));
        }
    }
}
