using Game.ElementField;

namespace Game.Rendering
{
    /// <summary>
    /// 两套 Water Presentation 的 pure feature-toggle policy。Renderer 必须在分配 Mesh/VRAM 前检查，
    /// 避免 Legacy Cell Mesh 与 GPU Procedural Surface 同帧叠加造成双重颜色、Depth 与 Overdraw。
    /// </summary>
    public static class WaterRendererModePolicy
    {
        public static bool ShouldRenderLegacy(WaterSimulationMode mode) =>
            mode == WaterSimulationMode.LegacyCell;

        public static bool ShouldRenderGpuPbf(WaterSimulationMode mode) =>
            mode == WaterSimulationMode.GpuPbf;
    }
}
