using System.Diagnostics;

namespace Norn.Business.Features.Rooms;

/// <summary>
/// PID 会复用，重接管/停止登记过的 DS 必须同时核对 PID、启动 Ticks 和实际模块路径。
/// Process.Start 返回并不保证主模块元数据已能枚举；暂不可读属于启动状态，不是空引用异常。
/// </summary>
public static class InstanceProcesses
{
    /// <summary>保留同步合同：调用者必须拿到完整真实身份；未就绪时抛明确的状态异常。</summary>
    public static InstanceProcessIdentity Describe(Process process) => TryDescribe(process, out var identity)
        ? identity : throw new InvalidOperationException("进程的实际身份元数据暂不可用或进程已经退出。");

    /// <summary>
    /// 新进程启动时最多读取 80 次、间隔 25 ms，给操作系统约 2 秒公开主模块元数据。
    /// 这只解决身份登记的短暂竞态；不是等待业务 Ready，也不能把配置的 BuildPath 充当实际路径。
    /// HTTP 取消会结束等待，由启动者在 catch 中回收其直接拥有的子进程。
    /// </summary>
    public static Task<InstanceProcessIdentity> DescribeAsync(Process process, CancellationToken ct) =>
        ReadIdentityWithRetryAsync(() => TryDescribe(process, out var identity) ? identity : null,
            maximumAttempts: 80, retryDelay: TimeSpan.FromMilliseconds(25), delay: Task.Delay, ct);

    /// <summary>
    /// 先持有 OS 进程句柄，再读取当前实际身份；同一个调用对象随后停止进程时仍使用该句柄，
    /// 避免先检查 PID、后按同一数值重新打开时碰到 PID 已被另一进程复用的窗口。
    /// Refresh 清理 Process 的托管元数据缓存，使启动期重试能重新枚举 MainModule；不会释放句柄。
    /// </summary>
    public static bool TryDescribe(Process process, out InstanceProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(process);
        identity = null!;
        try
        {
            _ = process.Handle;
            process.Refresh();
            if (process.HasExited) return false;
            int pid = process.Id;
            long ticks = process.StartTime.ToUniversalTime().Ticks;
            string? actualPath = process.MainModule?.FileName;
            // 进程可在枚举模块期间退出；不要将已退出进程的缓存身份登记为当前可运行对象。
            if (process.HasExited) return false;
            return TryCreateIdentity(pid, ticks, actualPath, out identity);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
        { return false; }
    }

    /// <summary>
    /// 纯边界只接受从 OS 读取的 actualPath；空值不会得到“看似完整”的替代路径。
    /// 内部可见是为了确定性验证 MainModule 为空的情况，业务调用者无需了解这个测试边界。
    /// </summary>
    internal static bool TryCreateIdentity(int pid, long startTicks, string? actualPath, out InstanceProcessIdentity identity)
    {
        identity = null!;
        if (pid <= 0 || startTicks <= 0 || string.IsNullOrWhiteSpace(actualPath)) return false;
        try
        {
            identity = new InstanceProcessIdentity(pid, startTicks, Path.GetFullPath(actualPath));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    /// <summary>
    /// 仅把“读不到”转成有界等待，不处理凭据、不创建进程、不修改数据库。
    /// 读取与延迟用小 delegate 隔离后，测试可精确复现 null/null/成功、耗尽、取消，
    /// 不必依赖真实机器启动速度，也无需为整个 Process 建立新的抽象层。
    /// </summary>
    internal static async Task<InstanceProcessIdentity> ReadIdentityWithRetryAsync(
        Func<InstanceProcessIdentity?> tryRead, int maximumAttempts, TimeSpan retryDelay,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tryRead); ArgumentNullException.ThrowIfNull(delay);
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var identity = tryRead();
            ct.ThrowIfCancellationRequested();
            if (identity != null) return identity;
            // 最后一轮失败直接结束，不多等待一个没有后续读取的空周期。
            if (attempt + 1 < maximumAttempts) await delay(retryDelay, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException("副本进程的实际身份元数据在限定启动窗口内仍不可用。");
    }

    public static bool Matches(InstanceProcessIdentity identity)
    {
        if (!TryCreateIdentity(identity.ProcessId, identity.StartTicks, identity.Executable, out var expected)) return false;
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            return TryDescribe(process, out var actual) && SameIdentity(actual, expected);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        { return false; }
    }

    public static void StopMatching(InstanceProcessIdentity identity)
    {
        // 管理器永远不能杀自己的业务进程，数据库测试登记当前 Host 身份时同样受保护。
        if (identity.ProcessId == Environment.ProcessId) return;
        if (!TryCreateIdentity(identity.ProcessId, identity.StartTicks, identity.Executable, out var expected)) return;
        try
        {
            using var process = Process.GetProcessById(expected.ProcessId);
            // TryDescribe 先持有此对象的句柄；核对和 Kill 始终使用同一对象，不另按 PID 重新打开。
            if (TryDescribe(process, out var actual) && SameIdentity(actual, expected)) process.Kill();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        { /* 已退出/元数据不可读/权限不足时不扩大搜索范围。 */ }
    }

    private static bool SameIdentity(InstanceProcessIdentity actual, InstanceProcessIdentity expected) =>
        actual.ProcessId == expected.ProcessId && actual.StartTicks == expected.StartTicks
        && string.Equals(actual.Executable, expected.Executable,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
