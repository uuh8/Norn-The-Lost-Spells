using NUnit.Framework;
using System;
using Game.Core;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class ContainerUseServiceTests
    {
        [Test] public void RuntimeUseServiceExists()
        { Assert.That(typeof(SinglePlayerProfile).Assembly.GetType("Game.Run.ContainerUseService"), Is.Not.Null); }
        [Test] public void SourceReservationExists()
        { Assert.That(typeof(SinglePlayerProfile).Assembly.GetType("Game.Run.ContainerSourceReservation"), Is.Not.Null); }

        private ItemCatalog _catalog;
        private ItemDefinition _bag, _bottle, _ice;
        private SinglePlayerProfile Profile(int capacity = 150)
        {
            _bag = Define("bag", ItemKind.Backpack); JsonUtility.FromJsonOverwrite("{\"_backpackWidth\":4,\"_backpackHeight\":2}", _bag);
            _bottle = Define("bottle", ItemKind.Container); JsonUtility.FromJsonOverwrite("{\"_containerCapacityGmu\":" + capacity + "}", _bottle);
            _ice = Define("ice", ItemKind.Material); _catalog = ScriptableObject.CreateInstance<ItemCatalog>();
            typeof(ItemCatalog).GetField("_definitions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(_catalog, new[] { _bag, _bottle, _ice });
            Assert.That(_catalog.TryCreateIndex(out var index, out var error), Is.True, error); var store = new ItemOwnershipStore(index);
            Assert.That(store.TryAdd(new RuntimeItemState("bag", "bag", 0), new ItemLocation(ItemLocationKind.EquippedBackpack, "player", 0, 0, 0, 0)), Is.True);
            var composition = new ContainerComposition((ulong)capacity, 410); composition.AddReserved(new[] { new MaterialAmount(1, 100), new MaterialAmount(3, 50) }, 2);
            Assert.That(store.TryAdd(new RuntimeItemState("bottle", "bottle", 0, composition: composition), new ItemLocation(ItemLocationKind.Backpack, "bag", 0, 0, 0, -1)), Is.True);
            Assert.That(store.TryAdd(new RuntimeItemState("ice", "ice", 0, material: new MaterialItemState(5, 200)), new ItemLocation(ItemLocationKind.Backpack, "bag", 1, 0, 0, -1)), Is.True);
            return new SinglePlayerProfile("player", store);
        }
        private static ItemDefinition Define(string id, ItemKind kind)
        { var item = ScriptableObject.CreateInstance<ItemDefinition>(); JsonUtility.FromJsonOverwrite("{\"_contentId\":\"" + id + "\",\"_kind\":" + (int)kind + "}", item); return item; }
        [TearDown] public void Cleanup()
        { foreach (var asset in new UnityEngine.Object[] { _bag, _bottle, _ice, _catalog }) if (asset != null) UnityEngine.Object.DestroyImmediate(asset); }
        private static ContainerUseRequestedEvent Request(SinglePlayerProfile profile, ContainerUseKind kind = ContainerUseKind.Pour, ulong sequence = 1)
            => new ContainerUseRequestedEvent { InventoryId = "inventory", WorldId = "world", SourceId = "user", ItemId = kind == ContainerUseKind.ReleaseSolid ? "ice" : "bottle",
                RequestId = sequence, Budget = 30, ExpectedVersion = profile.Items.Version, Kind = kind, Radius = .1f };
        private sealed class Proxy : IContainerThrowProxy
        { public bool Launched, Canceled; public void Launch() { Launched = true; } public void Cancel() { Canceled = true; } }
        private static void Accept(MaterialWorldDepositRequestedEvent command)
        {
            var exchange = command.Exchange; exchange.Handled = true;
            if (!exchange.Source.TryReserve(exchange.Amounts, exchange.Count, out exchange.Failure)) return;
            for (int i = 0; i < exchange.Count; i++) exchange.Transferred += exchange.Amounts[i].Quantity;
            exchange.Source.Commit();
        }
        [Test] public void ArcPourBeyondOldThreeMetersCommitsButNearbySolidLimitRemains()
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory");
            EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(Accept);
            try
            {
                var request = Request(profile); request.TargetPosition = Vector3.forward * 8;
                Assert.That(service.Execute(request).Success, Is.True);
                profile.Items.TryGet("bottle", out var bottle); Assert.That(bottle.Composition.Total, Is.EqualTo(120));
                var solid = Request(profile, ContainerUseKind.ReleaseSolid, 2); solid.TargetPosition = Vector3.forward * 8;
                Assert.That(service.Execute(solid).Transfer.Failure, Is.EqualTo(MaterialTransferFailure.InvalidRequest));
                profile.Items.TryGet("ice", out var ice); Assert.That(ice.Material.Quantity, Is.EqualTo(200));
            }
            finally { EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(Accept); }
        }
        [Test] public void PourPreservesMixedRatioAndConsumesOnlyCommittedAmount()
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory"); ulong water = 0, poison = 0;
            Action<MaterialWorldDepositRequestedEvent> handler = command =>
            { water += command.Exchange.Amounts[0].Quantity; poison += command.Exchange.Amounts[1].Quantity; Accept(command); };
            EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(handler);
            try
            { var result = service.Execute(Request(profile)); Assert.That(result.Success, Is.True); Assert.That(water, Is.EqualTo(20)); Assert.That(poison, Is.EqualTo(10));
                profile.Items.TryGet("bottle", out var bottle); Assert.That(bottle.Composition.GetQuantity(1), Is.EqualTo(80)); Assert.That(bottle.Composition.GetQuantity(3), Is.EqualTo(40)); }
            finally { EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(handler); }
        }
        [TestCase(false)] [TestCase(true)] public void RejectionOrMissingWorldKeepsAllInventory(bool handled)
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory"); uint version = profile.Items.Version;
            Action<MaterialWorldDepositRequestedEvent> handler = command =>
            { command.Exchange.Handled = handled; command.Exchange.Failure = MaterialTransferFailure.Capacity; };
            EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(handler);
            try { Assert.That(service.Execute(Request(profile)).Success, Is.False); profile.Items.TryGet("bottle", out var item);
                Assert.That(item.Composition.Total, Is.EqualTo(150)); Assert.That(profile.Items.Version, Is.EqualTo(version)); Assert.That(profile.Items.IsResourceBusy, Is.False); }
            finally { EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(handler); }
        }
        [Test] public void SolidPartialThenWholeReleaseKeepsIdentityAndRemovesExhaustedItem()
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory"); EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(Accept);
            try
            { Assert.That(service.Execute(Request(profile, ContainerUseKind.ReleaseSolid)).Success, Is.True); profile.Items.TryGet("ice", out var ice);
                Assert.That(ice.Material.Quantity, Is.EqualTo(170)); Assert.That(ice.InstanceId, Is.EqualTo("ice"));
                var request = Request(profile, ContainerUseKind.ReleaseSolid, 2); request.Budget = 999;
                Assert.That(service.Execute(request).Transfer.Transferred, Is.EqualTo(170)); Assert.That(profile.Items.TryGet("ice", out _), Is.False); }
            finally { EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(Accept); }
        }
        [Test] public void RepeatedAndStaleRequestsCannotConsumeTwice()
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory"); var request = Request(profile);
            EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(Accept);
            try { Assert.That(service.Execute(request).Success, Is.True); Assert.That(service.Execute(request).Transfer.Failure, Is.EqualTo(MaterialTransferFailure.DuplicateRequest));
                request.RequestId++; Assert.That(service.Execute(request).Transfer.Failure, Is.EqualTo(MaterialTransferFailure.StaleVersion)); }
            finally { EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(Accept); }
        }
        [Test] public void SourceReservationBlocksConcurrentMoveAndCopiesAmounts()
        {
            var profile = Profile(); profile.Items.TryGet("bottle", out var bottle); var source = new ContainerSourceReservation(profile.Items);
            source.Configure(bottle, profile.Items.Version); var amounts = new[] { new MaterialAmount(1, 20), new MaterialAmount(3, 10) };
            Assert.That(source.TryReserve(amounts, 2, out _), Is.True); amounts[0].Quantity = 999;
            Assert.That(profile.Items.TryMove("bottle", ItemLocation.Warehouse).Success, Is.False);
            source.Commit(); Assert.That(bottle.Composition.Total, Is.EqualTo(120)); Assert.That(profile.Items.IsResourceBusy, Is.False);
        }
        [Test] public void LargeMixedBottleSpillsInBoundedAtomicBatchesAndResolvesOnlyOnce()
        {
            var profile = Profile(15000); profile.Items.TryGet("bottle", out var bottle);
            bottle.Composition.AddReserved(new[] { new MaterialAmount(1, 9900), new MaterialAmount(3, 4950) }, 2);
            var service = new ContainerUseService(profile, "inventory"); ulong water = 0, poison = 0, resolved = 0; int calls = 0, receipts = 0;
            Action<ContainerThrownEvent> prepare = command => command.Exchange.Proxy = new Proxy();
            Action<MaterialWorldDepositRequestedEvent> deposit = command =>
            {
                calls++; command.Exchange.Handled = true;
                if (calls == 4) { command.Exchange.Failure = MaterialTransferFailure.WorldUnavailable; return; }
                ulong batch = 0;
                for (int i = 0; i < command.Exchange.Count; i++)
                { var amount = command.Exchange.Amounts[i]; batch += amount.Quantity; if (amount.MaterialKey == 1) water += amount.Quantity; else if (amount.MaterialKey == 3) poison += amount.Quantity; }
                Assert.That(batch, Is.LessThanOrEqualTo(1024)); Accept(command);
            };
            Action<ContainerTransitResolvedEvent> complete = result => { receipts++; resolved += result.Quantity; };
            EventBus<ContainerThrownEvent>.Subscribe(prepare); EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(deposit); EventBus<ContainerTransitResolvedEvent>.Subscribe(complete);
            try
            {
                var thrown = service.Execute(Request(profile, ContainerUseKind.Throw)); Assert.That(thrown.Success, Is.True);
                var impact = new ContainerImpactEvent { InventoryId = "inventory", WorldId = "world", FlightId = thrown.FlightId };
                for (int i = 0; i < 30 && service.PendingFlights > 0; i++)
                {
                    ulong before = bottle.Composition.Total; var result = service.Impact(impact);
                    Assert.That(before - bottle.Composition.Total, Is.EqualTo(result.Transfer.Transferred));
                    if (calls == 4) Assert.That(profile.Items.TryGet("bottle", out _), Is.True);
                }
                Assert.That(service.PendingFlights, Is.Zero); Assert.That(receipts, Is.EqualTo(1)); Assert.That(resolved, Is.EqualTo(15000));
                Assert.That(water, Is.EqualTo(10000)); Assert.That(poison, Is.EqualTo(5000)); Assert.That(profile.Items.TryGet("bottle", out _), Is.False);
                service.Impact(impact); Assert.That(receipts, Is.EqualTo(1));
            }
            finally
            { EventBus<ContainerThrownEvent>.Unsubscribe(prepare); EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(deposit); EventBus<ContainerTransitResolvedEvent>.Unsubscribe(complete); service.EndWorldTransits(); }
        }
        [Test] public void FailedThrowPreparationKeepsBottleInBackpack()
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory"); var result = service.Execute(Request(profile, ContainerUseKind.Throw));
            Assert.That(result.Success, Is.False); profile.Items.TryGetLocation("bottle", out var location);
            Assert.That(location.Kind, Is.EqualTo(ItemLocationKind.Backpack)); Assert.That(profile.Items.IsResourceBusy, Is.False); Assert.That(service.PendingFlights, Is.Zero);
        }
        [Test] public void FlightHasOneOwnerRejectsDragAndDuplicateImpactReleasesOnce()
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory"); var proxy = new Proxy();
            Action<ContainerThrownEvent> handler = command => command.Exchange.Proxy = proxy;
            EventBus<ContainerThrownEvent>.Subscribe(handler); EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(Accept);
            try
            { var result = service.Execute(Request(profile, ContainerUseKind.Throw)); Assert.That(result.Success, Is.True); Assert.That(proxy.Launched, Is.True);
                profile.Items.TryGetLocation("bottle", out var location); Assert.That(location.Kind, Is.EqualTo(ItemLocationKind.ThrownContainer));
                Assert.That(profile.Items.TryMove("bottle", ItemLocation.Warehouse).Success, Is.False); Assert.That(service.PendingFlights, Is.EqualTo(1));
                var impact = new ContainerImpactEvent { InventoryId = "inventory", WorldId = "world", FlightId = result.FlightId };
                Assert.That(service.Impact(impact).Transfer.Transferred, Is.EqualTo(150)); Assert.That(service.Impact(impact).Success, Is.False);
                Assert.That(profile.Items.TryGet("bottle", out _), Is.False); Assert.That(service.PendingFlights, Is.Zero); }
            finally { EventBus<ContainerThrownEvent>.Unsubscribe(handler); EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(Accept); service.EndWorldTransits(); }
        }
        [Test] public void DeferredImpactKeepsQuantityAndSceneEndRecordsEachComponent()
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory"); var proxy = new Proxy();
            Action<ContainerThrownEvent> handler = command => command.Exchange.Proxy = proxy; EventBus<ContainerThrownEvent>.Subscribe(handler);
            try
            { var result = service.Execute(Request(profile, ContainerUseKind.Throw)); var impact = new ContainerImpactEvent { InventoryId = "inventory", WorldId = "world", FlightId = result.FlightId };
                Assert.That(service.Impact(impact).Success, Is.False); profile.Items.TryGet("bottle", out var bottle); Assert.That(bottle.Composition.Total, Is.EqualTo(150));
                service.EndWorldTransits(); service.EndWorldTransits(); Assert.That(service.LostTransitQuantity(1), Is.EqualTo(100));
                Assert.That(service.LostTransitQuantity(3), Is.EqualTo(50)); Assert.That(profile.Items.TryGet("bottle", out _), Is.False); Assert.That(service.PendingFlights, Is.Zero); }
            finally { EventBus<ContainerThrownEvent>.Unsubscribe(handler); }
        }
        [Test] public void TransitCannotBeSavedOrRestoredAsAnOrphanHubItem()
        {
            var profile = Profile(); var dto = ProfileSaveMapper.ToDto(profile, 0); var service = new ContainerUseService(profile, "inventory");
            Action<ContainerThrownEvent> handler = command => command.Exchange.Proxy = new Proxy(); EventBus<ContainerThrownEvent>.Subscribe(handler);
            try
            {
                Assert.That(service.Execute(Request(profile, ContainerUseKind.Throw)).Success, Is.True);
                Assert.Throws<ArgumentException>(() => ProfileSaveMapper.ToDto(profile, 0));
                var repository = new ProfileSaveRepository("Temp/SinglePlayer/UnwrittenTransit/" + Guid.NewGuid().ToString("N"), profile.Items.Catalog);
                Assert.That(repository.TrySave(profile).Status, Is.EqualTo(ProfilePersistenceStatus.WriteBlocked));
                foreach (var item in dto.Items) if (item.InstanceId == "bottle") { item.LocationKind = ItemLocationKind.ThrownContainer; item.OwnerId = "orphan"; }
                Assert.That(ProfileSaveMapper.TryRestore(dto, profile.Items.Catalog, out _).Status, Is.EqualTo(ProfilePersistenceStatus.InvalidData));
            }
            finally { EventBus<ContainerThrownEvent>.Unsubscribe(handler); service.EndWorldTransits(); }
        }
        [Test] public void RejectedAfterSourceReservationCancelsLockAndKeepsComposition()
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory");
            Action<MaterialWorldDepositRequestedEvent> handler = command =>
            { command.Exchange.Handled = true; Assert.That(command.Exchange.Source.TryReserve(command.Exchange.Amounts, command.Exchange.Count, out _), Is.True);
                command.Exchange.Failure = MaterialTransferFailure.Capacity; };
            EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(handler);
            try { Assert.That(service.Execute(Request(profile)).Success, Is.False); profile.Items.TryGet("bottle", out var item);
                Assert.That(item.Composition.Total, Is.EqualTo(150)); Assert.That(profile.Items.IsResourceBusy, Is.False); }
            finally { EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(handler); }
        }
        [Test] public void DefaultDepositedEndMessageCannotDeleteUndeliveredFlight()
        {
            var profile = Profile(); var service = new ContainerUseService(profile, "inventory");
            Action<ContainerThrownEvent> handler = command => command.Exchange.Proxy = new Proxy(); EventBus<ContainerThrownEvent>.Subscribe(handler);
            try
            { var result = service.Execute(Request(profile, ContainerUseKind.Throw));
                service.End(new ContainerTransitEndedEvent { InventoryId = "inventory", WorldId = "world", FlightId = result.FlightId });
                Assert.That(service.PendingFlights, Is.EqualTo(1)); Assert.That(profile.Items.TryGet("bottle", out _), Is.True);
                Assert.That(service.LostTransitQuantity(1), Is.Zero); }
            finally { EventBus<ContainerThrownEvent>.Unsubscribe(handler); service.EndWorldTransits(); }
        }
    }
}
