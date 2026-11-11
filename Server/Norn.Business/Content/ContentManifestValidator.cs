using System.Text.Json;
using Game.NetworkProtocol;
using Norn.Business.Infrastructure;

namespace Norn.Business.Content;

/// <summary>只接受正式导出的物品身份清单；业务服不加载 SO，不信任 Client 上传的物品定义。</summary>
public sealed class ContentManifestValidator
{
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private readonly Dictionary<string, ContentManifestEntry> _items;
    public ContentManifest Manifest { get; }
    public string GameplayConfigHash { get; }
    private readonly string _entryBackpackContentId;
    private readonly string _entryContainerContentId;
    public ContentManifestValidator(OnlineOptions options)
    {
        Manifest = JsonSerializer.Deserialize<ContentManifest>(File.ReadAllText(options.ManifestPath), Json) ?? throw new InvalidOperationException("Manifest 为空。");
        if (Manifest.Hash != ContentManifestRules.CalculateHash(Manifest)) throw new InvalidOperationException("Manifest 摘要不符。");
        _items = Manifest.Items.ToDictionary(i => i.ContentId, StringComparer.Ordinal);
        GameplayConfigHash = options.GameplayConfigHash;
        _entryBackpackContentId = options.EntryBackpackContentId;
        _entryContainerContentId = options.EntryContainerContentId;
        if (options.StarterContentIds.Length != 2 || options.StarterSpellCharges is < 1 or > 1000000)
            throw new InvalidOperationException("初始战斗资产必须明确配置一杖一卡和有限次数。");
        foreach (string id in options.StarterContentIds) RequireItem(id);
        if (RequireItem(options.StarterContentIds[0]).Kind != 1 || RequireItem(options.StarterContentIds[1]).Kind != 0)
            throw new InvalidOperationException("初始资产顺序必须为 Wand、Spell，不允许用未知物品兜底。");
    }
    public ContentManifestEntry RequireItem(string id) => _items.TryGetValue(id, out var entry) ? entry : throw new OnlineFailure(409, "UnknownContent");
    /// <summary>只在实际入场保底使用可信导出定义；不能用未知物品、模板容量或大包替代获批8×8包。</summary>
    public ContentManifestEntry RequireEntryBackpack()
    {
        var bag = RequireItem(_entryBackpackContentId);
        if (bag.Kind != 2 || bag.BackpackWidth != 8 || bag.BackpackHeight != 8 || bag.MaxStack != 1)
            throw new OnlineFailure(409, "InvalidEntryBackpackConfiguration");
        return bag;
    }
    /// <summary>发放的是清单中已有的可用容器；容量只用于合法性验证，不代表获赠任何材料。</summary>
    public ContentManifestEntry RequireEntryContainer()
    {
        var container = RequireItem(_entryContainerContentId);
        if (container.Kind != 3 || container.UseKind != 1 || container.MaxStack != 1 ||
            container.ContainerCapacityGmu == 0 || container.ContainerMaterialsMask == 0)
            throw new OnlineFailure(409, "InvalidEntryContainerConfiguration");
        return container;
    }
    public void RequireVersion(uint protocol, string? hash)
    {
        if (protocol != Manifest.ProtocolVersion || !string.Equals(hash, Manifest.Hash, StringComparison.Ordinal)) throw new OnlineFailure(409, "ContentVersionMismatch");
    }
}
