using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 已验证账号Profile的只读Hub投影。只借Catalog解释原实例，不读取本地档、不创建Run、资格或事务。
    /// 不实现线上Gameplay命令上下文；UI即使能够显示所有物品，也不能借此获得本地或DS写权限。
    /// </summary>
    public sealed class AccountInventoryPresentationSource : IInventoryPresentationSource, IWalletReadOnly,
        IToolbeltReadOnly, IAccountInventoryCommandContext, IDisposable
    {
        private SinglePlayerProfile _profile;
        private InventoryReadModel _read;
        private IWandLoadoutReadOnly _programs;
        private readonly string[] _tools = new string[HeldItemSlots.ToolCount];
        private string _selectedWand, _backpack, _playerId;
        private int _selectedSlot = -1;
        private uint _version;
        private bool _disposed;
        // 多个Canvas各自拥有完整读模型，选择却属于同一Scene的用户意图。共享这个纯Presentation状态，
        // 不共享/借出Store，也不把仅切换手持的数字键变成账号资产版本或Gameplay授权。
        internal AccountHeldItemSelection HeldSelection { get; private set; }
        private readonly Dictionary<string, OnlineAccountWorldItemSnapshot> _ground = new Dictionary<string, OnlineAccountWorldItemSnapshot>(StringComparer.Ordinal);
        private Transform _groundActor;
        private string _worldSceneKey;
        internal ItemOwnershipStore PresentationItems => !_disposed ? _profile.Items : null;
        internal IReadOnlyDictionary<string, OnlineAccountWorldItemSnapshot> GroundItems => _ground;

        public IInventoryReadOnly ReadOnly => !_disposed ? _read : null;
        public IWalletReadOnly WalletReadOnly => !_disposed ? this : null;
        public IToolbeltReadOnly ToolbeltReadOnly => !_disposed ? this : null;
        public IWandLoadoutReadOnly WandsReadOnly => !_disposed ? _programs : null;
        public ISpellUseReadOnly SpellUsesReadOnly => null; // Hub没有本局资格，不从剩余次数造一个Raid。
        public bool UseSpellProgrammingV2 => true;
        public bool CanAccessWarehouse => !_disposed;
        public string WorldSceneKey => !_disposed ? _worldSceneKey ?? string.Empty : string.Empty;
        public string EquippedBackpackId => !_disposed ? _backpack : null;
        public int SpellStorageCapacity => !_disposed ? _profile.Items.Catalog.SpellStorageCapacity : 0;
        public string InitializationMessage => _disposed ? "账号资产来源已失效。" : "已读取账号资产；移动由服务器确认后更新。";
        public bool HasUnsavedChanges => false;
        public long Currency => !_disposed ? _profile.Currency : 0;
        public uint Version => !_disposed ? _version : 0;
        public string InventoryId => !_disposed ? _read.InventoryId : null;
        public string PlayerId => !_disposed ? _playerId : null;
        public uint ProfileVersion => Version;
        public uint ConfigurationVersion => Version;
        public uint SelectionVersion => !_disposed && HeldSelection != null ? HeldSelection.Revision : Version;
        public int SelectedSlot => !_disposed ? HeldSelection?.GetSlot(this) ?? _selectedSlot : -1;
        public string SelectedItemId => !_disposed ? HeldSelection?.GetItem(this) ?? _selectedWand : null;
        internal string ConfirmedWandId => _selectedWand;
        internal int ConfirmedWandSlot => _selectedSlot;

        private AccountInventoryPresentationSource() { }

        public static bool TryCreate(ItemCatalogIndex catalog, OnlineAccountInventorySnapshot snapshot,
            out AccountInventoryPresentationSource source, out string error)
        {
            source = null; error = null;
            if (catalog == null || snapshot == null || snapshot.SchemaVersion != OnlineAccountInventorySnapshot.CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(snapshot.PlayerId) || snapshot.Version == 0 || snapshot.Gold < 0
                || snapshot.AssetsLocked || !string.IsNullOrEmpty(snapshot.ActiveRunId)
                || snapshot.Items == null || snapshot.Items.Length > OnlineAccountInventorySnapshot.MaximumItems
                || snapshot.WorldItems == null || snapshot.WorldItems.Length > OnlineAccountInventorySnapshot.MaximumItems)
                return Fail("账号资产未完整读取、仍被副本占用或版本非法。", out error);
            var frozen = snapshot.CopyValue();
            try
            {
                var ground = new Dictionary<string, OnlineAccountWorldItemSnapshot>(StringComparer.Ordinal);
                foreach (var record in frozen.WorldItems)
                    if (record == null || string.IsNullOrWhiteSpace(record.InstanceId) || !ValidWorldOwner(record.WorldOwnerId)
                        || record.Pose == null || !record.Pose.IsValid() || !ground.TryAdd(record.InstanceId, record))
                        return Fail("账号地面身份或姿态非法，未用仓库替代地面。", out error);
                var entries = new InventoryEntry[frozen.Items.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    var item = frozen.Items[i];
                    if (item == null || item.Revision == 0 || !catalog.TryGet(item.ContentId, out var definition)
                        || !catalog.TryGetRules(item.ContentId, out var rules) || !TryLocation(item, frozen.PlayerId, ground, out var location))
                        return Fail("账号物品身份、目录或位置非法，未截断或补造资产。", out error);
                    WandStats? stats = null;
                    if (definition.Kind == ItemKind.Wand)
                    {
                        if (item.UseTemplateWandStats)
                        {
                            if (item.WandStats != null || !catalog.TryGetWandStats(item.ContentId, out _))
                                return Fail("账号法杖模板与实例属性冲突。", out error);
                        }
                        else
                        {
                            var value = item.WandStats;
                            if (value == null || value.AlwaysCastContentIds == null)
                                return Fail("账号法杖缺少真实固定属性。", out error);
                            var real = new WandStats(value.MaxMana, value.ManaRegenPerSecond, value.BaseCastDelay,
                                value.ProgramCapacity, value.BaseDraws, value.BaseReloadTime, value.BaseSpreadDegrees,
                                value.Shuffle, value.AlwaysCastContentIds);
                            if (!real.IsValid || !catalog.ValidateWandTraits(real)) return Fail("账号法杖固定属性非法。", out error);
                            stats = real;
                        }
                    }
                    else if (item.UseTemplateWandStats || item.WandStats != null)
                        return Fail("非法杖不能携带固定杖属性。", out error);
                    if (item.EntryBoundBackpack && definition.Kind != ItemKind.Backpack)
                        return Fail("保底背包标记不能属于其它物品。", out error);
                    // 状态解码复用原Owned校验；这里只有全账号位置映射不同，不另写材料/附件规则。
                    if (!OnlineOwnedWorkspaceFactory.TryReadComposition(item, rules, out var composition, out error)
                        || !OnlineOwnedWorkspaceFactory.TryReadMaterial(item, rules, out var material, out error)
                        || !OnlineOwnedWorkspaceFactory.TryReadAttachment(item, definition, out var attachment, out error)) return false;
                    var state = new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges,
                        item.HasUnlimitedCharges, composition: composition, material: material, attachment: attachment,
                        rolledWandStats: stats, entryBoundBackpack: item.EntryBoundBackpack);
                    entries[i] = new InventoryEntry(state, location);
                }
                if (!ItemOwnershipStore.TryRestore(catalog, entries, frozen.Version, out var store, out var reason))
                    return Fail("账号物品父链、占格或归属非法：" + reason, out error);
                var profile = new SinglePlayerProfile(frozen.PlayerId, store, currency: frozen.Gold);
                string inventory = "account." + frozen.PlayerId;
                var candidate = new AccountInventoryPresentationSource { _profile = profile,
                    _read = new InventoryReadModel(profile, inventory), _playerId = frozen.PlayerId, _version = frozen.Version,
                    _selectedWand = string.IsNullOrEmpty(frozen.SelectedWandInstanceId) ? null : frozen.SelectedWandInstanceId };
                foreach (var pair in ground)
                {
                    if (!store.TryGetLocation(pair.Key, out var world) || world.Kind != ItemLocationKind.WorldItem)
                        return Fail("账号地面姿态没有对应的真实WorldItem实例。", out error);
                    candidate._ground.Add(pair.Key, pair.Value);
                }
                foreach (var entry in store)
                {
                    if (entry.Location.Kind == ItemLocationKind.EquippedBackpack) candidate._backpack = entry.Item.InstanceId;
                    if (entry.Location.Kind == ItemLocationKind.EquippedTool) candidate._tools[entry.Location.SlotIndex] = entry.Item.InstanceId;
                }
                if (candidate._selectedWand != null)
                {
                    if (!store.TryGetLocation(candidate._selectedWand, out var location)
                        || location.Kind != ItemLocationKind.EquippedWand || location.OwnerId != frozen.PlayerId)
                        return Fail("账号选中杖不在自己的装备槽。", out error);
                    candidate._selectedSlot = location.SlotIndex;
                }
                // 只复用原卡序投影；不Tick、不暴露其初始化运行资源，避免把模板满蓝冒充DS退出读数。
                var projections = new WandLoadoutService(profile, inventory);
                // 首次冷构建不借用内部缓存哨兵判断；uint.MaxValue也是已持久化版本的合法读值。
                projections.Synchronize(true);
                candidate._programs = new ProgramsOnly(projections.ReadOnly, candidate._selectedWand, frozen.Version);
                candidate.BindHeldSelection(new AccountHeldItemSelection());
                source = candidate; return true;
            }
            catch (ArgumentException) { return Fail("账号物品包含非法值，未安装半份资产。", out error); }
            catch (OverflowException) { return Fail("账号材料数量溢出，未安装半份资产。", out error); }
        }

        private static bool TryLocation(OnlineOwnedItemSnapshot item, string player,
            Dictionary<string, OnlineAccountWorldItemSnapshot> ground, out ItemLocation location)
        {
            location = default;
            // DTO位置是int，而运行时枚举底层为byte；先拦原值，防止256等宽值窄化成仓库。
            if (item.ParentInstanceId == null || item.LocationKind < 0 || item.LocationKind > 8) return false;
            switch ((ItemLocationKind)item.LocationKind)
            {
                case ItemLocationKind.Warehouse:
                    if (item.ParentInstanceId.Length != 0 || item.Slot < 0 || item.Slot >= 16
                        || item.X != 0 || item.Y != 0 || item.QuarterTurns != 0) return false;
                    // 业务平面仓库Slot仅用于分页排序；原Store仓库规范位置固定slot=-1，不伪造背包父项。
                    location = ItemLocation.Warehouse; return true;
                case ItemLocationKind.WorldItem:
                    if (item.ParentInstanceId.Length != 0 || item.Slot != -1 || item.X != 0 || item.Y != 0 || item.QuarterTurns != 0
                        || item.EntryBoundBackpack || !ground.TryGetValue(item.InstanceId, out var world)) return false;
                    location = new ItemLocation(ItemLocationKind.WorldItem, world.WorldOwnerId, 0, 0, 0, -1); return true;
                case ItemLocationKind.Backpack:
                case ItemLocationKind.WandSlot:
                    if (string.IsNullOrWhiteSpace(item.ParentInstanceId)) return false;
                    location = new ItemLocation((ItemLocationKind)item.LocationKind, item.ParentInstanceId,
                        item.X, item.Y, item.QuarterTurns, item.Slot); return true;
                case ItemLocationKind.EquippedWand:
                case ItemLocationKind.EquippedBackpack:
                case ItemLocationKind.EquippedTool:
                case ItemLocationKind.SpellStorage:
                    if (item.ParentInstanceId.Length != 0) return false;
                    location = new ItemLocation((ItemLocationKind)item.LocationKind, player,
                        item.X, item.Y, item.QuarterTurns, item.Slot); return true;
                default: return false; // 在途仍属于副本权威，不从账号Profile补造飞行状态。
            }
        }

        public bool TryGetBinding(int slot, out string id)
        { id = null; if (_disposed || slot < 0 || slot >= _tools.Length || _tools[slot] == null) return false; id = _tools[slot]; return true; }

        internal void BindHeldSelection(AccountHeldItemSelection selection)
        {
            if (_disposed || selection == null) return;
            HeldSelection = selection; selection.Reconcile(this);
        }

        /// <summary>
        /// 新旧完整Profile/独立Canvas只复用手持选择，不复用任何可变资产。不同账号不能承接选择，
        /// 更旧Profile也不能把新选择回滚；同版本多视图绑定不会重复发布事件。
        /// </summary>
        internal bool TryShareHeldSelectionFrom(AccountInventoryPresentationSource owner)
        {
            if (_disposed || owner == null || owner._disposed || PlayerId != owner.PlayerId || Version < owner.Version) return false;
            BindHeldSelection(owner.HeldSelection); return true;
        }

        internal bool TrySelectToolForPresentation(int toolSlot)
            => !_disposed && HeldSelection != null && HeldSelection.TrySelectTool(this, toolSlot);

        internal bool TrySelectConfirmedWandForPresentation(int slot)
            => !_disposed && HeldSelection != null && slot == _selectedSlot && _selectedWand != null
                && HeldSelection.TrySelectConfirmedWand(this);
        public bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot) { snapshot = null; return false; }
        public bool TryCopyContainer(string itemId, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity)
        {
            count = 0; total = capacity = 0;
            if (_disposed || !_profile.Items.TryGet(itemId, out var item) || item.Composition == null) return false;
            count = item.Composition.CopyTo(buffer); total = item.Composition.Total; capacity = item.Composition.Capacity; return true;
        }
        internal void BindGroundContext(Transform actor, string sceneKey)
        { _groundActor = actor; _worldSceneKey = sceneKey; }
        public bool TryGetGroundItem(string id, out OnlineAccountWorldItemSnapshot world)
        {
            world = null;
            if (_disposed || string.IsNullOrEmpty(id) || !_ground.TryGetValue(id, out var value)) return false;
            world = value.CopyValue(); return true;
        }
        public bool IsGroundItemNearby(string id, Transform actor, float reach = 3f)
        {
            if (_disposed || string.IsNullOrEmpty(id) || actor == null || _groundActor == null || actor != _groundActor
                || !float.IsFinite(reach) || reach < 0 || actor.gameObject.scene != _groundActor.gameObject.scene
                || !_ground.TryGetValue(id, out var world) || world.Pose.WorldSceneKey != _worldSceneKey) return false;
            var pose = world.Pose;
            return (new Vector3(pose.PositionX, pose.PositionY, pose.PositionZ) - actor.position).sqrMagnitude <= reach * reach;
        }
        /// <summary>F拾取只选择原规则的第一个合法目标，预览不写Store；最终空间/数量仍由服务器事务重算。</summary>
        public bool TryPrepareGroundPickup(string itemId, out OnlineAccountInventoryCommandIntent intent, out string error)
        {
            intent = default; error = "物品已不在本人附近或尚未完整确认。";
            if (!IsGroundItemNearby(itemId, _groundActor) || !_read.TryGetView(itemId, out var item)
                || !_ground.TryGetValue(itemId, out var world)) return false;
            intent = new OnlineAccountInventoryCommandIntent { Kind = OnlineAccountInventoryCommandKind.Pickup,
                PlayerId = PlayerId, SceneHandle = _groundActor.gameObject.scene.handle, ExpectedVersion = Version,
                ItemId = itemId, WorldOwnerId = world.WorldOwnerId, TargetParentInstanceId = "", TargetSlot = -1 };
            if (item.Kind == ItemKind.Spell)
                for (int slot = 0; slot < SpellStorageCapacity; slot++)
                {
                    bool occupied = false;
                    foreach (var entry in _profile.Items)
                        if (entry.Location.Kind == ItemLocationKind.SpellStorage && entry.Location.OwnerId == PlayerId && entry.Location.SlotIndex == slot)
                        { occupied = true; break; }
                    if (!occupied) { intent.TargetLocationKind = (int)ItemLocationKind.SpellStorage; intent.TargetSlot = slot; error = null; return true; }
                }
            if (item.Kind == ItemKind.Backpack)
            {
                if (_backpack != null) { error = "背包装备位已占用，请先整理装备。"; return false; }
                intent.TargetLocationKind = (int)ItemLocationKind.EquippedBackpack; intent.TargetSlot = 0; error = null; return true;
            }
            if (_backpack == null || !_read.TryGetView(_backpack, out var bag)) { error = "没有可装入物品的背包。"; return false; }
            var preview = _read as IInventoryPlacementPreview;
            for (int turn = 0; turn < 4; turn++)
                for (int y = 0; y <= bag.Rules.BackpackHeight - item.Rules.Shape.GetHeight(turn); y++)
                    for (int x = 0; x <= bag.Rules.BackpackWidth - item.Rules.Shape.GetWidth(turn); x++)
                        if (preview.PreviewPlacement(itemId, _backpack, new GridPlacement(x, y, turn), false, Version) == InventoryFailureReason.None)
                        {
                            intent.TargetLocationKind = (int)ItemLocationKind.Backpack; intent.TargetParentInstanceId = _backpack;
                            intent.GridX = x; intent.GridY = y; intent.QuarterTurns = turn; error = null; return true;
                        }
            error = "当前背包放不下，请先整理或拖到明确位置。"; return false;
        }
        internal static bool ValidWorldOwner(string value)
        {
            if (value == null || value.Length != 32) return false;
            for (int i = 0; i < value.Length; i++) if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }
        public void Dispose() { _disposed = true; _read = null; _profile = null; _programs = null; _ground.Clear(); _groundActor = null; }
        private static bool Fail(string message, out string error) { error = message; return false; }

        private sealed class ProgramsOnly : IWandLoadoutReadOnly
        {
            private readonly IWandLoadoutReadOnly _source;
            private readonly string _selected;
            private readonly uint _version;
            public ProgramsOnly(IWandLoadoutReadOnly source, string selected, uint version)
            { _source = source; _selected = selected; _version = version; }
            public string InventoryId => _source.InventoryId;
            public string SelectedWandId => _selected;
            public uint ResourceVersion => _version;
            public bool TryGetSlot(int slot, out WandStateSnapshot state) { state = default; return false; }
            public bool TryGetState(string id, out WandStateSnapshot state) { state = default; return false; }
            public bool TryGetProgram(string id, out WandProgramSnapshot program) => _source.TryGetProgram(id, out program);
        }
    }

    /// <summary>
    /// Session级只读手持状态：Profile仍由服务器确认，数字键仅决定本机当前展示哪个已装备工具。
    /// 同Scene的Tab/仓库/地面Source都借此对象；任何Source.Dispose均不拥有它的清理权。
    /// </summary>
    internal sealed class AccountHeldItemSelection
    {
        private string _player, _confirmedWand, _tool;
        private uint _profileVersion;
        private int _slot = -1;
        internal uint Revision { get; private set; } = 1;

        internal void Reconcile(AccountInventoryPresentationSource source)
        {
            if (source == null || source.ReadOnly == null || source.Version < _profileVersion && source.PlayerId == _player) return;
            if (_player != source.PlayerId)
            { _player = source.PlayerId; _profileVersion = 0; _slot = -1; _tool = null; _confirmedWand = null; }
            if (_profileVersion == source.Version) return;
            string previous = _tool; int previousSlot = _slot;
            // 服务器确认另一根杖时回到杖模式；普通买卖/材料读数更新保持同一真实工具槽。
            if (_confirmedWand != source.ConfirmedWandId || !IsTool(source, _slot, out _tool))
            { _slot = -1; _tool = null; }
            _confirmedWand = source.ConfirmedWandId; _profileVersion = source.Version;
            if (previous != _tool || previousSlot != _slot) Revision++;
        }

        internal int GetSlot(AccountInventoryPresentationSource source)
            => IsCurrent(source) && _tool != null ? HeldItemSlots.WandCount + _slot : source.ConfirmedWandSlot;
        internal string GetItem(AccountInventoryPresentationSource source)
            => IsCurrent(source) && _tool != null ? _tool : source.ConfirmedWandId;
        private bool IsCurrent(AccountInventoryPresentationSource source)
            => source.ReadOnly != null && source.PlayerId == _player && source.Version == _profileVersion;
        private static bool IsTool(AccountInventoryPresentationSource source, int slot, out string id)
        {
            id = null;
            return source.TryGetBinding(slot, out id) && source.ReadOnly.TryGetView(id, out var item)
                && item.Location.Kind == ItemLocationKind.EquippedTool && item.Location.OwnerId == source.PlayerId
                && item.Location.SlotIndex == slot && item.Rules.UseKind != ItemUseKind.None;
        }
        internal bool TrySelectTool(AccountInventoryPresentationSource source, int slot)
        {
            if (!IsCurrent(source) || !IsTool(source, slot, out var id)) return false;
            if (_slot == slot && _tool == id) return true;
            _slot = slot; _tool = id; Revision++; Publish(source); return true;
        }
        internal bool TrySelectConfirmedWand(AccountInventoryPresentationSource source)
        {
            if (!IsCurrent(source) || source.ConfirmedWandId == null) return false;
            if (_tool == null) return true;
            _slot = -1; _tool = null; Revision++; Publish(source); return true;
        }
        private void Publish(AccountInventoryPresentationSource source)
            => EventBus<ToolbeltChangedEvent>.Publish(new ToolbeltChangedEvent { InventoryId = source.InventoryId,
                ItemId = GetItem(source), SelectedSlot = GetSlot(source), ConfigurationVersion = source.ConfigurationVersion,
                SelectionVersion = Revision });
    }
}
