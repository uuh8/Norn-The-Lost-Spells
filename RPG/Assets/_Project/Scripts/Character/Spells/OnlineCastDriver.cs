using System;
using Game.Combat;
using Game.Core;
using Game.Run;
using Game.Skills;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>
    /// 显式验收的独立值副本，不是协议或库存Source。TemplateId复用稳定ContentId；
    /// SpellStorage里的卡没有父杖，不能把玩家OwnerId误标成Wand实例。
    /// </summary>
    [Serializable]
    public sealed class OnlineCastInventoryCardDiagnostic
    {
        public string InstanceId, ParentWandInstanceId, TemplateId;
        public int LocationKind, Slot, RemainingCharges;
        public bool Unlimited;
    }

    /// <summary>
    /// 诊断中的完整实例值，包含真实背包/工具/备用杖和容器，不能把所有非法杖物品统称为卡。
    /// HasContainerState 区分“已知空瓶”和“未提供组分”；Authority DTO不带容量时标明不可用，不能猜成0容量。
    /// Components使用独立整数值数组，不暴露Source、领域组分或输入DTO的可写引用。
    /// </summary>
    [Serializable]
    public sealed class OnlineCastInventoryItemDiagnostic
    {
        public string InstanceId, TemplateId, OwnerId;
        public int LocationKind, Slot, X, Y, QuarterTurns, RemainingCharges;
        public bool Unlimited, IsSpell, HasContainerState, ContainerCapacityAvailable;
        public ulong ContainerTotal, ContainerCapacity;
        public MaterialAmount[] Components = Array.Empty<MaterialAmount>();
    }

    /// <summary>
    /// 诊断区分“本次观察Tick”和确实取得的完整库存Snapshot Tick，避免把较新观察时刻
    /// 冒充服务器资源事实的原始时间。所有数组/对象只在显式稀疏报告时创建，不接入Tick。
    /// </summary>
    [Serializable]
    public sealed class OnlineCastInventoryDiagnosticSnapshot
    {
        public string SourceKind, SelectedWandInstanceId, SelectedItemInstanceId;
        public ulong ActorId, InstalledSnapshotSequence;
        public uint ConnectionEpoch, ProbeSampleTick, ActualSnapshotTick, InventoryVersion, ResourceVersion, ProgramRevision, SelectionVersion;
        // Held是原1–0栏的零起点槽：0–2法杖，3–9工具；最后法杖ID不代表当前一定手持法杖。
        public int SelectedHeldSlot = -1;
        public bool ActualSnapshotTickAvailable;
        // 空手是合法Owner事实；此标志为false时，下面的0不是某根已选法杖的Mana/CD读数。
        public bool SelectedWandResourcesAvailable;
        public float Mana, CooldownRemaining;
        public int TotalItemCount;
        public OnlineCastInventoryItemDiagnostic[] Items = Array.Empty<OnlineCastInventoryItemDiagnostic>();
        public OnlineCastInventoryCardDiagnostic[] Cards = Array.Empty<OnlineCastInventoryCardDiagnostic>();

        /// <summary>
        /// 只映射TryCaptureInventory刚导出的权威当前值，不查询Entry、不扣费、不推进发送序列。
        /// 诊断调用传入的sequence=1只是生成一次值副本，不能报告为真实网络库存的发送序号。
        /// </summary>
        public static bool TryFromAuthoritySnapshot(OnlineInventoryPresentationSnapshot source, uint probeSampleTick,
            out OnlineCastInventoryDiagnosticSnapshot snapshot)
        {
            snapshot = null;
            if (source == null || source.Assets == null || source.ConnectionEpoch == 0 || source.Assets.ActorId == 0
                || source.Assets.Items == null
                || source.Assets.Items.Length > OnlineOwnedAssetSnapshot.MaximumItems
                || source.Wands == null || source.SpellUses == null || source.SpellUses.Length > source.Assets.Items.Length) return false;
            OnlineInventoryWandPresentationSnapshot selected = null;
            for (int i = 0; i < source.Wands.Length; i++)
                if (source.Wands[i] != null && source.Wands[i].InstanceId == source.Assets.SelectedWandInstanceId)
                { selected = source.Wands[i]; break; }
            bool hasSelectedWand = !string.IsNullOrEmpty(source.Assets.SelectedWandInstanceId);
            if (hasSelectedWand && (selected == null || selected.ProgramRevision == 0)
                || source.SelectedToolSlotPlusOne > HeldItemSlots.ToolCount) return false;
            string selectedItemId = null; int selectedHeldSlot = -1;
            // Authority值合同没有Kind字段；只有同批SpellUses明确指出哪些实例是真实法术，不能按位置猜卡。
            var spellIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < source.SpellUses.Length; i++)
                if (source.SpellUses[i] == null || string.IsNullOrWhiteSpace(source.SpellUses[i].InstanceId)
                    || !spellIds.Add(source.SpellUses[i].InstanceId)) return false;
            var identities = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var items = new OnlineCastInventoryItemDiagnostic[source.Assets.Items.Length];
            var cards = new OnlineCastInventoryCardDiagnostic[spellIds.Count]; int at = 0;
            for (int i = 0; i < source.Assets.Items.Length; i++)
            {
                var item = source.Assets.Items[i];
                if (item == null || string.IsNullOrWhiteSpace(item.InstanceId) || !identities.Add(item.InstanceId)
                    || item.ParentInstanceId == null) return false;
                // 从这份已捕获快照的真实根位置解析Held，不按数组顺序、模板或最后WandID猜当前工具。
                bool held = source.SelectedToolSlotPlusOne == 0
                    ? hasSelectedWand && item.InstanceId == selected.InstanceId && item.LocationKind == (int)ItemLocationKind.EquippedWand
                    : item.LocationKind == (int)ItemLocationKind.EquippedTool && item.Slot == (int)source.SelectedToolSlotPlusOne - 1;
                if (held)
                {
                    int capacity = source.SelectedToolSlotPlusOne == 0 ? HeldItemSlots.WandCount : HeldItemSlots.ToolCount;
                    if (selectedItemId != null || item.Slot < 0 || item.Slot >= capacity) return false;
                    selectedItemId = item.InstanceId;
                    selectedHeldSlot = source.SelectedToolSlotPlusOne == 0 ? item.Slot : HeldItemSlots.WandCount + item.Slot;
                }
                bool isSpell = spellIds.Contains(item.InstanceId);
                if (!TryCopyComponents(item.ContainerState, out var components, out ulong total)) return false;
                items[i] = new OnlineCastInventoryItemDiagnostic
                {
                    InstanceId = item.InstanceId, TemplateId = item.ContentId,
                    OwnerId = item.LocationKind == (int)ItemLocationKind.Backpack || item.LocationKind == (int)ItemLocationKind.WandSlot
                        ? item.ParentInstanceId : source.Assets.PlayerId,
                    LocationKind = item.LocationKind, Slot = item.Slot, X = item.X, Y = item.Y, QuarterTurns = item.QuarterTurns,
                    RemainingCharges = item.RemainingCharges, Unlimited = item.HasUnlimitedCharges, IsSpell = isSpell,
                    HasContainerState = item.ContainerState != null, ContainerTotal = total, Components = components,
                    ContainerCapacityAvailable = false // 该Authority值合同没有可信容量字段；诊断不去重读模板猜测。
                };
                if (!isSpell) continue;
                cards[at++] = new OnlineCastInventoryCardDiagnostic
                {
                    InstanceId = item.InstanceId, TemplateId = item.ContentId,
                    ParentWandInstanceId = item.LocationKind == (int)ItemLocationKind.WandSlot ? item.ParentInstanceId : string.Empty,
                    LocationKind = item.LocationKind, Slot = item.Slot, RemainingCharges = item.RemainingCharges,
                    Unlimited = item.HasUnlimitedCharges
                };
            }
            if (at != cards.Length || selectedItemId == null && (hasSelectedWand || source.SelectedToolSlotPlusOne != 0)) return false;
            snapshot = new OnlineCastInventoryDiagnosticSnapshot
            {
                SourceKind = "AuthorityWorkspace", ActorId = source.Assets.ActorId, ConnectionEpoch = source.ConnectionEpoch,
                ProbeSampleTick = probeSampleTick, ActualSnapshotTickAvailable = true, ActualSnapshotTick = source.ServerTick,
                InventoryVersion = source.Assets.InventoryVersion, ResourceVersion = source.ResourceVersion,
                SelectedItemInstanceId = selectedItemId, SelectedHeldSlot = selectedHeldSlot, SelectionVersion = source.SelectionVersion,
                SelectedWandInstanceId = hasSelectedWand ? selected.InstanceId : string.Empty,
                SelectedWandResourcesAvailable = hasSelectedWand, ProgramRevision = hasSelectedWand ? selected.ProgramRevision : 0,
                Mana = hasSelectedWand ? selected.CurrentMana : 0, CooldownRemaining = hasSelectedWand ? selected.CooldownRemaining : 0, Cards = cards,
                TotalItemCount = items.Length, Items = items
            };
            return true;
        }

        private static bool TryCopyComponents(OnlineOwnedContainerStateSnapshot state, out MaterialAmount[] components, out ulong total)
        {
            components = Array.Empty<MaterialAmount>(); total = 0;
            if (state == null) return true;
            if (state.SchemaVersion != 1 || state.Components == null || state.Components.Length > 8) return false;
            components = new MaterialAmount[state.Components.Length];
            ushort seen = 0;
            for (int i = 0; i < components.Length; i++)
            {
                var value = state.Components[i];
                if (value == null || !MaterialAmount.IsKnown(value.MaterialKey) || value.Quantity == 0
                    || (seen & 1 << value.MaterialKey) != 0) return false;
                try { total = checked(total + value.Quantity); }
                catch (OverflowException) { return false; }
                seen |= (ushort)(1 << value.MaterialKey);
                components[i] = new MaterialAmount(value.MaterialKey, value.Quantity);
            }
            return true;
        }
    }

    /// <summary>
    /// 在线 Wizard 的动作/Owned 装配。移动仍由原 Simulate 推进；动作作为独立 Overlay，
    /// 因而 Prediction 重放移动不会再发包、扣资源、创建投射物或重播音效。
    /// </summary>
    [DefaultExecutionOrder(25)]
    public sealed partial class OnlineCastDriver : MonoBehaviour
    {
        private WizardController _wizard;
        private SpellCaster _caster;
        private OwnedWandCastAdapter _adapter;
        private OnlineCombatDefinition _definition;
        private OnlineOwnedWorkspace _workspace;
        private OnlineInventoryReplica _inventoryReplica;
        private ItemCatalogIndex _inventoryCatalog;
        private CombatSimulationWorld _world;
        private readonly AuthorityActionTimeline _timeline = new AuthorityActionTimeline();
        private readonly OnlineCastReceipt[] _receipts = new OnlineCastReceipt[128];
        private int _receiptCursor;
        private ulong _actor, _lastCommand;
        private uint _epoch, _lastTick, _clientSequence;
        private bool _authority, _connected, _hasTick, _queued, _activeAir, _clientOwner, _waitRelease, _suppressed;
        private float _queueRemaining, _clientCooldown, _castInterval = .8f;
        private OnlineCastIntent _pending, _active;
        private WandProgramSnapshot _activeProgram;
        private OwnedWandCastContext _activeSource;
        private string _clientWand;
        private uint _clientProgramRevision;
        private uint _predictionSoundKey;
        private readonly ulong[] _soundCommands = new ulong[64];
        private readonly uint[] _soundKeys = new uint[64];
        private Vector3 _clientPendingAim;
        private Transform _presentationVisual;
        private Quaternion _baseVisualLocalRotation;
        // 这是上一Render帧实际交给模型的world rotation，不是可被Reconcile改写的物理根状态。
        // 若每帧从子节点rotation重新读起点，父节点的Authority校正已传播进来，Slerp仍会显示那次跳转。
        private Quaternion _presentedVisualWorldRotation;
        private bool _ownsVisualFacing;
        private InputAction _select1, _select2, _select3;
        // 1–3引用既有共享Action；4–0由Driver独占。数组固定七项，只在生命周期内创建一次。
        private InputAction[] _selectTools;
        private bool _focused = true, _applicationPaused;
        // F/H/G/K只在正式World完成绑定后开放；配置只取一次，重连不得重建Run预算、飞行或资产。
        private string _containerWorldId;
        private ContainerUseDefinition _containerUse;
        private float _containerCollectionRadius, _containerPourSpeed, _containerPourLift;
        private float _containerThrowSpeed, _containerThrowLift;
        private Func<ContainerThrowExchange, string, GameObject, IContainerThrowProxy> _containerThrowFactory;
        private bool _containerThrowSubscribed;
        private readonly RaycastHit[] _containerAimHits = new RaycastHit[32];
        private readonly RaycastHit[] _containerTrajectoryHits = new RaycastHit[16];
        private readonly MaterialCollectionAimExchange _containerAim = new MaterialCollectionAimExchange();
        private bool _preparingContainer;
        private OnlineContainerInput _containerInput;
        private OnlineGroundItemAuthority _groundWorld;
        private OnlineGroundItemPresentation _groundPresentation;
        private GameObject _groundCoinPrefab;
        private NearbyPickupInput _onlineNearbyInput;
        private readonly MaterialAmount[] _clientContainerParts = new MaterialAmount[8];
        public bool IsAuthority => _authority;
        // Multiplayer 只见 Core 值与基础身份，不从这里取得 Run 的可变库存/事务对象。
        public uint OwnedInventoryVersion => _workspace != null ? _workspace.InventoryVersion : 0;
        // 工具选择保留最后法杖ID，Pump需独立观察选择版本，才能推送纯工具模式变化。
        public uint OwnedInventorySelectionVersion => _workspace != null ? _workspace.SelectionVersion : 0;
        // 钱包变化不修改物品Store或Mana版本；发送端单独比较此水位，重连仍读存活Workspace。
        public uint OwnedCurrencyVersion => _workspace != null ? _workspace.CurrencyVersion : 0;
        public string SelectedOwnedWandId => _workspace != null ? _workspace.WandsReadOnly.SelectedWandId : null;
        public uint SelectedOwnedWandSlotPlusOne => _workspace != null &&
            _workspace.WandsReadOnly.TryGetState(_workspace.WandsReadOnly.SelectedWandId, out var state) ? (uint)state.SlotIndex + 1 : 0;
        public bool ClientInventoryReady => _inventoryReplica != null && _inventoryReplica.HasSnapshot && _inventoryReplica.ConnectionEpoch == _epoch;
        public int ClientInventoryCount => ClientInventoryReady ? _inventoryReplica.Source.ReadOnly.Count : 0;
        public uint ClientInventoryVersion => ClientInventoryReady ? _inventoryReplica.InventoryVersion : 0;
        public ulong ClientInventorySequence => ClientInventoryReady ? _inventoryReplica.SnapshotSequence : 0;
        public float ClientInventoryMana
        {
            get
            {
                if (!ClientInventoryReady) return 0;
                var read = _inventoryReplica.Source.WandsReadOnly;
                return read.TryGetState(read.SelectedWandId, out var state) ? state.CurrentMana : 0;
            }
        }

        /// <summary>
        /// 显式诊断复制Client实际已安装的当前Source，不保存可变接口或复用收到的旧DTO。
        /// 当前Source的Mana可能已被较新ActorState更新；必须从只读接口现取，而非克隆早前整包。
        /// 新Epoch尚未安装时拒绝旧Source。原始Snapshot Tick没有公开只读接口，明确标不可用；
        /// ProbeSampleTick仅是调用方观察时刻，不能据此推断每张卡/资源事实的原始提交Tick。
        /// </summary>
        public bool TryCaptureClientInventoryDiagnostic(uint probeSampleTick, out OnlineCastInventoryDiagnosticSnapshot snapshot)
        {
            snapshot = null;
            if (_authority || !_clientOwner || !ClientInventoryReady) return false;
            var source = _inventoryReplica.Source;
            if (source == null || source.ReadOnly.Count > OnlineOwnedAssetSnapshot.MaximumItems + OnlineGroundInventorySnapshot.MaximumItems) return false;
            var wands = source.WandsReadOnly;
            bool hasSelectedWand = !string.IsNullOrEmpty(wands.SelectedWandId);
            WandStateSnapshot selected = default; WandProgramSnapshot program = default;
            // 只有显式空Selected可以缺资源来源；非空ID却缺Program/State仍是损坏快照，不能伪装空手。
            if (hasSelectedWand && (!wands.TryGetState(wands.SelectedWandId, out selected)
                || !wands.TryGetProgram(wands.SelectedWandId, out program))) return false;
            var items = new InventoryItemSnapshot[source.ReadOnly.Count];
            int copied = source.ReadOnly.CopyItemsTo(items), count = 0;
            if (copied != items.Length) return false;
            int carried = 0;
            for (int i = 0; i < copied; i++)
            {
                // Source合并共享地面供附近UI读取；原Owner probe仍只统计随身，不能把别人的地面瓶算进携带资产。
                if (items[i].Location.Kind == ItemLocationKind.WorldItem) continue;
                carried++; if (items[i].Kind == ItemKind.Spell) count++;
            }
            if (carried > OnlineOwnedAssetSnapshot.MaximumItems) return false;
            var cards = new OnlineCastInventoryCardDiagnostic[count]; int at = 0;
            var instances = new OnlineCastInventoryItemDiagnostic[carried]; int instanceAt = 0;
            var compositionBuffer = new MaterialAmount[8];
            for (int i = 0; i < copied; i++)
            {
                var item = items[i];
                if (item.Location.Kind == ItemLocationKind.WorldItem) continue;
                var components = Array.Empty<MaterialAmount>();
                bool container = item.Kind == ItemKind.Container;
                ulong total = 0, capacity = 0;
                if (container)
                {
                    if (!source.TryCopyContainer(item.InstanceId, compositionBuffer, out int componentCount, out total, out capacity)) return false;
                    components = new MaterialAmount[componentCount];
                    Array.Copy(compositionBuffer, components, componentCount);
                }
                instances[instanceAt++] = new OnlineCastInventoryItemDiagnostic
                {
                    InstanceId = item.InstanceId, TemplateId = item.ContentId, OwnerId = item.Location.OwnerId,
                    LocationKind = (int)item.Location.Kind, Slot = item.Location.SlotIndex,
                    X = item.Location.X, Y = item.Location.Y, QuarterTurns = item.Location.QuarterTurns,
                    RemainingCharges = item.RemainingCharges, Unlimited = item.HasUnlimitedCharges, IsSpell = item.Kind == ItemKind.Spell,
                    HasContainerState = container, ContainerTotal = total, ContainerCapacity = capacity,
                    ContainerCapacityAvailable = container, Components = components
                };
                if (item.Kind != ItemKind.Spell) continue;
                cards[at++] = new OnlineCastInventoryCardDiagnostic
                {
                    InstanceId = item.InstanceId, TemplateId = item.ContentId,
                    ParentWandInstanceId = item.Location.Kind == ItemLocationKind.WandSlot ? item.Location.OwnerId : string.Empty,
                    LocationKind = (int)item.Location.Kind, Slot = item.Location.SlotIndex,
                    RemainingCharges = item.RemainingCharges, Unlimited = item.HasUnlimitedCharges
                };
            }
            snapshot = new OnlineCastInventoryDiagnosticSnapshot
            {
                SourceKind = "InstalledClientSource", ActorId = _actor, ConnectionEpoch = _epoch,
                ProbeSampleTick = probeSampleTick, ActualSnapshotTickAvailable = false,
                InstalledSnapshotSequence = _inventoryReplica.SnapshotSequence,
                InventoryVersion = source.ReadOnly.Version, ResourceVersion = wands.ResourceVersion,
                SelectedItemInstanceId = source.ToolbeltReadOnly.SelectedItemId, SelectedHeldSlot = source.ToolbeltReadOnly.SelectedSlot,
                SelectionVersion = source.ToolbeltReadOnly.SelectionVersion,
                SelectedWandInstanceId = hasSelectedWand ? selected.InstanceId : string.Empty,
                SelectedWandResourcesAvailable = hasSelectedWand, ProgramRevision = hasSelectedWand ? program.ProgramRevision : 0,
                Mana = hasSelectedWand ? selected.CurrentMana : 0, CooldownRemaining = hasSelectedWand ? selected.CooldownRemaining : 0, Cards = cards,
                TotalItemCount = instances.Length, Items = instances
            };
            return true;
        }

        /// <summary>离散导出当前权威值；重连沿用存活工作区，不从业务 EntrySnapshot 重新发放资源。</summary>
        public bool TryCaptureInventory(uint epoch, ulong sequence, uint serverTick, out OnlineInventoryPresentationSnapshot snapshot)
        {
            snapshot = null;
            if (!_authority || epoch != _epoch || _workspace == null || !_workspace.TryCapturePresentation(epoch, sequence, serverTick, out snapshot)) return false;
            if (_groundWorld != null)
            {
                snapshot.Ground = _groundWorld.Capture();
                if (snapshot.Ground == null) { snapshot = null; return false; }
            }
            return true;
        }

        /// <summary>同Run重连复用原Journal与工作区；只读捕获不从Entry重新补满资源。</summary>
        public bool ConfigureCheckpointJournal(IOnlineItemRetirementJournal journal)
            => _authority && _workspace != null && _workspace.ConfigureCheckpointJournal(journal);
        public bool TryCaptureCheckpoint(out OnlineOwnedCheckpointSnapshot snapshot)
        {
            snapshot = null;
            return _authority && _workspace != null && _workspace.TryCaptureCheckpoint(out snapshot);
        }

        /// <summary>绑定房间共享Authority，不取得其生命周期所有权；Actor销毁只能释放自己的投影。</summary>
        public bool ConfigureGroundWorld(OnlineGroundItemAuthority world)
        {
            if (!_authority || _workspace == null || world == null || world.Version == 0) return false;
            if (_groundWorld != null) return ReferenceEquals(_groundWorld, world);
            if (!world.Attach(_workspace)) return false;
            _groundWorld = world; return true;
        }

        /// <summary>
        /// 网络已验证Owner后，再由Character阻止死亡、施法Timeline及排队输入期间编辑。
        /// Busy/冻结/限流也经过Workspace的同一去重账本，不能让同ID在稍后的状态下获得第二种结果。
        /// </summary>
        public OnlineInventoryCommandReceipt ExecuteInventoryCommand(in OnlineInventoryCommandIntent intent,
            OnlineInventoryCommandFailure forcedFailure = OnlineInventoryCommandFailure.None)
        {
            if (!_authority || _workspace == null || intent.ConnectionEpoch != _epoch)
                return new OnlineInventoryCommandReceipt { CommandId = intent.CommandId, ConnectionEpoch = intent.ConnectionEpoch,
                    InventoryVersion = OwnedInventoryVersion, Failure = OnlineInventoryCommandFailure.NotReady };
            if (!_connected || _wizard.Health == null || !_wizard.Health.IsAlive) forcedFailure = OnlineInventoryCommandFailure.NotReady;
            else if (forcedFailure == OnlineInventoryCommandFailure.None && (_timeline.IsActive || _queued))
                forcedFailure = OnlineInventoryCommandFailure.Busy;
            if (forcedFailure != OnlineInventoryCommandFailure.None)
                return _workspace.RejectInventoryCommand(intent, forcedFailure);
            if (intent.Kind == OnlineInventoryCommandKind.DropContainer || intent.Kind == OnlineInventoryCommandKind.PickupContainer
                || intent.Kind == OnlineInventoryCommandKind.DropItem || intent.Kind == OnlineInventoryCommandKind.PickupItem)
                return _groundWorld != null ? _groundWorld.Execute(_workspace, transform, intent)
                    : _workspace.RejectInventoryCommand(intent, OnlineInventoryCommandFailure.NotReady);
            if (intent.Kind == OnlineInventoryCommandKind.CollectMaterial || intent.Kind == OnlineInventoryCommandKind.PourContainer
                || intent.Kind == OnlineInventoryCommandKind.ThrowContainer || intent.Kind == OnlineInventoryCommandKind.ReleaseSolid)
            {
                var context = PrepareContainerContext(intent);
                // Context不是意图指纹的一部分。Run先查原收据，再消费预算/事务，重试时几何变化也不能重做转移。
                return _workspace.ExecuteInventoryCommand(intent, context);
            }
            return _workspace.ExecuteInventoryCommand(intent);
        }

        /// <summary>DS显式绑定正式材料Adapter共有的WorldId；同配置重复调用无副作用，坏配置不回退默认值。</summary>
        public bool ConfigureContainerWorld(string worldId)
        {
            if (!_authority || _workspace == null || _definition == null || _definition.ContainerUse == null
                || string.IsNullOrWhiteSpace(worldId)) return false;
            var use = _definition.ContainerUse;
            if (_containerWorldId != null)
                return _containerWorldId == worldId && ReferenceEquals(_containerUse, use)
                    && _adapter.ConfigureMaterialAuthority(_world, worldId);
            if (!float.IsFinite(use.PourSpeed) || use.PourSpeed <= 0 || !float.IsFinite(use.PourLift) || use.PourLift < 0
                || !float.IsFinite(use.ThrowSpeed) || use.ThrowSpeed <= 0 || !float.IsFinite(use.ThrowLift) || use.ThrowLift < 0)
                return false;
            try { _workspace.ConfigureContainerActions(use, worldId); }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            if (!_adapter.ConfigureMaterialAuthority(_world, worldId)) return false;
            _containerUse = use; _containerWorldId = worldId;
            _containerCollectionRadius = use.CollectionRadius; _containerPourSpeed = use.PourSpeed; _containerPourLift = use.PourLift;
            _containerThrowSpeed = use.ThrowSpeed; _containerThrowLift = use.ThrowLift;
            return true;
        }

        /// <summary>
        /// 组合层独占输出槽与原物理代理的准备，Character只承接经过Workspace验证的模型/ContentId。
        /// 同局只注册一个工厂；重连和Actor暂时禁用都保留它，只有真实销毁才解除全局Prepare订阅。
        /// </summary>
        public bool ConfigureContainerThrowFactory(Func<ContainerThrowExchange, string, GameObject, IContainerThrowProxy> factory)
        {
            if (!_authority || _workspace == null || factory == null) return false;
            if (_containerThrowFactory != null) return _containerThrowFactory == factory;
            _containerThrowFactory = factory;
            EventBus<ContainerThrownEvent>.Subscribe(OnPrepareContainerThrow); _containerThrowSubscribed = true;
            return true;
        }

        private void OnPrepareContainerThrow(ContainerThrownEvent value)
        {
            var exchange = value.Exchange;
            if (!_authority || _workspace == null || _containerThrowFactory == null || exchange == null || exchange.Proxy != null
                || !_workspace.TryGetContainerThrowPresentation(exchange, out string contentId, out GameObject model)) return;
            // null表示输出容量/表现准备失败：Run仍拥有未提交的原瓶，不在这里Launch或取消已经提交的flight。
            var proxy = _containerThrowFactory(exchange, contentId, model);
            if (proxy != null) exchange.Proxy = proxy;
        }

        /// <summary>只构造当前已确认容器的F/H意图；组分和选择始终等权威快照，不执行单机转移事件。</summary>
        public bool RequestContainerTransfer(OnlineInventoryCommandKind kind, ulong budget, Ray ray)
        {
            if (!CanRequestSelection || _timeline.IsActive || _queued
                || _definition.ContainerUse == null || budget == 0
                || kind != OnlineInventoryCommandKind.CollectMaterial && kind != OnlineInventoryCommandKind.PourContainer
                || !FiniteContainerVector(ray.origin) || !ValidContainerDirection(ray.direction)) return false;
            if (!TryGetClientContainer(kind, out var item)) return false;
            EventBus<OnlineInventoryCommandRequestedEvent>.Publish(new OnlineInventoryCommandRequestedEvent
            {
                ActorId = _actor, CallerId = GetInstanceID(), Intent = new OnlineInventoryCommandIntent
                {
                    CommandId = OnlineInventoryCommandIds.Next(), ConnectionEpoch = _epoch,
                    ExpectedInventoryVersion = _inventoryReplica.InventoryVersion, Kind = kind, ItemId = item.InstanceId, TargetSlot = item.Location.SlotIndex,
                    TransferBudget = budget, AimOrigin = ray.origin, AimDirection = ray.direction.normalized
                }
            });
            return true;
        }

        /// <summary>G释放仅发送一次整瓶意图；Wire仍只带镜头射线，速度与起点由DS按原Definition重算。</summary>
        public bool RequestContainerThrow(Ray ray)
        {
            if (!CanUseContainerHeldInput || !FiniteContainerVector(ray.origin) || !ValidContainerDirection(ray.direction)
                || !TryGetClientContainer(OnlineInventoryCommandKind.ThrowContainer, out var item)) return false;
            EventBus<OnlineInventoryCommandRequestedEvent>.Publish(new OnlineInventoryCommandRequestedEvent
            {
                ActorId = _actor, CallerId = GetInstanceID(), Intent = new OnlineInventoryCommandIntent
                {
                    CommandId = OnlineInventoryCommandIds.Next(), ConnectionEpoch = _epoch,
                    ExpectedInventoryVersion = _inventoryReplica.InventoryVersion, Kind = OnlineInventoryCommandKind.ThrowContainer,
                    ItemId = item.InstanceId, TargetSlot = item.Location.SlotIndex, TransferBudget = 0,
                    AimOrigin = ray.origin, AimDirection = ray.direction.normalized
                }
            });
            return true;
        }

        /// <summary>
        /// K只声明当前确认的包内第一Material实例。数量上限100及落点均由DS/Run决定，
        /// 不借用F/H预算或G镜头射线；客户端也不先扣量或把模板容量当作现存材料。
        /// </summary>
        public bool RequestReleaseSolid()
        {
            if (!CanUseContainerHeldInput || !_inventoryReplica.TryGetFirstBackpackMaterial(out var item)) return false;
            EventBus<OnlineInventoryCommandRequestedEvent>.Publish(new OnlineInventoryCommandRequestedEvent
            {
                ActorId = _actor, CallerId = GetInstanceID(), Intent = new OnlineInventoryCommandIntent
                {
                    CommandId = OnlineInventoryCommandIds.Next(), ConnectionEpoch = _epoch,
                    ExpectedInventoryVersion = _inventoryReplica.InventoryVersion, Kind = OnlineInventoryCommandKind.ReleaseSolid,
                    ItemId = item.InstanceId, TransferBudget = 0, AimOrigin = Vector3.zero, AimDirection = Vector3.zero
                }
            });
            return true;
        }

        public ContainerUseDefinition ClientContainerUse => _definition != null ? _definition.ContainerUse : null;
        internal bool IsLocalContainerOwner => !_authority && _clientOwner;
        internal uint ContainerInputEpoch => _epoch;
        internal bool CanUseContainerHeldInput => isActiveAndEnabled && CanRequestSelection
            && ClientContainerUse != null && !_timeline.IsActive && !_queued;

        /// <summary>
        /// 输入和离散请求共用当前确认容器解析。只认真实EqTool绑定，背包点击不创造另一份选择状态；
        /// BindTool换物后的快照才会让此处返回新实例，UI尚未确认时仍返回原绑定。
        /// </summary>
        public bool TryGetClientContainer(OnlineInventoryCommandKind kind, out InventoryItemSnapshot item)
        {
            item = default;
            if (!IsLocalContainerOwner || !ClientToolSelected
                || kind != OnlineInventoryCommandKind.CollectMaterial && kind != OnlineInventoryCommandKind.PourContainer
                    && kind != OnlineInventoryCommandKind.ThrowContainer) return false;
            var source = _inventoryReplica.Source;
            var tools = source.ToolbeltReadOnly;
            int slot = tools.SelectedSlot - HeldItemSlots.WandCount;
            return tools.TryGetBinding(slot, out string itemId) && !string.IsNullOrEmpty(itemId) && itemId == tools.SelectedItemId
                && source.ReadOnly.TryGetView(itemId, out item) && item.Kind == ItemKind.Container && item.Rules.UseKind == ItemUseKind.Container
                && item.Location.Kind == ItemLocationKind.EquippedTool && item.Location.OwnerId == source.ReadOnly.ActorId
                && item.Location.SlotIndex == slot;
        }
        internal bool ClientContainerContainsGas(string itemId)
        {
            if (!ClientInventoryReady || !_inventoryReplica.Source.TryCopyContainer(itemId, _clientContainerParts, out int count, out _, out _))
                return false;
            for (int i = 0; i < count; i++)
                if (_clientContainerParts[i].MaterialKey >= 7 && _clientContainerParts[i].Quantity != 0) return true;
            return false;
        }
        private void BindContainerInput()
        {
            if (_containerInput == null) _containerInput = GetComponent<OnlineContainerInput>();
            if (!IsLocalContainerOwner) { _containerInput?.Configure(null); return; }
            if (_containerInput == null) _containerInput = gameObject.AddComponent<OnlineContainerInput>();
            _containerInput.Configure(this);
        }

        private void BindOnlinePickupInput()
        {
            if (!IsLocalContainerOwner) return;
            if (_onlineNearbyInput == null) _onlineNearbyInput = GetComponent<NearbyPickupInput>();
            if (_onlineNearbyInput == null) _onlineNearbyInput = gameObject.AddComponent<NearbyPickupInput>();
            // Controller由统一UI Scope在同Actor装配；列表事件处理其晚到，不重复创建另一份Controller/F行为。
            _onlineNearbyInput.ConfigureOnline(GetComponent<NearbyPickupController>());
            _onlineNearbyInput.enabled = true;
        }
        private bool SyncGroundPresentation(out string error)
        {
            error = null;
            if (!IsLocalContainerOwner || !ClientInventoryReady) return true;
            if (!(_inventoryReplica.Source is OnlineInventoryPresentationSource source))
            { error = "线上地面投影缺少已验证Source。"; return false; }
            if (_groundPresentation == null || !_groundPresentation.IsFor(source, transform))
            {
                _groundPresentation?.Dispose();
                _groundPresentation = new OnlineGroundItemPresentation(source, transform, _groundCoinPrefab);
            }
            if (_groundPresentation.Sync()) return true;
            error = "已确认地面快照无法同步自身模型代理。"; return false;
        }

        /// <summary>金币仍复用作者Prefab；晚于Owner安装的装配也会重建代理，相同引用不反复创建表现。</summary>
        public void ConfigureGroundCoinPrefab(GameObject prefab)
        {
            if (_groundCoinPrefab == prefab) return;
            _groundCoinPrefab = prefab;
            _groundPresentation?.Dispose(); _groundPresentation = null;
        }

        private OnlineContainerAuthorityContext PrepareContainerContext(in OnlineInventoryCommandIntent intent)
        {
            var context = new OnlineContainerAuthorityContext
            { WorldId = _containerWorldId, ActorPosition = transform.position, TargetPosition = transform.position };
            if (_containerWorldId == null || _containerUse == null) return context;
            context.CanAdvance = true; // Frozen/身份/死亡/施法Busy已由外层传入原收据路径，不能在此重新放行。
            if (intent.Kind == OnlineInventoryCommandKind.ReleaseSolid)
            {
                // 原K先从装备包的Store顺序取第一材料。再次核当前来源，不能让旧Source选中的后续材料越序消耗。
                if (!FiniteContainerVector(context.ActorPosition) || intent.TransferBudget != 0
                    || !intent.AimOrigin.Equals(Vector3.zero) || !intent.AimDirection.Equals(Vector3.zero))
                { context.PreparationFailure = MaterialTransferFailure.InvalidRequest; return context; }
                if (!_workspace.TryGetFirstBackpackMaterial(out var first) || first.InstanceId != intent.ItemId)
                { context.PreparationFailure = MaterialTransferFailure.WrongOwnership; return context; }
                context.TargetPosition = transform.position + transform.forward * 1.5f + Vector3.up * .25f;
                if (!FiniteContainerVector(context.TargetPosition)) context.PreparationFailure = MaterialTransferFailure.InvalidRequest;
                return context;
            }
            if (!FiniteContainerVector(context.ActorPosition) || !FiniteContainerVector(intent.AimOrigin)
                || !ValidContainerDirection(intent.AimDirection))
            { context.PreparationFailure = MaterialTransferFailure.InvalidRequest; return context; }
            if (_preparingContainer)
            { context.PreparationFailure = MaterialTransferFailure.ResourceBusy; return context; }
            if (string.IsNullOrEmpty(intent.ItemId) || !_workspace.TryGetItem(intent.ItemId, out var item) || item.Composition == null)
            { context.PreparationFailure = MaterialTransferFailure.WrongOwnership; return context; }
            _preparingContainer = true;
            try
            {
                if (intent.Kind == OnlineInventoryCommandKind.ThrowContainer)
                {
                    // 原单机先AlignWithCamera；等效使用瞄准水平投影取起点，纯垂直瞄准才保留Actor forward。
                    // 完整俯仰只参与速度，不让客户端提交速度，也不为投掷旋转权威移动根。
                    Vector3 direction = intent.AimDirection.normalized, horizontal = direction; horizontal.y = 0;
                    horizontal = horizontal.sqrMagnitude >= .001f ? horizontal.normalized : transform.forward;
                    context.ThrowOrigin = transform.position + horizontal * .8f + Vector3.up * 1.2f;
                    context.ThrowVelocity = direction * _containerThrowSpeed + Vector3.up * _containerThrowLift;
                    if (!FiniteContainerVector(context.ThrowOrigin) || !FiniteContainerVector(context.ThrowVelocity))
                        context.PreparationFailure = MaterialTransferFailure.InvalidRequest;
                    return context;
                }
                context.PreparationFailure = intent.Kind == OnlineInventoryCommandKind.CollectMaterial
                    ? PrepareCollectionTarget(intent, item.Composition.AllowedMaterials, out context.TargetPosition)
                    : PreparePourTarget(intent.AimDirection.normalized, out context.TargetPosition);
                return context;
            }
            finally { _preparingContainer = false; }
        }

        private MaterialTransferFailure PrepareCollectionTarget(in OnlineInventoryCommandIntent intent, ushort allowed, out Vector3 target)
        {
            target = transform.position;
            Vector3 direction = intent.AimDirection.normalized;
            const float reach = 3, maximumRay = 100;
            int count = Physics.RaycastNonAlloc(intent.AimOrigin, direction, _containerAimHits, maximumRay, ~0, QueryTriggerInteraction.Ignore);
            if (count == _containerAimHits.Length) return MaterialTransferFailure.SamplingLimit;
            float limit = maximumRay;
            for (int i = 0; i < count; i++)
                if (!_containerAimHits[i].collider.transform.IsChildOf(transform) && _containerAimHits[i].distance < limit)
                    limit = _containerAimHits[i].distance;
            // 保留原第三人称Ray与真实Actor的Reach球裁剪，材料Adapter决定世界中是否存在可采样格。
            // 本片不生成固体库存项，不能沿用单机“无瓶自动采固体”的分支。
            allowed = (ushort)(allowed & ~((1 << 5) | (1 << 6)));
            _containerAim.WorldId = _containerWorldId; _containerAim.RayOrigin = intent.AimOrigin; _containerAim.RayDirection = direction;
            _containerAim.ActorPosition = transform.position; _containerAim.Reach = reach; _containerAim.MaximumRayDistance = limit + .03f;
            _containerAim.SampleRadius = _containerCollectionRadius; _containerAim.AllowedMaterials = allowed;
            _containerAim.Handled = _containerAim.Found = false; _containerAim.MaterialKey = 0; _containerAim.Point = default;
            EventBus<MaterialCollectionAimRequestedEvent>.Publish(new MaterialCollectionAimRequestedEvent { Exchange = _containerAim });
            if (!_containerAim.Handled) return MaterialTransferFailure.WorldUnavailable;
            if (!_containerAim.Found) return MaterialTransferFailure.NoMaterial;
            if (!FiniteContainerVector(_containerAim.Point) || (_containerAim.Point - transform.position).sqrMagnitude > reach * reach
                || _containerAim.MaterialKey < 1 || _containerAim.MaterialKey > 8
                || (allowed & 1 << _containerAim.MaterialKey) == 0) return MaterialTransferFailure.GeometryRejected;
            target = _containerAim.Point;
            return MaterialTransferFailure.None;
        }

        private MaterialTransferFailure PreparePourTarget(Vector3 direction, out Vector3 target)
        {
            // 复用原容器48点抛物线参数，不读取DS Camera，不改变移动、TargetFacing或真实Actor朝向。
            // 原单机先AlignWithCamera再取起点；DS等效使用瞄准的水平投影，保持Actor本身不旋转。
            return OnlineContainerTrajectory.Prepare(transform, direction, _containerPourSpeed, _containerPourLift,
                _containerTrajectoryHits, null, out _, out target);
        }
        private static bool FiniteContainerVector(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool ValidContainerDirection(Vector3 direction)
            => FiniteContainerVector(direction) && float.IsFinite(direction.sqrMagnitude) && direction.sqrMagnitude >= .001f;

        private void BindSelectionInput()
        {
            UnbindSelectionInput();
            if (_authority || !_clientOwner || _definition == null || !isActiveAndEnabled) return;
            _select1 = _definition.GetWandSelectionAction(0)?.action;
            _select2 = _definition.GetWandSelectionAction(1)?.action;
            _select3 = _definition.GetWandSelectionAction(2)?.action;
            if (_select1 != null) { _select1.performed += SelectFirst; _select1.Enable(); }
            if (_select2 != null) { _select2.performed += SelectSecond; _select2.Enable(); }
            if (_select3 != null) { _select3.performed += SelectThird; _select3.Enable(); }
            if (_selectTools == null)
            {
                _selectTools = new InputAction[HeldItemSlots.ToolCount];
                for (int i = 0; i < _selectTools.Length; i++)
                {
                    string key = HeldItemSlots.KeyLabel(i + HeldItemSlots.WandCount);
                    _selectTools[i] = new InputAction("OnlineTool" + key, InputActionType.Button, "<Keyboard>/" + key);
                    _selectTools[i].performed += SelectTool;
                }
            }
            for (int i = 0; i < _selectTools.Length; i++) _selectTools[i].Enable();
        }
        private void UnbindSelectionInput()
        {
            // InputActionReference指向共享Asset，不在此Disable整个Action，避免移除一个订阅者时关掉其他原控件。
            if (_select1 != null) _select1.performed -= SelectFirst;
            if (_select2 != null) _select2.performed -= SelectSecond;
            if (_select3 != null) _select3.performed -= SelectThird;
            _select1 = _select2 = _select3 = null;
            if (_selectTools != null)
                for (int i = 0; i < _selectTools.Length; i++) _selectTools[i].Disable();
        }
        private void SelectFirst(InputAction.CallbackContext _) => RequestWandSelection(0);
        private void SelectSecond(InputAction.CallbackContext _) => RequestWandSelection(1);
        private void SelectThird(InputAction.CallbackContext _) => RequestWandSelection(2);
        private void SelectTool(InputAction.CallbackContext context)
        {
            // 共用一个回调，避免每次重绑创建捕获槽号的闭包；七项扫描只发生在按键边沿。
            if (_selectTools == null) return;
            for (int i = 0; i < _selectTools.Length; i++)
                if (ReferenceEquals(_selectTools[i], context.action)) { RequestToolSelection(i); return; }
        }
        // 选择工具/法杖只要求当前Owner库存已确认，不要求手上已经有可施法程序。
        // 因此最后一杖入包后仍可选工具；连接、Frozen、焦点、暂停与生命资格继续逐项保留。
        private bool CanRequestSelection => !_authority && _clientOwner && _definition != null && _connected && !_suppressed
            && _focused && !_applicationPaused && Application.isFocused && Time.timeScale > 0
            && ClientInventoryReady && _wizard != null && _wizard.Health != null && _wizard.Health.IsAlive;
        private bool ClientToolSelected => ClientInventoryReady
            && _inventoryReplica.Source.ToolbeltReadOnly.SelectedSlot >= HeldItemSlots.WandCount
            && _inventoryReplica.Source.ToolbeltReadOnly.SelectedSlot < HeldItemSlots.TotalCount;

        /// <summary>
        /// 读取已安装Source中的真实工具绑定，沿单机Toolbelt.IsUsable的UseKind规则允许选择普通可用物。
        /// 这里只发选择意图，不授予使用效果；F/H/G仍由TryGetClientContainer严格检查真实Container。
        /// </summary>
        public bool RequestToolSelection(int toolSlot)
        {
            if (!CanRequestSelection || toolSlot < 0 || toolSlot >= HeldItemSlots.ToolCount) return false;
            var source = _inventoryReplica.Source;
            var tools = source.ToolbeltReadOnly;
            if (!tools.TryGetBinding(toolSlot, out string itemId) || string.IsNullOrEmpty(itemId)
                || !source.ReadOnly.TryGetView(itemId, out var item) || item.Rules.UseKind == ItemUseKind.None
                || item.Location.Kind != ItemLocationKind.EquippedTool
                || item.Location.OwnerId != source.ReadOnly.ActorId || item.Location.SlotIndex != toolSlot
                || tools.SelectedSlot == toolSlot + HeldItemSlots.WandCount && tools.SelectedItemId == itemId) return false;
            PublishSelectionCommand(OnlineInventoryCommandKind.SelectTool, itemId, toolSlot);
            return true;
        }

        public bool RequestWandSelection(int slot)
        {
            if (!CanRequestSelection) return false;
            var read = _inventoryReplica.Source.WandsReadOnly;
            // 选工具时保留最后法杖ID；同ID仍需发SelectWand，待权威快照真正清除工具选择。
            if (!read.TryGetSlot(slot, out var state) || string.IsNullOrEmpty(state.InstanceId)
                || state.InstanceId == read.SelectedWandId && !ClientToolSelected) return false;
            PublishSelectionCommand(OnlineInventoryCommandKind.SelectWand, state.InstanceId, slot);
            return true;
        }
        private void PublishSelectionCommand(OnlineInventoryCommandKind kind, string itemId, int slot)
        {
            EventBus<OnlineInventoryCommandRequestedEvent>.Publish(new OnlineInventoryCommandRequestedEvent
            { ActorId = _actor, CallerId = GetInstanceID(), Intent = new OnlineInventoryCommandIntent
                { CommandId = OnlineInventoryCommandIds.Next(), ConnectionEpoch = _epoch,
                    ExpectedInventoryVersion = _inventoryReplica.InventoryVersion, Kind = kind,
                    ItemId = itemId, TargetSlot = slot } });
        }

        /// <summary>
        /// Inbox 排序前的纯领域校验入口：此时本机 Actor 可以尚未生成，不能为了验包创建组件或 Authority。
        /// 使用同一 Catalog/Source 完整检查身份、父链、空间和容器组分，校验候选立即释放；
        /// 不持有/替换当前 Source、不确认网络序号，正式安装仍负责现有 Source 的二次时序校验。
        /// </summary>
        public static bool ValidateClientInventory(OnlineCombatDefinition definition, in OnlineInventoryPresentationIdentity expected,
            OnlineInventoryPresentationSnapshot snapshot, out string error)
        {
            error = null;
            if (definition == null || definition.Catalog == null)
            { error = "缺少可信战斗目录，无法校验线上库存。"; return false; }
            if (!definition.Catalog.TryCreateIndex(out var catalog, out error)) return false;
            if (!OnlineInventoryPresentationSource.TryCreate(catalog, expected, snapshot, out var candidate, out error)) return false;
            candidate.Dispose();
            return true;
        }

        /// <summary>
        /// 已有本机 Actor 时，在队列排序前同时验证当前 Source 的版本栅栏；这里不会创建 Replica 或安装数据。
        /// 重连后旧 Source 仍可暂存供退出清理，新 Epoch 首包必须按新身份做纯结构检查，
        /// 不能拿旧 Source 的 Epoch 拒绝全部新包。正式 Install 继续执行身份/时序的二次裁决。
        /// </summary>
        public bool CanInstallClientInventory(in OnlineInventoryPresentationIdentity expected,
            OnlineInventoryPresentationSnapshot snapshot, out string error)
        {
            error = null;
            if (_authority || !_clientOwner || _definition == null || expected.ActorId != _actor || expected.ConnectionEpoch != _epoch)
            { error = "库存预检不属于当前本机角色。"; return false; }
            if (_inventoryReplica != null && _inventoryReplica.Source is OnlineInventoryPresentationSource current)
            {
                // 与 Replica.Install 的 SameActor 范围一致；新 Epoch 也不能借机改 Run/玩家/区域。
                if (current.ReadOnly.ActorId != expected.PlayerId || current.InventoryId != expected.InventoryId
                    || current.SpellUsesReadOnly.RaidId != expected.RunId || current.WorldSceneKey != expected.WorldSceneKey
                    || expected.GameplayConfigHash != _definition.GameplayConfigHash)
                { error = "库存预期身份不属于当前已安装的玩家上下文。"; return false; }
                if (current.ConnectionEpoch == expected.ConnectionEpoch) return current.CanInstall(snapshot, out error);
                if (unchecked((int)(expected.ConnectionEpoch - current.ConnectionEpoch)) < 0)
                { error = "库存预期连接代次已经过期。"; return false; }
            }
            return ValidateClientInventory(_definition, expected, snapshot, out error);
        }

        /// <summary>
        /// 网络层的 Core DTO 在 Character 内转成 Run 只读模型，CatalogIndex 也在此创建并缓存。
        /// 这条路径不绑定 OwnedWandCastAdapter 的 Authority Workspace，不会在 Client 扣费或回蓝。
        /// </summary>
        public bool InstallClientInventory(in OnlineInventoryPresentationIdentity expected,
            OnlineInventoryPresentationSnapshot snapshot, out string error)
        {
            error = null;
            if (_authority || !_clientOwner || _definition == null || expected.ActorId != _actor || expected.ConnectionEpoch != _epoch)
            { error = "库存快照不属于当前本机角色。"; return false; }
            if (_inventoryCatalog == null && !_definition.Catalog.TryCreateIndex(out _inventoryCatalog, out error)) return false;
            bool previousToolSelected = ClientToolSelected;
            EnsureInventoryReplica();
            if (!_inventoryReplica.Install(expected, snapshot, _inventoryCatalog, out error)) return false;
            if (ClientToolSelected || previousToolSelected)
            {
                // 只有完整快照安装成功才切模式。工具确认会撤销旧法杖缓冲/动画；切回时须先松开左键。
                CancelPending(); _waitRelease = true;
            }
            var read = _inventoryReplica.Source.WandsReadOnly;
            if (read.TryGetProgram(read.SelectedWandId, out var program) && read.TryGetState(read.SelectedWandId, out var state))
            {
                // 次数变化不等于换程序；相同来源不重置攻击缓冲/按住连续施法的手感。
                if (_clientWand != read.SelectedWandId || _clientProgramRevision != program.ProgramRevision)
                    InstallClientSetup(read.SelectedWandId, program.ProgramRevision, state.CastCooldown);
            }
            else
            {
                // 完整Owner快照确认空手时，旧程序/冷却不能继续授予预测施法。
                // 备用杖的资源仍留在Source；仅撤销Driver对上一把手持杖的输入和动画所有权。
                CancelPending(); _clientWand = null; _clientProgramRevision = 0; _clientCooldown = 0;
                _predictionSoundKey = 0; _queueRemaining = 0; _pending = default; _active = default;
                _clientPendingAim = default; _waitRelease = true;
            }
            return SyncGroundPresentation(out error);
        }

        /// <summary>ActorState 高频事实只写固定大小的资源值；不重建物品数组，也不从Render deltaTime推算权威余额。</summary>
        public void InstallClientInventoryResources(uint epoch, uint serverTick, uint slotPlusOne, uint revision, float mana, float cooldown)
        {
            if (_authority || !_clientOwner || epoch != _epoch || serverTick == 0 || revision == 0 ||
                slotPlusOne < 1 || slotPlusOne > 3) return;
            EnsureInventoryReplica();
            _inventoryReplica.ApplySlotResources(epoch, serverTick, slotPlusOne, revision, mana, cooldown);
        }
        private void EnsureInventoryReplica()
        {
            if (_inventoryReplica != null) return;
            _inventoryReplica = GetComponent<OnlineInventoryReplica>();
            if (_inventoryReplica == null) _inventoryReplica = gameObject.AddComponent<OnlineInventoryReplica>();
        }
        // Setup可先于下一份Owner快照到达，因此继续缓存其音效等元数据；施法资格仍服从已确认空手事实。
        // 首次库存前保留既有bootstrap，非空换杖流程不扩大到新的消息排序/诊断机制。
        public bool Ready => _definition != null && _connected && (_authority ? _workspace != null
            : _clientOwner && _clientProgramRevision != 0 && (!ClientInventoryReady
                || !string.IsNullOrEmpty(_inventoryReplica.Source.WandsReadOnly.SelectedWandId)));
        public ulong ActorId => _actor;
        public event Action<OnlineCastIntent> IntentProduced;
        public event Action<OnlineCastReceipt> ReceiptProduced;

        private void Cache()
        {
            if (_wizard == null) _wizard = GetComponent<WizardController>();
            if (_caster == null) _caster = GetComponent<SpellCaster>();
            if (_presentationVisual == null && _wizard != null && _wizard.Animator != null && _wizard.Animator.transform != transform)
            {
                _presentationVisual = _wizard.Animator.transform;
                _baseVisualLocalRotation = _presentationVisual.localRotation;
            }
            if (_adapter == null)
            {
                _adapter = GetComponent<OwnedWandCastAdapter>();
                if (_adapter == null) _adapter = gameObject.AddComponent<OwnedWandCastAdapter>();
            }
        }
        /// <summary>独立于 Gameplay Tick 的输入门。Frozen 停 Tick 时仍必须立即清掉 UI/失焦期间的点击。</summary>
        public void SetBlocked(bool blocked)
        {
            if (_authority || _suppressed == blocked) return;
            _suppressed = blocked;
            if (blocked) { CancelPending(); _waitRelease = true; _containerInput?.ResetHeldState(); }
        }
        public bool InitializeAuthority(OnlineOwnedAssetSnapshot assets, OnlineCombatDefinition definition,
            CombatSimulationWorld world, out string error)
        {
            error = null; Cache();
            if (assets == null || definition == null || world == null || _wizard == null || _caster == null)
            { error = "权威角色缺少资产或正式配置。"; return false; }
            if (_workspace != null)
            {
                // 连接代次变化不等于新 Run；重连续用原 Mana、Draw、冷却和次数。
                bool same = _workspace.RunId == assets.RunId && _workspace.PlayerId == assets.PlayerId && _workspace.ActorId == assets.ActorId;
                if (!same) error = "不能把存活的角色工作区替换为另一个入局身份。";
                return same;
            }
            if (!definition.Catalog.TryCreateIndex(out var catalog, out error)) return false;
            OnlineOwnedWorkspace workspace;
            bool created = _homeBindings != null
                ? OnlineOwnedWorkspaceFactory.TryCreatePractice(catalog, assets, definition.GameplayConfigHash, out workspace, out error)
                : OnlineOwnedWorkspaceFactory.TryCreate(catalog, assets, definition.GameplayConfigHash, out workspace, out error);
            if (!created) return false;
            _workspace = workspace; _definition = definition; _world = world; _actor = assets.ActorId; _authority = true;
            _containerInput?.Configure(null);
            try
            {
                _world.ConfigureHierarchy(gameObject, CombatExecutionMode.Authority);
                // 原始EventBus与延迟提交同样读World动态资格，不能只依赖网络收到意图时的一次校验。
                workspace.ConfigureAuthorityWriteGate(() => _world.AllowsAuthorityWrites);
                if (_homeBindings != null) workspace.ConfigureHomeResearchAccess(HasAuthorityResearchAccess);
                _adapter.BindWorkspace(workspace); _caster.ConfigureAuthority(world, _actor);
                return true;
            }
            catch
            {
                _workspace.Dispose(); _workspace = null; _authority = false; throw;
            }
        }
        public void InitializeClient(ulong actorId, uint epoch, OnlineCombatDefinition definition, bool localOwner)
        {
            Cache(); _actor = actorId; _definition = definition; _clientOwner = localOwner; _authority = false;
            _caster.ConfigureReplica(); SetConnection(epoch, true);
            BindSelectionInput();
            BindContainerInput();
            BindOnlinePickupInput();
            BindHomeResearchPresentation();
        }
        public void SetConnection(uint epoch, bool connected)
        {
            if (epoch < _epoch) return;
            bool changed = epoch != _epoch || _connected != connected;
            if (!changed) return;
            // 主动退出已Fence新意图，但原动作/供料仍需结束。断开通知不能替它Cancel/退货。
            if (!HasMemberExitFence) CancelPending();
            _epoch = epoch; _connected = connected; _lastCommand = 0; _clientSequence = 0;
            if (!connected) { CloseResearchSession(); ClearHomeResearchPresentation(); }
            else BindHomeResearchPresentation();
            _groundPresentation?.Dispose(); _groundPresentation = null;
            _containerInput?.ResetHeldState();
            Array.Clear(_receipts, 0, _receipts.Length); _receiptCursor = 0; _waitRelease = true;
            Array.Clear(_soundCommands, 0, _soundCommands.Length); Array.Clear(_soundKeys, 0, _soundKeys.Length);
        }
        public void InstallClientSetup(string wandId, uint programRevision, float castInterval, uint predictionSoundKey = 0)
        {
            if (_authority || !_clientOwner) return;
            _clientWand = wandId; _clientProgramRevision = programRevision;
            _predictionSoundKey = predictionSoundKey;
            _castInterval = Mathf.Max(.05f, castInterval); _waitRelease = true;
            EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent { Visible = true });
        }
        public void InstallClientCooldown(float cooldown)
        { if (!_authority && float.IsFinite(cooldown)) _clientCooldown = Mathf.Max(0, cooldown); }
        public void CaptureAttackPress()
        {
            if (!Ready || _authority || _suppressed || ResearchPaused || !_clientOwner || Time.timeScale <= 0
                || !_focused || _applicationPaused || !Application.isFocused || ClientToolSelected) return;
            _clientPendingAim = _wizard.CaptureOnlineCastAim();
            _queued = true; _queueRemaining = _clientCooldown + _wizard.AttackBufferTime + .05f;
            _waitRelease = false;
        }
        public void StepPrediction(float deltaTime, uint tick, bool suppressed)
        {
            if (_authority || !_clientOwner || !Ready) return;
            SetBlocked(suppressed);
            if (ResearchPaused) { CancelPending(); _waitRelease = true; return; }
            if (ClientToolSelected || !_focused || _applicationPaused || !Application.isFocused || Time.timeScale <= 0)
            {
                // 工具左键交给材料操作。即使旧法杖按键已进入缓冲，也不能再预测或发送Cast。
                CancelPending(); _waitRelease = true; return;
            }
            bool held = _wizard.IsOnlineAttackHeld;
            if (_suppressed) return;
            if (!held) _waitRelease = false;
            _clientCooldown = Mathf.Max(0, _clientCooldown - deltaTime);
            if (_timeline.IsActive && _timeline.Step(deltaTime, tick))
            {
                _clientCooldown = _castInterval; _wizard.FinishOnlineActionPresentation();
                PlayConfirmedCastSound(_active.CommandId, _predictionSoundKey, transform.position);
            }
            if (_queued) { _queueRemaining -= deltaTime; if (_queueRemaining <= 0) _queued = false; }
            if (_timeline.IsActive || _clientCooldown > 0 || _waitRelease || !_queued && !held
                || _wizard.CaptureMovementSnapshot().Locomotion == PlayerLocomotionKind.Dash) return;
            if (!_queued) _clientPendingAim = _wizard.CaptureOnlineCastAim();
            _queued = false;
            if (_clientSequence == uint.MaxValue) return;
            _active = new OnlineCastIntent { CommandId = ((ulong)_epoch << 32) | ++_clientSequence,
                ConnectionEpoch = _epoch, WandInstanceId = _clientWand, ProgramRevision = _clientProgramRevision,
                ClaimedOrigin = transform.position, AimPoint = _clientPendingAim };
            _activeAir = !_wizard.GroundChecker.IsGrounded;
            BeginTimeline(_active.CommandId, _activeAir); PresentAction(_activeAir);
            IntentProduced?.Invoke(_active);
        }

        public bool TryQueue(in OnlineCastIntent intent, out OnlineCastReceipt receipt)
        {
            for (int i = 0; i < _receipts.Length; i++)
                if (_receipts[i].CommandId == intent.CommandId && intent.CommandId != 0 && _receipts[i].ConnectionEpoch == intent.ConnectionEpoch)
                { receipt = _receipts[i]; ReceiptProduced?.Invoke(receipt); return receipt.Result != OnlineCastResult.Rejected; }
            var failure = Validate(intent);
            receipt = NewReceipt(intent, failure == OnlineCastFailure.None ? OnlineCastResult.Queued : OnlineCastResult.Rejected, failure);
            if (intent.CommandId > _lastCommand) _lastCommand = intent.CommandId;
            if (failure == OnlineCastFailure.None)
            {
                if (_queued) Store(NewReceipt(_pending, OnlineCastResult.Rejected, OnlineCastFailure.Interrupted));
                _pending = intent; _queued = true;
                TryGetAuthorityResources(out var resources); _queueRemaining = resources.CooldownRemaining + _wizard.AttackBufferTime + .1f;
            }
            Store(receipt); return failure == OnlineCastFailure.None;
        }
        private OnlineCastFailure Validate(in OnlineCastIntent intent)
        {
            if (!_authority || !Ready || _wizard.Health == null || !_wizard.Health.IsAlive) return OnlineCastFailure.NotReady;
            // 只拒绝尚未接受的新意图；TryQueue已先查原收据，StepAuthority仍完成旧队列/时间线。
            if (!_workspace.CanAcceptNewPlayerIntents) return OnlineCastFailure.NotReady;
            // 容器选中时最后法杖ID仍保留；必须在接受意图/启动Timeline前拒绝伪造的旧法杖施法。
            if (!_workspace.WorldActionsAllowed) return OnlineCastFailure.NotOwned;
            if (intent.CommandId == 0 || intent.ConnectionEpoch != _epoch || intent.CommandId <= _lastCommand) return OnlineCastFailure.StaleCommand;
            if (!OnlineCastIntentRules.ValidateAim(transform.position, intent.ClaimedOrigin, intent.AimPoint, 3, 120)) return OnlineCastFailure.InvalidInput;
            var read = _workspace.WandsReadOnly;
            if (intent.WandInstanceId != read.SelectedWandId || !read.TryGetState(intent.WandInstanceId, out var state) || !state.Carried) return OnlineCastFailure.NotOwned;
            if (!read.TryGetProgram(intent.WandInstanceId, out var program) || program.ProgramRevision != intent.ProgramRevision) return OnlineCastFailure.StaleProgram;
            // 当前入局 Schema 只导入装备与卡，不承载材料 Attachment / 容器组分；因此不能从客户端补传材料预算。
            // 材料并非 SpellDefinition 的数量字段，真实供应来自 Run 物品附件，N5 才扩展那份受控快照。
            return OnlineCastFailure.None;
        }
        public void StepAuthority(float deltaTime, uint tick)
        {
            RefreshHomeResearchAccess();
            if (ResearchPaused) return;
            if (!_authority || _workspace == null || !float.IsFinite(deltaTime) || deltaTime <= 0
                || _hasTick && unchecked((int)(tick - _lastTick)) <= 0) return;
            _hasTick = true; _lastTick = tick;
            // 上一 Physics Tick 的致死事件必须先取消旧 deadline；否则到期预警会在检查死亡前补打一发。
            if ((!_connected || _wizard.Health == null || !_wizard.Health.IsAlive) && !HasMemberExitFence)
            { CancelPending(); return; }
            _workspace.StepAuthority(deltaTime, tick);
            _caster.StepAuthority(deltaTime, tick);
            if (_queued)
            {
                _queueRemaining -= deltaTime;
                if (_queueRemaining <= 0) { Store(NewReceipt(_pending, OnlineCastResult.Rejected, OnlineCastFailure.Interrupted)); _queued = false; }
            }
            if (!_timeline.IsActive && _queued && _wizard.CaptureMovementSnapshot().Locomotion != PlayerLocomotionKind.Dash
                && TryGetAuthorityResources(out var resources) && resources.CooldownRemaining <= 0)
            {
                _active = _pending; _queued = false;
                if (!_adapter.TryCapture(out _activeProgram, out _activeSource)) { Store(NewReceipt(_active, OnlineCastResult.Rejected, OnlineCastFailure.NotOwned)); return; }
                _activeAir = !_wizard.GroundChecker.IsGrounded; BeginTimeline(_active.CommandId, _activeAir);
            }
            if (!_timeline.IsActive) return;
            Vector3 facing = _active.AimPoint - transform.position; facing.y = 0;
            if (facing.sqrMagnitude > .001f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(facing), Mathf.Min(1, deltaTime * 15));
            if (!_timeline.Step(deltaTime, tick)) return;
            _caster.SetAuthorityCommand(_active.CommandId);
            _activeSource = _activeSource.WithAuthorityOutput(_actor, _active.CommandId);
            Vector3 spawn = transform.TransformPoint(_definition.SpawnOffset(_activeAir));
            int emitted = _caster.CastOwnedProgram(_activeProgram, _activeSource, spawn, _active.AimPoint,
                _wizard.Health.TeamId, gameObject.GetInstanceID(), _wizard.CharacterController);
            Store(NewReceipt(_active, emitted > 0 ? OnlineCastResult.Released : OnlineCastResult.Rejected,
                emitted > 0 ? OnlineCastFailure.None : _adapter.LastFailure == OwnedWandFailureReason.None ? OnlineCastFailure.EmptyOutput : OnlineCastFailure.ResourceRejected));
        }
        /// <summary>退出状态属于存活Owner，不属于连接代次；同局重连不重建背包或重发开局瓶。</summary>
        public bool HasMemberExitFence => _authority && _workspace != null && _workspace.HasMemberExitFence;
        public bool TryBeginMemberExit(string commandId)
        {
            if (!_authority || _workspace == null) return false;
            CloseResearchSession(); return _workspace.TryBeginMemberExit(commandId);
        }
        public bool TryReplaceRejectedMemberExit(string rejectedCommand, string nextCommand)
            => _authority && _workspace != null && _workspace.TryReplaceRejectedMemberExit(rejectedCommand, nextCommand);

        public bool TryCaptureMemberExitAssets(string commandId, out OnlineOwnedAssetSnapshot assets)
        {
            assets = null;
            // queued/正在释放/延迟程序都必须自然结束；不把所有队友的任务算入本人的稳定条件。
            if (!_authority || _workspace == null || !HasMemberExitFence || _queued || _timeline.IsActive
                || _caster == null || _caster.PendingAuthorityActions != 0) return false;
            return _workspace.TryCaptureMemberExitAssets(commandId, out assets);
        }

        /// <summary>正式退出捕获同时携带原累计损耗，和资产共用本人动作/预约/材料输出的稳定边界。</summary>
        public bool TryCaptureMemberExitFacts(string commandId, out OnlineOwnedAssetSnapshot assets, out OnlineExitedMemberLossSnapshot losses)
        {
            assets = null; losses = null;
            if (!_authority || _workspace == null || !HasMemberExitFence || _queued || _timeline.IsActive
                || _caster == null || _caster.PendingAuthorityActions != 0) return false;
            return _workspace.TryCaptureMemberExitFacts(commandId, out assets, out losses);
        }

        private void BeginTimeline(ulong command, bool air)
        {
            float release = _definition.ReleaseSeconds(air);
            _timeline.TryStart(command, release, release); // 单机也是释放后立刻交回 locomotion，不锁整段 Clip。
            if (_authority) EventBus<OnlineCharacterActionEvent>.Publish(new OnlineCharacterActionEvent
            { ActorId = _actor, AnimationHash = _definition.AnimationHash(air), ActionKind = 1, FadeSeconds = .1f });
        }
        public void PresentAction(bool airborne)
        {
            if (_authority || _wizard == null || _wizard.Animator == null) return;
            // 在动作开始时捕获连续的画面朝向，避免首个LateUpdate前恰好收到Snapshot时继承父节点突变。
            // 连续下一发若尚在自然归还中，沿用正在显示的world缓存，不从已经校正过的层级重采样。
            if (_clientOwner && _presentationVisual != null && !_ownsVisualFacing)
            {
                _presentedVisualWorldRotation = _presentationVisual.rotation;
                _ownsVisualFacing = true;
            }
            _wizard.SetOnlineActionPresentation(true);
            _wizard.Animator.CrossFadeInFixedTime(_definition.AnimationHash(airborne), .1f, 0, 0);
        }
        /// <summary>远端只播放已批准动作；本机 Command Prediction 由网络组合层去重。</summary>
        public void ApplyReplicaAction(int animationHash, uint kind, float fadeSeconds)
        {
            if (_authority || _wizard == null || _wizard.Animator == null || animationHash == 0) return;
            if (kind == 1)
            {
                _wizard.SetOnlineActionPresentation(true);
                _wizard.Animator.CrossFadeInFixedTime(animationHash, Mathf.Clamp(fadeSeconds, 0, 1), 0, 0);
            }
            else if (kind == 2) _wizard.Animator.SetTrigger(animationHash);
        }
        public void ApplyReceipt(in OnlineCastReceipt receipt)
        {
            if (_authority || receipt.ConnectionEpoch != _epoch) return;
            if (receipt.Result == OnlineCastResult.Rejected && (!_clientOwner || _active.CommandId == receipt.CommandId))
            { _timeline.Cancel(); _clientCooldown = Mathf.Max(.05f, _clientCooldown); _wizard.SetOnlineActionPresentation(false); }
            if (receipt.Result == OnlineCastResult.Released && (!_clientOwner || _active.CommandId == receipt.CommandId))
                _wizard.FinishOnlineActionPresentation();
        }
        /// <summary>确认先到或预测先到均只播放一次；固定窗口不随联机时长增长，也不参与移动 replay。</summary>
        public void PlayConfirmedCastSound(ulong command, uint key, Vector3 position)
        {
            if (_authority || !_clientOwner || command == 0 || key == 0 || _definition == null) return;
            int slot = (int)(command % (ulong)_soundCommands.Length);
            if (_soundCommands[slot] == command && _soundKeys[slot] == key) return;
            var clip = _definition.SoundClip(key); if (clip == null) return;
            _soundCommands[slot] = command; _soundKeys[slot] = key;
            AudioSource.PlayClipAtPoint(clip, position);
        }
        public bool TryGetAuthorityResources(out OnlineCastResourceSnapshot snapshot)
        {
            snapshot = default;
            if (_workspace == null || !_workspace.WandsReadOnly.TryGetState(_workspace.WandsReadOnly.SelectedWandId, out var state)
                || !_workspace.WandsReadOnly.TryGetProgram(state.InstanceId, out var program)) return false;
            snapshot = new OnlineCastResourceSnapshot { WandInstanceId = state.InstanceId, ProgramRevision = program.ProgramRevision,
                CurrentMana = state.CurrentMana, MaxMana = state.MaxMana, CooldownRemaining = state.CooldownRemaining, CastInterval = state.CastCooldown };
            if (_workspace.TryGetWandExecution(state.InstanceId, out var execution))
                for (int i = 0; i < execution.Program.Count; i++)
                {
                    var instruction = execution.Program.Instructions[i];
                    if (instruction.IsIntrinsic || instruction.Kind != SpellKind.Emit || !_workspace.TryGetItem(instruction.InstanceId, out var card)) continue;
                    snapshot.RemainingCharges = card.RemainingCharges; snapshot.Unlimited = card.HasUnlimitedCharges;
                    // 仅单 Emit、无 Trigger 的确定程序预测首声；复杂牌堆不猜本次会抽到哪张卡。
                    if (execution.Program.Count == 1 && instruction.Emission.PayloadTrigger == PayloadTriggerMode.None
                        && (card.HasUnlimitedCharges || card.RemainingCharges > 0))
                        snapshot.PredictionSoundKey = _definition.FindSoundKey(instruction.Emission.CastSfx);
                    break;
                }
            return true;
        }
        private OnlineCastReceipt NewReceipt(in OnlineCastIntent intent, OnlineCastResult result, OnlineCastFailure failure) =>
            new OnlineCastReceipt { ActorId = _actor, CommandId = intent.CommandId, ConnectionEpoch = intent.ConnectionEpoch,
                Result = result, Failure = failure, Airborne = _activeAir, AimPoint = intent.AimPoint };
        private void Store(in OnlineCastReceipt receipt)
        {
            for (int i = 0; i < _receipts.Length; i++) if (_receipts[i].CommandId == receipt.CommandId && _receipts[i].ConnectionEpoch == receipt.ConnectionEpoch)
            { _receipts[i] = receipt; ReceiptProduced?.Invoke(receipt); return; }
            _receipts[_receiptCursor] = receipt; _receiptCursor = (_receiptCursor + 1) % _receipts.Length; ReceiptProduced?.Invoke(receipt);
        }
        private void CancelPending()
        {
            _queued = false; _timeline.Cancel(); _activeProgram = default; _activeSource = default;
            RestoreVisualFacing();
            _wizard?.SetOnlineActionPresentation(false);
            if (_authority) { _adapter?.CancelOutstanding(); _caster?.CancelAuthorityPending(); }
        }
        private void OnEnable()
        {
            EventBus<DeathEvent>.Subscribe(OnActorDeath); BindSelectionInput(); BindContainerInput(); BindOnlinePickupInput();
            BindHomeResearchPresentation();
            if (!SyncGroundPresentation(out var error)) GameLog.Warn(error, "OnlineInventory");
        }
        private void OnApplicationFocus(bool focused)
        {
            _focused = focused;
            // 必须保留失焦边沿；若同一轮 Update 前失焦又回焦，只轮询最终值会漏掉取消。
            if (!focused) SetBlocked(true);
        }
        private void OnApplicationPause(bool paused)
        {
            _applicationPaused = paused;
            // 恢复回调不自行解锁：World 仍可能 Frozen/断线/打开菜单，且必须继续等待旧按键释放。
            if (paused) SetBlocked(true);
        }
        private void LateUpdate()
        {
            if (_authority || !_clientOwner || !_timeline.IsActive || _suppressed || _presentationVisual == null)
            { RestoreVisualFacing(true); return; }
            // 单机施法时身体朝相机水平朝向。这里仅旋转 Animator 模型子节点：CC 根、TargetFacing、
            // Prediction 历史和网络 Snapshot 仍由 Movement Authority 独占，不把表现修正伪装成物理移动。
            var pivot = _wizard.CameraRoot;
            Vector3 direction = pivot != null ? pivot.forward : _active.AimPoint - transform.position;
            direction.y = 0;
            if (direction.sqrMagnitude < .000001f) return;
            Quaternion target = Quaternion.LookRotation(direction) * _baseVisualLocalRotation;
            Quaternion previous = _ownsVisualFacing ? _presentedVisualWorldRotation : _presentationVisual.rotation;
            _presentedVisualWorldRotation = Quaternion.Slerp(previous, target, Mathf.Clamp01(_wizard.RotationSpeed * Time.deltaTime));
            _presentationVisual.rotation = _presentedVisualWorldRotation;
            _ownsVisualFacing = true;
        }
        private void RestoreVisualFacing(bool smoothly = false)
        {
            if (!_ownsVisualFacing) return;
            if (_presentationVisual != null)
            {
                // 自然出手后模型可能仍朝准心、运动根仍朝旧行进方向。瞬间归零 localRotation 会使每发
                // 都发生一次可见跳转；只在表现子节点平滑归还，不改 Prediction 根、TargetFacing 或历史输入。
                // 取消/停用的调用方保留默认立即清理，避免把旧场景或失效输入的姿态带到下次绑定。
                if (smoothly)
                {
                    float step = Mathf.Clamp(Time.deltaTime, 0f, .05f) * (_wizard != null ? _wizard.RotationSpeed : 15f);
                    // 归还也从上一帧world姿态开始。localRotation插值只能修正模型与根的偏差，
                    // 却无法消除这一帧父节点被Snapshot旋转时已经带来的画面突变。
                    // 目标仍是原局部姿态乘当前父节点rotation，最终完整交回真实locomotion朝向。
                    Quaternion target = _presentationVisual.parent != null
                        ? _presentationVisual.parent.rotation * _baseVisualLocalRotation : _baseVisualLocalRotation;
                    _presentedVisualWorldRotation = Quaternion.Slerp(_presentedVisualWorldRotation, target, Mathf.Clamp01(step));
                    _presentationVisual.rotation = _presentedVisualWorldRotation;
                    if (Quaternion.Angle(_presentedVisualWorldRotation, target) > .1f) return;
                }
                _presentationVisual.localRotation = _baseVisualLocalRotation;
            }
            _ownsVisualFacing = false;
        }
        private void OnActorDeath(DeathEvent fact)
        {
            // 在同步 DeathEvent 内取消，与该 Tick 后续角色推进之间没有可出手的空隙。
            if (fact.TargetId == gameObject.GetInstanceID()) { CloseResearchSession(); CancelPending(); if (!_authority) SetBlocked(true); }
        }
        private void OnDisable()
        {
            CloseResearchSession(); ClearHomeResearchPresentation();
            EventBus<DeathEvent>.Unsubscribe(OnActorDeath); UnbindSelectionInput(); CancelPending();
            _containerInput?.Detach(this);
            _groundPresentation?.Dispose(); _groundPresentation = null;
            _onlineNearbyInput?.ReleaseOnline(false);
        }
        private void OnDestroy()
        {
            CloseResearchSession(); ClearHomeResearchPresentation();
            if (_containerThrowSubscribed) EventBus<ContainerThrownEvent>.Unsubscribe(OnPrepareContainerThrow);
            _containerThrowSubscribed = false; _containerThrowFactory = null;
            _groundPresentation?.Dispose(); _groundPresentation = null;
            _onlineNearbyInput?.ReleaseOnline(true); _onlineNearbyInput = null;
            _groundWorld = null; // 房间Scope负责共享Authority.Dispose，单个玩家不得清空队友地面资产。
            _containerInput?.DisposeForOwner(this); _containerInput = null;
            UnbindSelectionInput();
            if (_selectTools != null)
            {
                for (int i = 0; i < _selectTools.Length; i++)
                { _selectTools[i].performed -= SelectTool; _selectTools[i].Dispose(); }
                _selectTools = null;
            }
            CancelPending(); _workspace?.Dispose(); _workspace = null;
        }
    }
}
