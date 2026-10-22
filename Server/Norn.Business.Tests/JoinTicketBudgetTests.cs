using System.Diagnostics;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Rooms;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>长期实例也有有界存储；被删除的消费票仍拒绝，不靠保留无限历史证明防重放。</summary>
public sealed class JoinTicketBudgetTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task MintIsPersistentlyThrottled_AndExpiredBudgetCanRetry()
    {
        await using var f = new OnlineFactory(instances: true); var run = await RoomInstanceTests.Reserve(f);
        var (service, grant) = await Ready(f, run);
        try
        {
            _ = await service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default);
            var error = await Assert.ThrowsAsync<OnlineFailure>(() => service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default));
            Assert.Equal(429, error.Status); Assert.Equal("TicketRateLimited", error.Code);
            f.Clock.Advance(TimeSpan.FromSeconds(5)); _ = await service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default);
        }
        finally { await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default); } // RED也释放自己租约，不清其他测试数据。
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ConsumedTicketHistoryStaysOnePerMember_AndOldTicketStillRejected()
    {
        await using var f = new OnlineFactory(instances: true); var run = await RoomInstanceTests.Reserve(f); var (service, grant) = await Ready(f, run);
        try
        {
            string first = "";
            await using var db = new MySqlConnection(f.Connection);
            for (int i = 0; i < 10; i++)
            {
                var ticket = await service.MintTicketAsync(run.A.PlayerId, run.Run.RoomId, default); if (i == 0) first = ticket.Ticket;
                await service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, new ConsumeJoinTicketRequest { Ticket = ticket.Ticket, PlayerId = run.A.PlayerId, RoomEpoch = run.Run.RoomEpoch }, default);
                Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM JoinTickets WHERE InstanceId=@Id", new { Id = grant.Descriptor.InstanceId }));
                f.Clock.Advance(TimeSpan.FromSeconds(5));
            }
            var replay = await Assert.ThrowsAsync<OnlineFailure>(() => service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, new ConsumeJoinTicketRequest { Ticket = first, PlayerId = run.A.PlayerId, RoomEpoch = run.Run.RoomEpoch }, default));
            Assert.Equal("InvalidJoinTicket", replay.Code);
        }
        finally { await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default); }
    }

    private static async Task<(RoomInstanceService, InstanceLaunchGrant)> Ready(OnlineFactory f, RoomInstanceTests.RunData run)
    {
        var s = f.Services.GetRequiredService<RoomInstanceService>(); var g = await s.AllocateAsync(run.Run, default);
        using var p = Process.GetCurrentProcess(); var identity = new InstanceProcessIdentity(p.Id, p.StartTime.ToUniversalTime().Ticks, p.MainModule!.FileName!);
        await s.RecordProcessAsync(g.Descriptor.InstanceId, identity, default);
        await s.ReadyAsync(g.Descriptor.InstanceId, g.Secret!, new InstanceReadyRequest { ProcessId = identity.ProcessId, ProcessStartTicks = identity.StartTicks, RoomEpoch = run.Run.RoomEpoch,
            ProtocolVersion = f.Manifest.ProtocolVersion, BuildVersion = f.Manifest.BuildVersion, ManifestHash = f.Manifest.Hash }, default);
        return (s, g);
    }
}
