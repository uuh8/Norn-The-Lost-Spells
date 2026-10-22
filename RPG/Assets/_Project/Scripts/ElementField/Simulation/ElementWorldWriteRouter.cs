namespace Game.ElementField
{
    /// <summary>
    /// ElementWorld 的纯分流规则。Single Writer 在这里意味着一次请求只选一个下游；
    /// PBF Water 失败时绝不回写 Cell，否则同一份水量会形成两套 Simulation Truth。
    /// </summary>
    public static class ElementWorldWriteRouter
    {
        public static bool TryRoute(
            IMaterialSimulationRouteReadOnly routes,
            in ElementWriteRequest request,
            IElementWriteSink cellSink,
            IFluidDepositSink fluidSink)
        {
            return TryRoute(routes, in request, cellSink, fluidSink, null);
        }

        public static bool TryRoute(
            IMaterialSimulationRouteReadOnly routes,
            in ElementWriteRequest request,
            IElementWriteSink cellSink,
            IFluidDepositSink fluidSink,
            IFluidDepositSink gridLiquidSink,
            IMaterialDepositSink fixedSolidSink = null,
            IMaterialDepositSink gasSink = null)
        {
            if (routes == null
                || !routes.TryResolve(request.MaterialKind, out MaterialSimulationBackendKind backend))
            {
                return false;
            }

            switch (backend)
            {
                case MaterialSimulationBackendKind.ElementCell:
                    return cellSink != null && cellSink.TryEnqueueWrite(in request);

                case MaterialSimulationBackendKind.GpuPbfLiquid:
                    // Sink 失败也绝不尝试 Cell：一个请求只能有一个 Simulation Writer。
                    return fluidSink != null
                        && fluidSink.IsFluidInitialized
                        && fluidSink.TryEnqueueDeposit(in request);

                case MaterialSimulationBackendKind.GridLiquid:
                    return gridLiquidSink != null
                        && gridLiquidSink.IsFluidInitialized
                        && gridLiquidSink.TryEnqueueDeposit(in request);

                case MaterialSimulationBackendKind.FixedSolid:
                    return fixedSolidSink != null && fixedSolidSink.IsMaterialInitialized
                        && fixedSolidSink.TryEnqueueDeposit(in request);
                case MaterialSimulationBackendKind.GridGas:
                    return gasSink != null && gasSink.IsMaterialInitialized
                        && gasSink.TryEnqueueDeposit(in request);

                default:
                    // Unsupported 与未来未知 Backend 都是明确拒绝，不能形成 Silent Failover。
                    return false;
            }
        }
    }
}
