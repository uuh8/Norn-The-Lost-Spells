using System.Collections.Generic;
using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 锁定体积水的跨 Chunk Dirty 依赖：隐式场会读取三维邻域，
    /// 因而对角 Chunk 改变也必须使中心 Chunk 的视觉签名变化。
    /// </summary>
    public sealed class WorldWaterVisualVersionTests
    {
        [Test]
        public void DiagonalNeighborVersionChangesVisualSignature()
        {
            var world = new FakeWorld();
            var center = new ElementChunkKey(0, 0, 0);
            uint before =
                WorldWaterVisualVersion.Calculate(world, center, 1);

            world.SetVersion(new ElementChunkKey(1, 1, 1), 2u);
            uint after =
                WorldWaterVisualVersion.Calculate(world, center, 1);

            Assert.That(after, Is.Not.EqualTo(before));
        }

        private sealed class FakeWorld : IElementWorldReadOnly
        {
            private readonly Dictionary<ElementChunkKey, uint> _versions =
                new Dictionary<ElementChunkKey, uint>();

            public bool IsInitialized => true;
            public Vector3 Origin => Vector3.zero;
            public float CellSize => 0.25f;
            public int ChunkSize => 16;
            public int MaximumResidentChunkCount => 16;

            public int CopyVisibleChunkKeys(ElementChunkKey[] destination)
            {
                return 0;
            }

            public bool TryGetCell(
                Vector3Int globalCell,
                out ElementCell cell)
            {
                cell = default;
                return false;
            }

            public bool IsSolid(Vector3Int globalCell)
            {
                return false;
            }

            public uint GetChunkVersion(ElementChunkKey key)
            {
                return _versions.TryGetValue(key, out uint version)
                    ? version
                    : 0u;
            }

            public void SetVersion(ElementChunkKey key, uint version)
            {
                _versions[key] = version;
            }
        }
    }
}
