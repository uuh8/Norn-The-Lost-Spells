using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Norn.Business.Features.Inventory;
using Norn.Business.Content;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>
/// 个人ESC最终事务的隔离业务验证，不是GUI/玩家实操。先反射检查新合同，缺实现时不启动Host或迁移。
/// 全部Actor/物品/Run均由本case的新账号和原Fixture创建；不读取或改写主库/旧测试资产。
/// </summary>
public sealed class RunMemberExitIntegrationTests
{
    private static void Contracts()
    {
        Assert.NotNull(typeof(OnlineProfileReply).Assembly.GetType("Game.NetworkProtocol.OnlineRunMemberExitRequest"));
        Assert.NotNull(typeof(RunAssetCheckpointService).Assembly.GetType("Norn.Business.Features.Runs.RunMemberExitService"));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExitPersistsRealInventoryAndGoldBeforeEndingOnlyThatMember()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var seed = Seed(f, 1); seed.Assets.Gold += 37; seed.Assets.CurrencyVersion++;
        seed.Assets.Items.Single(i => i.LocationKind == 2).RemainingCharges--;
        var reply = await Post(f, seed); Assert.Equal(200, reply.Status);
        using var receipt = JsonDocument.Parse(reply.Text);
        Assert.True(receipt.RootElement.GetProperty("Exited").GetBoolean());
        Assert.Equal(seed.ExpectedPersistentVersion + 1, receipt.RootElement.GetProperty("CommittedVersion").GetUInt32());
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal("Exited", await db.QuerySingleAsync<string>("SELECT State FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run=seed.RunId, Player=seed.PlayerId }));
        Assert.Equal(seed.Assets.Gold, await db.QuerySingleAsync<long>("SELECT Balance FROM Wallets WHERE CharacterId=@Player", new { Player=seed.PlayerId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player", new { Player=seed.PlayerId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PartyMembers WHERE CharacterId=@Player", new { Player=seed.PlayerId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM JoinTickets WHERE InstanceId=@Instance AND CharacterId=@Player", new { Instance=seed.InstanceId, Player=seed.PlayerId }));
        var other = f.Ready.Members.Single(m => m.ActorId == 2);
        Assert.Equal("Admitted", await db.QuerySingleAsync<string>("SELECT State FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run=seed.RunId, Player=other.Profile.PlayerId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player", new { Player=other.Profile.PlayerId }));
        Assert.Equal("Running", await db.QuerySingleAsync<string>("SELECT State FROM Parties WHERE RoomId=@Room", new { Room=f.Grant.Descriptor.RoomId }));
        Assert.Equal(seed.RoomEpoch, await db.QuerySingleAsync<uint>("SELECT RoomEpoch FROM Parties WHERE RoomId=@Room", new { Room=f.Grant.Descriptor.RoomId }));
        Assert.Equal(other.Profile.PlayerId, await db.QuerySingleAsync<string>("SELECT LeaderCharacterId FROM Parties WHERE RoomId=@Room", new { Room=f.Grant.Descriptor.RoomId }));
        var profile = await Profile(f, 1); Assert.False(Locked(profile)); Assert.Equal("", LockedRun(profile));
        Assert.Equal(seed.Assets.Gold, profile.Gold);
        var card = seed.Assets.Items.Single(i => i.LocationKind == 2);
        Assert.Equal(card.RemainingCharges, profile.Items.Single(i => i.InstanceId == card.InstanceId).RemainingCharges);
        var queried = await PlayerQuery(f, 1); Assert.Equal(200, queried.Status);
        Assert.Equal(seed.SettlementId, JsonDocument.Parse(queried.Text).RootElement.GetProperty("SettlementId").GetString());
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SameFixedIdAfterLeaseReleaseReturnsReceiptAndDifferentBodyConflicts()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var seed = Seed(f, 1); var first = await Post(f, seed); Assert.Equal(200, first.Status);
        var repeated = await Post(f, Clone(seed)); Assert.Equal(200, repeated.Status); Assert.Equal(first.Text, repeated.Text);
        seed.Assets.Gold++; seed.Assets.CurrencyVersion++;
        var conflict = await Post(f, seed); Assert.Equal(409, conflict.Status);
        Assert.Equal("RunMemberExitIdempotencyConflict", Code(conflict));
    }

    [Theory, InlineData("connection"), InlineData("version"), InlineData("actor"), InlineData("gold"), InlineData("lease")]
    [Trait("Category", "DatabaseIntegration")]
    public async Task InvalidFenceOrCurrencyCannotPartiallyCommit(string defect)
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var seed = Seed(f, 1);
        switch (defect)
        {
            case "connection": seed.ConnectionEpoch++; break;
            case "version": seed.ExpectedPersistentVersion--; break;
            case "actor": seed.ActorId = 2; seed.Assets.ActorId = 2; break;
            case "gold": seed.Assets.Gold = -1; break;
            case "lease":
                await using (var db = new MySqlConnection(f.Factory.Connection))
                    await db.ExecuteAsync("DELETE FROM AssetLeases WHERE RunId=@Run AND CharacterId=@Player", new { Run=seed.RunId, Player=seed.PlayerId });
                break;
        }
        string before = await f.AssetState(); var rejected = await Post(f, seed); Assert.Equal(409, rejected.Status);
        Assert.Equal(before, await f.AssetState());
    }

    [Theory, InlineData(true), InlineData(false)]
    [Trait("Category", "DatabaseIntegration")]
    public async Task OriginalCrossOwnerItemSettlesOnceInEitherExitOrder(bool originalOwnerFirst)
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var a = Seed(f, 1); var b = Seed(f, 2);
        var bottle = a.Assets.Items.Single(i => i.LocationKind == 8);
        a.Assets.Items = a.Assets.Items.Where(i => i.InstanceId != bottle.InstanceId).ToArray();
        bottle.ParentInstanceId = b.Assets.Items.Single(i => i.LocationKind == 4).InstanceId;
        bottle.LocationKind = 1; bottle.Slot = -1; bottle.X = bottle.Y = 0; bottle.Revision++;
        b.Assets.Items = [.. b.Assets.Items, bottle]; a.Assets.InventoryVersion++; b.Assets.InventoryVersion++;
        Assert.Equal(200, (await Post(f, originalOwnerFirst ? a : b)).Status);
        var stillActive = await Profile(f, originalOwnerFirst ? 2UL : 1UL);
        Assert.True(Locked(stillActive)); Assert.Equal(a.RunId, LockedRun(stillActive));
        Assert.NotNull(stillActive.CombatAssets); Assert.NotEmpty(stillActive.CombatAssets.Items);
        Assert.Equal(200, (await Post(f, originalOwnerFirst ? b : a)).Status);
        var finalA = await Profile(f, 1); var finalB = await Profile(f, 2);
        Assert.DoesNotContain(finalA.Items, i => i.InstanceId == bottle.InstanceId);
        Assert.Single(finalB.Items.Where(i => i.InstanceId == bottle.InstanceId));
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal(b.PlayerId, await db.QuerySingleAsync<string>("SELECT OwnerCharacterId FROM Items WHERE InstanceId=@Item", new { Item=bottle.InstanceId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task NonRunWarehouseItemSurvivesFinalCarriedClosureReplacement()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var seed = Seed(f, 1); string warehouse = Guid.NewGuid().ToString("N");
        await using var db = new MySqlConnection(f.Factory.Connection);
        // 仅自建账号的非入局仓库测试行；退出事务的覆盖集不能扩成“全部Owner物品”。
        await db.ExecuteAsync("""
            INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
              VALUES(@Item,@Player,'wandtest.fire',1,5,FALSE);
            INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot) VALUES(@Item,@Player,'Warehouse',0)
            """, new { Item=warehouse, Player=seed.PlayerId });
        Assert.Equal(200, (await Post(f, seed)).Status);
        Assert.Contains((await Profile(f, 1)).Items, i => i.InstanceId == warehouse && i.Area == "Warehouse");
        Assert.Equal(5, await db.QuerySingleAsync<int>("SELECT RemainingCharges FROM Items WHERE InstanceId=@Item", new { Item=warehouse }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExplicitEmptyHandsPersistWithoutInventingSelectedWandOrLosingTrustedLoadout()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var seed = Seed(f, 1);
        seed.Assets.Items = seed.Assets.Items.Where(i => i.LocationKind is not (2 or 3)).ToArray();
        seed.Assets.SelectedWandInstanceId = ""; seed.Assets.InventoryVersion++;
        Assert.Equal(200, (await Post(f, seed)).Status);
        var profile = await Profile(f, 1);
        Assert.NotNull(profile.CombatAssets); Assert.Equal("", profile.CombatAssets.SelectedWandInstanceId);
        Assert.DoesNotContain(profile.Items, i => i.LocationKind is 2 or 3);
        Assert.Contains(profile.Items, i => i.LocationKind == 4);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task AlreadySettledOriginalIdCannotBeReclaimedByOtherMemberOldEntry()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var a = Seed(f, 1); var b = Seed(f, 2); Assert.Equal(200, (await Post(f, a)).Status);
        var copied = Clone(a.Assets.Items.Single(i => i.LocationKind == 2));
        copied.LocationKind = 1; copied.ParentInstanceId = b.Assets.Items.Single(i => i.LocationKind == 4).InstanceId;
        copied.Slot = -1; copied.X = 3; copied.Y = 0;
        b.Assets.Items = [.. b.Assets.Items, copied]; b.Assets.InventoryVersion++;
        Assert.Equal(409, (await Post(f, b)).Status);
        Assert.Contains((await Profile(f, 1)).Items, i => i.InstanceId == copied.InstanceId);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task RemainingMemberCanPublishCheckpointWithoutReintroducingSettledIds()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var before = Complete(f.Current()); await f.Save(before);
        Assert.Equal(200, (await Post(f, Seed(f, 1))).Status);
        var next = Complete(f.Current()); next.Sequence = 2; next.CheckpointId = Guid.NewGuid().ToString("N");
        next.Members = next.Members.Where(m => m.ActorId == 2).ToArray();
        await f.Save(next);
        var head = await f.Service.ReadHeadAsync(next.InstanceId, f.Grant.Secret!, next.RoomEpoch, default);
        Assert.Single(head.Checkpoint.Members); Assert.Equal(2UL, head.Checkpoint.Members[0].ActorId);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task LaterInstanceFailureNeverOverwritesCommittedMemberOrWallet()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var seed = Seed(f, 1); seed.Assets.Gold += 37; seed.Assets.CurrencyVersion++;
        Assert.Equal(200, (await Post(f, seed)).Status);
        await f.Factory.Services.GetRequiredService<RoomInstanceService>().FailAsync(seed.InstanceId, "Cancelled", default);
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal("Exited", await db.QuerySingleAsync<string>("SELECT State FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run=seed.RunId, Player=seed.PlayerId }));
        Assert.Equal(seed.Assets.Gold, await db.QuerySingleAsync<long>("SELECT Balance FROM Wallets WHERE CharacterId=@Player", new { Player=seed.PlayerId }));
        Assert.Equal(200, (await PlayerQuery(f, 1)).Status);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task OtherAccountCannotQueryTheExitingPlayersReceiptThroughMeRoute()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        Assert.Equal(200, (await Post(f, Seed(f, 1))).Status);
        Assert.Equal(404, (await PlayerQuery(f, 2)).Status);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExitOlderCurrencyRevisionCannotRevertAnAlreadyRecordedGoldFact()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var head = Complete(f.Current()); var member = head.Members.Single(m => m.ActorId == 1);
        member.Assets.Gold += 37; member.Assets.CurrencyVersion++;
        await f.Save(head);
        string before = await f.AssetState();
        // 旧候选的身份/物品仍合法，失败必须来自币水位而非租约或测试限频。
        var rejected = await Post(f, Seed(f, 1)); Assert.Equal(409, rejected.Status);
        Assert.Equal("RunMemberExitCurrencyConflict", Code(rejected));
        Assert.Equal(before, await f.AssetState());
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM RunMemberExitReceipts WHERE RunId=@Run", new { Run=head.RunId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExitMatchingRecordedCurrencyVersionAndGoldRemainsLegal()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var head = Complete(f.Current()); var member = head.Members.Single(m => m.ActorId == 1);
        member.Assets.Gold += 37; member.Assets.CurrencyVersion++;
        await f.Save(head);
        var seed = Seed(f, 1); seed.Assets = Clone(member.Assets);
        var reply = await Post(f, seed); Assert.Equal(200, reply.Status);
        Assert.Equal(member.Assets.Gold, (await Profile(f, 1)).Gold);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SpellStorageFinalSlotPersistsTheSameRealCardAndCharges()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var seed = Seed(f, 1); var card = seed.Assets.Items.Single(i => i.LocationKind == 2);
        card.LocationKind=7; card.ParentInstanceId=""; card.Slot=11;
        card.X=card.Y=card.QuarterTurns=0; card.RemainingCharges--; card.Revision++;
        seed.Assets.InventoryVersion++;
        Assert.Equal(200, (await Post(f, seed)).Status);
        var actual = (await Profile(f, 1)).Items.Single(i => i.InstanceId == card.InstanceId);
        Assert.Equal(7, actual.LocationKind); Assert.Equal(11, actual.Slot);
        Assert.Equal("", actual.ParentInstanceId); Assert.Equal(card.ContentId, actual.ContentId);
        Assert.Equal(card.Revision, actual.Revision); Assert.Equal(card.RemainingCharges, actual.RemainingCharges);
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal(7, await db.QuerySingleAsync<int>(
            "SELECT CombatLocationKind FROM ItemLocations WHERE InstanceId=@Item", new { Item=card.InstanceId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ProvenOlderConnectionAndAuthenticatedMissingReceiptAllowCurrentConnectionToExit()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var old = Seed(f, 1); var instances = f.Factory.Services.GetRequiredService<RoomInstanceService>();
        f.Factory.Clock.Advance(TimeSpan.FromSeconds(6));
        var ticket = await instances.MintTicketAsync(old.PlayerId, f.Grant.Descriptor.RoomId, default);
        var joined = await instances.ConsumeAsync(old.InstanceId, f.Grant.Secret!, new ConsumeJoinTicketRequest
        { Ticket=ticket.Ticket, PlayerId=old.PlayerId, RoomEpoch=old.RoomEpoch }, default);
        Assert.Equal(2u, joined.ConnectionEpoch);
        string before = await f.AssetState();
        var rejected = await Post(f, old); Assert.Equal(409, rejected.Status);
        Assert.Equal("RunMemberExitConnectionSuperseded", Code(rejected));
        Assert.Equal(404, (await InstanceQuery(f, old.SettlementId)).Status);
        Assert.Equal(before, await f.AssetState());
        var current = Seed(f, 1); current.ConnectionEpoch=joined.ConnectionEpoch;
        var committed = await Post(f, current); Assert.Equal(200, committed.Status);
        Assert.Equal(current.SettlementId, JsonDocument.Parse(committed.Text).RootElement.GetProperty("SettlementId").GetString());
        Assert.Equal(200, (await InstanceQuery(f, current.SettlementId)).Status);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FutureConnectionOrMissingReceiptAloneNeverClaimsPermanentSupersession()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var impossible = Seed(f, 1); impossible.ConnectionEpoch=2;
        string before = await f.AssetState();
        var rejected = await Post(f, impossible); Assert.Equal(409, rejected.Status);
        Assert.Equal("RunMemberExitStateConflict", Code(rejected));
        Assert.Equal(404, (await InstanceQuery(f, impossible.SettlementId)).Status);
        Assert.Equal(before, await f.AssetState());
        Assert.Equal(200, (await Post(f, Seed(f, 1))).Status);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task NewBackpackCannotInventAProtectedGrantSourceDuringExit()
    {
        Contracts(); await using var factory = new OnlineFactory(instances:true);
        await using var f = await OrdinaryBackpackFixture(factory);
        var seed = Seed(f, 1); var bag = seed.Assets.Items.Single(i => i.LocationKind == 4);
        Assert.False(bag.EntryBoundBackpack);
        string previous = bag.InstanceId; bag.InstanceId=Guid.NewGuid().ToString("N"); bag.EntryBoundBackpack=true;
        foreach (var item in seed.Assets.Items) if (item.ParentInstanceId==previous) item.ParentInstanceId=bag.InstanceId;
        seed.Assets.InventoryVersion++;
        string before = await f.AssetState();
        var rejected = await Post(f, seed); Assert.Equal(409, rejected.Status);
        Assert.Equal("RunMemberExitOwnershipConflict", Code(rejected));
        Assert.Equal(before, await f.AssetState());
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ReplacingAWandInTheSameSlotLeavesTheOldInstanceInRunCustodyForItsTeammate()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var a=Seed(f,1); var b=Seed(f,2);
        var old=Clone(a.Assets.Items.Single(i=>i.LocationKind==3));
        var replacement=a.Assets.Items.Single(i=>i.InstanceId==old.InstanceId);
        replacement.InstanceId=Guid.NewGuid().ToString("N");
        foreach(var item in a.Assets.Items)if(item.ParentInstanceId==old.InstanceId)item.ParentInstanceId=replacement.InstanceId;
        a.Assets.SelectedWandInstanceId=replacement.InstanceId;a.Assets.InventoryVersion++;
        Assert.Equal(200,(await Post(f,a)).Status);
        var finalA=await Profile(f,1);
        Assert.Contains(finalA.Items,i=>i.InstanceId==replacement.InstanceId&&i.LocationKind==3&&i.Slot==old.Slot);
        Assert.DoesNotContain(finalA.Items,i=>i.InstanceId==old.InstanceId);
        await using var db=new MySqlConnection(f.Factory.Connection);
        Assert.Equal(1,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE InstanceId=@Item",new{Item=old.InstanceId}));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemLocations WHERE InstanceId=@Item",new{Item=old.InstanceId}));
        Assert.Equal("Active",await db.QuerySingleAsync<string>(
            "SELECT State FROM RunItemCustody WHERE RunId=@Run AND InstanceId=@Item",new{Run=a.RunId,Item=old.InstanceId}));
        // 已离开A携带闭包的原杖仍可由队友真实承接，不能通过删Items或塞仓库消掉占槽。
        old.Slot=1;old.Revision++;b.Assets.Items=[..b.Assets.Items,old];b.Assets.InventoryVersion++;
        Assert.Equal(200,(await Post(f,b)).Status);
        var finalB=await Profile(f,2);
        Assert.Single(finalB.Items.Where(i=>i.InstanceId==old.InstanceId));
        Assert.Equal(b.PlayerId,await db.QuerySingleAsync<string>(
            "SELECT OwnerCharacterId FROM Items WHERE InstanceId=@Item",new{Item=old.InstanceId}));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FirstCheckpointAfterAnExitCanKeepTheExitedOwnersActiveCustodyWithoutAnyPriorHead()
    {
        Contracts();await using var f=await RunAssetCheckpointServiceTests.Fixture.Create();
        var a=Seed(f,1);var bottle=a.Assets.Items.Single(i=>i.LocationKind==8);
        a.Assets.Items=a.Assets.Items.Where(i=>i.InstanceId!=bottle.InstanceId).ToArray();a.Assets.InventoryVersion++;
        Assert.Equal(200,(await Post(f,a)).Status);
        await using var db=new MySqlConnection(f.Factory.Connection);
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointHeads WHERE RunId=@Run",new{Run=a.RunId}));
        var next=Complete(f.Current());next.Members=next.Members.Where(m=>m.ActorId==2).ToArray();
        var b=next.Members.Single();bottle.Slot=1;bottle.Revision++;
        b.Assets.Items=[..b.Assets.Items,bottle];b.Assets.InventoryVersion++;
        string before=await f.AssetState();
        var saved=await f.Save(next);Assert.Equal(next.CheckpointId,saved.CheckpointId);
        Assert.Equal(before,await f.AssetState());
        var head=await f.Service.ReadHeadAsync(next.InstanceId,f.Grant.Secret!,next.RoomEpoch,default);
        Assert.Single(head.Checkpoint.Members);
        Assert.Contains(head.Checkpoint.Members[0].Assets.Items,i=>i.InstanceId==bottle.InstanceId&&i.Slot==1);
        Assert.Equal("Active",await db.QuerySingleAsync<string>(
            "SELECT State FROM RunItemCustody WHERE RunId=@Run AND InstanceId=@Item",new{Run=a.RunId,Item=bottle.InstanceId}));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExitedMembersRecordedLossesCannotDisappearButCanRemainWithoutResourceMembership()
    {
        Contracts();await using var f=await RunAssetCheckpointServiceTests.Fixture.Create();
        var first=Complete(f.Current());var a=first.Members.Single(m=>m.ActorId==1);
        first.LostMaterials=ExitedLossFacts(a.PlayerId,1);await f.Save(first);
        Assert.Equal(200,(await Post(f,Seed(f,1))).Status);
        var next=Complete(f.Current());next.Sequence=2;next.Members=next.Members.Where(m=>m.ActorId==2).ToArray();
        var omitted=await Assert.ThrowsAsync<OnlineFailure>(()=>f.Save(next));
        Assert.Equal("RunCheckpointCompleteInvalid",omitted.Code);
        next.LostMaterials=ExitedLossFacts(a.PlayerId,2);
        var changedActor=await Assert.ThrowsAsync<OnlineFailure>(()=>f.Save(next));
        Assert.Equal("RunCheckpointCompleteInvalid",changedActor.Code);
        next.LostMaterials=Clone(first.LostMaterials);string before=await f.AssetState();
        var saved=await f.Save(next);Assert.Equal(2UL,saved.Sequence);Assert.Equal(before,await f.AssetState());
        var head=await f.Service.ReadHeadAsync(next.InstanceId,f.Grant.Secret!,next.RoomEpoch,default);
        Assert.Single(head.Checkpoint.Members);Assert.Equal(2UL,head.Checkpoint.Members[0].ActorId);
        Assert.Equal(JsonSerializer.Serialize(first.LostMaterials,OnlineFactory.Json),
            JsonSerializer.Serialize(head.Checkpoint.LostMaterials,OnlineFactory.Json));
        await using var db=new MySqlConnection(f.Factory.Connection);
        Assert.Equal("Exited",await db.QuerySingleAsync<string>(
            "SELECT State FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player",new{Run=first.RunId,Player=a.PlayerId}));
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE CharacterId=@Player",new{Player=a.PlayerId}));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExitedLossesWithoutPriorHeadNeedTheRealReceiptPlayerAndActor()
    {
        Contracts();await using var f=await RunAssetCheckpointServiceTests.Fixture.Create();
        var a=Seed(f,1);Assert.Equal(200,(await Post(f,a)).Status);
        await using var db=new MySqlConnection(f.Factory.Connection);
        Assert.Equal(0,await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointHeads WHERE RunId=@Run",new{Run=a.RunId}));
        var next=Complete(f.Current());next.Members=next.Members.Where(m=>m.ActorId==2).ToArray();
        next.LostMaterials=ExitedLossFacts(Guid.NewGuid().ToString("N"),1);
        Assert.Equal("RunCheckpointCompleteInvalid",(await Assert.ThrowsAsync<OnlineFailure>(()=>f.Save(next))).Code);
        next.LostMaterials=ExitedLossFacts(a.PlayerId,2);
        Assert.Equal("RunCheckpointCompleteInvalid",(await Assert.ThrowsAsync<OnlineFailure>(()=>f.Save(next))).Code);
        next.LostMaterials=ExitedLossFacts(a.PlayerId,1);string before=await f.AssetState();
        await f.Save(next);Assert.Equal(before,await f.AssetState());
        var head=await f.Service.ReadHeadAsync(next.InstanceId,f.Grant.Secret!,next.RoomEpoch,default);
        Assert.Single(head.Checkpoint.Members);Assert.Equal(2,head.Checkpoint.LostMaterials.Length);
        Assert.All(head.Checkpoint.LostMaterials,l=>{Assert.Equal(a.PlayerId,l.PlayerId);Assert.Equal(1UL,l.ActorId);});
    }

    private static OnlineRunAssetCheckpointLostMaterial[] ExitedLossFacts(string player,ulong actor)=>
    [
        new(){PlayerId=player,ActorId=actor,Source=OnlineRunAssetCheckpointLostMaterial.ContainerTransitSource,MaterialKey=1,Quantity=7},
        new(){PlayerId=player,ActorId=actor,Source=OnlineRunAssetCheckpointLostMaterial.SpellOutputSource,MaterialKey=1,Quantity=5}
    ];

    private static async Task<RunAssetCheckpointServiceTests.Fixture> OrdinaryBackpackFixture(OnlineFactory factory)
    {
        // 注册现在已经拥有真实基础包；把本case普通包放仓库，再沿正式原子换包命令装备。
        // 保留原grant及其真实旧包，不能为构造普通来源而删除来源历史或直接占用装备唯一槽。
        var a = await factory.NewPlayerAsync(); var b = await factory.NewPlayerAsync();
        string bag = Guid.NewGuid().ToString("N");
        var content = factory.Services.GetRequiredService<ContentManifestValidator>();
        await using (var db = new MySqlConnection(factory.Connection))
            await db.ExecuteAsync("""
                INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
                  VALUES(@Item,@Player,@Content,1,0,FALSE);
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind)
                  VALUES(@Item,@Player,'Warehouse',0,NULL);
                UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player
                """, new { Item=bag, Player=a.Profile.PlayerId, Content=content.RequireEntryBackpack().ContentId });
        var inventory = factory.Services.GetRequiredService<InventoryService>();
        var profileA = await inventory.GetProfileAsync(a.Profile.PlayerId,default);
        string originalGrant = profileA.Items.Single(item => item.EntryBoundBackpack).InstanceId;
        using (var swapped = await a.Client.PostAsJsonAsync("/api/online/inventory/move", new InventoryMoveRequest {
            CommandId = Guid.NewGuid().ToString("N"), ExpectedVersion = profileA.Version, InstanceId = bag,
            OperationKind = 1, TargetLocationKind = 4, Slot = 0 }, OnlineFactory.Json))
            Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        profileA = await inventory.GetProfileAsync(a.Profile.PlayerId,default);
        Assert.Contains(profileA.Items, item => item.InstanceId == originalGrant && item.EntryBoundBackpack && item.LocationKind == 0);
        Assert.Contains(profileA.Items, item => item.InstanceId == bag && !item.EntryBoundBackpack && item.LocationKind == 4);
        var room = await RoomAdmissionTests.Create(a.Client);
        using var join = await RoomAdmissionTests.Join(b.Client,room.RoomId); Assert.Equal(HttpStatusCode.OK,join.StatusCode);
        room = await RoomAdmissionTests.View(a.Client,room.RoomId);
        room = await RoomAdmissionTests.Ready(a.Client,room,profileA.Version);
        room = await RoomAdmissionTests.Ready(b.Client,room,b.Profile.Version);
        var reservation = await factory.Services.GetRequiredService<RunEntryService>()
            .PrepareAsync(profileA.PlayerId,room.RoomId,room.ReadyRevision,default);
        var instances = factory.Services.GetRequiredService<RoomInstanceService>();
        var grant = await instances.AllocateAsync(reservation,default);
        using var process = Process.GetCurrentProcess();
        var identity = new InstanceProcessIdentity(process.Id,process.StartTime.ToUniversalTime().Ticks,process.MainModule!.FileName!);
        await instances.RecordProcessAsync(grant.Descriptor.InstanceId,identity,default);
        var ready = await instances.ReadyAsync(grant.Descriptor.InstanceId,grant.Secret!,new InstanceReadyRequest
        { DungeonId="p7",RoomEpoch=reservation.RoomEpoch,ProtocolVersion=factory.Manifest.ProtocolVersion,
            BuildVersion=factory.Manifest.BuildVersion,ManifestHash=factory.Manifest.Hash,GameplayConfigHash=OnlineFactory.GameplayHash,
            ProcessId=identity.ProcessId,ProcessStartTicks=identity.StartTicks },default);
        foreach (var member in ready.Members)
        {
            var ticket = await instances.MintTicketAsync(member.Profile.PlayerId,reservation.RoomId,default);
            await instances.ConsumeAsync(grant.Descriptor.InstanceId,grant.Secret!,new ConsumeJoinTicketRequest
            { Ticket=ticket.Ticket,PlayerId=member.Profile.PlayerId,RoomEpoch=reservation.RoomEpoch },default);
        }
        return new() { Factory=factory,Grant=grant,Ready=ready,
            Service=new(factory.Services.GetRequiredService<RoomTransactions>(),instances,content) };
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task LastReliableMemberExitDiscardsOnlyWorldRemainderAndCompletesIdempotently()
    {
        Contracts(); await using var f = await RunAssetCheckpointServiceTests.Fixture.Create();
        var a = Seed(f, 1); var b = Seed(f, 2);
        var bottle = a.Assets.Items.Single(i => i.LocationKind == 8);
        a.Assets.Items = a.Assets.Items.Where(i => i.InstanceId != bottle.InstanceId).ToArray(); a.Assets.InventoryVersion++;
        Assert.Equal(200, (await Post(f, a)).Status);
        // 还有真正成员留局，不能因第一个玩家主动退出而关闭实例。
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal("Running", await db.QuerySingleAsync<string>("SELECT State FROM Runs WHERE RunId=@Run", new { Run = a.RunId }));
        Assert.Equal(200, (await Post(f, b)).Status);
        var final = Complete(f.Current()); final.Members = [];
        bottle.LocationKind = 5; bottle.ParentInstanceId = ""; bottle.Slot = -1;
        bottle.X = bottle.Y = bottle.QuarterTurns = 0; bottle.Revision++;
        final.GroundItems = [new() { WorldOwnerId = Guid.NewGuid().ToString("N"), Item = bottle,
            Position = new(), Rotation = new() { W = 1 } }];
        final.Coins = [new() { Id = 5, Amount = 13, Position = new() }];
        var saved = await f.Service.SaveTerminalAsync(final.InstanceId, f.Grant.Secret!, final.RoomEpoch, final, default);
        var request = new InstanceCompleteRequest { RunId = final.RunId, RoomEpoch = final.RoomEpoch,
            CheckpointId = saved.CheckpointId, Sequence = saved.Sequence, PayloadHash = saved.PayloadHash };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, OnlineFactory.Json);
        Task<Microsoft.AspNetCore.Http.HttpContext> Send() => f.Factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "POST";
            context.Request.Path = "/api/instances/" + final.InstanceId + "/complete";
            context.Request.Headers["X-Norn-Instance-Key"] = f.Grant.Secret!;
            context.Request.ContentType = "application/json"; context.Request.ContentLength = json.Length;
            context.Request.Body = new MemoryStream(json);
            // TestServer直接构造HttpContext不会自动建立Kestrel的HasBody特征；此处确有原JSON正文。
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new TerminalRequestBodyPresent());
        });
        string personal = await f.AssetState();
        var firstReply = await Send();
        using (var reader = new StreamReader(firstReply.Response.Body))
        {
            string error = (await reader.ReadToEndAsync()).Split("HEADERS", 2, StringSplitOptions.None)[0];
            Assert.True(firstReply.Response.StatusCode == 200, "Complete HTTP " + firstReply.Response.StatusCode + ": " + error);
        }
        Assert.Equal(200, (await Send()).Response.StatusCode); // 原Head重复请求不能再次结算或卡住。
        Assert.Equal(personal, await f.AssetState());
        Assert.Equal("Completed", await db.QuerySingleAsync<string>("SELECT State FROM Runs WHERE RunId=@Run", new { Run = final.RunId }));
        Assert.Equal("Completed", await db.QuerySingleAsync<string>("SELECT State FROM ServerInstances WHERE InstanceId=@Id", new { Id = final.InstanceId }));
        Assert.Equal("Retired", await db.QuerySingleAsync<string>("SELECT State FROM RunItemCustody WHERE RunId=@Run AND InstanceId=@Id", new { Run = final.RunId, Id = bottle.InstanceId }));
        Assert.True(await db.QuerySingleAsync<bool>("SELECT IsRetired FROM RunAssetCheckpointIdentities WHERE RunId=@Run AND InstanceId=@Id", new { Run = final.RunId, Id = bottle.InstanceId }));
        Assert.False(await f.Factory.Services.GetRequiredService<RoomInstanceService>().FailAsync(final.InstanceId, "Cancelled", default));
    }

    private sealed class TerminalRequestBodyPresent : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    { public bool CanHaveBody => true; }

    private static ExitSeed Seed(RunAssetCheckpointServiceTests.Fixture f, ulong actor)
    {
        var m = f.Ready.Members.Single(m => m.ActorId == actor);
        return new() { SettlementId=Guid.NewGuid().ToString("N"), InstanceId=f.Grant.Descriptor.InstanceId,
            RunId=f.Grant.Descriptor.RunId, RoomEpoch=f.Grant.Descriptor.RoomEpoch, PlayerId=m.Profile.PlayerId,
            ActorId=m.ActorId, ConnectionEpoch=1, ExpectedPersistentVersion=m.Profile.Version, Assets=Clone(m.Profile.CombatAssets) };
    }
    private sealed class ExitSeed
    {
        public uint SchemaVersion=1, RoomEpoch, ConnectionEpoch, ExpectedPersistentVersion;
        public string SettlementId="", InstanceId="", RunId="", PlayerId="";
        public ulong ActorId;
        public OnlineOwnedAssetSnapshot Assets=null!;
    }
    private static async Task<(int Status,string Text)> Post(RunAssetCheckpointServiceTests.Fixture f, ExitSeed body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, OnlineFactory.Json));
        var context = await f.Factory.Server.SendAsync(c =>
        {
            c.Connection.RemoteIpAddress=IPAddress.Loopback; c.Request.Method="POST";
            c.Request.Path="/api/instances/"+body.InstanceId+"/member-exits";
            c.Request.Headers["X-Norn-Instance-Key"]=f.Grant.Secret!;
            c.Request.ContentType="application/json"; c.Request.ContentLength=bytes.Length; c.Request.Body=new MemoryStream(bytes);
        });
        if (context.Response.Body.CanSeek) context.Response.Body.Position=0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen:true);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }
    private static async Task<OnlineProfileReply> Profile(RunAssetCheckpointServiceTests.Fixture f, ulong actor)
    {
        var member=f.Ready.Members.Single(m=>m.ActorId==actor); using var client=f.Factory.CreateClient();
        await f.Factory.LoginAsync(client, member.Profile.Username);
        return (await client.GetFromJsonAsync<OnlineProfileReply>("/api/online/profile",OnlineFactory.Json))!;
    }
    private static async Task<(int Status,string Text)> PlayerQuery(RunAssetCheckpointServiceTests.Fixture f, ulong actor)
    {
        var member=f.Ready.Members.Single(m=>m.ActorId==actor); using var client=f.Factory.CreateClient();
        await f.Factory.LoginAsync(client, member.Profile.Username);
        using var response=await client.GetAsync("/api/online/runs/"+f.Grant.Descriptor.RunId+"/member-exit");
        return ((int)response.StatusCode,await response.Content.ReadAsStringAsync());
    }
    private static async Task<(int Status,string Text)> InstanceQuery(RunAssetCheckpointServiceTests.Fixture f,string settlement)
    {
        var context=await f.Factory.Server.SendAsync(c=>
        {
            c.Connection.RemoteIpAddress=IPAddress.Loopback;c.Request.Method="GET";
            c.Request.Path="/api/instances/"+f.Grant.Descriptor.InstanceId+"/member-exits/"+settlement;
            c.Request.QueryString=new Microsoft.AspNetCore.Http.QueryString("?roomEpoch="+f.Grant.Descriptor.RoomEpoch);
            c.Request.Headers["X-Norn-Instance-Key"]=f.Grant.Secret!;
        });
        if(context.Response.Body.CanSeek)context.Response.Body.Position=0;
        using var reader=new StreamReader(context.Response.Body,Encoding.UTF8,leaveOpen:true);
        return(context.Response.StatusCode,await reader.ReadToEndAsync());
    }
    private static OnlineRunAssetCheckpoint Complete(OnlineRunAssetCheckpoint source)
    {
        var value=RunAssetCheckpointServiceTests.Complete(source);
        var coins=typeof(OnlineRunAssetCheckpoint).GetField("Coins");
        if(coins!=null) coins.SetValue(value,Array.CreateInstance(coins.FieldType.GetElementType()!,0));
        return value;
    }
    private static string Code((int Status,string Text) response)
    { using var json=JsonDocument.Parse(response.Text); return json.RootElement.GetProperty("Code").GetString()!; }
    private static bool Locked(OnlineProfileReply profile)
    { var field=typeof(OnlineProfileReply).GetField("AssetsLocked");Assert.NotNull(field);return (bool)field!.GetValue(profile)!; }
    private static string LockedRun(OnlineProfileReply profile)
    { var field=typeof(OnlineProfileReply).GetField("ActiveRunId");Assert.NotNull(field);return (string)field!.GetValue(profile)!; }
    private static T Clone<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,OnlineFactory.Json),OnlineFactory.Json)!;
}
