using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 一名 Actor 在一个 Run 中唯一的 Owned 资源持有者。上层在重连时复用此对象；这里没有磁盘保存或重新发放入口。
    /// Authority Driver 显式推进资源，Client 只消费复制结果，不创建第二个会自动恢复资源的工作区。
    /// </summary>
    public sealed partial class OnlineOwnedWorkspace : IOwnedCastWorkspace, IDisposable
    {
        private readonly SinglePlayerProfile _profile;
        private readonly WandLoadoutService _wands;
        private readonly WandExecutionTransactionService _casts;
        private readonly ToolbeltService _toolBindings;
        private readonly SpellAttachmentService _attachments;
        // 配置服务只接收这一个工作区内部caller，不能让网络CallerId无限增长其去重字典。
        private readonly string _attachmentCallerId = Guid.NewGuid().ToString("N");
        private ulong _attachmentRequestId;
        private readonly RaidSpellEligibility _eligibility;
        private readonly Dictionary<string, uint> _itemRevisions;
        private readonly string _gameplayHash;
        private readonly bool _isPeacefulPractice;
        private readonly uint _entryInventoryVersion, _entryCurrencyVersion;
        public bool IsPeacefulPractice => _isPeacefulPractice;
        public bool IsAwayRuntime => !_isPeacefulPractice;
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
        // 0表示仍持最后选中的装备杖；1..7表示真实EquippedTool槽。选择属于存活Workspace，重连不重建。
        private uint _selectedToolSlotPlusOne;
        private string _selectedContainerItemId;
        private ContainerUseDefinition _containerDefinition;
        private string _containerWorldId;
        private OnlineGroundItemStore _groundItems;
        // 仅Runtime完成真实支撑预检后临时绑定；不属于Wire，也不由Client提供世界姿态。
        private IReadOnlyList<OnlineGroundItemStore.Placement> _preparedGroundDrop;
        private InventoryMaterialTransferService _containerCollections;
        private ContainerUseService _containerUses;
        // 同一存活Workspace只使用一份内部来源身份；连接Epoch变化不能占满旧服务的32来源槽。
        private readonly string _containerSourceId = Guid.NewGuid().ToString("N");
        private ulong _containerRequestId, _collectBudget, _pourBudget, _maximumPourBatch;
        private readonly CollectionRateAccumulator _collectRate = new CollectionRateAccumulator();
        private readonly CollectionRateAccumulator _pourRate = new CollectionRateAccumulator();
        private double _collectionPerSecond, _pourPerSecond, _gasPourMultiplier;
        private float _collectionRadius, _pourRadius, _gasDepositRadius;
        private bool _disposed, _hasTick;
        private Func<bool> _authorityCanWrite;
        private uint _lastTick;
        // 与库存/Mana/选择修订独立。这里只接受可信冻结值并随原Owner存活，不因新连接Epoch重建或补满。
        // 金币发生变动及永久Wallet提交须由各自明确权威事务推进，不能把捕获/读回当结算。
        private uint _currencyVersion;
        public string RunId { get; }
        public string PlayerId { get; }
        public ulong ActorId { get; }
        public string InventoryId { get; }
        public uint InventoryVersion => _profile.Items.Version;
        public uint SelectionVersion => _selectionVersion;
        public long Gold => _profile.Currency;
        public uint CurrencyVersion => _currencyVersion;
        public ulong CollectedGmu { get; private set; }

        /// <summary>
        /// 正式Coin世界同步调用的原子记账口：新拾币服从退出Fence，不把Fence扩展到已有输出。
        /// 余额与独立DS货币修订全部预检后一起推进；没有可能重入/抛错的事件，失败不消费金币。
        /// 此方法不写永久Wallet，网络表现沿货币修订发布；拿着工具也仍可自动拾金币。
        /// </summary>
        public bool TryCreditCoins(long amount)
        {
            if (!AuthorityWritesAllowed || !CanAcceptNewPlayerIntents || _profile.RaidActionsFrozen || amount <= 0
                || _profile.Currency < 0 || _profile.Currency > long.MaxValue - amount || _currencyVersion == uint.MaxValue) return false;
            _profile.Currency += amount; _currencyVersion++; return true;
        }
        // 检查点是独立事实口；不能复用UI过滤后假装已包含全部在途/退役，也不伪造网络Epoch。
        private IOnlineItemRetirementJournal _checkpointJournal;
        public bool ConfigureCheckpointJournal(IOnlineItemRetirementJournal journal)
        {
            if (_disposed || journal == null || _checkpointJournal != null && !ReferenceEquals(_checkpointJournal, journal)) return false;
            if (!_profile.Items.ConfigureCheckpointJournal(journal, InventoryId, CheckpointRevision)) return false;
            _checkpointJournal = journal;
            return true;
        }
        private uint CheckpointRevision(string id) => _itemRevisions.TryGetValue(id, out uint revision) ? revision : 0;
        /// <summary>
        /// 当前提交事实的独立值副本；这里只做离散捕获，调用方负责在同一主线程安全Tick拼入物理/材料事实。
        /// 已提交但仍有后续层的Ticket不是“准备中”；真正的Store锁/当前Prepare则必须拒绝，不能读半状态。
        /// Location6也在Assets中保留一次原实例，并由Transit.Item提供同值交叉证据；Shared聚合须按其合同分区。
        /// </summary>
        public bool TryCaptureCheckpoint(out OnlineOwnedCheckpointSnapshot snapshot)
        {
            snapshot = null;
            if (_disposed || _checkpointJournal == null || _profile.Items.IsResourceBusy || _casts.IsPending
                || _profile.Items.Count > OnlineOwnedAssetSnapshot.MaximumItems) return false;
            _wands.Synchronize();
            if (!_checkpointJournal.TryCapture(out var allRetired)
                || !_casts.MaterialOutputs.TryCaptureCheckpoint(out var outputs, out var spellLost)) return false;
            OnlineContainerTransitCheckpointSnapshot[] transits = Array.Empty<OnlineContainerTransitCheckpointSnapshot>();
            OnlineOwnedMaterialAmountSnapshot[] containerLost = Array.Empty<OnlineOwnedMaterialAmountSnapshot>();
            if (_containerUses != null && !_containerUses.TryCaptureCheckpoint(out transits, out containerLost)) return false;

            var items = new OnlineOwnedItemSnapshot[_profile.Items.Count];
            var wands = new List<OnlineInventoryWandPresentationSnapshot>();
            var uses = new List<OnlineInventorySpellUsePresentationSnapshot>();
            int at = 0, flights = 0;
            foreach (var entry in _profile.Items)
            {
                if (!TryProjectOwnerItem(entry, out bool carried) || !_itemRevisions.TryGetValue(entry.Item.InstanceId, out uint revision)
                    || revision == 0 || !_profile.Items.Catalog.TryGet(entry.Item.ContentId, out var definition)) return false;
                if (!carried)
                {
                    if (entry.Location.Kind != ItemLocationKind.ThrownContainer) return false;
                    bool found = false;
                    for (int i = 0; i < transits.Length; i++)
                        if (transits[i].ItemId == entry.Item.InstanceId && transits[i].FlightId == entry.Location.OwnerId) { found = true; break; }
                    if (!found) return false;
                    flights++;
                }
                items[at++] = CopyGroundItem(entry.Item, entry.Location, revision, _profile.Items.Catalog);
                if (definition.Kind == ItemKind.Wand)
                {
                    if (!_wands.ReadOnly.TryGetState(entry.Item.InstanceId, out var state)
                        || !_wands.ReadOnly.TryGetProgram(entry.Item.InstanceId, out var program)) return false;
                    wands.Add(new OnlineInventoryWandPresentationSnapshot { InstanceId = entry.Item.InstanceId,
                        ProgramRevision = program.ProgramRevision, CurrentMana = state.CurrentMana, CooldownRemaining = state.CooldownRemaining });
                }
                if (definition.Kind == ItemKind.Spell)
                {
                    if (!_eligibility.ReadOnly.TryGetState(entry.Item.InstanceId, out var state)) return false;
                    uses.Add(new OnlineInventorySpellUsePresentationSnapshot { InstanceId = entry.Item.InstanceId, Eligible = state.Eligible });
                }
            }
            if (flights != transits.Length) return false;
            var retired = new List<OnlineItemRetirementSnapshot>();
            for (int i = 0; i < allRetired.Length; i++)
                if (allRetired[i].InventoryId == InventoryId) retired.Add(allRetired[i]);
            snapshot = new OnlineOwnedCheckpointSnapshot
            {
                Assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
                    RunId = RunId, PlayerId = PlayerId, ActorId = ActorId, InventoryId = InventoryId, InventoryVersion = InventoryVersion,
                    GameplayConfigHash = _gameplayHash, SelectedWandInstanceId = _wands.ReadOnly.SelectedWandId ?? string.Empty,
                    Gold = Gold, CurrencyVersion = CurrencyVersion, Items = items },
                ResourceVersion = Math.Max(1u, _wands.ReadOnly.ResourceVersion), SelectionVersion = _selectionVersion,
                SpellUseVersion = Math.Max(1u, _eligibility.Version), SelectedToolSlotPlusOne = _selectedToolSlotPlusOne,
                Wands = wands.ToArray(), SpellUses = uses.ToArray(), ContainerTransits = transits,
                MaterialOutputs = outputs, ContainerLost = containerLost, SpellMaterialLost = spellLost,
                Retired = retired.ToArray(), RetirementSequence = _checkpointJournal.Sequence
            };
            return true;
        }
        public bool IsActiveAndEnabled => !_disposed;
        public bool UseSpellProgrammingV2 => true;
        private bool AuthorityWritesAllowed => !_disposed && (_authorityCanWrite == null || _authorityCanWrite());
        public bool WorldActionsAllowed => AuthorityWritesAllowed && !ResearchOpen && _selectedToolSlotPlusOne == 0;
        public IWandLoadoutReadOnly WandsReadOnly => _wands.ReadOnly;
        public ICastResourceReadOnly CastResourcesReadOnly => _casts.ReadOnly;
        public ICastResourceReadOnly V2CastResourcesReadOnly => _casts.ReadOnly;

        internal OnlineOwnedWorkspace(string runId, string playerId, ulong actorId, string inventoryId,
            SinglePlayerProfile profile, WandLoadoutService wands, WandExecutionTransactionService casts,
            RaidSpellEligibility eligibility, string gameplayHash, Dictionary<string, uint> itemRevisions, uint currencyVersion,
            bool peacefulPractice = false)
        {
            RunId = runId; PlayerId = playerId; ActorId = actorId; InventoryId = inventoryId;
            _profile = profile; _wands = wands; _casts = casts;
            _currencyVersion = currencyVersion;
            _eligibility = eligibility; _gameplayHash = gameplayHash; _itemRevisions = itemRevisions;
            _isPeacefulPractice = peacefulPractice;
            _researchSessionVersion = peacefulPractice ? 1u : 0u;
            _entryInventoryVersion = profile.Items.Version; _entryCurrencyVersion = currencyVersion;
            // 复用单机已验证的装备/交换/形状回包事务，但不把该服务内部默认Wand0的选择当作线上事实。
            // 私有固定scope只隔离其单机ToolbeltChanged通知；网络InventoryId、真实Profile和资产身份均不改变。
            // 服务没有事件订阅；随Workspace保留一次实例/字符串，Dispose无需额外退订。
            _toolBindings = new ToolbeltService(profile, inventoryId + ":owned-tool-bindings");
            _attachments = new SpellAttachmentService(profile, inventoryId);
            _casts.MaterialOutputs.ConfigureAuthorityWriteGate(CanWriteMaterialOutputs);
            EventBus<CastResourceRequestedEvent>.Subscribe(OnCastRequested);
            EventBus<WandSelectionRequestedEvent>.Subscribe(OnWandSelected);
            EventBus<SpellExecutionObservedEvent>.Subscribe(OnObserved);
            EventBus<ContainerImpactEvent>.Subscribe(OnContainerImpact);
            EventBus<ContainerTransitEndedEvent>.Subscribe(OnContainerTransitEnded);
            EventBus<SpellMaterialOutputEvent>.Subscribe(OnSpellMaterialOutput);
        }

        public bool TryGetWandExecution(string id, out WandExecutionSnapshot snapshot)
        { snapshot = default; return !_disposed && _casts.TryGetState(id, out snapshot); }
        public bool TryGetItem(string id, out RuntimeItemState item)
        { item = null; return !_disposed && _profile.Items.TryGet(id, out item); }

        /// <summary>
        /// 正式权威装配绑定世界的动态只读写入许可；每次入口重新查询，不能缓存成入场时的 true。
        /// 不绑定时保留纯工作区原行为。同一回调可重复绑定，但不能借重装配替换现场的权威来源。
        /// 暂停只阻止新玩法写入，不取消预留、不退款，也不代替明确的 Run 退出/Dispose 生命周期。
        /// </summary>
        public void ConfigureAuthorityWriteGate(Func<bool> canWrite)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OnlineOwnedWorkspace));
            if (canWrite == null) throw new ArgumentNullException(nameof(canWrite));
            if (_authorityCanWrite != null && _authorityCanWrite != canWrite)
                throw new InvalidOperationException("活跃 Owned 工作区不能替换权威写入门。");
            _authorityCanWrite = canWrite;
        }

        /// <summary>
        /// 只绑定DS受控参数和现有材料事务，不读取单机档、不创建第二套库存。参数在此冻结，
        /// 同绑定重入不重建服务/来源/预算；同一活跃Workspace不能换世界借此重置请求高水位或补充额度。
        /// </summary>
        public void ConfigureContainerActions(ContainerUseDefinition definition, string worldId)
        {
            if (_disposed) throw new InvalidOperationException("已退出的Owned工作区不能绑定容器服务。");
            // 使用已完成的世界绑定作生命周期标记；Unity资源的fake-null不能允许活跃工作区重建服务。
            if (_containerWorldId != null)
            {
                if (ReferenceEquals(_containerDefinition, definition) && _containerWorldId == worldId) return;
                throw new InvalidOperationException("活跃Owned工作区不能更换容器定义或世界绑定。");
            }
            if (definition == null || string.IsNullOrWhiteSpace(worldId)
                || !PositiveFinite(definition.CollectionRate) || !PositiveFinite(definition.PourRate)
                || !PositiveFinite(definition.GasPourMultiplier) || definition.GasPourMultiplier < 1
                || !PositiveFinite(definition.CollectionRadius) || definition.CollectionRadius > .5f
                || !PositiveFinite(definition.DepositRadius) || definition.DepositRadius > 1
                || !PositiveFinite(definition.GasDepositRadius) || definition.GasDepositRadius > 1
                || definition.MaximumPourBatchGmu == 0 || definition.MaximumPourBatchGmu > 1024)
                throw new ArgumentException("容器配置需要有限正速率、原服务允许半径及不超过1024GMU的投放批次。");
            _collectionPerSecond = definition.CollectionRate; _pourPerSecond = definition.PourRate;
            _gasPourMultiplier = definition.GasPourMultiplier; _collectionRadius = definition.CollectionRadius;
            _pourRadius = definition.DepositRadius; _gasDepositRadius = definition.GasDepositRadius;
            _maximumPourBatch = definition.MaximumPourBatchGmu;
            _containerCollections = new InventoryMaterialTransferService(_profile, InventoryId);
            _containerUses = new ContainerUseService(_profile, InventoryId, CanWriteContainerActions);
            _containerDefinition = definition; _containerWorldId = worldId;
        }

        private bool CanWriteContainerActions() => AuthorityWritesAllowed && !ResearchOpen;
        private bool CanWriteMaterialOutputs() => AuthorityWritesAllowed;

        /// <summary>
        /// Character 只取得这一次原服务准备中的真实模型，不接触可写 Store，也不能伪造另一个玩家的投掷。
        /// Exchange 必须就是本服务当前的对象；Source/World/内部请求号和确认的装备瓶同时匹配。
        /// </summary>
        public bool TryGetContainerThrowPresentation(ContainerThrowExchange exchange, out string contentId, out GameObject model)
        {
            contentId = null; model = null;
            if (!AuthorityWritesAllowed || _containerUses == null || !_containerUses.IsPreparingThrow(exchange)) return false;
            var request = exchange.Request;
            if (request.InventoryId != InventoryId || request.WorldId != _containerWorldId || request.SourceId != _containerSourceId
                || request.RequestId != _containerRequestId || request.ExpectedVersion != InventoryVersion
                || !TryGetSelectedContainer(request.ItemId, (int)_selectedToolSlotPlusOne - 1, out var item)
                || !_profile.Items.Catalog.TryGet(item.ContentId, out var definition) || definition.Kind != ItemKind.Container
                || definition.WorldModel == null) return false;
            contentId = item.ContentId; model = definition.WorldModel; return true;
        }

        /// <summary>共享地面属于整个DS世界；重连只重绑同一注册表，不重建实例、版本或收据。</summary>
        public void ConfigureGroundItems(OnlineGroundItemStore ground)
        {
            if (_disposed || ground == null) throw new ArgumentException("退出工作区或空共享地面不能绑定。");
            if (_groundItems != null && !ReferenceEquals(_groundItems, ground))
                throw new InvalidOperationException("活跃工作区不能切换共享地面所属世界。");
            _groundItems = ground;
        }
        internal ItemOwnershipStore OwnershipStore => _profile.Items;
        internal sealed class GroundTransferState
        {
            internal readonly Dictionary<string, uint> Revisions = new Dictionary<string, uint>(StringComparer.Ordinal);
            internal readonly HashSet<string> Qualified = new HashSet<string>(StringComparer.Ordinal);
            internal WandSessionContinuity Wands;
        }
        internal bool TryGetCarriedItem(string id, out RuntimeItemState item, out ItemLocation location)
        {
            item = null; location = default;
            if (_disposed || !_profile.Items.TryGet(id, out item) || !_profile.Items.TryGetLocation(id, out location)
                || location.Kind == ItemLocationKind.WorldItem || location.Kind == ItemLocationKind.ThrownContainer) return false;
            var root = location;
            for (int depth = 0; root.HasParentInstance; depth++)
                if (depth >= _profile.Items.Count || !_profile.Items.TryGetLocation(root.OwnerId, out root)) return false;
            return root.OwnerId == PlayerId && (root.Kind == ItemLocationKind.EquippedWand || root.Kind == ItemLocationKind.EquippedBackpack
                || root.Kind == ItemLocationKind.EquippedTool || root.Kind == ItemLocationKind.SpellStorage);
        }
        internal bool TryCaptureGroundDeparture(string id, out GroundTransferState transfer)
        {
            transfer = null;
            if (!TryGetCarriedItem(id, out var item, out _) || item.EntryBoundBackpack) return false;
            var next = new GroundTransferState();
            foreach (var entry in _profile.Items)
            {
                bool member = entry.Item.InstanceId == id;
                for (var parent = entry.Location; !member && parent.HasParentInstance;)
                {
                    if (parent.OwnerId == id) { member = true; break; }
                    if (!_profile.Items.TryGetLocation(parent.OwnerId, out parent)) return false;
                }
                if (!member) continue;
                if (entry.Item.EntryBoundBackpack || entry.Item.InstanceId != id
                    && (entry.Location.Kind != ItemLocationKind.WandSlot || entry.Location.OwnerId != id)
                    || !_itemRevisions.TryGetValue(entry.Item.InstanceId, out uint revision)) return false;
                next.Revisions.Add(entry.Item.InstanceId, revision);
                if (_eligibility.IsEligible(entry.Item.InstanceId)) next.Qualified.Add(entry.Item.InstanceId);
            }
            next.Wands = _wands.CaptureTransfer(next.Revisions.Keys); transfer = next; return true;
        }
        internal bool PrepareGroundAdmission(GroundTransferState transfer)
        {
            if (transfer == null || _profile.Items.Count + transfer.Revisions.Count > OnlineOwnedAssetSnapshot.MaximumItems) return false;
            foreach (var pair in transfer.Revisions) if (_itemRevisions.ContainsKey(pair.Key) || pair.Value == 0) return false;
            _itemRevisions.EnsureCapacity(_itemRevisions.Count + transfer.Revisions.Count);
            foreach (var pair in transfer.Revisions) _itemRevisions.Add(pair.Key, pair.Value);
            _eligibility.BeginIncoming(transfer.Qualified); return true;
        }
        internal void CancelGroundAdmission(GroundTransferState transfer)
        { foreach (var pair in transfer.Revisions) _itemRevisions.Remove(pair.Key); _eligibility.EndIncoming(); }
        internal void CompleteGroundAdmission(GroundTransferState transfer)
        {
            string previousWand = _wands.ReadOnly.SelectedWandId;
            _eligibility.EndIncoming(); _wands.Synchronize(); _wands.RestoreContinuity(transfer.Wands);
            SynchronizeInventorySelection(previousWand);
        }
        internal void CompleteGroundDeparture(GroundTransferState transfer)
        {
            string previousWand = _wands.ReadOnly.SelectedWandId;
            foreach (var pair in transfer.Revisions) _itemRevisions.Remove(pair.Key);
            _eligibility.ForgetTransferred(transfer.Qualified); _wands.ForgetTransferred(transfer.Revisions.Keys);
            SynchronizeInventorySelection(previousWand);
        }
        internal List<string> GroundDepartureRoots(string id, bool wholeStack)
        {
            var result = new List<string>();
            if (!TryGetCarriedItem(id, out _, out var source)) return result;
            if (wholeStack && source.Kind != ItemLocationKind.Backpack) return result;
            foreach (var entry in _profile.Items)
                if (entry.Item.InstanceId == id || wholeStack && entry.Location == source) result.Add(entry.Item.InstanceId);
            return result;
        }
        internal void PrepareGroundDrop(IReadOnlyList<OnlineGroundItemStore.Placement> placements)
        {
            if (_preparedGroundDrop != null) throw new InvalidOperationException("落地准备不能重入。");
            _preparedGroundDrop = placements ?? throw new ArgumentNullException(nameof(placements));
        }
        internal void ClearPreparedGroundDrop() => _preparedGroundDrop = null;
        private void SynchronizeInventorySelection() => SynchronizeInventorySelection(_wands.ReadOnly.SelectedWandId);
        private void SynchronizeInventorySelection(string previousWand)
        {
            _wands.Synchronize(); _toolBindings.SynchronizeWithoutNotifications();
            bool changed = previousWand != _wands.ReadOnly.SelectedWandId;
            if (_selectedToolSlotPlusOne != 0)
            {
                int slot = (int)_selectedToolSlotPlusOne - 1;
                if (!_toolBindings.TryGetBinding(slot, out var selected))
                { _selectedToolSlotPlusOne = 0; _selectedContainerItemId = null; changed = true; }
                else if (_selectedContainerItemId != selected) { _selectedContainerItemId = selected; changed = true; }
            }
            // 资产本身的选择回退也要产生可复制的版本，空手保持空值而不是创建/补发新杖。
            if (changed) { _selectionVersion++; ResetContainerBudgets(); }
        }
        internal bool TryGetCarriedContainer(string id, out RuntimeItemState item, out ItemLocation location)
        {
            item = null; location = default;
            if (_disposed || !_profile.Items.TryGet(id, out item) || !_profile.Items.TryGetLocation(id, out location)
                || item.Composition == null || !_profile.Items.Catalog.TryGetRules(item.ContentId, out var rules)
                || rules.Kind != ItemKind.Container) return false;
            if (location.Kind == ItemLocationKind.EquippedTool) return location.OwnerId == PlayerId;
            return location.Kind == ItemLocationKind.Backpack && _profile.Items.TryGetLocation(location.OwnerId, out var parent)
                && parent.Kind == ItemLocationKind.EquippedBackpack && parent.OwnerId == PlayerId;
        }
        internal bool TryGetItemRevision(string id, out uint revision) => _itemRevisions.TryGetValue(id, out revision);
        internal bool PrepareGroundAdmission(string id, uint revision, bool createsInstance = true)
        {
            if (_disposed || revision == 0 || createsInstance && _profile.Items.Count >= OnlineOwnedAssetSnapshot.MaximumItems
                || _itemRevisions.ContainsKey(id)) return false;
            // 在资产提交前完成可能扩容的元数据写入；失败会取消，整个主线程事务中不发布中间视图。
            _itemRevisions.Add(id, revision); return true;
        }
        internal void CancelGroundAdmission(string id) => _itemRevisions.Remove(id);
        internal void CompleteGroundAdmission()
        {
            _wands.Synchronize();
        }
        internal void CompleteGroundDeparture(string id)
        {
            _itemRevisions.Remove(id); _toolBindings.SynchronizeWithoutNotifications();
            if (_selectedContainerItemId == id)
                SelectWandOrEmptyHandAfterToolDeparture(_toolBindings.SelectedSlot);
            _wands.Synchronize();
        }

        /// <summary>
        /// Runtime完成真实模型绑定/移除且原命令收据落账之后调用。通知不是事务提交的一部分；
        /// 任一UI/诊断订阅者异常都不能抹掉成功收据、销毁已准备模型或让重试再迁移同一物品。
        /// </summary>
        public void NotifyGroundInventoryCommitted()
        {
            try { EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = InventoryId, Version = InventoryVersion }); }
            catch (Exception exception)
            { GameLog.Error("共享容器已提交；通知订阅者异常：" + exception.GetType().Name + ": " + exception.Message, "OnlineInventory"); }
        }

        /// <summary>
        /// 入场、重连及库存离散变化时导出当前已提交的值。不能复用业务 EntrySnapshot：本工作区可能
        /// 已消耗次数、取得本局资格或恢复过 Mana。这里只复制读数，不调用资格 Begin、施法或 Tick。
        /// 数组分配仅允许在上述离散边界；高频 Mana/CD 继续使用单独的有界资源事实。
        /// </summary>
        public bool TryCapturePresentation(uint epoch, ulong sequence, uint serverTick, out OnlineInventoryPresentationSnapshot snapshot)
        {
            snapshot = null;
            if (_disposed || epoch == 0 || sequence == 0
                || _profile.Items.Count > OnlineOwnedAssetSnapshot.MaximumItems) return false;
            _wands.Synchronize();
            OnlineWandResearchStateSnapshot research = null;
            if (ResearchOpen && !TryCaptureResearchState(out research)) return false;
            int wandCount = 0, cardCount = 0, carriedCount = 0;
            foreach (var entry in _profile.Items)
            {
                if (!TryProjectOwnerItem(entry, out bool include)) return false;
                if (!include) continue;
                carriedCount++;
                if (!_itemRevisions.ContainsKey(entry.Item.InstanceId)) return false;
                if (!_profile.Items.Catalog.TryGet(entry.Item.ContentId, out var definition)) return false;
                // 只导出当前玩家的真实随身闭包。原有法术专区也是玩家根，但仓库/地面/飞行物不是。
                // 父链来自已经通过 Store 校验的同一批实例，备用杖及其安装卡不会被压平成普通槽号。
                var root = entry.Location;
                for (int depth = 0; root.HasParentInstance; depth++)
                    if (depth >= _profile.Items.Count || !_profile.Items.TryGetLocation(root.OwnerId, out root)) return false;
                if (root.OwnerId != PlayerId || root.Kind != ItemLocationKind.EquippedWand
                    && root.Kind != ItemLocationKind.EquippedBackpack && root.Kind != ItemLocationKind.EquippedTool
                    && root.Kind != ItemLocationKind.SpellStorage) return false;
                // 装备槽仍只有三根；本Owner读数必须包含包内备用杖，供编辑器读取真实程序修订和资源。
                if (definition.Kind == ItemKind.Wand) wandCount++;
                // 资格事实按真实物品种类计数；包、瓶、工具、备用杖都不应占用 SpellUses 数组。
                if (definition.Kind == ItemKind.Spell) cardCount++;
            }
            if (wandCount > OnlineOwnedAssetSnapshot.MaximumItems || cardCount > OnlineOwnedAssetSnapshot.MaximumItems) return false;
            var items = new OnlineOwnedItemSnapshot[carriedCount];
            var wands = new OnlineInventoryWandPresentationSnapshot[wandCount];
            var uses = new OnlineInventorySpellUsePresentationSnapshot[cardCount];
            int itemIndex = 0, wandIndex = 0, cardIndex = 0;
            foreach (var entry in _profile.Items)
            {
                // 两遍使用同一严格谓词，不能只缩短数组或仅在填充时漏掉在途行。
                if (!TryProjectOwnerItem(entry, out bool include)) return false;
                if (!include) continue;
                var item = entry.Item; var location = entry.Location;
                _profile.Items.Catalog.TryGet(item.ContentId, out var definition);
                items[itemIndex++] = new OnlineOwnedItemSnapshot
                {
                    InstanceId = item.InstanceId, ContentId = item.ContentId, Revision = _itemRevisions[item.InstanceId],
                    LocationKind = (int)location.Kind, ParentInstanceId = location.HasParentInstance ? location.OwnerId : "",
                    Slot = location.SlotIndex, X = location.X, Y = location.Y, QuarterTurns = location.QuarterTurns,
                    RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                    UseTemplateWandStats = definition.Kind == ItemKind.Wand && !item.RolledWandStats.HasValue,
                    WandStats = CopyStats(item), ContainerState = CopyComposition(item), MaterialState = CopyMaterial(item),
                    AttachmentState = CopyAttachment(item), EntryBoundBackpack = item.EntryBoundBackpack
                };
                if (definition.Kind == ItemKind.Wand)
                {
                    if (!_wands.ReadOnly.TryGetState(item.InstanceId, out var resource)
                        || !_wands.ReadOnly.TryGetProgram(item.InstanceId, out var program)) return false;
                    wands[wandIndex++] = new OnlineInventoryWandPresentationSnapshot
                    { InstanceId = item.InstanceId, ProgramRevision = program.ProgramRevision,
                        CurrentMana = resource.CurrentMana, CooldownRemaining = resource.CooldownRemaining };
                }
                if (definition.Kind == ItemKind.Spell)
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
                SelectedToolSlotPlusOne = _selectedToolSlotPlusOne, ResearchOpen = ResearchOpen, ResearchSessionVersion = ResearchSessionVersion, Research = research,
                Assets = new OnlineOwnedAssetSnapshot
                {
                    SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = RunId, PlayerId = PlayerId,
                    ActorId = ActorId, InventoryId = InventoryId, InventoryVersion = InventoryVersion, GameplayConfigHash = _gameplayHash,
                    Gold = Gold, CurrencyVersion = CurrencyVersion,
                    // 领域层以null表示空手，跨模块快照使用合同约定的空字符串。
                    // 保持Codec拒绝缺失字段，避免合法移走最后一把杖触发整个世界的发布故障。
                    SelectedWandInstanceId = _wands.ReadOnly.SelectedWandId ?? string.Empty, Items = items
                }
            };
            return true;
        }

        /// <summary>
        /// DS 唯一 Store 继续持有飞行瓶与余量；Owner UI 仅投影可交互携带位置。
        /// 位置6只有经原 Flight 的 ItemId/FlightId/WorldId 精确核实才允许不投影，坏行仍拒绝整份快照。
        /// </summary>
        private bool TryProjectOwnerItem(InventoryEntry entry, out bool include)
        {
            include = false;
            if (!_itemRevisions.ContainsKey(entry.Item.InstanceId)
                || !_profile.Items.Catalog.TryGet(entry.Item.ContentId, out var definition)
                || !_profile.Items.Catalog.TryGetRules(entry.Item.ContentId, out var rules)) return false;
            if (definition.Kind == ItemKind.Material)
            {
                if (entry.Item.Material == null || entry.Item.Material.MaterialKey != rules.MaterialKey
                    || entry.Item.Material.Quantity == 0 || entry.Item.Material.Quantity > rules.MaterialItemCapacity
                    || entry.Item.Composition != null || entry.Item.RolledWandStats.HasValue) return false;
            }
            else if (entry.Item.Material != null) return false;
            if (entry.Location.Kind == ItemLocationKind.ThrownContainer)
                return definition.Kind == ItemKind.Container && _containerUses != null
                    && _containerUses.IsTrackedFlight(entry.Item.InstanceId, entry.Location.OwnerId, _containerWorldId);
            var root = entry.Location;
            for (int depth = 0; root.HasParentInstance; depth++)
                if (depth >= _profile.Items.Count || !_profile.Items.TryGetLocation(root.OwnerId, out root)) return false;
            if (root.OwnerId != PlayerId || root.Kind != ItemLocationKind.EquippedWand
                && root.Kind != ItemLocationKind.EquippedBackpack && root.Kind != ItemLocationKind.EquippedTool
                && root.Kind != ItemLocationKind.SpellStorage) return false;
            include = true; return true;
        }

        /// <summary>
        /// 每次导出独立的组分 DTO，空瓶也明确提供 Schema=1 和空数组。不能借用入局 DTO 或领域对象，
        /// 否则网络缓存/后续快照的可变数组会获得改写当前资产的旁路。此分配只发生在离散完整快照边界。
        /// </summary>
        private static OnlineOwnedContainerStateSnapshot CopyComposition(RuntimeItemState item)
        {
            if (item.Composition == null) return null;
            var values = new MaterialAmount[8];
            int count = item.Composition.CopyTo(values);
            var components = new OnlineOwnedMaterialAmountSnapshot[count];
            for (int i = 0; i < count; i++)
                components[i] = new OnlineOwnedMaterialAmountSnapshot
                { MaterialKey = values[i].MaterialKey, Quantity = values[i].Quantity };
            return new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = components };
        }

        internal static OnlineOwnedMaterialStateSnapshot CopyMaterial(RuntimeItemState item)
            => item.Material == null ? null : new OnlineOwnedMaterialStateSnapshot
            { SchemaVersion = 1, MaterialKey = item.Material.MaterialKey, Quantity = item.Material.Quantity };

        private static OnlineOwnedSpellAttachmentStateSnapshot CopyAttachment(RuntimeItemState item)
            => item.Attachment == null ? null : new OnlineOwnedSpellAttachmentStateSnapshot
            { SchemaVersion = 1, ContainerId = item.Attachment.ContainerId, Dose = item.Attachment.Dose, Enabled = item.Attachment.Enabled };

        /// <summary>按原 Store 捕获顺序取装备包中第一份固体，与原单机 K 一致；不新增选材或整包合并。</summary>
        public bool TryGetFirstBackpackMaterial(out InventoryItemSnapshot item)
        {
            item = default; if (_disposed) return false;
            string bag = null;
            foreach (var entry in _profile.Items)
                if (entry.Location.Kind == ItemLocationKind.EquippedBackpack && entry.Location.OwnerId == PlayerId)
                { bag = entry.Item.InstanceId; break; }
            if (bag == null) return false;
            foreach (var entry in _profile.Items)
                if (entry.Location.Kind == ItemLocationKind.Backpack && entry.Location.OwnerId == bag && entry.Item.Material != null
                    && _profile.Items.Catalog.TryGet(entry.Item.ContentId, out var definition)
                    && _profile.Items.Catalog.TryGetRules(entry.Item.ContentId, out var rules) && rules.Kind == ItemKind.Material)
                { item = new InventoryItemSnapshot(entry.Item, entry.Location, definition, rules); return true; }
            return false;
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

        internal static OnlineOwnedItemSnapshot CopyGroundItem(RuntimeItemState item, ItemLocation location, uint revision, ItemCatalogIndex catalog)
        {
            catalog.TryGetRules(item.ContentId, out var rules);
            return new OnlineOwnedItemSnapshot { InstanceId = item.InstanceId, ContentId = item.ContentId, Revision = revision,
                LocationKind = (int)location.Kind, ParentInstanceId = location.HasParentInstance ? location.OwnerId : "",
                Slot = location.SlotIndex, X = location.X, Y = location.Y, QuarterTurns = location.QuarterTurns,
                RemainingCharges = item.RemainingCharges, HasUnlimitedCharges = item.HasUnlimitedCharges,
                UseTemplateWandStats = rules.Kind == ItemKind.Wand && !item.RolledWandStats.HasValue,
                WandStats = CopyStats(item), ContainerState = CopyComposition(item), MaterialState = CopyMaterial(item),
                AttachmentState = CopyAttachment(item), EntryBoundBackpack = item.EntryBoundBackpack };
        }

        public void StepAuthority(float deltaTime, uint serverTick)
        {
            if (!AuthorityWritesAllowed || ResearchPaused || !WandRuntimeState.IsFinite(deltaTime) || deltaTime <= 0
                || _hasTick && unchecked((int)(serverTick - _lastTick)) <= 0) return;
            // uint 差值支持正常回卷；同一 Tick 重入不能重复恢复 Mana 或缩短冷却。
            _hasTick = true; _lastTick = serverTick;
            _wands.Tick(deltaTime);
            if (_containerCollections != null && TryGetSelectedContainer(_selectedContainerItemId,
                (int)_selectedToolSlotPlusOne - 1, out var container))
            {
                // 权威Tick是唯一额度时钟。长帧只保留有界批次，重复/旧Tick已在上方拒绝，不给失败或重连积累债务。
                _collectBudget = Math.Min(4096UL, _collectBudget + _collectRate.Advance(_collectionPerSecond, deltaTime));
                double multiplier = ContainsGas(container) ? _gasPourMultiplier : 1;
                _pourBudget = Math.Min(_maximumPourBatch, _pourBudget + _pourRate.Advance(_pourPerSecond * multiplier, deltaTime));
            }
        }

        public CastResourceResultEvent Execute(in CastResourceRequestedEvent request)
        {
            bool canWrite = AuthorityWritesAllowed;
            // 旧结果是已发生的事实。只读查询原账本，不能为了重放调用可能创建预留/提交的新请求入口。
            if (!canWrite && !_disposed && request.UseV2 && request.InventoryId == InventoryId
                && _casts.TryGetRecordedRequest(request, out var recorded)) return recorded;
            if (!canWrite || ResearchOpen || _selectedToolSlotPlusOne != 0 || !request.UseV2 || request.InventoryId != InventoryId)
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
            => ProcessInventoryCommand(intent, OnlineInventoryCommandFailure.None, default);

        /// <summary>上下文只由DS组合层生成；不参与意图指纹，重试时Actor移动或Frozen也只回原最终收据。</summary>
        public OnlineInventoryCommandReceipt ExecuteInventoryCommand(in OnlineInventoryCommandIntent intent,
            in OnlineContainerAuthorityContext context)
            => ProcessInventoryCommand(intent, OnlineInventoryCommandFailure.None, context);

        /// <summary>
        /// 外层发现死亡、施法忙、冻结或限流时也必须经过此入口记录拒绝。若直接在 Driver 返回 Busy，
        /// 相同 CommandId 在忙结束后重试就可能变成成功，破坏“一次意图只有一个结果”的约定。
        /// 此方法同样先查原收据；不能用当前 Busy 覆盖以前已成功提交的结果。
        /// </summary>
        public OnlineInventoryCommandReceipt RejectInventoryCommand(in OnlineInventoryCommandIntent intent,
            OnlineInventoryCommandFailure failure)
            => ProcessInventoryCommand(intent, failure == OnlineInventoryCommandFailure.None
                ? OnlineInventoryCommandFailure.Rejected : failure, default);

        /// <summary>
        /// 世界模型准备前只读查询同一账本。精确重放/冲突/窗口外旧ID均给原规则结果；不推进Epoch、
        /// 高水位或资产，因此已被队友拾取的旧Drop重放不会重新创建模型或再执行Physics准备。
        /// </summary>
        public bool TryGetRecordedInventoryCommand(in OnlineInventoryCommandIntent intent, out OnlineInventoryCommandReceipt receipt)
        {
            receipt = default;
            if (intent.CommandId == 0 || intent.ConnectionEpoch == 0 || intent.ConnectionEpoch > _inventoryCommandEpoch) return false;
            if (intent.ConnectionEpoch < _inventoryCommandEpoch)
            { receipt = InventoryReceipt(intent, OnlineInventoryCommandFailure.StaleCommand); return true; }
            for (int i = 0; i < _inventoryCommandCount; i++)
            {
                var previous = _inventoryCommands[i];
                if (previous.Intent.CommandId != intent.CommandId) continue;
                receipt = SameInventoryCommand(previous.Intent, intent) ? previous.Receipt
                    : InventoryReceipt(intent, OnlineInventoryCommandFailure.CommandConflict);
                return true;
            }
            if (intent.CommandId > _latestInventoryCommand) return false;
            receipt = InventoryReceipt(intent, OnlineInventoryCommandFailure.StaleCommand); return true;
        }

        private OnlineInventoryCommandReceipt ProcessInventoryCommand(in OnlineInventoryCommandIntent intent,
            OnlineInventoryCommandFailure forcedFailure, in OnlineContainerAuthorityContext context)
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
            ulong transferred = 0;
            MaterialTransferFailure transferFailure = MaterialTransferFailure.None;
            // 原账本去重已经完成；新请求即使来自直接调用或 Reject 路径，也必须服从同一动态门。
            // 仍按原窗口记录拒绝，不推进物品/选择/资源版本，不把暂停当成取消现场事务。
            if (!AuthorityWritesAllowed || !CanAcceptNewPlayerIntents) failure = OnlineInventoryCommandFailure.NotReady;
            else if (failure == OnlineInventoryCommandFailure.None)
            {
                if (IsResearchCommand(intent.Kind)) failure = ApplyResearchSessionCommand(intent);
                else if (ResearchOpen && !IsResearchStructuralCommand(intent.Kind)) failure = OnlineInventoryCommandFailure.Busy;
                else if (_isPeacefulPractice && (RequiresResearchEditAccess(intent) || ResearchOpen && IsResearchStructuralCommand(intent.Kind))
                    && (!ResearchOpen || _homeResearchAccess == null || !_homeResearchAccess())) failure = OnlineInventoryCommandFailure.NotOwned;
                else failure = ApplyInventoryCommand(intent, context, out transferred, out transferFailure);
            }
            var receipt = InventoryReceipt(intent, failure);
            receipt.Transferred = transferred; receipt.TransferFailure = transferFailure;
            _inventoryCommands[_inventoryCommandNext] = new InventoryCommandRecord { Intent = intent, Receipt = receipt };
            _inventoryCommandNext = (_inventoryCommandNext + 1) % InventoryCommandReceiptWindow;
            if (_inventoryCommandCount < InventoryCommandReceiptWindow) _inventoryCommandCount++;
            _latestInventoryCommand = intent.CommandId;
            return receipt;
        }

        private OnlineInventoryCommandFailure ApplyInventoryCommand(in OnlineInventoryCommandIntent intent,
            in OnlineContainerAuthorityContext context, out ulong transferred, out MaterialTransferFailure transferFailure)
        {
            transferred = 0; transferFailure = MaterialTransferFailure.None;
            if (intent.ExpectedInventoryVersion == 0 || string.IsNullOrWhiteSpace(intent.ItemId)
                || intent.TargetSlot < 0 || intent.TargetSlot > 255)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (intent.Kind != OnlineInventoryCommandKind.ConfigureSpellAttachment
                && (!string.IsNullOrEmpty(intent.AttachmentContainerId) || intent.AttachmentDose != 0 || intent.AttachmentEnabled))
                return OnlineInventoryCommandFailure.InvalidInput;
            if ((intent.Kind == OnlineInventoryCommandKind.SelectWand || intent.Kind == OnlineInventoryCommandKind.PlaceCard
                || intent.Kind == OnlineInventoryCommandKind.SelectTool || intent.Kind == OnlineInventoryCommandKind.BindTool)
                && (intent.TransferBudget != 0 || !Zero(intent.AimOrigin) || !Zero(intent.AimDirection)))
                return OnlineInventoryCommandFailure.InvalidInput;
            if (intent.ExpectedInventoryVersion != InventoryVersion) return OnlineInventoryCommandFailure.StaleVersion;
            if (_profile.RaidActionsFrozen || _profile.Items.IsResourceBusy || _casts.IsPending || _casts.ActiveReservations > 0)
                return OnlineInventoryCommandFailure.Busy;
            _wands.Synchronize();
            bool genericGround = intent.Kind == OnlineInventoryCommandKind.DropItem || intent.Kind == OnlineInventoryCommandKind.PickupItem;
            bool groundCommand = genericGround || intent.Kind == OnlineInventoryCommandKind.DropContainer || intent.Kind == OnlineInventoryCommandKind.PickupContainer;
            if (genericGround) return TransferGroundItem(intent, context, out transferred);
            if (intent.Kind == OnlineInventoryCommandKind.MoveOwned || intent.Kind == OnlineInventoryCommandKind.EquipWand)
                return MoveOwnedItem(intent);
            if (intent.TargetLocationKind != 0 || intent.WholeStack) return OnlineInventoryCommandFailure.InvalidInput;
            if (!groundCommand && (!string.IsNullOrEmpty(intent.WorldOwnerId) || !string.IsNullOrEmpty(intent.TargetBackpackId)
                || intent.GridX != 0 || intent.GridY != 0 || intent.QuarterTurns != 0 || intent.AutoPlace))
                return OnlineInventoryCommandFailure.InvalidInput;
            if (groundCommand) return TransferGroundContainer(intent, context, out transferred);
            if (intent.Kind == OnlineInventoryCommandKind.ConfigureSpellAttachment) return ConfigureSpellAttachment(intent);
            if (intent.Kind == OnlineInventoryCommandKind.SelectWand) return SelectInventoryWand(intent);
            if (intent.Kind == OnlineInventoryCommandKind.SelectTool) return SelectInventoryTool(intent);
            if (intent.Kind == OnlineInventoryCommandKind.BindTool) return BindInventoryTool(intent);
            if (intent.Kind == OnlineInventoryCommandKind.ThrowContainer) return ThrowContainer(intent, context);
            if (intent.Kind == OnlineInventoryCommandKind.ReleaseSolid) return ReleaseSolid(intent, context, out transferred, out transferFailure);
            if (intent.Kind == OnlineInventoryCommandKind.CollectMaterial || intent.Kind == OnlineInventoryCommandKind.PourContainer)
                return TransferContainer(intent, context, out transferred, out transferFailure);
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
            else if (source.Kind == ItemLocationKind.SpellStorage && source.OwnerId == PlayerId
                || source.Kind == ItemLocationKind.Backpack && TryGetCarriedItem(intent.ItemId, out _, out _))
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
            var result = InventoryMoveOps.TryPlaceSpell(_profile.Items, intent.ItemId, target, intent.ExpectedInventoryVersion);
            if (!result.Success) return InventoryFailure(result.Reason);
            _wands.Synchronize();
            if (InventoryVersion != before)
                EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = InventoryId, Version = InventoryVersion });
            return OnlineInventoryCommandFailure.None;
        }

        private OnlineInventoryCommandFailure ValidateMoveSource(in OnlineInventoryCommandIntent intent)
        {
            if (!TryGetCarriedItem(intent.ItemId, out _, out var source)) return OnlineInventoryCommandFailure.NotOwned;
            if (source.Kind == ItemLocationKind.WandSlot)
            {
                if (intent.SourceWandId != source.OwnerId) return OnlineInventoryCommandFailure.NotOwned;
                if (!MatchesProgram(source.OwnerId, intent.SourceProgramRevision)) return OnlineInventoryCommandFailure.StaleProgram;
            }
            else if (!string.IsNullOrEmpty(intent.SourceWandId) || intent.SourceProgramRevision != 0)
                return OnlineInventoryCommandFailure.InvalidInput;
            return OnlineInventoryCommandFailure.None;
        }
        internal OnlineInventoryCommandFailure ResolveItemTarget(in OnlineInventoryCommandIntent intent, out ItemLocation target)
        {
            target = default;
            var kind = (ItemLocationKind)intent.TargetLocationKind;
            if (intent.TargetLocationKind < 1 || intent.TargetLocationKind > 8 || kind == ItemLocationKind.WorldItem || kind == ItemLocationKind.ThrownContainer)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (kind == ItemLocationKind.Backpack)
            {
                if (string.IsNullOrEmpty(intent.TargetBackpackId) || !_profile.Items.TryGetLocation(intent.TargetBackpackId, out var bag)
                    || bag.Kind != ItemLocationKind.EquippedBackpack || bag.OwnerId != PlayerId)
                    return OnlineInventoryCommandFailure.NotOwned;
                if (!string.IsNullOrEmpty(intent.TargetWandId) || intent.TargetProgramRevision != 0 || intent.TargetSlot != 0)
                    return OnlineInventoryCommandFailure.InvalidInput;
                target = new ItemLocation(kind, intent.TargetBackpackId, intent.GridX, intent.GridY, intent.QuarterTurns, -1);
            }
            else
            {
                if (!string.IsNullOrEmpty(intent.TargetBackpackId) || intent.GridX != 0 || intent.GridY != 0 || intent.QuarterTurns != 0)
                    return OnlineInventoryCommandFailure.InvalidInput;
                if (kind == ItemLocationKind.WandSlot)
                {
                    if (!TryGetCarriedItem(intent.TargetWandId, out var wand, out _) || !_profile.Items.Catalog.TryGetRules(wand.ContentId, out var rules)
                        || rules.Kind != ItemKind.Wand) return OnlineInventoryCommandFailure.NotOwned;
                    if (!MatchesProgram(intent.TargetWandId, intent.TargetProgramRevision)) return OnlineInventoryCommandFailure.StaleProgram;
                    target = new ItemLocation(kind, intent.TargetWandId, 0, 0, 0, intent.TargetSlot);
                }
                else
                {
                    if (!string.IsNullOrEmpty(intent.TargetWandId) || intent.TargetProgramRevision != 0) return OnlineInventoryCommandFailure.InvalidInput;
                    target = new ItemLocation(kind, PlayerId, 0, 0, 0, intent.TargetSlot);
                }
            }
            return OnlineInventoryCommandFailure.None;
        }
        private OnlineInventoryCommandFailure MoveOwnedItem(in OnlineInventoryCommandIntent intent)
        {
            if (intent.TransferBudget != 0 || !Zero(intent.AimOrigin) || !Zero(intent.AimDirection) || intent.ToSpellStorage || intent.AutoPlace
                || !string.IsNullOrEmpty(intent.WorldOwnerId)) return OnlineInventoryCommandFailure.InvalidInput;
            var failure = ValidateMoveSource(intent); if (failure != OnlineInventoryCommandFailure.None) return failure;
            failure = ResolveItemTarget(intent, out var target); if (failure != OnlineInventoryCommandFailure.None) return failure;
            bool equip = intent.Kind == OnlineInventoryCommandKind.EquipWand;
            if (equip ? target.Kind != ItemLocationKind.EquippedWand || intent.WholeStack
                : target.Kind != ItemLocationKind.Backpack && target.Kind != ItemLocationKind.WandSlot
                    && target.Kind != ItemLocationKind.SpellStorage && target.Kind != ItemLocationKind.EquippedTool)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (target.Kind == ItemLocationKind.EquippedTool)
                return BindOwnedTool(intent);
            _profile.Items.TryGetLocation(intent.ItemId, out var source);
            if (intent.WholeStack && (source.Kind != ItemLocationKind.Backpack || target.Kind != ItemLocationKind.Backpack))
                return OnlineInventoryCommandFailure.InvalidInput;
            uint before = InventoryVersion;
            var result = equip ? LoadoutOps.TryEquipWand(_profile.Items, intent.ItemId, PlayerId, intent.TargetSlot, intent.ExpectedInventoryVersion)
                : target.Kind == ItemLocationKind.WandSlot || target.Kind == ItemLocationKind.SpellStorage
                    ? InventoryMoveOps.TryPlaceSpell(_profile.Items, intent.ItemId, target, intent.ExpectedInventoryVersion)
                    : intent.WholeStack ? ItemStackOps.TryMoveStack(_profile.Items, intent.ItemId, target, intent.ExpectedInventoryVersion)
                    : _profile.Items.TryMove(intent.ItemId, target, intent.ExpectedInventoryVersion);
            if (!result.Success) return InventoryFailure(result.Reason);
            SynchronizeInventorySelection();
            if (InventoryVersion != before) NotifyGroundInventoryCommitted();
            return OnlineInventoryCommandFailure.None;
        }
        private OnlineInventoryCommandFailure BindOwnedTool(in OnlineInventoryCommandIntent intent)
        {
            if (intent.WholeStack || !ToolbeltService.IsUsable(_profile, intent.ItemId)) return OnlineInventoryCommandFailure.NotOwned;
            uint before = InventoryVersion;
            if (!_toolBindings.TryBind(intent.TargetSlot, intent.ItemId, _toolBindings.ConfigurationVersion))
                return _toolBindings.LastFailure == ToolbeltFailure.BackpackFull ? OnlineInventoryCommandFailure.NoCapacity : OnlineInventoryCommandFailure.Rejected;
            SynchronizeInventorySelection();
            if (InventoryVersion != before) NotifyGroundInventoryCommitted();
            return OnlineInventoryCommandFailure.None;
        }
        private OnlineInventoryCommandFailure TransferGroundItem(in OnlineInventoryCommandIntent intent,
            in OnlineContainerAuthorityContext context, out ulong transferred)
        {
            transferred = 0;
            if (_groundItems == null || !context.CanAdvance || context.WorldId != _groundItems.WorldId) return OnlineInventoryCommandFailure.NotReady;
            if (intent.TransferBudget != 0 || !Zero(intent.AimOrigin) || !Zero(intent.AimDirection) || intent.ToSpellStorage)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (intent.Kind == OnlineInventoryCommandKind.DropItem)
            {
                if (intent.TargetLocationKind != 0 || !string.IsNullOrEmpty(intent.WorldOwnerId) || !string.IsNullOrEmpty(intent.TargetBackpackId)
                    || !string.IsNullOrEmpty(intent.TargetWandId) || intent.TargetProgramRevision != 0 || intent.TargetSlot != 0
                    || intent.GridX != 0 || intent.GridY != 0 || intent.QuarterTurns != 0 || intent.AutoPlace)
                    return OnlineInventoryCommandFailure.InvalidInput;
                var source = ValidateMoveSource(intent); if (source != OnlineInventoryCommandFailure.None) return source;
                // Runtime先准备每个成员的模型和合法姿态；纯Workspace单根入口仍可用于无Physics规则测试。
                if (intent.WholeStack)
                {
                    var roots = GroundDepartureRoots(intent.ItemId, true);
                    if (_preparedGroundDrop == null || roots.Count != _preparedGroundDrop.Count || roots.Count == 0)
                        return OnlineInventoryCommandFailure.InvalidInput;
                    for (int i = 0; i < roots.Count; i++) if (_preparedGroundDrop[i].ItemId != roots[i]) return OnlineInventoryCommandFailure.InvalidInput;
                    return _groundItems.DropMany(this, _preparedGroundDrop);
                }
                return _groundItems.Drop(this, intent.ItemId, context.TargetPosition, context.TargetRotation);
            }
            if (intent.WholeStack || !string.IsNullOrEmpty(intent.SourceWandId) || intent.SourceProgramRevision != 0)
                return OnlineInventoryCommandFailure.InvalidInput;
            return _groundItems.Pickup(this, intent, context.ActorPosition, out transferred);
        }

        private OnlineInventoryCommandFailure TransferGroundContainer(in OnlineInventoryCommandIntent intent,
            in OnlineContainerAuthorityContext context, out ulong transferred)
        {
            transferred = 0;
            if (_groundItems == null || !context.CanAdvance || context.WorldId != _groundItems.WorldId)
                return OnlineInventoryCommandFailure.NotReady;
            if (intent.TargetSlot != 0 || intent.TransferBudget != 0 || !Zero(intent.AimOrigin) || !Zero(intent.AimDirection)
                || intent.ToSpellStorage || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision != 0 || intent.TargetProgramRevision != 0)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (intent.Kind == OnlineInventoryCommandKind.DropContainer)
            {
                if (!string.IsNullOrEmpty(intent.WorldOwnerId) || !string.IsNullOrEmpty(intent.TargetBackpackId)
                    || intent.GridX != 0 || intent.GridY != 0 || intent.QuarterTurns != 0 || intent.AutoPlace)
                    return OnlineInventoryCommandFailure.InvalidInput;
                if (!TryGetCarriedContainer(intent.ItemId, out _, out _)) return OnlineInventoryCommandFailure.NotOwned;
                return _groundItems.Drop(this, intent.ItemId, context.TargetPosition, context.TargetRotation);
            }
            return _groundItems.Pickup(this, intent, context.ActorPosition, out transferred);
        }

        /// <summary>
        /// 只编辑真实 Owned 发射卡的不可变设置。新来源必须是装备包内瓶；工具瓶不支持供料。
        /// 设置本身不扣量；未来源失效也不自动删除配置，Partial/Empty/InvalidSource由原执行事务裁决。
        /// </summary>
        private OnlineInventoryCommandFailure ConfigureSpellAttachment(in OnlineInventoryCommandIntent intent)
        {
            if (intent.TargetSlot != 0 || intent.TransferBudget != 0 || !Zero(intent.AimOrigin) || !Zero(intent.AimDirection)
                || intent.ToSpellStorage || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision != 0 || intent.TargetProgramRevision != 0 || _attachmentRequestId == ulong.MaxValue)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (intent.AttachmentEnabled
                ? string.IsNullOrWhiteSpace(intent.AttachmentContainerId) || intent.AttachmentDose == 0
                : !string.IsNullOrEmpty(intent.AttachmentContainerId) || intent.AttachmentDose != 0)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (!_profile.Items.TryGet(intent.ItemId, out var card) || !_profile.Items.TryGetLocation(intent.ItemId, out var location)
                || !TryProjectOwnerItem(new InventoryEntry(card, location), out bool include) || !include)
                return OnlineInventoryCommandFailure.NotOwned;
            if (!_profile.Items.Catalog.TryGet(card.ContentId, out var definition) || definition.Kind != ItemKind.Spell
                || definition.SpellDefinition == null || definition.SpellDefinition.Kind != Game.Skills.SpellKind.Emit
                    && definition.SpellDefinition.Kind != Game.Skills.SpellKind.StaticProjectile)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (intent.AttachmentEnabled)
            {
                if (!_profile.Items.TryGet(intent.AttachmentContainerId, out var source) || source.Composition == null
                    || !_profile.Items.Catalog.TryGetRules(source.ContentId, out var rules) || rules.Kind != ItemKind.Container
                    || !_profile.Items.TryGetLocation(source.InstanceId, out var sourceLocation) || sourceLocation.Kind != ItemLocationKind.Backpack
                    || !_profile.Items.TryGetLocation(sourceLocation.OwnerId, out var bag) || bag.Kind != ItemLocationKind.EquippedBackpack
                    || bag.OwnerId != PlayerId) return OnlineInventoryCommandFailure.NotOwned;
            }
            var result = _attachments.Execute(new SpellAttachmentRequestedEvent
            {
                InventoryId = InventoryId, SpellId = intent.ItemId, ContainerId = intent.AttachmentContainerId,
                Dose = intent.AttachmentDose, Enabled = intent.AttachmentEnabled, CallerId = _attachmentCallerId,
                RequestId = ++_attachmentRequestId, ExpectedVersion = intent.ExpectedInventoryVersion
            });
            if (!result.Success) return InventoryFailure(result.Reason);
            _wands.Synchronize(); return OnlineInventoryCommandFailure.None;
        }

        private OnlineInventoryCommandFailure SelectInventoryWand(in OnlineInventoryCommandIntent intent)
        {
            if (intent.ToSpellStorage || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision != 0 || intent.TargetProgramRevision != 0 || intent.TargetSlot > 2)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (!IsOwnedEquippedWand(intent.ItemId, out var location) || location.SlotIndex != intent.TargetSlot)
                return OnlineInventoryCommandFailure.NotOwned;
            return TrySelectWand(intent.TargetSlot) ? OnlineInventoryCommandFailure.None : OnlineInventoryCommandFailure.Rejected;
        }

        private OnlineInventoryCommandFailure SelectInventoryTool(in OnlineInventoryCommandIntent intent)
        {
            if (intent.ToSpellStorage || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision != 0 || intent.TargetProgramRevision != 0 || intent.TargetSlot >= HeldItemSlots.ToolCount)
                return OnlineInventoryCommandFailure.InvalidInput;
            // 此命令只选择既有绑定容器，不移动物品，也不改变后续F/H可接受包内容器的原领域规则。
            if (!_profile.Items.TryGet(intent.ItemId, out var item) || !_profile.Items.TryGetLocation(intent.ItemId, out var location)
                || location.Kind != ItemLocationKind.EquippedTool || location.OwnerId != PlayerId || location.SlotIndex != intent.TargetSlot
                || !ToolbeltService.IsUsable(_profile, intent.ItemId))
                return OnlineInventoryCommandFailure.NotOwned;
            uint selected = (uint)intent.TargetSlot + 1;
            if (_selectedToolSlotPlusOne != selected || _selectedContainerItemId != intent.ItemId)
            {
                _selectedToolSlotPlusOne = selected; _selectedContainerItemId = intent.ItemId;
                _selectionVersion++; ResetContainerBudgets();
            }
            return OnlineInventoryCommandFailure.None;
        }

        private OnlineInventoryCommandFailure BindInventoryTool(in OnlineInventoryCommandIntent intent)
        {
            if (intent.ToSpellStorage || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision != 0 || intent.TargetProgramRevision != 0 || intent.TargetSlot >= HeldItemSlots.ToolCount)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (!_profile.Items.TryGet(intent.ItemId, out var item)
                || !_profile.Items.Catalog.TryGet(item.ContentId, out var definition) || definition.Kind != ItemKind.Container
                || !ToolbeltService.IsUsable(_profile, intent.ItemId))
                return OnlineInventoryCommandFailure.NotOwned;

            int selectedSlot = (int)_selectedToolSlotPlusOne - 1;
            string previousSelected = null;
            if (selectedSlot >= 0)
            {
                _toolBindings.TryGetBinding(selectedSlot, out previousSelected);
                _toolBindings.TrySelect(HeldItemSlots.WandCount + selectedSlot);
            }
            // Wire的InventoryVersion已在共同入口检查。内部配置版本由真实服务持有，不能误传客户端库存计数。
            if (!_toolBindings.TryBind(intent.TargetSlot, intent.ItemId, _toolBindings.ConfigurationVersion))
            {
                switch (_toolBindings.LastFailure)
                {
                    case ToolbeltFailure.BackpackFull: return OnlineInventoryCommandFailure.NoCapacity;
                    case ToolbeltFailure.StaleVersion: return OnlineInventoryCommandFailure.StaleVersion;
                    case ToolbeltFailure.Busy: return OnlineInventoryCommandFailure.Busy;
                    case ToolbeltFailure.InvalidSlot: return OnlineInventoryCommandFailure.InvalidInput;
                    case ToolbeltFailure.NotCarried:
                    case ToolbeltFailure.InvalidKind: return OnlineInventoryCommandFailure.NotOwned;
                    default: return OnlineInventoryCommandFailure.Rejected;
                }
            }
            // 绑定不自动选择目标槽；比较当前槽提交前后的真实ID。换入/交换当前槽才改变线上选择版本，
            // 非当前槽的装备和同槽重复绑定均保留已有手持与可用F/H额度。
            if (selectedSlot >= 0)
            {
                _toolBindings.TryGetBinding(selectedSlot, out var currentSelected);
                if (currentSelected != previousSelected)
                {
                    if (currentSelected == null)
                    {
                        // 当前瓶移到另一空槽后，原服务已按装备杖0..2执行SelectFallback。
                        // 同步这个真实回退，不追着瓶跳槽，也不能导出指向空工具槽的选择；
                        // 共享TrySelectWand统一清工具模式/额度，并且只推进一次线上SelectionVersion。
                        SelectWandOrEmptyHandAfterToolDeparture(_toolBindings.SelectedSlot);
                    }
                    else
                    {
                        _selectedContainerItemId = currentSelected;
                        _selectionVersion++; ResetContainerBudgets();
                    }
                }
            }
            return OnlineInventoryCommandFailure.None;
        }

        private bool TrySelectWand(int slot)
        {
            string previous = _wands.ReadOnly.SelectedWandId;
            if (!_wands.TrySelect(slot)) return false;
            // 即使重选同一根最后法杖，退出工具模式也是真实选择变化，必须推进共享选择版本。
            bool changed = _selectedToolSlotPlusOne != 0 || previous != _wands.ReadOnly.SelectedWandId;
            _selectedToolSlotPlusOne = 0;
            _selectedContainerItemId = null;
            if (changed) { _selectionVersion++; ResetContainerBudgets(); }
            return true;
        }

        /// <summary>
        /// 仅用于物品归属已经提交的离手收尾。显式选杖失败仍须保持原选择，但原瓶已投出/换槽时，
        /// 没有装备杖是合法空手结果：不能把失败的选杖当成继续选中已空工具槽的理由。
        /// 统一同步只清已失效的真实绑定并推进一次SelectionVersion，不补发物品、不把包内备用杖自动装备。
        /// </summary>
        private void SelectWandOrEmptyHandAfterToolDeparture(int preferredSlot)
        {
            if (!TrySelectWand(preferredSlot)) SynchronizeInventorySelection();
        }

        private OnlineInventoryCommandFailure TransferContainer(in OnlineInventoryCommandIntent intent,
            in OnlineContainerAuthorityContext context, out ulong transferred, out MaterialTransferFailure transferFailure)
        {
            transferred = 0; transferFailure = MaterialTransferFailure.None;
            if (_containerCollections == null || _containerUses == null || !context.CanAdvance)
                return OnlineInventoryCommandFailure.NotReady;
            if (context.WorldId != _containerWorldId || intent.TransferBudget == 0 || intent.ToSpellStorage
                || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision != 0 || intent.TargetProgramRevision != 0
                || (byte)context.PreparationFailure > (byte)MaterialTransferFailure.IncompatibleMaterial
                || !Finite(context.ActorPosition) || !Finite(context.TargetPosition) || !Finite(intent.AimOrigin) || !Finite(intent.AimDirection))
                return OnlineInventoryCommandFailure.InvalidInput;
            if (!TryGetSelectedContainer(intent.ItemId, intent.TargetSlot, out var container))
                return OnlineInventoryCommandFailure.NotOwned;
            bool collect = intent.Kind == OnlineInventoryCommandKind.CollectMaterial;
            ulong available = collect ? _collectBudget : _pourBudget;
            if (intent.TransferBudget > available) return OnlineInventoryCommandFailure.RateLimited;
            if (_containerRequestId == ulong.MaxValue) return OnlineInventoryCommandFailure.InvalidInput;
            // 进入数量事务就消费请求额度，不按实际接受量退款；无handler、几何拒绝和容量失败都不能积攒重试洪峰。
            if (collect) _collectBudget -= intent.TransferBudget; else _pourBudget -= intent.TransferBudget;
            // 可信DS未采到材料或达到采样上限时没有有效目标；记录原失败并消费本次额度，
            // 不能以默认落点调用World。外层账本仍保存此最终结果，后续相同意图只重放收据。
            if (context.PreparationFailure != MaterialTransferFailure.None)
            {
                transferFailure = context.PreparationFailure;
                return TransferFailure(transferFailure);
            }
            ulong request = ++_containerRequestId;
            if (collect)
            {
                var result = _containerCollections.Execute(new MaterialTransferRequestedEvent
                {
                    InventoryId = InventoryId, WorldId = _containerWorldId, SourceId = _containerSourceId,
                    ItemId = intent.ItemId, RequestId = request, Budget = intent.TransferBudget, ExpectedVersion = intent.ExpectedInventoryVersion,
                    CollectorPosition = context.ActorPosition, SampleCenter = context.TargetPosition, Radius = _collectionRadius
                });
                transferFailure = result.Failure; transferred = result.Success ? result.Transferred : 0;
                // 只在原采集事务真正提交的这一次累加。上层统一ledger已提前返回重放收据，
                // 因而重发、失败与倾倒都不会再次增加成功出局进度；进度不从包内余额差分推测。
                if (result.Success)
                    CollectedGmu = result.Transferred > ulong.MaxValue - CollectedGmu
                        ? ulong.MaxValue : CollectedGmu + result.Transferred;
            }
            else
            {
                var result = _containerUses.Execute(new ContainerUseRequestedEvent
                {
                    InventoryId = InventoryId, WorldId = _containerWorldId, SourceId = _containerSourceId,
                    ItemId = intent.ItemId, RequestId = request, Budget = intent.TransferBudget, ExpectedVersion = intent.ExpectedInventoryVersion,
                    Kind = ContainerUseKind.Pour, ActorPosition = context.ActorPosition, TargetPosition = context.TargetPosition,
                    Radius = ContainsGas(container) ? _gasDepositRadius : _pourRadius
                });
                transferFailure = result.Transfer.Failure; transferred = result.Success ? result.Transfer.Transferred : 0;
            }
            return TransferFailure(transferFailure);
        }

        private OnlineInventoryCommandFailure ReleaseSolid(in OnlineInventoryCommandIntent intent,
            in OnlineContainerAuthorityContext context, out ulong transferred, out MaterialTransferFailure transferFailure)
        {
            transferred = 0; transferFailure = MaterialTransferFailure.None;
            if (_containerUses == null || !context.CanAdvance) return OnlineInventoryCommandFailure.NotReady;
            if (context.WorldId != _containerWorldId || intent.TransferBudget != 0 || intent.TargetSlot != 0
                || !Zero(intent.AimOrigin) || !Zero(intent.AimDirection) || intent.ToSpellStorage
                || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision != 0 || intent.TargetProgramRevision != 0
                || context.PreparationFailure != MaterialTransferFailure.None
                || !Finite(context.ActorPosition) || !Finite(context.TargetPosition) || _containerRequestId == ulong.MaxValue)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (!TryGetFirstBackpackMaterial(out var material) || material.InstanceId != intent.ItemId)
                return OnlineInventoryCommandFailure.NotOwned;
            // K 沿原服务固定100GMU。客户端只给当前首材身份，不能选择另一包内材料或扩大单次预算。
            var result = _containerUses.Execute(new ContainerUseRequestedEvent
            {
                InventoryId = InventoryId, WorldId = _containerWorldId, SourceId = _containerSourceId,
                ItemId = material.InstanceId, RequestId = ++_containerRequestId, ExpectedVersion = intent.ExpectedInventoryVersion,
                Kind = ContainerUseKind.ReleaseSolid, Budget = 100, ActorPosition = context.ActorPosition,
                TargetPosition = context.TargetPosition, Radius = _pourRadius
            });
            transferFailure = result.Transfer.Failure; transferred = result.Success ? result.Transfer.Transferred : 0;
            if (result.Success && !_profile.Items.TryGet(material.InstanceId, out _)) _itemRevisions.Remove(material.InstanceId);
            return TransferFailure(transferFailure);
        }

        private OnlineInventoryCommandFailure ThrowContainer(in OnlineInventoryCommandIntent intent,
            in OnlineContainerAuthorityContext context)
        {
            if (_containerUses == null || !context.CanAdvance) return OnlineInventoryCommandFailure.NotReady;
            if (context.WorldId != _containerWorldId || intent.TransferBudget != 0 || intent.ToSpellStorage
                || !string.IsNullOrEmpty(intent.SourceWandId) || !string.IsNullOrEmpty(intent.TargetWandId)
                || intent.SourceProgramRevision != 0 || intent.TargetProgramRevision != 0
                || context.PreparationFailure != MaterialTransferFailure.None
                || !Finite(context.ActorPosition) || !Finite(context.ThrowOrigin) || !Finite(context.ThrowVelocity)
                || !Finite(intent.AimOrigin) || !Finite(intent.AimDirection)
                || (context.ThrowOrigin - context.ActorPosition).sqrMagnitude > 9 || _containerRequestId == ulong.MaxValue)
                return OnlineInventoryCommandFailure.InvalidInput;
            if (!TryGetSelectedContainer(intent.ItemId, intent.TargetSlot, out _)) return OnlineInventoryCommandFailure.NotOwned;
            // Wire 命令号属于连接 Epoch；原服务则始终使用本工作区稳定 Source 与单调内部序号。
            // 统一库存账本已先完成重放检查，重复命令不会再次准备代理或迁移同一瓶。
            var result = _containerUses.Execute(new ContainerUseRequestedEvent
            {
                InventoryId = InventoryId, WorldId = _containerWorldId, SourceId = _containerSourceId,
                ItemId = intent.ItemId, RequestId = ++_containerRequestId, ExpectedVersion = intent.ExpectedInventoryVersion,
                Kind = ContainerUseKind.Throw, ActorPosition = context.ActorPosition, TargetPosition = context.ThrowOrigin,
                ThrowVelocity = context.ThrowVelocity, Radius = _pourRadius
            });
            if (!result.Success) return TransferFailure(result.Transfer.Failure);
            // 资产仍在同 Store 的在途位置。只清已经失效的手持绑定，不从别处复制/补发替代瓶。
            int lastWandSlot = 0;
            if (_profile.Items.TryGetLocation(_wands.ReadOnly.SelectedWandId, out var lastWand)
                && lastWand.Kind == ItemLocationKind.EquippedWand) lastWandSlot = lastWand.SlotIndex;
            _toolBindings.SynchronizeWithoutNotifications();
            SelectWandOrEmptyHandAfterToolDeparture(lastWandSlot);
            return OnlineInventoryCommandFailure.None;
        }

        private bool TryGetSelectedContainer(string id, int toolSlot, out RuntimeItemState container)
        {
            container = null;
            return toolSlot >= 0 && toolSlot < HeldItemSlots.ToolCount
                && _selectedToolSlotPlusOne == (uint)toolSlot + 1 && id == _selectedContainerItemId
                && id != null && _profile.Items.TryGet(id, out container) && container.Composition != null
                && _profile.Items.TryGetLocation(id, out var location) && location.Kind == ItemLocationKind.EquippedTool
                && location.OwnerId == PlayerId && location.SlotIndex == toolSlot
                && _profile.Items.Catalog.TryGet(container.ContentId, out var definition) && definition.Kind == ItemKind.Container;
        }
        private static bool ContainsGas(RuntimeItemState item) => item.Composition.GetQuantity(7) != 0 || item.Composition.GetQuantity(8) != 0;
        private void ResetContainerBudgets() { _collectBudget = _pourBudget = 0; _collectRate.Reset(); _pourRate.Reset(); }
        private static bool PositiveFinite(float value) => float.IsFinite(value) && value > 0;
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Zero(Vector3 value) => value.x == 0 && value.y == 0 && value.z == 0;
        private static bool SameVector(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        private static OnlineInventoryCommandFailure TransferFailure(MaterialTransferFailure failure)
        {
            switch (failure)
            {
                case MaterialTransferFailure.None: return OnlineInventoryCommandFailure.None;
                case MaterialTransferFailure.InvalidRequest: return OnlineInventoryCommandFailure.InvalidInput;
                case MaterialTransferFailure.StaleVersion: return OnlineInventoryCommandFailure.StaleVersion;
                case MaterialTransferFailure.WrongOwnership: return OnlineInventoryCommandFailure.NotOwned;
                case MaterialTransferFailure.ResourceBusy: return OnlineInventoryCommandFailure.Busy;
                case MaterialTransferFailure.Capacity: return OnlineInventoryCommandFailure.NoCapacity;
                default: return OnlineInventoryCommandFailure.Rejected;
            }
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
                && a.TransferBudget == b.TransferBudget && SameVector(a.AimOrigin, b.AimOrigin) && SameVector(a.AimDirection, b.AimDirection)
                && a.Kind == b.Kind && a.ExpectedInventoryVersion == b.ExpectedInventoryVersion
                && a.SourceProgramRevision == b.SourceProgramRevision && a.TargetProgramRevision == b.TargetProgramRevision
                && a.ItemId == b.ItemId && (a.SourceWandId ?? "") == (b.SourceWandId ?? "")
                && (a.TargetWandId ?? "") == (b.TargetWandId ?? "") && a.TargetSlot == b.TargetSlot
                && a.ToSpellStorage == b.ToSpellStorage
                && a.TargetLocationKind == b.TargetLocationKind && a.WholeStack == b.WholeStack
                && (a.WorldOwnerId ?? "") == (b.WorldOwnerId ?? "") && (a.TargetBackpackId ?? "") == (b.TargetBackpackId ?? "")
                && a.GridX == b.GridX && a.GridY == b.GridY && a.QuarterTurns == b.QuarterTurns && a.AutoPlace == b.AutoPlace
                && (a.AttachmentContainerId ?? "") == (b.AttachmentContainerId ?? "")
                && a.AttachmentDose == b.AttachmentDose && a.AttachmentEnabled == b.AttachmentEnabled;

        private void OnCastRequested(CastResourceRequestedEvent request)
        {
            // 未命中 InventoryId 的服务不回复；否则另一名玩家的拒绝收据会抢先结束同步请求。
            if (_disposed || request.InventoryId != InventoryId) return;
            EventBus<CastResourceResultEvent>.Publish(Execute(request));
        }
        private void OnWandSelected(WandSelectionRequestedEvent request)
        {
            if (!AuthorityWritesAllowed || request.InventoryId != InventoryId) return;
            TrySelectWand(request.SlotIndex);
        }
        private void OnObserved(SpellExecutionObservedEvent observation)
        // Observe 只记录已提交输出的释放事实并收集已完成 ticket；冻结不应丢弃这些事实。
        { if (!_disposed && observation.InventoryId == InventoryId) _casts.Observe(observation); }

        private void OnSpellMaterialOutput(SpellMaterialOutputEvent request)
        {
            if (!AuthorityWritesAllowed || request.InventoryId != InventoryId || !_casts.MaterialOutputs.OwnsOutput(request.OutputId)) return;
            var result = _casts.MaterialOutputs.Execute(request);
            // 输出只归原服务持有；重试原OutputId不能再次从瓶里取料。已提交结果通知也不能撤销真实材料事实。
            try { EventBus<SpellMaterialOutputResultEvent>.Publish(result); }
            catch (Exception exception)
            { GameLog.Error("供料输出结果已确定，但表现通知失败：" + exception.Message, "SpellMaterials"); }
        }

        private void OnContainerImpact(ContainerImpactEvent impact)
        {
            if (!AuthorityWritesAllowed || _containerUses == null || impact.InventoryId != InventoryId
                || impact.WorldId != _containerWorldId
                || !_containerUses.TryGetTrackedFlightItem(impact.FlightId, impact.WorldId, out var itemId)) return;
            uint before = InventoryVersion;
            var result = _containerUses.Impact(impact);
            CompleteContainerFlightMutation(itemId, before);
            PublishContainerUseResult(result);
        }
        private void OnContainerTransitEnded(ContainerTransitEndedEvent ended)
        {
            if (!AuthorityWritesAllowed || _containerUses == null || ended.InventoryId != InventoryId
                || ended.WorldId != _containerWorldId
                || !_containerUses.TryGetTrackedFlightItem(ended.FlightId, ended.WorldId, out var itemId)) return;
            uint before = InventoryVersion;
            _containerUses.End(ended);
            CompleteContainerFlightMutation(itemId, before);
        }
        private void CompleteContainerFlightMutation(string itemId, uint previousVersion)
        {
            if (!_profile.Items.TryGet(itemId, out _)) _itemRevisions.Remove(itemId);
            if (previousVersion == InventoryVersion) return;
            // 部分成功同样已经消耗实际组分；不能等到整瓶结束才发布 Owner 库存版本变化。
            try { EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = InventoryId, Version = InventoryVersion }); }
            catch (Exception exception)
            { GameLog.Error("在途容器资源已提交，但库存通知失败：" + exception.Message, "ContainerUse"); }
        }
        private static void PublishContainerUseResult(ContainerUseResultEvent result)
        {
            try { EventBus<ContainerUseResultEvent>.Publish(result); }
            catch (Exception exception)
            { GameLog.Error("在途容器结果已确定，但通知失败：" + exception.Message, "ContainerUse"); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EventBus<CastResourceRequestedEvent>.Unsubscribe(OnCastRequested);
            EventBus<WandSelectionRequestedEvent>.Unsubscribe(OnWandSelected);
            EventBus<SpellExecutionObservedEvent>.Unsubscribe(OnObserved);
            EventBus<ContainerImpactEvent>.Unsubscribe(OnContainerImpact);
            EventBus<ContainerTransitEndedEvent>.Unsubscribe(OnContainerTransitEnded);
            EventBus<SpellMaterialOutputEvent>.Unsubscribe(OnSpellMaterialOutput);
            // 退出工作区先取消尚未提交的 Reservation，再结束已归属的材料代理；不发布单机结算事件。
            _casts.CancelAll(); _casts.MaterialOutputs.EndAll();
        }
    }
}
