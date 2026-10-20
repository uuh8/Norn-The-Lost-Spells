using NUnit.Framework;

namespace Game.Skills.Tests
{
    public sealed class SpellExecutionTraceContractTests : SpellProgramTestSupport
    {
        [Test] public void CandidateMana_ChangesAtPaymentEventRatherThanAnUnrelatedMovement()
        {
            var p=Compile(Emit(cost:5));var b=new SpellBuildBuffer();
            new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(80),b,out _);
            float previous=80;
            foreach(var s in b.Trace.Steps)
            {
                if(s.ManaAfter!=previous)Assert.That(s.Kind,Is.EqualTo(SpellTraceKind.ManaPaid),"单步回放不能在支付事件之前偷偷改变Mana。");
                previous=s.ManaAfter;
            }
        }
        [Test] public void ManaPaid_RecordsFiniteUseBeforeAndAfterForUi()
        {
            var before=typeof(SpellExecutionTraceStep).GetField("UsesBefore");
            var after=typeof(SpellExecutionTraceStep).GetField("UsesAfter");
            Assert.That(before,Is.Not.Null,"Trace缺少次数变化，UI不应从现有库存猜历史次数。");Assert.That(after,Is.Not.Null);
            var p=Compile(Emit());var b=new SpellBuildBuffer();
            new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100,remainingUses:new[]{2}),b,out _);
            foreach(var s in b.Trace.Steps)if(s.Kind==SpellTraceKind.ManaPaid)
            {Assert.That(before.GetValue(s),Is.EqualTo(2));Assert.That(after.GetValue(s),Is.EqualTo(1));return;}
            Assert.Fail("没有ManaPaid事件。");
        }
        [Test] public void DrawRequest_IdentifiesTheInstructionThatRequestedIt()
        {
            var p=Compile(Multi(),Emit(),Emit());var b=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out _),Is.True);
            Assert.That(b.Trace.Steps,Has.Some.Matches<SpellExecutionTraceStep>(s=>s.Kind==SpellTraceKind.DrawRequested && s.CardIndex==0 && s.ParentRequestId>=0));
        }

        [Test] public void EveryEventHasMonotonicIdentity_AndScopeClosureFollowsItsOutputs()
        {
            var p=Compile(Multi(),Emit(),Modify(),Emit());var b=new SpellBuildBuffer();
            new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out _);
            for(int i=0;i<b.Trace.Steps.Count;i++) Assert.That(b.Trace.Steps[i].StepId,Is.EqualTo(i));
            foreach(var output in b.Graph.Outputs) Assert.That(b.Graph.Scopes[output.ScopeId].ClosedAtStep,Is.GreaterThan(output.RegisteredAtStep));
            Assert.That(b.Trace.Truncated,Is.False);
        }

        [Test] public void ReusingBuffer_DoesNotMutateEarlierGraphOrTrace()
        {
            var d=Emit(10);var p=Compile(d,Emit(20));var b=new SpellBuildBuffer();var machine=new SpellProgramMachine();
            machine.TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out _);
            var oldGraph=b.Graph;var oldTrace=b.Trace;int oldCount=oldTrace.Steps.Count;
            machine.TryBuild(p,b.NextState,new SpellExecutionInputs(100),b,out _); d.BaseDamage=999;
            Assert.That(oldGraph.Outputs[0].Command.Damage,Is.EqualTo(10));Assert.That(oldTrace.Steps.Count,Is.EqualTo(oldCount));
            Assert.That(b.Graph.Outputs[0].Command.Damage,Is.EqualTo(20));
        }
    }
}
