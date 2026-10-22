using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// World Solid Bake 必须使用 Global Cell 坐标计算世界中心；尤其是负 Chunk Key，
    /// 不能直接把 Local Cell 当世界坐标，否则玩家跨过 World Origin 后地板会整体错位。
    /// </summary>
    public sealed class ElementWorldSolidBakerTests
    {
        private GameObject _bakerObject;
        private GameObject _solidObject;

        [TearDown]
        public void TearDown()
        {
            if (_solidObject != null)
                Object.DestroyImmediate(_solidObject);
            if (_bakerObject != null)
                Object.DestroyImmediate(_bakerObject);
        }

        [Test]
        public void BakeWorldChunk_MapsNegativeChunkKeyToCorrectWorldCell()
        {
            const int chunkSize = 2;
            Vector3 origin = new Vector3(10f, 20f, 30f);
            var key = new ElementChunkKey(-1, 0, 1);
            var store = new ElementWorldStore(chunkSize, maximumResidentChunks: 4);
            Assert.That(store.TryGetOrCreateChunk(key, out ElementWorldChunk chunk), Is.True);

            _solidObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _solidObject.name = "WorldSolidBakerTestCollider";
            _solidObject.transform.position = new Vector3(9.5f, 20.5f, 32.5f);
            _solidObject.transform.localScale = Vector3.one * 0.8f;

            _bakerObject = new GameObject("WorldSolidBakerTestRoot");
            ElementFieldSolidBaker baker = _bakerObject.AddComponent<ElementFieldSolidBaker>();
            SetPrivateField(baker, "_solidLayerMask", (LayerMask)(1 << _solidObject.layer));
            Physics.SyncTransforms();

            int solidCount = baker.BakeWorldChunk(chunk, origin, cellSize: 1f);

            int expectedIndex = ElementFieldCoordinates.ToIndex(
                new Vector3Int(1, 0, 0),
                chunk.Grid.Dimensions);
            int neighboringIndex = ElementFieldCoordinates.ToIndex(
                new Vector3Int(0, 0, 0),
                chunk.Grid.Dimensions);
            Assert.That(solidCount, Is.EqualTo(1));
            Assert.That(chunk.SolidMask[expectedIndex], Is.True);
            Assert.That(chunk.SolidMask[neighboringIndex], Is.False);
        }

        [Test]
        public void DefaultCubeBlocksGroundOnlyMaskButRigidbodyAndTriggerDoNot()
        {
            _solidObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _solidObject.transform.position = new Vector3(90.5f, 120.5f, 80.5f);
            _solidObject.transform.localScale = Vector3.one * .8f;
            _bakerObject = new GameObject("Default environment sampling");
            var baker = _bakerObject.AddComponent<ElementFieldSolidBaker>();
            SetPrivateField(baker,"_solidLayerMask",(LayerMask)(1<<8));
            Physics.SyncTransforms();
            var cell = new Vector3Int(90,120,80);
            Assert.That(baker.IsStaticSolidCell(cell,Vector3.zero,1f),Is.True);
            _solidObject.GetComponent<BoxCollider>().isTrigger=true;
            Assert.That(baker.IsStaticSolidCell(cell,Vector3.zero,1f),Is.False);
            _solidObject.GetComponent<BoxCollider>().isTrigger=false;
            _solidObject.AddComponent<Rigidbody>().isKinematic=true;
            Assert.That(baker.IsStaticSolidCell(cell,Vector3.zero,1f),Is.False);
        }

        private static void SetPrivateField<T>(object target, string fieldName, T value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }
    }
}
