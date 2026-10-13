using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class StarterKitServiceTests : RaidTestFixture
    {
        [Test] public void UsableFiniteCardCannotReplaceUnlimitedStarterSpell()
        {
            Build(); Depart(); new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,Game.Core.RaidOutcome.Death),out var hub,out _);
            var kit=Track(ScriptableObject.CreateInstance<StarterKitDefinition>());
            kit.Entries=new[]{new StarterKitDefinition.Entry{ContentId="wand"},new StarterKitDefinition.Entry{ContentId="bag"},new StarterKitDefinition.Entry{ContentId="spell",Unlimited=true}};
            Assert.That(new StarterKitService().TrySupply(hub,Repository,kit,out var supplied).Success,Is.True);
            bool unlimitedInstalled=false;
            foreach(var e in supplied.Items) if(e.Location.Kind==ItemLocationKind.WandSlot && e.Item.ContentId=="spell" && e.Item.HasUnlimitedCharges)unlimitedInstalled=true;
            Assert.That(unlimitedInstalled,Is.True,"有限保护卡保留，但免费基础卡必须无限且能直接施法。");
        }
        [Test] public void MissingOnlyKitIsFreePersistentAndDoesNotDuplicate()
        { Build(); Depart(); new RaidSettlementService().TrySettle(Profile,Repository,new RaidSettlementRequest(Profile.PendingRaid.RaidId,Game.Core.RaidOutcome.Death),out var hub,out _);
            var kit=Track(ScriptableObject.CreateInstance<StarterKitDefinition>()); kit.Entries=new[]{new StarterKitDefinition.Entry{ContentId="wand"},new StarterKitDefinition.Entry{ContentId="bag"},new StarterKitDefinition.Entry{ContentId="bottle"}};
            var service=new StarterKitService(); Assert.That(service.TrySupply(hub,Repository,kit,out var supplied).Success,Is.True); int count=supplied.Items.Count;
            Assert.That(service.TrySupply(supplied,Repository,kit,out var again).Success,Is.True); Assert.That(again.Items.Count,Is.EqualTo(count));
            foreach(var entry in again.Items)
                if(entry.Item.ContentId=="wand"||entry.Item.ContentId=="bottle")Assert.That(entry.Item.IsStarterEquipment,Is.True);
            Assert.That(again.Items.TryGet("bag",out var retained),Is.True,"已有背包保留原身份，不补发免费背包");
            Assert.That(retained.IsStarterEquipment,Is.False); }
    }
}
