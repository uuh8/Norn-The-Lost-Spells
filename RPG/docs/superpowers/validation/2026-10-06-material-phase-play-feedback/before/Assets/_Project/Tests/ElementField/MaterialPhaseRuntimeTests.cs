using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialPhaseRuntimeTests
    {
        private static void Profile(object target, Object value) => target.GetType().GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        [Test]
        public void SolidPublishesColliderAndRemovalClearsSupportWithoutRepeatedRebuild()
        {
            var host = new GameObject("Solid Physics Test"); host.SetActive(false);
            var profile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":2,\"_maximumNavigationBoxesPerChunk\":8}", profile);
            try
            {
                var runtime = host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime, profile);
                Assert.That(runtime.TryInitialize(null, null, Vector3.one * 100, 1, 2), Is.True);
                host.SetActive(true); // Physics 只注册 activeInHierarchy 的 Collider。
                var request = new ElementWriteRequest(Vector3.one * 100 + Vector3.one * .1f, MaterialId.Ice, 512, 0, false);
                Assert.That(runtime.TryEnqueueDeposit(in request), Is.True); runtime.Step(); Physics.SyncTransforms();
                Assert.That(runtime.TotalQuantity, Is.EqualTo(512UL));
                Assert.That(runtime.TryGetSurface(0, out Mesh mesh, out _), Is.True);
                Assert.That(mesh.bounds.max.y, Is.EqualTo(.5f).Within(.001f));
                Assert.That(Physics.Raycast(new Vector3(100.5f, 102, 100.5f), Vector3.down, out var hit, 2, 1 << 8), Is.True);
                Assert.That(hit.point.y, Is.EqualTo(100.5f).Within(.01f));
                uint rebuilds = runtime.SurfaceRebuildCount;
                for (int i = 0; i < 300; i++) runtime.Step();
                Assert.That(runtime.SurfaceRebuildCount, Is.EqualTo(rebuilds));
                Assert.That(runtime.TryRemove(Vector3Int.zero, MaterialId.Ice, 512), Is.True); Physics.SyncTransforms();
                Assert.That(Physics.Raycast(new Vector3(100.5f, 102, 100.5f), Vector3.down, 2, 1 << 8), Is.False);
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void SolidDepositAvoidsCharacterBodyAndRetainsPendingQuantity()
        {
            var host = new GameObject("Solid Actor Test"); host.SetActive(false);
            var actor = new GameObject("Actor"); actor.transform.position = new Vector3(100.5f, 100, 100.5f);
            var controller = actor.AddComponent<CharacterController>(); controller.center = Vector3.up; controller.height = 2;
            var profile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":1,\"_maximumNavigationBoxesPerChunk\":2}", profile);
            try
            {
                var runtime = host.AddComponent<FixedSolidWorldRuntime>(); Profile(runtime, profile);
                Assert.That(runtime.TryInitialize(null, null, Vector3.one * 100, 1, 2), Is.True); Physics.SyncTransforms();
                var request = new ElementWriteRequest(Vector3.one * 100 + Vector3.one * .1f, MaterialId.Ice, 512, 0, false);
                Assert.That(runtime.TryEnqueueDeposit(in request), Is.True); runtime.Step();
                Assert.That(runtime.TotalQuantity, Is.Zero); Assert.That(runtime.PendingQuantity, Is.EqualTo(512UL));
                actor.transform.position += Vector3.right * 5; Physics.SyncTransforms(); runtime.Step();
                Assert.That(runtime.TotalQuantity, Is.EqualTo(512UL)); Assert.That(runtime.PendingQuantity, Is.Zero);
            }
            finally { Object.DestroyImmediate(actor); Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
        [Test]
        public void GasRuntimeRetainsQuantityWhileFrozenAndContinuesAfterReturn()
        {
            var host = new GameObject("Gas Runtime Test"); host.SetActive(false);
            var profile = ScriptableObject.CreateInstance<GasWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32}", profile);
            try
            {
                var runtime = host.AddComponent<GasWorldRuntime>(); Profile(runtime, profile);
                Assert.That(runtime.TryInitialize(null, null, Vector3.one * 100, 1, 2), Is.True);
                var domain = new ElementWorldActivityRegions(); domain.TryAdd(new Bounds(Vector3.zero, Vector3.one * 4)); runtime.SetActivityDomain(domain);
                var request = new ElementWriteRequest(Vector3.one * 100 + Vector3.one * .1f, MaterialId.Steam, 2048, 0, false);
                Assert.That(runtime.TryEnqueueDeposit(in request), Is.True);
                for (int i = 0; i < 300; i++) runtime.Step(.1f);
                runtime.TryGetQuantity(Vector3Int.zero, MaterialId.Steam, out uint amount);
                Assert.That(amount, Is.EqualTo(2048)); Assert.That(runtime.TotalQuantity, Is.EqualTo(2048UL));
                domain.Clear(); domain.TryAdd(new Bounds(Vector3.one * 100, Vector3.one * 16)); runtime.SetActivityDomain(domain); runtime.Step(.1f);
                Assert.That(runtime.TryGetQuantity(Vector3Int.up, MaterialId.Steam, out _), Is.True);
                Assert.That(runtime.TotalQuantity, Is.EqualTo(2048UL));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(profile); }
        }
    }
}
