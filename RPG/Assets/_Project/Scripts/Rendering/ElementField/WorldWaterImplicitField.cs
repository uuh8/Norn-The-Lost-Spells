using Game.Materials;
using System;
using Game.ElementField;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 一个 Visual Water Cell 在本次 Rebuild 中对应的不可变 Rounded Box。
    /// Supported、Visual Amount cbrt 与六邻域连接都已在 Filter 后的预计算阶段烘焙完成。
    /// </summary>
    internal readonly struct WorldWaterPrimitive
    {
        public WorldWaterPrimitive(
            Vector3 center,
            Vector3 halfExtents,
            float roundRadius)
        {
            Center = center;
            HalfExtents = halfExtents;
            RoundRadius = roundRadius;
        }

        public Vector3 Center { get; }
        public Vector3 HalfExtents { get; }
        public float RoundRadius { get; }
    }

    /// <summary>
    /// 把离散 World Water Cell 转换为连续 Signed Distance。
    ///
    /// Signed Distance 小于 0 表示水体内部，大于 0 表示外部，等于 0 是后续 Surface Nets
    /// 要提取的表面。这里使用 Global Cell Space：整数边界对应 Cell 边界，因此同一个纯函数
    /// 能被相邻 Chunk 重复采样并得到完全相同的结果，避免 Seam。
    /// </summary>
    public static class WorldWaterImplicitField
    {
        internal const float EmptyDistance = 2f;

        public static float Sample(
            IElementWorldReadOnly world,
            Vector3 globalCellSpacePosition,
            in WaterVolumeMeshingSettings settings)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (!world.IsInitialized)
                return EmptyDistance;

            var source = new WorldWaterSampleSource(world);
            return SampleCore(in source, globalCellSpacePosition, in settings);
        }

        internal static float SampleCached(
            WaterVolumeMeshingWorkspace workspace,
            Vector3 globalCellSpacePosition,
            in WaterVolumeMeshingSettings settings)
        {
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (!workspace.HasWorldCellSnapshot)
            {
                throw new InvalidOperationException(
                    "Workspace must snapshot world cells before cached SDF sampling.");
            }
            if (!workspace.HasVisualWaterCache)
            {
                throw new InvalidOperationException(
                    "Workspace must complete the visual water cache after the latest "
                    + "world snapshot before cached SDF sampling.");
            }

            var source = new WorldWaterSampleSource(workspace);
            return SampleCore(in source, globalCellSpacePosition, in settings);
        }

        private static float SampleCore(
            in WorldWaterSampleSource source,
            Vector3 globalCellSpacePosition,
            in WaterVolumeMeshingSettings settings)
        {
            var containingCell = new Vector3Int(
                Mathf.FloorToInt(globalCellSpacePosition.x),
                Mathf.FloorToInt(globalCellSpacePosition.y),
                Mathf.FloorToInt(globalCellSpacePosition.z));
            // SolidMask 是 Gameplay 对占用空间的权威描述。即使异常 Snapshot 同时残留 Water，
            // Presentation 也必须把 Solid 内部裁掉，避免水面穿进地板或容器壁。
            if (source.IsSolid(containingCell))
                return EmptyDistance;

            float combinedDistance = EmptyDistance;
            bool hasPrimitive = false;

            // Rounded Primitive 的最大外延小于一个 Cell；固定 3³ 邻域足以覆盖所有贡献者。
            // 固定 for 循环不创建集合，也不会让 Sample 成本随 Resident World 无限增长。
            for (int z = containingCell.z - 1; z <= containingCell.z + 1; z++)
            for (int y = containingCell.y - 1; y <= containingCell.y + 1; y++)
            for (int x = containingCell.x - 1; x <= containingCell.x + 1; x++)
            {
                var waterCell = new Vector3Int(x, y, z);
                if (!source.TryGetPrimitive(
                        waterCell,
                        in settings,
                        out WorldWaterPrimitive primitive))
                    continue;

                float distance = RoundedBoxDistance(
                    globalCellSpacePosition,
                    primitive.Center,
                    primitive.HalfExtents,
                    primitive.RoundRadius);
                combinedDistance = hasPrimitive
                    ? SmoothMinimum(combinedDistance, distance, settings.SmoothUnionRadius)
                    : distance;
                hasPrimitive = true;
            }

            return hasPrimitive ? combinedDistance : EmptyDistance;
        }

        internal static WorldWaterPrimitive CreateCachedPrimitive(
            WaterVolumeMeshingWorkspace workspace,
            Vector3Int cell,
            byte amount,
            bool supported,
            in WaterVolumeMeshingSettings settings)
        {
            var source = new WorldWaterSampleSource(workspace);
            return CreatePrimitive(
                in source,
                cell,
                amount,
                supported,
                in settings);
        }

        internal static float SamplePrimitive(
            Vector3 point,
            in WorldWaterPrimitive primitive)
        {
            return RoundedBoxDistance(
                point,
                primitive.Center,
                primitive.HalfExtents,
                primitive.RoundRadius);
        }

        internal static float CombineDistances(
            float current,
            float next,
            float smoothUnionRadius)
        {
            return SmoothMinimum(current, next, smoothUnionRadius);
        }

        private static WorldWaterPrimitive CreatePrimitive(
            in WorldWaterSampleSource source,
            Vector3Int cell,
            byte amount,
            in WaterVolumeMeshingSettings settings)
        {
            return CreatePrimitive(
                in source,
                cell,
                amount,
                IsVerticallySupported(in source, cell),
                in settings);
        }

        private static WorldWaterPrimitive CreatePrimitive(
            in WorldWaterSampleSource source,
            Vector3Int cell,
            byte amount,
            bool supported,
            in WaterVolumeMeshingSettings settings)
        {
            return supported
                ? CreateSupportedPrimitive(cell, amount, in settings)
                : CreateAirbornePrimitive(in source, cell, amount, in settings);
        }

        private static WorldWaterPrimitive CreateSupportedPrimitive(
            Vector3Int cell,
            byte amount,
            in WaterVolumeMeshingSettings settings)
        {
            float height = Mathf.Max(
                amount / (float)byte.MaxValue,
                settings.MinimumSupportedHeight);
            // Floor Capture Bias 只把 Primitive Bottom 轻微压到支撑面下，使支撑面 Sample
            // 获得负 Signed Distance 并与上方正 Sample 形成 Crossing；Top 仍由真实 Visual Amount
            // 决定，因此低水量不会再被强制抬成半格厚。
            float bottom = cell.y - settings.SupportedFloorCaptureDepth;
            float top = cell.y + height;
            var center = new Vector3(
                cell.x + 0.5f,
                (bottom + top) * 0.5f,
                cell.z + 0.5f);
            var halfExtents = new Vector3(
                0.5f,
                (top - bottom) * 0.5f,
                0.5f);
            return new WorldWaterPrimitive(
                center,
                halfExtents,
                settings.SupportedCornerRadius);
        }

        private static WorldWaterPrimitive CreateAirbornePrimitive(
            in WorldWaterSampleSource source,
            Vector3Int cell,
            byte amount,
            in WaterVolumeMeshingSettings settings)
        {
            // 三维体积的线性尺度与体积的立方根成正比，而不是与 Amount 线性等比。
            // 若半径直接乘 Amount，半 Amount 的球体积会缩成 1/8；cbrt 能让视觉大小
            // 与 Cell 中的体积存量保持更直观的对应关系。
            float amount01 = amount / (float)byte.MaxValue;
            float volumeScale = Mathf.Pow(amount01, 1f / 3f);
            float radius = Mathf.Lerp(
                settings.MinimumAirborneRadius,
                settings.MaximumAirborneRadius,
                volumeScale);
            var cellCenter = new Vector3(
                cell.x + 0.5f,
                cell.y + 0.5f,
                cell.z + 0.5f);
            Vector3 minimum = cellCenter - Vector3.one * radius;
            Vector3 maximum = cellCenter + Vector3.one * radius;

            // 朝 Water 邻居的一侧延伸到公共 Cell Boundary，使相邻低 Amount Cell 仍构成
            // 一个连续水团；朝 Empty 的开放方向保留 Amount 半径，避免整个团块重新膨胀成方盒。
            if (source.TryGetWater(cell + Vector3Int.left, out _))
                minimum.x = cell.x;
            if (source.TryGetWater(cell + Vector3Int.right, out _))
                maximum.x = cell.x + 1f;
            if (source.TryGetWater(cell + Vector3Int.down, out _))
                minimum.y = cell.y;
            if (source.TryGetWater(cell + Vector3Int.up, out _))
                maximum.y = cell.y + 1f;
            if (source.TryGetWater(cell + new Vector3Int(0, 0, -1), out _))
                minimum.z = cell.z;
            if (source.TryGetWater(cell + new Vector3Int(0, 0, 1), out _))
                maximum.z = cell.z + 1f;

            Vector3 center = (minimum + maximum) * 0.5f;
            Vector3 halfExtents = (maximum - minimum) * 0.5f;

            return new WorldWaterPrimitive(
                center,
                halfExtents,
                radius);
        }

        private static float RoundedBoxDistance(
            Vector3 point,
            Vector3 center,
            Vector3 halfExtents,
            float roundRadius)
        {
            float effectiveRadius = Mathf.Min(
                roundRadius,
                Mathf.Min(halfExtents.x, Mathf.Min(halfExtents.y, halfExtents.z)));
            Vector3 innerHalfExtents = halfExtents - Vector3.one * effectiveRadius;
            Vector3 offset = point - center;
            var q = new Vector3(
                Mathf.Abs(offset.x) - innerHalfExtents.x,
                Mathf.Abs(offset.y) - innerHalfExtents.y,
                Mathf.Abs(offset.z) - innerHalfExtents.z);
            var outside = new Vector3(
                Mathf.Max(q.x, 0f),
                Mathf.Max(q.y, 0f),
                Mathf.Max(q.z, 0f));
            float inside = Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
            return outside.magnitude + inside - effectiveRadius;
        }

        private static float SmoothMinimum(float a, float b, float radius)
        {
            float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / radius);
            return Mathf.Lerp(b, a, h) - radius * h * (1f - h);
        }

        private static bool IsVerticallySupported(
            in WorldWaterSampleSource source,
            Vector3Int cell)
        {
            Vector3Int below = cell + Vector3Int.down;
            return source.IsSolid(below) || source.TryGetWater(below, out _);
        }

        private readonly struct WorldWaterSampleSource
        {
            private readonly IElementWorldReadOnly _world;
            private readonly WaterVolumeMeshingWorkspace _workspace;

            public WorldWaterSampleSource(IElementWorldReadOnly world)
            {
                _world = world;
                _workspace = null;
            }

            public WorldWaterSampleSource(WaterVolumeMeshingWorkspace workspace)
            {
                _world = null;
                _workspace = workspace;
            }

            public bool IsSolid(Vector3Int cell)
            {
                return _workspace != null
                    ? _workspace.IsCachedSolid(cell)
                    : _world.IsSolid(cell);
            }

            public bool TryGetWater(Vector3Int cell, out byte amount)
            {
                if (_workspace != null)
                    return _workspace.TryGetCachedVisualWater(cell, out amount);
                if (_world.TryGetCell(cell, out ElementCell value)
                    && !value.IsEmpty
                    && value.MaterialKind == MaterialId.Water)
                {
                    amount = value.Amount;
                    return true;
                }

                amount = 0;
                return false;
            }

            public bool TryGetPrimitive(
                Vector3Int cell,
                in WaterVolumeMeshingSettings settings,
                out WorldWaterPrimitive primitive)
            {
                if (_workspace != null)
                    return _workspace.TryGetCachedPrimitive(cell, out primitive);
                if (!TryGetWater(cell, out byte amount))
                {
                    primitive = default;
                    return false;
                }

                primitive = CreatePrimitive(in this, cell, amount, in settings);
                return true;
            }
        }
    }
}
