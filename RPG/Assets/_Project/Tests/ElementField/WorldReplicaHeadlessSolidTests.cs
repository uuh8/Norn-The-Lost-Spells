using Game.Materials;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;

namespace Game.ElementField.Tests
{
    public sealed class WorldReplicaHeadlessSolidTests
    {
        [Test]
        public void ActualAuthorityRuntimeSelectsPhysicsOnlyAdapterBeforeInitialization()
        {
            var root = new GameObject("Authority solid ownership test"); root.SetActive(false);
            var profile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            try
            {
                var world = root.AddComponent<ElementWorldRuntime>();
                world.ConfigureExecutionMode(WorldExecutionMode.Authority);
                var solids = root.AddComponent<FixedSolidWorldRuntime>();
                typeof(FixedSolidWorldRuntime).GetField("_profile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(solids, profile);
                Assert.That(solids.TryInitialize(world, root.GetComponent<ElementFieldSolidBaker>(), Vector3.zero, 1, 2), Is.True);
                Assert.That(solids.BuildsVisualMeshes, Is.False);
                Assert.That(root.GetComponentsInChildren<MeshCollider>(true).Length, Is.EqualTo(profile.MaximumChunks));
                for (int i = 0; i < solids.SurfaceCapacity; i++)
                { Assert.That(solids.TryGetSurface(i, out var visual, out _), Is.False); Assert.That(visual, Is.Null); }
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(profile); }
        }
        [TestCase(false)]
        [TestCase(true)]
        public void CpuCollisionAndNavigationSurviveVisualMeshSelection(bool visuals)
        {
            var root = new GameObject("Headless solid geometry test");
            FixedSolidPhysicsAdapter adapter = null;
            try
            {
                var store = new FixedSolidVolumeStore(2, 1, 1024, new[] { MaterialId.Ice });
                Assert.That(store.TryAdd(Vector3Int.zero, MaterialId.Ice, 1024), Is.True);
                adapter = new FixedSolidPhysicsAdapter(root.transform, store, material => material == MaterialId.Ice,
                    4, Vector3.zero, 1, buildVisualMeshes: visuals);
                var key = new ElementChunkKey(0, 0, 0);
                Assert.That(adapter.CanPrepare(key), Is.True);
                adapter.Publish(key);
                Assert.That(adapter.BuildsVisualMeshes, Is.EqualTo(visuals));
                Assert.That(adapter.TryGetSurface(0, out var surface, out _), Is.EqualTo(visuals));
                if (!visuals) Assert.That(surface, Is.Null);
                var collider = root.GetComponentInChildren<MeshCollider>();
                Assert.That(collider, Is.Not.Null);
                Assert.That(collider.sharedMesh, Is.Not.Null);
                Assert.That(collider.sharedMesh.vertexCount, Is.GreaterThan(0));
                Assert.That(root.GetComponentInChildren<NavMeshObstacle>().enabled, Is.True);
                Assert.That(root.GetComponentsInChildren<MeshRenderer>(true), Is.Empty);
            }
            finally { adapter?.Dispose(); Object.DestroyImmediate(root); }
        }
    }
}
