using System;
using NUnit.Framework;
using Game.Core;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class DungeonRandomizationTests:InventoryTestFixture
    {
        [TestCase("Game.Run.DungeonRewardProfile")]
        [TestCase("Game.Run.DungeonWandRollProfile")]
        public void ArtistCanConfigureRandomRanges(string type)
        {Assert.That(typeof(RuntimeItemState).Assembly.GetType(type),Is.Not.Null);}
        [Test]public void WandRollIsInstanceDataAndSaveHasPresenceBit()
        {Assert.That(typeof(RuntimeItemState).GetProperty("RolledWandStats"),Is.Not.Null);Assert.That(typeof(SinglePlayerItemDto).GetField("HasRolledWandStats"),Is.Not.Null);}
        [Test]public void RewardsSelectThreeDistinctSpellsAndFiniteInclusiveCharges()
        {
            var config=Track(ScriptableObject.CreateInstance<DungeonRewardProfile>());
            config.Pool=new[]{Define("a",ItemKind.Spell).SpellDefinition,Define("b",ItemKind.Spell).SpellDefinition,Define("c",ItemKind.Spell).SpellDefinition,Define("d",ItemKind.Spell).SpellDefinition};
            var previous=UnityEngine.Random.state;
            try
            {UnityEngine.Random.InitState(174);bool low=false,high=false;
                for(int i=0;i<200;i++){var roll=config.Roll(3);Assert.That(roll.Length,Is.EqualTo(3));
                    Assert.That(roll[0].Spell,Is.Not.SameAs(roll[1].Spell));Assert.That(roll[0].Spell,Is.Not.SameAs(roll[2].Spell));Assert.That(roll[1].Spell,Is.Not.SameAs(roll[2].Spell));
                    foreach(var reward in roll){Assert.That(reward.Charges,Is.InRange(20,40));low|=reward.Charges==20;high|=reward.Charges==40;}}
                Assert.That(low&&high,Is.True);config.Pool[1]=config.Pool[0];Assert.That(config.IsValid,Is.False);}
            finally{UnityEngine.Random.state=previous;}
        }
        [Test]public void WandRangeRollHasVariationAndDoesNotTakeModelAsInput()
        {
            var config=Track(ScriptableObject.CreateInstance<DungeonWandRollProfile>());var previous=UnityEngine.Random.state;
            try
            {UnityEngine.Random.InitState(555);var first=config.Roll();bool varied=false;
                for(int i=0;i<50;i++){var stats=config.Roll();Assert.That(stats.IsValid,Is.True);Assert.That(stats.MaxMana,Is.InRange(80,240));
                    Assert.That(stats.ManaRegenPerSecond,Is.InRange(8,30));Assert.That(stats.CastCooldown,Is.InRange(.15f,1));
                    Assert.That(stats.ProgramCapacity,Is.InRange(3,8));Assert.That(stats.BaseDraws,Is.InRange(1,3));varied|=!first.SameAs(stats);}
                Assert.That(varied,Is.True);config.Mana=new Vector2(float.NaN,240);Assert.That(config.IsValid,Is.False);}
            finally{UnityEngine.Random.state=previous;}
        }
        [Test]public void InstanceCapacityResourcesAndDtoPersistAcrossModelsAndMoves()
        {
            var store=Store(Define("Item_Wand_StarterA",ItemKind.Wand),Define("Item_Wand_StarterB",ItemKind.Wand),Define("spell",ItemKind.Spell));
            var stats=new WandStats(222,17,.42f,6,2);
            Assert.That(store.TryAdd(new RuntimeItemState("w1","Item_Wand_StarterA",0,rolledWandStats:stats),new ItemLocation(ItemLocationKind.EquippedWand,"player",0,0,0,0)),Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("w2","Item_Wand_StarterB",0,rolledWandStats:stats),ItemLocation.Warehouse),Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("s","spell",25),new ItemLocation(ItemLocationKind.WandSlot,"w1",0,0,0,5)),Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("overflow","spell",25),new ItemLocation(ItemLocationKind.WandSlot,"w1",0,0,0,6)),Is.False);
            var profile=new SinglePlayerProfile("player",store);var service=new WandLoadoutService(profile,"test");
            Assert.That(service.ReadOnly.TryGetState("w1",out var state),Is.True);Assert.That(state.MaxMana,Is.EqualTo(222));
            Assert.That(service.ReadOnly.TryGetProgram("w1",out var program),Is.True);Assert.That(program.BaseDraws,Is.EqualTo(2));
            var spent=service.Execute(new WandResourceRequestedEvent{InventoryId="test",SourceWandId="w1",CallerId="test",RequestId=1,ProgramRevision=program.ProgramRevision,ManaCost=20,IsRootCast=true});
            Assert.That(spent.Success,Is.True);Assert.That(spent.CurrentMana,Is.EqualTo(202));Assert.That(spent.CooldownRemaining,Is.EqualTo(.42f));
            var dto=JsonUtility.FromJson<SinglePlayerSaveDto>(JsonUtility.ToJson(ProfileSaveMapper.ToDto(profile,1)));
            Assert.That(ProfileSaveMapper.TryRestore(dto,store.Catalog,out var restored).Success,Is.True);
            restored.Items.TryGet("w1",out var item);Assert.That(item.RolledWandStats.Value.SameAs(stats),Is.True);
            Assert.That(restored.Items.TryMove("w1",ItemLocation.Warehouse).Success,Is.True);restored.Items.TryGet("w1",out item);Assert.That(item.RolledWandStats.Value.SameAs(stats),Is.True);
            Assert.That(restored.Items.TryApply(new[]{new InventoryEntry(new RuntimeItemState("w1","Item_Wand_StarterA",0),ItemLocation.Warehouse)},restored.Items.Version).Success,Is.False);
            dto.SchemaVersion=12;Assert.That(ProfileSaveMapper.TryRestore(dto,store.Catalog,out _).Success,Is.False);
            dto.SchemaVersion=13;dto.Items[0].WandMaxMana=float.PositiveInfinity;Assert.That(ProfileSaveMapper.TryRestore(dto,store.Catalog,out _).Success,Is.False);
        }
    }
}
