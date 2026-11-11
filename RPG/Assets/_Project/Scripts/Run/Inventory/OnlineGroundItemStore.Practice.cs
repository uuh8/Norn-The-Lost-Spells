using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    public sealed partial class OnlineGroundItemStore
    {
        /// <summary>
        /// 只供已认证Home Entry的冻结地面闭包恢复。先在候选Store核完整父链、状态和占格，成功后一次替换；
        /// 失败不安装任何一个ID，且不生成新实例/抽roll/补容器量。普通Raid不能拿此入口重新授予旧地面资产。
        /// </summary>
        public bool TryRestorePractice(OnlineGroundInventorySnapshot source, out string error)
            => RestorePractice(source, null, out error);

        public bool TryRestorePracticeWithContinuity(OnlineGroundInventorySnapshot source,
            OnlineWandResearchStateSnapshot continuity, out string error) => RestorePractice(source, continuity, out error);

        private bool RestorePractice(OnlineGroundInventorySnapshot source, OnlineWandResearchStateSnapshot continuity, out string error)
        {
            error = null;
            if (_disposed || _records.Count != 0 || _store.Count != 0 || _lootKeys.Count != 0 || source == null
                || source.Version == 0 || source.Items == null || source.Items.Length > OnlineAccountInventorySnapshot.MaximumItems
                || source.Coins == null || source.Coins.Length != 0) return PracticeFail("Home地面恢复必须是空Owner及完整无奖励闭包。", out error);
            var entries = new List<InventoryEntry>();
            var records = new Dictionary<string, Record>(StringComparer.Ordinal);
            foreach (var root in source.Items)
            {
                if (root == null || root.Item == null || root.Children == null || root.Children.Length > 32
                    || !Id(root.WorldOwnerId) || !Finite(root.Position) || !ValidRotation(root.Rotation)
                    || root.InstanceId != root.Item.InstanceId || root.ContentId != root.Item.ContentId || root.Revision != root.Item.Revision
                    || root.Item.LocationKind != (int)ItemLocationKind.WorldItem || !string.IsNullOrEmpty(root.Item.ParentInstanceId)
                    || root.Item.Slot != -1 || root.Item.X != 0 || root.Item.Y != 0 || root.Item.QuarterTurns != 0
                    || root.Item.EntryBoundBackpack || records.ContainsKey(root.InstanceId)
                    || !SamePracticeState(root.ContainerState, root.Item.ContainerState)
                    || !SamePracticeState(root.MaterialState, root.Item.MaterialState))
                    return PracticeFail("Home地面根身份、姿态或状态别名不一致。", out error);
                if (!OnlineOwnedWorkspaceFactory.TryReadGroundItemState(_store.Catalog, root.Item, out var state, out error)) return false;
                var transfer = new OnlineOwnedWorkspace.GroundTransferState(); transfer.Revisions.Add(state.InstanceId, root.Revision);
                entries.Add(new InventoryEntry(state, new ItemLocation(ItemLocationKind.WorldItem, root.WorldOwnerId, 0, 0, 0, -1)));
                var record = new Record { WorldOwner = root.WorldOwnerId, Revision = root.Revision, Position = root.Position,
                    Rotation = root.Rotation, Transfer = transfer };
                records.Add(state.InstanceId, record);
                foreach (var child in root.Children)
                {
                    if (child == null || child.X != 0 || child.Y != 0 || child.QuarterTurns != 0 || child.EntryBoundBackpack
                        || child.LocationKind != (int)ItemLocationKind.WandSlot || child.ParentInstanceId != state.InstanceId
                        || child.Slot < 0 || child.Slot >= 32 || transfer.Revisions.ContainsKey(child.InstanceId)
                        || !OnlineOwnedWorkspaceFactory.TryReadGroundItemState(_store.Catalog, child, out var childState, out error))
                        return PracticeFail(error ?? "Home地面安装卡父链非法。", out error);
                    transfer.Revisions.Add(child.InstanceId, child.Revision);
                    entries.Add(new InventoryEntry(childState, new ItemLocation(ItemLocationKind.WandSlot, state.InstanceId, 0, 0, 0, child.Slot)));
                }
                if (entries.Count > OnlineAccountInventorySnapshot.MaximumItems)
                    return PracticeFail("Home地面超过原整份Profile容量。", out error);
            }
            if (!ItemOwnershipStore.TryRestore(_store.Catalog, entries, source.Version, out var candidate, out var reason))
                return PracticeFail("Home地面唯一归属恢复失败：" + reason, out error);
            foreach (var record in records.Values)
            {
                // 新登录没有旧scope正文，沿原新session初态；同scope有正文则必须匹配，不能静默补满。
                if (!OnlineWandResearchContinuityBridge.TryRestoreGround(continuity, candidate, record.Transfer, out error)) return false;
            }
            if (_practiceJournal != null && !candidate.ConfigureCheckpointJournal(_practiceJournal, WorldId, CheckpointRevision))
                return PracticeFail("Home地面无法接入同Run退役Journal。", out error);
            _store = candidate; _records.EnsureCapacity(records.Count);
            foreach (var pair in records) _records.Add(pair.Key, pair.Value);
            Version = source.Version; return true;
        }

        /// <summary>保存每个真实地面杖树的原运行状态；仅复制事实，不推进或重新执行程序。</summary>
        public bool TryCapturePracticeContinuity(OnlineWandResearchStateSnapshot target, out string error)
        {
            error = null;
            if (_disposed || target == null) return PracticeFail("Home连续性Owner缺失。", out error);
            var wands = new List<OnlineWandResearchEntrySnapshot>();
            var uses = new List<OnlineInventorySpellUsePresentationSnapshot>();
            foreach (var record in _records.Values)
            {
                if (!OnlineWandResearchContinuityBridge.TryCaptureGround(_store, record.Transfer, _store.Version,
                    out var captured, out var capturedUses, out error)) return false;
                wands.AddRange(captured); uses.AddRange(capturedUses);
            }
            target.GroundWands = wands.ToArray(); target.GroundSpellUses = uses.ToArray(); return true;
        }

        /// <summary>仅最终业务收据后移交：清当前Run引用，不登记Destroyed，也不删除数据库中的同ID资产。</summary>
        public void CompletePracticeHandoff()
        {
            if (_disposed) return;
            _records.Clear(); _store = new ItemOwnershipStore(_store.Catalog);
            if (_practiceJournal != null) _store.ConfigureCheckpointJournal(_practiceJournal, WorldId, CheckpointRevision);
            Version = Version == uint.MaxValue ? Version : Version + 1;
        }
        private static bool SamePracticeState(object a, object b) => a == null ? b == null : b != null
            && string.Equals(JsonUtility.ToJson(a), JsonUtility.ToJson(b), StringComparison.Ordinal);
        private static bool PracticeFail(string message, out string error) { error = message; return false; }
    }
}
