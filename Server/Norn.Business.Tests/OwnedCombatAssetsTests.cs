using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

public sealed class OwnedCombatAssetsTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task Registration_GrantsWandCardAndBaselineBagWithFiniteChargesAndOneCombatAudit()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        // 注册如今在同一事务建立一杖一卡和基础包；战斗发放审计仍只记一次，登录不得重发任何实例。
        Assert.Equal(3, player.Profile.Items.Length);
        Assert.Single(player.Profile.Items, i => i.LocationKind == 4 && i.EntryBoundBackpack);
        var wand = Assert.Single(player.Profile.Items, i => i.LocationKind == 3);
        var card = Assert.Single(player.Profile.Items, i => i.LocationKind == 2);
        Assert.Equal("EquippedWand", wand.Area); Assert.Equal("WandSlot", card.Area);
        Assert.Equal(wand.InstanceId, card.ParentInstanceId); Assert.Equal(0, wand.Slot); Assert.Equal(0, card.Slot);
        Assert.Equal(30, card.RemainingCharges); Assert.False(card.HasUnlimitedCharges);
        Assert.Equal(wand.InstanceId, player.Profile.CombatAssets.SelectedWandInstanceId);
        Assert.Equal(player.Profile.Items.Select(i => i.InstanceId).Order(), player.Profile.CombatAssets.Items.Select(i => i.InstanceId).Order());
        await using var db = new MySqlConnection(f.Connection);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM OwnedCombatStarterAudits WHERE CharacterId=@Player AND Status='Applied'", new { Player = player.Profile.PlayerId }));
        await f.LoginAsync(f.CreateClient(), player.Username);
        Assert.Equal(3, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE OwnerCharacterId=@Player", new { Player = player.Profile.PlayerId }));
        var request = new InventoryMoveRequest { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = 1, InstanceId = wand.InstanceId, Destination = "Warehouse", Slot = 0 };
        using var move = await player.Client.PostAsJsonAsync("/api/online/inventory/move", request, OnlineFactory.Json);
        await RoomAdmissionTests.Error(move, HttpStatusCode.Conflict, "EquippedItemMoveUnsupported");
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FrozenSnapshot_KeepsExactResourcesAndRejectsWrongGameplayHash()
    {
        await using var f = new OnlineFactory(instances: true); var run = await RoomInstanceTests.Reserve(f);
        var service = f.Services.GetRequiredService<RoomInstanceService>(); var grant = await service.AllocateAsync(run.Run, default);
        Assert.Equal(OnlineFactory.GameplayHash, grant.Descriptor.GameplayConfigHash);
        using var process = Process.GetCurrentProcess();
        var identity = new InstanceProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!);
        await service.RecordProcessAsync(grant.Descriptor.InstanceId, identity, default);
        var request = new InstanceReadyRequest { DungeonId = grant.Descriptor.DungeonId, RoomEpoch = run.Run.RoomEpoch, ProtocolVersion = f.Manifest.ProtocolVersion, BuildVersion = f.Manifest.BuildVersion,
            ManifestHash = f.Manifest.Hash, GameplayConfigHash = new string('b', 64), ProcessId = identity.ProcessId, ProcessStartTicks = identity.StartTicks };
        var wrong = await Assert.ThrowsAsync<OnlineFailure>(() => service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default));
        Assert.Equal("ContentVersionMismatch", wrong.Code);
        request.GameplayConfigHash = OnlineFactory.GameplayHash;
        var ready = await service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default);
        foreach (var member in ready.Members)
        {
            var assets = member.Profile.CombatAssets; Assert.Equal(OnlineOwnedAssetSnapshot.CurrentSchemaVersion, assets.SchemaVersion);
            Assert.Equal(run.Run.RunId, assets.RunId); Assert.Equal(member.ActorId, assets.ActorId);
            Assert.Equal(member.Profile.Version, assets.InventoryVersion); Assert.Equal(member.Profile.PlayerId, assets.PlayerId);
            var card = Assert.Single(assets.Items, i => i.LocationKind == 2); Assert.Equal(30, card.RemainingCharges); Assert.False(card.HasUnlimitedCharges);
        }
        await using var db = new MySqlConnection(f.Connection);
        string before = await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = run.Run.RunId, Player = run.A.PlayerId });
        await service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default);
        Assert.Equal(before, await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = run.Run.RunId, Player = run.A.PlayerId }));
        // 模拟业务进程重启后的配置漂移：控制面续租也必须重新核对冻结 Hash，不能只在首次 Ready 检查。
        await db.ExecuteAsync("UPDATE RunMembers SET EntrySnapshot=JSON_SET(EntrySnapshot,'$.CombatAssets.GameplayConfigHash',@Hash) WHERE RunId=@Run AND CharacterId=@Player", new { Hash = new string('b', 64), Run = run.Run.RunId, Player = run.A.PlayerId });
        var drift = await Assert.ThrowsAsync<OnlineFailure>(() => service.HeartbeatAsync(grant.Descriptor.InstanceId, grant.Secret!, run.Run.RoomEpoch, default));
        Assert.Equal("ContentVersionMismatch", drift.Code);
        await service.FailAsync(grant.Descriptor.InstanceId, "Cancelled", default);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task MissingCombatGrant_CannotReserveAnyMember()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection);
        await db.ExecuteAsync("DELETE FROM CharacterCombatLoadouts WHERE CharacterId=@Player", new { Player = b.Profile.PlayerId });
        var room = await RoomAdmissionTests.Create(a.Client); using var join = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId); room = await RoomAdmissionTests.Ready(a.Client, room, 1); room = await RoomAdmissionTests.Ready(b.Client, room, 1);
        var error = await Assert.ThrowsAsync<OnlineFailure>(() => f.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        Assert.Equal("CombatAssetsUnavailable", error.Code);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Runs WHERE RoomId=@Room", new { Room = room.RoomId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId IN (@A,@B)", new { A = a.Profile.PlayerId, B = b.Profile.PlayerId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SqlLocationConstraints_RejectForeignParentAndDuplicateProgramSlot()
    {
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection);
        await db.OpenAsync();
        string card = a.Profile.Items.Single(i => i.LocationKind == 2).InstanceId, foreign = b.Profile.Items.Single(i => i.LocationKind == 3).InstanceId;
        await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync("UPDATE ItemLocations SET ParentInstanceId=@Parent WHERE InstanceId=@Card", new { Parent = foreign, Card = card }));
        string extra = Guid.NewGuid().ToString("N"), parent = a.Profile.Items.Single(i => i.LocationKind == 3).InstanceId;
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision) VALUES(@Id,@Player,'wandtest.fire',1)", new { Id = extra, Player = a.Profile.PlayerId }, tx);
        await Assert.ThrowsAsync<MySqlException>(() => db.ExecuteAsync("INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId) VALUES(@Id,@Player,NULL,0,2,@Parent)", new { Id = extra, Player = a.Profile.PlayerId, Parent = parent }, tx));
        await tx.RollbackAsync();
    }


    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task WholeProfileOver4120Items_IsRejectedBeforeBuildingAnUnboundedOwnedGraph()
    {
        ContainerInventoryTestSeed.RequireMigrationResource();
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        await using var db = new MySqlConnection(f.Connection);
        await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        // 隔离故障数据：数据库外键允许同Owner的长父链，Profile读取必须先按4120硬界限拒绝；
        // 不能靠截取4096 Owned或先递归坏父链来“保证有界”。这不是向正式玩家发放物品。
        // 固定构造硬上限+1，扣除实际注册资产；基础包加入后仍须落在4121这个边界值。
        string[] ids = Enumerable.Range(0, 4121 - player.Profile.Items.Length).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        string wand = player.Profile.Items.Single(i => i.LocationKind == 3).InstanceId;
        var parameters = new DynamicParameters(); parameters.Add("Player", player.Profile.PlayerId);
        var items = new string[ids.Length]; var locations = new string[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            parameters.Add("I" + i, ids[i]); parameters.Add("P" + i, i == 0 ? wand : ids[i - 1]);
            items[i] = $"(@I{i},@Player,'wandtest.fire',1,1,FALSE)";
            locations[i] = $"(@I{i},@Player,NULL,{(i == 0 ? 1 : 0)},2,@P{i},0,0,0)";
        }
        await db.ExecuteAsync("INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges) VALUES "
            + string.Join(",", items) + "; INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId,GridX,GridY,QuarterTurns) VALUES "
            + string.Join(",", locations), parameters, tx);
        await tx.CommitAsync();
        Assert.Equal(4121, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE OwnerCharacterId=@Player", new { Player = player.Profile.PlayerId }));
        using var response = await player.Client.GetAsync("/api/online/profile");
        await RoomAdmissionTests.Error(response, HttpStatusCode.Conflict, "InventoryCapacityExceeded");
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ProfileAndFreeze_KeepRealBagMixedBottleAndEmptyTool_ButExcludeWarehouseWandChildren()
    {
        ContainerInventoryTestSeed.RequireMigrationResource();
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var seed = await ContainerInventoryTestSeed.InsertAsync(f, a.Profile.PlayerId);
        var profile = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(7, profile.Items.Length);
        var bottle = Assert.Single(profile.Items, i => i.InstanceId == seed.Bottle);
        Assert.Equal(seed.Bag, bottle.ParentInstanceId); Assert.Equal(-1, bottle.Slot);
        Assert.Equal(1, bottle.X); Assert.Equal(1, bottle.Y); Assert.Equal(1, bottle.QuarterTurns);
        Assert.Equal(27UL, bottle.ContainerState!.Components.Aggregate(0UL, (sum, p) => checked(sum + p.Quantity)));
        Assert.Empty(Assert.Single(profile.Items, i => i.InstanceId == seed.Tool).ContainerState!.Components);
        Assert.Equal(5, profile.CombatAssets.Items.Length);
        Assert.DoesNotContain(profile.CombatAssets.Items, i => i.InstanceId == seed.WarehouseWand || i.InstanceId == seed.WarehouseCard);

        var room = await RoomAdmissionTests.Create(a.Client);
        using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, profile.Version);
        room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        var run = await f.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default);
        await using var db = new MySqlConnection(f.Connection);
        string json = await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",
            new { Run = run.RunId, Player = a.Profile.PlayerId });
        var frozen = JsonSerializer.Deserialize<OnlineProfileReply>(json, OnlineFactory.Json)!;
        Assert.Equal(run.RunId, frozen.CombatAssets.RunId);
        // 新Run先在同一事务发一只开局空瓶，再冻结真实版本；不能继续把入场前版本当作冻结版本。
        // 这里只检查已批准发瓶带来的精确一次推进，不接受任意版本变化或重新构建冻结资产。
        Assert.Equal(checked(profile.Version + 1), frozen.Version);
        Assert.Equal(frozen.Version, frozen.CombatAssets.InventoryVersion);
        var entered = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(entered.Version, frozen.Version);
        var granted = Assert.Single(frozen.Items, i => !profile.Items.Any(old => old.InstanceId == i.InstanceId));
        Assert.Equal("item.container.container_01", granted.ContentId);
        Assert.Equal(8, granted.LocationKind); Assert.Empty(granted.ContainerState!.Components);
        Assert.Equal(20UL, Assert.Single(frozen.CombatAssets.Items, i => i.InstanceId == seed.Bottle).ContainerState!.Components.Single(p => p.MaterialKey == 1).Quantity);
        // 直接 SQL 仅用于隔离测试故障注入；可靠 EntrySnapshot 不能在稍后的读取中重建或补量。
        await db.ExecuteAsync("UPDATE ItemContainerComponents SET Quantity=19 WHERE InstanceId=@Id AND MaterialKey=1", new { Id = seed.Bottle });
        Assert.Equal(json, await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",
            new { Run = run.RunId, Player = a.Profile.PlayerId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FreezeRejectsLegacyFlatBackpack_InsteadOfInventingBagCoordinates()
    {
        await using var f = new OnlineFactory(); var a = await f.NewInventoryPlayerAsync(); var b = await f.NewPlayerAsync();
        var room = await RoomAdmissionTests.Create(a.Client);
        using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version);
        room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        var error = await Assert.ThrowsAsync<OnlineFailure>(() =>
            f.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        Assert.Equal("LegacyBackpackLayoutUnsupported", error.Code);
        await using var db = new MySqlConnection(f.Connection);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Runs WHERE RoomId=@Room", new { Room = room.RoomId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task OldMoveEndpointRejectsRealGridItem_WithoutChangingParentGridOrVersions()
    {
        ContainerInventoryTestSeed.RequireMigrationResource();
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        var seed = await ContainerInventoryTestSeed.InsertAsync(f, player.Profile.PlayerId);
        var profile = (await player.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var request = new InventoryMoveRequest { CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profile.Version,
            InstanceId = seed.Bottle, Destination = OnlineInventoryAreas.Warehouse, Slot = 3 };
        using var response = await player.Client.PostAsJsonAsync("/api/online/inventory/move", request, OnlineFactory.Json);
        await RoomAdmissionTests.Error(response, HttpStatusCode.Conflict, "EquippedItemMoveUnsupported");
        await using var db = new MySqlConnection(f.Connection);
        Assert.Equal(seed.Bag + ":1:1:1:-1", await db.QuerySingleAsync<string>(
            "SELECT CONCAT(ParentInstanceId,':',GridX,':',GridY,':',QuarterTurns,':',Slot) FROM ItemLocations WHERE InstanceId=@Id", new { Id = seed.Bottle }));
        Assert.Equal(profile.Version, await db.ExecuteScalarAsync<uint>("SELECT Version FROM Characters WHERE CharacterId=@Player", new { Player = player.Profile.PlayerId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM InventoryCommands WHERE CharacterId=@Player AND CommandId=@Command", new { Player = player.Profile.PlayerId, Command = request.CommandId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FreezeSeesAndRejectsContainerStateOnASpell_InsteadOfFilteringItByKind()
    {
        ContainerInventoryTestSeed.RequireMigrationResource();
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var room = await RoomAdmissionTests.Create(a.Client);
        using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version);
        room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        await using var db = new MySqlConnection(f.Connection);
        string card = a.Profile.Items.Single(i => i.LocationKind == 2).InstanceId;
        // 表只约束外键和数字合法性；Kind/组分语义必须由读取后的完整业务规则拒绝，不能 SELECT 时删坏数据。
        await db.ExecuteAsync("INSERT INTO ItemContainerStates(InstanceId,SchemaVersion) VALUES(@Id,1)", new { Id = card });
        var error = await Assert.ThrowsAsync<OnlineFailure>(() =>
            f.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        Assert.Equal("InvalidCombatAsset", error.Code);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Runs WHERE RoomId=@Room", new { Room = room.RoomId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration"), Trait("Feature", "OwnedMaterial")]
    public async Task MaterialProfileAndFrozenEntryKeepActualQuantityAndIndependentState()
    {
        MaterialInventoryTestSeed.RequireMigrationResource();
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var seed = await MaterialInventoryTestSeed.InsertAsync(f, a.Profile.PlayerId);
        var profile = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var view = Assert.Single(profile.Items, i => i.InstanceId == seed.Solid);
        var owned = Assert.Single(profile.CombatAssets.Items, i => i.InstanceId == seed.Solid);
        Assert.Equal(seed.Bag, owned.ParentInstanceId); Assert.Equal(1, owned.LocationKind); Assert.Equal(-1, owned.Slot);
        Assert.Equal(1u, view.MaterialState!.SchemaVersion); Assert.Equal((byte)5, view.MaterialState.MaterialKey);
        Assert.Equal(256UL, view.MaterialState.Quantity); Assert.Equal(256UL, owned.MaterialState!.Quantity);
        Assert.Null(view.ContainerState); Assert.Null(owned.ContainerState);
        Assert.All(profile.CombatAssets.Items.Where(i => i.InstanceId != seed.Solid), i => Assert.Null(i.MaterialState));

        // HTTP反序列化本来就生成新对象；再直接检查真实读取器的两份DTO，防止Profile视图改写待冻结Owned状态。
        var raw = await f.Services.GetRequiredService<Norn.Business.Features.Inventory.InventoryService>().GetProfileAsync(a.Profile.PlayerId, default);
        var rawView = Assert.Single(raw.Items, i => i.InstanceId == seed.Solid).MaterialState!;
        var rawOwned = Assert.Single(raw.CombatAssets.Items, i => i.InstanceId == seed.Solid).MaterialState!;
        Assert.NotSame(rawView, rawOwned); rawView.Quantity = 1;
        Assert.Equal(256UL, rawOwned.Quantity);

        var room = await RoomAdmissionTests.Create(a.Client);
        using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, profile.Version);
        room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        var run = await f.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default);
        await using var db = new MySqlConnection(f.Connection);
        string json = await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",
            new { Run = run.RunId, Player = a.Profile.PlayerId });
        var frozen = JsonSerializer.Deserialize<OnlineProfileReply>(json, OnlineFactory.Json)!;
        var frozenSolid = Assert.Single(frozen.CombatAssets.Items, i => i.InstanceId == seed.Solid);
        Assert.Equal(run.RunId, frozen.CombatAssets.RunId);
        // 保留真实固体的原数量断言，同时核查开局发瓶之后的精确版本，不能拿入场前Profile覆盖它。
        Assert.Equal(checked(profile.Version + 1), frozen.Version);
        Assert.Equal(frozen.Version, frozen.CombatAssets.InventoryVersion);
        var entered = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        Assert.Equal(entered.Version, frozen.Version);
        var granted = Assert.Single(frozen.Items, i => !profile.Items.Any(old => old.InstanceId == i.InstanceId));
        Assert.Equal("item.container.container_01", granted.ContentId);
        Assert.Equal(8, granted.LocationKind); Assert.Empty(granted.ContainerState!.Components);
        Assert.Equal(1u, frozenSolid.MaterialState!.SchemaVersion); Assert.Equal((byte)5, frozenSolid.MaterialState.MaterialKey);
        Assert.Equal(256UL, frozenSolid.MaterialState.Quantity);
        Assert.Equal(256UL, Assert.Single(frozen.Items, i => i.InstanceId == seed.Solid).MaterialState!.Quantity);
        // 本测试只注入隔离账户的后续状态漂移；已经冻结的JSON不得通过重读模板/容量重建。
        await db.ExecuteAsync("UPDATE ItemMaterialStates SET Quantity=255 WHERE InstanceId=@Id", new { Id = seed.Solid });
        Assert.Equal(json, await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",
            new { Run = run.RunId, Player = a.Profile.PlayerId }));
    }

    [Theory, InlineData("missing-material-state"), InlineData("material-on-spell"),
        Trait("Category", "DatabaseIntegration"), Trait("Feature", "OwnedMaterial")]
    public async Task InvalidMaterialStateRejectsProfileAndFreezeWithoutCreatingRunOrLease(string defect)
    {
        MaterialInventoryTestSeed.RequireMigrationResource();
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var room = await RoomAdmissionTests.Create(a.Client);
        using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version);
        room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        await using var db = new MySqlConnection(f.Connection);
        if (defect == "missing-material-state")
            await MaterialInventoryTestSeed.InsertAsync(f, a.Profile.PlayerId, includeState: false, advanceVersion: false);
        else
        {
            string card = a.Profile.Items.Single(i => i.LocationKind == 2).InstanceId;
            await db.ExecuteAsync("INSERT INTO ItemMaterialStates(InstanceId,SchemaVersion,MaterialKey,Quantity) VALUES(@Id,1,5,256)", new { Id = card });
        }
        // Ready后故障注入不改版本，专门命中真实读取/状态门，不能由别的StaleVersion检查替它通过。
        string expected = defect == "missing-material-state" ? "InvalidCombatMaterial" : "InvalidCombatAsset";
        using var response = await a.Client.GetAsync("/api/online/profile");
        await RoomAdmissionTests.Error(response, HttpStatusCode.Conflict, expected);
        var error = await Assert.ThrowsAsync<OnlineFailure>(() => f.Services.GetRequiredService<RunEntryService>()
            .PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        Assert.Equal(expected, error.Code);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Runs WHERE RoomId=@Room", new { Room = room.RoomId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId IN (@A,@B)", new { A = a.Profile.PlayerId, B = b.Profile.PlayerId }));
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "DatabaseIntegration"), Trait("Feature", "OwnedAttachment")]
    public async Task AttachmentProfileAndFrozenEntryKeepMissingSourceExactDoseAndIndependentState(bool enabled)
    {
        SpellAttachmentInventoryTestSeed.RequireMigrationResource();
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        string card = a.Profile.Items.Single(i => i.LocationKind == 2).InstanceId;
        string missingSource = Guid.NewGuid().ToString("N");
        await using var db = new MySqlConnection(f.Connection);
        await db.ExecuteAsync("""
            INSERT INTO ItemSpellAttachmentStates(InstanceId,SchemaVersion,ContainerId,Dose,Enabled)
            VALUES(@Id,1,@Source,@Dose,@Enabled);
            UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player
            """, new { Id = card, Source = missingSource, Dose = ulong.MaxValue, Enabled = enabled, Player = a.Profile.PlayerId });
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE InstanceId=@Id", new { Id = missingSource }));
        var profile = (await a.Client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile", OnlineFactory.Json))!;
        var view = Assert.Single(profile.Items, i => i.InstanceId == card);
        var owned = Assert.Single(profile.CombatAssets.Items, i => i.InstanceId == card);
        var viewSetting = Assert.IsType<OnlineOwnedSpellAttachmentStateSnapshot>(view.AttachmentState);
        var ownedSetting = Assert.IsType<OnlineOwnedSpellAttachmentStateSnapshot>(owned.AttachmentState);
        Assert.Equal(1u, viewSetting.SchemaVersion); Assert.Equal(missingSource, viewSetting.ContainerId);
        Assert.Equal(ulong.MaxValue, viewSetting.Dose); Assert.Equal(enabled, viewSetting.Enabled);
        Assert.Equal(ulong.MaxValue, ownedSetting.Dose); Assert.Equal(enabled, ownedSetting.Enabled);
        Assert.Null(view.MaterialState); Assert.Null(view.ContainerState);
        Assert.All(profile.Items.Where(i => i.InstanceId != card), i => Assert.Null(i.AttachmentState));

        // 直接读取器的两个DTO也必须各自拥有设置值，不能只依靠HTTP反序列化意外复制。
        var raw = await f.Services.GetRequiredService<Norn.Business.Features.Inventory.InventoryService>().GetProfileAsync(a.Profile.PlayerId, default);
        var rawView = Assert.Single(raw.Items, i => i.InstanceId == card).AttachmentState!;
        var rawOwned = Assert.Single(raw.CombatAssets.Items, i => i.InstanceId == card).AttachmentState!;
        Assert.NotSame(rawView, rawOwned); rawView.Dose = 1; rawView.Enabled = !enabled;
        Assert.Equal(ulong.MaxValue, rawOwned.Dose); Assert.Equal(enabled, rawOwned.Enabled);

        var room = await RoomAdmissionTests.Create(a.Client);
        using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, profile.Version);
        room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        var run = await f.Services.GetRequiredService<RunEntryService>().PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default);
        string json = await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",
            new { Run = run.RunId, Player = a.Profile.PlayerId });
        var frozen = JsonSerializer.Deserialize<OnlineProfileReply>(json, OnlineFactory.Json)!;
        var frozenSetting = Assert.Single(frozen.CombatAssets.Items, i => i.InstanceId == card).AttachmentState;
        Assert.NotNull(frozenSetting); Assert.Equal(1u, frozenSetting.SchemaVersion);
        Assert.Equal(missingSource, frozenSetting.ContainerId); Assert.Equal(ulong.MaxValue, frozenSetting.Dose); Assert.Equal(enabled, frozenSetting.Enabled);
        Assert.Equal(ulong.MaxValue, Assert.Single(frozen.Items, i => i.InstanceId == card).AttachmentState!.Dose);
        Assert.Equal(view.RemainingCharges, Assert.Single(frozen.CombatAssets.Items, i => i.InstanceId == card).RemainingCharges);
        await db.ExecuteAsync("UPDATE ItemSpellAttachmentStates SET Dose=1,Enabled=@Enabled WHERE InstanceId=@Id", new { Id = card, Enabled = !enabled });
        Assert.Equal(json, await db.QuerySingleAsync<string>("SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",
            new { Run = run.RunId, Player = a.Profile.PlayerId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration"), Trait("Feature", "OwnedAttachment")]
    public async Task NonSpellAttachmentStateCannotHideFromProfileOrFreezeAndCannotCreateRunOrLease()
    {
        SpellAttachmentInventoryTestSeed.RequireMigrationResource();
        await using var f = new OnlineFactory(); var a = await f.NewPlayerAsync(); var b = await f.NewPlayerAsync();
        var room = await RoomAdmissionTests.Create(a.Client);
        using var joined = await RoomAdmissionTests.Join(b.Client, room.RoomId); Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        room = await RoomAdmissionTests.View(a.Client, room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client, room, a.Profile.Version);
        room = await RoomAdmissionTests.Ready(b.Client, room, b.Profile.Version);
        await using var db = new MySqlConnection(f.Connection);
        string wand = a.Profile.Items.Single(i => i.LocationKind == 3).InstanceId;
        // 准备完成后注入一个合法数字但Kind错误的设置，不改版本，以免StaleVersion提前挡住真正读取门。
        await db.ExecuteAsync("INSERT INTO ItemSpellAttachmentStates(InstanceId,SchemaVersion,ContainerId,Dose,Enabled) VALUES(@Id,1,@Source,10,TRUE)",
            new { Id = wand, Source = Guid.NewGuid().ToString("N") });
        using var response = await a.Client.GetAsync("/api/online/profile");
        await RoomAdmissionTests.Error(response, HttpStatusCode.Conflict, "InvalidCombatAsset");
        var error = await Assert.ThrowsAsync<OnlineFailure>(() => f.Services.GetRequiredService<RunEntryService>()
            .PrepareAsync(a.Profile.PlayerId, room.RoomId, room.ReadyRevision, default));
        Assert.Equal("InvalidCombatAsset", error.Code);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Runs WHERE RoomId=@Room", new { Room = room.RoomId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId IN (@A,@B)", new { A = a.Profile.PlayerId, B = b.Profile.PlayerId }));
    }
}

internal static class SpellAttachmentInventoryTestSeed
{
    // 缺资源先明确RED，避免无008时启动Host后才得到不存在表的间接噪声。
    internal static void RequireMigrationResource()
        => Assert.Contains(typeof(SchemaMigrator).Assembly.GetManifestResourceNames(), n => n.EndsWith("008_spell_attachment_inventory.sql", StringComparison.Ordinal));
}

/// <summary>只给随机隔离测试账户建立真实固体持有行；不是商店、注册赠送或P7持久结算入口。</summary>
internal static class MaterialInventoryTestSeed
{
    internal readonly record struct Seed(string Bag, string Solid);
    internal static void RequireMigrationResource()
        => Assert.Contains(typeof(SchemaMigrator).Assembly.GetManifestResourceNames(), n => n.EndsWith("007_material_inventory.sql", StringComparison.Ordinal));

    internal static async Task<Seed> InsertAsync(OnlineFactory f, string player, bool includeState = true, bool advanceVersion = true)
    {
        RequireMigrationResource();
        Assert.Contains(f.Manifest.Items, i => i.ContentId == "containertest.ice" && i.Kind == 4 && i.MaterialKey == 5 && i.MaterialItemCapacityGmu >= 256);
        await using var db = new MySqlConnection(f.Connection); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("SELECT CharacterId FROM Characters WHERE CharacterId=@Player FOR UPDATE", new { Player = player }, tx);
        // 复用当前注册已授予的唯一装备包；夹具只补材料，不制造第二个同槽装备包绕过唯一归属约束。
        string bag = await db.QuerySingleAsync<string>("SELECT InstanceId FROM ItemLocations WHERE OwnerCharacterId=@Player AND CombatLocationKind=4", new { Player = player }, tx);
        var seed = new Seed(bag, Guid.NewGuid().ToString("N"));
        await db.ExecuteAsync("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
            VALUES(@Solid,@Player,'containertest.ice',1,0,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId,GridX,GridY,QuarterTurns)
            VALUES(@Solid,@Player,NULL,-1,1,@Bag,0,0,0)
            """, new { seed.Bag, seed.Solid, Player = player }, tx);
        if (includeState) await db.ExecuteAsync("INSERT INTO ItemMaterialStates(InstanceId,SchemaVersion,MaterialKey,Quantity) VALUES(@Id,1,5,256)", new { Id = seed.Solid }, tx);
        if (advanceVersion) await db.ExecuteAsync("UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player", new { Player = player }, tx);
        await tx.CommitAsync(); return seed;
    }
}
