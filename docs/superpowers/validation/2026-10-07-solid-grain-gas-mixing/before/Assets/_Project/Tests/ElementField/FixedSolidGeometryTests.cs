using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FixedSolidGeometryTests
    {
        private static bool Blocks(MaterialId material) => material == MaterialId.Ice;
        [Test]
        public void AdjacentChunksRemoveInternalFacesAndPreservePartialHeight()
        {
            var store = new FixedSolidVolumeStore(2, 4, 1024, new[] { MaterialId.Ice, MaterialId.DriedSlime });
            store.TryAdd(new Vector3Int(1, 0, 0), MaterialId.Ice, 1024);
            store.TryAdd(new Vector3Int(2, 0, 0), MaterialId.Ice, 512);
            var builder = new FixedSolidSurfaceBuilder(store, Blocks, 8);
            builder.Build(new ElementChunkKey(0, 0, 0), 1f, true);
            // 满格面对半格，只留下上半段侧面；跨 Chunk 使用世界坐标读取同一占据。
            int count = 0;
            for (int i = 0; i < builder.VertexCount; i++)
                if (builder.Normals[i] == Vector3.right) { Assert.That(builder.Vertices[i].y, Is.InRange(.5f, 1f)); count++; }
            Assert.That(count, Is.EqualTo(4));
            builder.Build(new ElementChunkKey(1, 0, 0), 1f, true);
            for (int i = 0; i < builder.VertexCount; i++) Assert.That(builder.Normals[i], Is.Not.EqualTo(Vector3.left));
        }
        [Test]
        public void ResidueIsVisibleButDoesNotCreateCollisionOrNavigation()
        {
            var store = new FixedSolidVolumeStore(2, 1, 1024, new[] { MaterialId.Ice, MaterialId.DriedSlime });
            store.TryAdd(Vector3Int.zero, MaterialId.DriedSlime, 256);
            var builder = new FixedSolidSurfaceBuilder(store, Blocks, 8);
            builder.Build(new ElementChunkKey(0, 0, 0), 1f, false);
            Assert.That(builder.VertexCount, Is.EqualTo(24));
            Assert.That(builder.Vertices[0].y, Is.EqualTo(.25f));
            builder.Build(new ElementChunkKey(0, 0, 0), 1f, true);
            Assert.That(builder.VertexCount, Is.Zero);
            Assert.That(builder.BuildNavigationBoxes(new ElementChunkKey(0, 0, 0), 1f), Is.True);
            Assert.That(builder.NavigationBoxCount, Is.Zero);
        }
        [Test]
        public void NavigationGreedyMergePreservesDoorwayAndRejectsInsufficientCapacity()
        {
            var store = new FixedSolidVolumeStore(4, 1, 1024, new[] { MaterialId.Ice });
            for (int y = 0; y < 3; y++) for (int z = 0; z < 4; z++) for (int x = 0; x < 4; x++)
                if (x != 1 || y == 2) store.TryAdd(new Vector3Int(x, y, z), MaterialId.Ice, 1024);
            var builder = new FixedSolidSurfaceBuilder(store, Blocks, 16);
            Assert.That(builder.BuildNavigationBoxes(new ElementChunkKey(0, 0, 0), 1f), Is.True);
            Assert.That(builder.NavigationBoxCount, Is.LessThan(8));
            var doorway = new Vector3(1.5f, .5f, 1.5f);
            for (int i = 0; i < builder.NavigationBoxCount; i++) Assert.That(builder.NavigationBoxes[i].Contains(doorway), Is.False);
            var insufficient = new FixedSolidSurfaceBuilder(store, Blocks, 1);
            Assert.That(insufficient.BuildNavigationBoxes(new ElementChunkKey(0, 0, 0), 1f), Is.False);
        }
        [Test]
        public void GeometryReusesBuffersWithZeroManagedAllocation()
        {
            var store = new FixedSolidVolumeStore(4, 1, 1024, new[] { MaterialId.Ice });
            store.TryAdd(Vector3Int.zero, MaterialId.Ice, 512);
            var builder = new FixedSolidSurfaceBuilder(store, Blocks, 16);
            var key = new ElementChunkKey(0, 0, 0);
            builder.Build(key, .25f, false); builder.BuildNavigationBoxes(key, .25f);
            long start = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { builder.Build(key, .25f, false); builder.BuildNavigationBoxes(key, .25f); }
            long bytes = System.GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.That(bytes, Is.Zero);
        }
    }
}
