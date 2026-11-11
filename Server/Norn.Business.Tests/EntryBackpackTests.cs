using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Norn.Business.Features.Inventory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>只使用受OnlineFactory保护的test Schema；通过正式Prepare验证补包，不用fixture造包冒充获取入口。</summary>
public sealed class EntryBackpackTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task AccountBaselineBackpack_RegistrationCommitsTheBoundBagBeforeTheFirstProfileRead()
    {
        await using var f = new OnlineFactory(); using var client = f.CreateClient();
        string username = "baseline_" + Guid.NewGuid().ToString("N")[..15];
        using var registered = await client.PostAsJsonAsync("/api/online/accounts",
            new RegisterAccountRequest { Username = username, Password = OnlineFactory.Password }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await using var db = new MySqlConnection(f.Connection);
        string player = await db.QuerySingleAsync<string>("SELECT c.CharacterId FROM Characters c JOIN Accounts a ON a.AccountId=c.AccountId WHERE a.Username=@Name", new { Name = username });
        // 必须在第一次GET之前看真实行，避免用Profile惰性修复掩盖注册事务漏接。
        var bags = (await db.QueryAsync<string>("SELECT InstanceId FROM ItemLocations WHERE OwnerCharacterId=@Player AND CombatLocationKind=4", new { Player = player })).ToArray();
        string bag = Assert.Single(bags);
        Assert.Equal(bag, await db.QuerySingleAsync<string>("SELECT InstanceId FROM EntryBackpackGrants WHERE CharacterId=@Player AND GrantedRunId IS NULL", new { Player = player }));
        Assert.Equal(1u, await db.QuerySingleAsync<uint>("SELECT Version FROM Characters WHERE CharacterId=@Player", new { Player = player }));
        Assert.Equal(3, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE OwnerCharacterId=@Player", new { Player = player }));
        Assert.Equal(10000L, await db.QuerySingleAsync<long>("SELECT Balance FROM Wallets WHERE CharacterId=@Player", new { Player = player }));
        await f.LoginAsync(client, username);
        var profile = (await client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var actual = Assert.Single(profile.Items, i => i.LocationKind == 4);
        Assert.Equal(bag, actual.InstanceId); Assert.True(actual.EntryBoundBackpack); Assert.Equal(1u, profile.Version);
        var definition = Assert.Single(f.Manifest.Items, i => i.ContentId == actual.ContentId);
        Assert.Equal(8, definition.BackpackWidth); Assert.Equal(8, definition.BackpackHeight);
        Assert.DoesNotContain(profile.Items, i => f.Manifest.Items.Single(d => d.ContentId == i.ContentId).Kind == 3);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task AccountBaselineBackpack_ConcurrentFirstProfilesRepairLegacyMissingBagExactlyOnce()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync();
        await RemoveRegisteredBaselineForLegacyFixture(f, a.Profile.PlayerId);
        var service = f.Services.GetRequiredService<InventoryService>();
        var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.GetProfileAsync(a.Profile.PlayerId, default)));
        string bag = Assert.Single(replies[0].Items, i => i.LocationKind == 4).InstanceId;
        foreach (var profile in replies)
        {
            Assert.Equal(a.Profile.Version + 1, profile.Version); Assert.Equal(a.Profile.Gold, profile.Gold);
            Assert.Equal(bag, Assert.Single(profile.Items, i => i.LocationKind == 4 && i.EntryBoundBackpack).InstanceId);
            Assert.All(a.Profile.Items.Where(i => !i.EntryBoundBackpack), old => Assert.Contains(profile.Items,
                i => i.InstanceId == old.InstanceId && i.Revision == old.Revision && i.RemainingCharges == old.RemainingCharges && i.ParentInstanceId == old.ParentInstanceId));
        }
        await using var db = new MySqlConnection(f.Connection);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId=@Player AND GrantedRunId IS NULL", new { Player = a.Profile.PlayerId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE OwnerCharacterId=@Player AND ContentId=@Content", new { Player = a.Profile.PlayerId, Content = replies[0].Items.Single(i => i.InstanceId == bag).ContentId }));
        var again = await service.GetProfileAsync(a.Profile.PlayerId, default);
        Assert.Equal(replies[0].Version, again.Version); Assert.Equal(bag, Assert.Single(again.Items, i => i.LocationKind == 4).InstanceId);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task AccountBaselineBackpack_FirstProfileRestoresTheOriginalWarehouseGrantWithoutReissuing()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f);
        var runs = f.Services.GetRequiredService<RunEntryService>();
        var run = await runs.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default);
        Assert.True(await runs.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default));
        var before = (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var bag = Assert.Single(before.Items, i => i.LocationKind == 4);
        await using var db = new MySqlConnection(f.Connection);
        // 仅本账号fixture模拟升级前保存的入局来源和仓库位置；Run是真实Prepare创建，绝不造假Run。
        await db.ExecuteAsync("UPDATE EntryBackpackGrants SET GrantedRunId=@Run WHERE CharacterId=@Player; UPDATE ItemLocations SET Area='Warehouse',CombatLocationKind=NULL,Slot=0 WHERE InstanceId=@Bag",
            new { Run = run.RunId, Player = before.PlayerId, Bag = bag.InstanceId });
        var after = (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var restored = Assert.Single(after.Items, i => i.LocationKind == 4);
        Assert.Equal(bag.InstanceId, restored.InstanceId); Assert.True(restored.EntryBoundBackpack);
        Assert.Equal(bag.Revision + 1, restored.Revision); Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(before.Gold, after.Gold); Assert.Equal(before.Items.Length, after.Items.Length);
        Assert.Equal(run.RunId, await db.QuerySingleAsync<string>("SELECT GrantedRunId FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = before.PlayerId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = before.PlayerId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task AccountBaselineBackpack_LeasedProfileKeepsTheFrozenBagAndNeverRepairsPersistentRows()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f);
        var runs = f.Services.GetRequiredService<RunEntryService>();
        var run = await runs.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default);
        await using var db = new MySqlConnection(f.Connection);
        var frozen = (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var bag = Assert.Single(frozen.Items, i => i.LocationKind == 4);
        try
        {
            // 在局读必须服从不可变Entry；持久旧行缺位置也不能趁GET恢复或再发一份。
            await db.ExecuteAsync("DELETE FROM ItemLocations WHERE InstanceId=@Bag AND OwnerCharacterId=@Player", new { Bag = bag.InstanceId, Player = frozen.PlayerId });
            int count = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE OwnerCharacterId=@Player", new { Player = frozen.PlayerId });
            var actual = (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
            Assert.True(actual.AssetsLocked); Assert.Equal(run.RunId, actual.ActiveRunId); Assert.Equal(frozen.Version, actual.Version);
            Assert.Equal(bag.InstanceId, Assert.Single(actual.Items, i => i.LocationKind == 4).InstanceId);
            Assert.Equal(count, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE OwnerCharacterId=@Player", new { Player = frozen.PlayerId }));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemLocations WHERE InstanceId=@Bag", new { Bag = bag.InstanceId }));
        }
        finally
        {
            await db.ExecuteAsync("INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId) VALUES(@Bag,@Player,NULL,0,4,NULL)", new { Bag = bag.InstanceId, Player = frozen.PlayerId });
            await runs.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default);
        }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task AccountBaselineBackpack_StructuredMoveCannotUnequipTheLastBagOrCommitAReceipt()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection);
        var equipped = a.Profile.Items.SingleOrDefault(i => i.LocationKind == 4);
        if (equipped == null)
        {
            // RED也要真实打到旧Move漏洞，不能只因旧注册缺包就提前失败。这个普通包只属于本fixture。
            string id = Guid.NewGuid().ToString("N");
            await db.ExecuteAsync("INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges) VALUES(@Bag,@Player,'raidtest.starterbag',1,0,FALSE); INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind) VALUES(@Bag,@Player,NULL,0,4)",
                new { Bag = id, Player = a.Profile.PlayerId });
        }
        var before = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var bag = Assert.Single(before.Items, i => i.LocationKind == 4);
        var move = new InventoryMoveRequest { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = before.Version,
            InstanceId = bag.InstanceId, OperationKind = 1, TargetLocationKind = 0, TargetParentInstanceId = "", Slot = -1 };
        using var response = await a.Client.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json);
        await RoomAdmissionTests.Error(response, HttpStatusCode.Conflict, "MinimumBackpackRequired");
        Assert.Equal(before.Version, await db.QuerySingleAsync<uint>("SELECT Version FROM Characters WHERE CharacterId=@Player", new { Player = before.PlayerId }));
        Assert.Equal(4, await db.QuerySingleAsync<int>("SELECT CombatLocationKind FROM ItemLocations WHERE InstanceId=@Bag", new { Bag = bag.InstanceId }));
        Assert.Equal(bag.Revision, await db.QuerySingleAsync<uint>("SELECT Revision FROM Items WHERE InstanceId=@Bag", new { Bag = bag.InstanceId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM InventoryCommands WHERE CharacterId=@Player AND CommandId=@Command", new { Player = before.PlayerId, Command = move.CommandId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task AccountBaselineBackpack_ExistingLargerEquippedBagIsKeptWithoutGrantOrVersionChange()
    {
        await using var f = new LargerBagBaselineFactory(); var a = await f.NewPlayerAsync();
        await RemoveRegisteredBaselineForLegacyFixture(f, a.Profile.PlayerId);
        string bag = Guid.NewGuid().ToString("N"); await using var db = new MySqlConnection(f.Connection);
        await db.ExecuteAsync("INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges) VALUES(@Bag,@Player,'wandtest.bag',7,0,FALSE); INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind) VALUES(@Bag,@Player,NULL,0,4)",
            new { Bag = bag, Player = a.Profile.PlayerId });
        var profile = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var actual = Assert.Single(profile.Items, i => i.LocationKind == 4);
        Assert.Equal(bag, actual.InstanceId); Assert.Equal(7u, actual.Revision); Assert.False(actual.EntryBoundBackpack);
        Assert.Equal(a.Profile.Version, profile.Version); Assert.Equal(a.Profile.Gold, profile.Gold);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = profile.PlayerId }));
        var rules = Assert.Single(f.Manifest.Items, i => i.ContentId == actual.ContentId);
        Assert.Equal(12, rules.BackpackWidth); Assert.Equal(10, rules.BackpackHeight);
        var again = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(profile.Version, again.Version); Assert.Equal(bag, Assert.Single(again.Items, i => i.LocationKind == 4).InstanceId);
    }

    /// <summary>只清本用例刚注册的可信包来模拟历史无包账号；不用于任何生产修复，也不动钱包/原杖卡。</summary>
    private static async Task RemoveRegisteredBaselineForLegacyFixture(OnlineFactory f, string player)
    {
        await using var db = new MySqlConnection(f.Connection);
        var bags = (await db.QueryAsync<string>("SELECT InstanceId FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = player })).ToArray();
        foreach (string bag in bags)
            await db.ExecuteAsync("DELETE FROM EntryBackpackGrants WHERE CharacterId=@Player AND InstanceId=@Bag; DELETE FROM ItemLocations WHERE InstanceId=@Bag AND OwnerCharacterId=@Player; DELETE FROM Items WHERE InstanceId=@Bag AND OwnerCharacterId=@Player", new { Player = player, Bag = bag });
    }

    /// <summary>正式目录的两包当前都是8×8；仅私有Host副本提供12×10输入，证明保底不降级玩家大包。</summary>
    private sealed class LargerBagBaselineFactory : OnlineFactory
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "norn-baseline-large-bag-" + Guid.NewGuid().ToString("N") + ".json");
        public LargerBagBaselineFactory()
        {
            var copy = JsonSerializer.Deserialize<ContentManifest>(JsonSerializer.Serialize(Manifest, Json), Json)!;
            var bag = copy.Items.Single(i => i.ContentId == "wandtest.bag"); bag.BackpackWidth = 12; bag.BackpackHeight = 10;
            copy.Hash = ContentManifestRules.CalculateHash(copy); Manifest.Items = copy.Items; Manifest.Hash = copy.Hash;
            File.WriteAllText(_path, JsonSerializer.Serialize(copy, Json));
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Online:ManifestPath"] = _path }));
        }
        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); }
            finally { if (File.Exists(_path)) File.Delete(_path); }
        }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task RegisteredBaseline_ConcurrentEntryKeepsTheSameBoundBag_WithoutSpendingGold()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f);
        Assert.All(new[] { p.A, p.B }, profile => Assert.Single(profile.Items, i => i.LocationKind == 4 && i.EntryBoundBackpack));
        var service = f.Services.GetRequiredService<RunEntryService>();
        var runs = await Task.WhenAll(service.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default),
            service.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default));
        Assert.Equal(runs[0], runs[1]);
        await using var db = new MySqlConnection(f.Connection);
        foreach (var before in new[] { p.A, p.B })
        {
            var after = await f.Services.GetRequiredService<Norn.Business.Features.Inventory.InventoryService>().GetProfileAsync(before.PlayerId, default);
            var bag = Assert.Single(after.Items, i => i.LocationKind == 4);
            Assert.Equal("raidtest.starterbag", bag.ContentId); Assert.Equal(before.Gold, after.Gold);
            // 最小包已在注册事务存在；入局只有本轮空瓶新增一次，不能重新发包或改来源。
            Assert.Equal(before.Items.Single(i => i.LocationKind == 4).InstanceId, bag.InstanceId);
            Assert.Equal(before.Version + 1, after.Version); Assert.Equal(before.Items.Length + 1, after.Items.Length);
            Assert.True(bag.EntryBoundBackpack); Assert.Single(after.Items, i => i.LocationKind == 8);
            Assert.All(before.Items, item => Assert.Contains(after.Items, i => i.InstanceId == item.InstanceId && i.Revision == item.Revision));
            var definition = Assert.Single(f.Manifest.Items, i => i.ContentId == bag.ContentId);
            Assert.Equal(8, definition.BackpackWidth); Assert.Equal(8, definition.BackpackHeight);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId=@Player AND InstanceId=@Item AND GrantedRunId IS NULL",
                new { Player = before.PlayerId, Item = bag.InstanceId, Run = runs[0].RunId }));
            string json = await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = runs[0].RunId, Player = before.PlayerId });
            var frozen = JsonSerializer.Deserialize<OnlineProfileReply>(json, OnlineFactory.Json)!;
            Assert.Equal(after.Version, frozen.Version); Assert.Equal(after.Version, frozen.CombatAssets.InventoryVersion);
            Assert.Equal(bag.InstanceId, Assert.Single(frozen.CombatAssets.Items, i => i.LocationKind == 4).InstanceId);
            Assert.True(Assert.Single(frozen.CombatAssets.Items, i => i.LocationKind == 4).EntryBoundBackpack);
        }
        Assert.True(await service.FailStartAsync(runs[0].RunId, runs[0].RoomEpoch, "Cancelled", default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExistingEquippedBag_IsKeptWithItsIdentity_AndOnlyTheEntryBottleChangesVersion()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        string bag = Guid.NewGuid().ToString("N");
        await using var db = new MySqlConnection(f.Connection);
        string baseline = Assert.Single(a.Profile.Items, i => i.LocationKind == 4 && i.EntryBoundBackpack).InstanceId;
        // 仅这个用例构造已经换上的普通包，注册保底原身份留仓库；不删除来源、也不造第二装备位。
        await db.ExecuteAsync("""
            UPDATE ItemLocations SET Area='Warehouse',CombatLocationKind=NULL,Slot=0 WHERE InstanceId=@Baseline;
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges) VALUES(@Bag,@Player,'wandtest.bag',7,0,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId) VALUES(@Bag,@Player,NULL,0,4,NULL)
            """, new { Bag = bag, Baseline = baseline, Player = a.Profile.PlayerId });
        var room = await ReadyPair(a.Client, a.Profile, b.Client, b.Profile);
        var service = f.Services.GetRequiredService<RunEntryService>();
        var run = await service.PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default);
        var after = await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json);
        Assert.Equal(bag, Assert.Single(after!.Items, i => i.LocationKind == 4).InstanceId);
        Assert.Equal(7u, Assert.Single(after.Items, i => i.InstanceId == bag).Revision);
        Assert.Equal(a.Profile.Version + 1, after.Version); Assert.Equal(a.Profile.Gold, after.Gold);
        Assert.False(Assert.Single(after.Items, i => i.LocationKind == 4).EntryBoundBackpack);
        Assert.Single(after.Items, i => i.LocationKind == 8);
        Assert.Equal(baseline, await db.QuerySingleAsync<string>("SELECT InstanceId FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = a.Profile.PlayerId }));
        Assert.Contains(after.Items, i => i.InstanceId == baseline && i.LocationKind == 0 && i.EntryBoundBackpack);
        Assert.True(await service.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task StartFailureAndNextEntry_KeepTheSameBag_WhileANewRunGrantsItsOwnBottle()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f); var service = f.Services.GetRequiredService<RunEntryService>();
        var first = await service.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default);
        var granted = (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        string bag = Assert.Single(granted.Items, i => i.LocationKind == 4).InstanceId;
        Assert.True(await service.FailStartAsync(first.RunId, first.RoomEpoch, "Cancelled", default));
        var b = (await p.ClientB.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var room = await RoomAdmissionTests.View(p.ClientA, p.Room.RoomId);
        room = await RoomAdmissionTests.Ready(p.ClientA, room, granted.Version); room = await RoomAdmissionTests.Ready(p.ClientB, room, b.Version);
        var next = await service.PrepareAsync(p.A.PlayerId, room.RoomId, room.ReadyRevision, default);
        var after = (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(bag, Assert.Single(after.Items, i => i.LocationKind == 4).InstanceId); Assert.Equal(granted.Version + 1, after.Version);
        Assert.Equal(2, after.Items.Count(i => i.LocationKind == 8));
        Assert.NotEqual(first.RunId, next.RunId); Assert.True(await service.FailStartAsync(next.RunId, next.RoomEpoch, "Cancelled", default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task InvalidMember_RollsBackAllNewBagsVersionsAndReservations_AndLeavesLegacyAssetsUntouched()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f);
        await using var db = new MySqlConnection(f.Connection);
        // 后处理成员故意含legacy平面物，确保前一成员的补包也回滚；不转换或删除旧物。
        string second = StringComparer.Ordinal.Compare(p.A.PlayerId, p.B.PlayerId) > 0 ? p.A.PlayerId : p.B.PlayerId;
        string legacy = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges) VALUES(@Item,@Player,'wandtest.fire',1,5,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot) VALUES(@Item,@Player,'Backpack',0)
            """, new { Item = legacy, Player = second });
        var error = await Assert.ThrowsAsync<OnlineFailure>(() => f.Services.GetRequiredService<RunEntryService>().PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default));
        Assert.Equal("LegacyBackpackLayoutUnsupported", error.Code);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId IN (@A,@B) AND GrantedRunId IS NULL", new { A = p.A.PlayerId, B = p.B.PlayerId }));
        foreach (var original in new[] { p.A, p.B })
            Assert.Equal(original.Items.Single(i => i.LocationKind == 4).InstanceId,
                await db.QuerySingleAsync<string>("SELECT InstanceId FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = original.PlayerId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Runs WHERE RoomId=@Room", new { Room = p.Room.RoomId }));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Characters WHERE CharacterId IN (@A,@B) AND Version=1", new { A = p.A.PlayerId, B = p.B.PlayerId }));
        Assert.Equal("Backpack", await db.QuerySingleAsync<string>("SELECT Area FROM ItemLocations WHERE InstanceId=@Item", new { Item = legacy }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FreeBag_TrustedOwnerBindingRejectsTransfer_AndOldMoveCannotUnbindIt()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f); var service = f.Services.GetRequiredService<RunEntryService>();
        var run = await service.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default);
        var profile = (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var bag = Assert.Single(profile.Items, i => i.LocationKind == 4);
        Assert.True(await service.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default));
        var move = new InventoryMoveRequest { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profile.Version, InstanceId = bag.InstanceId, Destination = "Warehouse", Slot = 0 };
        using var result = await p.ClientA.PostAsJsonAsync("/api/online/inventory/move", move, OnlineFactory.Json);
        await RoomAdmissionTests.Error(result, HttpStatusCode.Conflict, "EntryBackpackBound");
        await using var db = new MySqlConnection(f.Connection);
        // 移除位置只为隔离FK归因，事务末尾回滚；不是生产卸包/转交入口。
        await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("DELETE FROM ItemLocations WHERE InstanceId=@Bag", new { Bag = bag.InstanceId }, tx);
        var error = await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync("UPDATE Items SET OwnerCharacterId=@Other WHERE InstanceId=@Bag", new { Bag = bag.InstanceId, Other = p.B.PlayerId }, tx));
        Assert.Equal(1451, error.Number); await tx.RollbackAsync();
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExistingGrantOutsideEquipment_PreservesStateAndRefusesToGenerateAnotherBag()
    {
        await using var f = new OnlineFactory(); var p = await Pair(f); var service = f.Services.GetRequiredService<RunEntryService>();
        var first = await service.PrepareAsync(p.A.PlayerId, p.Room.RoomId, p.Room.ReadyRevision, default);
        Assert.True(await service.FailStartAsync(first.RunId, first.RoomEpoch, "Cancelled", default));
        var profile = (await p.ClientA.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        string bag = Assert.Single(profile.Items, i => i.LocationKind == 4).InstanceId;
        await using var db = new MySqlConnection(f.Connection);
        // 旧仓库位置现在是明确允许的兼容恢复；这里改测真正未知的缺位置状态，仍不得补第二身份。
        await db.ExecuteAsync("DELETE FROM ItemLocations WHERE InstanceId=@Bag", new { Bag = bag });
        using var rejected = await p.ClientA.GetAsync("/api/online/profile");
        await RoomAdmissionTests.Error(rejected, HttpStatusCode.Conflict, "LocationMissing");
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = profile.PlayerId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemLocations WHERE InstanceId=@Bag", new { Bag = bag }));
        Assert.Equal(profile.Version, await db.ExecuteScalarAsync<uint>("SELECT Version FROM Characters WHERE CharacterId=@Player", new { Player = profile.PlayerId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Runs WHERE RoomId=@Room", new { Room = p.Room.RoomId }));
    }

    private static async Task<(HttpClient ClientA, HttpClient ClientB, OnlineProfileReply A, OnlineProfileReply B, RoomReply Room)> Pair(OnlineFactory f)
    {
        var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        return (a.Client, b.Client, a.Profile, b.Profile, await ReadyPair(a.Client, a.Profile, b.Client, b.Profile));
    }
    private static async Task<RoomReply> ReadyPair(HttpClient a, OnlineProfileReply profileA, HttpClient b, OnlineProfileReply profileB)
    {
        var room = await RoomAdmissionTests.Create(a); using var joined = await RoomAdmissionTests.Join(b, room.RoomId);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode); room = await RoomAdmissionTests.View(a, room.RoomId);
        room = await RoomAdmissionTests.Ready(a, room, profileA.Version); return await RoomAdmissionTests.Ready(b, room, profileB.Version);
    }
}
