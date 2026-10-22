using Game.ElementField;
using Game.Materials;

namespace Game.Rendering
{
    /// <summary>Presentation 只根据最终 Route 判断目标 Material 是否由 GPU Liquid Backend 承载。</summary>
    public static class LiquidBackendAvailabilityPolicy
    {
        public static bool ShouldRender(
            MaterialId material,
            IMaterialSimulationRouteReadOnly routes)
        {
            return material != MaterialId.Empty
                && routes != null
                && routes.TryResolve(material, out MaterialSimulationBackendKind backend)
                && backend == MaterialSimulationBackendKind.GpuPbfLiquid;
        }
    }
}
