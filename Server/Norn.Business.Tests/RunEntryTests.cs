using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>Prepare只预留资产，尚未启动DS；直接调用内部Service，避免为测试暴露危险的Client解锁/伪启动API。</summary>
public sealed class RunEntryTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task StartRequiresTwoReadyMembers_CurrentRevision_AndLeader()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var room = await RoomAdmissionTests.Create(a.Client);
        room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version); var entries = f.Services.GetRequiredService<RunEntryService>();
        await Failure("NotEnoughPlayers", () => entries.PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        var b = await f.NewPlayerAsync(); using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        await Failure("LeaderRequired", () => entries.PrepareAsync(b.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        await Failure("MembersNotReady", () => entries.PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        uint stale = room.ReadyRevision;
        room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version); room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        await Failure("RoomRevisionConflict", () => entries.PrepareAsync(a.Profile.PlayerId, room.RoomId, stale, default));
        Assert.Equal(0, await Count(f, "SELECT COUNT(*) FROM Runs WHERE RoomId=@Id", room.RoomId));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ConcurrentStart_OnlyOneRunAndTwoImmutableSnapshots()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f); var entries = f.Services.GetRequiredService<RunEntryService>();
        var results = await Task.WhenAll(entries.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default), entries.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default));
        Assert.Equal(results[0], results[1]); Assert.Equal(1u, results[0].RoomEpoch);
        Assert.Equal(1, await Count(f, "SELECT COUNT(*) FROM Runs WHERE RoomId=@Id", p.Room.RoomId));
        Assert.Equal(2, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Id", results[0].RunId));
        await using var db = new MySqlConnection(f.Connection);
        var json = (await db.QueryAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Id", new { Id = results[0].RunId })).ToArray(); Assert.Equal(2, json.Length);
        var snapshots = json.Select(j => JsonSerializer.Deserialize<OnlineProfileReply>(j, OnlineFactory.Json)!).ToArray();
        Assert.All(snapshots, s => { Assert.Equal(1u, s.Version); Assert.Equal(0, s.Gold); Assert.Equal(2, s.Items.Length); });
        Assert.Equal(p.A.Items.Select(i => i.InstanceId).Order(), snapshots.Single(s => s.PlayerId == p.A.PlayerId).Items.Select(i => i.InstanceId).Order());
        using var leave = await p.ClientA.DeleteAsync($"/api/online/rooms/{p.Room.RoomId}/members/me"); await RoomAdmissionTests.Error(leave, HttpStatusCode.Conflict, "RoomNotOpen");
        Assert.True(await entries.FailStartAsync(results[0].RunId, results[0].RoomEpoch, "Cancelled", default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task LeasedInventoryRejectsNewMoves_ButReturnsAlreadyCommittedReceipt()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f);
        var old = OnlineFactory.Move(p.A); using var moved = await p.ClientA.PostAsJsonAsync("/api/online/inventory/move", old, OnlineFactory.Json); Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        p.Room = await RoomAdmissionTests.Ready(p.ClientA, p.Room, 2);
        var run = await f.Services.GetRequiredService<RunEntryService>().PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default);
        using var retry = await p.ClientA.PostAsJsonAsync("/api/online/inventory/move", old, OnlineFactory.Json); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var next = OnlineFactory.Move(p.A, 1, 1); next.ExpectedVersion = 2;
        using var denied = await p.ClientA.PostAsJsonAsync("/api/online/inventory/move", next, OnlineFactory.Json); await RoomAdmissionTests.Error(denied, HttpStatusCode.Conflict, "AssetsLeased");
        Assert.Equal(2, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Id", run.RunId));
        Assert.True(await f.Services.GetRequiredService<RunEntryService>().FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task AssetChangedAfterReady_DoesNotReserveAnyMember()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f);
        using var moved = await p.ClientB.PostAsJsonAsync("/api/online/inventory/move", OnlineFactory.Move(p.B), OnlineFactory.Json); Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        await Failure("ProfileVersionConflict", () => f.Services.GetRequiredService<RunEntryService>().PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default));
        Assert.Equal(0, await Count(f, "SELECT COUNT(*) FROM Runs WHERE RoomId=@Id", p.Room.RoomId));
        Assert.Equal(0, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Id", p.A.PlayerId));
        Assert.Equal("Open", (await RoomAdmissionTests.View(p.ClientA, p.Room.RoomId)).State);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task StartFailure_ReleasesOnlyMatchingEpochLease()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f); var entries = f.Services.GetRequiredService<RunEntryService>();
        var first = await entries.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default);
        Assert.False(await entries.FailStartAsync(first.RunId, first.RoomEpoch + 1, "LaunchFailed", default));
        Assert.Equal(2, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Id", first.RunId));
        Assert.True(await entries.FailStartAsync(first.RunId, first.RoomEpoch, "LaunchFailed", default));
        p.Room = await RoomAdmissionTests.View(p.ClientA, p.Room.RoomId); Assert.Equal("Open", p.Room.State); Assert.All(p.Room.Members, m => Assert.False(m.Ready));
        p.Room = await RoomAdmissionTests.Ready(p.ClientA, p.Room, p.A.Version); p.Room = await RoomAdmissionTests.Ready(p.ClientB, p.Room, p.B.Version);
        var second = await entries.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default); Assert.True(second.RoomEpoch > first.RoomEpoch); Assert.NotEqual(first.RunId, second.RunId);
        Assert.False(await entries.FailStartAsync(first.RunId, first.RoomEpoch, "LaunchFailed", default)); // 迟到的旧启动器不能解锁新Run。
        Assert.Equal(2, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Id", second.RunId));
        Assert.True(await entries.FailStartAsync(second.RunId, second.RoomEpoch, "ReadyTimeout", default));
        Assert.Equal(0, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Id", second.RunId));
        var restored = (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(p.A.Version, restored.Version); Assert.Equal(p.A.Items.Select(i => i.InstanceId), restored.Items.Select(i => i.InstanceId));
        using var usable = await p.ClientA.PostAsJsonAsync("/api/online/inventory/move", OnlineFactory.Move(restored), OnlineFactory.Json); Assert.Equal(HttpStatusCode.OK, usable.StatusCode);
        Assert.Equal(2, await Count(f, "SELECT COUNT(*) FROM RunLifecycleEvents WHERE RunId=@Id", first.RunId)); // Reserved + 1次失败，重试不重复审计。
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ReservationSurvivesHostRestart_AndRetryDoesNotDuplicate()
    {
        string username; RunReservation run; string roomId; uint revision; string player;
        await using (var f = new OnlineFactory())
        {
            var p = await Pair(f); roomId = p.Room.RoomId; revision = p.Room.ReadyRevision; player = p.A.PlayerId; username = p.UsernameA;
            run = await f.Services.GetRequiredService<RunEntryService>().PrepareAsync(player, roomId, revision, default);
        }
        await using var restarted = new OnlineFactory(); using var client = restarted.CreateClient(); await restarted.LoginAsync(client, username);
        Assert.Equal("Starting", (await RoomAdmissionTests.View(client, roomId)).State);
        Assert.Equal(run, await restarted.Services.GetRequiredService<RunEntryService>().PrepareAsync(player, roomId, revision, default));
        Assert.Equal(2, await Count(restarted, "SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Id", run.RunId));
        Assert.True(await restarted.Services.GetRequiredService<RunEntryService>().FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ConcurrentInventoryMoveAndStart_NeverSnapshotWrongVersion()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f); var entries = f.Services.GetRequiredService<RunEntryService>();
        async Task<RunReservation?> Start() { try { return await entries.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default); } catch (OnlineFailure failure) { Assert.Equal("ProfileVersionConflict", failure.Code); return null; } }
        var start = Start(); var move = p.ClientA.PostAsJsonAsync("/api/online/inventory/move", OnlineFactory.Move(p.A), OnlineFactory.Json);
        await Task.WhenAll(start, move); using var moved = await move;
        if (await start is { } run)
        {
            await RoomAdmissionTests.Error(moved, HttpStatusCode.Conflict, "AssetsLeased");
            Assert.Equal(2, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Id", run.RunId));
            Assert.Equal(1u, (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!.Version);
            Assert.True(await entries.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default));
        }
        else { Assert.Equal(HttpStatusCode.OK, moved.StatusCode); Assert.Equal(0, await Count(f, "SELECT COUNT(*) FROM Runs WHERE RoomId=@Id", p.Room.RoomId)); }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task InvalidSecondSnapshot_RollsBackWholeReservation()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f);
        await using var db = new MySqlConnection(f.Connection);
        // Prepare按CharacterId排序；故意让后处理的成员失败，确认前一成员插入的快照/Lease也一起回滚。
        string second = StringComparer.Ordinal.Compare(p.A.PlayerId, p.B.PlayerId) > 0 ? p.A.PlayerId : p.B.PlayerId;
        await db.ExecuteAsync("UPDATE Items SET ContentId='not-a-real-content-id' WHERE OwnerCharacterId=@Id", new { Id = second });
        await Failure("UnknownContent", () => f.Services.GetRequiredService<RunEntryService>().PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default));
        Assert.Equal(0, await Count(f, "SELECT COUNT(*) FROM Runs WHERE RoomId=@Id", p.Room.RoomId));
        Assert.Equal(0, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Id", p.A.PlayerId));
        Assert.Equal("Open", (await RoomAdmissionTests.View(p.ClientA, p.Room.RoomId)).State);
    }

    private static async Task Failure(string code, Func<Task<RunReservation>> operation) { var failure = await Assert.ThrowsAsync<OnlineFailure>(operation); Assert.Equal(code, failure.Code); }
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FourReadyMembers_ReserveExactlyFourSnapshotsAndLeases()
    {
        await using var f = new OnlineFactory();
        var players = new List<(HttpClient Client, OnlineProfileReply Profile)>();
        for (int i = 0; i < 4; i++) { var player = await f.NewPlayerAsync(); players.Add((player.Client, player.Profile)); }
        var room = await RoomAdmissionTests.Create(players[0].Client);
        for (int i = 1; i < 4; i++) { using var join = await RoomAdmissionTests.Join(players[i].Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, join.StatusCode); }
        room = await RoomAdmissionTests.View(players[0].Client, room.RoomId);
        foreach (var player in players) room = await RoomAdmissionTests.Ready(player.Client, room, player.Profile.Version);
        var entries = f.Services.GetRequiredService<RunEntryService>();
        var run = await entries.PrepareAsync(players[0].Profile.PlayerId, room.RoomId, room.ReadyRevision, default);
        Assert.Equal(4, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Id", run.RunId));
        Assert.Equal(4, await Count(f, "SELECT COUNT(*) FROM RunMembers WHERE RunId=@Id", run.RunId));
        Assert.True(await entries.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default));
        Assert.Equal(0, await Count(f, "SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Id", run.RunId));
    }
    private static async Task<int> Count(OnlineFactory f, string sql, string id) { await using var db = new MySqlConnection(f.Connection); return await db.ExecuteScalarAsync<int>(sql, new { Id = id }); }
    private static async Task<PairData> Pair(OnlineFactory f)
    {
        var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync(); var room = await RoomAdmissionTests.Create(a.Client);
        using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode); room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version); room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        return new PairData { ClientA = a.Client, ClientB = b.Client, A = a.Profile, B = b.Profile, UsernameA = a.Username, Room = room };
    }
    private sealed class PairData
    { public HttpClient ClientA = null!, ClientB = null!; public OnlineProfileReply A = null!, B = null!; public string UsernameA = ""; public RoomReply Room = null!; }
}
