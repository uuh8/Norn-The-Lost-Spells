using Game.Core;
using Unity.Profiling;

namespace Game.Run
{
    /// <summary>Run 只管理资产预约，通过 Core 同步命令把真实世界扣量交给 ElementField。</summary>
    public sealed class InventoryMaterialTransferService
    {
        private readonly SinglePlayerProfile _profile;
        private readonly string _inventoryId;
        private readonly ContainerReservation _reservation;
        private readonly MaterialTransferExchange _exchange = new MaterialTransferExchange();
        private readonly string[] _sources = new string[32];
        private readonly ulong[] _lastRequests = new ulong[32];
        private int _sourceCount;
        private bool _executing;
        private static readonly ProfilerMarker TransferMarker = new ProfilerMarker("Norn.MaterialTransfer.Execute");
        public InventoryMaterialTransferService(SinglePlayerProfile profile, string inventoryId)
        { _profile = profile; _inventoryId = inventoryId; _reservation = new ContainerReservation(profile.Items); }
        public MaterialTransferResultEvent Execute(in MaterialTransferRequestedEvent request)
        {
            using var sample = TransferMarker.Auto();
            var result = new MaterialTransferResultEvent { InventoryId = request.InventoryId, WorldId = request.WorldId,
                SourceId = request.SourceId, ItemId = request.ItemId, RequestId = request.RequestId, Version = _profile.Items.Version };
            if (_executing || _profile.Items.IsResourceBusy) { result.Failure = MaterialTransferFailure.ResourceBusy; return result; }
            if (request.InventoryId != _inventoryId || string.IsNullOrEmpty(request.SourceId) || string.IsNullOrEmpty(request.WorldId)
                || request.RequestId == 0 || request.Budget == 0)
            { result.Failure = MaterialTransferFailure.InvalidRequest; return result; }
            int source = 0; while (source < _sourceCount && _sources[source] != request.SourceId) source++;
            if (source == _sourceCount)
            { if (_sourceCount == _sources.Length) { result.Failure = MaterialTransferFailure.InvalidRequest; return result; }
                _sources[source] = request.SourceId; _sourceCount++; }
            if (request.RequestId <= _lastRequests[source]) { result.Failure = MaterialTransferFailure.DuplicateRequest; return result; }
            _lastRequests[source] = request.RequestId;
            if (request.ExpectedVersion != _profile.Items.Version) { result.Failure = MaterialTransferFailure.StaleVersion; return result; }
            RuntimeItemState item; InventoryItemRules rules;
            if (request.SolidItem)
            {
                if (!_profile.Items.Catalog.TryGetRules(request.MaterialContentId, out rules) || rules.Kind != ItemKind.Material
                    || (rules.MaterialKey != 5 && rules.MaterialKey != 6) || !IsCarriedBackpack(request.BackpackId))
                { result.Failure = MaterialTransferFailure.WrongOwnership; return result; }
                item = null; // 固体目标由预约选择已有堆；只有确需新格时创建身份。
            }
            else
            {
                if (!_profile.Items.TryGet(request.ItemId, out item) || item.Composition == null
                    || !_profile.Items.TryGetLocation(request.ItemId, out var location)
                    || !(location.Kind == ItemLocationKind.EquippedTool && location.OwnerId == _profile.ActorId
                        || location.Kind == ItemLocationKind.Backpack && IsCarriedBackpack(location.OwnerId)))
                { result.Failure = MaterialTransferFailure.WrongOwnership; return result; }
                _profile.Items.Catalog.TryGetRules(item.ContentId, out rules);
            }
            _reservation.Configure(item, request.BackpackId, rules, request.ExpectedVersion, request.SolidItem,request.MaterialContentId);
            if (_reservation.FreeCapacity == 0) { result.Failure = MaterialTransferFailure.Capacity; return result; }
            _executing = true;
            try
            {
                _exchange.Request = request; _exchange.Destination = _reservation; _exchange.Handled = false;
                _exchange.Result = result;
                EventBus<MaterialWorldTransferRequestedEvent>.Publish(new MaterialWorldTransferRequestedEvent { Exchange = _exchange });
                result = _exchange.Result;
                if (!_exchange.Handled) result.Failure = MaterialTransferFailure.WorldUnavailable;
                result.Version = _profile.Items.Version;
                if (result.Success) result.ItemId = _reservation.CommittedItemId;
                return result;
            }
            finally { _reservation.Cancel(); _executing = false; _exchange.Destination = null; }
        }
        private bool IsCarriedBackpack(string id) => _profile.Items.TryGet(id, out var item)
            && _profile.Items.Catalog.TryGetRules(item.ContentId, out var rules) && rules.Kind == ItemKind.Backpack
            && _profile.Items.TryGetLocation(id, out var location) && location.Kind == ItemLocationKind.EquippedBackpack
            && location.OwnerId == _profile.ActorId;
    }
}
