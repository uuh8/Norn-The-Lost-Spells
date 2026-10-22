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
    public ContentManifestValidator(OnlineOptions options)
    {
        Manifest = JsonSerializer.Deserialize<ContentManifest>(File.ReadAllText(options.ManifestPath), Json) ?? throw new InvalidOperationException("Manifest 为空。");
        if (Manifest.Hash != ContentManifestRules.CalculateHash(Manifest)) throw new InvalidOperationException("Manifest 摘要不符。");
        _items = Manifest.Items.ToDictionary(i => i.ContentId, StringComparer.Ordinal);
        if (options.StarterContentIds.Length > OnlineInventoryAreas.BackpackCapacity || options.StarterContentIds.Distinct(StringComparer.Ordinal).Count() != options.StarterContentIds.Length)
            throw new InvalidOperationException("初始物品超出容量或重复。");
        foreach (string id in options.StarterContentIds) RequireItem(id);
    }
    public ContentManifestEntry RequireItem(string id) => _items.TryGetValue(id, out var entry) ? entry : throw new OnlineFailure(409, "UnknownContent");
    public void RequireVersion(uint protocol, string? hash)
    {
        if (protocol != Manifest.ProtocolVersion || !string.Equals(hash, Manifest.Hash, StringComparison.Ordinal)) throw new OnlineFailure(409, "ContentVersionMismatch");
    }
}
