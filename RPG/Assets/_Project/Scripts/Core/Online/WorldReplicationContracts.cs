using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>独立原生网格域；LegacyCell的byte量（尤其Fire强度）不参与三相GMU求和。</summary>
    public enum WorldMaterialDomain : byte { LegacyCell, Liquid, Solid, Gas, Cellular }
    public enum WorldMaterialStorage : byte { Resident, Dynamic, Stable }
    public enum WorldCaptureFailure : byte
    {
        None, NotInitialized, UnsupportedBackend, SimulationInProgress, SimulationFaulted,
        InvalidGeometry, CellCapacity, PendingCapacity, InspectionBudget,
        // 接收端需要基底版本、显式空覆盖与细格数量合同。追加故障原因，保留既有诊断编号。
        UnsupportedCellularWorld
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
        public readonly ulong BaseVersion;
        public readonly uint CellCapacity;
        public WorldGridGeometry(WorldMaterialDomain domain, Vector3 origin, float cellSize, int chunkSize)
            : this(domain, origin, cellSize, chunkSize, 0, 0) { }
        public WorldGridGeometry(WorldMaterialDomain domain, Vector3 origin, float cellSize, int chunkSize,
            ulong baseVersion, uint cellCapacity)
        { Domain = domain; GridId = (byte)domain; Origin = origin; CellSize = cellSize; ChunkSize = chunkSize;
          BaseVersion = baseVersion; CellCapacity = cellCapacity; }
    }

    /// <summary>地形只有形状。作者液体记录相对可信基底的剩余量，后成静水从空地开始积累。</summary>
    public enum WorldCellularSpanKind : byte { Terrain = 1, AuthoredLiquid = 2, ColdMaterial = 3, ColdLiquid = ColdMaterial }

    public readonly struct WorldCellularSpan
    {
        public readonly WorldCellularSpanKind Kind;
        public readonly BoundsInt Cells;
        public readonly byte MaterialId;
        public readonly uint OriginalQuantity, Quantity;
        public WorldCellularSpan(WorldCellularSpanKind kind, BoundsInt cells, byte materialId, uint originalQuantity, uint quantity)
        { Kind = kind; Cells = cells; MaterialId = materialId; OriginalQuantity = originalQuantity; Quantity = quantity; }
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
        public readonly long RetryTick;
        public WorldPendingDepositSample(WorldMaterialDomain domain, int queueIndex, byte materialId,
            Vector3 worldPosition, ushort totalAmount, float radius, bool useLinearFalloff,
            Vector3 initialVelocity, Vector3 surfaceNormal, uint remaining, int cursor, uint waitingTicks)
            : this(domain,queueIndex,materialId,worldPosition,totalAmount,radius,useLinearFalloff,
                initialVelocity,surfaceNormal,remaining,cursor,waitingTicks,0) { }
        public WorldPendingDepositSample(WorldMaterialDomain domain, int queueIndex, byte materialId,
            Vector3 worldPosition, ushort totalAmount, float radius, bool useLinearFalloff,
            Vector3 initialVelocity, Vector3 surfaceNormal, uint remaining, int cursor, uint waitingTicks, long retryTick)
        {
            Domain = domain; QueueIndex = queueIndex; MaterialId = materialId; WorldPosition = worldPosition;
            TotalAmount = totalAmount; Radius = radius; UseLinearFalloff = useLinearFalloff;
            InitialVelocity = initialVelocity; SurfaceNormal = surfaceNormal; Remaining = remaining;
            Cursor = cursor; WaitingTicks = waitingTicks; RetryTick = retryTick;
        }
    }

    /// <summary>
    /// 当前提交边界的空间数量快照元数据，不冒充网络Revision/ACK。
    /// 没有包含Solver速度、Outlet fractional、规则进度等，不能用于恢复Authority规则执行。
    /// </summary>
    public readonly struct WorldReplicaHeader
    {
        public readonly long WorldTick;
        public readonly int CellCount, PendingCount, GeometryCount, SpanCount;
        public bool CanRestoreAuthorityRules => false;
        public WorldReplicaHeader(long worldTick, int cellCount, int pendingCount, int geometryCount)
            : this(worldTick,cellCount,pendingCount,geometryCount,0) { }
        public WorldReplicaHeader(long worldTick, int cellCount, int pendingCount, int geometryCount, int spanCount)
        { WorldTick = worldTick; CellCount = cellCount; PendingCount = pendingCount; GeometryCount = geometryCount; SpanCount = spanCount; }
    }

    /// <summary>
    /// caller-owned 工作区。普通构造沿用固定数组；正式 Cellular 权威可按需申请 4096 行的额外页。
    /// 有效前缀由Header限定。调用方应独占缓冲，完成整个TryCapture后再读取。
    /// InspectionBudget限制每遍检查槽/Cell/Region/请求数，两遍总检查数至多其两倍；
    /// 容量/预算不足时Source必须保留所有旧数组与Header，不扩容、不发布半份结果。
    /// </summary>
    public sealed class WorldReplicaBuffer
    {
        public const int CapturePageRows=4096;
        private sealed class Overflow<T>
        {
            private readonly T[] _first;
            private T[][] _pages=Array.Empty<T[]>();
            private int _livePages;
            internal readonly int Limit;
            internal Overflow(T[] first,int limit){_first=first;Limit=Math.Max(first.Length,limit);}
            internal int Allocated=0;
            internal T Read(int index)=>index<_first.Length?_first[index]:_pages[(index-_first.Length)/CapturePageRows][(index-_first.Length)%CapturePageRows];
            internal void Write(int index,T value)
            {
                if(index<_first.Length){_first[index]=value;return;}
                if((uint)index>=(uint)Limit)throw new ArgumentOutOfRangeException(nameof(index));
                int extra=index-_first.Length,page=extra/CapturePageRows;
                Grow(page);_pages[page][extra%CapturePageRows]=value;
            }
            private void Grow(int page)
            {
                if(page<_livePages)return;
                if(page>=_pages.Length)Array.Resize(ref _pages,Math.Max(page+1,Math.Max(4,2*_pages.Length)));
                while(_livePages<=page)
                {
                    int length=Math.Min(CapturePageRows,Limit-_first.Length-_livePages*CapturePageRows);
                    _pages[_livePages++]=new T[length];Allocated+=length;
                }
            }
            // 私有 staging 与目标共用相同首段尺寸和上限，每页仅复制本次提交的有效前缀。
            internal void CopyTo(Overflow<T> target,int count)
            {
                int head=Math.Min(count,_first.Length);Array.Copy(_first,target._first,head);
                for(int i=0,remaining=count-head;remaining>0;i++)
                {
                    target.Grow(i);int rows=Math.Min(remaining,CapturePageRows);
                    Array.Copy(_pages[i],target._pages[i],rows);remaining-=rows;
                }
            }
        }
        private readonly Overflow<WorldMaterialCellSample> _cellRows;
        private readonly Overflow<WorldCellularSpan> _spanRows;
        public WorldMaterialCellSample[] Cells { get; }
        public WorldPendingDepositSample[] Pending { get; }
        public WorldGridGeometry[] Geometries { get; }
        public WorldCellularSpan[] Spans { get; }
        public WorldChunkKey[] ChangedChunks { get; }
        public int InspectionBudget { get; }
        public int MaximumCellRows=>_cellRows.Limit;
        public int MaximumSpanRows=>_spanRows.Limit;
        public int AllocatedCellRows=>Cells.Length+_cellRows.Allocated;
        public int AllocatedSpanRows=>Spans.Length+_spanRows.Allocated;
        public bool UsesPagedCapture=>MaximumCellRows>Cells.Length || MaximumSpanRows>Spans.Length;
        public WorldMaterialCellSample ReadCellRow(int index)=>_cellRows.Read(index);
        public WorldCellularSpan ReadSpanRow(int index)=>_spanRows.Read(index);
        public void WriteCellRow(int index,WorldMaterialCellSample row)=>_cellRows.Write(index,row);
        public void WriteSpanRow(int index,WorldCellularSpan row)=>_spanRows.Write(index,row);
        public void CopyRowsFrom(WorldReplicaBuffer source,int cellRows,int spanRows)
        {
            if(source==null || Cells.Length!=source.Cells.Length || Spans.Length!=source.Spans.Length ||
                cellRows<0 || cellRows>MaximumCellRows || cellRows>source.AllocatedCellRows ||
                spanRows<0 || spanRows>MaximumSpanRows || spanRows>source.AllocatedSpanRows)throw new ArgumentOutOfRangeException();
            source._cellRows.CopyTo(_cellRows,cellRows);source._spanRows.CopyTo(_spanRows,spanRows);
        }
        public WorldReplicaHeader Header { get; set; }
        public WorldReplicaBuffer(int cellCapacity, int pendingCapacity, int inspectionBudget)
            : this(cellCapacity,pendingCapacity,inspectionBudget,-1) { }
        public WorldReplicaBuffer(int cellCapacity, int pendingCapacity, int inspectionBudget, int spanCapacity, int changedChunkCapacity = 128)
            :this(cellCapacity,pendingCapacity,inspectionBudget,spanCapacity,changedChunkCapacity,cellCapacity,spanCapacity) { }
        public WorldReplicaBuffer(int cellCapacity,int pendingCapacity,int inspectionBudget,int spanCapacity,int changedChunkCapacity,int maximumCellRows,int maximumSpanRows)
        {
            if (cellCapacity < 0 || pendingCapacity < 0 || inspectionBudget <= 0 || spanCapacity < -1 || changedChunkCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(cellCapacity));
            Cells = new WorldMaterialCellSample[cellCapacity]; Pending = new WorldPendingDepositSample[pendingCapacity];
            Spans = new WorldCellularSpan[spanCapacity < 0 ? cellCapacity : spanCapacity];
            _cellRows=new Overflow<WorldMaterialCellSample>(Cells,maximumCellRows);_spanRows=new Overflow<WorldCellularSpan>(Spans,maximumSpanRows);
            ChangedChunks = new WorldChunkKey[changedChunkCapacity];
            Geometries = new WorldGridGeometry[5]; InspectionBudget = inspectionBudget;
        }
    }

    public interface IWorldReplicationSource
    {
        /// <summary>仅在世界所属主线程的提交边界同步调用；失败时整个destination保持原样。</summary>
        bool TryCapture(WorldReplicaBuffer destination, out WorldCaptureFailure failure);
    }
}
