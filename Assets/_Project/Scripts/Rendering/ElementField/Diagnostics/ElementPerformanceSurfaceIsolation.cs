using System;
using Game.Core;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>只挂在专用性能场景。隔离液面开销，不改变粒子模拟、查询或生产场景设置。</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class ElementPerformanceSurfaceIsolation : MonoBehaviour
    {
        [SerializeField] private GpuLiquidSurfaceRenderer[] _surfaces;
        [SerializeField] private GpuLiquidSurfaceRenderer _water;

        private void Start()
        {
            string mode = "all";
            bool diagnosticsEnabled = true;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-ewperfSurface" && i + 1 < args.Length) mode = args[++i];
                if (args[i] == "-ewperfSurfaceDiagnostics" && i + 1 < args.Length)
                    diagnosticsEnabled = args[++i] != "off";
            }
            if (mode != "all" && mode != "water" && mode != "off")
            {
                GameLog.Error("[ELEMENT-PERF] Invalid surface isolation mode", "ElementPerformance");
                if (!Application.isEditor) Application.Quit(1);
                return;
            }
            // Start 早于 Lab 的 Warmup。资源释放与开关成本不计入 Measure；all 路径不触碰组件。
            if (_surfaces != null)
                for (int i = 0; i < _surfaces.Length; i++)
                    if (_surfaces[i] != null)
                    {
                        _surfaces[i].SetDevelopmentDiagnosticsEnabled(diagnosticsEnabled);
                        if (mode != "all")
                            _surfaces[i].enabled = mode == "water" && _surfaces[i] == _water;
                    }
            GameLog.Info(
                $"[ELEMENT-PERF] SurfaceMode={mode}; SurfaceDiagnostics={diagnosticsEnabled}",
                "ElementPerformance");
        }

        private void OnApplicationQuit()
        {
            if (_surfaces == null)
                return;

            // GPU耗时接近off时，仅凭组件引用不能证明真正生成并绘制了液面；退出前保存最终Stage和计数。
            // 此处是一次性Development诊断边界，字符串分配发生在Measure结束之后。
            for (int i = 0; i < _surfaces.Length; i++)
            {
                GpuLiquidSurfaceRenderer surface = _surfaces[i];
                if (surface == null || !surface.enabled)
                    continue;
                GameLog.Info(
                    $"[ELEMENT-PERF-SURFACE] Material={surface.TargetMaterial}; "
                    + $"Stage={surface.DiagnosticStage}; Geometry={surface.GeometryDiagnosticStage}; "
                    + $"Triangles={surface.DiagnosticTriangleCount}; Overflow={surface.DiagnosticTriangleOverflow}; "
                    + $"PeakTriangles={surface.DiagnosticMaximumTriangleCount}; "
                    + $"PositiveReads={surface.DiagnosticPositiveTriangleReadbackCount}; "
                    + $"DrawReadyReads={surface.DiagnosticDrawReadyObservationCount}; "
                    + $"GeometryReadyReads={surface.DiagnosticGeometryReadyObservationCount}; "
                    + $"Rebuilds={surface.DiagnosticSurfaceRebuildCount}; Draws={surface.DiagnosticDrawCount}; "
                    + $"Grid={surface.DiagnosticGridResolution}",
                    "ElementPerformance");
            }
        }
    }
}
