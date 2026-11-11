using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Rooms;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>由主线程集中运行的真实隔离MySQL测试；保留Items不变，不能据此宣称ESC保存已经完成。</summary>
public sealed class RunAssetCheckpointServiceTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SaveStoresCheckpointAndReceiptButLeavesAllRealAssetsAndLeasesUntouched()
    {
        await using var f = await Fixture.Create();
        string before = await f.AssetState(); var checkpoint = f.Current();
        var card = checkpoint.Members[0].Assets.Items.First(i => i.ContentId == "wandtest.fire");
        card.RemainingCharges--; checkpoint.Members[0].Assets.InventoryVersion++;
        var receipt = await f.Save(checkpoint);
        Assert.Equal(checkpoint.CheckpointId, receipt.CheckpointId); Assert.Equal(1UL, receipt.Sequence);
        Assert.Equal(checkpoint.RunId, receipt.RunId); Assert.Equal(64, receipt.PayloadHash.Length);
        Assert.Equal(before, await f.AssetState());
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointHeads WHERE RunId=@Run", new { Run = checkpoint.RunId }));
        var stored = await db.QuerySingleAsync<string>("SELECT PayloadJson FROM RunAssetCheckpointHeads WHERE RunId=@Run", new { Run = checkpoint.RunId });
        var snapshot = JsonSerializer.Deserialize<OnlineRunAssetCheckpoint>(stored, OnlineFactory.Json)!;
        Assert.Equal(card.RemainingCharges, snapshot.Members.Single(m => m.PlayerId == checkpoint.Members[0].PlayerId)
            .Assets.Items.Single(i => i.InstanceId == card.InstanceId).RemainingCharges);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task SameIdSameContentReturnsDurableReceiptAndDifferentContentConflicts()
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current();
        var first = await f.Save(checkpoint); var retry = await f.Save(Clone(checkpoint));
        Assert.Equal(JsonSerializer.Serialize(first, OnlineFactory.Json), JsonSerializer.Serialize(retry, OnlineFactory.Json));
        var reordered = Clone(checkpoint); Array.Reverse(reordered.Members);
        foreach (var member in reordered.Members) Array.Reverse(member.Assets.Items);
        var sameValues = await f.Save(reordered); Assert.Equal(first.PayloadHash, sameValues.PayloadHash);
        checkpoint.ServerTick++;
        var failure = await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(checkpoint));
        Assert.Equal("RunCheckpointIdempotencyConflict", failure.Code);
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointReceipts WHERE RunId=@Run", new { Run = checkpoint.RunId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task JournalSurvivesServiceRecreationAndCanResolveAnUnknownHttpOutcome()
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current(); var saved = await f.Save(checkpoint);
        var recreated = f.NewService();
        var found = await recreated.FindAsync(f.Grant.Descriptor.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch, checkpoint.CheckpointId, default);
        Assert.Equal(checkpoint.CheckpointId, found.CheckpointId); Assert.Equal(1UL, found.Sequence); Assert.Equal(64, found.PayloadHash.Length);
        Assert.Equal(saved.PayloadHash, found.PayloadHash); Assert.Equal(saved.Sequence, found.Sequence);
        var repeated = await recreated.SaveAsync(f.Grant.Descriptor.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch, Clone(checkpoint), default);
        Assert.Equal(saved.PayloadHash, repeated.PayloadHash);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task CompleteHeadReturnsFullCheckpointAndKeepsLegacyHeadExplicitlyIncomplete()
    {
        await using var f = await Fixture.Create(); var old = f.Current();
        await f.Save(old);
        var legacyHead = await f.Service.ReadHeadAsync(old.InstanceId, f.Grant.Secret!, old.RoomEpoch, default);
        Assert.False(legacyHead.IsComplete); Assert.NotNull(legacyHead.Checkpoint);
        Assert.Equal(old.CheckpointId, legacyHead.Checkpoint.CheckpointId);
        var complete = Complete(Clone(old)); complete.CheckpointId = Guid.NewGuid().ToString("N"); complete.Sequence = 2;
        var saved = await f.Save(complete);
        var head = await f.Service.ReadHeadAsync(complete.InstanceId, f.Grant.Secret!, complete.RoomEpoch, default);
        Assert.True(head.IsComplete); Assert.Equal(saved.PayloadHash, head.Receipt.PayloadHash);
        Assert.Equal(OnlineRunAssetCheckpoint.CompleteSchemaVersion, head.Checkpoint.SchemaVersion); Assert.Equal(42UL, head.Checkpoint.World.WorldEpoch);
        Assert.Equal(25u, head.Checkpoint.World.Chunks[0].Words[17]);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task CompleteNeverAdmittedMemberUsesRealReservedRowAndHistoricalHeadDoesNotRegrantEntry()
    {
        await using var f = await Fixture.Create(admitAll: false); var checkpoint = Complete(f.Current());
        var never = checkpoint.Members.Single(m => m.ActorId == 2);
        never.NeverAdmitted = true; never.ConnectionEpoch = 0;
        never.ResourceVersion = never.SelectionVersion = never.SpellUseVersion = 0; never.Wands = []; never.SpellUses = [];
        await f.Save(checkpoint);
        var instances = f.Factory.Services.GetRequiredService<RoomInstanceService>();
        var ticket = await instances.MintTicketAsync(never.PlayerId, f.Grant.Descriptor.RoomId, default);
        await instances.ConsumeAsync(f.Grant.Descriptor.InstanceId, f.Grant.Secret!, new ConsumeJoinTicketRequest
        { PlayerId = never.PlayerId, Ticket = ticket.Ticket, RoomEpoch = checkpoint.RoomEpoch }, default);
        var historical = await f.Service.ReadHeadAsync(checkpoint.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch, default);
        Assert.True(historical.Checkpoint.Members.Single(m => m.ActorId == 2).NeverAdmitted);
        checkpoint.CheckpointId = Guid.NewGuid().ToString("N"); checkpoint.Sequence = 2;
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(checkpoint)); // 已Admitted不能再以原Entry伪装未入场。
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HeadRejectsCorruptionInsteadOfReturningOnlyAnUnverifiedReceipt()
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current(); await f.Save(checkpoint);
        await using var db = new MySqlConnection(f.Factory.Connection);
        await db.ExecuteAsync("UPDATE RunAssetCheckpointHeads SET PayloadHash=@Hash WHERE RunId=@Run",
            new { Hash = new string('0', 64), Run = checkpoint.RunId });
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Service.ReadHeadAsync(checkpoint.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch, default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HeadRequiresTheOriginalInstanceCredentialAndEpoch()
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current(); await f.Save(checkpoint);
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Service.ReadHeadAsync(checkpoint.InstanceId, new string('0', 64), checkpoint.RoomEpoch, default));
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Service.ReadHeadAsync(checkpoint.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch + 1, default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task HistoricalSchema1ReceiptHashSurvivesAdditionOfCompleteSchemaFields()
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current();
        // 这是升级前真实Owned2记录：币字段不存在，不能借新Fixture的Wallet投影为历史补余额。
        foreach (var member in checkpoint.Members)
        { member.Assets.SchemaVersion = OnlineOwnedAssetSnapshot.PreCurrencySchemaVersion; member.Assets.Gold = 0; member.Assets.CurrencyVersion = 0; }
        Array.Sort(checkpoint.Members, (a, b) => StringComparer.Ordinal.Compare(a.PlayerId, b.PlayerId));
        foreach (var member in checkpoint.Members) Array.Sort(member.Assets.Items, (a, b) => StringComparer.Ordinal.Compare(a.InstanceId, b.InstanceId));
        // 独立重现V2字段出现前的序列化形状，不用新生产canonicalizer给自身生成期望Hash。
        var node = JsonSerializer.SerializeToNode(new { checkpoint.SchemaVersion, checkpoint.CheckpointId, checkpoint.InstanceId, checkpoint.RunId,
            checkpoint.GameplayConfigHash, checkpoint.RoomEpoch, checkpoint.ServerTick, checkpoint.Sequence,
            Members = checkpoint.Members.Select(m => new { m.PlayerId, m.ActorId, m.ConnectionEpoch, m.Assets }).ToArray(),
            GroundItems = checkpoint.GroundItems.Select(g => new { g.WorldOwnerId, g.Item, g.Children }).ToArray(), checkpoint.RetiredItems }, OnlineFactory.Json)!;
        foreach (var member in node["Members"]!.AsArray())
        { var assets = member!["Assets"]!.AsObject(); assets.Remove("Gold"); assets.Remove("CurrencyVersion"); }
        string json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        await using var db = new MySqlConnection(f.Factory.Connection);
        await db.ExecuteAsync("""
            INSERT INTO RunAssetCheckpointReceipts(RunId,CheckpointId,InstanceId,RoomEpoch,Sequence,PayloadHash,RecordedAtUtc)
            VALUES(@Run,@Checkpoint,@Instance,@Epoch,1,@Hash,UTC_TIMESTAMP(6));
            INSERT INTO RunAssetCheckpointHeads(RunId,InstanceId,RoomEpoch,Sequence,CheckpointId,PayloadHash,PayloadJson,PayloadBytes,UpdatedAtUtc)
            VALUES(@Run,@Instance,@Epoch,1,@Checkpoint,@Hash,@Json,@Bytes,UTC_TIMESTAMP(6))
            """, new { Run = checkpoint.RunId, Checkpoint = checkpoint.CheckpointId, Instance = checkpoint.InstanceId,
                Epoch = checkpoint.RoomEpoch, Hash = hash, Json = json, Bytes = System.Text.Encoding.UTF8.GetByteCount(json) });
        var repeated = await f.Save(checkpoint); Assert.Equal(hash, repeated.PayloadHash);
        var head = await f.Service.ReadHeadAsync(checkpoint.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch, default);
        Assert.False(head.IsComplete); Assert.Equal(hash, head.Receipt.PayloadHash);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task NewIdWithStaleSequenceCannotReplaceHead()
    {
        await using var f = await Fixture.Create(); var first = f.Current(); var saved = await f.Save(first);
        var stale = Clone(first); stale.CheckpointId = Guid.NewGuid().ToString("N");
        var error = await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(stale)); Assert.Equal("RunCheckpointReplay", error.Code);
        var found = await f.Service.FindAsync(f.Grant.Descriptor.InstanceId, f.Grant.Secret!, first.RoomEpoch, first.CheckpointId, default);
        Assert.Equal(saved.PayloadHash, found.PayloadHash);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task CrossOwnerTransferAndNewLootKeepOriginalIdsWithoutUpdatingPersistentOwners()
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current();
        var a = checkpoint.Members[0].Assets; var b = checkpoint.Members[1].Assets;
        var bottle = a.Items.First(i => i.LocationKind == 8 && i.ContainerState != null);
        a.Items = a.Items.Where(i => i.InstanceId != bottle.InstanceId).ToArray();
        bottle.LocationKind = 1; bottle.ParentInstanceId = b.Items.Single(i => i.LocationKind == 4).InstanceId;
        bottle.Slot = -1; bottle.X = 0; bottle.Y = 0;
        b.Items = [.. b.Items, bottle]; a.InventoryVersion++; b.InventoryVersion++;
        var fresh = JsonSerializer.Deserialize<OnlineOwnedItemSnapshot>(JsonSerializer.Serialize(bottle, OnlineFactory.Json), OnlineFactory.Json)!;
        fresh.InstanceId = Guid.NewGuid().ToString("N"); fresh.X = 2; b.Items = [.. b.Items, fresh];
        var receipt = await f.Save(checkpoint); Assert.Equal(1UL, receipt.Sequence);
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal(a.PlayerId, await db.QuerySingleAsync<string>("SELECT OwnerCharacterId FROM Items WHERE InstanceId=@Id", new { Id = bottle.InstanceId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items WHERE InstanceId=@Id", new { Id = fresh.InstanceId }));
        Assert.Equal(b.PlayerId, await db.QuerySingleAsync<string>("SELECT OwnerPlayerId FROM RunAssetCheckpointIdentities WHERE InstanceId=@Id", new { Id = bottle.InstanceId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task RetiredIdCannotBeResurrectedAsFreshLootInLaterCheckpoint()
    {
        await using var f = await Fixture.Create(); var first = f.Current();
        var owner = first.Members[0].Assets; var removed = owner.Items.First(i => i.LocationKind == 8);
        owner.Items = owner.Items.Where(i => i.InstanceId != removed.InstanceId).ToArray(); owner.InventoryVersion++;
        first.RetiredItems = [new() { InstanceId = removed.InstanceId, ContentId = removed.ContentId,
            Revision = removed.Revision, Reason = OnlineRunAssetRetiredReason.Destroyed }];
        await f.Save(first);
        var next = Clone(first); next.CheckpointId = Guid.NewGuid().ToString("N"); next.Sequence = 2; next.RetiredItems = [];
        next.Members[0].Assets.Items = [.. next.Members[0].Assets.Items, removed];
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(next));
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.True(await db.QuerySingleAsync<bool>("SELECT IsRetired FROM RunAssetCheckpointIdentities WHERE InstanceId=@Id", new { Id = removed.InstanceId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task ExistingForeignInventoryIdCannotBeDeclaredNewLoot()
    {
        await using var f = await Fixture.Create(); var foreign = await f.Factory.NewPlayerAsync();
        var checkpoint = f.Current(); var target = checkpoint.Members[0].Assets;
        var source = foreign.Profile.CombatAssets.Items.Single(i => i.LocationKind == 2);
        var item = JsonSerializer.Deserialize<OnlineOwnedItemSnapshot>(JsonSerializer.Serialize(source, OnlineFactory.Json), OnlineFactory.Json)!;
        item.ParentInstanceId = target.Items.Single(i => i.LocationKind == 3).InstanceId; item.Slot = 1;
        target.Items = [.. target.Items, item]; target.InventoryVersion++;
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(checkpoint));
        await using var db = new MySqlConnection(f.Factory.Connection);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointHeads WHERE RunId=@Run", new { Run = checkpoint.RunId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task PersistentCharacterVersionDrift_IsRejectedBeforeSaving()
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current();
        await using var db = new MySqlConnection(f.Factory.Connection);
        // 仅隔离Schema造出违反租约的外部漂移；拿到角色锁本身不能证明行仍是Run冻结的那一代。
        await db.ExecuteAsync("UPDATE Characters SET Version=Version+1 WHERE CharacterId=@Player",
            new { Player = checkpoint.Members[0].PlayerId });
        var failure = await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(checkpoint));
        Assert.Equal("RunCheckpointProfileVersionConflict", failure.Code);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointHeads WHERE RunId=@Run", new { Run = checkpoint.RunId }));
    }

    [Theory, InlineData("owner"), InlineData("content"), Trait("Category", "DatabaseIntegration")]
    public async Task PersistentKnownItemOwnerOrContentDrift_IsRejectedDespiteEntryIdentity(string defect)
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current();
        var bottle = checkpoint.Members[0].Assets.Items.First(i => i.LocationKind == 8 && i.ContainerState != null);
        await using var db = new MySqlConnection(f.Factory.Connection);
        if (defect == "owner")
        {
            var foreign = await f.Factory.NewPlayerAsync();
            // 测试必须按真实复合外键顺序制造外部Owner漂移，不能关FK或把被保护的保底包转给别人。
            await db.ExecuteAsync("""
                DELETE FROM ItemLocations WHERE InstanceId=@Item;
                UPDATE Items SET OwnerCharacterId=@Foreign WHERE InstanceId=@Item;
                INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot) VALUES(@Item,@Foreign,'Warehouse',0)
                """, new { Item = bottle.InstanceId, Foreign = foreign.Profile.PlayerId });
        }
        else await db.ExecuteAsync("UPDATE Items SET ContentId='wandtest.fire' WHERE InstanceId=@Item", new { Item = bottle.InstanceId });
        var failure = await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(checkpoint));
        Assert.Equal("RunCheckpointOwnershipConflict", failure.Code);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointHeads WHERE RunId=@Run", new { Run = checkpoint.RunId }));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task WrongSecretEpochExpiredLeaseAndMissingMemberLeaseAreRejected()
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current();
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Service.SaveAsync(checkpoint.InstanceId, new string('0', 64), checkpoint.RoomEpoch, checkpoint, default));
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Service.SaveAsync(checkpoint.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch + 1, checkpoint, default));
        await using var db = new MySqlConnection(f.Factory.Connection);
        await db.ExecuteAsync("DELETE FROM AssetLeases WHERE RunId=@Run AND CharacterId=@Player", new { Run = checkpoint.RunId, Player = checkpoint.Members[0].PlayerId });
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(checkpoint));
        f.Factory.Clock.Advance(TimeSpan.FromSeconds(121));
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Service.AuthorizeAsync(checkpoint.InstanceId, f.Grant.Secret!, checkpoint.RoomEpoch, default));
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task InvalidHighSequenceRollsBackAndCannotPoisonTheNextValidSubmission()
    {
        await using var f = await Fixture.Create(); var valid = f.Current(); var invalid = Clone(valid);
        invalid.Sequence = 100000; invalid.Members[0].Assets.Items[0].ContentId = "unknown.item";
        await Assert.ThrowsAsync<OnlineFailure>(() => f.Save(invalid));
        var saved = await f.Save(valid); Assert.Equal(1UL, saved.Sequence);
    }

    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task RouteAuthenticatesBeforeJsonParsingAndAllowsItsOwnBoundedBodyAboveFourKiB()
    {
        await using var f = await Fixture.Create(); var checkpoint = f.Current();
        // TestServer默认没有真实远端IP；显式用其请求Context提供loopback，不能为测试放宽生产LocalControlOnly。
        var rejected = await InstanceRequest(f, "POST", "/checkpoints", new string('0', 64), [123]);
        Assert.Equal(401, rejected.Response.StatusCode);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(checkpoint, OnlineFactory.Json); Assert.True(body.Length > 4096);
        var saved = await InstanceRequest(f, "POST", "/checkpoints", f.Grant.Secret!, body); Assert.Equal(200, saved.Response.StatusCode);
        var found = await InstanceRequest(f, "GET", "/checkpoints/" + checkpoint.CheckpointId, f.Grant.Secret!, []);
        Assert.Equal(200, found.Response.StatusCode);
    }

    [Theory]
    [InlineData(67108865, ulong.MaxValue)]
    [InlineData(4096, 8192UL)]
    public void JsonAndStoragePacketBudgetsRejectWithoutChangingServerConfiguration(int bytes, ulong packet)
        => Assert.Throws<OnlineFailure>(() => RunAssetCheckpointService.RequirePayloadBudget(bytes, packet));

    [Fact]
    public void ExplicitTransportBudgetAcceptsCurrentBoundedPayloadWithoutPromisingExtremeLayouts()
        => RunAssetCheckpointService.RequirePayloadBudget(4096, 65536);

    [Fact]
    public void DtoMaximumLayoutCanExceedTransportBudgetAndMustFailExplicitly()
    {
        // 这是实际DTO的保守wire布局上界，不宣称同时合法的所有物品都会有全部可选State。
        // 例如现业务只接受模板杖，不接受这里为尺寸测量填写的实例roll；不能把估算冒充合法背包验收。
        var item = new OnlineOwnedItemSnapshot { InstanceId = new string('a', 32), ContentId = new string('b', 128),
            ParentInstanceId = new string('c', 32), Revision = uint.MaxValue, LocationKind = 1, Slot = -1,
            X = int.MaxValue, Y = int.MaxValue, QuarterTurns = 3, RemainingCharges = int.MaxValue,
            WandStats = new() { MaxMana = float.MaxValue, ManaRegenPerSecond = float.MaxValue,
                BaseCastDelay = float.MaxValue, BaseReloadTime = float.MaxValue, BaseSpreadDegrees = float.MaxValue,
                ProgramCapacity = 32, BaseDraws = 32, AlwaysCastContentIds = Enumerable.Repeat(new string('d', 128), 32).ToArray() } };
        long activeBytes = (long)JsonSerializer.SerializeToUtf8Bytes(item, OnlineFactory.Json).Length * OnlineRunAssetCheckpoint.MaximumActiveItems;
        var retired = new OnlineRunAssetCheckpointRetired { InstanceId = item.InstanceId, ContentId = item.ContentId,
            Revision = uint.MaxValue, Reason = OnlineRunAssetRetiredReason.Destroyed };
        long retiredBytes = (long)JsonSerializer.SerializeToUtf8Bytes(retired, OnlineFactory.Json).Length * OnlineRunAssetCheckpoint.MaximumRetiredItems;
        long conservativeWireBytes = activeBytes + retiredBytes + 4096;
        Assert.True(conservativeWireBytes > RunAssetCheckpointService.MaximumPayloadBytes);
        Assert.Throws<OnlineFailure>(() => RunAssetCheckpointService.RequirePayloadBudget(checked((int)conservativeWireBytes), ulong.MaxValue));
    }

    private static OnlineRunAssetCheckpoint Clone(OnlineRunAssetCheckpoint checkpoint)
        => JsonSerializer.Deserialize<OnlineRunAssetCheckpoint>(JsonSerializer.Serialize(checkpoint, OnlineFactory.Json), OnlineFactory.Json)!;

    internal static OnlineRunAssetCheckpoint Complete(OnlineRunAssetCheckpoint checkpoint)
    {
        checkpoint.SchemaVersion = OnlineRunAssetCheckpoint.CompleteSchemaVersion;
        checkpoint.World = RunCheckpointWorldRulesTests.Valid(); checkpoint.Flights = []; checkpoint.MaterialOutputs = []; checkpoint.LostMaterials = [];
        checkpoint.Coins = []; // 当前Schema4的真实已知空表，历史夹具保持缺字段。
        foreach (var member in checkpoint.Members)
        {
            member.ResourceVersion = member.SelectionVersion = member.SpellUseVersion = 1;
            member.Wands = member.Assets.Items.Where(i => i.LocationKind == 3).Select(i => new OnlineRunAssetCheckpointWandResource
            { InstanceId = i.InstanceId, ProgramRevision = 1, CurrentMana = 1, CooldownRemaining = .1f }).ToArray();
            member.SpellUses = member.Assets.Items.Where(i => i.LocationKind == 2).Select(i => new OnlineRunAssetCheckpointSpellUse
            { InstanceId = i.InstanceId, Eligible = true }).ToArray();
        }
        return checkpoint;
    }

    private static Task<Microsoft.AspNetCore.Http.HttpContext> InstanceRequest(Fixture f, string method, string suffix, string secret, byte[] body)
        => f.Factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = method;
            context.Request.Path = "/api/instances/" + f.Grant.Descriptor.InstanceId + suffix;
            context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?roomEpoch=" + f.Grant.Descriptor.RoomEpoch);
            context.Request.Headers["X-Norn-Instance-Key"] = secret;
            context.Request.ContentType = "application/json"; context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body);
        });

    internal sealed class Fixture : IAsyncDisposable
    {
        public required OnlineFactory Factory;
        public required InstanceLaunchGrant Grant;
        public required InstanceReadyReply Ready;
        public required RunAssetCheckpointService Service;
        private bool _ownsFactory;
        public static async Task<Fixture> Create(bool admitAll = true, OnlineFactory? factory = null)
        {
            // 外部Kestrel桥在第一次Services访问前选择真实监听；多个Run共用Host，但各自只清理自己的Run。
            bool ownsFactory = factory == null;
            factory ??= new OnlineFactory(instances: true);
            var run = await RoomInstanceTests.Reserve(factory); var instances = factory.Services.GetRequiredService<RoomInstanceService>();
            var grant = await instances.AllocateAsync(run.Run, default);
            using var process = Process.GetCurrentProcess();
            var identity = new InstanceProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!);
            await instances.RecordProcessAsync(grant.Descriptor.InstanceId, identity, default);
            var ready = await instances.ReadyAsync(grant.Descriptor.InstanceId, grant.Secret!, new InstanceReadyRequest
            { DungeonId = "p7", RoomEpoch = run.Run.RoomEpoch, ProtocolVersion = factory.Manifest.ProtocolVersion,
                BuildVersion = factory.Manifest.BuildVersion, ManifestHash = factory.Manifest.Hash, GameplayConfigHash = OnlineFactory.GameplayHash,
                ProcessId = identity.ProcessId, ProcessStartTicks = identity.StartTicks }, default);
            foreach (var member in ready.Members)
            {
                if (!admitAll && member.ActorId != 1) continue;
                var ticket = await instances.MintTicketAsync(member.Profile.PlayerId, run.Run.RoomId, default);
                await instances.ConsumeAsync(grant.Descriptor.InstanceId, grant.Secret!, new ConsumeJoinTicketRequest
                { Ticket = ticket.Ticket, PlayerId = member.Profile.PlayerId, RoomEpoch = run.Run.RoomEpoch }, default);
            }
            var fixture = new Fixture { Factory = factory, Grant = grant, Ready = ready, Service = null!, _ownsFactory = ownsFactory };
            fixture.Service = fixture.NewService(); return fixture;
        }
        public RunAssetCheckpointService NewService() => new(Factory.Services.GetRequiredService<RoomTransactions>(),
            Factory.Services.GetRequiredService<RoomInstanceService>(), Factory.Services.GetRequiredService<ContentManifestValidator>());
        public OnlineRunAssetCheckpoint Current() => new()
        {
            SchemaVersion = 1, CheckpointId = Guid.NewGuid().ToString("N"), InstanceId = Grant.Descriptor.InstanceId,
            RunId = Grant.Descriptor.RunId, RoomEpoch = Grant.Descriptor.RoomEpoch, Sequence = 1, ServerTick = 100,
            GameplayConfigHash = OnlineFactory.GameplayHash,
            Members = Ready.Members.Select(m => new OnlineRunAssetCheckpointMember { PlayerId = m.Profile.PlayerId,
                ActorId = m.ActorId, ConnectionEpoch = 1,
                Assets = JsonSerializer.Deserialize<OnlineOwnedAssetSnapshot>(JsonSerializer.Serialize(m.Profile.CombatAssets, OnlineFactory.Json), OnlineFactory.Json)! }).ToArray()
        };
        public Task<OnlineRunAssetCheckpointReceipt> Save(OnlineRunAssetCheckpoint checkpoint)
            => Service.SaveAsync(Grant.Descriptor.InstanceId, Grant.Secret!, checkpoint.RoomEpoch, checkpoint, default);
        public async Task<string> AssetState()
        {
            await using var db = new MySqlConnection(Factory.Connection);
            var owners = Ready.Members.Select(m => m.Profile.PlayerId).Order(StringComparer.Ordinal).ToArray();
            var items = await db.QueryAsync("SELECT * FROM Items WHERE OwnerCharacterId IN @Owners ORDER BY InstanceId", new { Owners = owners });
            var locations = await db.QueryAsync("SELECT * FROM ItemLocations WHERE OwnerCharacterId IN @Owners ORDER BY InstanceId", new { Owners = owners });
            var wallets = await db.QueryAsync("SELECT * FROM Wallets WHERE CharacterId IN @Owners ORDER BY CharacterId", new { Owners = owners });
            var members = await db.QueryAsync("SELECT * FROM RunMembers WHERE RunId=@Run ORDER BY CharacterId", new { Run = Grant.Descriptor.RunId });
            var leases = await db.QueryAsync("SELECT * FROM AssetLeases WHERE RunId=@Run ORDER BY CharacterId", new { Run = Grant.Descriptor.RunId });
            var characters = await db.QueryAsync("SELECT * FROM Characters WHERE CharacterId IN @Owners ORDER BY CharacterId", new { Owners = owners });
            return JsonSerializer.Serialize(new { items, locations, wallets, members, leases, characters }, OnlineFactory.Json);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Factory.Services.GetRequiredService<RoomInstanceService>().FailAsync(Grant.Descriptor.InstanceId, "Cancelled", default); }
            finally { if (_ownsFactory) await Factory.DisposeAsync(); }
        }
    }
}
