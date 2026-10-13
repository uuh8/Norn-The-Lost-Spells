using System;
using System.IO;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class SinglePlayerInventorySessionTests : InventoryTestFixture
    {
        private ItemOwnershipStore _store;
        private SinglePlayerInventorySession _session;
        private string _directory;
        [SetUp]
        public void SetupSession()
        {
            _store = Store(Define("bag", ItemKind.Backpack, bagWidth: 3), Define("spell", ItemKind.Spell, maxStack: 2), Define("wand", ItemKind.Wand));
            Add(_store, "bag", "bag"); Add(_store, "a", "spell", 3); Add(_store, "b", "spell", 3); Add(_store, "wand", "wand");
            Assert.That(_store.TryMove("bag", new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)).Success, Is.True);
            _directory = Path.GetFullPath("Temp/SinglePlayer/SessionTests/" + Guid.NewGuid().ToString("N"));
            _session = Track(new GameObject("Inventory session test")).AddComponent<SinglePlayerInventorySession>();
            Assert.That(_session.Initialize(new SinglePlayerProfile("player", _store), new ProfileSaveRepository(_directory, _store.Catalog)), Is.True);
            Lifecycle("OnEnable");
        }
        [TearDown]
        public void CleanupSession()
        {
            Lifecycle("OnDisable");
            string root = Path.GetFullPath("Temp/SinglePlayer/SessionTests") + Path.DirectorySeparatorChar;
            if (_directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        private void Lifecycle(string name) => typeof(SinglePlayerInventorySession).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_session, null);
        private void Send(InventoryCommandEvent command)
        { command.InventoryId = _session.ReadOnly.InventoryId; EventBus<InventoryCommandEvent>.Publish(command); }

        [Test]
        public void ReadModel_ExposesAllInstances_AndRejectsTruncatedBuffer()
        {
            IInventoryReadOnly read = _session.ReadOnly;
            Assert.That(read.WarehouseCount, Is.EqualTo(3));
            var buffer = new InventoryItemSnapshot[read.WarehouseCount];
            Assert.That(read.CopyWarehouseTo(buffer), Is.EqualTo(3));
            Assert.That(read.TryGetView("b", out InventoryItemSnapshot b), Is.True);
            Assert.That(b.InstanceId, Is.EqualTo("b"));
            Assert.Throws<ArgumentException>(() => read.CopyWarehouseTo(new InventoryItemSnapshot[1]));
        }

        [Test]
        public void WrongInventoryScope_DoesNotMutateThisSession()
        {
            uint before = _store.Version;
            EventBus<InventoryCommandEvent>.Publish(new InventoryCommandEvent
            { InventoryId = "different", ItemId = "a", OwnerId = "bag", Kind = InventoryCommandKind.Place, ExpectedVersion = before });
            At(_store, "a", ItemLocation.Warehouse); Assert.That(_store.Version, Is.EqualTo(before));
        }

        [Test]
        public void SuccessfulCommand_EmitsOneResultAndOneVersionChange_FailureOnlyResult()
        {
            int results = 0, changes = 0;
            Action<InventoryCommandResultEvent> onResult = e => results++;
            Action<InventoryChangedEvent> onChange = e => changes++;
            EventBus<InventoryCommandResultEvent>.Subscribe(onResult); EventBus<InventoryChangedEvent>.Subscribe(onChange);
            try
            {
                Send(new InventoryCommandEvent { Kind = InventoryCommandKind.Place, ItemId = "a", OwnerId = "bag", ExpectedVersion = _store.Version });
                Send(new InventoryCommandEvent { Kind = InventoryCommandKind.Place, ItemId = "b", OwnerId = "bag", X = 999, ExpectedVersion = _store.Version });
                Assert.That(results, Is.EqualTo(2)); Assert.That(changes, Is.EqualTo(1));
                At(_store, "a", Bag()); At(_store, "b", ItemLocation.Warehouse);
            }
            finally { EventBus<InventoryCommandResultEvent>.Unsubscribe(onResult); EventBus<InventoryChangedEvent>.Unsubscribe(onChange); }
        }

        [Test]
        public void MoveWholeStackToWarehouse_PreservesBothIdsAndCommitsOnce()
        {
            Assert.That(_store.TryMove("a", Bag()).Success, Is.True); Assert.That(_store.TryMove("b", Bag()).Success, Is.True);
            uint before = _store.Version;
            Send(new InventoryCommandEvent { Kind = InventoryCommandKind.Place, ItemId = "a", Destination = InventoryDestination.Warehouse,
                WholeStack = true, ExpectedVersion = before });
            At(_store, "a", ItemLocation.Warehouse); At(_store, "b", ItemLocation.Warehouse);
            Assert.That(_store.Version, Is.EqualTo(before + 1)); Assert.That(_store.Count, Is.EqualTo(4));
        }

        [Test]
        public void WandProgramCapacity_IsIndependentOfBackpackSpace()
        {
            Send(new InventoryCommandEvent { Kind = InventoryCommandKind.Place, Destination = InventoryDestination.WandSlot,
                ItemId = "a", OwnerId = "wand", SlotIndex = 0, ExpectedVersion = _store.Version });
            uint before = _store.Version;
            Send(new InventoryCommandEvent { Kind = InventoryCommandKind.Place, Destination = InventoryDestination.WandSlot,
                ItemId = "b", OwnerId = "wand", SlotIndex = 1, ExpectedVersion = before });
            At(_store, "b", ItemLocation.Warehouse); Assert.That(_store.Version, Is.EqualTo(before));
            Assert.That(_session.ReadOnly.TryGetView("wand", out InventoryItemSnapshot wand), Is.True);
            Assert.That(wand.Rules.WandProgramCapacity, Is.EqualTo(1));
        }
        [Test]
        public void SplitIntoStoredBackpackCannotBypassWarehouseAccess()
        {
            Add(_store, "stored.bag", "bag");
            Assert.That(_store.TryMove("a", Bag()).Success, Is.True);
            Assert.That(_store.TryMove("b", Bag()).Success, Is.True);
            var hub = Track(new GameObject("Unreachable split station")).AddComponent<HubInteractionController>();
            typeof(SinglePlayerInventorySession).GetProperty("HubInteraction", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_session, hub);
            uint version = _store.Version;
            Send(new InventoryCommandEvent { Kind = InventoryCommandKind.Split, ItemId = "a", OwnerId = "stored.bag", ExpectedVersion = version });
            At(_store, "a", Bag()); Assert.That(_store.Version, Is.EqualTo(version));
            // 拆分到自己的另一格仍然可用；权限只约束仓库父链。
            Send(new InventoryCommandEvent { Kind = InventoryCommandKind.Split, ItemId = "a", OwnerId = "bag", X = 1, ExpectedVersion = version });
            At(_store, "a", Bag(1));
        }
        [Test]
        public void ConfiguredHubWithoutValidStationCannotMoveWarehouseAsset()
        {
            var hub = Track(new GameObject("Unreachable station")).AddComponent<HubInteractionController>();
            typeof(SinglePlayerInventorySession).GetProperty("HubInteraction", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_session, hub);
            uint version = _store.Version;
            Send(new InventoryCommandEvent { Kind = InventoryCommandKind.Place, ItemId = "a", OwnerId = "bag", ExpectedVersion = version });
            At(_store, "a", ItemLocation.Warehouse); Assert.That(_store.Version, Is.EqualTo(version));
            // 玩家仍可整理随身背包；访问仓库与背包内部操作使用不同权限边界。
            Assert.That(_store.TryMove("b", Bag()).Success, Is.True);
            Send(new InventoryCommandEvent { Kind = InventoryCommandKind.Place, ItemId = "b", OwnerId = "bag", X = 1, ExpectedVersion = _store.Version });
            At(_store, "b", Bag(1));
        }
    }
}
