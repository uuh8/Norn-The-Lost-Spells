using Game.Materials;
using System.Collections.Generic;
using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 先锁定稀疏世界 Water Mesh 最危险的两个跨 Chunk 不变量：
    /// 邻页必须参与剔面，UV 必须来自 Global Cell，而不是每个 Chunk 从零开始。
    /// </summary>
    public sealed class WorldWaterChunkMeshBuilderTests
    {
        private readonly List<Vector3> _vertices = new List<Vector3>(128);
        private readonly List<Vector2> _uvs = new List<Vector2>(128);
        private readonly List<int> _indices = new List<int>(192);

        [SetUp]
        public void SetUp()
        {
            _vertices.Clear();
            _uvs.Clear();
            _indices.Clear();
        }

        [Test]
        public void AdjacentWaterAcrossChunkBoundaryDoesNotCreateInternalSide()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 2);
            world.SetWater(new Vector3Int(1, 0, 0), byte.MaxValue);
            world.SetWater(new Vector3Int(2, 0, 0), byte.MaxValue);

            WaterChunkMeshBuilder.BuildWorldChunk(
                world,
                new ElementChunkKey(0, 0, 0),
                _vertices,
                _uvs,
                _indices);

            // 当前 Chunk 中只有 Global X=1 的一格水；+X 邻居位于下一个 Chunk。
            // 若 Builder 只读本页，就会在 Local X=0.5m 生成一堵错误的内部水墙。
            Assert.That(CountVerticalTrianglesAtX(0.5f), Is.Zero);
            Assert.That(_vertices.Count, Is.EqualTo(16),
                "跨页邻居应消除 +X Side，最终只保留 Top、-X、-Z、+Z 四个 Quad。");
        }

        [Test]
        public void UvUsesGlobalCellMetersInsteadOfRestartingAtEachChunk()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 2);
            world.SetWater(new Vector3Int(2, 0, 0), byte.MaxValue);

            WaterChunkMeshBuilder.BuildWorldChunk(
                world,
                new ElementChunkKey(1, 0, 0),
                _vertices,
                _uvs,
                _indices);

            // Chunk(1,0,0) 的第一个 Local Cell 是 Global X=2。
            // uv = globalCellXZ * cellSize，所以顶面 U 范围必须是 0.5m～0.75m，不能回到 0～0.25m。
            float minTopU = float.MaxValue;
            float maxTopU = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                minTopU = Mathf.Min(minTopU, _uvs[i].x);
                maxTopU = Mathf.Max(maxTopU, _uvs[i].x);
            }

            Assert.That(minTopU, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(maxTopU, Is.EqualTo(0.75f).Within(0.0001f));
        }

        private int CountVerticalTrianglesAtX(float x)
        {
            int count = 0;
            for (int triangle = 0; triangle < _indices.Count / 3; triangle++)
            {
                Vector3 a = _vertices[_indices[triangle * 3]];
                Vector3 b = _vertices[_indices[triangle * 3 + 1]];
                Vector3 c = _vertices[_indices[triangle * 3 + 2]];
                if (Mathf.Approximately(a.x, x)
                    && Mathf.Approximately(b.x, x)
                    && Mathf.Approximately(c.x, x))
                {
                    count++;
                }
            }

            return count;
        }

        private sealed class FakeWorld : IElementWorldReadOnly
        {
            private readonly Dictionary<Vector3Int, ElementCell> _cells =
                new Dictionary<Vector3Int, ElementCell>();

            public FakeWorld(float cellSize, int chunkSize)
            {
                CellSize = cellSize;
                ChunkSize = chunkSize;
            }

            public bool IsInitialized => true;
            public Vector3 Origin => Vector3.zero;
            public float CellSize { get; }
            public int ChunkSize { get; }
            public int MaximumResidentChunkCount => 16;

            public int CopyVisibleChunkKeys(ElementChunkKey[] destination)
            {
                return 0;
            }

            public bool TryGetCell(Vector3Int globalCell, out ElementCell cell)
            {
                return _cells.TryGetValue(globalCell, out cell);
            }

            public bool IsSolid(Vector3Int globalCell)
            {
                return false;
            }

            public uint GetChunkVersion(ElementChunkKey key)
            {
                return 1;
            }

            public void SetWater(Vector3Int globalCell, byte amount)
            {
                _cells[globalCell] = new ElementCell(MaterialId.Water, amount);
            }
        }
    }
}
