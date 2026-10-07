using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialPhaseSolidMaskTests
    {
        [Test]
        public void CoarseLiquidCellDetectsOffCenterIceWithoutPermanentTerrainBake()
        {
            var host = new GameObject("Off Center Ice Test"); host.SetActive(false);
            var profile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":1,\"_maximumNavigationBoxesPerChunk\":2}", profile);
            try
            {
                var solid = host.AddComponent<FixedSolidWorldRuntime>();
                typeof(FixedSolidWorldRuntime).GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(solid, profile);
                Assert.That(solid.TryInitialize(null, null, Vector3.one * 100, .5f, 2), Is.True);
                var baker = host.AddComponent<ElementFieldSolidBaker>();
                typeof(ElementFieldSolidBaker).GetField("_solidLayerMask", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(baker, (LayerMask)(1 << 8));
                baker.SetMaterialSolids(solid, Vector3.one * 100, .5f);
                host.SetActive(true);
                var request = new ElementWriteRequest(Vector3.one * 100 + Vector3.one * .1f, MaterialId.Ice, 512, 0, false);
                solid.TryEnqueueDeposit(in request); solid.Step(); Physics.SyncTransforms();
                Assert.That(baker.SampleEnvironmentSolidWorldCell(Vector3Int.zero, Vector3.one * 100, 1), Is.False);
                // 粗格中心在细格 (1,1,1)，真实冰在 (0,0,0)；不能只查中心而漏过它。
                Assert.That(baker.SampleSolidWorldCell(Vector3Int.zero, Vector3.one * 100, 1), Is.True);
                Assert.That(solid.TryRemove(Vector3Int.zero, MaterialId.Ice, 512), Is.True); Physics.SyncTransforms();
                Assert.That(baker.SampleSolidWorldCell(Vector3Int.zero, Vector3.one * 100, 1), Is.False);
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
    }
}
