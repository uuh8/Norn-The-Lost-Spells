using System;
using Game.Core;
using Unity.Profiling;
using UnityEngine;

namespace Game.Run
{
    /// <summary>撒出/投掷的资产写持有者；代理不保存组分，在途位置和背包共用唯一实例账本。</summary>
    public sealed class ContainerUseService
    {
        private sealed class Flight
        { public string Id, ItemId, WorldId, SourceId; public Vector3 Position; public bool Active, Impacted; public float Radius; public ulong Deposited; }
        private readonly SinglePlayerProfile _profile;
        private readonly string _inventoryId;
        private readonly Flight[] _flights = new Flight[8];
        private readonly ulong[] _lost = new ulong[256];
        private readonly string[] _sources = new string[32];
        private readonly ulong[] _lastRequests = new ulong[32];
        private readonly MaterialAmount[] _all = new MaterialAmount[8], _amounts = new MaterialAmount[8];
        private readonly ulong[] _remainders = new ulong[8];
        private readonly ContainerSourceReservation _reservation;
        private readonly MaterialDepositExchange _deposit = new MaterialDepositExchange();
        private readonly ContainerThrowExchange _throw = new ContainerThrowExchange();
        private int _sourceCount;
        private bool _executing;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.ContainerUse.Execute");
        public int PendingFlights { get; private set; }
        public ulong LostTransitQuantity(byte key) => _lost[key];
        public ContainerUseService(SinglePlayerProfile profile, string inventoryId)
        { _profile = profile; _inventoryId = inventoryId; _reservation = new ContainerSourceReservation(profile.Items);
            for (int i = 0; i < _flights.Length; i++) _flights[i] = new Flight(); }
        public ContainerUseResultEvent Execute(in ContainerUseRequestedEvent request)
        {
            using var marker = Marker.Auto();
            var result = Result(request.Kind, request.SourceId, request.ItemId, request.WorldId, request.RequestId);
            if (_executing || _profile.Items.IsResourceBusy) return Fail(result, MaterialTransferFailure.ResourceBusy);
            if (request.InventoryId != _inventoryId || string.IsNullOrEmpty(request.SourceId) || string.IsNullOrEmpty(request.WorldId)
                || request.RequestId == 0 || (request.Kind != ContainerUseKind.Throw && request.Budget == 0)
                || !Finite(request.ActorPosition) || !Finite(request.TargetPosition) || !Finite(request.ThrowVelocity)
                || !float.IsFinite(request.Radius) || request.Radius <= 0 || request.Radius > 1
                // 旧3m限制只适用于近处放固体/投瓶发射点；撒料落点来自Camera的物理弧线。
                // 世界Deposit仍校验实际区域/容量并预约来源，拒绝时不扣材料，不能用旧近距限制吞掉合法落点。
                || request.Kind != ContainerUseKind.Pour && (request.TargetPosition - request.ActorPosition).sqrMagnitude > 9
                || request.Kind > ContainerUseKind.ReleaseSolid) return Fail(result, MaterialTransferFailure.InvalidRequest);
            int source = 0; while (source < _sourceCount && _sources[source] != request.SourceId) source++;
            if (source == _sourceCount)
            { if (_sourceCount == _sources.Length) return Fail(result, MaterialTransferFailure.InvalidRequest); _sources[source] = request.SourceId; _sourceCount++; }
            if (request.RequestId <= _lastRequests[source]) return Fail(result, MaterialTransferFailure.DuplicateRequest);
            _lastRequests[source] = request.RequestId;
            if (request.ExpectedVersion != _profile.Items.Version) return Fail(result, MaterialTransferFailure.StaleVersion);
            if (!_profile.Items.TryGet(request.ItemId, out var item) || !IsCarried(request.ItemId)) return Fail(result, MaterialTransferFailure.WrongOwnership);
            if (request.Kind == ContainerUseKind.Throw
                ? !_profile.Items.Catalog.TryGetRules(item.ContentId,out var throwRules) || throwRules.UseKind==ItemUseKind.None
                : request.Kind == ContainerUseKind.ReleaseSolid ? item.Material == null : item.Composition == null)
                return Fail(result, MaterialTransferFailure.WrongOwnership);
            _executing = true;
            try
            {
                if (request.Kind == ContainerUseKind.Throw) return Throw(request, item, result);
                int count;
                if (item.Composition != null) count = item.Composition.CopyTo(_all);
                else { count = 1; _all[0] = new MaterialAmount(item.Material.MaterialKey, item.Material.Quantity); }
                if (count == 0) return Fail(result, MaterialTransferFailure.NoMaterial);
                MaterialAmountOps.SplitBudget(_all, count, request.Budget, _amounts, _remainders, (int)(request.RequestId % 8));
                _reservation.Configure(item, _profile.Items.Version);
                result.Transfer.Failure = Deposit(request.WorldId, request.TargetPosition, request.Radius, count, out result.Transfer.Transferred);
                result.Transfer.Version = _profile.Items.Version; result.Success = result.Transfer.Failure == MaterialTransferFailure.None;
                return result;
            }
            finally { _reservation.Cancel(); _deposit.Source = null; _executing = false; }
        }
        private ContainerUseResultEvent Throw(in ContainerUseRequestedEvent request, RuntimeItemState item, ContainerUseResultEvent result)
        {
            Flight flight = null; for (int i = 0; i < _flights.Length; i++) if (!_flights[i].Active) { flight = _flights[i]; break; }
            if (flight == null) return Fail(result, MaterialTransferFailure.Capacity);
            string id = Guid.NewGuid().ToString("N");
            var location = new ItemLocation(ItemLocationKind.ThrownContainer, id, 0, 0, 0, -1);
            if (!_profile.Items.TryPrepareMaterialItem(item, location, false, out var prepared)) return Fail(result, MaterialTransferFailure.ResourceBusy);
            _throw.Request = request; _throw.FlightId = id; _throw.Proxy = null;
            try
            {
                EventBus<ContainerThrownEvent>.Publish(new ContainerThrownEvent { Exchange = _throw });
                if (_throw.Proxy == null) { _profile.Items.CompletePrepared(prepared, false); return Fail(result, MaterialTransferFailure.WorldUnavailable); }
            }
            catch (Exception exception)
            { _profile.Items.CompletePrepared(prepared, false); _throw.Proxy?.Cancel(); GameLog.Warn("投掷代理准备失败：" + exception.Message, "ContainerUse"); return Fail(result, MaterialTransferFailure.WorldUnavailable); }
            // 代理的 Launch 只启用已经准备好的物理体；资产先离开背包，不存在可用的第二份瓶。
            _profile.Items.CompletePrepared(prepared, true);
            flight.Id = id; flight.ItemId = item.InstanceId; flight.WorldId = request.WorldId; flight.SourceId = request.SourceId;
            flight.Active = true; flight.Impacted = false; flight.Deposited = 0; flight.Radius = request.Radius; PendingFlights++;
            result.Success = true; result.FlightId = id; result.Transfer.Version = _profile.Items.Version;
            result.Transfer.Transferred = item.Composition?.Total ?? item.Material?.Quantity ?? 0;
            try { _throw.Proxy.Launch(); }
            catch (Exception exception)
            { GameLog.Error("已提交投掷代理启动失败，数量保留在途：" + exception.Message, "ContainerUse"); }
            _throw.Proxy = null; return result;
        }
        public ContainerUseResultEvent Impact(in ContainerImpactEvent impact)
        {
            Flight flight = Find(impact.FlightId);
            var result = Result(ContainerUseKind.Throw, flight?.SourceId, flight?.ItemId, impact.WorldId, 0); result.FlightId = impact.FlightId;
            if (impact.InventoryId != _inventoryId || flight == null || flight.WorldId != impact.WorldId || !Finite(impact.Position))
                return Fail(result, MaterialTransferFailure.WrongOwnership);
            if (_executing || _profile.Items.IsResourceBusy) return Fail(result, MaterialTransferFailure.ResourceBusy);
            if (!flight.Impacted) { flight.Position = impact.Position; flight.Impacted = true; }
            if (!_profile.Items.TryGet(flight.ItemId, out var item)) return Fail(result, MaterialTransferFailure.WrongOwnership);
            _executing = true;
            try
            {
                if(item.Composition==null && item.Material==null)
                {
                    // 普通物体落地继续是同一实例，只改归属；不伪造材料Deposit或自动触发炼金。
                    var ground=new ItemLocation(ItemLocationKind.WorldItem,flight.Id,0,0,0,-1);
                    if(!_profile.Items.TryPrepareMaterialItem(item,ground,false,out var placement))return Fail(result,MaterialTransferFailure.ResourceBusy);
                    _profile.Items.CompletePrepared(placement,true);result.Success=true;result.Transfer.Version=_profile.Items.Version;
                    Resolve(flight,ContainerTransitEndReason.Deposited,0);return result;
                }
                if (item.Material != null)
                {
                    // 固体落地交回真正的材料 Owner；先预约扣量，世界拒绝时保留原在途实例。
                    ulong amount = System.Math.Min(1024UL, item.Material.Quantity);
                    _amounts[0] = new MaterialAmount(item.Material.MaterialKey, amount);
                    _reservation.Configure(item, _profile.Items.Version);
                    result.Transfer.Failure = Deposit(flight.WorldId, flight.Position, System.Math.Max(flight.Radius,.75f), 1, out result.Transfer.Transferred);
                    // 大堆只能部分落地时，固定半径会永久耗尽空间。按真实拒收逐级扩展，仍保留有界采样和原在途余量。
                    if(result.Transfer.Failure==MaterialTransferFailure.Capacity)flight.Radius=Math.Min(2f,Math.Max(.75f,flight.Radius)+.25f);
                    if (result.Transfer.Failure != MaterialTransferFailure.None) return result;
                    flight.Deposited += result.Transfer.Transferred;
                    result.Transfer.Version = _profile.Items.Version;
                    if (_profile.Items.TryGet(flight.ItemId, out _)) return result;
                    result.Success = true; Resolve(flight, ContainerTransitEndReason.Deposited, flight.Deposited); return result;
                }
                int count = item.Composition.CopyTo(_amounts);
                if (count == 0)
                {
                    if (!_profile.Items.TryGetLocation(item.InstanceId, out var location)
                        || !_profile.Items.TryPrepareMaterialChange(item.InstanceId, null, location, out var prepared)) return Fail(result, MaterialTransferFailure.ResourceBusy);
                    _profile.Items.CompletePrepared(prepared, true);
                }
                else
                {
                    // 大瓶分批释放，来源扣量与世界接收仍在同一个预约事务中。
                    // 预算限定 journal 压力；未接收的组分继续留在原在途实例中，下次碰撞回执重试。
                    int allCount = item.Composition.CopyTo(_all);
                    MaterialAmountOps.SplitBudget(_all, allCount, 1024, _amounts, _remainders, 0);
                    int batchCount = 0;
                    for (int i = 0; i < allCount; i++) if (_amounts[i].Quantity > 0) _amounts[batchCount++] = _amounts[i];
                    ulong total = 0; for (int i = 0; i < batchCount; i++) total += _amounts[i].Quantity;
                    bool final = total == item.Composition.Total;
                    _reservation.Configure(item, _profile.Items.Version, final);
                    // 最后一批也保留相同落地区域；气体每格容量较小，缩回瓶体半径会使尾量永久拒收。
                    result.Transfer.Failure = Deposit(flight.WorldId, flight.Position, System.Math.Max(flight.Radius, .75f), batchCount, out result.Transfer.Transferred);
                    // 满气瓶与固体堆共用容量约束；不能只扩大固体区域而让容器尾量永久卡在空壳中。
                    if(result.Transfer.Failure==MaterialTransferFailure.Capacity)flight.Radius=Math.Min(2f,Math.Max(.75f,flight.Radius)+.25f);
                    if (result.Transfer.Failure != MaterialTransferFailure.None) return result;
                    flight.Deposited += result.Transfer.Transferred;
                    if (!final) { result.Transfer.Version = _profile.Items.Version; return result; }
                }
                result.Success = true; result.Transfer.Version = _profile.Items.Version;
                Resolve(flight, ContainerTransitEndReason.Deposited, flight.Deposited); return result;
            }
            finally { _reservation.Cancel(); _deposit.Source = null; _executing = false; }
        }
        private MaterialTransferFailure Deposit(string world, Vector3 point, float radius, int count, out ulong quantity)
        {
            _deposit.WorldId = world; _deposit.Position = point; _deposit.Radius = radius; _deposit.Amounts = _amounts;
            _deposit.Count = count; _deposit.Source = _reservation; _deposit.Handled = false; _deposit.Transferred = 0;
            _deposit.Failure = MaterialTransferFailure.None;
            EventBus<MaterialWorldDepositRequestedEvent>.Publish(new MaterialWorldDepositRequestedEvent { Exchange = _deposit });
            quantity = _deposit.Transferred; return _deposit.Handled ? _deposit.Failure : MaterialTransferFailure.WorldUnavailable;
        }
        public void End(in ContainerTransitEndedEvent ended)
        {
            Flight flight = Find(ended.FlightId);
            if (ended.InventoryId != _inventoryId || flight == null || ended.WorldId != flight.WorldId || _executing
                || ended.Reason == ContainerTransitEndReason.Deposited || ended.Reason > ContainerTransitEndReason.ProxyDestroyed) return;
            // “已投放”只能由世界写入成功路径裁决；外部默认struct消息不能冒充成功销毁材料。
            if (!_profile.Items.TryGet(flight.ItemId, out var item) || !_profile.Items.TryGetLocation(flight.ItemId, out var location)
                || !_profile.Items.TryPrepareMaterialChange(flight.ItemId, null, location, out var prepared)) return;
            int count = item.Composition?.CopyTo(_all)??0; ulong quantity = item.Composition?.Total ?? item.Material?.Quantity ?? 0;
            if (item.Material != null) { _all[0] = new MaterialAmount(item.Material.MaterialKey, item.Material.Quantity); count = 1; }
            _profile.Items.CompletePrepared(prepared, true);
            for (int i = 0; i < count; i++) _lost[_all[i].MaterialKey] += _all[i].Quantity;
            GameLog.Warn($"投掷世界归属结束：{flight.Id}, {quantity} GMU, {ended.Reason}；未自动返还物品。", "ContainerUse");
            Resolve(flight, ended.Reason, quantity);
        }
        public void EndWorldTransits()
        {
            for (int i = 0; i < _flights.Length; i++) if (_flights[i].Active)
                End(new ContainerTransitEndedEvent { InventoryId = _inventoryId, WorldId = _flights[i].WorldId, FlightId = _flights[i].Id, Reason = ContainerTransitEndReason.SceneEnded });
        }
        private void Resolve(Flight flight, ContainerTransitEndReason reason, ulong quantity)
        {
            flight.Active = false; PendingFlights--;
            EventBus<ContainerTransitResolvedEvent>.Publish(new ContainerTransitResolvedEvent
            { InventoryId = _inventoryId, FlightId = flight.Id, Reason = reason, Quantity = quantity });
        }
        private Flight Find(string id)
        { for (int i = 0; i < _flights.Length; i++) if (_flights[i].Active && _flights[i].Id == id) return _flights[i]; return null; }
        private bool IsCarried(string id) => _profile.Items.TryGetLocation(id, out var location)
            && (location.Kind == ItemLocationKind.EquippedTool && location.OwnerId == _profile.ActorId
                || location.Kind == ItemLocationKind.Backpack && _profile.Items.TryGetLocation(location.OwnerId, out var bag)
                && bag.Kind == ItemLocationKind.EquippedBackpack && bag.OwnerId == _profile.ActorId);
        private ContainerUseResultEvent Result(ContainerUseKind kind, string source, string item, string world, ulong request)
            => new ContainerUseResultEvent { Kind = kind, Transfer = new MaterialTransferResultEvent { InventoryId = _inventoryId,
                SourceId = source, ItemId = item, WorldId = world, RequestId = request, Version = _profile.Items.Version } };
        private ContainerUseResultEvent Fail(ContainerUseResultEvent result, MaterialTransferFailure failure)
        { result.Success = false; result.Transfer.Failure = failure; result.Transfer.Version = _profile.Items.Version; return result; }
        private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    }
}
