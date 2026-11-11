using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>011只由主线程在严格隔离Schema应用；fixture造物不代表新增产品获取渠道或退出结算。</summary>
public sealed class PersistentWandStateTests
{
    [Fact]
    public void ProfileItemContractDeclaresTemplateModeAndInstanceWandStatsExplicitly()
    {
        // 先约束真实HTTP DTO，而不是给测试另造一层投影。反射允许旧合同仍能编译，
        // RED应明确失败在缺少公开字段，随后业务读取测试继续验证字段里的真实持久值。
        var mode = typeof(OnlineItemReply).GetField("UseTemplateWandStats");
        var stats = typeof(OnlineItemReply).GetField("WandStats");
        Assert.NotNull(mode); Assert.Equal(typeof(bool), mode.FieldType);
        Assert.NotNull(stats); Assert.Equal(typeof(OnlineOwnedWandStatsSnapshot), stats.FieldType);
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "DatabaseIntegration")]
    public async Task ProfileItemsPreserveDistinctWarehouseAndCarriedRollsWithoutMutatingAssets(bool throughHttp)
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        string templateId = player.Profile.Items.Single(i => i.LocationKind == 3).InstanceId;
        var warehouseStats = Stats(); string warehouseId = await Seed(f, player.Profile.PlayerId, warehouseStats);
        await PutSeededWandInWarehouse(f, warehouseId);
        var carriedStats = Stats();
        carriedStats.MaxMana = 234.56789f; carriedStats.ManaRegenPerSecond = 7.9876543f;
        carriedStats.BaseCastDelay = .27531415f; carriedStats.BaseReloadTime = 1.2345678f;
        carriedStats.BaseSpreadDegrees = 8.7654321f; carriedStats.BaseDraws = 2; carriedStats.Shuffle = false;
        carriedStats.AlwaysCastContentIds = ["norn.v2.cast_faster", "norn.v2.reload_faster"];
        string carriedId = await Seed(f, player.Profile.PlayerId, carriedStats);
        string before = await ProfileReadState(f, player.Profile.PlayerId);

        OnlineProfileReply profile;
        if (throughHttp)
        {
            // 检查真实endpoint的原始JSON后再反序列化，避免仅在内存补字段却漏过HTTP合同。
            using var response = await player.Client.GetAsync("/api/online/profile");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var wireItems = document.RootElement.GetProperty(nameof(OnlineProfileReply.Items));
            foreach (string id in new[] { warehouseId, carriedId, templateId })
            {
                var wire = wireItems.EnumerateArray().Single(i => i.GetProperty(nameof(OnlineItemReply.InstanceId)).GetString() == id);
                Assert.True(wire.TryGetProperty("UseTemplateWandStats", out var template), "Profile.Items必须显式发送模板模式，不能由缺字段或ContentId猜测。");
                Assert.Equal(id == templateId, template.GetBoolean());
                Assert.True(wire.TryGetProperty("WandStats", out var stats), "仓库和携带物品应发送同一持久roll合同。");
                Assert.Equal(id == templateId ? JsonValueKind.Null : JsonValueKind.Object, stats.ValueKind);
            }
            profile = JsonSerializer.Deserialize<OnlineProfileReply>(json, OnlineFactory.Json)!;
        }
        else profile = await f.Services.GetRequiredService<InventoryService>().GetProfileAsync(player.Profile.PlayerId, default);

        var warehouse = profile.Items.Single(i => i.InstanceId == warehouseId);
        var carried = profile.Items.Single(i => i.InstanceId == carriedId);
        Assert.Equal(warehouse.ContentId, carried.ContentId);
        Assert.Equal(OnlineInventoryAreas.Warehouse, warehouse.Area); Assert.Equal("EquippedWand", carried.Area);
        EqualStats(warehouseStats, ProfileWandStats(warehouse, false));
        EqualStats(carriedStats, ProfileWandStats(carried, false));
        Assert.Null(ProfileWandStats(profile.Items.Single(i => i.InstanceId == templateId), true));
        Assert.DoesNotContain(profile.CombatAssets.Items, i => i.InstanceId == warehouseId);
        EqualStats(carriedStats, profile.CombatAssets.Items.Single(i => i.InstanceId == carriedId).WandStats);
        Assert.Equal(31, profile.CombatAssets.Items.Single(i => i.ParentInstanceId == carriedId).Slot);
        // 只读Profile不能补roll、转换模板、改版本或触碰租约/钱包；状态表也必须逐行保持不变。
        Assert.Equal(before, await ProfileReadState(f, player.Profile.PlayerId));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task LegacyTemplateKeepsOriginalIdValueAndHasNoAutomaticStateConversion()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        string before = await Assets(f, player.Profile.PlayerId);
        var read = await f.Services.GetRequiredService<InventoryService>().GetProfileAsync(player.Profile.PlayerId, default);
        var wand = Assert.Single(read.CombatAssets.Items, i => i.LocationKind == 3);
        Assert.True(wand.UseTemplateWandStats); Assert.Null(wand.WandStats);
        Assert.Equal(before, await Assets(f, player.Profile.PlayerId));
        await using var db = new MySqlConnection(f.Connection);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemWandStates WHERE OwnerCharacterId=@Owner", new { Owner = player.Profile.PlayerId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT WandStatsMode FROM Items WHERE InstanceId=@Id", new { Id = wand.InstanceId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task CombatAssetsReaderProjectsRealRollWithExactFloatBitsAndTraitOrder()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync(); var expected = Stats();
        var id = await Seed(f, player.Profile.PlayerId, expected);
        var read = await f.Services.GetRequiredService<InventoryService>().GetProfileAsync(player.Profile.PlayerId, default);
        var wand = read.CombatAssets.Items.Single(i => i.InstanceId == id);
        Assert.False(wand.UseTemplateWandStats); EqualStats(expected, wand.WandStats);
        Assert.Equal(31, read.CombatAssets.Items.Single(i => i.ParentInstanceId == id).Slot);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task TrustedFreezeUsesInstanceCapacityWithoutOpeningTheDefaultTemplateGate()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync(); var expected = Stats();
        var id = await Seed(f, player.Profile.PlayerId, expected);
        var inventory = f.Services.GetRequiredService<InventoryService>(); var profile = await inventory.GetProfileAsync(player.Profile.PlayerId, default);
        // 定向隔离冻结门：当前旧reader丢roll也不能令此例只停在投影断言，按真实已持久fixture补齐其值。
        var item = profile.CombatAssets.Items.Single(i => i.InstanceId == id); item.UseTemplateWandStats = false; item.WandStats = expected;
        inventory.FreezeCombatAssets(profile, Guid.NewGuid().ToString("N"), 1);
        EqualStats(expected, item.WandStats);
        Assert.Throws<OnlineFailure>(() => OwnedCombatAssetRules.Require(profile.CombatAssets, player.Profile.PlayerId, OnlineFactory.GameplayHash,
            f.Services.GetRequiredService<ContentManifestValidator>(), true));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ProfileEntryAndReadyPreserveRollAndSlot31AcrossActualBusinessPipeline()
    {
        await using var f = new OnlineFactory(instances: true); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var expected = Stats(); var id = await Seed(f, a.Profile.PlayerId, expected);
        var profile = await f.Services.GetRequiredService<InventoryService>().GetProfileAsync(a.Profile.PlayerId, default);
        var room = await RoomAdmissionTests.Create(a.Client); using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, profile.Version); room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        var run = await f.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default);
        var instances = f.Services.GetRequiredService<RoomInstanceService>(); InstanceLaunchGrant? grant = null;
        try
        {
            grant = await instances.AllocateAsync(run, default); using var process = Process.GetCurrentProcess();
            var identity = new InstanceProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!);
            await instances.RecordProcessAsync(grant.Descriptor.InstanceId, identity, default);
            var ready = await instances.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, new InstanceReadyRequest
            { DungeonId = "p7", RoomEpoch = run.RoomEpoch, ProtocolVersion = f.Manifest.ProtocolVersion, BuildVersion = f.Manifest.BuildVersion,
                ManifestHash = f.Manifest.Hash, GameplayConfigHash = OnlineFactory.GameplayHash, ProcessId = identity.ProcessId, ProcessStartTicks = identity.StartTicks }, default);
            var owned = ready.Members.Single(m => m.Profile.PlayerId == a.Profile.PlayerId).Profile.CombatAssets;
            EqualStats(expected, owned.Items.Single(i => i.InstanceId == id).WandStats);
            Assert.Equal(31, owned.Items.Single(i => i.ParentInstanceId == id).Slot);
        }
        finally
        {
            if (grant != null) await instances.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default);
            else await f.Services.GetRequiredService<RunEntryService>().FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default);
        }
    }

    [Theory, InlineData("missing"), InlineData("template-extra"), InlineData("non-wand"), InlineData("hash"),
        InlineData("numbers"), InlineData("unknown-trait"), InlineData("non-spell-trait"), InlineData("trait-count"), InlineData("null-traits"),
        Trait("Category", "DatabaseIntegration")]
    public async Task ReaderRejectsMissingUnexpectedAndInvalidPersistentRoll(string defect)
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync(); var stats = Stats();
        if (defect == "numbers") stats.MaxMana = 0;
        if (defect == "unknown-trait") stats.AlwaysCastContentIds = ["unknown.trait"];
        if (defect == "non-spell-trait") stats.AlwaysCastContentIds = ["wandtest.wand.a"];
        if (defect == "trait-count") stats.AlwaysCastContentIds = Enumerable.Repeat("wandtest.fire", 5).ToArray();
        if (defect == "null-traits") stats.AlwaysCastContentIds = null!;
        string id = await Seed(f, player.Profile.PlayerId, stats, defect != "missing", defect == "template-extra" ? 0 : 1);
        await using var db = new MySqlConnection(f.Connection);
        if (defect == "non-wand") await db.ExecuteAsync("UPDATE Items SET ContentId='wandtest.fire' WHERE InstanceId=@Id", new { Id = id });
        if (defect == "hash") await db.ExecuteAsync("UPDATE ItemWandStates SET StateHash=@Hash WHERE InstanceId=@Id", new { Id = id, Hash = new string('f', 64) });
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Services.GetRequiredService<InventoryService>().GetProfileAsync(player.Profile.PlayerId, default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task WarehouseRollIsValidatedBeforeAnyProfileProjectionIsReturned()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        string id = await Seed(f, player.Profile.PlayerId, Stats());
        await using var db = new MySqlConnection(f.Connection);
        // 仅fixture把无子链的新杖存仓；去除安装卡后保持FK合法，产品不新增存取入口。
        string child = await db.QuerySingleAsync<string>("SELECT InstanceId FROM ItemLocations WHERE ParentInstanceId=@Id", new { Id = id });
        await db.ExecuteAsync("DELETE FROM ItemLocations WHERE InstanceId=@Child; DELETE FROM Items WHERE InstanceId=@Child; UPDATE ItemLocations SET Area='Warehouse',Slot=0,CombatLocationKind=NULL,ParentInstanceId=NULL WHERE InstanceId=@Id", new { Id = id, Child = child });
        await db.ExecuteAsync("DELETE FROM ItemWandStates WHERE InstanceId=@Id", new { Id = id });
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Services.GetRequiredService<InventoryService>().GetProfileAsync(player.Profile.PlayerId, default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SameValueWriteIsIdempotentAndLeavesOriginalAssetAggregatesUntouched()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync(); var stats = Stats();
        await using var db = new MySqlConnection(f.Connection); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await Lock(db, tx, player.Profile.PlayerId); string id = await Insert(db, tx, player.Profile.PlayerId, 1);
        string before = await Assets(db, tx, player.Profile.PlayerId);
        var content = f.Services.GetRequiredService<ContentManifestValidator>();
        await PersistentWandStateStore.WriteNewAsync(db, tx, player.Profile.PlayerId, id, stats, content, default);
        await PersistentWandStateStore.WriteNewAsync(db, tx, player.Profile.PlayerId, id, Clone(stats), content, default);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemWandStates WHERE InstanceId=@Id", new { Id = id }, tx));
        Assert.Equal(before, await Assets(db, tx, player.Profile.PlayerId));
        string json = await db.QuerySingleAsync<string>("SELECT PayloadJson FROM ItemWandStates WHERE InstanceId=@Id", new { Id = id }, tx);
        EqualStats(stats, JsonSerializer.Deserialize<OnlineOwnedWandStatsSnapshot>(json, OnlineFactory.Json)!);
        await tx.RollbackAsync();
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SameInstanceCannotBeRerolledOnRetry()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await Lock(db, tx, player.Profile.PlayerId); string id = await Insert(db, tx, player.Profile.PlayerId, 1); var stats = Stats();
        var content = f.Services.GetRequiredService<ContentManifestValidator>();
        await PersistentWandStateStore.WriteNewAsync(db, tx, player.Profile.PlayerId, id, stats, content, default); stats.MaxMana++;
        var failure = await Assert.ThrowsAsync<OnlineFailure>(() => PersistentWandStateStore.WriteNewAsync(db, tx, player.Profile.PlayerId, id, stats, content, default));
        Assert.Equal("PersistentWandStateConflict", failure.Code); await tx.RollbackAsync();
    }

    [Theory, InlineData("template"), InlineData("foreign"), InlineData("invalid"), Trait("Category", "DatabaseIntegration")]
    public async Task WriterCannotConvertOldTemplatesOrAcceptUnauthorizedOrInvalidState(string defect)
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await Lock(db, tx, player.Profile.PlayerId);
        string id = defect == "template" ? player.Profile.Items.Single(i => i.LocationKind == 3).InstanceId
            : await Insert(db, tx, player.Profile.PlayerId, 1);
        string owner = defect == "foreign" ? Guid.NewGuid().ToString("N") : player.Profile.PlayerId;
        var stats = Stats(); if (defect == "invalid") stats.MaxMana = float.NaN;
        await Assert.ThrowsAsync<OnlineFailure>(() => PersistentWandStateStore.WriteNewAsync(db, tx, owner, id, stats,
            f.Services.GetRequiredService<ContentManifestValidator>(), default));
        await tx.RollbackAsync();
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task CallerRollbackRemovesNewItemAndItsStateTogether()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync(); string before = await Assets(f, player.Profile.PlayerId);
        await using var db = new MySqlConnection(f.Connection); await db.OpenAsync(); string id;
        await using (var tx = await db.BeginTransactionAsync())
        {
            await Lock(db, tx, player.Profile.PlayerId); id = await Insert(db, tx, player.Profile.PlayerId, 1);
            await PersistentWandStateStore.WriteNewAsync(db, tx, player.Profile.PlayerId, id, Stats(), f.Services.GetRequiredService<ContentManifestValidator>(), default);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemWandStates WHERE InstanceId=@Id", new { Id = id }, tx));
            await tx.RollbackAsync();
        }
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE InstanceId=@Id", new { Id = id }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemWandStates WHERE InstanceId=@Id", new { Id = id }));
        Assert.Equal(before, await Assets(f, player.Profile.PlayerId));
    }

    private static OnlineOwnedWandStatsSnapshot? ProfileWandStats(OnlineItemReply item, bool expectedTemplate)
    {
        var mode = typeof(OnlineItemReply).GetField("UseTemplateWandStats");
        var stats = typeof(OnlineItemReply).GetField("WandStats");
        Assert.NotNull(mode); Assert.NotNull(stats);
        Assert.Equal(expectedTemplate, Assert.IsType<bool>(mode.GetValue(item)));
        var value = stats.GetValue(item);
        return value == null ? null : Assert.IsType<OnlineOwnedWandStatsSnapshot>(value);
    }
    private static async Task PutSeededWandInWarehouse(OnlineFactory f, string id)
    {
        await using var db = new MySqlConnection(f.Connection);
        // 仅隔离fixture搬走首把新杖，给下一把同Content实物保留原装备槽；不新增产品存取或赠物入口。
        string child = await db.QuerySingleAsync<string>("SELECT InstanceId FROM ItemLocations WHERE ParentInstanceId=@Id", new { Id = id });
        await db.ExecuteAsync("DELETE FROM ItemLocations WHERE InstanceId=@Child; DELETE FROM Items WHERE InstanceId=@Child; UPDATE ItemLocations SET Area='Warehouse',Slot=0,CombatLocationKind=NULL,ParentInstanceId=NULL WHERE InstanceId=@Id", new { Id = id, Child = child });
    }
    private static async Task<string> ProfileReadState(OnlineFactory f, string owner)
    {
        await using var db = new MySqlConnection(f.Connection);
        string assets = await Assets(db, null, owner);
        var states = await db.QueryAsync("SELECT * FROM ItemWandStates WHERE OwnerCharacterId=@Owner ORDER BY InstanceId", new { Owner = owner });
        return JsonSerializer.Serialize(new { assets, states }, OnlineFactory.Json);
    }
    private static OnlineOwnedWandStatsSnapshot Stats() => new()
    { MaxMana = 123.45678f, ManaRegenPerSecond = 19.13579f, BaseCastDelay = .17531415f,
        ProgramCapacity = 32, BaseDraws = 3, BaseReloadTime = .7123456f, BaseSpreadDegrees = 4.876543f, Shuffle = true,
        AlwaysCastContentIds = ["norn.v2.reload_faster", "norn.v2.cast_faster"] };
    private static OnlineOwnedWandStatsSnapshot Clone(OnlineOwnedWandStatsSnapshot stats)
        => JsonSerializer.Deserialize<OnlineOwnedWandStatsSnapshot>(JsonSerializer.Serialize(stats, OnlineFactory.Json), OnlineFactory.Json)!;
    private static void EqualStats(OnlineOwnedWandStatsSnapshot expected, OnlineOwnedWandStatsSnapshot? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.MaxMana), BitConverter.SingleToUInt32Bits(actual.MaxMana));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.ManaRegenPerSecond), BitConverter.SingleToUInt32Bits(actual.ManaRegenPerSecond));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.BaseCastDelay), BitConverter.SingleToUInt32Bits(actual.BaseCastDelay));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.BaseReloadTime), BitConverter.SingleToUInt32Bits(actual.BaseReloadTime));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected.BaseSpreadDegrees), BitConverter.SingleToUInt32Bits(actual.BaseSpreadDegrees));
        Assert.Equal(expected.ProgramCapacity, actual.ProgramCapacity); Assert.Equal(expected.BaseDraws, actual.BaseDraws);
        Assert.Equal(expected.Shuffle, actual.Shuffle); Assert.Equal(expected.AlwaysCastContentIds, actual.AlwaysCastContentIds);
    }
    private static Task Lock(MySqlConnection db, MySqlTransaction tx, string owner)
        => db.ExecuteScalarAsync<uint>("SELECT Version FROM Characters WHERE CharacterId=@Owner FOR UPDATE", new { Owner = owner }, tx);
    private static async Task<string> Insert(MySqlConnection db, MySqlTransaction tx, string owner, int mode)
    {
        string id = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges,WandStatsMode)
            VALUES(@Id,@Owner,'wandtest.wand.a',1,0,FALSE,@Mode);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId)
            VALUES(@Id,@Owner,NULL,1,3,NULL)
            """, new { Id = id, Owner = owner, Mode = mode }, tx);
        return id;
    }
    private static async Task<string> Seed(OnlineFactory f, string owner, OnlineOwnedWandStatsSnapshot stats, bool state = true, int mode = 1)
    {
        await using var db = new MySqlConnection(f.Connection); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await Lock(db, tx, owner); string id = await Insert(db, tx, owner, mode);
        string child = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
            VALUES(@Child,@Owner,'wandtest.fire',1,7,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId)
            VALUES(@Child,@Owner,NULL,31,2,@Id);
            UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Owner
            """, new { Child = child, Owner = owner, Id = id }, tx);
        if (state)
        {
            string json = JsonSerializer.Serialize(stats, OnlineFactory.Json);
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            await db.ExecuteAsync("INSERT INTO ItemWandStates(InstanceId,OwnerCharacterId,SchemaVersion,PayloadJson,StateHash) VALUES(@Id,@Owner,1,@Json,@Hash)",
                new { Id = id, Owner = owner, Json = json, Hash = hash }, tx);
        }
        await tx.CommitAsync(); return id;
    }
    private static async Task<string> Assets(OnlineFactory f, string owner)
    { await using var db = new MySqlConnection(f.Connection); return await Assets(db, null, owner); }
    private static async Task<string> Assets(MySqlConnection db, MySqlTransaction? tx, string owner)
    {
        var items = await db.QueryAsync("SELECT * FROM Items WHERE OwnerCharacterId=@Owner ORDER BY InstanceId", new { Owner = owner }, tx);
        var locations = await db.QueryAsync("SELECT * FROM ItemLocations WHERE OwnerCharacterId=@Owner ORDER BY InstanceId", new { Owner = owner }, tx);
        var wallet = await db.QueryAsync("SELECT * FROM Wallets WHERE CharacterId=@Owner", new { Owner = owner }, tx);
        var leases = await db.QueryAsync("SELECT * FROM AssetLeases WHERE CharacterId=@Owner", new { Owner = owner }, tx);
        var character = await db.QueryAsync("SELECT * FROM Characters WHERE CharacterId=@Owner", new { Owner = owner }, tx);
        return JsonSerializer.Serialize(new { items, locations, wallet, leases, character }, OnlineFactory.Json);
    }
}
