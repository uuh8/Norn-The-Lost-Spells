using Game.Materials;
using System;
using Unity.Collections;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// GPU 与 CPU 之间的“粒子 Gameplay 样本”编码契约。
    /// 单个 <see cref="Vector4"/> 同时携带 Position.xyz 与状态 tag，保证一次 Readback 原子取得位置、
    /// active 状态和材料身份；CPU 不需要再分别回读多个 Buffer 并承担跨帧配对错误。
    /// tag = 0 表示该 GPU slot 未被活跃粒子占用；其余整数为 <c>materialId + 1</c>。
    /// 这个 +1 偏移把合法的 Water/Empty 枚举值和 inactive 哨兵值分开，避免“空材料”被误判成活跃水粒子。
    /// </summary>
    internal static class FluidGameplaySampleCodec
    {
        // 这里必须复用 GPU Layout 的字节步长；C# 与 Compute Shader 对同一 Buffer 元素布局不一致时，
        // Readback 得到的 x/y/z/w 会整体错位，后面的格子投影即使算法正确也没有意义。
        internal const int Stride = FluidGpuLayout.Float4Stride;

        /// <summary>
        /// 将“该 slot 是否活跃”和材料身份压缩到 <c>float4.w</c>。
        /// GPU Buffer 使用 float4，tag 虽以 float 存放，语义上始终是可精确表示的小整数。
        /// </summary>
        internal static float EncodeTag(bool active, MaterialId materialKind)
        {
            return active ? (byte)materialKind + 1f : 0f;
        }

        /// <summary>
        /// 尝试从 GPU 回读的 tag 恢复活跃材料。
        /// 对非整数、0、NaN 或 Infinity 一律失败，而不让坏数据进入 Gameplay：一个无效位置或身份
        /// 若继续参与格子聚合，可能把角色的 Wet/中毒等状态投影到完全错误的世界位置。
        /// </summary>
        internal static bool TryDecodeMaterial(float tag, out MaterialId materialKind)
        {
            int encoded = Mathf.RoundToInt(tag);
            if (encoded <= 0 || Mathf.Abs(tag - encoded) > 0.001f)
            {
                materialKind = MaterialId.Empty;
                return false;
            }

            materialKind = (MaterialId)(encoded - 1);
            return true;
        }
    }

    /// <summary>
    /// 一次 AsyncGPUReadback 请求在“发起瞬间”冻结的解释上下文。
    /// GPU 回读在未来某帧才完成，期间 Interest Bounds、世界 Origin、粒子容量或 GPU Layout 都可能改变。
    /// 因而完成回调拿到的 samples 只能由这一份 Metadata 解释；绝不能使用完成帧的新 Bounds，
    /// 否则移动 Interest Bounds 时会把旧粒子映射到错误的 Global Cell。
    /// 这是数据一致性快照，保存的是解释 samples 所需的值，不是可被 Gameplay 修改的 Runtime 状态。
    /// </summary>
    internal readonly struct FluidGameplayReadbackMetadata
    {
        // 本次回读覆盖的半开世界区域；只有区域内粒子才可进入此 Gameplay 投影。
        internal readonly Bounds Bounds;
        // ElementWorld 的固定坐标参考点，与 CellSize 一起决定 World -> Global Cell 的离散化规则。
        internal readonly Vector3 Origin;
        // 一个 Global Cell 在世界空间中的边长；必须为有限正数，否则 floor 网格映射无定义。
        internal readonly float CellSize;
        // 发起请求时 GPU Particle Pool 的容量，用来拒绝布局已变而长度不再可信的 samples。
        internal readonly int ParticleCapacity;
        // “一个粒子等于多少 Gameplay Amount Unit”的材料配置快照，不能临时回查可编辑 Authoring 数据。
        internal readonly LiquidMaterialAmountScaleSnapshot AmountScales;
        // GPU Buffer 的字段布局版本；供 Bridge/调试确认 CPU 仍按同一契约解读数据。
        internal readonly uint LayoutVersion;
        // 粒子池/Interest 区域等空间拓扑的版本，用于识别不同空间布局产出的快照。
        internal readonly uint TopologyVersion;
        // 对 Gameplay 消费者公开的单调快照标识，用来判断数据是否已经更新。
        internal readonly uint SnapshotVersion;

        internal FluidGameplayReadbackMetadata(
            Bounds bounds,
            Vector3 origin,
            float cellSize,
            int particleCapacity,
            LiquidMaterialAmountScaleSnapshot amountScales,
            uint layoutVersion,
            uint topologyVersion,
            uint snapshotVersion)
        {
            Bounds = bounds;
            Origin = origin;
            CellSize = cellSize;
            ParticleCapacity = particleCapacity;
            AmountScales = amountScales;
            LayoutVersion = layoutVersion;
            TopologyVersion = topologyVersion;
            SnapshotVersion = snapshotVersion;
        }
    }

    /// <summary>
    /// 在写入 staging Snapshot 前验证“解释规则”本身。
    /// 验证失败时上层会保留已发布的旧 Snapshot；这比清空数据更安全，因为消费者仍能读到一份
    /// 坐标和版本自洽的历史投影，而不会看到半构建或由 NaN 推导出的错误状态。
    /// </summary>
    internal static class FluidGameplayMetadataValidator
    {
        internal static bool IsValid(in FluidGameplayReadbackMetadata metadata)
        {
            // Bounds 的 min/max 由 center/size 推导；四者都检查能覆盖损坏序列化、错误计算及 NaN 传播。
            Vector3 center = metadata.Bounds.center;
            Vector3 size = metadata.Bounds.size;
            Vector3 minimum = metadata.Bounds.min;
            Vector3 maximum = metadata.Bounds.max;
            return metadata.ParticleCapacity > 0
                && metadata.AmountScales != null
                && IsPositiveFinite(metadata.CellSize)
                && IsFinite(metadata.Origin)
                && IsFinite(center)
                && IsFinite(size)
                && IsFinite(minimum)
                && IsFinite(maximum)
                && size.x > 0f
                && size.y > 0f
                && size.z > 0f;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsPositiveFinite(float value)
        {
            return value > 0f && IsFinite(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>
    /// GPU 粒子数据在 CPU Gameplay 中的低频、只读占据快照（Gameplay occupancy snapshot）。
    ///
    /// 这个类不保存 GPU PBF Solver 的逐粒子真相（Simulation Truth），而是把每个活跃粒子投影到
    /// <c>Global Cell + MaterialId</c> 的离散 Amount。角色 Exposure、材料反应等 Gameplay 系统只需
    /// 稳定回答“这个格子里有多少某种材料”，不应依赖 Renderer 的 Mesh/Alpha，也不应反向修改 GPU 粒子。
    ///
    /// 内部采用预分配的 Open Addressing Table（开放寻址哈希表）：数组槽位直接保存 key/value，冲突时
    /// 向后线性探测，而不使用 Dictionary/LINQ。这样重建时没有托管分配，按 GPU slot 0..capacity-1 的固定
    /// 顺序 Binning，且同一组输入会得到可重复的饱和累加结果。它由 Bridge 作为 staging 对象构建完成后
    /// 整体交换为 published 对象，读取方永远不会观察到正在清表或正在累加的中间状态。
    /// </summary>
    internal sealed class LiquidGameplaySnapshot : ILiquidOccupancyReadOnly
    {
        // 一个逻辑 key 由“固定世界格子坐标 + 材料身份”组成；同一格子的 Water 与 Poison 必须独立累加。
        private readonly Vector3Int[] _cellKeys;
        private readonly MaterialId[] _materialKeys;
        // value 使用 byte 表示压缩后的 Amount；到达 255 后饱和，防止 uint 累加再截断导致回绕为小值。
        private readonly byte[] _amounts;
        // 不另设对象或 Nullable 标记；0/1 占用数组让查找、清表和热路径读取保持紧凑、无 GC。
        private readonly byte[] _occupied;
        // 槽位数始终为 2 的幂，Hash & mask 等价于取模，却避免昂贵的除法。
        private readonly int _slotMask;
        // 本 Snapshot 覆盖范围对应的 Global Cell 闭区间，用于查询前快速拒绝区域外请求。
        private Vector3Int _minimumCell;
        private Vector3Int _maximumCell;

        /// <summary>
        /// 在初始化期一次性分配表容量。表至少为预计最大 Occupied Cell 数的两倍，并向上取 2 的幂，
        /// 以降低线性探测冲突概率，同时支持 mask 取模。运行中的 Readback 重建只复用这些数组。
        /// </summary>
        internal LiquidGameplaySnapshot(int maximumCellCount)
        {
            if (maximumCellCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumCellCount));

            int slotCount = 2;
            while (slotCount < maximumCellCount * 2)
                slotCount <<= 1;

            _cellKeys = new Vector3Int[slotCount];
            _materialKeys = new MaterialId[slotCount];
            _amounts = new byte[slotCount];
            _occupied = new byte[slotCount];
            _slotMask = slotCount - 1;
        }

        /// <summary>仅当样本、坐标解释和版本均已完整写入后才为 true；它是读方的首个安全门。</summary>
        public bool HasValidSnapshot { get; private set; }
        /// <summary>本快照可回答查询的世界空间 Bounds，与 samples 使用同一份冻结 Metadata。</summary>
        public Bounds SnapshotBounds { get; private set; }
        /// <summary>构建本快照时使用的 GPU 数据布局版本，主要供内部一致性诊断。</summary>
        internal uint LayoutVersion { get; private set; }
        /// <summary>构建本快照时的空间拓扑版本，避免用一个拓扑解释另一个拓扑的粒子样本。</summary>
        internal uint TopologyVersion { get; private set; }
        /// <summary>已发布数据的版本号；消费者可据此跳过未变化的二次处理。</summary>
        public uint SnapshotVersion { get; private set; }
        // Amount 的单位属于本次 Metadata 的一部分，因此只在完整构建成功时与表数据一起切换。
        private LiquidMaterialAmountScaleSnapshot _amountScales;

        /// <summary>
        /// 将已完成 AsyncGPUReadback 的粒子样本重建为 staging 占据表。
        /// 成功前不会修改公开的 Bounds、版本与有效标记；调用方只有在成功后才交换 staging/published 引用，
        /// 所以失败的 Readback 不会破坏上一份仍可用的 Gameplay 数据。
        /// </summary>
        internal bool TryRebuild(
            NativeArray<Vector4> samples,
            in FluidGameplayReadbackMetadata metadata)
        {
            if (!samples.IsCreated
                || samples.Length > metadata.ParticleCapacity
                || !FluidGameplayMetadataValidator.IsValid(in metadata))
            {
                return false;
            }

            // 每次重建先清除旧表。key 数组无需清空：occupied=0 已令这些旧 key 对查找完全不可见。
            Array.Clear(_occupied, 0, _occupied.Length);
            Array.Clear(_amounts, 0, _amounts.Length);

            Vector3 boundsMin = metadata.Bounds.min;
            Vector3 boundsMax = metadata.Bounds.max;
            // 以 GPU slot 的自然顺序扫描。顺序固定不仅便于复现问题，也让 byte 饱和时的最终结果确定。
            for (int particleSlot = 0; particleSlot < samples.Length; particleSlot++)
            {
                Vector4 sample = samples[particleSlot];
                if (!FluidGameplaySampleCodec.TryDecodeMaterial(sample.w, out MaterialId material)
                    || !metadata.AmountScales.TryGet(material, out uint amountUnitsPerParticle)
                    || !IsFinite(sample.x)
                    || !IsFinite(sample.y)
                    || !IsFinite(sample.z))
                {
                    continue;
                }

                // Bounds 使用 half-open [min,max)：落在 max 面上的粒子属于相邻区域，不能双重计数。
                if (sample.x < boundsMin.x || sample.y < boundsMin.y || sample.z < boundsMin.z
                    || sample.x >= boundsMax.x || sample.y >= boundsMax.y || sample.z >= boundsMax.z)
                {
                    continue;
                }

                // 连续世界坐标被 floor 到固定的 Global Cell；之后 Gameplay 再也不关心该格中有几颗粒子。
                Vector3Int cell = ElementWorldCoordinates.WorldToGlobalCell(
                    new Vector3(sample.x, sample.y, sample.z),
                    metadata.Origin,
                    metadata.CellSize);
                Accumulate(cell, material, amountUnitsPerParticle);
            }

            // 查询边界需要与上面的 half-open [min,max) 规则一致。max 减一个很小的 inset 后再 floor，
            // 可把“最大可包含格”变成闭区间端点，避免 max 面外的格子被错误视作 Snapshot 内部。
            float inset = Mathf.Min(0.0001f, metadata.CellSize * 0.001f);
            _minimumCell = ElementWorldCoordinates.WorldToGlobalCell(
                boundsMin, metadata.Origin, metadata.CellSize);
            _maximumCell = ElementWorldCoordinates.WorldToGlobalCell(
                boundsMax - Vector3.one * inset, metadata.Origin, metadata.CellSize);

            // data、Bounds、Amount Scale 与版本最后一起切为 valid；Bridge 只交换完整构建的 Snapshot 引用。
            SnapshotBounds = metadata.Bounds;
            LayoutVersion = metadata.LayoutVersion;
            TopologyVersion = metadata.TopologyVersion;
            SnapshotVersion = metadata.SnapshotVersion;
            _amountScales = metadata.AmountScales;
            HasValidSnapshot = true;
            return true;
        }

        /// <summary>
        /// 查询指定 Global Cell 内某一种材料的离散 Amount。
        /// 返回 false 表示该位置或材料不在这份快照的查询契约内；返回 true 且 amount=0 则表示
        /// 查询本身有效，只是该格目前没有该材料。这个区分使 Exposure 能把“区域外/数据未就绪”
        /// 与“区域内确实干燥”分开处理。
        /// </summary>
        public bool TryGetAmount(
            Vector3Int globalCell,
            MaterialId materialKind,
            out byte amount)
        {
            if (!HasValidSnapshot
                || _amountScales == null
                || !_amountScales.TryGet(materialKind, out _)
                || globalCell.x < _minimumCell.x || globalCell.x > _maximumCell.x
                || globalCell.y < _minimumCell.y || globalCell.y > _maximumCell.y
                || globalCell.z < _minimumCell.z || globalCell.z > _maximumCell.z)
            {
                amount = 0;
                return false;
            }

            // 对有效范围内的未占用 key，FindSlot 会停在其应插入的位置，因此可直接以 occupied 判零。
            int slot = FindSlot(globalCell, materialKind);
            amount = _occupied[slot] != 0 ? _amounts[slot] : (byte)0;
            return true;
        }

        /// <summary>
        /// 返回本快照中“一颗该材料粒子代表多少 Gameplay Amount Unit”。
        /// 该换算只读且随快照发布，保证消费者不会用新配置去解释旧回读数据。
        /// </summary>
        public bool TryGetAmountUnitsPerParticle(MaterialId material, out uint amountUnits)
        {
            amountUnits = 0u;
            return HasValidSnapshot
                && _amountScales != null
                && _amountScales.TryGet(material, out amountUnits);
        }

        /// <summary>
        /// 将指定材料的非空格子复制到调用方提供的数组，并返回实际写入数。
        /// 选择“调用方提供 destination”而非返回 List，是为了让 Rendering 或批量 Gameplay 查询复用缓冲区；
        /// 返回数量超过数组容量时只复制前面的确定性槽位序列，调用方可在下一帧/更大缓冲区继续处理。
        /// </summary>
        public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (!HasValidSnapshot || _amountScales == null || !_amountScales.TryGet(material, out _))
                return 0;

            int count = 0;
            // 直接扫描固定 Open Addressing Table，不创建 List/Enumerator，低频 Readback 路径保持 zero-GC。
            for (int slot = 0; slot < _occupied.Length && count < destination.Length; slot++)
            {
                if (_occupied[slot] == 0 || _materialKeys[slot] != material || _amounts[slot] == 0)
                    continue;
                destination[count++] = new LiquidMaterialCellSample(_cellKeys[slot], _amounts[slot]);
            }
            return count;
        }

        /// <summary>
        /// 把一颗活跃粒子的 Amount 加入其 <c>cell + material</c> 桶。
        /// Amount 使用饱和加法：Gameplay 的 byte 表达上限意味着“足够多”，比溢出回绕成低水量更安全。
        /// </summary>
        private void Accumulate(
            Vector3Int cell,
            MaterialId material,
            uint amountUnitsPerParticle)
        {
            int slot = FindSlot(cell, material);
            if (_occupied[slot] == 0)
            {
                _occupied[slot] = 1;
                _cellKeys[slot] = cell;
                _materialKeys[slot] = material;
            }

            uint accumulated = (uint)_amounts[slot] + amountUnitsPerParticle;
            _amounts[slot] = accumulated >= byte.MaxValue
                ? byte.MaxValue
                : (byte)accumulated;
        }

        /// <summary>
        /// 找到现有 key 或其第一个可插入槽位。Open Addressing 的关键不变式是：每个 key 从自己的
        /// 初始 hash 槽开始连续探测，直到空槽；因此查询与插入必须使用完全相同的探测规则。
        /// </summary>
        private int FindSlot(Vector3Int cell, MaterialId material)
        {
            int slot = Hash(cell, material) & _slotMask;
            while (_occupied[slot] != 0
                && (_cellKeys[slot] != cell || _materialKeys[slot] != material))
                slot = (slot + 1) & _slotMask;
            return slot;
        }

        /// <summary>
        /// 将三维整数格坐标和材料身份混合为一个 int。不同质数降低规则网格坐标的相关性；
        /// <c>unchecked</c> 明确允许 int 溢出按二进制回绕，它是哈希混合的一部分，不是 Amount 计算。
        /// </summary>
        private static int Hash(Vector3Int cell, MaterialId material)
        {
            unchecked
            {
                int hash = cell.x * 73856093;
                hash ^= cell.y * 19349663;
                hash ^= cell.z * 83492791;
                hash ^= (byte)material * -1640531527;
                return hash;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
