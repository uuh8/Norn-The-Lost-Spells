using System.Text.Json;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>不连接数据库的业务规则 RED/GREEN；真实 Migration/事务另有 MySQL 集成测试。</summary>
public sealed class OwnedCombatAssetRulesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "n4-owned-manifest-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly ContentManifestValidator _content;
    private static readonly string Hash = new('a', 64);
    private static readonly string Player = new('1', 32), Wand = new('2', 32), Card = new('3', 32), Run = new('4', 32);
    public OwnedCombatAssetRulesTests()
    {
        // 规则 fixture 显式来自本测试目录：后端必须消费可信形状/容量，不能猜模板默认值。
        var manifest = new ContentManifest { SchemaVersion = 2, BuildVersion = "owned-test", Items =
            [Definition("wandtest.wand.a", 1), Definition("wandtest.fire", 0),
             Definition("owned.bag", 2), Definition("owned.bottle", 3, true), Definition("owned.vial", 3), Definition("owned.solid", 4)] };
        manifest.Hash = ContentManifestRules.CalculateHash(manifest);
        File.WriteAllText(_path, JsonSerializer.Serialize(manifest, ContentManifestValidator.Json));
        _content = new ContentManifestValidator(new OnlineOptions { ManifestPath = _path, GameplayConfigHash = Hash });
    }
    public void Dispose() => File.Delete(_path);
    private static OnlineOwnedAssetSnapshot Valid() => new()
    {
        SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, PlayerId = Player, RunId = Run, InventoryId = Run + "/" + Player,
        ActorId = 1, InventoryVersion = 1, Gold = 0, CurrencyVersion = 1, GameplayConfigHash = Hash, SelectedWandInstanceId = Wand,
        Items = [new() { InstanceId = Wand, ContentId = "wandtest.wand.a", Revision = 1, LocationKind = 3, Slot = 0, UseTemplateWandStats = true },
            new() { InstanceId = Card, ContentId = "wandtest.fire", Revision = 1, LocationKind = 2, ParentInstanceId = Wand, Slot = 0, RemainingCharges = 30 }]
    };
    [Fact, Trait("Category", "OwnedCombatContract")]
    public void FiniteOwnedProgram_IsAcceptedWithoutGrantingUnlimited()
    {
        var assets = Valid(); OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        Assert.Equal(30, assets.Items[1].RemainingCharges); Assert.False(assets.Items[1].HasUnlimitedCharges);
    }
    [Fact, Trait("Category", "OwnedCombatContract")]
    public void MissingLegacySchema_AndMissingRulesHash_AreRejected()
    {
        var assets = Valid(); assets.SchemaVersion = 0; Reject(assets);
        assets = Valid(); assets.GameplayConfigHash = ""; Reject(assets);
        assets = Valid(); Assert.Throws<OnlineFailure>(() => OwnedCombatAssetRules.Require(assets, Player, "", _content, true));
    }
    [Fact, Trait("Category", "OwnedCombatContract")]
    public void EmptyHandedOwnerKeepsItsRealBagWithoutInventingASelectedWand()
    {
        var assets = Valid(); assets.SelectedWandInstanceId = "";
        assets.Items = [new() { InstanceId = Bag, ContentId = "owned.bag", Revision = 1, LocationKind = 4, Slot = 0 }];
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        Assert.Single(assets.Items); Assert.Equal(Bag, assets.Items[0].InstanceId); Assert.Equal("", assets.SelectedWandInstanceId);
    }
    [Fact, Trait("Category", "OwnedCombatContract")]
    public void EmptyOwnerIsARealZeroItemSnapshotAndStillCannotSelectAForeignWand()
    {
        var assets = Valid(); assets.Items = []; assets.SelectedWandInstanceId = "";
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        Assert.Empty(assets.Items);
        assets.SelectedWandInstanceId = Wand; Reject(assets);
    }
    [Fact, Trait("Category", "OwnedCombatContract")]
    public void ForeignParent_UnknownLocation_AndDuplicateSlot_AreRejected()
    {
        var assets = Valid(); assets.Items[1].ParentInstanceId = new string('9', 32); Reject(assets);
        assets = Valid(); assets.Items[1].LocationKind = 1; Reject(assets);
        assets = Valid(); assets.Items = [assets.Items[0], assets.Items[1], new() { InstanceId = new string('8', 32), ContentId = "wandtest.fire", Revision = 1, LocationKind = 2, ParentInstanceId = Wand, Slot = 0, RemainingCharges = 1 }]; Reject(assets);
    }
    [Fact, Trait("Category", "OwnedCombatContract")]
    public void WrongOwner_SelectedForeignWand_AndForgedWandResources_AreRejected()
    {
        var assets = Valid(); assets.PlayerId = new string('9', 32); Reject(assets);
        assets = Valid(); assets.SelectedWandInstanceId = new string('9', 32); Reject(assets);
        assets = Valid(); assets.Items[0].HasUnlimitedCharges = true; Reject(assets);
        assets = Valid(); assets.Items[1].RemainingCharges = -1; Reject(assets);
        assets = Valid(); assets.Items[0].UseTemplateWandStats = false; Reject(assets);
    }
    [Theory, InlineData(32), InlineData(63), Trait("Category", "OwnedCombatContract")]
    public void CardOutsideRuntimeMaximumCannotBecomeFrozenCombatAsset(int slot)
    {
        // Unity WandStats 的明确上限是32槽；控制面不能冻结 DS 必定拒绝的第33..64槽。
        var assets = Valid(); assets.Items[1].Slot = slot; Reject(assets);
    }

    private static readonly string Bag = new('5', 32), Bottle = new('6', 32), Tool = new('7', 32), Spare = new('8', 32), SpareCard = new('a', 32);

    private static ContentManifestEntry Definition(string id, int kind, bool shaped = false) => new()
    {
        ContentId = id, Kind = kind, DisplayName = id, RulesSchemaVersion = 1,
        UseKind = kind == 3 ? 1 : 0, MaxStack = 1,
        ShapeWidth = shaped ? 2 : 1, ShapeHeight = shaped ? 2 : 1,
        OccupiedCells = shaped ? [new() { X = 0, Y = 0 }, new() { X = 0, Y = 1 }, new() { X = 1, Y = 1 }] : [new() { X = 0, Y = 0 }],
        // ItemDefinition 会为所有种类导出完整规则，本 fixture 也显式给出这些值，不依赖生产补默认。
        BackpackWidth = 8, BackpackHeight = 8, WandProgramCapacity = 4,
        ContainerCapacityGmu = 15000, ContainerMaterialsMask = 410, MaterialKey = 5, MaterialItemCapacityGmu = 1024
    };
    private static OnlineOwnedContainerStateSnapshot State(params OnlineOwnedMaterialAmountSnapshot[] components)
        => new() { SchemaVersion = 1, Components = components };

    private static OnlineOwnedAssetSnapshot Carried()
    {
        var assets = Valid();
        assets.Items = [.. assets.Items,
            new() { InstanceId = Bag, ContentId = "owned.bag", Revision = 2, LocationKind = 4, Slot = 0 },
            new() { InstanceId = Bottle, ContentId = "owned.bottle", Revision = 3, LocationKind = 1, ParentInstanceId = Bag, Slot = -1, X = 0, Y = 0,
                ContainerState = State(new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 20 }, new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 3, Quantity = 7 }) },
            new() { InstanceId = Tool, ContentId = "owned.vial", Revision = 1, LocationKind = 8, Slot = 6, ContainerState = State() },
            new() { InstanceId = Spare, ContentId = "wandtest.wand.a", Revision = 2, LocationKind = 1, ParentInstanceId = Bag, Slot = -1, X = 4, Y = 0, UseTemplateWandStats = true },
            new() { InstanceId = SpareCard, ContentId = "wandtest.fire", Revision = 4, LocationKind = 2, ParentInstanceId = Spare, Slot = 3, RemainingCharges = 9 }];
        return assets;
    }

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void RealBackpack_MixedContainer_Tool_AndSpareWandProgram_AreAcceptedAsOneClosure()
    {
        var assets = Carried();
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        Assert.Equal(20UL, assets.Items.Single(i => i.InstanceId == Bottle).ContainerState.Components[0].Quantity);
        Assert.Empty(assets.Items.Single(i => i.InstanceId == Tool).ContainerState.Components);
        Assert.Equal(Spare, assets.Items.Single(i => i.InstanceId == SpareCard).ParentInstanceId);
    }

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void SparseShapeHole_IsUsableButOccupiedCellStillRejectsOverlap()
    {
        var assets = Carried();
        assets.Items = [.. assets.Items, new() { InstanceId = new string('b', 32), ContentId = "owned.vial", Revision = 1,
            LocationKind = 1, ParentInstanceId = Bag, Slot = -1, X = 1, Y = 0, ContainerState = State() }];
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        assets.Items[^1].Y = 1;
        Reject(assets);
    }

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void RotatedSparseShape_UsesActualRotatedCellsRatherThanItsOldOutline()
    {
        var assets = Carried();
        assets.Items.Single(i => i.InstanceId == Bottle).QuarterTurns = 1;
        // 原 L：(0,0),(0,1),(1,1)，顺时针后：(1,0),(0,0),(0,1)，空洞是(1,1)。
        assets.Items = [.. assets.Items, new() { InstanceId = new string('b', 32), ContentId = "owned.vial", Revision = 1,
            LocationKind = 1, ParentInstanceId = Bag, Slot = -1, X = 1, Y = 1, ContainerState = State() }];
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        assets.Items[^1].X = 0;
        Reject(assets);
    }

    [Theory, InlineData("missing"), InlineData("schema"), InlineData("null-components"), InlineData("duplicate-key"),
        InlineData("unknown-key"), InlineData("zero"), InlineData("incompatible"), InlineData("capacity"), InlineData("overflow"),
        Trait("Category", "OwnedCombatContract")]
    public void InvalidContainerState_IsRejectedWithoutOmittingTheBadItem(string defect)
    {
        var assets = Carried(); var bottle = assets.Items.Single(i => i.InstanceId == Bottle);
        switch (defect)
        {
            case "missing": bottle.ContainerState = null; break;
            case "schema": bottle.ContainerState.SchemaVersion = 0; break;
            case "null-components": bottle.ContainerState.Components = null!; break;
            case "duplicate-key": bottle.ContainerState = State(new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 1 }, new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 1 }); break;
            case "unknown-key": bottle.ContainerState = State(new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 9, Quantity = 1 }); break;
            case "zero": bottle.ContainerState = State(new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 0 }); break;
            case "incompatible": bottle.ContainerState = State(new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 2, Quantity = 1 }); break;
            case "capacity": bottle.ContainerState = State(new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 15001 }); break;
            case "overflow": bottle.ContainerState = State(new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = ulong.MaxValue }, new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 3, Quantity = 2 }); break;
        }
        Reject(assets);
        Assert.Contains(assets.Items, i => i.InstanceId == Bottle);
    }

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void NonContainerCannotSmuggleState_AndStateCannotMakeANonUsableTool()
    {
        var assets = Carried(); assets.Items.Single(i => i.InstanceId == Card).ContainerState = State(); Reject(assets);
        assets = Carried(); var tool = assets.Items.Single(i => i.InstanceId == Tool);
        tool.ContentId = "wandtest.fire"; tool.ContainerState = null; Reject(assets);
    }

    [Theory, InlineData("bag-root-grid"), InlineData("bag-slot"), InlineData("missing-bag"), InlineData("grid-overflow"),
        InlineData("rotation"), InlineData("tool-slot"), InlineData("tool-grid"), InlineData("legacy-flat"),
        InlineData("card-parent"), InlineData("trusted-card-capacity"), Trait("Category", "OwnedCombatContract")]
    public void InvalidRealLocationsAndParents_AreRejected(string defect)
    {
        var assets = Carried();
        var bag = assets.Items.Single(i => i.InstanceId == Bag);
        var bottle = assets.Items.Single(i => i.InstanceId == Bottle);
        var tool = assets.Items.Single(i => i.InstanceId == Tool);
        var card = assets.Items.Single(i => i.InstanceId == SpareCard);
        switch (defect)
        {
            case "bag-root-grid": bag.X = 1; break;
            case "bag-slot": bag.Slot = 1; break;
            case "missing-bag": bottle.ParentInstanceId = new string('f', 32); break;
            case "grid-overflow": bottle.X = 7; break;
            case "rotation": bottle.QuarterTurns = 4; break;
            case "tool-slot": tool.Slot = 7; break;
            case "tool-grid": tool.Y = 1; break;
            case "legacy-flat": bottle.ParentInstanceId = ""; bottle.Slot = 0; break;
            case "card-parent": card.ParentInstanceId = Bottle; break;
            case "trusted-card-capacity": card.Slot = 4; break;
        }
        Reject(assets);
    }

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void SolidMaterialWithoutQuantityContract_IsRejectedRatherThanImportedAsAnEmptyShell()
    {
        var assets = Carried();
        // 形状/父链/规则都合法，唯一缺口是当前Owned ABI没有MaterialItemState.Key/Quantity。
        // 不能因G/K留后续，却把固体导入成Material=null并称为资产无损。
        assets.Items = [.. assets.Items, new() { InstanceId = new string('c', 32), ContentId = "owned.solid", Revision = 1,
            LocationKind = 1, ParentInstanceId = Bag, Slot = -1, X = 6, Y = 2 }];
        Reject(assets);
    }

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void BackpackSpareWandProgramsMayExceed171Items_WithoutExceedingTheSharedMaximum()
    {
        var assets = Valid();
        var items = assets.Items.ToList();
        items.Add(new() { InstanceId = Bag, ContentId = "owned.bag", Revision = 1, LocationKind = 4, Slot = 0 });
        for (int i = 0; i < 35; i++)
        {
            string wand = (1000 + i).ToString("x32");
            items.Add(new() { InstanceId = wand, ContentId = "wandtest.wand.a", Revision = 1, LocationKind = 1,
                ParentInstanceId = Bag, Slot = -1, X = i % 8, Y = i / 8, UseTemplateWandStats = true });
            for (int slot = 0; slot < 4; slot++)
                items.Add(new() { InstanceId = (2000 + i * 4 + slot).ToString("x32"), ContentId = "wandtest.fire",
                    Revision = 1, LocationKind = 2, ParentInstanceId = wand, Slot = slot, RemainingCharges = 1 });
        }
        assets.Items = items.ToArray();
        Assert.True(assets.Items.Length > 171);
        Assert.True(assets.Items.Length < OnlineOwnedAssetSnapshot.MaximumItems);
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
    }

    [Theory, InlineData(1UL), InlineData(256UL), InlineData(1024UL), Trait("Category", "OwnedMaterialContract")]
    public void CarriedMaterialKeepsItsExplicitQuantityInsteadOfBeingFilledFromTrustedCapacity(ulong quantity)
    {
        var assets = CarriedMaterial(quantity);
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        var solid = assets.Items[^1];
        Assert.Equal((byte)5, solid.MaterialState.MaterialKey);
        Assert.Equal(quantity, solid.MaterialState.Quantity);
        Assert.Null(solid.ContainerState);
        Assert.Equal(Bag, solid.ParentInstanceId);
    }

    [Theory, InlineData("missing"), InlineData("schema-zero"), InlineData("schema-two"), InlineData("empty-shell"),
        InlineData("wrong-key"), InlineData("unknown-key"), InlineData("zero"), InlineData("capacity"), InlineData("container-state"),
        Trait("Category", "OwnedMaterialContract")]
    public void MaterialNeedsCurrentStateMatchingItsTrustedKeyAndPositiveBoundedQuantity(string defect)
    {
        var assets = CarriedMaterial(256); var solid = assets.Items[^1];
        switch (defect)
        {
            case "missing": solid.MaterialState = null!; break;
            case "schema-zero": solid.MaterialState.SchemaVersion = 0; break;
            case "schema-two": solid.MaterialState.SchemaVersion = 2; break;
            case "empty-shell": solid.MaterialState = new(); break;
            case "wrong-key": solid.MaterialState.MaterialKey = 6; break;
            case "unknown-key": solid.MaterialState.MaterialKey = 9; break;
            case "zero": solid.MaterialState.Quantity = 0; break;
            case "capacity": solid.MaterialState.Quantity = 1025; break;
            case "container-state": solid.ContainerState = State(); break;
        }
        Reject(assets);
        Assert.Contains(assets.Items, i => i.InstanceId == solid.InstanceId);
    }

    [Theory, InlineData("wand"), InlineData("spell"), InlineData("container"), Trait("Category", "OwnedMaterialContract")]
    public void AValidMaterialStateCannotBeAttachedToAnotherItemKind(string kind)
    {
        var assets = Carried();
        var item = assets.Items.Single(i => i.InstanceId == (kind == "wand" ? Wand : kind == "spell" ? Card : Bottle));
        item.MaterialState = MaterialState(256);
        Reject(assets);
    }

    [Fact, Trait("Category", "OwnedMaterialContract")]
    public void MaterialStateJsonPreservesUnsignedQuantityWithoutTreatingItAsACapacityGrant()
    {
        // 这里只证明中立JSON的无损值合同，不把ulong.MaxValue声明为正式15000GMU材料能合法容纳的数量。
        var original = MaterialState(ulong.MaxValue);
        string json = JsonSerializer.Serialize(original, ContentManifestValidator.Json);
        var copy = JsonSerializer.Deserialize<OnlineOwnedMaterialStateSnapshot>(json, ContentManifestValidator.Json)!;
        Assert.Equal(1u, copy.SchemaVersion); Assert.Equal((byte)5, copy.MaterialKey); Assert.Equal(ulong.MaxValue, copy.Quantity);
        original.Quantity = 1;
        Assert.Equal(ulong.MaxValue, copy.Quantity);
        var assets = CarriedMaterial(copy.Quantity);
        Reject(assets); // 相同值进真实规则仍须被1024容量拒绝，不截断或钳制成另一笔资产。
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "OwnedAttachmentContract")]
    public void SpellAttachmentRetainsMissingSourceUnsignedDoseAndEnabledAcrossFrozenJson(bool enabled)
    {
        var assets = Carried(); var card = assets.Items.Single(i => i.InstanceId == Card);
        card.AttachmentState = AttachmentState(enabled);
        Assert.DoesNotContain(assets.Items, i => i.InstanceId == card.AttachmentState.ContainerId);
        // 设置没有GMU所有权；失效来源允许保留，是否能实际供料留给DS的当前背包资格裁决。
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        string json = JsonSerializer.Serialize(assets, ContentManifestValidator.Json);
        var copy = JsonSerializer.Deserialize<OnlineOwnedAssetSnapshot>(json, ContentManifestValidator.Json)!;
        var setting = copy.Items.Single(i => i.InstanceId == Card).AttachmentState;
        Assert.NotNull(setting); Assert.Equal(1u, setting.SchemaVersion);
        Assert.Equal(new string('f', 32), setting.ContainerId); Assert.Equal(ulong.MaxValue, setting.Dose); Assert.Equal(enabled, setting.Enabled);
        card.AttachmentState.Dose = 1; card.AttachmentState.Enabled = !enabled;
        Assert.Equal(ulong.MaxValue, setting.Dose); Assert.Equal(enabled, setting.Enabled);
    }

    [Theory, InlineData("schema-zero"), InlineData("schema-two"), InlineData("blank"), InlineData("malformed"),
        InlineData("uppercase"), InlineData("zero-dose"), InlineData("empty-shell"), Trait("Category", "OwnedAttachmentContract")]
    public void SpellAttachmentRequiresExplicitSchemaLegalSourceIdentityAndPositiveDose(string defect)
    {
        var assets = Carried(); var card = assets.Items.Single(i => i.InstanceId == Card);
        card.AttachmentState = AttachmentState(true);
        switch (defect)
        {
            case "schema-zero": card.AttachmentState.SchemaVersion = 0; break;
            case "schema-two": card.AttachmentState.SchemaVersion = 2; break;
            case "blank": card.AttachmentState.ContainerId = ""; break;
            case "malformed": card.AttachmentState.ContainerId = "missing-source"; break;
            case "uppercase": card.AttachmentState.ContainerId = new string('F', 32); break;
            case "zero-dose": card.AttachmentState.Dose = 0; break;
            case "empty-shell": card.AttachmentState = new(); break;
        }
        Reject(assets);
    }

    [Theory, InlineData("wand"), InlineData("container"), InlineData("material"), InlineData("bag"), Trait("Category", "OwnedAttachmentContract")]
    public void SpellAttachmentCannotGiveSettingsToANonSpellInstance(string kind)
    {
        var assets = CarriedMaterial(256);
        var item = kind == "material" ? assets.Items[^1] : assets.Items.Single(i => i.InstanceId == (kind == "wand" ? Wand : kind == "container" ? Bottle : Bag));
        item.AttachmentState = AttachmentState(true);
        Reject(assets);
    }

    [Theory, InlineData("container"), InlineData("material"), InlineData("wand"), Trait("Category", "OwnedAttachmentContract")]
    public void SpellAttachmentDoesNotPermitOtherKindsOfStateOnTheCard(string otherState)
    {
        var assets = Carried(); var card = assets.Items.Single(i => i.InstanceId == Card);
        card.AttachmentState = AttachmentState(true);
        if (otherState == "container") card.ContainerState = State();
        else if (otherState == "material") card.MaterialState = MaterialState(1);
        else card.UseTemplateWandStats = true;
        Reject(assets);
    }

    [Theory, InlineData(0), InlineData(11), Trait("Category", "OwnedSpellStorageContract")]
    public void SpellStorage_RealPlayerRootKeepsCardIdentityChargesAndSettingsAcrossAllTrustedReadGates(int slot)
    {
        // 原Catalog正式容量12，Store将SpellStorage视为玩家根而非Wand/Bag的子节点。
        // 与法杖程序的同号槽互不占位；移动卡不能换ID、补次数或丢掉供料设置。
        var assets = SpellStorage(slot);
        var card = assets.Items.Single(i => i.InstanceId == Card);
        card.AttachmentState = AttachmentState(true);
        assets.Items = [.. assets.Items, new() { InstanceId = SpareCard, ContentId = "wandtest.fire", Revision = 1,
            LocationKind = 2, ParentInstanceId = Wand, Slot = 0, RemainingCharges = 7 }];
        OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true);
        OwnedCombatAssetRules.RequireCheckpoint(assets, Player, Hash, _content);
        OwnedCombatAssetRules.RequirePersisted(assets, Player, Hash, _content);
        Assert.Equal(Card, card.InstanceId); Assert.Equal(30, card.RemainingCharges); Assert.False(card.HasUnlimitedCharges);
        Assert.Equal("", card.ParentInstanceId); Assert.Equal(slot, card.Slot);
        Assert.Equal(ulong.MaxValue, card.AttachmentState.Dose); Assert.True(card.AttachmentState.Enabled);
        Assert.Equal(3, assets.Items.Length);
    }

    [Theory, InlineData("negative-slot"), InlineData("capacity"), InlineData("catalog-validation-limit"),
        InlineData("non-spell"), InlineData("wand-parent"), InlineData("player-as-parent"), InlineData("null-parent"),
        InlineData("grid-x"), InlineData("grid-y"), InlineData("rotation"), InlineData("duplicate-slot"),
        Trait("Category", "OwnedSpellStorageContract")]
    public void SpellStorage_InvalidKindSlotParentOrGridIsRejectedWithoutDroppingTheCard(string defect)
    {
        var assets = SpellStorage(0); var card = assets.Items.Single(i => i.InstanceId == Card);
        switch (defect)
        {
            case "negative-slot": card.Slot = -1; break;
            case "capacity": card.Slot = 12; break;
            case "catalog-validation-limit": card.Slot = 128; break;
            case "non-spell": card.ContentId = "owned.vial"; card.RemainingCharges = 0; card.ContainerState = State(); break;
            case "wand-parent": card.ParentInstanceId = Wand; break;
            case "player-as-parent": card.ParentInstanceId = Player; break;
            case "null-parent": card.ParentInstanceId = null!; break;
            case "grid-x": card.X = 1; break;
            case "grid-y": card.Y = 1; break;
            case "rotation": card.QuarterTurns = 1; break;
            case "duplicate-slot": assets.Items = [.. assets.Items, new() { InstanceId = SpareCard, ContentId = "wandtest.fire",
                Revision = 1, LocationKind = 7, Slot = 0, RemainingCharges = 1 }]; break;
        }
        Reject(assets);
        Assert.Contains(assets.Items, i => i.InstanceId == Card);
    }

    private static OnlineOwnedAssetSnapshot SpellStorage(int slot)
    {
        var assets = Valid(); var card = assets.Items[1];
        card.LocationKind = 7; card.ParentInstanceId = ""; card.Slot = slot;
        return assets;
    }

    private static OnlineOwnedSpellAttachmentStateSnapshot AttachmentState(bool enabled)
        => new() { SchemaVersion = 1, ContainerId = new string('f', 32), Dose = ulong.MaxValue, Enabled = enabled };

    private static OnlineOwnedMaterialStateSnapshot MaterialState(ulong quantity)
        => new() { SchemaVersion = 1, MaterialKey = 5, Quantity = quantity };

    private static OnlineOwnedAssetSnapshot CarriedMaterial(ulong quantity)
    {
        var assets = Carried();
        assets.Items = [.. assets.Items, new() { InstanceId = new string('c', 32), ContentId = "owned.solid", Revision = 1,
            LocationKind = 1, ParentInstanceId = Bag, Slot = -1, X = 6, Y = 2, MaterialState = MaterialState(quantity) }];
        return assets;
    }

    private void Reject(OnlineOwnedAssetSnapshot assets) => Assert.Throws<OnlineFailure>(() => OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true));
}
