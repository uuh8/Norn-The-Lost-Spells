using Shared = Game.NetworkProtocol;
using Local = Game.Core;

namespace Game.Multiplayer
{
    /// <summary>
    /// 控制面 DTO 到 Run 中性合同的显式边界。JsonUtility 把 inline 普通类的 null 展开为空壳，
    /// 不能再经第二次 JSON 转换判断“是否携带属性”。只归一化不可能是合法 WandStats 的全默认空壳；
    /// 非默认属性必须保留，交给 Factory 拒绝非法位置或配置，不能直接按物品种类丢弃。
    /// </summary>
    public static class OnlineOwnedAssetMapping
    {
        public static Local.OnlineOwnedAssetSnapshot ToCore(Shared.OnlineOwnedAssetSnapshot source)
        {
            if (source == null) return null;
            var result = new Local.OnlineOwnedAssetSnapshot
            {
                SchemaVersion = source.SchemaVersion, ActorId = source.ActorId, RunId = source.RunId, PlayerId = source.PlayerId,
                InventoryId = source.InventoryId, InventoryVersion = source.InventoryVersion,
                GameplayConfigHash = source.GameplayConfigHash, SelectedWandInstanceId = source.SelectedWandInstanceId,
                Items = source.Items == null ? null : new Local.OnlineOwnedItemSnapshot[source.Items.Length]
            };
            if (source.Items == null) return result;
            for (int i = 0; i < source.Items.Length; i++)
            {
                var item = source.Items[i]; if (item == null) continue;
                result.Items[i] = new Local.OnlineOwnedItemSnapshot
                {
                    InstanceId = item.InstanceId, ContentId = item.ContentId, ParentInstanceId = item.ParentInstanceId,
                    Revision = item.Revision, LocationKind = item.LocationKind, Slot = item.Slot, X = item.X, Y = item.Y,
                    QuarterTurns = item.QuarterTurns, RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                    UseTemplateWandStats = item.UseTemplateWandStats, WandStats = CopyStats(item.WandStats)
                };
            }
            return result;
        }
        private static Local.OnlineOwnedWandStatsSnapshot CopyStats(Shared.OnlineOwnedWandStatsSnapshot stats)
        {
            if (stats == null || stats.MaxMana == 0 && stats.ManaRegenPerSecond == 0 && stats.BaseCastDelay == 0
                && stats.BaseReloadTime == 0 && stats.BaseSpreadDegrees == 0 && stats.ProgramCapacity == 0 && stats.BaseDraws == 0
                && !stats.Shuffle && (stats.AlwaysCastContentIds == null || stats.AlwaysCastContentIds.Length == 0)) return null;
            // 字符串不可变，可共享引用；数组与可变 DTO 必须独立，避免上层继续修改冻结快照。
            return new Local.OnlineOwnedWandStatsSnapshot
            {
                MaxMana = stats.MaxMana, ManaRegenPerSecond = stats.ManaRegenPerSecond, BaseCastDelay = stats.BaseCastDelay,
                BaseReloadTime = stats.BaseReloadTime, BaseSpreadDegrees = stats.BaseSpreadDegrees, ProgramCapacity = stats.ProgramCapacity,
                BaseDraws = stats.BaseDraws, Shuffle = stats.Shuffle,
                AlwaysCastContentIds = stats.AlwaysCastContentIds == null ? null : (string[])stats.AlwaysCastContentIds.Clone()
            };
        }
    }
}
