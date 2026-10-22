using System;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 将离散 Gameplay Water Amount 转换成更连续的 Presentation Density。
    /// 它只写 Workspace 的 Visual Cache，不写 World、Solver 或原始 CachedWaterAmounts，因而不会改变水量守恒与状态语义。
    /// </summary>
    internal static class WorldWaterVisualDensityFilter
    {
        public static void Build(
            WaterVolumeMeshingWorkspace workspace,
            in WaterVolumeMeshingSettings settings)
        {
            Array.Clear(
                workspace.CachedVisualWaterAmounts,
                0,
                workspace.CachedVisualWaterAmounts.Length);
            Array.Clear(
                workspace.CachedVisualSupportedMask,
                0,
                workspace.CachedVisualSupportedMask.Length);

            // 落地水只沿地面 X/Z 平滑，避免二维水膜被滤波抬升进空中。
            Seed(workspace, workspace.VisualDensityBufferA, supportedSeed: true);
            FilterAxis(
                workspace,
                workspace.VisualDensityBufferA,
                workspace.VisualDensityBufferB,
                1,
                0,
                0,
                settings.SupportedSmoothingStrength);
            FilterAxis(
                workspace,
                workspace.VisualDensityBufferB,
                workspace.VisualDensityBufferA,
                0,
                0,
                1,
                settings.SupportedSmoothingStrength);
            Commit(
                workspace,
                workspace.VisualDensityBufferA,
                settings.VisualDensityThreshold,
                supported: true);

            // 空中水团是体积现象，才额外沿 Y 轴扩散；同一对 Buffer 在两个 pass 之间复用。
            Seed(workspace, workspace.VisualDensityBufferA, supportedSeed: false);
            FilterAxis(
                workspace,
                workspace.VisualDensityBufferA,
                workspace.VisualDensityBufferB,
                1,
                0,
                0,
                settings.AirborneSmoothingStrength);
            FilterAxis(
                workspace,
                workspace.VisualDensityBufferB,
                workspace.VisualDensityBufferA,
                0,
                1,
                0,
                settings.AirborneSmoothingStrength);
            FilterAxis(
                workspace,
                workspace.VisualDensityBufferA,
                workspace.VisualDensityBufferB,
                0,
                0,
                1,
                settings.AirborneSmoothingStrength);
            Commit(
                workspace,
                workspace.VisualDensityBufferB,
                settings.VisualDensityThreshold,
                supported: false);

            workspace.UpdateCachedVisualWaterCellCount();
        }

        private static void Seed(
            WaterVolumeMeshingWorkspace workspace,
            float[] destination,
            bool supportedSeed)
        {
            Array.Clear(destination, 0, destination.Length);
            int dimension = workspace.CellCacheDimension;
            Vector3Int origin = workspace.CellCacheGlobalOrigin;
            for (int z = 0; z < dimension; z++)
            for (int y = 0; y < dimension; y++)
            for (int x = 0; x < dimension; x++)
            {
                int index = workspace.CellCacheIndex(x, y, z);
                var globalCell = new Vector3Int(
                    origin.x + x,
                    origin.y + y,
                    origin.z + z);
                bool isSupported = workspace.IsCachedSolid(globalCell + Vector3Int.down)
                    || workspace.TryGetCachedWater(globalCell + Vector3Int.down, out _);
                destination[index] = isSupported == supportedSeed
                    ? workspace.CachedWaterAmounts[index] / 255f
                    : 0f;
            }
        }

        private static void FilterAxis(
            WaterVolumeMeshingWorkspace workspace,
            float[] source,
            float[] destination,
            int dx,
            int dy,
            int dz,
            float strength)
        {
            int dimension = workspace.CellCacheDimension;
            for (int z = 0; z < dimension; z++)
            for (int y = 0; y < dimension; y++)
            for (int x = 0; x < dimension; x++)
            {
                destination[workspace.CellCacheIndex(x, y, z)] = FilterAxisSample(
                    workspace,
                    source,
                    x,
                    y,
                    z,
                    dx,
                    dy,
                    dz,
                    strength);
            }
        }

        private static float FilterAxisSample(
            WaterVolumeMeshingWorkspace workspace,
            float[] source,
            int x,
            int y,
            int z,
            int dx,
            int dy,
            int dz,
            float strength)
        {
            int centerIndex = workspace.CellCacheIndex(x, y, z);
            if (workspace.CachedSolidMask[centerIndex])
                return 0f;

            float center = source[centerIndex];
            float negative = ReadOpenOrReflect(
                workspace,
                source,
                x - dx,
                y - dy,
                z - dz,
                center);
            float positive = ReadOpenOrReflect(
                workspace,
                source,
                x + dx,
                y + dy,
                z + dz,
                center);
            float filtered = (negative + 2f * center + positive) * 0.25f;
            return Mathf.LerpUnclamped(center, filtered, strength);
        }

        private static float ReadOpenOrReflect(
            WaterVolumeMeshingWorkspace workspace,
            float[] source,
            int localX,
            int localY,
            int localZ,
            float center)
        {
            // Axis pass 的 x/y/z 是数组下标而非 Gameplay Global Cell；只能走 local helper。
            if (!workspace.TryGetLocalCellCacheIndex(
                    localX,
                    localY,
                    localZ,
                    out int index))
                return center;
            return workspace.CachedSolidMask[index] ? center : source[index];
        }

        private static void Commit(
            WaterVolumeMeshingWorkspace workspace,
            float[] densities,
            float threshold,
            bool supported)
        {
            int dimension = workspace.CellCacheDimension;
            Vector3Int origin = workspace.CellCacheGlobalOrigin;
            for (int z = 0; z < dimension; z++)
            for (int y = 0; y < dimension; y++)
            for (int x = 0; x < dimension; x++)
            {
                int index = workspace.CellCacheIndex(x, y, z);
                if (workspace.CachedSolidMask[index])
                    continue;

                float density = densities[index];
                var globalCell = new Vector3Int(
                    origin.x + x,
                    origin.y + y,
                    origin.z + z);
                // Mesh Ownership 与 Halo 可见性是两件事：raw-empty 邻 Chunk 不会创建 View，
                // 因此不能接收 Visual Cell，否则边界外层 Owned Edge 将无人输出而形成开口。
                // 只有目标 owner 自己的 target±1 raw 邻域含水才放行；远端 Water 不能越权。
                if (!workspace.CanCommitVisualWaterToOwner(globalCell))
                    continue;

                bool isVerticallySupported =
                    workspace.IsCachedVerticallySupported(globalCell);

                // Blur 可以把密度扩散到 Bridge，却不应把真实 Seed 的峰值削低，否则满格水柱会
                // 被拆成多个薄层。保峰只写 Visual Cache，且 raw support 分类必须属于当前 pass，
                // 避免 Supported Seed 在 Airborne pass（或反向）被错误保留。
                byte rawAmount = workspace.CachedWaterAmounts[index];
                if (rawAmount > 0 && isVerticallySupported == supported)
                {
                    density = Mathf.Max(
                        density,
                        rawAmount / (float)byte.MaxValue);
                }

                // 单个 Amount=1/2 是需要隐藏的碎点；但多个相邻痕量 Cell 代表真实连续水膜。
                // 对 Supported raw seed 汇总同层 3×3 Gameplay Amount：局部总量达到视觉阈值时，
                // 只把该 seed 提升到 threshold，不把总和全部复制给每格，避免视觉水量膨胀。
                if (supported
                    && rawAmount > 0
                    && isVerticallySupported
                    && density < threshold
                    && HasCoherentSupportedTrace(
                        workspace,
                        x,
                        y,
                        z,
                        threshold))
                {
                    density = threshold;
                }

                // Threshold 必须在保峰之后执行：Amount=1 即使恢复为 1/255，仍低于 3/255，
                // 所以孤立残量继续只存在于玩法层；只有上面的连续痕量水膜例外。
                if (density < threshold)
                    continue;
                if (supported && !isVerticallySupported)
                    continue;

                float existingDensity = workspace.CachedVisualWaterAmounts[index] / 255f;
                if (!supported && density <= existingDensity)
                    continue;

                workspace.CachedVisualWaterAmounts[index] = (byte)Mathf.Clamp(
                    Mathf.RoundToInt(density * 255f),
                    0,
                    byte.MaxValue);
                // 空中 pass 覆盖时仍从真实的下方 Snapshot 判断支撑，不能直接把标记清成 false。
                workspace.CachedVisualSupportedMask[index] = supported
                    || isVerticallySupported;
            }
        }

        private static bool HasCoherentSupportedTrace(
            WaterVolumeMeshingWorkspace workspace,
            int centerX,
            int centerY,
            int centerZ,
            float threshold)
        {
            int requiredAmount = Mathf.Max(
                1,
                Mathf.CeilToInt(threshold * byte.MaxValue));
            int accumulatedAmount = 0;
            Vector3Int origin = workspace.CellCacheGlobalOrigin;

            // Supported Water 只在地面 X/Z 平面聚合；不读取 Y 邻层，避免把空中水柱误认为水滩。
            // 固定 3×3 循环只访问 Workspace 连续数组，不查询 Sparse World，也不产生 GC Alloc。
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = centerX + dx;
                int z = centerZ + dz;
                if (!workspace.TryGetLocalCellCacheIndex(
                        x,
                        centerY,
                        z,
                        out int index))
                {
                    continue;
                }

                byte amount = workspace.CachedWaterAmounts[index];
                if (amount == 0)
                    continue;

                var globalCell = new Vector3Int(
                    origin.x + x,
                    origin.y + centerY,
                    origin.z + z);
                if (!workspace.IsCachedVerticallySupported(globalCell))
                    continue;

                accumulatedAmount += amount;
                if (accumulatedAmount >= requiredAmount)
                    return true;
            }

            return false;
        }
    }
}
