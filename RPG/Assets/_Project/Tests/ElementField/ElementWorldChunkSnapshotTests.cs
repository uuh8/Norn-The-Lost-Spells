using System;
using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>验证批量只读边界：相同 Cell 顺序、不暴露内部数组、不隐式创建 Chunk、零分配。</summary>
    public sealed class ElementWorldChunkSnapshotTests
    {
        private GameObject _host;
        private ElementWorldProfile _profile;
        private ElementWorldRuntime _world;
        private ElementWorldStore _store;
        private readonly ElementChunkKey _key = new ElementChunkKey(-1,2,1);
        private Func<ElementChunkKey,ElementCell[],int> _copy;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("Read-only Chunk Snapshot test"); _host.SetActive(false);
            _world = _host.AddComponent<ElementWorldRuntime>();
            _profile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            _store = new ElementWorldStore(8,8);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ElementWorldRuntime).GetField("_store",fields).SetValue(_world,_store);
            typeof(ElementWorldRuntime).GetField("_profile",fields).SetValue(_world,_profile);
            typeof(ElementWorldRuntime).GetProperty("IsInitialized").SetValue(_world,true);
            _store.TryGetOrCreateChunk(_key,out var chunk);
            for(int z=0;z<8;z++) for(int y=0;y<8;y++) for(int x=0;x<8;x++)
                chunk.SetCell(new Vector3Int(x,y,z), new ElementCell((x+y+z)%3==0?MaterialId.Fire:MaterialId.Water,(byte)(1+x+y*8+z*16)));
            MethodInfo method = typeof(ElementWorldRuntime).GetMethod("CopyChunkCells");
            Assert.That(method,Is.Not.Null,"需要调用方持有 Buffer 的批量只读入口。");
            _copy = (Func<ElementChunkKey,ElementCell[],int>)Delegate.CreateDelegate(typeof(Func<ElementChunkKey,ElementCell[],int>),_world,method);
        }
        [TearDown]
        public void TearDown() { if(_host!=null) Object.DestroyImmediate(_host); if(_profile!=null)Object.DestroyImmediate(_profile); }

        [Test]
        public void SnapshotMatchesScalarQueriesAndDoesNotExposeInternalArray()
        {
            var cells = new ElementCell[512]; Assert.That(_copy(_key,cells),Is.EqualTo(512));
            for(int z=0;z<8;z++) for(int y=0;y<8;y++) for(int x=0;x<8;x++)
            {
                var global = ElementWorldCoordinates.ComposeGlobalCell(_key,new Vector3Int(x,y,z),8);
                Assert.That(_world.TryGetCell(global,out var expected),Is.True);
                ElementCell copied = cells[(z*8+y)*8+x];
                Assert.That(copied.MaterialKind,Is.EqualTo(expected.MaterialKind)); Assert.That(copied.Amount,Is.EqualTo(expected.Amount));
            }
            cells[0]=default;
            Assert.That(_world.TryGetCell(new Vector3Int(-8,16,8),out var retained),Is.True);
            Assert.That(retained.MaterialKind,Is.EqualTo(MaterialId.Fire)); Assert.That(retained.Amount,Is.EqualTo(1));
        }
        [Test]
        public void MissingChunkAndInvalidBufferDoNotMutateWorld()
        {
            var cells = new ElementCell[512]; cells[0]=new ElementCell(MaterialId.Poison,42);
            Assert.That(_copy(new ElementChunkKey(90,90,90),cells),Is.Zero);
            Assert.That(_store.ResidentChunkCount,Is.EqualTo(1)); Assert.That(cells[0].Amount,Is.EqualTo(42));
            Assert.Throws<ArgumentNullException>(()=>_copy(_key,null));
            Assert.Throws<ArgumentException>(()=>_copy(_key,new ElementCell[511]));
        }
        [Test]
        public void RepeatedWarmCopyAllocatesZeroBytes()
        {
            var cells = new ElementCell[512]; _copy(_key,cells);
            long start=GC.GetAllocatedBytesForCurrentThread(); int total=0;
            for(int i=0;i<100;i++) total+=_copy(_key,cells);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-start;
            Assert.That(total,Is.EqualTo(51200)); Assert.That(allocated,Is.Zero);
        }
    }
}
