using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 液体的空间与数量映射。粗格负责少算，单位体积倍率负责注水手感，两者分别配置。
    /// 进入 Play 时建立快照，运行中不能改坐标尺寸或容量，否则旧账本将被错误解释。
    /// </summary>
    public readonly struct LiquidVolumeSettings
    {
        public readonly float WorldCellSize, CellSize;
        public readonly int ResolutionScale, CapacityGmu;
        public readonly double CubicMetersPerGmu;

        public LiquidVolumeSettings(float worldCellSize, int resolutionScale,
            int referenceCapacityGmu, float unitVolumeMultiplier)
        {
            if (!float.IsFinite(worldCellSize) || worldCellSize <= 0f
                || resolutionScale < 1 || resolutionScale > 4
                || referenceCapacityGmu < 1 || referenceCapacityGmu > 255
                || !float.IsFinite(unitVolumeMultiplier) || unitVolumeMultiplier <= 0f)
                throw new ArgumentOutOfRangeException(nameof(resolutionScale));
            double capacity = (double)referenceCapacityGmu * resolutionScale * resolutionScale
                * resolutionScale / unitVolumeMultiplier;
            if (capacity < 1d || capacity > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(unitVolumeMultiplier));
            float cellSize = worldCellSize * resolutionScale;
            if (!float.IsFinite(cellSize))
                throw new ArgumentOutOfRangeException(nameof(worldCellSize));
            WorldCellSize = worldCellSize;
            ResolutionScale = resolutionScale;
            CellSize = cellSize;
            CapacityGmu = (int)Math.Round(capacity, MidpointRounding.AwayFromZero);
            // GMU 为整数，容量四舍五入后以实际容量为准；显示、湖泊和注水共用此体积。
            CubicMetersPerGmu = (double)CellSize * CellSize * CellSize / CapacityGmu;
        }

        public Vector3Int WorldCellToLiquidCell(Vector3Int worldCell)
        {
            int scale = Math.Max(1, ResolutionScale);
            return new Vector3Int(FloorDivide(worldCell.x, scale),
                FloorDivide(worldCell.y, scale), FloorDivide(worldCell.z, scale));
        }

        public Vector3Int LiquidCellToWorldCell(Vector3Int liquidCell)
        {
            int scale = Math.Max(1, ResolutionScale);
            // 火的点火位置取液体底层的中心细格，不能把粗格编号当作火格编号。
            return liquidCell * scale + new Vector3Int(scale / 2, 0, scale / 2);
        }

        private static int FloorDivide(int value, int divisor)
        {
            int quotient = value / divisor;
            return value % divisor < 0 ? quotient - 1 : quotient;
        }
    }
}
