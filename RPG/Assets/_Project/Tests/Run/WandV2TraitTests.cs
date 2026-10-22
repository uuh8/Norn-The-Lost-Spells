using NUnit.Framework;
namespace Game.Run.Tests
{
    public sealed class WandV2TraitTests:InventoryTestFixture
    {
        [TestCase(32,true)][TestCase(33,false)] public void CapacityStopsAt32(int capacity,bool valid)
        {Assert.That(new WandStats(100,10,.05f,capacity,1,.2f,0,false).IsValid,Is.EqualTo(valid));}
        [Test] public void TraitsHaveStableIdsInStatsAndSaveContract()
        {Assert.That(typeof(WandStats).GetProperty("AlwaysCastContentIds"),Is.Not.Null);Assert.That(typeof(SinglePlayerItemDto).GetField("WandAlwaysCastContentIds"),Is.Not.Null);}
        [Test]public void TraitIdsAreImmutableAndRoundTripWithRolledInstance()
        {
            var store=Store(Define("wand",ItemKind.Wand),Define("fixed",ItemKind.Spell));var ids=new[]{"fixed"};
            var stats=new WandStats(100,10,.05f,32,1,.2f,0,false,ids);ids[0]="mutated";
            Assert.That(store.TryAdd(new RuntimeItemState("w","wand",0,rolledWandStats:stats),ItemLocation.Warehouse),Is.True);
            var profile=new SinglePlayerProfile("player",store);var dto=ProfileSaveMapper.ToDto(profile,0);
            Assert.That(ProfileSaveMapper.TryRestore(dto,store.Catalog,out var restored).Success,Is.True);restored.Items.TryGet("w",out var item);
            Assert.That(item.RolledWandStats.Value.AlwaysCastContentIds,Is.EqualTo(new[]{"fixed"}));
            dto.Items[0].WandAlwaysCastContentIds[0]="missing";Assert.That(ProfileSaveMapper.TryRestore(dto,store.Catalog,out _).Success,Is.False);
        }
    }
}
