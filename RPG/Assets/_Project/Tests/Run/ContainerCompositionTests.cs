using NUnit.Framework;
using System;
using Game.Core;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class ContainerCompositionTests
    {
        [Test]
        public void ProductionProvidesRealCompositionAndTransferService()
        {
            var assembly = typeof(RuntimeItemState).Assembly;
            Assert.That(assembly.GetType("Game.Run.ContainerComposition"), Is.Not.Null, "必须先建立真实组分。");
            Assert.That(assembly.GetType("Game.Run.InventoryMaterialTransferService"), Is.Not.Null);
        }

        [Test]
        public void MixedSampleCapacityAndSnapshotsKeepTrueQuantity()
        {
            var sample = new[] { new MaterialAmount(1, 100), new MaterialAmount(3, 50) };
            var output = new MaterialAmount[2]; var remainders = new ulong[2];
            Assert.That(ContainerTransferOps.SplitBudget(sample, 2, 30, output, remainders), Is.EqualTo(30));
            Assert.That(output[0].Quantity, Is.EqualTo(20)); Assert.That(output[1].Quantity, Is.EqualTo(10));
            var bottle = new ContainerComposition(30, (1 << 1) | (1 << 3));
            Assert.That(bottle.CanAdd(output, 2), Is.True); bottle.AddReserved(output, 2);
            output[0].Quantity = 999;
            Assert.That(bottle.GetQuantity(1), Is.EqualTo(20)); Assert.That(bottle.Total, Is.EqualTo(30));
            Assert.That(bottle.CanAdd(sample, 2), Is.False); Assert.Throws<ArgumentException>(() => bottle.CopyTo(new MaterialAmount[1]));
            bottle.CopyTo(output); Assert.That(output[0].Quantity, Is.EqualTo(20));
        }

        [TestCase(0)] [TestCase(9)] [TestCase(255)]
        public void UnknownKeysAreRejectedBeforeWriting(int key)
        {
            var bottle = new ContainerComposition(100, 410);
            Assert.That(bottle.CanAdd(new[] { new MaterialAmount((byte)key, 1) }, 1), Is.False);
            Assert.That(bottle.Total, Is.Zero);
        }
        [Test]
        public void DuplicateCompositionOverflowAndForbiddenFireAreRejected()
        {
            var bottle = new ContainerComposition(100, 410);
            Assert.That(bottle.CanAdd(new[] { new MaterialAmount(1, 80), new MaterialAmount(1, 1) }, 2), Is.False);
            Assert.That(bottle.CanAdd(new[] { new MaterialAmount(1, ulong.MaxValue), new MaterialAmount(3, 1) }, 2), Is.False);
            Assert.That(bottle.CanAdd(new[] { new MaterialAmount(2, 1) }, 1), Is.False);
        }
        [Test]
        public void LargestRemainderKeepsTotalsWithTinyAndHugeBudget()
        {
            var sample = new[] { new MaterialAmount(1, 100), new MaterialAmount(3, 50) };
            var output = new MaterialAmount[2]; var rest = new ulong[2];
            ContainerTransferOps.SplitBudget(sample, 2, 1, output, rest);
            Assert.That(output[0].Quantity, Is.EqualTo(1)); Assert.That(output[1].Quantity, Is.Zero);
            sample[0].Quantity = ulong.MaxValue - 1; sample[1].Quantity = 1;
            Assert.That(ContainerTransferOps.SplitBudget(sample, 2, ulong.MaxValue, output, rest), Is.EqualTo(ulong.MaxValue));
            Assert.That(output[0].Quantity, Is.EqualTo(ulong.MaxValue - 1)); Assert.That(output[1].Quantity, Is.EqualTo(1));
        }
        [Test]
        public void LargeProductsUseExactIntegerRemaindersRatherThanRoundedFractions()
        {
            var source = new[] { new MaterialAmount(1, ulong.MaxValue - 4), new MaterialAmount(3, 4) };
            var output = new MaterialAmount[2]; var rest = new ulong[2];
            const ulong budget = ulong.MaxValue - 7;
            Assert.That(MaterialAmountOps.SplitBudget(source, 2, budget, output, rest), Is.EqualTo(budget));
            Assert.That(output[0].Quantity, Is.EqualTo(budget - 4)); Assert.That(output[1].Quantity, Is.EqualTo(4));
            // BigInteger只存在测试中作为独立精确参考，生产路径不用堆上的大整数。
            var random = new System.Random(17);
            for (int i = 0; i < 128; i++)
            {
                ulong a = ((ulong)random.Next() << 32) | (uint)random.Next();
                source[0].Quantity = a; source[1].Quantity = ulong.MaxValue - a;
                ulong requested = ((ulong)random.Next() << 32) | (uint)random.Next();
                MaterialAmountOps.SplitBudget(source, 2, requested, output, rest);
                var expected = System.Numerics.BigInteger.DivRem((System.Numerics.BigInteger)a * requested, ulong.MaxValue, out var remainder);
                var otherRemainder = (System.Numerics.BigInteger)(ulong.MaxValue - a) * requested % ulong.MaxValue;
                if (remainder + otherRemainder > 0 && remainder >= otherRemainder) expected++;
                Assert.That(output[0].Quantity, Is.EqualTo((ulong)expected));
                Assert.That(output[0].Quantity + output[1].Quantity, Is.EqualTo(requested));
            }
        }
        [Test]
        public void RateIsFrameIndependentAndReleaseClearsFraction()
        {
            var high = new CollectionRateAccumulator(); var low = new CollectionRateAccumulator(); ulong a = 0, b = 0;
            for (int i = 0; i < 60; i++) a += high.Advance(100, 1d / 60);
            for (int i = 0; i < 10; i++) b += low.Advance(100, .1);
            Assert.That(a, Is.EqualTo(100)); Assert.That(b, Is.EqualTo(100));
            high.Advance(1, .75); high.Reset(); Assert.That(high.Advance(1, .5), Is.Zero);
            Assert.That(high.Advance(double.NaN, 1), Is.Zero);
        }

        private ItemDefinition _bag, _bottle, _solid;
        private ItemCatalog _catalog;
        private SinglePlayerProfile Profile(int width = 3)
        {
            _bag = Define("bag", ItemKind.Backpack); JsonUtility.FromJsonOverwrite("{\"_backpackWidth\":" + width + ",\"_backpackHeight\":1}", _bag);
            _bottle = Define("bottle", ItemKind.Container); JsonUtility.FromJsonOverwrite("{\"_maxStack\":3,\"_containerCapacityGmu\":30}", _bottle);
            _solid = Define("ice", ItemKind.Material);
            _catalog = ScriptableObject.CreateInstance<ItemCatalog>();
            var field = typeof(ItemCatalog).GetField("_definitions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field.SetValue(_catalog, new[] { _bag, _bottle, _solid });
            Assert.That(_catalog.TryCreateIndex(out var index, out var error), Is.True, error);
            var store = new ItemOwnershipStore(index);
            Assert.That(store.TryAdd(new RuntimeItemState("bag", "bag", 0), new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)), Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("a", "bottle", 0), new ItemLocation(ItemLocationKind.Backpack, "bag", 0, 0, 0, -1)), Is.True);
            return new SinglePlayerProfile("player", store);
        }
        private static ItemDefinition Define(string id, ItemKind kind)
        { var item = ScriptableObject.CreateInstance<ItemDefinition>(); JsonUtility.FromJsonOverwrite("{\"_contentId\":\"" + id + "\",\"_kind\":" + (int)kind + "}", item); return item; }
        [TearDown]
        public void Cleanup()
        { foreach (var asset in new UnityEngine.Object[] { _bag, _bottle, _solid, _catalog }) if (asset != null) UnityEngine.Object.DestroyImmediate(asset); }
        private static MaterialTransferRequestedEvent Request(SinglePlayerProfile profile, ulong id = 1)
            => new MaterialTransferRequestedEvent { InventoryId = "inventory", WorldId = "world", SourceId = "collector", ItemId = "a",
                Budget = 30, RequestId = id, ExpectedVersion = profile.Items.Version };
        [TestCase(1, false)] [TestCase(2, true)]
        public void IndependentContainerFillPreservesOtherBottleAndLocksWrites(int width, bool otherCarried)
        {
            var profile = Profile(width); var store = profile.Items;
            Assert.That(store.TryAdd(new RuntimeItemState("b", "bottle", 0), new ItemLocation(ItemLocationKind.Backpack, "bag", 0, 0, 0, -1)), Is.False, "空瓶也必须独占一格");
            var otherLocation = otherCarried ? new ItemLocation(ItemLocationKind.Backpack, "bag", 1, 0, 0, -1) : ItemLocation.Warehouse;
            Assert.That(store.TryAdd(new RuntimeItemState("b", "bottle", 0), otherLocation), Is.True);
            var service = new InventoryMaterialTransferService(profile, "inventory"); ulong world = 100;
            Action<MaterialWorldTransferRequestedEvent> handler = e =>
            {
                e.Exchange.Handled = true;
                var amounts = new[] { new MaterialAmount(1, 30) };
                if (!e.Exchange.Destination.TryReserve(amounts, 1, out var failure)) { e.Exchange.Result.Failure = failure; return; }
                Assert.That(store.TryMove("b", ItemLocation.Warehouse).Success, Is.False, "预约必须阻止其他写入。");
                world -= 30; e.Exchange.Destination.Commit(); e.Exchange.Result.Transferred = 30;
            };
            EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(handler);
            try
            {
                var result = service.Execute(Request(profile)); Assert.That(result.Success, Is.True);
                store.TryGet("a", out var a); store.TryGet("b", out var b);
                Assert.That(a.Composition.Total, Is.EqualTo(30)); Assert.That(b.Composition.Total, Is.Zero);
                Assert.That(world, Is.EqualTo(70)); Assert.That(store.IsResourceBusy, Is.False);
                Assert.That(ItemStackOps.AreCompatible(a, b), Is.False);
                Assert.That(store.TryGetLocation("b", out var actual), Is.True); Assert.That(actual, Is.EqualTo(otherLocation));
            }
            finally { EventBus<MaterialWorldTransferRequestedEvent>.Unsubscribe(handler); }
        }
        [Test]
        public void FailedWorldDuplicateAndStaleRequestsNeverModifyInventory()
        {
            var profile = Profile(); var service = new InventoryMaterialTransferService(profile, "inventory"); uint version = profile.Items.Version;
            Assert.That(service.Execute(Request(profile)).Failure, Is.EqualTo(MaterialTransferFailure.WorldUnavailable));
            Assert.That(service.Execute(Request(profile)).Failure, Is.EqualTo(MaterialTransferFailure.DuplicateRequest));
            var stale = Request(profile, 2); stale.ExpectedVersion--;
            Assert.That(service.Execute(stale).Failure, Is.EqualTo(MaterialTransferFailure.StaleVersion));
            Assert.That(profile.Items.Version, Is.EqualTo(version)); Assert.That(profile.Items.IsResourceBusy, Is.False);
        }
        [Test]
        public void CompositionAndSolidStateRoundTripWithStableIdentity()
        {
            var profile = Profile(); profile.Items.TryGet("a", out var a);
            var sample = new[] { new MaterialAmount(1, 20), new MaterialAmount(3, 10) }; a.Composition.AddReserved(sample, 2);
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("ice-a", "ice", 0, material: new MaterialItemState(5, 25)), ItemLocation.Warehouse), Is.True);
            var dto = ProfileSaveMapper.ToDto(profile, 1); string json = JsonUtility.ToJson(dto);
            var restore = ProfileSaveMapper.TryRestore(JsonUtility.FromJson<SinglePlayerSaveDto>(json), profile.Items.Catalog, out var restored);
            Assert.That(restore.Success, Is.True, restore.Status + ":" + restore.Message + " json=" + json);
            restored.Items.TryGet("a", out var copy); restored.Items.TryGet("ice-a", out var ice);
            Assert.That(copy.Composition.GetQuantity(3), Is.EqualTo(10)); Assert.That(ice.Material.Quantity, Is.EqualTo(25));
            Assert.That(ice.InstanceId, Is.EqualTo("ice-a")); Assert.That(ItemStackOps.AreCompatible(ice,
                new RuntimeItemState("ice-b", "ice", 0, material: new MaterialItemState(5, 26))), Is.False);
            dto.Items[1].Composition = new[] { new MaterialAmount(1, 31) };
            Assert.That(ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out _).Success, Is.False);
        }
        [Test]
        public void SchemaOneRestoresEmptyContainersWithoutInventingMaterial()
        {
            var profile = Profile(); var dto = ProfileSaveMapper.ToDto(profile, 1); dto.SchemaVersion = 1;
            for (int i = 0; i < dto.Items.Length; i++) dto.Items[i].Composition = null;
            Assert.That(ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out var restored).Success, Is.True);
            restored.Items.TryGet("a", out var a); Assert.That(a.Composition.Total, Is.Zero);
        }
        [Test]
        public void GeneralStateReplacementCannotEraseRealComposition()
        {
            var profile = Profile(); profile.Items.TryGet("a", out var a); a.Composition.AddReserved(new[] { new MaterialAmount(1, 20) }, 1);
            var old = profile.Items.Version;
            Assert.That(ItemStackOps.TryChangeState(profile.Items, new RuntimeItemState("a", "bottle", 0), ItemLocation.Warehouse, old).Success, Is.False);
            profile.Items.TryGet("a", out var after); Assert.That(after.Composition.GetQuantity(1), Is.EqualTo(20));
        }
        [Test]
        public void GeneralStateReplacementCannotDuplicateSolidQuantity()
        {
            var profile = Profile(); profile.Items.TryAdd(new RuntimeItemState("ice-a", "ice", 0, material: new MaterialItemState(5, 20)), ItemLocation.Warehouse);
            Assert.That(ItemStackOps.TryChangeState(profile.Items, new RuntimeItemState("ice-a", "ice", 0, material: new MaterialItemState(5, 40)),
                ItemLocation.Warehouse, profile.Items.Version).Success, Is.False);
        }
        [Test]
        public void SolidReservationRequiresRealBackpackSpaceAndBlocksSaving()
        {
            var profile = Profile(1); var service = new InventoryMaterialTransferService(profile, "inventory"); ulong world = 100;
            Action<MaterialWorldTransferRequestedEvent> handler = e =>
            {
                e.Exchange.Handled = true; var amounts = new[] { new MaterialAmount(5, 25) };
                if (!e.Exchange.Destination.TryReserve(amounts, 1, out var failure)) { e.Exchange.Result.Failure = failure; return; }
                world -= 25; e.Exchange.Destination.Commit(); e.Exchange.Result.Transferred = 25;
            };
            EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(handler);
            try
            {
                var request = Request(profile); request.SolidItem = true; request.MaterialContentId = "ice"; request.BackpackId = "bag";
                Assert.That(service.Execute(request).Failure, Is.EqualTo(MaterialTransferFailure.Capacity)); Assert.That(world, Is.EqualTo(100));
                Assert.That(profile.Items.Count, Is.EqualTo(2)); Assert.That(profile.Items.IsResourceBusy, Is.False);
            }
            finally { EventBus<MaterialWorldTransferRequestedEvent>.Unsubscribe(handler); }
            var reservation = new ContainerReservation(profile.Items); profile.Items.TryGet("a", out var item); profile.Items.Catalog.TryGetRules("bottle", out var rules);
            reservation.Configure(item, "bag", rules, profile.Items.Version, false);
            Assert.That(reservation.TryReserve(new[] { new MaterialAmount(1, 10) }, 1, out _), Is.True);
            var repository = new ProfileSaveRepository("Temp/SinglePlayer/ReservationMustNotWrite", profile.Items.Catalog);
            Assert.That(repository.TrySave(profile).Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked)); reservation.Cancel();
        }
        [TestCase(1, 1)] [TestCase(9, 1)] [TestCase(1, 31)]
        public void CorruptSavedComponentsNeverPublishProfile(int key, int quantity)
        {
            var profile = Profile(); var dto = ProfileSaveMapper.ToDto(profile, 1);
            dto.Items[1].Composition = key == 1 && quantity == 1
                ? new[] { new MaterialAmount(1, 1), new MaterialAmount(1, 1) } : new[] { new MaterialAmount((byte)key, (ulong)quantity) };
            Assert.That(ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out var restored).Success, Is.False); Assert.That(restored, Is.Null);
        }
        [Test]
        public void CapacityConfigMismatchAndSharedCompositionCannotCreateAssets()
        {
            var profile = Profile(); var dto = ProfileSaveMapper.ToDto(profile, 1); dto.Items[1].ContainerCapacity++;
            Assert.That(ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out _).Success, Is.False);
            profile.Items.TryGet("a", out var a);
            Assert.That(profile.Items.TryAdd(new RuntimeItemState("b", "bottle", 0, composition: a.Composition), ItemLocation.Warehouse), Is.False);
        }
    }
}
