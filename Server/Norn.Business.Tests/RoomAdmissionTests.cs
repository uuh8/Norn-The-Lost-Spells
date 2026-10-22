using System.Net;
using System.Net.Http.Json;
using Game.NetworkProtocol;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>实际HTTP+MySQL验证席位与Revision；不通过客户端按钮是否变灰推断权威正确性。</summary>
public sealed class RoomAdmissionTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FifthPlayer_IsRejected()
    {
        await using var factory = new OnlineFactory(); var leader = await factory.NewPlayerAsync();
        var room = await Create(leader.Client);
        for (int i = 0; i < 3; i++)
        {
            var member = await factory.NewPlayerAsync(); using var joined = await Join(member.Client, room.RoomId);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }
        var fifth = await factory.NewPlayerAsync(); using var rejected = await Join(fifth.Client, room.RoomId);
        await Error(rejected, HttpStatusCode.Conflict, "RoomFull");
        Assert.Equal(4, (await View(leader.Client, room.RoomId)).Members.Length);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ConcurrentLastSeat_AdmitsOnlyOnePlayer()
    {
        await using var factory = new OnlineFactory(); var leader = await factory.NewPlayerAsync(); var room = await Create(leader.Client);
        for (int i = 0; i < 2; i++) { var player = await factory.NewPlayerAsync(); using var reply = await Join(player.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, reply.StatusCode); }
        var a = await factory.NewPlayerAsync(); var b = await factory.NewPlayerAsync();
        var replies = await Task.WhenAll(Join(a.Client, room.RoomId), Join(b.Client, room.RoomId));
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(replies, r => r.StatusCode == HttpStatusCode.Conflict);
        var final = await View(leader.Client, room.RoomId); Assert.Equal(4, final.Members.Length); Assert.Equal(4, final.Members.Select(m => m.Seat).Distinct().Count());
        foreach (var reply in replies) reply.Dispose();
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task CharacterCannotJoinTwoRooms_AndOutsiderCannotRead()
    {
        await using var factory = new OnlineFactory(); var a = await factory.NewPlayerAsync(); var b = await factory.NewPlayerAsync();
        var one = await Create(a.Client); var two = await Create(b.Client);
        using var duplicate = await Join(a.Client, two.RoomId); await Error(duplicate, HttpStatusCode.Conflict, "AlreadyInRoom");
        using var read = await b.Client.GetAsync("/api/online/rooms/" + one.RoomId); Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var repeated = await Join(a.Client, one.RoomId); Assert.Equal(HttpStatusCode.OK, repeated.StatusCode); // 同一房间重试不多占席位。
        Assert.Single((await View(a.Client, one.RoomId)).Members);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task MembershipChangeResetsReadiness_AndStaleRevisionCannotReady()
    {
        await using var factory = new OnlineFactory(); var a = await factory.NewPlayerAsync(); var b = await factory.NewPlayerAsync(); var room = await Create(a.Client);
        room = await Ready(a.Client, room, a.Profile.Version);
        using var joined = await Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var current = await View(a.Client, room.RoomId); Assert.All(current.Members, m => Assert.False(m.Ready));
        using var stale = await a.Client.PostAsJsonAsync($"/api/online/rooms/{room.RoomId}/ready", new RoomReadyRequest { ExpectedRevision = room.ReadyRevision, ExpectedInventoryVersion = a.Profile.Version, Ready = true }, OnlineFactory.Json);
        await Error(stale, HttpStatusCode.Conflict, "RoomRevisionConflict");
        Assert.Equal(current.ReadyRevision, (await View(a.Client, room.RoomId)).ReadyRevision);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task LeaderLeaveTransfersLeadership_AndRoomSurvivesBusinessRestart()
    {
        string id, memberName;
        await using (var factory = new OnlineFactory())
        {
            var a = await factory.NewPlayerAsync(); var b = await factory.NewPlayerAsync(); memberName = b.Username;
            var room = await Create(a.Client); id = room.RoomId; using var join = await Join(b.Client, id); Assert.Equal(HttpStatusCode.OK, join.StatusCode);
            using var left = await a.Client.DeleteAsync("/api/online/rooms/" + id + "/members/me"); Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
            Assert.Equal(b.Profile.PlayerId, (await View(b.Client, id)).LeaderPlayerId);
        }
        // 新建Host/DI容器读取同一MySQL，证明状态不依赖原Service的Dictionary。
        await using var restarted = new OnlineFactory(); using var client = restarted.CreateClient(); await restarted.LoginAsync(client, memberName);
        var restored = await View(client, id); Assert.Single(restored.Members); Assert.Equal(restored.Members[0].PlayerId, restored.LeaderPlayerId);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task InvalidDungeon_Unauthenticated_AndDisabledFeature_AreRejected()
    {
        await using var factory = new OnlineFactory(); using var anonymous = factory.CreateClient();
        using var denied = await anonymous.PostAsJsonAsync("/api/online/rooms", new CreateRoomRequest { DungeonId = "p7" }, OnlineFactory.Json); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var player = await factory.NewPlayerAsync(); using var invalid = await player.Client.PostAsJsonAsync("/api/online/rooms", new CreateRoomRequest { DungeonId = "../../arbitrary-scene" }, OnlineFactory.Json);
        await Error(invalid, HttpStatusCode.BadRequest, "UnknownDungeon");
        await using var disabled = new OnlineFactory(rooms: false); var other = await disabled.NewPlayerAsync();
        using var absent = await other.Client.PostAsJsonAsync("/api/online/rooms", new CreateRoomRequest { DungeonId = "p7" }, OnlineFactory.Json); Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    internal static async Task<RoomReply> Create(HttpClient client)
    {
        using var reply = await client.PostAsJsonAsync("/api/online/rooms", new CreateRoomRequest { DungeonId = "p7" }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode); return (await reply.Content.ReadFromJsonAsync<RoomReply>(OnlineFactory.Json))!;
    }
    internal static Task<HttpResponseMessage> Join(HttpClient client, string id) => client.PostAsJsonAsync($"/api/online/rooms/{id}/members", new { }, OnlineFactory.Json);
    internal static async Task<RoomReply> View(HttpClient client, string id) => (await client.GetFromJsonAsync<RoomReply>("/api/online/rooms/" + id, OnlineFactory.Json))!;
    internal static async Task<RoomReply> Ready(HttpClient client, RoomReply room, uint inventoryVersion, bool value = true)
    {
        using var reply = await client.PostAsJsonAsync($"/api/online/rooms/{room.RoomId}/ready", new RoomReadyRequest { ExpectedRevision = room.ReadyRevision, ExpectedInventoryVersion = inventoryVersion, Ready = value }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode); return (await reply.Content.ReadFromJsonAsync<RoomReply>(OnlineFactory.Json))!;
    }
    internal static async Task Error(HttpResponseMessage reply, HttpStatusCode status, string code)
    { Assert.Equal(status, reply.StatusCode); Assert.Equal(code, (await reply.Content.ReadFromJsonAsync<OnlineErrorReply>(OnlineFactory.Json))!.Code); }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task LostCreateResponse_CanRecoverCurrentRoom_AndBadManifestCannotMutate()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync(); var room = await Create(player.Client);
        using var duplicate = await player.Client.PostAsJsonAsync("/api/online/rooms", new CreateRoomRequest { DungeonId = "p7" }, OnlineFactory.Json);
        await Error(duplicate, HttpStatusCode.Conflict, "AlreadyInRoom");
        using var restored = await player.Client.GetAsync("/api/online/rooms/current"); Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(room.RoomId, (await restored.Content.ReadFromJsonAsync<RoomReply>(OnlineFactory.Json))!.RoomId);
        player.Client.DefaultRequestHeaders.Remove("X-Norn-Manifest"); player.Client.DefaultRequestHeaders.Add("X-Norn-Manifest", new string('0', 64));
        using var denied = await player.Client.DeleteAsync($"/api/online/rooms/{room.RoomId}/members/me"); await Error(denied, HttpStatusCode.Conflict, "ContentVersionMismatch");
        player.Client.DefaultRequestHeaders.Remove("X-Norn-Manifest"); player.Client.DefaultRequestHeaders.Add("X-Norn-Manifest", f.Manifest.Hash);
        Assert.Single((await View(player.Client, room.RoomId)).Members);
    }
}
