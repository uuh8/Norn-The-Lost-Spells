using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class WorldItemPickupTests : InventoryTestFixture
    {
        [Test]
        public void DropAndPickup_MoveSameInstanceAndPreserveCharges()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("spell", ItemKind.Spell));
            Add(store, "bag", "bag"); Add(store, "a", "spell", 7);
            Assert.That(store.TryMove("a", Bag()).Success, Is.True);
            uint version = store.Version;
            Assert.That(InventoryMoveOps.TryDrop(store, "a", "ground-a", version).Success, Is.True);
            At(store, "a", new ItemLocation(ItemLocationKind.WorldItem, "ground-a", 0, 0, 0, -1));
            GameObject proxy = Track(new GameObject("Pickup test"));
            WorldItemPickup pickup = proxy.AddComponent<WorldItemPickup>();
            Assert.That(pickup.TryBind(store, "a", "ground-a"), Is.True);
            Assert.That(pickup.TryCollect("bag", new GridPlacement(0, 0, 0), store.Version).Success, Is.True);
            At(store, "a", Bag()); Assert.That(proxy.activeSelf, Is.False);
            store.TryGet("a", out RuntimeItemState a); Assert.That(a.RemainingCharges, Is.EqualTo(7));
            Assert.That(a.InstanceId, Is.EqualTo("a")); Assert.That(store.Count, Is.EqualTo(2));
            Assert.That(pickup.TryCollect("bag", new GridPlacement(0, 0, 0), store.Version).Success, Is.False);
        }

        [Test]
        public void FailedPickup_RemainsOnGroundAndActive()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("small", ItemKind.Consumable));
            Add(store, "bag", "bag"); Add(store, "a", "small"); Add(store, "b", "small");
            Assert.That(store.TryMove("b", Bag()).Success, Is.True);
            Assert.That(InventoryMoveOps.TryDrop(store, "a", "ground-a", store.Version).Success, Is.True);
            GameObject proxy = Track(new GameObject("Pickup test"));
            WorldItemPickup pickup = proxy.AddComponent<WorldItemPickup>();
            Assert.That(pickup.TryBind(store, "a", "ground-a"), Is.True);
            uint version = store.Version;
            Assert.That(pickup.TryCollect("bag", new GridPlacement(0, 0, 0), version).Success, Is.False);
            Assert.That(proxy.activeSelf, Is.True); Assert.That(store.Version, Is.EqualTo(version));
            At(store, "a", new ItemLocation(ItemLocationKind.WorldItem, "ground-a", 0, 0, 0, -1));
        }

        [Test]
        public void PickupEvent_OnlyMatchingWorldOwnerHandlesItAndPublishesOneResult()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("small", ItemKind.Consumable));
            Add(store, "bag", "bag"); Add(store, "a", "small");
            Assert.That(InventoryMoveOps.TryDrop(store, "a", "ground-a", store.Version).Success, Is.True);
            WorldItemPickup pickup = Track(new GameObject("Pickup test")).AddComponent<WorldItemPickup>();
            Assert.That(pickup.TryBind(store, "a", "ground-a"), Is.True);
            int results = 0, changes = 0;
            // 普通 MonoBehaviour 在此 EditMode fixture 未自动执行 OnEnable；显式驱动边界。
            // 不加 ExecuteAlways 让生产组件在编辑状态处理游戏事件；实际生命周期仍需 PlayMode 验证。
            InvokeLifecycle(pickup, "OnEnable");
            System.Action<InventoryCommandResultEvent> onResult = e => results++;
            System.Action<InventoryChangedEvent> onChanged = e => changes++;
            EventBus<InventoryCommandResultEvent>.Subscribe(onResult);
            EventBus<InventoryChangedEvent>.Subscribe(onChanged);
            try
            {
                EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent
                { Kind = InventoryCommandKind.Pickup, ItemId = "a", WorldOwner = "wrong", OwnerId = "bag", ExpectedVersion = store.Version });
                Assert.That(results, Is.Zero);
                EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent
                { Kind = InventoryCommandKind.Pickup, ItemId = "a", WorldOwner = "ground-a", OwnerId = "bag", ExpectedVersion = store.Version });
                Assert.That(results, Is.EqualTo(1)); Assert.That(changes, Is.EqualTo(1)); At(store, "a", Bag());
            }
            finally
            {
                EventBus<InventoryCommandResultEvent>.Unsubscribe(onResult);
                EventBus<InventoryChangedEvent>.Unsubscribe(onChanged);
                InvokeLifecycle(pickup, "OnDisable");
            }
        }

        [Test]
        public void SameWorldInstance_CannotBindTwoPickupProxies()
        {
            ItemOwnershipStore store = Store(Define("small", ItemKind.Consumable));
            Add(store, "a", "small");
            Assert.That(InventoryMoveOps.TryDrop(store, "a", "ground-a", store.Version).Success, Is.True);
            WorldItemPickup first = Track(new GameObject("first")).AddComponent<WorldItemPickup>();
            WorldItemPickup second = Track(new GameObject("second")).AddComponent<WorldItemPickup>();
            Assert.That(first.TryBind(store, "a", "ground-a"), Is.True);
            Assert.That(second.TryBind(store, "a", "ground-a"), Is.False);
        }

        [Test]
        public void CollectedProxy_ReleasesIdentityForNextDropBeforeDestroy()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("small", ItemKind.Consumable));
            Add(store, "bag", "bag"); Add(store, "a", "small");
            Assert.That(InventoryMoveOps.TryDrop(store, "a", "ground-a", store.Version).Success, Is.True);
            WorldItemPickup first = Track(new GameObject("first")).AddComponent<WorldItemPickup>();
            Assert.That(first.TryBind(store, "a", "ground-a"), Is.True);
            Assert.That(first.TryCollect("bag", new GridPlacement(0, 0, 0), store.Version).Success, Is.True);
            Assert.That(InventoryMoveOps.TryDrop(store, "a", "ground-b", store.Version).Success, Is.True);
            WorldItemPickup second = Track(new GameObject("second")).AddComponent<WorldItemPickup>();
            Assert.That(second.TryBind(store, "a", "ground-b"), Is.True);
            Object.DestroyImmediate(first.gameObject);
            WorldItemPickup third = Track(new GameObject("third")).AddComponent<WorldItemPickup>();
            Assert.That(third.TryBind(store, "a", "ground-b"), Is.False, "旧代理的延后 Destroy 不能解除新代理身份。");
        }

        private static void InvokeLifecycle(WorldItemPickup pickup, string method)
        {
            // SendMessage 在普通 EditMode Behaviour 上会触发 ShouldRunBehaviour native Assert；
            // 反射只驱动 C# 订阅边界，本测试不冒充 Unity PlayMode 的自动生命周期。
            typeof(WorldItemPickup).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(pickup, null);
        }

        [Test]
        public void DroppedWand_PickupKeepsInstalledChildSpellIdentity()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("wand", ItemKind.Wand), Define("spell", ItemKind.Spell));
            Add(store, "bag", "bag"); Add(store, "wand", "wand"); Add(store, "spell", "spell", 6);
            var slot = new ItemLocation(ItemLocationKind.WandSlot, "wand", 0, 0, 0, 0);
            Assert.That(store.TryMove("spell", slot).Success, Is.True);
            Assert.That(InventoryMoveOps.TryDrop(store, "wand", "ground-wand", store.Version).Success, Is.True);
            WorldItemPickup pickup = Track(new GameObject("wand proxy")).AddComponent<WorldItemPickup>();
            Assert.That(pickup.TryBind(store, "wand", "ground-wand"), Is.True);
            Assert.That(pickup.TryCollect("bag", new GridPlacement(0, 0, 0), store.Version).Success, Is.True);
            At(store, "spell", slot); At(store, "wand", Bag());
            store.TryGet("spell", out RuntimeItemState spell); Assert.That(spell.RemainingCharges, Is.EqualTo(6));
            Assert.That(store.Count, Is.EqualTo(3));
        }

        [Test]
        public void DroppedBackpack_WithContentsCanBeReEquippedWithoutNestingShell()
        {
            ItemOwnershipStore store = Store(Define("bag", ItemKind.Backpack), Define("spell", ItemKind.Spell));
            Add(store, "bag", "bag"); Add(store, "spell", "spell", 6);
            var equipped = new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0);
            Assert.That(store.TryMove("bag", equipped).Success, Is.True);
            Assert.That(store.TryMove("spell", Bag()).Success, Is.True);
            Assert.That(InventoryMoveOps.TryDrop(store, "bag", "ground-bag", store.Version).Success, Is.True);
            WorldItemPickup pickup = Track(new GameObject("bag proxy")).AddComponent<WorldItemPickup>();
            Assert.That(pickup.TryBind(store, "bag", "ground-bag"), Is.True);
            Assert.That(pickup.TryCollect(equipped, store.Version).Success, Is.True);
            At(store, "bag", equipped); At(store, "spell", Bag()); Assert.That(store.Count, Is.EqualTo(2));
        }
    }
}
