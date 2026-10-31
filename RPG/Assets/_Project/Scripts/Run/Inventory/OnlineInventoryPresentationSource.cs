using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;

namespace Game.Run
{
    /// <summary>
    /// Client 的纯库存读模型。复用单机原 UI 的只读接口，但不创建 Session、资格策略、施法事务或资源 Tick。
    /// Source 与各只读接口保持同一对象，完整快照校验后只交换内部 State；因此打开中的原 Tab 不必每次重绑。
    /// 认证/解包仍属于上层职责，TryInstall 只是第二道身份、父链、容量和数值校验，不代表网络身份认证。
    /// </summary>
    public sealed class OnlineInventoryPresentationSource : IInventoryPresentationSource, IInventoryReadOnly,
        IWandLoadoutReadOnly, IToolbeltReadOnly, ISpellUseReadOnly, IOnlineInventoryCommandContext, IDisposable
    {
        private const int MaximumItems = 99;
        private sealed class WandEntry
        {
            internal string Id;
            internal int Slot;
            internal WandStateSnapshot State;
            internal WandProgramSnapshot Program;
            internal WandRuntimeState Resource;
            internal uint LastResourceTick;
        }
        private sealed class State
        {
            internal InventoryReadModel Inventory;
            internal WandEntry[] Wands;
            internal Dictionary<string, SpellUseState> SpellUses;
            internal string SelectedWandId;
            internal int SelectedSlot;
            internal ulong Sequence;
            internal uint ResourceVersion, SelectionVersion, SpellUseVersion;
        }

        private readonly ItemCatalogIndex _catalog;
        private readonly OnlineInventoryPresentationIdentity _identity;
        private State _state;
        private bool _disposed;
        private uint _resourcePresentationVersion;
        public bool HasSnapshot => _state != null;
        public ulong SnapshotSequence => _state != null ? _state.Sequence : 0;
        public uint ConnectionEpoch => _identity.ConnectionEpoch;
        ulong IOnlineInventoryCommandContext.ActorId => _identity.ActorId;

        public IInventoryReadOnly ReadOnly => this;
        public IWandLoadoutReadOnly WandsReadOnly => this;
        public IToolbeltReadOnly ToolbeltReadOnly => this;
        public ISpellUseReadOnly SpellUsesReadOnly => this;
        // 本合同没有钱包、真实 Bag、地面/材料数据。null/false明确表示尚未接入，不能用默认0或虚构容器冒充权威值。
        public IWalletReadOnly WalletReadOnly => null;
        public bool UseSpellProgrammingV2 => true;
        public bool CanAccessWarehouse => false;
        public string WorldSceneKey => _state != null ? _identity.WorldSceneKey : null;
        public string EquippedBackpackId => null;
        public int SpellStorageCapacity => !_disposed && _state != null ? _catalog.SpellStorageCapacity : 0;
        public bool HasUnsavedChanges => false;
        public string InitializationMessage => _disposed ? "线上库存展示已退出。" :
            _state != null ? "已安装服务器库存读数；支持已有法杖和法术专区，普通背包及仓库尚未接入。" : "等待服务器完整库存快照。";

        private OnlineInventoryPresentationSource(ItemCatalogIndex catalog, in OnlineInventoryPresentationIdentity identity)
        { _catalog = catalog; _identity = identity; }

        /// <summary>
        /// 创建时必须同时提供可信连接身份和待验证快照。候选失败不发布半初始化 Source，
        /// 不调用 OnlineOwnedWorkspaceFactory：后者属于 Authority，会注册施法命令并建立本局资格策略。
        /// </summary>
        public static bool TryCreate(ItemCatalogIndex catalog, OnlineInventoryPresentationIdentity identity,
            OnlineInventoryPresentationSnapshot snapshot, out OnlineInventoryPresentationSource source, out string error)
        {
            source = null; error = null;
            if (catalog == null || !ValidIdentity(identity)) return Fail("缺少可信线上库存身份或目录。", out error);
            var candidate = new OnlineInventoryPresentationSource(catalog, identity);
            if (!candidate.TryInstall(snapshot, out error)) return false;
            source = candidate; return true;
        }

        /// <summary>
        /// 仅由已认证的组合层在收到完整快照时离散调用。所有分配、父链验证和程序数组构造均在此完成，
        /// 不能从 Update 每帧重建。先构造不可见候选，最后一次引用交换；失败时旧界面继续读取上一份一致数据。
        /// 成功后的 UI InventoryChanged/ToolbeltChanged 通知由组合层按实际版本变化发布，避免资源包触发全背包重建。
        /// </summary>
        public bool TryInstall(OnlineInventoryPresentationSnapshot snapshot, out string error)
        {
            error = null;
            if (_disposed) return Fail("线上库存展示已退出，拒绝迟到快照。", out error);
            if (!TryBuild(snapshot, out var candidate, out error)) return false;
            if (_state != null)
            {
                if (candidate.Sequence <= _state.Sequence) return Fail("库存快照序号未推进。", out error);
                if (candidate.Inventory.Version < _state.Inventory.Version || candidate.ResourceVersion < _state.ResourceVersion
                    || candidate.SelectionVersion < _state.SelectionVersion || candidate.SpellUseVersion < _state.SpellUseVersion)
                    return Fail("库存、资源或选择版本倒退。", out error);
                for (int i = 0; i < candidate.Wands.Length; i++)
                {
                    var previous = FindWand(_state, candidate.Wands[i].Id);
                    if (previous != null && candidate.Wands[i].Program.ProgramRevision < previous.Program.ProgramRevision)
                        return Fail("法杖程序版本倒退。", out error);
                }
            }
            bool first = _state == null;
            _state = candidate;
            if (first) _resourcePresentationVersion = candidate.ResourceVersion;
            else AdvanceResourcePresentationVersion();
            return true;
        }

        /// <summary>
        /// 安装服务器对一根已知法杖的高频资源事实。保留同一个 Source、数组和读接口，既不调用 Tick
        /// 预测回蓝，也不重建库存/卡次数。Epoch、Tick、实例与 ProgramRevision 必须同时符合当前快照。
        /// 内部 Resource 只是数值容器，CommitExecution 不触发施法；唯一写入资格仍来自已认证的上层事实。
        /// </summary>
        public bool TryApplyResources(uint epoch, uint serverTick, string wandId, uint programRevision, float currentMana, float cooldown)
        {
            if (_disposed || _state == null || epoch != _identity.ConnectionEpoch || serverTick == 0) return false;
            var entry = FindWand(_state, wandId);
            if (entry == null || entry.Program.ProgramRevision != programRevision
                || unchecked((int)(serverTick - entry.LastResourceTick)) <= 0
                || !WandRuntimeState.IsFinite(currentMana) || currentMana < 0 || currentMana > entry.State.MaxMana
                || !WandRuntimeState.IsFinite(cooldown) || cooldown < 0) return false;
            entry.LastResourceTick = serverTick;
            if (entry.State.CurrentMana == currentMana && entry.State.CooldownRemaining == cooldown) return true;
            entry.Resource.CommitExecution(currentMana, cooldown);
            entry.State = new WandStateSnapshot(entry.Resource, entry.Slot, _state.SelectedWandId == entry.Id, true);
            AdvanceResourcePresentationVersion();
            return true;
        }

        private void AdvanceResourcePresentationVersion()
        {
            // 显示修订与 Server ResourceVersion 是不同序列。不能把每次本地刷新次数写回 Server
            // 版本比较，否则若若干资源包先到，后来的合法完整快照就会被误判为“版本倒退”。
            _resourcePresentationVersion++;
            if (_resourcePresentationVersion == 0) _resourcePresentationVersion++;
        }

        private bool TryBuild(OnlineInventoryPresentationSnapshot snapshot, out State candidate, out string error)
        {
            candidate = null; error = null;
            if (snapshot == null || snapshot.SchemaVersion != OnlineInventoryPresentationSnapshot.CurrentSchemaVersion
                || snapshot.ConnectionEpoch != _identity.ConnectionEpoch || snapshot.SnapshotSequence == 0
                || snapshot.ResourceVersion == 0 || snapshot.SelectionVersion == 0 || snapshot.SpellUseVersion == 0)
                return Fail("线上库存 Schema、连接代次、序号或版本非法。", out error);
            var assets = snapshot.Assets;
            if (assets == null || assets.SchemaVersion != OnlineOwnedAssetSnapshot.CurrentSchemaVersion
                || assets.RunId != _identity.RunId || assets.PlayerId != _identity.PlayerId || assets.ActorId != _identity.ActorId
                || assets.InventoryId != _identity.InventoryId || assets.GameplayConfigHash != _identity.GameplayConfigHash
                || assets.InventoryVersion == 0 || assets.Items == null || assets.Items.Length < 1 || assets.Items.Length > MaximumItems)
                return Fail("线上库存身份、配置 Hash 或容量不符合当前连接。", out error);

            if (!TryImportItems(assets, out var store, out error)) return false;
            if (!store.TryGetLocation(assets.SelectedWandInstanceId, out var selected)
                || selected.Kind != ItemLocationKind.EquippedWand || selected.OwnerId != _identity.PlayerId)
                return Fail("当前选择不是本角色装备的真实法杖。", out error);

            int equippedCount = 0, spellCount = 0;
            foreach (var entry in store)
                if (entry.Location.Kind == ItemLocationKind.EquippedWand) equippedCount++; else spellCount++;
            if (snapshot.Wands == null || snapshot.Wands.Length != equippedCount
                || snapshot.SpellUses == null || snapshot.SpellUses.Length != spellCount)
                return Fail("快照必须完整提供每根法杖资源和每张卡的资格读数。", out error);

            var wands = new WandEntry[equippedCount];
            var wandIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < wands.Length; i++)
            {
                var current = snapshot.Wands[i];
                if (current == null || string.IsNullOrWhiteSpace(current.InstanceId) || !wandIds.Add(current.InstanceId)
                    || !store.TryGet(current.InstanceId, out var item) || !store.TryGetLocation(current.InstanceId, out var location)
                    || location.Kind != ItemLocationKind.EquippedWand || !_catalog.TryGetWandStats(item, out var stats)
                    || current.ProgramRevision == 0 || !WandRuntimeState.IsFinite(current.CurrentMana)
                    || current.CurrentMana < 0 || current.CurrentMana > stats.MaxMana
                    || !WandRuntimeState.IsFinite(current.CooldownRemaining) || current.CooldownRemaining < 0)
                    return Fail("法杖资源缺失、重复或超出合法范围。", out error);

                // WandStateSnapshot沿用原UI合同。临时对象只转换已验证数值，随后丢弃；不创建会Tick/扣费的服务。
                var resource = new WandRuntimeState(item.InstanceId, stats.MaxMana, stats.ManaRegenPerSecond, stats.CastCooldown);
                resource.CommitExecution(current.CurrentMana, current.CooldownRemaining);
                var spells = new SpellDefinition[stats.ProgramCapacity]; var ids = new string[stats.ProgramCapacity];
                foreach (var card in store)
                {
                    if (card.Location.Kind != ItemLocationKind.WandSlot || card.Location.OwnerId != item.InstanceId) continue;
                    _catalog.TryGet(card.Item.ContentId, out var definition);
                    spells[card.Location.SlotIndex] = definition.SpellDefinition; ids[card.Location.SlotIndex] = card.Item.InstanceId;
                }
                wands[i] = new WandEntry
                {
                    Id = item.InstanceId, Slot = location.SlotIndex,
                    Resource = resource, LastResourceTick = snapshot.ServerTick,
                    State = new WandStateSnapshot(resource, location.SlotIndex, assets.SelectedWandInstanceId == item.InstanceId, true),
                    Program = new WandProgramSnapshot(item.InstanceId, current.ProgramRevision, stats.BaseDraws,
                        new WandProgramProjection(spells, ids))
                };
            }

            var spellUses = new Dictionary<string, SpellUseState>(spellCount, StringComparer.Ordinal);
            for (int i = 0; i < snapshot.SpellUses.Length; i++)
            {
                var use = snapshot.SpellUses[i];
                if (use == null || string.IsNullOrWhiteSpace(use.InstanceId) || spellUses.ContainsKey(use.InstanceId)
                    || !store.TryGet(use.InstanceId, out var item) || !_catalog.TryGet(item.ContentId, out var definition)
                    || definition.Kind != ItemKind.Spell)
                    return Fail("法术资格读数缺失、重复或指向非本批卡实例。", out error);
                // IsQualification只是目录分类，Eligible是服务器给出的本局事实；不创建RaidSpellEligibility去重新扣费。
                spellUses.Add(item.InstanceId, new SpellUseState(item.RemainingCharges, item.HasUnlimitedCharges,
                    RaidSpellEligibility.IsQualification(definition.SpellDefinition), use.Eligible));
            }

            // SinglePlayerProfile在这里仅是既有InventoryReadModel需要的私有纯数据壳；没有Repository或MonoBehaviour。
            // 不暴露Profile/Store，View无法沿接口拿到TryMove、Save或施法事务入口。
            candidate = new State
            {
                Inventory = new InventoryReadModel(new SinglePlayerProfile(_identity.PlayerId, store), _identity.InventoryId),
                Wands = wands, SpellUses = spellUses, SelectedWandId = assets.SelectedWandInstanceId, SelectedSlot = selected.SlotIndex,
                Sequence = snapshot.SnapshotSequence, ResourceVersion = snapshot.ResourceVersion,
                SelectionVersion = snapshot.SelectionVersion, SpellUseVersion = snapshot.SpellUseVersion
            };
            return true;
        }

        private bool TryImportItems(OnlineOwnedAssetSnapshot assets, out ItemOwnershipStore store, out string error)
        {
            store = null; error = null;
            var entries = new InventoryEntry[assets.Items.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                var item = assets.Items[i];
                if (item == null || item.Revision == 0 || item.ParentInstanceId == null
                    || !_catalog.TryGet(item.ContentId, out var definition)) return Fail("物品身份、修订或ContentId非法。", out error);
                bool equipped = item.LocationKind == (int)ItemLocationKind.EquippedWand;
                bool installed = item.LocationKind == (int)ItemLocationKind.WandSlot;
                bool storage = item.LocationKind == (int)ItemLocationKind.SpellStorage;
                if ((!equipped && !installed && !storage) || equipped && (definition.Kind != ItemKind.Wand || item.ParentInstanceId.Length != 0)
                    || installed && (definition.Kind != ItemKind.Spell || string.IsNullOrWhiteSpace(item.ParentInstanceId))
                    || storage && (definition.Kind != ItemKind.Spell || item.ParentInstanceId.Length != 0))
                    return Fail("当前合同仅接受装备法杖、安装卡及玩家法术专区；不能编造普通Bag。", out error);

                WandStats? instanceStats = null;
                if (equipped)
                {
                    if (item.UseTemplateWandStats)
                    {
                        if (item.WandStats != null || !_catalog.TryGetWandStats(item.ContentId, out _))
                            return Fail("模板法杖标记与实例属性冲突。", out error);
                    }
                    else
                    {
                        var incoming = item.WandStats;
                        if (incoming == null || incoming.AlwaysCastContentIds == null) return Fail("法杖缺少完整实例固定属性。", out error);
                        var stats = new WandStats(incoming.MaxMana, incoming.ManaRegenPerSecond, incoming.BaseCastDelay,
                            incoming.ProgramCapacity, incoming.BaseDraws, incoming.BaseReloadTime, incoming.BaseSpreadDegrees,
                            incoming.Shuffle, incoming.AlwaysCastContentIds);
                        if (!stats.IsValid || !_catalog.ValidateWandTraits(stats)) return Fail("法杖实例固定属性非法。", out error);
                        instanceStats = stats; // WandStats构造会复制固定法术数组，不与可变网络DTO共享所有权。
                    }
                }
                else if (item.UseTemplateWandStats || item.WandStats != null) return Fail("法术卡不能携带法杖属性。", out error);

                entries[i] = new InventoryEntry(new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges,
                    item.HasUnlimitedCharges, rolledWandStats: instanceStats), new ItemLocation(
                    equipped ? ItemLocationKind.EquippedWand : storage ? ItemLocationKind.SpellStorage : ItemLocationKind.WandSlot,
                    equipped || storage ? _identity.PlayerId : item.ParentInstanceId, item.X, item.Y, item.QuarterTurns, item.Slot));
            }
            // 原恢复器先收齐实例再检查完整父链、格位和重复ID；数组顺序不决定父实例是否存在。
            if (!ItemOwnershipStore.TryRestore(_catalog, entries, assets.InventoryVersion, out store, out var reason))
                return Fail("线上库存完整归属校验失败：" + reason, out error);
            return true;
        }

        public void Dispose() { _disposed = true; _state = null; _resourcePresentationVersion = 0; }
        public bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot) { snapshot = null; return false; }
        public bool TryCopyContainer(string itemId, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity)
        { count = 0; total = capacity = 0; return false; }

        // 以下为原View的稳定只读Adapter：每次读取当前已发布State，不把UI缓存永久绑在上一份InventoryReadModel。
        public string InventoryId => _state != null ? _identity.InventoryId : null;
        string IInventoryReadOnly.ActorId => _state != null ? _identity.PlayerId : null;
        uint IInventoryReadOnly.Version => _state != null ? _state.Inventory.Version : 0;
        public int Count => _state != null ? _state.Inventory.Count : 0;
        public int WarehouseCount => 0;
        public bool TryGetView(string instanceId, out InventoryItemSnapshot view)
        { view = default; return _state != null && _state.Inventory.TryGetView(instanceId, out view); }
        public int CopyWarehouseTo(InventoryItemSnapshot[] buffer) => 0;
        public int CopyItemsTo(InventoryItemSnapshot[] buffer) => _state != null ? _state.Inventory.CopyItemsTo(buffer) : 0;
        public string SelectedWandId => _state != null ? _state.SelectedWandId : null;
        public uint ResourceVersion => _state != null ? _resourcePresentationVersion : 0;
        public bool TryGetSlot(int slot, out WandStateSnapshot state)
        {
            state = default; if (_state == null) return false;
            for (int i = 0; i < _state.Wands.Length; i++)
                if (_state.Wands[i].Slot == slot) { state = _state.Wands[i].State; return true; }
            return false;
        }
        bool IWandLoadoutReadOnly.TryGetState(string id, out WandStateSnapshot state)
        { var entry = FindWand(_state, id); state = entry != null ? entry.State : default; return entry != null; }
        public bool TryGetProgram(string id, out WandProgramSnapshot program)
        { var entry = FindWand(_state, id); program = entry != null ? entry.Program : default; return entry != null; }
        public uint ConfigurationVersion => _state != null ? _state.Inventory.Version : 0;
        public uint SelectionVersion => _state != null ? _state.SelectionVersion : 0;
        public int SelectedSlot => _state != null ? _state.SelectedSlot : -1;
        public string SelectedItemId => SelectedWandId;
        public bool TryGetBinding(int toolSlot, out string instanceId) { instanceId = null; return false; }
        public string RaidId => _state != null ? _identity.RunId : null;
        uint ISpellUseReadOnly.Version => _state != null ? _state.SpellUseVersion : 0;
        bool ISpellUseReadOnly.TryGetState(string instanceId, out SpellUseState state)
        { state = default; return _state != null && instanceId != null && _state.SpellUses.TryGetValue(instanceId, out state); }

        private static WandEntry FindWand(State state, string id)
        {
            if (state == null || id == null) return null;
            for (int i = 0; i < state.Wands.Length; i++) if (state.Wands[i].Id == id) return state.Wands[i];
            return null;
        }
        private static bool ValidIdentity(in OnlineInventoryPresentationIdentity identity) =>
            !string.IsNullOrWhiteSpace(identity.RunId) && !string.IsNullOrWhiteSpace(identity.PlayerId)
            && !string.IsNullOrWhiteSpace(identity.InventoryId) && identity.ActorId >= 1 && identity.ActorId <= 4
            && identity.ConnectionEpoch > 0 && !string.IsNullOrWhiteSpace(identity.WorldSceneKey) && ValidHash(identity.GameplayConfigHash);
        private static bool ValidHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            for (int i = 0; i < hash.Length; i++) if (!(hash[i] >= '0' && hash[i] <= '9') && !(hash[i] >= 'a' && hash[i] <= 'f')) return false;
            return true;
        }
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
