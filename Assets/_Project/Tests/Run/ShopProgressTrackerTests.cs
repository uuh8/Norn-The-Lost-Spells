using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class ShopProgressTrackerTests : RaidTestFixture
    {
        private ShopConfiguration Configure(int threshold=1)
        {
            var definition=Track(ScriptableObject.CreateInstance<ShopDefinition>());definition.RefreshEveryProgress=threshold;definition.PurchaseBudget=40;
            definition.Offers=new[]{new ShopOffer{OfferId="card",ContentId="spell",Stock=2}};
            Assert.That(definition.TryFreeze(Profile.Items.Catalog,out var config),Is.True);
            Assert.That(ShopProgressTracker.TryPrepare(Profile,config,out var next,out var changed),Is.True);Assert.That(changed,Is.True);
            Assert.That(Repository.TrySave(next).Success,Is.True);Profile=next;return config;
        }
        [Test]public void ReopeningAndSavingDoesNotRefillConsumedStockOrBudget()
        { Build();var config=Configure();Profile.Shop.Stocks[0].Remaining=0;Profile.Shop.RemainingBudget=1;
            Assert.That(Repository.TrySave(Profile).Success,Is.True);Assert.That(Repository.TryLoad(out Profile).Success,Is.True);
            Assert.That(ShopProgressTracker.TryPrepare(Profile,config,out var next,out var changed),Is.True);Assert.That(changed,Is.False);Assert.That(next.Shop.Stocks[0].Remaining,Is.Zero);Assert.That(next.Shop.RemainingBudget,Is.EqualTo(1)); }
        [Test]public void ProgressThresholdRefreshesOnceAndPreservesRemainder()
        { Build();var config=Configure(2);Profile.AdventureProgress=3;Profile.Shop.Stocks[0].Remaining=0;
            Assert.That(ShopProgressTracker.TryPrepare(Profile,config,out var next,out var changed),Is.True);Assert.That(changed,Is.True);
            Assert.That(next.Shop.Stocks[0].Remaining,Is.EqualTo(2));Assert.That(next.Shop.AppliedProgress,Is.EqualTo(2));Profile=next;
            Assert.That(ShopProgressTracker.TryPrepare(Profile,config,out _,out changed),Is.True);Assert.That(changed,Is.False); }
        [Test]public void DepartureRollbackAndFailurePreserveWalletAndShop()
        { Build();Configure();Profile.Currency=83;Depart(false);Assert.That(Profile.Currency,Is.EqualTo(83));Assert.That(Profile.Shop,Is.Not.Null);
            Assert.That(new RaidPreparationService().TryRollback(Profile,Repository,out var hub).Success,Is.True);Assert.That(hub.Currency,Is.EqualTo(83));Assert.That(hub.Shop.RemainingBudget,Is.EqualTo(40)); }
        [Test]public void SchemaFourMigratesWithoutGivingFreeMoneyOrShopReset()
        { Build();var dto=ProfileSaveMapper.ToDto(Profile,0);dto.SchemaVersion=4;
            Assert.That(ProfileSaveMapper.TryRestore(dto,Profile.Items.Catalog,out var restored).Success,Is.True);Assert.That(restored.Currency,Is.Zero);Assert.That(restored.Shop,Is.Null); }
        [Test]public void CorruptNegativeBudgetAndDuplicateOfferAreRejected()
        { Build();Configure();var dto=ProfileSaveMapper.ToDto(Profile,Profile.SaveGeneration);dto.Shop.RemainingBudget=-1;
            Assert.That(ProfileSaveMapper.TryRestore(dto,Profile.Items.Catalog,out _).Success,Is.False); }
        [TestCase(10UL,0)][TestCase(0UL,1)]public void SuccessfulRealProgressIsPersistedOnceWithSettlement(ulong collected,int defeated)
        {
            Build();Configure();Depart();string id=Profile.PendingRaid.RaidId;var service=new RaidSettlementService();
            Assert.That(service.TrySettle(Profile,Repository,new RaidSettlementRequest(id,RaidOutcome.Success,collected,defeated),out var hub,out _).Success,Is.True);
            Assert.That(hub.AdventureProgress,Is.EqualTo(1));Assert.That(hub.Shop,Is.Not.Null);
            Assert.That(service.TrySettle(hub,Repository,new RaidSettlementRequest(id,RaidOutcome.Success,collected,defeated),out var again,out _).Success,Is.True);
            Assert.That(again.AdventureProgress,Is.EqualTo(1));Assert.That(Repository.TryLoad(out var loaded).Success,Is.True);Assert.That(loaded.AdventureProgress,Is.EqualTo(1));
        }
        [TestCase(RaidOutcome.Death,10UL)][TestCase(RaidOutcome.Success,0UL)]public void FailedOrEmptyRunDoesNotCreateEconomicProgress(RaidOutcome outcome,ulong collected)
        {Build();Configure();Depart();Assert.That(new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,outcome,collected),out var hub,out _).Success,Is.True);Assert.That(hub.AdventureProgress,Is.Zero);}
    }
}
