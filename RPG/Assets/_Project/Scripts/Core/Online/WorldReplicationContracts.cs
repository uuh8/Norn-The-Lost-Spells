using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>独立原生网格域；LegacyCell的byte量（尤其Fire强度）不参与三相GMU求和。</summary>
    public enum WorldMaterialDomain : byte { LegacyCell, Liquid, Solid, Gas }
    public enum WorldMaterialStorage : byte { Resident, Dynamic, Stable }
    public enum WorldCaptureFailure : byte
    {
        None, NotInitialized, UnsupportedBackend, SimulationInProgress, SimulationFaulted,
        InvalidGeometry, CellCapacity, PendingCapacity, InspectionBudget
    }

    /// <summary>
    /// Cell地址必须与所属网格一起解释。粗液体格不能替换成覆盖的多个World细格，
    /// 否则同一份库存被重复计量；ChunkSize用于后续按原生Chunk组织冻结页。
    /// </summary>
    public readonly struct WorldGridGeometry
    {
        public readonly WorldMaterialDomain Domain;
        public readonly byte GridId;
        public readonly Vector3 Origin;
        public readonly float CellSize;
        public readonly int ChunkSize;
        public WorldGridGeometry(WorldMaterialDomain domain, Vector3 origin, float cellSize, int chunkSize)
        { Domain = domain; GridId = (byte)domain; Origin = origin; CellSize = cellSize; ChunkSize = chunkSize; }
    }

    /// <summary>
    /// 当前已落地占据的值副本。Quantity在Liquid/Solid/Gas是GMU；LegacyCell保留原byte数值，
    /// Fire表示强度。Stable与Dynamic可分别拥有量，inactive退役Stable不得再导出第二份。
    /// </summary>
    public readonly struct WorldMaterialCellSample
    {
        public readonly WorldMaterialDomain Domain;
        public readonly byte GridId;
        public readonly WorldMaterialStorage Storage;
        public readonly Vector3Int Cell;
        public readonly byte MaterialId;
        public readonly uint Quantity;
        public WorldMaterialCellSample(WorldMaterialDomain domain, WorldMaterialStorage storage,
            Vector3Int cell, byte materialId, uint quantity)
        { Domain = domain; GridId = (byte)domain; Storage = storage; Cell = cell; MaterialId = materialId; Quantity = quantity; }
    }

    /// <summary>
    /// 尚未落地的独立数量owner。复制原始请求及FIFO顺序/扫描进度，不能把Remaining假装成某格已驻留量。
    /// Core使用稳定数字MaterialId，不依赖ElementField的请求类型或Materials程序集。
    /// </summary>
    public readonly struct WorldPendingDepositSample
    {
        public readonly WorldMaterialDomain Domain;
        public readonly int QueueIndex;
        public readonly byte MaterialId;
        public readonly Vector3 WorldPosition, InitialVelocity, SurfaceNormal;
        public readonly ushort TotalAmount;
        public readonly float Radius;
        public readonly bool UseLinearFalloff;
        public readonly uint Remaining;
        public readonly int Cursor;
        public readonly uint WaitingTicks;
        public WorldPendingDepositSample(WorldMaterialDomain domain, int queueIndex, byte materialId,
            Vector3 worldPosition, ushort totalAmount, float radius, bool useLinearFalloff,
            Vector3 initialVelocity, Vector3 surfaceNormal, uint remaining, int cursor, uint waitingTicks)
        {
            Domain = domain; QueueIndex = queueIndex; MaterialId = materialId; WorldPosition = worldPosition;
            TotalAmount = totalAmount; Radius = radius; UseLinearFalloff = useLinearFalloff;
            InitialVelocity = initialVelocity; SurfaceNormal = surfaceNormal; Remaining = remaining;
            Cursor = cursor; WaitingTicks = waitingTicks;
        }
    }

    /// <summary>
    /// 当前提交边界的空间数量快照元数据，不冒充网络Revision/ACK。
    /// 没有包含Solver速度、Outlet fractional、规则进度等，不能用于恢复Authority规则执行。
    /// </summary>
    public readonly struct WorldReplicaHeader
    {
        public readonly long WorldTick;
        public readonly int CellCount, PendingCount, GeometryCount;
        public bool CanRestoreAuthorityRules => false;
        public WorldReplicaHeader(long worldTick, int cellCount, int pendingCount, int geometryCount)
        { WorldTick = worldTick; CellCount = cellCount; PendingCount = pendingCount; GeometryCount = geometryCount; }
    }

    /// <summary>
    /// caller-owned固定工作区，仅构造时分配。数组属于调用方，绝不指向live Store；
    /// 有效前缀由Header限定。调用方应独占缓冲，完成整个TryCapture后再读取。
    /// InspectionBudget限制每遍检查槽/Cell/Region/请求数，两遍总检查数至多其两倍；
    /// 容量/预算不足时Source必须保留所有旧数组与Header，不扩容、不发布半份结果。
    /// </summary>
    public sealed class WorldReplicaBuffer
    {
        public WorldMaterialCellSample[] Cells { get; }
        public WorldPendingDepositSample[] Pending { get; }
        public WorldGridGeometry[] Geometries { get; }
        public int InspectionBudget { get; }
        public WorldReplicaHeader Header { get; set; }
        public WorldReplicaBuffer(int cellCapacity, int pendingCapacity, int inspectionBudget)
        {
            if (cellCapacity < 0 || pendingCapacity < 0 || inspectionBudget <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellCapacity));
            Cells = new WorldMaterialCellSample[cellCapacity]; Pending = new WorldPendingDepositSample[pendingCapacity];
            Geometries = new WorldGridGeometry[4]; InspectionBudget = inspectionBudget;
        }
    }

    public interface IWorldReplicationSource
    {
        /// <summary>仅在世界所属主线程的提交边界同步调用；失败时整个destination保持原样。</summary>
        bool TryCapture(WorldReplicaBuffer destination, out WorldCaptureFailure failure);
    }
}
