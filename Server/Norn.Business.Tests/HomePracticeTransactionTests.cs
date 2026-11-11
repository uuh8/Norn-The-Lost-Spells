using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Inventory;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Norn.Business.Features.Trading;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>只验证可信Home退出的业务事务/作用域，不把压缩壳运输样本称为真实Core资源恢复或玩家验收。</summary>
public sealed class HomePracticeTransactionTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task FailureAfterHomeAdmissionPreservesTheCustodyInsteadOfRegrantingTheFrozenEntry()
    {
        await using var f = await Fixture.Create();
        var member = Assert.Single(f.Ready.Members);
        await using var db = new MySqlConnection(f.Factory.Connection);
        int active = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunItemCustody WHERE RunId=@Run AND State='Active'", new { Run = f.Grant.Descriptor.RunId });
        Assert.True(active > 0);
        Assert.True(await f.Factory.Services.GetRequiredService<RoomInstanceService>().FailAsync(f.Grant.Descriptor.InstanceId, "Cancelled", default));
        Assert.Equal(active, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunItemCustody WHERE RunId=@Run AND State='Active'", new { Run = f.Grant.Descriptor.RunId }));
        var frozen = await f.Inventory.GetProfileAsync(member.Profile.PlayerId, default, f.Scope);
        Assert.True(frozen.AssetsLocked); Assert.Equal(f.Grant.Descriptor.RunId, frozen.ActiveRunId);
        var rejected = await Assert.ThrowsAsync<OnlineFailure>(() => f.Factory.Services.GetRequiredService<RunEntryService>().PrepareSingleAsync(
            member.Profile.PlayerId, new SinglePlayerEntryRequest { CommandId = Guid.NewGuid().ToString("N"), DungeonId = "home",
                ExpectedInventoryVersion = frozen.Version }, default, f.Scope));
        Assert.Equal("RunAssetsPendingRecovery", rejected.Code);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SameSessionReadsBackTheCommittedContinuityAndReloginHidesItWithoutDeletingHistory()
    {
        await using var f = await Fixture.Create(); var request = f.Request(true);
        var receipt = await f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, request, default);
        Assert.Equal(request.PracticeSessionScope, receipt.PracticeSessionScope);
        Assert.Equal(request.PracticeContinuityHash, receipt.PracticeContinuityHash);
        var profile = await f.Inventory.GetProfileAsync(request.PlayerId, default, f.Scope);
        Assert.Equal(request.PracticeContinuityJson, profile.PracticeContinuityJson);
        Assert.Equal(f.Scope, profile.PracticeSessionScope);
        string beforeReplay = await f.State();
        var again = await f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, Clone(request), default);
        Assert.Equal(receipt.PayloadHash, again.PayloadHash); Assert.Equal(beforeReplay, await f.State());
        using var otherSession = f.Factory.CreateClient();
        string token = await f.Factory.LoginAsync(otherSession, f.Username);
        string newScope = Norn.Business.Features.Accounts.AccountService.PracticeSessionScope(token);
        Assert.NotEqual(f.Scope, newScope);
        var fresh = await f.Inventory.GetProfileAsync(request.PlayerId, default, newScope);
        Assert.Equal("", fresh.PracticeContinuityJson); Assert.Equal(newScope, fresh.PracticeSessionScope);
        Assert.Equal(beforeReplay, await f.State());
    }

    [Theory, InlineData("death"), InlineData("success"), InlineData("scope")]
    [Trait("Category", "DatabaseIntegration")]
    public async Task HomeCannotClearCarriedAssetsClaimSuccessProgressOrReplaceTheFrozenSessionScope(string defect)
    {
        await using var f = await Fixture.Create(); var request = f.Request(defect == "scope"); string expected;
        switch (defect)
        {
            case "death":
                request.Reason = OnlineRunMemberExitReason.FinalDeath;
                request.FinalRetiredItems = request.Assets.Items.Where(i => i.LocationKind != 4).Select(i => new OnlineRunAssetCheckpointRetired
                { InstanceId = i.InstanceId, ContentId = i.ContentId, Revision = i.Revision, Reason = OnlineRunAssetRetiredReason.Destroyed }).ToArray();
                expected = "HomeFinalDeathForbidden"; break;
            case "success": request.SuccessfulExtraction = true; request.CollectedGmu = 1; expected = "HomeSuccessfulExtractionForbidden"; break;
            default: request.PracticeSessionScope = new string('c', 64); expected = "PracticeSessionScopeConflict"; break;
        }
        string before = await f.State();
        var rejected = await Assert.ThrowsAsync<OnlineFailure>(() => f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, request, default));
        Assert.Equal(expected, rejected.Code); Assert.Equal(before, await f.State());
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, OnlineFactory.Json), OnlineFactory.Json)!;

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HomeTerminalHeadKeepsReceiptedGroundAndDiscardsWorldWithoutRegrantOrSecondRetirement()
    {
        await using var f = await Fixture.Create(); var finalized = await FinalizeHomeWithGroundAndRealRetirement(f);
        var service = f.Factory.Services.GetRequiredService<RunAssetCheckpointService>();
        var saved = await service.SaveTerminalAsync(finalized.Exit.InstanceId, f.Grant.Secret!, finalized.Exit.RoomEpoch, finalized.Terminal, default);
        var profile = await f.Inventory.GetProfileAsync(finalized.Exit.PlayerId, default, f.Scope);
        string before = JsonSerializer.Serialize(profile, OnlineFactory.Json);
        var head = await service.ReadHeadAsync(finalized.Exit.InstanceId, f.Grant.Secret!, finalized.Exit.RoomEpoch, default);
        Assert.True(head.IsComplete); Assert.Empty(head.Checkpoint.GroundItems);
        Assert.Equal(Assert.Single(finalized.Terminal.RetiredItems).InstanceId, Assert.Single(head.Checkpoint.RetiredItems).InstanceId);
        Assert.NotEmpty(head.Checkpoint.World.Chunks); // 世界余量只随实例结束，不自动发回包或兑换金币。
        var complete = new InstanceCompleteRequest { RunId = finalized.Exit.RunId, RoomEpoch = finalized.Exit.RoomEpoch,
            CheckpointId = saved.CheckpointId, Sequence = saved.Sequence, PayloadHash = saved.PayloadHash };
        var instances = f.Factory.Services.GetRequiredService<RoomInstanceService>();
        Assert.True((await instances.CompleteAsync(finalized.Exit.InstanceId, f.Grant.Secret!, complete, default)).Completed);
        Assert.True((await instances.CompleteAsync(finalized.Exit.InstanceId, f.Grant.Secret!, Clone(complete), default)).Completed);
        Assert.Equal(before, JsonSerializer.Serialize(await f.Inventory.GetProfileAsync(finalized.Exit.PlayerId, default, f.Scope), OnlineFactory.Json));
        await using var db = new MySqlConnection(f.Factory.Connection);
        var ground = Assert.Single(finalized.Exit.PracticeGroundItems);
        Assert.Equal("Settled", await db.QuerySingleAsync<string>("SELECT State FROM RunItemCustody WHERE RunId=@Run AND InstanceId=@Id",
            new { Run = finalized.Exit.RunId, Id = ground.Item.InstanceId }));
        Assert.Equal("Retired", await db.QuerySingleAsync<string>("SELECT State FROM RunItemCustody WHERE RunId=@Run AND InstanceId=@Id",
            new { Run = finalized.Exit.RunId, Id = finalized.Exit.FinalRetiredItems[0].InstanceId }));
        Assert.Equal("Completed", await db.QuerySingleAsync<string>("SELECT State FROM Runs WHERE RunId=@Run", new { Run = finalized.Exit.RunId }));
    }

    [Theory, InlineData("content"), InlineData("revision"), InlineData("reason"), InlineData("ground"), InlineData("unknown")]
    [Trait("Category", "DatabaseIntegration")]
    public async Task HomeTerminalHeadCannotAlterReceiptRetirementsResurrectHandedOffGroundOrCreateNewFacts(string defect)
    {
        await using var f = await Fixture.Create(); var finalized = await FinalizeHomeWithGroundAndRealRetirement(f);
        var terminal = finalized.Terminal;
        if (defect == "content") terminal.RetiredItems[0].ContentId = finalized.Exit.PracticeGroundItems[0].Item.ContentId;
        if (defect == "revision") terminal.RetiredItems[0].Revision++;
        if (defect == "reason") terminal.RetiredItems[0].Reason = OnlineRunAssetRetiredReason.Destroyed;
        if (defect == "ground") terminal.GroundItems = Clone(finalized.Exit.PracticeGroundItems);
        if (defect == "unknown") terminal.RetiredItems[0].InstanceId = Guid.NewGuid().ToString("N");
        string before = await f.State();
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Factory.Services.GetRequiredService<RunAssetCheckpointService>()
            .SaveTerminalAsync(finalized.Exit.InstanceId, f.Grant.Secret!, finalized.Exit.RoomEpoch, terminal, default));
        Assert.Equal(before, await f.State());
    }

    private static async Task<(OnlineRunMemberExitRequest Exit, OnlineRunAssetCheckpoint Terminal)> FinalizeHomeWithGroundAndRealRetirement(Fixture f)
    {
        var exit = f.Request(true); var ground = DropWand(exit, Guid.NewGuid().ToString("N"));
        var spent = Assert.Single(ground.Children); ground.Children = [];
        exit.FinalRetiredItems = [new() { InstanceId = spent.InstanceId, ContentId = spent.ContentId,
            Revision = spent.Revision + 1, Reason = OnlineRunAssetRetiredReason.Consumed }];
        await f.Exits.CommitAsync(exit.InstanceId, f.Grant.Secret!, exit, default);
        // DS已经按最终收据移交了地面资产，但原Journal不应Clear；本终态保留尚未周期ACK的真实退休。
        // 这正是个人Exit领先周期Head的正常时序，业务需用最终收据证明这一精确子集已持久化。
        return (exit, new OnlineRunAssetCheckpoint { SchemaVersion = OnlineRunAssetCheckpoint.CompleteSchemaVersion,
            CheckpointId = Guid.NewGuid().ToString("N"), InstanceId = exit.InstanceId, RunId = exit.RunId,
            RoomEpoch = exit.RoomEpoch, Sequence = 1, ServerTick = 100, GameplayConfigHash = OnlineFactory.GameplayHash,
            Members = [], GroundItems = [], RetiredItems = Clone(exit.FinalRetiredItems),
            World = RunCheckpointWorldRulesTests.Valid(), Flights = [], MaterialOutputs = [], LostMaterials = [], Coins = [] });
    }

    [Theory, InlineData(false), InlineData(true)]
    [Trait("Category", "DatabaseIntegration")]
    public async Task HomeGroundPreservesTheOriginalWandChildrenAndPoseAndReentersAsKnownWorldAssets(bool mapLegacyOwner)
    {
        await using var f = await Fixture.Create(); var request = f.Request(true);
        string rawOwner = (mapLegacyOwner ? "scene.home.map1." : "") + Guid.NewGuid().ToString("N");
        var ground = DropWand(request, rawOwner); string rootId = ground.Item.InstanceId, cardId = Assert.Single(ground.Children).InstanceId;
        string expectedOwner = mapLegacyOwner ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "Norn.HomeGround.v1:" + request.RunId + ":" + rawOwner)))[..32] : rawOwner;
        await f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, request, default);
        var profile = await f.Inventory.GetProfileAsync(request.PlayerId, default, f.Scope);
        var world = Assert.Single(profile.WorldItems);
        Assert.Equal(rootId, world.InstanceId); Assert.Equal(expectedOwner, world.WorldOwnerId);
        Assert.Equal("home.map1", world.Pose.SceneKey); Assert.Equal("home.map1", world.Pose.WorldSceneKey);
        Assert.Equal(ground.Position.X, world.Pose.PositionX); Assert.Equal(ground.Position.Y, world.Pose.PositionY); Assert.Equal(ground.Position.Z, world.Pose.PositionZ);
        Assert.Equal(ground.Rotation.W, world.Pose.RotationW);
        Assert.Contains(profile.Items, i => i.InstanceId == rootId && i.LocationKind == 5 && i.Revision == ground.Item.Revision);
        Assert.Contains(profile.Items, i => i.InstanceId == cardId && i.LocationKind == 2 && i.ParentInstanceId == rootId && i.RemainingCharges == ground.Children[0].RemainingCharges);
        Assert.DoesNotContain(profile.CombatAssets.Items, i => i.InstanceId == rootId || i.InstanceId == cardId);
        Assert.Equal(request.Assets.Gold, profile.Gold);
        var entries = f.Factory.Services.GetRequiredService<RunEntryService>();
        var next = await entries.PrepareSingleAsync(request.PlayerId, new SinglePlayerEntryRequest {
            CommandId = Guid.NewGuid().ToString("N"), DungeonId = "home", ExpectedInventoryVersion = profile.Version }, default, f.Scope);
        try
        {
            await using var db = new MySqlConnection(f.Factory.Connection);
            var frozen = JsonSerializer.Deserialize<OnlineProfileReply>(await db.QuerySingleAsync<string>(
                "SELECT EntrySnapshot FROM RunMembers WHERE RunId=@Run AND CharacterId=@Player", new { Run = next.RunId, Player = request.PlayerId }), OnlineFactory.Json)!;
            Assert.Equal(profile.Items.Length, frozen.Items.Length); Assert.Equal(profile.Version, frozen.Version);
            Assert.Equal(expectedOwner, Assert.Single(frozen.WorldItems).WorldOwnerId);
            Assert.DoesNotContain(frozen.CombatAssets.Items, i => i.InstanceId == rootId || i.InstanceId == cardId);
            var held = (await db.QueryAsync<string>("SELECT InstanceId FROM RunItemCustody WHERE RunId=@Run AND State='Active'", new { Run = next.RunId })).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(rootId, held); Assert.Contains(cardId, held);
            Assert.Contains(frozen.CombatAssets.Items.Single(i => i.LocationKind == 4).InstanceId, held);
        }
        finally { await entries.FailStartAsync(next.RunId, next.RoomEpoch, "Cancelled", default); }
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HomeGroundReplacementNeverDeletesTheSeparateHubSceneRecord()
    {
        await using var f = await Fixture.Create("hub"); var request = f.Request(true); DropWand(request, Guid.NewGuid().ToString("N"));
        await f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, request, default);
        var profile = await f.Inventory.GetProfileAsync(request.PlayerId, default, f.Scope);
        Assert.Equal(2, profile.WorldItems.Length);
        Assert.Equal(f.HubGroundJson, JsonSerializer.Serialize(profile.WorldItems.Single(w => w.InstanceId == f.HubGroundId), OnlineFactory.Json));
        Assert.Single(profile.WorldItems, w => w.Pose.WorldSceneKey == "home.map1");
        Assert.Equal(Assert.Single(f.Ready.Members).Profile.Gold, profile.Gold);
    }

    [Theory, InlineData("consume", false), InlineData("merge", false), InlineData("destroy", false)]
    [InlineData("consume", true), InlineData("merge", true), InlineData("destroy", true)]
    [Trait("Category", "DatabaseIntegration")]
    public async Task HomeRetiresOnlyExplicitRealJournalFactsAndPreservesTheRemainingIdentityAndAmounts(string cause, bool checkpointFirst)
    {
        await using var f = await Fixture.Create(cause == "merge" ? "material-pair" : cause == "destroy" ? "bottle" : null);
        var request = f.Request(true); OnlineOwnedItemSnapshot source; string? targetId = null;
        var reason = OnlineRunAssetRetiredReason.Consumed;
        if (cause == "merge")
        {
            var materials = request.Assets.Items.Where(i => i.MaterialState != null).OrderBy(i => i.InstanceId, StringComparer.Ordinal).ToArray();
            Assert.Equal(2, materials.Length); source = materials[1]; targetId = materials[0].InstanceId;
            materials[0].MaterialState.Quantity += source.MaterialState.Quantity; materials[0].Revision++; reason = OnlineRunAssetRetiredReason.Merged;
        }
        else if (cause == "destroy") { source = request.Assets.Items.Single(i => i.ContainerState != null); reason = OnlineRunAssetRetiredReason.Destroyed; }
        else source = request.Assets.Items.Single(i => i.LocationKind == 2);
        request.Assets.Items = request.Assets.Items.Where(i => i.InstanceId != source.InstanceId).ToArray(); request.Assets.InventoryVersion++;
        request.FinalRetiredItems = [new() { InstanceId = source.InstanceId, ContentId = source.ContentId, Revision = source.Revision + 1, Reason = reason }];
        if (checkpointFirst)
        {
            // 真实周期Journal先落库会把此身份custody标为Retired，但Items/旧位置仍保留到最终事务。
            // 不能只测退出领先Head；已确认的退休证据也不能令合法携带父物的安装/清位被拒。
            await f.Factory.Services.GetRequiredService<RunAssetCheckpointService>().SaveAsync(request.InstanceId, f.Grant.Secret!, request.RoomEpoch,
                new OnlineRunAssetCheckpoint { SchemaVersion = OnlineRunAssetCheckpoint.CurrentSchemaVersion,
                    CheckpointId = Guid.NewGuid().ToString("N"), InstanceId = request.InstanceId, RunId = request.RunId,
                    RoomEpoch = request.RoomEpoch, Sequence = 1, ServerTick = 100, GameplayConfigHash = OnlineFactory.GameplayHash,
                    Members = [new() { PlayerId = request.PlayerId, ActorId = request.ActorId, ConnectionEpoch = request.ConnectionEpoch,
                        Assets = Clone(request.Assets) }], GroundItems = [], RetiredItems = Clone(request.FinalRetiredItems) }, default);
            await using var checkpointDb = new MySqlConnection(f.Factory.Connection);
            Assert.Equal("Retired", await checkpointDb.QuerySingleAsync<string>(
                "SELECT State FROM RunItemCustody WHERE RunId=@Run AND InstanceId=@Id", new { Run = request.RunId, Id = source.InstanceId }));
        }
        var receipt = await f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, request, default);
        Assert.Equal(reason, Assert.Single(receipt.FinalRetiredItems).Reason);
        var profile = await f.Inventory.GetProfileAsync(request.PlayerId, default, f.Scope);
        Assert.DoesNotContain(profile.Items, i => i.InstanceId == source.InstanceId); Assert.Equal(request.Assets.Gold, profile.Gold);
        Assert.Single(profile.Items, i => i.LocationKind == 4 && i.EntryBoundBackpack);
        if (targetId != null) Assert.Equal(2UL, profile.Items.Single(i => i.InstanceId == targetId).MaterialState.Quantity);
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal("Retired", await db.QuerySingleAsync<string>("SELECT State FROM RunItemCustody WHERE RunId=@Run AND InstanceId=@Id", new { Run = request.RunId, Id = source.InstanceId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE InstanceId=@Id", new { Id = source.InstanceId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ItemLocations WHERE InstanceId=@Id", new { Id = source.InstanceId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HomeMissingOriginalWithoutAnExplicitRetirementKeepsTheLiveRunAndAllAssets()
    {
        await using var f = await Fixture.Create(); var request = f.Request(true);
        string card = request.Assets.Items.Single(i => i.LocationKind == 2).InstanceId;
        request.Assets.Items = request.Assets.Items.Where(i => i.InstanceId != card).ToArray(); request.Assets.InventoryVersion++;
        string before = await f.State();
        var error = await Assert.ThrowsAsync<OnlineFailure>(() => f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, request, default));
        Assert.Equal("PracticeRetirementRequired", error.Code); Assert.Equal(before, await f.State());
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HomeGroundCannotAdoptAnotherAccountsRealWandOrItsInstalledCard()
    {
        await using var f = await Fixture.Create(); var other = await f.Factory.NewPlayerAsync(); var request = f.Request(true);
        var otherAssets = AccountInventoryMoves.AccountSnapshot(other.Profile, f.Factory.Services.GetRequiredService<Norn.Business.Content.ContentManifestValidator>());
        var wand = otherAssets.Items.Single(i => i.LocationKind == 3); var child = otherAssets.Items.Single(i => i.LocationKind == 2);
        wand.LocationKind = 5; wand.Slot = -1; wand.ParentInstanceId = "";
        request.PracticeGroundItems = [new() { WorldOwnerId = Guid.NewGuid().ToString("N"), Item = wand, Children = [child],
            Position = new() { X = 2, Y = .2f, Z = 3 }, Rotation = new() { W = 1 } }];
        string before = await f.State();
        var error = await Assert.ThrowsAsync<OnlineFailure>(() => f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, request, default));
        Assert.Equal("RunMemberExitOwnershipConflict", error.Code); Assert.Equal(before, await f.State());
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HomeRetirementCannotOverlapAnItemStillPresentInTheGroundClosure()
    {
        await using var f = await Fixture.Create(); var request = f.Request(true); var ground = DropWand(request, Guid.NewGuid().ToString("N"));
        request.FinalRetiredItems = [new() { InstanceId = ground.Item.InstanceId, ContentId = ground.Item.ContentId, Revision = ground.Item.Revision, Reason = OnlineRunAssetRetiredReason.Destroyed }];
        string before = await f.State();
        var error = await Assert.ThrowsAsync<OnlineFailure>(() => f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, request, default));
        Assert.Equal("RunMemberExitInvalid", error.Code); Assert.Equal(before, await f.State());
    }

    [Theory, InlineData(true, 1UL, true), InlineData(false, 0UL, false), InlineData(true, 0UL, false)]
    [Trait("Category", "DatabaseIntegration")]
    public async Task TrustedRaidSuccessRefreshesOriginalShopRulesExactlyOnceWhileIdleEscDoesNot(bool success, ulong collected, bool refresh)
    {
        await using var f = await Fixture.Create("shop-spent", "p7"); var request = f.Request(false);
        request.SuccessfulExtraction = success; request.CollectedGmu = collected;
        var receipt = await f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, request, default);
        var shops = f.Factory.Services.GetRequiredService<AccountShopService>();
        var after = await shops.GetAsync(request.PlayerId, default); var before = f.ShopBeforeRun!;
        var config = f.Factory.Manifest.Shop; var offer = config.Offers.Single(o => o.ContentId == "containertest.bottle");
        Assert.Equal(refresh ? config.PurchaseBudget : before.Shop.RemainingBudget, after.Shop.RemainingBudget);
        Assert.Equal(refresh ? offer.InitialStock : before.Shop.Stocks.Single(s => s.OfferId == offer.OfferId).Remaining,
            after.Shop.Stocks.Single(s => s.OfferId == offer.OfferId).Remaining);
        Assert.Equal(request.Assets.Gold, after.Profile.Gold); Assert.Equal(request.Assets.Items.Length, after.Profile.CombatAssets.Items.Length);
        string state = await f.State();
        Assert.Equal(receipt.PayloadHash, (await f.Exits.CommitAsync(request.InstanceId, f.Grant.Secret!, Clone(request), default)).PayloadHash);
        Assert.Equal(state, await f.State());
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal(refresh ? 1L : 0L, await db.QuerySingleAsync<long>("SELECT AdventureProgress FROM CharacterEconomyProgress WHERE CharacterId=@Player", new { Player = request.PlayerId }));
        Assert.Equal(refresh, await db.QuerySingleAsync<bool>("SELECT ShopProgressApplied FROM RunMemberExitReceipts WHERE RunId=@Run AND CharacterId=@Player", new { Run = request.RunId, Player = request.PlayerId }));
    }

    private static OnlineRunAssetCheckpointGround DropWand(OnlineRunMemberExitRequest request, string owner)
    {
        var wand = request.Assets.Items.Single(i => i.LocationKind == 3);
        var children = request.Assets.Items.Where(i => i.ParentInstanceId == wand.InstanceId).ToArray();
        request.Assets.Items = request.Assets.Items.Where(i => i.InstanceId != wand.InstanceId && i.ParentInstanceId != wand.InstanceId).ToArray();
        request.Assets.SelectedWandInstanceId = ""; request.Assets.InventoryVersion++;
        wand.LocationKind = 5; wand.ParentInstanceId = ""; wand.Slot = -1; wand.Revision++;
        var ground = new OnlineRunAssetCheckpointGround { WorldOwnerId = owner, Item = wand, Children = children,
            Position = new() { X = 2, Y = .2f, Z = 3 }, Rotation = new() { W = 1 } };
        request.PracticeGroundItems = [ground]; return ground;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required OnlineFactory Factory; public required string Username, Scope; public required InstanceLaunchGrant Grant;
        public required InstanceReadyReply Ready; public required RunMemberExitService Exits; public required InventoryService Inventory;
        public string? HubGroundJson, HubGroundId; public AccountShopReply? ShopBeforeRun;
        public static async Task<Fixture> Create(string? setup = null, string dungeon = "home")
        {
            var factory = new OnlineFactory(instances: true); var a = await factory.NewPlayerAsync();
            string scope = Norn.Business.Features.Accounts.AccountService.PracticeSessionScope(a.Token);
            var inventory = factory.Services.GetRequiredService<InventoryService>(); var starting = a.Profile;
            string? hubJson = null, hubId = null; AccountShopReply? shopBefore = null;
            if (setup != null)
            {
                var shops = factory.Services.GetRequiredService<AccountShopService>(); var shop = await shops.GetAsync(a.Profile.PlayerId, default);
                var offer = factory.Manifest.Shop.Offers.Single(o => o.ContentId == (setup == "material-pair" ? "containertest.ice" : "containertest.bottle"));
                var buy = new AccountShopTradeRequest { CommandId = Guid.NewGuid().ToString("N"), ShopId = shop.Shop.ShopId, ConfigHash = shop.Shop.ConfigHash,
                    ExpectedVersion = shop.Profile.Version, ExpectedShopVersion = shop.Shop.Version, Kind = 0, Quantity = setup == "material-pair" ? 2UL : 1UL,
                    OfferId = offer.OfferId, ToBackpack = true, AllowWarehouseFallback = true, BackpackId = shop.Profile.Items.Single(i => i.LocationKind == 4).InstanceId };
                await shops.TradeAsync(a.Profile.PlayerId, buy, default); shop = await shops.GetAsync(a.Profile.PlayerId, default);
                if (setup == "shop-spent")
                {
                    var bottle = shop.Profile.Items.Single(i => i.ContentId == offer.ContentId);
                    await shops.TradeAsync(a.Profile.PlayerId, new AccountShopTradeRequest { CommandId = Guid.NewGuid().ToString("N"), ShopId = shop.Shop.ShopId,
                        ConfigHash = shop.Shop.ConfigHash, ExpectedVersion = shop.Profile.Version, ExpectedShopVersion = shop.Shop.Version, Kind = 1, Quantity = 1, ItemInstanceId = bottle.InstanceId }, default);
                    shopBefore = await shops.GetAsync(a.Profile.PlayerId, default);
                }
                else if (setup == "hub")
                {
                    var bottle = shop.Profile.Items.Single(i => i.ContentId == offer.ContentId);
                    await inventory.TryApplyInventoryAsync(a.Profile.PlayerId, new InventoryMoveRequest { CommandId = Guid.NewGuid().ToString("N"),
                        InstanceId = bottle.InstanceId, ExpectedVersion = shop.Profile.Version, OperationKind = 4, TargetLocationKind = 5, Slot = -1,
                        WorldPose = new() { SceneKey = "hub.map1", WorldSceneKey = "hub.map1", PoseKind = 2, PositionX = 1, PositionY = .2f, PositionZ = 1 } }, default);
                }
                starting = await inventory.GetProfileAsync(a.Profile.PlayerId, default, scope);
                if (setup == "hub") { var world = Assert.Single(starting.WorldItems); hubId = world.InstanceId; hubJson = JsonSerializer.Serialize(world, OnlineFactory.Json); }
            }
            var entries = factory.Services.GetRequiredService<RunEntryService>();
            var run = await entries.PrepareSingleAsync(a.Profile.PlayerId, new SinglePlayerEntryRequest {
                CommandId = Guid.NewGuid().ToString("N"), DungeonId = dungeon, ExpectedInventoryVersion = starting.Version }, default, scope);
            var instances = factory.Services.GetRequiredService<RoomInstanceService>(); var grant = await instances.AllocateAsync(run, default);
            // 沿既有实例业务fixture登记当前测试Host身份，不Process.Start、不构建或启动真实DS。
            using var process = Process.GetCurrentProcess();
            var identity = new InstanceProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!);
            await instances.RecordProcessAsync(grant.Descriptor.InstanceId, identity, default);
            var ready = await instances.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, new InstanceReadyRequest {
                DungeonId = dungeon, RoomEpoch = run.RoomEpoch, ProtocolVersion = factory.Manifest.ProtocolVersion, BuildVersion = factory.Manifest.BuildVersion,
                ManifestHash = factory.Manifest.Hash, GameplayConfigHash = OnlineFactory.GameplayHash, ProcessId = identity.ProcessId, ProcessStartTicks = identity.StartTicks }, default);
            var ticket = await instances.MintTicketAsync(a.Profile.PlayerId, run.RoomId, default);
            await instances.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, new ConsumeJoinTicketRequest {
                Ticket = ticket.Ticket, PlayerId = a.Profile.PlayerId, RoomEpoch = run.RoomEpoch }, default);
            return new Fixture { Factory = factory, Username = a.Username, Scope = scope, Grant = grant, Ready = ready,
                Exits = factory.Services.GetRequiredService<RunMemberExitService>(), Inventory = inventory, HubGroundJson = hubJson, HubGroundId = hubId, ShopBeforeRun = shopBefore };
        }
        public OnlineRunMemberExitRequest Request(bool continuity)
        {
            var member = Assert.Single(Ready.Members); var assets = Clone(member.Profile.CombatAssets);
            string json = "", hash = "";
            if (continuity)
            {
                // 最小平面DTO仅用于业务壳运输；真实Owner Wands/Deck/Trace由Unity Core独立严格测试。
                string raw = JsonSerializer.Serialize(new { SchemaVersion = 1, PlayerId = assets.PlayerId, InventoryId = assets.InventoryId,
                    GameplayConfigHash = OnlineFactory.GameplayHash, SelectedWandId = "", InventoryVersion = assets.InventoryVersion,
                    ResourceVersion = 1, SpellUseVersion = 1, Wands = Array.Empty<object>(), Library = Array.Empty<object>(), Replays = Array.Empty<object>() }, OnlineFactory.Json);
                Assert.True(PracticeContinuityEnvelopeRules.TryPack(assets.PlayerId, OnlineFactory.GameplayHash, raw, out var envelope, out string error), error);
                json = JsonSerializer.Serialize(envelope, OnlineFactory.Json); hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            }
            return new OnlineRunMemberExitRequest { SchemaVersion = OnlineRunMemberExitRequest.CurrentSchemaVersion,
                SettlementId = Guid.NewGuid().ToString("N"), InstanceId = Grant.Descriptor.InstanceId, RunId = Grant.Descriptor.RunId,
                PlayerId = assets.PlayerId, RoomEpoch = Grant.Descriptor.RoomEpoch, ConnectionEpoch = 1,
                ExpectedPersistentVersion = member.Profile.Version, ActorId = member.ActorId, Assets = assets,
                Reason = OnlineRunMemberExitReason.VoluntaryExit, FinalRetiredItems = [], FinalDeathLostMaterials = [], PracticeGroundItems = [],
                PracticeSessionScope = continuity ? Scope : "", PracticeContinuityJson = json, PracticeContinuityHash = hash };
        }
        public async Task<string> State()
        {
            string player = Assert.Single(Ready.Members).Profile.PlayerId; await using var db = new MySqlConnection(Factory.Connection);
            var items = await db.QueryAsync("SELECT * FROM Items WHERE OwnerCharacterId=@Player ORDER BY InstanceId", new { Player = player });
            var locations = await db.QueryAsync("SELECT * FROM ItemLocations WHERE OwnerCharacterId=@Player ORDER BY InstanceId", new { Player = player });
            var character = await db.QueryAsync("SELECT * FROM Characters WHERE CharacterId=@Player", new { Player = player });
            var wallet = await db.QueryAsync("SELECT * FROM Wallets WHERE CharacterId=@Player", new { Player = player });
            var members = await db.QueryAsync("SELECT * FROM RunMembers WHERE RunId=@Run ORDER BY CharacterId", new { Run = Grant.Descriptor.RunId });
            var leases = await db.QueryAsync("SELECT * FROM AssetLeases WHERE CharacterId=@Player", new { Player = player });
            var receipts = await db.QueryAsync("SELECT * FROM RunMemberExitReceipts WHERE RunId=@Run", new { Run = Grant.Descriptor.RunId });
            var continuity = await db.QueryAsync("SELECT * FROM AccountPracticeContinuity WHERE CharacterId=@Player", new { Player = player });
            var ground = await db.QueryAsync("SELECT * FROM HubWorldItems WHERE OwnerCharacterId=@Player ORDER BY InstanceId", new { Player = player });
            var shops = await db.QueryAsync("SELECT * FROM CharacterShopStates WHERE CharacterId=@Player ORDER BY ShopId", new { Player = player });
            var progress = await db.QueryAsync("SELECT * FROM CharacterEconomyProgress WHERE CharacterId=@Player", new { Player = player });
            // 失败不仅要保持玩家资产，也不能推进托管/Head/幂等回执水位；这些表不是可忽略的旁路。
            var custody = await db.QueryAsync("SELECT * FROM RunItemCustody WHERE RunId=@Run ORDER BY InstanceId", new { Run = Grant.Descriptor.RunId });
            var checkpointHeads = await db.QueryAsync("SELECT * FROM RunAssetCheckpointHeads WHERE RunId=@Run", new { Run = Grant.Descriptor.RunId });
            var checkpointReceipts = await db.QueryAsync("SELECT * FROM RunAssetCheckpointReceipts WHERE RunId=@Run ORDER BY CheckpointId", new { Run = Grant.Descriptor.RunId });
            var identities = await db.QueryAsync("SELECT * FROM RunAssetCheckpointIdentities WHERE RunId=@Run ORDER BY InstanceId", new { Run = Grant.Descriptor.RunId });
            return JsonSerializer.Serialize(new { items, locations, character, wallet, members, leases, receipts, continuity, ground, shops, progress,
                custody, checkpointHeads, checkpointReceipts, identities }, OnlineFactory.Json);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Factory.Services.GetRequiredService<RoomInstanceService>().FailAsync(Grant.Descriptor.InstanceId, "Cancelled", default); }
            finally { await Factory.DisposeAsync(); }
        }
    }
}
