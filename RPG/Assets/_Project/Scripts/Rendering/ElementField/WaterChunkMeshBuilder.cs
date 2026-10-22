using Game.Materials;
using System;
using System.Collections.Generic;
using Game.ElementField;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 把一个 Chunk 内的离散 Water Cell 转换为只包含可见表面的 Mesh 数据。
    ///
    /// Builder 不创建 Mesh、GameObject 或 Material，也不修改 ElementField；它只通过
    /// IElementFieldReadOnly 读取 Gameplay Snapshot，并把结果写入调用方长期复用的 List。
    /// 这种纯转换便于 NUnit 测试，也把“水面几何”与“Water Shader 着色”明确分离。
    /// </summary>
    public static class WaterChunkMeshBuilder
    {
        private const float HeightEpsilon = 0.000001f;

        /// <summary>
        /// 生成 Chunk Local Space 顶点、连续 UV 与 Triangle Index。
        /// 调用方应把承载 Mesh 的 Transform 放在该 Chunk 的 World Space 原点。
        /// </summary>
        public static void Build(
            IElementFieldReadOnly field,
            Vector3Int chunkCoordinate,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> indices)
        {
            if (field == null)
                throw new ArgumentNullException(nameof(field));
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (uvs == null)
                throw new ArgumentNullException(nameof(uvs));
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));

            // Clear 只把 Count 归零，不释放 List 内部数组；下一次 Dirty Chunk 重建会复用 Capacity。
            vertices.Clear();
            uvs.Clear();
            indices.Clear();

            if (!field.IsInitialized || field.CellSize <= 0f || field.ChunkSize <= 0)
                return;

            Vector3Int chunkCounts = field.ChunkCounts;
            if (chunkCoordinate.x < 0 || chunkCoordinate.x >= chunkCounts.x
                || chunkCoordinate.y < 0 || chunkCoordinate.y >= chunkCounts.y
                || chunkCoordinate.z < 0 || chunkCoordinate.z >= chunkCounts.z)
            {
                return;
            }

            int chunkSize = field.ChunkSize;
            Vector3Int dimensions = field.Dimensions;
            int startX = chunkCoordinate.x * chunkSize;
            int startY = chunkCoordinate.y * chunkSize;
            int startZ = chunkCoordinate.z * chunkSize;
            int endX = Mathf.Min(startX + chunkSize, dimensions.x);
            int endY = Mathf.Min(startY + chunkSize, dimensions.y);
            int endZ = Mathf.Min(startZ + chunkSize, dimensions.z);

            for (int z = startZ; z < endZ; z++)
            for (int y = startY; y < endY; y++)
            for (int x = startX; x < endX; x++)
            {
                if (!TryGetWaterAmount(field, x, y, z, out byte amount))
                    continue;

                AddCellSurface(
                    field,
                    x,
                    y,
                    z,
                    amount,
                    startX,
                    startY,
                    startZ,
                    vertices,
                    uvs,
                    indices);
            }
        }

        /// <summary>
        /// 为稀疏世界的一个 Chunk 生成 Local Space Water Mesh。
        /// 与有限 Field 版本的核心差别有两点：邻居查询使用 Global Cell，因此能跨 Chunk 剔除内部面；
        /// UV 使用固定世界米制坐标，因此相邻 Chunk 的噪声纹理不会在边界重新从零开始。
        /// </summary>
        public static void BuildWorldChunk(
            IElementWorldReadOnly world,
            ElementChunkKey chunkKey,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> indices)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (uvs == null)
                throw new ArgumentNullException(nameof(uvs));
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));

            vertices.Clear();
            uvs.Clear();
            indices.Clear();

            if (!world.IsInitialized || world.CellSize <= 0f || world.ChunkSize <= 0)
                return;

            int chunkSize = world.ChunkSize;
            for (int z = 0; z < chunkSize; z++)
            for (int y = 0; y < chunkSize; y++)
            for (int x = 0; x < chunkSize; x++)
            {
                var localCell = new Vector3Int(x, y, z);
                Vector3Int globalCell = ElementWorldCoordinates.ComposeGlobalCell(
                    chunkKey,
                    localCell,
                    chunkSize);
                if (!TryGetWorldWaterAmount(world, globalCell, out byte amount))
                    continue;

                AddWorldCellSurface(
                    world,
                    localCell,
                    globalCell,
                    amount,
                    vertices,
                    uvs,
                    indices);
            }
        }

        private static void AddWorldCellSurface(
            IElementWorldReadOnly world,
            Vector3Int localCell,
            Vector3Int globalCell,
            byte amount,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> indices)
        {
            float cellSize = world.CellSize;
            float x0 = localCell.x * cellSize;
            float x1 = x0 + cellSize;
            float z0 = localCell.z * cellSize;
            float z1 = z0 + cellSize;
            float bottomY = localCell.y * cellSize;
            float fillRatio = amount / (float)byte.MaxValue;
            float surfaceY = bottomY + cellSize * fillRatio;

            // UV 的单位是 World Meter，而不是 Local Cell Index：
            // uv = globalCellXZ * cellSize。Shader 中的 Noise Scale 因而跨 Chunk 连续。
            float globalX0 = globalCell.x * cellSize;
            float globalX1 = globalX0 + cellSize;
            float globalZ0 = globalCell.z * cellSize;
            float globalZ1 = globalZ0 + cellSize;
            float globalBottomY = globalCell.y * cellSize;
            float globalSurfaceY = globalBottomY + cellSize * fillRatio;

            if (!TryGetWorldWaterAmount(world, globalCell + Vector3Int.up, out _))
            {
                AddQuad(
                    new Vector3(x0, surfaceY, z0),
                    new Vector3(x0, surfaceY, z1),
                    new Vector3(x1, surfaceY, z1),
                    new Vector3(x1, surfaceY, z0),
                    new Vector2(globalX0, globalZ0),
                    new Vector2(globalX0, globalZ1),
                    new Vector2(globalX1, globalZ1),
                    new Vector2(globalX1, globalZ0),
                    vertices,
                    uvs,
                    indices);
            }

            float negativeX = GetWorldNeighborSurfaceY(
                world, globalCell + Vector3Int.left, bottomY, cellSize);
            if (surfaceY - negativeX > HeightEpsilon)
            {
                float neighborWorldY = globalBottomY + (negativeX - bottomY);
                AddQuad(
                    new Vector3(x0, negativeX, z0),
                    new Vector3(x0, negativeX, z1),
                    new Vector3(x0, surfaceY, z1),
                    new Vector3(x0, surfaceY, z0),
                    new Vector2(globalZ0, neighborWorldY),
                    new Vector2(globalZ1, neighborWorldY),
                    new Vector2(globalZ1, globalSurfaceY),
                    new Vector2(globalZ0, globalSurfaceY),
                    vertices, uvs, indices);
            }

            float positiveX = GetWorldNeighborSurfaceY(
                world, globalCell + Vector3Int.right, bottomY, cellSize);
            if (surfaceY - positiveX > HeightEpsilon)
            {
                float neighborWorldY = globalBottomY + (positiveX - bottomY);
                AddQuad(
                    new Vector3(x1, positiveX, z1),
                    new Vector3(x1, positiveX, z0),
                    new Vector3(x1, surfaceY, z0),
                    new Vector3(x1, surfaceY, z1),
                    new Vector2(globalZ1, neighborWorldY),
                    new Vector2(globalZ0, neighborWorldY),
                    new Vector2(globalZ0, globalSurfaceY),
                    new Vector2(globalZ1, globalSurfaceY),
                    vertices, uvs, indices);
            }

            float negativeZ = GetWorldNeighborSurfaceY(
                world, globalCell + new Vector3Int(0, 0, -1), bottomY, cellSize);
            if (surfaceY - negativeZ > HeightEpsilon)
            {
                float neighborWorldY = globalBottomY + (negativeZ - bottomY);
                AddQuad(
                    new Vector3(x1, negativeZ, z0),
                    new Vector3(x0, negativeZ, z0),
                    new Vector3(x0, surfaceY, z0),
                    new Vector3(x1, surfaceY, z0),
                    new Vector2(globalX1, neighborWorldY),
                    new Vector2(globalX0, neighborWorldY),
                    new Vector2(globalX0, globalSurfaceY),
                    new Vector2(globalX1, globalSurfaceY),
                    vertices, uvs, indices);
            }

            float positiveZ = GetWorldNeighborSurfaceY(
                world, globalCell + new Vector3Int(0, 0, 1), bottomY, cellSize);
            if (surfaceY - positiveZ > HeightEpsilon)
            {
                float neighborWorldY = globalBottomY + (positiveZ - bottomY);
                AddQuad(
                    new Vector3(x0, positiveZ, z1),
                    new Vector3(x1, positiveZ, z1),
                    new Vector3(x1, surfaceY, z1),
                    new Vector3(x0, surfaceY, z1),
                    new Vector2(globalX0, neighborWorldY),
                    new Vector2(globalX1, neighborWorldY),
                    new Vector2(globalX1, globalSurfaceY),
                    new Vector2(globalX0, globalSurfaceY),
                    vertices, uvs, indices);
            }
        }

        private static float GetWorldNeighborSurfaceY(
            IElementWorldReadOnly world,
            Vector3Int globalCell,
            float currentLocalBottomY,
            float cellSize)
        {
            return TryGetWorldWaterAmount(world, globalCell, out byte amount)
                ? currentLocalBottomY + cellSize * (amount / (float)byte.MaxValue)
                : currentLocalBottomY;
        }

        private static bool TryGetWorldWaterAmount(
            IElementWorldReadOnly world,
            Vector3Int globalCell,
            out byte amount)
        {
            if (world.IsSolid(globalCell)
                || !world.TryGetCell(globalCell, out ElementCell cell)
                || cell.IsEmpty
                || cell.MaterialKind != MaterialId.Water)
            {
                amount = 0;
                return false;
            }

            amount = cell.Amount;
            return true;
        }

        private static void AddCellSurface(
            IElementFieldReadOnly field,
            int x,
            int y,
            int z,
            byte amount,
            int chunkStartX,
            int chunkStartY,
            int chunkStartZ,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> indices)
        {
            float cellSize = field.CellSize;
            float x0 = (x - chunkStartX) * cellSize;
            float x1 = x0 + cellSize;
            float z0 = (z - chunkStartZ) * cellSize;
            float z1 = z0 + cellSize;
            float bottomY = (y - chunkStartY) * cellSize;

            // Cell Amount 是 [0,255] 离散存量；视觉高度使用线性映射。
            // surfaceY = cellBottomY + cellSize * amount / 255。
            float fillRatio = amount / (float)byte.MaxValue;
            float surfaceY = bottomY + cellSize * fillRatio;

            // 当前 Cell 上方没有 Water 时才生成顶面。顶点顺序使 Cross(B-A, C-A) 指向 +Y。
            if (!TryGetWaterAmount(field, x, y + 1, z, out _))
            {
                AddQuad(
                    new Vector3(x0, surfaceY, z0),
                    new Vector3(x0, surfaceY, z1),
                    new Vector3(x1, surfaceY, z1),
                    new Vector3(x1, surfaceY, z0),
                    new Vector2(x, z),
                    new Vector2(x, z + 1f),
                    new Vector2(x + 1f, z + 1f),
                    new Vector2(x + 1f, z),
                    vertices,
                    uvs,
                    indices);
            }

            // Side 只生成“当前水面高于邻居水面”的高度差。相同高度邻居之间没有内部面，
            // 高低水位相邻时也只补齐露出的水墙，不生成从 Cell 底部贯穿上来的重叠墙面。
            float negativeXNeighbor = GetNeighborSurfaceY(field, x - 1, y, z, bottomY, cellSize);
            if (surfaceY - negativeXNeighbor > HeightEpsilon)
            {
                AddQuad(
                    new Vector3(x0, negativeXNeighbor, z0),
                    new Vector3(x0, negativeXNeighbor, z1),
                    new Vector3(x0, surfaceY, z1),
                    new Vector3(x0, surfaceY, z0),
                    new Vector2(z, y + (negativeXNeighbor - bottomY) / cellSize),
                    new Vector2(z + 1f, y + (negativeXNeighbor - bottomY) / cellSize),
                    new Vector2(z + 1f, y + fillRatio),
                    new Vector2(z, y + fillRatio),
                    vertices,
                    uvs,
                    indices);
            }

            float positiveXNeighbor = GetNeighborSurfaceY(field, x + 1, y, z, bottomY, cellSize);
            if (surfaceY - positiveXNeighbor > HeightEpsilon)
            {
                AddQuad(
                    new Vector3(x1, positiveXNeighbor, z1),
                    new Vector3(x1, positiveXNeighbor, z0),
                    new Vector3(x1, surfaceY, z0),
                    new Vector3(x1, surfaceY, z1),
                    new Vector2(z + 1f, y + (positiveXNeighbor - bottomY) / cellSize),
                    new Vector2(z, y + (positiveXNeighbor - bottomY) / cellSize),
                    new Vector2(z, y + fillRatio),
                    new Vector2(z + 1f, y + fillRatio),
                    vertices,
                    uvs,
                    indices);
            }

            float negativeZNeighbor = GetNeighborSurfaceY(field, x, y, z - 1, bottomY, cellSize);
            if (surfaceY - negativeZNeighbor > HeightEpsilon)
            {
                AddQuad(
                    new Vector3(x1, negativeZNeighbor, z0),
                    new Vector3(x0, negativeZNeighbor, z0),
                    new Vector3(x0, surfaceY, z0),
                    new Vector3(x1, surfaceY, z0),
                    new Vector2(x + 1f, y + (negativeZNeighbor - bottomY) / cellSize),
                    new Vector2(x, y + (negativeZNeighbor - bottomY) / cellSize),
                    new Vector2(x, y + fillRatio),
                    new Vector2(x + 1f, y + fillRatio),
                    vertices,
                    uvs,
                    indices);
            }

            float positiveZNeighbor = GetNeighborSurfaceY(field, x, y, z + 1, bottomY, cellSize);
            if (surfaceY - positiveZNeighbor > HeightEpsilon)
            {
                AddQuad(
                    new Vector3(x0, positiveZNeighbor, z1),
                    new Vector3(x1, positiveZNeighbor, z1),
                    new Vector3(x1, surfaceY, z1),
                    new Vector3(x0, surfaceY, z1),
                    new Vector2(x, y + (positiveZNeighbor - bottomY) / cellSize),
                    new Vector2(x + 1f, y + (positiveZNeighbor - bottomY) / cellSize),
                    new Vector2(x + 1f, y + fillRatio),
                    new Vector2(x, y + fillRatio),
                    vertices,
                    uvs,
                    indices);
            }
        }

        private static float GetNeighborSurfaceY(
            IElementFieldReadOnly field,
            int x,
            int y,
            int z,
            float currentCellBottomY,
            float cellSize)
        {
            return TryGetWaterAmount(field, x, y, z, out byte amount)
                ? currentCellBottomY + cellSize * (amount / (float)byte.MaxValue)
                : currentCellBottomY;
        }

        private static bool TryGetWaterAmount(
            IElementFieldReadOnly field,
            int x,
            int y,
            int z,
            out byte amount)
        {
            Vector3Int dimensions = field.Dimensions;
            if (x < 0 || x >= dimensions.x
                || y < 0 || y >= dimensions.y
                || z < 0 || z >= dimensions.z
                || field.IsSolid(x, y, z))
            {
                amount = 0;
                return false;
            }

            ElementCell cell = field.GetCell(x, y, z);
            if (cell.IsEmpty || cell.MaterialKind != MaterialId.Water)
            {
                amount = 0;
                return false;
            }

            amount = cell.Amount;
            return true;
        }

        private static void AddQuad(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 d,
            Vector2 uvA,
            Vector2 uvB,
            Vector2 uvC,
            Vector2 uvD,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> indices)
        {
            int first = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            uvs.Add(uvA);
            uvs.Add(uvB);
            uvs.Add(uvC);
            uvs.Add(uvD);

            // 两个 Triangle 共用同一个 Quad 的四个顶点；Winding 为 A→B→C、A→C→D。
            indices.Add(first);
            indices.Add(first + 1);
            indices.Add(first + 2);
            indices.Add(first);
            indices.Add(first + 2);
            indices.Add(first + 3);
        }
    }
}
