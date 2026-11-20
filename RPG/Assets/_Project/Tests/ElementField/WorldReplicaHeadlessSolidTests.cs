using Game.Materials;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;

namespace Game.ElementField.Tests
{
    /// <summary>真实只读Store与Collider的生命周期回归：Key退出不能借列表压缩关闭别的地形。</summary>
    public sealed class WorldReplicaSolidSlotTests
    {
        private GameObject _root;
        private FixedSolidWorldProfile _profile;
        private WorldReplicaStore _store;
        private WorldReplicaSolidReadModel _surface;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Replica stable solid slots");
            _profile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":3,\"_cellCapacityGmu\":64,\"_maximumNavigationBoxesPerChunk\":4}", _profile);
            _store = new WorldReplicaStore(7, 8, 32, 0, 256);
            var geometry = new WorldGridGeometry(WorldMaterialDomain.Solid, new Vector3(12000, 12000, 12000), 1, 2);
            Assert.That(_store.ConfigureGeometry(geometry), Is.True);
            Assert.That(_store.ConfigureMaterials(WorldMaterialDomain.Solid, 64, new[] { (byte)MaterialId.Ice }, true, 3), Is.True);
            _surface = new WorldReplicaSolidReadModel(_store, geometry, _profile, _root.transform);
        }

        [TearDown]
        public void TearDown()
        {
            _surface?.Dispose();
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_profile);
        }

        [Test]
        public void EvictingFirstChunkKeepsUnchangedCollidersAndMeshesLive()
        {
            Install(0); Install(3); Install(6);
            for (int i = 0; i < 3; i++) _surface.PresentOneChunk();
            var middle = ColliderAt(3); var last = ColliderAt(6);
            Mesh middleMesh = middle.sharedMesh, lastMesh = last.sharedMesh;
            Assert.That(_store.TryEvictInterest(new WorldChunkKey(WorldMaterialDomain.Solid, 0, 0, 0)), Is.True);

            _surface.PresentOneChunk();

            AssertLive(middle, middleMesh); AssertLive(last, lastMesh);
            Assert.That(ActiveColliders(), Is.EqualTo(2));
        }

        [Test]
        public void NewKeyReusesRetiredSlotWithoutRebindingSurvivingGeometry()
        {
            Install(0); Install(3); Install(6);
            for (int i = 0; i < 3; i++) _surface.PresentOneChunk();
            var middle = ColliderAt(3); var last = ColliderAt(6);
            Mesh middleMesh = middle.sharedMesh, lastMesh = last.sharedMesh;
            Assert.That(_store.TryEvictInterest(new WorldChunkKey(WorldMaterialDomain.Solid, 0, 0, 0)), Is.True);
            Install(9);
            Assert.That(_store.TryGetQuantity(WorldMaterialDomain.Solid, new Vector3Int(18, 0, 0), (byte)MaterialId.Ice, out uint installed), Is.True);
            Assert.That(installed, Is.EqualTo(64), "先证明Replica已安装新Key的真实数量。");

            _surface.PresentOneChunk();

            AssertLive(middle, middleMesh); AssertLive(last, lastMesh);
            var replacement = ColliderAt(9);
            Assert.That(replacement.sharedMesh, Is.Not.Null, DescribeSlots());
            AssertLive(replacement, replacement.sharedMesh);
            Assert.That(ActiveColliders(), Is.EqualTo(3), DescribeSlots());
            Assert.That(replacement.sharedMesh.vertexCount, Is.GreaterThan(0));
        }

        [Test]
        public void PresentationStepBuildsOnlyOneNewChunk()
        {
            Install(0); Install(3); Install(6);
            _surface.PresentOneChunk(); Assert.That(ActiveColliders(), Is.EqualTo(1));
            _surface.PresentOneChunk(); Assert.That(ActiveColliders(), Is.EqualTo(2));
            _surface.PresentOneChunk(); Assert.That(ActiveColliders(), Is.EqualTo(3));
        }

        private void Install(int x)
        {
            var words = new uint[18]; words[0] = WorldChunkPayloadCodec.Magic; words[1] = 1; words[3] = 1;
            WorldChunkPayloadCodec.WriteGeometry(words, 4, new WorldGridGeometry(WorldMaterialDomain.Solid, new Vector3(12000, 12000, 12000), 1, 2));
            WorldChunkPayloadCodec.WriteCell(words, 11, new WorldMaterialCellSample(WorldMaterialDomain.Solid,
                WorldMaterialStorage.Resident, new Vector3Int(x * 2, 0, 0), (byte)MaterialId.Ice, 64));
            var header = new WorldChunkPayloadHeader(7, (ulong)x + 1, new WorldChunkKey(WorldMaterialDomain.Solid, x, 0, 0),
                1, 0, 1, 1, words.Length, WorldChunkPayloadCodec.ComputeChecksum(words, words.Length), true);
            Assert.That(_store.TryInstall(header, words, words.Length), Is.True);
        }

        private MeshCollider ColliderAt(int x)
        {
            foreach (var collider in _root.GetComponentsInChildren<MeshCollider>(true))
                if (collider.gameObject.activeSelf && collider.transform.position == new Vector3(12000 + x * 2, 12000, 12000)) return collider;
            Assert.Fail("缺少已发布Chunk的碰撞：" + x); return null;
        }

        private int ActiveColliders()
        {
            int count = 0;
            foreach (var collider in _root.GetComponentsInChildren<MeshCollider>(true))
                if (collider.gameObject.activeSelf && collider.sharedMesh != null) count++;
            return count;
        }

        private string DescribeSlots()
        {
            var text = new System.Text.StringBuilder("GeometryVersion=").Append(_surface.GeometryVersion);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var keys = (ElementChunkKey[])typeof(WorldReplicaSolidReadModel).GetField("_boundKeys", flags).GetValue(_surface);
            var versions = (uint[])typeof(WorldReplicaSolidReadModel).GetField("_versions", flags).GetValue(_surface);
            var bound = (bool[])typeof(WorldReplicaSolidReadModel).GetField("_bound", flags).GetValue(_surface);
            var collisions = (Mesh[])typeof(WorldReplicaSolidReadModel).GetField("_collision", flags).GetValue(_surface);
            var colliders = (MeshCollider[])typeof(WorldReplicaSolidReadModel).GetField("_colliders", flags).GetValue(_surface);
            for (int i = 0; i < _surface.SurfaceCapacity; i++)
            {
                bool visible = _surface.TryGetSurface(i, out var mesh, out var position);
                text.Append("; slot=").Append(i).Append(" key=").Append(keys[i]).Append(" bound=").Append(bound[i])
                    .Append(" version=").Append(versions[i]).Append(" visible=").Append(visible)
                    .Append(" vertices=").Append(mesh.vertexCount).Append(" position=").Append(position)
                    .Append(" collisionVertices=").Append(collisions[i].vertexCount)
                    .Append(" sharedMesh=").Append(colliders[i].sharedMesh != null ? colliders[i].sharedMesh.name : "null")
                    .Append(" colliderEnabled=").Append(colliders[i].enabled)
                    .Append(" parent=").Append(colliders[i].transform.parent == _root.transform);
            }
            return text.ToString();
        }

        private static void AssertLive(MeshCollider collider, Mesh mesh)
        {
            Assert.That(collider.gameObject.activeInHierarchy, Is.True, "另一个Key退出不得关闭仍有效的Collider。");
            Assert.That(collider.sharedMesh, Is.SameAs(mesh), "未变化的Key应保留原几何，不能按压缩列表位置重绑。");
            Physics.SyncTransforms();
            Assert.That(collider.Raycast(new Ray(collider.transform.position + new Vector3(.5f, 2, .5f), Vector3.down), out _, 3), Is.True);
        }
    }

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
