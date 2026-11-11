using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>
/// 奖励只经正式注册/开局事务产生；夹具写入仅构造旧余额、已有道具和坏资产，
/// 全部连接来自 OnlineFactory 已保护的 test Schema，不触碰主库或启动 Unity DS。
/// </summary>
public sealed class EntryRewardTests
{
    private const string BottleContent = "item.container.container_01";

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task Registration_GrantsTenThousandOnce_DuplicateAndLoginCannotRefillSpentGold()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        Assert.Equal(10000, player.Profile.Gold);
        Assert.Equal(1u, player.Profile.Version); Assert.Equal(3, player.Profile.Items.Length);
        Assert.Single(player.Profile.Items, item => item.LocationKind == 4 && item.EntryBoundBackpack);
        // 模拟已经花掉的真实持久余额；登录/重试注册必须读取它，不能把初始奖励当余额下限。
        await using var db = new MySqlConnection(f.Connection);
        await db.ExecuteAsync("UPDATE Wallets SET Balance=137 WHERE CharacterId=@Player", new { Player = player.Profile.PlayerId });
        using var duplicate = await player.Client.PostAsJsonAsync("/api/online/accounts",
            new RegisterAccountRequest { Username = player.Username.ToUpperInvariant(), Password = OnlineFactory.Password }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var relogin = f.CreateClient(); await f.LoginAsync(relogin, player.Username);
        var after = (await relogin.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(137, after.Gold); Assert.Equal(player.Profile.PlayerId, after.PlayerId);
        Assert.Equal(player.Profile.Items.Select(i => i.InstanceId).Order(), after.Items.Select(i => i.InstanceId).Order());
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExistingZeroBalanceAccount_LoginDoesNotReceiveRetroactiveGold()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        // 旧号是否有钱不能作为发奖标记；零余额账号也不得在登录时补偿。
        await using var db = new MySqlConnection(f.Connection);
        await db.ExecuteAsync("UPDATE Wallets SET Balance=0 WHERE CharacterId=@Player", new { Player = player.Profile.PlayerId });
        using var relogin = f.CreateClient(); await f.LoginAsync(relogin, player.Username);
        var after = (await relogin.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(0, after.Gold); Assert.Equal(player.Profile.Version, after.Version);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task Entry_UsesFirstEmptyToolSlot_AndGrantsARealEmptyContainerWithoutReplacingExistingItems()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection);
        string first = await AddBottle(db, a.Profile.PlayerId, 0, 71);
        string third = await AddBottle(db, a.Profile.PlayerId, 2, 83);
        var before = await Profile(f, a.Profile.PlayerId);
        var room = await ReadyPair(a.Client, before, b.Client, b.Profile);
        var entries = f.Services.GetRequiredService<RunEntryService>();
        var run = await entries.PrepareAsync(before.PlayerId, room.RoomId, room.ReadyRevision, default);
        try
        {
            var after = await Profile(f, before.PlayerId);
            var added = Assert.Single(after.Items, i => i.LocationKind == 8 && !before.Items.Any(old => old.InstanceId == i.InstanceId));
            Assert.Equal(BottleContent, added.ContentId); Assert.Equal(1, added.Slot);
            Assert.Equal("", added.ParentInstanceId); Assert.Equal(1u, added.Revision);
            Assert.NotNull(added.ContainerState); Assert.Equal(1u, added.ContainerState.SchemaVersion); Assert.Empty(added.ContainerState.Components);
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemContainerStates WHERE InstanceId=@Item AND SchemaVersion=1", new { Item = added.InstanceId }));
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemContainerComponents WHERE InstanceId=@Item", new { Item = added.InstanceId }));
            Assert.Equal(71ul, Assert.Single(after.Items.Single(i => i.InstanceId == first).ContainerState.Components).Quantity);
            Assert.Equal(83ul, Assert.Single(after.Items.Single(i => i.InstanceId == third).ContainerState.Components).Quantity);
            Assert.Equal(before.Gold, after.Gold);
            Assert.All(before.Items, old => Assert.Contains(after.Items, i => i.InstanceId == old.InstanceId && i.Revision == old.Revision && i.LocationKind == old.LocationKind && i.Slot == old.Slot));
            var frozen = await Frozen(db, run.RunId, before.PlayerId);
            Assert.Equal(after.Version, frozen.Version); Assert.Equal(after.Version, frozen.CombatAssets.InventoryVersion);
            var frozenBottle = Assert.Single(frozen.CombatAssets.Items, i => i.InstanceId == added.InstanceId);
            Assert.Equal(8, frozenBottle.LocationKind); Assert.Equal(1, frozenBottle.Slot); Assert.Empty(frozenBottle.ContainerState.Components);
        }
        finally { Assert.True(await entries.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default)); }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ConcurrentSameRun_GrantsOnlyOneBottle_PerMember_AndANewRunGrantsAnother()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var room = await ReadyPair(a.Client, a.Profile, b.Client, b.Profile);
        var entries = f.Services.GetRequiredService<RunEntryService>();
        var runs = await Task.WhenAll(entries.PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default),
            entries.PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        Assert.Equal(runs[0], runs[1]);
        var firstA = await Profile(f, a.Profile.PlayerId); var firstB = await Profile(f, b.Profile.PlayerId);
        string bottleA = Assert.Single(firstA.Items, i => i.LocationKind == 8).InstanceId;
        Assert.Single(firstB.Items, i => i.LocationKind == 8);
        Assert.Equal(runs[0], await entries.PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        var retry = await Profile(f, a.Profile.PlayerId);
        Assert.Equal(firstA.Version, retry.Version); Assert.Equal(bottleA, Assert.Single(retry.Items, i => i.LocationKind == 8).InstanceId);
        Assert.True(await entries.FailStartAsync(runs[0].RunId, runs[0].RoomEpoch, "Cancelled", default));
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, firstA.Version); room = await RoomAdmissionTests.Ready(b.Client, room, firstB.Version);
        var next = await entries.PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default);
        try
        {
            Assert.NotEqual(runs[0].RunId, next.RunId);
            var after = await Profile(f, a.Profile.PlayerId);
            Assert.Equal(2, after.Items.Count(i => i.LocationKind == 8));
            Assert.Equal(bottleA, after.Items.Single(i => i.LocationKind == 8 && i.Slot == 0).InstanceId);
            Assert.NotEqual(bottleA, after.Items.Single(i => i.LocationKind == 8 && i.Slot == 1).InstanceId);
            Assert.Equal(firstA.Version + 1, after.Version); Assert.Equal(firstA.Gold, after.Gold);
            Assert.Equal(Assert.Single(firstA.Items, i => i.LocationKind == 4).InstanceId, Assert.Single(after.Items, i => i.LocationKind == 4).InstanceId);
        }
        finally { Assert.True(await entries.FailStartAsync(next.RunId, next.RoomEpoch, "Cancelled", default)); }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FullToolbelt_SkipsGrantWithoutReplacement_BackpackFallback_OrVersionChange()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection);
        // 注册已有合法基础包，直接使用它隔离“满道具栏跳过”分支；不能向唯一装备槽硬插第二只包。
        Assert.Single(a.Profile.Items, item => item.LocationKind == 4 && item.EntryBoundBackpack);
        for (int slot = 0; slot < 7; slot++) await AddBottle(db, a.Profile.PlayerId, slot, (ulong)slot + 1);
        var before = await Profile(f, a.Profile.PlayerId); var room = await ReadyPair(a.Client, before, b.Client, b.Profile);
        var entries = f.Services.GetRequiredService<RunEntryService>(); var run = await entries.PrepareAsync(before.PlayerId, room.RoomId, room.ReadyRevision, default);
        try
        {
            var after = await Profile(f, before.PlayerId);
            Assert.Equal(before.Version, after.Version); Assert.Equal(before.Gold, after.Gold);
            Assert.Equal(before.Items.Select(i => i.InstanceId).Order(), after.Items.Select(i => i.InstanceId).Order());
            Assert.Equal(7, after.Items.Count(i => i.LocationKind == 8)); Assert.DoesNotContain(after.Items, i => i.LocationKind == 1);
            Assert.All(after.Items.Where(i => i.LocationKind == 8), item => Assert.Equal((ulong)item.Slot + 1, Assert.Single(item.ContainerState.Components).Quantity));
            // 队友有空位仍应正常获得一只，不能把“任一成员满栏”当全队跳过条件。
            Assert.Single((await Profile(f, b.Profile.PlayerId)).Items, i => i.LocationKind == 8);
            Assert.Equal(after.Items.Length, (await Frozen(db, run.RunId, before.PlayerId)).Items.Length);
        }
        finally { Assert.True(await entries.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default)); }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task OrdinaryBagWithTheSameContentId_IsNotMarkedAsTheBoundEntryGrant()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection); string bag = Guid.NewGuid().ToString("N");
        string baseline = Assert.Single(a.Profile.Items, item => item.LocationKind == 4 && item.EntryBoundBackpack).InstanceId;
        // 同款普通包只有Items/Location，没有可信赠送来源；ContentId相同不能授予绑定身份。
        await db.ExecuteAsync("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges) VALUES(@Bag,@Player,'raidtest.starterbag',1,0,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId) VALUES(@Bag,@Player,'Warehouse',0,NULL,NULL)
            """, new { Bag = bag, Player = a.Profile.PlayerId });
        // 经过原HTTP原子换包：保留注册基础包及其可信来源，普通包占装备槽、基础包换入原仓库位。
        using var equipped = await a.Client.PostAsJsonAsync("/api/online/inventory/move", new InventoryMoveRequest
        {
            CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = a.Profile.Version,
            InstanceId = bag, OperationKind = 1, TargetLocationKind = 4, Slot = 0
        }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.OK, equipped.StatusCode);
        var before = await Profile(f, a.Profile.PlayerId);
        Assert.False(Assert.Single(before.Items, i => i.InstanceId == bag).EntryBoundBackpack);
        var room = await ReadyPair(a.Client, before, b.Client, b.Profile);
        var entries = f.Services.GetRequiredService<RunEntryService>(); var run = await entries.PrepareAsync(before.PlayerId, room.RoomId, room.ReadyRevision, default);
        try
        {
            var after = await Profile(f, before.PlayerId); var frozen = await Frozen(db, run.RunId, before.PlayerId);
            Assert.False(Assert.Single(after.Items, i => i.InstanceId == bag).EntryBoundBackpack);
            Assert.False(Assert.Single(frozen.CombatAssets.Items, i => i.InstanceId == bag).EntryBoundBackpack);
            Assert.Equal(baseline, await db.QuerySingleAsync<string>("SELECT InstanceId FROM EntryBackpackGrants WHERE CharacterId=@Player", new { Player = before.PlayerId }));
            Assert.Contains(after.Items, item => item.InstanceId == baseline && item.LocationKind == 0 && item.EntryBoundBackpack);
            Assert.True(Assert.Single((await Profile(f, b.Profile.PlayerId)).Items, i => i.LocationKind == 4).EntryBoundBackpack);
        }
        finally { Assert.True(await entries.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default)); }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task InvalidLaterMember_RollsBackEarlierBottle_Bag_Version_AndReservation()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var room = await ReadyPair(a.Client, a.Profile, b.Client, b.Profile);
        string later = StringComparer.Ordinal.Compare(a.Profile.PlayerId, b.Profile.PlayerId) > 0 ? a.Profile.PlayerId : b.Profile.PlayerId;
        await using var db = new MySqlConnection(f.Connection);
        await db.ExecuteAsync("UPDATE Items SET ContentId='invalid.reward.member' WHERE OwnerCharacterId=@Player", new { Player = later });
        var failure = await Assert.ThrowsAsync<OnlineFailure>(() => f.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        Assert.Equal("UnknownContent", failure.Code);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Runs WHERE RoomId=@Room", new { Room = room.RoomId }));
        // 失败只回滚本次入局赠瓶，不能要求注册已提交的两只基础包也消失。
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemLocations WHERE OwnerCharacterId IN (@A,@B) AND CombatLocationKind=8", new { A = a.Profile.PlayerId, B = b.Profile.PlayerId }));
        var bags = await db.QueryAsync<string>("SELECT InstanceId FROM ItemLocations WHERE OwnerCharacterId IN (@A,@B) AND CombatLocationKind=4", new { A = a.Profile.PlayerId, B = b.Profile.PlayerId });
        Assert.Equal(new[] { a.Profile.Items.Single(i => i.LocationKind == 4).InstanceId, b.Profile.Items.Single(i => i.LocationKind == 4).InstanceId }.Order(), bags.Order());
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Characters WHERE CharacterId IN (@A,@B) AND Version=1", new { A = a.Profile.PlayerId, B = b.Profile.PlayerId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task BusinessHostRestart_SameReservationKeepsTheOriginalBottle()
    {
        string player, room, username, bottle; uint revision, version; RunReservation run;
        await using (var f = new OnlineFactory())
        {
            var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync(); var ready = await ReadyPair(a.Client, a.Profile, b.Client, b.Profile);
            player = a.Profile.PlayerId; room = ready.RoomId; username = a.Username; revision = ready.ReadyRevision;
            run = await f.Services.GetRequiredService<RunEntryService>().PrepareAsync(player, room, revision, default);
            var after = await Profile(f, player); bottle = Assert.Single(after.Items, i => i.LocationKind == 8).InstanceId; version = after.Version;
        }
        await using var restarted = new OnlineFactory(); using var client = restarted.CreateClient(); await restarted.LoginAsync(client, username);
        var entries = restarted.Services.GetRequiredService<RunEntryService>();
        Assert.Equal(run, await entries.PrepareAsync(player, room, revision, default));
        var retained = await Profile(restarted, player);
        Assert.Equal(version, retained.Version); Assert.Equal(bottle, Assert.Single(retained.Items, i => i.LocationKind == 8).InstanceId);
        Assert.True(await entries.FailStartAsync(run.RunId, run.RoomEpoch, "Cancelled", default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SameRunRejoin_IncreasesConnectionEpoch_WithoutIssuingAnotherBottleOrChangingFrozenEntry()
    {
        await using var f = new OnlineFactory(instances: true); var reserved = await RoomInstanceTests.Reserve(f);
        var service = f.Services.GetRequiredService<RoomInstanceService>(); var grant = await service.AllocateAsync(reserved.Run, default);
        // 真实控制面契约使用 testhost 的受信进程身份，不启动/冒充 Unity Gameplay。
        using var process = Process.GetCurrentProcess();
        var identity = new InstanceProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!);
        await service.RecordProcessAsync(grant.Descriptor.InstanceId, identity, default);
        await service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, new InstanceReadyRequest
        {
            DungeonId = "p7", RoomEpoch = reserved.Run.RoomEpoch, ProtocolVersion = f.Manifest.ProtocolVersion,
            BuildVersion = f.Manifest.BuildVersion, ManifestHash = f.Manifest.Hash, GameplayConfigHash = OnlineFactory.GameplayHash,
            ProcessId = identity.ProcessId, ProcessStartTicks = identity.StartTicks
        }, default);
        try
        {
            var before = await Profile(f, reserved.A.PlayerId); string bottle = Assert.Single(before.Items, i => i.LocationKind == 8).InstanceId;
            await using var db = new MySqlConnection(f.Connection);
            string frozen = await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = reserved.Run.RunId, Player = reserved.A.PlayerId });
            var ticket = await service.MintTicketAsync(reserved.A.PlayerId, reserved.Run.RoomId, default);
            var first = await service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, new ConsumeJoinTicketRequest { Ticket = ticket.Ticket, PlayerId = reserved.A.PlayerId, RoomEpoch = reserved.Run.RoomEpoch }, default);
            f.Clock.Advance(TimeSpan.FromSeconds(5));
            ticket = await service.MintTicketAsync(reserved.A.PlayerId, reserved.Run.RoomId, default);
            var rejoin = await service.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, new ConsumeJoinTicketRequest { Ticket = ticket.Ticket, PlayerId = reserved.A.PlayerId, RoomEpoch = reserved.Run.RoomEpoch }, default);
            Assert.Equal(first.RunId, rejoin.RunId); Assert.Equal(first.ActorId, rejoin.ActorId); Assert.Equal(first.ConnectionEpoch + 1, rejoin.ConnectionEpoch);
            var after = await Profile(f, reserved.A.PlayerId);
            Assert.Equal(before.Version, after.Version); Assert.Equal(bottle, Assert.Single(after.Items, i => i.LocationKind == 8).InstanceId);
            Assert.Equal(frozen, await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = reserved.Run.RunId, Player = reserved.A.PlayerId }));
        }
        finally { Assert.True(await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default)); }
    }

    private static Task<OnlineProfileReply> Profile(OnlineFactory f, string player)
        => f.Services.GetRequiredService<InventoryService>().GetProfileAsync(player, default);

    private static async Task<OnlineProfileReply> Frozen(MySqlConnection db, string run, string player)
    {
        string json = await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = run, Player = player });
        return JsonSerializer.Deserialize<OnlineProfileReply>(json, OnlineFactory.Json)!;
    }

    private static async Task<string> AddBottle(MySqlConnection db, string player, int slot, ulong quantity)
    {
        string item = Guid.NewGuid().ToString("N");
        await db.ExecuteAsync("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges) VALUES(@Item,@Player,@Content,1,0,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId) VALUES(@Item,@Player,NULL,@Slot,8,NULL);
            INSERT INTO ItemContainerStates(InstanceId,SchemaVersion) VALUES(@Item,1);
            INSERT INTO ItemContainerComponents(InstanceId,MaterialKey,Quantity) VALUES(@Item,1,@Quantity)
            """, new { Item = item, Player = player, Content = BottleContent, Slot = slot, Quantity = quantity });
        return item;
    }

    private static async Task<RoomReply> ReadyPair(HttpClient a, OnlineProfileReply profileA, HttpClient b, OnlineProfileReply profileB)
    {
        var room = await RoomAdmissionTests.Create(a); using var joined = await RoomAdmissionTests.Join(b, room.RoomId);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode); room = await RoomAdmissionTests.View(a, room.RoomId);
        room = await RoomAdmissionTests.Ready(a, room, profileA.Version); return await RoomAdmissionTests.Ready(b, room, profileB.Version);
    }
}
