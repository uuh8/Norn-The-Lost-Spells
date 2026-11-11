using System.Text.Json;
using Game.NetworkProtocol;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>HTTP 冻结快照不能静默丢弃资源和父子关系；旧 JSON 不得获得可执行的 N4 默认值。</summary>
public sealed class OwnedCombatContractTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    [Fact, Trait("Category", "OwnedContainerContract")]
    public void MissingComponentArray_RemainsUnknownRatherThanBecomingAnExplicitEmptyBottle()
    {
        var snapshot = JsonSerializer.Deserialize<OnlineOwnedAssetSnapshot>("{\"SchemaVersion\":3,\"Gold\":0,\"CurrencyVersion\":1,\"Items\":[{\"ContainerState\":{\"SchemaVersion\":1}}]}", Json)!;
        Assert.Null(snapshot.Items[0].ContainerState.Components);
    }

    [Fact, Trait("Category", "OwnedContainerContract")]
    public void OwnedContainerRoundTrip_PreservesRealGridAndUnsignedCompositionWithoutImplicitEmptyState()
    {
        // 这是传输无损测试；超出具体瓶容量的ulong仍必须原样到达后续校验门，不能先被double舍入。
        const string incoming = """
            {"SchemaVersion":3,"Gold":0,"CurrencyVersion":1,"Items":[{"InstanceId":"bag","ContentId":"bagtest.small","LocationKind":4},{"InstanceId":"bottle","ContentId":"containertest.bottle","ParentInstanceId":"bag","Revision":7,"LocationKind":1,"Slot":-1,"X":2,"Y":3,"QuarterTurns":1,"ContainerState":{"SchemaVersion":1,"Components":[{"MaterialKey":1,"Quantity":9007199254740993},{"MaterialKey":3,"Quantity":2}]}}]}
            """;
        var snapshot = JsonSerializer.Deserialize<OnlineOwnedAssetSnapshot>(incoming, Json)!;
        using var restored = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, Json));
        var bottle = restored.RootElement.GetProperty("Items")[1];
        Assert.Equal("bag", bottle.GetProperty("ParentInstanceId").GetString());
        Assert.Equal(2, bottle.GetProperty("X").GetInt32()); Assert.Equal(3, bottle.GetProperty("Y").GetInt32());
        Assert.Equal(1, bottle.GetProperty("QuarterTurns").GetInt32());
        Assert.True(bottle.TryGetProperty("ContainerState", out var state), "Owned合同丢失了真实容器组分。");
        Assert.Equal(1u, state.GetProperty("SchemaVersion").GetUInt32());
        var parts = state.GetProperty("Components");
        Assert.Equal(9007199254740993ul, parts[0].GetProperty("Quantity").GetUInt64());
        Assert.Equal(2ul, parts[1].GetProperty("Quantity").GetUInt64());
    }

    [Fact, Trait("Category", "OwnedContainerContract")]
    public void OwnedContainerRoundTrip_DistinguishesExplicitEmptyBottleFromMissingContainerState()
    {
        const string incoming = """
            {"SchemaVersion":3,"Gold":0,"CurrencyVersion":1,"Items":[{"InstanceId":"empty","ContainerState":{"SchemaVersion":1,"Components":[]}},{"InstanceId":"missing"}]}
            """;
        var snapshot = JsonSerializer.Deserialize<OnlineOwnedAssetSnapshot>(incoming, Json)!;
        using var restored = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, Json));
        var items = restored.RootElement.GetProperty("Items");
        Assert.True(items[0].TryGetProperty("ContainerState", out var empty), "不能把合法显式空瓶丢成未知组分。");
        Assert.Equal(1u, empty.GetProperty("SchemaVersion").GetUInt32());
        Assert.Equal(0, empty.GetProperty("Components").GetArrayLength());
        Assert.True(items[1].TryGetProperty("ContainerState", out var missing));
        Assert.Equal(JsonValueKind.Null, missing.ValueKind);
    }

    [Fact, Trait("Category", "OwnedCombatContract")]
    public void ProfileRoundTrip_PreservesExplicitOwnedResourcesAndInstalledParent()
    {
        const string incoming = """
            {"PlayerId":"player","CombatAssets":{"SchemaVersion":3,"RunId":"run","PlayerId":"player","InventoryId":"inventory","GameplayConfigHash":"rules","SelectedWandInstanceId":"wand","ActorId":2,"InventoryVersion":7,"Gold":0,"CurrencyVersion":1,"Items":[{"InstanceId":"wand","ContentId":"wandtest.wand.a","Revision":1,"LocationKind":3,"Slot":0,"UseTemplateWandStats":true},{"InstanceId":"card","ContentId":"wandtest.fire","ParentInstanceId":"wand","Revision":2,"LocationKind":2,"Slot":0,"RemainingCharges":30,"HasUnlimitedCharges":false}]}}
            """;
        var profile = JsonSerializer.Deserialize<OnlineProfileReply>(incoming, Json)!;
        using var restored = JsonDocument.Parse(JsonSerializer.Serialize(profile, Json));
        Assert.True(restored.RootElement.TryGetProperty("CombatAssets", out var assets), "HTTP Profile dropped CombatAssets.");
        Assert.Equal(OnlineOwnedAssetSnapshot.CurrentSchemaVersion, assets.GetProperty("SchemaVersion").GetUInt32());
        Assert.Equal(0L, assets.GetProperty("Gold").GetInt64());
        Assert.Equal(1u, assets.GetProperty("CurrencyVersion").GetUInt32());
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
