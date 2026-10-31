using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Rooms;
/// <summary>数据库唯一实例声明决定谁可 Start；启动发生在预留 Transaction 提交之后。</summary>
public sealed class RoomLauncher(RoomService rooms, RunEntryService entries, RoomInstanceService instances, OnlineOptions options, OnlineDatabase database)
{
    public async Task<RoomInstanceReply> StartRunAsync(string player, string room, uint revision, CancellationToken ct)
    {
        var view = await rooms.GetAsync(player, room, ct);
        if (view.LeaderPlayerId != player) throw new OnlineFailure(403, "LeaderRequired");
        if (view.ReadyRevision != revision) throw new OnlineFailure(409, "RoomRevisionConflict");
        if (view.State == "Running") return await instances.ViewAsync(player, room, ct);
        var run = await entries.PrepareAsync(player, room, revision, ct);
        await StartReservedAsync(run, ct); return await instances.ViewAsync(player, room, ct);
    }
    public async Task StartReservedAsync(RunReservation run, CancellationToken ct)
    {
        InstanceLaunchGrant grant;
        try
        {
            if (!options.Instances.Enabled) throw new OnlineFailure(503, "InstancesDisabled");
            if (!File.Exists(options.Instances.BuildPath)) throw new OnlineFailure(503, "BuildNotFound");
            grant = await instances.AllocateAsync(run, ct);
        }
        catch (OnlineFailure failure)
        {
            await entries.FailStartAsync(run.RunId, run.RoomEpoch, failure.Code is "BuildNotFound" or "PortUnavailable" ? failure.Code : "LaunchFailed", CancellationToken.None); throw;
        }
        if (!grant.IsNew) return; // 重试只能等待已有启动者，不因 PID 还没写入就另起进程。
        Process? process = null;
        InstanceProcessIdentity? identity = null;
        bool ownsProcessHandle = false;
        try
        {
            string folder = Path.Combine(Path.GetFullPath(options.Instances.WorkRoot), grant.Descriptor.InstanceId); Directory.CreateDirectory(folder);
            string descriptor = Path.Combine(folder, "launch.json"); await File.WriteAllTextAsync(descriptor, JsonSerializer.Serialize(grant.Descriptor, ContentManifestValidator.Json), ct);
            process = Process.Start(CreateStartInfo(Path.GetFullPath(options.Instances.BuildPath), descriptor, Path.Combine(folder, "server.log"), grant.Secret!)) ?? throw new InvalidOperationException("Process.Start未返回子进程。");
            // 从这一刻起启动者持有自己刚创建的 OS 子进程对象。MainModule 可能仍未就绪，
            // 但失败清理无需重新 Describe 或按 PID 搜索；这个句柄的所有权与 DB 重接管的三元身份不同。
            _ = process.Handle; ownsProcessHandle = true;
            identity = await InstanceProcesses.DescribeAsync(process, ct);
            await instances.RecordProcessAsync(grant.Descriptor.InstanceId, identity, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or OnlineFailure or OperationCanceledException or NotSupportedException)
        {
            if (process != null && ownsProcessHandle) StopOwnedChild(process);
            // 失败路径不使用已取消的 HTTP Token；即使调用者断开，也须释放本次预留的实例和资产。
            await instances.FailAsync(grant.Descriptor.InstanceId, "LaunchFailed", CancellationToken.None);
            throw new OnlineFailure(503, "LaunchFailed");
        }
        finally { process?.Dispose(); }
    }

    /// <summary>
    /// 仅供当前 StartReservedAsync 的失败分支调用，参数必须是 Process.Start 直接返回且已持有句柄的对象。
    /// 这不是通用按 PID 停服 API：不重新读取尚未就绪的 MainModule，不把 BuildPath 当作真实身份，
    /// 不枚举/重开其他进程；已登记实例的回收和 Host 重接管仍必须走 StopMatching 的三元核对。
    /// </summary>
    private static void StopOwnedChild(Process process)
    {
        try
        {
            if (process.Id != Environment.ProcessId && !process.HasExited) process.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { /* 自有子进程已退出或无法终止时仍继续 FailAsync，不扩大停止范围。 */ }
    }

    public static ProcessStartInfo CreateStartInfo(string executable, string descriptor, string log, string secret)
    {
        var start = new ProcessStartInfo { FileName = executable, WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false, CreateNoWindow = true };
        // ProcessStartInfo 默认复制父环境；DS 无需数据库/迁移凭据，必须在创建子进程前剔除。
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Online__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("NORN_N1_", StringComparison.OrdinalIgnoreCase) || k == "NORN_CONTENT_MANIFEST").ToArray()) start.Environment.Remove(key);
        foreach (string argument in new[] { "-batchmode", "-nographics", "-logFile", log, "--norn-instance", descriptor }) start.ArgumentList.Add(argument);
        start.Environment["NORN_INSTANCE_SECRET"] = secret; return start;
    }
    public async Task ResumeUnlaunchedAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        // 覆盖 Prepare 提交后、Allocate 之前 HTTP 取消/业务崩溃的缝隙。
        var runs = await db.QueryAsync<PendingRun>(new CommandDefinition("SELECT r.RunId,r.RoomId,r.RoomEpoch,r.StartRevision FROM Runs r WHERE r.State='Starting' AND NOT EXISTS(SELECT 1 FROM ServerInstances s WHERE s.RunId=r.RunId)", cancellationToken: ct));
        foreach (var run in runs) try { await StartReservedAsync(new RunReservation(run.RunId, run.RoomId, run.RoomEpoch, run.StartRevision), ct); } catch (OnlineFailure) { }
    }
    private sealed class PendingRun { public string RunId { get; set; } = ""; public string RoomId { get; set; } = ""; public uint RoomEpoch { get; set; } public uint StartRevision { get; set; } }
}
