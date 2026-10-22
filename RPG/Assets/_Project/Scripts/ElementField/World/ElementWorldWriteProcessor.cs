using Game.Materials;
using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 把一个 World Space Deposit 离散到稀疏世界的 Global Cells。
    /// 球形写入使用“两遍扫描”：第一遍求可接收 Cell 的总权重/容量，第二遍才真正写入，
    /// 因此跨越多少个 Chunk 都不会把 TotalAmount 误当成“每个 Chunk 各一份”。
    /// </summary>
    public static class ElementWorldWriteProcessor
    {
        private const float WeightEpsilon = 0.000001f;

        public static bool TryApply(
            ElementWorldStore store,
            in ElementWriteRequest request,
            Vector3 worldOrigin,
            float cellSize,
            long worldTick,
            out int changedCells)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (cellSize <= 0f || float.IsNaN(cellSize) || float.IsInfinity(cellSize))
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (worldTick < 0)
                throw new ArgumentOutOfRangeException(nameof(worldTick));

            changedCells = 0;
            if (!IsSupported(request.MaterialKind)
                || request.TotalAmount == 0
                || request.Radius < 0f
                || float.IsNaN(request.Radius)
                || float.IsInfinity(request.Radius))
            {
                return false;
            }

            if (request.Radius == 0f)
            {
                Vector3Int globalCell = ElementWorldCoordinates.WorldToGlobalCell(
                    request.WorldPosition,
                    worldOrigin,
                    cellSize);
                return TryApplyToGlobalCell(
                    store,
                    globalCell,
                    request.MaterialKind,
                    request.TotalAmount,
                    worldTick,
                    out changedCells);
            }

            Vector3 radiusVector = Vector3.one * request.Radius;
            Vector3Int min = ElementWorldCoordinates.WorldToGlobalCell(
                request.WorldPosition - radiusVector,
                worldOrigin,
                cellSize);
            Vector3Int max = ElementWorldCoordinates.WorldToGlobalCell(
                request.WorldPosition + radiusVector,
                worldOrigin,
                cellSize);

            float weightSum = 0f;
            long capacitySum = 0L;

            // 第一遍只计算“哪些 Global Cells 真能接收”和归一化分母。
            // 遍历顺序固定为 Z→Y→X，保证同输入下余数总是落到同一组 Global Cells。
            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                var globalCell = new Vector3Int(x, y, z);
                float weight = CalculateWeight(globalCell, in request, worldOrigin, cellSize);
                if (weight <= 0f
                    || !TryGetWritableCell(
                        store,
                        globalCell,
                        request.MaterialKind,
                        createIfMissing: true,
                        out _,
                        out _,
                        out ElementCell current))
                {
                    continue;
                }

                int capacity = GetIncomingCapacity(current, request.MaterialKind);
                if (capacity <= 0)
                    continue;

                weightSum += weight;
                capacitySum += capacity;
            }

            if (weightSum <= WeightEpsilon || capacitySum <= 0L)
                return false;

            int remainingAmount = (int)Math.Min(request.TotalAmount, capacitySum);
            float remainingWeight = weightSum;
            int appliedAmount = 0;

            // 第二遍按“剩余 Amount / 剩余 Weight”逐步归一化。
            // Cell 容量不足时只拿走它能接收的部分，余量继续留给后续 Cell，避免高 Amount 写入被静默吞掉。
            for (int z = min.z; z <= max.z && remainingAmount > 0; z++)
            for (int y = min.y; y <= max.y && remainingAmount > 0; y++)
            for (int x = min.x; x <= max.x && remainingAmount > 0; x++)
            {
                var globalCell = new Vector3Int(x, y, z);
                float weight = CalculateWeight(globalCell, in request, worldOrigin, cellSize);
                if (weight <= 0f
                    || !TryGetWritableCell(
                        store,
                        globalCell,
                        request.MaterialKind,
                        createIfMissing: false,
                        out ElementWorldChunk chunk,
                        out Vector3Int localCell,
                        out ElementCell current))
                {
                    continue;
                }

                int capacity = GetIncomingCapacity(current, request.MaterialKind);
                if (capacity <= 0)
                    continue;

                int share = remainingWeight <= weight + WeightEpsilon
                    ? remainingAmount
                    : Mathf.RoundToInt(remainingAmount * weight / remainingWeight);
                share = Mathf.Clamp(share, 0, Math.Min(remainingAmount, capacity));
                remainingWeight -= weight;
                if (share <= 0)
                    continue;

                ApplyAmount(chunk, localCell, current, request.MaterialKind, share);
                chunk.WakeForSimulation(worldTick);
                remainingAmount -= share;
                appliedAmount += share;
                changedCells++;
            }

            return appliedAmount > 0;
        }

        private static bool TryApplyToGlobalCell(
            ElementWorldStore store,
            Vector3Int globalCell,
            MaterialId incomingKind,
            int incomingAmount,
            long worldTick,
            out int changedCells)
        {
            changedCells = 0;
            if (!TryGetWritableCell(
                    store,
                    globalCell,
                    incomingKind,
                    createIfMissing: true,
                    out ElementWorldChunk chunk,
                    out Vector3Int localCell,
                    out ElementCell current))
            {
                return false;
            }

            int accepted = Math.Min(incomingAmount, GetIncomingCapacity(current, incomingKind));
            if (accepted <= 0)
                return false;

            ApplyAmount(chunk, localCell, current, incomingKind, accepted);
            chunk.WakeForSimulation(worldTick);
            changedCells = 1;
            return true;
        }

        private static bool TryGetWritableCell(
            ElementWorldStore store,
            Vector3Int globalCell,
            MaterialId incomingKind,
            bool createIfMissing,
            out ElementWorldChunk chunk,
            out Vector3Int localCell,
            out ElementCell current)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell,
                store.ChunkSize,
                out ElementChunkKey key,
                out localCell);

            bool available = createIfMissing
                ? store.TryGetOrCreateChunk(key, out chunk)
                : store.TryGetChunk(key, out chunk);
            if (!available)
            {
                current = default;
                return false;
            }

            int index = ElementFieldCoordinates.ToIndex(localCell, chunk.Grid.Dimensions);
            if (chunk.SolidMask[index])
            {
                current = default;
                return false;
            }

            current = chunk.CurrentCells[index];
            return CanAccept(current, incomingKind);
        }

        private static void ApplyAmount(
            ElementWorldChunk chunk,
            Vector3Int localCell,
            ElementCell before,
            MaterialId incomingKind,
            int incomingAmount)
        {
            ElementCell after;
            if (before.IsEmpty)
            {
                after = new ElementCell(incomingKind, (byte)incomingAmount);
            }
            else if (before.MaterialKind == incomingKind)
            {
                after = new ElementCell(incomingKind, (byte)(before.Amount + incomingAmount));
            }
            else
            {
                // Water/Fire Deposit 的即时接触仍沿用 P6-A：先 1:1 抵消旧材料，剩余 Incoming 才占据 Cell。
                int consumed = Math.Min(before.Amount, incomingAmount);
                int oldRemaining = before.Amount - consumed;
                int incomingRemaining = incomingAmount - consumed;
                if (oldRemaining > 0)
                    after = new ElementCell(before.MaterialKind, (byte)oldRemaining);
                else if (incomingRemaining > 0)
                    after = new ElementCell(incomingKind, (byte)incomingRemaining);
                else
                    after = default;
            }

            chunk.SetCell(localCell, after);
            chunk.Grid.MarkCellDirty(localCell);
        }

        private static int GetIncomingCapacity(
            ElementCell current,
            MaterialId incomingKind)
        {
            if (current.IsEmpty)
                return byte.MaxValue;
            if (current.MaterialKind == incomingKind)
                return byte.MaxValue - current.Amount;
            if (IsOpposedWaterFire(current.MaterialKind, incomingKind))
            {
                // 先抵消旧 Amount，再最多形成 255 新 Amount，因此整条命令最多可消费 old+255。
                return current.Amount + byte.MaxValue;
            }

            return 0;
        }

        private static float CalculateWeight(
            Vector3Int globalCell,
            in ElementWriteRequest request,
            Vector3 worldOrigin,
            float cellSize)
        {
            Vector3 center = worldOrigin + new Vector3(
                (globalCell.x + 0.5f) * cellSize,
                (globalCell.y + 0.5f) * cellSize,
                (globalCell.z + 0.5f) * cellSize);
            float distance = Vector3.Distance(center, request.WorldPosition);
            if (distance > request.Radius)
                return 0f;
            if (!request.UseLinearFalloff)
                return 1f;
            return Mathf.Clamp01(1f - distance / request.Radius);
        }

        private static bool CanAccept(ElementCell current, MaterialId incomingKind)
        {
            return current.IsEmpty
                || current.MaterialKind == incomingKind
                || IsOpposedWaterFire(current.MaterialKind, incomingKind);
        }

        private static bool IsOpposedWaterFire(MaterialId a, MaterialId b)
        {
            return a == MaterialId.Water && b == MaterialId.Fire
                || a == MaterialId.Fire && b == MaterialId.Water;
        }

        private static bool IsSupported(MaterialId kind)
        {
            return kind == MaterialId.Water || kind == MaterialId.Fire;
        }
    }
}
