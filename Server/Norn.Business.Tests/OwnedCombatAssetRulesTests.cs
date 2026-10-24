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
        var manifest = new ContentManifest { BuildVersion = "owned-test", Items = [new() { ContentId = "wandtest.wand.a", Kind = 1, DisplayName = "Wand" }, new() { ContentId = "wandtest.fire", Kind = 0, DisplayName = "Fire" }] };
        manifest.Hash = ContentManifestRules.CalculateHash(manifest);
        File.WriteAllText(_path, JsonSerializer.Serialize(manifest, ContentManifestValidator.Json));
        _content = new ContentManifestValidator(new OnlineOptions { ManifestPath = _path, GameplayConfigHash = Hash });
    }
    public void Dispose() => File.Delete(_path);
    private static OnlineOwnedAssetSnapshot Valid() => new()
    {
        SchemaVersion = 1, PlayerId = Player, RunId = Run, InventoryId = Run + "/" + Player,
        ActorId = 1, InventoryVersion = 1, GameplayConfigHash = Hash, SelectedWandInstanceId = Wand,
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
    private void Reject(OnlineOwnedAssetSnapshot assets) => Assert.Throws<OnlineFailure>(() => OwnedCombatAssetRules.Require(assets, Player, Hash, _content, true));
}
