using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Rooms;
/// <summary>数据库唯一实例声明决定谁可Start；启动发生在预留Transaction提交之后。</summary>
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
        if (!grant.IsNew) return; // 重试只能等待已有启动者，不因PID还没写入就另起进程。
        Process? process = null;
        try
        {
            string folder = Path.Combine(Path.GetFullPath(options.Instances.WorkRoot), grant.Descriptor.InstanceId); Directory.CreateDirectory(folder);
            string descriptor = Path.Combine(folder, "launch.json"); await File.WriteAllTextAsync(descriptor, JsonSerializer.Serialize(grant.Descriptor, ContentManifestValidator.Json), ct);
            process = Process.Start(CreateStartInfo(Path.GetFullPath(options.Instances.BuildPath), descriptor, Path.Combine(folder, "server.log"), grant.Secret!)) ?? throw new InvalidOperationException("Process.Start未返回子进程。");
            await instances.RecordProcessAsync(grant.Descriptor.InstanceId, InstanceProcesses.Describe(process), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or OnlineFailure or OperationCanceledException)
        {
            if (process != null) try { InstanceProcesses.StopMatching(InstanceProcesses.Describe(process)); } catch (InvalidOperationException) { }
            await instances.FailAsync(grant.Descriptor.InstanceId, "LaunchFailed", CancellationToken.None); throw new OnlineFailure(503, "LaunchFailed");
        }
        finally { process?.Dispose(); }
    }
    public static ProcessStartInfo CreateStartInfo(string executable, string descriptor, string log, string secret)
    {
        var start = new ProcessStartInfo { FileName = executable, WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false, CreateNoWindow = true };
        // ProcessStartInfo默认复制父环境；DS无需数据库/迁移凭据，必须在创建子进程前剔除。
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Online__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("NORN_N1_", StringComparison.OrdinalIgnoreCase) || k == "NORN_CONTENT_MANIFEST").ToArray()) start.Environment.Remove(key);
        foreach (string argument in new[] { "-batchmode", "-nographics", "-logFile", log, "--norn-instance", descriptor }) start.ArgumentList.Add(argument);
        start.Environment["NORN_INSTANCE_SECRET"] = secret; return start;
    }
    public async Task ResumeUnlaunchedAsync(CancellationToken ct)
    {
        await using var db = await database.OpenAsync(ct);
        // 覆盖Prepare提交后、Allocate之前HTTP取消/业务崩溃的缝隙。
        var runs = await db.QueryAsync<PendingRun>(new CommandDefinition("SELECT r.RunId,r.RoomId,r.RoomEpoch,r.StartRevision FROM Runs r WHERE r.State='Starting' AND NOT EXISTS(SELECT 1 FROM ServerInstances s WHERE s.RunId=r.RunId)", cancellationToken: ct));
        foreach (var run in runs) try { await StartReservedAsync(new RunReservation(run.RunId, run.RoomId, run.RoomEpoch, run.StartRevision), ct); } catch (OnlineFailure) { }
    }
    private sealed class PendingRun { public string RunId { get; set; } = ""; public string RoomId { get; set; } = ""; public uint RoomEpoch { get; set; } public uint StartRevision { get; set; } }
}
