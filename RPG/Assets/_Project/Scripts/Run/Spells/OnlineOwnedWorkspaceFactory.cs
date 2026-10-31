using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Run
{
    /// <summary>只把服务器冻结的值导入内存；不读取本地存档，不发放 StarterKit，也不重建业务物品身份。</summary>
    public static class OnlineOwnedWorkspaceFactory
    {
        public static bool TryCreate(ItemCatalogIndex catalog, OnlineOwnedAssetSnapshot snapshot,
            string expectedGameplayConfigHash, out OnlineOwnedWorkspace workspace, out string error)
        {
            workspace = null; error = null;
            if (catalog == null || snapshot == null || snapshot.SchemaVersion != OnlineOwnedAssetSnapshot.CurrentSchemaVersion)
                return Fail("缺少当前版本的 Owned 入局数据。", out error);
            if (!ValidHash(expectedGameplayConfigHash) || !string.Equals(snapshot.GameplayConfigHash, expectedGameplayConfigHash, StringComparison.Ordinal))
                return Fail("入局 Gameplay 配置 Hash 不一致。", out error);
            if (string.IsNullOrWhiteSpace(snapshot.RunId) || string.IsNullOrWhiteSpace(snapshot.PlayerId)
                || string.IsNullOrWhiteSpace(snapshot.InventoryId) || snapshot.ActorId < 1 || snapshot.ActorId > 4
                || snapshot.InventoryVersion == 0 || string.IsNullOrWhiteSpace(snapshot.SelectedWandInstanceId)
                || snapshot.Items == null || snapshot.Items.Length < 1 || snapshot.Items.Length > 99)
                return Fail("Owned 入局身份、选择或容量非法。", out error);

            var entries = new InventoryEntry[snapshot.Items.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                var item = snapshot.Items[i];
                if (item == null || item.Revision == 0 || !catalog.TryGet(item.ContentId, out var definition))
                    return Fail("入局物品缺少实例修订或已知 ContentId。", out error);
                if (item.ParentInstanceId == null)
                    return Fail("根位置使用空父 ID，不能缺少位置字段。", out error);

                // N4 执行投影只承认真实装备及其安装闭包；其它业务资产仍留在业务快照，不在这里编造容器。
                bool equipped = item.LocationKind == (int)ItemLocationKind.EquippedWand;
                bool installed = item.LocationKind == (int)ItemLocationKind.WandSlot;
                if ((!equipped && !installed) || equipped && (definition.Kind != ItemKind.Wand || item.ParentInstanceId.Length != 0)
                    || installed && (definition.Kind != ItemKind.Spell || string.IsNullOrWhiteSpace(item.ParentInstanceId)))
                    return Fail("入局执行投影必须为已装备法杖及其安装卡闭包。", out error);

                WandStats? instanceStats = null;
                if (equipped)
                {
                    if (item.UseTemplateWandStats)
                    {
                        if (item.WandStats != null || !catalog.TryGetWandStats(item.ContentId, out _))
                            return Fail("模板法杖标记与实例属性冲突。", out error);
                    }
                    else
                    {
                        var source = item.WandStats;
                        if (source == null || source.AlwaysCastContentIds == null)
                            return Fail("法杖缺少明确的模板标记或完整实例属性。", out error);
                        var stats = new WandStats(source.MaxMana, source.ManaRegenPerSecond, source.BaseCastDelay,
                            source.ProgramCapacity, source.BaseDraws, source.BaseReloadTime, source.BaseSpreadDegrees,
                            source.Shuffle, source.AlwaysCastContentIds);
                        if (!stats.IsValid || !catalog.ValidateWandTraits(stats))
                            return Fail("法杖实例固定属性非法。", out error);
                        instanceStats = stats;
                    }
                }
                else if (item.UseTemplateWandStats || item.WandStats != null)
                    return Fail("法术卡不能携带法杖固定属性。", out error);

                var state = new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges,
                    item.HasUnlimitedCharges, rolledWandStats: instanceStats);
                var location = new ItemLocation(equipped ? ItemLocationKind.EquippedWand : ItemLocationKind.WandSlot,
                    equipped ? snapshot.PlayerId : item.ParentInstanceId, item.X, item.Y, item.QuarterTurns, item.Slot);
                entries[i] = new InventoryEntry(state, location);
            }
            // TryRestore 先建立所有身份再校验父链，业务 DTO 的排列顺序不决定导入是否成功。
            if (!ItemOwnershipStore.TryRestore(catalog, entries, snapshot.InventoryVersion, out var store, out var reason))
                return Fail("Owned 入局所有权校验失败：" + reason, out error);
            if (!store.TryGetLocation(snapshot.SelectedWandInstanceId, out var selected)
                || selected.Kind != ItemLocationKind.EquippedWand || selected.OwnerId != snapshot.PlayerId)
                return Fail("选择的法杖不属于当前角色装备槽。", out error);

            var profile = new SinglePlayerProfile(snapshot.PlayerId, store);
            var eligibility = new RaidSpellEligibility(profile);
            if (!eligibility.TryBegin(snapshot.RunId, out reason))
                return Fail("入局资格提交失败：" + reason, out error);
            var wands = new WandLoadoutService(profile, snapshot.InventoryId);
            if (!wands.TrySelect(selected.SlotIndex))
                return Fail("无法选择入局法杖。", out error);
            var casts = new WandExecutionTransactionService(profile, wands, eligibility, snapshot.InventoryId,
                new WandExecutionReplayArchive());
            // RuntimeItemState 不保存数据库 Revision。为完整导出保留已校验的冻结修订，不能拿 Store
            // 全局 Version 冒充某一物品的数据库版本；当前余额与位置仍从存活工作区实时读取。
            var revisions = new Dictionary<string, uint>(snapshot.Items.Length, StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Items.Length; i++) revisions.Add(snapshot.Items[i].InstanceId, snapshot.Items[i].Revision);
            workspace = new OnlineOwnedWorkspace(snapshot.RunId, snapshot.PlayerId, snapshot.ActorId,
                snapshot.InventoryId, profile, wands, casts, eligibility, snapshot.GameplayConfigHash, revisions);
            return true;
        }

        private static bool Fail(string message, out string error) { error = message; return false; }
        private static bool ValidHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            for (int i = 0; i < hash.Length; i++)
                if (!(hash[i] >= '0' && hash[i] <= '9') && !(hash[i] >= 'a' && hash[i] <= 'f')) return false;
            return true;
        }
    }
}
