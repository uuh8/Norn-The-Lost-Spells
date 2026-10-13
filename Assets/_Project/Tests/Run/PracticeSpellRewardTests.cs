using System;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class PracticeSpellRewardTests:InventoryTestFixture
    {
        private string Grant(SinglePlayerProfile profile,SpellDefinition spell,bool expected)
        {
            var type=typeof(PracticeInventoryWorkspace).Assembly.GetType("Game.Run.PracticeSpellRewardOps");
            Assert.That(type,Is.Not.Null,"真实奖励需要写入库存而不是旧Library");
            var args=new object[]{profile,spell,null};
            Assert.That((bool)type.GetMethod("TryGrant").Invoke(null,args),Is.EqualTo(expected));return (string)args[2];
        }
        [Test] public void RewardsFillSpellStorageBeforeBackpackAndKeepUnlimitedPracticeSemantics()
        {
            var spell=Define("spell",ItemKind.Spell);var bag=Define("bag",ItemKind.Backpack,bagWidth:2,bagHeight:2);
            var store=Store(spell,bag);Assert.That(store.TryAdd(new RuntimeItemState("bag","bag",0),new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0)),Is.True);
            var profile=new SinglePlayerProfile("player",store);
            for(int i=0;i<12;i++)
            {string id=Grant(profile,spell.SpellDefinition,true);At(store,id,new ItemLocation(ItemLocationKind.SpellStorage,"player",0,0,0,i));Assert.That(store.TryGet(id,out var state)&&state.HasUnlimitedCharges,Is.True);}
            At(store,Grant(profile,spell.SpellDefinition,true),Bag());
        }
        [Test] public void FullCapacityAndUnknownSpellLeaveInventoryUnchanged()
        {
            var spell=Define("spell",ItemKind.Spell);var store=Store(spell);var profile=new SinglePlayerProfile("player",store);
            for(int i=0;i<12;i++)Grant(profile,spell.SpellDefinition,true);
            uint version=store.Version;Grant(profile,spell.SpellDefinition,false);
            Grant(profile,Track(ScriptableObject.CreateInstance<SpellDefinition>()),false);
            Assert.That(store.Version,Is.EqualTo(version));Assert.That(store.Count,Is.EqualTo(12));
        }
    }
}
