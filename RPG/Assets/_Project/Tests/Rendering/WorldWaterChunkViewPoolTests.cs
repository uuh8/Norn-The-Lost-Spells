using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 验证 Presentation View 的生命周期与 Gameplay Chunk 数据分离。
    /// Chunk 离开显示范围时只回收/禁用 View；再次进入时复用 Mesh/GameObject，而不是重新分配。
    /// </summary>
    public sealed class WorldWaterChunkViewPoolTests
    {
        private GameObject _root;
        private WorldWaterChunkViewPool _pool;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("WorldWaterChunkViewPoolTests.Root");
            _pool = new WorldWaterChunkViewPool(
                _root.transform,
                sharedMaterial: null,
                maximumViews: 1,
                new WaterVolumeMeshingSettings(
                    samplesPerCell: 2,
                    supportedCornerRadius: 0.16f,
                    minimumSupportedHeight: 0.08f,
                    minimumAirborneRadius: 0.30f,
                    maximumAirborneRadius: 0.48f,
                    smoothUnionRadius: 0.16f),
                chunkSize: 2);
        }

        [TearDown]
        public void TearDown()
        {
            _pool?.Dispose();
            if (_root != null)
                Object.DestroyImmediate(_root);
        }

        [Test]
        public void ReleasedViewIsDisabledThenReusedForAnotherChunk()
        {
            var firstKey = new ElementChunkKey(1, 0, 2);
            Assert.That(
                _pool.TryAcquire(firstKey, new Vector3(2f, 0f, 4f), out WorldWaterChunkView first),
                Is.True);
            Assert.That(first.GameObject.activeSelf, Is.True);

            _pool.Release(firstKey);
            Assert.That(first.GameObject.activeSelf, Is.False,
                "回收 View 只关闭 Presentation，不删除 Gameplay Chunk 数据。");

            var secondKey = new ElementChunkKey(3, 0, 4);
            Assert.That(
                _pool.TryAcquire(secondKey, new Vector3(6f, 0f, 8f), out WorldWaterChunkView second),
                Is.True);

            Assert.That(second, Is.SameAs(first),
                "重新进入显示范围时必须复用已有 Mesh/GameObject，不能反复 Instantiate。");
            Assert.That(second.Normals, Is.SameAs(first.Normals),
                "回收 View 只能清空 List.Count，不能替换 Normal Buffer。");
            Assert.That(second.MeshingWorkspace, Is.SameAs(first.MeshingWorkspace),
                "Surface Nets Workspace 含固定数组，Streaming 时必须复用而不能重新分配。");
            Assert.That(second.Key, Is.EqualTo(secondKey));
            Assert.That(second.Transform.position, Is.EqualTo(new Vector3(6f, 0f, 8f)));
            Assert.That(_pool.CreatedCount, Is.EqualTo(1));
        }

        [Test]
        public void PoolNeverCreatesBeyondMaximumViewBudget()
        {
            Assert.That(
                _pool.TryAcquire(new ElementChunkKey(0, 0, 0), Vector3.zero, out _),
                Is.True);
            Assert.That(
                _pool.TryAcquire(new ElementChunkKey(1, 0, 0), Vector3.right, out _),
                Is.False);
            Assert.That(_pool.CreatedCount, Is.EqualTo(1));
        }
    }
}
