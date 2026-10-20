using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class WandExecutionObservationTests : WandExecutionTransactionTestFixture
    {
        [Test] public void ObservationRequiresMatchingOwnerLayerAndUnreleasedOutput()
        {
            Build();var root=Request(CastResourceOperation.PrepareRoot);Request(CastResourceOperation.Complete,root.TicketId);
            _casts.Archive.TryGetLatest("wand",out var replay);
            var observation=new SpellExecutionObservedEvent{InventoryId="scope",SourceWandId="wand",CallerId="other",TicketId=root.TicketId,
                ProgramRevision=1,LayerId=0,OutputId=0,SimulationTime=10,Kind=SpellExecutionObservationKind.OutputReleased};
            Assert.That(_casts.Observe(observation),Is.False);Assert.That(replay.WorldStatus,Is.EqualTo(WandWorldExecutionStatus.NotObserved));
            observation.CallerId="test";observation.OutputId=1;Assert.That(_casts.Observe(observation),Is.False);
            observation.OutputId=0;Assert.That(_casts.Observe(observation),Is.True);Assert.That(_casts.Observe(observation),Is.False);
            Assert.That(replay.WorldStatus,Is.EqualTo(WandWorldExecutionStatus.Released));
        }
        [Test] public void DelayedOutputObservationSurvivesDiagnosticEviction()
        {
            Build(trigger:false,cost:0,root:20);string first=null;
            for(int i=0;i<4;i++)
            {
                var prepare=NewRequest(CastResourceOperation.PrepareRoot);prepare.UseV2=true;var root=_casts.Execute(prepare);
                if(i==0)first=root.TicketId;Request(CastResourceOperation.Complete,root.TicketId);_wands.Tick(1);
            }
            Assert.That(_casts.Archive.Count,Is.EqualTo(3));Assert.That(_casts.TryGetGraph(first,out _),Is.True);
            Assert.That(_casts.Observe(new SpellExecutionObservedEvent{InventoryId="scope",SourceWandId="wand",CallerId="test",TicketId=first,
                ProgramRevision=1,LayerId=0,OutputId=0,SimulationTime=10,Kind=SpellExecutionObservationKind.OutputReleased}),Is.True);
            Assert.That(_casts.TryGetGraph(first,out _),Is.False);
        }
    }
}
