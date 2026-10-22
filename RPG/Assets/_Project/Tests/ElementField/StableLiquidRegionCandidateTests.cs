using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class StableLiquidRegionCandidateTests
    {
        [Test]
        public void IncrementalDependencyTrackingDoesNotAllocateDuringDiscovery()
        {
            bool Solid(Vector3Int cell) => cell.y == -1
                || cell.y == 0 && ((cell.x == -1 || cell.x == 6)
                    && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6)
                    && cell.x >= -1 && cell.x <= 6);
            var store = new LiquidVolumeStore(4, 16, 255,
                new[] { MaterialId.Water }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
                Assert.That(store.TryAdd(new Vector3Int(x, 0, z), MaterialId.Water, 255), Is.True);
            var regions = new StableLiquidRegionRuntime(2, 4, 256, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 256, 64, 2);
            detector.Step();
            long allocated = System.GC.GetAllocatedBytesForCurrentThread();
            for (int tick = 0; tick < 10; tick++) detector.Step();
            long bytes = System.GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.That(detector.HasCandidate, Is.True);
            Assert.That(bytes, Is.Zero, "局部依赖集合必须在初始化预留，跨 Tick 发现不能扩容。");
            Assert.That(store.TotalQuantity, Is.EqualTo(36UL * 255UL));
        }

        [Test]
        public void DependencyCapacityLimitKeepsAllQuantityInDynamicStore()
        {
            bool Solid(Vector3Int cell) => cell.y == -1
                || cell.y == 0 && ((cell.x == -1 || cell.x == 6)
                    && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6)
                    && cell.x >= -1 && cell.x <= 6);
            var store = new LiquidVolumeStore(1, 128, 255,
                new[] { MaterialId.Water }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
                Assert.That(store.TryAdd(new Vector3Int(x, 0, z), MaterialId.Water, 255), Is.True);
            var regions = new StableLiquidRegionRuntime(2, 4, 64, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 64, 64, 2);
            for (int tick = 0; tick < 300; tick++) detector.Step();
            Assert.That(regions.RegionCount, Is.Zero,
                "依赖空间不足时保留原表示，不能省略边界验证强行压缩。");
            Assert.That(store.TotalQuantity, Is.EqualTo(36UL * 255UL));
            Assert.That(detector.InvalidatedCandidates, Is.GreaterThan(0));
        }

        [Test]
        public void ContinuousRemoteWaterWritesDoNotStarveQuietWaterCompression()
        {
            bool Solid(Vector3Int cell) => cell.y == -1
                || cell.y == 0 && ((cell.x == -1 || cell.x == 6)
                    && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6)
                    && cell.x >= -1 && cell.x <= 6);
            var store = new LiquidVolumeStore(4, 32, 255,
                new[] { MaterialId.Water }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
                Assert.That(store.TryAdd(new Vector3Int(x, 0, z), MaterialId.Water, 255), Is.True);
            var regions = new StableLiquidRegionRuntime(2, 4, 256, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 256, 64, 2);

            for (int tick = 0; tick < 300 && regions.RegionCount == 0; tick++)
            {
                Assert.That(store.TryAdd(new Vector3Int(100 + tick % 16, 0, 100),
                    MaterialId.Water, 1), Is.True);
                detector.Step();
            }
            Assert.That(regions.RegionCount, Is.EqualTo(1),
                "其他位置持续施法时，平静湖仍应进入 Stable 表示，避免长期重复动态处理。");
            Assert.That(detector.InvalidatedCandidates, Is.Zero);
            Assert.That(regions.TotalQuantity, Is.EqualTo(36UL * 255UL));
        }

        [Test]
        public void LiquidAddedAbovePreviouslyCheckedColumnInvalidatesDiscovery()
        {
            bool Solid(Vector3Int cell) => cell.y == -1
                || cell.y == 0 && ((cell.x == -1 || cell.x == 6)
                    && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6)
                    && cell.x >= -1 && cell.x <= 6);
            var store = new LiquidVolumeStore(1, 128, 255,
                new[] { MaterialId.Water, MaterialId.Poison }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
                Assert.That(store.TryAdd(new Vector3Int(x, 0, z), MaterialId.Water, 255), Is.True);
            var regions = new StableLiquidRegionRuntime(2, 4, 256, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 256, 64, 2);
            detector.Step();
            Assert.That(detector.HasCandidate, Is.True);
            Assert.That(store.TryAdd(Vector3Int.up, MaterialId.Poison, 100), Is.True);
            detector.Step();
            Assert.That(detector.InvalidatedCandidates, Is.EqualTo(1u),
                "相邻 Chunk 的异种液体改变了已读取的顶边界，必须使旧拓扑失效。");
            Assert.That(regions.RegionCount, Is.Zero);
            Assert.That(store.TotalQuantity, Is.EqualTo(36UL * 255UL + 100UL));
        }

        [Test]
        public void RemoteWaterWriteDoesNotCancelPoisonDiscovery()
        {
            bool Solid(Vector3Int cell) => cell.y == -1
                || cell.y == 0 && ((cell.x == -1 || cell.x == 6)
                    && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6)
                    && cell.x >= -1 && cell.x <= 6);
            var store = new LiquidVolumeStore(4, 16, 255,
                new[] { MaterialId.Water, MaterialId.Poison }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
                Assert.That(store.TryAdd(new Vector3Int(x, 0, z),
                    MaterialId.Poison, 255), Is.True);
            var regions = new StableLiquidRegionRuntime(2, 4, 256, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Poison }, 256, 64, 2);

            detector.Step(); // 找到 Poison Seed，进入跨 Tick 发现阶段。
            Assert.That(detector.HasCandidate, Is.True);
            Assert.That(store.TryAdd(new Vector3Int(20, 0, 20),
                MaterialId.Water, 100), Is.True);
            detector.Step();

            Assert.That(detector.InvalidatedCandidates, Is.Zero);
            for (int tick = 0; tick < 300 && regions.RegionCount == 0; tick++)
                detector.Step();
            Assert.That(regions.RegionCount, Is.EqualTo(1));
            Assert.That(store.TotalQuantity + regions.TotalQuantity,
                Is.EqualTo(36ul * 255ul + 100ul));
        }

        [TestCase(MaterialId.Water)]
        [TestCase(MaterialId.Poison)]
        [TestCase(MaterialId.Sticky)]
        public void NearlyLevelPartialSurfaceInRaisedBankCompressesWithExactQuantity(
            MaterialId material)
        {
            bool Solid(Vector3Int cell)
            {
                if (cell.y == -1) return true;
                if (cell.y < 0 || cell.y > 1) return false;
                if (cell.x >= 3 && cell.x < 6 && cell.z >= 0 && cell.z < 6
                    && cell.y == 0) return true;
                return (cell.x == -1 || cell.x == 6) && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6) && cell.x >= -1 && cell.x <= 6;
            }
            var store = new LiquidVolumeStore(4, 16, 255,
                new[] { material }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
            {
                if (x < 3)
                    Assert.That(store.TryAdd(new Vector3Int(x, 0, z),
                        material, 255), Is.True);
                Assert.That(store.TryAdd(new Vector3Int(x, 1, z),
                    material, x < 3 ? 200u : 210u), Is.True);
            }
            ulong before = store.TotalQuantity;
            var regions = new StableLiquidRegionRuntime(2, 4, 256, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { material }, 256, 64, 2);

            for (int tick = 0; tick < 800 && regions.RegionCount == 0; tick++)
                detector.Step();

            Assert.That(regions.RegionCount, Is.EqualTo(1));
            Assert.That(detector.PartialSurfaceCompressedRegions, Is.EqualTo(1u));
            Assert.That(store.TotalQuantity, Is.Zero);
            Assert.That(regions.TotalQuantity, Is.EqualTo(before));
            Assert.That(regions.TryGetQuantity(new Vector3Int(4, 1, 2),
                material, out uint top), Is.True);
            Assert.That(top, Is.InRange(200u, 210u));
        }

        [Test]
        public void StronglyUnevenPartialSurfaceKeepsDynamicOwner()
        {
            bool Solid(Vector3Int cell) => cell.y == -1
                || cell.y >= 0 && cell.y < 2
                && ((cell.x == -1 || cell.x == 6) && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6) && cell.x >= -1 && cell.x <= 6);
            var store = new LiquidVolumeStore(4, 16, 255,
                new[] { MaterialId.Water }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
            {
                Assert.That(store.TryAdd(new Vector3Int(x, 0, z),
                    MaterialId.Water, 255), Is.True);
                Assert.That(store.TryAdd(new Vector3Int(x, 1, z),
                    MaterialId.Water, x < 3 ? 220u : 40u), Is.True);
            }
            ulong before = store.TotalQuantity;
            var regions = new StableLiquidRegionRuntime(2, 4, 256, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 256, 64, 2);
            for (int tick = 0; tick < 800; tick++) detector.Step();

            Assert.That(detector.RejectedCandidates, Is.GreaterThan(0));
            Assert.That(regions.RegionCount, Is.Zero);
            Assert.That(store.TotalQuantity, Is.EqualTo(before));
        }

        [Test]
        public void PartialSurfaceExtractionRejectsStaleExpectedLedgerBeforeMutation()
        {
            var store = new LiquidVolumeStore(4, 4, 255,
                new[] { MaterialId.Water }, _ => false);
            Assert.That(store.TryAdd(Vector3Int.zero, MaterialId.Water, 255), Is.True);
            Assert.That(store.TryAdd(Vector3Int.up, MaterialId.Water, 200), Is.True);
            ulong before = store.TotalQuantity;
            Assert.That(store.TryExtractLevelFootprint(Vector3Int.zero,
                new Vector3Int(1, 2, 1), new[] { true }, MaterialId.Water,
                before + 1, out ulong extracted), Is.False);
            Assert.That(extracted, Is.Zero);
            Assert.That(store.TotalQuantity, Is.EqualTo(before));
            Assert.That(store.TryExtractLevelFootprint(Vector3Int.zero,
                new Vector3Int(1, 2, 1), new[] { true }, MaterialId.Water,
                before, out extracted), Is.True);
            Assert.That(extracted, Is.EqualTo(before));
            Assert.That(store.TotalQuantity, Is.Zero);
        }

        [Test]
        public void HeightfieldExtractionRejectsPartialColumnWithoutChangingLedger()
        {
            var store = new LiquidVolumeStore(4, 4, 255,
                new[] { MaterialId.Water }, _ => false);
            Assert.That(store.TryAdd(new Vector3Int(0, 0, 0), MaterialId.Water, 255),
                Is.True);
            Assert.That(store.TryAdd(new Vector3Int(0, 1, 0), MaterialId.Water, 255),
                Is.True);
            Assert.That(store.TryAdd(new Vector3Int(1, 1, 0), MaterialId.Water, 128),
                Is.True);
            ulong before = store.TotalQuantity;

            Assert.That(store.TryExtractFullHeightfield(Vector3Int.zero,
                new Vector3Int(2, 2, 1), new[] { 0, 1 }, MaterialId.Water,
                out ulong extracted), Is.False);
            Assert.That(extracted, Is.Zero);
            Assert.That(store.TotalQuantity, Is.EqualTo(before));
        }

        [Test]
        public void ClosedBasinWithRaisedBottomCompressesWithoutLosingWater()
        {
            bool Solid(Vector3Int cell)
            {
                if (cell.y == -1) return true;
                if (cell.y < 0 || cell.y > 1) return false;
                if (cell.x >= 0 && cell.x < 6 && cell.z >= 0 && cell.z < 6)
                    return cell.x >= 3 && cell.y == 0;
                return (cell.x == -1 || cell.x == 6) && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6) && cell.x >= -1 && cell.x <= 6;
            }
            var store = new LiquidVolumeStore(4, 16, 255,
                new[] { MaterialId.Water }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
            for (int y = x < 3 ? 0 : 1; y < 2; y++)
                Assert.That(store.TryAdd(new Vector3Int(x, y, z),
                    MaterialId.Water, 255), Is.True);
            ulong before = store.TotalQuantity;
            var regions = new StableLiquidRegionRuntime(2, 4, 256, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 256, 64, 2);

            for (int tick = 0; tick < 800 && regions.RegionCount == 0; tick++)
                detector.Step();

            Assert.That(regions.RegionCount, Is.EqualTo(1));
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(before));
            Assert.That(store.TotalQuantity, Is.Zero);
            Assert.That(regions.TryGetQuantity(new Vector3Int(4, 0, 2),
                MaterialId.Water, out _), Is.False);
            Assert.That(regions.TryGetQuantity(new Vector3Int(4, 1, 2),
                MaterialId.Water, out uint upper), Is.True);
            Assert.That(upper, Is.EqualTo(255u));
        }

        [Test]
        public void OversizedLakeKeepsDynamicQuantityAndBoundsRepeatedSearch()
        {
            bool Solid(Vector3Int cell) => cell.y == -1
                || cell.y == 0 && ((cell.x == -1 || cell.x == 20)
                    && cell.z >= -1 && cell.z <= 20
                    || (cell.z == -1 || cell.z == 20)
                    && cell.x >= -1 && cell.x <= 20);
            var store = new LiquidVolumeStore(4, 64, 255,
                new[] { MaterialId.Water }, Solid);
            for (int z = 0; z < 20; z++)
            for (int x = 0; x < 20; x++)
                Assert.That(store.TryAdd(new Vector3Int(x, 0, z),
                    MaterialId.Water, 255), Is.True);
            ulong before = store.TotalQuantity;
            var regions = new StableLiquidRegionRuntime(2, 4, 64, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 64, 32, 2);

            for (int tick = 0; tick < 1000; tick++) detector.Step();

            Assert.That(regions.RegionCount, Is.Zero);
            Assert.That(store.TotalQuantity, Is.EqualTo(before));
            Assert.That(detector.RejectedCandidates, Is.LessThan(100),
                "超限湖泊不应从每粒水反复执行相同的 Flood Fill。");
        }

        [Test]
        public void ClosedNotchedBasinCompressesWithExactQuantityAndNoWaterInSolidNotch()
        {
            var notch = new Vector3Int(0, 0, 2);
            bool Solid(Vector3Int cell)
            {
                if (cell.y == -1) return true;
                if (cell.y != 0) return false;
                return cell == notch
                    || (cell.x == -1 || cell.x == 6) && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6) && cell.x >= -1 && cell.x <= 6;
            }
            var store = new LiquidVolumeStore(4, 16, 255,
                new[] { MaterialId.Water }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
                if (new Vector3Int(x, 0, z) != notch)
                    Assert.That(store.TryAdd(new Vector3Int(x, 0, z),
                        MaterialId.Water, 255), Is.True);
            ulong before = store.TotalQuantity;
            var regions = new StableLiquidRegionRuntime(2, 4, 256, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 256, 64, 2);

            for (int tick = 0; tick < 200 && regions.RegionCount == 0; tick++)
                detector.Step();

            Assert.That(regions.RegionCount, Is.EqualTo(1));
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(before));
            Assert.That(store.TotalQuantity, Is.Zero);
            Assert.That(regions.TryGetQuantity(notch, MaterialId.Water, out _), Is.False);
            var patches = new StableLiquidSurfacePatch[8];
            Assert.That(regions.CopySurfacePatches(MaterialId.Water, patches),
                Is.GreaterThan(1));
        }

        [Test]
        public void OpenNotchKeepsDynamicOwnerUntilWallCloses()
        {
            bool closeNotch = false;
            var notch = new Vector3Int(0, 0, 2);
            bool Solid(Vector3Int cell)
            {
                if (cell.y == -1) return true;
                if (cell.y != 0) return false;
                return (closeNotch && cell == notch)
                    || (cell.x == -1 || cell.x == 6) && cell.z >= -1 && cell.z <= 6
                    || (cell.z == -1 || cell.z == 6) && cell.x >= -1 && cell.x <= 6;
            }
            var store = new LiquidVolumeStore(4, 16, 255,
                new[] { MaterialId.Water }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
                if (new Vector3Int(x, 0, z) != notch)
                    Assert.That(store.TryAdd(new Vector3Int(x, 0, z),
                        MaterialId.Water, 255), Is.True);
            ulong before = store.TotalQuantity;
            var regions = new StableLiquidRegionRuntime(2, 4, 256, store,
                new LiquidActivityScheduler(64, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 256, 64, 2);
            for (int tick = 0; tick < 200; tick++) detector.Step();
            Assert.That(regions.RegionCount, Is.Zero);
            Assert.That(store.TotalQuantity, Is.EqualTo(before));

            closeNotch = true;
            store.InvalidateStaticCell(notch);
            for (int tick = 0; tick < 200 && regions.RegionCount == 0; tick++)
                detector.Step();
            Assert.That(regions.RegionCount, Is.EqualTo(1));
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(before));
        }

        [Test]
        public void FullSeedScanSkipsUnrelatedChunksWithinItsBudget()
        {
            var store = new LiquidVolumeStore(4, 4, 255,
                new[] { MaterialId.Water, MaterialId.Poison }, _ => false);
            Assert.That(store.TryAdd(Vector3Int.zero, MaterialId.Poison, 100), Is.True);
            var waterCell = new Vector3Int(8, 0, 0);
            Assert.That(store.TryAdd(waterCell, MaterialId.Water, 255), Is.True);
            int cursor = 0;

            Assert.That(store.TryFindFullCell(MaterialId.Water, ref cursor, 1,
                out _, out int firstCost, out int firstTraversed), Is.False);
            Assert.That(firstCost, Is.EqualTo(1));
            Assert.That(firstTraversed, Is.EqualTo(64));
            Assert.That(store.TryFindFullCell(MaterialId.Water, ref cursor, 1,
                out Vector3Int found, out int secondCost, out _), Is.True);
            Assert.That(secondCost, Is.EqualTo(1));
            Assert.That(found, Is.EqualTo(waterCell));
        }

        [Test]
        public void RejectedOpenPuddleDoesNotBlockClosedBasinAndRetriesAfterWallChange()
        {
            bool closeOpenPuddle = false;
            bool Solid(Vector3Int cell)
            {
                if (cell.y == -1) return true;
                if (cell.y != 0) return false;
                bool closedBasinWall = (cell.x == 19 || cell.x == 26)
                    && cell.z >= 20 && cell.z <= 25
                    || (cell.z == 19 || cell.z == 26)
                    && cell.x >= 20 && cell.x <= 25;
                bool openPuddleWall = closeOpenPuddle &&
                    ((cell.x == -1 || cell.x == 6) && cell.z >= 0 && cell.z <= 5
                    || (cell.z == -1 || cell.z == 6) && cell.x >= 0 && cell.x <= 5);
                return closedBasinWall || openPuddleWall;
            }

            var store = new LiquidVolumeStore(4, 32, 255,
                new[] { MaterialId.Water }, Solid);
            for (int z = 0; z < 6; z++)
            for (int x = 0; x < 6; x++)
                Assert.That(store.TryAdd(new Vector3Int(x, 0, z), MaterialId.Water, 255),
                    Is.True);
            for (int z = 20; z < 26; z++)
            for (int x = 20; x < 26; x++)
                Assert.That(store.TryAdd(new Vector3Int(x, 0, z), MaterialId.Water, 255),
                    Is.True);
            ulong total = store.TotalQuantity;
            var activity = new LiquidActivityScheduler(64, store);
            var regions = new StableLiquidRegionRuntime(4, 4, 256,
                store, activity, Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            var detector = new StableLiquidRegionDetector(store, regions,
                new[] { MaterialId.Water }, 256, 64, 2);

            for (int tick = 0; tick < 200 && regions.RegionCount == 0; tick++)
                detector.Step();

            Assert.That(detector.RejectedCandidates, Is.GreaterThan(0));
            Assert.That(regions.RegionCount, Is.EqualTo(1));
            Assert.That(regions.TryGetQuantity(new Vector3Int(20, 0, 20),
                MaterialId.Water, out _), Is.True);
            Assert.That(store.TryGetQuantity(Vector3Int.zero, MaterialId.Water, out _), Is.True);
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(total));

            for (int tick = 0; tick < 100; tick++) detector.Step();
            ulong quietScanCount = detector.SeedInspections;
            for (int tick = 0; tick < 20; tick++) detector.Step();
            Assert.That(detector.SeedInspections, Is.EqualTo(quietScanCount),
                "未变化的开放水滩不应每帧反复扫描。");

            // 地形版本变化必须撤销负面缓存；否则玩家堵住浅滩四壁后永远无法压缩。
            closeOpenPuddle = true;
            for (int z = 0; z < 6; z++)
            {
                store.InvalidateStaticCell(new Vector3Int(-1, 0, z));
                store.InvalidateStaticCell(new Vector3Int(6, 0, z));
            }
            for (int x = 0; x < 6; x++)
            {
                store.InvalidateStaticCell(new Vector3Int(x, 0, -1));
                store.InvalidateStaticCell(new Vector3Int(x, 0, 6));
            }
            for (int tick = 0; tick < 200 && regions.RegionCount < 2; tick++)
                detector.Step();

            Assert.That(regions.RegionCount, Is.EqualTo(2));
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(total));
        }
    }
}
