using Game.Materials;
using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 把一个 World Space 写入命令转换成有限数量的 Cell 修改。
    /// 两遍扫描直接遍历球体 AABB，不建立临时 List，因而不会在命中时产生 GC Alloc。
    /// </summary>
    public static class ElementFieldWriteProcessor
    {
        private const float WeightEpsilon = 0.000001f;

        public static bool TryApply(
            ElementGrid grid,
            in ElementWriteRequest request,
            Vector3 origin,
            float cellSize,
            out int changedCells)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));

            changedCells = 0;
            if (!IsSupported(request.MaterialKind)
                || request.TotalAmount == 0
                || request.Radius < 0f
                || float.IsNaN(request.Radius)
                || float.IsInfinity(request.Radius))
            {
                return false;
            }

            // Point Deposit 不做 d/r，既避免 radius=0 除法，也精确定义“命中所在单格”。
            if (request.Radius == 0f)
            {
                if (!ElementFieldCoordinates.TryWorldToCell(
                        request.WorldPosition, origin, cellSize, grid.Dimensions, out Vector3Int cell)
                    || grid.IsSolid(cell)
                    || !CanAccept(grid.GetCell(cell), request.MaterialKind))
                {
                    return false;
                }

                if (ApplyAmount(grid, cell, request.MaterialKind, request.TotalAmount))
                    changedCells = 1;
                return true;
            }

            Vector3 radiusVector = Vector3.one * request.Radius;
            Vector3Int min = ClampToGrid(
                ElementFieldCoordinates.WorldToCellUnchecked(
                    request.WorldPosition - radiusVector, origin, cellSize),
                grid.Dimensions);
            Vector3Int max = ClampToGrid(
                ElementFieldCoordinates.WorldToCellUnchecked(
                    request.WorldPosition + radiusVector, origin, cellSize),
                grid.Dimensions);

            float weightSum = 0f;
            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var coordinate = new Vector3Int(x, y, z);
                if (grid.IsSolid(coordinate)
                    || !CanAccept(grid.GetCell(coordinate), request.MaterialKind))
                {
                    continue;
                }

                float weight = CalculateWeight(coordinate, request, origin, cellSize);
                if (weight > 0f)
                    weightSum += weight;
            }

            if (weightSum <= WeightEpsilon)
                return false;

            int remainingAmount = request.TotalAmount;
            float remainingWeight = weightSum;
            for (int z = min.z; z <= max.z && remainingAmount > 0; z++)
            for (int y = min.y; y <= max.y && remainingAmount > 0; y++)
            for (int x = min.x; x <= max.x && remainingAmount > 0; x++)
            {
                var coordinate = new Vector3Int(x, y, z);
                if (grid.IsSolid(coordinate)
                    || !CanAccept(grid.GetCell(coordinate), request.MaterialKind))
                {
                    continue;
                }

                float weight = CalculateWeight(coordinate, request, origin, cellSize);
                if (weight <= 0f)
                    continue;

                // 用“剩余量/剩余权重”逐步分配，最后一个有效 Cell 自动拿走余数，
                // 避免每格独立 Round(total*weight/sum) 导致总量凭空丢失。
                int share = remainingWeight <= weight + WeightEpsilon
                    ? remainingAmount
                    : Mathf.RoundToInt(remainingAmount * weight / remainingWeight);
                share = Mathf.Clamp(share, 0, remainingAmount);
                remainingAmount -= share;
                remainingWeight -= weight;

                if (share > 0 && ApplyAmount(grid, coordinate, request.MaterialKind, share))
                    changedCells++;
            }

            return true;
        }

        private static bool ApplyAmount(
            ElementGrid grid,
            Vector3Int coordinate,
            MaterialId incomingKind,
            int incomingAmount)
        {
            ElementCell before = grid.GetCell(coordinate);
            ElementCell after;

            if (before.IsEmpty)
            {
                after = new ElementCell(incomingKind, (byte)Mathf.Min(incomingAmount, byte.MaxValue));
            }
            else if (before.MaterialKind == incomingKind)
            {
                after = new ElementCell(
                    incomingKind,
                    (byte)Mathf.Min(before.Amount + incomingAmount, byte.MaxValue));
            }
            else
            {
                // Water/Fire 即时写入先按 1:1 抵消旧材料，只有剩余 Incoming 才能替换 Cell。
                int consumed = Mathf.Min(before.Amount, incomingAmount);
                int oldRemaining = before.Amount - consumed;
                int incomingRemaining = incomingAmount - consumed;
                if (oldRemaining > 0)
                    after = new ElementCell(before.MaterialKind, (byte)oldRemaining);
                else if (incomingRemaining > 0)
                    after = new ElementCell(incomingKind, (byte)Mathf.Min(incomingRemaining, byte.MaxValue));
                else
                    after = default;
            }

            if (before.MaterialKind == after.MaterialKind && before.Amount == after.Amount)
                return false;

            grid.SetCell(coordinate, after);
            grid.MarkCellDirty(coordinate);
            return true;
        }

        private static bool CanAccept(ElementCell cell, MaterialId incomingKind)
        {
            return cell.IsEmpty
                || cell.MaterialKind == incomingKind
                || IsOpposedWaterFire(cell.MaterialKind, incomingKind);
        }

        private static bool IsOpposedWaterFire(MaterialId a, MaterialId b)
        {
            return (a == MaterialId.Water && b == MaterialId.Fire)
                || (a == MaterialId.Fire && b == MaterialId.Water);
        }

        private static bool IsSupported(MaterialId kind)
        {
            return kind == MaterialId.Water || kind == MaterialId.Fire;
        }

        private static float CalculateWeight(
            Vector3Int coordinate,
            in ElementWriteRequest request,
            Vector3 origin,
            float cellSize)
        {
            Vector3 center = origin + new Vector3(
                (coordinate.x + 0.5f) * cellSize,
                (coordinate.y + 0.5f) * cellSize,
                (coordinate.z + 0.5f) * cellSize);
            float distance = Vector3.Distance(center, request.WorldPosition);
            if (distance > request.Radius)
                return 0f;
            if (!request.UseLinearFalloff)
                return 1f;
            return Mathf.Clamp01(1f - distance / request.Radius);
        }

        private static Vector3Int ClampToGrid(Vector3Int coordinate, Vector3Int dimensions)
        {
            return new Vector3Int(
                Mathf.Clamp(coordinate.x, 0, dimensions.x - 1),
                Mathf.Clamp(coordinate.y, 0, dimensions.y - 1),
                Mathf.Clamp(coordinate.z, 0, dimensions.z - 1));
        }
    }
}
