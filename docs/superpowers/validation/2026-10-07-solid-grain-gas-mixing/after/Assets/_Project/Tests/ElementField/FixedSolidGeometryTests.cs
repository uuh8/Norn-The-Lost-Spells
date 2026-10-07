using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FixedSolidGeometryTests
    {
        private static bool Blocks(MaterialId material) => material == MaterialId.Ice;
        [Test]
        public void FilledChunkMergesPlanesIntoSixQuadsWithoutChangingVolume()
        {
            var store=new FixedSolidVolumeStore(4,1,64,new[]{MaterialId.Ice});
            for(int z=0;z<4;z++) for(int y=0;y<4;y++) for(int x=0;x<4;x++)
                store.TryAdd(new Vector3Int(x,y,z),MaterialId.Ice,64);
            var builder=new FixedSolidSurfaceBuilder(store,Blocks,8);
            builder.Build(new ElementChunkKey(0,0,0),.25f,true);
            Assert.That(builder.VertexCount,Is.EqualTo(24),"连续土/冰平面应合并，不能每个细格都留下独立四边形。");
            float volume=0;
            for(int i=0;i<builder.IndexCount;i+=3)
                volume+=Vector3.Dot(builder.Vertices[builder.Indices[i]],Vector3.Cross(builder.Vertices[builder.Indices[i+1]],builder.Vertices[builder.Indices[i+2]]))/6f;
            Assert.That(volume,Is.EqualTo(1f).Within(.0001f));
            Assert.That(store.TotalQuantity,Is.EqualTo(4096UL));
        }
        [Test]
        public void MergedSolidPlanesPreserveVerticalHoleAndMaterialBoundaries()
        {
            var store=new FixedSolidVolumeStore(4,1,64,new[]{MaterialId.Ice,MaterialId.DriedSlime});
            for(int z=0;z<4;z++) for(int y=0;y<2;y++) for(int x=0;x<4;x++)
                if(x!=1 || z!=1) store.TryAdd(new Vector3Int(x,y,z),x<2?MaterialId.Ice:MaterialId.DriedSlime,64);
            var builder=new FixedSolidSurfaceBuilder(store,Blocks,8);
            builder.Build(new ElementChunkKey(0,0,0),1,false);
            float volume=0; bool ice=false,slime=false;
            for(int i=0;i<builder.IndexCount;i+=3)
                volume+=Vector3.Dot(builder.Vertices[builder.Indices[i]],Vector3.Cross(builder.Vertices[builder.Indices[i+1]],builder.Vertices[builder.Indices[i+2]]))/6f;
            Assert.That(volume,Is.EqualTo(30f).Within(.0001f),"合并不能封洞、重叠体积或漏掉部分轮廓。");
            for(int i=0;i<builder.VertexCount;i+=4)
            {
                byte material=builder.MaterialIds[i].r; ice|=material==(byte)MaterialId.Ice; slime|=material==(byte)MaterialId.DriedSlime;
                for(int n=1;n<4;n++) Assert.That(builder.MaterialIds[i+n].r,Is.EqualTo(material));
                if(builder.Normals[i]!=Vector3.up) continue;
                float minX=float.MaxValue,maxX=float.MinValue,minZ=float.MaxValue,maxZ=float.MinValue;
                for(int n=0;n<4;n++) { var p=builder.Vertices[i+n]; minX=Mathf.Min(minX,p.x);maxX=Mathf.Max(maxX,p.x);minZ=Mathf.Min(minZ,p.z);maxZ=Mathf.Max(maxZ,p.z); }
                Assert.That(minX<1.5f && maxX>1.5f && minZ<1.5f && maxZ>1.5f,Is.False,"顶面不能盖住 1×1 的通孔。");
            }
            Assert.That(ice && slime,Is.True);
        }
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
