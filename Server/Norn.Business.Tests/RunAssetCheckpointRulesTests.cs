using System.Text.Json;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>真实清单与值对象的纯规则测试；不接数据库，也不把DTO校验称为退出事务验收。</summary>
public sealed class RunAssetCheckpointRulesTests : IDisposable
{
    private static readonly string Hash = new('a', 64), Instance = Id(1), Run = Id(2), PlayerA = Id(3), PlayerB = Id(4);
    private static readonly string WandA = Id(5), CardA = Id(6), BagA = Id(7), Bottle = Id(8);
    private static readonly string WandB = Id(9), CardB = Id(10), BagB = Id(11), FreshBottle = Id(12);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "run-checkpoint-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly ContentManifestValidator _content;

    public RunAssetCheckpointRulesTests()
    {
        var manifest = new ContentManifest { SchemaVersion = 2, BuildVersion = "checkpoint-test", Items =
            [Definition("wandtest.wand.a", 1), Definition("wandtest.fire", 0), Definition("checkpoint.bag", 2),
             Definition("checkpoint.bottle", 3), Definition("checkpoint.solid", 4), Definition("norn.v2.cast_faster", 0)] };
        manifest.Hash = ContentManifestRules.CalculateHash(manifest);
        File.WriteAllText(_path, JsonSerializer.Serialize(manifest, ContentManifestValidator.Json));
        _content = new ContentManifestValidator(new OnlineOptions { ManifestPath = _path, GameplayConfigHash = Hash });
    }

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void CrossOwnerTransfer_PreservesOriginalIdAndCurrentQuantity()
    {
        var f = Valid();
        var item = RemoveBottle(f.Checkpoint.Members[0].Assets);
        item.ParentInstanceId = BagB; item.ContainerState.Components[0].Quantity = 7;
        f.Checkpoint.Members[1].Assets.Items = [.. f.Checkpoint.Members[1].Assets.Items, item];
        RunAssetCheckpointRules.Require(f.Checkpoint, f.Trusted, _content);
        Assert.Equal(Bottle, f.Checkpoint.Members[1].Assets.Items[^1].InstanceId);
        Assert.Equal(7UL, f.Checkpoint.Members[1].Assets.Items[^1].ContainerState.Components[0].Quantity);
        Assert.DoesNotContain(f.Checkpoint.Members[0].Assets.Items, x => x.InstanceId == Bottle);
    }

    [Fact]
    public void TrustedNewPickup_IsAcceptedWithoutGrantingWarehouseAccess()
    {
        var f = Valid(); var fresh = BottleItem(FreshBottle, BagB, 1);
        f.Checkpoint.Members[1].Assets.Items = [.. f.Checkpoint.Members[1].Assets.Items, fresh];
        f.Trusted = f.Trusted with { NewItemIds = new HashSet<string>(StringComparer.Ordinal) { FreshBottle } };
        RunAssetCheckpointRules.Require(f.Checkpoint, f.Trusted, _content);
        Assert.Equal(1UL, fresh.ContainerState.Components[0].Quantity);
        Assert.False(fresh.HasUnlimitedCharges);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("run")]
    [InlineData("epoch")]
    [InlineData("lease")]
    [InlineData("manifest")]
    [InlineData("schema")]
    [InlineData("checkpoint-id")]
    public void UntrustedInstanceOrCompatibility_IsRejectedBeforeAssetProcessing(string defect)
    {
        var f = Valid();
        switch (defect)
        {
            case "instance": f.Checkpoint.InstanceId = Id(99); break;
            case "run": f.Checkpoint.RunId = Id(99); break;
            case "epoch": f.Checkpoint.RoomEpoch++; break;
            case "lease": f.Trusted = f.Trusted with { InstanceLeaseValid = false }; break;
            case "manifest": f.Checkpoint.GameplayConfigHash = new string('b', 64); break;
            case "schema": f.Checkpoint.SchemaVersion = 0; break;
            case "checkpoint-id": f.Checkpoint.CheckpointId = "not-an-id"; break;
        }
        Reject(f);
    }

    [Theory]
    [InlineData("player")]
    [InlineData("actor")]
    [InlineData("connection")]
    [InlineData("lease")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("exited")]
    [InlineData("inventory-id")]
    [InlineData("assets-run")]
    public void MemberMustMatchCurrentLeasedActorAndFullInventoryIdentity(string defect)
    {
        var f = Valid(); var member = f.Checkpoint.Members[0];
        switch (defect)
        {
            case "player": member.PlayerId = Id(99); break;
            case "actor": member.ActorId = 4; break;
            case "connection": member.ConnectionEpoch = 1; break;
            case "lease": f.Trusted = f.Trusted with { Members = [f.Trusted.Members[0] with { HasLease = false }, f.Trusted.Members[1]] }; break;
            case "missing": f.Checkpoint.Members = [member]; break;
            case "duplicate": f.Checkpoint.Members = [member, member]; break;
            case "exited": f.Trusted = f.Trusted with { ExitedPlayerIds = new HashSet<string>(StringComparer.Ordinal) { PlayerA } }; break;
            case "inventory-id": member.Assets.InventoryId = "other-scope"; break;
            case "assets-run": member.Assets.RunId = Id(99); break;
        }
        Reject(f);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("inventory")]
    [InlineData("revision")]
    [InlineData("charges")]
    [InlineData("unlimited")]
    public void OldEntryOrCheckpointCannotRefillOrRollBackSubmittedState(string defect)
    {
        var f = Valid(); var card = f.Checkpoint.Members[0].Assets.Items.Single(x => x.InstanceId == CardA);
        switch (defect)
        {
            case "sequence": f.Checkpoint.Sequence = 10; break;
            case "inventory": f.Checkpoint.Members[0].Assets.InventoryVersion = 7; break;
            case "revision": card.Revision = 2; break;
            case "charges": card.RemainingCharges = 30; break;
            case "unlimited": card.HasUnlimitedCharges = true; break;
        }
        Reject(f);
    }

    [Theory]
    [InlineData("two-members")]
    [InlineData("member-ground")]
    [InlineData("member-retired")]
    [InlineData("two-ground")]
    [InlineData("two-retired")]
    public void OneOriginalInstanceCannotAppearInTwoOwnershipPartitions(string defect)
    {
        var f = Valid(); var item = Copy(f.Checkpoint.Members[0].Assets.Items.Single(x => x.InstanceId == Bottle));
        switch (defect)
        {
            case "two-members":
                item.ParentInstanceId = BagB;
                f.Checkpoint.Members[1].Assets.Items = [.. f.Checkpoint.Members[1].Assets.Items, item]; break;
            case "member-ground": f.Checkpoint.GroundItems = [Ground(item)]; break;
            case "member-retired": f.Checkpoint.RetiredItems = [Retired(item)]; break;
            case "two-ground":
                RemoveBottle(f.Checkpoint.Members[0].Assets);
                f.Checkpoint.GroundItems = [Ground(item), Ground(Copy(item))]; break;
            case "two-retired":
                RemoveBottle(f.Checkpoint.Members[0].Assets);
                f.Checkpoint.RetiredItems = [Retired(item), Retired(item)]; break;
        }
        Reject(f);
    }

    [Fact]
    public void OriginalIdMustHaveCurrentOwnerGroundOrExplicitRetirement()
    {
        var f = Valid(); RemoveBottle(f.Checkpoint.Members[0].Assets); Reject(f);
    }

    [Fact]
    public void ExplicitRetirementCoversOriginalIdWithoutReissuingIt()
    {
        var f = Valid(); var item = RemoveBottle(f.Checkpoint.Members[0].Assets);
        f.Checkpoint.RetiredItems = [Retired(item)];
        RunAssetCheckpointRules.Require(f.Checkpoint, f.Trusted, _content);
        Assert.DoesNotContain(f.Checkpoint.Members.SelectMany(m => m.Assets.Items), x => x.InstanceId == Bottle);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("forbidden")]
    [InlineData("renamed")]
    [InlineData("retired-unknown")]
    [InlineData("retired-unknown-reason")]
    public void UnknownWarehouseOrAlteredIdentityCannotBeLaunderedIntoRun(string defect)
    {
        var f = Valid(); var item = f.Checkpoint.Members[0].Assets.Items.Single(x => x.InstanceId == Bottle);
        switch (defect)
        {
            case "unknown": item.InstanceId = Id(99); break;
            case "forbidden":
                f.Trusted = f.Trusted with { ForbiddenExistingIds = new HashSet<string>(StringComparer.Ordinal) { Bottle } }; break;
            case "renamed": item.ContentId = "checkpoint.solid"; item.ContainerState = null!;
                item.MaterialState = new() { SchemaVersion = 1, MaterialKey = 5, Quantity = 1 }; break;
            case "retired-unknown": f.Checkpoint.RetiredItems = [Retired(BottleItem(Id(99), BagA, 1))]; break;
            case "retired-unknown-reason":
                RemoveBottle(f.Checkpoint.Members[0].Assets);
                f.Checkpoint.RetiredItems = [Retired(item)]; f.Checkpoint.RetiredItems[0].Reason = OnlineRunAssetRetiredReason.None; break;
        }
        Reject(f);
    }

    [Fact]
    public void GroundWandRetainsItsCompleteOriginalInstalledCardClosure()
    {
        var f = Valid(); var spare = new OnlineOwnedItemSnapshot
        { InstanceId = Id(20), ContentId = "wandtest.wand.a", Revision = 1, LocationKind = 5, Slot = -1, UseTemplateWandStats = true };
        var card = new OnlineOwnedItemSnapshot
        { InstanceId = Id(21), ContentId = "wandtest.fire", Revision = 1, LocationKind = 2, ParentInstanceId = spare.InstanceId, Slot = 0, RemainingCharges = 4 };
        f.Checkpoint.GroundItems = [new() { WorldOwnerId = Id(22), Item = spare, Children = [card] }];
        f.Trusted = f.Trusted with { NewItemIds = new HashSet<string>(StringComparer.Ordinal) { spare.InstanceId, card.InstanceId } };
        RunAssetCheckpointRules.Require(f.Checkpoint, f.Trusted, _content);
        Assert.Equal(spare.InstanceId, f.Checkpoint.GroundItems[0].Children[0].ParentInstanceId);
        f.Checkpoint.GroundItems[0].Children = []; Reject(f);
    }

    [Theory, InlineData("backpack"), InlineData("ground")]
    public void P7RollSnapshot_IsAcceptedWithItsOwnCapacityAndOriginalTraits(string placement)
    {
        var f = WithP7Roll(placement);
        RunAssetCheckpointRules.Require(f.Checkpoint, f.Trusted, _content);
        var roll = placement == "ground" ? f.Checkpoint.GroundItems[0].Item
            : f.Checkpoint.Members[0].Assets.Items.Single(i => i.InstanceId == Id(70));
        Assert.False(roll.UseTemplateWandStats); Assert.Equal(32, roll.WandStats.ProgramCapacity);
        Assert.Equal("norn.v2.cast_faster", Assert.Single(roll.WandStats.AlwaysCastContentIds));
    }

    [Fact]
    public void ProfileAndEntryFreezeRemainTemplateOnlyForRandomWandSnapshots()
    {
        var f = WithP7Roll("backpack");
        Assert.Throws<OnlineFailure>(() => Norn.Business.Features.Inventory.OwnedCombatAssetRules.Require(
            f.Checkpoint.Members[0].Assets, PlayerA, Hash, _content, true));
    }

    [Theory, InlineData("nan"), InlineData("mana"), InlineData("regen"), InlineData("delay"),
        InlineData("capacity"), InlineData("draws"), InlineData("reload"), InlineData("spread"),
        InlineData("missing-traits"), InlineData("too-many-traits"), InlineData("unknown-trait"), InlineData("non-spell-trait")]
    public void P7RollSnapshot_RejectsInvalidNumbersCapacitiesAndUntrustedTraits(string defect)
    {
        var f = WithP7Roll("ground"); var stats = f.Checkpoint.GroundItems[0].Item.WandStats;
        switch (defect)
        {
            case "nan": stats.MaxMana = float.NaN; break;
            case "mana": stats.MaxMana = 0; break;
            case "regen": stats.ManaRegenPerSecond = -1; break;
            case "delay": stats.BaseCastDelay = -1; break;
            case "capacity": stats.ProgramCapacity = 33; break;
            case "draws": stats.BaseDraws = 33; break;
            case "reload": stats.BaseReloadTime = -1; break;
            case "spread": stats.BaseSpreadDegrees = -1; break;
            case "missing-traits": stats.AlwaysCastContentIds = null!; break;
            case "too-many-traits": stats.AlwaysCastContentIds = Enumerable.Repeat("norn.v2.cast_faster", 5).ToArray(); break;
            case "unknown-trait": stats.AlwaysCastContentIds = ["unknown.trait"]; break;
            case "non-spell-trait": stats.AlwaysCastContentIds = ["checkpoint.bag"]; break;
        }
        Reject(f);
    }

    [Theory, InlineData("stats"), InlineData("shuffle"), InlineData("traits"), InlineData("template")]
    public void KnownRandomWandCannotBeRerolledOrConvertedOnLaterCheckpoint(string defect)
    {
        var f = WithP7Roll("backpack"); var item = f.Checkpoint.Members[0].Assets.Items.Single(i => i.InstanceId == Id(70));
        var known = new Dictionary<string, RunAssetCheckpointKnownItem>(f.Trusted.KnownItems, StringComparer.Ordinal)
        { [item.InstanceId] = new(PlayerA, Copy(item)) };
        f.Trusted = f.Trusted with { KnownItems = known, NewItemIds = new HashSet<string>(StringComparer.Ordinal) { Id(71) } };
        switch (defect)
        {
            case "stats": item.WandStats.MaxMana++; break;
            case "shuffle": item.WandStats.Shuffle = !item.WandStats.Shuffle; break;
            case "traits": item.WandStats.AlwaysCastContentIds = ["wandtest.fire"]; break;
            case "template":
                item.UseTemplateWandStats = true; item.WandStats = null!;
                // 换成模板合法槽，确保本例专门抓“同ID改roll模式”，而非偶然被原模板4槽上限挡住。
                f.Checkpoint.Members[0].Assets.Items.Single(i => i.InstanceId == Id(71)).Slot = 0; break;
        }
        Reject(f);
    }

    [Theory]
    [InlineData("world-owner")]
    [InlineData("wrong-location")]
    [InlineData("parent")]
    [InlineData("child")]
    [InlineData("state")]
    public void GroundEvidenceCannotHideBadRootsChildrenOrContainerState(string defect)
    {
        var f = Valid(); var item = RemoveBottle(f.Checkpoint.Members[0].Assets); var ground = Ground(item);
        f.Checkpoint.GroundItems = [ground];
        switch (defect)
        {
            case "world-owner": ground.WorldOwnerId = ""; break;
            case "wrong-location": ground.Item.LocationKind = 6; break;
            case "parent": ground.Item.ParentInstanceId = BagA; break;
            case "child": ground.Children = [Copy(f.Checkpoint.Members[0].Assets.Items.Single(x => x.InstanceId == CardA))]; break;
            case "state": ground.Item.ContainerState.Components[0].Quantity = 1001; break;
        }
        Reject(f);
    }

    [Fact]
    public void InvalidCarriedGridOrMaterialState_IsRejectedByExistingOwnedRules()
    {
        var f = Valid(); var item = f.Checkpoint.Members[0].Assets.Items.Single(x => x.InstanceId == Bottle);
        item.X = 8; Reject(f);
        f = Valid(); item = f.Checkpoint.Members[0].Assets.Items.Single(x => x.InstanceId == Bottle);
        item.ContainerState = null!; Reject(f);
    }

    [Theory]
    [InlineData("members")]
    [InlineData("ground")]
    [InlineData("retired")]
    [InlineData("ground-overflow")]
    [InlineData("retired-overflow")]
    public void MissingPartitionsAndOversizedArraysAreRejectedBeforeTraversal(string defect)
    {
        var f = Valid();
        switch (defect)
        {
            case "members": f.Checkpoint.Members = null!; break;
            case "ground": f.Checkpoint.GroundItems = null!; break;
            case "retired": f.Checkpoint.RetiredItems = null!; break;
            case "ground-overflow": f.Checkpoint.GroundItems = new OnlineRunAssetCheckpointGround[OnlineRunAssetCheckpoint.MaximumGroundItems + 1]; break;
            case "retired-overflow": f.Checkpoint.RetiredItems = new OnlineRunAssetCheckpointRetired[OnlineRunAssetCheckpoint.MaximumRetiredItems + 1]; break;
        }
        Reject(f);
    }

    private void Reject(Fixture f) => Assert.Throws<OnlineFailure>(() => RunAssetCheckpointRules.Require(f.Checkpoint, f.Trusted, _content));
    private static string Id(int value) => value.ToString("x32");
    private static OnlineOwnedItemSnapshot Copy(OnlineOwnedItemSnapshot item)
        => JsonSerializer.Deserialize<OnlineOwnedItemSnapshot>(JsonSerializer.Serialize(item, ContentManifestValidator.Json), ContentManifestValidator.Json)!;
    private static OnlineOwnedItemSnapshot RemoveBottle(OnlineOwnedAssetSnapshot assets)
    {
        var item = assets.Items.Single(x => x.InstanceId == Bottle);
        assets.Items = assets.Items.Where(x => x.InstanceId != Bottle).ToArray(); return item;
    }
    private static OnlineRunAssetCheckpointRetired Retired(OnlineOwnedItemSnapshot item) => new()
    { InstanceId = item.InstanceId, ContentId = item.ContentId, Revision = item.Revision, Reason = OnlineRunAssetRetiredReason.Destroyed };
    private static OnlineRunAssetCheckpointGround Ground(OnlineOwnedItemSnapshot item)
    {
        item.LocationKind = 5; item.ParentInstanceId = ""; item.Slot = -1; item.X = item.Y = item.QuarterTurns = 0;
        return new() { WorldOwnerId = Id(50), Item = item };
    }
    private static OnlineOwnedItemSnapshot BottleItem(string id, string bag, ulong quantity) => new()
    {
        InstanceId = id, ContentId = "checkpoint.bottle", Revision = 3, LocationKind = 1, ParentInstanceId = bag, Slot = -1,
        ContainerState = new() { SchemaVersion = 1, Components = [new() { MaterialKey = 1, Quantity = quantity }] }
    };
    private static OnlineOwnedAssetSnapshot Assets(string player, ulong actor, string wand, string card, string bag) => new()
    {
        SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, PlayerId = player, ActorId = actor, RunId = Run,
        InventoryId = Run + "/" + player, InventoryVersion = 8, Gold = 0, CurrencyVersion = 1, GameplayConfigHash = Hash, SelectedWandInstanceId = wand,
        Items = [new() { InstanceId = wand, ContentId = "wandtest.wand.a", Revision = 3, LocationKind = 3, Slot = 0, UseTemplateWandStats = true },
            new() { InstanceId = card, ContentId = "wandtest.fire", Revision = 3, LocationKind = 2, ParentInstanceId = wand, Slot = 0, RemainingCharges = 9 },
            new() { InstanceId = bag, ContentId = "checkpoint.bag", Revision = 3, LocationKind = 4, Slot = 0 }]
    };
    private static Fixture Valid()
    {
        var a = Assets(PlayerA, 1, WandA, CardA, BagA); var b = Assets(PlayerB, 2, WandB, CardB, BagB);
        a.Items = [.. a.Items, BottleItem(Bottle, BagA, 20)];
        var known = new Dictionary<string, RunAssetCheckpointKnownItem>(StringComparer.Ordinal);
        foreach (var owner in new[] { a, b }) foreach (var item in owner.Items)
            known.Add(item.InstanceId, new(owner.PlayerId, Copy(item)));
        return new()
        {
            Checkpoint = new()
            {
                SchemaVersion = 1, CheckpointId = Id(30), InstanceId = Instance, RunId = Run, RoomEpoch = 4,
                Sequence = 11, ServerTick = 50, GameplayConfigHash = Hash,
                Members = [new() { PlayerId = PlayerA, ActorId = 1, ConnectionEpoch = 3, Assets = a },
                    new() { PlayerId = PlayerB, ActorId = 2, ConnectionEpoch = 5, Assets = b }]
            },
            Trusted = new()
            {
                InstanceId = Instance, RunId = Run, RoomEpoch = 4, InstanceLeaseValid = true, LastSequence = 10,
                Members = [new(PlayerA, 1, 3, 8, true), new(PlayerB, 2, 5, 8, true)], KnownItems = known
            }
        };
    }
    private static Fixture WithP7Roll(string placement)
    {
        var f = Valid();
        // 原DungeonWandRolls.asset：Mana80..240/Regen8..30/Cast.05...3/Capacity3..32/Draw1..3、
        // Reload.2..1.2/Spread0..8，AlwaysCast候选包含norn.v2.cast_faster。这里保真一个可产生的值快照；
        // 业务测试不能引用UnityEngine运行Roll，实际DungeonLootPlanner.Roll→Store链由Unity原掉落测试验收。
        var wand = new OnlineOwnedItemSnapshot { InstanceId = Id(70), ContentId = "wandtest.wand.a", Revision = 1,
            UseTemplateWandStats = false, LocationKind = 1, ParentInstanceId = BagA, Slot = -1, X = 4,
            WandStats = new() { MaxMana = 160, ManaRegenPerSecond = 19, BaseCastDelay = .175f,
                ProgramCapacity = 32, BaseDraws = 3, BaseReloadTime = .7f, BaseSpreadDegrees = 4, Shuffle = true,
                AlwaysCastContentIds = ["norn.v2.cast_faster"] } };
        var card = new OnlineOwnedItemSnapshot { InstanceId = Id(71), ContentId = "wandtest.fire", Revision = 1,
            LocationKind = 2, ParentInstanceId = wand.InstanceId, Slot = 31, RemainingCharges = 4 };
        if (placement == "ground") f.Checkpoint.GroundItems = [new() { WorldOwnerId = Id(72), Item = Ground(wand).Item, Children = [card] }];
        else f.Checkpoint.Members[0].Assets.Items = [.. f.Checkpoint.Members[0].Assets.Items, wand, card];
        f.Trusted = f.Trusted with { NewItemIds = new HashSet<string>(StringComparer.Ordinal) { wand.InstanceId, card.InstanceId } };
        return f;
    }
    private sealed class Fixture
    {
        public required OnlineRunAssetCheckpoint Checkpoint;
        public required RunAssetCheckpointContext Trusted;
    }
    private static ContentManifestEntry Definition(string id, int kind) => new()
    {
        ContentId = id, Kind = kind, DisplayName = id, RulesSchemaVersion = 1, UseKind = kind == 3 ? 1 : 0,
        MaxStack = 1, ShapeWidth = 1, ShapeHeight = 1, OccupiedCells = [new() { X = 0, Y = 0 }],
        BackpackWidth = 8, BackpackHeight = 8, WandProgramCapacity = 4,
        ContainerCapacityGmu = 1000, ContainerMaterialsMask = 2, MaterialKey = 5, MaterialItemCapacityGmu = 1024
    };
}
