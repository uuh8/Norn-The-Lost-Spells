using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidSurfaceVersionTests
    {
        private static LiquidVolumeStore CreateStore(System.Func<Vector3Int, bool> isSolid) =>
            new LiquidVolumeStore(4, 8, 255,
                new[] { MaterialId.Water, MaterialId.Poison }, isSolid);

        [Test]
        public void WaterChangeDoesNotInvalidateUnrelatedPoisonSurface()
        {
            LiquidVolumeStore store = CreateStore(_ => false);
            var waterCell = new Vector3Int(0, 0, 0);
            var poisonCell = new Vector3Int(12, 0, 0);
            var poisonChunk = new ElementChunkKey(3, 0, 0);
            Assert.That(store.TryAdd(poisonCell, MaterialId.Poison, 100), Is.True);
            uint poisonGlobal = store.GetMaterialSurfaceVersion(MaterialId.Poison);
            uint poisonChunkVersion = store.GetMaterialChunkSurfaceVersion(
                poisonChunk, MaterialId.Poison);

            Assert.That(store.TryAdd(waterCell, MaterialId.Water, 100), Is.True);

            Assert.That(store.GetMaterialSurfaceVersion(MaterialId.Poison), Is.EqualTo(poisonGlobal));
            Assert.That(store.GetMaterialChunkSurfaceVersion(poisonChunk, MaterialId.Poison),
                Is.EqualTo(poisonChunkVersion));
        }

        [Test]
        public void MixedCellDominanceSwitchInvalidatesBothMaterials()
        {
            LiquidVolumeStore store = CreateStore(_ => false);
            var cell = Vector3Int.zero;
            var chunk = new ElementChunkKey(0, 0, 0);
            Assert.That(store.TryAdd(cell, MaterialId.Poison, 100), Is.True);
            Assert.That(store.TryAdd(cell, MaterialId.Water, 80), Is.True);
            uint poisonGlobal = store.GetMaterialSurfaceVersion(MaterialId.Poison);
            uint poisonChunk = store.GetMaterialChunkSurfaceVersion(chunk, MaterialId.Poison);
            uint waterGlobal = store.GetMaterialSurfaceVersion(MaterialId.Water);

            // 水越过毒液份额时，毒液旧表面必须被撤下，水表面必须出现。
            Assert.That(store.TryAdd(cell, MaterialId.Water, 30), Is.True);

            Assert.That(store.IsDominantMaterial(cell, MaterialId.Water), Is.True);
            Assert.That(store.GetMaterialSurfaceVersion(MaterialId.Water), Is.GreaterThan(waterGlobal));
            Assert.That(store.GetMaterialSurfaceVersion(MaterialId.Poison), Is.GreaterThan(poisonGlobal));
            Assert.That(store.GetMaterialChunkSurfaceVersion(chunk, MaterialId.Poison),
                Is.GreaterThan(poisonChunk));
        }

        [Test]
        public void StaticChangeInvalidatesSurfaceEvenWithoutResidentChunk()
        {
            bool solid = false;
            LiquidVolumeStore store = CreateStore(_ => solid);
            uint before = store.GetMaterialSurfaceVersion(MaterialId.Water);

            solid = true;
            store.InvalidateStaticCell(new Vector3Int(20, 0, 0));

            Assert.That(store.GetMaterialSurfaceVersion(MaterialId.Water), Is.GreaterThan(before));
        }

        [Test]
        public void RecycledChunkSlotCannotReuseOldMaterialVersion()
        {
            LiquidVolumeStore store = CreateStore(_ => false);
            var cell = Vector3Int.zero;
            var chunk = new ElementChunkKey(0, 0, 0);
            Assert.That(store.TryAdd(cell, MaterialId.Water, 80), Is.True);
            uint firstVersion = store.GetMaterialChunkSurfaceVersion(chunk, MaterialId.Water);
            Assert.That(store.TryRemove(cell, MaterialId.Water, 80), Is.True);
            Assert.That(store.ResidentChunkCount, Is.Zero);

            Assert.That(store.TryAdd(cell, MaterialId.Water, 80), Is.True);

            Assert.That(store.GetMaterialChunkSurfaceVersion(chunk, MaterialId.Water),
                Is.GreaterThan(firstVersion));
        }

        [Test]
        public void ChunkReadsStayCorrectAfterPoolCompactionAndReuseAtNegativeCoordinates()
        {
            // 最后一次读取的页可能恰好被回收；移槽和对象复用都不能留下旧坐标的命中。
            var store = new LiquidVolumeStore(4, 2, 255,
                new[] { MaterialId.Water, MaterialId.Poison }, _ => false);
            Vector3Int origin = Vector3Int.zero, negative = new Vector3Int(-1, 0, -1);
            Vector3Int far = new Vector3Int(8, 0, 0);
            Assert.That(store.TryAdd(origin, MaterialId.Water, 80), Is.True);
            Assert.That(store.TryAdd(negative, MaterialId.Poison, 100), Is.True);
            Assert.That(store.TryGetQuantity(origin, MaterialId.Water, out uint q), Is.True);
            Assert.That(q, Is.EqualTo(80));
            // 回收槽0会把负坐标页从槽1移来，但该页的数据/坐标仍应保持一致。
            Assert.That(store.TryRemove(origin, MaterialId.Water, 80), Is.True);
            Assert.That(store.HasAnyLiquid(origin), Is.False);
            Assert.That(store.TryGetQuantity(negative, MaterialId.Poison, out q), Is.True);
            Assert.That(q, Is.EqualTo(100));
            Assert.That(store.TryAdd(far, MaterialId.Water, 60), Is.True);
            Assert.That(store.HasAnyLiquid(origin), Is.False);
            Assert.That(store.TryTransfer(negative, negative + Vector3Int.left,
                MaterialId.Poison, 40), Is.True);
            Assert.That(store.OccupancyAt(negative), Is.EqualTo(60));
            Assert.That(store.TryRemove(far, MaterialId.Water, 60), Is.True);
            // 再次复用同一对象到原点；原点0与回收对象的default Key不能混淆。
            Assert.That(store.TryAdd(origin, MaterialId.Water, 30), Is.True);
            Assert.That(store.TryGetQuantity(far, MaterialId.Water, out q), Is.False);
            Assert.That(q, Is.Zero);
            Assert.That(store.TryGetQuantity(origin, MaterialId.Water, out q), Is.True);
            Assert.That(q, Is.EqualTo(30));
            Assert.That(store.TryGetQuantity(negative + Vector3Int.left,
                MaterialId.Poison, out q), Is.True);
            Assert.That(q, Is.EqualTo(40));
            Assert.That(store.ResidentChunkCount, Is.EqualTo(2));
            Assert.That(store.TotalQuantity, Is.EqualTo(130UL));
        }

        [Test]
        public void SurfaceCandidatesContainOnlyOccupiedMaterialPages()
        {
            LiquidVolumeStore store = CreateStore(_ => false);
            var water = new Vector3Int(3, 3, 3);
            Assert.That(store.TryAdd(water, MaterialId.Water, 100), Is.True);
            Assert.That(store.TryAdd(new Vector3Int(4, 0, 0), MaterialId.Poison, 100), Is.True);
            var keys = new ElementChunkKey[store.MaximumChunks];

            int count = store.CopyChunkKeys(MaterialId.Water, keys);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(keys[0], Is.EqualTo(new ElementChunkKey(0, 0, 0)));
            Assert.That(store.MaximumSurfaceCandidateKeys, Is.EqualTo(store.MaximumChunks));
            Assert.That(store.TryRemove(water, MaterialId.Water, 100), Is.True);
            Assert.That(store.CopyChunkKeys(MaterialId.Water, keys), Is.Zero);
        }

        [Test]
        public void InPageSpreadChangesSurfaceWithoutRefreshingCandidateSet()
        {
            LiquidVolumeStore store = CreateStore(_ => false);
            var source = new Vector3Int(1, 1, 1);
            Assert.That(store.TryAdd(source, MaterialId.Water, 100), Is.True);
            uint candidates = store.GetCandidateVersion(MaterialId.Water);
            uint surface = store.GetMaterialSurfaceVersion(MaterialId.Water);

            Assert.That(store.TryTransfer(source, new Vector3Int(3, 1, 3),
                MaterialId.Water, 50), Is.True);

            Assert.That(store.GetCandidateVersion(MaterialId.Water), Is.EqualTo(candidates));
            Assert.That(store.GetMaterialSurfaceVersion(MaterialId.Water), Is.GreaterThan(surface));
            Assert.That(store.TotalQuantity, Is.EqualTo(100ul));
        }
    }
}
