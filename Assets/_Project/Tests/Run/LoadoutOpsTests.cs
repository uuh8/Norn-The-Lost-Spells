using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class LoadoutOpsTests : InventoryTestFixture
    {
        [TestCase(1, false)]
        [TestCase(3, true)]
        public void ReplaceBackpack_ValidatesWholeMigrationBeforeCommitting(int newWidth, bool success)
        {
            ItemOwnershipStore store = Store(Define("old", ItemKind.Backpack, bagWidth: 3),
                Define("new", ItemKind.Backpack, bagWidth: newWidth), Define("small", ItemKind.Consumable));
            Add(store, "bag", "old"); Add(store, "replacement", "new"); Add(store, "a", "small"); Add(store, "b", "small");
            var equipped = new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0);
            Assert.That(store.TryMove("bag", equipped).Success, Is.True);
            Assert.That(store.TryMove("a", Bag()).Success, Is.True);
            Assert.That(store.TryMove("b", Bag(2)).Success, Is.True);
            uint version = store.Version;
            InventoryOperationResult result = LoadoutOps.TryReplaceBackpack(store, "replacement", "player", version);
            Assert.That(result.Success, Is.EqualTo(success));
            if (success)
            {
                At(store, "bag", ItemLocation.Warehouse); At(store, "replacement", equipped);
                At(store, "a", Bag(owner: "replacement")); At(store, "b", Bag(2, owner: "replacement"));
                Assert.That(store.Version, Is.EqualTo(version + 1));
            }
            else
            {
                At(store, "bag", equipped); At(store, "replacement", ItemLocation.Warehouse);
                At(store, "a", Bag()); At(store, "b", Bag(2)); Assert.That(store.Version, Is.EqualTo(version));
            }
            Assert.That(store.Count, Is.EqualTo(4));
        }

        [Test]
        public void SpareWandCanEnterBackpackWhileThreeWandsStayEquipped()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack, bagWidth: 4), Define("wand", ItemKind.Wand));
            Add(store, "bag", "bag");
            Assert.That(store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)).Success, Is.True);
            for (int i = 0; i < 4; i++) Add(store, "w" + i, "wand");
            for (int i = 0; i < 3; i++) Assert.That(LoadoutOps.TryEquipWand(store, "w" + i, "player", i, store.Version).Success, Is.True);
            uint version = store.Version;
            Assert.That(store.TryMove("w3", Bag()).Success, Is.True);
            At(store, "w3", Bag()); Assert.That(store.Version, Is.EqualTo(version+1));
            Assert.That(LoadoutOps.TryEquipWand(store,"w3","player",3,store.Version).Success,Is.False);
            At(store,"w3",Bag());
        }

        [Test]
        public void EquipFromBackpack_SwapsOccupiedWandIntoFreedCellsAtomically()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("wand", ItemKind.Wand));
            Add(store, "bag", "bag"); Add(store, "a", "wand"); Add(store, "b", "wand");
            Assert.That(store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)).Success, Is.True);
            Assert.That(store.TryMove("a", Bag()).Success, Is.True);
            Assert.That(LoadoutOps.TryEquipWand(store, "b", "player", 0, store.Version).Success, Is.True);
            uint version = store.Version;
            Assert.That(LoadoutOps.TryEquipWand(store, "a", "player", 0, version).Success, Is.True);
            At(store, "b", Bag()); At(store, "a", new ItemLocation(ItemLocationKind.EquippedWand, "player", 0, 0, 0, 0));
            Assert.That(store.Version, Is.EqualTo(version + 1));
        }
    }
}
