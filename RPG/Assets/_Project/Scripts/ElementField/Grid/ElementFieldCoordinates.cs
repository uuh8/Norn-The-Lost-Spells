using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 集中管理 World Space、Grid Space 与线性数组之间的坐标协议。
    /// Projectile、Exposure、Simulation 和 Rendering 必须复用同一公式，避免同一点在不同模块落入不同 Cell。
    /// </summary>
    public static class ElementFieldCoordinates
    {
        public static bool TryWorldToCell(
            Vector3 worldPosition,
            Vector3 origin,
            float cellSize,
            Vector3Int dimensions,
            out Vector3Int coordinate)
        {
            ValidateDimensions(dimensions);
            coordinate = WorldToCellUnchecked(worldPosition, origin, cellSize);
            return IsInside(coordinate, dimensions);
        }

        /// <summary>
        /// 只执行坐标量化，不进行 Bounds Check。范围型写入会先计算球体 AABB 的最小/最大 Cell，
        /// 再把坐标 Clamp 到 Field 范围；若这里提前拒绝越界端点，会丢掉仍位于 Field 内的有效部分。
        /// </summary>
        public static Vector3Int WorldToCellUnchecked(
            Vector3 worldPosition,
            Vector3 origin,
            float cellSize)
        {
            ValidateCellSize(cellSize);

            Vector3 local = worldPosition - origin;

            // 公式：cell = floor((world - origin) / cellSize)。
            // 必须使用 Floor 而不是 (int) 截断：-0.04 / 0.25 应属于 -1，不能被错误截断为第 0 格。
            return new Vector3Int(
                Mathf.FloorToInt(local.x / cellSize),
                Mathf.FloorToInt(local.y / cellSize),
                Mathf.FloorToInt(local.z / cellSize));
        }

        public static bool IsInside(Vector3Int coordinate, Vector3Int dimensions)
        {
            ValidateDimensions(dimensions);
            return coordinate.x >= 0 && coordinate.x < dimensions.x
                && coordinate.y >= 0 && coordinate.y < dimensions.y
                && coordinate.z >= 0 && coordinate.z < dimensions.z;
        }

        public static int ToIndex(Vector3Int coordinate, Vector3Int dimensions)
        {
            int cellCount = GetCellCount(dimensions);
            if (!IsInside(coordinate, dimensions))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(coordinate),
                    coordinate,
                    "Cell coordinate must be inside the grid dimensions.");
            }

            // X 是最内层、变化最快的轴：index = x + sizeX * (y + sizeY * z)。
            // 相邻 X Cell 因而位于相邻内存地址，按 X 扫描时更符合 CPU Cache 的连续读取方式。
            long index = coordinate.x
                + (long)dimensions.x * (coordinate.y + (long)dimensions.y * coordinate.z);

            if (index < 0 || index >= cellCount)
            {
                throw new ArgumentOutOfRangeException(nameof(coordinate));
            }

            return (int)index;
        }

        public static Vector3Int FromIndex(int index, Vector3Int dimensions)
        {
            int cellCount = GetCellCount(dimensions);
            if (index < 0 || index >= cellCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "Linear index must be inside the grid storage.");
            }

            int x = index % dimensions.x;
            int yz = index / dimensions.x;
            int y = yz % dimensions.y;
            int z = yz / dimensions.y;
            return new Vector3Int(x, y, z);
        }

        internal static int GetCellCount(Vector3Int dimensions)
        {
            ValidateDimensions(dimensions);

            // 先提升到 long 再相乘，避免 int 在安全检查前溢出并变成错误的小数或负数。
            long count = (long)dimensions.x * dimensions.y * dimensions.z;
            if (count > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dimensions),
                    dimensions,
                    "Grid cell count exceeds the maximum managed array length represented by Int32.");
            }

            return (int)count;
        }

        private static void ValidateDimensions(Vector3Int dimensions)
        {
            if (dimensions.x <= 0 || dimensions.y <= 0 || dimensions.z <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dimensions),
                    dimensions,
                    "All grid dimensions must be greater than zero.");
            }
        }

        private static void ValidateCellSize(float cellSize)
        {
            if (cellSize <= 0f || float.IsNaN(cellSize) || float.IsInfinity(cellSize))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellSize),
                    cellSize,
                    "Cell size must be a finite value greater than zero.");
            }
        }
    }
}
