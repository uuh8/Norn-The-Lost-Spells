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
    public async Task Registration_GrantsExactlyTwoRealEquippedInstancesWithFiniteChargesAndOneAudit()
    {
        await using var f = new OnlineFactory(); var player = await f.NewPlayerAsync();
        Assert.Equal(2, player.Profile.Items.Length);
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
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE OwnerCharacterId=@Player", new { Player = player.Profile.PlayerId }));
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
        var request = new InstanceReadyRequest { RoomEpoch = run.Run.RoomEpoch, ProtocolVersion = f.Manifest.ProtocolVersion, BuildVersion = f.Manifest.BuildVersion,
            ManifestHash = f.Manifest.Hash, GameplayConfigHash = new string('b', 64), ProcessId = identity.ProcessId, ProcessStartTicks = identity.StartTicks };
        var wrong = await Assert.ThrowsAsync<OnlineFailure>(() => service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default));
        Assert.Equal("ContentVersionMismatch", wrong.Code);
        request.GameplayConfigHash = OnlineFactory.GameplayHash;
        var ready = await service.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, request, default);
        foreach (var member in ready.Members)
        {
            var assets = member.Profile.CombatAssets; Assert.Equal(1u, assets.SchemaVersion);
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
}
