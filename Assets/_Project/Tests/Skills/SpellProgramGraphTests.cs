using System.Collections.Generic;
using NUnit.Framework;

namespace Game.Skills.Tests
{
    public sealed class SpellProgramGraphTests : SpellProgramTestSupport
    {
        [Test] public void NoitaEightCardGolden_ReadOrderAndIndependentScopes()
        {
            // 对照lua_detailed_results.jsonl/full_pipeline的8次action、两层各2输出；数值映射为Norn单位。
            var d=Modify(10);var t=Emit(trigger:PayloadTriggerMode.OnImpact);var f=Emit();var recharge=Modify(0);
            recharge.CastDelayDelta=-.1f;recharge.ReloadDelta=-.3f;
            var p=Compile(d,Multi(),t,Multi(),f,f,recharge,f);var b=new SpellBuildBuffer();
            Assert.That(new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out var summary),Is.True);
            var read=new List<int>();foreach(var s in b.Trace.Steps)if(s.Kind==SpellTraceKind.CardDrawn)read.Add(s.CardIndex);
            Assert.That(read,Is.EqualTo(new[]{0,1,2,3,4,5,6,7}));
            Assert.That(b.Graph.Scopes.Count,Is.EqualTo(2));Assert.That(b.Graph.Scopes[0].OutputIds.Count,Is.EqualTo(2));
            Assert.That(b.Graph.Scopes[1].OutputIds.Count,Is.EqualTo(2));
            foreach(var output in b.Graph.Outputs)Assert.That(output.Command.Damage,Is.EqualTo(output.ScopeId==0?20:10));
            Assert.That(summary.CastInterval,Is.EqualTo(.05f).Within(.0001));
            Assert.That(summary.ReloadInterval,Is.EqualTo(.4f).Within(.0001));
        }
        [Test] public void FrozenGraph_IgnoresDefinitionAndSourceOrderEdits()
        {
            var f=Emit();var d=Modify(6);var source=new[]{Multi(),f,d,f};
            Assert.That(SpellProgramCompiler.TryCompile(source,new SpellProgramCompileInputs(1),out var p,out _),Is.True);
            var b=new SpellBuildBuffer();new SpellProgramMachine().TryBuild(p,WandDrawSnapshot.CreateInitial(p),new SpellExecutionInputs(100),b,out _);
            f.BaseDamage=999;d.ModDamageAddFlat=999;source[0]=f;
            Assert.That(b.Graph.Outputs[0].Command.Damage,Is.EqualTo(16));
            Assert.That(b.Graph.Outputs[1].Parameters.Contributions[0].CardIndex,Is.EqualTo(2));
        }
    }
}
