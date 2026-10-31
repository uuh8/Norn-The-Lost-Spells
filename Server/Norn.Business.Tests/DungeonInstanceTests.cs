using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>真实数据库路径验证 Dungeon 由不可变 Party 派生，重试和重连不能换成另一张地图。</summary>
public sealed class DungeonInstanceTests
{
    [Theory, Trait("Category", "DatabaseIntegration")]
    [InlineData("p7")]
    [InlineData("p8")]
    public async Task AllocationReadyAndReentryCarrySameAuthoritativeDungeon(string dungeon)
    {
        await using var factory = new OnlineFactory(instances: true);
        var run = await Reserve(factory, dungeon);
        var service = factory.Services.GetRequiredService<RoomInstanceService>();
        var grant = await service.AllocateAsync(run.Run, default);
        try
        {
            Assert.Equal(dungeon, grant.Descriptor.DungeonId);
            var retry = await service.AllocateAsync(run.Run, default);
            Assert.False(retry.IsNew); Assert.Null(retry.Secret);
            Assert.Equal(grant.Descriptor.InstanceId, retry.Descriptor.InstanceId);
            Assert.Equal(dungeon, retry.Descriptor.DungeonId);
            await MakeReady(factory, run, service, grant, dungeon);
            var ticket = await service.MintTicketAsync(run.Player.PlayerId, run.Run.RoomId, default);
            Assert.Equal(dungeon, ticket.DungeonId);
            var request = new ConsumeJoinTicketRequest { Ticket = ticket.Ticket, PlayerId = run.Player.PlayerId, RoomEpoch = run.Run.RoomEpoch };
            var first = await service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default);
            Assert.Equal(dungeon, first.DungeonId); Assert.Equal(1u, first.ConnectionEpoch);
            await Failure("InvalidJoinTicket", () => service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default));
            factory.Clock.Advance(TimeSpan.FromSeconds(5));
            request.Ticket = (await service.MintTicketAsync(run.Player.PlayerId, run.Run.RoomId, default)).Ticket;
            var second = await service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default);
            Assert.Equal(first.ActorId, second.ActorId); Assert.Equal(2u, second.ConnectionEpoch);
            Assert.Equal(dungeon, second.DungeonId);
        }
        finally { await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default); }
    }

    [Theory, Trait("Category", "DatabaseIntegration")]
    [InlineData("")]
    [InlineData("p8")]
    [InlineData("../../p7")]
    public async Task WrongReadyDungeonCannotMakeInstanceOrRunReady(string presentedDungeon)
    {
        await using var factory = new OnlineFactory(instances: true);
        var run = await Reserve(factory, "p7");
        var service = factory.Services.GetRequiredService<RoomInstanceService>();
        var grant = await service.AllocateAsync(run.Run, default);
        try
        {
            var identity = Identity(); await service.RecordProcessAsync(grant.Descriptor.InstanceId, identity, default);
            var request = Ready(factory, run.Run, identity, presentedDungeon);
            await Failure("DungeonMismatch", () => service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default));
            Assert.Equal("Launching", (await service.ViewAsync(run.Player.PlayerId, run.Run.RoomId, default)).State);
            Assert.Equal("Starting", (await RoomAdmissionTests.View(run.Client, run.Run.RoomId)).State);
            await Failure("InstanceNotReady", () => service.MintTicketAsync(run.Player.PlayerId, run.Run.RoomId, default));
            // 拒绝必须保持原事务状态，正确的 DS 随后仍可完成 Ready，而不是被错误回复污染。
            request.DungeonId = "p7";
            await service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default);
            Assert.Equal("Ready", (await service.ViewAsync(run.Player.PlayerId, run.Run.RoomId, default)).State);
        }
        finally { await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default); }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task RemovedDeploymentDungeonCannotAllocateAnInstance()
    {
        await using var factory = new OnlineFactory(instances: true);
        var run = await Reserve(factory, "p8");
        var service = factory.Services.GetRequiredService<RoomInstanceService>();
        try
        {
            // 模拟部署配置撤下 p8：即使旧 Party 的字符串格式合法，也不能启动未部署的地图。
            factory.Services.GetRequiredService<OnlineOptions>().DungeonIds = ["p7"];
            await Failure("UnknownDungeon", () => service.AllocateAsync(run.Run, default));
            Assert.DoesNotContain(await service.ActiveAsync(default), row => row.RunId == run.Run.RunId);
        }
        finally
        {
            await factory.Services.GetRequiredService<RunEntryService>().FailStartAsync(run.Run.RunId, run.Run.RoomEpoch, "Cancelled", default);
        }
    }

    private static async Task<(RunReservation Run, OnlineProfileReply Player, HttpClient Client)> Reserve(OnlineFactory factory, string dungeon)
    {
        var a = await factory.NewPlayerAsync(); var b = await factory.NewPlayerAsync();
        using var created = await a.Client.PostAsJsonAsync("/api/online/rooms", new CreateRoomRequest { DungeonId = dungeon }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = (await created.Content.ReadFromJsonAsync<RoomReply>(OnlineFactory.Json))!;
        using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version);
        room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        return (await factory.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default), a.Profile, a.Client);
    }

    private static async Task MakeReady(OnlineFactory factory, (RunReservation Run, OnlineProfileReply Player, HttpClient Client) run,
        RoomInstanceService service, InstanceLaunchGrant grant, string dungeon)
    {
        var identity = Identity(); await service.RecordProcessAsync(grant.Descriptor.InstanceId, identity, default);
        await service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, Ready(factory, run.Run, identity, dungeon), default);
    }

    private static InstanceReadyRequest Ready(OnlineFactory factory, RunReservation run, InstanceProcessIdentity identity, string dungeon) => new()
    {
        DungeonId = dungeon, RoomEpoch = run.RoomEpoch, ProtocolVersion = factory.Manifest.ProtocolVersion,
        BuildVersion = factory.Manifest.BuildVersion, ManifestHash = factory.Manifest.Hash, GameplayConfigHash = OnlineFactory.GameplayHash,
        ProcessId = identity.ProcessId, ProcessStartTicks = identity.StartTicks
    };
    private static InstanceProcessIdentity Identity()
    { using var process = Process.GetCurrentProcess(); return new InstanceProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!); }
    private static async Task Failure<T>(string code, Func<Task<T>> operation)
    { var failure = await Assert.ThrowsAsync<OnlineFailure>(async () => await operation()); Assert.Equal(code, failure.Code); }
}
