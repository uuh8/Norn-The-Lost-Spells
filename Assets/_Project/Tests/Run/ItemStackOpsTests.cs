using Game.Core;
using NUnit.Framework;

namespace Game.Run.Tests
{
    public sealed class ItemStackOpsTests : InventoryTestFixture
    {
        private ItemOwnershipStore StackStore(int bagWidth = 3)
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack, bagWidth: bagWidth),
                Define("spell", ItemKind.Spell, maxStack: 2), Define("bottle", ItemKind.Container, maxStack: 3));
            Add(store, "bag", "bag"); Add(store, "a", "spell", 3); Add(store, "b", "spell", 3); Add(store, "c", "spell", 3);
            Assert.That(store.TryMove("a", Bag()).Success, Is.True);
            return store;
        }

        [Test]
        public void MergeRetainsIndividualIds_AndRejectsStackOverCapacity()
        {
            ItemOwnershipStore store = StackStore();
            Assert.That(ItemStackOps.TryMerge(store, "b", "a", store.Version).Success, Is.True);
            uint version = store.Version;
            Assert.That(ItemStackOps.TryMerge(store, "c", "a", version).Reason, Is.EqualTo(InventoryFailureReason.Capacity));
            At(store, "a", Bag()); At(store, "b", Bag()); At(store, "c", ItemLocation.Warehouse);
            Assert.That(store.Count, Is.EqualTo(4)); Assert.That(store.Version, Is.EqualTo(version));
        }

        [Test]
        public void SplitSpecificMember_RetainsProtectedIdentityAndCharges()
        {
            ItemOwnershipStore store = StackStore();
            Assert.That(ItemStackOps.TryMerge(store, "b", "a", store.Version).Success, Is.True);
            const string protectedId = "b";
            Assert.That(ItemStackOps.TrySplit(store, protectedId, "bag", new GridPlacement(1, 0, 0), store.Version).Success, Is.True);
            At(store, "a", Bag()); At(store, protectedId, Bag(1));
            store.TryGet(protectedId, out RuntimeItemState member);
            Assert.That(member.InstanceId, Is.EqualTo(protectedId)); Assert.That(member.RemainingCharges, Is.EqualTo(3));
        }

        [Test]
        public void MovingWholeStack_CommitsOnce_AndFailureMovesNone()
        {
            ItemOwnershipStore store = StackStore();
            Assert.That(ItemStackOps.TryMerge(store, "b", "a", store.Version).Success, Is.True);
            uint version = store.Version;
            Assert.That(ItemStackOps.TryMoveStack(store, "a", "bag", new GridPlacement(1, 0, 0), version).Success, Is.True);
            Assert.That(store.Version, Is.EqualTo(version + 1));
            At(store, "a", Bag(1)); At(store, "b", Bag(1));
            version = store.Version;
            Assert.That(ItemStackOps.TryMoveStack(store, "b", "bag", new GridPlacement(99, 0, 0), version).Success, Is.False);
            At(store, "a", Bag(1)); At(store, "b", Bag(1)); Assert.That(store.Version, Is.EqualTo(version));
        }

        [Test]
        public void SplitToSameGroup_IsRejectedWithoutSilentNoOp()
        {
            ItemOwnershipStore store = StackStore();
            Assert.That(ItemStackOps.TryMerge(store, "b", "a", store.Version).Success, Is.True);
            uint version = store.Version;
            Assert.That(ItemStackOps.TrySplit(store, "b", "bag", new GridPlacement(0, 0, 0), version).Reason,
                Is.EqualTo(InventoryFailureReason.InvalidState));
            Assert.That(store.Version, Is.EqualTo(version));
        }

        [Test]
        public void StateChange_ReservesSeparateFootprintBeforeConsumingCharges()
        {
            ItemOwnershipStore store = StackStore();
            Assert.That(ItemStackOps.TryMerge(store, "b", "a", store.Version).Success, Is.True);
            var changed = new RuntimeItemState("b", "spell", 2);
            uint version = store.Version;
            Assert.That(ItemStackOps.TryChangeState(store, changed, Bag(1), version).Success, Is.True);
            At(store, "a", Bag()); At(store, "b", Bag(1));
            store.TryGet("a", out RuntimeItemState a); store.TryGet("b", out RuntimeItemState b);
            Assert.That(a.RemainingCharges, Is.EqualTo(3)); Assert.That(b.RemainingCharges, Is.EqualTo(2));
            Assert.That(store.Version, Is.EqualTo(version + 1));
        }

        [Test]
        public void FullBag_StateChangeOrSplitFailureRetainsAllChargesAndIds()
        {
            ItemOwnershipStore store = StackStore(1);
            Assert.That(ItemStackOps.TryMerge(store, "b", "a", store.Version).Success, Is.True);
            uint version = store.Version;
            Assert.That(ItemStackOps.TryChangeState(store, new RuntimeItemState("b", "spell", 2), Bag(), version).Success, Is.False);
            Assert.That(ItemStackOps.TrySplit(store, "b", "bag", new GridPlacement(1, 0, 0), version).Success, Is.False);
            store.TryGet("b", out RuntimeItemState b);
            Assert.That(b.RemainingCharges, Is.EqualTo(3)); At(store, "a", Bag()); At(store, "b", Bag());
            Assert.That(store.Count, Is.EqualTo(4)); Assert.That(store.Version, Is.EqualTo(version));
        }

        [TestCase(false, "mix.water:1")]
        [TestCase(true, "")]
        public void DifferentStateKeysOrUnlimitedFlag_CannotMerge(bool unlimited, string stateKey)
        {
            ItemOwnershipStore store = StackStore();
            Assert.That(store.TryAdd(new RuntimeItemState("other", "spell", 3, unlimited, stateKey), ItemLocation.Warehouse), Is.True);
            uint version = store.Version;
            Assert.That(ItemStackOps.TryMerge(store, "other", "a", version).Success, Is.False);
            At(store, "other", ItemLocation.Warehouse); Assert.That(store.Version, Is.EqualTo(version));
        }

        [Test]
        public void EmptyContainersCannotMergeAndRetainSeparateIdentities()
        {
            ItemOwnershipStore store = StackStore();
            Add(store, "bottle-a", "bottle"); Add(store, "bottle-b", "bottle");
            Assert.That(store.TryMove("bottle-a", Bag(1)).Success, Is.True);
            Assert.That(ItemStackOps.TryMerge(store, "bottle-b", "bottle-a", store.Version).Success, Is.False);
            At(store,"bottle-b",ItemLocation.Warehouse);
            // 容器的内容属于具体瓶子；即使空瓶也不能共享背包格。
            var changed = new RuntimeItemState("bottle-b", "bottle", 0, false, "sample.water:10;oil:5");
            Assert.That(ItemStackOps.TryChangeState(store, changed, Bag(2), store.Version).Success, Is.True);
            At(store, "bottle-a", Bag(1)); At(store, "bottle-b", Bag(2));
            Assert.That(store.Count, Is.EqualTo(6));
            Assert.That(ItemStackOps.TryMerge(store, "bottle-b", "bottle-a", store.Version).Success, Is.False);
        }

        [Test]
        public void StateReplacement_CannotChangeContentIdentity()
        {
            ItemOwnershipStore store = StackStore();
            uint version = store.Version;
            Assert.That(ItemStackOps.TryChangeState(store, new RuntimeItemState("a", "bottle", 0), Bag(), version).Reason,
                Is.EqualTo(InventoryFailureReason.InvalidIdentity));
            Assert.That(store.Version, Is.EqualTo(version));
        }

        [Test]
        public void DifferentRemainingCharges_CannotShareOneStackFootprint()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("spell", ItemKind.Spell));
            Add(store, "bag", "bag"); Add(store, "a", "spell", 3); Add(store, "b", "spell", 8);
            Assert.That(store.TryMove("a", Bag()).Success, Is.True);
            uint before = store.Version;
            Assert.That(store.TryMove("b", Bag()).Success, Is.False);
            At(store, "a", Bag()); At(store, "b", ItemLocation.Warehouse);
            store.TryGet("a", out RuntimeItemState a); store.TryGet("b", out RuntimeItemState b);
            Assert.That(a.RemainingCharges + b.RemainingCharges, Is.EqualTo(11));
            Assert.That(store.Version, Is.EqualTo(before));
        }
    }
}
