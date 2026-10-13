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
        internal bool IsResourceBusy => _prepared != null || _materialLocked;
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
                            next.Item.HasUnlimitedCharges, next.Item.StackStateKey, previous.Item.Composition, next.Item.Material), next.Location);
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
        private InventoryOperationResult FinishCandidate(Dictionary<string, InventoryEntry> candidate, bool changed)
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
            foreach (var pair in uses)
            {
                if (!candidate.TryGetValue(pair.Key, out InventoryEntry entry) || pair.Value <= 0) return false;
                RuntimeItemState item = entry.Item; if (item.HasUnlimitedCharges) continue;
                if (item.RemainingCharges < pair.Value) return false;
                candidate[pair.Key] = new InventoryEntry(new RuntimeItemState(item.InstanceId, item.ContentId,
                    item.RemainingCharges - pair.Value, false, item.StackStateKey, item.Composition, item.Material, item.Attachment), entry.Location); changed = true;
            }
            if (ValidateAll(candidate) != InventoryFailureReason.None) return false;
            prepared = _prepared = new PreparedInventory(candidate, changed); return true;
        }
        internal bool CompletePrepared(PreparedInventory prepared, bool commit, bool materialChanged = false)
        {
            if (prepared == null || !ReferenceEquals(prepared, _prepared)) return false;
            if (commit && (prepared.Changed || materialChanged)) Commit(prepared.Candidate);
            _prepared = null; return true;
        }
        internal sealed class PreparedInventory
        {
            internal readonly Dictionary<string, InventoryEntry> Candidate;
            internal readonly bool Changed;
            internal PreparedInventory(Dictionary<string, InventoryEntry> candidate, bool changed) { Candidate = candidate; Changed = changed; }
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
        private RuntimeItemState NormalizeContainer(RuntimeItemState item)
        {
            if (item == null || item.Composition != null || !_catalog.TryGetRules(item.ContentId, out var rules) || rules.Kind != ItemKind.Container) return item;
            return new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges, item.HasUnlimitedCharges,
                item.StackStateKey, new ContainerComposition(rules.ContainerCapacity, rules.ContainerMaterials), item.Material);
        }

        internal bool TryPrepareMaterialChange(string id, RuntimeItemState replacement, ItemLocation location, out PreparedInventory prepared)
        {
            prepared = null;
            if (IsResourceBusy || !_items.TryGetValue(id, out var previous)
                || !_catalog.TryGetRules(previous.Item.ContentId, out var rules)
                || (rules.Kind != ItemKind.Container && rules.Kind != ItemKind.Material)) return false;
            var candidate = new Dictionary<string, InventoryEntry>(_items, StringComparer.Ordinal);
            if (replacement == null) candidate.Remove(id);
            else
            {
                if (replacement.InstanceId != id || replacement.ContentId != previous.Item.ContentId) return false;
                candidate[id] = new InventoryEntry(replacement, location);
            }
            if (ValidateAll(candidate) != InventoryFailureReason.None) return false;
            prepared = _prepared = new PreparedInventory(candidate, true); return true;
        }

        internal InventoryOperationResult Result(bool success, InventoryFailureReason reason) => new InventoryOperationResult(success, reason, Version);
        private void Commit(Dictionary<string, InventoryEntry> candidate) { _items = candidate; Version++; }

        private InventoryFailureReason ValidateItem(RuntimeItemState item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.InstanceId) || string.IsNullOrWhiteSpace(item.ContentId))
                return InventoryFailureReason.InvalidIdentity;
            if (item.RemainingCharges < 0 || item.StackStateKey == null) return InventoryFailureReason.InvalidState;
            if (!_catalog.TryGetRules(item.ContentId, out InventoryItemRules rules)) return InventoryFailureReason.UnknownContent;
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
                if (entry.Item.Composition != null)
                    foreach (InventoryEntry other in candidate.Values)
                        if (entry.Item.InstanceId != other.Item.InstanceId && ReferenceEquals(entry.Item.Composition, other.Item.Composition))
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
                    _catalog.TryGetRules(candidate[location.OwnerId].Item.ContentId, out InventoryItemRules wandRules);
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
                    if (rules.Kind != ItemKind.Container) return InventoryFailureReason.WrongKind;
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
