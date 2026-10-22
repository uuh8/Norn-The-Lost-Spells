using Game.Materials;
using System.Collections.Generic;
using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 验证离散 Water Cell 到 Mesh 拓扑的纯转换。
    /// 测试不创建 GameObject、MeshRenderer 或 Material，先把“几何在哪里”与“怎样渲染”分开。
    /// </summary>
    public sealed class WaterChunkMeshBuilderTests
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
        public void SingleFullWaterCellCreatesTopAndExposedSides()
        {
            FakeField field = CreateField(new Vector3Int(1, 1, 1), cellSize: 1f, chunkSize: 1);
            field.SetWater(0, 0, 0, byte.MaxValue);

            WaterChunkMeshBuilder.Build(field, Vector3Int.zero, _vertices, _uvs, _indices);

            // 不生成底面：一个孤立水格 = 1 个 Top + 4 个 Side = 5 Quads。
            Assert.That(_vertices.Count, Is.EqualTo(20));
            Assert.That(_uvs.Count, Is.EqualTo(20));
            Assert.That(_indices.Count, Is.EqualTo(30));

            Vector3 topNormal = TriangleNormal(_vertices, _indices, triangleIndex: 0);
            Assert.That(topNormal.y, Is.GreaterThan(0.999f),
                "顶面 Triangle Winding 必须让 Front Face 与 RecalculateNormals 都朝向 +Y。");
        }

        [Test]
        public void AdjacentWaterCellsDoNotCreateInternalFace()
        {
            FakeField field = CreateField(new Vector3Int(2, 1, 1), cellSize: 1f, chunkSize: 2);
            field.SetWater(0, 0, 0, byte.MaxValue);
            field.SetWater(1, 0, 0, byte.MaxValue);

            WaterChunkMeshBuilder.Build(field, Vector3Int.zero, _vertices, _uvs, _indices);

            // 两格各有 Top；外轮廓共有 6 个 Side。内部接触面不能生成两遍。
            Assert.That(_vertices.Count, Is.EqualTo(8 * 4));
            Assert.That(_indices.Count, Is.EqualTo(8 * 6));
            Assert.That(CountVerticalTrianglesAtX(1f), Is.Zero,
                "相同高度的相邻 Water Cell 之间不应存在内部三角形。");
        }

        [Test]
        public void HalfAmountPlacesTopAtHalfCellHeight()
        {
            FakeField field = CreateField(new Vector3Int(1, 1, 1), cellSize: 1f, chunkSize: 1);
            field.SetWater(0, 0, 0, amount: 128);

            WaterChunkMeshBuilder.Build(field, Vector3Int.zero, _vertices, _uvs, _indices);

            float expectedHeight = 128f / byte.MaxValue;
            Assert.That(_vertices[0].y, Is.EqualTo(expectedHeight).Within(0.0001f));
            Assert.That(_vertices[1].y, Is.EqualTo(expectedHeight).Within(0.0001f));
            Assert.That(_vertices[2].y, Is.EqualTo(expectedHeight).Within(0.0001f));
            Assert.That(_vertices[3].y, Is.EqualTo(expectedHeight).Within(0.0001f));
        }

        [Test]
        public void UvIsContinuousAcrossCellBoundary()
        {
            FakeField field = CreateField(new Vector3Int(2, 1, 1), cellSize: 1f, chunkSize: 2);
            field.SetWater(0, 0, 0, byte.MaxValue);
            field.SetWater(1, 0, 0, byte.MaxValue);

            WaterChunkMeshBuilder.Build(field, Vector3Int.zero, _vertices, _uvs, _indices);

            int sharedTopVertexCount = 0;
            for (int i = 0; i < _vertices.Count; i++)
            {
                Vector3 vertex = _vertices[i];
                if (!Mathf.Approximately(vertex.x, 1f) || !Mathf.Approximately(vertex.y, 1f))
                    continue;

                sharedTopVertexCount++;
                Assert.That(_uvs[i].x, Is.EqualTo(1f).Within(0.0001f),
                    "相邻 Quad 在公共边界必须使用同一个全局 Cell UV，而不能各自重置为 0/1。");
            }

            Assert.That(sharedTopVertexCount, Is.GreaterThanOrEqualTo(4));
        }

        [Test]
        public void SolidOrEmptyCellCreatesNoWaterFace()
        {
            FakeField field = CreateField(new Vector3Int(2, 1, 1), cellSize: 1f, chunkSize: 2);
            field.SetWater(1, 0, 0, byte.MaxValue);
            field.SetSolid(1, 0, 0, true);

            WaterChunkMeshBuilder.Build(field, Vector3Int.zero, _vertices, _uvs, _indices);

            Assert.That(_vertices, Is.Empty);
            Assert.That(_uvs, Is.Empty);
            Assert.That(_indices, Is.Empty);
        }

        [Test]
        public void HigherWaterCellCreatesOnlyHeightDifferenceSideAgainstLowerNeighbor()
        {
            FakeField field = CreateField(new Vector3Int(2, 1, 1), cellSize: 1f, chunkSize: 2);
            field.SetWater(0, 0, 0, byte.MaxValue);
            field.SetWater(1, 0, 0, amount: 128);

            WaterChunkMeshBuilder.Build(field, Vector3Int.zero, _vertices, _uvs, _indices);

            float lowerSurface = 128f / byte.MaxValue;
            int internalTriangleCount = 0;
            for (int triangle = 0; triangle < _indices.Count / 3; triangle++)
            {
                Vector3 a = _vertices[_indices[triangle * 3]];
                Vector3 b = _vertices[_indices[triangle * 3 + 1]];
                Vector3 c = _vertices[_indices[triangle * 3 + 2]];
                if (!Mathf.Approximately(a.x, 1f)
                    || !Mathf.Approximately(b.x, 1f)
                    || !Mathf.Approximately(c.x, 1f))
                {
                    continue;
                }

                internalTriangleCount++;
                Assert.That(Mathf.Min(a.y, Mathf.Min(b.y, c.y)),
                    Is.GreaterThanOrEqualTo(lowerSurface - 0.0001f),
                    "高水格只应补齐邻居水面以上的高度差，不能生成贯穿到底部的内部墙面。");
            }

            Assert.That(internalTriangleCount, Is.EqualTo(2));
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

        private static Vector3 TriangleNormal(
            IReadOnlyList<Vector3> vertices,
            IReadOnlyList<int> indices,
            int triangleIndex)
        {
            Vector3 a = vertices[indices[triangleIndex * 3]];
            Vector3 b = vertices[indices[triangleIndex * 3 + 1]];
            Vector3 c = vertices[indices[triangleIndex * 3 + 2]];
            return Vector3.Cross(b - a, c - a).normalized;
        }

        private static FakeField CreateField(Vector3Int dimensions, float cellSize, int chunkSize)
        {
            return new FakeField(dimensions, cellSize, chunkSize);
        }

        /// <summary>
        /// 测试替身只实现只读接口，不暴露 ElementGrid 的内部数组，确保 Builder 依赖正式架构边界。
        /// </summary>
        private sealed class FakeField : IElementFieldReadOnly
        {
            private readonly ElementCell[] _cells;
            private readonly bool[] _solid;

            public FakeField(Vector3Int dimensions, float cellSize, int chunkSize)
            {
                Dimensions = dimensions;
                CellSize = cellSize;
                ChunkSize = chunkSize;
                ChunkCounts = new Vector3Int(
                    Mathf.CeilToInt(dimensions.x / (float)chunkSize),
                    Mathf.CeilToInt(dimensions.y / (float)chunkSize),
                    Mathf.CeilToInt(dimensions.z / (float)chunkSize));
                int count = dimensions.x * dimensions.y * dimensions.z;
                _cells = new ElementCell[count];
                _solid = new bool[count];
            }

            public bool IsInitialized => true;
            public Vector3 Origin => Vector3.zero;
            public Vector3Int Dimensions { get; }
            public float CellSize { get; }
            public int ChunkSize { get; }
            public Vector3Int ChunkCounts { get; }

            public ElementCell GetCell(int x, int y, int z)
            {
                return _cells[ToIndex(x, y, z)];
            }

            public bool IsSolid(int x, int y, int z)
            {
                return _solid[ToIndex(x, y, z)];
            }

            public uint GetChunkVersion(int chunkX, int chunkY, int chunkZ)
            {
                return 1;
            }

            public void SetWater(int x, int y, int z, byte amount)
            {
                _cells[ToIndex(x, y, z)] = new ElementCell(MaterialId.Water, amount);
            }

            public void SetSolid(int x, int y, int z, bool solid)
            {
                _solid[ToIndex(x, y, z)] = solid;
            }

            private int ToIndex(int x, int y, int z)
            {
                return x + Dimensions.x * (y + Dimensions.y * z);
            }
        }
    }
}
