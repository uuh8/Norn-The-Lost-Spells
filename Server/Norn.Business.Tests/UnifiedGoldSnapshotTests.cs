using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Microsoft.Extensions.DependencyInjection;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>
/// 统一余额只证明真实入局/DS当前读数保真，不把Journal保存解释成Wallet结算。
/// Reflection使新增字段缺失时仍可编译并形成行为RED；数据库部分只复用既有隔离Fixture。
/// </summary>
public sealed class UnifiedGoldSnapshotTests : IDisposable
{
    private static readonly string Hash = new('a', 64), Player = Id(1), Run = Id(2), Instance = Id(3);
    private readonly string _manifestPath = Path.Combine(Path.GetTempPath(), "norn-unified-gold-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly ContentManifestValidator _content;

    public UnifiedGoldSnapshotTests()
    {
        // 缺字段必须先于Manifest/Host构造失败，不能把未知初始物品配置当功能RED。
        Fields();
        var manifest = new ContentManifest { SchemaVersion = 2, BuildVersion = "unified-gold-test",
            Items = [Definition("gold.test.wand", 1), Definition("gold.test.spell", 0)] };
        manifest.Hash = ContentManifestRules.CalculateHash(manifest);
        File.WriteAllText(_manifestPath, JsonSerializer.Serialize(manifest, OnlineFactory.Json));
        _content = new(new OnlineOptions { ManifestPath = _manifestPath, GameplayConfigHash = Hash,
            StarterContentIds = ["gold.test.wand", "gold.test.spell"] });
    }

    public void Dispose() => File.Delete(_manifestPath);

    [Fact]
    public void GoldContractRequiresNewOwnedAndCompleteSchemasWithoutChangingLegacySchemaOne()
    {
        Fields();
        Assert.Equal(3u, OnlineOwnedAssetSnapshot.CurrentSchemaVersion);
        Assert.Equal(3u, OnlineRunAssetCheckpoint.LegacyCurrencyCompleteSchemaVersion);
        Assert.Equal(4u, OnlineRunAssetCheckpoint.CompleteSchemaVersion);
        Assert.Equal(1u, OnlineRunAssetCheckpoint.CurrentSchemaVersion);
    }

    [Theory, InlineData(0L), InlineData(10000L), InlineData(long.MaxValue)]
    public void ExplicitNonNegativeBalanceAndRevisionAreAcceptedWithoutInventingItems(long gold)
    {
        var assets = Assets(gold, 1);
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        Assert.Equal(gold, Gold(assets)); Assert.Equal(1u, CurrencyVersion(assets)); Assert.Empty(assets.Items);
    }

    [Theory, InlineData(-1L, 1u), InlineData(long.MinValue, 1u), InlineData(10000L, 0u)]
    public void NegativeBalanceOrMissingCurrencyRevisionIsRejected(long gold, uint revision)
    {
        var assets = Assets(gold, revision);
        Assert.Throws<OnlineFailure>(() => OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true));
    }

    [Fact]
    public void NewCompleteCheckpointPreservesActualGoldAndVersionInCanonicalBody()
    {
        var current = Complete(10037, 2); Validate(current);
        string json = Canonical(current);
        Assert.Contains("\"Gold\":10037", json, StringComparison.Ordinal);
        Assert.Contains("\"CurrencyVersion\":2", json, StringComparison.Ordinal);
        var clone = Clone(current); SetCurrency(clone.Members[0].Assets, 10038, 3);
        Assert.NotEqual(json, Canonical(clone));
        Assert.Equal(10037, Gold(current.Members[0].Assets));
    }

    [Theory, InlineData(10037L, 1u), InlineData(10000L, 0u)]
    public void OldCurrencyRevisionCannotChangePreviouslyConfirmedBalance(long gold, uint revision)
    {
        var current = Complete(gold, revision); var prior = Complete(10000, 1);
        current.Sequence = 2; current.CheckpointId = Id(5);
        var trusted = Context() with { PreviousCheckpoint = prior, LastSequence = 1 };
        Assert.Throws<OnlineFailure>(() => RunAssetCheckpointRules.Require(current, trusted, _content));
    }

    [Fact]
    public void LegitimateNewCurrencyVersionChangesCurrentFactsWithoutDefiningWalletSettlement()
    {
        var current = Complete(10037, 2); current.Sequence = 2; current.CheckpointId = Id(5);
        RunAssetCheckpointRules.Require(current, Context() with { PreviousCheckpoint = Complete(10000, 1), LastSequence = 1 }, _content);
        Assert.Equal(10037, Gold(current.Members[0].Assets));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ProfileAndFrozenEntryUseSameRealWalletBalanceAndInitialCurrencyRevision()
    {
        Fields();
        await using var factory = new OnlineFactory(instances: true);
        var a = await factory.NewPlayerAsync(); var b = await factory.NewPlayerAsync();
        Assert.Equal(10000, a.Profile.Gold);
        // 仅修改本case新建、尚无租约的隔离测试账号。非10000金额能抓住错误硬编码赠送常数；
        // Setup不是生产加款入口，也不触旧测试账号或任何主库资产。
        await using var db = new MySqlConnection(factory.Connection);
        await db.OpenAsync();
        await using (var tx = await db.BeginTransactionAsync())
        {
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player",
                new { Player = a.Profile.PlayerId }, tx));
            await db.ExecuteAsync("""
                UPDATE Wallets SET Balance=12347 WHERE CharacterId=@Player;
                UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player
                """, new { Player = a.Profile.PlayerId }, tx);
            await tx.CommitAsync();
        }
        var current = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(12347, current.Gold); Assert.Equal(current.Gold, Gold(current.CombatAssets));
        Assert.Equal(1u, CurrencyVersion(current.CombatAssets));
        var room = await RoomAdmissionTests.Create(a.Client); RunReservation? reservation = null;
        var entries = factory.Services.GetRequiredService<RunEntryService>();
        try
        {
            using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
            room = await RoomAdmissionTests.View(a.Client, room.RoomId);
            room = await RoomAdmissionTests.Ready(a.Client, room, current.Version);
            room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
            reservation = await entries.PrepareAsync(current.PlayerId, room.RoomId, room.ReadyRevision, default);
            string json = await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",
                new { Run = reservation.RunId, Player = current.PlayerId });
            var frozen = JsonSerializer.Deserialize<OnlineProfileReply>(json, OnlineFactory.Json)!;
            Assert.Equal(12347, frozen.Gold); Assert.Equal(frozen.Gold, Gold(frozen.CombatAssets));
            Assert.Equal(1u, CurrencyVersion(frozen.CombatAssets));
            Assert.Equal(12347, await db.ExecuteScalarAsync<long>("SELECT Balance FROM Wallets WHERE CharacterId=@Player", new { Player = current.PlayerId }));
        }
        finally
        {
            if (reservation != null) await entries.FailStartAsync(reservation.RunId, reservation.RoomEpoch, "Cancelled", default);
            using var leaveA = await a.Client.DeleteAsync("/api/online/rooms/" + room.RoomId + "/members/me");
            using var leaveB = await b.Client.DeleteAsync("/api/online/rooms/" + room.RoomId + "/members/me");
        }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task GoldJournalReplayAndHeadAreExactButNeverCreditPersistentWallet()
    {
        Fields();
        await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        string before = await f.AssetState();
        var checkpoint = RunAssetCheckpointServiceTests.Complete(f.Current());
        var member = checkpoint.Members[0]; long initial = Gold(member.Assets);
        SetCurrency(member.Assets, checked(initial + 37), 2);
        var saved = await f.Save(checkpoint); var retry = await f.Save(Clone(checkpoint));
        Assert.Equal(saved.PayloadHash, retry.PayloadHash);
        Assert.Equal(before, await f.AssetState());
        var head = await f.Service.ReadHeadAsync(checkpoint.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch, default);
        Assert.True(head.IsComplete); Assert.Equal(OnlineRunAssetCheckpoint.CompleteSchemaVersion, head.Checkpoint.SchemaVersion);
        var restored = head.Checkpoint.Members.Single(m => m.PlayerId == member.PlayerId).Assets;
        Assert.Equal(initial + 37, Gold(restored)); Assert.Equal(2u, CurrencyVersion(restored));
        var changed = Clone(checkpoint); SetCurrency(changed.Members[0].Assets, initial + 38, 3);
        Assert.Equal("RunCheckpointIdempotencyConflict", (await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(changed))).Code);
        Assert.Equal(before, await f.AssetState());
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointReceipts WHERE RunId=@Run", new { Run = checkpoint.RunId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HistoricalSchemaTwoHeadPreservesOriginalHashAndIsNotCurrentGoldComplete()
    {
        Fields();
        await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var checkpoint = RunAssetCheckpointServiceTests.Complete(f.Current()); checkpoint.SchemaVersion = 2;
        // 原V2没有世界Coin全表。不能把当前夹具附带的[]写入旧正文，或追加Coins:null改变历史Hash。
        checkpoint.Coins = null;
        foreach (var member in checkpoint.Members) { member.Assets.SchemaVersion = 2; SetCurrency(member.Assets, 0, 0); }
        Array.Sort(checkpoint.Members, (a, b) => StringComparer.Ordinal.Compare(a.PlayerId, b.PlayerId));
        foreach (var member in checkpoint.Members) Array.Sort(member.Assets.Items, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
        // 独立复现升级前真实JSON字段，不拿新生产canonicalizer为自己生成预期。
        var node = JsonSerializer.SerializeToNode(checkpoint, OnlineFactory.Json)!;
        node.AsObject().Remove("Coins");
        foreach (var member in node["Members"]!.AsArray())
        { var assets = member!["Assets"]!.AsObject(); assets.Remove("Gold"); assets.Remove("CurrencyVersion"); }
        string json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await using var db = new MySqlConnection(f.Factory.Connection);
        await db.ExecuteAsync("""
            INSERT INTO RunAssetCheckpointReceipts(RunId,CheckpointId,InstanceId,RoomEpoch,Sequence,PayloadHash,RecordedAtUtc)
            VALUES(@Run,@Checkpoint,@Instance,@Epoch,1,@Hash,UTC_TIMESTAMP(6));
            INSERT INTO RunAssetCheckpointHeads(RunId,InstanceId,RoomEpoch,Sequence,CheckpointId,PayloadHash,PayloadJson,PayloadBytes,UpdatedAtUtc)
            VALUES(@Run,@Instance,@Epoch,1,@Checkpoint,@Hash,@Json,@Bytes,UTC_TIMESTAMP(6))
            """, new { Run = checkpoint.RunId, Checkpoint = checkpoint.CheckpointId, Instance = checkpoint.InstanceId,
                Epoch = checkpoint.RoomEpoch, Hash = hash, Json = json, Bytes = Encoding.UTF8.GetByteCount(json) });
        var head = await f.Service.ReadHeadAsync(checkpoint.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch, default);
        Assert.False(head.IsComplete); Assert.Equal(2u, head.Checkpoint.SchemaVersion);
        Assert.Equal(hash, head.Receipt.PayloadHash); Assert.Equal(0u, CurrencyVersion(head.Checkpoint.Members[0].Assets));
        var repeated = await f.Save(checkpoint); Assert.Equal(hash, repeated.PayloadHash);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointReceipts WHERE RunId=@Run", new { Run = checkpoint.RunId }));
        // 穿过正式Head GET而不是只看C#默认值；旧JSON不能被新DTO补Gold=0伪装成可信余额。
        var response = await f.Factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            context.Request.Method = "GET";
            context.Request.Path = "/api/instances/" + checkpoint.InstanceId + "/checkpoints/head";
            context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?roomEpoch=" + checkpoint.RoomEpoch);
            context.Request.Headers["X-Norn-Instance-Key"] = f.Grant.Secret!;
        });
        Assert.Equal(200, response.Response.StatusCode);
        using var document = await JsonDocument.ParseAsync(response.Response.Body);
        Assert.False(document.RootElement.GetProperty("IsComplete").GetBoolean());
        var historicalAssets = document.RootElement.GetProperty("Checkpoint").GetProperty("Members")[0].GetProperty("Assets");
        Assert.False(historicalAssets.TryGetProperty("Gold", out _));
        Assert.False(historicalAssets.TryGetProperty("CurrencyVersion", out _));
    }

    private static OnlineOwnedAssetSnapshot Assets(long gold, uint version)
    {
        var assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
            RunId = Run, PlayerId = Player, ActorId = 1, InventoryId = Run + "/" + Player, InventoryVersion = 1,
            GameplayConfigHash = Hash, SelectedWandInstanceId = "", Items = [] };
        SetCurrency(assets, gold, version); return assets;
    }
    private static OnlineRunAssetCheckpoint Complete(long gold, uint version) => new()
    {
        SchemaVersion = OnlineRunAssetCheckpoint.CompleteSchemaVersion, CheckpointId = Id(4), InstanceId = Instance, RunId = Run,
        GameplayConfigHash = Hash, RoomEpoch = 1, Sequence = 1, World = RunCheckpointWorldRulesTests.Valid(),
        // V4必须显式声明世界Coin全表；本组只验证成员余额，因此真实已知为空，不能以null缺事实代替。
        Flights = [], MaterialOutputs = [], LostMaterials = [], Coins = [],
        Members = [new() { PlayerId = Player, ActorId = 1, ConnectionEpoch = 1, Assets = Assets(gold, version),
            ResourceVersion = 1, SelectionVersion = 1, SpellUseVersion = 1, Wands = [], SpellUses = [] }]
    };
    private static RunAssetCheckpointContext Context() => new()
    {
        InstanceId = Instance, RunId = Run, RoomEpoch = 1, InstanceLeaseValid = true,
        Members = [new(Player, 1, 1, 1, true, false, Assets(10000, 1))]
    };
    private void Validate(OnlineRunAssetCheckpoint current) => RunAssetCheckpointRules.Require(current, Context(), _content);
    private static FieldInfo Field(string name)
    {
        var field = typeof(OnlineOwnedAssetSnapshot).GetField(name);
        Assert.NotNull(field); return field!;
    }
    private static void Fields() { _ = Field("Gold"); _ = Field("CurrencyVersion"); }
    private static void SetCurrency(OnlineOwnedAssetSnapshot value, long gold, uint version)
    { Field("Gold").SetValue(value, gold); Field("CurrencyVersion").SetValue(value, version); }
    private static long Gold(OnlineOwnedAssetSnapshot value) => (long)Field("Gold").GetValue(value)!;
    private static uint CurrencyVersion(OnlineOwnedAssetSnapshot value) => (uint)Field("CurrencyVersion").GetValue(value)!;
    private static string Canonical(OnlineRunAssetCheckpoint value) => (string)typeof(RunAssetCheckpointService)
        .GetMethod("CanonicalJson", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [value])!;
    private static string Id(int value) => value.ToString("x32");
    private static ContentManifestEntry Definition(string id, int kind) => new()
    {
        ContentId = id, Kind = kind, DisplayName = "test", RulesSchemaVersion = 1,
        ShapeWidth = 1, ShapeHeight = 1, MaxStack = 1, OccupiedCells = [new() { X = 0, Y = 0 }],
        BackpackWidth = 8, BackpackHeight = 8, WandProgramCapacity = 4,
        ContainerCapacityGmu = 1000, ContainerMaterialsMask = 2, MaterialKey = 1, MaterialItemCapacityGmu = 1000
    };
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, OnlineFactory.Json), OnlineFactory.Json)!;
}
