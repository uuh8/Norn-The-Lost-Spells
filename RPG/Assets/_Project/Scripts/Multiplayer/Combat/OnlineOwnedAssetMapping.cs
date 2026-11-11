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
        /// <summary>
        /// 全Owner Profile包含仓库物，不能只用携带CombatAssets查roll。沿原账号事件保留真实属性，
        /// 并复用控制面null壳归一化；缺旧字段不会被猜为模板，Core视图另冻结可变DTO/trait。
        /// </summary>
        public static Local.OnlineItemView ToLobbyItem(Shared.OnlineItemReply item)
        {
            if (item == null) return default;
            return new Local.OnlineItemView(item.InstanceId, item.DisplayName, item.Area, item.Slot,
                item.UseTemplateWandStats, CopyStats(item.WandStats));
        }

        /// <summary>控制面完整事实需要保留真正null，不能经JsonUtility二次转换为默认属性壳。</summary>
        public static Shared.OnlineOwnedAssetSnapshot ToShared(Local.OnlineOwnedAssetSnapshot source)
        {
            if (source == null) return null;
            var copy = new Shared.OnlineOwnedAssetSnapshot { SchemaVersion = source.SchemaVersion,
                RunId = source.RunId, PlayerId = source.PlayerId, ActorId = source.ActorId, InventoryId = source.InventoryId,
                InventoryVersion = source.InventoryVersion, Gold = source.Gold, CurrencyVersion = source.CurrencyVersion, GameplayConfigHash = source.GameplayConfigHash,
                SelectedWandInstanceId = source.SelectedWandInstanceId, Items = source.Items == null ? null
                    : new Shared.OnlineOwnedItemSnapshot[source.Items.Length] };
            if (source.Items != null) for (int i = 0; i < source.Items.Length; i++) copy.Items[i] = ToSharedItem(source.Items[i]);
            return copy;
        }
        public static Shared.OnlineOwnedItemSnapshot ToSharedItem(Local.OnlineOwnedItemSnapshot source)
        {
            if (source == null) return null;
            var copy = new Shared.OnlineOwnedItemSnapshot { InstanceId = source.InstanceId, ContentId = source.ContentId,
                Revision = source.Revision, LocationKind = source.LocationKind, ParentInstanceId = source.ParentInstanceId,
                Slot = source.Slot, X = source.X, Y = source.Y, QuarterTurns = source.QuarterTurns,
                RemainingCharges = source.RemainingCharges, HasUnlimitedCharges = source.HasUnlimitedCharges,
                UseTemplateWandStats = source.UseTemplateWandStats, EntryBoundBackpack = source.EntryBoundBackpack };
            if (source.WandStats != null)
            {
                var stats = source.WandStats;
                copy.WandStats = new Shared.OnlineOwnedWandStatsSnapshot { MaxMana = stats.MaxMana,
                    ManaRegenPerSecond = stats.ManaRegenPerSecond, BaseCastDelay = stats.BaseCastDelay,
                    BaseReloadTime = stats.BaseReloadTime, BaseSpreadDegrees = stats.BaseSpreadDegrees,
                    ProgramCapacity = stats.ProgramCapacity, BaseDraws = stats.BaseDraws, Shuffle = stats.Shuffle,
                    AlwaysCastContentIds = stats.AlwaysCastContentIds == null ? null : (string[])stats.AlwaysCastContentIds.Clone() };
            }
            if (source.ContainerState != null) copy.ContainerState = new Shared.OnlineOwnedContainerStateSnapshot
                { SchemaVersion = source.ContainerState.SchemaVersion, Components = ToSharedAmounts(source.ContainerState.Components) };
            if (source.MaterialState != null) copy.MaterialState = new Shared.OnlineOwnedMaterialStateSnapshot
                { SchemaVersion = source.MaterialState.SchemaVersion, MaterialKey = source.MaterialState.MaterialKey, Quantity = source.MaterialState.Quantity };
            if (source.AttachmentState != null) copy.AttachmentState = new Shared.OnlineOwnedSpellAttachmentStateSnapshot
                { SchemaVersion = source.AttachmentState.SchemaVersion, ContainerId = source.AttachmentState.ContainerId,
                    Dose = source.AttachmentState.Dose, Enabled = source.AttachmentState.Enabled };
            return copy;
        }
        public static Shared.OnlineOwnedMaterialAmountSnapshot[] ToSharedAmounts(Local.OnlineOwnedMaterialAmountSnapshot[] source)
        {
            if (source == null) return null;
            var result = new Shared.OnlineOwnedMaterialAmountSnapshot[source.Length];
            for (int i = 0; i < source.Length; i++) if (source[i] != null)
                result[i] = new Shared.OnlineOwnedMaterialAmountSnapshot { MaterialKey = source[i].MaterialKey, Quantity = source[i].Quantity };
            return result;
        }

        public static Local.OnlineOwnedAssetSnapshot ToCore(Shared.OnlineOwnedAssetSnapshot source)
        {
            if (source == null) return null;
            var result = new Local.OnlineOwnedAssetSnapshot
            {
                SchemaVersion = source.SchemaVersion, ActorId = source.ActorId, RunId = source.RunId, PlayerId = source.PlayerId,
                InventoryId = source.InventoryId, InventoryVersion = source.InventoryVersion, Gold = source.Gold, CurrencyVersion = source.CurrencyVersion,
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
                    UseTemplateWandStats = item.UseTemplateWandStats, EntryBoundBackpack = item.EntryBoundBackpack,
                    WandStats = CopyStats(item.WandStats),
                    ContainerState = CopyContainer(item.ContainerState), MaterialState = CopyMaterial(item.MaterialState), AttachmentState = CopyAttachment(item.AttachmentState)
                };
            }
            return result;
        }
        private static Local.OnlineOwnedSpellAttachmentStateSnapshot CopyAttachment(Shared.OnlineOwnedSpellAttachmentStateSnapshot state)
        {
            // JsonUtility会把inline null展开全默认壳；仅这一个形态归一化。坏非默认状态保留给Factory拒绝。
            // 来源ID失效不是格式无效：导入保持设置，实际施法仍根据此刻的装备包瓶裁决。
            if (state == null || state.SchemaVersion == 0 && string.IsNullOrEmpty(state.ContainerId) && state.Dose == 0 && !state.Enabled) return null;
            return new Local.OnlineOwnedSpellAttachmentStateSnapshot
                { SchemaVersion = state.SchemaVersion, ContainerId = state.ContainerId, Dose = state.Dose, Enabled = state.Enabled };
        }
        private static Local.OnlineOwnedMaterialStateSnapshot CopyMaterial(Shared.OnlineOwnedMaterialStateSnapshot state)
        {
            // 仅Unity inline null产生的全默认壳可归一化；任何显式/非法非默认值仍交领域层拒绝。
            // 同时断开可变DTO引用，避免后续Profile或冻结对象编辑反向改变已接管的DS数量。
            if (state == null || state.SchemaVersion == 0 && state.MaterialKey == 0 && state.Quantity == 0) return null;
            return new Local.OnlineOwnedMaterialStateSnapshot
                { SchemaVersion = state.SchemaVersion, MaterialKey = state.MaterialKey, Quantity = state.Quantity };
        }
        private static Local.OnlineOwnedContainerStateSnapshot CopyContainer(Shared.OnlineOwnedContainerStateSnapshot state)
        {
            // JsonUtility 的 inline null 空壳只有全默认这一种可归一化。Schema=1 的空数组
            // 是有意义的空瓶；其它异常状态原样传给 Factory，不能借归一化吞掉坏组分。
            if (state == null || state.SchemaVersion == 0 && (state.Components == null || state.Components.Length == 0)) return null;
            var result = new Local.OnlineOwnedContainerStateSnapshot
            {
                SchemaVersion = state.SchemaVersion,
                Components = state.Components == null ? null : new Local.OnlineOwnedMaterialAmountSnapshot[state.Components.Length]
            };
            if (state.Components != null)
                for (int i = 0; i < state.Components.Length; i++)
                {
                    var part = state.Components[i];
                    if (part != null) result.Components[i] = new Local.OnlineOwnedMaterialAmountSnapshot
                        { MaterialKey = part.MaterialKey, Quantity = part.Quantity };
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
