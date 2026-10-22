using Game.Core;

namespace Game.Run
{
    /// <summary>世界接收材料前锁定实际来源；连续撒出原地扣量，固体/破碎走离散候选表。</summary>
    public sealed class ContainerSourceReservation : IMaterialTransferSource
    {
        private readonly ItemOwnershipStore _store;
        private readonly MaterialAmount[] _amounts = new MaterialAmount[8];
        private RuntimeItemState _item;
        private uint _version;
        private int _count;
        private bool _reserved, _destroyShell;
        private ItemOwnershipStore.PreparedInventory _prepared;
        public ContainerSourceReservation(ItemOwnershipStore store) { _store = store; }
        internal void Configure(RuntimeItemState item, uint version, bool destroyShell = false)
        { _item = item; _version = version; _destroyShell = destroyShell; }
        public bool TryReserve(MaterialAmount[] amounts, int count, out MaterialTransferFailure failure)
        {
            failure = MaterialTransferFailure.ResourceBusy;
            if (_reserved || _store.IsResourceBusy) return false;
            if (_store.Version != _version) { failure = MaterialTransferFailure.StaleVersion; return false; }
            if (amounts == null || count <= 0 || count > 8 || count > amounts.Length)
            { failure = MaterialTransferFailure.InvalidRequest; return false; }
            if (_item.Composition != null)
            {
                if (!_item.Composition.CanRemove(amounts, count)) { failure = MaterialTransferFailure.NoMaterial; return false; }
                if (_destroyShell)
                {
                    ulong total = 0; for (int i = 0; i < count; i++) total += amounts[i].Quantity;
                    if (total != _item.Composition.Total) { failure = MaterialTransferFailure.InvalidRequest; return false; }
                    if (!_store.TryGetLocation(_item.InstanceId, out var location) || location.Kind != ItemLocationKind.ThrownContainer
                        || !_store.TryPrepareMaterialChange(_item.InstanceId, null, location, out _prepared)) return false;
                }
                else if (!_store.TryLockMaterial(_version)) return false;
            }
            else
            {
                if (_item.Material == null || count != 1 || amounts[0].MaterialKey != _item.Material.MaterialKey
                    || amounts[0].Quantity == 0 || amounts[0].Quantity > _item.Material.Quantity)
                { failure = MaterialTransferFailure.NoMaterial; return false; }
                ulong remaining = _item.Material.Quantity - amounts[0].Quantity;
                RuntimeItemState replacement = remaining == 0 ? null : new RuntimeItemState(_item.InstanceId, _item.ContentId,
                    _item.RemainingCharges, _item.HasUnlimitedCharges, _item.StackStateKey, material: new MaterialItemState(_item.Material.MaterialKey, remaining));
                _store.TryGetLocation(_item.InstanceId, out var location);
                if (!_store.TryPrepareMaterialChange(_item.InstanceId, replacement, location, out _prepared)) return false;
            }
            for (int i = 0; i < count; i++) _amounts[i] = amounts[i];
            _count = count; _reserved = true; failure = MaterialTransferFailure.None; return true;
        }
        public void Commit()
        {
            if (!_reserved) throw new System.InvalidOperationException("来源未预约。");
            if (_item.Composition != null) _item.Composition.RemoveReserved(_amounts, _count);
            if (_prepared != null) _store.CompletePrepared(_prepared, true); else _store.FinishMaterial(true);
            _reserved = false; _prepared = null;
        }
        public void Cancel()
        {
            if (!_reserved) return;
            if (_prepared != null) _store.CompletePrepared(_prepared, false); else _store.FinishMaterial(false);
            _reserved = false; _prepared = null;
        }
    }
}
