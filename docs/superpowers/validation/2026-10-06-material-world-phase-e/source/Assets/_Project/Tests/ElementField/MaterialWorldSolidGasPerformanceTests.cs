using System;
using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialWorldSolidGasPerformanceTests
    {
        [Test]
        public void StaticSolidInventoryDoesNotRebuildOrAllocateAfterWarmup()
        {
            var host=new GameObject("Solid Cold Performance Test"); host.SetActive(false); host.hideFlags=HideFlags.DontSave;
            var profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumCellsPerTick\":8,\"_maximumNavigationBoxesPerChunk\":8}",profile);
            try
            {
                var runtime=host.AddComponent<FixedSolidWorldRuntime>();
                typeof(FixedSolidWorldRuntime).GetField("_profile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(runtime,profile);
                Assert.That(runtime.TryInitialize(null,null,Vector3.one*100,1,4),Is.True);
                for(int i=0;i<8;i++)
                {
                    var request=new ElementWriteRequest(Vector3.one*100+Vector3.right*(i*4)+Vector3.one*.1f,
                        MaterialId.Ice,512,0,false);
                    Assert.That(runtime.TryEnqueueDeposit(in request),Is.True);
                }
                runtime.Step(); Assert.That(runtime.ResidentChunkCount,Is.EqualTo(8));
                uint rebuilds=runtime.SurfaceRebuildCount; Assert.That(rebuilds,Is.GreaterThan(0));
                for(int tick=0;tick<30;tick++) runtime.Step();
                long before=GC.GetAllocatedBytesForCurrentThread();
                for(int tick=0;tick<300;tick++) runtime.Step();
                long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
                Assert.That(allocated,Is.Zero); Assert.That(runtime.SurfaceRebuildCount,Is.EqualTo(rebuilds));
                Assert.That(runtime.PendingDepositCount,Is.Zero); Assert.That(runtime.TotalQuantity,Is.EqualTo(4096UL));
            }
            finally { UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(profile); }
        }
        [Test]
        public void ZeroIntegerFluxDoesNotSampleAllSixNeighborTerrains()
        {
            var store=new GasVolumeStore(4,8,4096,new[]{MaterialId.Steam});
            var activity=new MaterialCellActivityQueue(64,store);
            int terrainCalls=0;
            var solver=new GasFluxSolver(store,activity,new[]{new GasMaterialBehavior
                {Material=MaterialId.Steam,DiffusionPerSecond=1,BuoyancyPerSecond=1}},1,
                cell=>{terrainCalls++;return false;});
            store.TryAdd(Vector3Int.zero,MaterialId.Steam,1); activity.Wake(Vector3Int.zero);
            solver.Step(.1f);
            Assert.That(store.TotalQuantity,Is.EqualTo(1UL));
            Assert.That(store.ResidentChunkCount,Is.EqualTo(1));
            Assert.That(terrainCalls,Is.EqualTo(1),"所有面都不足 1 GMU；只检查源地形，不预热六邻面。");
        }
        [Test]
        public void LargeFrozenInventoryRetainsQuantityAndDoesNotRunTerrainOrAllocateAfterRecovery()
        {
            var store=new GasVolumeStore(4,128,4096,new[]{MaterialId.Steam,MaterialId.ToxicVapor});
            var activity=new MaterialCellActivityQueue(64,store); int terrainCalls=0;
            var solver=new GasFluxSolver(store,activity,new[]{
                new GasMaterialBehavior{Material=MaterialId.Steam,DiffusionPerSecond=1,BuoyancyPerSecond=1},
                new GasMaterialBehavior{Material=MaterialId.ToxicVapor,DiffusionPerSecond=1}},8,
                cell=>{terrainCalls++;return false;});
            for(int chunk=0;chunk<96;chunk++) store.TryAdd(new Vector3Int(chunk*8,0,0),MaterialId.Steam,2000);
            var domain=new ElementWorldActivityRegions(); activity.SetDomain(domain,Vector3.zero,1);
            for(int tick=0;tick<30;tick++) solver.Step(.1f);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int tick=0;tick<300;tick++) solver.Step(.1f);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(terrainCalls,Is.Zero); Assert.That(solver.ProcessedCellsLastTick,Is.Zero);
            Assert.That(allocated,Is.Zero); Assert.That(store.TotalQuantity,Is.EqualTo(192000UL));
            Assert.That(store.ResidentChunkCount,Is.EqualTo(96));
        }
        [Test]
        public void ActiveGasWorkIsBoundedAndConservativeAfterWarmup()
        {
            var store=new GasVolumeStore(4,128,4096,new[]{MaterialId.Steam});
            var activity=new MaterialCellActivityQueue(64,store);
            var solver=new GasFluxSolver(store,activity,new[]{new GasMaterialBehavior
                {Material=MaterialId.Steam,DiffusionPerSecond=2,BuoyancyPerSecond=1}},8,cell=>false);
            for(int i=0;i<96;i++)
            {
                var cell=new Vector3Int(i*8,0,0);
                store.TryAdd(cell,MaterialId.Steam,2000); activity.WakeNeighbors(cell);
            }
            for(int tick=0;tick<30;tick++) solver.Step(.1f);
            int maximumProcessed=0;
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int tick=0;tick<300;tick++)
            { solver.Step(.1f); maximumProcessed=Math.Max(maximumProcessed,solver.ProcessedCellsLastTick); }
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(maximumProcessed,Is.GreaterThan(0).And.LessThanOrEqualTo(8));
            Assert.That(allocated,Is.Zero); Assert.That(store.TotalQuantity,Is.EqualTo(192000UL));
            Assert.That(store.ResidentChunkCount,Is.LessThanOrEqualTo(128));
        }
        [Test]
        public void BulkReaderCopiesMixedCurrentQuantityIntoStridedBufferAndClearsMissingMaterial()
        {
            var store=new GasVolumeStore(4,8,4096,new[]{MaterialId.Steam,MaterialId.ToxicVapor});
            var key=new ElementChunkKey(-2,1,-3);
            for(int z=0;z<4;z++) for(int y=0;y<4;y++) for(int x=0;x<4;x++)
            {
                var cell=ElementWorldCoordinates.ComposeGlobalCell(key,new Vector3Int(x,y,z),4);
                store.TryAdd(cell,MaterialId.Steam,(uint)(1+x+10*y+100*z));
                store.TryAdd(cell,MaterialId.ToxicVapor,500);
            }
            var destination=new uint[80]; Array.Fill(destination,999u);
            store.CopyMaterialBox(key,new Vector3Int(1,1,1),new Vector3Int(2,2,2),MaterialId.Steam,destination,3,5,20);
            for(int z=0;z<2;z++) for(int y=0;y<2;y++) for(int x=0;x<2;x++)
                Assert.That(destination[3+x+5*y+20*z],Is.EqualTo((uint)(1+x+1+10*(y+1)+100*(z+1))));
            Assert.That(destination[2],Is.EqualTo(999)); Assert.That(destination[5],Is.EqualTo(999));
            store.CopyMaterialBox(key,Vector3Int.zero,Vector3Int.one,MaterialId.Water,destination,3,5,20);
            Assert.That(destination[3],Is.Zero);
            store.CopyMaterialBox(new ElementChunkKey(30,30,30),Vector3Int.zero,Vector3Int.one,
                MaterialId.Steam,destination,4,5,20);
            Assert.That(destination[4],Is.Zero);
            Assert.That(store.TotalQuantity,Is.EqualTo(42720UL));
        }
        [Test]
        public void BulkReaderCannotPublishUncommittedFlux()
        {
            var store=new GasVolumeStore(2,4,4096,new[]{MaterialId.Steam});
            store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000);
            var buffer=new uint[8];
            store.BeginFluxTick(); Assert.That(store.Transfer(Vector3Int.zero,Vector3Int.right,MaterialId.Steam,300),Is.EqualTo(300));
            store.CopyMaterialBox(new ElementChunkKey(0,0,0),Vector3Int.zero,Vector3Int.one*2,MaterialId.Steam,buffer,0,2,4);
            Assert.That(buffer[0],Is.EqualTo(1000)); Assert.That(buffer[1],Is.Zero);
            store.CommitFluxTick();
            store.CopyMaterialBox(new ElementChunkKey(0,0,0),Vector3Int.zero,Vector3Int.one*2,MaterialId.Steam,buffer,0,2,4);
            Assert.That(buffer[0],Is.EqualTo(700)); Assert.That(buffer[1],Is.EqualTo(300));
        }
        [Test]
        public void BulkReaderRejectsOutOfChunkOrOverlappingDestinationLayout()
        {
            var store=new GasVolumeStore(2,4,4096,new[]{MaterialId.Steam}); var buffer=new uint[8];
            Assert.Throws<ArgumentOutOfRangeException>(()=>store.CopyMaterialBox(new ElementChunkKey(0,0,0),
                Vector3Int.one,Vector3Int.one*2,MaterialId.Steam,buffer,0,2,4));
            Assert.Throws<ArgumentOutOfRangeException>(()=>store.CopyMaterialBox(new ElementChunkKey(0,0,0),
                Vector3Int.zero,Vector3Int.one*2,MaterialId.Steam,buffer,0,1,4));
            Assert.Throws<ArgumentOutOfRangeException>(()=>store.CopyMaterialBox(new ElementChunkKey(0,0,0),
                Vector3Int.zero,Vector3Int.one*2,MaterialId.Steam,buffer,1,2,4));
        }
    }
}
