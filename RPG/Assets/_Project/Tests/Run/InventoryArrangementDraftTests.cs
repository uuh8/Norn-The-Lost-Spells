using Game.UI;
using NUnit.Framework;
namespace Game.Run.Tests
{
    public sealed class InventoryArrangementDraftTests:InventoryTestFixture
    {
        [Test]public void DraftContainsWholeGroupButNeverChangesRealLocation()
        {
            var bag=Define("bag",ItemKind.Backpack,bagWidth:8,bagHeight:8);var spell=Define("spell",ItemKind.Spell,maxStack:4);
            var store=Store(bag,spell);Assert.That(store.TryAdd(new RuntimeItemState("bag","bag",0),new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0)),Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("a","spell",8),Bag()),Is.True);Assert.That(store.TryAdd(new RuntimeItemState("b","spell",8),Bag()),Is.True);
            var read=new InventoryReadModel(new SinglePlayerProfile("player",store),"test");read.TryGetView("a",out var source);uint version=read.Version;
            var draft=new InventoryArrangementDraft();draft.Set(source,"bag",new GridPlacement(7,7,1),true,read);
            Assert.That(draft.Contains("a"),Is.True);Assert.That(draft.Contains("b"),Is.True);Assert.That(draft.OriginUnchanged(read),Is.True);
            draft.Set(source,"bag",new GridPlacement(-1,6,2),true,read);Assert.That(draft.Origin,Is.EqualTo(Bag()));Assert.That(read.Version,Is.EqualTo(version));At(store,"a",Bag());At(store,"b",Bag());
            draft.Clear();Assert.That(draft.Contains("a"),Is.False);At(store,"a",Bag());
        }
        [Test]public void SplitDraftContainsOnlyChosenInstance()
        {
            var bag=Define("bag",ItemKind.Backpack,bagWidth:8,bagHeight:8);var spell=Define("spell",ItemKind.Spell,maxStack:4);var store=Store(bag,spell);
            Assert.That(store.TryAdd(new RuntimeItemState("bag","bag",0),new ItemLocation(ItemLocationKind.EquippedBackpack,"player",0,0,0,0)),Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("a","spell",8),Bag()),Is.True);Assert.That(store.TryAdd(new RuntimeItemState("b","spell",8),Bag()),Is.True);
            var read=new InventoryReadModel(new SinglePlayerProfile("player",store),"test");read.TryGetView("b",out var source);
            var draft=new InventoryArrangementDraft();draft.Set(source,"bag",new GridPlacement(7,7,1),false,read);
            Assert.That(draft.Contains("b"),Is.True);Assert.That(draft.Contains("a"),Is.False);Assert.That(draft.OriginUnchanged(read),Is.True);
        }
    }
}
