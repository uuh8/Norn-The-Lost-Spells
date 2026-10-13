using Game.Core;

namespace Game.Run
{
    /// <summary>一次同步 World 命令的目标预约；数组由预约自己持有，世界采样 Buffer 返回后可立即复用。</summary>
    public sealed class ContainerReservation : IMaterialTransferDestination
    {
        private readonly ItemOwnershipStore _store;
        private readonly MaterialAmount[] _amounts = new MaterialAmount[8];
        private RuntimeItemState _item;
        private string _backpackId;
        private InventoryItemRules _rules;
        private uint _version;
        private int _count;
        private bool _reserved, _solid;
        private ItemOwnershipStore.PreparedInventory _prepared;
        public string CommittedItemId { get; private set; }
        public ushort AllowedMaterials => _solid ? (ushort)(1 << _rules.MaterialKey) : _item.Composition.AllowedMaterials;
        public ulong FreeCapacity => _solid ? _rules.MaterialItemCapacity : _item.Composition.FreeCapacity;
        public ContainerReservation(ItemOwnershipStore store) { _store = store; }
        internal void Configure(RuntimeItemState item, string backpackId, in InventoryItemRules rules, uint version, bool solid)
        { _item = item; _backpackId = backpackId; _rules = rules; _version = version; _solid = solid; CommittedItemId = null; }
        public bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure)
        {
            failure = MaterialTransferFailure.ResourceBusy;
            if (_reserved || _store.IsResourceBusy) return false;
            if (_store.Version != _version) { failure = MaterialTransferFailure.StaleVersion; return false; }
            if (count <= 0 || count > 8 || amounts == null || count > amounts.Length)
            { failure = MaterialTransferFailure.InvalidRequest; return false; }
            if (_solid)
            {
                if (count != 1 || amounts[0].MaterialKey != _rules.MaterialKey || amounts[0].Quantity == 0
                    || amounts[0].Quantity > _rules.MaterialItemCapacity) { failure = MaterialTransferFailure.InvalidRequest; return false; }
                // 固体拾取是离散发放；先找到实际格位，再创建唯一身份并预约候选表。
                _item = new RuntimeItemState(System.Guid.NewGuid().ToString("N"), _item.ContentId, 0, material:
                    new MaterialItemState(_rules.MaterialKey, amounts[0].Quantity));
                if (!FindPlacement(_item, _backpackId, true)) { failure = MaterialTransferFailure.Capacity; return false; }
            }
            else
            {
                if (!_item.Composition.CanAdd(amounts, count)) { failure = MaterialTransferFailure.Capacity; return false; }
                _store.TryGetLocation(_item.InstanceId, out var location);
                if (ItemStackOps.CountAt(_store, location) > 1)
                { if (!FindPlacement(_item, location.OwnerId, false)) { failure = MaterialTransferFailure.Capacity; return false; } }
                else if (!_store.TryLockMaterial(_version)) return false;
            }
            for (int i = 0; i < count; i++) _amounts[i] = amounts[i];
            _count = count; _reserved = true; failure = MaterialTransferFailure.None; return true;
        }
        private bool FindPlacement(RuntimeItemState item, string backpackId, bool add)
        {
            if (!_store.TryGet(backpackId, out var backpack) || !_store.Catalog.TryGetRules(backpack.ContentId, out var bag)) return false;
            _store.TryGetLocation(item.InstanceId, out var source);
            for (int turn = 0; turn < 4; turn++)
                for (int y = 0; y < bag.BackpackHeight; y++) for (int x = 0; x < bag.BackpackWidth; x++)
                {
                    var destination = new ItemLocation(ItemLocationKind.Backpack, backpackId, x, y, turn, -1);
                    if (!add && destination == source) continue;
                    // 禁止预约已有堆叠：准备时空瓶兼容，提交填料后兼容性会改变。
                    bool occupied = false;
                    foreach (var entry in _store)
                        if (entry.Item.InstanceId != item.InstanceId && entry.Location.Kind == ItemLocationKind.Backpack && entry.Location.OwnerId == backpackId)
                        {
                            _store.Catalog.TryGetRules(entry.Item.ContentId, out var other);
                            if (InventoryGrid.Overlaps(_rules.Shape, destination, other.Shape, entry.Location)) { occupied = true; break; }
                        }
                    if (!occupied && _store.TryPrepareMaterialItem(item, destination, add, out _prepared)) return true;
                }
            return false;
        }
        public void Commit()
        {
            if (!_reserved) throw new System.InvalidOperationException("目标未预约。");
            if (!_solid) _item.Composition.AddReserved(_amounts, _count);
            if (_prepared != null) _store.CompletePrepared(_prepared, true); else _store.FinishMaterial(true);
            CommittedItemId = _item.InstanceId; _reserved = false; _prepared = null;
        }
        public void Cancel()
        {
            if (!_reserved) return;
            if (_prepared != null) _store.CompletePrepared(_prepared, false); else _store.FinishMaterial(false);
            _reserved = false; _prepared = null;
        }
    }
}
