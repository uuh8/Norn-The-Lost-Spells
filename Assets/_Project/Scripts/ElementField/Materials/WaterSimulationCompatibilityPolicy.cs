using System;

namespace Game.ElementField
{
    /// <summary>
    /// Requested Mode 与当前硬件能力共同产生的初始化期事实。
    /// UsedUnsupportedHardwareFallback 只表示硬件不支持；配置损坏不得伪装成同一种回退。
    /// </summary>
    public readonly struct WaterSimulationCompatibilityDecision
    {
        public readonly WaterSimulationMode RequestedMode;
        public readonly WaterSimulationMode EffectiveMode;
        public readonly MaterialSimulationBackendKind WaterBackend;
        public readonly bool UsedUnsupportedHardwareFallback;

        internal WaterSimulationCompatibilityDecision(
            WaterSimulationMode requestedMode,
            WaterSimulationMode effectiveMode,
            MaterialSimulationBackendKind waterBackend,
            bool usedUnsupportedHardwareFallback)
        {
            RequestedMode = requestedMode;
            EffectiveMode = effectiveMode;
            WaterBackend = waterBackend;
            UsedUnsupportedHardwareFallback = usedUnsupportedHardwareFallback;
        }
    }

    public static class WaterSimulationCompatibilityPolicy
    {
        public static WaterSimulationCompatibilityDecision Resolve(
            WaterSimulationMode requestedMode,
            bool supportsComputeShaders)
        {
            switch (requestedMode)
            {
                case WaterSimulationMode.LegacyCell:
                    return new WaterSimulationCompatibilityDecision(
                        requestedMode,
                        WaterSimulationMode.LegacyCell,
                        MaterialSimulationBackendKind.ElementCell,
                        false);

                case WaterSimulationMode.GpuPbf when supportsComputeShaders:
                    return new WaterSimulationCompatibilityDecision(
                        requestedMode,
                        WaterSimulationMode.GpuPbf,
                        MaterialSimulationBackendKind.GpuPbfLiquid,
                        false);

                case WaterSimulationMode.GpuPbf:
                    return new WaterSimulationCompatibilityDecision(
                        requestedMode,
                        WaterSimulationMode.LegacyCell,
                        MaterialSimulationBackendKind.ElementCell,
                        true);

                case WaterSimulationMode.GridLiquid:
                    return new WaterSimulationCompatibilityDecision(
                        requestedMode,
                        WaterSimulationMode.GridLiquid,
                        MaterialSimulationBackendKind.GridLiquid,
                        false);

                default:
                    throw new ArgumentOutOfRangeException(nameof(requestedMode), requestedMode, "未知 Water Simulation Mode。");
            }
        }
    }
}
