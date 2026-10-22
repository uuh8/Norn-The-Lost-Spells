using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class StableLiquidExpansionTests
    {
        [Test]
        public void IrregularFootprintKeepsExactQuantityWithoutOwningExcludedColumns()
        {
            var minimum = Vector3Int.zero;
            var maximum = new Vector3Int(3, 2, 3);
            bool[] footprint =
            {
                true, true, false,
                true, false, false,
                true, true, true,
            };
            var curve = new LiquidCapacityCurve(minimum, maximum, footprint, 100);
            Assert.That(curve.Capacity, Is.EqualTo(1200));
            Assert.That(curve.FootprintRunCount, Is.EqualTo(3));
            Assert.That(curve.SurfaceHeightInCells(800), Is.EqualTo(1f + 1f / 3f)
                .Within(.0001f));
            ulong reconstructed = 0;
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 3; z++)
            for (int x = 0; x < 3; x++)
                reconstructed += curve.QuantityAt(new Vector3Int(x, y, z), 800);
            Assert.That(reconstructed, Is.EqualTo(800));
            Assert.That(curve.Contains(new Vector3Int(1, 0, 1)), Is.False);
            Assert.That(curve.QuantityAt(new Vector3Int(1, 0, 1), 800), Is.Zero);
        }

        [Test]
        public void SameWidthFootprintRowsMergeIntoOneSurfacePatch()
        {
            var curve = new LiquidCapacityCurve(Vector3Int.zero,
                new Vector3Int(4, 1, 3), new bool[]
                {
                    true, true, true, true,
                    true, true, true, true,
                    true, true, true, true,
                }, 255);
            Assert.That(curve.FootprintRunCount, Is.EqualTo(1));
            Assert.That(curve.GetFootprintRun(0).MinimumZ, Is.Zero);
            Assert.That(curve.GetFootprintRun(0).MaximumZ, Is.EqualTo(3));
        }

        [Test]
        public void IrregularFootprintDoesNotStealDynamicLiquidInItsBoundingBox()
        {
            bool[] footprint = { true, true, false, true, false, false,
                true, true, true };
            var excluded = new Vector3Int(1, 0, 1);
            var store = new LiquidVolumeStore(2, 16, 100,
                new[] { MaterialId.Water, MaterialId.Poison }, _ => false);
            Assert.That(store.TryAdd(excluded, MaterialId.Poison, 25), Is.True);
            var regions = new StableLiquidRegionRuntime(1, 1, 18, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f,
                maximumSurfacePatches: 3);
            store.SetExternalOccupancy(regions.OccupancyAt);

            Assert.That(regions.TryRegisterFootprint(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(3, 2, 3), footprint, 800, out _), Is.True);
            Assert.That(regions.TryGetQuantity(excluded, MaterialId.Water, out _), Is.False);
            Assert.That(store.TryGetQuantity(excluded, MaterialId.Poison, out uint poison),
                Is.True);
            Assert.That(poison, Is.EqualTo(25));
            var patches = new StableLiquidSurfacePatch[3];
            Assert.That(regions.CopySurfacePatches(MaterialId.Water, patches), Is.EqualTo(3));
            Assert.That(regions.TotalQuantity + store.TotalQuantity, Is.EqualTo(825));
        }

        [Test]
        public void IrregularFootprintRejectsPatchBudgetWithoutTakingQuantity()
        {
            bool[] footprint = { true, false, true };
            var store = new LiquidVolumeStore(2, 4, 100,
                new[] { MaterialId.Water }, _ => false);
            var regions = new StableLiquidRegionRuntime(1, 1, 3, store,
                new LiquidActivityScheduler(16, store), Vector3.zero, 1f,
                maximumSurfacePatches: 1);
            Assert.That(regions.TryRegisterFootprint(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(3, 1, 1), footprint, 200, out _), Is.False);
            Assert.That(regions.TotalQuantity + store.TotalQuantity, Is.Zero);
        }

        [Test]
        public void FullContactCellCanDepositIntoRegionWideHeadroom()
        {
            var store = new LiquidVolumeStore(2, 2, 255,
                new[] { MaterialId.Water, MaterialId.Poison }, _ => false);
            var regions = new StableLiquidRegionRuntime(1, 1, 4, store,
                new LiquidActivityScheduler(16, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            Assert.That(regions.TryRegister(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(2, 2, 1), 2, 0, 510, out _), Is.True);
            Assert.That(store.FreeCapacity(Vector3Int.zero), Is.Zero);

            // 命中已满的湖底格仍应计入上层剩余容量，不能卡在单格容量判断。
            Assert.That(regions.TryDeposit(Vector3Int.zero, MaterialId.Water, 300),
                Is.EqualTo(300));
            Assert.That(regions.TotalQuantity, Is.EqualTo(810));
            Assert.That(store.TotalQuantity, Is.Zero);
            Assert.That(regions.TryDeposit(Vector3Int.zero, MaterialId.Poison, 10), Is.Zero);
        }

        [Test]
        public void RegistrationRejectsDynamicLiquidInFutureWetLayer()
        {
            var store = new LiquidVolumeStore(2, 2, 255,
                new[] { MaterialId.Water, MaterialId.Poison }, _ => false);
            Assert.That(store.TryAdd(new Vector3Int(0, 1, 0), MaterialId.Poison, 5), Is.True);
            var regions = new StableLiquidRegionRuntime(1, 1, 4, store,
                new LiquidActivityScheduler(16, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);

            Assert.That(regions.TryRegister(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(2, 2, 1), 2, 0, 510, out _), Is.False);
            Assert.That(store.TotalQuantity, Is.EqualTo(5));
            Assert.That(regions.TotalQuantity, Is.Zero);
        }

        [Test]
        public void FallingLiquidAboveSurfaceTransfersToTheStableOwner()
        {
            var store = new LiquidVolumeStore(2, 2, 255,
                new[] { MaterialId.Water }, _ => false);
            var regions = new StableLiquidRegionRuntime(1, 1, 6, store,
                new LiquidActivityScheduler(16, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            Assert.That(regions.TryRegister(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(2, 3, 1), 2, 0, 510, out _), Is.True);
            var source = new Vector3Int(-1, 2, 0);
            Assert.That(store.TryAdd(source, MaterialId.Water, 100), Is.True);

            Assert.That(regions.TryAbsorb(source, new Vector3Int(0, 2, 0),
                MaterialId.Water, 100), Is.True);
            Assert.That(store.TotalQuantity, Is.Zero);
            Assert.That(regions.TotalQuantity, Is.EqualTo(610));
            Assert.That(regions.RegionCount, Is.EqualTo(1));
        }

        [Test]
        public void TemporaryDynamicOverlapDoesNotPermanentlyDisableAbsorption()
        {
            var store = new LiquidVolumeStore(2, 2, 255,
                new[] { MaterialId.Water, MaterialId.Poison, MaterialId.Sticky }, _ => false);
            var regions = new StableLiquidRegionRuntime(1, 1, 4, store,
                new LiquidActivityScheduler(16, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            Assert.That(regions.TryRegister(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(2, 2, 1), 2, 0, 510, out _), Is.True);
            var dryCell = new Vector3Int(0, 1, 0);
            Assert.That(store.TryAdd(dryCell, MaterialId.Poison, 10), Is.True);
            Assert.That(store.TryAdd(dryCell, MaterialId.Sticky, 5), Is.True);
            Assert.That(regions.TryDeposit(Vector3Int.zero, MaterialId.Water, 100), Is.Zero);

            Assert.That(store.TryRemove(dryCell, MaterialId.Poison, 10), Is.True);
            Assert.That(regions.TryDeposit(Vector3Int.zero, MaterialId.Water, 100), Is.Zero);
            Assert.That(store.TryRemove(dryCell, MaterialId.Sticky, 5), Is.True);
            Assert.That(regions.TryDeposit(Vector3Int.zero, MaterialId.Water, 100),
                Is.EqualTo(100));
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(610));
        }

        [Test]
        public void DynamicTransferOutOfRegionReleasesItsOverlap()
        {
            var store = new LiquidVolumeStore(2, 3, 255,
                new[] { MaterialId.Water, MaterialId.Poison }, _ => false);
            var regions = new StableLiquidRegionRuntime(1, 1, 4, store,
                new LiquidActivityScheduler(16, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            Assert.That(regions.TryRegister(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(2, 2, 1), 2, 0, 510, out _), Is.True);
            var outside = new Vector3Int(2, 1, 0);
            var inside = new Vector3Int(0, 1, 0);
            Assert.That(store.TryAdd(outside, MaterialId.Poison, 10), Is.True);
            Assert.That(store.TryTransfer(outside, inside, MaterialId.Poison, 10), Is.True);
            Assert.That(regions.TryDeposit(Vector3Int.zero, MaterialId.Water, 100), Is.Zero);

            Assert.That(store.TryTransfer(inside, outside, MaterialId.Poison, 10), Is.True);
            Assert.That(regions.TryDeposit(Vector3Int.zero, MaterialId.Water, 100),
                Is.EqualTo(100));
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(620));
        }

        [Test]
        public void InternalWallChangeTransfersExactlyOnceAndRetiresOutlet()
        {
            bool addedWall = false;
            var changed = new Vector3Int(0, 1, 0);
            var store = new LiquidVolumeStore(2, 4, 255,
                new[] { MaterialId.Water }, cell => cell == changed && addedWall);
            var activity = new LiquidActivityScheduler(128, store);
            var regions = new StableLiquidRegionRuntime(2, 2, 8, store, activity,
                Vector3.zero, 1f, 8);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            Assert.That(regions.TryRegister(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(2, 2, 1), 2, 0, 510, out int id), Is.True);
            Assert.That(regions.TryRegisterOutlet(id, new Vector3Int(2, 0, 0),
                .5f, 1f, 1f, true, out int outlet), Is.True);
            ulong before = store.TotalQuantity + regions.TotalQuantity;

            Assert.That(regions.TryPrepareStaticCellChange(changed, true), Is.True);
            addedWall = true;
            store.InvalidateStaticCell(changed);
            regions.OnStaticCellChanged(changed);

            Assert.That(regions.RegionCount, Is.Zero);
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(before));
            var patches = new StableLiquidSurfacePatch[2];
            Assert.That(regions.CopySurfacePatches(MaterialId.Water, patches), Is.EqualTo(1),
                "Gameplay 已移交后，旧自由面仍需覆盖 Mesh 准备的短窗口。");
            Assert.That(regions.IsSurfaceHandoffPending(MaterialId.Water,
                new ElementChunkKey(0, 0, 0)), Is.True);
            Assert.That(store.TryGetQuantity(Vector3Int.zero, MaterialId.Water,
                out uint first), Is.True);
            Assert.That(first, Is.EqualTo(255));
            Assert.That(regions.TryGetQuantity(Vector3Int.zero, MaterialId.Water,
                out _), Is.False);
            Assert.That(regions.SetOutletOpen(outlet, true), Is.False);
            Assert.That(regions.ExpandedRegions, Is.EqualTo(1));
            Assert.That(regions.UnsupportedInternalGeometryChanges, Is.Zero);

            Assert.That(regions.TryRegister(MaterialId.Water, new Vector3Int(4, 0, 0),
                new Vector3Int(6, 1, 1), 6, 0, 510, out int nextRegion), Is.True);
            Assert.That(nextRegion, Is.Not.EqualTo(id));
            Assert.That(regions.TryRegisterOutlet(nextRegion, new Vector3Int(6, 0, 0),
                .5f, 1f, 1f, true, out int nextOutlet), Is.True);
            Assert.That(nextOutlet, Is.Not.EqualTo(outlet));
            Assert.That(regions.SetOutletOpen(outlet, true), Is.False,
                "旧开关不能因数组槽位复用而控制新区域。");
            Assert.That(regions.SetOutletOpen(nextOutlet, false), Is.True);
        }

        [Test]
        public void OccupiedNewWallOrInsufficientChunkBudgetKeepsStableOwner()
        {
            var dryWall = new Vector3Int(0, 1, 0);
            var store = new LiquidVolumeStore(1, 1, 255,
                new[] { MaterialId.Water }, cell => cell == dryWall);
            var activity = new LiquidActivityScheduler(16, store);
            var regions = new StableLiquidRegionRuntime(1, 1, 4, store, activity,
                Vector3.zero, 1f, 4);
            store.SetExternalOccupancy(regions.OccupancyAt);
            Assert.That(regions.TryRegister(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(2, 2, 1), 2, 0, 510, out _), Is.True);

            Assert.That(regions.TryPrepareStaticCellChange(Vector3Int.zero, true), Is.False);
            Assert.That(regions.LastExpansionFailure,
                Is.EqualTo(StableLiquidExpansionFailure.OccupiedTarget));
            Assert.That(regions.TryPrepareStaticCellChange(dryWall, false), Is.False);
            Assert.That(regions.LastExpansionFailure,
                Is.EqualTo(StableLiquidExpansionFailure.ChunkBudget));
            Assert.That(regions.TotalQuantity, Is.EqualTo(510));
            Assert.That(store.TotalQuantity, Is.Zero);
            Assert.That(regions.RegionCount, Is.EqualTo(1));
        }

        [Test]
        public void ImmediateExpansionCellLimitRejectsWithoutChangingQuantity()
        {
            var store = new LiquidVolumeStore(2, 2, 255,
                new[] { MaterialId.Poison }, _ => false);
            var activity = new LiquidActivityScheduler(16, store);
            var regions = new StableLiquidRegionRuntime(1, 1, 2, store, activity,
                Vector3.zero, 1f, maximumImmediateExpansionCells: 1);
            store.SetExternalOccupancy(regions.OccupancyAt);
            Assert.That(regions.TryRegister(MaterialId.Poison, Vector3Int.zero,
                new Vector3Int(2, 1, 1), 2, 0, 510, out _), Is.True);

            Assert.That(regions.TryPrepareStaticCellChange(Vector3Int.zero, true), Is.False);
            Assert.That(regions.LastExpansionFailure,
                Is.EqualTo(StableLiquidExpansionFailure.RegionTooLarge));
            Assert.That(regions.TotalQuantity, Is.EqualTo(510));
            Assert.That(store.TotalQuantity, Is.Zero);
        }

        [Test]
        public void SurfaceHandoffBudgetRejectsWithoutDuplicatingMaterial()
        {
            var dryWall = new Vector3Int(0, 1, 0);
            var store = new LiquidVolumeStore(1, 2, 255,
                new[] { MaterialId.Water }, cell => cell == dryWall);
            var regions = new StableLiquidRegionRuntime(1, 1, 4, store,
                new LiquidActivityScheduler(16, store), Vector3.zero, 1f,
                maximumImmediateExpansionCells: 4,
                maximumImmediateExpansionSurfaceChunks: 1);
            store.SetExternalOccupancy(regions.OccupancyAt);
            Assert.That(regions.TryRegister(MaterialId.Water, Vector3Int.zero,
                new Vector3Int(2, 2, 1), 2, 0, 510, out _), Is.True);

            Assert.That(regions.TryPrepareStaticCellChange(dryWall, false), Is.False);
            Assert.That(regions.LastExpansionFailure,
                Is.EqualTo(StableLiquidExpansionFailure.SurfaceBudget));
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(510));
            Assert.That(regions.RegionCount, Is.EqualTo(1));
        }
    }
}
