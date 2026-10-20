using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Phase F Player 矩阵的串行调度器。一次只运行一个独立 Player，避免多个样本争抢
    /// CPU/GPU；每次 Player 仍由生产 Performance Lab 自己导出原始 CSV 与审计证据。
    /// </summary>
    [InitializeOnLoad]
    internal static class FluidBenchmarkRunner
    {
        private const string RequestPath = "Temp/ElementPerformance/run-phase-f-benchmarks.request";
        private const string ResultPath = "Temp/ElementPerformance/phase-f-benchmark-result.json";
        private static readonly List<RunSpec> Queue = new List<RunSpec>(16);
        private static readonly List<RunResult> Results = new List<RunResult>(16);
        private static Process _process;
        private static RunSpec _current;
        private static string _currentLog;
        private static double _nextProcessPoll;
        private static double _finishedObservedAt = -1d;
        private static bool _closeFallbackUsed;

        static FluidBenchmarkRunner() => EditorApplication.update += Poll;

        [MenuItem("Tools/Element Performance/Run Fluid Short Matrix")]
        private static void RunShortMatrix() => Begin(false);

        [MenuItem("Tools/Element Performance/Run Fluid 30 Minute Soak")]
        private static void RunSoak() => Begin(true);

        private static void Poll()
        {
            if (_process != null)
            {
                if (!_process.HasExited)
                {
                    if (EditorApplication.timeSinceStartup < _nextProcessPoll) return;
                    _nextProcessPoll = EditorApplication.timeSinceStartup + 1d;
                    if (_finishedObservedAt < 0d && File.Exists(_currentLog)
                        && File.ReadAllText(_currentLog).Contains("[ELEMENT-PERF] Finished"))
                        _finishedObservedAt = EditorApplication.timeSinceStartup;
                    // 完成导出不代表进程已经退出；不能仅凭停留现象归因为 DX12 teardown。
                    // 等待 5 秒后用 WM_CLOSE 兜底，并在报告标明，不能当成自动退出验收通过。
                    if (_finishedObservedAt >= 0d
                        && EditorApplication.timeSinceStartup - _finishedObservedAt >= 5d)
                    {
                        _closeFallbackUsed = _process.CloseMainWindow();
                        _finishedObservedAt = double.PositiveInfinity;
                    }
                    return;
                }
                Results.Add(new RunResult
                {
                    Name = _current.Name,
                    Scenario = _current.Scenario,
                    Api = _current.Dx11 ? "DX11" : "DX12",
                    Uncapped = _current.Uncapped,
                    ExitCode = _process.ExitCode,
                    CloseFallbackUsed = _closeFallbackUsed
                });
                _process.Dispose();
                _process = null;
                StartNext();
                return;
            }

            if (!File.Exists(RequestPath) || EditorApplication.isCompiling
                || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            string mode = File.ReadAllText(RequestPath).Trim();
            File.Delete(RequestPath);
            Begin(string.Equals(mode, "soak", StringComparison.OrdinalIgnoreCase));
        }

        private static void Begin(bool soakOnly)
        {
            if (_process != null || Queue.Count != 0) return;
            string player = Path.GetFullPath("Builds/ElementPerformance/ElementPerformance.exe");
            if (!File.Exists(player))
            {
                WriteFailure("缺少 Development Player，请先运行 Element Performance Build。");
                return;
            }
            Directory.CreateDirectory("Temp/ElementPerformance");
            Results.Clear();
            if (soakOnly)
            {
                Queue.Add(new RunSpec("F03-soak-dx12", "F03-soak", false, false));
            }
            else
            {
                AddRepeated("F00-audit", 3);
                AddRepeated("F01-reaction-audit", 3);
                AddRepeated("F02-multi-source", 3);
                Queue.Add(new RunSpec("F02-multi-source-uncapped", "F02-multi-source", true, false));
                Queue.Add(new RunSpec("F02-multi-source-dx11", "F02-multi-source", false, true));
                Queue.Add(new RunSpec("F04-over-budget-dx12", "F04-over-budget", false, false));
            }
            File.WriteAllText(ResultPath, JsonUtility.ToJson(new BenchmarkReport
                { Complete = false, Error = string.Empty, Runs = Array.Empty<RunResult>() }, true));
            StartNext();
        }

        private static void AddRepeated(string scenario, int count)
        {
            for (int i = 1; i <= count; i++)
                Queue.Add(new RunSpec($"{scenario}-dx12-run{i}", scenario, false, false));
        }

        private static void StartNext()
        {
            if (Queue.Count == 0)
            {
                WriteReport(string.Empty);
                return;
            }
            _current = Queue[0];
            Queue.RemoveAt(0);
            string player = Path.GetFullPath("Builds/ElementPerformance/ElementPerformance.exe");
            string log = Path.GetFullPath("Temp/ElementPerformance/" + _current.Name + ".log");
            if (File.Exists(log)) File.Delete(log);
            _currentLog = log;
            _nextProcessPoll = 0d;
            _finishedObservedAt = -1d;
            _closeFallbackUsed = false;
            string arguments = "-screen-width 1920 -screen-height 1080 -window-mode borderless "
                + "-ewperfScenario " + _current.Scenario + " -ewperfSurface off -ewperfQuit "
                + (_current.Uncapped ? "-ewperfUncapped " : string.Empty)
                + (_current.Dx11 ? "-force-d3d11 " : "-force-d3d12 ")
                + "-logFile \"" + log + "\"";
            try
            {
                _process = Process.Start(new ProcessStartInfo(player, arguments)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(player)
                });
                if (_process == null) throw new InvalidOperationException("Player 进程未创建。");
                GameLog.Info($"[PHASE-F-BENCHMARK] Started {_current.Name}", "Editor");
            }
            catch (Exception exception)
            {
                Results.Add(new RunResult { Name = _current.Name, Scenario = _current.Scenario,
                    Api = _current.Dx11 ? "DX11" : "DX12", Uncapped = _current.Uncapped, ExitCode = -1 });
                WriteFailure(exception.Message);
            }
        }

        private static void WriteFailure(string error) => WriteReport(error);

        private static void WriteReport(string error)
        {
            File.WriteAllText(ResultPath, JsonUtility.ToJson(new BenchmarkReport
            {
                Complete = string.IsNullOrEmpty(error),
                Error = error ?? string.Empty,
                Runs = Results.ToArray()
            }, true));
            GameLog.Info($"[PHASE-F-BENCHMARK] Complete Runs={Results.Count} Error={error}", "Editor");
        }

        private readonly struct RunSpec
        {
            public readonly string Name, Scenario;
            public readonly bool Uncapped, Dx11;
            public RunSpec(string name, string scenario, bool uncapped, bool dx11)
            { Name = name; Scenario = scenario; Uncapped = uncapped; Dx11 = dx11; }
        }

        [Serializable] private sealed class BenchmarkReport
        { public bool Complete; public string Error; public RunResult[] Runs; }

        [Serializable] private struct RunResult
        { public string Name, Scenario, Api; public bool Uncapped, CloseFallbackUsed; public int ExitCode; }
    }
}
