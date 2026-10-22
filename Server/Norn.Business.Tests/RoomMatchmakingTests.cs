using System.Net;
using System.Net.Http.Json;
using Game.NetworkProtocol;
using Xunit;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;

namespace Norn.Business.Tests;

/// <summary>从实际HTTP观察原子匹配/重试/大厅失联，不把本机UI按钮状态当数据库权威。</summary>
public sealed class RoomMatchmakingTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ConcurrentMatch_FourSeatsAndFifthNewRoom_RetryDoesNotDuplicate()
    {
        await using var f = new OnlineFactory();
        var players = new List<HttpClient>();
        for (int i = 0; i < 5; i++) players.Add((await f.NewPlayerAsync()).Client);
        var rooms = await Task.WhenAll(players.Select(c => Match(c)));
        Assert.Equal(2, rooms.Select(r => r.RoomId).Distinct().Count());
        var group = rooms.GroupBy(r => r.RoomId).OrderByDescending(g => g.Count()).First();
        Assert.Equal(4, group.Count());
        for (int i = 0; i < players.Count; i++)
        {
            Assert.Equal(rooms[i].RoomId, (await Match(players[i])).RoomId);
            using var left = await players[i].DeleteAsync($"/api/online/rooms/{rooms[i].RoomId}/members/me");
            Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task PresenceKeepsMember_StaleLeaderExpiresWithoutReleasingRunAssets()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var room = await Match(a.Client); Assert.Equal(room.RoomId, (await Match(b.Client)).RoomId);
        f.Clock.Advance(TimeSpan.FromSeconds(89));
        using var live = await b.Client.PostAsJsonAsync($"/api/online/rooms/{room.RoomId}/presence", new { }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        var c = await f.NewPlayerAsync(); Assert.Equal(room.RoomId, (await Match(c.Client)).RoomId);
        var current = await RoomAdmissionTests.View(b.Client, room.RoomId);
        Assert.Equal(b.Profile.PlayerId, current.LeaderPlayerId);
        Assert.DoesNotContain(current.Members, m => m.PlayerId == a.Profile.PlayerId);
        Assert.All(current.Members, m => Assert.False(m.Ready));
        foreach (var client in new[] { b.Client, c.Client })
        { using var left = await client.DeleteAsync($"/api/online/rooms/{room.RoomId}/members/me"); Assert.Equal(HttpStatusCode.NoContent, left.StatusCode); }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task MatchRejectsForeignDungeonAndAnonymous_AndRestoresCurrentRoom()
    {
        await using var f = new OnlineFactory(); using var anonymous = f.CreateClient();
        using var denied = await anonymous.PostAsJsonAsync("/api/online/rooms/match", new CreateRoomRequest { DungeonId = "p7" }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var a = await f.NewPlayerAsync(); var room = await Match(a.Client);
        using var wrong = await a.Client.PostAsJsonAsync("/api/online/rooms/match", new CreateRoomRequest { DungeonId = "p8" }, OnlineFactory.Json);
        await RoomAdmissionTests.Error(wrong, HttpStatusCode.Conflict, "DifferentDungeonRoom");
        // 同一人的当前房间优先恢复，不因重试在另一队列另占一席。
        Assert.Equal(room.RoomId, (await Match(a.Client)).RoomId);
        using var left = await a.Client.DeleteAsync($"/api/online/rooms/{room.RoomId}/members/me"); Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
    }

    internal static async Task<RoomReply> Match(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/online/rooms/match", new CreateRoomRequest { DungeonId = "p7" }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoomReply>(OnlineFactory.Json))!;
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task LobbyExpiry_DoesNotReleaseStartingRunOrItsMemberSeats()
    {
        await using var f = new OnlineFactory(instances: true); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var room = await Match(a.Client); _ = await Match(b.Client); room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version); room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        var entries = f.Services.GetRequiredService<RunEntryService>(); var run = await entries.PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default);
        try
        {
            f.Clock.Advance(TimeSpan.FromSeconds(91)); await f.Services.GetRequiredService<RoomMatchmakingService>().ExpireAsync(default);
            var current = await RoomAdmissionTests.View(a.Client, room.RoomId); Assert.Equal("Starting", current.State); Assert.Equal(2, current.Members.Length);
            await using var db = new MySqlConnection(f.Connection);
            Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Run", new { Run = run.RunId }));
        }
        finally { await entries.FailStartAsync(run.RunId, run.RoomEpoch, "LaunchFailed", default); }
        foreach (var client in new[] { a.Client, b.Client }) { using var left = await client.DeleteAsync($"/api/online/rooms/{room.RoomId}/members/me"); Assert.Equal(HttpStatusCode.NoContent, left.StatusCode); }
    }
}
