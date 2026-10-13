using Game.Core;
using NUnit.Framework;
namespace Game.Run.Tests
{
    public sealed class RaidSettlementServiceTests : RaidTestFixture
    {
        [Test] public void DeathKeepsOriginalEquippedBackpackAndRemapsHubActorButLosesContents()
        {
            Build();Depart();
            Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,
                new RaidSettlementRequest(Profile.PendingRaid.RaidId,RaidOutcome.Death),out var hub,out _).Success,Is.True);
            Assert.That(hub.Items.TryGet("bag",out _),Is.True,"正式远征死亡也保留装备背包");
            Assert.That(hub.Items.TryGetLocation("bag",out var location),Is.True);
            Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.EquippedBackpack));
            Assert.That(location.OwnerId,Is.EqualTo(hub.ActorId));
            Assert.That(hub.Items.TryGet("ordinary",out _),Is.False);
        }
        [TestCase(RaidOutcome.Success)][TestCase(RaidOutcome.Death)]
        public void MixedContainerReturnsOnlyOnSuccessWithExactComponents(RaidOutcome outcome)
        {
            Build();Depart();Profile.Items.TryGet("bottle",out var bottle);bottle.Composition.AddReserved(new[]{new MaterialAmount(1,7),new MaterialAmount(3,3)},2);
            Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,outcome),out var hub,out _).Success,Is.True);
            if(outcome==RaidOutcome.Success){Assert.That(hub.Items.TryGet("bottle",out var returned),Is.True);Assert.That(returned.Composition.GetQuantity(1),Is.EqualTo(7));Assert.That(returned.Composition.GetQuantity(3),Is.EqualTo(3));}
            else Assert.That(hub.Items.TryGet("bottle",out _),Is.False);
        }
        [Test]public void UnlimitedSpellStillNeedsItsOriginalProtectionToSurviveFailure()
        {
            Build();foreach(string id in new[]{"protected","ordinary"})
            {Profile.Items.TryGet(id,out var item);Profile.Items.TryGetLocation(id,out var at);Assert.That(ItemStackOps.TryChangeState(Profile.Items,new RuntimeItemState(id,item.ContentId,0,true),at,Profile.Items.Version).Success,Is.True);}
            Depart();Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,RaidOutcome.Death),out var hub,out _).Success,Is.True);
            Assert.That(hub.Items.TryGet("protected",out var card),Is.True);Assert.That(card.HasUnlimitedCharges,Is.True);Assert.That(hub.Items.TryGet("ordinary",out _),Is.False);
        }
        [Test]public void RecoveredOriginalProtectionRetainsItsActualRemainingCount()
        {
            Build();Depart();SetCharges("protected",2);Assert.That(InventoryMoveOps.TryDrop(Profile.Items,"protected","world",Profile.Items.Version).Success,Is.True);
            Assert.That(Profile.Items.TryMove("protected",Bag(3,0)).Success,Is.True);
            Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,RaidOutcome.Death),out var hub,out _).Success,Is.True);
            Assert.That(hub.Items.TryGet("protected",out var card),Is.True);Assert.That(card.RemainingCharges,Is.EqualTo(2));
        }
        [Test]public void LastProtectedQualificationWorksThisRaidButDoesNotReturnAfterSuccess()
        {
            Build();Assert.That(Profile.Items.TryMove("protected",Bag(3,0)).Success,Is.True);
            Assert.That(Profile.Items.TryMove("qualification",new ItemLocation(ItemLocationKind.WandSlot,"wand",0,0,0,0)).Success,Is.True);
            Protection.Clear();Assert.That(Protection.Select(Profile,"qualification",true),Is.True);Depart();
            var eligibility=new RaidSpellEligibility(Profile);eligibility.Resume(Profile.PendingRaid);
            Assert.That(eligibility.IsEligible("qualification"),Is.True);Profile.Items.TryGet("qualification",out var used);Assert.That(used.RemainingCharges,Is.Zero);
            Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,RaidOutcome.Success),out var hub,out _).Success,Is.True);
            Assert.That(hub.Items.TryGet("qualification",out _),Is.False);Assert.That(hub.Items.TryGet("protected",out _),Is.True);
        }
        [TestCase(RaidOutcome.Death)][TestCase(RaidOutcome.Abandoned)][TestCase(RaidOutcome.Closed)][TestCase(RaidOutcome.Crash)]
        public void FailureLosesOrdinaryButKeepsSpecificProtectedRemaining(RaidOutcome outcome)
        { Build(); Depart(); SetCharges("protected",2); var result=new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,outcome),out var hub,out var receipt);
            Assert.That(result.Success,Is.True); Assert.That(hub.Items.Count,Is.EqualTo(3)); hub.Items.TryGet("protected",out var card); Assert.That(card.RemainingCharges,Is.EqualTo(2));
            Assert.That(hub.Items.TryGet("ordinary",out _),Is.False); Assert.That(hub.Items.TryGet("bag",out _),Is.True);
            Assert.That(receipt.Returned,Is.EqualTo(2),"保护原法术+保留背包本体"); }
        [Test] public void ExhaustedProtectedIsNotRestored()
        { Build(1); Depart(); SetCharges("protected",0); Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,RaidOutcome.Death),out var hub,out _).Success,Is.True); Assert.That(hub.Items.TryGet("protected",out _),Is.False); }
        [Test] public void DroppedProtectionOnlyReturnsWhenSameInstancePickedBack()
        { Build(); Depart(); InventoryMoveOps.TryDrop(Profile.Items,"protected","world",Profile.Items.Version);
            Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,RaidOutcome.Death),out var hub,out _).Success,Is.True); Assert.That(hub.Items.TryGet("protected",out _),Is.False); }
        [Test] public void SuccessfulSettlementKeepsActualCarriedAndDropsWorldLoot()
        { Build(); Depart(); InventoryMoveOps.TryDrop(Profile.Items,"ordinary","world",Profile.Items.Version); SetCharges("protected",1);
            Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,RaidOutcome.Success),out var hub,out _).Success,Is.True);
            Assert.That(hub.ActorId,Is.EqualTo("player")); Assert.That(hub.Items.Count,Is.EqualTo(6)); Assert.That(hub.Items.TryGet("ordinary",out _),Is.False); }
        [Test] public void RepeatedSettlementReturnsReceiptWithoutAnotherSave()
        { Build(); Depart(); string id=Profile.PendingRaid.RaidId; var service=new RaidSettlementService(); service.TrySettle(Profile,Repository,new RaidSettlementRequest(id,RaidOutcome.Success),out var hub,out var receipt);
            long generation=hub.SaveGeneration; Assert.That(service.TrySettle(hub,Repository,new RaidSettlementRequest(id,RaidOutcome.Death),out var again,out var r).Success,Is.True);
            Assert.That(again,Is.SameAs(hub)); Assert.That(r.Outcome,Is.EqualTo(RaidOutcome.Success)); Assert.That(again.SaveGeneration,Is.EqualTo(generation)); }
        [Test] public void FinalReplaceFailureKeepsPendingAndCanRetry()
        { Build(); Depart(); string id=Profile.PendingRaid.RaidId; Files.FailReplace=true; var service=new RaidSettlementService();
            Assert.That(service.TrySettle(Profile,Repository,new RaidSettlementRequest(id,RaidOutcome.Success),out var next,out var receipt).Success,Is.False); Assert.That(next,Is.Null); Assert.That(receipt,Is.Null); Assert.That(Profile.Items.Count,Is.EqualTo(7));
            Files.FailReplace=false; Assert.That(service.TrySettle(Profile,Repository,new RaidSettlementRequest(id,RaidOutcome.Success),out next,out _).Success,Is.True); }
    }
}
