using Game.Materials;
using System;
using Game.Combat;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 纯 Water/Fire 固定 Tick 模拟器。Stage 顺序固定为：写入 → Reaction → Down Flow
    /// → Horizontal Flow → Fire Decay；每个 Stage 读取稳定 Current，并写入预分配 Next/Delta。
    /// </summary>
    public sealed class ElementFieldSimulator
    {
        private const int LateralDiffusionDivisor = 8;

        private static readonly Vector3Int[] HorizontalDirections =
        {
            Vector3Int.left,
            Vector3Int.right,
            Vector3Int.forward,
            Vector3Int.back,
        };

        private uint _tickIndex;

        public ElementFieldSimulationStats Tick(
            ElementGrid grid,
            ElementWriteQueue writes,
            Vector3 origin,
            float deltaTime,
            in ElementFieldSimulationSettings settings)
        {
            // 保持原子语义：参数非法时必须在消费 Queue 前失败，否则修正参数后重试会丢失 Projectile 写入。
            ValidateSimulationArguments(grid, deltaTime, in settings);
            ElementFieldSimulationStats stats = ApplyWrites(
                grid, writes, origin, settings.CellSize);
            SimulateStages(grid, deltaTime, in settings, ref stats);
            return stats;
        }

        /// <summary>
        /// 将离散命令应用到 Current Buffer。拆出这个阶段不是改变规则，而是让 Unity Runtime 能用独立
        /// ProfilerMarker 区分“外部写入成本”和“场内模拟成本”。普通纯测试仍可继续调用 Tick。
        /// </summary>
        internal ElementFieldSimulationStats ApplyWrites(
            ElementGrid grid,
            ElementWriteQueue writes,
            Vector3 origin,
            float cellSize)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (writes == null)
                throw new ArgumentNullException(nameof(writes));

            var stats = new ElementFieldSimulationStats
            {
                RejectedWrites = writes.TakeRejectedEnqueueCount(),
            };

            while (writes.TryDequeue(out ElementWriteRequest request))
            {
                if (ElementFieldWriteProcessor.TryApply(
                        grid, in request, origin, cellSize, out _))
                {
                    stats.ProcessedWrites++;
                }
                else
                {
                    stats.RejectedWrites++;
                }
            }

            return stats;
        }

        /// <summary>
        /// 推进 Reaction/Flow/Decay，并在末尾提交 Chunk Version。它只读取本次固定 Tick 的 deltaTime，
        /// 不读取 Time.deltaTime，因此仍可由测试或 Replay 使用明确的时间步驱动。
        /// </summary>
        internal void SimulateStages(
            ElementGrid grid,
            float deltaTime,
            in ElementFieldSimulationSettings settings,
            ref ElementFieldSimulationStats stats)
        {
            ValidateSimulationArguments(grid, deltaTime, in settings);

            RunReactionStage(grid, deltaTime, in settings.Extinguish, ref stats);
            RunDownFlowStage(grid, settings.MaxDownFlowPerTick, ref stats);
            RunHorizontalFlowStage(grid, settings.MaxLateralFlowPerTick, ref stats);
            RunFireDecayStage(grid, settings.FireDecayPerTick, ref stats);

            stats.ChangedCells = grid.CommitDirtyVersions();
            _tickIndex++;
        }

        private static void ValidateSimulationArguments(
            ElementGrid grid,
            float deltaTime,
            in ElementFieldSimulationSettings settings)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
                throw new ArgumentOutOfRangeException(nameof(deltaTime));
            if (settings.ChunkSize != grid.ChunkSize)
                throw new ArgumentException("Simulation settings ChunkSize must match the ElementGrid.", nameof(settings));
        }

        private static void RunReactionStage(
            ElementGrid grid,
            float deltaTime,
            in ExtinguishTuning tuning,
            ref ElementFieldSimulationStats stats)
        {
            grid.PrepareNextFromCurrent();
            grid.ClearAmountDelta();

            Vector3Int size = grid.Dimensions;
            for (int z = 0; z < size.z; z++)
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                int index = ElementFieldCoordinates.ToIndex(cell, size);
                if (x + 1 < size.x)
                    ReactPair(grid, index, ElementFieldCoordinates.ToIndex(new Vector3Int(x + 1, y, z), size), deltaTime, in tuning, ref stats);
                if (y + 1 < size.y)
                    ReactPair(grid, index, ElementFieldCoordinates.ToIndex(new Vector3Int(x, y + 1, z), size), deltaTime, in tuning, ref stats);
                if (z + 1 < size.z)
                    ReactPair(grid, index, ElementFieldCoordinates.ToIndex(new Vector3Int(x, y, z + 1), size), deltaTime, in tuning, ref stats);
            }

            ApplyReactionDeltas(grid);
            grid.CommitPreparedStage();
        }

        private static void ReactPair(
            ElementGrid grid,
            int firstIndex,
            int secondIndex,
            float deltaTime,
            in ExtinguishTuning tuning,
            ref ElementFieldSimulationStats stats)
        {
            ElementCell first = grid.CurrentCells[firstIndex];
            ElementCell second = grid.CurrentCells[secondIndex];
            bool opposed = first.MaterialKind == MaterialId.Water
                && second.MaterialKind == MaterialId.Fire
                || first.MaterialKind == MaterialId.Fire
                && second.MaterialKind == MaterialId.Water;
            if (!opposed)
                return;

            int firstAvailable = first.Amount + grid.AmountDelta[firstIndex];
            int secondAvailable = second.Amount + grid.AmountDelta[secondIndex];
            if (firstAvailable <= 0 || secondAvailable <= 0)
                return;

            int fireAmount = first.MaterialKind == MaterialId.Fire
                ? firstAvailable
                : secondAvailable;
            int waterAmount = first.MaterialKind == MaterialId.Water
                ? firstAvailable
                : secondAvailable;
            float fireIntensity = fireAmount * 100f / byte.MaxValue;
            float waterIntensity = waterAmount * 100f / byte.MaxValue;
            float threshold = Mathf.Max(0f, tuning.FormalThreshold);
            bool useFormalRate = fireIntensity >= threshold && waterIntensity >= threshold;

            ExtinguishResult consumption = ElementReactionEvaluator.CalculateExtinguish(
                fireIntensity,
                waterIntensity,
                deltaTime,
                useFormalRate,
                in tuning);
            int fireConsumed = Mathf.Min(
                Mathf.RoundToInt(consumption.FireRemoved * byte.MaxValue / 100f),
                fireAmount);
            int waterConsumed = Mathf.Min(
                Mathf.RoundToInt(consumption.WaterConsumed * byte.MaxValue / 100f),
                waterAmount);
            if (fireConsumed <= 0 || waterConsumed <= 0)
                return;

            // Pair 的遍历顺序不代表材料角色，因此分别按 MaterialKind 写回两侧消耗。
            grid.AmountDelta[firstIndex] -= first.MaterialKind == MaterialId.Fire
                ? fireConsumed
                : waterConsumed;
            grid.AmountDelta[secondIndex] -= second.MaterialKind == MaterialId.Fire
                ? fireConsumed
                : waterConsumed;
            stats.ReactionPairs++;
        }

        private static void ApplyReactionDeltas(ElementGrid grid)
        {
            for (int index = 0; index < grid.CellCount; index++)
            {
                int delta = grid.AmountDelta[index];
                if (delta == 0)
                    continue;

                ElementCell current = grid.CurrentCells[index];
                int amount = Mathf.Clamp(current.Amount + delta, 0, byte.MaxValue);
                grid.NextCells[index] = amount > 0
                    ? new ElementCell(current.MaterialKind, (byte)amount)
                    : default;
            }
        }

        private static void RunDownFlowStage(
            ElementGrid grid,
            byte maxTransfer,
            ref ElementFieldSimulationStats stats)
        {
            grid.PrepareNextFromCurrent();
            grid.ClearAmountDelta();
            if (maxTransfer == 0)
            {
                grid.CommitPreparedStage();
                return;
            }

            Vector3Int size = grid.Dimensions;
            for (int z = 0; z < size.z; z++)
            for (int y = 1; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                var source = new Vector3Int(x, y, z);
                int sourceIndex = ElementFieldCoordinates.ToIndex(source, size);
                if (grid.CurrentCells[sourceIndex].MaterialKind != MaterialId.Water)
                    continue;

                var target = new Vector3Int(x, y - 1, z);
                int targetIndex = ElementFieldCoordinates.ToIndex(target, size);
                if (grid.SolidMask[targetIndex] || !CanReceiveWater(grid.CurrentCells[targetIndex]))
                    continue;

                TransferWater(grid, sourceIndex, targetIndex, maxTransfer, balanceByDifference: false, ref stats);
            }

            ApplyWaterDeltas(grid);
            grid.CommitPreparedStage();
        }

        private void RunHorizontalFlowStage(
            ElementGrid grid,
            byte maxTransfer,
            ref ElementFieldSimulationStats stats)
        {
            grid.PrepareNextFromCurrent();
            grid.ClearAmountDelta();
            if (maxTransfer == 0)
            {
                grid.CommitPreparedStage();
                return;
            }

            Vector3Int size = grid.Dimensions;
            bool reverse = (_tickIndex & 1u) != 0u;
            for (int z = 0; z < size.z; z++)
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                var source = new Vector3Int(x, y, z);
                int sourceIndex = ElementFieldCoordinates.ToIndex(source, size);
                if (grid.CurrentCells[sourceIndex].MaterialKind != MaterialId.Water)
                    continue;

                for (int order = 0; order < HorizontalDirections.Length; order++)
                {
                    int directionIndex = reverse
                        ? HorizontalDirections.Length - 1 - order
                        : order;
                    Vector3Int target = source + HorizontalDirections[directionIndex];
                    if (!ElementFieldCoordinates.IsInside(target, size))
                        continue;

                    int targetIndex = ElementFieldCoordinates.ToIndex(target, size);
                    if (grid.SolidMask[targetIndex] || !CanReceiveWater(grid.CurrentCells[targetIndex]))
                        continue;

                    TransferWater(grid, sourceIndex, targetIndex, maxTransfer, balanceByDifference: true, ref stats);
                }
            }

            ApplyWaterDeltas(grid);
            grid.CommitPreparedStage();
        }

        private static void TransferWater(
            ElementGrid grid,
            int sourceIndex,
            int targetIndex,
            int maxTransfer,
            bool balanceByDifference,
            ref ElementFieldSimulationStats stats)
        {
            ElementCell source = grid.CurrentCells[sourceIndex];
            ElementCell target = grid.CurrentCells[targetIndex];
            int sourceAvailable = source.Amount + grid.AmountDelta[sourceIndex];
            int targetOriginal = target.MaterialKind == MaterialId.Water ? target.Amount : 0;
            int targetProjected = targetOriginal + grid.AmountDelta[targetIndex];
            int targetCapacity = byte.MaxValue - targetProjected;
            if (sourceAvailable <= 0 || targetCapacity <= 0)
                return;

            int desired = maxTransfer;
            if (balanceByDifference)
            {
                // 水平流动是二维四邻域显式扩散。旧公式 difference/2 会让一个 Cell 同时向
                // 四个方向过量输出，典型的 64→四个 0 会在单 Tick 被抽空，下一 Tick 又回流，
                // 最终让 Water Mesh 呈现奇偶格闪烁。稳定系数集中在纯数学内核中并由测试锁定。
                desired = CalculateStableLateralTransfer(
                    source.Amount,
                    targetOriginal,
                    maxTransfer);
            }

            int transfer = Mathf.Min(desired, Mathf.Min(sourceAvailable, targetCapacity));
            transfer = Mathf.Min(transfer, maxTransfer);
            if (transfer <= 0)
                return;

            grid.AmountDelta[sourceIndex] -= transfer;
            grid.AmountDelta[targetIndex] += transfer;
            stats.WaterTransfers++;
        }

        /// <summary>
        /// 计算一条水平邻接边上的 Water Transfer。
        /// 公式：transfer = min(maxTransfer, max(source-target, 0) / 8)。
        /// 二维四邻域显式扩散在 k=1/4 时仍可能保持棋盘高频振荡；这里取 k=1/8 增加阻尼，
        /// 同时保证单格在一个 Tick 中最多向四边流出约一半 Amount，不会反复变成 Empty。
        /// </summary>
        internal static int CalculateStableLateralTransfer(
            int sourceAmount,
            int targetAmount,
            int maxTransfer)
        {
            if (sourceAmount <= targetAmount || maxTransfer <= 0)
                return 0;

            int difference = sourceAmount - targetAmount;
            int desired = difference / LateralDiffusionDivisor;
            return Math.Min(desired, maxTransfer);
        }

        private static void ApplyWaterDeltas(ElementGrid grid)
        {
            for (int index = 0; index < grid.CellCount; index++)
            {
                int delta = grid.AmountDelta[index];
                if (delta == 0)
                    continue;

                ElementCell current = grid.CurrentCells[index];
                int original = current.MaterialKind == MaterialId.Water ? current.Amount : 0;
                int amount = Mathf.Clamp(original + delta, 0, byte.MaxValue);
                grid.NextCells[index] = amount > 0
                    ? new ElementCell(MaterialId.Water, (byte)amount)
                    : default;
            }
        }

        private static void RunFireDecayStage(
            ElementGrid grid,
            byte decay,
            ref ElementFieldSimulationStats stats)
        {
            grid.PrepareNextFromCurrent();
            if (decay > 0)
            {
                for (int index = 0; index < grid.CellCount; index++)
                {
                    ElementCell current = grid.CurrentCells[index];
                    if (current.MaterialKind != MaterialId.Fire || current.Amount == 0)
                        continue;

                    int amount = Mathf.Max(0, current.Amount - decay);
                    grid.NextCells[index] = amount > 0
                        ? new ElementCell(MaterialId.Fire, (byte)amount)
                        : default;
                    stats.FireCellsDecayed++;
                }
            }

            grid.CommitPreparedStage();
        }

        private static bool CanReceiveWater(ElementCell cell)
        {
            return cell.IsEmpty || cell.MaterialKind == MaterialId.Water;
        }
    }
}
