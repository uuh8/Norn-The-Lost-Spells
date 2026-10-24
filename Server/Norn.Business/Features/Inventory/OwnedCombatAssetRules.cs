using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Inventory;

/// <summary>业务冻结门：校验真实装备闭包，而不是相信某个 DTO 有值就能进入 DS。</summary>
public static class OwnedCombatAssetRules
{
    public static void Require(OnlineOwnedAssetSnapshot? assets, string player, string gameplayHash, ContentManifestValidator content, bool frozen)
    {
        if (assets == null || assets.SchemaVersion != 1 || assets.PlayerId != player || assets.InventoryVersion == 0 ||
            string.IsNullOrEmpty(assets.InventoryId) || !IsHash(gameplayHash) || assets.GameplayConfigHash != gameplayHash ||
            assets.Items == null || assets.Items.Length is < 2 or > 99 ||
            frozen && (string.IsNullOrEmpty(assets.RunId) || assets.ActorId is < 1 or > 4))
            throw new OnlineFailure(409, "CombatAssetsUnavailable");
        var ids = new Dictionary<string, OnlineOwnedItemSnapshot>(StringComparer.Ordinal);
        var slots = new HashSet<(int Kind, string Parent, int Slot)>();
        foreach (var item in assets.Items)
        {
            if (item == null || !IsId(item.InstanceId) || string.IsNullOrEmpty(item.ContentId) || item.Revision == 0 || item.ParentInstanceId == null ||
                !ids.TryAdd(item.InstanceId, item) || !slots.Add((item.LocationKind, item.ParentInstanceId, item.Slot)) ||
                item.X != 0 || item.Y != 0 || item.QuarterTurns != 0 || item.RemainingCharges < 0)
                throw new OnlineFailure(409, "InvalidCombatAsset");
            var definition = content.RequireItem(item.ContentId);
            if (item.LocationKind == 3)
            {
                if (definition.Kind != 1 || item.ParentInstanceId.Length != 0 || item.Slot is < 0 or >= 3 ||
                    item.RemainingCharges != 0 || item.HasUnlimitedCharges || !item.UseTemplateWandStats || item.WandStats != null)
                    throw new OnlineFailure(409, "InvalidCombatWand");
            }
            else if (item.LocationKind == 2)
            {
                // Runtime WandStats 明确最多32卡槽；控制面先拒绝超界，不能等 DS 入场后才发现合同不一致。
                if (definition.Kind != 0 || !IsId(item.ParentInstanceId) || item.Slot is < 0 or >= 32 || item.UseTemplateWandStats || item.WandStats != null)
                    throw new OnlineFailure(409, "InvalidCombatCard");
            }
            else throw new OnlineFailure(409, "InvalidCombatLocation");
        }
        if (!ids.TryGetValue(assets.SelectedWandInstanceId ?? "", out var selected) || selected.LocationKind != 3)
            throw new OnlineFailure(409, "SelectedWandNotOwned");
        foreach (var item in assets.Items)
            if (item.LocationKind == 2 && (!ids.TryGetValue(item.ParentInstanceId, out var parent) || parent.LocationKind != 3))
                throw new OnlineFailure(409, "CombatParentNotOwned");
        // N4 的业务库尚无随机 Wand roll；只接受明确模板模式。将来加入实例 roll 时必须一起扩展持久 Schema/导出校验。
    }
    private static bool IsId(string value) => Guid.TryParseExact(value, "N", out var parsed) && parsed.ToString("N") == value;
    private static bool IsHash(string value) => value != null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
