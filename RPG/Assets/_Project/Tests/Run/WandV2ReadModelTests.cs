using Game.UI;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class WandV2ReadModelTests : WandExecutionTransactionTestFixture
    {
        [Test] public void OwnedWorkbenchBindsCurrentStateInsteadOfSandboxSample()
        {
            Build(extra: 1); var state = State();
            Assert.That(typeof(WandProgramWorkbenchPresenter).GetMethod("InitializeOwned"), Is.Not.Null);
            Assert.That(typeof(WandProgramWorkbenchPresenter).GetMethod("BindOwned"), Is.Not.Null);
            Assert.That(typeof(SinglePlayerInventorySession).GetMethod("TryCaptureWandResearch"), Is.Not.Null);
            Assert.That(state.Draw.ExecutionRevision, Is.Zero);
        }
        [Test] public void ResearchHasSeparateControllerRatherThanReusingBackpackCanvas()
        {
            Assert.That(typeof(WandProgramWorkbenchPresenter).Assembly.GetType("Game.UI.WandResearchController"), Is.Not.Null);
        }
        [Test] public void PeacefulQualificationRequiresPositiveRemainingUsesAndDoesNotGrantRaidEligibility()
        {
            Build();var finite=new RuntimeItemState("a","emit",1);var exhausted=new RuntimeItemState("a","emit",0);
            Assert.That(_raid.CanExecuteQualification(finite,false),Is.False);
            Assert.That(_raid.CanExecuteQualification(finite,true),Is.True);
            Assert.That(_raid.CanExecuteQualification(exhausted,true),Is.False);
            Assert.That(_raid.IsEligible("a"),Is.False,"试验检查不能伪造下次远征资格。");
            Assert.That(finite.RemainingCharges,Is.EqualTo(1));
        }
        [Test] public void ActualRaidStillRequiresItsOwnQualificationEvenWhenSceneAllowsExperiments()
        {
            Build();Assert.That(_raid.TryBegin("real.raid",out _),Is.True);
            Assert.That(_raid.CanExecuteQualification(new RuntimeItemState("not-qualified","emit",8),true),Is.False);
        }
        [Test]public void ExhaustedUncarriedQualificationDoesNotBlockPractice()
        {
            Build(root:0,trigger:false);_store.Catalog.TryGet("emit",out var definition);definition.SpellDefinition.Kind=Game.Skills.SpellKind.Multicast;
            Assert.That(RaidSpellEligibility.CanStartPractice(_profile),Is.False);
            Assert.That(_store.TryMove("a",ItemLocation.Warehouse).Success,Is.True);
            Assert.That(RaidSpellEligibility.CanStartPractice(_profile),Is.True);
        }
    }
}
