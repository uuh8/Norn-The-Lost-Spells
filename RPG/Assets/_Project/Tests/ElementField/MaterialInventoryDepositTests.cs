using NUnit.Framework;
using System;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialInventoryDepositTests
    {
        [Test] public void RealWorldDepositPortExists()
        { Assert.That(typeof(ElementWorldRuntime).Assembly.GetType("Game.ElementField.MaterialInventoryDepositPort"), Is.Not.Null); }
        private sealed class Source : IMaterialTransferSource
        {
            public bool Reject, Reserved, Committed;
            public readonly ulong[] Removed = new ulong[256];
            private readonly MaterialAmount[] _amounts = new MaterialAmount[8]; private int _count;
            public bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure)
            { failure = Reject ? MaterialTransferFailure.ResourceBusy : MaterialTransferFailure.None; if (Reject) return false;
                for (int i = 0; i < count; i++) _amounts[i] = amounts[i]; _count = count; Reserved = true; return true; }
            public void Commit() { for (int i = 0; i < _count; i++) Removed[_amounts[i].MaterialKey] += _amounts[i].Quantity; Committed = true; Reserved = false; }
            public void Cancel() { Reserved = false; }
        }
        private static MaterialDepositExchange Request(Source source, Vector3 point, params MaterialAmount[] amounts)
            => new MaterialDepositExchange { WorldId = "test-world", Position = point, Radius = .1f, Source = source, Amounts = amounts, Count = amounts.Length };
        [TestCase(1)] [TestCase(2)] public void MixedDepositUsesActualBackendOriginAndResolution(int scale)
        {
            using var world = new MaterialInventoryTransferTests.World(scale); var source = new Source();
            Vector3 point = world.Liquid.Origin + Vector3.one * world.Liquid.CellSize * .5f;
            var request = Request(source, point, new MaterialAmount(1, 20), new MaterialAmount(3, 10));
            Assert.That(new MaterialInventoryDepositPort(world.Runtime).Deposit(request, out ulong quantity), Is.EqualTo(MaterialTransferFailure.None));
            Assert.That(quantity, Is.EqualTo(30)); world.Liquid.TryGetQuantity(Vector3Int.zero, MaterialId.Water, out uint water);
            world.Liquid.TryGetQuantity(Vector3Int.zero, MaterialId.Poison, out uint poison);
            Assert.That(water, Is.EqualTo(20)); Assert.That(poison, Is.EqualTo(10)); Assert.That(source.Removed[1], Is.EqualTo(20));
            Assert.That(world.Runtime.ImportedMaterialQuantity(MaterialId.Poison), Is.EqualTo(10));
        }
        [Test] public void LaterComponentCapacityFailureRestoresEarlierWorldWritesAndInventory()
        {
            using var world = new MaterialInventoryTransferTests.World(); var source = new Source();
            var request = Request(source, Vector3.one * 100.5f, new MaterialAmount(1, 20), new MaterialAmount(7, 10000));
            Assert.That(new MaterialInventoryDepositPort(world.Runtime).Deposit(request, out ulong quantity), Is.EqualTo(MaterialTransferFailure.Capacity));
            Assert.That(quantity, Is.Zero); Assert.That(world.Liquid.TotalQuantity, Is.Zero); Assert.That(world.Gas.TotalQuantity, Is.Zero);
            Assert.That(source.Committed, Is.False); Assert.That(source.Reserved, Is.False); Assert.That(world.Runtime.ImportedMaterialQuantity(MaterialId.Water), Is.Zero);
        }
        [Test] public void GasAndSolidWriteTheirTrueOwnerAndImportedQuantity()
        {
            using var world = new MaterialInventoryTransferTests.World(); var port = new MaterialInventoryDepositPort(world.Runtime); var source = new Source();
            Assert.That(port.Deposit(Request(source, Vector3.one * 100.5f, new MaterialAmount(7, 20), new MaterialAmount(8, 10)), out _), Is.EqualTo(MaterialTransferFailure.None));
            world.Gas.TryGetQuantity(Vector3Int.zero, MaterialId.Steam, out uint steam); Assert.That(steam, Is.EqualTo(20));
            Assert.That(port.Deposit(Request(source, new Vector3(101.5f, 100.5f, 100.5f), new MaterialAmount(5, 30)), out _), Is.EqualTo(MaterialTransferFailure.None));
            world.Solid.TryGetQuantity(Vector3Int.right, MaterialId.Ice, out uint ice); Assert.That(ice, Is.EqualTo(30));
            Assert.That(world.Runtime.ImportedMaterialQuantity(MaterialId.Ice), Is.EqualTo(30));
        }
        [Test] public void SolidGeometryRejectedAfterStageRestoresOriginalQuantity()
        {
            using var world = new MaterialInventoryTransferTests.World(navigationBoxes: 1); var source = new Source();
            world.Solid.BeginConversionGeometry(); Assert.That(world.Solid.StageConversionAdd(Vector3Int.zero, MaterialId.DriedSlime, 100), Is.True);
            Assert.That(world.Solid.StageConversionAdd(Vector3Int.right, MaterialId.DriedSlime, 100), Is.True); world.Solid.PublishConversionGeometry();
            var request = Request(source, Vector3.one * 100.5f, new MaterialAmount(6, 30));
            Assert.That(new MaterialInventoryDepositPort(world.Runtime).Deposit(request, out _), Is.EqualTo(MaterialTransferFailure.GeometryRejected));
            world.Solid.TryGetQuantity(Vector3Int.zero, MaterialId.DriedSlime, out uint quantity); Assert.That(quantity, Is.EqualTo(100));
            Assert.That(source.Committed, Is.False); Assert.That(world.Solid.TotalQuantity, Is.EqualTo(200));
        }
        [Test] public void StableWaterIsNotOverfilledByImportAndFailurePreservesItsTotal()
        {
            using var world = new MaterialInventoryTransferTests.World(); var source = new Source();
            Assert.That(world.Liquid.TryRegisterStableRegion(MaterialId.Water, Vector3Int.zero, Vector3Int.one, 0, 0, 100, out _), Is.True);
            var request = Request(source, Vector3.one * 100.5f, new MaterialAmount(1, 200));
            Assert.That(new MaterialInventoryDepositPort(world.Runtime).Deposit(request, out _), Is.EqualTo(MaterialTransferFailure.Capacity));
            world.Liquid.TryGetQuantity(Vector3Int.zero, MaterialId.Water, out uint water); Assert.That(water, Is.EqualTo(100)); Assert.That(source.Committed, Is.False);
        }
        [TestCase(0)] [TestCase(9)] public void UnknownMaterialCannotReserveOrWrite(int key)
        {
            using var world = new MaterialInventoryTransferTests.World(); var source = new Source();
            Assert.That(new MaterialInventoryDepositPort(world.Runtime).Deposit(Request(source, Vector3.one * 100.5f, new MaterialAmount((byte)key, 1)), out _), Is.EqualTo(MaterialTransferFailure.InvalidRequest));
            Assert.That(source.Reserved, Is.False); Assert.That(world.Liquid.TotalQuantity, Is.Zero);
        }
        [Test] public void SourceRejectionAndDuplicateComponentDoNotWrite()
        {
            using var world = new MaterialInventoryTransferTests.World(); var source = new Source { Reject = true }; var port = new MaterialInventoryDepositPort(world.Runtime);
            Assert.That(port.Deposit(Request(source, Vector3.one * 100.5f, new MaterialAmount(1, 20)), out _), Is.EqualTo(MaterialTransferFailure.ResourceBusy));
            Assert.That(port.Deposit(Request(source, Vector3.one * 100.5f, new MaterialAmount(1, 20), new MaterialAmount(1, 10)), out _), Is.EqualTo(MaterialTransferFailure.InvalidRequest));
            Assert.That(world.Liquid.TotalQuantity, Is.Zero);
        }
        [Test] public void WarmLiquidDepositHasNoManagedAllocationInWorldPort()
        {
            using var world = new MaterialInventoryTransferTests.World(); var source = new Source(); var port = new MaterialInventoryDepositPort(world.Runtime);
            var request = Request(source, Vector3.one * 100.5f, new MaterialAmount(1, 1));
            for (int i = 0; i < 4; i++) Assert.That(port.Deposit(request, out _), Is.EqualTo(MaterialTransferFailure.None));
            long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 16; i++) port.Deposit(request, out _);
            long allocation = GC.GetAllocatedBytesForCurrentThread() - before; Assert.That(allocation, Is.Zero, "仅World接收调用；不是整场景Profiler证据。");
            Assert.That(world.Runtime.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(20));
        }
        [Test] public void FamilyBalanceCountsImportedSeparatelyFromExternalInjection()
        { Assert.That(new MaterialQuantityFamilySnapshot(150, 150, 0, 0, 0, 30, 30).IsBalanced, Is.True); }
    }
}
