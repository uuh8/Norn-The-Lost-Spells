using System.Text.Json;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

public sealed class CompleteRunAssetCheckpointRulesTests : IDisposable
{
    private static readonly string Hash = new('a', 64), Player = Id(1), Run = Id(2), Instance = Id(3), Wand = Id(4), Card = Id(5), Bag = Id(6), Bottle = Id(7);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "complete-checkpoint-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly ContentManifestValidator _content;
    public CompleteRunAssetCheckpointRulesTests()
    {
        var manifest = new ContentManifest { BuildVersion = "complete-test", Items = [Definition("wandtest.wand.a", 1),
            Definition("wandtest.fire", 0), Definition("complete.bag", 2), Definition("complete.bottle", 3)] };
        manifest.Hash = ContentManifestRules.CalculateHash(manifest); File.WriteAllText(_path, JsonSerializer.Serialize(manifest, OnlineFactory.Json));
        _content = new(new OnlineOptions { ManifestPath = _path, GameplayConfigHash = Hash });
    }
    public void Dispose() => File.Delete(_path);
    [Fact]
    public void CompleteCurrentOwnerAndPublishedWorldAreAcceptedWithoutEnablingSolverRestore()
    { var f = Valid(); RunAssetCheckpointRules.Require(f.Candidate, f.Context, _content); }
    [Fact]
    public void EmptyActivePartitionRequiresExplicitVerifiedTerminalCaptureAndKeepsTheWorld()
    {
        var f = Valid(); f.Candidate.Members = [];
        // 普通在局保存不能把缺失Owner解释成全员已离局。终态授权只来自真实收据控制入口。
        var terminal = f.Context with { Members = [], KnownItems = new Dictionary<string, RunAssetCheckpointKnownItem>(),
            ExitedPlayerIds = new HashSet<string>([Player]), ExitedActors = new Dictionary<string, ulong> { [Player] = 1 } };
        Assert.Throws<OnlineFailure>(() => RunAssetCheckpointRules.Require(f.Candidate, terminal, _content));
        terminal = terminal with { TerminalCapture = true };
        RunAssetCheckpointRules.Require(f.Candidate, terminal, _content);
        Assert.NotNull(f.Candidate.World); Assert.False(f.Candidate.World.CanRestoreAuthorityRules);
        Assert.Throws<OnlineFailure>(() => RunAssetCheckpointRules.Require(f.Candidate,
            terminal with { ExitedActors = new Dictionary<string, ulong>() }, _content));
    }
    [Fact]
    public void TerminalWorldDiscardRequiresExactCustodyAndNeverAwardsUnpickedCoins()
    {
        var f = Valid(); var owner = f.Candidate.Members[0].Assets; var gold = owner.Gold;
        var wand = owner.Items.Single(i => i.InstanceId == Wand);
        var card = owner.Items.Single(i => i.InstanceId == Card);
        f.Candidate.Members = [];
        f.Candidate.GroundItems = [new() { WorldOwnerId = Id(20), Item = wand, Children = [card] }];
        f.Candidate.Coins = [new() { Id = 5, Amount = 13, Position = new() }];
        var remaining = new HashSet<string>([Wand, Card]);
        var ids = RunCheckpointCompleteRules.RequireTerminalDiscard(f.Candidate, remaining, new HashSet<string>(), false);
        Assert.Equal(2, ids.Length); Assert.Equal(gold, owner.Gold);
        Assert.False(f.Candidate.Coins[0].Collected); // 终止不伪造拾取者、也不发钱包款项。
        RunCheckpointCompleteRules.RequireTerminalDiscard(f.Candidate, new HashSet<string>(), remaining, true);
        Assert.Throws<OnlineFailure>(() => RunCheckpointCompleteRules.RequireTerminalDiscard(f.Candidate,
            new HashSet<string>([Wand, Card, Id(99)]), new HashSet<string>(), false));
        Assert.Throws<OnlineFailure>(() => RunCheckpointCompleteRules.RequireTerminalDiscard(f.Candidate,
            remaining, remaining, true));
    }

    [Fact]
    public void InFlightOriginalIdRemainsLiveOutsideTheOwnerUiPartition()
    {
        var f = Valid(); var owner = f.Candidate.Members[0].Assets; var item = owner.Items.Single(i => i.InstanceId == Bottle);
        owner.Items = owner.Items.Where(i => i.InstanceId != Bottle).ToArray(); string flight = Id(8);
        item.LocationKind = 6; item.ParentInstanceId = flight;
        f.Candidate.Flights = [new() { PlayerId = Player, ActorId = 1, FlightId = flight, WorldId = "material-world", SourceId = "container-source",
            Item = item, HasPhysicalProxy = true, Radius = .2f, Position = new(), Rotation = new() { W = 1 }, Velocity = new() { Y = 1 } }];
        RunAssetCheckpointRules.Require(f.Candidate, f.Context, _content);
    }
    [Fact]
    public void ImpactedFlightWithoutProxyKeepsActualImpactPositionAndNoInventedRotationOrVelocity()
    {
        var f = Valid(); var owner = f.Candidate.Members[0].Assets; var item = owner.Items.Single(i => i.InstanceId == Bottle);
        owner.Items = owner.Items.Where(i => i.InstanceId != Bottle).ToArray(); string flight = Id(8);
        item.LocationKind = 6; item.ParentInstanceId = flight;
        f.Candidate.Flights = [new() { PlayerId = Player, ActorId = 1, FlightId = flight, WorldId = "material-world", SourceId = "container-source",
            Item = item, Impacted = true, HasPhysicalProxy = false, Radius = .2f, Position = new() { X = 3 } }];
        RunAssetCheckpointRules.Require(f.Candidate, f.Context, _content);
        f.Candidate.Flights[0].Impacted = false;
        Assert.Throws<OnlineFailure>(() => RunAssetCheckpointRules.Require(f.Candidate, f.Context, _content));
    }
    [Theory, InlineData("output"), InlineData("loss")]
    public void PriorBudgetCannotRefillAndLossCounterCannotDecrease(string defect)
    {
        var f = Valid(); f.Candidate.MaterialOutputs = [new() { PlayerId = Player, ActorId = 1, OutputId = Id(9), WorldId = "material-world",
            Remaining = [new() { MaterialKey = 1, Quantity = 7 }] }];
        f.Candidate.LostMaterials = [new() { PlayerId = Player, ActorId = 1, Source = "SpellOutput", MaterialKey = 1, Quantity = 3 }];
        f.Context = f.Context with { PreviousCheckpoint = Clone(f.Candidate), LastSequence = 1 };
        f.Candidate.Sequence = 2; f.Candidate.CheckpointId = Id(11);
        if (defect == "output") f.Candidate.MaterialOutputs[0].Remaining[0].Quantity = 8;
        else f.Candidate.LostMaterials[0].Quantity = 2;
        Assert.Throws<OnlineFailure>(() => RunAssetCheckpointRules.Require(f.Candidate, f.Context, _content));
    }
    [Fact]
    public void NeverAdmittedUsesOnlyExplicitReservedEntryFacts()
    {
        var f = Valid(); var member = f.Candidate.Members[0]; member.NeverAdmitted = true; member.ConnectionEpoch = 0;
        member.Wands = []; member.SpellUses = []; member.ResourceVersion = member.SelectionVersion = member.SpellUseVersion = 0;
        f.Context = f.Context with { Members = [new(Player, 1, 0, 1, true, true, Clone(member.Assets))] };
        RunAssetCheckpointRules.Require(f.Candidate, f.Context, _content);
    }
    [Theory, InlineData("world"), InlineData("flights"), InlineData("outputs"), InlineData("lost"), InlineData("wands"),
        InlineData("spell-uses"), InlineData("resource"), InlineData("mana"), InlineData("tool"), InlineData("never-admitted")]
    public void MissingCompleteFactsOrForgedOwnerReadingsAreRejected(string defect)
    {
        var f = Valid(); var m = f.Candidate.Members[0];
        switch (defect)
        {
            case "world": f.Candidate.World = null!; break;
            case "flights": f.Candidate.Flights = null!; break;
            case "outputs": f.Candidate.MaterialOutputs = null!; break;
            case "lost": f.Candidate.LostMaterials = null!; break;
            case "wands": m.Wands = null!; break;
            case "spell-uses": m.SpellUses = null!; break;
            case "resource": m.ResourceVersion = 0; break;
            case "mana": m.Wands[0].CurrentMana = float.NaN; break;
            case "tool": m.SelectedToolSlotPlusOne = 7; break;
            case "never-admitted": m.NeverAdmitted = true; break;
        }
        Assert.Throws<OnlineFailure>(() => RunAssetCheckpointRules.Require(f.Candidate, f.Context, _content));
    }
    [Fact]
    public void MaterialOutputAndLossKeepIndependentBudgetFacts()
    {
        var f = Valid(); f.Candidate.MaterialOutputs = [new() { PlayerId = Player, ActorId = 1, OutputId = Id(9), WorldId = "material-world",
            Remaining = [new() { MaterialKey = 1, Quantity = 7 }] }];
        f.Candidate.LostMaterials = [new() { PlayerId = Player, ActorId = 1, Source = "SpellOutput", MaterialKey = 1, Quantity = 3 }];
        RunAssetCheckpointRules.Require(f.Candidate, f.Context, _content);
    }
    private static Fixture Valid()
    {
        var assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = Run, PlayerId = Player,
            ActorId = 1, InventoryId = Run + "/" + Player, InventoryVersion = 1, Gold = 0, CurrencyVersion = 1, GameplayConfigHash = Hash, SelectedWandInstanceId = Wand,
            Items = [new() { InstanceId = Wand, ContentId = "wandtest.wand.a", Revision = 1, LocationKind = 3, UseTemplateWandStats = true },
                new() { InstanceId = Card, ContentId = "wandtest.fire", Revision = 1, LocationKind = 2, ParentInstanceId = Wand, RemainingCharges = 10 },
                new() { InstanceId = Bag, ContentId = "complete.bag", Revision = 1, LocationKind = 4 },
                new() { InstanceId = Bottle, ContentId = "complete.bottle", Revision = 1, LocationKind = 1, ParentInstanceId = Bag, Slot = -1,
                    ContainerState = new() { SchemaVersion = 1, Components = [new() { MaterialKey = 1, Quantity = 5 }] } }] };
        return new() { Candidate = new() { SchemaVersion = OnlineRunAssetCheckpoint.CompleteSchemaVersion, CheckpointId = Id(10), InstanceId = Instance, RunId = Run, RoomEpoch = 1,
            Sequence = 1, GameplayConfigHash = Hash, World = RunCheckpointWorldRulesTests.Valid(), Flights = [], MaterialOutputs = [], LostMaterials = [], Coins = [],
            Members = [new() { PlayerId = Player, ActorId = 1, ConnectionEpoch = 1, Assets = assets,
                ResourceVersion = 1, SelectionVersion = 1, SpellUseVersion = 1,
                Wands = [new() { InstanceId = Wand, ProgramRevision = 1, CurrentMana = 1, CooldownRemaining = .1f }],
                SpellUses = [new() { InstanceId = Card, Eligible = true }] }] },
            Context = new() { InstanceId = Instance, RunId = Run, RoomEpoch = 1, InstanceLeaseValid = true,
                Members = [new(Player, 1, 1, 1, true)], KnownItems = assets.Items.ToDictionary(i => i.InstanceId,
                    i => new RunAssetCheckpointKnownItem(Player, Clone(i)), StringComparer.Ordinal) } };
    }
    private static string Id(int n) => n.ToString("x32");
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, OnlineFactory.Json), OnlineFactory.Json)!;
    private sealed class Fixture { public required OnlineRunAssetCheckpoint Candidate; public required RunAssetCheckpointContext Context; }
    private static ContentManifestEntry Definition(string id, int kind) => new() { ContentId = id, Kind = kind, DisplayName = id,
        RulesSchemaVersion = 1, UseKind = kind == 3 ? 1 : 0, ShapeWidth = 1, ShapeHeight = 1, MaxStack = 1,
        OccupiedCells = [new() { X = 0, Y = 0 }], BackpackWidth = 8, BackpackHeight = 8, WandProgramCapacity = 4,
        ContainerCapacityGmu = 1000, ContainerMaterialsMask = 2, MaterialKey = 1, MaterialItemCapacityGmu = 1000 };
}
