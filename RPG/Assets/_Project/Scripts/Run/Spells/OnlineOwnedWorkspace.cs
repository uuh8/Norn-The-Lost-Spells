using System;
using System.Collections.Generic;
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
        private readonly RaidSpellEligibility _eligibility;
        private readonly Dictionary<string, uint> _itemRevisions;
        private readonly string _gameplayHash;
        public const int InventoryCommandReceiptWindow = 128;
        // 固定容量 Ring 在工作区创建时分配，随 Actor 存活而不是随 Client 连接重建。窗口外只保留高水位，
        // 因此旧命令即使已淘汰，也不会再次改资产；新 Epoch 仅清收据，绝不重建余额或本局资格。
        private readonly InventoryCommandRecord[] _inventoryCommands = new InventoryCommandRecord[InventoryCommandReceiptWindow];
        private int _inventoryCommandCount, _inventoryCommandNext;
        private uint _inventoryCommandEpoch;
        private ulong _latestInventoryCommand;
        private struct InventoryCommandRecord
        {
            internal OnlineInventoryCommandIntent Intent;
            internal OnlineInventoryCommandReceipt Receipt;
        }
        private uint _selectionVersion = 1;
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
            SinglePlayerProfile profile, WandLoadoutService wands, WandExecutionTransactionService casts,
            RaidSpellEligibility eligibility, string gameplayHash, Dictionary<string, uint> itemRevisions)
        {
            RunId = runId; PlayerId = playerId; ActorId = actorId; InventoryId = inventoryId;
            _profile = profile; _wands = wands; _casts = casts;
            _eligibility = eligibility; _gameplayHash = gameplayHash; _itemRevisions = itemRevisions;
            EventBus<CastResourceRequestedEvent>.Subscribe(OnCastRequested);
            EventBus<WandSelectionRequestedEvent>.Subscribe(OnWandSelected);
            EventBus<SpellExecutionObservedEvent>.Subscribe(OnObserved);
        }

        public bool TryGetWandExecution(string id, out WandExecutionSnapshot snapshot)
        { snapshot = default; return !_disposed && _casts.TryGetState(id, out snapshot); }
        public bool TryGetItem(string id, out RuntimeItemState item)
        { item = null; return !_disposed && _profile.Items.TryGet(id, out item); }

        /// <summary>
        /// 入场、重连及库存离散变化时导出当前已提交的值。不能复用业务 EntrySnapshot：本工作区可能
        /// 已消耗次数、取得本局资格或恢复过 Mana。这里只复制读数，不调用资格 Begin、施法或 Tick。
        /// 数组分配仅允许在上述离散边界；高频 Mana/CD 继续使用单独的有界资源事实。
        /// </summary>
        public bool TryCapturePresentation(uint epoch, ulong sequence, uint serverTick, out OnlineInventoryPresentationSnapshot snapshot)
        {
            snapshot = null;
            if (_disposed || epoch == 0 || sequence == 0 || _profile.Items.Count < 1 || _profile.Items.Count > 99) return false;
            _wands.Synchronize();
            int wandCount = 0, cardCount = 0;
            foreach (var entry in _profile.Items)
            {
                if (!_itemRevisions.ContainsKey(entry.Item.InstanceId)) return false;
                if (entry.Location.Kind == ItemLocationKind.EquippedWand) wandCount++;
                else if (entry.Location.Kind == ItemLocationKind.WandSlot
                    || entry.Location.Kind == ItemLocationKind.SpellStorage && entry.Location.OwnerId == PlayerId) cardCount++;
                else return false; // 当前闭包没有真实 Bag；不得用 Client 或旧平面业务槽补造父实例。
            }
            if (wandCount < 1 || wandCount > 3 || cardCount > 96) return false;
            var items = new OnlineOwnedItemSnapshot[_profile.Items.Count];
            var wands = new OnlineInventoryWandPresentationSnapshot[wandCount];
            var uses = new OnlineInventorySpellUsePresentationSnapshot[cardCount];
            int itemIndex = 0, wandIndex = 0, cardIndex = 0;
            foreach (var entry in _profile.Items)
            {
                var item = entry.Item; var location = entry.Location;
                items[itemIndex++] = new OnlineOwnedItemSnapshot
                {
                    InstanceId = item.InstanceId, ContentId = item.ContentId, Revision = _itemRevisions[item.InstanceId],
                    LocationKind = (int)location.Kind, ParentInstanceId = location.HasParentInstance ? location.OwnerId : "",
                    Slot = location.SlotIndex, X = location.X, Y = location.Y, QuarterTurns = location.QuarterTurns,
                    RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                    UseTemplateWandStats = location.Kind == ItemLocationKind.EquippedWand && !item.RolledWandStats.HasValue,
                    WandStats = CopyStats(item)
                };
                if (location.Kind == ItemLocationKind.EquippedWand)
                {
                    if (!_wands.ReadOnly.TryGetState(item.InstanceId, out var resource)
                        || !_wands.ReadOnly.TryGetProgram(item.InstanceId, out var program)) return false;
                    wands[wandIndex++] = new OnlineInventoryWandPresentationSnapshot
                    { InstanceId = item.InstanceId, ProgramRevision = program.ProgramRevision,
                        CurrentMana = resource.CurrentMana, CooldownRemaining = resource.CooldownRemaining };
                }
                else
                {
                    if (!_eligibility.ReadOnly.TryGetState(item.InstanceId, out var use)) return false;
                    uses[cardIndex++] = new OnlineInventorySpellUsePresentationSnapshot { InstanceId = item.InstanceId, Eligible = use.Eligible };
                }
            }
            snapshot = new OnlineInventoryPresentationSnapshot
            {
                SchemaVersion = OnlineInventoryPresentationSnapshot.CurrentSchemaVersion, ConnectionEpoch = epoch,
                SnapshotSequence = sequence, ServerTick = serverTick, ResourceVersion = Math.Max(1u, _wands.ReadOnly.ResourceVersion),
                SelectionVersion = _selectionVersion, SpellUseVersion = Math.Max(1u, _eligibility.Version), Wands = wands, SpellUses = uses,
                Assets = new OnlineOwnedAssetSnapshot
                {
                    SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = RunId, PlayerId = PlayerId,
                    ActorId = ActorId, InventoryId = InventoryId, InventoryVersion = InventoryVersion, GameplayConfigHash = _gameplayHash,
                    SelectedWandInstanceId = _wands.ReadOnly.SelectedWandId, Items = items
                }
            };
            return true;
        }

        private static OnlineOwnedWandStatsSnapshot CopyStats(RuntimeItemState item)
        {
            if (!item.RolledWandStats.HasValue) return null;
            var value = item.RolledWandStats.Value; var fixedCards = new string[value.AlwaysCastContentIds.Count];
            for (int i = 0; i < fixedCards.Length; i++) fixedCards[i] = value.AlwaysCastContentIds[i];
            return new OnlineOwnedWandStatsSnapshot
            {
                MaxMana = value.MaxMana, ManaRegenPerSecond = value.ManaRegenPerSecond, BaseCastDelay = value.BaseCastDelay,
                BaseReloadTime = value.BaseReloadTime, BaseSpreadDegrees = value.BaseSpreadDegrees, ProgramCapacity = value.ProgramCapacity,
                BaseDraws = value.BaseDraws, Shuffle = value.Shuffle, AlwaysCastContentIds = fixedCards
            };
        }

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

        /// <summary>
        /// 线上库存的 Authority 命令入口。调用者必须先核实当前认证 Actor/Epoch；本层只认识物品领域，
        /// 不读网络连接。去重先于当前版本/忙检查，因此原成功命令在后续施法忙时重试仍返回原成功收据。
        /// 真正移动沿用 Store 的完整候选事务和资格策略，不能先摘来源再尝试放目标。
        /// </summary>
        public OnlineInventoryCommandReceipt ExecuteInventoryCommand(in OnlineInventoryCommandIntent intent)
            => ProcessInventoryCommand(intent, OnlineInventoryCommandFailure.None);

        /// <summary>
        /// 外层发现死亡、施法忙、冻结或限流时也必须经过此入口记录拒绝。若直接在 Driver 返回 Busy，
        /// 相同 CommandId 在忙结束后重试就可能变成成功，破坏“一次意图只有一个结果”的约定。
        /// 此方法同样先查原收据；不能用当前 Busy 覆盖以前已成功提交的结果。
        /// </summary>
        public OnlineInventoryCommandReceipt RejectInventoryCommand(in OnlineInventoryCommandIntent intent,
            OnlineInventoryCommandFailure failure)
            => ProcessInventoryCommand(intent, failure == OnlineInventoryCommandFailure.None
                ? OnlineInventoryCommandFailure.Rejected : failure);

        private OnlineInventoryCommandReceipt ProcessInventoryCommand(in OnlineInventoryCommandIntent intent,
            OnlineInventoryCommandFailure forcedFailure)
        {
            if (intent.CommandId == 0 || intent.ConnectionEpoch == 0)
                return InventoryReceipt(intent, OnlineInventoryCommandFailure.InvalidInput);
            if (_inventoryCommandEpoch != 0 && intent.ConnectionEpoch < _inventoryCommandEpoch)
                return InventoryReceipt(intent, OnlineInventoryCommandFailure.StaleCommand);
            if (intent.ConnectionEpoch > _inventoryCommandEpoch)
            {
                // 只能由已认证上层传入当前代次；这里不接受来自 UI 的 Epoch 作为自行认证的依据。
                Array.Clear(_inventoryCommands, 0, _inventoryCommands.Length);
                _inventoryCommandEpoch = intent.ConnectionEpoch; _latestInventoryCommand = 0;
                _inventoryCommandCount = _inventoryCommandNext = 0;
            }
            for (int i = 0; i < _inventoryCommandCount; i++)
            {
                var previous = _inventoryCommands[i];
                if (previous.Intent.CommandId != intent.CommandId) continue;
                return SameInventoryCommand(previous.Intent, intent) ? previous.Receipt
                    : InventoryReceipt(intent, OnlineInventoryCommandFailure.CommandConflict);
            }
            if (intent.CommandId <= _latestInventoryCommand)
                return InventoryReceipt(intent, OnlineInventoryCommandFailure.StaleCommand);

            var failure = forcedFailure;
            if (_disposed) failure = OnlineInventoryCommandFailure.NotReady;
            else if (failure == OnlineInventoryCommandFailure.None)
                failure = ApplyInventoryCommand(intent);
            var receipt = InventoryReceipt(intent, failure);
            _inventoryCommands[_inventoryCommandNext] = new InventoryCommandRecord { Intent = intent, Receipt = receipt };
            _inventoryCommandNext = (_inventoryCommandNext + 1) % InventoryCommandReceiptWindow;
            if (_inventoryCommandCount < InventoryCommandReceiptWindow) _inventoryCommandCount++;
            _latestInventoryCommand = intent.CommandId;
            return receipt;
        }

        private OnlineInventoryCommandFailure ApplyInventoryCommand(in OnlineInventoryCommandIntent intent)
        {
            if (intent.ExpectedInventoryVersion == 0 || string.IsNullOrWhiteSpace(intent.ItemId)
                || intent.TargetSlot < 0 || intent.TargetSlot > 255)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (intent.ExpectedInventoryVersion != InventoryVersion) return OnlineInventoryCommandFailure.StaleVersion;
            if (_profile.RaidActionsFrozen || _profile.Items.IsResourceBusy || _casts.IsPending || _casts.ActiveReservations > 0)
                return OnlineInventoryCommandFailure.Busy;
            _wands.Synchronize();
            if (intent.Kind == OnlineInventoryCommandKind.SelectWand) return SelectInventoryWand(intent);
            if (intent.Kind != OnlineInventoryCommandKind.PlaceCard) return OnlineInventoryCommandFailure.InvalidInput;

            if (!_profile.Items.TryGet(intent.ItemId, out var item)
                || !_profile.Items.TryGetLocation(intent.ItemId, out var source)
                || !_profile.Items.Catalog.TryGet(item.ContentId, out var definition) || definition.Kind != ItemKind.Spell)
                return OnlineInventoryCommandFailure.NotOwned;
            if (source.Kind == ItemLocationKind.WandSlot)
            {
                if (intent.SourceWandId != source.OwnerId || !IsOwnedEquippedWand(source.OwnerId, out _))
                    return OnlineInventoryCommandFailure.NotOwned;
                if (!MatchesProgram(source.OwnerId, intent.SourceProgramRevision)) return OnlineInventoryCommandFailure.StaleProgram;
            }
            else if (source.Kind == ItemLocationKind.SpellStorage && source.OwnerId == PlayerId)
            {
                if (!string.IsNullOrEmpty(intent.SourceWandId) || intent.SourceProgramRevision != 0)
                    return OnlineInventoryCommandFailure.InvalidInput;
            }
            else return OnlineInventoryCommandFailure.NotOwned;

            ItemLocation target;
            if (intent.ToSpellStorage)
            {
                if (!string.IsNullOrEmpty(intent.TargetWandId) || intent.TargetProgramRevision != 0)
                    return OnlineInventoryCommandFailure.InvalidInput;
                if (intent.TargetSlot >= _profile.Items.Catalog.SpellStorageCapacity) return OnlineInventoryCommandFailure.NoCapacity;
                target = new ItemLocation(ItemLocationKind.SpellStorage, PlayerId, 0, 0, 0, intent.TargetSlot);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(intent.TargetWandId) || !IsOwnedEquippedWand(intent.TargetWandId, out _))
                    return OnlineInventoryCommandFailure.NotOwned;
                if (!MatchesProgram(intent.TargetWandId, intent.TargetProgramRevision)) return OnlineInventoryCommandFailure.StaleProgram;
                target = new ItemLocation(ItemLocationKind.WandSlot, intent.TargetWandId, 0, 0, 0, intent.TargetSlot);
            }
            uint before = InventoryVersion;
            var result = _profile.Items.TryMove(intent.ItemId, target, intent.ExpectedInventoryVersion);
            if (!result.Success) return InventoryFailure(result.Reason);
            _wands.Synchronize();
            if (InventoryVersion != before)
                EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = InventoryId, Version = InventoryVersion });
            return OnlineInventoryCommandFailure.None;
        }

        private OnlineInventoryCommandFailure SelectInventoryWand(in OnlineInventoryCommandIntent intent)
        {
            if (intent.ToSpellStorage || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision != 0 || intent.TargetProgramRevision != 0 || intent.TargetSlot > 2)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (!IsOwnedEquippedWand(intent.ItemId, out var location) || location.SlotIndex != intent.TargetSlot)
                return OnlineInventoryCommandFailure.NotOwned;
            string previous = _wands.ReadOnly.SelectedWandId;
            if (!_wands.TrySelect(intent.TargetSlot)) return OnlineInventoryCommandFailure.Rejected;
            if (previous != _wands.ReadOnly.SelectedWandId) _selectionVersion++;
            return OnlineInventoryCommandFailure.None;
        }

        private bool IsOwnedEquippedWand(string id, out ItemLocation location)
        {
            location = default;
            return _profile.Items.TryGet(id, out var item) && _profile.Items.TryGetLocation(id, out location)
                && location.Kind == ItemLocationKind.EquippedWand && location.OwnerId == PlayerId
                && _profile.Items.Catalog.TryGet(item.ContentId, out var definition) && definition.Kind == ItemKind.Wand;
        }
        private bool MatchesProgram(string id, uint revision) => revision > 0
            && _wands.ReadOnly.TryGetProgram(id, out var program) && program.ProgramRevision == revision;
        private OnlineInventoryCommandReceipt InventoryReceipt(in OnlineInventoryCommandIntent intent, OnlineInventoryCommandFailure failure)
            => new OnlineInventoryCommandReceipt { CommandId = intent.CommandId, ConnectionEpoch = intent.ConnectionEpoch,
                InventoryVersion = InventoryVersion, Failure = failure };
        private static OnlineInventoryCommandFailure InventoryFailure(InventoryFailureReason reason)
        {
            switch (reason)
            {
                case InventoryFailureReason.StaleVersion: return OnlineInventoryCommandFailure.StaleVersion;
                case InventoryFailureReason.ResourceBusy: return OnlineInventoryCommandFailure.Busy;
                case InventoryFailureReason.OutOfBounds:
                case InventoryFailureReason.Occupied:
                case InventoryFailureReason.Capacity: return OnlineInventoryCommandFailure.NoCapacity;
                case InventoryFailureReason.InvalidOwnership:
                case InventoryFailureReason.NotFound: return OnlineInventoryCommandFailure.NotOwned;
                default: return OnlineInventoryCommandFailure.Rejected;
            }
        }
        private static bool SameInventoryCommand(in OnlineInventoryCommandIntent a, in OnlineInventoryCommandIntent b)
            => a.CommandId == b.CommandId && a.ConnectionEpoch == b.ConnectionEpoch
                && a.Kind == b.Kind && a.ExpectedInventoryVersion == b.ExpectedInventoryVersion
                && a.SourceProgramRevision == b.SourceProgramRevision && a.TargetProgramRevision == b.TargetProgramRevision
                && a.ItemId == b.ItemId && (a.SourceWandId ?? "") == (b.SourceWandId ?? "")
                && (a.TargetWandId ?? "") == (b.TargetWandId ?? "") && a.TargetSlot == b.TargetSlot
                && a.ToSpellStorage == b.ToSpellStorage;

        private void OnCastRequested(CastResourceRequestedEvent request)
        {
            // 未命中 InventoryId 的服务不回复；否则另一名玩家的拒绝收据会抢先结束同步请求。
            if (_disposed || request.InventoryId != InventoryId) return;
            EventBus<CastResourceResultEvent>.Publish(Execute(request));
        }
        private void OnWandSelected(WandSelectionRequestedEvent request)
        {
            if (_disposed || request.InventoryId != InventoryId) return;
            string previous = _wands.ReadOnly.SelectedWandId;
            if (_wands.TrySelect(request.SlotIndex) && previous != _wands.ReadOnly.SelectedWandId) _selectionVersion++;
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
