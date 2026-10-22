using System.Collections.Generic;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class StableLiquidSectorTests
    {
        [Test]
        public void AllMaterialSurfaceSnapshotMarksRetiredOwnerWithoutDuplicatingQuantity()
        {
            var store=new LiquidVolumeStore(4,8,255,new[]{MaterialId.Water,MaterialId.Poison},_=>false);
            var activity=new LiquidActivityScheduler(128,store);
            var regions=new StableLiquidRegionRuntime(4,4,64,store,activity,Vector3.zero,1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            Assert.That(regions.TryRegister(MaterialId.Water,Vector3Int.zero,new Vector3Int(2,2,2),
                2,0,1020,out _),Is.True);
            Assert.That(regions.TryRegister(MaterialId.Poison,new Vector3Int(8,0,0),new Vector3Int(10,2,2),
                10,0,1020,out _),Is.True);
            var patches=new StableLiquidSurfacePatch[4];
            Assert.That(regions.CopyAllSurfacePatches(patches),Is.EqualTo(2));
            Assert.That(patches[0].Material,Is.EqualTo(MaterialId.Water));
            Assert.That(patches[1].Material,Is.EqualTo(MaterialId.Poison));
            Assert.That(patches[0].MinimumYCells,Is.Zero);
            Assert.That(patches[0].MaximumYCells,Is.EqualTo(2));
            Assert.That(regions.TryPrepareMaterialContact(Vector3Int.zero,MaterialId.Poison),Is.True);
            Assert.That(regions.CopyAllSurfacePatches(patches),Is.EqualTo(1),
                "冻结提交只复制活跃Owner，旧面不能挤占仍有数量的Surface Patch预算。");
            Assert.That(patches[0].Material,Is.EqualTo(MaterialId.Poison));
            Assert.That(patches[0].HasGameplayQuantity,Is.True);
            Assert.That(regions.CopySurfacePatches(MaterialId.Water,patches),Is.EqualTo(1));
            Assert.That(patches[0].HasGameplayQuantity,Is.False,
                "历史交接入口仍能识别退役旧面，数量不再归它持有。");
            Assert.That(regions.TotalQuantity+store.TotalQuantity,Is.EqualTo(2040UL));
        }

        [Test]
        public void ForeignSurfaceContactExpandsOneSectorWithoutHotPathAllocation()
        {
            var materials=new[]{MaterialId.Water,MaterialId.Poison};
            var store=new LiquidVolumeStore(8,8,255,materials,_=>false);
            var activity=new LiquidActivityScheduler(2048,store);
            var regions=new StableLiquidRegionRuntime(4,4,1024,store,activity,Vector3.zero,1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            ulong initial=16UL*8*255;
            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Water,Vector3Int.zero,
                new Vector3Int(16,2,8),new int[16*8],initial,out _),Is.True);
            long before=System.GC.GetAllocatedBytesForCurrentThread();
            bool prepared=regions.TryPrepareMaterialContact(Vector3Int.zero,MaterialId.Poison);
            long bytes=System.GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(prepared,Is.True);Assert.That(bytes,Is.Zero);
            Assert.That(regions.RegionCount,Is.EqualTo(1));
            Assert.That(regions.ExpandedRegions,Is.EqualTo(1));
            Assert.That(regions.TotalQuantity+store.TotalQuantity,Is.EqualTo(initial));
            Assert.That(regions.HasForeignOccupancy(new Vector3Int(12,0,0),MaterialId.Poison),Is.True);
            Assert.That(regions.HasForeignOccupancy(Vector3Int.zero,MaterialId.Poison),Is.False);
        }

        [Test]
        public void ThreeDenseMaterialLakesKeepDeepWaterStableAndExpandOnlyLocalSectors()
        {
            MaterialId[] materials = { MaterialId.Water, MaterialId.Poison, MaterialId.Sticky };
            var store = new LiquidVolumeStore(8, 8, 255, materials, _ => false);
            var activity = new LiquidActivityScheduler(4096, store);
            var regions = new StableLiquidRegionRuntime(128, 128, 1024, store,
                activity, Vector3.zero, .25f, maximumSurfacePatches: 2048);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            int[] floors = new int[48 * 48];
            ulong perLake = 48ul * 48ul * 8ul * 255ul * 65ul / 100ul;
            var patches = new StableLiquidSurfacePatch[128];
            for (int i = 0; i < materials.Length; i++)
            {
                int x = i * 56;
                Assert.That(regions.TryRegisterSectorizedHeightfield(materials[i],
                    new Vector3Int(x, 0, 0), new Vector3Int(x + 48, 8, 48),
                    floors, perLake, out _), Is.True);
                int count = regions.CopySurfacePatches(materials[i], patches);
                Assert.That(count, Is.EqualTo(36));
                float area = 0f;
                for (int p = 0; p < count; p++)
                {
                    area += patches[p].SizeXZ.x * patches[p].SizeXZ.y;
                    Assert.That(patches[p].RegionId, Is.EqualTo(patches[0].RegionId));
                    Assert.That(patches[p].Center.y, Is.EqualTo(patches[0].Center.y));
                }
                Assert.That(area, Is.EqualTo(12f * 12f).Within(.001f));
            }
            Assert.That(regions.RegionCount, Is.EqualTo(108));
            Assert.That(regions.TotalQuantity, Is.EqualTo(perLake * 3ul));
            Assert.That(store.ResidentChunkCount, Is.Zero,
                "深水数量不应提前展开成动态格页或进入活动队列。");
            Assert.That(activity.Count, Is.Zero);

            for (int i = 0; i < materials.Length; i++)
                Assert.That(regions.TryPrepareStaticCellChange(
                    new Vector3Int(i * 56 + 1, 7, 1), true), Is.True);
            Assert.That(regions.ExpandedRegions, Is.EqualTo(3));
            Assert.That(regions.RegionCount, Is.EqualTo(105));
            Assert.That(store.ResidentChunkCount, Is.EqualTo(3));
            Assert.That(regions.TotalQuantity + store.TotalQuantity, Is.EqualTo(perLake * 3ul));
            Assert.That(regions.TryGetQuantity(new Vector3Int(40, 0, 40),
                MaterialId.Water, out _), Is.True, "远处深水仍由稳定区域直接回答查询。");
        }

        [Test]
        public void LargeLakeChangesOnlyOneChunkSectorAndKeepsExactLedger()
        {
            var store = new LiquidVolumeStore(8, 32, 255,
                new[] { MaterialId.Water }, _ => false);
            var activity = new LiquidActivityScheduler(4096, store);
            var regions = new StableLiquidRegionRuntime(16, 16, 4096, store,
                activity, Vector3.zero, 1f, maximumImmediateExpansionCells: 1024);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            int[] floors = new int[24 * 24];
            ulong initial = (ulong)(24 * 24 * 4 * 255 / 2);

            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Water,
                Vector3Int.zero, new Vector3Int(24, 4, 24), floors, initial,
                out int groupId), Is.True);
            Assert.That(groupId, Is.EqualTo(0));
            Assert.That(regions.RegionCount, Is.EqualTo(9));
            Assert.That(regions.TotalQuantity, Is.EqualTo(initial));
            var dryCell = new Vector3Int(1, 3, 1);

            Assert.That(regions.TryPrepareStaticCellChange(dryCell, true), Is.True);
            Assert.That(regions.RegionCount, Is.EqualTo(8));
            Assert.That(regions.ExpandedRegions, Is.EqualTo(1));
            Assert.That(regions.TotalQuantity + store.TotalQuantity,
                Is.EqualTo(initial), "交接后每一份 GMU 只能在稳定片区或动态 Store 中出现一次。");
            Assert.That(store.TotalQuantity, Is.GreaterThan(0));
            Assert.That(regions.TotalQuantity, Is.GreaterThan(0));
            Assert.That(regions.TryGetQuantity(new Vector3Int(17, 0, 17),
                MaterialId.Water, out _), Is.True, "远处片区仍由稳定 Owner 持有。");
        }

        [Test]
        public void OpeningRaisedFloorPreparesNeighborSectorBeforeColliderChange()
        {
            var plug = new Vector3Int(4, 0, 4);
            bool plugged = true;
            var store = new LiquidVolumeStore(8, 16, 255,
                new[] { MaterialId.Poison }, cell => plugged && cell == plug);
            var regions = new StableLiquidRegionRuntime(4, 4, 1024, store,
                new LiquidActivityScheduler(2048, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            int[] floors = new int[16 * 8];
            floors[plug.z * 16 + plug.x] = 1;
            ulong initial = 16ul * 8ul * 255ul;
            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Poison,
                Vector3Int.zero, new Vector3Int(16, 2, 8), floors,
                initial, out _), Is.True);
            Assert.That(regions.RegionCount, Is.EqualTo(2));

            Assert.That(regions.TryPrepareStaticCellChange(plug, false), Is.True);
            Assert.That(regions.RegionCount, Is.EqualTo(1));
            Assert.That(regions.TotalQuantity + store.TotalQuantity,
                Is.EqualTo(initial));
            plugged = false;
            store.InvalidateStaticCell(plug);
            regions.OnStaticCellChanged(plug);
            Assert.That(regions.UnsupportedInternalGeometryChanges, Is.Zero);
        }

        [Test]
        public void StableSectorAndExpandedNeighborExchangeBoundedQuantity()
        {
            var store = new LiquidVolumeStore(8, 16, 255,
                new[] { MaterialId.Sticky }, _ => false);
            var activity = new LiquidActivityScheduler(2048, store);
            var regions = new StableLiquidRegionRuntime(4, 4, 1024, store,
                activity, Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            int[] floors = new int[16 * 8];
            ulong initial = 16ul * 8ul * 255ul;
            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Sticky,
                Vector3Int.zero, new Vector3Int(16, 2, 8), floors,
                initial, out _), Is.True);
            Assert.That(regions.TryPrepareStaticCellChange(
                new Vector3Int(0, 1, 0), true), Is.True);
            regions.Step(.1f);
            Assert.That(regions.Ledger.CommittedGmu, Is.Zero,
                "等高接触边不能把湖底深度误当水头并反复泵水。");
            var boundary = new Vector3Int(7, 0, 0);
            Assert.That(store.TryRemove(boundary, MaterialId.Sticky, 100), Is.True);
            ulong before = regions.TotalQuantity + store.TotalQuantity;
            ulong stableBefore = regions.TotalQuantity;

            regions.Step(.1f);

            Assert.That(regions.TotalQuantity + store.TotalQuantity,
                Is.EqualTo(before));
            Assert.That(regions.TotalQuantity, Is.LessThan(stableBefore));
            Assert.That(regions.Ledger.CommittedGmu, Is.GreaterThan(0));
            Assert.That(regions.Ledger.CommittedGmu, Is.LessThanOrEqualTo(1024));
        }

        [Test]
        public void DeepDynamicCellCannotDrainIntoEqualHeightStableNeighbor()
        {
            var store = new LiquidVolumeStore(8, 16, 255,
                new[] { MaterialId.Water }, _ => false);
            var regions = new StableLiquidRegionRuntime(4, 4, 2048, store,
                new LiquidActivityScheduler(2048, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            ulong initial = (ulong)(16 * 8 * 4 * 255 * .6f);
            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Water,
                Vector3Int.zero, new Vector3Int(16, 4, 8), new int[16 * 8],
                initial, out _), Is.True);
            Assert.That(regions.TryPrepareStaticCellChange(
                new Vector3Int(0, 3, 0), true), Is.True);
            var deep = new Vector3Int(7, 0, 0);
            var stableNeighbor = new Vector3Int(8, 0, 0);
            Assert.That(store.TryGetQuantity(deep, MaterialId.Water,
                out uint deepAmount), Is.True);
            Assert.That(regions.AvailableHorizontalAbsorption(deep, stableNeighbor,
                MaterialId.Water, deepAmount), Is.Zero);
            Assert.That(regions.TryAbsorb(deep, stableNeighbor,
                MaterialId.Water, 1), Is.False);

            var surface = new Vector3Int(7, 2, 0);
            Assert.That(store.TryAdd(surface, MaterialId.Water, 100), Is.True);
            Assert.That(store.TryGetQuantity(surface, MaterialId.Water,
                out uint raisedAmount), Is.True);
            Assert.That(regions.AvailableHorizontalAbsorption(surface,
                new Vector3Int(8, 2, 0), MaterialId.Water, raisedAmount),
                Is.GreaterThan(0));
            ulong before = store.TotalQuantity + regions.TotalQuantity;
            Assert.That(regions.TryAbsorb(surface, new Vector3Int(8, 2, 0),
                MaterialId.Water, 10), Is.True);
            Assert.That(store.TotalQuantity + regions.TotalQuantity,
                Is.EqualTo(before));
        }

        [Test]
        public void NearlyLevelSectorsShareOneRenderMeshButLargeHeadStaysLocal()
        {
            var store = new LiquidVolumeStore(8, 8, 255,
                new[] { MaterialId.Water }, _ => false);
            var regions = new StableLiquidRegionRuntime(4, 4, 1024, store,
                new LiquidActivityScheduler(32, store), Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Water,
                Vector3Int.zero, new Vector3Int(16, 2, 8), new int[16 * 8],
                16ul * 8ul * 255ul, out _), Is.True);
            var patches = new StableLiquidSurfacePatch[4];
            Assert.That(regions.CopySurfacePatches(MaterialId.Water, patches),
                Is.EqualTo(2));
            Assert.That(patches[0].RegionId, Is.EqualTo(patches[1].RegionId));
            Assert.That(patches[0].Center.y, Is.EqualTo(patches[1].Center.y));

            Assert.That(regions.TryDeposit(new Vector3Int(0, 0, 0),
                MaterialId.Water, 2000), Is.EqualTo(2000));
            Assert.That(regions.CopySurfacePatches(MaterialId.Water, patches),
                Is.EqualTo(2));
            Assert.That(patches[0].RegionId, Is.Not.EqualTo(patches[1].RegionId),
                "真实水头差明显时显示层不得把两片强行拉平。");
        }

        [Test]
        public void InsufficientSlotsRejectWholeLakeWithoutPartialOwners()
        {
            var store = new LiquidVolumeStore(8, 8, 255,
                new[] { MaterialId.Water }, _ => false);
            var regions = new StableLiquidRegionRuntime(2, 4, 4096, store,
                new LiquidActivityScheduler(32, store), Vector3.zero, 1f);
            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Water,
                Vector3Int.zero, new Vector3Int(24, 4, 24), new int[24 * 24],
                1000, out _), Is.False);
            Assert.That(regions.RegionCount, Is.Zero);
            Assert.That(regions.TotalQuantity + store.TotalQuantity, Is.Zero);
        }

        [Test]
        public void QuietExpandedSectorReturnsToOriginalOwnerWithoutChangingWaterTotal()
        {
            var changed = new Vector3Int(3, 1, 3);
            bool obstacle = false;
            var store = new LiquidVolumeStore(8, 8, 255,
                new[] { MaterialId.Water }, cell => cell.y < 0 || cell.x < 0
                    || cell.x >= 8 || cell.z < 0 || cell.z >= 8
                    || (obstacle && cell == changed));
            var activity = new LiquidActivityScheduler(2048, store);
            var regions = new StableLiquidRegionRuntime(4, 4, 1024, store,
                activity, Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            const ulong initial = 8ul * 8ul * 255ul;
            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Water,
                Vector3Int.zero, new Vector3Int(8, 2, 8), new int[8 * 8],
                initial, out int groupId), Is.True);

            Assert.That(regions.TryPrepareStaticCellChange(changed, true), Is.True);
            obstacle = true;
            store.InvalidateStaticCell(changed);
            Assert.That(regions.RegionCount, Is.Zero);
            Assert.That(regions.ExpandedSectorCount, Is.EqualTo(1));
            Assert.That(store.TotalQuantity + regions.TotalQuantity, Is.EqualTo(initial));

            // 先移走干格上的障碍，再让局部版本连续安静；不调用 Solver，隔离回压缩账本。
            Assert.That(regions.TryPrepareStaticCellChange(changed, false), Is.True);
            obstacle = false;
            store.InvalidateStaticCell(changed);
            for (int i = 0; i < 26; i++) regions.TryRecaptureQuietSector(true);

            Assert.That(regions.RecapturedRegions, Is.EqualTo(1));
            Assert.That(regions.ExpandedSectorCount, Is.Zero);
            Assert.That(regions.RegionCount, Is.EqualTo(1));
            Assert.That(regions.TotalQuantity + store.TotalQuantity, Is.EqualTo(initial));
            Assert.That(store.TotalQuantity, Is.Zero);
            Assert.That(regions.TryGetQuantity(new Vector3Int(3, 0, 3),
                MaterialId.Water, out uint amount), Is.True);
            Assert.That(amount, Is.EqualTo(255));
            Assert.That(groupId, Is.Zero);
        }

        [Test]
        public void MixedMaterialBlocksRecaptureUntilItsDynamicAmountIsRemoved()
        {
            var dry = new Vector3Int(3, 1, 3);
            var store = new LiquidVolumeStore(8, 8, 255,
                new[] { MaterialId.Water, MaterialId.Poison }, cell => cell.y < 0
                    || cell.x < 0 || cell.x >= 8 || cell.z < 0 || cell.z >= 8);
            var activity = new LiquidActivityScheduler(2048, store);
            var regions = new StableLiquidRegionRuntime(4, 4, 1024, store,
                activity, Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            const ulong initial = 8ul * 8ul * 255ul;
            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Water,
                Vector3Int.zero, new Vector3Int(8, 2, 8), new int[8 * 8],
                initial, out _), Is.True);
            Assert.That(regions.TryPrepareStaticCellChange(dry, true), Is.True);
            Assert.That(store.TryAdd(dry, MaterialId.Poison, 12), Is.True);

            for (int i = 0; i < 26; i++) regions.TryRecaptureQuietSector(true);
            Assert.That(regions.ExpandedSectorCount, Is.EqualTo(1));
            Assert.That(regions.LastRecaptureFailure,
                Is.EqualTo(StableLiquidRecaptureFailure.MixedMaterial));
            Assert.That(store.TotalQuantity + regions.TotalQuantity,
                Is.EqualTo(initial + 12ul));

            Assert.That(store.TryRemove(dry, MaterialId.Poison, 12), Is.True);
            for (int i = 0; i < 26; i++) regions.TryRecaptureQuietSector(true);
            Assert.That(regions.RecapturedRegions, Is.EqualTo(1));
            Assert.That(regions.TotalQuantity + store.TotalQuantity, Is.EqualTo(initial));
        }

        [Test]
        public void FloodedExpandedSectorCanCloseOneCellWithoutLosingMixedMaterials()
        {
            var changed = new Vector3Int(3, 1, 3);
            bool closed = false;
            var store = new LiquidVolumeStore(8, 8, 255,
                new[] { MaterialId.Water, MaterialId.Poison },
                cell => cell.y < 0 || cell.x < 0 || cell.x >= 8
                    || cell.z < 0 || cell.z >= 8 || (closed && cell == changed));
            var activity = new LiquidActivityScheduler(2048, store);
            var regions = new StableLiquidRegionRuntime(4, 4, 1024, store,
                activity, Vector3.zero, 1f);
            store.SetExternalOccupancy(regions.OccupancyAt);
            store.SetDynamicOccupancyChangedCallback(regions.OnDynamicOccupancyChanged);
            const ulong initial = 8ul * 8ul * 255ul;
            Assert.That(regions.TryRegisterSectorizedHeightfield(MaterialId.Water,
                Vector3Int.zero, new Vector3Int(8, 2, 8), new int[8 * 8],
                initial, out _), Is.True);
            Assert.That(regions.TryPrepareStaticCellChange(changed, true), Is.True);
            Assert.That(store.TryAdd(changed, MaterialId.Water, 100), Is.True);
            Assert.That(store.TryAdd(changed, MaterialId.Poison, 12), Is.True);
            ulong before = regions.TotalQuantity + store.TotalQuantity;

            Assert.That(regions.TryPrepareStaticCellChange(changed, true), Is.True);
            Assert.That(store.HasAnyLiquid(changed), Is.False);
            Assert.That(regions.TotalQuantity + store.TotalQuantity, Is.EqualTo(before));
            closed = true;
            store.InvalidateStaticCell(changed);
            Assert.That(store.IsSolid(changed), Is.True);
            Assert.That(regions.ExpandedSectorCount, Is.EqualTo(1));
        }

        [Test]
        public void SolidPlacementSplitsFullCellAcrossNearbyFreeSurfaceAndRejectsFullBasin()
        {
            bool closed = false;
            var store = new LiquidVolumeStore(8, 4, 255,
                new[] { MaterialId.Water }, cell => cell.y < 0 || cell.y > 1
                    || cell.z != 0 || cell.x < 0 || cell.x >= 5
                    || (closed && cell == Vector3Int.zero));
            for (int x = 0; x < 5; x++)
            {
                Assert.That(store.TryAdd(new Vector3Int(x, 0, 0),
                    MaterialId.Water, 255), Is.True);
                Assert.That(store.TryAdd(new Vector3Int(x, 1, 0),
                    MaterialId.Water, 204), Is.True);
            }
            ulong before = store.TotalQuantity;
            var targets = new List<Vector3Int>();

            Assert.That(store.TryRelocateCellForStaticSolid(Vector3Int.zero,
                64, targets), Is.True);
            Assert.That(store.HasAnyLiquid(Vector3Int.zero), Is.False);
            Assert.That(targets.Count, Is.EqualTo(5));
            Assert.That(store.TotalQuantity, Is.EqualTo(before));
            closed = true;
            store.InvalidateStaticCell(Vector3Int.zero);
            Assert.That(store.TryRelocateCellForStaticSolid(new Vector3Int(1, 0, 0),
                64, targets), Is.False, "湖盆剩余格已满时必须保留原地形和材料。");
            Assert.That(store.TryGetQuantity(new Vector3Int(1, 0, 0),
                MaterialId.Water, out uint amount), Is.True);
            Assert.That(amount, Is.EqualTo(255));
            Assert.That(store.TotalQuantity, Is.EqualTo(before));
        }
    }
}
