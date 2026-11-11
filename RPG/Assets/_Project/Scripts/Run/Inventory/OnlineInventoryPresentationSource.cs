using System;
using System.Collections.Generic;
using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// Client 的纯库存读模型。复用单机原 UI 的只读接口，但不创建 Session、资格策略、施法事务或资源 Tick。
    /// Source 与各只读接口保持同一对象，完整快照校验后只交换内部 State；因此打开中的原 Tab 不必每次重绑。
    /// 认证/解包仍属于上层职责，TryInstall 只是第二道身份、父链、容量和数值校验，不代表网络身份认证。
    /// </summary>
    public sealed class OnlineInventoryPresentationSource : IInventoryPresentationSource, IInventoryReadOnly, IInventoryPlacementPreview,
        IWandLoadoutReadOnly, IToolbeltReadOnly, ISpellUseReadOnly, IOnlineInventoryCommandContext, IOnlineGroundInventoryContext, IWalletReadOnly, IDisposable
    {
        private const int MaximumItems = OnlineOwnedAssetSnapshot.MaximumItems;
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
            internal ItemOwnershipStore Items;
            internal WandEntry[] Wands;
            internal Dictionary<string, SpellUseState> SpellUses;
            internal string EquippedBackpackId;
            internal string[] ToolBindings;
            internal string SelectedWandId;
            internal string SelectedItemId;
            internal uint SelectedToolSlotPlusOne;
            internal int SelectedSlot;
            internal ulong Sequence;
            internal uint ResourceVersion, SelectionVersion, SpellUseVersion, GroundVersion;
            internal long Gold;
            internal uint CurrencyVersion;
            internal Dictionary<string, GroundPose> GroundPoses;
            internal Dictionary<string, uint> GroundItemRevisions;
            internal OnlineCoinSnapshot[] GroundCoins;
            internal bool ResearchOpen;
            internal uint ResearchSessionVersion;
            internal OnlineWandResearchReadModel Research;
        }

        private readonly struct GroundPose
        {
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal readonly uint Revision;
            internal GroundPose(Vector3 position, Quaternion rotation, uint revision)
            { Position = position; Rotation = rotation; Revision = revision; }
        }

        private readonly ItemCatalogIndex _catalog;
        private readonly OnlineInventoryPresentationIdentity _identity;
        private State _state;
        private bool _disposed;
        private uint _resourcePresentationVersion;
        public bool HasSnapshot => _state != null;
        public uint GroundVersion => _state != null ? _state.GroundVersion : 0;
        /// <summary>
        /// 表现层预分配最多12项缓冲，只复制已安装的值事实；不暴露内部数组或提供领取/金币写入口。
        /// 本方法可被表现更新轮询，既不分配对象，也不在Client推算新的掉落或余额。
        /// </summary>
        public int CopyGroundCoinsTo(OnlineCoinSnapshot[] buffer)
        {
            if (_disposed || _state == null || buffer == null) return 0;
            int count = Math.Min(buffer.Length, _state.GroundCoins.Length);
            Array.Copy(_state.GroundCoins, buffer, count);
            return count;
        }
        // 同Run表现代理只读绑定当前候选Store；UI/Multiplayer仍不可取得此内部写引用。
        internal ItemOwnershipStore PresentationItems => _state != null ? _state.Items : null;
        public bool TryGetGroundPose(string id, out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = Quaternion.identity;
            if (_disposed || _state == null || id == null || !_state.GroundPoses.TryGetValue(id, out var pose)) return false;
            position = pose.Position; rotation = pose.Rotation; return true;
        }
        public ulong SnapshotSequence => _state != null ? _state.Sequence : 0;
        public uint ConnectionEpoch => _identity.ConnectionEpoch;
        public bool ResearchOpen => !_disposed && _state != null && _state.ResearchOpen;
        public uint ResearchSessionVersion => _state != null ? _state.ResearchSessionVersion : 0;
        public int ReplayCount => _state?.Research != null ? _state.Research.Archive.Count : 0;
        public WandExecutionReplay GetReplayOldestFirst(int index) => _state?.Research != null ? _state.Research.Archive.GetOldestFirst(index) : null;
        public bool TryGetLatestReplay(string wandId, out WandExecutionReplay replay)
        { replay = null; return !_disposed && _state?.Research != null && _state.Research.Archive.TryGetLatest(wandId, out replay); }
        ulong IOnlineInventoryCommandContext.ActorId => _identity.ActorId;

        public IInventoryReadOnly ReadOnly => this;
        public IWandLoadoutReadOnly WandsReadOnly => this;
        public IToolbeltReadOnly ToolbeltReadOnly => this;
        public ISpellUseReadOnly SpellUsesReadOnly => this;
        // 原共享UI读取同一余额，只投影已验证DS事实；不创建第二钱包，也不向Client开放记账/永久提交入口。
        public IWalletReadOnly WalletReadOnly => !_disposed && _state != null ? this : null;
        public long Currency => _state != null ? _state.Gold : 0;
        uint IWalletReadOnly.Version => _state != null ? _state.CurrencyVersion : 0;
        public bool UseSpellProgrammingV2 => true;
        public bool CanAccessWarehouse => false;
        public string WorldSceneKey => _state != null ? _identity.WorldSceneKey : null;
        public string EquippedBackpackId => _state != null ? _state.EquippedBackpackId : null;
        public int SpellStorageCapacity => !_disposed && _state != null ? _catalog.SpellStorageCapacity : 0;
        public bool HasUnsavedChanges => false;
        public string InitializationMessage => _disposed ? "线上库存展示已退出。" :
            _state != null ? "已安装服务器真实随身库存与容器读数。" : "等待服务器完整库存快照。";

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
            if (!TryValidateInstall(snapshot, out var candidate, out error)) return false;
            bool first = _state == null;
            _state = candidate;
            if (first) _resourcePresentationVersion = candidate.ResourceVersion;
            else AdvanceResourcePresentationVersion();
            return true;
        }

        /// <summary>
        /// 排队/排序前验证完整数据及当前 Source 的时序栅栏，只构造待丢弃候选，不安装、不通知、不确认序号。
        /// 与 TryInstall 共用同一验证入口，避免 fresh Source 的纯结构检查遗漏已安装版本回退。
        /// 预检后仍须正式安装时再次验证：两次调用之间可能已经收到另一份合法快照或资源事实。
        /// </summary>
        public bool CanInstall(OnlineInventoryPresentationSnapshot snapshot, out string error)
            => TryValidateInstall(snapshot, out _, out error);

        private bool TryValidateInstall(OnlineInventoryPresentationSnapshot snapshot, out State candidate, out string error)
        {
            candidate = null; error = null;
            if (_disposed) return Fail("线上库存展示已退出，拒绝迟到快照。", out error);
            if (!TryBuild(snapshot, out candidate, out error)) return false;
            if (_state != null)
            {
                if (candidate.Sequence <= _state.Sequence) return Fail("库存快照序号未推进。", out error);
                if (candidate.Inventory.Version < _state.Inventory.Version || candidate.ResourceVersion < _state.ResourceVersion
                    || candidate.SelectionVersion < _state.SelectionVersion || candidate.SpellUseVersion < _state.SpellUseVersion)
                    return Fail("库存、资源或选择版本倒退。", out error);
                if (candidate.CurrencyVersion < _state.CurrencyVersion)
                    return Fail("统一金币修订倒退。", out error);
                if (candidate.CurrencyVersion == _state.CurrencyVersion && candidate.Gold != _state.Gold)
                    return Fail("相同货币修订不能改变统一金币余额。", out error);
                if (candidate.ResearchSessionVersion < _state.ResearchSessionVersion
                    || candidate.ResearchSessionVersion == _state.ResearchSessionVersion && candidate.ResearchOpen != _state.ResearchOpen)
                    return Fail("研究会话版本倒退或相同版本改变Open事实。", out error);
                if (candidate.SelectionVersion == _state.SelectionVersion
                    && (candidate.SelectedToolSlotPlusOne != _state.SelectedToolSlotPlusOne || candidate.SelectedItemId != _state.SelectedItemId
                        || candidate.SelectedWandId != _state.SelectedWandId || candidate.SelectedSlot != _state.SelectedSlot))
                    return Fail("相同选择版本不能改变手持实例、槽位或最后法杖。", out error);
                if (candidate.GroundVersion < _state.GroundVersion)
                    return Fail("公开地面修订倒退或已知地面被缺省值清空。", out error);
                if (candidate.GroundVersion == _state.GroundVersion && !SameGround(candidate, _state))
                    return Fail("相同公开地面修订不能改实例、落地身份、位置或数量。", out error);
                for (int i = 0; i < candidate.Wands.Length; i++)
                {
                    var previous = FindWand(_state, candidate.Wands[i].Id);
                    if (previous != null && candidate.Wands[i].Program.ProgramRevision < previous.Program.ProgramRevision)
                        return Fail("法杖程序版本倒退。", out error);
                }
            }
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
            // 高频法杖资源只是余额事实；工具选中时不能借更新Mana/CD把UI选中恢复成最后法杖。
            entry.State = new WandStateSnapshot(entry.Resource, entry.Slot,
                entry.Slot >= 0 && _state.SelectedToolSlotPlusOne == 0 && _state.SelectedWandId == entry.Id, true);
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
                || assets.InventoryVersion == 0 || assets.Items == null || assets.Items.Length > MaximumItems)
                return Fail("线上库存身份、配置 Hash 或容量不符合当前连接。", out error);
            // 零余额是合法事实；CurrencyVersion是明确存在性的栅栏。不能把缺字段默认为已安装钱包。
            if (assets.Gold < 0 || assets.CurrencyVersion == 0)
                return Fail("线上统一金币余额或DS货币修订非法。", out error);

            if (!TryImportItems(assets, out var store, out error)) return false;
            bool hasSelectedWand = !string.IsNullOrEmpty(assets.SelectedWandInstanceId);
            ItemLocation selected = default;
            if (hasSelectedWand && (!store.TryGetLocation(assets.SelectedWandInstanceId, out selected)
                || selected.Kind != ItemLocationKind.EquippedWand || selected.OwnerId != _identity.PlayerId))
                return Fail("当前选择不是本角色装备的真实法杖。", out error);

            int wandCount = 0, spellCount = 0;
            string backpackId = null;
            var toolBindings = new string[HeldItemSlots.ToolCount];
            foreach (var entry in store)
            {
                if (entry.Location.Kind == ItemLocationKind.EquippedBackpack) backpackId = entry.Item.InstanceId;
                else if (entry.Location.Kind == ItemLocationKind.EquippedTool) toolBindings[entry.Location.SlotIndex] = entry.Item.InstanceId;
                _catalog.TryGet(entry.Item.ContentId, out var definition);
                if (definition.Kind == ItemKind.Wand) wandCount++;
                if (definition.Kind == ItemKind.Spell) spellCount++;
            }
            uint toolChoice = snapshot.SelectedToolSlotPlusOne;
            if (toolChoice > HeldItemSlots.ToolCount) return Fail("工具选择超出真实装备槽范围。", out error);
            string selectedItemId = assets.SelectedWandInstanceId;
            // 最后一根杖可以正常移走；空手是有效事实，不能因没有选中杖拒绝整个Owner背包。
            int selectedHeldSlot = hasSelectedWand ? selected.SlotIndex : -1;
            if (toolChoice != 0)
            {
                selectedItemId = toolBindings[(int)toolChoice - 1];
                if (selectedItemId == null || !store.TryGet(selectedItemId, out var tool)
                    || !_catalog.TryGetRules(tool.ContentId, out var toolRules) || toolRules.UseKind == ItemUseKind.None)
                    return Fail("工具选择必须指向当前玩家该槽真实绑定的可用道具。", out error);
                selectedHeldSlot = HeldItemSlots.WandCount + (int)toolChoice - 1;
            }
            if (snapshot.Wands == null || snapshot.Wands.Length != wandCount
                || snapshot.SpellUses == null || snapshot.SpellUses.Length != spellCount)
                return Fail("快照必须完整提供每根法杖资源和每张卡的资格读数。", out error);

            // Wands是完整携带资源事实，不等于三个装备槽。备用杖可编辑，真实ProgramRevision/Mana/CD由DS提供；
            // 客户端不从导入顺序创建rev1，也不因读到备用杖而授予选中或施法权。
            var wands = new WandEntry[wandCount];
            var wandIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < wands.Length; i++)
            {
                var current = snapshot.Wands[i];
                if (current == null || string.IsNullOrWhiteSpace(current.InstanceId) || !wandIds.Add(current.InstanceId)
                    || !store.TryGet(current.InstanceId, out var item) || !store.TryGetLocation(current.InstanceId, out var location)
                    || location.Kind != ItemLocationKind.EquippedWand && location.Kind != ItemLocationKind.Backpack
                    || !_catalog.TryGetWandStats(item, out var stats)
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
                int slot = location.Kind == ItemLocationKind.EquippedWand ? location.SlotIndex : -1;
                wands[i] = new WandEntry
                {
                    Id = item.InstanceId, Slot = slot,
                    Resource = resource, LastResourceTick = snapshot.ServerTick,
                    State = new WandStateSnapshot(resource, slot,
                        slot >= 0 && toolChoice == 0 && assets.SelectedWandInstanceId == item.InstanceId, true),
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

            // 先完整验证Owner随身闭包，再合并独立公开地面；地面根不能混进Owner资产消息来绕过归属校验。
            if (!TryImportGround(snapshot.Ground, store, assets.InventoryVersion, out var merged, out var groundPoses, out var groundRevisions, out error)) return false;
            if (!TryCopyGroundCoins(snapshot.Ground, out var groundCoins, out error)) return false;

            // SinglePlayerProfile在这里仅是既有InventoryReadModel需要的私有纯数据壳；没有Repository或MonoBehaviour。
            // 不暴露Profile/Store，View无法沿接口拿到TryMove、Save或施法事务入口。
            candidate = new State
            {
                Inventory = new InventoryReadModel(new SinglePlayerProfile(_identity.PlayerId, merged, currency: assets.Gold), _identity.InventoryId),
                Items = merged, EquippedBackpackId = backpackId, ToolBindings = toolBindings,
                Wands = wands, SpellUses = spellUses, SelectedWandId = assets.SelectedWandInstanceId,
                SelectedItemId = selectedItemId, SelectedToolSlotPlusOne = toolChoice, SelectedSlot = selectedHeldSlot,
                Sequence = snapshot.SnapshotSequence, ResourceVersion = snapshot.ResourceVersion,
                Gold = assets.Gold, CurrencyVersion = assets.CurrencyVersion,
                SelectionVersion = snapshot.SelectionVersion, SpellUseVersion = snapshot.SpellUseVersion,
                GroundVersion = snapshot.Ground != null ? snapshot.Ground.Version : 0, GroundPoses = groundPoses,
                GroundItemRevisions = groundRevisions, GroundCoins = groundCoins,
                ResearchOpen = snapshot.ResearchOpen, ResearchSessionVersion = snapshot.ResearchSessionVersion
            };
            // 研究与其Owner、资源和资格属于同一候选。任一程序/历史失败就拒绝整批，不能先发布新背包再补旧Draw。
            if (snapshot.ResearchOpen)
            {
                if (snapshot.ResearchSessionVersion == 0) return Fail("打开研究必须提供可信会话版本。", out error);
                if (!TryBuildResearch(candidate, snapshot.Research, out var research, out error)) return false;
                candidate.Research = research;
            }
            else if (snapshot.Research != null && snapshot.Research.SchemaVersion != 0)
                return Fail("关闭的研究会话不能夹带可安装研究正文。", out error);
            return true;
        }

        private bool TryImportGround(OnlineGroundInventorySnapshot ground, ItemOwnershipStore owned, uint version,
            out ItemOwnershipStore merged, out Dictionary<string, GroundPose> poses, out Dictionary<string, uint> revisions, out string error)
        {
            merged = owned; poses = new Dictionary<string, GroundPose>(StringComparer.Ordinal); error = null;
            revisions = new Dictionary<string, uint>(StringComparer.Ordinal);
            if (ground == null) return true; // 无该能力的旧隔离来源不创建地面权限；已知来源的倒退在CanInstall拒绝。
            if (ground.Version == 0 || ground.Items == null || ground.Items.Length > OnlineGroundInventorySnapshot.MaximumItems)
                return Fail("公开地面版本或有界完整列表非法。", out error);
            int groundCount = 0;
            foreach (var current in ground.Items)
            {
                if (current == null || current.Item == null || current.Children == null
                    || current.Children.Length > MaximumItems || groundCount > OnlineGroundInventorySnapshot.MaximumItems - 1 - current.Children.Length)
                    return Fail("公开地面缺少完整实例或子卡闭包超出有界预算。", out error);
                groundCount += 1 + current.Children.Length;
            }
            var entries = new InventoryEntry[owned.Count + groundCount];
            int at = 0; foreach (var entry in owned) entries[at++] = entry;
            poses.EnsureCapacity(ground.Items.Length);
            revisions.EnsureCapacity(groundCount);
            foreach (var current in ground.Items)
            {
                if (current == null || !GroundId(current.InstanceId) || !GroundId(current.WorldOwnerId) || current.Revision == 0
                    || !GroundFinite(current.Position) || !GroundRotation(current.Rotation) || poses.ContainsKey(current.InstanceId)
                    || !_catalog.TryGetRules(current.ContentId, out var rules))
                    return Fail("公开地面实例、落地身份、模型位置或目录非法。", out error);
                var value = current.Item;
                // 新协议的完整根是唯一资产事实；旧别名仍校验一致，避免Codec/表现各读一半冲突数据。
                if (value.InstanceId != current.InstanceId || value.ContentId != current.ContentId || value.Revision != current.Revision
                    || value.LocationKind != (int)ItemLocationKind.WorldItem || value.ParentInstanceId != "" || value.Slot != -1
                    || value.X != 0 || value.Y != 0 || value.QuarterTurns != 0 || value.EntryBoundBackpack
                    || !SameCompositionValue(value.ContainerState, current.ContainerState) || !SameMaterialValue(value.MaterialState, current.MaterialState)
                    || current.Children.Length != 0 && rules.Kind != ItemKind.Wand)
                    return Fail("公开地面完整根、旧别名或免费包边界不一致。", out error);
                if (!TryReadItemValue(value, out var runtime, out error)) return false;
                if (!revisions.TryAdd(value.InstanceId, value.Revision)) return Fail("公开地面实例重复。", out error);
                entries[at++] = new InventoryEntry(runtime,
                    new ItemLocation(ItemLocationKind.WorldItem, current.WorldOwnerId, 0, 0, 0, -1));
                // 只允许这根杖直接安装的卡。子数组不能夹入另一Owner、另一地面根或装备来偷渡归属。
                foreach (var child in current.Children)
                {
                    if (child == null || child.LocationKind != (int)ItemLocationKind.WandSlot || child.ParentInstanceId != value.InstanceId
                        || !TryReadLocation(child, out var location) || !TryReadItemValue(child, out var card, out error)
                        || !_catalog.TryGetRules(child.ContentId, out var childRules) || childRules.Kind != ItemKind.Spell)
                        return Fail("公开地面法杖子卡必须属于本根的真实安装槽。", out error);
                    entries[at++] = new InventoryEntry(card, location);
                    if (!revisions.TryAdd(child.InstanceId, child.Revision)) return Fail("公开地面子卡实例重复。", out error);
                }
                poses.Add(current.InstanceId, new GroundPose(current.Position, current.Rotation, current.Revision));
            }
            if (!ItemOwnershipStore.TryRestore(_catalog, entries, version, out merged, out var reason))
                return Fail("公开地面与Owner身份重复或合并归属非法：" + reason, out error);
            return true;
        }
        private static bool TryCopyGroundCoins(OnlineGroundInventorySnapshot ground, out OnlineCoinSnapshot[] coins, out string error)
        {
            coins = Array.Empty<OnlineCoinSnapshot>(); error = null;
            if (ground == null) return true;
            if (ground.Coins == null || ground.Coins.Length > OnlineGroundInventorySnapshot.MaximumCoins)
                return Fail("公开金币缺少完整列表或超过原NPC掉落上界。", out error);
            ushort identities = 0;
            for (int i = 0; i < ground.Coins.Length; i++)
            {
                var coin = ground.Coins[i];
                if (coin.Id < 5 || coin.Id > 16 || coin.Amount <= 0 || !GroundFinite(coin.Position))
                    return Fail("公开金币身份、原抽样金额或死亡位置非法。", out error);
                int bit = 1 << (int)(coin.Id - 5);
                if ((identities & bit) != 0) return Fail("同一NPC的金币掉落身份重复。", out error);
                identities |= (ushort)bit;
            }
            // DTO由网络调用者持有；完整验证后才复制，后续修改输入数组不能改写已发布事实。
            coins = (OnlineCoinSnapshot[])ground.Coins.Clone();
            return true;
        }

        private static bool SameGround(State a, State b)
        {
            if (a.GroundCoins.Length != b.GroundCoins.Length) return false;
            for (int i = 0; i < a.GroundCoins.Length; i++)
            {
                var coin = a.GroundCoins[i]; bool found = false;
                for (int j = 0; j < b.GroundCoins.Length; j++)
                    if (coin.Id == b.GroundCoins[j].Id)
                    {
                        // 不使用Vector3近似相等：同版本哪怕极小坐标变化也不能偷偷替换已确认事实。
                        if (coin.Amount != b.GroundCoins[j].Amount || !coin.Position.Equals(b.GroundCoins[j].Position)) return false;
                        found = true; break;
                    }
                if (!found) return false;
            }
            if (a.GroundPoses.Count != b.GroundPoses.Count) return false;
            if (a.GroundItemRevisions.Count != b.GroundItemRevisions.Count) return false;
            foreach (var revision in a.GroundItemRevisions)
                if (!b.GroundItemRevisions.TryGetValue(revision.Key, out var previousRevision) || revision.Value != previousRevision) return false;
            foreach (var pair in a.GroundPoses)
            {
                if (!b.GroundPoses.TryGetValue(pair.Key, out var other) || pair.Value.Revision != other.Revision
                    || !pair.Value.Position.Equals(other.Position)
                    || !pair.Value.Rotation.Equals(other.Rotation) || !a.Items.TryGet(pair.Key, out var item)
                    || !b.Items.TryGet(pair.Key, out var previous) || item.ContentId != previous.ContentId
                    || !a.Items.TryGetLocation(pair.Key, out var loc) || !b.Items.TryGetLocation(pair.Key, out var oldLoc)
                    || loc != oldLoc || !SameGroundResources(item, previous)) return false;
            }
            int countA = 0, countB = 0;
            // 子卡不在GroundPoses中，但其次数、附件、槽位同样受GroundVersion保护；不能只比较模型根。
            foreach (var entry in a.Items)
            {
                if (!IsGroundEntry(a.Items, entry.Location)) continue;
                countA++;
                if (!b.Items.TryGet(entry.Item.InstanceId, out var previous)
                    || !b.Items.TryGetLocation(entry.Item.InstanceId, out var previousLocation)
                    || entry.Location != previousLocation || !SameGroundResources(entry.Item, previous)) return false;
            }
            foreach (var entry in b.Items) if (IsGroundEntry(b.Items, entry.Location)) countB++;
            if (countA != countB) return false;
            return true;
        }
        private static bool IsGroundEntry(ItemOwnershipStore store, ItemLocation location)
        {
            for (int depth = 0; location.HasParentInstance; depth++)
                if (depth >= store.Count || !store.TryGetLocation(location.OwnerId, out location)) return false;
            return location.Kind == ItemLocationKind.WorldItem;
        }
        private static bool SameGroundResources(RuntimeItemState item, RuntimeItemState previous)
        {
            if (item.ContentId != previous.ContentId || item.RemainingCharges != previous.RemainingCharges
                || item.HasUnlimitedCharges != previous.HasUnlimitedCharges || item.StackStateKey != previous.StackStateKey
                || item.EntryBoundBackpack != previous.EntryBoundBackpack || item.RolledWandStats.HasValue != previous.RolledWandStats.HasValue
                || item.RolledWandStats.HasValue && !item.RolledWandStats.Value.SameAs(previous.RolledWandStats.Value)
                || (item.Attachment == null ? previous.Attachment != null : !item.Attachment.SameAs(previous.Attachment))) return false;
            if (item.Composition == null ? previous.Composition != null : previous.Composition == null || !item.Composition.SameAs(previous.Composition)) return false;
            return item.Material == null ? previous.Material == null : previous.Material != null
                && item.Material.MaterialKey == previous.Material.MaterialKey && item.Material.Quantity == previous.Material.Quantity;
        }
        private static bool SameCompositionValue(OnlineOwnedContainerStateSnapshot a, OnlineOwnedContainerStateSnapshot b)
        {
            if (a == null || b == null) return a == b;
            if (a.SchemaVersion != b.SchemaVersion || a.Components == null || b.Components == null || a.Components.Length != b.Components.Length) return false;
            for (int i = 0; i < a.Components.Length; i++)
                if (a.Components[i] == null || b.Components[i] == null || a.Components[i].MaterialKey != b.Components[i].MaterialKey
                    || a.Components[i].Quantity != b.Components[i].Quantity) return false;
            return true;
        }
        private static bool SameMaterialValue(OnlineOwnedMaterialStateSnapshot a, OnlineOwnedMaterialStateSnapshot b)
            => a == null || b == null ? a == b : a.SchemaVersion == b.SchemaVersion && a.MaterialKey == b.MaterialKey && a.Quantity == b.Quantity;
        private static bool GroundId(string id)
        { if (id == null || id.Length != 32) return false; foreach (char c in id) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false; return true; }
        private static bool GroundFinite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
        private static bool GroundRotation(Quaternion q)
        { float n = q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w; return float.IsFinite(n) && n >= .99f && n <= 1.01f; }

        private bool TryImportItems(OnlineOwnedAssetSnapshot assets, out ItemOwnershipStore store, out string error)
        {
            store = null; error = null;
            var entries = new InventoryEntry[assets.Items.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                var item = assets.Items[i];
                if (!TryReadItemValue(item, out var runtime, out error)) return false;
                if (!TryReadLocation(item, out var location)) return Fail("物品不属于当前玩家真实随身位置。", out error);
                entries[i] = new InventoryEntry(runtime, location);
            }
            // 原恢复器先收齐实例再检查完整父链、格位和重复ID；数组顺序不决定父实例是否存在。
            if (!ItemOwnershipStore.TryRestore(_catalog, entries, assets.InventoryVersion, out store, out var reason))
                return Fail("线上库存完整归属校验失败：" + reason, out error);
            foreach (var entry in store)
            {
                var root = entry.Location;
                for (int depth = 0; root.HasParentInstance; depth++)
                    if (depth >= store.Count || !store.TryGetLocation(root.OwnerId, out root))
                        return Fail("线上库存父链不完整。", out error);
                if (root.OwnerId != _identity.PlayerId || root.Kind != ItemLocationKind.EquippedWand
                    && root.Kind != ItemLocationKind.EquippedBackpack && root.Kind != ItemLocationKind.EquippedTool
                    && root.Kind != ItemLocationKind.SpellStorage) return Fail("线上库存包含非当前玩家的随身根。", out error);
            }
            return true;
        }

        /// <summary>Owner与公开地面共用同一纯实例验证，保留次数、随机属性和附件；不创建Authority或推导资格。</summary>
        private bool TryReadItemValue(OnlineOwnedItemSnapshot item, out RuntimeItemState runtime, out string error)
        {
            runtime = null; error = null;
            if (item == null || item.Revision == 0 || item.ParentInstanceId == null || string.IsNullOrWhiteSpace(item.InstanceId)
                || item.RemainingCharges < 0 || !_catalog.TryGet(item.ContentId, out var definition) || !_catalog.TryGetRules(item.ContentId, out var rules)
                || item.EntryBoundBackpack && definition.Kind != ItemKind.Backpack)
                return Fail("物品身份、修订、ContentId或免费包标记非法。", out error);
                WandStats? instanceStats = null;
                // 包内备用杖与装备杖共用固定属性合同，但只有装备杖消费 Wands 资源事实。
                if (definition.Kind == ItemKind.Wand)
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
                else if (item.UseTemplateWandStats || item.WandStats != null) return Fail("非法杖物品不能携带法杖属性。", out error);

                // 仅复用纯组分验证，不调用会创建资格/施法服务和全局订阅的 Authority Factory.TryCreate。
                if (!OnlineOwnedWorkspaceFactory.TryReadComposition(item, rules, out var composition, out error)) return false;
                if (!OnlineOwnedWorkspaceFactory.TryReadMaterial(item, rules, out var material, out error)) return false;
                if (!OnlineOwnedWorkspaceFactory.TryReadAttachment(item, definition, out var attachment, out error)) return false;

                runtime = new RuntimeItemState(item.InstanceId, item.ContentId, item.RemainingCharges,
                    item.HasUnlimitedCharges, composition: composition, material: material, attachment: attachment,
                    rolledWandStats: instanceStats, entryBoundBackpack: item.EntryBoundBackpack);
            return true;
        }

        private bool TryReadLocation(OnlineOwnedItemSnapshot item, out ItemLocation location)
        {
            location = default;
            ItemLocationKind kind;
            string owner;
            switch (item.LocationKind)
            {
                case (int)ItemLocationKind.EquippedWand:
                case (int)ItemLocationKind.EquippedBackpack:
                case (int)ItemLocationKind.EquippedTool:
                case (int)ItemLocationKind.SpellStorage:
                    if (item.ParentInstanceId.Length != 0) return false;
                    kind = (ItemLocationKind)item.LocationKind; owner = _identity.PlayerId;
                    break;
                case (int)ItemLocationKind.Backpack:
                case (int)ItemLocationKind.WandSlot:
                    if (string.IsNullOrWhiteSpace(item.ParentInstanceId)) return false;
                    kind = (ItemLocationKind)item.LocationKind; owner = item.ParentInstanceId;
                    break;
                default: return false;
            }
            location = new ItemLocation(kind, owner, item.X, item.Y, item.QuarterTurns, item.Slot);
            return true;
        }

        public void Dispose() { _disposed = true; _state = null; _resourcePresentationVersion = 0; }
        /// <summary>
        /// 返回DS本次Open/编辑时冻结的一致起点。高频Mana包不包含独立Cast/Reload/Draw，不能在Client猜分量或重抽牌。
        /// UI另以可信ResearchOpen控制编辑授权；只读研究对象和历史回放从来不成为Gameplay写权限。
        /// </summary>
        public bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot)
        { snapshot = null; return !_disposed && _state?.Research != null && id != null && _state.Research.Wands.TryGetValue(id, out snapshot); }
        public bool TryInstallResearchState(OnlineWandResearchStateSnapshot snapshot, out string error)
        {
            if (_disposed || _state == null) return Fail("研究Source尚未绑定真实Owner。", out error);
            if (!TryBuildResearch(_state, snapshot, out var candidate, out error)) return false;
            _state.Research = candidate; return true;
        }
        private bool TryBuildResearch(State owner, OnlineWandResearchStateSnapshot snapshot,
            out OnlineWandResearchReadModel candidate, out string error)
        {
            candidate = null; error = null;
            if (snapshot == null || snapshot.PlayerId != _identity.PlayerId || snapshot.InventoryId != _identity.InventoryId
                || snapshot.GameplayConfigHash != _identity.GameplayConfigHash || snapshot.InventoryVersion != owner.Inventory.Version
                || snapshot.ResourceVersion != owner.ResourceVersion || snapshot.SpellUseVersion != owner.SpellUseVersion
                || snapshot.SelectedWandId != owner.SelectedWandId)
                return Fail("研究正文与当前Owner身份或领域水位不一致。", out error);
            if (!OnlineWandResearchReadModel.TryCreate(snapshot, _catalog, owner.Items, owner.SpellUses, true, out candidate, out error)) return false;
            foreach (var entry in snapshot.Wands)
            {
                var wand = FindWand(owner, entry.WandId);
                if (wand == null || wand.Program.ProgramRevision != entry.ProgramRevision || wand.State.CurrentMana != entry.Mana
                    || wand.State.CooldownRemaining != entry.Ready) return Fail("研究资源不等于同批Owner的真实法杖读数。", out error);
            }
            return true;
        }
        public bool TryCopyContainer(string itemId, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity)
        {
            count = 0; total = capacity = 0;
            if (_state == null || !_state.Items.TryGet(itemId, out var item) || item.Composition == null) return false;
            // CopyTo 按稳定材料键输出独立值；缓冲不足抛出而非截断真实组分，旧 DTO/调用方数组都不能改写 State。
            count = item.Composition.CopyTo(buffer); total = item.Composition.Total; capacity = item.Composition.Capacity;
            return true;
        }

        // 以下为原View的稳定只读Adapter：每次读取当前已发布State，不把UI缓存永久绑在上一份InventoryReadModel。
        public string InventoryId => _state != null ? _identity.InventoryId : null;
        string IInventoryReadOnly.ActorId => _state != null ? _identity.PlayerId : null;
        uint IInventoryReadOnly.Version => _state != null ? _state.Inventory.Version : 0;
        public int Count => _state != null ? _state.Inventory.Count : 0;
        public int WarehouseCount => 0;
        public bool TryGetView(string instanceId, out InventoryItemSnapshot view)
        { view = default; return _state != null && _state.Inventory.TryGetView(instanceId, out view); }
        /// <summary>与 DS 按同一捕获顺序遍历真实装备包；不截断到256个UI候选，也不把地面固体当作已携带。</summary>
        public bool TryGetFirstBackpackMaterial(out InventoryItemSnapshot item)
        {
            item = default;
            if (_disposed || _state == null || _state.EquippedBackpackId == null) return false;
            foreach (var entry in _state.Items)
                if (entry.Location.Kind == ItemLocationKind.Backpack && entry.Location.OwnerId == _state.EquippedBackpackId
                    && entry.Item.Material != null) return _state.Inventory.TryGetView(entry.Item.InstanceId, out item);
            return false;
        }
        /// <summary>
        /// 复用原背包的纯形状/占据预览，使共享地面容器拖入背包时也能得到准确格位提示。
        /// 这不是客户端拾取权限：只允许本段已批准的地面容器到当前随身包，最终距离、
        /// WorldOwner、版本与归属仍由 DS 原子事务检查；不开放卡牌移动或随身物品重排。
        /// </summary>
        public InventoryFailureReason PreviewPlacement(string itemId, string bagId, in GridPlacement placement,
            bool wholeStack, uint expectedVersion)
        {
            if (_disposed || _state == null) return InventoryFailureReason.InvalidState;
            if (expectedVersion != _state.Inventory.Version) return InventoryFailureReason.StaleVersion;
            if (string.IsNullOrEmpty(bagId) || bagId != _state.EquippedBackpackId
                || !_state.Inventory.TryGetView(itemId, out var item)
                || item.EntryBoundBackpack || item.Kind == ItemKind.Backpack)
                return InventoryFailureReason.InvalidOwnership;
            var root = item.Location;
            for (int depth = 0; root.HasParentInstance; depth++)
                if (depth >= _state.Items.Count || !_state.Items.TryGetLocation(root.OwnerId, out root)) return InventoryFailureReason.InvalidOwnership;
            if (root.Kind == ItemLocationKind.WorldItem ? !_state.GroundPoses.ContainsKey(item.InstanceId)
                : root.OwnerId != _identity.PlayerId) return InventoryFailureReason.InvalidOwnership;
            return _state.Inventory.PreviewPlacement(itemId, bagId, placement, wholeStack, expectedVersion);
        }

        public int CopyWarehouseTo(InventoryItemSnapshot[] buffer) => 0;
        public int CopyItemsTo(InventoryItemSnapshot[] buffer) => _state != null ? _state.Inventory.CopyItemsTo(buffer) : 0;
        public string SelectedWandId => _state != null ? _state.SelectedWandId : null;
        public uint ResourceVersion => _state != null ? _resourcePresentationVersion : 0;
        public bool TryGetSlot(int slot, out WandStateSnapshot state)
        {
            state = default; if (_state == null || slot < 0 || slot >= HeldItemSlots.WandCount) return false;
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
        public string SelectedItemId => _state != null ? _state.SelectedItemId : null;
        public bool TryGetBinding(int toolSlot, out string instanceId)
        {
            instanceId = _state != null && toolSlot >= 0 && toolSlot < _state.ToolBindings.Length
                ? _state.ToolBindings[toolSlot] : null;
            return instanceId != null;
        }
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
