using Game.Materials;
using System;
using System.Runtime.CompilerServices;
using Game.ElementField;
using UnityEngine;

// Tests 需要验证 Rendering 内部的纯缓存契约；friend assembly 不会把它们暴露成 Runtime public API。
[assembly: InternalsVisibleTo("Game.Rendering.Tests")]

namespace Game.Rendering
{
    /// <summary>
    /// 单个 Water Chunk View 长期复用的 Surface Nets 工作内存。
    ///
    /// Scalar 需要比 Chunk 多两层负向/一层正向 Halo，才能让跨 Chunk Dual Cell 读取同一组
    /// SDF Sample 并生成一致的 Surface Nets 顶点；Dual Vertex Index 使用更小的实际访问范围。
    /// 法线现在直接来自 sign-changing Edge 的 inside→outside 方向，不再保存 Gradient Buffer。
    /// 所有数组只在 View 创建时分配，Dirty Rebuild 只覆盖内容。
    /// </summary>
    public sealed class WaterVolumeMeshingWorkspace
    {
        private readonly bool[] _visualWaterCommitEligibility;

        public WaterVolumeMeshingWorkspace(int chunkSize, int samplesPerCell,
            bool allowCrossChunkVisualSpread = false)
        {
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            if (samplesPerCell <= 0)
                throw new ArgumentOutOfRangeException(nameof(samplesPerCell));

            ChunkSize = chunkSize;
            AllowCrossChunkVisualSpread = allowCrossChunkVisualSpread;
            SamplesPerCell = samplesPerCell;
            SampleResolution = checked(chunkSize * samplesPerCell);
            ScalarDimension = checked(SampleResolution + 4);
            DualDimension = checked(SampleResolution + 1);

            int minimumSampleCell = Mathf.FloorToInt(-2f / samplesPerCell);
            int maximumSampleCell = Mathf.FloorToInt(
                (SampleResolution + 1f) / samplesPerCell);
            // SDF 会读取 containingCell±1 的 Primitive；空中 Primitive 又查询六邻居，
            // 因此在 Scalar Sample 所落 Cell 范围外再保留两层 Cell Halo。
            CellCacheMinimumLocalCoordinate = minimumSampleCell - 2;
            int cellCacheMaximumLocalCoordinate = maximumSampleCell + 2;
            CellCacheDimension = checked(
                cellCacheMaximumLocalCoordinate
                - CellCacheMinimumLocalCoordinate
                + 1);

            Scalars = new float[CheckedCube(ScalarDimension)];
            CachedScalarSolidMask = new bool[CheckedCube(ScalarDimension)];
            DualVertexIndices = new int[CheckedCube(DualDimension)];
            CachedWaterAmounts = new byte[CheckedCube(CellCacheDimension)];
            CachedSolidMask = new bool[CheckedCube(CellCacheDimension)];
            // Visual Cache 与 Gameplay Snapshot 分开保存：滤波只能改变画面，绝不能篡改求解器真相。
            VisualDensityBufferA = new float[CheckedCube(CellCacheDimension)];
            VisualDensityBufferB = new float[CheckedCube(CellCacheDimension)];
            CachedVisualWaterAmounts = new byte[CheckedCube(CellCacheDimension)];
            CachedVisualSupportedMask = new bool[CheckedCube(CellCacheDimension)];
            CachedWaterPrimitives =
                new WorldWaterPrimitive[CheckedCube(CellCacheDimension)];
            // Eligibility 与每个目标 Cell 一一对应，不能只记录“整个 owner Chunk 是否有水”；
            // 否则同 Chunk 远端 Water 会错误授权边界 Bridge。数组只在构造期分配。
            _visualWaterCommitEligibility =
                new bool[CheckedCube(CellCacheDimension)];
            ClearDualVertexIndices();
        }

        public int ChunkSize { get; }
        internal bool AllowCrossChunkVisualSpread { get; }
        public int SamplesPerCell { get; }
        public int SampleResolution { get; }

        internal int ScalarDimension { get; }
        internal int DualDimension { get; }
        internal int CellCacheMinimumLocalCoordinate { get; }
        internal int CellCacheDimension { get; }
        internal float[] Scalars { get; }
        internal bool[] CachedScalarSolidMask { get; }
        internal int[] DualVertexIndices { get; }
        internal byte[] CachedWaterAmounts { get; }
        internal bool[] CachedSolidMask { get; }
        internal float[] VisualDensityBufferA { get; }
        internal float[] VisualDensityBufferB { get; }
        internal byte[] CachedVisualWaterAmounts { get; }
        internal bool[] CachedVisualSupportedMask { get; }
        internal WorldWaterPrimitive[] CachedWaterPrimitives { get; }
        internal bool HasWorldCellSnapshot { get; private set; }
        internal bool HasVisualWaterCache { get; private set; }
        public int CachedPrimitiveCount { get; private set; }
        public int CachedVisualWaterCellCount { get; private set; }
        internal bool HasActiveSampleBounds { get; private set; }
        internal int ActiveSampleMinimumX { get; private set; }
        internal int ActiveSampleMinimumY { get; private set; }
        internal int ActiveSampleMinimumZ { get; private set; }
        internal int ActiveSampleMaximumX { get; private set; }
        internal int ActiveSampleMaximumY { get; private set; }
        internal int ActiveSampleMaximumZ { get; private set; }

        internal Vector3Int CellCacheGlobalOrigin { get; private set; }

        /// <summary>
        /// -1 表示该 Dual Cell 没有穿过等值面。Array.Fill 只写已有数组，不产生托管分配。
        /// </summary>
        public void ClearDualVertexIndices()
        {
            Array.Fill(DualVertexIndices, -1);
        }

        /// <summary>
        /// 记录本次至少被一个 Water Primitive 采样过的 Lattice AABB。
        /// Surface Nets 只需扫描这个范围附近；Chunk 其余位置保持已知的正距离，
        /// 不必为了证明“没有等值面”继续遍历全部 Dual Cell。
        /// </summary>
        internal void SetActiveSampleBounds(
            int minimumX,
            int minimumY,
            int minimumZ,
            int maximumX,
            int maximumY,
            int maximumZ)
        {
            HasActiveSampleBounds = true;
            ActiveSampleMinimumX = minimumX;
            ActiveSampleMinimumY = minimumY;
            ActiveSampleMinimumZ = minimumZ;
            ActiveSampleMaximumX = maximumX;
            ActiveSampleMaximumY = maximumY;
            ActiveSampleMaximumZ = maximumZ;
        }

        internal void ClearActiveSampleBounds()
        {
            HasActiveSampleBounds = false;
        }

        /// <summary>
        /// 把稀疏 World Dictionary 查询集中到每次 Dirty Rebuild 的开头。
        /// 后续数千个 SDF Sample 只读取连续数组，避免重复执行 Global Cell→Chunk→Dictionary。
        /// </summary>
        internal void SnapshotWorldCells(
            IElementWorldReadOnly world,
            ElementChunkKey chunkKey,
            in WaterVolumeMeshingSettings settings)
        {
            // Snapshot 是一次 transaction：入口先撤销上一轮 World/Visual 有效性。
            // 即使 null、World 查询或后续映射抛异常，调用者也不能误读部分覆盖的 Cache。
            HasWorldCellSnapshot = false;
            HasVisualWaterCache = false;
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            int baseX = checked(chunkKey.X * ChunkSize);
            int baseY = checked(chunkKey.Y * ChunkSize);
            int baseZ = checked(chunkKey.Z * ChunkSize);
            CellCacheGlobalOrigin = new Vector3Int(
                baseX + CellCacheMinimumLocalCoordinate,
                baseY + CellCacheMinimumLocalCoordinate,
                baseZ + CellCacheMinimumLocalCoordinate);

            for (int z = 0; z < CellCacheDimension; z++)
            for (int y = 0; y < CellCacheDimension; y++)
            for (int x = 0; x < CellCacheDimension; x++)
            {
                var globalCell = new Vector3Int(
                    CellCacheGlobalOrigin.x + x,
                    CellCacheGlobalOrigin.y + y,
                    CellCacheGlobalOrigin.z + z);
                int index = CellCacheIndex(x, y, z);
                byte waterAmount =
                    world.TryGetCell(globalCell, out ElementCell cell)
                    && !cell.IsEmpty
                    && cell.MaterialKind == MaterialId.Water
                        ? cell.Amount
                        : (byte)0;
                CachedWaterAmounts[index] = waterAmount;
                CachedSolidMask[index] = world.IsSolid(globalCell);
            }

            PrecomputeVisualWaterCommitEligibility();

            // Snapshot 只保存 Gameplay 真相和 Solid Sample Mask。Visual Filter 尚未运行，
            // 所以这里不能提前按 Gameplay Water 构造 Primitive，否则 Runtime 仍会绕过 Presentation Bridge。
            CachedPrimitiveCount = 0;
            CachedVisualWaterCellCount = 0;

            // Solid 对 Sample Lattice 的映射在本次 Rebuild 内固定。预先缓存后，
            // Cell-centric 内循环不再为每个 Primitive×Sample 重复 FloorToInt 与 Cache 查询。
            for (int z = -2; z <= SampleResolution + 1; z++)
            for (int y = -2; y <= SampleResolution + 1; y++)
            for (int x = -2; x <= SampleResolution + 1; x++)
            {
                var containingCell = new Vector3Int(
                    baseX + FloorDivide(x, SamplesPerCell),
                    baseY + FloorDivide(y, SamplesPerCell),
                    baseZ + FloorDivide(z, SamplesPerCell));
                CachedScalarSolidMask[ScalarIndex(x, y, z)] =
                    IsCachedSolid(containingCell);
            }

            // 只有 raw、eligibility 与 Scalar Solid 映射全部完成，才能原子式提交 World 阶段。
            HasWorldCellSnapshot = true;
            // Snapshot 结束时只保证 Gameplay/Solid 数据有效；Visual Filter 尚未执行。
            HasVisualWaterCache = false;
        }

        internal bool TryGetCachedWater(Vector3Int globalCell, out byte amount)
        {
            int x = globalCell.x - CellCacheGlobalOrigin.x;
            int y = globalCell.y - CellCacheGlobalOrigin.y;
            int z = globalCell.z - CellCacheGlobalOrigin.z;
            if (!IsInsideCellCache(x, y, z))
            {
                amount = 0;
                return false;
            }

            amount = CachedWaterAmounts[CellCacheIndex(x, y, z)];
            return amount > 0;
        }

        /// <summary>
        /// 判断当前 Filter 是否能把 Visual Density 提交到目标 Cell。
        /// 结果来自 Snapshot 后预计算的 target-local raw 邻域，查询本身是 O(1) 连续数组读取。
        /// </summary>
        internal bool CanCommitVisualWaterToOwner(Vector3Int globalCell)
        {
            if (!TryGetGlobalCellCacheIndex(globalCell, out int index))
                return false;

            return _visualWaterCommitEligibility[index];
        }

        /// <summary>
        /// 读取仅供 Presentation 使用的滤波结果；原始 CachedWaterAmounts 仍是 Gameplay Snapshot。
        /// </summary>
        internal bool TryGetCachedVisualWater(Vector3Int globalCell, out byte amount)
        {
            if (!TryGetGlobalCellCacheIndex(globalCell, out int index))
            {
                amount = 0;
                return false;
            }

            amount = CachedVisualWaterAmounts[index];
            return amount > 0;
        }

        internal bool IsCachedVisualSupported(Vector3Int globalCell)
        {
            return TryGetGlobalCellCacheIndex(globalCell, out int index)
                && CachedVisualSupportedMask[index];
        }

        /// <summary>
        /// Supported 的判定始终读取未滤波的 Solid/Water Snapshot，避免视觉扩散反向制造 Gameplay 支撑关系。
        /// </summary>
        internal bool IsCachedVerticallySupported(Vector3Int globalCell)
        {
            Vector3Int below = globalCell + Vector3Int.down;
            return IsCachedSolid(below) || TryGetCachedWater(below, out _);
        }

        /// <summary>
        /// 将 World/Gameplay 使用的 Global Cell 映射到当前 Chunk Cache 的数组索引。
        /// Cache 有 Halo，故 Chunk (0,0,0) 的 Origin 也可能为负数；调用者不能把 local 数组坐标传进来。
        /// </summary>
        internal bool TryGetGlobalCellCacheIndex(
            Vector3Int globalCell,
            out int index)
        {
            int localX = globalCell.x - CellCacheGlobalOrigin.x;
            int localY = globalCell.y - CellCacheGlobalOrigin.y;
            int localZ = globalCell.z - CellCacheGlobalOrigin.z;
            return TryGetLocalCellCacheIndex(localX, localY, localZ, out index);
        }

        /// <summary>
        /// 只接受 [0, CellCacheDimension) 的 Cache-local 数组坐标。
        /// Filter 的卷积循环已经在该坐标空间中，使用此方法可避免重复减去 Global Origin。
        /// </summary>
        internal bool TryGetLocalCellCacheIndex(
            int localX,
            int localY,
            int localZ,
            out int index)
        {
            if (!IsInsideCellCache(localX, localY, localZ))
            {
                index = 0;
                return false;
            }

            index = CellCacheIndex(localX, localY, localZ);
            return true;
        }

        /// <summary>
        /// Filter 先把 Gameplay Snapshot 转成 Visual Density，再为每个 Visual Cell 烘焙一个 Primitive。
        /// 两段循环都只读复用数组；Dirty Rebuild 热路径不创建集合或托管对象。
        /// </summary>
        internal void BuildVisualWaterCache(in WaterVolumeMeshingSettings settings)
        {
            // Filter 与 Primitive 预计算必须作为一个阶段提交；任何中途失败都保持 false。
            HasVisualWaterCache = false;
            WorldWaterVisualDensityFilter.Build(this, in settings);

            CachedPrimitiveCount = 0;
            for (int z = 0; z < CellCacheDimension; z++)
            for (int y = 0; y < CellCacheDimension; y++)
            for (int x = 0; x < CellCacheDimension; x++)
            {
                int index = CellCacheIndex(x, y, z);
                byte amount = CachedVisualWaterAmounts[index];
                if (amount == 0)
                {
                    CachedWaterPrimitives[index] = default;
                    continue;
                }

                var globalCell = new Vector3Int(
                    CellCacheGlobalOrigin.x + x,
                    CellCacheGlobalOrigin.y + y,
                    CellCacheGlobalOrigin.z + z);
                CachedWaterPrimitives[index] =
                    WorldWaterImplicitField.CreateCachedPrimitive(
                        this,
                        globalCell,
                        amount,
                        CachedVisualSupportedMask[index],
                        in settings);
                CachedPrimitiveCount++;
            }

            HasVisualWaterCache = true;
        }

        internal void UpdateCachedVisualWaterCellCount()
        {
            int count = 0;
            for (int index = 0; index < CachedVisualWaterAmounts.Length; index++)
            {
                if (CachedVisualWaterAmounts[index] > 0)
                    count++;
            }

            CachedVisualWaterCellCount = count;
        }

        internal bool IsCachedSolid(Vector3Int globalCell)
        {
            int x = globalCell.x - CellCacheGlobalOrigin.x;
            int y = globalCell.y - CellCacheGlobalOrigin.y;
            int z = globalCell.z - CellCacheGlobalOrigin.z;
            return IsInsideCellCache(x, y, z)
                && CachedSolidMask[CellCacheIndex(x, y, z)];
        }

        internal bool TryGetCachedPrimitive(
            Vector3Int globalCell,
            out WorldWaterPrimitive primitive)
        {
            int x = globalCell.x - CellCacheGlobalOrigin.x;
            int y = globalCell.y - CellCacheGlobalOrigin.y;
            int z = globalCell.z - CellCacheGlobalOrigin.z;
            if (!IsInsideCellCache(x, y, z))
            {
                primitive = default;
                return false;
            }

            int index = CellCacheIndex(x, y, z);
            if (CachedVisualWaterAmounts[index] == 0)
            {
                primitive = default;
                return false;
            }

            primitive = CachedWaterPrimitives[index];
            return true;
        }

        internal int ScalarIndex(int logicalX, int logicalY, int logicalZ)
        {
            // Scalar logical range = [-2, N+1]，所以数组索引偏移 +2。
            int x = logicalX + 2;
            int y = logicalY + 2;
            int z = logicalZ + 2;
            return x + ScalarDimension * (y + ScalarDimension * z);
        }

        internal int DualIndex(int logicalX, int logicalY, int logicalZ)
        {
            // Dual logical range = [-1, N-1]，所以数组索引偏移 +1。
            int x = logicalX + 1;
            int y = logicalY + 1;
            int z = logicalZ + 1;
            return x + DualDimension * (y + DualDimension * z);
        }

        private bool IsInsideCellCache(int x, int y, int z)
        {
            return (uint)x < (uint)CellCacheDimension
                && (uint)y < (uint)CellCacheDimension
                && (uint)z < (uint)CellCacheDimension;
        }

        internal int CellCacheIndex(int x, int y, int z)
        {
            return x + CellCacheDimension * (y + CellCacheDimension * z);
        }

        private void PrecomputeVisualWaterCommitEligibility()
        {
            Array.Clear(
                _visualWaterCommitEligibility,
                0,
                _visualWaterCommitEligibility.Length);

            int dimension = CellCacheDimension;
            for (int z = 0; z < dimension; z++)
            for (int y = 0; y < dimension; y++)
            for (int x = 0; x < dimension; x++)
            {
                int targetGlobalX = CellCacheGlobalOrigin.x + x;
                int targetGlobalY = CellCacheGlobalOrigin.y + y;
                int targetGlobalZ = CellCacheGlobalOrigin.z + z;
                int targetOwnerX = FloorDivide(targetGlobalX, ChunkSize);
                int targetOwnerY = FloorDivide(targetGlobalY, ChunkSize);
                int targetOwnerZ = FloorDivide(targetGlobalZ, ChunkSize);
                bool eligible = false;

                // Separable Filter 的最大传播范围是 Chebyshev 1：三个 Axis Pass 合成后，
                // 只有 target±1 的 3×3×3 raw 邻域能影响目标。邻居还必须与目标属于同一
                // Gameplay owner，防止 x8 借用 owner0 的 x7，或被 owner1 的远端 x15 授权。
                for (int dz = -1; dz <= 1 && !eligible; dz++)
                for (int dy = -1; dy <= 1 && !eligible; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int neighborX = x + dx;
                    int neighborY = y + dy;
                    int neighborZ = z + dz;
                    if (!IsInsideCellCache(neighborX, neighborY, neighborZ))
                        continue;
                    if (CachedWaterAmounts[
                            CellCacheIndex(neighborX, neighborY, neighborZ)] == 0)
                    {
                        continue;
                    }

                    int neighborGlobalX = targetGlobalX + dx;
                    int neighborGlobalY = targetGlobalY + dy;
                    int neighborGlobalZ = targetGlobalZ + dz;
                    // Dormant Renderer 为边缘的空页也分配 View，允许同一滤波核跨页连续。
                    // 旧 Renderer 只创建含水页，仍保留原有 owner 限制。
                    if (!AllowCrossChunkVisualSpread
                        && (FloorDivide(neighborGlobalX, ChunkSize) != targetOwnerX
                        || FloorDivide(neighborGlobalY, ChunkSize) != targetOwnerY
                        || FloorDivide(neighborGlobalZ, ChunkSize) != targetOwnerZ))
                    {
                        continue;
                    }

                    eligible = true;
                    break;
                }

                _visualWaterCommitEligibility[CellCacheIndex(x, y, z)] =
                    eligible;
            }
        }

        private static int CheckedCube(int value)
        {
            return checked(value * value * value);
        }

        private static int FloorDivide(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;
            return remainder < 0 ? quotient - 1 : quotient;
        }
    }
}
