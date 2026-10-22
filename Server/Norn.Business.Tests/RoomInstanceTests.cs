using System.Diagnostics;
using System.Net;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>真实MySQL控制面契约；本类用实际测试Host的PID表示受信控制调用，不声称它是Unity进程。</summary>
public sealed class RoomInstanceTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task DuplicateAllocation_OnlyOnePort_AndTicketRequiresReady()
    {
        await using var f = new OnlineFactory(instances: true); var run = await Reserve(f); var service = f.Services.GetRequiredService<RoomInstanceService>();
        var a = await service.AllocateAsync(run.Run, default); var b = await service.AllocateAsync(run.Run, default);
        Assert.True(a.IsNew); Assert.False(b.IsNew); Assert.Null(b.Secret); Assert.Equal(a.Descriptor.InstanceId, b.Descriptor.InstanceId);
        await Failure("InstanceNotReady", () => service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default));
        Assert.True(await service.FailAsync(a.Descriptor.InstanceId, "LaunchFailed", default));
        Assert.Equal(0, await Leases(f, run.Run.RunId));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ReadyRequiresSecret_ProcessIdentity_AndExactBuildManifest()
    {
        await using var f = new OnlineFactory(instances: true); var run = await Reserve(f); var service = f.Services.GetRequiredService<RoomInstanceService>();
        var grant = await service.AllocateAsync(run.Run, default); var process = Identity(); await service.RecordProcessAsync(grant.Descriptor.InstanceId, process, default);
        var request = Ready(f, run, process); await Failure("InvalidInstanceCredential", () => service.ReadyAsync(grant.Descriptor.InstanceId, new string('0', 64), request, default));
        request.BuildVersion += "bad"; await Failure("ContentVersionMismatch", () => service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default));
        request = Ready(f, run, process); request.ProcessStartTicks++; await Failure("InstanceProcessMismatch", () => service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default));
        var reply = await service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, Ready(f, run, process), default);
        Assert.Equal(2, reply.Members.Length); Assert.Equal(2, reply.Members.Select(m => m.ActorId).Distinct().Count());
        Assert.Contains(reply.Members, m => m.Profile.PlayerId == run.A.PlayerId && m.Profile.Items.Length == 2);
        await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task JoinTicket_CannotBeReplayedForAnotherActor()
    {
        await using var f = new OnlineFactory(instances: true); var run = await Reserve(f); var (service, grant) = await MakeReady(f, run);
        var ticket = await service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default);
        var request = new ConsumeJoinTicketRequest { Ticket = ticket.Ticket, PlayerId = run.B.PlayerId, RoomEpoch = run.Run.RoomEpoch };
        await Failure("InvalidJoinTicket", () => service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default));
        request.PlayerId = run.A.PlayerId; var first = await service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default);
        Assert.Equal(run.A.PlayerId, first.PlayerId); Assert.Equal(1u, first.ConnectionEpoch);
        await Failure("InvalidJoinTicket", () => service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default));
        f.Clock.Advance(TimeSpan.FromSeconds(5)); // 新票有持久化最短间隔，不让重连刷票绕过数据库预算。
        var second = await service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default); request.Ticket = second.Ticket;
        var rejoin = await service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default);
        Assert.Equal(first.ActorId, rejoin.ActorId); Assert.Equal(2u, rejoin.ConnectionEpoch);
        await using var db = new MySqlConnection(f.Connection);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM JoinTickets WHERE TicketHash=@Ticket", new { Ticket = second.Ticket })); // DB只存摘要。
        await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExpiredTicket_AndConcurrentConsume_ProduceAtMostOneAdmission()
    {
        await using var f = new OnlineFactory(instances: true); var run = await Reserve(f); var (service, grant) = await MakeReady(f, run);
        var ticket = await service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default); f.Clock.Advance(TimeSpan.FromSeconds(61));
        var request = new ConsumeJoinTicketRequest { Ticket = ticket.Ticket, PlayerId = run.A.PlayerId, RoomEpoch = run.Run.RoomEpoch };
        await Failure("InvalidJoinTicket", () => service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default));
        request.Ticket = (await service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default)).Ticket;
        async Task<bool> Consume() { try { await service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default); return true; } catch (OnlineFailure e) { Assert.Equal("InvalidJoinTicket", e.Code); return false; } }
        var result = await Task.WhenAll(Consume(), Consume()); Assert.Single(result, v => v);
        await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task OldServerEpoch_CannotRenewOrAdmit_AndLeaseExpiryReleasesAssets()
    {
        await using var f = new OnlineFactory(instances: true); var run = await Reserve(f); var (service, grant) = await MakeReady(f, run);
        var ticket = await service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default);
        await Failure("InstanceEpochExpired", () => service.HeartbeatAsync(grant.Descriptor.InstanceId, grant.Secret!, run.Run.RoomEpoch + 1, default));
        f.Clock.Advance(TimeSpan.FromSeconds(121)); Assert.Equal(1, await service.ExpireDueAsync(default)); Assert.Equal(0, await Leases(f, run.Run.RunId));
        await Failure("InstanceEpochExpired", () => service.HeartbeatAsync(grant.Descriptor.InstanceId, grant.Secret!, run.Run.RoomEpoch, default));
        await Failure("InstanceEpochExpired", () => service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, new ConsumeJoinTicketRequest { Ticket = ticket.Ticket, PlayerId = run.A.PlayerId, RoomEpoch = run.Run.RoomEpoch }, default));
        var room = await RoomAdmissionTests.View(run.ClientA, run.Run.RoomId); Assert.Equal("Open", room.State); Assert.True(room.RoomEpoch > run.Run.RoomEpoch);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ReadyTimeout45Seconds_ReleasesOnlyThatRun()
    {
        await using var f = new OnlineFactory(instances: true); var run = await Reserve(f); var service = f.Services.GetRequiredService<RoomInstanceService>(); var grant = await service.AllocateAsync(run.Run, default);
        f.Clock.Advance(TimeSpan.FromSeconds(44)); Assert.Equal(0, await service.ExpireDueAsync(default)); Assert.Equal(2, await Leases(f, run.Run.RunId));
        f.Clock.Advance(TimeSpan.FromSeconds(2)); Assert.Equal(1, await service.ExpireDueAsync(default)); Assert.Equal(0, await Leases(f, run.Run.RunId));
        Assert.False(await service.FailAsync(grant.Descriptor.InstanceId, "LaunchFailed", default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task NewBusinessHost_RecognizesSameReadyInstance_AndRenews()
    {
        InstanceLaunchGrant grant; RunData run;
        await using (var f = new OnlineFactory(instances: true)) { run = await Reserve(f); (_, grant) = await MakeReady(f, run); }
        await using var restarted = new OnlineFactory(instances: true); _ = restarted.CreateClient();
        var service = restarted.Services.GetRequiredService<RoomInstanceService>();
        var lease = await service.HeartbeatAsync(grant.Descriptor.InstanceId, grant.Secret!, run.Run.RoomEpoch, default);
        Assert.True(lease.LeaseExpiresUnixMilliseconds > restarted.Clock.GetUtcNow().ToUnixTimeMilliseconds()); Assert.Equal(2, await Leases(restarted, run.Run.RunId));
        await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default);
    }

    private static InstanceProcessIdentity Identity()
    { using var p = Process.GetCurrentProcess(); return new InstanceProcessIdentity(p.Id, p.StartTime.ToUniversalTime().Ticks, p.MainModule!.FileName!); }
    private static InstanceReadyRequest Ready(OnlineFactory f, RunData run, InstanceProcessIdentity p) => new() { RoomEpoch = run.Run.RoomEpoch, ProtocolVersion = f.Manifest.ProtocolVersion, BuildVersion = f.Manifest.BuildVersion, ManifestHash = f.Manifest.Hash, ProcessId = p.ProcessId, ProcessStartTicks = p.StartTicks };
    private static async Task<(RoomInstanceService, InstanceLaunchGrant)> MakeReady(OnlineFactory f, RunData run)
    {
        var service = f.Services.GetRequiredService<RoomInstanceService>(); var grant = await service.AllocateAsync(run.Run, default); var p = Identity();
        await service.RecordProcessAsync(grant.Descriptor.InstanceId, p, default); await service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, Ready(f, run, p), default); return (service, grant);
    }
    internal static async Task<RunData> Reserve(OnlineFactory f)
    {
        var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync(); var room = await RoomAdmissionTests.Create(a.Client);
        using var join = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId); room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version); room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        return new RunData(await f.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default), a.Profile, b.Profile, a.Client);
    }
    private static async Task<int> Leases(OnlineFactory f, string run) { await using var db = new MySqlConnection(f.Connection); return await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Run", new { Run = run }); }
    private static async Task Failure<T>(string code, Func<Task<T>> operation) { var error = await Assert.ThrowsAsync<OnlineFailure>(async () => await operation()); Assert.Equal(code, error.Code); }
    internal sealed record RunData(RunReservation Run, OnlineProfileReply A, OnlineProfileReply B, HttpClient ClientA);
}
