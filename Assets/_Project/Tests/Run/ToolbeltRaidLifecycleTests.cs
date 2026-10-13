using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class ToolbeltRaidLifecycleTests : RaidTestFixture
    {
        [TestCase(RaidOutcome.Success)][TestCase(RaidOutcome.Death)]
        public void ActualSettlementKeepsOnlyReferencesToSurvivingCarriedAssets(RaidOutcome outcome)
        {
            Build();var tools=new ToolbeltService(Profile,"hub");Assert.That(tools.TryBind(2,"bottle",tools.ConfigurationVersion),Is.True);
            Depart();tools=new ToolbeltService(Profile,"raid");Assert.That(tools.TryGetBinding(2,out var id),Is.True);Assert.That(id,Is.EqualTo("bottle"));
            Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,outcome),out var hub,out _).Success,Is.True);
            tools=new ToolbeltService(hub,"returned");Assert.That(tools.TryGetBinding(2,out _),Is.EqualTo(outcome==RaidOutcome.Success));
            Assert.That(hub.Items.TryGet("bottle",out _),Is.EqualTo(outcome==RaidOutcome.Success));
            Assert.That(hub.Currency,Is.EqualTo(Profile.Currency));
        }
        [Test] public void FailedSettlementWriteLeavesOriginalShortcutsAndAssetsForRetry()
        {
            Build();var tools=new ToolbeltService(Profile,"hub");tools.TryBind(0,"bottle",tools.ConfigurationVersion);Depart();
            Files.FailReplace=true;
            Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,RaidOutcome.Success),out var hub,out _).Success,Is.False);
            Assert.That(hub,Is.Null);Assert.That(new ToolbeltService(Profile,"retry").TryGetBinding(0,out _),Is.True);
            Assert.That(Profile.Items.TryGet("bottle",out _),Is.True);
        }
    }
}
