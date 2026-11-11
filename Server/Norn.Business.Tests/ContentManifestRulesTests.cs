using System.Text.Json;
using Game.NetworkProtocol;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>中立目录是业务服校验真实布局和瓶内容量的信任边界，不能只签 ContentId。</summary>
public sealed class ContentManifestRulesTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private const string Entry = """
        {"ContentId":"container.test","Kind":3,"DisplayName":"测试瓶","RulesSchemaVersion":1,"UseKind":1,"ShapeWidth":2,"ShapeHeight":2,"MaxStack":1,"OccupiedCells":[{"X":0,"Y":0},{"X":1,"Y":0},{"X":0,"Y":1}],"BackpackWidth":8,"BackpackHeight":8,"WandProgramCapacity":1,"ContainerCapacityGmu":1024,"ContainerMaterialsMask":410,"MaterialKey":5,"MaterialItemCapacityGmu":1024}
        """;
    private static ContentManifest Manifest(string entry = Entry) => JsonSerializer.Deserialize<ContentManifest>(
        "{\"SchemaVersion\":2,\"ProtocolVersion\":" + ProtocolCompatibility.CurrentVersion + ",\"BuildVersion\":\"test\",\"Items\":[" + entry + "]}", Json)!;

    [Fact, Trait("Category", "ContainerManifest")]
    public void RulesRoundTrip_PreservesSparseShapeAndUnsignedCapacity()
    {
        var manifest = Manifest(Entry.Replace("1024", "9007199254740993"));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(manifest, Json));
        var item = doc.RootElement.GetProperty("Items")[0];
        Assert.True(item.TryGetProperty("OccupiedCells", out var cells), "清单丢失了真实占格；矩形尺寸不能代替空洞形状。");
        Assert.Equal(3, cells.GetArrayLength());
        Assert.Equal(9007199254740993ul, item.GetProperty("ContainerCapacityGmu").GetUInt64());
    }

    [Fact, Trait("Category", "ContainerManifest")]
    public void Hash_ProtectsGameplayRulesAndIgnoresAuthoringCellOrder()
    {
        string hash = ContentManifestRules.CalculateHash(Manifest());
        Assert.NotEqual(hash, ContentManifestRules.CalculateHash(Manifest(Entry.Replace("1024", "1025"))));
        Assert.NotEqual(hash, ContentManifestRules.CalculateHash(Manifest(Entry.Replace("\"X\":0,\"Y\":1", "\"X\":1,\"Y\":1"))));
        Assert.Equal(hash, ContentManifestRules.CalculateHash(Manifest(Entry.Replace(
            "{\"X\":0,\"Y\":0},{\"X\":1,\"Y\":0},{\"X\":0,\"Y\":1}",
            "{\"X\":0,\"Y\":1},{\"X\":0,\"Y\":0},{\"X\":1,\"Y\":0}"))));
    }

    [Theory, Trait("Category", "ContainerManifest")]
    [InlineData("\"RulesSchemaVersion\":1", "\"RulesSchemaVersion\":0")]
    [InlineData("\"X\":0,\"Y\":1", "\"X\":0,\"Y\":0")]
    [InlineData("\"ShapeWidth\":2", "\"ShapeWidth\":65")]
    [InlineData("\"ContainerMaterialsMask\":410", "\"ContainerMaterialsMask\":65535")]
    [InlineData("\"ContainerCapacityGmu\":1024", "\"ContainerCapacityGmu\":0")]
    [InlineData("\"WandProgramCapacity\":1", "\"WandProgramCapacity\":33")]
    public void InvalidOrUnboundedRules_AreRejected(string from, string to) =>
        Assert.Throws<ArgumentException>(() => ContentManifestRules.CalculateHash(Manifest(Entry.Replace(from, to))));

    [Fact, Trait("Category", "ContainerManifest")]
    public void LegacyIdentityOnlyManifest_CannotAcquireLayoutAuthority()
    {
        var manifest = Manifest(); manifest.SchemaVersion = 1;
        Assert.Throws<ArgumentException>(() => ContentManifestRules.CalculateHash(manifest));
    }
}
