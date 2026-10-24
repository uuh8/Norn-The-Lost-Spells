using System.Text.Json;
using Game.NetworkProtocol;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>HTTP 冻结快照不能静默丢弃资源和父子关系；旧 JSON 不得获得可执行的 N4 默认值。</summary>
public sealed class OwnedCombatContractTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void ProfileRoundTrip_PreservesExplicitOwnedResourcesAndInstalledParent()
    {
        const string incoming = """
            {"PlayerId":"player","CombatAssets":{"SchemaVersion":1,"RunId":"run","PlayerId":"player","InventoryId":"inventory","GameplayConfigHash":"rules","SelectedWandInstanceId":"wand","ActorId":2,"InventoryVersion":7,"Items":[{"InstanceId":"wand","ContentId":"wandtest.wand.a","Revision":1,"LocationKind":3,"Slot":0,"UseTemplateWandStats":true},{"InstanceId":"card","ContentId":"wandtest.fire","ParentInstanceId":"wand","Revision":2,"LocationKind":2,"Slot":0,"RemainingCharges":30,"HasUnlimitedCharges":false}]}}
            """;
        var profile = JsonSerializer.Deserialize<OnlineProfileReply>(incoming, Json)!;
        using var restored = JsonDocument.Parse(JsonSerializer.Serialize(profile, Json));
        Assert.True(restored.RootElement.TryGetProperty("CombatAssets", out var assets), "HTTP Profile dropped CombatAssets.");
        Assert.Equal(1u, assets.GetProperty("SchemaVersion").GetUInt32());
        Assert.Equal("wand", assets.GetProperty("SelectedWandInstanceId").GetString());
        Assert.Equal(7u, assets.GetProperty("InventoryVersion").GetUInt32());
        var card = assets.GetProperty("Items")[1];
        Assert.Equal("wand", card.GetProperty("ParentInstanceId").GetString());
        Assert.Equal(30, card.GetProperty("RemainingCharges").GetInt32());
        Assert.False(card.GetProperty("HasUnlimitedCharges").GetBoolean());
    }

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void LegacyProfile_DoesNotManufactureExecutableCombatAssets()
    {
        var profile = JsonSerializer.Deserialize<OnlineProfileReply>("{\"PlayerId\":\"legacy\",\"Items\":[]}", Json)!;
        using var restored = JsonDocument.Parse(JsonSerializer.Serialize(profile, Json));
        Assert.True(restored.RootElement.TryGetProperty("CombatAssets", out var assets), "The new contract must distinguish missing combat data explicitly.");
        Assert.Equal(JsonValueKind.Null, assets.ValueKind);
    }

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void InstanceContracts_RoundTripGameplayHashSeparatelyFromIdentityHash()
    {
        const string incoming = "{\"GameplayConfigHash\":\"gameplay\",\"ManifestHash\":\"identity\"}";
        var descriptor = JsonSerializer.Deserialize<InstanceLaunchDescriptor>(incoming, Json)!;
        var ready = JsonSerializer.Deserialize<InstanceReadyRequest>(incoming, Json)!;
        using var descriptorJson = JsonDocument.Parse(JsonSerializer.Serialize(descriptor, Json));
        using var readyJson = JsonDocument.Parse(JsonSerializer.Serialize(ready, Json));
        Assert.True(descriptorJson.RootElement.TryGetProperty("GameplayConfigHash", out var launchHash), "Launch dropped GameplayConfigHash.");
        Assert.Equal("gameplay", launchHash.GetString());
        Assert.Equal("identity", descriptorJson.RootElement.GetProperty("ManifestHash").GetString());
        Assert.True(readyJson.RootElement.TryGetProperty("GameplayConfigHash", out var readyHash), "Ready dropped GameplayConfigHash.");
        Assert.Equal("gameplay", readyHash.GetString());
    }
}
