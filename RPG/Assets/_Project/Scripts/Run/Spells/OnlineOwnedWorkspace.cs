using System;
using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// 一名 Actor 在一个 Run 中唯一的 Owned 资源持有者。上层在重连时复用此对象；这里没有磁盘保存或重新发放入口。
    /// Authority Driver 显式推进资源，Client 只消费复制结果，不创建第二个会自动恢复资源的工作区。
    /// </summary>
    public sealed class OnlineOwnedWorkspace : IOwnedCastWorkspace, IDisposable
    {
        private readonly SinglePlayerProfile _profile;
        private readonly WandLoadoutService _wands;
        private readonly WandExecutionTransactionService _casts;
        private bool _disposed, _hasTick;
        private uint _lastTick;
        public string RunId { get; }
        public string PlayerId { get; }
        public ulong ActorId { get; }
        public string InventoryId { get; }
        public uint InventoryVersion => _profile.Items.Version;
        public bool IsActiveAndEnabled => !_disposed;
        public bool UseSpellProgrammingV2 => true;
        public bool WorldActionsAllowed => !_disposed;
        public IWandLoadoutReadOnly WandsReadOnly => _wands.ReadOnly;
        public ICastResourceReadOnly CastResourcesReadOnly => _casts.ReadOnly;
        public ICastResourceReadOnly V2CastResourcesReadOnly => _casts.ReadOnly;

        internal OnlineOwnedWorkspace(string runId, string playerId, ulong actorId, string inventoryId,
            SinglePlayerProfile profile, WandLoadoutService wands, WandExecutionTransactionService casts)
        {
            RunId = runId; PlayerId = playerId; ActorId = actorId; InventoryId = inventoryId;
            _profile = profile; _wands = wands; _casts = casts;
            EventBus<CastResourceRequestedEvent>.Subscribe(OnCastRequested);
            EventBus<WandSelectionRequestedEvent>.Subscribe(OnWandSelected);
            EventBus<SpellExecutionObservedEvent>.Subscribe(OnObserved);
        }

        public bool TryGetWandExecution(string id, out WandExecutionSnapshot snapshot)
        { snapshot = default; return !_disposed && _casts.TryGetState(id, out snapshot); }
        public bool TryGetItem(string id, out RuntimeItemState item)
        { item = null; return !_disposed && _profile.Items.TryGet(id, out item); }

        public void StepAuthority(float deltaTime, uint serverTick)
        {
            if (_disposed || !WandRuntimeState.IsFinite(deltaTime) || deltaTime <= 0
                || _hasTick && unchecked((int)(serverTick - _lastTick)) <= 0) return;
            // uint 差值支持正常回卷；同一 Tick 重入不能重复恢复 Mana 或缩短冷却。
            _hasTick = true; _lastTick = serverTick;
            _wands.Tick(deltaTime);
        }

        public CastResourceResultEvent Execute(in CastResourceRequestedEvent request)
        {
            if (_disposed || !request.UseV2 || request.InventoryId != InventoryId)
                return new CastResourceResultEvent { InventoryId = request.InventoryId, SourceWandId = request.SourceWandId,
                    CallerId = request.CallerId, RequestId = request.RequestId, TicketId = request.TicketId,
                    Operation = request.Operation, Reason = OwnedWandFailureReason.InvalidRequest };
            uint before = InventoryVersion;
            var result = _casts.Execute(request);
            if (before != InventoryVersion)
                EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = InventoryId, Version = InventoryVersion });
            return result;
        }

        private void OnCastRequested(CastResourceRequestedEvent request)
        {
            // 未命中 InventoryId 的服务不回复；否则另一名玩家的拒绝收据会抢先结束同步请求。
            if (_disposed || request.InventoryId != InventoryId) return;
            EventBus<CastResourceResultEvent>.Publish(Execute(request));
        }
        private void OnWandSelected(WandSelectionRequestedEvent request)
        {
            if (!_disposed && request.InventoryId == InventoryId) _wands.TrySelect(request.SlotIndex);
        }
        private void OnObserved(SpellExecutionObservedEvent observation)
        { if (!_disposed && observation.InventoryId == InventoryId) _casts.Observe(observation); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EventBus<CastResourceRequestedEvent>.Unsubscribe(OnCastRequested);
            EventBus<WandSelectionRequestedEvent>.Unsubscribe(OnWandSelected);
            EventBus<SpellExecutionObservedEvent>.Unsubscribe(OnObserved);
            // 退出工作区先取消尚未提交的 Reservation，再结束已归属的材料代理；不发布单机结算事件。
            _casts.CancelAll(); _casts.MaterialOutputs.EndAll();
        }
    }
}
