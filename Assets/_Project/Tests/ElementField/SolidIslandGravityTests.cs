using System;
using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>整块真实GMU移动，不以Rigidbody模型掉落替代Store/Collider位置。</summary>
    public sealed class SolidIslandGravityTests
    {
        private GameObject _host,_floor;
        private FixedSolidWorldProfile _profile;
        private FixedSolidWorldRuntime _runtime;
        private FixedSolidVolumeStore _store;
        [SetUp] public void Setup()
        {
            _host=new GameObject("Gravity actual solid owner");_host.SetActive(false);
            _profile=ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_cellCapacityGmu\":64,\"_maximumColliderChunksPerTick\":8,\"_enableGravity\":true}",_profile);
            var baker=_host.AddComponent<ElementFieldSolidBaker>();_runtime=_host.AddComponent<FixedSolidWorldRuntime>();
            typeof(FixedSolidWorldRuntime).GetField("_profile",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_runtime,_profile);
            Assert.That(_runtime.TryInitialize(null,baker,Vector3.one*100,.25f,4),Is.True);
            _store=(FixedSolidVolumeStore)typeof(FixedSolidWorldRuntime).GetField("_store",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(_runtime);
            _floor=GameObject.CreatePrimitive(PrimitiveType.Cube);_floor.transform.position=new Vector3(101,99.75f,101);_floor.transform.localScale=new Vector3(8,.5f,8);Physics.SyncTransforms();
        }
        [TearDown] public void Cleanup(){UnityEngine.Object.DestroyImmediate(_host);UnityEngine.Object.DestroyImmediate(_floor);UnityEngine.Object.DestroyImmediate(_profile);}
        private void Step(float dt)
        {var method=typeof(FixedSolidWorldRuntime).GetMethod("StepGravity");Assert.That(method,Is.Not.Null,"固体Owner缺少整块重力入口");method.Invoke(_runtime,new object[]{dt});}
        [TestCase(MaterialId.Ice)] [TestCase(MaterialId.DriedSlime)]
        public void WholeCubeFallsAcrossChunksAndRetainsEveryCellAndMass(MaterialId material)
        {
            for(int x=3;x<=4;x++)for(int y=8;y<=9;y++)for(int z=1;z<=2;z++)Assert.That(_store.TryAdd(new Vector3Int(x,y,z),material,64),Is.True);
            for(int tick=0;tick<40;tick++)Step(.05f);
            Assert.That(_runtime.TotalQuantity,Is.EqualTo(512UL));
            for(int x=3;x<=4;x++)for(int y=0;y<=1;y++)for(int z=1;z<=2;z++)
                Assert.That(_runtime.TryGetQuantity(new Vector3Int(x,y,z),material,out var quantity)&&quantity==64,Is.True,"整块形状不能变成砂粒");
            Assert.That(_runtime.SurfaceRebuildCount,Is.GreaterThan(0),"真实Collider/显示几何必须一起发布");
        }
        [Test] public void RemovingSupportWakesWholeIslandAndNoMaterialDisappears()
        {
            _store.TryAdd(new Vector3Int(1,2,1),MaterialId.Ice,64);
            var shelf=GameObject.CreatePrimitive(PrimitiveType.Cube);shelf.transform.position=new Vector3(100.375f,100.375f,100.375f);shelf.transform.localScale=Vector3.one*.25f;Physics.SyncTransforms();
            try
            {
                for(int i=0;i<10;i++)Step(.05f);Assert.That(_runtime.TryGetQuantity(new Vector3Int(1,2,1),MaterialId.Ice,out _),Is.True);
                shelf.SetActive(false);Physics.SyncTransforms();for(int i=0;i<40;i++)Step(.05f);
                Assert.That(_runtime.TryGetQuantity(new Vector3Int(1,0,1),MaterialId.Ice,out var quantity)&&quantity==64,Is.True);
                Assert.That(_runtime.TotalQuantity,Is.EqualTo(64UL));
            }
            finally{UnityEngine.Object.DestroyImmediate(shelf);}
        }
    }
}
