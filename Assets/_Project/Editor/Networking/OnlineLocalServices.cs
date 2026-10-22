using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using Game.Core;
using UnityEditor;
using UnityEngine;

namespace Game.Project.Editor
{
    /// <summary>GUI 管理本机已配置服务；凭据只从忽略目录读到子进程环境，不进入 Inspector/命令行/日志。</summary>
    public static class OnlineLocalServices
    {
        [Serializable] private sealed class Connections { public string Development, DevelopmentMigration; }
        [Serializable] private sealed class ProcessRecord { public int ProcessId; public long StartTicks; public string Executable; }
        private const string DatabaseState = ".tools/local-mysql/state.json";
        private const string BusinessState = ".tools/local-online/online-business.json";

        [MenuItem("Tools/Norn/Online/Start Local Business Server")]
        public static void StartBusiness() => StartBusiness(true);
        [MenuItem("Tools/Norn/Online/Start Local Room Business Server (Diagnostic)")]
        public static void StartRoomBusiness() => StartBusiness(true);
        private static void StartBusiness(bool roomInstances)
        {
            if (FindRecorded(BusinessState) != null) { GameLog.Info("本工具业务服仍在运行。", "OnlineServices"); return; }
            AssertPortFree(5080);
            string root = Path.GetFullPath(".");
            var secrets = JsonUtility.FromJson<Connections>(File.ReadAllText(".tools/local-mysql/connections.private.json"));
            string directory = Path.GetFullPath("../Server/Norn.Business");
            // Windows 会锁住运行中的 DLL；独立 Publish 目录让 Test/Build 的 bin 输出不被服务进程占用。
            string dll = Path.GetFullPath("../Server/.artifacts/publish/online/Norn.Business.dll");
            if (!File.Exists(dll)) throw new InvalidOperationException("请先在 Visual Studio Publish 到 Server/.artifacts/publish/online。");
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(root, ".tools/dotnet/dotnet.exe"), WorkingDirectory = directory,
                UseShellExecute = false, CreateNoWindow = true,
                Arguments = "\"" + dll + "\" --urls http://127.0.0.1:5080"
            };
            start.EnvironmentVariables["Online__Enabled"] = "true"; start.EnvironmentVariables["Online__AutoMigrate"] = "true";
            start.EnvironmentVariables["ConnectionStrings__Online"] = secrets.Development;
            start.EnvironmentVariables["Online__MigrationConnectionString"] = secrets.DevelopmentMigration;
            start.EnvironmentVariables["Online__ManifestPath"] = Path.GetFullPath(ContentManifestExporter.Output);
            // N2本机入口显式启用，不依赖机器环境；部署默认值仍是false，远端另有独立Gate。
            start.EnvironmentVariables["Online__RoomsEnabled"] = roomInstances ? "true" : "false";
            start.EnvironmentVariables["Online__Instances__Enabled"] = roomInstances ? "true" : "false";
            if (roomInstances)
            {
                start.EnvironmentVariables["Online__Instances__BuildPath"] = Path.GetFullPath(RoomLifecycleTools.ServerPath);
                start.EnvironmentVariables["Online__Instances__WorkRoot"] = Path.GetFullPath("../Server/.artifacts/instances");
            }
            SaveStarted(start, BusinessState);
            GameLog.Info("已请求启动业务服；浏览器访问 http://127.0.0.1:5080/health/ready 检查数据库就绪。", "OnlineServices");
        }
        [MenuItem("Tools/Norn/Online/Stop Local Business Server")]
        public static void StopBusiness()
        {
            using var process = FindRecorded(BusinessState);
            if (process != null) { process.Kill(); process.WaitForExit(5000); }
            if (File.Exists(BusinessState)) File.Delete(BusinessState);
            GameLog.Info("已停止本工具登记的业务服。", "OnlineServices");
        }
        [MenuItem("Tools/Norn/Online/Start Local Database")]
        public static void StartDatabase()
        {
            if (FindRecorded(DatabaseState) != null) { GameLog.Info("Norn 数据库仍在运行。", "OnlineServices"); return; }
            AssertPortFree(13384);
            string ini = Path.GetFullPath(".tools/local-mysql/my.ini");
            if (!File.Exists(ini)) throw new InvalidOperationException("尚未配置 Norn 本地 MySQL；请按基础指南初始化，不会覆盖旧库。");
            SaveStarted(new ProcessStartInfo { FileName = Path.GetFullPath(".tools/mysql/mysql-8.4.13-winx64/bin/mysqld.exe"),
                // 禁用 Windows monitor，避免把 launcher 的退出误当成实际数据库已停止。
                Arguments = "\"--defaults-file=" + ini + "\" --no-monitor", UseShellExecute = false, CreateNoWindow = true }, DatabaseState);
            GameLog.Info("已请求启动 Norn MySQL，端口 13384；业务 /health/ready 会实际执行认证查询。", "OnlineServices");
        }
        [MenuItem("Tools/Norn/Online/Stop Local Database")]
        public static void StopDatabase()
        {
            using var process = FindRecorded(DatabaseState);
            if (process == null)
            {
                AssertPortFree(13384); // 记录过期但端口仍有服务时必须报错，不能宣布停止成功。
                GameLog.Info("Norn 数据库当前未运行。", "OnlineServices"); return;
            }
            // 不强杀 InnoDB；专用控制账号只有 SHUTDOWN，不是 Runtime 或全局 root。
            var admin = Process.Start(new ProcessStartInfo { FileName = Path.GetFullPath(".tools/mysql/mysql-8.4.13-winx64/bin/mysqladmin.exe"),
                Arguments = "\"--defaults-extra-file=" + Path.GetFullPath(".tools/local-mysql/control.private.ini") + "\" shutdown", UseShellExecute = false, CreateNoWindow = true });
            if (admin == null) throw new InvalidOperationException("无法发起数据库关闭请求。");
            // Editor.update 只在这次离散操作期间轮询，不阻塞 Editor 主线程等待 InnoDB Flush。
            double deadline = EditorApplication.timeSinceStartup + 20;
            EditorApplication.CallbackFunction poll = null;
            Process tracked = Process.GetProcessById(process.Id);
            long expectedStartTicks = process.StartTime.ToUniversalTime().Ticks;
            poll = () =>
            {
                bool done = admin.HasExited;
                if (!done && EditorApplication.timeSinceStartup < deadline) return;
                if (done && admin.ExitCode == 0 && !tracked.HasExited && EditorApplication.timeSinceStartup < deadline) return;
                EditorApplication.update -= poll;
                if (done && admin.ExitCode == 0 && tracked.HasExited)
                {
                    // 旧实例退出到本回调之间，用户可能已启动新实例；不能删掉新PID的记录。
                    if (File.Exists(DatabaseState))
                    {
                        var record = JsonUtility.FromJson<ProcessRecord>(File.ReadAllText(DatabaseState));
                        if (record.ProcessId == tracked.Id && record.StartTicks == expectedStartTicks)
                            File.Delete(DatabaseState);
                    }
                    GameLog.Info("Norn MySQL 已完成认证 SHUTDOWN，实际 Server 进程已退出。", "OnlineServices");
                }
                else GameLog.Error("Norn MySQL 关闭未通过确认，保留进程记录；请检查私有实例日志。", "OnlineServices");
                tracked.Dispose(); admin.Dispose();
            };
            EditorApplication.update += poll;
            GameLog.Info("正在等待数据库认证 SHUTDOWN 和实际进程退出。", "OnlineServices");
        }
        private static void AssertPortFree(int port)
        {
            // 正常关闭后连接会留在 TIME_WAIT，临时 Exclusive TcpListener 会误判端口仍被服务占用。
            // 只查真实 LISTEN；创建进程后的最终 bind/Ready 仍是裁决，不用 ReuseAddress 探针抢现有连接。
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (endpoint.Port == port) throw new InvalidOperationException("本机端口仍有监听服务：" + port);
        }
        private static Process FindRecorded(string path)
        {
            if (!File.Exists(path)) return null;
            var record = JsonUtility.FromJson<ProcessRecord>(File.ReadAllText(path));
            Process process;
            try { process = Process.GetProcessById(record.ProcessId); } catch (ArgumentException) { return null; }
            if (process.StartTime.ToUniversalTime().Ticks != record.StartTicks || !string.Equals(Path.GetFullPath(process.MainModule.FileName).Replace('/', '\\'), Path.GetFullPath(record.Executable).Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
            { process.Dispose(); throw new InvalidOperationException("服务 PID 身份变化，拒绝操作。"); }
            return process;
        }
        private static void SaveStarted(ProcessStartInfo start, string state)
        {
            using var process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("无法创建本机服务进程。");
            Directory.CreateDirectory(Path.GetDirectoryName(state));
            File.WriteAllText(state, JsonUtility.ToJson(new ProcessRecord { ProcessId = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks, Executable = Path.GetFullPath(start.FileName).Replace('/', '\\') }, true));
        }
    }
}
