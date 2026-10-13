using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class PracticeDeathSettlementTests : RaidTestFixture
    {
        [Test] public void KeepsEquippedBackpackButLosesItsContentsAndOtherCarriedGear()
        {
            Build();Profile.Currency=12345;
            Add(Profile.Items,"warehouse.wand","wand");Add(Profile.Items,"warehouse.spell","spell",7);
            Assert.That(Profile.Items.TryMove("warehouse.spell",new ItemLocation(ItemLocationKind.WandSlot,"warehouse.wand",0,0,0,0)).Success,Is.True);
            Assert.That(Profile.Items.TryMove("bottle",new ItemLocation(ItemLocationKind.EquippedTool,Profile.ActorId,0,0,0,0)).Success,Is.True);
            Profile.ToolBindings[0]="bottle";
            Add(Profile.Items,"carried.tray","spell",2);
            Assert.That(Profile.Items.TryMove("carried.tray",new ItemLocation(ItemLocationKind.SpellStorage,Profile.ActorId,0,0,0,0)).Success,Is.True);
            string before=UnityEngine.JsonUtility.ToJson(ProfileSaveMapper.ToDto(Profile,0));
            Assert.That(PracticeDeathSettlement.TryCreate(Profile,out var candidate),Is.True);
            Assert.That(UnityEngine.JsonUtility.ToJson(ProfileSaveMapper.ToDto(Profile,0)),Is.EqualTo(before),"构建候选不修改原档");
            Assert.That(candidate.Currency,Is.EqualTo(12345));
            foreach(var entry in candidate.Items)
                if(entry.Item.InstanceId!="bag")Assert.That(RaidProfileOps.IsCarried(candidate,entry.Item.InstanceId),Is.False);
            Assert.That(candidate.Items.TryGet("warehouse.wand",out _),Is.True);Assert.That(candidate.Items.TryGet("warehouse.spell",out _),Is.True);
            Assert.That(candidate.Items.TryGet("bottle",out _),Is.False);
            Assert.That(candidate.Items.TryGet("bag",out var bag),Is.True,"保留原背包，不免费补发同名背包");
            Assert.That(Profile.Items.TryGet("bag",out var original),Is.True);
            Assert.That(bag.InstanceId,Is.EqualTo(original.InstanceId));Assert.That(bag.ContentId,Is.EqualTo(original.ContentId));
            Assert.That(candidate.Items.TryGetLocation("bag",out var location),Is.True);
            Assert.That(location.Kind,Is.EqualTo(ItemLocationKind.EquippedBackpack));
            Assert.That(location.OwnerId,Is.EqualTo(Profile.ActorId));
            Assert.That(candidate.Items.TryGet("ordinary",out _),Is.False,"保留背包本体不递归保护内容");
            foreach(string id in candidate.ToolBindings)Assert.That(id,Is.Null);
        }
    }
}
