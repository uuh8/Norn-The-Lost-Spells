using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 固定世界坐标、Global Cell、Chunk Key 与 Local Cell 之间的纯数学转换。
    /// 这里不读取 Transform，也不保存玩家位置，因此玩家移动不会改变已经写入的元素数据身份。
    /// </summary>
    public static class ElementWorldCoordinates
    {
        public static Vector3Int WorldToGlobalCell(
            Vector3 worldPosition,
            Vector3 worldOrigin,
            float cellSize)
        {
            if (cellSize <= 0f || float.IsNaN(cellSize) || float.IsInfinity(cellSize))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cellSize),
                    cellSize,
                    "Cell size must be finite and greater than zero.");
            }

            Vector3 relative = worldPosition - worldOrigin;
            float inverseCellSize = 1f / cellSize;

            // 数学公式：globalCell = floor((worldPosition - origin) / cellSize)。
            // 必须使用 FloorToInt；若用 C# 强制转 int，-0.04 会截断为 0，导致原点负侧落入错误 Cell。
            return new Vector3Int(
                Mathf.FloorToInt(relative.x * inverseCellSize),
                Mathf.FloorToInt(relative.y * inverseCellSize),
                Mathf.FloorToInt(relative.z * inverseCellSize));
        }

        public static void GlobalCellToChunkAndLocal(
            Vector3Int globalCell,
            int chunkSize,
            out ElementChunkKey chunk,
            out Vector3Int localCell)
        {
            SplitAxis(globalCell.x, chunkSize, out int chunkX, out int localX);
            SplitAxis(globalCell.y, chunkSize, out int chunkY, out int localY);
            SplitAxis(globalCell.z, chunkSize, out int chunkZ, out int localZ);

            chunk = new ElementChunkKey(chunkX, chunkY, chunkZ);
            localCell = new Vector3Int(localX, localY, localZ);
        }

        public static Vector3Int ComposeGlobalCell(
            ElementChunkKey chunk,
            Vector3Int localCell,
            int chunkSize)
        {
            ValidateChunkSize(chunkSize);
            ValidateLocalAxis(localCell.x, chunkSize, nameof(localCell));
            ValidateLocalAxis(localCell.y, chunkSize, nameof(localCell));
            ValidateLocalAxis(localCell.z, chunkSize, nameof(localCell));

            checked
            {
                return new Vector3Int(
                    chunk.X * chunkSize + localCell.x,
                    chunk.Y * chunkSize + localCell.y,
                    chunk.Z * chunkSize + localCell.z);
            }
        }

        public static void SplitAxis(
            int globalCell,
            int chunkSize,
            out int chunk,
            out int local)
        {
            ValidateChunkSize(chunkSize);

            // C# 的整数除法向 0 截断，不等于空间分块需要的 Floor Division。
            // 先做普通除法，再在“负数且有余数”时向更小整数修正一次：-1 / 8 应属于 Chunk -1，而不是 0。
            chunk = globalCell / chunkSize;
            int remainder = globalCell % chunkSize;
            if (remainder < 0)
            {
                chunk--;
            }

            // 公式：local = global - chunk * chunkSize，因此 local 始终落在 [0, chunkSize - 1]。
            local = globalCell - chunk * chunkSize;
        }

        private static void ValidateChunkSize(int chunkSize)
        {
            if (chunkSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkSize),
                    chunkSize,
                    "Chunk size must be greater than zero.");
            }
        }

        private static void ValidateLocalAxis(int value, int chunkSize, string parameterName)
        {
            if (value < 0 || value >= chunkSize)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    $"Local cell axis must be in [0, {chunkSize - 1}].");
            }
        }
    }
}
