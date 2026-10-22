using System.Diagnostics;

namespace Norn.Business.Features.Rooms;

/// <summary>PID会复用，停止/重接管必须同时核对启动Ticks与路径；绝不按进程名搜索结束。</summary>
public static class InstanceProcesses
{
    public static InstanceProcessIdentity Describe(Process process) => new(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(process.MainModule!.FileName!));
    public static bool Matches(InstanceProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartTicks &&
                string.Equals(Path.GetFullPath(process.MainModule!.FileName!), Path.GetFullPath(identity.Executable), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }
    public static void StopMatching(InstanceProcessIdentity identity)
    {
        // 管理器永远不能杀自己的业务进程，数据库测试登记当前Host身份时同样受保护。
        if (identity.ProcessId == Environment.ProcessId) return;
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            _ = process.Handle; // 持有同一OS进程对象后再核对/终止，避免核对与重新Open PID之间的复用窗口。
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartTicks &&
                string.Equals(Path.GetFullPath(process.MainModule!.FileName!), Path.GetFullPath(identity.Executable), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) process.Kill();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* 已退出/权限不足时不扩大搜索范围。 */ }
    }
}
