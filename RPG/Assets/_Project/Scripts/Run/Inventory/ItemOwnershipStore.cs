using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Run
{
    /// <summary>只读归属视图；组分引用只有读取权限，独立数量快照请使用 CopyTo。</summary>
    public readonly struct InventoryEntry
    {
        public readonly RuntimeItemState Item;
        public readonly ItemLocation Location;
        public InventoryEntry(RuntimeItemState item, in ItemLocation location) { Item = item; Location = location; }
    }

    /// <summary>
    /// 唯一写入口。离散事务构造完整候选表、验证，再一次发布；
    /// 读者不会看到“来源已删、目标未加”，临时表分配不进入每帧热路径。
    /// </summary>
    public sealed class ItemOwnershipStore
    {
        private readonly ItemCatalogIndex _catalog;
        private Dictionary<string, InventoryEntry> _items = new Dictionary<string, InventoryEntry>(StringComparer.Ordinal);
        internal IInventoryCommitPolicy CommitPolicy;
        private PreparedInventory _prepared;
        private bool _materialLocked;
        private IOnlineItemRetirementJournal _checkpointJournal;
        private string _checkpointInventoryId;
        private Func<string, uint> _checkpointRevision;
        internal bool IsResourceBusy => _prepared != null || _materialLocked;
        /// <summary>
        /// 同Run装配的可选退役Journal；Revision由原Owner/地面元数据提供，不从模板或缺失ID推测。
        /// 单机未绑定时保持原事务行为。绑定在Run装配时完成；活跃Store不能换Journal逃过历史容量。
        /// </summary>
        internal bool ConfigureCheckpointJournal(IOnlineItemRetirementJournal journal, string inventoryId, Func<string, uint> revision)
        {
            if (journal == null || string.IsNullOrWhiteSpace(inventoryId) || revision == null || IsResourceBusy) return false;
            if (_checkpointJournal != null)
                return ReferenceEquals(_checkpointJournal, journal) && _checkpointInventoryId == inventoryId && _checkpointRevision == revision;
            _checkpointJournal = journal; _checkpointInventoryId = inventoryId; _checkpointRevision = revision;
            return true;
        }
        internal uint CheckpointRevision(string id) => _checkpointRevision != null ? _checkpointRevision(id) : 0;
        public int Count => _items.Count;
        public uint Version { get; private set; }
        public ItemCatalogIndex Catalog => _catalog;
        internal bool HasWorldTransit
        { get { foreach (var entry in _items.Values) if (entry.Location.Kind == ItemLocationKind.ThrownContainer) return true; return false; } }

        public ItemOwnershipStore(ItemCatalogIndex catalog) { _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); }

        internal static bool TryRestore(ItemCatalogIndex catalog, IReadOnlyList<InventoryEntry> entries, uint version,
            out ItemOwnershipStore store, out InventoryFailureReason reason)
        {
            store = null; reason = InventoryFailureReason.InvalidState;
            if (catalog == null || entries == null) return false;
            var restored = new ItemOwnershipStore(catalog);
            // 先建所有身份，再验证父链；DTO 数组顺序不应决定资产能否恢复。
            for (int i = 0; i < entries.Count; i++)
            {
                InventoryEntry entry = entries[i];
                entry = new InventoryEntry(restored.NormalizeContainer(entry.Item), entry.Location);
                reason = restored.ValidateItem(entry.Item);
                if (reason != InventoryFailureReason.None) return false;
                if (restored._items.ContainsKey(entry.Item.InstanceId)) { reason = InventoryFailureReason.DuplicateIdentity; return false; }
                restored._items.Add(entry.Item.InstanceId, entry);
            }
            reason = restored.ValidateAll(restored._items);
            if (reason != InventoryFailureReason.None) return false;
            restored.Version = version; store = restored; return true;
        }

        // 具体结构体 Enumerator 避免 IEnumerable 装箱；提交新表后旧枚举仍是一致的旧视图。
        public Enumerator GetEnumerator() => new Enumerator(_items.GetEnumerator());

        public struct Enumerator
        {
            private Dictionary<string, InventoryEntry>.Enumerator _inner;
            internal Enumerator(Dictionary<string, InventoryEntry>.Enumerator inner) { _inner = inner; }
            public InventoryEntry Current => _inner.Current.Value;
            public bool MoveNext() => _inner.MoveNext();
            public void Dispose() => _inner.Dispose();
        }

        public bool TryAdd(RuntimeItemState item, in ItemLocation location)
        {
            if (IsResourceBusy) return false;
            item = NormalizeContainer(item);
            if (ValidateItem(item) != InventoryFailureReason.None || _items.ContainsKey(item.InstanceId)) return false;
            var candidate = new Dictionary<string, InventoryEntry>(_items, StringComparer.Ordinal);
            candidate.Add(item.InstanceId, new InventoryEntry(item, location));
            if (ValidateAll(candidate) != InventoryFailureReason.None) return false;
            return FinishCandidate(candidate, true).Success;
        }

        public bool TryGet(string instanceId, out RuntimeItemState item)
        {
            item = null;
            if (string.IsNullOrWhiteSpace(instanceId) || !_items.TryGetValue(instanceId, out InventoryEntry entry)) return false;
            item = entry.Item; return true;
        }

        public bool TryGetLocation(string instanceId, out ItemLocation location)
        {
            location = default;
            if (string.IsNullOrWhiteSpace(instanceId) || !_items.TryGetValue(instanceId, out InventoryEntry entry)) return false;
            location = entry.Location; return true;
        }

        public InventoryOperationResult TryMove(string instanceId, in ItemLocation destination) => TryMove(instanceId, destination, Version);

        public InventoryOperationResult TryMove(string instanceId, in ItemLocation destination, uint expectedVersion)
        {
            if (expectedVersion != Version) return Result(false, InventoryFailureReason.StaleVersion);
            if (!TryGet(instanceId, out RuntimeItemState item)) return Result(false, InventoryFailureReason.NotFound);
            if(item.Material!=null&&destination.Kind==ItemLocationKind.Backpack)
                foreach(var target in _items.Values)if(target.Item.InstanceId!=instanceId&&target.Location==destination&&MaterialStackOps.Compatible(item,target.Item))
                    return MaterialStackOps.TryMerge(this,instanceId,target.Item.InstanceId,expectedVersion);
            return TryApply(new[] { new InventoryEntry(item, destination) }, expectedVersion);
        }

        internal InventoryOperationResult TryApply(IReadOnlyList<InventoryEntry> changes, uint expectedVersion)
        {
            if (IsResourceBusy) return Result(false, InventoryFailureReason.ResourceBusy);
            if (expectedVersion != Version) return Result(false, InventoryFailureReason.StaleVersion);
            if (changes == null || changes.Count == 0) return Result(false, InventoryFailureReason.InvalidState);
            var candidate = new Dictionary<string, InventoryEntry>(_items, StringComparer.Ordinal);
            var changedIds = new HashSet<string>(StringComparer.Ordinal);
            bool changed = false;
            for (int i = 0; i < changes.Count; i++)
            {
                InventoryEntry next = changes[i];
                InventoryFailureReason reason = ValidateItem(next.Item);
                if (reason != InventoryFailureReason.None) return Result(false, reason);
                if (!candidate.TryGetValue(next.Item.InstanceId, out InventoryEntry previous)) return Result(false, InventoryFailureReason.NotFound);
                if (previous.Item.IsStarterEquipment != next.Item.IsStarterEquipment) return Result(false, InventoryFailureReason.InvalidState);
                if (previous.Item.EntryBoundBackpack != next.Item.EntryBoundBackpack) return Result(false, InventoryFailureReason.InvalidState);
                if(!ItemStackOps.MatchingWandStats(previous.Item,next.Item))return Result(false,InventoryFailureReason.InvalidState);
                // 在途归属只允许投掷服务写入，普通拖拽不能从飞行体取回同一满瓶。
                if (previous.Location.Kind == ItemLocationKind.ThrownContainer || next.Location.Kind == ItemLocationKind.ThrownContainer)
                    return Result(false, InventoryFailureReason.InvalidOwnership);
                if (!changedIds.Add(next.Item.InstanceId)) return Result(false, InventoryFailureReason.DuplicateIdentity);
                if (!string.Equals(next.Item.ContentId, previous.Item.ContentId, StringComparison.Ordinal))
                    return Result(false, InventoryFailureReason.InvalidIdentity);
                // 通用编辑入口可以搬位置/编辑法术状态，不能伪造、抹掉材料账本。
                // 旧空瓶相容键测试仍可替换壳状态，沿用原空组分而不是重新生成数量。
                if (previous.Item.Composition != null)
                {
                    if (next.Item.Composition == null && previous.Item.Composition.Total == 0)
                        next = new InventoryEntry(new RuntimeItemState(next.Item.InstanceId, next.Item.ContentId, next.Item.RemainingCharges,
                            next.Item.HasUnlimitedCharges, next.Item.StackStateKey, previous.Item.Composition, next.Item.Material,next.Item.Attachment,next.Item.RolledWandStats, next.Item.EntryBoundBackpack), next.Location);
                    if (!ReferenceEquals(next.Item.Composition, previous.Item.Composition)) return Result(false, InventoryFailureReason.InvalidState);
                }
                if (previous.Item.Material == null ? next.Item.Material != null : next.Item.Material == null
                    || previous.Item.Material.MaterialKey != next.Item.Material.MaterialKey || previous.Item.Material.Quantity != next.Item.Material.Quantity)
                    return Result(false, InventoryFailureReason.InvalidState);
                changed |= previous.Location != next.Location || !ItemStackOps.AreCompatible(previous.Item, next.Item);
                candidate[next.Item.InstanceId] = next;
            }
            InventoryFailureReason validation = ValidateAll(candidate);
            if (validation != InventoryFailureReason.None) return Result(false, validation);
            return FinishCandidate(candidate, changed);
        }

        // 策略先准备，完整空间/父链验证通过后才同时提交资格；失败没有半次扣费。
        private InventoryOperationResult FinishCandidate(Dictionary<string, InventoryEntry> candidate, bool changed,
            IOnlineItemRetirementReservation retirement = null)
        {
            object preparation = null;
            if (CommitPolicy != null)
            {
                InventoryFailureReason reason = CommitPolicy.Prepare(candidate, out preparation, out bool policyChanged);
                if (reason != InventoryFailureReason.None) return Result(false, reason);
                changed |= policyChanged;
                reason = ValidateAll(candidate);
                if (reason != InventoryFailureReason.None) return Result(false, reason);
            }
            if (changed) Commit(candidate);
            // 和Store发布在同一无回调区间提交身份事实。其容量早已预留，不把后续通知异常当作未消耗。
            retirement?.Commit();
            CommitPolicy?.Commit(preparation);
            return Result(true, InventoryFailureReason.None);
        }
        internal InventoryOperationResult ReconcilePolicy()
        {
            if (IsResourceBusy) return Result(false, InventoryFailureReason.ResourceBusy);
            return FinishCandidate(new Dictionary<string, InventoryEntry>(_items, StringComparer.Ordinal), false);
        }
        internal bool TryPrepareUses(Dictionary<string, int> uses, out PreparedInventory prepared)
        {
            prepared = null; if (IsResourceBusy) return false;
            var candidate = new Dictionary<string, InventoryEntry>(_items, StringComparer.Ordinal); bool changed = false;
            List<RuntimeItemState> retired = null;
            foreach (var pair in uses)
            {
                if (!candidate.TryGetValue(pair.Key, out InventoryEntry entry) || pair.Value <= 0) return false;
                RuntimeItemState item = entry.Item; if (item.HasUnlimitedCharges) continue;
                if (item.RemainingCharges < pair.Value) return false;
                // 候选表只在成功支付后提交；最后一次 Emit 的 Trigger 已保存在 Ticket 中，
                // 移除卡片释放格子不会取消预付的后续层，失败则整张卡保持原样。
                if (item.RemainingCharges == pair.Value)
                {
                    candidate.Remove(pair.Key);
                    if (_checkpointJournal != null) { retired ??= new List<RuntimeItemState>(); retired.Add(item); }
                }
                else candidate[pair.Key] = new InventoryEntry(new RuntimeItemState(item.InstanceId, item.ContentId,
                    item.RemainingCharges - pair.Value, false, item.StackStateKey, item.Composition, item.Material, item.Attachment,item.RolledWandStats, item.EntryBoundBackpack), entry.Location); changed = true;
            }
            if (ValidateAll(candidate) != InventoryFailureReason.None) return false;
            if (!TryReserveRetirements(retired, OnlineItemRetirementReason.Consumed, out var retirement)) return false;
            prepared = _prepared = new PreparedInventory(candidate, changed, retirement); return true;
        }
        internal bool CompletePrepared(PreparedInventory prepared, bool commit, bool materialChanged = false)
        {
            if (prepared == null || !ReferenceEquals(prepared, _prepared)) return false;
            if (commit && (prepared.Changed || materialChanged)) Commit(prepared.Candidate);
            if (commit) prepared.Retirement?.Commit(); else prepared.Retirement?.Cancel();
            _prepared = null; return true;
        }
        internal bool OwnsPreparation(PreparedInventory prepared) => prepared != null && ReferenceEquals(prepared, _prepared);
        internal sealed class PreparedInventory
        {
            internal readonly Dictionary<string, InventoryEntry> Candidate;
            internal readonly bool Changed;
            internal readonly IOnlineItemRetirementReservation Retirement;
            internal PreparedInventory(Dictionary<string, InventoryEntry> candidate, bool changed, IOnlineItemRetirementReservation retirement = null)
            { Candidate = candidate; Changed = changed; Retirement = retirement; }
        }

        // 连续数量变化不改身份/归属，不重复复制 Dictionary 或重新发放入局资格。
        internal bool TryLockMaterial(uint expectedVersion)
        { if (IsResourceBusy || expectedVersion != Version) return false; _materialLocked = true; return true; }
        internal void FinishMaterial(bool commit)
        { if (!_materialLocked) throw new InvalidOperationException("材料预约没有持有库存锁。"); if (commit) Version++; _materialLocked = false; }
        internal bool TryPrepareMaterialItem(RuntimeItemState item, in ItemLocation destination, bool add, out PreparedInventory prepared)
        {
            prepared = null;
            if (IsResourceBusy || ValidateItem(item) != InventoryFailureReason.None) return false;
            var candidate = new Dictionary<string, InventoryEntry>(_items, StringComparer.Ordinal);
            if (add) { if (candidate.ContainsKey(item.InstanceId)) return false; candidate.Add(item.InstanceId, new InventoryEntry(item, destination)); }
            else { if (!candidate.ContainsKey(item.InstanceId)) return false; candidate[item.InstanceId] = new InventoryEntry(item, destination); }
            if (ValidateAll(candidate) != InventoryFailureReason.None) return false;
            prepared = _prepared = new PreparedInventory(candidate, true); return true;
        }
        /// <summary>
        /// 同一DS主线程上的容器归属交接原语。调用层先确认认证、租约/生命、距离与世界落地；
        /// 此处只负责两份真实Store的版本、准备锁、目录/形状和唯一实例，不能作为客户端写权限。
        /// 仅承接随身容器↔共享地面，不迁移法术/资格/仓库；因此不重建Raid资格或执行次数策略。
        /// </summary>
        public InventoryOperationResult TryTransferContainerTo(ItemOwnershipStore destination, string itemId,
            ItemLocation expectedSource, ItemLocation target, uint expectedSourceVersion, uint expectedDestinationVersion)
        {
            if (destination == null || ReferenceEquals(destination, this)) return Result(false, InventoryFailureReason.InvalidState);
            if (Version != expectedSourceVersion || destination.Version != expectedDestinationVersion)
                return Result(false, InventoryFailureReason.StaleVersion);
            if (IsResourceBusy || destination.IsResourceBusy) return Result(false, InventoryFailureReason.ResourceBusy);
            if (!TryGet(itemId, out var item) || !TryGetLocation(itemId, out var source))
                return Result(false, InventoryFailureReason.NotFound);
            if (source != expectedSource) return Result(false, InventoryFailureReason.InvalidOwnership);
            bool drop = (source.Kind == ItemLocationKind.Backpack || source.Kind == ItemLocationKind.EquippedTool)
                && target.Kind == ItemLocationKind.WorldItem;
            bool pickup = source.Kind == ItemLocationKind.WorldItem && target.Kind == ItemLocationKind.Backpack;
            if (!drop && !pickup) return Result(false, InventoryFailureReason.InvalidOwnership);
            if (!_catalog.TryGetRules(item.ContentId, out var sourceRules) || sourceRules.Kind != ItemKind.Container
                || item.Composition == null) return Result(false, InventoryFailureReason.WrongKind);
            // 目标目录也验证真实容量/mask，不能把同一个ContentId在不同目录中的相异规则当成兼容。
            var validItem = destination.ValidateItem(item);
            if (validItem != InventoryFailureReason.None) return Result(false, validItem);
            if (destination._items.ContainsKey(itemId)) return Result(false, InventoryFailureReason.DuplicateIdentity);
            var targetCandidate = new Dictionary<string, InventoryEntry>(destination._items, StringComparer.Ordinal);
            targetCandidate.Add(itemId, new InventoryEntry(item, target));
            var validTarget = destination.ValidateAll(targetCandidate);
            if (validTarget != InventoryFailureReason.None) return Result(false, validTarget);

            PreparedInventory sourcePrepared = null, targetPrepared = null;
            try
            {
                // 复用数量事务已有的准备/取消机制。双方准备完成前不提交、不发布事件、不调用外部代码。
                if (!TryPrepareMaterialChange(itemId, null, default, out sourcePrepared))
                    return Result(false, InventoryFailureReason.ResourceBusy);
                if (!destination.TryPrepareMaterialItem(item, target, true, out targetPrepared))
                    return Result(false, InventoryFailureReason.ResourceBusy);
                if (!OwnsPreparation(sourcePrepared) || !destination.OwnsPreparation(targetPrepared))
                    return Result(false, InventoryFailureReason.ResourceBusy);
                // Commit仅替换候选Dictionary并增加本Store版本，不回调策略/事件；同一主线程中两次提交
                // 之间不存在用户代码或可失败验证点。成功后上层再统一发布Owner/地面完整读快照。
                CompletePrepared(sourcePrepared, true);
                destination.CompletePrepared(targetPrepared, true);
                return Result(true, InventoryFailureReason.None);
            }
            finally
            {
                if (OwnsPreparation(sourcePrepared)) CompletePrepared(sourcePrepared, false);
                if (destination.OwnsPreparation(targetPrepared)) destination.CompletePrepared(targetPrepared, false);
            }
        }

        /// <summary>
        /// 通用物品树的双Store原子交接。根改变世界/携带归属，安装卡继续指向原杖ID；不通过逐项
        /// TryRemove/TryAdd制造悬空父链。资格策略先在完整候选上Prepare，双方全部通过后才提交。
        /// 调用层负责认证、距离、Actor根和免费绑定包限制；此原语不接受网络字段作为权限。
        /// </summary>
        internal InventoryOperationResult TryTransferItemTreeTo(ItemOwnershipStore destination, string itemId,
            ItemLocation expectedSource, ItemLocation target, uint expectedSourceVersion, uint expectedDestinationVersion)
            => TryTransferItemTreesTo(destination, new[] { new ItemTransferRoot(itemId, expectedSource, target) },
                expectedSourceVersion, expectedDestinationVersion);

        internal readonly struct ItemTransferRoot
        {
            internal readonly string ItemId;
            internal readonly ItemLocation Source, Target;
            internal ItemTransferRoot(string id, ItemLocation source, ItemLocation target)
            { ItemId = id; Source = source; Target = target; }
        }

        internal InventoryOperationResult TryTransferItemTreesTo(ItemOwnershipStore destination, IReadOnlyList<ItemTransferRoot> roots,
            uint expectedSourceVersion, uint expectedDestinationVersion)
        {
            if (destination == null || ReferenceEquals(destination, this) || roots == null || roots.Count == 0)
                return Result(false, InventoryFailureReason.InvalidState);
            if (Version != expectedSourceVersion || destination.Version != expectedDestinationVersion)
                return Result(false, InventoryFailureReason.StaleVersion);
            if (IsResourceBusy || destination.IsResourceBusy) return Result(false, InventoryFailureReason.ResourceBusy);
            var sourceCandidate = CopyCandidate(); var targetCandidate = destination.CopyCandidate();
            var moved = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (!_items.TryGetValue(root.ItemId, out var original)) return Result(false, InventoryFailureReason.NotFound);
                if (original.Location != root.Source || original.Location.Kind == ItemLocationKind.ThrownContainer)
                    return Result(false, InventoryFailureReason.InvalidOwnership);
                bool drop = root.Source.Kind != ItemLocationKind.WorldItem && root.Target.Kind == ItemLocationKind.WorldItem;
                bool pickup = root.Source.Kind == ItemLocationKind.WorldItem && root.Target.Kind != ItemLocationKind.WorldItem
                    && root.Target.Kind != ItemLocationKind.Warehouse && root.Target.Kind != ItemLocationKind.ThrownContainer;
                if (!drop && !pickup) return Result(false, InventoryFailureReason.InvalidOwnership);
                foreach (var entry in _items.Values)
                {
                    bool member = entry.Item.InstanceId == root.ItemId;
                    for (var parent = entry.Location; !member && parent.HasParentInstance;)
                    {
                        if (parent.OwnerId == root.ItemId) { member = true; break; }
                        if (!_items.TryGetValue(parent.OwnerId, out var ancestor)) break;
                        parent = ancestor.Location;
                    }
                    if (!member) continue;
                    if (entry.Item.EntryBoundBackpack) return Result(false, InventoryFailureReason.InvalidOwnership);
                    if (!moved.Add(entry.Item.InstanceId) || targetCandidate.ContainsKey(entry.Item.InstanceId))
                        return Result(false, InventoryFailureReason.DuplicateIdentity);
                    // 当前共享地面只承接原杖与直接安装卡；背包内容不借此开放嵌套包/免费包交易。
                    if (entry.Item.InstanceId != root.ItemId && (entry.Location.Kind != ItemLocationKind.WandSlot || entry.Location.OwnerId != root.ItemId))
                        return Result(false, InventoryFailureReason.InvalidOwnership);
                    var valid = destination.ValidateItem(entry.Item);
                    if (valid != InventoryFailureReason.None) return Result(false, valid);
                    sourceCandidate.Remove(entry.Item.InstanceId);
                    targetCandidate.Add(entry.Item.InstanceId, entry.Item.InstanceId == root.ItemId
                        ? new InventoryEntry(entry.Item, root.Target) : entry);
                }
            }
            var reason = ValidateAll(sourceCandidate);
            if (reason != InventoryFailureReason.None) return Result(false, reason);
            reason = destination.ValidateAll(targetCandidate);
            if (reason != InventoryFailureReason.None) return Result(false, reason);
            object sourcePolicy = null, targetPolicy = null;
            var sourcePrepared = _prepared = new PreparedInventory(sourceCandidate, true);
            var targetPrepared = destination._prepared = new PreparedInventory(targetCandidate, true);
            try
            {
                // Prepare期间双方持有原数量事务锁，策略或诊断不能重入另一写口改变刚验证的版本。
                if (CommitPolicy != null)
                {
                    reason = CommitPolicy.Prepare(sourceCandidate, out sourcePolicy, out _);
                    if (reason != InventoryFailureReason.None) return Result(false, reason);
                }
                if (destination.CommitPolicy != null)
                {
                    reason = destination.CommitPolicy.Prepare(targetCandidate, out targetPolicy, out _);
                    if (reason != InventoryFailureReason.None) return Result(false, reason);
                }
                reason = ValidateAll(sourceCandidate);
                if (reason != InventoryFailureReason.None) return Result(false, reason);
                reason = destination.ValidateAll(targetCandidate);
                if (reason != InventoryFailureReason.None) return Result(false, reason);
                // 单一DS主线程、无外部事件/可失败验证点：两份候选和资格均已准备，提交不重新创建物品。
                CompletePrepared(sourcePrepared, true); destination.CompletePrepared(targetPrepared, true);
                CommitPolicy?.Commit(sourcePolicy); destination.CommitPolicy?.Commit(targetPolicy);
                return Result(true, InventoryFailureReason.None);
            }
            finally
            {
                if (OwnsPreparation(sourcePrepared)) CompletePrepared(sourcePrepared, false);
                if (destination.OwnsPreparation(targetPrepared)) destination.CompletePrepared(targetPrepared, false);
            }
        }

        /// <summary>
        /// 只为共享地面固体拾取增加双 Store 原子事务，不开放固体 Drop 或普通物品跨 Store。
        /// 同格 Compatible 时保持原目标ID并最多合并其剩余容量；源有余量仍由原世界实例持有。
        /// 双方先验证/准备，之后只切换内部候选表；任何失败都不会先删源或重复增加目标数量。
        /// </summary>
        internal InventoryOperationResult TryPickupMaterialTo(ItemOwnershipStore destination, string itemId,
            ItemLocation expectedSource, ItemLocation target, uint expectedSourceVersion, uint expectedDestinationVersion,
            out ulong transferred)
        {
            transferred = 0;
            if (destination == null || ReferenceEquals(destination, this)) return Result(false, InventoryFailureReason.InvalidState);
            if (Version != expectedSourceVersion || destination.Version != expectedDestinationVersion) return Result(false, InventoryFailureReason.StaleVersion);
            if (IsResourceBusy || destination.IsResourceBusy) return Result(false, InventoryFailureReason.ResourceBusy);
            if (!TryGet(itemId, out var item) || !TryGetLocation(itemId, out var source)) return Result(false, InventoryFailureReason.NotFound);
            if (source != expectedSource || source.Kind != ItemLocationKind.WorldItem || target.Kind != ItemLocationKind.Backpack)
                return Result(false, InventoryFailureReason.InvalidOwnership);
            if (item.Material == null || !_catalog.TryGetRules(item.ContentId, out var rules) || rules.Kind != ItemKind.Material)
                return Result(false, InventoryFailureReason.WrongKind);
            var validation = destination.ValidateItem(item);
            if (validation != InventoryFailureReason.None) return Result(false, validation);
            if (destination._items.ContainsKey(itemId)) return Result(false, InventoryFailureReason.DuplicateIdentity);
            InventoryEntry merge = default; bool merging = false;
            foreach (var entry in destination._items.Values)
                if (entry.Location == target && MaterialStackOps.Compatible(item, entry.Item)) { merge = entry; merging = true; break; }
            ulong amount = item.Material.Quantity;
            RuntimeItemState destinationItem = item;
            var candidate = new Dictionary<string, InventoryEntry>(destination._items, StringComparer.Ordinal);
            if (merging)
            {
                destination._catalog.TryGetRules(merge.Item.ContentId, out var targetRules);
                if (merge.Item.Material.Quantity >= targetRules.MaterialItemCapacity) return Result(false, InventoryFailureReason.Capacity);
                amount = Math.Min(amount, targetRules.MaterialItemCapacity - merge.Item.Material.Quantity);
                destinationItem = MaterialStackOps.WithQuantity(merge.Item, checked(merge.Item.Material.Quantity + amount));
                candidate[merge.Item.InstanceId] = new InventoryEntry(destinationItem, target);
            }
            else candidate.Add(itemId, new InventoryEntry(item, target));
            validation = destination.ValidateAll(candidate);
            if (validation != InventoryFailureReason.None) return Result(false, validation);
            PreparedInventory sourcePrepared = null, destinationPrepared = null;
            try
            {
                RuntimeItemState remainder = amount == item.Material.Quantity ? null : MaterialStackOps.WithQuantity(item, item.Material.Quantity - amount);
                if (!TryPrepareMaterialChange(itemId, remainder, source, out sourcePrepared,
                    merging && remainder == null ? OnlineItemRetirementReason.Merged : OnlineItemRetirementReason.None))
                    return Result(false, InventoryFailureReason.ResourceBusy);
                bool prepared = merging
                    ? destination.TryPrepareMaterialChange(merge.Item.InstanceId, destinationItem, target, out destinationPrepared)
                    : destination.TryPrepareMaterialItem(item, target, true, out destinationPrepared);
                if (!prepared || !OwnsPreparation(sourcePrepared) || !destination.OwnsPreparation(destinationPrepared))
                    return Result(false, InventoryFailureReason.ResourceBusy);
                CompletePrepared(sourcePrepared, true); destination.CompletePrepared(destinationPrepared, true);
                transferred = amount; return Result(true, InventoryFailureReason.None);
            }
            finally
            {
                if (OwnsPreparation(sourcePrepared)) CompletePrepared(sourcePrepared, false);
                if (destination.OwnsPreparation(destinationPrepared)) destination.CompletePrepared(destinationPrepared, false);
            }
        }

        private RuntimeItemState NormalizeContainer(RuntimeItemState item)
        {
            if (item == null || item.Composition != null || !_catalog.TryGetRules(item.ContentId, out var rules) || rules.Kind != ItemKind.Container) return item;
            return new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges, item.HasUnlimitedCharges,
                item.StackStateKey, new ContainerComposition(rules.ContainerCapacity, rules.ContainerMaterials), item.Material,item.Attachment,item.RolledWandStats, item.EntryBoundBackpack);
        }

        internal bool TryPrepareMaterialChange(string id, RuntimeItemState replacement, ItemLocation location, out PreparedInventory prepared,
            OnlineItemRetirementReason retirementReason = OnlineItemRetirementReason.None)
        {
            prepared = null;
            if (IsResourceBusy || !_items.TryGetValue(id, out var previous)
                || !_catalog.TryGetRules(previous.Item.ContentId, out var rules)
                || (rules.Kind != ItemKind.Container && rules.Kind != ItemKind.Material
                    && !(previous.Location.Kind==ItemLocationKind.ThrownContainer && rules.UseKind==ItemUseKind.Throw))) return false;
            var candidate = new Dictionary<string, InventoryEntry>(_items, StringComparer.Ordinal);
            if (replacement == null) candidate.Remove(id);
            else
            {
                if (replacement.InstanceId != id || replacement.ContentId != previous.Item.ContentId) return false;
                candidate[id] = new InventoryEntry(replacement, location);
            }
            if (ValidateAll(candidate) != InventoryFailureReason.None) return false;
            IOnlineItemRetirementReservation retirement = null;
            // None还用于双Store原ID迁移。只有调用方明确的消耗/归并/销毁才预留终止证据。
            if (replacement == null && retirementReason != OnlineItemRetirementReason.None
                && !TryReserveRetirements(new[] { previous.Item }, retirementReason, out retirement)) return false;
            prepared = _prepared = new PreparedInventory(candidate, true, retirement); return true;
        }

        internal InventoryOperationResult Result(bool success, InventoryFailureReason reason) => new InventoryOperationResult(success, reason, Version);
        internal Dictionary<string,InventoryEntry> CopyCandidate()=>new Dictionary<string,InventoryEntry>(_items,StringComparer.Ordinal);
        internal InventoryOperationResult CommitMaterialLayout(Dictionary<string,InventoryEntry> candidate,uint version,
            IReadOnlyList<string> mergedIds = null)
        {
            if(IsResourceBusy)return Result(false,InventoryFailureReason.ResourceBusy);
            if(version!=Version)return Result(false,InventoryFailureReason.StaleVersion);
            // 整理/合并是离散候选事务，材料总量逐键比较；通用移动入口仍不允许改数量。
            var before=new ulong[256];var after=new ulong[256];
            foreach(var entry in _items.Values)if(entry.Item.Material!=null)before[entry.Item.Material.MaterialKey]+=entry.Item.Material.Quantity;
            foreach(var entry in candidate.Values)if(entry.Item.Material!=null)after[entry.Item.Material.MaterialKey]+=entry.Item.Material.Quantity;
            for(int i=0;i<256;i++)if(before[i]!=after[i])return Result(false,InventoryFailureReason.InvalidState);
            var reason=ValidateAll(candidate); if(reason!=InventoryFailureReason.None)return Result(false,reason);
            List<RuntimeItemState> retired = null;
            if (_checkpointJournal != null)
            {
                var declared = new HashSet<string>(StringComparer.Ordinal);
                if (mergedIds != null) for (int i = 0; i < mergedIds.Count; i++)
                {
                    string id = mergedIds[i];
                    if (id == null || !declared.Add(id) || candidate.ContainsKey(id) || !_items.TryGetValue(id, out var original)
                        || original.Item.Material == null) return Result(false, InventoryFailureReason.InvalidState);
                    retired ??= new List<RuntimeItemState>(); retired.Add(original.Item);
                }
                // 只核对显式Merge决策完整，不从差集推断原因。未知删除宁可拒绝整次整理，也不丢身份证据。
                foreach (var original in _items.Values)
                    if (!candidate.ContainsKey(original.Item.InstanceId) && !declared.Contains(original.Item.InstanceId))
                        return Result(false, InventoryFailureReason.InvalidState);
            }
            if (!TryReserveRetirements(retired, OnlineItemRetirementReason.Merged, out var retirement))
                return Result(false, InventoryFailureReason.ResourceBusy);
            try { return FinishCandidate(candidate, true, retirement); }
            finally { retirement?.Cancel(); }
        }
        private bool TryReserveRetirements(IReadOnlyList<RuntimeItemState> items, OnlineItemRetirementReason reason,
            out IOnlineItemRetirementReservation reservation)
        {
            reservation = null;
            if (_checkpointJournal == null || items == null || items.Count == 0) return true;
            var facts = new OnlineItemRetirementSnapshot[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i]; uint revision = CheckpointRevision(item.InstanceId);
                if (revision == 0) return false;
                facts[i] = new OnlineItemRetirementSnapshot { InventoryId = _checkpointInventoryId, InstanceId = item.InstanceId,
                    ContentId = item.ContentId, Revision = revision, Reason = reason };
            }
            return _checkpointJournal.TryReserve(facts, out reservation);
        }
        private void Commit(Dictionary<string, InventoryEntry> candidate) { _items = candidate; Version++; }

        private InventoryFailureReason ValidateItem(RuntimeItemState item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.InstanceId) || string.IsNullOrWhiteSpace(item.ContentId))
                return InventoryFailureReason.InvalidIdentity;
            if (item.RemainingCharges < 0 || item.StackStateKey == null) return InventoryFailureReason.InvalidState;
            if (!_catalog.TryGetRules(item.ContentId, out InventoryItemRules rules)) return InventoryFailureReason.UnknownContent;
            if(item.RolledWandStats.HasValue&&(rules.Kind!=ItemKind.Wand||!item.RolledWandStats.Value.IsValid||!_catalog.ValidateWandTraits(item.RolledWandStats.Value)))return InventoryFailureReason.InvalidState;
            if (item.EntryBoundBackpack && rules.Kind != ItemKind.Backpack) return InventoryFailureReason.InvalidState;
            if (rules.Kind != ItemKind.Spell && (item.RemainingCharges != 0 || item.HasUnlimitedCharges)) return InventoryFailureReason.InvalidState;
            if (item.Attachment != null && rules.Kind != ItemKind.Spell) return InventoryFailureReason.InvalidState;
            if (item.Composition != null && (rules.Kind != ItemKind.Container || item.Composition.Capacity != rules.ContainerCapacity
                || item.Composition.AllowedMaterials != rules.ContainerMaterials)) return InventoryFailureReason.InvalidState;
            if (item.Material != null && (rules.Kind != ItemKind.Material || item.Material.MaterialKey != rules.MaterialKey
                || item.Material.Quantity > rules.MaterialItemCapacity)) return InventoryFailureReason.InvalidState;
            return InventoryFailureReason.None;
        }

        private InventoryFailureReason ValidateAll(Dictionary<string, InventoryEntry> candidate)
        {
            foreach (InventoryEntry entry in candidate.Values)
            {
                // 一个可更新组分只能属于一个实例，防止两个瓶子共享同一数组复制资产。
                if (entry.Item.Composition != null || entry.Item.Material!=null)
                    foreach (InventoryEntry other in candidate.Values)
                        if (entry.Item.InstanceId != other.Item.InstanceId && (entry.Item.Composition!=null && ReferenceEquals(entry.Item.Composition, other.Item.Composition)
                            || entry.Item.Material!=null && ReferenceEquals(entry.Item.Material,other.Item.Material)))
                            return InventoryFailureReason.InvalidState;
                InventoryFailureReason reason = ValidateLocation(entry, candidate);
                if (reason != InventoryFailureReason.None) return reason;
            }
            InventoryFailureReason grid = InventoryGrid.Validate(_catalog, candidate);
            if (grid != InventoryFailureReason.None) return grid;
            // 装备数量由三个 EquippedWand 槽约束；备用杖是普通占格资产，不能把它们也算进装备上限。
            return InventoryFailureReason.None;
        }

        private InventoryFailureReason ValidateLocation(in InventoryEntry entry, Dictionary<string, InventoryEntry> candidate)
        {
            ItemLocation location = entry.Location;
            _catalog.TryGetRules(entry.Item.ContentId, out InventoryItemRules rules);
            if (location.Kind > ItemLocationKind.EquippedTool) return InventoryFailureReason.InvalidState;
            if (string.IsNullOrWhiteSpace(location.OwnerId)) return InventoryFailureReason.InvalidOwnership;
            if (location.X < 0 || location.Y < 0 || location.QuarterTurns < 0 || location.QuarterTurns > 3) return InventoryFailureReason.OutOfBounds;
            if (location.HasParentInstance)
            {
                if (!candidate.TryGetValue(location.OwnerId, out InventoryEntry parent) || !IsAcyclicParent(entry.Item.InstanceId, location.OwnerId, candidate))
                    return InventoryFailureReason.InvalidOwnership;
                _catalog.TryGetRules(parent.Item.ContentId, out InventoryItemRules parentRules);
                ItemKind required = location.Kind == ItemLocationKind.Backpack ? ItemKind.Backpack : ItemKind.Wand;
                if (parentRules.Kind != required) return InventoryFailureReason.WrongKind;
            }
            switch (location.Kind)
            {
                case ItemLocationKind.EquippedTool:
                    if (rules.UseKind == ItemUseKind.None) return InventoryFailureReason.WrongKind;
                    if (location.SlotIndex < 0 || location.SlotIndex >= HeldItemSlots.ToolCount) return InventoryFailureReason.OutOfBounds;
                    if (!HasZeroGridMetadata(location)) return InventoryFailureReason.InvalidState;
                    break;
                case ItemLocationKind.SpellStorage:
                    if (rules.Kind != ItemKind.Spell) return InventoryFailureReason.WrongKind;
                    if (location.SlotIndex < 0 || location.SlotIndex >= _catalog.SpellStorageCapacity) return InventoryFailureReason.OutOfBounds;
                    if (!HasZeroGridMetadata(location)) return InventoryFailureReason.InvalidState;
                    break;
                case ItemLocationKind.Backpack:
                    if (location.SlotIndex != -1) return InventoryFailureReason.InvalidState;
                    if (rules.Kind == ItemKind.Backpack) return InventoryFailureReason.WrongKind;
                    break;
                case ItemLocationKind.WandSlot:
                    if (rules.Kind != ItemKind.Spell) return InventoryFailureReason.WrongKind;
                    if (location.SlotIndex < 0) return InventoryFailureReason.OutOfBounds;
                    _catalog.TryGetRules(candidate[location.OwnerId].Item, out InventoryItemRules wandRules);
                    if (location.SlotIndex >= wandRules.WandProgramCapacity) return InventoryFailureReason.OutOfBounds;
                    if (!HasZeroGridMetadata(location)) return InventoryFailureReason.InvalidState;
                    break;
                case ItemLocationKind.EquippedWand:
                    if (rules.Kind != ItemKind.Wand) return InventoryFailureReason.WrongKind;
                    if (location.SlotIndex < 0 || location.SlotIndex >= 3) return InventoryFailureReason.OutOfBounds;
                    if (!HasZeroGridMetadata(location)) return InventoryFailureReason.InvalidState;
                    break;
                case ItemLocationKind.EquippedBackpack:
                    if (rules.Kind != ItemKind.Backpack) return InventoryFailureReason.WrongKind;
                    if (location.SlotIndex != 0) return InventoryFailureReason.OutOfBounds;
                    if (!HasZeroGridMetadata(location)) return InventoryFailureReason.InvalidState;
                    break;
                case ItemLocationKind.Warehouse:
                    if (!string.Equals(location.OwnerId, "warehouse", StringComparison.Ordinal)) return InventoryFailureReason.InvalidOwnership;
                    if (location.SlotIndex != -1 || !HasZeroGridMetadata(location)) return InventoryFailureReason.InvalidState;
                    break;
                case ItemLocationKind.ThrownContainer:
                    // 保留旧序列化location编号；它现在也承载普通物体在途，不复制第二份库存。
                    if (rules.Kind != ItemKind.Container && rules.UseKind != ItemUseKind.Throw) return InventoryFailureReason.WrongKind;
                    if (location.SlotIndex != -1 || !HasZeroGridMetadata(location)) return InventoryFailureReason.InvalidState;
                    break;
                case ItemLocationKind.WorldItem:
                    if (location.SlotIndex != -1 || !HasZeroGridMetadata(location)) return InventoryFailureReason.InvalidState;
                    break;
            }
            if (IsExclusive(location.Kind))
                foreach (InventoryEntry other in candidate.Values)
                    if (!string.Equals(other.Item.InstanceId, entry.Item.InstanceId, StringComparison.Ordinal)
                        && other.Location.Kind == location.Kind && other.Location.SlotIndex == location.SlotIndex
                        && string.Equals(other.Location.OwnerId, location.OwnerId, StringComparison.Ordinal)) return InventoryFailureReason.Occupied;
            return InventoryFailureReason.None;
        }

        private static bool IsAcyclicParent(string id, string parentId, Dictionary<string, InventoryEntry> candidate)
        {
            for (int visited = 0; visited <= candidate.Count; visited++)
            {
                if (string.Equals(id, parentId, StringComparison.Ordinal) || !candidate.TryGetValue(parentId, out InventoryEntry parent)) return false;
                if (!parent.Location.HasParentInstance) return true;
                parentId = parent.Location.OwnerId;
            }
            return false;
        }

        private static bool HasZeroGridMetadata(in ItemLocation location) => location.X == 0 && location.Y == 0 && location.QuarterTurns == 0;
        private static bool IsExclusive(ItemLocationKind kind) => kind == ItemLocationKind.SpellStorage || kind == ItemLocationKind.WandSlot || kind == ItemLocationKind.EquippedWand
            || kind == ItemLocationKind.EquippedBackpack || kind == ItemLocationKind.EquippedTool || kind == ItemLocationKind.WorldItem || kind == ItemLocationKind.ThrownContainer;
    }
}
