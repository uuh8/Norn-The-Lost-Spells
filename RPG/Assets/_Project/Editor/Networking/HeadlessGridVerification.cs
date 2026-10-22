using System;
using System.Diagnostics;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>从 Editor 菜单启动真实 Server 验证，轮询进程而不阻塞 Editor，也不接管用户的其他进程。</summary>
    [InitializeOnLoad]
    public static class HeadlessGridVerification
    {
        public const string ReportDirectory = "Tools/Networking/.artifacts/headless-grid";
        private const string PlayerDirectory = "Builds/OnlineLab/HeadlessGrid";
        private static Process _process;
        private static DateTime _startedUtc;
        private static bool _positive, _negativePassed;
        private static double _nextPoll;

        static HeadlessGridVerification()
        {
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload += StopOwnedProcess;
            EditorApplication.quitting += StopOwnedProcess;
        }

        [MenuItem("Tools/Norn/Online/Run Headless Grid Verification")]
        public static void Run()
        {
            OnlineSceneSetup.RequireCleanEditor();
            if (_process != null) throw new InvalidOperationException("Headless Grid 验证正在运行，请等待结果。");
            if (!File.Exists(Path.Combine(PlayerDirectory, "NornGridServer.exe")))
                throw new InvalidOperationException("请先 Build Headless Grid Windows Server。");
            Directory.CreateDirectory(ReportDirectory);
            _negativePassed = false;
            _positive = false;
            WriteSummary("Running", false, null);
            try { StartRound(); }
            catch (Exception exception)
            {
                StopOwnedProcess();
                WriteSummary("Failed", false, exception.ToString());
                throw;
            }
        }

        private static void StartRound()
        {
            string log = Path.GetFullPath(Path.Combine(ReportDirectory, _positive ? "positive.log" : "wrong-rate.log"));
            // 只使用工具固定的参数和受控路径；不经过 shell。隐藏无图形 Server，用户仍可正常操作 Editor。
            var start = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(Path.Combine(PlayerDirectory, "NornGridServer.exe")),
                WorkingDirectory = Path.GetFullPath(PlayerDirectory),
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = "-batchmode -nographics -nornHeadlessGridProbe -nornHeadlessGridExpectedHz " +
                    (_positive ? "10" : "20") + " -logFile \"" + log + "\""
            };
            _startedUtc = DateTime.UtcNow;
            _process = Process.Start(start);
            if (_process == null) throw new InvalidOperationException("无法启动 Grid Server。");
        }

        private static void Poll()
        {
            if (_process == null || EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + .25d;
            try
            {
                if (!_process.HasExited)
                {
                    if ((DateTime.UtcNow - _startedUtc).TotalSeconds > 45)
                        throw new TimeoutException("Grid Server 45 秒内未退出；仅清理本工具启动的进程。");
                    return;
                }
                int code = _process.ExitCode;
                _process.Dispose();
                _process = null;
                string source = Path.Combine(PlayerDirectory, "ProbeReports/headless-grid.json");
                // 时间取文件的 UTC 元数据，不重复解析 JSON 日期，避免本地时区造成旧报告误判。
                if (!File.Exists(source) || File.GetLastWriteTimeUtc(source) < _startedUtc.AddSeconds(-1))
                    throw new InvalidOperationException("没有本轮新生成的 Grid 报告。");
                string json = File.ReadAllText(source);
                File.WriteAllText(Path.Combine(ReportDirectory, _positive ? "positive.json" : "wrong-rate.json"), json);
                ProbeResult report = JsonUtility.FromJson<ProbeResult>(json);
                bool accepted = report != null && (_positive
                    ? code == 0 && report.Passed
                    : code == 1 && !report.Passed && report.Error == "ConfiguredTickRateMismatch");
                if (!accepted) throw new InvalidOperationException("Grid " + (_positive ? "正向" : "负向") + "检查未通过，ExitCode=" + code);
                if (!_positive)
                {
                    _negativePassed = true;
                    _positive = true;
                    StartRound();
                    return;
                }
                WriteSummary("Finished", true, null);
                GameLog.Info("Headless Grid 正反向验证通过；报告位于 " + ReportDirectory, "OnlineLab");
            }
            catch (Exception exception)
            {
                StopOwnedProcess();
                WriteSummary("Failed", false, exception.ToString());
                GameLog.Error(exception.Message, "OnlineLab");
            }
        }

        private static void StopOwnedProcess()
        {
            if (_process == null) return;
            // Domain Reload / 退出只终止登记的子进程；不按进程名查杀任何外部 Server 或 Editor。
            try { if (!_process.HasExited) _process.Kill(); }
            finally { _process.Dispose(); _process = null; }
            WriteSummary("Interrupted", false, "Editor 重载、退出或验证异常；本轮不能计为通过。");
        }

        private static void WriteSummary(string state, bool passed, string error)
        {
            Directory.CreateDirectory(ReportDirectory);
            File.WriteAllText(Path.Combine(ReportDirectory, "verification.json"), JsonUtility.ToJson(new Summary
            {
                State = state, Passed = passed, NegativePassed = _negativePassed,
                Error = error, RecordedAtUtc = DateTime.UtcNow.ToString("O")
            }, true));
        }

        [Serializable] private sealed class ProbeResult { public bool Passed; public string Error; }
        [Serializable] private sealed class Summary
        {
            public string State, Error, RecordedAtUtc;
            public bool Passed, NegativePassed;
        }
    }
}
