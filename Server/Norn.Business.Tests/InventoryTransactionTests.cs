using System.Net;
using System.Net.Http.Json;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Xunit;

namespace Norn.Business.Tests;

public sealed class InventoryTransactionTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ConcurrentMove_OnlyOneVersionCommits()
    {
        await using var factory = new OnlineFactory(); var player = await factory.NewPlayerAsync();
        var a = OnlineFactory.Move(player.Profile, 0, 0); var b = OnlineFactory.Move(player.Profile, 1, 1);
        HttpResponseMessage[] replies = await Task.WhenAll(player.Client.PostAsJsonAsync("/api/online/inventory/move", a, OnlineFactory.Json), player.Client.PostAsJsonAsync("/api/online/inventory/move", b, OnlineFactory.Json));
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.Conflict);
        var profile = (await player.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(2u, profile.Version);
        Assert.Single(profile.Items, i => i.Area == "Warehouse");
        await using var connection = new MySqlConnection(factory.Connection);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM InventoryCommands WHERE CharacterId=@Id", new { Id = profile.PlayerId }));
        foreach (var reply in replies) reply.Dispose();
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task RepeatedCommand_ReturnsOriginalResult()
    {
        await using var factory = new OnlineFactory(); var player = await factory.NewPlayerAsync();
        var move = OnlineFactory.Move(player.Profile);
        using var first = await player.Client.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var repeat = await player.Client.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await repeat.Content.ReadAsStringAsync());
        var profile = (await player.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(2u, profile.Version); Assert.Equal(2u, profile.Items.Single(i => i.InstanceId == move.InstanceId).Revision);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ReusedCommandWithDifferentBody_AndStaleVersion_AreRejected()
    {
        await using var factory = new OnlineFactory(); var player = await factory.NewPlayerAsync();
        var move = OnlineFactory.Move(player.Profile);
        Assert.Equal(HttpStatusCode.OK, (await player.Client.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json)).StatusCode);
        move.Slot = 1;
        Assert.Equal(HttpStatusCode.Conflict, (await player.Client.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json)).StatusCode);
        move.CommandId = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Conflict, (await player.Client.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json)).StatusCode);
        Assert.Equal(2u, (await player.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!.Version);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task OtherPlayersItem_IsNeverWritable()
    {
        await using var factory = new OnlineFactory(); var a = await factory.NewPlayerAsync(); var b = await factory.NewPlayerAsync();
        var move = OnlineFactory.Move(a.Profile); move.InstanceId = b.Profile.Items[0].InstanceId;
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json)).StatusCode);
        Assert.Equal(1u, (await b.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!.Version);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task UnknownContent_IsRejectedWithoutChangingLocationOrVersion()
    {
        await using var factory = new OnlineFactory(); var player = await factory.NewPlayerAsync(); var move = OnlineFactory.Move(player.Profile);
        await using var connection = new MySqlConnection(factory.Connection); await connection.OpenAsync();
        await connection.ExecuteAsync("UPDATE Items SET ContentId='invalid.not_in_manifest' WHERE InstanceId=@Id", new { Id = move.InstanceId });
        Assert.Equal(HttpStatusCode.Conflict, (await player.Client.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json)).StatusCode);
        Assert.Equal(1u, await connection.ExecuteScalarAsync<uint>("SELECT Version FROM Characters WHERE CharacterId=@Id", new { Id = player.Profile.PlayerId }));
        Assert.Equal("Backpack", await connection.ExecuteScalarAsync<string>("SELECT Area FROM ItemLocations WHERE InstanceId=@Id", new { Id = move.InstanceId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SqlFailureAfterLocationUpdate_RollsBackAllChanges()
    {
        await using var factory = new OnlineFactory(); var player = await factory.NewPlayerAsync(); var move = OnlineFactory.Move(player.Profile);
        await using var connection = new MySqlConnection(factory.Connection); await connection.OpenAsync();
        // 用真实 UNSIGNED 溢出在第二步 UPDATE 触发 SQL 错误，检查此前的位置改动确实被回滚。
        await connection.ExecuteAsync("UPDATE Items SET Revision=4294967295 WHERE InstanceId=@Id", new { Id = move.InstanceId });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await player.Client.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json)).StatusCode);
        Assert.Equal("Backpack", await connection.ExecuteScalarAsync<string>("SELECT Area FROM ItemLocations WHERE InstanceId=@Id", new { Id = move.InstanceId }));
        Assert.Equal(1u, await connection.ExecuteScalarAsync<uint>("SELECT Version FROM Characters WHERE CharacterId=@Id", new { Id = player.Profile.PlayerId }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM InventoryCommands WHERE CharacterId=@Id", new { Id = player.Profile.PlayerId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task OccupiedSlot_RejectsWithoutConsumingVersion()
    {
        await using var factory = new OnlineFactory(); var player = await factory.NewPlayerAsync();
        Assert.Equal(HttpStatusCode.OK, (await player.Client.PostAsJsonAsync("/api/online/inventory/move", OnlineFactory.Move(player.Profile), OnlineFactory.Json)).StatusCode);
        var second = OnlineFactory.Move(player.Profile, 1); second.ExpectedVersion = 2;
        Assert.Equal(HttpStatusCode.Conflict, (await player.Client.PostAsJsonAsync("/api/online/inventory/move", second, OnlineFactory.Json)).StatusCode);
        var profile = (await player.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(2u, profile.Version); Assert.Single(profile.Items, i => i.Area == "Warehouse");
    }
}
