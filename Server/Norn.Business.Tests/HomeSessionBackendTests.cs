using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Runs;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Rooms;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>Home控制面首段：仍是真实账号/租约/冻结，不启动DS进程；旧Canonical字节也必须保持。</summary>
public sealed class HomeSessionBackendTests
{
    [Theory, InlineData(false), InlineData(true)]
    [Trait("Category", "DatabaseIntegration")]
    public async Task CancelledUnadmittedHomeReleasesOnlyItsFreezeSeedAndCanEnterAgain(bool instanceAllocated)
    {
        await using var factory = new OnlineFactory(instances: true);
        var account = await factory.NewPlayerAsync();
        var entries = factory.Services.GetRequiredService<RunEntryService>();
        var inventory = factory.Services.GetRequiredService<InventoryService>();
        var instances = factory.Services.GetRequiredService<RoomInstanceService>();
        string scope = Norn.Business.Features.Accounts.AccountService.PracticeSessionScope(account.Token);
        var request = new SinglePlayerEntryRequest { CommandId = Guid.NewGuid().ToString("N"), DungeonId = "home",
            ExpectedInventoryVersion = account.Profile.Version };
        var run = await entries.PrepareSingleAsync(account.Profile.PlayerId, request, default, scope);
        await using var db = new MySqlConnection(factory.Connection);
        Assert.True(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunItemCustody WHERE RunId=@Run AND State='Active'", new { Run = run.RunId }) > 0);
        if (instanceAllocated)
        {
            var grant = await instances.AllocateAsync(run, default);
            Assert.True(await instances.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default));
        }
        else Assert.True(await entries.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunItemCustody WHERE RunId=@Run", new { Run = run.RunId }));
        var unlocked = await inventory.GetProfileAsync(account.Profile.PlayerId, default, scope);
        Assert.False(unlocked.AssetsLocked); Assert.Equal(account.Profile.Version, unlocked.Version);
        Assert.Equal(account.Profile.Items.Select(i => i.InstanceId).Order(StringComparer.Ordinal),
            unlocked.Items.Select(i => i.InstanceId).Order(StringComparer.Ordinal));
        // 新Run重新冻结同一组实际实例，既不能被上一次未启动seed永久拦住，也不能每次取消再发一套。
        var next = await entries.PrepareSingleAsync(account.Profile.PlayerId, new SinglePlayerEntryRequest {
            CommandId = Guid.NewGuid().ToString("N"), DungeonId = "home", ExpectedInventoryVersion = unlocked.Version }, default, scope);
        try
        {
            Assert.NotEqual(run.RunId, next.RunId);
            Assert.Equal(unlocked.CombatAssets.Items.Length, await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM RunItemCustody WHERE RunId=@Run AND State='Active'", new { Run = next.RunId }));
        }
        finally { await entries.FailStartAsync(next.RunId, next.RoomEpoch, "Cancelled", default); }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HomeSingleEntryFreezesExactlyOneExistingLoadoutWithoutAFreeEntryBottle()
    {
        await using var f = new HomeEnabledFactory(); var a = await f.NewPlayerAsync();
        var entries = f.Services.GetRequiredService<RunEntryService>();
        var request = new SinglePlayerEntryRequest { CommandId = Guid.NewGuid().ToString("N"), DungeonId = "home", ExpectedInventoryVersion = a.Profile.Version };
        const string scope = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        // RED先沿旧真实入口到冻结结果；新重载增加最后一个可信scope参数时仍走同一行为断言。
        var method = typeof(RunEntryService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(m => m.Name == "PrepareSingleAsync").OrderByDescending(m => m.GetParameters().Length).First();
        object?[] arguments = method.GetParameters().Length == 4
            ? [a.Profile.PlayerId, request, default(CancellationToken), scope] : [a.Profile.PlayerId, request, default(CancellationToken)];
        var run = await (Task<RunReservation>)method.Invoke(entries, arguments)!;
        try
        {
            await using var db = new MySqlConnection(f.Connection);
            var frozen = JsonSerializer.Deserialize<OnlineProfileReply>(await db.QuerySingleAsync<string>(
                "SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = run.RunId, Player = a.Profile.PlayerId }), OnlineFactory.Json)!;
            Assert.Equal(a.Profile.Items.Length, frozen.Items.Length);
            Assert.Equal(a.Profile.Version, frozen.Version); Assert.Equal(a.Profile.Gold, frozen.Gold);
            Assert.All(a.Profile.Items, old => Assert.Contains(frozen.Items, i => i.InstanceId == old.InstanceId && i.Revision == old.Revision && i.RemainingCharges == old.RemainingCharges));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunMembers WHERE RunId=@Run", new { Run = run.RunId }));
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Run AND CharacterId=@Player", new { Run = run.RunId, Player = a.Profile.PlayerId }));
            var field = typeof(OnlineProfileReply).GetField("PracticeSessionScope"); Assert.NotNull(field); Assert.Equal(scope, field.GetValue(frozen));
        }
        finally { await entries.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default); }
    }

    [Theory, InlineData(""), InlineData("/match")]
    [Trait("Category", "DatabaseIntegration")]
    public async Task HomeCannotBeCreatedOrMatchedCooperativelyEvenWhenItsIdIsConfigured(string suffix)
    {
        await using var f = new HomeEnabledFactory(); var a = await f.NewPlayerAsync();
        using var response = await a.Client.PostAsJsonAsync("/api/online/rooms" + suffix, new CreateRoomRequest { DungeonId = "home" }, OnlineFactory.Json);
        await RoomAdmissionTests.Error(response, HttpStatusCode.BadRequest, "HomeSinglePlayerOnly");
        await using var db = new MySqlConnection(f.Connection);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PartyMembers WHERE CharacterId=@Player", new { Player = a.Profile.PlayerId }));
    }

    [Theory, InlineData(1u), InlineData(2u)]
    public void HistoricalExitCanonicalKeepsItsExactFieldOrderAndBytes(uint schema)
    {
        var request = Request(schema);
        var copy = RunMemberExitRules.Freeze(request, out string actual, out string hash);
        string expected = schema == 1 ? JsonSerializer.Serialize(new { copy.SchemaVersion, copy.SettlementId, copy.InstanceId, copy.RunId, copy.PlayerId,
            copy.RoomEpoch, copy.ConnectionEpoch, copy.ExpectedPersistentVersion, copy.ActorId, copy.Assets }, OnlineFactory.Json)
            : JsonSerializer.Serialize(new { copy.SchemaVersion, copy.SettlementId, copy.InstanceId, copy.RunId, copy.PlayerId,
                copy.RoomEpoch, copy.ConnectionEpoch, copy.ExpectedPersistentVersion, copy.ActorId, copy.Assets,
                copy.Reason, copy.FinalRetiredItems, copy.FinalDeathLostMaterials }, OnlineFactory.Json);
        Assert.Equal(expected, actual);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected))), hash);
    }

    [Fact]
    public void ExitSchemaThreeExplicitlyCarriesAnEmptyPeacefulSessionAndSuccessMetadata()
    {
        var body = JsonNode.Parse(JsonSerializer.Serialize(Request(2), OnlineFactory.Json))!.AsObject();
        body["SchemaVersion"] = 3u; body["SuccessfulExtraction"] = false; body["CollectedGmu"] = 0UL; body["DefeatedEnemies"] = 0u;
        body["PracticeSessionScope"] = ""; body["PracticeContinuityJson"] = ""; body["PracticeContinuityHash"] = "";
        body["PracticeGroundItems"] = new JsonArray();
        var request = JsonSerializer.Deserialize<OnlineRunMemberExitRequest>(body.ToJsonString(), OnlineFactory.Json)!;
        RunMemberExitRules.Freeze(request, out string canonical, out _);
        using var document = JsonDocument.Parse(canonical);
        Assert.False(document.RootElement.GetProperty("SuccessfulExtraction").GetBoolean());
        Assert.Equal("", document.RootElement.GetProperty("PracticeContinuityJson").GetString());
    }

    [Theory, InlineData("SuccessfulExtraction"), InlineData("PracticeSessionScope"), InlineData("PracticeGroundItems")]
    public void HistoricalExitCannotSilentlyDropNewSuccessOrPracticeFields(string field)
    {
        var body = JsonNode.Parse(JsonSerializer.Serialize(Request(2), OnlineFactory.Json))!.AsObject();
        body[field] = field == "SuccessfulExtraction" ? JsonValue.Create(true)
            : field == "PracticeGroundItems" ? new JsonArray() : JsonValue.Create(new string('b', 64));
        if (field == "SuccessfulExtraction") { body["CollectedGmu"] = 1UL; body["DefeatedEnemies"] = 0u; }
        var request = JsonSerializer.Deserialize<OnlineRunMemberExitRequest>(body.ToJsonString(), OnlineFactory.Json)!;
        var error = Assert.Throws<OnlineFailure>(() => RunMemberExitRules.Freeze(request, out _, out _));
        Assert.Equal("RunMemberExitInvalid", error.Code);
    }

    private static OnlineRunMemberExitRequest Request(uint schema)
    {
        string player = new string('1', 32), run = new string('2', 32);
        return new OnlineRunMemberExitRequest { SchemaVersion = schema, SettlementId = new string('3', 32), InstanceId = new string('4', 32), RunId = run, PlayerId = player,
            RoomEpoch = 1, ConnectionEpoch = 1, ExpectedPersistentVersion = 1, ActorId = 1,
            Assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, PlayerId = player, RunId = run, ActorId = 1,
                InventoryId = run + "/" + player, InventoryVersion = 1, CurrencyVersion = 1, GameplayConfigHash = OnlineFactory.GameplayHash, Items = [] },
            Reason = schema == 1 ? OnlineRunMemberExitReason.None : OnlineRunMemberExitReason.VoluntaryExit,
            FinalRetiredItems = schema == 1 ? null! : [], FinalDeathLostMaterials = schema == 1 ? null! : [] };
    }
    private sealed class HomeEnabledFactory : OnlineFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // 单Host配置证明名单包含home也不能打开合作路径，不改变正式配置或启动真实实例。
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Online:DungeonIds:2"] = "home" }));
        }
    }
}
