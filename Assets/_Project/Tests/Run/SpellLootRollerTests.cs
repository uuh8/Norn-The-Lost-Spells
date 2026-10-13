using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class SpellLootRollerTests : InventoryTestFixture
    {
        [Test] public void ProbabilityAndWeightBoundariesAreExplicit()
        { var table=Track(ScriptableObject.CreateInstance<SpellLootTable>());table.DropChance=.5f;table.Entries=new[]{new SpellLootTable.Entry{ContentId="a",Weight=1},new SpellLootTable.Entry{ContentId="b",Weight=3}};
            Assert.That(SpellLootRoller.TryRoll(table,.5,0,out _),Is.False);Assert.That(SpellLootRoller.TryRoll(table,0,.249,out var a),Is.True);Assert.That(a.ContentId,Is.EqualTo("a"));
            Assert.That(SpellLootRoller.TryRoll(table,0,.25,out var b),Is.True);Assert.That(b.ContentId,Is.EqualTo("b"));Assert.That(SpellLootRoller.TryRoll(table,0,1,out _),Is.False); }
    }
}
