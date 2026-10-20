using NUnit.Framework;
using Game.Combat;
namespace Game.Skills.Tests
{
    public sealed class SpellLifetimeV2Tests : SpellProgramTestSupport
    {
        [TestCase(1,2.5f)] [TestCase(2,1.25f)] [TestCase(3,.625f)]
        public void HalfLifeModifiersMultiplyAndFreezeInsteadOfSubtractingTowardZero(int count,float expected)
        {
            var field=typeof(SpellDefinition).GetField("ModLifetimeMul");Assert.That(field,Is.Not.Null);
            var cards=new SpellDefinition[count+1];
            for(int i=0;i<count;i++){cards[i]=Modify(0);field.SetValue(cards[i],.5f);}
            cards[count]=Emit();cards[count].LifetimeSeconds=5;
            var p=Compile(cards);foreach(var card in cards)field.SetValue(card,1f);
            var b=new SpellBuildBuffer();Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out _),Is.True);
            Assert.That(b.Graph.Outputs[0].Command.LifetimeSeconds,Is.EqualTo(expected).Within(.0001f));
        }

        [Test] public void HalfLifeInsideTriggerPayloadDoesNotShortenItsParentOrRootSibling()
        {
            var parent=Emit(trigger:PayloadTriggerMode.OnTermination);parent.LifetimeSeconds=5;
            var half=Modify(0);half.ModLifetimeMul=.5f;
            var child=Emit();child.LifetimeSeconds=5;var sibling=Emit();sibling.LifetimeSeconds=5;
            var p=Compile(Multi(),parent,half,child,sibling);var b=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out _),Is.True);
            Assert.That(b.Graph.Outputs[0].Command.LifetimeSeconds,Is.EqualTo(5));
            Assert.That(b.Graph.Outputs[1].Command.LifetimeSeconds,Is.EqualTo(2.5f));
            Assert.That(b.Graph.Outputs[2].Command.LifetimeSeconds,Is.EqualTo(5));
        }

        [TestCase(-1f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void InvalidLifetimeMultiplierIsRejectedByCompiler(float multiplier)
        {
            var half=Modify(0);half.ModLifetimeMul=multiplier;
            Assert.That(SpellProgramCompiler.TryCompile(new[]{half,Emit()},new SpellProgramCompileInputs(1),out _,out _),Is.False);
        }

        [TestCase(false,.68f)] [TestCase(true,1f)]
        public void CastFasterChangesCastDelayWhileLongerReloadStillLimitsTheCycle(bool single,float expectedReady)
        {
            var modifier=Modify(0);modifier.CastDelayDelta=-.12f;
            var cards=single?new[]{modifier,Emit()}:new[]{modifier,Emit(),Emit()};
            Assert.That(SpellProgramCompiler.TryCompile(cards,new SpellProgramCompileInputs(1,baseCastDelay:.8f,baseReloadTime:1),out var p,out _),Is.True);
            var b=new SpellBuildBuffer();Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out var summary),Is.True);
            Assert.That(summary.CastInterval,Is.EqualTo(.68f).Within(.0001f));
            Assert.That(summary.NeedsReload?System.Math.Max(summary.CastInterval,summary.ReloadInterval):summary.CastInterval,Is.EqualTo(expectedReady).Within(.0001f));
        }

        [Test] public void TerminationIsACompiledTriggerWithGenericCombatReason()
        {var p=Compile(Emit(trigger:(PayloadTriggerMode)3),Emit());Assert.That(p.Count,Is.EqualTo(2));Assert.That(typeof(ProjectileBase).GetEvent("Terminated"),Is.Not.Null);}
        [Test] public void LifetimeIsFrozenAndSupportsLateScopeModifier()
        {
            var d=Emit();var m=Modify(0);var lifetime=typeof(SpellDefinition).GetField("LifetimeSeconds");var delta=typeof(SpellDefinition).GetField("ModLifetimeAddSeconds");
            Assert.That(lifetime,Is.Not.Null);Assert.That(delta,Is.Not.Null);lifetime.SetValue(d,2f);delta.SetValue(m,-1.5f);
            var p=Compile(Multi(),d,m,Emit());lifetime.SetValue(d,20f);var b=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out _),Is.True);
            Assert.That(typeof(EmitCommand).GetField("LifetimeSeconds").GetValue(b.Graph.Outputs[0].Command),Is.EqualTo(.5f));
        }
    }
}
