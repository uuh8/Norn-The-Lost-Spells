using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 真实CPU材料Store的只读Source。先计数/验证预算再复制，两个同步遍历间不调用外部回调或推进模拟，
    /// 因而第二遍读取同一提交状态；执行中/异常锁存的世界不能发布。不是Transport、Replica安装器或规则存档。
    /// Source与buffer均由装配方初始化并复用；TryCapture不分配、不排序、不借出Store数组。
    /// </summary>
    public sealed class WorldReplicationSource : IWorldReplicationSource
    {
        private readonly ElementWorldRuntime _world;
        public WorldReplicationSource(ElementWorldRuntime world) => _world = world;

        public bool TryCapture(WorldReplicaBuffer destination, out WorldCaptureFailure failure)
        {
            failure = WorldCaptureFailure.None;
            if (_world == null || !_world.IsInitialized || destination == null)
            { failure = WorldCaptureFailure.NotInitialized; return false; }
            if (_world.IsWorldStepInProgress)
            { failure = WorldCaptureFailure.SimulationInProgress; return false; }
            if (_world.IsSimulationFaulted)
            { failure = WorldCaptureFailure.SimulationFaulted; return false; }
            // GPU粒子/Archive没有包含在CPU四域Store中，禁止返回看似完整的部分世界。
            if (_world.EffectiveWaterSimulationMode == WaterSimulationMode.GpuPbf)
            { failure = WorldCaptureFailure.UnsupportedBackend; return false; }
            // Water的最终模式不能代表Poison/Sticky的独立Route。检查完整byte身份空间，
            // 固定255次只读查询、不枚举Enum或分配数组；任何GPU owner均拒绝部分CPU结果。
            IMaterialSimulationRouteReadOnly routes = _world.MaterialRoutes;
            if (routes != null)
                for (int material = 1; material <= byte.MaxValue; material++)
                    if (routes.TryResolve((MaterialId)material, out var backend)
                        && backend == MaterialSimulationBackendKind.GpuPbfLiquid)
                    { failure = WorldCaptureFailure.UnsupportedBackend; return false; }

            var preflight = new WorldCaptureWriter(destination, false);
            if (!_world.VisitReplicationState(ref preflight))
            { failure = preflight.Failure; return false; }
            var copy = new WorldCaptureWriter(destination, true);
            _world.VisitReplicationState(ref copy);
            // 本同步调用内没有yield、事件或写入口；第二遍不能出现预检后状态变化。
            // Header最后发布，有效前缀外的旧值继续由caller保留，避免每次清空整个最大缓冲。
            destination.Header = new WorldReplicaHeader(_world.WorldTick, copy.CellCount, copy.PendingCount, copy.GeometryCount);
            return true;
        }
    }

    /// <summary>
    /// 同一套遍历在预检时只增加计数，复制时仅写预留槽。每个空槽也消耗inspection，
    /// 避免稀疏世界/大湖用极少输出记录绕过CPU预算。固定数组容量及几何在首遍全部验证。
    /// </summary>
    internal struct WorldCaptureWriter
    {
        private readonly WorldReplicaBuffer _buffer;
        private readonly bool _copy;
        private int _inspections;
        internal int CellCount, PendingCount, GeometryCount;
        internal WorldCaptureFailure Failure;
        internal WorldCaptureWriter(WorldReplicaBuffer buffer, bool copy)
        {
            _buffer = buffer; _copy = copy; _inspections = 0;
            CellCount = PendingCount = GeometryCount = 0; Failure = WorldCaptureFailure.None;
        }
        internal bool Inspect()
        {
            if (!_copy && _inspections >= _buffer.InspectionBudget)
            { Failure = WorldCaptureFailure.InspectionBudget; return false; }
            _inspections++; return true;
        }
        internal bool Geometry(WorldMaterialDomain domain, Vector3 origin, float size, int chunkSize)
        {
            if (!Inspect()) return false;
            if (!_copy && (!float.IsFinite(origin.x) || !float.IsFinite(origin.y) || !float.IsFinite(origin.z)
                || !float.IsFinite(size) || size <= 0 || chunkSize <= 0))
            { Failure = WorldCaptureFailure.InvalidGeometry; return false; }
            if (_copy) _buffer.Geometries[GeometryCount] = new WorldGridGeometry(domain, origin, size, chunkSize);
            GeometryCount++; return true;
        }
        internal bool Cell(WorldMaterialDomain domain, WorldMaterialStorage storage, Vector3Int cell, byte material, uint quantity)
        {
            if (quantity == 0) return true;
            if (!_copy && CellCount >= _buffer.Cells.Length)
            { Failure = WorldCaptureFailure.CellCapacity; return false; }
            if (_copy) _buffer.Cells[CellCount] = new WorldMaterialCellSample(domain, storage, cell, material, quantity);
            CellCount++; return true;
        }
        internal bool Pending(WorldMaterialDomain domain, int index, in ElementWriteRequest request,
            uint remaining, int cursor, uint waitingTicks)
        {
            if (!Inspect()) return false;
            if (!_copy && PendingCount >= _buffer.Pending.Length)
            { Failure = WorldCaptureFailure.PendingCapacity; return false; }
            if (_copy) _buffer.Pending[PendingCount] = new WorldPendingDepositSample(domain, index, (byte)request.MaterialKind,
                request.WorldPosition, request.TotalAmount, request.Radius, request.UseLinearFalloff,
                request.InitialVelocity, request.SurfaceNormal, remaining, cursor, waitingTicks);
            PendingCount++; return true;
        }
    }
}
