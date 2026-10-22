using Game.Core;
using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class ExtractionProgressTests : InventoryTestFixture
    {
        [Test] public void ConditionsBlockThenLeavingAndCancelResetAndOnlyFinishOnce()
        { var d=Track(ScriptableObject.CreateInstance<ExtractionConditionDefinition>());d.RequiredCollectedGmu=3; var c=new ExtractionConditionTracker(d);var p=new ExtractionProgress(c,2);
            p.SetInside(true);Assert.That(p.Tick(10),Is.False);c.AddCollected(3);Assert.That(p.Start(),Is.True);p.Tick(1);Assert.That(p.Remaining,Is.EqualTo(1));p.SetInside(false);Assert.That(p.Remaining,Is.EqualTo(2));
            p.SetInside(true);Assert.That(p.Start(),Is.True);p.Cancel();Assert.That(p.Tick(10),Is.False);p.SetInside(false);p.SetInside(true);Assert.That(p.Start(),Is.True);Assert.That(p.Tick(2),Is.True);Assert.That(p.Tick(2),Is.False); }
        [Test]public void ReadyExitWaitsForPlayerAndCancelNeedsAnotherExplicitStart()
        {
            var c=new ExtractionConditionTracker(null);var p=new ExtractionProgress(c,3);p.SetInside(true);
            Assert.That(p.Tick(100),Is.False);Assert.That(p.Running,Is.False);Assert.That(p.Start(),Is.True);
            p.Tick(1);p.Cancel();Assert.That(p.Tick(100),Is.False);Assert.That(p.Start(),Is.True);Assert.That(p.Remaining,Is.EqualTo(3));
        }
        [Test] public void ExplicitObjectiveAndDeathWinsSameFrame()
        { var d=Track(ScriptableObject.CreateInstance<ExtractionConditionDefinition>());d.RequiredCollectedGmu=0;d.RequiredObjectiveKey="switch";var c=new ExtractionConditionTracker(d);
            Assert.That(c.Complete("wrong"),Is.False);Assert.That(c.Ready,Is.False);c.Complete("switch");Assert.That(c.Ready,Is.True);
            var state=new RaidState();state.RequestOutcome(RaidOutcome.Success);state.RequestOutcome(RaidOutcome.Death);Assert.That(state.TryResolve(out var outcome),Is.True);Assert.That(outcome,Is.EqualTo(RaidOutcome.Death)); }
    }
}
