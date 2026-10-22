using Game.Materials;
using System.Collections.Generic;
using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 验证连续 Water Signed Distance 到 Chunk-local Surface Mesh 的纯转换。
    /// 测试不创建 Unity Mesh 或 Renderer，Position/Normal/UV/Index 全部写入可复用 List。
    /// </summary>
    public sealed class WorldWaterVolumeMeshBuilderTests
    {
        private readonly List<Vector3> _vertices = new List<Vector3>(256);
        private readonly List<Vector3> _normals = new List<Vector3>(256);
        private readonly List<Vector2> _uvs = new List<Vector2>(256);
        private readonly List<int> _indices = new List<int>(512);

        [Test]
        public void AirborneWaterProducesNonZeroBoundsOnAllThreeAxes()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 2);
            world.SetWater(Vector3Int.zero, amount: 64);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            Bounds bounds = CalculateBounds(_vertices);
            Assert.That(_indices, Is.Not.Empty);
            Assert.That(bounds.size.x, Is.GreaterThan(0.02f));
            Assert.That(bounds.size.y, Is.GreaterThan(0.02f));
            Assert.That(bounds.size.z, Is.GreaterThan(0.02f));
        }

        [Test]
        public void EmptyChunkProducesNoGeometry()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 2);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            Assert.That(_vertices, Is.Empty);
            Assert.That(_normals, Is.Empty);
            Assert.That(_uvs, Is.Empty);
            Assert.That(_indices, Is.Empty);
        }

        [Test]
        public void EmptyChunkStopsAfterOneCheapCellScan()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 2);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            int cellsInChunk = world.ChunkSize * world.ChunkSize * world.ChunkSize;
            Assert.That(
                world.TryGetCellCallCount,
                Is.LessThanOrEqualTo(cellsInChunk),
                "空 Chunk 只应做一次 O(ChunkCells) 水存在性扫描，不能进入高成本 Signed Distance 采样。");
        }

        [Test]
        public void WaterChunkSnapshotsSparseWorldBeforeDenseSampling()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            world.SetWater(new Vector3Int(4, 4, 4), amount: 96);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            int sparseWorldQueryCount =
                world.TryGetCellCallCount + world.IsSolidCallCount;
            Assert.That(_indices, Is.Not.Empty);
            Assert.That(
                sparseWorldQueryCount,
                Is.LessThanOrEqualTo(7000),
                "密集 SDF Sample 必须读取 Workspace Cell Cache，不能为每个 Sample 反复查询稀疏 World Dictionary。");
        }

        [Test]
        public void WaterChunkPrecomputesOnePrimitivePerVisualCell()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            world.SetWater(new Vector3Int(3, 3, 3), amount: 48);
            world.SetWater(new Vector3Int(4, 3, 3), amount: 96);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            Assert.That(
                workspace.CachedPrimitiveCount,
                Is.EqualTo(workspace.CachedVisualWaterCellCount),
                "每个 Visual Water Cell 每次 Rebuild 只能预计算一个 Primitive；Filter 生成的 Presentation Bridge 不要求与 Gameplay Water Cell 1:1。");
        }

        [Test]
        public void ClosedAirborneDropIsWatertight()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 4);
            world.SetWater(new Vector3Int(1, 1, 1), amount: 96);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            AssertWatertight(_indices);
        }

        [Test]
        public void BoundaryBlurDoesNotEnterGameplayEmptyOwnerAndMeshStaysWatertight()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 4);
            var boundarySeed = new Vector3Int(3, 1, 1);
            var emptyNeighborCell = new Vector3Int(4, 1, 1);
            world.SetWater(boundarySeed, amount: 96);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            Assert.That(_indices, Is.Not.Empty);
            AssertWatertight(_indices);
            Assert.That(
                workspace.TryGetCachedVisualWater(emptyNeighborCell, out _),
                Is.False,
                "Gameplay-empty 邻 Chunk 没有 View，Visual Commit 不能把水写进由它拥有的 Cell。");
        }

        [Test]
        public void NeighborOwnerBecomesVisualEligibleAfterGameplayWaterAppears()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 4);
            // Chunk -1 的最后一个 Cell 是 -1，而不是 0；此场景锁定负坐标必须使用 Floor Division。
            var boundarySeed = new Vector3Int(-1, 1, 1);
            var neighborCell = new Vector3Int(0, 1, 1);
            var currentChunk = new ElementChunkKey(-1, 0, 0);
            var neighborChunk = new ElementChunkKey(0, 0, 0);
            world.SetWater(boundarySeed, amount: 96);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);

            workspace.SnapshotWorldCells(world, currentChunk, in settings);
            workspace.BuildVisualWaterCache(in settings);
            Assert.That(
                workspace.TryGetCachedVisualWater(neighborCell, out _),
                Is.False,
                "邻 Chunk 尚无 Gameplay Water 时，View decision 与 Visual owner 都必须保持 false。");

            world.SetWater(neighborCell, amount: 96);
            Assert.That(
                WorldWaterChunkRelevance.ContainsWater(world, neighborChunk),
                Is.True,
                "加入 raw Gameplay Water 后，邻 Chunk 必须进入 Renderer relevance。");

            workspace.SnapshotWorldCells(world, currentChunk, in settings);
            workspace.BuildVisualWaterCache(in settings);
            Assert.That(
                workspace.TryGetCachedVisualWater(neighborCell, out byte visualAmount),
                Is.True,
                "新 Snapshot 的 owner mask 应允许 Visual Cache 进入已经拥有 View 的邻 Chunk。");
            Assert.That(visualAmount, Is.GreaterThan(0));
        }

        [Test]
        public void FarOwnerWaterDoesNotAuthorizeBoundaryBridgeButNearWaterDoes()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            var boundarySeed = new Vector3Int(7, 1, 1);
            var targetCell = new Vector3Int(8, 1, 1);
            var farOwnerWater = new Vector3Int(15, 1, 1);
            var nearOwnerWater = new Vector3Int(9, 1, 1);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var leftWorkspace = CreateWorkspace(world, in settings);
            var rightWorkspace = CreateWorkspace(world, in settings);
            world.SetWater(boundarySeed, amount: 96);
            world.SetWater(farOwnerWater, amount: 96);

            SnapshotAndBuildVisual(
                world,
                new ElementChunkKey(0, 0, 0),
                in settings,
                leftWorkspace);
            SnapshotAndBuildVisual(
                world,
                new ElementChunkKey(1, 0, 0),
                in settings,
                rightWorkspace);

            var sharedBoundarySample = new Vector3(8f, 1.5f, 1.5f);
            float leftFarScalar = WorldWaterImplicitField.SampleCached(
                leftWorkspace,
                sharedBoundarySample,
                in settings);
            float rightFarScalar = WorldWaterImplicitField.SampleCached(
                rightWorkspace,
                sharedBoundarySample,
                in settings);
            Assert.That(
                rightFarScalar,
                Is.EqualTo(leftFarScalar).Within(0.000001f),
                "x15 的远端 Water 不应让右 Workspace 单方面授权 x8，否则 shared-boundary Scalar 会分歧。");
            Assert.That(
                leftWorkspace.TryGetCachedVisualWater(targetCell, out _),
                Is.False);
            Assert.That(
                rightWorkspace.TryGetCachedVisualWater(targetCell, out _),
                Is.False,
                "目标 x8 只能读取 owner1 内 x8±1 的 raw Water，不能借用同 Chunk 远端 x15。");

            world.SetWater(nearOwnerWater, amount: 96);
            SnapshotAndBuildVisual(
                world,
                new ElementChunkKey(0, 0, 0),
                in settings,
                leftWorkspace);
            SnapshotAndBuildVisual(
                world,
                new ElementChunkKey(1, 0, 0),
                in settings,
                rightWorkspace);

            Assert.That(
                leftWorkspace.TryGetCachedVisualWater(targetCell, out byte leftAmount),
                Is.True);
            Assert.That(
                rightWorkspace.TryGetCachedVisualWater(targetCell, out byte rightAmount),
                Is.True);
            Assert.That(rightAmount, Is.EqualTo(leftAmount));
            float leftNearScalar = WorldWaterImplicitField.SampleCached(
                leftWorkspace,
                sharedBoundarySample,
                in settings);
            float rightNearScalar = WorldWaterImplicitField.SampleCached(
                rightWorkspace,
                sharedBoundarySample,
                in settings);
            Assert.That(
                rightNearScalar,
                Is.EqualTo(leftNearScalar).Within(0.000001f),
                "owner1 内 x9 是 target-local 近邻；两侧 Workspace 应共同放行 x8 并得到相同 Scalar。");
        }

        private static void AssertWatertight(IReadOnlyList<int> indices)
        {
            var edgeUseCounts = new Dictionary<UndirectedEdge, int>();
            for (int index = 0; index < indices.Count; index += 3)
            {
                CountEdge(edgeUseCounts, indices[index], indices[index + 1]);
                CountEdge(edgeUseCounts, indices[index + 1], indices[index + 2]);
                CountEdge(edgeUseCounts, indices[index + 2], indices[index]);
            }

            Assert.That(edgeUseCounts, Is.Not.Empty);
            foreach (KeyValuePair<UndirectedEdge, int> pair in edgeUseCounts)
            {
                Assert.That(
                    pair.Value,
                    Is.EqualTo(2),
                    $"闭合水滴的 Mesh Edge {pair.Key} 不是恰好被两个 Triangle 共用。");
            }
        }

        [Test]
        public void SupportedPuddleShoreIsNotLockedToElementCellGrid()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 4);
            var waterCell = new Vector3Int(1, 1, 1);
            world.SetSolid(waterCell + Vector3Int.down);
            world.SetWater(waterCell, amount: 80);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            // Surface Nets 的 Sample Lattice 本来就比 Element Cell 更密；真正需要打破的是
            // Gameplay Cell 的 25 cm 方块轮廓，而不是要求每个 Vertex 都脱离采样格点。
            float elementCellSpacing = world.CellSize;
            bool foundInterpolatedCoordinate = false;
            for (int i = 0; i < _vertices.Count; i++)
            {
                Vector3 vertex = _vertices[i];
                if (!IsGridAligned(vertex.x, elementCellSpacing)
                    || !IsGridAligned(vertex.z, elementCellSpacing))
                {
                    foundInterpolatedCoordinate = true;
                    break;
                }
            }

            Assert.That(
                foundInterpolatedCoordinate,
                Is.True,
                "岸线 Vertex 全落在 Element Cell 边界上，仍会保留原始方格轮廓。");
        }

        [Test]
        public void SupportedLowAmountWaterProducesSurfaceAboveSupportPlane()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 4);
            var waterCell = new Vector3Int(1, 1, 1);
            world.SetSolid(waterCell + Vector3Int.down);
            world.SetWater(waterCell, amount: 16);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            Bounds bounds = CalculateBounds(_vertices);
            float supportPlaneY = waterCell.y * world.CellSize;
            float visibleThickness = bounds.max.y - supportPlaneY;
            Assert.That(
                visibleThickness,
                Is.GreaterThan(world.CellSize * 0.04f),
                "Supported Water 顶面必须至少高出地面 0.04 Cell；仅约 0.01 Cell 的数学分离"
                + "在透明 Alpha、透视缩小与 Depth Test 共同作用下仍会近似不可见。");
            Assert.That(
                visibleThickness,
                Is.LessThan(world.CellSize * 0.20f),
                "低 Amount 水只能保持视觉浅水，不能再用 0.51 Cell 强制厚度掩盖 Sampling 问题。");
        }

        [Test]
        public void AdjacentAirborneCellsProduceOneConnectedSurface()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 4);
            world.SetWater(new Vector3Int(1, 1, 1), amount: 48);
            world.SetWater(new Vector3Int(2, 1, 1), amount: 48);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            Assert.That(CountTriangleComponents(_vertices.Count, _indices), Is.EqualTo(1));
        }

        [Test]
        public void DiagonalAirborneClusterProducesOneConnectedSurface()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            world.SetWater(new Vector3Int(3, 4, 3), amount: 96);
            world.SetWater(new Vector3Int(4, 4, 4), amount: 96);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            Assert.That(_indices, Is.Not.Empty);
            Assert.That(CountTriangleComponents(_vertices.Count, _indices), Is.EqualTo(1));
        }

        [Test]
        public void SupportedLowAmountClusterProducesOnePuddleSurface()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            SetFloor(world, y: 0, minimumX: 1, maximumX: 5, minimumZ: 1, maximumZ: 5);
            world.SetWater(new Vector3Int(2, 1, 2), amount: 16);
            world.SetWater(new Vector3Int(3, 1, 3), amount: 16);
            world.SetWater(new Vector3Int(4, 1, 2), amount: 16);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            Assert.That(_indices, Is.Not.Empty);
            Assert.That(CountTriangleComponents(_vertices.Count, _indices), Is.EqualTo(1));
        }

        [Test]
        public void SupportedPuddleTopTrianglesFaceUpwardForBackfaceCulling()
        {
            FakeWorld world = BuildSupportedPuddle();

            float supportPlaneY = world.CellSize;
            Bounds bounds = CalculateBounds(_vertices);
            float topBandMinimumY = Mathf.Lerp(supportPlaneY, bounds.max.y, 0.5f);
            int upwardTriangleCount = 0;
            int downwardTopTriangleCount = 0;
            for (int index = 0; index < _indices.Count; index += 3)
            {
                Vector3 a = _vertices[_indices[index]];
                Vector3 b = _vertices[_indices[index + 1]];
                Vector3 c = _vertices[_indices[index + 2]];
                Vector3 faceNormal = Vector3.Cross(b - a, c - a);
                if (faceNormal.sqrMagnitude <= 0.0000000001f)
                    continue;

                float centroidY = (a.y + b.y + c.y) / 3f;
                float normalizedY = faceNormal.normalized.y;
                if (centroidY <= topBandMinimumY || Mathf.Abs(normalizedY) < 0.5f)
                    continue;

                if (normalizedY > 0f)
                    upwardTriangleCount++;
                else
                    downwardTopTriangleCount++;
            }

            Assert.That(
                upwardTriangleCount,
                Is.GreaterThan(0),
                "Supported 水滩必须至少输出一个朝上的顶面 Triangle，"
                + "否则 Shader 的 Cull Back 会让玩家从上方完全看不到水。"
                + $" 当前 MeshY=[{bounds.min.y},{bounds.max.y}]、"
                + $"TopBandMinY={topBandMinimumY}、朝下顶面 Triangle={downwardTopTriangleCount}。");
            Assert.That(
                downwardTopTriangleCount,
                Is.EqualTo(0),
                "支撑面上方发现朝下的近水平 Triangle；这会造成 Cull Back 下的周期性方格空洞。");
        }

        [Test]
        public void SupportedPuddleTopVertexNormalsFaceUpwardForLighting()
        {
            FakeWorld world = BuildSupportedPuddle();

            float supportPlaneY = world.CellSize;
            Bounds bounds = CalculateBounds(_vertices);
            float topBandMinimumY = Mathf.Lerp(supportPlaneY, bounds.max.y, 0.5f);
            int upwardNormalCount = 0;
            int downwardNormalCount = 0;
            for (int index = 0; index < _vertices.Count; index++)
            {
                if (_vertices[index].y <= topBandMinimumY
                    || Mathf.Abs(_normals[index].y) < 0.5f)
                {
                    continue;
                }

                if (_normals[index].y > 0f)
                    upwardNormalCount++;
                else
                    downwardNormalCount++;
            }

            Assert.That(
                upwardNormalCount,
                Is.GreaterThan(0),
                "Supported 顶面必须存在朝上的 Vertex Normal，供 PBR Lighting、Fresnel "
                + $"和程序化波纹使用；当前朝下 Normal={downwardNormalCount}。");
            Assert.That(
                downwardNormalCount,
                Is.EqualTo(0),
                "顶面 Vertex Normal 不能朝下，否则即使关闭 Cull，也会得到错误的水面光照。");
        }

        [Test]
        public void SupportedPuddleHasNoOpenSquareHoles()
        {
            BuildSupportedPuddle();

            // 若顶面漏掉一个 Quad，它周围的四条 Mesh Edge 就只会被一个 Triangle 使用；
            // 完整闭合曲面中，每条无向 Edge 必须恰好被两个 Triangle 共享。
            // 这比仅检查“已有 Triangle 是否朝上”更严格：后者无法发现根本没生成的面。
            AssertWatertight(_indices);
        }

        [Test]
        public void SupportedPuddleTopProjectionCoversItsInteriorWithoutGridHoles()
        {
            BuildSupportedPuddle();

            int uncoveredSampleCount = CountUncoveredPuddleSamples();

            Assert.That(
                uncoveredSampleCount,
                Is.EqualTo(0),
                "Supported 水滩内部的垂直投影存在没有顶面 Triangle 覆盖的采样点；"
                + "这代表几何中存在闭合方孔，而不是单纯的透明度或 Foam 视觉图案。");
        }

        [Test]
        public void SupportedTraceClustersBelowThresholdRemainVisibleWhileIsolatedTraceIsHidden()
        {
            for (byte traceAmount = 1; traceAmount <= 2; traceAmount++)
            {
                var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
                SetFloor(world, y: 0, minimumX: 1, maximumX: 5, minimumZ: 1, maximumZ: 5);
                for (int z = 2; z <= 4; z++)
                for (int x = 2; x <= 4; x++)
                    world.SetWater(new Vector3Int(x, 1, z), traceAmount);

                WaterVolumeMeshingSettings settings = CreateDensitySettings();
                var workspace = CreateWorkspace(world, in settings);

                Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

                Assert.That(
                    _indices,
                    Is.Not.Empty,
                    $"Threshold 可以隐藏孤立碎点，但不能把连续 3×3 Amount={traceAmount} "
                    + "痕量水滩整体删除。");
                Assert.That(
                    CountTriangleComponents(_vertices.Count, _indices),
                    Is.EqualTo(1),
                    "连续痕量水应合并成一个 Presentation 水滩，而不是重新退化为离散点。");
            }
        }

        [Test]
        public void WaterStackAboveSolidProducesOneConnectedSurface()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 4);
            var bottomWater = new Vector3Int(1, 1, 1);
            world.SetSolid(bottomWater + Vector3Int.down);
            world.SetWater(bottomWater, amount: 255);
            world.SetWater(bottomWater + Vector3Int.up, amount: 96);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            Assert.That(CountTriangleComponents(_vertices.Count, _indices), Is.EqualTo(1));
        }

        [Test]
        public void RepeatedBuildWithSameInputIsDeterministic()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 4);
            world.SetWater(new Vector3Int(1, 1, 1), amount: 73);
            world.SetWater(new Vector3Int(2, 1, 1), amount: 121);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);
            Vector3[] firstVertices = _vertices.ToArray();
            Vector3[] firstNormals = _normals.ToArray();
            Vector2[] firstUvs = _uvs.ToArray();
            int[] firstIndices = _indices.ToArray();

            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);

            CollectionAssert.AreEqual(firstVertices, _vertices);
            CollectionAssert.AreEqual(firstNormals, _normals);
            CollectionAssert.AreEqual(firstUvs, _uvs);
            CollectionAssert.AreEqual(firstIndices, _indices);
        }

        [Test]
        public void UvUsesGlobalMetersInsteadOfRestartingPerChunk()
        {
            var world = new FakeWorld(
                cellSize: 0.25f,
                chunkSize: 2,
                origin: new Vector3(10f, 3f, -7f));
            world.SetWater(new Vector3Int(2, 0, 0), amount: 96);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);
            var chunkKey = new ElementChunkKey(1, 0, 0);

            Build(world, chunkKey, in settings, workspace);

            float chunkOriginX =
                world.Origin.x + chunkKey.X * world.ChunkSize * world.CellSize;
            Assert.That(_vertices, Is.Not.Empty);
            for (int i = 0; i < _vertices.Count; i++)
            {
                Assert.That(
                    _uvs[i].x,
                    Is.EqualTo(chunkOriginX + _vertices[i].x).Within(0.000001f));
                Assert.That(
                    _uvs[i].y,
                    Is.EqualTo(world.Origin.z + _vertices[i].z).Within(0.000001f));
            }
        }

        [Test]
        public void AdjacentChunksRepeatBoundaryVerticesAndNormalsConsistently()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 2);
            world.SetWater(new Vector3Int(1, 1, 0), amount: 96);
            world.SetWater(new Vector3Int(2, 1, 0), amount: 96);
            WaterVolumeMeshingSettings settings = CreateDefaultSettings();
            var workspace = CreateWorkspace(world, in settings);

            Build(
                world,
                new ElementChunkKey(0, 0, 0),
                in settings,
                workspace);
            Vector3[] leftVertices = _vertices.ToArray();
            Vector3[] leftNormals = _normals.ToArray();

            var rightChunk = new ElementChunkKey(1, 0, 0);
            Build(world, rightChunk, in settings, workspace);
            Vector3 rightOrigin = new Vector3(
                rightChunk.X * world.ChunkSize * world.CellSize,
                0f,
                0f);

            int matchingBoundaryVertexCount = 0;
            for (int left = 0; left < leftVertices.Length; left++)
            for (int right = 0; right < _vertices.Count; right++)
            {
                Vector3 rightGlobalPosition = rightOrigin + _vertices[right];
                if ((leftVertices[left] - rightGlobalPosition).sqrMagnitude > 0.0000000001f)
                    continue;

                matchingBoundaryVertexCount++;
                Assert.That(
                    Vector3.Dot(leftNormals[left], _normals[right]),
                    Is.GreaterThan(0.9999f),
                    "相邻 Chunk 对同一 Boundary Vertex 算出了不同的 Gradient Normal。");
            }

            Assert.That(
                matchingBoundaryVertexCount,
                Is.GreaterThan(0),
                "两个 Chunk 没有复算出任何相同的边界 Vertex，运行时会出现可见 Seam。");
        }

        [Test]
        public void SupportedDiagonalSeedsFillAnOpenPresentationBridge()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            SetFloor(world, y: 0, minimumX: 0, maximumX: 4, minimumZ: 0, maximumZ: 4);
            world.SetWater(new Vector3Int(1, 1, 1), amount: 64);
            world.SetWater(new Vector3Int(2, 1, 2), amount: 64);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);

            workspace.SnapshotWorldCells(world, new ElementChunkKey(0, 0, 0), in settings);
            WorldWaterVisualDensityFilter.Build(workspace, in settings);

            Assert.That(
                workspace.TryGetCachedVisualWater(new Vector3Int(2, 1, 1), out byte bridgeAmount),
                Is.True);
            Assert.That(bridgeAmount, Is.GreaterThan(0));
            Assert.That(workspace.IsCachedVisualSupported(new Vector3Int(2, 1, 1)), Is.True);
        }

        [Test]
        public void SupportedFilterPreservesSeedPeakWhileCreatingBridge()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            SetFloor(world, y: 0, minimumX: 0, maximumX: 4, minimumZ: 0, maximumZ: 4);
            var firstSeed = new Vector3Int(1, 1, 1);
            world.SetWater(firstSeed, amount: 64);
            world.SetWater(new Vector3Int(2, 1, 2), amount: 64);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);

            workspace.SnapshotWorldCells(world, new ElementChunkKey(0, 0, 0), in settings);
            workspace.BuildVisualWaterCache(in settings);

            Assert.That(
                workspace.TryGetCachedVisualWater(new Vector3Int(2, 1, 1), out byte bridgeAmount),
                Is.True,
                "Peak-preserving Blur 仍必须在两个对角 Seed 之间创建 Visual Bridge。");
            Assert.That(bridgeAmount, Is.GreaterThan(0));
            Assert.That(
                workspace.TryGetCachedVisualWater(firstSeed, out byte preservedSeedAmount),
                Is.True);
            Assert.That(
                preservedSeedAmount,
                Is.EqualTo(64),
                "属于 Supported pass 的原始 Seed 不能被 Blur 降峰；保峰只作用于 Presentation Cache。");
        }

        [Test]
        public void CachedSampleRequiresCompletedVisualWaterCache()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            var waterCell = new Vector3Int(3, 4, 3);
            world.SetWater(waterCell, amount: 96);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);
            workspace.SnapshotWorldCells(
                world,
                new ElementChunkKey(0, 0, 0),
                in settings);

            System.InvalidOperationException exception =
                Assert.Throws<System.InvalidOperationException>(
                    () => WorldWaterImplicitField.SampleCached(
                        workspace,
                        (Vector3)waterCell + Vector3.one * 0.5f,
                        in settings));
            Assert.That(
                exception.Message,
                Does.Contain("visual water cache").IgnoreCase);

            workspace.BuildVisualWaterCache(in settings);
            Assert.DoesNotThrow(
                () => WorldWaterImplicitField.SampleCached(
                    workspace,
                    (Vector3)waterCell + Vector3.one * 0.5f,
                    in settings));
        }

        [Test]
        public void FailedSnapshotInvalidatesPreviousWorldAndVisualSnapshots()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            world.SetWater(new Vector3Int(3, 4, 3), amount: 96);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);
            var chunkKey = new ElementChunkKey(0, 0, 0);
            workspace.SnapshotWorldCells(world, chunkKey, in settings);
            workspace.BuildVisualWaterCache(in settings);
            Assert.That(workspace.HasWorldCellSnapshot, Is.True);
            Assert.That(
                workspace.HasVisualWaterCache,
                Is.True,
                "前置条件必须证明上一轮 Visual Cache 确实已经有效，否则无法验证失败 Snapshot 会撤销它。");

            world.ThrowOnTryGetCellCall =
                world.TryGetCellCallCount + 2;
            Assert.Throws<System.InvalidOperationException>(
                () => workspace.SnapshotWorldCells(
                    world,
                    chunkKey,
                    in settings));

            Assert.That(
                workspace.HasWorldCellSnapshot,
                Is.False,
                "新 Snapshot 中途失败后，不能继续把上一轮 World Snapshot 标记为有效。");
            Assert.That(
                workspace.HasVisualWaterCache,
                Is.False,
                "新 Snapshot 中途失败后，不能继续把上一轮 Visual Cache 标记为有效。");
        }

        [Test]
        public void IsolatedTraceAmountFallsBelowVisualThreshold()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            world.SetSolid(new Vector3Int(3, 2, 3));
            world.SetWater(new Vector3Int(3, 3, 3), amount: 1);
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);

            workspace.SnapshotWorldCells(world, new ElementChunkKey(0, 0, 0), in settings);
            WorldWaterVisualDensityFilter.Build(workspace, in settings);

            Assert.That(
                workspace.TryGetCachedVisualWater(new Vector3Int(3, 3, 3), out _),
                Is.False);
        }

        [Test]
        public void SolidCellBlocksVisualDensity()
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            world.SetWater(new Vector3Int(2, 3, 3), amount: 255);
            world.SetSolid(new Vector3Int(3, 3, 3));
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            var workspace = CreateWorkspace(world, in settings);

            workspace.SnapshotWorldCells(world, new ElementChunkKey(0, 0, 0), in settings);
            WorldWaterVisualDensityFilter.Build(workspace, in settings);

            Assert.That(
                workspace.TryGetCachedVisualWater(new Vector3Int(3, 3, 3), out _),
                Is.False);
        }

        private void Build(
            FakeWorld world,
            ElementChunkKey chunkKey,
            in WaterVolumeMeshingSettings settings,
            WaterVolumeMeshingWorkspace workspace)
        {
            WorldWaterVolumeMeshBuilder.BuildWorldChunk(
                world,
                chunkKey,
                in settings,
                workspace,
                _vertices,
                _normals,
                _uvs,
                _indices);
        }

        private FakeWorld BuildSupportedPuddle()
        {
            WaterVolumeMeshingSettings settings = CreateDensitySettings();
            return BuildSupportedPuddle(in settings);
        }

        private FakeWorld BuildSupportedPuddle(
            in WaterVolumeMeshingSettings settings)
        {
            var world = new FakeWorld(cellSize: 0.25f, chunkSize: 8);
            SetFloor(world, y: 0, minimumX: 1, maximumX: 6, minimumZ: 1, maximumZ: 6);
            for (int z = 2; z <= 5; z++)
            for (int x = 2; x <= 5; x++)
                world.SetWater(new Vector3Int(x, 1, z), amount: 16);

            var workspace = CreateWorkspace(world, in settings);
            Build(world, new ElementChunkKey(0, 0, 0), in settings, workspace);
            return world;
        }

        private static WaterVolumeMeshingWorkspace CreateWorkspace(
            FakeWorld world,
            in WaterVolumeMeshingSettings settings)
        {
            return new WaterVolumeMeshingWorkspace(
                world.ChunkSize,
                settings.SamplesPerCell);
        }

        private static void SnapshotAndBuildVisual(
            FakeWorld world,
            ElementChunkKey chunkKey,
            in WaterVolumeMeshingSettings settings,
            WaterVolumeMeshingWorkspace workspace)
        {
            workspace.SnapshotWorldCells(world, chunkKey, in settings);
            workspace.BuildVisualWaterCache(in settings);
        }

        private static bool IsGridAligned(float value, float spacing)
        {
            float nearestGridLine = Mathf.Round(value / spacing) * spacing;
            return Mathf.Abs(value - nearestGridLine) <= 0.00001f;
        }

        private bool HasUpwardTriangleAbove(Vector2 sample)
        {
            for (int index = 0; index < _indices.Count; index += 3)
            {
                Vector3 a = _vertices[_indices[index]];
                Vector3 b = _vertices[_indices[index + 1]];
                Vector3 c = _vertices[_indices[index + 2]];
                if (Vector3.Cross(b - a, c - a).y <= 0f)
                    continue;

                if (ContainsPointInProjectedTriangle(
                        sample,
                        new Vector2(a.x, a.z),
                        new Vector2(b.x, b.z),
                        new Vector2(c.x, c.z)))
                {
                    return true;
                }
            }

            return false;
        }

        private int CountUncoveredPuddleSamples()
        {
            const float minimum = 0.625f;
            const float maximum = 1.375f;
            const int intervals = 24;
            int uncoveredSampleCount = 0;
            for (int z = 0; z <= intervals; z++)
            for (int x = 0; x <= intervals; x++)
            {
                var sample = new Vector2(
                    Mathf.Lerp(minimum, maximum, x / (float)intervals),
                    Mathf.Lerp(minimum, maximum, z / (float)intervals));
                if (!HasUpwardTriangleAbove(sample))
                    uncoveredSampleCount++;
            }

            return uncoveredSampleCount;
        }

        private static bool ContainsPointInProjectedTriangle(
            Vector2 point,
            Vector2 a,
            Vector2 b,
            Vector2 c)
        {
            float sideAB = Cross2D(b - a, point - a);
            float sideBC = Cross2D(c - b, point - b);
            float sideCA = Cross2D(a - c, point - c);
            const float epsilon = 0.000001f;
            bool hasNegative = sideAB < -epsilon
                || sideBC < -epsilon
                || sideCA < -epsilon;
            bool hasPositive = sideAB > epsilon
                || sideBC > epsilon
                || sideCA > epsilon;
            return !(hasNegative && hasPositive);
        }

        private static float Cross2D(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static void CountEdge(
            Dictionary<UndirectedEdge, int> counts,
            int first,
            int second)
        {
            var edge = new UndirectedEdge(first, second);
            counts.TryGetValue(edge, out int count);
            counts[edge] = count + 1;
        }

        private static int CountTriangleComponents(
            int vertexCount,
            IReadOnlyList<int> indices)
        {
            var adjacency = new List<int>[vertexCount];
            var used = new bool[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                adjacency[i] = new List<int>();

            for (int i = 0; i < indices.Count; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];
                Connect(adjacency, a, b);
                Connect(adjacency, b, c);
                Connect(adjacency, c, a);
                used[a] = true;
                used[b] = true;
                used[c] = true;
            }

            int componentCount = 0;
            var stack = new Stack<int>();
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                if (!used[vertex])
                    continue;

                componentCount++;
                stack.Push(vertex);
                used[vertex] = false;
                while (stack.Count > 0)
                {
                    int current = stack.Pop();
                    List<int> neighbors = adjacency[current];
                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        int neighbor = neighbors[i];
                        if (!used[neighbor])
                            continue;
                        used[neighbor] = false;
                        stack.Push(neighbor);
                    }
                }
            }

            return componentCount;
        }

        private static void Connect(List<int>[] adjacency, int first, int second)
        {
            adjacency[first].Add(second);
            adjacency[second].Add(first);
        }

        private static Bounds CalculateBounds(IReadOnlyList<Vector3> vertices)
        {
            Assert.That(vertices, Is.Not.Empty);
            var bounds = new Bounds(vertices[0], Vector3.zero);
            for (int i = 1; i < vertices.Count; i++)
                bounds.Encapsulate(vertices[i]);
            return bounds;
        }

        private static WaterVolumeMeshingSettings CreateDefaultSettings()
        {
            return new WaterVolumeMeshingSettings(
                samplesPerCell: 2,
                supportedCornerRadius: 0.12f,
                minimumSupportedHeight: 0.30f,
                minimumAirborneRadius: 0.30f,
                maximumAirborneRadius: 0.48f,
                smoothUnionRadius: 0.16f);
        }

        private static WaterVolumeMeshingSettings CreateDensitySettings()
        {
            return new WaterVolumeMeshingSettings(
                samplesPerCell: 2,
                supportedCornerRadius: 0.12f,
                minimumSupportedHeight: 0.30f,
                minimumAirborneRadius: 0.30f,
                maximumAirborneRadius: 0.48f,
                smoothUnionRadius: 0.16f,
                visualDensityThreshold: 3f / 255f,
                supportedSmoothingStrength: 0.90f,
                airborneSmoothingStrength: 0.90f,
                supportedFloorCaptureDepth: 0.08f);
        }

        private static void SetFloor(
            FakeWorld world,
            int y,
            int minimumX,
            int maximumX,
            int minimumZ,
            int maximumZ)
        {
            for (int z = minimumZ; z <= maximumZ; z++)
            for (int x = minimumX; x <= maximumX; x++)
                world.SetSolid(new Vector3Int(x, y, z));
        }

        private readonly struct UndirectedEdge
        {
            public UndirectedEdge(int first, int second)
            {
                Minimum = Mathf.Min(first, second);
                Maximum = Mathf.Max(first, second);
            }

            private int Minimum { get; }
            private int Maximum { get; }

            public override bool Equals(object value)
            {
                return value is UndirectedEdge other
                    && Minimum == other.Minimum
                    && Maximum == other.Maximum;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Minimum * 397) ^ Maximum;
                }
            }

            public override string ToString()
            {
                return $"({Minimum}, {Maximum})";
            }
        }

        private sealed class FakeWorld : IElementWorldReadOnly
        {
            private readonly Dictionary<Vector3Int, ElementCell> _cells =
                new Dictionary<Vector3Int, ElementCell>();
            private readonly HashSet<Vector3Int> _solidCells =
                new HashSet<Vector3Int>();

            public FakeWorld(
                float cellSize,
                int chunkSize,
                Vector3 origin = default)
            {
                CellSize = cellSize;
                ChunkSize = chunkSize;
                Origin = origin;
            }

            public bool IsInitialized => true;
            public Vector3 Origin { get; }
            public float CellSize { get; }
            public int ChunkSize { get; }
            public int MaximumResidentChunkCount => 16;
            public int TryGetCellCallCount { get; private set; }
            public int IsSolidCallCount { get; private set; }
            public int ThrowOnTryGetCellCall { get; set; }

            public int CopyVisibleChunkKeys(ElementChunkKey[] destination)
            {
                return 0;
            }

            public bool TryGetCell(Vector3Int globalCell, out ElementCell cell)
            {
                TryGetCellCallCount++;
                if (ThrowOnTryGetCellCall > 0
                    && TryGetCellCallCount == ThrowOnTryGetCellCall)
                {
                    throw new System.InvalidOperationException(
                        "Intentional snapshot failure.");
                }

                return _cells.TryGetValue(globalCell, out cell);
            }

            public bool IsSolid(Vector3Int globalCell)
            {
                IsSolidCallCount++;
                return _solidCells.Contains(globalCell);
            }

            public uint GetChunkVersion(ElementChunkKey key)
            {
                return 1u;
            }

            public void SetWater(Vector3Int globalCell, byte amount)
            {
                _cells[globalCell] = new ElementCell(MaterialId.Water, amount);
            }

            public void SetSolid(Vector3Int globalCell)
            {
                _solidCells.Add(globalCell);
            }
        }
    }
}
