using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 一个DS副本唯一的共享容器归属表。只保存当前地面记录，不保留无限历史/tombstone；幂等由各
    /// Workspace原有128收据账本负责。每次落地生成独立WorldOwner，使旧拾取不能作用于同瓶下次落地。
    /// 不创建配方/物品、不保存数据库、不决定死亡/撤离；模型/Physics由Runtime适配层先准备。
    /// </summary>
    public sealed partial class OnlineGroundItemStore : IDisposable
    {
        private sealed class Record
        {
            internal string WorldOwner;
            internal uint Revision;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal OnlineOwnedWorkspace.GroundTransferState Transfer;
        }
        private ItemOwnershipStore _store;
        private readonly Dictionary<string, Record> _records = new Dictionary<string, Record>(StringComparer.Ordinal);
        // 与当前地面记录分开：瓶被拾走后仍保留已发放key，直到本Run的GroundStore结束。
        private readonly Dictionary<string, string> _lootKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        // 固定固体来源仅原P7的一个；与12个敌死容器稳定key分别有界，拾取后仍记账。
        private string _fixedMaterialKey, _fixedMaterialInstanceId;
        private readonly MaterialAmount[] _parts = new MaterialAmount[8];
        private bool _disposed;
        private IOnlineItemRetirementJournal _practiceJournal;
        public string WorldId { get; } = Guid.NewGuid().ToString("N");
        public uint Version { get; private set; } = 1;
        public int Count => _records.Count;
        internal ItemOwnershipStore Store => _store;
        /// <summary>DS同Run唯一Journal；材料被完整合并时才终止源ID，普通拾取/转交仍保留同一实物。</summary>
        public bool ConfigureCheckpointJournal(IOnlineItemRetirementJournal journal)
        {
            if (_disposed || !_store.ConfigureCheckpointJournal(journal, WorldId, CheckpointRevision)) return false;
            _practiceJournal = journal; return true;
        }
        private uint CheckpointRevision(string id)
        {
            if (_records.TryGetValue(id, out var root)) return root.Revision;
            foreach (var record in _records.Values)
                if (record.Transfer != null && record.Transfer.Revisions.TryGetValue(id, out uint revision)) return revision;
            return 0;
        }

        public OnlineGroundItemStore(ItemCatalogIndex catalog) => _store = new ItemOwnershipStore(catalog);

        public bool TryGetLootRegistration(string key, out string instanceId)
        {
            instanceId = null;
            return !_disposed && !string.IsNullOrWhiteSpace(key) && _lootKeys.TryGetValue(key, out instanceId);
        }

        /// <summary>DS已有P7计划注册空瓶；不借玩家库存、不用MaterialQuantity补满组分，失败不记成功key。</summary>
        public OnlineInventoryCommandFailure TryRegisterLoot(string key, string contentId, Vector3 position, Quaternion rotation, out string instanceId)
        {
            instanceId = null;
            if (_disposed) return OnlineInventoryCommandFailure.NotReady;
            if (TryGetLootRegistration(key, out instanceId)) return OnlineInventoryCommandFailure.None;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(contentId) || !Finite(position) || !ValidRotation(rotation)
                || !_store.Catalog.TryGetRules(contentId, out var rules) || rules.Kind != ItemKind.Container)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (_lootKeys.Count >= OnlineDungeonContainerLoot.MaximumLootItems || _store.Count >= OnlineGroundInventorySnapshot.MaximumItems || Version == uint.MaxValue)
                return OnlineInventoryCommandFailure.NoCapacity;
            if (_store.IsResourceBusy) return OnlineInventoryCommandFailure.Busy;
            string id = Guid.NewGuid().ToString("N");
            var item = new RuntimeItemState(id, contentId, 0,
                composition: new ContainerComposition(rules.ContainerCapacity, rules.ContainerMaterials));
            var record = new Record { WorldOwner = Guid.NewGuid().ToString("N"), Revision = 1, Position = position, Rotation = rotation };
            _records.EnsureCapacity(_records.Count + 1); _lootKeys.EnsureCapacity(_lootKeys.Count + 1);
            if (!_store.TryAdd(item, new ItemLocation(ItemLocationKind.WorldItem, record.WorldOwner, 0, 0, 0, -1)))
                return _store.IsResourceBusy ? OnlineInventoryCommandFailure.Busy : OnlineInventoryCommandFailure.Rejected;
            _records.Add(id, record); _lootKeys.Add(key, id); Version++; instanceId = id;
            return OnlineInventoryCommandFailure.None;
        }

        public bool TryGetMaterialLootRegistration(string key, out string instanceId)
        {
            instanceId = null;
            if (_disposed || string.IsNullOrWhiteSpace(key) || key != _fixedMaterialKey) return false;
            instanceId = _fixedMaterialInstanceId; return true;
        }
        /// <summary>只登记原固定来源的真实数量；Key由Catalog固定，成功key不会因拾取而遗忘。</summary>
        public OnlineInventoryCommandFailure TryRegisterMaterialLoot(string key, string contentId, ulong quantity,
            Vector3 position, Quaternion rotation, out string instanceId)
        {
            instanceId = null;
            if (_disposed) return OnlineInventoryCommandFailure.NotReady;
            if (TryGetMaterialLootRegistration(key, out instanceId)) return OnlineInventoryCommandFailure.None;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(contentId) || !Finite(position) || !ValidRotation(rotation)
                || !_store.Catalog.TryGetRules(contentId, out var rules) || rules.Kind != ItemKind.Material
                || !MaterialAmount.IsKnown(rules.MaterialKey) || quantity == 0 || quantity > rules.MaterialItemCapacity)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (_fixedMaterialKey != null || _store.Count >= OnlineGroundInventorySnapshot.MaximumItems || Version == uint.MaxValue)
                return OnlineInventoryCommandFailure.NoCapacity;
            if (_store.IsResourceBusy) return OnlineInventoryCommandFailure.Busy;
            string id = Guid.NewGuid().ToString("N");
            var item = new RuntimeItemState(id, contentId, 0, material: new MaterialItemState(rules.MaterialKey, quantity));
            var record = new Record { WorldOwner = Guid.NewGuid().ToString("N"), Revision = 1, Position = position, Rotation = rotation };
            _records.EnsureCapacity(_records.Count + 1);
            if (!_store.TryAdd(item, new ItemLocation(ItemLocationKind.WorldItem, record.WorldOwner, 0, 0, 0, -1)))
                return _store.IsResourceBusy ? OnlineInventoryCommandFailure.Busy : OnlineInventoryCommandFailure.Rejected;
            _records.Add(id, record); _fixedMaterialKey = key; _fixedMaterialInstanceId = id;
            Version++; instanceId = id; return OnlineInventoryCommandFailure.None;
        }

        public bool TryGet(string id, out OnlineGroundItemSnapshot snapshot)
        {
            snapshot = null;
            if (_disposed || id == null || !_records.TryGetValue(id, out var record) || !_store.TryGet(id, out var item)) return false;
            snapshot = ToSnapshot(item, record); return true;
        }
        public OnlineGroundInventorySnapshot Capture()
        {
            if (_disposed) return null;
            var result = new OnlineGroundInventorySnapshot { Version = Version, Items = new OnlineGroundItemSnapshot[_records.Count] };
            int i = 0;
            foreach (var pair in _records)
            {
                if (!_store.TryGet(pair.Key, out var item)) throw new InvalidOperationException("共享地面登记与唯一归属表不一致。");
                result.Items[i++] = ToSnapshot(item, pair.Value);
            }
            return result;
        }
        private OnlineGroundItemSnapshot ToSnapshot(RuntimeItemState item, Record record)
        {
            if (!_store.Catalog.TryGetRules(item.ContentId, out var rules)
                || rules.Kind == ItemKind.Container && (item.Composition == null || item.Material != null)
                || rules.Kind == ItemKind.Material && (item.Material == null || item.Composition != null
                    || item.Material.MaterialKey != rules.MaterialKey || item.Material.Quantity == 0 || item.Material.Quantity > rules.MaterialItemCapacity))
                throw new InvalidOperationException("共享地面状态与容器/固体目录规则不一致。");
            OnlineOwnedContainerStateSnapshot container = null;
            if (item.Composition != null)
            {
                int count = item.Composition.CopyTo(_parts);
                var components = new OnlineOwnedMaterialAmountSnapshot[count];
                for (int i = 0; i < count; i++) components[i] = new OnlineOwnedMaterialAmountSnapshot
                { MaterialKey = _parts[i].MaterialKey, Quantity = _parts[i].Quantity };
                container = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = components };
            }
            var children = new List<OnlineOwnedItemSnapshot>();
            foreach (var entry in _store)
                if (entry.Location.Kind == ItemLocationKind.WandSlot && entry.Location.OwnerId == item.InstanceId)
                {
                    if (record.Transfer == null || !record.Transfer.Revisions.TryGetValue(entry.Item.InstanceId, out uint revision))
                        throw new InvalidOperationException("地面杖安装卡缺少原实例修订。");
                    children.Add(OnlineOwnedWorkspace.CopyGroundItem(entry.Item, entry.Location, revision, _store.Catalog));
                }
            return new OnlineGroundItemSnapshot
            {
                InstanceId = item.InstanceId, ContentId = item.ContentId, WorldOwnerId = record.WorldOwner,
                Revision = record.Revision, Position = record.Position, Rotation = record.Rotation,
                ContainerState = container, MaterialState = OnlineOwnedWorkspace.CopyMaterial(item),
                Item = OnlineOwnedWorkspace.CopyGroundItem(item, new ItemLocation(ItemLocationKind.WorldItem, record.WorldOwner, 0, 0, 0, -1), record.Revision, _store.Catalog),
                Children = children.ToArray()
            };
        }

        /// <summary>原NPC混合计划的真实单件注册。状态已由计划冻结，失败不改key或重Roll；不借玩家库存造掉落。</summary>
        public OnlineInventoryCommandFailure TryRegisterPlannedLoot(string key, RuntimeItemState item, Vector3 position, Quaternion rotation, out string instanceId)
        {
            instanceId = null;
            if (_disposed) return OnlineInventoryCommandFailure.NotReady;
            if (TryGetLootRegistration(key, out instanceId)) return OnlineInventoryCommandFailure.None;
            if (string.IsNullOrWhiteSpace(key) || item == null || !Finite(position) || !ValidRotation(rotation) || item.EntryBoundBackpack)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (_lootKeys.Count >= OnlineDungeonContainerLoot.MaximumLootItems || _store.Count >= OnlineGroundInventorySnapshot.MaximumItems || Version == uint.MaxValue)
                return OnlineInventoryCommandFailure.NoCapacity;
            var transfer = new OnlineOwnedWorkspace.GroundTransferState(); transfer.Revisions.Add(item.InstanceId, 1);
            var record = new Record { WorldOwner = Guid.NewGuid().ToString("N"), Revision = 1, Position = position, Rotation = rotation, Transfer = transfer };
            _records.EnsureCapacity(_records.Count + 1); _lootKeys.EnsureCapacity(_lootKeys.Count + 1);
            if (!_store.TryAdd(item, new ItemLocation(ItemLocationKind.WorldItem, record.WorldOwner, 0, 0, 0, -1)))
                return _store.IsResourceBusy ? OnlineInventoryCommandFailure.Busy : OnlineInventoryCommandFailure.Rejected;
            _records.Add(item.InstanceId, record); _lootKeys.Add(key, item.InstanceId); Version++; instanceId = item.InstanceId;
            return OnlineInventoryCommandFailure.None;
        }

        internal readonly struct Placement
        {
            internal readonly string ItemId;
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal Placement(string id, Vector3 position, Quaternion rotation) { ItemId = id; Position = position; Rotation = rotation; }
        }

        public OnlineInventoryCommandFailure Drop(OnlineOwnedWorkspace owner, string id, Vector3 position, Quaternion rotation)
            => DropMany(owner, new[] { new Placement(id, position, rotation) });

        internal OnlineInventoryCommandFailure DropMany(OnlineOwnedWorkspace owner, IReadOnlyList<Placement> placements)
        {
            if (_disposed || owner == null || !owner.IsActiveAndEnabled) return OnlineInventoryCommandFailure.NotReady;
            if (placements == null || placements.Count == 0) return OnlineInventoryCommandFailure.InvalidInput;
            var records = new Record[placements.Count]; var roots = new ItemOwnershipStore.ItemTransferRoot[placements.Count];
            int instances = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                var place = placements[i];
                if (string.IsNullOrWhiteSpace(place.ItemId) || !Finite(place.Position) || !ValidRotation(place.Rotation)) return OnlineInventoryCommandFailure.InvalidInput;
                if (_records.ContainsKey(place.ItemId) || !owner.TryGetCarriedItem(place.ItemId, out _, out var source)
                    || !owner.TryCaptureGroundDeparture(place.ItemId, out var transfer)) return OnlineInventoryCommandFailure.NotOwned;
                instances += transfer.Revisions.Count;
                var record = new Record { WorldOwner = Guid.NewGuid().ToString("N"), Revision = transfer.Revisions[place.ItemId],
                    Position = place.Position, Rotation = place.Rotation, Transfer = transfer };
                records[i] = record; roots[i] = new ItemOwnershipStore.ItemTransferRoot(place.ItemId, source,
                    new ItemLocation(ItemLocationKind.WorldItem, record.WorldOwner, 0, 0, 0, -1));
            }
            if (_store.Count + instances > OnlineGroundInventorySnapshot.MaximumItems || Version == uint.MaxValue)
                return OnlineInventoryCommandFailure.NoCapacity;
            _records.EnsureCapacity(_records.Count + placements.Count);
            var result = owner.OwnershipStore.TryTransferItemTreesTo(_store, roots, owner.InventoryVersion, _store.Version);
            if (!result.Success) return Map(result.Reason);
            for (int i = 0; i < placements.Count; i++) { _records.Add(placements[i].ItemId, records[i]); owner.CompleteGroundDeparture(records[i].Transfer); }
            Version++; return OnlineInventoryCommandFailure.None;
        }

        public OnlineInventoryCommandFailure Pickup(OnlineOwnedWorkspace owner, in OnlineInventoryCommandIntent intent, Vector3 actorPosition)
            => Pickup(owner, intent, actorPosition, out _);

        public OnlineInventoryCommandFailure Pickup(OnlineOwnedWorkspace owner, in OnlineInventoryCommandIntent intent,
            Vector3 actorPosition, out ulong transferred)
        {
            transferred = 0;
            if (_disposed || owner == null || !owner.IsActiveAndEnabled) return OnlineInventoryCommandFailure.NotReady;
            if (string.IsNullOrWhiteSpace(intent.ItemId) || !Finite(actorPosition) || !Id(intent.WorldOwnerId) || intent.GridX < 0 || intent.GridY < 0
                || intent.GridX > 255 || intent.GridY > 255 || intent.QuarterTurns < 0 || intent.QuarterTurns > 3)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (!_records.TryGetValue(intent.ItemId, out var record) || record.WorldOwner != intent.WorldOwnerId
                || !_store.TryGetLocation(intent.ItemId, out var source) || source.OwnerId != record.WorldOwner)
                return OnlineInventoryCommandFailure.NotOwned;
            if ((record.Position - actorPosition).sqrMagnitude > 9) return OnlineInventoryCommandFailure.Rejected;
            if (Version == uint.MaxValue) return OnlineInventoryCommandFailure.NoCapacity;
            if (!_store.TryGet(intent.ItemId, out var item) || !_store.Catalog.TryGetRules(item.ContentId, out var rules)) return OnlineInventoryCommandFailure.InvalidInput;
            bool generic = intent.Kind == OnlineInventoryCommandKind.PickupItem;
            if (!generic && rules.Kind != ItemKind.Container && rules.Kind != ItemKind.Material) return OnlineInventoryCommandFailure.InvalidInput;
            if (item.EntryBoundBackpack) return OnlineInventoryCommandFailure.NotOwned;
            bool material = rules.Kind == ItemKind.Material;
            if (material && (item.Material == null || item.Composition != null || item.Material.MaterialKey != rules.MaterialKey
                || item.Material.Quantity == 0 || item.Material.Quantity > rules.MaterialItemCapacity)) return OnlineInventoryCommandFailure.InvalidInput;
            if (material && record.Revision == uint.MaxValue) return OnlineInventoryCommandFailure.NoCapacity;
            string bag = null; InventoryItemRules bagRules = default;
            foreach (var entry in owner.OwnershipStore)
                if (entry.Location.Kind == ItemLocationKind.EquippedBackpack && entry.Location.OwnerId == owner.PlayerId)
                { bag = entry.Item.InstanceId; owner.OwnershipStore.Catalog.TryGetRules(entry.Item.ContentId, out bagRules); break; }
            if (intent.AutoPlace && (!string.IsNullOrEmpty(intent.TargetBackpackId) || intent.GridX != 0 || intent.GridY != 0 || intent.QuarterTurns != 0
                || intent.TargetLocationKind != 0 || intent.TargetSlot != 0 || !string.IsNullOrEmpty(intent.TargetWandId) || intent.TargetProgramRevision != 0))
                return OnlineInventoryCommandFailure.InvalidInput;
            InventoryOperationResult result = default;
            if (!intent.AutoPlace)
            {
                ItemLocation target;
                if (generic)
                {
                    var failure = owner.ResolveItemTarget(intent, out target);
                    if (failure != OnlineInventoryCommandFailure.None) return failure;
                }
                else
                {
                    if (bag == null) return OnlineInventoryCommandFailure.NoCapacity;
                    if (intent.TargetBackpackId != bag) return OnlineInventoryCommandFailure.InvalidInput;
                    target = new ItemLocation(ItemLocationKind.Backpack, bag, intent.GridX, intent.GridY, intent.QuarterTurns, -1);
                }
                result = TryPickupAt(owner, intent.ItemId, record, source, target, material, out transferred);
            }
            else
            {
                bool found = false;
                // 原附近拾取语义：法术先放真实专区，满了才按形状进包；包壳只在没有装备包时入空装备位。
                if (generic && rules.Kind == ItemKind.Spell)
                    for (int slot = 0; slot < _store.Catalog.SpellStorageCapacity && !found; slot++)
                    {
                        result = TryPickupAt(owner, intent.ItemId, record, source,
                            new ItemLocation(ItemLocationKind.SpellStorage, owner.PlayerId, 0, 0, 0, slot), false, out transferred);
                        found = result.Success;
                    }
                if (generic && rules.Kind == ItemKind.Backpack && bag == null)
                {
                    result = TryPickupAt(owner, intent.ItemId, record, source,
                        new ItemLocation(ItemLocationKind.EquippedBackpack, owner.PlayerId, 0, 0, 0, 0), false, out transferred);
                    found = result.Success;
                }
                // 原Nearby先按包内顺序尝试一次可成功的兼容合并；部分成功立即结束，不整包吸取。
                if (material && bag != null)
                    foreach (var entry in owner.OwnershipStore)
                        if (entry.Location.Kind == ItemLocationKind.Backpack && entry.Location.OwnerId == bag
                            && MaterialStackOps.Compatible(item, entry.Item))
                        {
                            result = TryPickupAt(owner, intent.ItemId, record, source, entry.Location, true, out transferred);
                            if (result.Success) { found = true; break; }
                        }
                // 原旋转/行/列寻格；失败时不改变任一Store。
                for (int turn = 0; turn < 4 && !found && bag != null && rules.Kind != ItemKind.Backpack; turn++)
                    for (int y = 0; y <= bagRules.BackpackHeight - rules.Shape.GetHeight(turn) && !found; y++)
                        for (int x = 0; x <= bagRules.BackpackWidth - rules.Shape.GetWidth(turn) && !found; x++)
                        {
                            result = TryPickupAt(owner, intent.ItemId, record, source,
                                new ItemLocation(ItemLocationKind.Backpack, bag, x, y, turn, -1), material, out transferred);
                            found = result.Success;
                        }
                if (!found) return OnlineInventoryCommandFailure.NoCapacity;
            }
            if (!result.Success) return Map(result.Reason);
            if (_store.TryGet(intent.ItemId, out _)) record.Revision++;
            else _records.Remove(intent.ItemId);
            Version++; return OnlineInventoryCommandFailure.None;
        }

        private InventoryOperationResult TryPickupAt(OnlineOwnedWorkspace owner, string id, Record record,
            ItemLocation source, ItemLocation target, bool material, out ulong transferred)
        {
            transferred = 0; bool merging = false;
            if (material && _store.TryGet(id, out var item))
                foreach (var entry in owner.OwnershipStore)
                    if (entry.Location == target && MaterialStackOps.Compatible(item, entry.Item)) { merging = true; break; }
            if (!material)
            {
                var transfer = record.Transfer;
                if (transfer == null)
                { transfer = new OnlineOwnedWorkspace.GroundTransferState(); transfer.Revisions.Add(id, record.Revision); }
                if (!owner.PrepareGroundAdmission(transfer)) return _store.Result(false, InventoryFailureReason.Capacity);
                bool complete = false;
                try
                {
                    var result = _store.TryTransferItemTreeTo(owner.OwnershipStore, id, source, target, _store.Version, owner.InventoryVersion);
                    if (!result.Success) return result;
                    complete = true; owner.CompleteGroundAdmission(transfer); return result;
                }
                finally { if (!complete) owner.CancelGroundAdmission(transfer); }
            }
            if (!owner.PrepareGroundAdmission(id, record.Revision, !merging)) return _store.Result(false, InventoryFailureReason.Capacity);
            bool committed = false;
            try
            {
                var result = material
                    ? _store.TryPickupMaterialTo(owner.OwnershipStore, id, source, target, _store.Version, owner.InventoryVersion, out transferred)
                    : _store.TryTransferItemTreeTo(owner.OwnershipStore, id, source, target, _store.Version, owner.InventoryVersion);
                committed = result.Success;
                // 合并只保留目标ID；临时准备的源修订不能变成一份不存在的Owner资产。
                if (committed && !owner.TryGetItem(id, out _)) owner.CancelGroundAdmission(id);
                if (committed) owner.CompleteGroundAdmission();
                return result;
            }
            finally { if (!committed) owner.CancelGroundAdmission(id); }
        }

        private static OnlineInventoryCommandFailure Map(InventoryFailureReason reason)
        {
            switch (reason)
            {
                case InventoryFailureReason.None: return OnlineInventoryCommandFailure.None;
                case InventoryFailureReason.StaleVersion: return OnlineInventoryCommandFailure.StaleVersion;
                case InventoryFailureReason.ResourceBusy: return OnlineInventoryCommandFailure.Busy;
                case InventoryFailureReason.OutOfBounds:
                case InventoryFailureReason.Occupied:
                case InventoryFailureReason.Capacity: return OnlineInventoryCommandFailure.NoCapacity;
                case InventoryFailureReason.NotFound:
                case InventoryFailureReason.InvalidOwnership: return OnlineInventoryCommandFailure.NotOwned;
                default: return OnlineInventoryCommandFailure.Rejected;
            }
        }
        private static bool Id(string id)
        { if (id == null || id.Length != 32) return false; foreach (char c in id) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false; return true; }
        private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
        private static bool ValidRotation(Quaternion q)
        { float n = q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w; return float.IsFinite(n) && n >= .99f && n <= 1.01f; }
        public void Dispose() { if (_disposed) return; _disposed = true; _records.Clear(); _lootKeys.Clear(); _fixedMaterialKey = _fixedMaterialInstanceId = null; _store = null; }
    }
}
