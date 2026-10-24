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
    public ContentManifestValidator(OnlineOptions options)
    {
        Manifest = JsonSerializer.Deserialize<ContentManifest>(File.ReadAllText(options.ManifestPath), Json) ?? throw new InvalidOperationException("Manifest 为空。");
        if (Manifest.Hash != ContentManifestRules.CalculateHash(Manifest)) throw new InvalidOperationException("Manifest 摘要不符。");
        _items = Manifest.Items.ToDictionary(i => i.ContentId, StringComparer.Ordinal);
        GameplayConfigHash = options.GameplayConfigHash;
        if (options.StarterContentIds.Length != 2 || options.StarterSpellCharges is < 1 or > 1000000)
            throw new InvalidOperationException("初始战斗资产必须明确配置一杖一卡和有限次数。");
        foreach (string id in options.StarterContentIds) RequireItem(id);
        if (RequireItem(options.StarterContentIds[0]).Kind != 1 || RequireItem(options.StarterContentIds[1]).Kind != 0)
            throw new InvalidOperationException("初始资产顺序必须为 Wand、Spell，不允许用未知物品兜底。");
    }
    public ContentManifestEntry RequireItem(string id) => _items.TryGetValue(id, out var entry) ? entry : throw new OnlineFailure(409, "UnknownContent");
    public void RequireVersion(uint protocol, string? hash)
    {
        if (protocol != Manifest.ProtocolVersion || !string.Equals(hash, Manifest.Hash, StringComparison.Ordinal)) throw new OnlineFailure(409, "ContentVersionMismatch");
    }
}
