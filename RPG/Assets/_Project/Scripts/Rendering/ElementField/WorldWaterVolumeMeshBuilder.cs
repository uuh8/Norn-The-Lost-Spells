using Game.Materials;
using System;
using System.Collections.Generic;
using Game.ElementField;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 以 O(Chunk Cell Count) 的固定上限判断一个 Chunk 是否可能拥有自己负责输出的水面。
    /// 这是高成本 Signed Distance / Surface Nets 之前的 Broad Phase：
    /// 空 Chunk 不应为了证明“没有水”而采样更密的 Scalar Field。
    /// </summary>
    public static class WorldWaterChunkRelevance
    {
        public static bool ContainsWater(
            IElementWorldReadOnly world,
            ElementChunkKey chunkKey)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (!world.IsInitialized || world.ChunkSize <= 0)
                return false;

            int chunkSize = world.ChunkSize;
            int baseX = checked(chunkKey.X * chunkSize);
            int baseY = checked(chunkKey.Y * chunkSize);
            int baseZ = checked(chunkKey.Z * chunkSize);

            // Surface Nets 的 Owned Edge 规则规定：Chunk 只输出起点属于自己的 Edge，
            // 因而一个完全无水的 Chunk 不需要替相邻含水 Chunk 创建 Mesh。
            // 这里只扫描本 Chunk，避免把一份水扩散成 27 个高成本重建任务。
            for (int z = 0; z < chunkSize; z++)
            for (int y = 0; y < chunkSize; y++)
            for (int x = 0; x < chunkSize; x++)
            {
                var globalCell = new Vector3Int(baseX + x, baseY + y, baseZ + z);
                if (world.TryGetCell(globalCell, out ElementCell cell)
                    && !cell.IsEmpty
                    && cell.MaterialKind == MaterialId.Water)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 使用 Surface Nets 把连续 Water Signed Distance 提取为 Chunk-local Mesh 数据。
    ///
    /// Builder 不创建 Unity Mesh/GameObject，也不修改 ElementWorld。一个符号混合的 Dual Cell
    /// 最多生成一个共享 Vertex；发生正负变化的 Sample Edge 再连接周围四个 Dual Vertex。
    /// 相比逐 Cube 输出多组三角形，这能显著降低动态 Mesh 的 Vertex 数量。
    /// </summary>
    public static class WorldWaterVolumeMeshBuilder
    {
        private static readonly Vector3Int[] CornerOffsets =
        {
            new Vector3Int(0, 0, 0),
            new Vector3Int(1, 0, 0),
            new Vector3Int(1, 1, 0),
            new Vector3Int(0, 1, 0),
            new Vector3Int(0, 0, 1),
            new Vector3Int(1, 0, 1),
            new Vector3Int(1, 1, 1),
            new Vector3Int(0, 1, 1),
        };

        private static readonly int[] EdgeCornersA =
        {
            0, 1, 2, 3,
            4, 5, 6, 7,
            0, 1, 2, 3,
        };

        private static readonly int[] EdgeCornersB =
        {
            1, 2, 3, 0,
            5, 6, 7, 4,
            4, 5, 6, 7,
        };

        public static void BuildWorldChunk(
            IElementWorldReadOnly world,
            ElementChunkKey chunkKey,
            in WaterVolumeMeshingSettings settings,
            WaterVolumeMeshingWorkspace workspace,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> indices)
        {
            ValidateArguments(
                world,
                in settings,
                workspace,
                vertices,
                normals,
                uvs,
                indices);

            vertices.Clear();
            normals.Clear();
            uvs.Clear();
            indices.Clear();

            if (!world.IsInitialized || world.CellSize <= 0f)
                return;
            // 空 Halo 页也拥有一部分跨页表面；不能在读取邻页样本前丢弃。
            if (!workspace.AllowCrossChunkVisualSpread
                && !WorldWaterChunkRelevance.ContainsWater(world, chunkKey))
                return;

            workspace.SnapshotWorldCells(world, chunkKey, in settings);
            workspace.BuildVisualWaterCache(in settings);
            workspace.ClearDualVertexIndices();
            SampleScalarField(chunkKey, in settings, workspace);
            CreateDualVertices(
                world,
                chunkKey,
                workspace,
                vertices,
                normals,
                uvs);
            ConnectOwnedEdges(workspace, vertices, normals, indices);
        }

        private static void SampleScalarField(
            ElementChunkKey chunkKey,
            in WaterVolumeMeshingSettings settings,
            WaterVolumeMeshingWorkspace workspace)
        {
            int resolution = workspace.SampleResolution;
            int samplesPerCell = workspace.SamplesPerCell;
            float inverseSamplesPerCell = 1f / samplesPerCell;
            int baseX = checked(chunkKey.X * workspace.ChunkSize);
            int baseY = checked(chunkKey.Y * workspace.ChunkSize);
            int baseZ = checked(chunkKey.Z * workspace.ChunkSize);

            // 先用 +Infinity 表示“还没有 Water Primitive 贡献”。不能直接用 EmptyDistance，
            // 因为第一个 Primitive 的真实距离可能大于 2，仍必须保留给后续 Smooth Union。
            Array.Fill(workspace.Scalars, float.PositiveInfinity);

            // 从 Sample-centric 改为 Cell-centric：一个 Primitive 只影响 containingCell
            // 位于自己 ±1 邻域的 Sample。SamplesPerCell=2 时每轴最多 6 点，即 6³，
            // 避免 20³ 个 Sample 无条件各扫描 3³ Cell。
            int cacheDimension = workspace.CellCacheDimension;
            int activeMinimumX = resolution + 2;
            int activeMinimumY = resolution + 2;
            int activeMinimumZ = resolution + 2;
            int activeMaximumX = -3;
            int activeMaximumY = -3;
            int activeMaximumZ = -3;
            for (int cacheZ = 0; cacheZ < cacheDimension; cacheZ++)
            for (int cacheY = 0; cacheY < cacheDimension; cacheY++)
            for (int cacheX = 0; cacheX < cacheDimension; cacheX++)
            {
                int cacheIndex = workspace.CellCacheIndex(cacheX, cacheY, cacheZ);
                if (workspace.CachedVisualWaterAmounts[cacheIndex] == 0)
                    continue;

                WorldWaterPrimitive primitive =
                    workspace.CachedWaterPrimitives[cacheIndex];
                var globalCell = new Vector3Int(
                    workspace.CellCacheGlobalOrigin.x + cacheX,
                    workspace.CellCacheGlobalOrigin.y + cacheY,
                    workspace.CellCacheGlobalOrigin.z + cacheZ);
                int relativeX = globalCell.x - baseX;
                int relativeY = globalCell.y - baseY;
                int relativeZ = globalCell.z - baseZ;
                int minimumX = Mathf.Max(-2, (relativeX - 1) * samplesPerCell);
                int minimumY = Mathf.Max(-2, (relativeY - 1) * samplesPerCell);
                int minimumZ = Mathf.Max(-2, (relativeZ - 1) * samplesPerCell);
                int maximumX = Mathf.Min(
                    resolution + 1,
                    (relativeX + 2) * samplesPerCell - 1);
                int maximumY = Mathf.Min(
                    resolution + 1,
                    (relativeY + 2) * samplesPerCell - 1);
                int maximumZ = Mathf.Min(
                    resolution + 1,
                    (relativeZ + 2) * samplesPerCell - 1);
                activeMinimumX = Mathf.Min(activeMinimumX, minimumX);
                activeMinimumY = Mathf.Min(activeMinimumY, minimumY);
                activeMinimumZ = Mathf.Min(activeMinimumZ, minimumZ);
                activeMaximumX = Mathf.Max(activeMaximumX, maximumX);
                activeMaximumY = Mathf.Max(activeMaximumY, maximumY);
                activeMaximumZ = Mathf.Max(activeMaximumZ, maximumZ);

                for (int z = minimumZ; z <= maximumZ; z++)
                for (int y = minimumY; y <= maximumY; y++)
                for (int x = minimumX; x <= maximumX; x++)
                {
                    int scalarIndex = workspace.ScalarIndex(x, y, z);
                    if (workspace.CachedScalarSolidMask[scalarIndex])
                        continue;

                    var globalCellSpacePosition = new Vector3(
                        baseX + x * inverseSamplesPerCell,
                        baseY + y * inverseSamplesPerCell,
                        baseZ + z * inverseSamplesPerCell);
                    float distance = WorldWaterImplicitField.SamplePrimitive(
                        globalCellSpacePosition,
                        in primitive);
                    float current = workspace.Scalars[scalarIndex];
                    workspace.Scalars[scalarIndex] =
                        float.IsPositiveInfinity(current)
                            ? distance
                            : WorldWaterImplicitField.CombineDistances(
                                current,
                                distance,
                                settings.SmoothUnionRadius);
                }
            }

            if (activeMaximumX >= activeMinimumX)
            {
                workspace.SetActiveSampleBounds(
                    activeMinimumX,
                    activeMinimumY,
                    activeMinimumZ,
                    activeMaximumX,
                    activeMaximumY,
                    activeMaximumZ);
            }
            else
            {
                workspace.ClearActiveSampleBounds();
            }

            // 没有 Primitive 贡献的点恢复为与 Point Sampler 相同的有限正距离。
            for (int index = 0; index < workspace.Scalars.Length; index++)
            {
                if (float.IsPositiveInfinity(workspace.Scalars[index]))
                {
                    workspace.Scalars[index] =
                        WorldWaterImplicitField.EmptyDistance;
                }
            }
        }

        private static void CreateDualVertices(
            IElementWorldReadOnly world,
            ElementChunkKey chunkKey,
            WaterVolumeMeshingWorkspace workspace,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs)
        {
            if (!workspace.HasActiveSampleBounds)
                return;

            int resolution = workspace.SampleResolution;
            float inverseSamplesPerCell = 1f / workspace.SamplesPerCell;
            int baseX = checked(chunkKey.X * workspace.ChunkSize);
            int baseZ = checked(chunkKey.Z * workspace.ChunkSize);
            int minimumX = Mathf.Max(-1, workspace.ActiveSampleMinimumX - 1);
            int minimumY = Mathf.Max(-1, workspace.ActiveSampleMinimumY - 1);
            int minimumZ = Mathf.Max(-1, workspace.ActiveSampleMinimumZ - 1);
            int maximumX = Mathf.Min(resolution - 1, workspace.ActiveSampleMaximumX);
            int maximumY = Mathf.Min(resolution - 1, workspace.ActiveSampleMaximumY);
            int maximumZ = Mathf.Min(resolution - 1, workspace.ActiveSampleMaximumZ);

            // Dual logical range [-1, N-1] 包含本 Chunk Owned Edge 周围需要的负向 Halo。
            for (int z = minimumZ; z <= maximumZ; z++)
            for (int y = minimumY; y <= maximumY; y++)
            for (int x = minimumX; x <= maximumX; x++)
            {
                int crossingCount = 0;
                Vector3 positionSum = Vector3.zero;
                Vector3 gradientSum = Vector3.zero;

                for (int edge = 0; edge < EdgeCornersA.Length; edge++)
                {
                    Vector3Int cornerA = CornerOffsets[EdgeCornersA[edge]];
                    Vector3Int cornerB = CornerOffsets[EdgeCornersB[edge]];
                    int ax = x + cornerA.x;
                    int ay = y + cornerA.y;
                    int az = z + cornerA.z;
                    int bx = x + cornerB.x;
                    int by = y + cornerB.y;
                    int bz = z + cornerB.z;
                    float scalarA = Scalar(workspace, ax, ay, az);
                    float scalarB = Scalar(workspace, bx, by, bz);
                    if (IsInside(scalarA) == IsInside(scalarB))
                        continue;

                    float denominator = scalarA - scalarB;
                    float t = Mathf.Abs(denominator) > 0.000001f
                        ? Mathf.Clamp01(scalarA / denominator)
                        : 0.5f;
                    var sampleA = new Vector3(ax, ay, az);
                    var sampleB = new Vector3(bx, by, bz);
                    positionSum += Vector3.LerpUnclamped(sampleA, sampleB, t);
                    // Thin Supported Water 的上下表面可能只隔不到一个 Lattice 间距。
                    // 此时中央差分会同时“看见”两层 Crossing，插值 Gradient 可能反向；
                    // 后续若用它决定 Triangle winding，Cull Back 就会把整个顶面裁掉。
                    //
                    // Sign Change 本身提供了不会歧义的拓扑方向：Signed Distance 从负数
                    // (inside) 走向正数 (outside) 的方向一定朝外。Surface Nets 会把一个
                    // Dual Cell 内多条 X/Y/Z Crossing 的朝外方向相加：平面得到轴向法线，
                    // 圆角同时得到多个轴分量。它比高分辨率 Hermite Gradient 略显几何化，
                    // 但在当前 SamplesPerCell=2 的欠采样薄层中方向稳定；细节仍由 Shader
                    // 的程序化波纹补充。该计算不增加 Sparse World 查询或 managed allocation。
                    Vector3 outwardEdgeDirection = IsInside(scalarA)
                        ? sampleB - sampleA
                        : sampleA - sampleB;
                    gradientSum += outwardEdgeDirection;
                    crossingCount++;
                }

                if (crossingCount == 0)
                    continue;

                Vector3 localSamplePosition = positionSum / crossingCount;
                Vector3 localCellPosition = localSamplePosition * inverseSamplesPerCell;
                Vector3 normal = gradientSum.sqrMagnitude > 0.00000001f
                    ? gradientSum.normalized
                    : Vector3.up;
                int vertexIndex = vertices.Count;
                vertices.Add(localCellPosition * world.CellSize);
                normals.Add(normal);
                uvs.Add(new Vector2(
                    world.Origin.x + (baseX + localCellPosition.x) * world.CellSize,
                    world.Origin.z + (baseZ + localCellPosition.z) * world.CellSize));
                workspace.DualVertexIndices[workspace.DualIndex(x, y, z)] = vertexIndex;
            }
        }

        private static void ConnectOwnedEdges(
            WaterVolumeMeshingWorkspace workspace,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> indices)
        {
            if (!workspace.HasActiveSampleBounds)
                return;

            int resolution = workspace.SampleResolution;
            int minimumX = Mathf.Max(0, workspace.ActiveSampleMinimumX - 1);
            int minimumY = Mathf.Max(0, workspace.ActiveSampleMinimumY - 1);
            int minimumZ = Mathf.Max(0, workspace.ActiveSampleMinimumZ - 1);
            int maximumX = Mathf.Min(resolution - 1, workspace.ActiveSampleMaximumX);
            int maximumY = Mathf.Min(resolution - 1, workspace.ActiveSampleMaximumY);
            int maximumZ = Mathf.Min(resolution - 1, workspace.ActiveSampleMaximumZ);

            // 每个 Chunk 拥有 Global Sample Start 位于半开区间 [0,N) 的三条正轴 Edge。
            // 水体在 Empty 邻页方向不会越过 Cell Bounds；scalar==0 视为 outside，因此正好位于
            // Chunk Boundary 的外表面仍由含水 Chunk 自己输出，不依赖 Empty Chunk 拥有 View。
            for (int z = minimumZ; z <= maximumZ; z++)
            for (int y = minimumY; y <= maximumY; y++)
            for (int x = minimumX; x <= maximumX; x++)
            {
                if (HasSignChange(
                        Scalar(workspace, x, y, z),
                        Scalar(workspace, x + 1, y, z)))
                {
                    AddOrientedQuad(
                        Dual(workspace, x, y - 1, z - 1),
                        Dual(workspace, x, y, z - 1),
                        Dual(workspace, x, y, z),
                        Dual(workspace, x, y - 1, z),
                        vertices,
                        normals,
                        indices);
                }

                if (HasSignChange(
                        Scalar(workspace, x, y, z),
                        Scalar(workspace, x, y + 1, z)))
                {
                    AddOrientedQuad(
                        Dual(workspace, x - 1, y, z - 1),
                        Dual(workspace, x - 1, y, z),
                        Dual(workspace, x, y, z),
                        Dual(workspace, x, y, z - 1),
                        vertices,
                        normals,
                        indices);
                }

                if (HasSignChange(
                        Scalar(workspace, x, y, z),
                        Scalar(workspace, x, y, z + 1)))
                {
                    AddOrientedQuad(
                        Dual(workspace, x - 1, y - 1, z),
                        Dual(workspace, x, y - 1, z),
                        Dual(workspace, x, y, z),
                        Dual(workspace, x - 1, y, z),
                        vertices,
                        normals,
                        indices);
                }
            }
        }

        private static void AddOrientedQuad(
            int a,
            int b,
            int c,
            int d,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> indices)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0)
                return;

            Vector3 triangleNormal = Vector3.Cross(
                vertices[b] - vertices[a],
                vertices[c] - vertices[a]);
            Vector3 averageNormal = normals[a] + normals[b] + normals[c] + normals[d];
            if (Vector3.Dot(triangleNormal, averageNormal) < 0f)
            {
                int temporary = b;
                b = d;
                d = temporary;
            }

            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
            indices.Add(a);
            indices.Add(c);
            indices.Add(d);
        }

        private static float Scalar(
            WaterVolumeMeshingWorkspace workspace,
            int x,
            int y,
            int z)
        {
            return workspace.Scalars[workspace.ScalarIndex(x, y, z)];
        }

        private static int Dual(
            WaterVolumeMeshingWorkspace workspace,
            int x,
            int y,
            int z)
        {
            return workspace.DualVertexIndices[workspace.DualIndex(x, y, z)];
        }

        private static bool HasSignChange(float a, float b)
        {
            return IsInside(a) != IsInside(b);
        }

        private static bool IsInside(float scalar)
        {
            // d==0 属于表面外侧。这样位于 Chunk Boundary 的 supported box 外壁会在
            // 当前 Chunk 的 [0,1] Sample Edge 上产生 Crossing，而不是落到 Empty 邻页。
            return scalar < 0f;
        }

        private static void ValidateArguments(
            IElementWorldReadOnly world,
            in WaterVolumeMeshingSettings settings,
            WaterVolumeMeshingWorkspace workspace,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> indices)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (normals == null)
                throw new ArgumentNullException(nameof(normals));
            if (uvs == null)
                throw new ArgumentNullException(nameof(uvs));
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));
            if (workspace.ChunkSize != world.ChunkSize)
                throw new ArgumentException(
                    "Workspace ChunkSize must match the world.",
                    nameof(workspace));
            if (workspace.SamplesPerCell != settings.SamplesPerCell)
                throw new ArgumentException(
                    "Workspace SamplesPerCell must match the settings snapshot.",
                    nameof(workspace));
        }
    }
}
