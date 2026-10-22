using System;
using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
namespace Game.ElementField.Tests
{
    /// <summary>真实Cell运动与衰减分别断言，不能用GPU粒子的向下漂移冒充材料运动。</summary>
    public sealed class FireGravityTests
    {
        private static ElementFieldSimulationSettings Settings(bool water,byte decay=0)
        {
            var ctor=typeof(ElementFieldSimulationSettings).GetConstructor(new[]{typeof(float),typeof(byte),typeof(byte),typeof(byte),typeof(int),typeof(bool),typeof(ExtinguishTuning),typeof(byte)});
            Assert.That(ctor,Is.Not.Null,"独立火焰下落配置不能绑在Water后端开关上");
            return (ElementFieldSimulationSettings)ctor.Invoke(new object[]{1f,(byte)0,(byte)0,decay,2,water,default(ExtinguishTuning),(byte)255});
        }
        private static ElementWorldSimulator Solver(int capacity=16)
        {
            var catalog=AssetDatabase.LoadAssetAtPath<MaterialCatalog>("Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
            var bindings=AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>("Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset");
            return new ElementWorldSimulator(capacity,bindings.CreateSnapshot(catalog.CreateSnapshot()));
        }
        private static ElementWorldChunk Chunk(ElementWorldStore store,ElementChunkKey key)
        {Assert.That(store.TryGetOrCreateChunk(key,out var chunk),Is.True);chunk.ActivityState=ElementChunkActivityState.Active;return chunk;}
        [TestCase(false)][TestCase(true)]public void FireFallsAcrossChunkBoundaryWithEitherWaterBackendAndConservesIntensity(bool water)
        {
            var settings=Settings(water);var store=new ElementWorldStore(2,16);var source=Chunk(store,new ElementChunkKey(0,1,0));source.SetCell(Vector3Int.zero,new ElementCell(MaterialId.Fire,200));
            Solver().SimulateActiveChunks(store,new[]{source},1,1,.1f,in settings);
            Assert.That(store.TryGetChunk(new ElementChunkKey(0,0,0),out var target),Is.True);Assert.That(source.GetCell(Vector3Int.zero).IsEmpty,Is.True);
            Assert.That(target.GetCell(new Vector3Int(0,1,0)).MaterialKind,Is.EqualTo(MaterialId.Fire));Assert.That(target.GetCell(new Vector3Int(0,1,0)).Amount,Is.EqualTo(200));
        }
        [Test]public void SolidSupportStopsFireAndRemovingItResumesFall()
        {
            var settings=Settings(false);var store=new ElementWorldStore(2,16);var source=Chunk(store,default);source.SetCell(Vector3Int.up,new ElementCell(MaterialId.Fire,200));source.SolidMask[0]=true;
            var solver=Solver();var active=new[]{source};solver.SimulateActiveChunks(store,active,1,1,.1f,in settings);Assert.That(source.GetCell(Vector3Int.up).Amount,Is.EqualTo(200));
            source.SolidMask[0]=false;solver.SimulateActiveChunks(store,active,1,2,.1f,in settings);Assert.That(source.GetCell(Vector3Int.zero).Amount,Is.EqualTo(200));
        }
        [Test]public void ChunkCapacityFailureDoesNotEraseFire()
        {
            var settings=Settings(false);var store=new ElementWorldStore(2,1);var source=Chunk(store,default);source.SetCell(Vector3Int.zero,new ElementCell(MaterialId.Fire,200));
            Solver(1).SimulateActiveChunks(store,new[]{source},1,1,.1f,in settings);Assert.That(source.GetCell(Vector3Int.zero).Amount,Is.EqualTo(200));
        }
        [Test]public void FallingFireDecaysOnlyOncePerTick()
        {
            var settings=Settings(false,3);var store=new ElementWorldStore(2,16);var source=Chunk(store,default);source.SetCell(Vector3Int.up,new ElementCell(MaterialId.Fire,200));
            Solver().SimulateActiveChunks(store,new[]{source},1,1,.1f,in settings);Assert.That(source.GetCell(Vector3Int.zero).Amount,Is.EqualTo(197));
        }
        [Test]public void WarmFireFallHasNoManagedAllocation()
        {
            var settings=Settings(false);var store=new ElementWorldStore(2,16);var source=Chunk(store,default);var solver=Solver();var active=new[]{source};
            for(int i=0;i<4;i++){source.SetCell(Vector3Int.zero,default);source.SetCell(Vector3Int.up,new ElementCell(MaterialId.Fire,200));solver.SimulateActiveChunks(store,active,1,i,.1f,in settings);}
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=4;i<20;i++){source.SetCell(Vector3Int.zero,default);source.SetCell(Vector3Int.up,new ElementCell(MaterialId.Fire,200));solver.SimulateActiveChunks(store,active,1,i,.1f,in settings);}
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;Assert.That(source.GetCell(Vector3Int.zero).Amount,Is.EqualTo(200));Assert.That(allocated,Is.Zero,"仅测预热后纯Solver真实下落调用，场景Profiler另测");
        }
    }
}
