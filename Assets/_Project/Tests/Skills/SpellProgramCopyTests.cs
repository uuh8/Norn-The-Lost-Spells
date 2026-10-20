using NUnit.Framework;
namespace Game.Skills.Tests
{
    public sealed class SpellProgramCopyTests : SpellProgramTestSupport
    {
        // 使用已预留Opcode数值建立RED；随后与原Lua的动态pile选择探针对照。
        private SpellDefinition Copy(bool gamma=false)
        {var d=Make((SpellKind)4);d.DisplayName=gamma?"Gamma":"Alpha";d.ManaCost=4;var field=typeof(SpellDefinition).GetField("CopyMode");if(field!=null)field.SetValue(d,System.Enum.ToObject(field.FieldType,gamma?1:0));return d;}
        private SpellBuildBuffer Build(SpellProgram p,WandDrawSnapshot state,out SpellBuildSummary summary,int[] uses=null)
        {var b=new SpellBuildBuffer();Assert.That(new SpellProgramMachine().TryBuild(p,state,new SpellExecutionInputs(100,100,remainingUses:uses),b,out summary),Is.True,summary.Diagnostic.Message);return b;}
        [TestCase(false)][TestCase(true)] public void SelfCopyStopsAtLuaRecursiveLimitWithoutRejectingRoot(bool gamma)
        {var p=Compile(Copy(gamma));var b=Build(p,WandDrawSnapshot.CreateInitial(p),out var s);Assert.That(s.ManaAfter,Is.EqualTo(96));Assert.That(b.Graph.Outputs.Count,Is.Zero);Assert.That(b.Trace.Steps,Has.Some.Matches<SpellExecutionTraceStep>(e=>e.Kind.ToString()=="CopyRecursionRejected"));}
        [Test] public void GammaBorrowsLastDeckEmitWithoutMovingPayingOrConsumingIt()
        {var p=Compile(Copy(true),Emit(20,30));var b=Build(p,WandDrawSnapshot.CreateInitial(p),out var s,new[]{-1,2});Assert.That(s.ManaAfter,Is.EqualTo(96));Assert.That(b.RemainingUsesAfter[1],Is.EqualTo(2));Assert.That(b.NextState.Deck,Is.EqualTo(new[]{1}));Assert.That(b.Graph.Outputs[0].SourceCardIndex,Is.EqualTo(1));}
        [Test] public void AlphaPrefersDiscardOverItsOwnHandAndDoesNotRepayTarget()
        {var p=Compile(Emit(20,30),Copy());var first=Build(p,WandDrawSnapshot.CreateInitial(p),out _);var second=Build(p,first.NextState,out var s);Assert.That(s.ManaAfter,Is.EqualTo(96));Assert.That(second.Graph.Outputs[0].Command.Damage,Is.EqualTo(20));}
        [Test] public void BorrowedModifierStillPaysTheNewCardItDraws()
        {var p=Compile(Copy(true),Emit(20,30),Modify(5,9));var b=Build(p,WandDrawSnapshot.CreateInitial(p),out var s);Assert.That(s.ManaAfter,Is.EqualTo(66));Assert.That(b.Graph.Outputs[0].Command.Damage,Is.EqualTo(25));Assert.That(b.RemainingUsesAfter.Count,Is.EqualTo(3));}
        [Test] public void BorrowedNegativeManaDoesNotRefundAgain()
        {var p=Compile(Modify(0,-20),Copy());var a=Build(p,WandDrawSnapshot.CreateInitial(p),out _);var b=Build(p,a.NextState,out var s);Assert.That(s.ManaAfter,Is.EqualTo(96));Assert.That(b.Graph.Outputs.Count,Is.Zero);}
    }
}
