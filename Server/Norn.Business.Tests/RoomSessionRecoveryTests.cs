using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Accounts;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>
/// 补齐“新账号Session找回旧Run”的真实HTTP边界。实例准备仍沿既有测试Host身份，
/// 不启动Unity DS；2次卡与137金币是隔离账号的已消耗状态种子，不声称执行过Gameplay施法。
/// </summary>
public sealed class RoomSessionRecoveryTests
{
    [Theory]
    [InlineData("p7", true)]
    [InlineData("home", true)]
    [InlineData("p7", false)]
    [Trait("Category", "DatabaseIntegration")]
    public async Task NewLoginSessionRecoversTheSameRunAndActorWithoutRegrantingAssets(string dungeon, bool singlePlayer)
    {
        await using var factory = new OnlineFactory(instances: true);
        var account = await factory.NewPlayerAsync();
        using var originalClient = account.Client;
        var card = Assert.Single(account.Profile.Items, item => item.LocationKind == 2);
        Assert.False(card.HasUnlimitedCharges);
        await SeedPreviouslyConsumedAssets(factory, account.Profile.PlayerId, card.InstanceId);
        var starting = (await originalClient.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(2, Assert.Single(starting.Items, item => item.InstanceId == card.InstanceId).RemainingCharges);
        Assert.Equal(137L, starting.Gold);

        var entries = factory.Services.GetRequiredService<RunEntryService>();
        var instances = factory.Services.GetRequiredService<RoomInstanceService>();
        HttpClient? peerClient = null;
        string? peerId = null;
        RunReservation run;
        if (singlePlayer)
        {
            // 不调用会启动进程的/single-entry；目标是Session恢复，不重复验证RoomLauncher。
            run = await entries.PrepareSingleAsync(starting.PlayerId, new SinglePlayerEntryRequest {
                CommandId = Guid.NewGuid().ToString("N"), DungeonId = dungeon, ExpectedInventoryVersion = starting.Version
            }, default, AccountService.PracticeSessionScope(account.Token));
        }
        else
        {
            var peer = await factory.NewPlayerAsync(); peerClient = peer.Client; peerId = peer.Profile.PlayerId;
            var room = await RoomAdmissionTests.Create(originalClient);
            using var joined = await RoomAdmissionTests.Join(peerClient, room.RoomId);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
            room = await RoomAdmissionTests.View(originalClient, room.RoomId);
            room = await RoomAdmissionTests.Ready(originalClient, room, starting.Version);
            room = await RoomAdmissionTests.Ready(peerClient, room, peer.Profile.Version);
            run = await entries.PrepareAsync(starting.PlayerId, room.RoomId, room.ReadyRevision, default);
        }

        InstanceLaunchGrant? grant = null;
        try
        {
            grant = await instances.AllocateAsync(run, default);
            using var process = Process.GetCurrentProcess();
            var processIdentity = new InstanceProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!);
            await instances.RecordProcessAsync(grant.Descriptor.InstanceId, processIdentity, default);
            var ready = await instances.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, new InstanceReadyRequest {
                DungeonId = dungeon, RoomEpoch = run.RoomEpoch, ProtocolVersion = factory.Manifest.ProtocolVersion,
                BuildVersion = factory.Manifest.BuildVersion, ManifestHash = factory.Manifest.Hash,
                GameplayConfigHash = OnlineFactory.GameplayHash, ProcessId = processIdentity.ProcessId, ProcessStartTicks = processIdentity.StartTicks
            }, default);
            Assert.Equal(singlePlayer, ready.SinglePlayer);
            Assert.Equal(singlePlayer ? 1 : 2, ready.Members.Length);
            var member = Assert.Single(ready.Members, value => value.Profile.PlayerId == starting.PlayerId);
            Assert.Equal(2, Assert.Single(member.Profile.Items, item => item.InstanceId == card.InstanceId).RemainingCharges);

            var firstTicket = await Mint(originalClient, run.RoomId);
            var first = await Admit(factory, grant, firstTicket);
            Assert.Equal(1u, first.ConnectionEpoch);
            Assert.Equal(member.ActorId, first.ActorId);
            if (peerClient != null)
            {
                var peer = await Admit(factory, grant, await Mint(peerClient, run.RoomId));
                Assert.Equal(1u, peer.ConnectionEpoch);
                Assert.NotEqual(first.ActorId, peer.ActorId);
            }
            string assetsBefore = await AssetFacts(factory, run, starting.PlayerId);
            var profileBefore = (await originalClient.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
            Assert.True(profileBefore.AssetsLocked); Assert.Equal(run.RunId, profileBefore.ActiveRunId);

            // 真正撤销旧Bearer再创建另一HttpClient登录；没有复用原Token或直接按PlayerId绕过鉴权。
            using var logout = await originalClient.DeleteAsync("/api/online/sessions/current");
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            using var oldLookup = await originalClient.GetAsync("/api/online/rooms/current");
            Assert.Equal(HttpStatusCode.Unauthorized, oldLookup.StatusCode);
            using var resumedClient = factory.CreateClient();
            string newToken = await factory.LoginAsync(resumedClient, account.Username);
            Assert.True(newToken != account.Token, "新登录必须得到新Session；断言不得输出任何Bearer正文。");

            using var currentResponse = await resumedClient.GetAsync("/api/online/rooms/current");
            Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
            var current = (await currentResponse.Content.ReadFromJsonAsync<RoomReply>(OnlineFactory.Json))!;
            Assert.Equal(run.RoomId, current.RoomId); Assert.Equal(run.RunId, current.RunId);
            Assert.Equal(run.RoomEpoch, current.RoomEpoch); Assert.Equal(dungeon, current.DungeonId);
            Assert.Equal(singlePlayer, current.SinglePlayer); Assert.Equal("Running", current.State);
            Assert.Equal(singlePlayer ? 1 : 2, current.Members.Length);
            Assert.Equal(first.ActorId, (ulong)Assert.Single(current.Members, value => value.PlayerId == starting.PlayerId).Seat + 1);

            var instance = (await resumedClient.GetFromJsonAsync<RoomInstanceReply>(
                "/api/online/rooms/" + current.RoomId + "/instance", OnlineFactory.Json))!;
            Assert.Equal(grant.Descriptor.InstanceId, instance.InstanceId); Assert.Equal(run.RunId, instance.RunId);
            Assert.Equal(dungeon, instance.DungeonId); Assert.Equal("Ready", instance.State);
            factory.Clock.Advance(TimeSpan.FromSeconds(5)); // 只越过原持久签票限频，不越过实例租约。
            var nextTicket = await Mint(resumedClient, current.RoomId);
            Assert.Equal(firstTicket.InstanceId, nextTicket.InstanceId); Assert.Equal(firstTicket.RunId, nextTicket.RunId);
            Assert.Equal(firstTicket.RoomEpoch, nextTicket.RoomEpoch); Assert.Equal(dungeon, nextTicket.DungeonId);
            var resumed = await Admit(factory, grant, nextTicket);
            Assert.Equal(first.PlayerId, resumed.PlayerId); Assert.Equal(first.ActorId, resumed.ActorId);
            Assert.Equal(first.RunId, resumed.RunId); Assert.Equal(first.RoomId, resumed.RoomId);
            Assert.Equal(first.RoomEpoch, resumed.RoomEpoch); Assert.Equal(2u, resumed.ConnectionEpoch);

            var profileAfter = (await resumedClient.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
            Assert.True(profileAfter.AssetsLocked); Assert.Equal(run.RunId, profileAfter.ActiveRunId);
            Assert.Equal(profileBefore.Version, profileAfter.Version); Assert.Equal(137L, profileAfter.Gold);
            Assert.Equal(2, Assert.Single(profileAfter.Items, item => item.InstanceId == card.InstanceId).RemainingCharges);
            Assert.Equal(JsonSerializer.Serialize(profileBefore.Items, OnlineFactory.Json), JsonSerializer.Serialize(profileAfter.Items, OnlineFactory.Json));
            Assert.Equal(assetsBefore, await AssetFacts(factory, run, starting.PlayerId));
            await using var db = new MySqlConnection(factory.Connection);
            Assert.Equal(singlePlayer ? 1 : 2, await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM RunMembers WHERE RunId=@Run", new { Run = run.RunId }));
            Assert.Equal(2u, await db.ExecuteScalarAsync<uint>(
                "SELECT ConnectionEpoch FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = run.RunId, Player = starting.PlayerId }));
            if (peerId != null)
                Assert.Equal(1u, await db.ExecuteScalarAsync<uint>(
                    "SELECT ConnectionEpoch FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = run.RunId, Player = peerId }));
        }
        finally
        {
            // 只结束本case明确拥有的实例。已Admitted资产按产品恢复规则保留，不删除历史或冒充结算。
            if (grant != null) await instances.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default);
            else await entries.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default);
            peerClient?.Dispose();
        }
    }

    private static async Task SeedPreviouslyConsumedAssets(OnlineFactory factory, string player, string card)
    {
        await using var db = new MySqlConnection(factory.Connection);
        await db.ExecuteAsync("""
            UPDATE Items SET RemainingCharges=2,Revision=Revision+1 WHERE OwnerCharacterId=@Player AND InstanceId=@Card;
            UPDATE Wallets SET Balance=137 WHERE CharacterId=@Player;
            UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player
            """, new { Player = player, Card = card });
    }

    private static async Task<JoinTicketReply> Mint(HttpClient client, string room)
    {
        using var response = await client.PostAsJsonAsync("/api/online/rooms/" + room + "/ticket", new { }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JoinTicketReply>(OnlineFactory.Json))!;
    }

    private static async Task<ConsumeJoinTicketReply> Admit(OnlineFactory factory, InstanceLaunchGrant grant, JoinTicketReply ticket)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ConsumeJoinTicketRequest {
            Ticket = ticket.Ticket, PlayerId = ticket.PlayerId, RoomEpoch = ticket.RoomEpoch
        }, OnlineFactory.Json);
        var context = await factory.Server.SendAsync(http =>
        {
            http.Connection.RemoteIpAddress = IPAddress.Loopback;
            http.Request.Method = "POST"; http.Request.Path = "/api/instances/" + grant.Descriptor.InstanceId + "/admit";
            http.Request.Headers["X-Norn-Instance-Key"] = grant.Secret!;
            http.Request.ContentType = "application/json"; http.Request.ContentLength = bytes.Length;
            http.Request.Body = new MemoryStream(bytes);
            // SendAsync直接构造HttpContext，未经过Kestrel从Content-Length建立的正文检测特征。
            // /admit由Minimal API自动绑定DTO；必须说明此请求确有正文，才能进入真实业务校验。
            http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new AdmissionRequestBodyPresent());
        });
        if (context.Response.Body.CanSeek) context.Response.Body.Position = 0;
        if (context.Response.StatusCode != 200)
        {
            using var reader = new StreamReader(context.Response.Body);
            string code = SafeAdmissionError(await reader.ReadToEndAsync());
            Assert.Fail($"Admit HTTP {context.Response.StatusCode}; Code={code}");
        }
        return (await JsonSerializer.DeserializeAsync<ConsumeJoinTicketReply>(context.Response.Body, OnlineFactory.Json))!;
    }

    private sealed class AdmissionRequestBodyPresent : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    { public bool CanHaveBody => true; }

    private static string SafeAdmissionError(string body)
    {
        // Development错误页可能回显请求Header，绝不将原响应写入测试日志；仅输出已知错误码。
        if (body.Contains("Implicit body inferred", StringComparison.Ordinal) &&
            body.Contains("but no body was provided", StringComparison.Ordinal)) return "RequestBodyNotDetected";
        try
        {
            var error = JsonSerializer.Deserialize<OnlineErrorReply>(body, OnlineFactory.Json);
            return error?.Code switch
            {
                "LocalControlOnly" or "InvalidInstanceCredential" or "InstanceEpochExpired" or
                "InvalidJoinTicket" or "StorageUnavailable" => error.Code,
                _ => "UnrecognizedBusinessError"
            };
        }
        catch (JsonException) { return "NonJsonErrorResponse"; }
    }

    private static async Task<string> AssetFacts(OnlineFactory factory, RunReservation run, string player)
    {
        await using var db = new MySqlConnection(factory.Connection);
        var items = await db.QueryAsync("SELECT * FROM Items WHERE OwnerCharacterId=@Player ORDER BY InstanceId", new { Player = player });
        var locations = await db.QueryAsync("SELECT * FROM ItemLocations WHERE OwnerCharacterId=@Player ORDER BY InstanceId", new { Player = player });
        var wallet = await db.QueryAsync("SELECT * FROM Wallets WHERE CharacterId=@Player", new { Player = player });
        var leases = await db.QueryAsync("SELECT * FROM AssetLeases WHERE RunId=@Run ORDER BY CharacterId", new { Run = run.RunId });
        var custody = await db.QueryAsync("SELECT * FROM RunItemCustody WHERE RunId=@Run ORDER BY InstanceId", new { Run = run.RunId });
        var frozen = await db.QueryAsync("SELECT CharacterId,EntrySnapshot FROM RunMembers WHERE RunId=@Run ORDER BY CharacterId", new { Run = run.RunId });
        return JsonSerializer.Serialize(new { items, locations, wallet, leases, custody, frozen }, OnlineFactory.Json);
    }
}
