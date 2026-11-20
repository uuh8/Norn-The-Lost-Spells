using Game.Core;

namespace Game.ElementField
{
    /// <summary>
    /// Authority材料Tick的只读表现sidecar。调用方先完成同轮WorldReplicaBuffer数量捕获，
    /// 再同步调用此Source；本对象没有Update、时钟或Store写权限，不参与GMU、ACK或checkpoint。
    /// 成功的Count可为0（该Tick无合格组）；失败也将Count清零，旧数组前缀不再有效。
    /// 这种失败只代表本轮没有可靠运动证据，数量复制仍沿原提交链完成。
    /// </summary>
    public sealed class WorldLiquidMotionSource : IWorldLiquidMotionSource
    {
        private readonly ElementWorldRuntime _world;
        public WorldLiquidMotionSource(ElementWorldRuntime world) => _world = world;

        public bool TryCapture(WorldLiquidMotionCaptureBuffer destination, WorldReplicaBuffer quantities)
        {
            if (destination == null) return false;
            destination.Count = 0;
            if (_world == null || !_world.IsInitialized || _world.ExecutionMode != WorldExecutionMode.Authority
                || _world.IsWorldStepInProgress || _world.IsSimulationFaulted || quantities == null
                || quantities.Header.WorldTick != _world.WorldTick
                || quantities.Header.CellCount < 0 || quantities.Header.CellCount > quantities.Cells.Length
                || quantities.Header.GeometryCount < 0 || quantities.Header.GeometryCount > quantities.Geometries.Length)
                return false;
            // 用已经装配的Liquid Owner，不能新建Solver或绕过Replica所有权创建可写对象。
            var liquid = _world.GridLiquids as LiquidWorldRuntime;
            if (liquid == null || !liquid.IsFluidInitialized
                || !float.IsFinite(liquid.TransportInterval) || liquid.TransportInterval <= 0f) return false;
            bool foundGeometry = false;
            for (int i = 0; i < quantities.Header.GeometryCount; i++)
            {
                var geometry = quantities.Geometries[i];
                if (geometry.Domain != WorldMaterialDomain.Liquid) continue;
                if (foundGeometry || geometry.ChunkSize != liquid.ChunkSize
                    || geometry.CellSize != liquid.CellSize || geometry.Origin != liquid.Origin) return false;
                foundGeometry = true;
            }
            if (!foundGeometry || !liquid.TryCaptureSolverMotion(destination, quantities)) return false;
            destination.Epoch = liquid.TransportEpoch;
            destination.Interval = liquid.TransportInterval;
            destination.ChunkSize = liquid.ChunkSize;
            return true;
        }
    }
}
